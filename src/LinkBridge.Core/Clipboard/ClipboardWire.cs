using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LinkBridge.Core.Clipboard;

internal static class ClipboardWire
{
    internal const int HeaderSize = 53;
    internal static byte[] Header(ClipboardPayload payload)
    {
        var header = new byte[HeaderSize];
        payload.Id.TryWriteBytes(header.AsSpan(0, 16));
        header[16] = payload.Kind == ClipboardKinds.Text ? (byte)1 : (byte)2;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(17, 4), payload.Data.Length);
        SHA256.HashData(payload.Data).CopyTo(header, 21);
        return header;
    }
    internal static async Task<ClipboardPayload> ReceiveAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize]; await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var id = new Guid(header.AsSpan(0, 16));
        var kind = header[16] switch { 1 => ClipboardKinds.Text, 2 => ClipboardKinds.Png, _ => throw new InvalidDataException("剪贴板类型无效。") };
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(17, 4));
        ClipboardLimits.ValidateLength(kind, length);
        if (id == Guid.Empty) throw new InvalidDataException("剪贴板请求标识无效。");
        var data = new byte[length]; await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(header.AsSpan(21, 32), SHA256.HashData(data)))
            throw new InvalidDataException("剪贴板数据校验失败。");
        var payload = new ClipboardPayload(id, kind, data);
        ClipboardLimits.ValidatePayload(payload);
        return payload;
    }
    internal static async Task AcknowledgeAsync(Stream stream, Guid id, bool applied, CancellationToken ct)
    {
        var reply = new byte[17]; id.TryWriteBytes(reply.AsSpan(0, 16)); reply[16] = applied ? (byte)1 : (byte)0;
        await stream.WriteAsync(reply, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
