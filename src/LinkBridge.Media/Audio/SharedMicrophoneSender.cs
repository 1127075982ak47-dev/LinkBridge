using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using LinkBridge.Core.Models;
using LinkBridge.Core.Security;

namespace LinkBridge.Media.Audio;

/// <summary>Demand-gated physical microphone capture to a mutually authenticated virtual input.</summary>
public sealed class SharedMicrophoneSender(int port=SharedMicrophoneReceiver.DefaultPort) : IAsyncDisposable
{
    private readonly object _gate=new();
    private readonly SemaphoreSlim _startGate=new(1);
    private Session? _session;
    private bool _disposed;
    private long _sentBytes;
    public event Action<string>? LogEmitted;
    public event Action? StreamEnded;
    public long SentBytes=>Interlocked.Read(ref _sentBytes);
    public bool IsRunning { get { lock(_gate)return _session?.Capture is not null; } }
    private sealed class Session(TcpClient client,CancellationTokenSource cancellation)
    {
        public readonly TcpClient Client=client;
        public readonly CancellationTokenSource Cancellation=cancellation;
        public SslStream? Secure;
        public ISharedMicrophoneCapture? Capture;
        public Task? Pump;
        private int _cleaned;
        public void StopImmediate()
        {
            Cancellation.Cancel();
            try { Capture?.Stop(); } catch { }
            Client.Dispose();try { Secure?.Dispose(); } catch { }
        }
        public void Cleanup()
        {
            if(Interlocked.Exchange(ref _cleaned,1)!=0)return;
            try { Capture?.Dispose(); } catch { }
            try { Secure?.Dispose(); } catch { }
            Client.Dispose();Cancellation.Dispose();
        }
    }
    public Task StartAsync(string host,string secret,MediaDevice source,bool sharingRequested,CancellationToken ct=default)=>
        StartPcmAsync(host,secret,token=>SharedMicrophoneCapture.OpenAsync(source,line=>Log(line),token),sharingRequested,ct);

    /// <summary>Injectable source factory. It is invoked only after LBM1 TLS and sink permission succeed.</summary>
    public async Task StartPcmAsync(string host,string secret,Func<CancellationToken,ValueTask<ISharedMicrophoneCapture>> captureFactory,
        bool sharingRequested,CancellationToken ct=default)
    {
        if(!sharingRequested)throw new InvalidOperationException("应用尚未请求麦克风，也未手动开启麦克风共享。");
        ArgumentNullException.ThrowIfNull(captureFactory);
        if(port is <1 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        var key=PairedTlsTransport.SecretBytes(secret);
        var acquired=false;Session? session=null;
        try
        {
            await _startGate.WaitAsync(ct).ConfigureAwait(false);acquired=true;
            await StopAsync().ConfigureAwait(false);
            lock(_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed,this);
                session=new Session(new TcpClient{NoDelay=true},CancellationTokenSource.CreateLinkedTokenSource(ct));_session=session;
            }
            var token=session.Cancellation.Token;
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(PairedTlsTransport.Timeout);
                await session.Client.ConnectAsync(host,port,timeout.Token).ConfigureAwait(false);
                lock(_gate){token.ThrowIfCancellationRequested();session.Secure=new SslStream(session.Client.GetStream(),false);}
                var secure=session.Secure;
                await PairedTlsTransport.AuthenticateClientAsync(secure,key,SharedMicrophonePcm.Context,timeout.Token).ConfigureAwait(false);
                await secure.WriteAsync(SharedMicrophonePcm.Header(),timeout.Token).ConfigureAwait(false);
            }
            using var captureDeadline=CancellationTokenSource.CreateLinkedTokenSource(token);
            // Leave room within the receiver's 8s idle deadline; a slow device
            // enumeration must not open a microphone after the peer has timed out.
            captureDeadline.CancelAfter(TimeSpan.FromSeconds(6));
            var capture=await captureFactory(captureDeadline.Token).ConfigureAwait(false);
            lock(_gate)
            {
                if(captureDeadline.IsCancellationRequested||_disposed||!ReferenceEquals(_session,session)){capture.Stop();capture.Dispose();throw new OperationCanceledException(captureDeadline.Token);}
                session.Capture=capture;Interlocked.Exchange(ref _sentBytes,0);
                var current=session;session.Pump=Task.Run(()=>PumpAsync(current,token));
            }
            Log("[LinkBridge] 共享麦克风LBM1 TLS通道已认证；采集已启动，等待48kHz/16bit/双声道PCM。");
        }
        catch
        {
            if(session is not null)
            {
                lock(_gate){if(ReferenceEquals(_session,session))_session=null;session.StopImmediate();}
                session.Cleanup();
            }
            throw;
        }
        finally{CryptographicOperations.ZeroMemory(key);if(acquired)_startGate.Release();}
    }
    private async Task PumpAsync(Session session,CancellationToken ct)
    {
        try
        {
            // Fixed-size buffer and TCP backpressure: no unbounded producer queue.
            var data=new byte[SharedMicrophonePcm.ChunkBytes+3];var tail=0;
            using var read=CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var write=CancellationTokenSource.CreateLinkedTokenSource(ct);
            var source=session.Capture!.PcmStream;
            while(!ct.IsCancellationRequested)
            {
                read.CancelAfter(SharedMicrophonePcm.IdleTimeout);
                var received=await source.ReadAsync(data.AsMemory(tail,SharedMicrophonePcm.ChunkBytes-tail),read.Token).ConfigureAwait(false);
                read.CancelAfter(Timeout.InfiniteTimeSpan);
                if(received==0){if(tail!=0)throw new InvalidDataException("麦克风采集在不完整PCM帧处结束。");break;}
                var total=tail+received;var complete=total-total%4;
                if(complete>0)
                {
                    write.CancelAfter(SharedMicrophonePcm.WriteTimeout);
                    await session.Secure!.WriteAsync(data.AsMemory(0,complete),write.Token).ConfigureAwait(false);
                    write.CancelAfter(Timeout.InfiniteTimeSpan);Interlocked.Add(ref _sentBytes,complete);
                }
                tail=total-complete;if(tail>0)Buffer.BlockCopy(data,complete,data,0,tail);
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(Exception ex){if(!ct.IsCancellationRequested)Log("[提示] 共享麦克风发送结束："+ex.Message);}
        finally
        {
            bool ended;
            lock(_gate){ended=ReferenceEquals(_session,session);if(ended)_session=null;session.StopImmediate();}
            session.Cleanup();
            if(ended){try{StreamEnded?.Invoke();}catch(Exception ex){Log("[提示] 麦克风结束回调失败："+ex.Message);}}
        }
    }
    public ValueTask StopAsync()
    {
        Session? session;
        lock(_gate){session=_session;_session=null;session?.StopImmediate();}
        return session is null?ValueTask.CompletedTask:new ValueTask(FinishStopAsync(session));
    }
    private static async Task FinishStopAsync(Session session)
    {
        // While Start's async factory is pending, Start retains ownership of cleanup.
        if(session.Pump is null)return;
        try{await session.Pump.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);}
        catch(TimeoutException){_=session.Pump.ContinueWith(_=>session.Cleanup(),TaskScheduler.Default);}
    }
    public ValueTask DisposeAsync(){lock(_gate)_disposed=true;return StopAsync();}
    private void Log(string message){try{LogEmitted?.Invoke(message);}catch{}}
}
