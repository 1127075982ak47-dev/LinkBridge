using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LinkBridge.Core.Security;

namespace LinkBridge.Core.Clipboard;

/// <summary>One short paired push per connection. The caller owns clipboard loop suppression by content hash.</summary>
public sealed class ClipboardBridgeServer : IAsyncDisposable
{
    private readonly IPAddress _allowed;
    private readonly byte[] _key;
    private readonly Func<ClipboardPayload, CancellationToken, Task> _received;
    private readonly int _port;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly ConcurrentDictionary<TcpClient, Task> _clients = new();
    private readonly Queue<Guid> _recentIds = new();
    private readonly HashSet<Guid> _seenIds = [];
    private X509Certificate2? _certificate;
    private TcpListener? _listener;
    private Task? _accept;
    private int _disposed;
    /// <summary>Metadata-only diagnostics: never includes contents, paths, hashes, ids, keys or exception messages.</summary>
    public event Action<string>? Diagnostic;
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _port;

    public ClipboardBridgeServer(string allowedAddress, string secret, Func<ClipboardPayload, CancellationToken, Task> received,
        int port = ClipboardLimits.DefaultPort)
    {
        _allowed = IPAddress.Parse(allowedAddress).MapToIPv6();
        _key = PairedTlsTransport.SecretBytes(secret);
        _received = received ?? throw new ArgumentNullException(nameof(received));
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
    }
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_listener is not null) throw new InvalidOperationException("剪贴板接收服务已经启动。");
        _certificate = PairedTlsTransport.CreateServerCertificate(ClipboardLimits.ProtocolContext);
        _listener = new TcpListener(IPAddress.IPv6Any, _port); _listener.Server.DualMode = true; _listener.Start(8);
        _accept = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            if ((client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv6().Equals(_allowed) != true || _clients.Count >= 8)
            { client.Dispose(); continue; }
            client.NoDelay = true;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _clients[client] = completed.Task;
            _ = RunTrackedAsync(client, completed);
        }
    }
    private async Task RunTrackedAsync(TcpClient client, TaskCompletionSource completion)
    {
        try { await HandleAsync(client).ConfigureAwait(false); }
        catch { Emit("剪贴板请求失败"); }
        finally { client.Dispose(); completion.TrySetResult(); _clients.TryRemove(client, out _); }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using var stream = new SslStream(client.GetStream(), false);
        var ownsSlot = false; Guid id = Guid.Empty;
        try
        {
            using (var authentication = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                authentication.CancelAfter(PairedTlsTransport.Timeout);
                if (!await PairedTlsTransport.AuthenticateServerAsync(stream, _certificate!, _key, ClipboardLimits.ProtocolContext,
                        () => ownsSlot = _slot.Wait(0), authentication.Token).ConfigureAwait(false)) return;
            }
            ClipboardPayload payload;
            using (var transfer = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                transfer.CancelAfter(ClipboardLimits.TransferTimeout);
                payload = await ClipboardWire.ReceiveAsync(stream, transfer.Token).ConfigureAwait(false);
            }
            id = payload.Id;
            if (_seenIds.Contains(id)) throw new InvalidDataException("剪贴板请求重复。");
            using (var apply = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                apply.CancelAfter(ClipboardLimits.ApplyTimeout);
                var monitor = MonitorDisconnectAsync(stream, apply);
                try
                {
                    apply.Token.ThrowIfCancellationRequested();
                    await _received(payload, apply.Token).ConfigureAwait(false);
                    apply.Token.ThrowIfCancellationRequested();
                }
                finally { await apply.CancelAsync().ConfigureAwait(false); await monitor.ConfigureAwait(false); }
            }
            _seenIds.Add(id); _recentIds.Enqueue(id);
            if (_recentIds.Count > 256) _seenIds.Remove(_recentIds.Dequeue());
            await ClipboardWire.AcknowledgeAsync(stream, id, true, _stop.Token).ConfigureAwait(false);
        }
        catch
        {
            // Do not expose clipboard text through exception messages from a clipboard provider.
            Emit("剪贴板请求未应用或已取消");
            try
            {
                using var failure = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ClipboardWire.AcknowledgeAsync(stream, id, false, failure.Token).ConfigureAwait(false);
            }
            catch { }
        }
        finally { if (ownsSlot) _slot.Release(); }
    }
    private static async Task MonitorDisconnectAsync(SslStream stream, CancellationTokenSource lifetime)
    {
        try
        {
            // No application bytes follow this payload. A read detects TLS close_notify
            // and TCP EOF too; polling raw socket bytes can miss an encrypted close_notify.
            await stream.ReadAsync(new byte[1], lifetime.Token).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { await lifetime.CancelAsync().ConfigureAwait(false); }
    }
    private void Emit(string message) { try { Diagnostic?.Invoke(message); } catch { } }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false); _listener?.Stop();
        if (_accept is not null) await _accept.ConfigureAwait(false);
        foreach (var client in _clients.Keys) client.Dispose();
        await Task.WhenAll(_clients.Values).ConfigureAwait(false);
        _certificate?.Dispose(); CryptographicOperations.ZeroMemory(_key); _slot.Dispose(); _stop.Dispose();
    }
}
