using NAudio.Wave;

namespace LinkBridge.Media;

/// <summary>Assembles TCP fragments into complete interleaved PCM frames before buffering.</summary>
public sealed class PcmFrameAssembler(int blockAlign = 4)
{
    private readonly int _blockAlign = blockAlign > 0 ? blockAlign : throw new ArgumentOutOfRangeException(nameof(blockAlign));
    private byte[] _pending = new byte[Math.Max(blockAlign, 4096)];
    private int _tail;
    public int PendingBytes => _tail;
    public long DroppedBytes { get; private set; }

    public int Push(BufferedWaveProvider target, byte[] source, int offset, int count)
    {
        if (target.WaveFormat.BlockAlign != _blockAlign || target.BufferLength % _blockAlign != 0)
            throw new ArgumentException("PCM buffer must contain complete frames.", nameof(target));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > source.Length - count) throw new ArgumentException("Source range is invalid.");
        if (_pending.Length < _tail + count) Array.Resize(ref _pending, _tail + count);
        Buffer.BlockCopy(source, offset, _pending, _tail, count);
        var total = _tail + count;
        var complete = total - total % _blockAlign;
        if (complete != 0)
        {
            var start = 0;
            var write = complete;
            // Drop whole frames on overload, keeping the most recent audio and low latency.
            if (write > target.BufferLength - target.BufferedBytes)
            {
                DroppedBytes += target.BufferedBytes;
                target.ClearBuffer();
                if (write > target.BufferLength)
                {
                    start = write - target.BufferLength;
                    DroppedBytes += start;
                    write = target.BufferLength;
                }
            }
            target.AddSamples(_pending, start, write);
        }
        _tail = total - complete;
        if (_tail != 0) Buffer.BlockCopy(_pending, complete, _pending, 0, _tail);
        return complete;
    }
}
