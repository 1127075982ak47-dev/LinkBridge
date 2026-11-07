using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LinkBridge.Core.Discovery;

var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
await using var receiver = new PeerDiscovery(45679, 0) { PairingSecret = secret };
var requests = new List<string>(); receiver.ConnectionRequested += requests.Add;
var handle = typeof(PeerDiscovery).GetMethod("HandlePacket", BindingFlags.Instance | BindingFlags.NonPublic)!;
var instance = Guid.NewGuid().ToString("N");
var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
byte[] Packet(string nonce, long timestamp, string signingKey, string? sender = null)
{
    sender ??= instance;
    var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.ASCII.GetBytes(signingKey),
        Encoding.UTF8.GetBytes("CONNECT:" + sender + "|" + nonce + "|" + timestamp)));
    return Encoding.ASCII.GetBytes("LINKBRIDGE/1 CONNECT_REQUEST " + sender + " " + nonce + " " + timestamp + " " + signature);
}
void Deliver(byte[] packet) => handle.Invoke(receiver, [packet, new IPEndPoint(IPAddress.Parse("192.168.1.50"), 45678)]);
void Check(bool condition, string text) { if (!condition) throw new Exception(text); }
var nonce = Guid.NewGuid().ToString("N"); var valid = Packet(nonce, now, secret);
Deliver(valid); Deliver(valid); Check(requests.Count == 1, "Duplicate request invoked twice");
Deliver(Packet(Guid.NewGuid().ToString("N"), now, new string('x', 32)));
Deliver(Packet(Guid.NewGuid().ToString("N"), now - 100, secret));
Deliver(Packet(Guid.NewGuid().ToString("N"), long.MinValue, secret));
Deliver(Encoding.ASCII.GetBytes("LINKBRIDGE/1 CONNECT_REQUEST invalid"));
var ownInstance = (string)typeof(PeerDiscovery).GetField("InstanceId", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
Deliver(Packet(Guid.NewGuid().ToString("N"), now, secret, ownInstance));
Check(requests.Count == 1, "Invalid, expired or own request accepted");
Deliver(Packet(Guid.NewGuid().ToString("N"), now, secret));
Check(requests.Count == 2, "A fresh paired retry was rejected");
Console.WriteLine("PASS paired requests, replay deduplication, wrong key, expiry, extreme timestamps, malformed packet, own broadcast filtering and fresh retry");
receiver.IsDiscoverable = false;
await receiver.RequestConnectionAsync("192.168.1.50");
Check(!receiver.IsDiscoverable, "Manual request changed automatic discovery preference");
Console.WriteLine("PASS manual announcement with hidden discovery preserves the saved preference");
