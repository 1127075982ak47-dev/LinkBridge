using System.Buffers.Binary;
using System.Text;

namespace LinkBridge.Core.Clipboard;

public static class ClipboardKinds
{
    public const string Text = "text";
    public const string Png = "png";
}

public sealed record ClipboardPayload(Guid Id, string Kind, byte[] Data);

public static class ClipboardLimits
{
    public const string ProtocolContext = "LBC1";
    public const int DefaultPort = 45687;
    public const int MaximumTextBytes = 1024 * 1024;
    public const int MaximumImageBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(15);

    public static void ValidatePayload(ClipboardPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Id == Guid.Empty || payload.Data is null) throw new InvalidDataException("剪贴板数据无效。");
        ValidateLength(payload.Kind, payload.Data.Length);
        if (payload.Kind == ClipboardKinds.Text)
        {
            try { _ = new UTF8Encoding(false, true).GetCharCount(payload.Data); }
            catch (DecoderFallbackException) { throw new InvalidDataException("剪贴板文本必须为有效 UTF-8。"); }
        }
        else
        {
            ReadOnlySpan<byte> data = payload.Data;
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (data.Length < 33 || !data[..8].SequenceEqual(signature) ||
                BinaryPrimitives.ReadUInt32BigEndian(data[8..12]) != 13 || !data[12..16].SequenceEqual("IHDR"u8))
                throw new InvalidDataException("剪贴板图片必须为 PNG。");
            var width = BinaryPrimitives.ReadUInt32BigEndian(data[16..20]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(data[20..24]);
            // Bound decompressed memory without resizing or re-encoding the original PNG.
            if (width is < 1 or > 32768 || height is < 1 or > 32768 || (ulong)width * height > 64UL * 1024 * 1024)
                throw new InvalidDataException("剪贴板图片尺寸超出范围。");
        }
    }

    internal static void ValidateLength(string kind, int length)
    {
        if (length < 0 || kind != ClipboardKinds.Text && kind != ClipboardKinds.Png ||
            length > (kind == ClipboardKinds.Text ? MaximumTextBytes : MaximumImageBytes))
            throw new InvalidDataException("剪贴板类型或数据大小超出范围。");
    }
}
