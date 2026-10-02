using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LinkBridge.Core;
using Microsoft.Win32.SafeHandles;

namespace LinkBridge.Media.Camera;

public enum SharedCameraStatus { Offline, WaitingForFrames, Connected }

/// <summary>Starts a real 4K decoder only while native camera clients hold live leases.</summary>
[SupportedOSPlatform("windows")]
public sealed class SharedCameraBroker : IAsyncDisposable
{
    public const int DefaultPort=45683;
    private readonly object _gate=new();
    private readonly object _notificationGate=new();
    private int _lastNotifiedConsumers;
    private readonly string _mappingName, _pipeName;
    private readonly PipeSecurity _security;
    private readonly int _port;
    private readonly long _leaseMs;
    private readonly Dictionary<Guid,(int Pid,long Seen)> _leases=[];
    private readonly HashSet<NamedPipeServerStream> _pipes=[];
    private readonly HashSet<Task> _handlers=[];
    private readonly HashSet<Task> _retired=[];
    private readonly CancellationTokenSource _stop=new();
    private Task? _acceptTask,_leaseTask;
    private Capture? _capture;
    private long _generation,_published,_nextCaptureAttempt;
    private bool _started,_disposed;
    private SharedCameraStatus _status;
    private string? _expectedSourceAddress;
    private sealed class Capture(Process process,CancellationTokenSource cancellation,long generation)
    {
        public readonly Process Process=process;
        public readonly CancellationTokenSource Cancellation=cancellation;
        public readonly long Generation=generation;
        public Task? Pump;
        public CameraFrameMapping? Mapping;
        public long LastFrame,LastMappingWarning;
    }
    public event Action<int>? ConsumersChanged;
    public event Action<string>? BrokerLogEmitted;
    public event Action<SharedCameraStatus>? StatusChanged;
    public int ConsumerCount { get { lock(_gate)return _leases.Count; } }
    public SharedCameraStatus Status { get { lock(_gate)return _status; } }
    public long PublishedFrames=>Interlocked.Read(ref _published);
    public int Port=>_port;
    public string MappingName=>_mappingName;
    public string PipeName=>_pipeName;
    /// <summary>
    /// IPv4 address selected by the authenticated camera control channel. Null disables
    /// UDP capture even when applications have active leases. Changing the peer retires
    /// the old decoder and its frames before listening for the new peer.
    /// </summary>
    public string? ExpectedSourceAddress
    {
        get { lock(_gate)return _expectedSourceAddress; }
        set
        {
            string? address=null;
            if(value is not null)
            {
                if(!IPAddress.TryParse(value,out var parsed)||parsed.AddressFamily!=AddressFamily.InterNetwork||
                    parsed.Equals(IPAddress.Any)||parsed.Equals(IPAddress.Broadcast)||parsed.GetAddressBytes()[0]>=224)
                    throw new ArgumentException("A unicast IPv4 camera peer address is required.",nameof(value));
                address=parsed.ToString();
            }
            lock(_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed,this);
                if(_expectedSourceAddress==address)return;
                StopCaptureLocked();_expectedSourceAddress=address;_nextCaptureAttempt=0;
                if(_started)ReconcileCaptureLocked();
            }
        }
    }

    public SharedCameraBroker(int port=DefaultPort,string? mappingName=null,string? pipeName=null,TimeSpan? leaseTimeout=null)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        if(port is <1 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        if(!Environment.Is64BitProcess)throw new PlatformNotSupportedException("4K camera IPC requires a 64-bit process.");
        var sid=WindowsIdentity.GetCurrent().User??throw new InvalidOperationException("无法确定当前Windows用户SID。");
        _mappingName=mappingName??"Global\\LinkBridge.Camera.Frame.v1."+sid.Value;
        _pipeName=pipeName??"LinkBridge.Camera.Events.v1."+sid.Value;
        _port=port; _leaseMs=(long)(leaseTimeout??TimeSpan.FromSeconds(8)).TotalMilliseconds;
        if(_leaseMs<500)throw new ArgumentOutOfRangeException(nameof(leaseTimeout));
        _security=new PipeSecurity(); _security.SetAccessRuleProtection(true,false);
        foreach(var identity in new[]{sid,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.LocalServiceSid,null)})
            _security.AddAccessRule(new PipeAccessRule(identity,PipeAccessRights.FullControl,AccessControlType.Allow));
        _security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
    }
    public void Start()
    {
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this); if(_started)return;
            // Create the first instance synchronously: Start fails honestly if another broker owns the pipe.
            var first=CreatePipe(true); _pipes.Add(first); _started=true;
            _acceptTask=AcceptLoopAsync(first,_stop.Token); _leaseTask=LeaseLoopAsync(_stop.Token);
        }
        Log("[LinkBridge] 共享摄像头按需通道已就绪；等待应用调用。");
    }
    private NamedPipeServerStream CreatePipe(bool first=false)=>NamedPipeServerStreamAcl.Create(_pipeName,PipeDirection.In,
        NamedPipeServerStream.MaxAllowedServerInstances,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|(first?PipeOptions.FirstPipeInstance:PipeOptions.None),2048,0,_security);
    private async Task AcceptLoopAsync(NamedPipeServerStream? pipe,CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                if(pipe is null)
                {
                    lock(_gate){if(_disposed)break;pipe=CreatePipe();_pipes.Add(pipe);}
                }
                try { await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false); }
                catch(IOException) when(!ct.IsCancellationRequested)
                {
                    // Native notifications intentionally use a short connection; a client
                    // can close between CreateNamedPipe and ConnectNamedPipe. Keep listening.
                    lock(_gate)_pipes.Remove(pipe);pipe.Dispose();pipe=null;continue;
                }
                var connected=pipe;
                lock(_gate){if(_disposed){connected.Dispose();break;}pipe=CreatePipe();_pipes.Add(pipe);}
                var handler=ReadNotificationAsync(connected,ct);
                lock(_gate)_handlers.Add(handler);
                _=handler.ContinueWith(_=>{lock(_gate)_handlers.Remove(handler);},TaskScheduler.Default);
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(ObjectDisposedException)when(ct.IsCancellationRequested){}
        catch(Exception ex){Log("[错误] 摄像头调用通知监听中断："+ex.Message);}
        finally {if(pipe is not null){lock(_gate)_pipes.Remove(pipe);pipe.Dispose();}}
    }
    private async Task ReadNotificationAsync(NamedPipeServerStream pipe,CancellationToken ct)
    {
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
            var bytes=new byte[1024];var length=0;
            while(length<bytes.Length)
            {
                var read=await pipe.ReadAsync(bytes.AsMemory(length),deadline.Token).ConfigureAwait(false);
                if(read==0)break;
                length+=read;if(Array.IndexOf(bytes,(byte)'\n',0,length)>=0)break;
            }
            var newline=Array.IndexOf(bytes,(byte)'\n',0,length);
            if(newline<0)throw new InvalidDataException("摄像头通知缺少换行或过长。");
            using var json=JsonDocument.Parse(bytes.AsMemory(0,newline));var root=json.RootElement;
            if(root.GetProperty("v").GetInt32()!=1)return;
            var name=root.GetProperty("event").GetString();
            var instance=root.TryGetProperty("instance",out var value)?value.GetString():root.GetProperty("instanceGuid").GetString();
            if(!Guid.TryParse(instance,out var id))return;
            var pid=root.GetProperty("pid").GetInt32();
            if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var actualPid)||actualPid!=pid)return;
            var width=root.GetProperty("width").GetInt32();var height=root.GetProperty("height").GetInt32();
            if(name!="stop"&&!((width==3840&&height==2160)||(width==1920&&height==1080)))return;
            if(root.GetProperty("fps").GetInt32()!=30)return;
            UpdateLease(id,pid,name);
        }
        catch(OperationCanceledException){}
        catch(Exception ex)when(!ct.IsCancellationRequested){Log("[提示] 忽略无效摄像头通知："+ex.Message);}
        finally{lock(_gate)_pipes.Remove(pipe);pipe.Dispose();}
    }
    private void UpdateLease(Guid id,int pid,string? name)
    {
        int before,after;
        lock(_gate)
        {
            if(_disposed)return;before=_leases.Count;
            if(_leases.TryGetValue(id,out var existing)&&existing.Pid!=pid)return;
            if(name=="stop")_leases.Remove(id);
            else if(name is "start" or "heartbeat")_leases[id]=(pid,Environment.TickCount64);
            else return;
            after=_leases.Count; ReconcileCaptureLocked();
        }
        if(before!=after)NotifyConsumers(after);
    }
    private async Task LeaseLoopAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while(await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                int before,after;
                lock(_gate)
                {
                    if(_disposed)break;var now=Environment.TickCount64;before=_leases.Count;
                    foreach(var id in _leases.Where(pair=>now-pair.Value.Seen>_leaseMs).Select(pair=>pair.Key).ToArray())_leases.Remove(id);
                    after=_leases.Count;ReconcileCaptureLocked();
                    if(_capture is {} capture&&now-capture.LastFrame>3000){capture.Mapping?.MarkOffline();SetStatusLocked(SharedCameraStatus.WaitingForFrames);}
                }
                if(before!=after)NotifyConsumers(after);
            }
        }
        catch(OperationCanceledException){}
    }
    private void ReconcileCaptureLocked()
    {
        if(_leases.Count==0){StopCaptureLocked();_nextCaptureAttempt=0;SetStatusLocked(SharedCameraStatus.Offline);return;}
        if(_expectedSourceAddress is null){StopCaptureLocked();SetStatusLocked(SharedCameraStatus.WaitingForFrames);return;}
        if(_capture is not null||Environment.TickCount64<_nextCaptureAttempt)return;
        _nextCaptureAttempt=Environment.TickCount64+2000;
        try
        {
            var ffmpeg=FfmpegLocator.ExecutablePath??throw new InvalidOperationException("未找到ffmpeg。");
            var info=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardErrorEncoding=Encoding.UTF8};
            // Reject any non-native 3840x2160 input instead of silently upscaling it.
            // FFmpeg UDP sources filters packets before MPEG-TS decoding. The peer is
            // supplied only by paired control, never by an incoming UDP packet.
            foreach(var arg in new[]{"-hide_banner","-loglevel","warning","-nostdin","-i",$"udp://0.0.0.0:{_port}?listen=1&fifo_size=65536&overrun_nonfatal=1&sources={_expectedSourceAddress}",
                "-an","-vf","crop=w='if(eq(iw,3840)*eq(ih,2160),iw,-1)':h=ih","-pix_fmt","bgra","-f","rawvideo","pipe:1"})info.ArgumentList.Add(arg);
            var process=new Process{StartInfo=info};var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var capture=new Capture(process,cancellation,++_generation);
            process.ErrorDataReceived+=(_,e)=>{lock(_gate)if(IsCurrent(capture)&&!string.IsNullOrWhiteSpace(e.Data))Log("[4K] "+e.Data);};
            try
            {
                if(!process.Start())throw new InvalidOperationException("4K摄像头解码启动失败。");
                _capture=capture;process.BeginErrorReadLine();var token=cancellation.Token;
                capture.Pump=Task.Run(()=>PumpFramesAsync(capture,token));
                SetStatusLocked(SharedCameraStatus.WaitingForFrames);Log($"[LinkBridge] 应用正在调用共享摄像头；等待 {_expectedSourceAddress} 原生4K流（UDP {_port}）。");
            }
            catch{_capture=null;ProcessHelper.TryKill(process);process.Dispose();cancellation.Dispose();throw;}
        }
        catch(Exception ex){SetStatusLocked(SharedCameraStatus.WaitingForFrames);Log("[错误] 4K按需接收无法启动："+ex.Message);}
    }
    private bool IsCurrent(Capture capture)=>!_disposed&&ReferenceEquals(_capture,capture)&&capture.Generation==_generation&&!capture.Cancellation.IsCancellationRequested;
    private void StopCaptureLocked()
    {
        ++_generation;var capture=_capture;_capture=null;if(capture is null)return;
        capture.Cancellation.Cancel();ProcessHelper.TryKill(capture.Process);capture.Mapping?.MarkOffline();
        if(capture.Pump is {} task){_retired.Add(task);_=task.ContinueWith(_=>{lock(_gate)_retired.Remove(task);},TaskScheduler.Default);}
    }
    private async Task PumpFramesAsync(Capture capture,CancellationToken ct)
    {
        var frame=ArrayPool<byte>.Shared.Rent(CameraFrameMapping.FrameBytes);
        try
        {
            var stream=capture.Process.StandardOutput.BaseStream;var offset=0;
            while(!ct.IsCancellationRequested)
            {
                var count=await stream.ReadAsync(frame.AsMemory(offset,CameraFrameMapping.FrameBytes-offset),ct).ConfigureAwait(false);
                if(count==0)break;offset+=count;if(offset!=CameraFrameMapping.FrameBytes)continue;
                lock(_gate)
                {
                    if(!IsCurrent(capture))break;
                    try { capture.Mapping??=new CameraFrameMapping(_mappingName); }
                    catch(Win32Exception ex)when(ex.NativeErrorCode is 2 or 3)
                    {
                        if(Environment.TickCount64-capture.LastMappingWarning>2000)
                        {
                            capture.LastMappingWarning=Environment.TickCount64;
                            Log("[提示] 正在等待原生摄像头消费者创建4K帧通道。");
                        }
                        offset=0;continue;
                    }
                    capture.Mapping.Publish(frame);capture.LastFrame=Environment.TickCount64;
                    Interlocked.Increment(ref _published);SetStatusLocked(SharedCameraStatus.Connected);
                }
                offset=0;
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(Exception ex){lock(_gate)if(IsCurrent(capture))Log("[错误] 4K摄像头帧通道中断："+ex.Message);}
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
            lock(_gate)
            {
                ProcessHelper.TryKill(capture.Process);
                if(IsCurrent(capture)){capture.Mapping?.MarkOffline();_capture=null;_nextCaptureAttempt=Environment.TickCount64+2000;SetStatusLocked(_leases.Count>0?SharedCameraStatus.WaitingForFrames:SharedCameraStatus.Offline);}
                capture.Mapping?.Dispose();
            }
            try{await capture.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);}catch{}
            capture.Process.Dispose();capture.Cancellation.Dispose();
        }
    }
    private void SetStatusLocked(SharedCameraStatus status)
    {
        if(_status==status)return;_status=status;
        try{StatusChanged?.Invoke(status);}catch{}
    }
    private void NotifyConsumers(int count)
    {
        lock(_notificationGate)
        {
            // Concurrent short pipe handlers can finish out of order. Deliver the latest
            // state serially so a late start callback cannot follow a newer stop callback.
            lock(_gate)count=_leases.Count;
            if(_lastNotifiedConsumers==count)return;
            _lastNotifiedConsumers=count;
            try{ConsumersChanged?.Invoke(count);}catch(Exception ex){Log("[提示] 摄像头消费者回调失败："+ex.Message);}
        }
    }
    private void Log(string message){try{BrokerLogEmitted?.Invoke(message);}catch{}}
    public ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock(_gate)
        {
            if(_disposed)return ValueTask.CompletedTask;_disposed=true;_stop.Cancel();StopCaptureLocked();
            foreach(var pipe in _pipes.ToArray())pipe.Dispose();_pipes.Clear();_leases.Clear();SetStatusLocked(SharedCameraStatus.Offline);
            tasks=_handlers.Concat(_retired).Concat(new[]{_acceptTask,_leaseTask}.OfType<Task>()).ToArray();
        }
        NotifyConsumers(0);return new ValueTask(FinishDisposeAsync(tasks));
    }
    private async Task FinishDisposeAsync(Task[] tasks)
    {
        var all=Task.WhenAll(tasks);
        try{await all.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);}catch(Exception ex){Log("[提示] 摄像头清理等待结束："+ex.Message);}
        finally{if(all.IsCompleted)_stop.Dispose();else _=all.ContinueWith(_=>_stop.Dispose(),TaskScheduler.Default);}
    }
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
}





