using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LinkBridge.Core.Security;

namespace LinkBridge.Media.Audio;

/// <summary>Paired microphone PCM receiver. Never chooses or modifies a default playback endpoint.</summary>
public sealed class SharedMicrophoneReceiver : IAsyncDisposable
{
    public const int DefaultPort=45684;
    private readonly object _gate=new();
    private readonly TcpListener _listener;
    private readonly byte[] _secret;
    private readonly X509Certificate2 _certificate;
    private readonly Func<string,bool> _isPeer;
    private readonly Func<bool> _isDemandAllowed;
    private readonly Func<ISharedMicrophoneSink> _sinkFactory;
    private readonly CancellationTokenSource _stop=new();
    private Task? _task;
    private Session? _session;
    private bool _started,_disposed,_connected;
    private int _boundPort;
    private long _receivedBytes;
    private sealed class Session(TcpClient client,CancellationToken ct)
    {
        public readonly TcpClient Client=client;
        public readonly CancellationTokenSource Cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct);
        public SslStream? Secure;
        public ISharedMicrophoneSink? Sink;
        public void StopImmediate()
        {
            Cancellation.Cancel();Client.Dispose();try{Secure?.Dispose();}catch{}
            try{Sink?.Dispose();}catch{}Sink=null;
        }
    }
    public event Action<string>? LogEmitted;
    public event Action<bool>? ConnectionChanged;
    public long ReceivedBytes=>Interlocked.Read(ref _receivedBytes);
    public bool IsConnected {get{lock(_gate)return _connected;}}
    public string? SinkEndpointName {get{lock(_gate)return _session?.Sink?.EndpointName;}}
    public int Port {get{lock(_gate)return _boundPort;}}
    public SharedMicrophoneReceiver(string secret,Func<string,bool> isPeer,Func<ISharedMicrophoneSink> sinkFactory,
        int port=DefaultPort,Func<bool>? isDemandAllowed=null,IPAddress? bindAddress=null)
    {
        if(port is <0 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        _secret=PairedTlsTransport.SecretBytes(secret);_isPeer=isPeer??throw new ArgumentNullException(nameof(isPeer));
        _sinkFactory=sinkFactory??throw new ArgumentNullException(nameof(sinkFactory));_isDemandAllowed=isDemandAllowed??(()=>true);
        _listener=new TcpListener(bindAddress??IPAddress.Any,port);
        _certificate=PairedTlsTransport.CreateServerCertificate(SharedMicrophonePcm.Context);
    }
    public void Start()
    {
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);if(_started)return;
            _listener.Start(4);_boundPort=((IPEndPoint)_listener.LocalEndpoint).Port;_started=true;_task=RunAsync(_stop.Token);
        }
        Log($"[LinkBridge] 共享麦克风虚拟输入等待配对连接，LBM1 TLS端口 {Port}。");
    }
    /// <summary>Immediately ends the current microphone route while preserving the listener.</summary>
    public void Disconnect(){lock(_gate){_session?.StopImmediate();SetConnectedLocked(false);}}
    private async Task RunAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            Session? session=null;
            try
            {
                var client=await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);client.NoDelay=true;
                lock(_gate){if(_disposed){client.Dispose();break;}session=new Session(client,ct);_session=session;}
                var peer=((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                if(!_isPeer(peer))continue;
                var token=session.Cancellation.Token;
                lock(_gate){token.ThrowIfCancellationRequested();session.Secure=new SslStream(client.GetStream(),true);}
                var secure=session.Secure;
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(PairedTlsTransport.Timeout);
                    var accepted=await PairedTlsTransport.AuthenticateServerAsync(secure,_certificate,_secret,SharedMicrophonePcm.Context,()=>
                    {
                        lock(_gate)
                        {
                            if(_disposed||token.IsCancellationRequested||!_isPeer(peer)||!_isDemandAllowed())return false;
                            try{session.Sink=_sinkFactory();return true;}
                            catch(Exception ex){Log("[提示] 共享麦克风虚拟输入未就绪："+ex.Message);return false;}
                        }
                    },timeout.Token).ConfigureAwait(false);
                    if(!accepted)continue;
                }
                var header=new byte[12];using var read=CancellationTokenSource.CreateLinkedTokenSource(token);
                read.CancelAfter(SharedMicrophonePcm.IdleTimeout);await secure.ReadExactlyAsync(header,read.Token).ConfigureAwait(false);
                SharedMicrophonePcm.ValidateHeader(header);Interlocked.Exchange(ref _receivedBytes,0);
                var data=new byte[SharedMicrophonePcm.ChunkBytes+3];var tail=0;
                Log($"[LinkBridge] 对端麦克风送入虚拟设备：{session.Sink?.EndpointName}；应用需选择此虚拟输入。");
                while(!token.IsCancellationRequested)
                {
                    read.CancelAfter(SharedMicrophonePcm.IdleTimeout);
                    var count=await secure.ReadAsync(data.AsMemory(tail,SharedMicrophonePcm.ChunkBytes-tail),read.Token).ConfigureAwait(false);
                    if(count==0){if(tail!=0)throw new InvalidDataException("网络在不完整麦克风PCM帧处结束。");break;}
                    var total=tail+count;var complete=total-total%4;
                    lock(_gate)
                    {
                        if(_disposed||session.Sink is null||!_isPeer(peer)||!_isDemandAllowed())break;
                        if(complete>0){session.Sink.Write(data.AsSpan(0,complete));Interlocked.Add(ref _receivedBytes,complete);SetConnectedLocked(true);}
                    }
                    tail=total-complete;if(tail>0)Buffer.BlockCopy(data,complete,data,0,tail);
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex)when(!ct.IsCancellationRequested){Log("[提示] 共享麦克风接收结束："+ex.Message);}
            catch(Exception)when(ct.IsCancellationRequested){break;}
            finally
            {
                if(session is not null)
                {
                    lock(_gate){session.StopImmediate();if(ReferenceEquals(_session,session))_session=null;SetConnectedLocked(false);}
                    session.Cancellation.Dispose();
                }
            }
        }
    }
    private void SetConnectedLocked(bool connected)
    {
        if(_connected==connected)return;_connected=connected;
        try{ConnectionChanged?.Invoke(connected);}catch(Exception ex){Log("[提示] 麦克风状态回调失败："+ex.Message);}
    }
    private void Log(string message){try{LogEmitted?.Invoke(message);}catch{}}
    public ValueTask DisposeAsync()
    {
        Task? task;
        lock(_gate)
        {
            if(_disposed)return ValueTask.CompletedTask;_disposed=true;
            _stop.Cancel();_listener.Stop();_session?.StopImmediate();SetConnectedLocked(false);task=_task;
        }
        return new ValueTask(FinishDisposeAsync(task));
    }
    private async Task FinishDisposeAsync(Task? task)
    {
        try{if(task is not null)await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);}
        catch(TimeoutException){Log("[提示] 麦克风清理等待超时，网络与输出已同步关闭。");}
        finally{if(task is null||task.IsCompleted)Cleanup();else _=task.ContinueWith(_=>Cleanup(),TaskScheduler.Default);}
    }
    private void Cleanup(){_certificate.Dispose();CryptographicOperations.ZeroMemory(_secret);_stop.Dispose();}
}
