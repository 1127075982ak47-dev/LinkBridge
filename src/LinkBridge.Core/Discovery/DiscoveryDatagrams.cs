using System.Buffers.Binary;
using System.Text;

namespace LinkBridge.Core.Discovery;

/// <summary>Keep discovery below the LAN MTU, including large native device inventories.</summary>
internal sealed class DiscoveryDatagrams
{
    internal const int MaximumDatagramBytes = 1200;
    private const int MaximumPayloadBytes = 24576, MaximumPending = 64;
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("LINKBRIDGE/1 FRAGMENT ");
    private static int HeaderBytes => Prefix.Length + 24;
    private static int ChunkBytes => MaximumDatagramBytes - HeaderBytes;
    private readonly Dictionary<(string Address, Guid Id), Assembly> _pending = [];
    private sealed record Assembly(DateTimeOffset Started, int Total, byte[][] Parts)
    {
        public int Received { get; set; }
    }

    internal static IReadOnlyList<byte[]> Encode(byte[] payload)
    {
        if (payload.Length > MaximumPayloadBytes) throw new ArgumentException("Discovery inventory exceeds its limit.");
        if (payload.Length <= MaximumDatagramBytes) return [payload];
        var count = (payload.Length + ChunkBytes - 1) / ChunkBytes;
        var id = Guid.NewGuid();
        var packets = new byte[count][];
        for (var index = 0; index < count; index++)
        {
            var offset = index * ChunkBytes;
            var length = Math.Min(ChunkBytes, payload.Length - offset);
            var packet = packets[index] = new byte[HeaderBytes + length];
            Prefix.CopyTo(packet, 0);
            id.TryWriteBytes(packet.AsSpan(Prefix.Length, 16));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(Prefix.Length + 16), (ushort)index);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(Prefix.Length + 18), (ushort)count);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(Prefix.Length + 20), payload.Length);
            payload.AsSpan(offset, length).CopyTo(packet.AsSpan(HeaderBytes));
        }
        return packets;
    }

    /// <summary>Returns true for a fragment, including invalid/incomplete fragments.</summary>
    internal bool Accept(byte[] packet, string address, out byte[]? completed)
    {
        completed = null;
        if (!packet.AsSpan().StartsWith(Prefix)) return false;
        if (packet.Length <= HeaderBytes || packet.Length > MaximumDatagramBytes) return true;
        var id = new Guid(packet.AsSpan(Prefix.Length, 16));
        var index = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(Prefix.Length + 16));
        var count = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(Prefix.Length + 18));
        var total = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(Prefix.Length + 20));
        if (total <= MaximumDatagramBytes || total > MaximumPayloadBytes ||
            count != (total + ChunkBytes - 1) / ChunkBytes || index >= count ||
            packet.Length - HeaderBytes != Math.Min(ChunkBytes, total - index * ChunkBytes)) return true;
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in _pending.Where(p => now - p.Value.Started > TimeSpan.FromSeconds(8)).Select(p => p.Key).ToArray())
            _pending.Remove(expired);
        var key = (address, id);
        if (!_pending.TryGetValue(key, out var assembly))
        {
            if (_pending.Count >= MaximumPending) _pending.Remove(_pending.MinBy(p => p.Value.Started).Key);
            _pending[key] = assembly = new(now, total, new byte[count][]);
        }
        if (assembly.Total != total || assembly.Parts.Length != count) { _pending.Remove(key); return true; }
        var part = packet.AsSpan(HeaderBytes);
        if (assembly.Parts[index] is { } previous)
        {
            if (!part.SequenceEqual(previous)) _pending.Remove(key);
            return true;
        }
        assembly.Parts[index] = part.ToArray();
        if (++assembly.Received != count) return true;
        _pending.Remove(key);
        completed = new byte[total];
        for (var i = 0; i < count; i++) assembly.Parts[i].CopyTo(completed, i * ChunkBytes);
        return true;
    }
}
