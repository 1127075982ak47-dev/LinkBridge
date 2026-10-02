using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace LinkBridge.Input;

public sealed class InputBridgeServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly object _lifecycleGate = new();
    private readonly IPAddress _allowedAddress;
    private readonly byte[] _secret;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private TcpClient? _activeClient;
    private int _disposed;
    private X509Certificate2? _certificate;

    public event Action<string>? LogEmitted;
    public event Action<bool>? ConnectionChanged;

    public InputBridgeServer(string allowedAddress, string secret, int port = InputBridgeClient.Port)
    {
        if (!IPAddress.TryParse(allowedAddress, out var address) || secret.Length != 32)
            throw new ArgumentException("键鼠服务需要指定控制端 IP 和 32 位配对密钥。");
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _allowedAddress = address;
        _secret = InputSecureHandshake.SecretBytes(secret);
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_task is not null) return;
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            if (!MacInputInjector.IsTrusted())
                LogEmitted?.Invoke("[LinkBridge] 等待设备控制授权：请在联桥的连接页面请求权限，服务状态会实时更新。");
            _certificate = InputSecureHandshake.CreateServerCertificate();
            try { _listener.Start(8); } catch { _certificate.Dispose(); _certificate = null; throw; }
            _task = Task.Run(RunAsync);
            LogEmitted?.Invoke($"[LinkBridge] 键鼠接收服务已监听 TCP {((IPEndPoint)_listener.LocalEndpoint).Port} · LBI3 TLS");
        }
    }
    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (!_cts.IsCancellationRequested) LogEmitted?.Invoke($"[错误] 键鼠监听中断：{ex.Message}");
                break;
            }
            using (client)
            {
                var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                if (!address.Equals(_allowedAddress)) continue;
                _activeClient = client;
                client.NoDelay = true;
                try
                {
                    using var authWait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    authWait.CancelAfter(InputSecureHandshake.Timeout);
                    using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: true);
                    var permitted = await InputSecureHandshake.AuthenticateServerAsync(stream, _certificate!, _secret,
                        MacInputInjector.IsTrusted, authWait.Token).ConfigureAwait(false);
                    if (!permitted)
                    {
                        LogEmitted?.Invoke("[LinkBridge] 已认证的键鼠连接未获Mac辅助功能授权。");
                        continue;
                    }
                    LogEmitted?.Invoke($"[LinkBridge] 键鼠控制已加密连接：{address} · LBI3 TLS");
                    ConnectionChanged?.Invoke(true);
                    var injector = new MacInputInjector();
                    await RunSessionAsync(stream, injector.Apply, injector.ReleaseAll, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                { LogEmitted?.Invoke("[LinkBridge] 键鼠心跳/认证超时，已释放全部远端按键"); }
                catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException or ObjectDisposedException)
                {
                    if (!_cts.IsCancellationRequested) LogEmitted?.Invoke($"[LinkBridge] 键鼠连接结束：{ex.Message}");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { LogEmitted?.Invoke($"[错误] 键鼠接收中断：{ex.Message}"); }
                finally { _activeClient = null; ConnectionChanged?.Invoke(false); }
                LogEmitted?.Invoke("[LinkBridge] 键鼠控制已断开，全部按键已释放");
            }
        }
    }

    // The transport is separated from native injection so framing, deadlines and release can be tested safely.
    internal static async Task RunSessionAsync(Stream stream, Action<InputPacket> apply, Action release, CancellationToken ct)
    {
        var buffer = new byte[InputPacket.Size];
        var ackBuffer = new byte[InputPacket.Size];
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var write = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lastHeartbeat = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                idle.CancelAfter(InputProtocol.IdleTimeout);
                await stream.ReadExactlyAsync(buffer, idle.Token).ConfigureAwait(false);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                var packet = InputPacket.Read(buffer);
                if (packet.Type == InputEventType.Heartbeat)
                {
                    if (packet.X <= lastHeartbeat) throw new IOException("无效或重复的键鼠心跳。");
                    lastHeartbeat = packet.X;
                    new InputPacket(InputEventType.HeartbeatAck, 0, 0, packet.X, 0).Write(ackBuffer);
                    write.CancelAfter(InputProtocol.WriteTimeout);
                    await stream.WriteAsync(ackBuffer, write.Token).ConfigureAwait(false);
                    write.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                else if (packet.Type is InputEventType.Key or InputEventType.Move or InputEventType.Button or InputEventType.Wheel or InputEventType.FileDrag)
                {
                    if (packet.Type == InputEventType.Key && (packet.Code > 1 || packet.Key > 255) ||
                        packet.Type == InputEventType.Button && (packet.Code > 2 || packet.X is < 0 or > 1) ||
                        packet.Type == InputEventType.FileDrag && (packet.Code > 1 || packet.Key != 0 || packet.X != 0 || packet.Y != 0))
                        throw new IOException("无效的键鼠输入包。");
                    apply(packet);
                }
                else throw new IOException("键鼠协议版本不一致：需要 LBI3 TLS 心跳协议。");
            }
        }
        finally { release(); }
    }

    public ValueTask DisposeAsync()
    {
        Task? task;
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            _cts.Cancel();
            _activeClient?.Dispose();
            _listener.Stop();
            task = _task;
        }
        return new ValueTask(FinishDisposeAsync(task));
    }
    private async Task FinishDisposeAsync(Task? task)
    {
        try { if (task is not null) await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _ = task!.ContinueWith(_ => DisposeSecurity(), TaskScheduler.Default);
            return;
        }
        finally { if (task is null || task.IsCompleted) DisposeSecurity(); }
    }
    private void DisposeSecurity()
    {
        _certificate?.Dispose(); _certificate = null;
        CryptographicOperations.ZeroMemory(_secret);
        _cts.Dispose();
    }
}
