using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LinkBridge.Core.Security;

namespace LinkBridge.Core.WindowRoaming;

/// <summary>Authenticated full duplex window stream. One reader and one ordered writer per endpoint.</summary>
public sealed class WindowRoamingConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly SslStream _stream;
    private readonly X509Certificate2? _certificate;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    private WindowRoamingConnection(TcpClient client, SslStream stream, X509Certificate2? certificate = null)
    { _client = client; _stream = stream; _certificate = certificate; }

    public static async Task<WindowRoamingConnection> ConnectAsync(string address, string secret,
        int port = WindowRoamingLimits.DefaultPort, CancellationToken ct = default)
    {
        var client = new TcpClient { NoDelay = true };
        SslStream? stream = null;
        var key = PairedTlsTransport.SecretBytes(secret);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PairedTlsTransport.Timeout);
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            stream = new SslStream(client.GetStream(), false);
            await PairedTlsTransport.AuthenticateClientAsync(stream, key, WindowRoamingLimits.ProtocolContext, timeout.Token).ConfigureAwait(false);
            return new WindowRoamingConnection(client, stream);
        }
        catch { stream?.Dispose(); client.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static async Task<WindowRoamingConnection> FromAcceptedAsync(TcpClient client, string secret,
        Func<bool> permitted, CancellationToken ct = default)
    {
        client.NoDelay = true;
        var stream = new SslStream(client.GetStream(), false);
        var key = PairedTlsTransport.SecretBytes(secret);
        X509Certificate2? certificate = null;
        try
        {
            certificate = PairedTlsTransport.CreateServerCertificate(WindowRoamingLimits.ProtocolContext);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PairedTlsTransport.Timeout);
            if (!await PairedTlsTransport.AuthenticateServerAsync(stream, certificate, key,
                    WindowRoamingLimits.ProtocolContext, permitted, timeout.Token).ConfigureAwait(false))
                throw new UnauthorizedAccessException("窗口漫游未启用或已有会话。");
            return new WindowRoamingConnection(client, stream, certificate);
        }
        catch { stream.Dispose(); client.Dispose(); certificate?.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public Task SendDescriptorAsync(WindowDescriptor value, CancellationToken ct = default)
    {
        WindowRoamingLimits.Validate(value);
        return SendAsync(WindowMessageType.Descriptor, JsonSerializer.SerializeToUtf8Bytes(value), ct);
    }
    public Task SendInputAsync(WindowRemoteInput value, CancellationToken ct = default)
    {
        WindowRoamingLimits.Validate(value);
        return SendAsync(WindowMessageType.Input, JsonSerializer.SerializeToUtf8Bytes(value), ct);
    }
    public Task SendEndAsync(CancellationToken ct = default) => SendAsync(WindowMessageType.End, [], ct);
    public Task SendReadyAsync(CancellationToken ct = default) => SendAsync(WindowMessageType.Ready, [], ct);
    public Task SendHeartbeatAsync(CancellationToken ct = default) => SendAsync(WindowMessageType.Heartbeat, [], ct);
    public Task SendStatusAsync(string value, CancellationToken ct = default)
    {
        if (value.Length > 128) throw new InvalidDataException("窗口状态过长。");
        return SendAsync(WindowMessageType.Status, JsonSerializer.SerializeToUtf8Bytes(value), ct);
    }

    public async Task SendFrameAsync(WindowFrame value, CancellationToken ct = default)
    {
        var length = WindowRoamingLimits.FrameBytes(value.Width, value.Height);
        if (value.SessionId == Guid.Empty || value.Sequence < 0 || value.Pixels.Length != length)
            throw new InvalidDataException("窗口画面无效。");
        using var payload = new MemoryStream(Math.Min(length, 1024 * 1024));
        var header = new byte[32]; value.SessionId.TryWriteBytes(header.AsSpan(0, 16));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), value.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), value.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), value.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), length);
        payload.Write(header);
        using (var zip = new ZLibStream(payload, CompressionLevel.Fastest, true))
            await zip.WriteAsync(value.Pixels, ct).ConfigureAwait(false);
        if (payload.Length > WindowRoamingLimits.MaximumCompressedBytes)
            throw new InvalidDataException("压缩画面超出支持范围。");
        await SendAsync(WindowMessageType.Frame, payload.ToArray(), ct).ConfigureAwait(false);
    }
    private async Task SendAsync(WindowMessageType type, byte[] body, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (type != WindowMessageType.Frame && body.Length > WindowRoamingLimits.MaximumMetadataBytes)
            throw new InvalidDataException("窗口消息过长。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _writer.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var header = new byte[5]; header[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), body.Length);
            await _stream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            if (body.Length != 0) await _stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }
    public async Task<WindowMessage> ReadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var header = new byte[5]; await _stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
        var type = (WindowMessageType)header[0];
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        var maximum = type == WindowMessageType.Frame ? WindowRoamingLimits.MaximumCompressedBytes : WindowRoamingLimits.MaximumMetadataBytes;
        if (!Enum.IsDefined(type) || length < 0 || length > maximum ||
            (type is WindowMessageType.End or WindowMessageType.Ready or WindowMessageType.Heartbeat && length != 0))
            throw new InvalidDataException("窗口消息头无效。");
        var body = new byte[length];
        await _stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
        switch (type)
        {
            case WindowMessageType.Status:
                var status = JsonSerializer.Deserialize<string>(body) ?? throw new InvalidDataException();
                if (status.Length > 128) throw new InvalidDataException();
                return new(type, Status: status);
            case WindowMessageType.Descriptor:
                var descriptor = JsonSerializer.Deserialize<WindowDescriptor>(body) ?? throw new InvalidDataException();
                WindowRoamingLimits.Validate(descriptor);
                return new(type, Descriptor: descriptor);
            case WindowMessageType.Input:
                var input = JsonSerializer.Deserialize<WindowRemoteInput>(body) ?? throw new InvalidDataException();
                WindowRoamingLimits.Validate(input);
                return new(type, Input: input);
            case WindowMessageType.Frame:
                if (length < 34) throw new InvalidDataException("窗口画面头过短。");
                var id = new Guid(body.AsSpan(0, 16));
                var seq = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16));
                var width = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(20));
                var height = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(24));
                var bytes = WindowRoamingLimits.FrameBytes(width, height);
                if (id == Guid.Empty || seq < 0 || bytes != BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(28)))
                    throw new InvalidDataException("窗口画面尺寸不匹配。");
                var pixels = new byte[bytes];
                using (var source = new MemoryStream(body, 32, body.Length - 32, false))
                using (var zip = new ZLibStream(source, CompressionMode.Decompress))
                {
                    await zip.ReadExactlyAsync(pixels, timeout.Token).ConfigureAwait(false);
                    if (zip.ReadByte() != -1) throw new InvalidDataException("窗口画面解压越限。");
                }
                return new(type, Frame: new(id, seq, width, height, pixels));
            default: return new(type);
        }
    }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Cancel(); _client.Dispose(); _stream.Dispose(); _certificate?.Dispose();
            // Active writers release this semaphore in their finally; do not dispose it during that race.
        }
        return ValueTask.CompletedTask;
    }
}
