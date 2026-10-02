using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using LinkBridge.App.ViewModels;
using LinkBridge.Core;
using LinkBridge.Core.Discovery;
using LinkBridge.Core.Models;
using LinkBridge.Media;
using LinkBridge.Media.Camera;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Current probe is verified on Windows only.");
await LateReadyAfterShutdown();
await CancelPendingThenRestart();
await SupersedePendingGeneration();
await PeerUpdateAndActivePort();
await ShutdownCancellationPrefix();
Console.WriteLine("PASS AppIntegrationProbe complete: real MainVM methods, no GUI, user settings, physical media or production listeners.");

static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

static async Task LateReadyAfterShutdown()
{
    await using var f = new Fixture();
    var pending = f.Start();
    var request = await f.Request();
    await f.Vm.ShutdownAsync();
    await f.Ready(request);
    await pending.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(!f.Vm.IsSharing && !f.Vm.IsStartingShare && f.Session.Target is null && f.Launches == 0,
        "Delayed READY resurrected a shutdown VM.");
    await f.Start().WaitAsync(TimeSpan.FromSeconds(1));
    Assert(f.Launches == 0, "Start after VM shutdown created a process.");
    Console.WriteLine("PASS late authenticated READY after Shutdown: IsSharing=false, IsStartingShare=false, Target=null, synthetic launches=0; post-shutdown Start ignored.");
}

static async Task CancelPendingThenRestart()
{
    await using var f = new Fixture();
    var pending = f.Start();
    var request = await f.Request();
    f.Stop();
    await f.Ready(request);
    await pending.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(!f.Vm.IsSharing && !f.Vm.IsStartingShare && f.Launches == 0, "Cancel did not revoke pending media start.");
    var restarted = f.Start();
    await f.Ready(await f.Request());
    await restarted.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(f.Vm.IsSharing && !f.Vm.IsStartingShare && f.Session.IsRunning && f.Launches == 1, "Fresh Start after cancel failed.");
    f.Stop();
    Assert(!f.Session.IsRunning && f.Session.Target is null, "Stop failed to retire the synthetic child.");
    Console.WriteLine("PASS StopPending rejects delayed READY; fresh Start succeeds once with synthetic lavfi; subsequent Stop clears process/Target.");
}

static async Task SupersedePendingGeneration()
{
    await using var f = new Fixture();
    var first = f.Start(); var firstRequest = await f.Request();
    var second = f.Start(); var secondRequest = await f.Request();
    await f.Ready(firstRequest);
    await first.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(f.Launches == 0 && f.Vm.IsStartingShare, "Old generation altered new pending start state.");
    await f.Ready(secondRequest);
    await second.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(f.Launches == 1 && f.Vm.IsSharing && !f.Vm.IsStartingShare, "New generation did not commit exactly once.");
    f.Stop();
    Console.WriteLine("PASS superseded pending generation: old READY ignored; new pending state retained; newest READY starts exactly one synthetic child.");
}

static async Task PeerUpdateAndActivePort()
{
    await using var f = new Fixture();
    var selected = f.Vm.Peers[0]; f.Vm.SelectedPeer = selected;
    var replacement = selected with { MediaPort = 49001, HasSharedMicrophoneInput = true,
        Microphones = [new(MediaDeviceKind.Microphone, "isolated-id", "isolated mic", MediaBackend.AvFoundation)] };
    Fixture.Invoke(f.Vm, "SyncPeers", (object)new[] { replacement });
    Assert(f.Vm.Peers[0].MediaPort == 49001 && f.Vm.SelectedPeer?.MediaPort == 49001 &&
        f.Vm.SelectedPeer.HasSharedMicrophoneInput && f.Vm.SelectedPeer.Microphones.Length == 1,
        "Same-key Peer fields or selected Peer remained stale.");
    f.Vm.MediaPortText = "49001";
    Fixture.Invoke(f.Vm, "StartReceiving");
    var requested = (int)Fixture.Field(f.Receiver, "_requestedPort")!;
    Assert(f.Vm.Settings.MediaPort == 49001 && f.Discovery.MediaPort == 49000 && requested == 49000,
        "Configured next-start media port partially changed the active receive/discovery port.");
    Assert(!f.Receiver.TryAcceptAuthenticatedSource("127.0.0.1", 49001), "Inactive next-start port was accepted.");
    Assert(!f.Receiver.IsRunning && f.Receiver.IsListeningRequested, "Unpaired receive readiness created a decoder or lost receive intent.");
    Console.WriteLine("PASS same-key Peer port/capability/inventory updated and selection preserved; settings next port=49001, active discovery/receiver=49000; inactive-port bind rejected without FFmpeg.");
}

static async Task ShutdownCancellationPrefix()
{
    await using var f = new Fixture();
    Fixture.Set(f.Vm, "_initializationTask", new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);
    var elapsed = Stopwatch.StartNew();
    var shutdown = f.Vm.ShutdownAsync().AsTask();
    Assert((bool)Fixture.Field(f.AudioSender, "_disposed")! && !shutdown.IsCompleted,
        "Audio shutdown prefix waited behind a pending initialization task.");
    await shutdown.WaitAsync(TimeSpan.FromSeconds(7));
    Assert(elapsed.Elapsed < TimeSpan.FromSeconds(7), "Hung initialization escaped cleanup bound.");
    Console.WriteLine($"PASS shutdown invokes audio cancellation prefix before await; permanently pending initialization bounded; elapsed={elapsed.Elapsed.TotalSeconds:F2}s.");
}

public sealed class Fixture : IAsyncDisposable
{
    private readonly UdpClient _responder;
    private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly string _directory;
    private readonly ConcurrentBag<int> _children = [];
    private int _launches;
    public MainViewModel Vm { get; }
    public PeerDiscovery Discovery { get; }
    public MediaStreamSession Session { get; }
    public MediaStreamReceiver Receiver { get; } = new();
    public SharedAudioSender AudioSender { get; } = new();
    public int Launches => Volatile.Read(ref _launches);

    public Fixture()
    {
        // Do not call MainViewModel's constructor: it loads actual user settings.
        Vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        _directory = Path.Combine(Path.GetTempPath(), "linkbridge-app-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _responder = new UdpClient();
        _responder.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _responder.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)_responder.Client.LocalEndPoint!).Port;
        Discovery = new PeerDiscovery(49000, port) { PairingSecret = _secret, IsDiscoverable = false };
        // Discovery.Start is deliberately not called: no advertising or production service.
        Set(Vm, "<Settings>k__BackingField", new AppSettings { MediaPort = 49000, InputPairingSecret = _secret });
        Set(Vm, "_activeMediaPort", 49000);
        var tile = (DeviceTile)RuntimeHelpers.GetUninitializedObject(typeof(DeviceTile));
        Set(tile, "<Kind>k__BackingField", TileKind.Camera);
        Set(tile, "<Media>k__BackingField", new MediaDevice(MediaDeviceKind.Camera,
            "__LinkBridge_No_Physical_Device__", "isolated synthetic test", MediaBackend.DirectShow));
        tile.IsSelected = true;
        Set(Vm, "<Devices>k__BackingField", new ObservableCollection<DeviceTile> { tile });
        Set(Vm, "<Peers>k__BackingField", new ObservableCollection<PeerInfo>
            { new("isolated responder", "127.0.0.1", "Mac", 49000, DateTimeOffset.UtcNow) });
        Set(Vm, "<RemoteAudioDevices>k__BackingField", new ObservableCollection<PlaybackDeviceOption>());
        Set(Vm, "_remoteCameras", new List<DeviceTile>());
        Set(Vm, "_discovery", Discovery);
        var constructor = typeof(MediaStreamSession).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(Func<ProcessStartInfo, Process>)], null)!;
        Session = (MediaStreamSession)constructor.Invoke([(Func<ProcessStartInfo, Process>)(info =>
        {
            Interlocked.Increment(ref _launches);
            info.ArgumentList.Clear();
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "warning", "-nostdin", "-re",
                "-f", "lavfi", "-i", "color=c=black:s=16x16:r=30", "-f", "null", "-" })
                info.ArgumentList.Add(argument);
            return new Process { StartInfo = info };
        })]);
        Session.LogEmitted += line =>
        {
            if (line.Contains("已开始共享") && Field(Session, "_process") is Process process) _children.Add(process.Id);
        };
        Set(Vm, "_session", Session); Set(Vm, "_receiver", Receiver);
        Set(Vm, "_audioSender", AudioSender); Set(Vm, "_cameraProducer", new SharedCameraProducer());
        Set(Vm, "_lifetimeCts", new CancellationTokenSource());
        Set(Vm, "_floatTimer", new Avalonia.Threading.DispatcherTimer());
        Set(Vm, "_uiLogGate", new object()); Set(Vm, "_pendingUiLogs", new Queue<string>());
        var log = typeof(MainViewModel).Assembly.GetType("LinkBridge.App.Services.ProductLog")!;
        Set(Vm, "_productLog", Activator.CreateInstance(log, BindingFlags.Instance | BindingFlags.Public,
            null, [Path.Combine(_directory, "probe.log")], null));
    }

    public static object? Field(object instance, string name) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    public static void Set(object instance, string name, object? value) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    public static object? Invoke(object instance, string method, params object?[] args) => instance.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    public Task Start() => (Task)Invoke(Vm, "StartSharing")!;
    public void Stop() => Invoke(Vm, "StopSharing");
    public async Task<UdpReceiveResult> Request()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            try { return await _responder.ReceiveAsync(timeout.Token); }
            // A deliberately late READY sent to a revoked ephemeral UDP socket
            // can produce an ICMP reset on Windows. It is not a new request.
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { }
        }
    }
    public async Task Ready(UdpReceiveResult request)
    {
        var prefix = "LINKBRIDGE/1 MEDIA_START ";
        var fields = Encoding.UTF8.GetString(request.Buffer)[prefix.Length..].Split(' ');
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.ASCII.GetBytes(_secret),
            Encoding.UTF8.GetBytes("READY|" + fields[0] + "|" + fields[1])));
        var reply = Encoding.UTF8.GetBytes("LINKBRIDGE/1 MEDIA_READY " + fields[0] + " " + fields[1] + " " + signature);
        await _responder.SendAsync(reply, request.RemoteEndPoint);
    }
    public async ValueTask DisposeAsync()
    {
        await Vm.ShutdownAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(9));
        _responder.Dispose();
        Session.Dispose();
        foreach (var id in _children.Distinct())
        {
            try { using var child = Process.GetProcessById(id); if (!child.HasExited)
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (ArgumentException) { }
        }
        // This exact GUID directory was created by this fixture; no shared/user directories.
        if (!Path.GetFullPath(_directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid probe directory.");
        Directory.Delete(_directory, recursive: true);
    }
}
