using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LinkBridge.Core.Security;

namespace LinkBridge.Core.DisplayLayout;

public sealed record LayoutRequest(string Action, ScreenLayout? Layout = null);
public sealed record LayoutResponse(bool Ok, LayoutSnapshot? Snapshot = null, string Error = "");

/// <summary>Bounded request/response channel authenticated by the existing pairing identity.</summary>
public sealed class LayoutTransport : IAsyncDisposable
{
    public const int DefaultPort = 45691;
    private readonly byte[] _key;
    private readonly Func<string,bool> _allowed;
    private readonly Func<LayoutRequest,CancellationToken,Task<LayoutResponse>> _handle;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient,Task> _clients = new();
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _cert;
    private readonly SemaphoreSlim _slot = new(1);
    private readonly TcpListener _listener;
    private Task? _accept;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public LayoutTransport(string secret, Func<string,bool> allowed, Func<LayoutRequest,CancellationToken,Task<LayoutResponse>> handle, int port = DefaultPort)
    {
        _key = PairedTlsTransport.SecretBytes(secret); _allowed = allowed; _handle = handle;
        _cert = PairedTlsTransport.CreateServerCertificate("LBL1");
        _listener = new(IPAddress.IPv6Any, port); _listener.Server.DualMode = true;
    }
    public void Start() { _listener.Start(8); _accept = AcceptAsync(); }
    private async Task AcceptAsync()
    {
        try {
            while (!_stop.IsCancellationRequested) {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.MapToIPv4().ToString();
                if (_clients.Count >= 8 || !_allowed(address)) { client.Dispose(); continue; }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _clients[client] = completion.Task;
                _ = RunAsync(client, completion);
            }
        } catch (Exception) when (_stop.IsCancellationRequested) { }
    }
    private async Task RunAsync(TcpClient client, TaskCompletionSource completion)
    {
        bool owns = false;
        try {
            using var ct = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); ct.CancelAfter(TimeSpan.FromSeconds(20));
            using var stream = new SslStream(client.GetStream(), false);
            using (var auth = CancellationTokenSource.CreateLinkedTokenSource(ct.Token)) {
                auth.CancelAfter(PairedTlsTransport.Timeout);
                if (!await PairedTlsTransport.AuthenticateServerAsync(stream, _cert, _key, "LBL1", () => owns = _slot.Wait(0), auth.Token)) return;
            }
            var request = await ReadAsync<LayoutRequest>(stream, ct.Token);
            LayoutResponse response;
            try { response = await _handle(request, ct.Token); }
            catch (Exception ex) { response = new(false, Error: ex is InvalidDataException or InvalidOperationException ? ex.Message : "设置未应用，请检查连接后重试。"); }
            await WriteAsync(stream, response, ct.Token);
        } catch { }
        finally { if (owns) _slot.Release(); client.Dispose(); completion.TrySetResult(); _clients.TryRemove(client, out _); }
    }
    public static async Task<LayoutResponse> RequestAsync(string address, string secret, LayoutRequest request, CancellationToken token = default, int port = DefaultPort)
    {
        var key = PairedTlsTransport.SecretBytes(secret);
        try {
            using var ct = CancellationTokenSource.CreateLinkedTokenSource(token); ct.CancelAfter(TimeSpan.FromSeconds(15));
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(address, port, ct.Token);
            using var stream = new SslStream(client.GetStream(), false);
            await PairedTlsTransport.AuthenticateClientAsync(stream, key, "LBL1", ct.Token);
            await WriteAsync(stream, request, ct.Token);
            var response = await ReadAsync<LayoutResponse>(stream, ct.Token);
            if (!response.Ok) throw new InvalidOperationException(response.Error);
            return response;
        } finally { CryptographicOperations.ZeroMemory(key); }
    }
    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 2 || length > 32768) throw new InvalidDataException("布局消息长度无效。");
        var data = new byte[length]; await stream.ReadExactlyAsync(data, ct);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("布局消息无效。");
    }
    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > 32768) throw new InvalidDataException("屏幕信息过大。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(data, ct); await stream.FlushAsync(ct);
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener.Stop();
        if (_accept is not null) await _accept;
        foreach (var c in _clients.Keys) c.Dispose();
        await Task.WhenAll(_clients.Values);
        _cert.Dispose(); CryptographicOperations.ZeroMemory(_key); _slot.Dispose(); _stop.Dispose();
    }
}
