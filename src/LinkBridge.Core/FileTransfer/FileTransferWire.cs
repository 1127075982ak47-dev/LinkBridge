using System.Buffers.Binary;
using System.Text.Json;

namespace LinkBridge.Core.FileTransfer;

internal sealed record FileTransferReply(bool Accepted, string? Error = null, FileTransferReceipt? Receipt = null);

internal static class FileTransferWire
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 140 };
    internal const uint EndMarker = 0x3146424C;
    public static async Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (data.Length > FileTransferLimits.MaximumManifestBytes) throw new InvalidDataException("文件列表过大。");
        await WriteNumberAsync(stream, (uint)data.Length, ct).ConfigureAwait(false);
        using var timeout = IdleToken(ct);
        await stream.WriteAsync(data, timeout.Token).ConfigureAwait(false);
        await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
    }
    public static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken ct)
    {
        var length = await ReadNumberAsync(stream, ct).ConfigureAwait(false);
        if (length is < 1 or > FileTransferLimits.MaximumManifestBytes) throw new InvalidDataException("文件列表长度无效。");
        var bytes = new byte[length];
        using var timeout = IdleToken(ct);
        await stream.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("文件列表无效。");
    }
    public static async Task WriteNumberAsync(Stream stream, uint value, CancellationToken ct)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        using var timeout = IdleToken(ct);
        await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
    }
    public static async Task<uint> ReadNumberAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new byte[4];
        using var timeout = IdleToken(ct);
        await stream.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
    public static CancellationTokenSource IdleToken(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(FileTransferLimits.IdleTimeout);
        return source;
    }
}
