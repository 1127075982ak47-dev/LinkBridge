using LinkBridge.Core.Discovery;
using System.Buffers.Binary;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
foreach (var size in new[] { 100, 1200, 1201, 1660, 8192, 24576 })
{
    var payload = new byte[size]; new Random(size).NextBytes(payload);
    var packets = DiscoveryDatagrams.Encode(payload);
    Check(packets.All(p => p.Length <= 1200), "MTU limit exceeded");
    if (size <= 1200)
    {
        Check(ReferenceEquals(payload, packets.Single()), "Legacy small packets must stay unchanged");
        Check(!new DiscoveryDatagrams().Accept(payload, "A", out _), "Small legacy packet was treated as fragment");
        continue;
    }
    var receiver = new DiscoveryDatagrams(); byte[]? received = null;
    foreach (var packet in packets.Reverse())
    {
        Check(receiver.Accept(packet, "A", out var done), "Fragment not recognized");
        if (done is not null) received = done;
    }
    Check(received is not null && received.SequenceEqual(payload), "Reordered inventory changed");
    receiver = new(); received = null;
    foreach (var packet in packets.Take(packets.Count - 1))
    {
        receiver.Accept(packet, "A", out var done); Check(done is null, "Published partial inventory");
        receiver.Accept(packet, "A", out done); Check(done is null, "Duplicate counted twice");
    }
    receiver.Accept(packets.Last(), "B", out received); Check(received is null, "Mixed different source IPs");
    receiver.Accept(packets.Last(), "A", out received);
    Check(received is not null && received.SequenceEqual(payload), "Duplicate recovery failed");
    var malformed = packets[0].ToArray();
    var prefixBytes = System.Text.Encoding.ASCII.GetByteCount("LINKBRIDGE/1 FRAGMENT ");
    BinaryPrimitives.WriteInt32BigEndian(malformed.AsSpan(prefixBytes + 20), int.MaxValue);
    Check(new DiscoveryDatagrams().Accept(malformed, "A", out received) && received is null, "Oversized total accepted");
    Check(new DiscoveryDatagrams().Accept(packets[0][..^1], "A", out received) && received is null, "Truncated fragment accepted");
    Console.WriteLine($"PASS {size} bytes: bounded packets, reorder, duplicate, loss, source isolation, malformed lengths");
}
try { DiscoveryDatagrams.Encode(new byte[24577]); throw new Exception("Oversized inventory accepted"); }
catch (ArgumentException) { Console.WriteLine("PASS maximum inventory limit; legacy packets preserved"); }
