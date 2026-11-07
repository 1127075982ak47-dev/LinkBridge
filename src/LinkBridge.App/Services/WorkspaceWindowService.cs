using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia.Threading;
using LinkBridge.App.Views;
using LinkBridge.Core.Workspace;
using LinkBridge.Core.WindowRoaming;

namespace LinkBridge.App.Services;
public sealed record SharedWindowLayout(Guid SessionId,string Title,int X,int Y,int Width,int Height,int FrameWidth,int FrameHeight);

public sealed class WorkspaceWindowService : IAsyncDisposable
{
    private readonly WorkspaceProfile _profile;
    private readonly CancellationTokenSource _stop=new();
    private TcpListener? _listener;
    private Task? _accept,_session;
    private CancellationTokenSource? _sessionStop;
    private int _busy;
    public string Status {get;private set;}="窗口服务未启动";
    public event Action<string>? StatusChanged;
    public bool Busy=>Volatile.Read(ref _busy)!=0;
    public WorkspaceWindowService(WorkspaceProfile profile)=>_profile=profile;
    private void State(string text){Status=text;StatusChanged?.Invoke(text);}
    public void Start()
    {
        if(_listener is not null)return;
        _listener=new TcpListener(IPAddress.IPv6Any,_profile.SharingPort);_listener.Server.DualMode=true;_listener.Start(8);_accept=Accept();State("已监听 · 只接收工作区内已信任设备的窗口");
    }
    public void ReturnAll()=>_sessionStop?.Cancel();
    private async Task Accept()
    {
        while(!_stop.IsCancellationRequested)
        {
            TcpClient client;try{client=await _listener!.AcceptTcpClientAsync(_stop.Token);}catch when(_stop.IsCancellationRequested){break;}
            if(!_profile.AcceptWindows||Interlocked.CompareExchange(ref _busy,1,0)!=0){client.Dispose();continue;}
            _session=Receive(client);
        }
    }
    private static void Validate(SharedWindowLayout g)
    {
        if(g.SessionId==Guid.Empty||g.Title.Length>512||g.Width is < 100 or > 8192||g.Height is < 80 or > 8192||Math.Abs((long)g.X)>100000||Math.Abs((long)g.Y)>100000)
            throw new InvalidDataException("窗口位置或尺寸无效。");
        WindowRoamingLimits.FrameBytes(g.FrameWidth,g.FrameHeight);
    }
    private async Task Heartbeat(WorkspaceTransport wire,CancellationToken ct)
    {while(!ct.IsCancellationRequested){await Task.Delay(2500,ct);await wire.SendAsync(8,ReadOnlyMemory<byte>.Empty,ct);}}
    private async Task Receive(TcpClient client)
    {
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);_sessionStop=cancel;WindowSliceView? view=null;
        try
        {
            await using var wire=await WorkspaceTransport.AcceptAsync(_profile,client,cancel.Token);
            var offer=await wire.ReadAsync(cancel.Token);if(offer.Type!=1)throw new InvalidDataException("需要窗口邀请。");
            var geometry=JsonSerializer.Deserialize<SharedWindowLayout>(offer.Data)??throw new InvalidDataException();Validate(geometry);
            var sessionId=geometry.SessionId;
            var actions=Channel.CreateBounded<(byte Type,byte[] Bytes)>(new BoundedChannelOptions(128){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
            await Dispatcher.UIThread.InvokeAsync(()=>
            {
                view=new WindowSliceView(_profile.Local,geometry);
                view.InputRequested+=input=>{if(!actions.Writer.TryWrite((3,JsonSerializer.SerializeToUtf8Bytes(input))))cancel.Cancel();};
                view.LayoutRequested+=g=>{if(!actions.Writer.TryWrite((4,JsonSerializer.SerializeToUtf8Bytes(g))))cancel.Cancel();};
                view.ReturnRequested+=()=>cancel.Cancel();view.Show();
            });
            var writer=Task.Run(async()=>{await foreach(var item in actions.Reader.ReadAllAsync(cancel.Token))await wire.SendAsync(item.Type,item.Bytes,cancel.Token);},cancel.Token);
            var beat=Heartbeat(wire,cancel.Token);
            await wire.SendAsync(6,ReadOnlyMemory<byte>.Empty,cancel.Token);State("窗口已接入 · 等待真实画面");
            try
            {
                while(!cancel.IsCancellationRequested)
                {
                    var m=await wire.ReadAsync(cancel.Token);
                    if(m.Type==5)break;
                    if(m.Type==2){var pixels=Decode(m.Data,geometry.FrameWidth,geometry.FrameHeight);await Dispatcher.UIThread.InvokeAsync(()=>view!.SetFrame(pixels,geometry.FrameWidth,geometry.FrameHeight));State("窗口跨机显示中 · Esc 归还源窗口");}
                    else if(m.Type==4){var next=JsonSerializer.Deserialize<SharedWindowLayout>(m.Data)??throw new InvalidDataException();Validate(next);if(next.SessionId!=sessionId)throw new InvalidDataException();geometry=next;await Dispatcher.UIThread.InvokeAsync(()=>view!.SetLayout(geometry));}
                    else if(m.Type==7){var s=JsonSerializer.Deserialize<string>(m.Data)??"";State(s.Length<=200?s:"窗口状态异常");}
                    else if(m.Type!=8)throw new InvalidDataException("窗口消息类型不符。");
                }
            }
            finally{cancel.Cancel();actions.Writer.TryComplete();try{await Task.WhenAll(writer,beat);}catch(OperationCanceledException){}catch(IOException){} }
        }
        catch(OperationCanceledException){}
        catch(Exception e){State("窗口连接结束 · "+e.Message);}
        finally{cancel.Cancel();if(view is not null)await Dispatcher.UIThread.InvokeAsync(()=>view.CloseSession());client.Dispose();_sessionStop=null;Interlocked.Exchange(ref _busy,0);}
    }
    public Task SendAsync(WindowCaptureTarget target,WorkspaceDevice peer)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("当前可验证的离屏窗口源端是 Windows；Mac/Linux 源端暂未开放。");
        if(!_profile.Enabled)throw new InvalidOperationException("请先启用通用工作区。");
        if(Interlocked.CompareExchange(ref _busy,1,0)!=0)throw new InvalidOperationException("已有窗口会话，请先归还。");
        _session=SendCore(target,peer);return _session;
    }
    private async Task SendCore(WindowCaptureTarget target,WorkspaceDevice peer)
    {
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);_sessionStop=cancel;
        BorrowedWindow? native=null;WindowsWindowCapture? capture=null;WindowSliceView? local=null;
        var frames=Channel.CreateBounded<WindowCaptureFrame>(new BoundedChannelOptions(1){SingleReader=true,FullMode=BoundedChannelFullMode.DropOldest},frame=>frame.Dispose());
        try
        {
            native=new BorrowedWindow(target.Hwnd);
            capture=new WindowsWindowCapture();capture.Frame+=frame=>{if(!frames.Writer.TryWrite(frame))frame.Dispose();};capture.Failed+=s=>{State(s);cancel.Cancel();};
            capture.Start(target.Hwnd,25);
            using var first=await frames.Reader.ReadAsync(cancel.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var links=WorkspaceTopology.Links(_profile).Where(l=>l.SourceId==_profile.LocalDeviceId&&l.TargetId==peer.Id).ToArray();
            if(links.Length==0)throw new InvalidOperationException("目标设备需要与本机具有接缝连接。");
            var link=links[0];var width=first.Width;var height=first.Height;
            var x=link.Direction=="right"?peer.X-width/2:link.Direction=="left"?peer.X+peer.Width-width/2:_profile.Local.X+Math.Max(0,native.X);
            var y=link.Direction=="down"?peer.Y-height/2:link.Direction=="up"?peer.Y+peer.Height-height/2:_profile.Local.Y+Math.Clamp(native.Y,0,Math.Max(0,_profile.Local.Height-height));
            var geometry=new SharedWindowLayout(Guid.NewGuid(),target.Title,x,y,width,height,width,height);
            Validate(geometry);
            await using var wire=await WorkspaceTransport.ConnectAsync(_profile,peer,cancel.Token);
            await wire.SendJsonAsync(1,geometry,cancel.Token);var ready=await wire.ReadAsync(cancel.Token);if(ready.Type!=6)throw new InvalidDataException("对端没有准备窗口接收。");
            await Dispatcher.UIThread.InvokeAsync(()=>
            {
                native.Begin();
                local=new WindowSliceView(_profile.Local,geometry);
                local.InputRequested+=input=>{if(!native.Apply(input))State("此应用没有接受窗口输入，可归还后继续使用原生窗口。");};
                local.ReturnRequested+=()=>cancel.Cancel();local.Show();
            });
            var moves=Channel.CreateBounded<SharedWindowLayout>(new BoundedChannelOptions(1){SingleReader=true,FullMode=BoundedChannelFullMode.DropOldest});
            local!.LayoutRequested+=g=>moves.Writer.TryWrite(g);
            var moving=Task.Run(async()=>{await foreach(var g in moves.Reader.ReadAllAsync(cancel.Token)){geometry=g;await wire.SendJsonAsync(4,g,cancel.Token);}},cancel.Token);
            var reader=Task.Run(async()=>
            {
                while(!cancel.IsCancellationRequested)
                {
                    var m=await wire.ReadAsync(cancel.Token);
                    if(m.Type==5)break;
                    if(m.Type==3)
                    {
                        var input=JsonSerializer.Deserialize<WindowRemoteInput>(m.Data)??throw new InvalidDataException();WindowRoamingLimits.Validate(input);
                        if(input.SessionId!=geometry.SessionId||input.Width!=geometry.FrameWidth||input.Height!=geometry.FrameHeight)throw new InvalidDataException("输入会话不匹配。");
                        var accepted=await Dispatcher.UIThread.InvokeAsync(()=>native.Apply(input));
                        if(!accepted)await wire.SendJsonAsync(7,"源应用不接受此输入，请归还窗口后操作。",cancel.Token);
                    }
                    else if(m.Type==4){var g=JsonSerializer.Deserialize<SharedWindowLayout>(m.Data)??throw new InvalidDataException();Validate(g);if(g.SessionId!=geometry.SessionId||g.FrameWidth!=width||g.FrameHeight!=height)throw new InvalidDataException();geometry=g;await Dispatcher.UIThread.InvokeAsync(()=>local.SetLayout(g));}
                    else if(m.Type!=8)throw new InvalidDataException("窗口消息不符。");
                }
                cancel.Cancel();
            },cancel.Token);
            var beat=Heartbeat(wire,cancel.Token);
            try
            {
                await wire.SendAsync(2,Encode(first),cancel.Token);await Dispatcher.UIThread.InvokeAsync(()=>local.SetFrame(first.Data.AsSpan(0,first.Length).ToArray(),width,height));
                State("源窗口已接管 · 两端显示各自片段 · 关闭接力视图可归还");
                while(!cancel.IsCancellationRequested)
                {
                    using var frame=await frames.Reader.ReadAsync(cancel.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                    if(frame.Width!=width||frame.Height!=height)throw new IOException("源窗口尺寸发生变化，请归还后重新接管。");
                    var data=frame.Data.AsSpan(0,frame.Length).ToArray();
                    await wire.SendAsync(2,Encode(frame),cancel.Token);await Dispatcher.UIThread.InvokeAsync(()=>local.SetFrame(data,width,height));
                }
            }
            finally{cancel.Cancel();moves.Writer.TryComplete();try{await Task.WhenAll(reader,moving,beat);}catch(OperationCanceledException){}catch(IOException){} }
        }
        catch(OperationCanceledException){}
        catch(Exception e){State("窗口已归还 · "+e.Message);}
        finally
        {
            cancel.Cancel();capture?.Dispose();frames.Writer.TryComplete();while(frames.Reader.TryRead(out var f))f.Dispose();
            await Dispatcher.UIThread.InvokeAsync(()=>{local?.CloseSession();native?.Dispose();});_sessionStop=null;Interlocked.Exchange(ref _busy,0);
        }
    }
    private static byte[] Encode(WindowCaptureFrame frame)
    {using var stream=new MemoryStream();using(var zip=new ZLibStream(stream,CompressionLevel.Fastest,true))zip.Write(frame.Data,0,frame.Length);return stream.ToArray();}
    private static byte[] Decode(byte[] data,int w,int h)
    {var result=new byte[WindowRoamingLimits.FrameBytes(w,h)];using var input=new MemoryStream(data);using var zip=new ZLibStream(input,CompressionMode.Decompress);zip.ReadExactly(result);if(zip.ReadByte()!=-1)throw new InvalidDataException("画面长度不符。");return result;}
    public async ValueTask DisposeAsync()
    {_stop.Cancel();_sessionStop?.Cancel();_listener?.Stop();if(_session is not null)try{await _session.WaitAsync(TimeSpan.FromSeconds(5));}catch{}if(_accept is not null)try{await _accept;}catch{} }
}
