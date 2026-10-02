using System.Buffers;

namespace LinkBridge.Media;

/// <summary>预览帧的缓冲区租约。接收方用完后归还，避免逐帧大对象分配。</summary>
public sealed class PreviewFrame : IDisposable
{
    private byte[]? _data;
    internal PreviewFrame(byte[] data, int width, int height)
    { _data = data; Width = width; Height = height; }
    public byte[] Data => _data ?? throw new ObjectDisposedException(nameof(PreviewFrame));
    public int Width { get; }
    public int Height { get; }
    public int Length => Width * Height * 4;
    public void Dispose()
    {
        var data = Interlocked.Exchange(ref _data, null);
        if (data is not null) ArrayPool<byte>.Shared.Return(data);
    }
}
