using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LinkBridge.Core.Models;

namespace LinkBridge.Core.Discovery;

public sealed partial class PeerDiscovery
{
    private const string ConnectionRequest = "LINKBRIDGE/1 CONNECT_REQUEST ";
    private readonly Dictionary<string, DateTimeOffset> _connectionNonces = [];
    public event Action<string>? ConnectionRequested;

    private IReadOnlyList<byte[]> CreateAnnouncementPackets()
    {
        var bytes = Encoding.UTF8.GetBytes(Magic + JsonSerializer.Serialize(new PeerPayload
        {
            Instance = InstanceId, Name = DisplayName, Platform = PlatformInfo.DisplayName,
            MediaPort = MediaPort,
            AudioOutputs = FilterDevices(AudioOutputs, MediaDeviceKind.Speaker, 32),
            Cameras = FilterDevices(Cameras, MediaDeviceKind.Camera, 16),
            Microphones = FilterDevices(Microphones, MediaDeviceKind.Microphone, 16),
            HasSharedMicrophoneInput = HasSharedMicrophoneInput,
            CameraControlProtocolVersion = CameraControlProtocolVersion,
        }));
        var packets = DiscoveryDatagrams.Encode(bytes);
        if (_lastDiscoveryPacketCount != packets.Count)
        {
            _lastDiscoveryPacketCount = packets.Count;
            if (packets.Count > 1) LogEmitted?.Invoke($"[LinkBridge] 发现广播已分为 {packets.Count} 个小包 · {bytes.Length} 字节设备信息");
        }
        return packets;
    }

    /// <summary>A user click temporarily announces even when automatic advertising is hidden.</summary>
    public async Task RequestConnectionAsync(string? lastAddress = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (PairingSecret.Length != 32) throw new InvalidOperationException("请先完成两台电脑的配对。");
        var targets = BuildBroadcastTargets().ToList();
        foreach (var address in Snapshot().Select(p => p.Address).Append(lastAddress))
            if (IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                targets.Add(new(ip, _discoveryPort));
        var nonce = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var request = Encoding.ASCII.GetBytes(ConnectionRequest + InstanceId + " " + nonce + " " + timestamp + " " +
            Signature("CONNECT:" + InstanceId, nonce, timestamp));
        foreach (var target in targets.Distinct())
        {
            try
            {
                using var sender = new UdpClient(); sender.EnableBroadcast = true;
                foreach (var packet in CreateAnnouncementPackets()) await sender.SendAsync(packet, target, ct).ConfigureAwait(false);
                await sender.SendAsync(request, target, ct).ConfigureAwait(false);
            }
            catch (SocketException) { /* Another physical adapter may still reach the peer. */ }
        }
    }

    public async Task AnnounceToAsync(string address, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return;
        using var sender = new UdpClient();
        foreach (var packet in CreateAnnouncementPackets())
            await sender.SendAsync(packet, new IPEndPoint(ip, _discoveryPort), ct).ConfigureAwait(false);
    }

    private void HandleConnectionRequest(string text, IPEndPoint remote)
    {
        var fields = text[ConnectionRequest.Length..].Split(' ');
        var now = DateTimeOffset.UtcNow;
        if (PairingSecret.Length != 32 || fields.Length != 4 || fields[0] == InstanceId ||
            !Guid.TryParseExact(fields[0], "N", out _) || !Guid.TryParseExact(fields[1], "N", out _) ||
            !long.TryParse(fields[2], out var timestamp) || timestamp < now.ToUnixTimeSeconds() - 30 || timestamp > now.ToUnixTimeSeconds() + 30 ||
            !ValidSignature("CONNECT:" + fields[0], fields[1], fields[2], fields[3])) return;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var expiredKey in _connectionNonces.Where(p => now - p.Value > TimeSpan.FromSeconds(65)).Select(p => p.Key).ToArray())
                _connectionNonces.Remove(expiredKey);
            var key = remote.Address + ":" + fields[1];
            if (_connectionNonces.ContainsKey(key)) return;
            if (_connectionNonces.Count >= 128) _connectionNonces.Remove(_connectionNonces.MinBy(p => p.Value).Key);
            _connectionNonces[key] = now;
        }
        ConnectionRequested?.Invoke(remote.Address.ToString());
    }
}
