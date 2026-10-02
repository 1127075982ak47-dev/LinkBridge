using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using LinkBridge.Core.Models;

namespace LinkBridge.Core.Discovery;

/// <summary>局域网里被发现的一台对端。</summary>
public sealed record PeerInfo(
    string Name,
    string Address,
    string Platform,
    int MediaPort,
    DateTimeOffset LastSeen)
{
    /// <summary>用来去重的稳定键。</summary>
    public string Key => $"{Name}@{Address}";
    public MediaDevice[] AudioOutputs { get; init; } = [];
    public MediaDevice[] Cameras { get; init; } = [];
    public MediaDevice[] Microphones { get; init; } = [];
    /// <summary>Discovery hint only. Signed MIC control and LBM1 TLS still authorize actual use.</summary>
    public bool HasSharedMicrophoneInput { get; init; }
    /// <summary>Unsigned discovery hint; actual CAMERA2 control still requires paired signatures.</summary>
    public int CameraControlProtocolVersion { get; init; }
}

/// <summary>UDP 广播自动发现：每台装了 LinkBridge 的机器都会广播自己，彼此无需填 IP。</summary>
public sealed class PeerDiscovery : IAsyncDisposable
{
    public const int DiscoveryPort = 45678;
    public const int CameraControlProtocolVersion = 2;
    public static readonly TimeSpan CameraSourceLeaseTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MicrophoneSourceLeaseTimeout = TimeSpan.FromSeconds(10);

    private const string Magic = "LINKBRIDGE/1";
    private const string MediaStart = "LINKBRIDGE/1 MEDIA_START ";
    private const string MediaReady = "LINKBRIDGE/1 MEDIA_READY ";
    private const string CameraStart = "LINKBRIDGE/1 CAMERA2_START ";
    private const string CameraReady = "LINKBRIDGE/1 CAMERA2_READY ";
    private const string CameraStop = "LINKBRIDGE/1 CAMERA2_STOP ";
    private const string CameraStopped = "LINKBRIDGE/1 CAMERA2_STOPPED ";
    private const string LegacyCameraStart = "LINKBRIDGE/1 CAMERA_START ", LegacyCameraStop = "LINKBRIDGE/1 CAMERA_STOP ";
    private const string MicStart = "LINKBRIDGE/1 MIC_START ", MicReady = "LINKBRIDGE/1 MIC_READY ",
        MicStop = "LINKBRIDGE/1 MIC_STOP ", MicStopped = "LINKBRIDGE/1 MIC_STOPPED ";

    /// <summary>本进程唯一标识：自己发出去的广播绕回来时据此丢掉。</summary>
    private static readonly string InstanceId = Guid.NewGuid().ToString("N");

    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(8);

    private readonly UdpClient _listener;
    private readonly int _discoveryPort;
    private bool _disposed;
    private readonly Dictionary<string, CameraRequest> _cameraNonces = [];
    private readonly Dictionary<string, long> _cameraVersions = [], _cameraLatestStamp = [], _cameraOutgoingStamp = [];
    private readonly Dictionary<string, CameraEpoch> _cameraEpochs = [];
    private readonly Dictionary<string, CancellationTokenSource> _cameraOutgoing = [];
    private readonly HashSet<Task> _cameraTasks = [];
    private readonly HashSet<string> _cameraPendingReplies = [];
    private readonly SemaphoreSlim _cameraStartGate = new(1);
    private readonly Dictionary<string, long> _cameraOwners = [];
    private string? _cameraSourceOwnerAddress;
    private string? _cameraDesiredOwnerAddress;
    private string? _lastCameraControlError;
    private readonly Dictionary<string, MicrophoneRequest> _microphoneNonces = [];
    private readonly Dictionary<string, long> _microphoneVersions = [], _microphoneOwners = [], _microphoneLatestStamp = [], _microphoneOutgoingStamp = [];
    private readonly Dictionary<string, MicrophoneEpoch> _microphoneEpochs = [];
    private readonly Dictionary<string, CancellationTokenSource> _microphoneOutgoing = [];
    private readonly HashSet<Task> _microphoneTasks = [];
    private readonly HashSet<string> _microphonePendingReplies = [];
    private readonly SemaphoreSlim _microphoneStartGate = new(1);
    private string? _microphoneSourceOwnerAddress;
    private string? _microphoneDesiredOwnerAddress;
    private sealed record MicrophoneRequest(string Kind, string Timestamp, int Port, DateTimeOffset Accepted, long Version, Task<bool> Result);
    private sealed record MicrophoneEpoch(string Address, int Port, long Version)
    {
        public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record CameraRequest(string Kind, string Timestamp, int Port, DateTimeOffset Accepted, long Version, Task<bool> Result);
    private sealed record CameraEpoch(string Address, int Port, long Version)
    {
        public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, PeerInfo> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Dictionary<string, AcceptedMediaRequest> _acceptedMediaNonces = [];
    private sealed record AcceptedMediaRequest(DateTimeOffset Accepted, string Timestamp, long Epoch, bool Ready);
    private long _mediaReceiverEpoch;
    private DateTimeOffset _lastMediaReset;

    private Task? _listenTask;
    private Task? _broadcastTask;
    private Task? _cameraLeaseTask;
    private Task? _microphoneLeaseTask;

    /// <summary>对端列表发生变化时触发，用于刷新界面。</summary>
    public event Action<IReadOnlyList<PeerInfo>>? PeersChanged;
    public event Action<string>? LogEmitted;
    /// <summary>对端准备发送新流时，重新打开本机接收器。</summary>
    public event Func<bool>? MediaStartRequested;
    /// <summary>Production media reception binds the actual authenticated control source IP.</summary>
    public event Func<string, bool>? AuthenticatedMediaStartRequested;
    public event Func<string, int, Task<bool>>? SharedCameraStartRequested;
    public event Action<string>? SharedCameraStopRequested;
    public event Func<string, int, Task<bool>>? SharedMicrophoneStartRequested;
    public event Action<string>? SharedMicrophoneStopRequested;

    public int MediaPort { get; }
    public string DisplayName { get; set; } = PlatformInfo.MachineName;
    /// <summary>Controls advertising only; paired control requests and discovery reception remain available.</summary>
    private volatile bool _isDiscoverable = true;
    public bool IsDiscoverable { get => _isDiscoverable; set => _isDiscoverable = value; }
    public MediaDevice[] AudioOutputs { get; set; } = [];
    public MediaDevice[] Cameras { get; set; } = [];
    public MediaDevice[] Microphones { get; set; } = [];
    private volatile bool _hasSharedMicrophoneInput;
    public bool HasSharedMicrophoneInput { get => _hasSharedMicrophoneInput; set => _hasSharedMicrophoneInput = value; }
    public string PairingSecret { get; set; } = string.Empty;
    public string? LastCameraControlError { get { lock (_gate) return _lastCameraControlError; } }

    public PeerDiscovery(int mediaPort, int discoveryPort = DiscoveryPort)
    {
        MediaPort = mediaPort;
        _discoveryPort = discoveryPort;

        _listener = new UdpClient();
        _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var token = _cts.Token;
            _listenTask ??= Task.Run(() => ListenLoopAsync(token));
            _broadcastTask ??= Task.Run(() => BroadcastLoopAsync(token));
            _cameraLeaseTask ??= Task.Run(() => CameraLeaseLoopAsync(token));
            _microphoneLeaseTask ??= Task.Run(() => MicrophoneLeaseLoopAsync(token));
        }
    }

    /// <summary>当前在线的对端（已剔除超时的）。</summary>
    public IReadOnlyList<PeerInfo> Snapshot()
    {
        lock (_gate)
        {
            Prune();
            return _peers.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await _listener.ReceiveAsync(ct).ConfigureAwait(false);
                HandlePacket(received.Buffer, received.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    private void HandlePacket(byte[] buffer, IPEndPoint remote)
    {
        if (buffer.Length > 24576) return;
        try
        {
            var text = Encoding.UTF8.GetString(buffer);
            if (text.StartsWith(LegacyCameraStart, StringComparison.Ordinal) || text.StartsWith(LegacyCameraStop, StringComparison.Ordinal))
            {
                RejectLegacyCameraRequest(text, remote);
                return;
            }
            if (text.StartsWith(MicStart, StringComparison.Ordinal) || text.StartsWith(MicStop, StringComparison.Ordinal))
            {
                HandleMicrophoneRequest(text, remote);
                return;
            }
            if (text.StartsWith(CameraStart, StringComparison.Ordinal) || text.StartsWith(CameraStop, StringComparison.Ordinal))
            {
                HandleCameraRequest(text, remote);
                return;
            }
            if (text.StartsWith(MediaStart, StringComparison.Ordinal))
            {
                var fields = text[MediaStart.Length..].Split(' ');
                if (PairingSecret.Length != 32 || fields.Length != 3 || !Guid.TryParseExact(fields[0], "N", out _) ||
                    !long.TryParse(fields[1], out var timestamp) || timestamp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30 ||
                    timestamp > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30 ||
                    !ValidSignature("START", fields[0], fields[1], fields[2])) return;
                var nonce = fields[0];
                var ready = false;
                if (Snapshot().Any(p => p.Address == remote.Address.ToString()))
                {
                    var key = remote.Address + ":" + nonce;
                    long epoch;
                    bool cached;
                    lock (_gate)
                    {
                        if (_disposed) return;
                        foreach (var expired in _acceptedMediaNonces.Where(p => p.Value.Accepted < DateTimeOffset.UtcNow.AddSeconds(-65)).Select(p => p.Key).ToArray())
                            _acceptedMediaNonces.Remove(expired);
                        epoch = _mediaReceiverEpoch;
                        cached = _acceptedMediaNonces.TryGetValue(key, out var previous);
                        if (cached) ready = previous!.Ready && previous.Timestamp == fields[1] && previous.Epoch == epoch;
                        else if (DateTimeOffset.UtcNow - _lastMediaReset < TimeSpan.FromSeconds(1)) return;
                        else
                        {
                            // Reserve every accepted authenticated nonce before callbacks,
                            // including callbacks canceled by a concurrent user Stop.
                            _lastMediaReset = DateTimeOffset.UtcNow;
                            _acceptedMediaNonces[key] = new(_lastMediaReset, fields[1], epoch, false);
                        }
                    }
                    if (!cached)
                    {
                        try
                        {
                            ready = AuthenticatedMediaStartRequested is { } authenticated
                                ? authenticated(remote.Address.ToString()) : MediaStartRequested?.Invoke() == true;
                        }
                        catch { ready = false; }
                        lock (_gate)
                        {
                            // Stop invalidates both already-cached READY and a callback
                            // accepted before Stop whose result arrives afterward.
                            ready &= !_disposed && epoch == _mediaReceiverEpoch;
                            if (_acceptedMediaNonces.TryGetValue(key, out var accepted) && accepted.Epoch == epoch)
                                _acceptedMediaNonces[key] = accepted with { Ready = ready };
                        }
                    }
                }
                if (ready)
                {
                    var reply = Encoding.UTF8.GetBytes(MediaReady + nonce + " " + fields[1] + " " + Signature("READY", nonce, fields[1]));
                    _listener.Send(reply, reply.Length, remote);
                }
                return;
            }
            if (!text.StartsWith(Magic, StringComparison.Ordinal))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<PeerPayload>(text[Magic.Length..]);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Name) || payload.Name.Length > 80 ||
                payload.Platform?.Length > 32 || payload.Instance?.Length > 64 || payload.MediaPort is < 1025 or > 65535)
            {
                return;
            }

            // 自己的广播会绕回本机，直接忽略
            if (payload.Instance == InstanceId)
            {
                return;
            }

            var address = remote.Address.ToString();
            if (address == "127.0.0.1" || address == "::1")
            {
                return;
            }

            var peer = new PeerInfo(
                payload.Name,
                address,
                payload.Platform ?? "未知",
                payload.MediaPort,
                DateTimeOffset.Now) { AudioOutputs = (payload.AudioOutputs ?? []).Where(d => d is not null &&
                    d.Kind == MediaDeviceKind.Speaker && Enum.IsDefined(d.Backend) && !string.IsNullOrWhiteSpace(d.Id) &&
                    d.Id.Length <= 1024 && !string.IsNullOrWhiteSpace(d.Name) && d.Name.Length <= 256).Take(32).ToArray(),
                    Cameras = FilterDevices(payload.Cameras, MediaDeviceKind.Camera, 16),
                    Microphones = FilterDevices(payload.Microphones, MediaDeviceKind.Microphone, 16),
                    HasSharedMicrophoneInput = payload.HasSharedMicrophoneInput,
                    CameraControlProtocolVersion = payload.CameraControlProtocolVersion };

            bool changed;
            lock (_gate)
            {
                changed = !_peers.TryGetValue(peer.Key, out var old) || old.LastSeen < peer.LastSeen.AddSeconds(-5);
                foreach (var staleName in _peers.Where(p => p.Value.Address == address && p.Key != peer.Key).Select(p => p.Key).ToArray())
                    _peers.Remove(staleName);
                if (_peers.Count >= 64 && !_peers.ContainsKey(peer.Key))
                    _peers.Remove(_peers.MinBy(p => p.Value.LastSeen).Key);
                _peers[peer.Key] = peer;
                Prune();
            }

            if (changed)
            {
                PeersChanged?.Invoke(Snapshot());
            }
        }
        catch (JsonException)
        {
            // 不是我们的包，忽略
        }
    }

    private static MediaDevice[] FilterDevices(MediaDevice[]? devices, MediaDeviceKind kind, int limit) =>
        (devices ?? []).Where(device => device is not null && device.Kind == kind && Enum.IsDefined(device.Backend) &&
            !string.IsNullOrWhiteSpace(device.Id) && device.Id.Length <= 1024 &&
            !string.IsNullOrWhiteSpace(device.Name) && device.Name.Length <= 256).Take(limit).ToArray();

    private bool MicrophoneSecretValid => PairingSecret.Length == 32 && PairingSecret.All(c => c is >= '!' and <= '~');
    private void TrackMicrophoneTaskLocked(Task task)
    {
        _microphoneTasks.Add(task);
        _ = task.ContinueWith(_ => { lock (_gate) _microphoneTasks.Remove(task); }, TaskScheduler.Default);
    }
    private void ReplyMicrophoneTrackedLocked(IPEndPoint target, string nonce, MicrophoneRequest request)
    {
        var key = target + ":" + nonce;
        // UDP retry from the same socket already has a reply waiting for readiness;
        // it must not allocate another task for the same pending nonce.
        if (!_microphonePendingReplies.Add(key)) return;
        var task = ReplyMicrophoneAsync(target, nonce, request, _cts.Token);
        TrackMicrophoneTaskLocked(task);
        _ = task.ContinueWith(_ => { lock (_gate) _microphonePendingReplies.Remove(key); }, TaskScheduler.Default);
    }
    private void HandleMicrophoneRequest(string text, IPEndPoint remote)
    {
        var start = text.StartsWith(MicStart, StringComparison.Ordinal);
        var kind = start ? "MIC_START" : "MIC_STOP";
        var fields = text[(start ? MicStart.Length : MicStop.Length)..].Split(' ');
        var now = DateTimeOffset.UtcNow;
        // MIC timestamps are Unix milliseconds, permitting strict ordering even for a
        // rapid start/stop within the same second. The signed validity window is +/-30s.
        if (!MicrophoneSecretValid || fields.Length != 4 || !Guid.TryParseExact(fields[0], "N", out _) ||
            !long.TryParse(fields[1], out var stamp) || stamp < now.ToUnixTimeMilliseconds() - 30000 ||
            stamp > now.ToUnixTimeMilliseconds() + 30000 || !int.TryParse(fields[2], out var port) ||
            (start ? port is < 1025 or > 65535 : port != 0) || !ValidCameraSignature(kind, fields[0], fields[1], port, "", fields[3])) return;
        var address = remote.Address.ToString();
        if (!Snapshot().Any(peer => peer.Address == address)) return;
        lock (_gate)
        {
            if (_disposed || (start && _microphoneTasks.Count >= 64)) return;
            foreach (var expired in _microphoneNonces.Where(p => p.Value.Accepted < now.AddSeconds(-65)).Select(p => p.Key).ToArray())
                _microphoneNonces.Remove(expired);
            var key = address + ":" + fields[0];
            if (_microphoneNonces.TryGetValue(key, out var replay))
            {
                if (replay.Kind == kind && replay.Timestamp == fields[1] && replay.Port == port)
                    ReplyMicrophoneTrackedLocked(remote, fields[0], replay);
                // Replays may receive their cached reply but cannot refresh the lease.
                return;
            }
            if ((start && _microphoneNonces.Count >= 4096) || stamp <= _microphoneLatestStamp.GetValueOrDefault(address)) return;
            var previous = _microphoneVersions.GetValueOrDefault(address);
            var renewal = start && _microphoneDesiredOwnerAddress == address && _microphoneOwners.ContainsKey(address) && _microphoneEpochs.TryGetValue(address, out var old) && old.Port == port;
            var version = renewal ? previous : previous + 1;
            _microphoneLatestStamp[address] = stamp; _microphoneVersions[address] = version;
            Task<bool> result;
            if (start)
            {
                if (_microphoneDesiredOwnerAddress is { } previousOwner && previousOwner != address)
                {
                    _microphoneOwners.Remove(previousOwner);
                    _microphoneVersions[previousOwner] = _microphoneVersions.GetValueOrDefault(previousOwner) + 1;
                    if (_microphoneEpochs.Remove(previousOwner, out var previousEpoch)) previousEpoch.Result.TrySetResult(false);
                    try { StopOwnedMicrophoneLocked(previousOwner); } catch { }
                }
                _microphoneDesiredOwnerAddress = address;
                _microphoneOwners[address] = Environment.TickCount64;
                if (!renewal)
                {
                    if (_microphoneEpochs.Remove(address, out var previousEpoch)) previousEpoch.Result.TrySetResult(false);
                    try { StopOwnedMicrophoneLocked(address); } catch { }
                    var epoch = new MicrophoneEpoch(address, port, version); _microphoneEpochs[address] = epoch;
                    TrackMicrophoneTaskLocked(Task.Run(() => ProcessMicrophoneStartAsync(epoch, _cts.Token)));
                }
                result = _microphoneEpochs[address].Result.Task;
            }
            else
            {
                _microphoneOwners.Remove(address);
                if (_microphoneDesiredOwnerAddress == address) _microphoneDesiredOwnerAddress = null;
                if (_microphoneEpochs.Remove(address, out var epoch)) epoch.Result.TrySetResult(false);
                var stopped = true;
                try { StopOwnedMicrophoneLocked(address); } catch { stopped = false; }
                result = Task.FromResult(stopped);
            }
            var request = new MicrophoneRequest(kind, fields[1], port, now, version, result);
            // STOP takes priority over a full pending START/reply budget. Per-owner
            // monotonic timestamps still prevent an evicted nonce reopening a source.
            if (_microphoneNonces.Count >= 4096) _microphoneNonces.Remove(_microphoneNonces.MinBy(pair => pair.Value.Accepted).Key);
            _microphoneNonces[key] = request;
            ReplyMicrophoneTrackedLocked(remote, fields[0], request);
        }
    }
    private async Task ProcessMicrophoneStartAsync(MicrophoneEpoch epoch, CancellationToken ct)
    {
        var acquired = false; var invoked = false; var ready = false;
        try
        {
            // Async source callbacks are serialized independently from UDP discovery and
            // camera control. An old callback must settle before a newer one can open hardware.
            await _microphoneStartGate.WaitAsync(ct).ConfigureAwait(false); acquired = true;
            Task<bool>? work;
            lock (_gate)
            {
                if (!MicrophoneEpochCurrentLocked(epoch)) return;
                if (_microphoneSourceOwnerAddress is { } previousOwner)
                {
                    if (previousOwner != epoch.Address)
                    {
                        _microphoneOwners.Remove(previousOwner);
                        _microphoneVersions[previousOwner] = _microphoneVersions.GetValueOrDefault(previousOwner) + 1;
                        if (_microphoneEpochs.Remove(previousOwner, out var previousEpoch)) previousEpoch.Result.TrySetResult(false);
                    }
                    // Same owner with a different port is a new epoch too; only genuine
                    // lease renewals bypass Start and leave the existing source untouched.
                    StopOwnedMicrophoneLocked(previousOwner);
                }
                _microphoneSourceOwnerAddress = epoch.Address; invoked = true;
                work = SharedMicrophoneStartRequested?.Invoke(epoch.Address, epoch.Port);
            }
            // Source callbacks must honor synchronous Stop and bound their own I/O. Await
            // the actual callback (not an abandoned timeout proxy) before allowing another
            // source to start, so late completion cannot resurrect or stop newer capture.
            if (work is not null) ready = await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            lock (_gate)
            {
                ready &= MicrophoneEpochCurrentLocked(epoch);
                if (!ready)
                {
                    if (_microphoneVersions.GetValueOrDefault(epoch.Address) == epoch.Version)
                    {
                        _microphoneOwners.Remove(epoch.Address);
                        if (_microphoneDesiredOwnerAddress == epoch.Address) _microphoneDesiredOwnerAddress = null;
                        if (_microphoneEpochs.TryGetValue(epoch.Address, out var current) && ReferenceEquals(current, epoch))
                            _microphoneEpochs.Remove(epoch.Address);
                    }
                    if (invoked && (_microphoneSourceOwnerAddress == epoch.Address || _microphoneSourceOwnerAddress is null))
                    {
                        _microphoneSourceOwnerAddress = null;
                        try { SharedMicrophoneStopRequested?.Invoke(epoch.Address); } catch { }
                    }
                }
                epoch.Result.TrySetResult(ready);
            }
            if (acquired) _microphoneStartGate.Release();
        }
    }
    private bool MicrophoneEpochCurrentLocked(MicrophoneEpoch epoch) => !_disposed &&
        _microphoneDesiredOwnerAddress == epoch.Address && _microphoneVersions.GetValueOrDefault(epoch.Address) == epoch.Version && _microphoneOwners.ContainsKey(epoch.Address);
    private void StopOwnedMicrophoneLocked(string address)
    {
        if (_microphoneSourceOwnerAddress != address) return;
        _microphoneSourceOwnerAddress = null; SharedMicrophoneStopRequested?.Invoke(address);
    }
    private async Task MicrophoneLeaseLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    var now = Environment.TickCount64;
                    foreach (var address in _microphoneOwners.Where(p => now - p.Value >= MicrophoneSourceLeaseTimeout.TotalMilliseconds).Select(p => p.Key).ToArray())
                    {
                        _microphoneOwners.Remove(address); _microphoneVersions[address] = _microphoneVersions.GetValueOrDefault(address) + 1;
                        if (_microphoneDesiredOwnerAddress == address) _microphoneDesiredOwnerAddress = null;
                        if (_microphoneEpochs.Remove(address, out var epoch)) epoch.Result.TrySetResult(false);
                        try { StopOwnedMicrophoneLocked(address); } catch { }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task ReplyMicrophoneAsync(IPEndPoint target, string nonce, MicrophoneRequest request, CancellationToken ct)
    {
        try
        {
            var ready = await request.Result.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate) ready &= !_disposed && (request.Kind != "MIC_START" ||
                (_microphoneVersions.GetValueOrDefault(target.Address.ToString()) == request.Version && _microphoneOwners.ContainsKey(target.Address.ToString())));
            var kind = request.Kind == "MIC_START" ? "MIC_READY" : "MIC_STOPPED"; var result = ready ? "1" : "0";
            var bytes = Encoding.UTF8.GetBytes($"{(kind == "MIC_READY" ? MicReady : MicStopped)}{nonce} {request.Timestamp} {request.Port} {result} " +
                CameraSignature(kind, nonce, request.Timestamp, request.Port, result));
            await _listener.SendAsync(bytes, target, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }
    public Task<bool> PrepareRemoteMicrophoneAsync(string address, int port = 45684, CancellationToken ct = default) =>
        SendMicrophoneControlAsync(address, port, true, ct);
    public Task<bool> StopRemoteMicrophoneAsync(string address, CancellationToken ct = default) =>
        SendMicrophoneControlAsync(address, 0, false, ct);
    /// <summary>
    /// Called only for the current sender instance after unexpected stream termination
    /// or local sharing permission revocation. Invalidates cached READY so the next
    /// permitted fresh request can reopen the physical source.
    /// </summary>
    public bool ReportSharedMicrophoneSourceEnded(string address, int port)
    {
        lock (_gate)
        {
            if (_disposed || _microphoneSourceOwnerAddress != address || !_microphoneEpochs.TryGetValue(address, out var epoch) || epoch.Port != port) return false;
            _microphoneOwners.Remove(address); _microphoneEpochs.Remove(address);
            if (_microphoneDesiredOwnerAddress == address) _microphoneDesiredOwnerAddress = null;
            _microphoneVersions[address] = _microphoneVersions.GetValueOrDefault(address) + 1;
            epoch.Result.TrySetResult(false);
            try { StopOwnedMicrophoneLocked(address); } catch { }
            return true;
        }
    }
    private async Task<bool> SendMicrophoneControlAsync(string address, int port, bool start, CancellationToken ct)
    {
        if (!MicrophoneSecretValid) return false;
        if (start && port is < 1025 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var ip = IPAddress.Parse(address); address = ip.ToString();
        CancellationTokenSource linked; string timestamp;
        lock (_gate)
        {
            if (_disposed) return false;
            // A STOP supersedes a pending local START immediately, including its UDP retries.
            if (_microphoneOutgoing.TryGetValue(address, out var previous)) previous.Cancel();
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token); _microphoneOutgoing[address] = linked;
            var stamp = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _microphoneOutgoingStamp.GetValueOrDefault(address) + 1);
            _microphoneOutgoingStamp[address] = stamp; timestamp = stamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        try
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var nonce = Guid.NewGuid().ToString("N"); var kind = start ? "MIC_START" : "MIC_STOP";
            var target = new IPEndPoint(ip, _discoveryPort);
            var message = Encoding.UTF8.GetBytes($"{(start ? MicStart : MicStop)}{nonce} {timestamp} {port} " + CameraSignature(kind, nonce, timestamp, port));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await client.SendAsync(message, target, linked.Token).ConfigureAwait(false);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(linked.Token); wait.CancelAfter(start ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2));
                try
                {
                    while (!wait.IsCancellationRequested)
                    {
                        var reply = await client.ReceiveAsync(wait.Token).ConfigureAwait(false);
                        if (!reply.RemoteEndPoint.Address.Equals(target.Address) || reply.RemoteEndPoint.Port != _discoveryPort || reply.Buffer.Length > 1024) continue;
                        var prefix = start ? MicReady : MicStopped; var text = Encoding.UTF8.GetString(reply.Buffer);
                        if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        var fields = text[prefix.Length..].Split(' '); var replyKind = start ? "MIC_READY" : "MIC_STOPPED";
                        if (fields.Length == 5 && fields[0] == nonce && fields[1] == timestamp && fields[2] == port.ToString() && fields[3] is "0" or "1" &&
                            ValidCameraSignature(replyKind, nonce, timestamp, port, fields[3], fields[4])) return fields[3] == "1";
                    }
                }
                catch (OperationCanceledException) when (!linked.IsCancellationRequested) { }
            }
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_cts.IsCancellationRequested) { return false; }
        finally
        {
            lock (_gate) if (_microphoneOutgoing.TryGetValue(address, out var current) && ReferenceEquals(current, linked)) _microphoneOutgoing.Remove(address);
            linked.Dispose();
        }
    }

    private string CameraSignature(string kind, string nonce, string timestamp, int port, string result = "") =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.ASCII.GetBytes(PairingSecret),
            Encoding.UTF8.GetBytes($"{kind}|{nonce}|{timestamp}|{port}|{result}")));
    private bool ValidCameraSignature(string kind, string nonce, string timestamp, int port, string result, string signature)
    {
        if (signature.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature),
            Convert.FromHexString(CameraSignature(kind, nonce, timestamp, port, result))); }
        catch (FormatException) { return false; }
    }
    private bool CameraSecretValid => PairingSecret.Length == 32 && PairingSecret.All(c => c is >= '!' and <= '~');
    private void CameraControlError(string message)
    {
        lock (_gate) _lastCameraControlError = message;
        try { LogEmitted?.Invoke("[提示] " + message); } catch { }
    }
    private void RejectLegacyCameraRequest(string text, IPEndPoint remote)
    {
        var start = text.StartsWith(LegacyCameraStart, StringComparison.Ordinal);
        var fields = text[(start ? LegacyCameraStart.Length : LegacyCameraStop.Length)..].Split(' ');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!CameraSecretValid || fields.Length != 4 || !Guid.TryParseExact(fields[0], "N", out _) ||
            !long.TryParse(fields[1], out var stamp) || stamp < now - 30 || stamp > now + 30 ||
            !int.TryParse(fields[2], out var port) || (start ? port is < 1025 or > 65535 : port != 0) ||
            !ValidCameraSignature(start ? "CAMERA_START" : "CAMERA_STOP", fields[0], fields[1], port, "", fields[3]) ||
            !Snapshot().Any(peer => peer.Address == remote.Address.ToString())) return;
        CameraControlError("已拒绝旧版无序摄像头控制；请同时更新 Windows 和 Mac 到 CAMERA2 协议。");
    }
    private void TrackCameraTaskLocked(Task task)
    {
        _cameraTasks.Add(task);
        _ = task.ContinueWith(_ => { lock (_gate) _cameraTasks.Remove(task); }, TaskScheduler.Default);
    }
    private void ReplyCameraTrackedLocked(IPEndPoint target, string nonce, CameraRequest request)
    {
        var key = target + ":" + nonce;
        // UDP retry from the same socket already has a reply waiting for readiness;
        // it must not allocate another task for the same pending nonce.
        if (!_cameraPendingReplies.Add(key)) return;
        var task = ReplyCameraAsync(target, nonce, request, _cts.Token);
        TrackCameraTaskLocked(task);
        _ = task.ContinueWith(_ => { lock (_gate) _cameraPendingReplies.Remove(key); }, TaskScheduler.Default);
    }
    private void HandleCameraRequest(string text, IPEndPoint remote)
    {
        var start = text.StartsWith(CameraStart, StringComparison.Ordinal);
        var kind = start ? "CAMERA2_START" : "CAMERA2_STOP";
        var fields = text[(start ? CameraStart.Length : CameraStop.Length)..].Split(' ');
        var now = DateTimeOffset.UtcNow;
        // CAMERA2 timestamps are Unix milliseconds, permitting strict ordering even for a
        // rapid start/stop within the same second. The signed validity window is +/-30s.
        if (!CameraSecretValid || fields.Length != 4 || !Guid.TryParseExact(fields[0], "N", out _) ||
            !long.TryParse(fields[1], out var stamp) || stamp < now.ToUnixTimeMilliseconds() - 30000 ||
            stamp > now.ToUnixTimeMilliseconds() + 30000 || !int.TryParse(fields[2], out var port) ||
            (start ? port is < 1025 or > 65535 : port != 0) || !ValidCameraSignature(kind, fields[0], fields[1], port, "", fields[3])) return;
        var address = remote.Address.ToString();
        if (!Snapshot().Any(peer => peer.Address == address)) return;
        lock (_gate)
        {
            if (_disposed || (start && _cameraTasks.Count >= 64)) return;
            foreach (var expired in _cameraNonces.Where(p => p.Value.Accepted < now.AddSeconds(-65)).Select(p => p.Key).ToArray())
                _cameraNonces.Remove(expired);
            var key = address + ":" + fields[0];
            if (_cameraNonces.TryGetValue(key, out var replay))
            {
                if (replay.Kind == kind && replay.Timestamp == fields[1] && replay.Port == port)
                    ReplyCameraTrackedLocked(remote, fields[0], replay);
                // Replays may receive their cached reply but cannot refresh the lease.
                return;
            }
            if ((start && _cameraNonces.Count >= 4096) || stamp <= _cameraLatestStamp.GetValueOrDefault(address)) return;
            var previous = _cameraVersions.GetValueOrDefault(address);
            var renewal = start && _cameraDesiredOwnerAddress == address && _cameraOwners.ContainsKey(address) && _cameraEpochs.TryGetValue(address, out var old) && old.Port == port;
            var version = renewal ? previous : previous + 1;
            _cameraLatestStamp[address] = stamp; _cameraVersions[address] = version;
            Task<bool> result;
            if (start)
            {
                if (_cameraDesiredOwnerAddress is { } previousOwner && previousOwner != address)
                {
                    _cameraOwners.Remove(previousOwner);
                    _cameraVersions[previousOwner] = _cameraVersions.GetValueOrDefault(previousOwner) + 1;
                    if (_cameraEpochs.Remove(previousOwner, out var previousEpoch)) previousEpoch.Result.TrySetResult(false);
                    try { StopOwnedCameraLocked(previousOwner); } catch { }
                }
                _cameraDesiredOwnerAddress = address;
                _cameraOwners[address] = Environment.TickCount64;
                if (!renewal)
                {
                    if (_cameraEpochs.Remove(address, out var previousEpoch)) previousEpoch.Result.TrySetResult(false);
                    try { StopOwnedCameraLocked(address); } catch { }
                    var epoch = new CameraEpoch(address, port, version); _cameraEpochs[address] = epoch;
                    TrackCameraTaskLocked(Task.Run(() => ProcessCameraStartAsync(epoch, _cts.Token)));
                }
                result = _cameraEpochs[address].Result.Task;
            }
            else
            {
                _cameraOwners.Remove(address);
                if (_cameraDesiredOwnerAddress == address) _cameraDesiredOwnerAddress = null;
                if (_cameraEpochs.Remove(address, out var epoch)) epoch.Result.TrySetResult(false);
                var stopped = true;
                try { StopOwnedCameraLocked(address); } catch { stopped = false; }
                result = Task.FromResult(stopped);
            }
            var request = new CameraRequest(kind, fields[1], port, now, version, result);
            // STOP takes priority over a full pending START/reply budget. Per-owner
            // monotonic timestamps still prevent an evicted nonce reopening a source.
            if (_cameraNonces.Count >= 4096) _cameraNonces.Remove(_cameraNonces.MinBy(pair => pair.Value.Accepted).Key);
            _cameraNonces[key] = request;
            ReplyCameraTrackedLocked(remote, fields[0], request);
        }
    }
    private async Task ProcessCameraStartAsync(CameraEpoch epoch, CancellationToken ct)
    {
        var acquired = false; var invoked = false; var ready = false;
        try
        {
            // Async source callbacks are serialized independently from UDP discovery and
            // camera control. An old callback must settle before a newer one can open hardware.
            await _cameraStartGate.WaitAsync(ct).ConfigureAwait(false); acquired = true;
            Task<bool>? work;
            lock (_gate)
            {
                if (!CameraEpochCurrentLocked(epoch)) return;
                if (_cameraSourceOwnerAddress is { } previousOwner)
                {
                    if (previousOwner != epoch.Address)
                    {
                        _cameraOwners.Remove(previousOwner);
                        _cameraVersions[previousOwner] = _cameraVersions.GetValueOrDefault(previousOwner) + 1;
                        if (_cameraEpochs.Remove(previousOwner, out var previousEpoch)) previousEpoch.Result.TrySetResult(false);
                    }
                    // Same owner with a different port is a new epoch too; only genuine
                    // lease renewals bypass Start and leave the existing source untouched.
                    StopOwnedCameraLocked(previousOwner);
                }
                _cameraSourceOwnerAddress = epoch.Address; invoked = true;
                work = SharedCameraStartRequested?.Invoke(epoch.Address, epoch.Port);
            }
            // Source callbacks must honor synchronous Stop and bound their own I/O. Await
            // the actual callback (not an abandoned timeout proxy) before allowing another
            // source to start, so late completion cannot resurrect or stop newer capture.
            if (work is not null) ready = await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            lock (_gate)
            {
                ready &= CameraEpochCurrentLocked(epoch);
                if (!ready)
                {
                    if (_cameraVersions.GetValueOrDefault(epoch.Address) == epoch.Version)
                    {
                        _cameraOwners.Remove(epoch.Address);
                        if (_cameraDesiredOwnerAddress == epoch.Address) _cameraDesiredOwnerAddress = null;
                        if (_cameraEpochs.TryGetValue(epoch.Address, out var current) && ReferenceEquals(current, epoch))
                            _cameraEpochs.Remove(epoch.Address);
                    }
                    if (invoked && (_cameraSourceOwnerAddress == epoch.Address || _cameraSourceOwnerAddress is null))
                    {
                        _cameraSourceOwnerAddress = null;
                        try { SharedCameraStopRequested?.Invoke(epoch.Address); } catch { }
                    }
                }
                epoch.Result.TrySetResult(ready);
            }
            if (acquired) _cameraStartGate.Release();
        }
    }
    private bool CameraEpochCurrentLocked(CameraEpoch epoch) => !_disposed &&
        _cameraDesiredOwnerAddress == epoch.Address && _cameraVersions.GetValueOrDefault(epoch.Address) == epoch.Version && _cameraOwners.ContainsKey(epoch.Address);
    private void StopOwnedCameraLocked(string address)
    {
        if (_cameraSourceOwnerAddress != address) return;
        _cameraSourceOwnerAddress = null; SharedCameraStopRequested?.Invoke(address);
    }
    private async Task CameraLeaseLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    var now = Environment.TickCount64;
                    foreach (var address in _cameraOwners.Where(p => now - p.Value >= CameraSourceLeaseTimeout.TotalMilliseconds).Select(p => p.Key).ToArray())
                    {
                        _cameraOwners.Remove(address); _cameraVersions[address] = _cameraVersions.GetValueOrDefault(address) + 1;
                        if (_cameraDesiredOwnerAddress == address) _cameraDesiredOwnerAddress = null;
                        if (_cameraEpochs.Remove(address, out var epoch)) epoch.Result.TrySetResult(false);
                        try { StopOwnedCameraLocked(address); } catch { }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task ReplyCameraAsync(IPEndPoint target, string nonce, CameraRequest request, CancellationToken ct)
    {
        try
        {
            var ready = await request.Result.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate) ready &= !_disposed && (request.Kind != "CAMERA2_START" ||
                (_cameraVersions.GetValueOrDefault(target.Address.ToString()) == request.Version && _cameraOwners.ContainsKey(target.Address.ToString())));
            var kind = request.Kind == "CAMERA2_START" ? "CAMERA2_READY" : "CAMERA2_STOPPED"; var result = ready ? "1" : "0";
            var bytes = Encoding.UTF8.GetBytes($"{(kind == "CAMERA2_READY" ? CameraReady : CameraStopped)}{nonce} {request.Timestamp} {request.Port} {result} " +
                CameraSignature(kind, nonce, request.Timestamp, request.Port, result));
            await _listener.SendAsync(bytes, target, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }
    public Task<bool> PrepareRemoteCameraAsync(string address, int port, CancellationToken ct = default) =>
        SendCameraControlAsync(address, port, true, ct);
    public Task<bool> StopRemoteCameraAsync(string address, CancellationToken ct = default) =>
        SendCameraControlAsync(address, 0, false, ct);
    /// <summary>
    /// Called only for the current sender instance after unexpected stream termination
    /// or local sharing permission revocation. Invalidates cached READY so the next
    /// permitted fresh request can reopen the physical source.
    /// </summary>
    public bool ReportSharedCameraSourceEnded(string address, int port)
        => ReportSharedCameraSourceEndedCore(address, port, null);
    /// <summary>Current camera source epoch; capture it in Start's synchronous portion before awaiting.</summary>
    public long? GetSharedCameraSourceVersion(string address, int port)
    {
        lock (_gate) return !_disposed && _cameraSourceOwnerAddress == address && _cameraEpochs.TryGetValue(address, out var epoch) &&
            epoch.Port == port && CameraEpochCurrentLocked(epoch) ? epoch.Version : null;
    }
    /// <summary>Rejects an EOF from an older source even after the same address/port has reopened.</summary>
    public bool ReportSharedCameraSourceEnded(string address, int port, long expectedVersion)
        => ReportSharedCameraSourceEndedCore(address, port, expectedVersion);
    private bool ReportSharedCameraSourceEndedCore(string address, int port, long? expectedVersion)
    {
        lock (_gate)
        {
            if (_disposed || _cameraSourceOwnerAddress != address || !_cameraEpochs.TryGetValue(address, out var epoch) || epoch.Port != port ||
                (expectedVersion is not null && epoch.Version != expectedVersion)) return false;
            _cameraOwners.Remove(address); _cameraEpochs.Remove(address);
            if (_cameraDesiredOwnerAddress == address) _cameraDesiredOwnerAddress = null;
            _cameraVersions[address] = _cameraVersions.GetValueOrDefault(address) + 1;
            epoch.Result.TrySetResult(false);
            try { StopOwnedCameraLocked(address); } catch { }
            return true;
        }
    }
    private async Task<bool> SendCameraControlAsync(string address, int port, bool start, CancellationToken ct)
    {
        if (!CameraSecretValid) { CameraControlError("摄像头配对密钥未配置或格式无效。"); return false; }
        if (start && port is < 1025 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var ip = IPAddress.Parse(address); address = ip.ToString();
        var hint = Snapshot().FirstOrDefault(peer => peer.Address == address);
        if (hint is not null && hint.CameraControlProtocolVersion != CameraControlProtocolVersion)
            CameraControlError("对端未声明 CAMERA2 摄像头能力；只尝试有序协议，请同时更新两端，不会降级到旧版。");
        CancellationTokenSource linked; string timestamp;
        lock (_gate)
        {
            if (_disposed) return false;
            // A STOP supersedes a pending local START immediately, including its UDP retries.
            if (_cameraOutgoing.TryGetValue(address, out var previous)) previous.Cancel();
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token); _cameraOutgoing[address] = linked;
            var stamp = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _cameraOutgoingStamp.GetValueOrDefault(address) + 1);
            _cameraOutgoingStamp[address] = stamp; timestamp = stamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        try
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var nonce = Guid.NewGuid().ToString("N"); var kind = start ? "CAMERA2_START" : "CAMERA2_STOP";
            var target = new IPEndPoint(ip, _discoveryPort);
            var message = Encoding.UTF8.GetBytes($"{(start ? CameraStart : CameraStop)}{nonce} {timestamp} {port} " + CameraSignature(kind, nonce, timestamp, port));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await client.SendAsync(message, target, linked.Token).ConfigureAwait(false);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(linked.Token); wait.CancelAfter(start ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2));
                try
                {
                    while (!wait.IsCancellationRequested)
                    {
                        var reply = await client.ReceiveAsync(wait.Token).ConfigureAwait(false);
                        if (!reply.RemoteEndPoint.Address.Equals(target.Address) || reply.RemoteEndPoint.Port != _discoveryPort || reply.Buffer.Length > 1024) continue;
                        var prefix = start ? CameraReady : CameraStopped; var text = Encoding.UTF8.GetString(reply.Buffer);
                        var legacyPrefix = start ? "LINKBRIDGE/1 CAMERA_READY " : "LINKBRIDGE/1 CAMERA_STOPPED ";
                        if (text.StartsWith(legacyPrefix, StringComparison.Ordinal))
                        {
                            var old = text[legacyPrefix.Length..].Split(' ');
                            if (old.Length == 5 && old[0] == nonce && old[1] == timestamp && old[2] == port.ToString() && old[3] is "0" or "1" &&
                                ValidCameraSignature(start ? "CAMERA_READY" : "CAMERA_STOPPED", nonce, timestamp, port, old[3], old[4]))
                            { CameraControlError("对端返回旧版无序摄像头协议，已拒绝降级；请同时更新 Windows 和 Mac。"); return false; }
                            continue;
                        }
                        if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        var fields = text[prefix.Length..].Split(' '); var replyKind = start ? "CAMERA2_READY" : "CAMERA2_STOPPED";
                        if (fields.Length == 5 && fields[0] == nonce && fields[1] == timestamp && fields[2] == port.ToString() && fields[3] is "0" or "1" &&
                            ValidCameraSignature(replyKind, nonce, timestamp, port, fields[3], fields[4]))
                        {
                            if (fields[3] == "1") { lock (_gate) _lastCameraControlError = null; return true; }
                            CameraControlError("对端摄像头未就绪或请求已失效；请检查授权、原生4K能力及当前接收需求。"); return false;
                        }
                    }
                }
                catch (OperationCanceledException) when (!linked.IsCancellationRequested) { }
            }
            CameraControlError("未收到已认证的 CAMERA2 摄像头回应；请同时更新两端并检查配对与网络。"); return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_cts.IsCancellationRequested) { return false; }
        finally
        {
            lock (_gate) if (_cameraOutgoing.TryGetValue(address, out var current) && ReferenceEquals(current, linked)) _cameraOutgoing.Remove(address);
            linked.Dispose();
        }
    }

    private string Signature(string kind, string nonce, string timestamp) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.ASCII.GetBytes(PairingSecret), Encoding.UTF8.GetBytes(kind + "|" + nonce + "|" + timestamp)));
    private bool ValidSignature(string kind, string nonce, string timestamp, string signature)
    {
        if (signature.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature), Convert.FromHexString(Signature(kind, nonce, timestamp))); }
        catch (FormatException) { return false; }
    }

    /// <summary>Invalidate cached media READY without forgetting accepted nonces or allowing replayed resets.</summary>
    public void ReportMediaReceiverStopped()
    {
        lock (_gate) { ++_mediaReceiverEpoch; }
    }

    /// <summary>让已发现的对端重置解码器，收到确认后再开始发新流。</summary>
    public async Task<bool> PrepareRemoteReceiverAsync(string address, CancellationToken ct = default)
    {
        if (PairingSecret.Length != 32) return false;
        using var client = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var nonce = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var message = Encoding.UTF8.GetBytes(MediaStart + nonce + " " + timestamp + " " + Signature("START", nonce, timestamp));
        var target = new IPEndPoint(IPAddress.Parse(address), _discoveryPort);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await client.SendAsync(message, target, ct).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var reply = await client.ReceiveAsync(wait.Token).ConfigureAwait(false);
                if (reply.RemoteEndPoint.Address.Equals(target.Address) &&
                    Encoding.UTF8.GetString(reply.Buffer) == MediaReady + nonce + " " + timestamp + " " + Signature("READY", nonce, timestamp))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 对端忙或丢包，再试一次。
            }
        }
        return false;
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var bytes = Encoding.UTF8.GetBytes(Magic + JsonSerializer.Serialize(new PeerPayload
            {
                Instance = InstanceId,
                Name = DisplayName,
                Platform = PlatformInfo.DisplayName,
                MediaPort = MediaPort,
                AudioOutputs = FilterDevices(AudioOutputs, MediaDeviceKind.Speaker, 32),
                Cameras = FilterDevices(Cameras, MediaDeviceKind.Camera, 16),
                Microphones = FilterDevices(Microphones, MediaDeviceKind.Microphone, 16),
                HasSharedMicrophoneInput = HasSharedMicrophoneInput,
                CameraControlProtocolVersion = CameraControlProtocolVersion,
            }));
            PeersChanged?.Invoke(Snapshot());
            // 关键：不能只发 255.255.255.255。
            // 装了 Tailscale / Clash(TUN) / WSL / Hyper-V 的机器上，
            // 全量广播常常被虚拟网卡接管，包出去就回不到真实局域网了。
            // 这里改成「逐网卡算子网广播地址」。
            foreach (var target in IsDiscoverable ? BuildBroadcastTargets() : [])
            {
                try
                {
                    using var sender = new UdpClient();
                    sender.EnableBroadcast = true;
                    sender.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                    await sender.SendAsync(bytes, target, ct).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // 某块网卡不可用就跳过
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }

            try
            {
                await Task.Delay(BroadcastInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>算出每一块「真实」网卡的子网广播地址。</summary>
    private IEnumerable<IPEndPoint> BuildBroadcastTargets()
    {
        var found = false;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            if (IsVirtualAdapter(ni))
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = ni.GetIPProperties();
            }
            catch
            {
                continue;
            }

            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var mask = unicast.IPv4Mask;
                if (mask is null)
                {
                    continue;
                }

                var broadcast = ToBroadcast(unicast.Address, mask);
                if (broadcast is null)
                {
                    continue;
                }

                found = true;
                yield return new IPEndPoint(broadcast, _discoveryPort);
            }
        }

        // 一块真实网卡都没找到就退回全量广播，至少别什么都不发
        if (!found)
        {
            yield return new IPEndPoint(IPAddress.Broadcast, _discoveryPort);
        }
    }

    /// <summary>虚拟网卡会让广播跑偏，直接跳过。</summary>
    private static bool IsVirtualAdapter(NetworkInterface ni)
    {
        var text = $"{ni.Name} {ni.Description}";

        return text.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Clash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("WSL", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
               || text.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
               || text.Contains("VMware", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
               || text.Contains("VPN", StringComparison.OrdinalIgnoreCase)
               || text.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
               || text.Contains("utun", StringComparison.OrdinalIgnoreCase);
    }

    private static IPAddress? ToBroadcast(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();

        if (a.Length != 4 || m.Length != 4)
        {
            return null;
        }

        var result = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            result[i] = (byte)(a[i] | ~m[i]);
        }

        return new IPAddress(result);
    }

    /// <summary>调用方需持有 _gate。</summary>
    private void Prune()
    {
        var deadline = DateTimeOffset.Now - PeerTimeout;
        foreach (var key in _peers.Where(p => p.Value.LastSeen < deadline).Select(p => p.Key).ToList())
        {
            _peers.Remove(key);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        string[] owners, microphoneOwners;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _listener.Dispose();
            owners = _cameraSourceOwnerAddress is { } owner ? [owner] : [];
            _cameraSourceOwnerAddress = null; _cameraDesiredOwnerAddress = null; _cameraOwners.Clear();
            foreach (var address in _cameraVersions.Keys.ToArray()) _cameraVersions[address]++;
            foreach (var epoch in _cameraEpochs.Values) epoch.Result.TrySetResult(false);
            _cameraEpochs.Clear();
            microphoneOwners = _microphoneSourceOwnerAddress is { } microphoneOwner ? [microphoneOwner] : [];
            _microphoneSourceOwnerAddress = null; _microphoneDesiredOwnerAddress = null; _microphoneOwners.Clear();
            foreach (var address in _microphoneVersions.Keys.ToArray()) _microphoneVersions[address]++;
            foreach (var epoch in _microphoneEpochs.Values) epoch.Result.TrySetResult(false);
            _microphoneEpochs.Clear();
            tasks = _cameraTasks.Concat(_microphoneTasks).Concat(new[] { _listenTask, _broadcastTask, _cameraLeaseTask, _microphoneLeaseTask }.OfType<Task>()).ToArray();
        }
        foreach (var owner in owners) { try { SharedCameraStopRequested?.Invoke(owner); } catch { } }
        foreach (var owner in microphoneOwners) { try { SharedMicrophoneStopRequested?.Invoke(owner); } catch { } }
        var all = Task.WhenAll(tasks);
        try { await all.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (all.IsCompleted) _cts.Dispose();
            else _ = all.ContinueWith(_ => _cts.Dispose(), TaskScheduler.Default);
        }
    }
    private sealed class PeerPayload
    {
        [JsonPropertyName("i")]
        public string? Instance { get; set; }

        [JsonPropertyName("n")]
        public string? Name { get; set; }

        [JsonPropertyName("p")]
        public string? Platform { get; set; }

        [JsonPropertyName("mp")]
        public int MediaPort { get; set; }

        [JsonPropertyName("ao")]
        public MediaDevice[]? AudioOutputs { get; set; }
        [JsonPropertyName("ca")]
        public MediaDevice[]? Cameras { get; set; }
        [JsonPropertyName("mi")]
        public MediaDevice[]? Microphones { get; set; }
        [JsonPropertyName("miin")]
        public bool HasSharedMicrophoneInput { get; set; }
        [JsonPropertyName("cv")]
        public int CameraControlProtocolVersion { get; set; }
    }
}







