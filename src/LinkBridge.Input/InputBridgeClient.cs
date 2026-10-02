using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Threading.Channels;

namespace LinkBridge.Input;

public sealed class InputBridgeClient : IAsyncDisposable
{
    public const int Port = 45680;
    private readonly TcpClient _client;
    private readonly SslStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<InputPacket> _packets = Channel.CreateBounded<InputPacket>(
        new BoundedChannelOptions(InputProtocol.QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _writer, _reader, _heartbeat;
    private int _closed, _disposed;
    private long _lastAckTick = Environment.TickCount64;
    private int _heartbeatSequence, _lastAckSequence;

    private InputBridgeClient(TcpClient client, SslStream stream)
    {
        _client = client;
        _stream = stream;
        _writer = Task.Run(WriteLoopAsync);
        _reader = Task.Run(ReadLoopAsync);
        _heartbeat = Task.Run(HeartbeatLoopAsync);
    }

    public event Action? Disconnected;
    public bool IsConnected => Volatile.Read(ref _closed) == 0;
    public int PendingPackets => _packets.Reader.Count;
    public string? DisconnectReason { get; private set; }

    public static Task<InputBridgeClient> ConnectAsync(string address, string secret, CancellationToken ct = default)
        => ConnectAsync(address, secret, Port, ct);

    /// <summary>The port overload allows isolated transport tests on loopback ports.</summary>
    public static async Task<InputBridgeClient> ConnectAsync(string address, string secret, int port, CancellationToken ct = default)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var key = InputSecureHandshake.SecretBytes(secret);
        var client = new TcpClient { NoDelay = true };
        SslStream? stream = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InputSecureHandshake.Timeout);
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await InputSecureHandshake.AuthenticateClientAsync(stream, address, key, timeout.Token).ConfigureAwait(false);
            return new InputBridgeClient(client, stream);
        }
        catch { client.Dispose(); stream?.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public bool TrySend(InputPacket packet)
    {
        if (!IsConnected) return false;
        if ((byte)packet.Type == 0) return true; // Legacy VM timer: the transport now owns heartbeat delivery.
        if (_packets.Writer.TryWrite(packet)) return true;
        FailConnection("输入发送积压，已恢复本机控制");
        return false;
    }

    private void FailConnection(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        DisconnectReason = reason;
        _packets.Writer.TryComplete();
        _cts.Cancel();
        _client.Dispose();
        _stream.Dispose();
        // A full queue can be detected in a low-level hook. Subscriber work belongs outside that hook.
        ThreadPool.QueueUserWorkItem(_ => { try { Disconnected?.Invoke(); } catch { } });
    }

    private async Task WriteLoopAsync()
    {
        var buffer = new byte[InputPacket.Size];
        try
        {
            var stream = _stream;
            using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            await foreach (var packet in _packets.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                packet.Write(buffer);
                writeTimeout.CancelAfter(InputProtocol.WriteTimeout);
                await stream.WriteAsync(buffer, writeTimeout.Token).ConfigureAwait(false);
                writeTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        { FailConnection("输入发送中断或超时，已恢复本机控制"); }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[InputPacket.Size];
        try
        {
            var stream = _stream;
            while (!_cts.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(buffer, _cts.Token).ConfigureAwait(false);
                var packet = InputPacket.Read(buffer);
                if (packet.Type != InputEventType.HeartbeatAck || packet.X <= 0 ||
                    packet.X > Volatile.Read(ref _heartbeatSequence) || packet.X <= Volatile.Read(ref _lastAckSequence))
                    throw new IOException("无效的键鼠心跳响应。");
                Volatile.Write(ref _lastAckSequence, packet.X);
                Volatile.Write(ref _lastAckTick, Environment.TickCount64);
            }
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        { FailConnection("Mac 输入连接已断开，已恢复本机控制"); }
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (Environment.TickCount64 - Volatile.Read(ref _lastAckTick) >= InputProtocol.IdleTimeout.TotalMilliseconds)
                { FailConnection("Mac 心跳超时，已恢复本机控制"); return; }
                if (!TrySend(new InputPacket(InputEventType.Heartbeat, 0, 0, Interlocked.Increment(ref _heartbeatSequence), 0))) return;
                await Task.Delay(InputProtocol.HeartbeatInterval, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _closed, 1);
        _packets.Writer.TryComplete();
        _cts.Cancel();
        _client.Dispose();
        _stream.Dispose();
        var all = Task.WhenAll(_writer, _reader, _heartbeat);
        try { await all.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { _ = all.ContinueWith(_ => _cts.Dispose(), TaskScheduler.Default); return; }
        finally { if (all.IsCompleted) _cts.Dispose(); }
    }
}




