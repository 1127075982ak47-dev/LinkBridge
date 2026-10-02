using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LinkBridge.App.Services;

var timing = new MacCameraTiming(TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(45),
    TimeSpan.FromMilliseconds(130), TimeSpan.FromMilliseconds(45), TimeSpan.FromMilliseconds(45), TimeSpan.FromSeconds(2));
const int port = 49083;
void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
async Task Until(Func<bool> condition, string reason, int limit = 1800)
{
    var deadline = Environment.TickCount64 + limit;
    while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(4);
    Check(condition(), reason);
}
async Task<(MacSharedCameraCoordinator Service, Control Control, Factory Factory, Host Bridge)> Begin(bool blockedWrite = false)
{
    var control = new Control(); var factory = new Factory { Available = true, BlockBridgeWrites = blockedWrite };
    var service = new MacSharedCameraCoordinator(control, factory, port, default, timing); service.Start();
    await Until(() => factory.Observers.Count > 0, "watcher start");
    factory.Observers.Last().Demand("start", 1);
    await Until(() => factory.Bridges.Count > 0, "demand bridge");
    var bridge = factory.Bridges.Last(); bridge.Demand("start", 1);
    await Until(() => control.Calls.Count > 0, "signed prepare");
    return (service, control, factory, bridge);
}

var unavailableFactory = new Factory(); var unavailable = new MacSharedCameraCoordinator(new Control(), unavailableFactory, port, default, timing);
unavailable.Start(); await Until(() => unavailableFactory.Probes >= 2, "unavailable retries");
Check(unavailable.State == MacSharedCameraState.Unavailable && unavailableFactory.Bridges.Count == 0 && unavailableFactory.Observers.Count == 0, "must not launch uninstalled helper");
unavailableFactory.Available = true; await Until(() => unavailableFactory.Observers.Count > 0, "installation automatically recovered");
await unavailable.DisposeAsync(); Console.WriteLine("PASS unavailable recheck / future installation recovery");

var healthy = await Begin();
healthy.Control.Calls.Last().Ready.TrySetResult(true);
await Until(() => healthy.Bridge.HasWrite("pairedReady"), "authenticated ready");
Check(healthy.Service.State == MacSharedCameraState.WaitingForFrames, "READY alone must not claim actual camera frames");
healthy.Bridge.Demand("heartbeat", 1, fresh: true);
await Until(() => healthy.Service.State == MacSharedCameraState.Streaming, "real helper frame truth");
await Until(() => healthy.Control.Calls.Count >= 2, "renew must not stall");
var late = healthy.Control.Calls.Last();
healthy.Bridge.Demand("stop", 0);
await Until(() => healthy.Bridge.Stopped && healthy.Factory.Observers.Count >= 2, "zero demand returns observer and kills decoder");
var writesAfterStop = healthy.Bridge.Writes.Count;
late.Ready.TrySetResult(true); await Task.Delay(70);
Check(healthy.Bridge.Writes.Count == writesAfterStop && healthy.Control.Stops.Count > 0, "old ACK revived ended helper");
await healthy.Service.DisposeAsync(); Console.WriteLine("PASS consumer demand / READY versus actual frames / renewal / STOP / late ACK");

var switching = await Begin(); var a = switching.Control.Calls.Last();
switching.Control.Peer = "192.0.2.22"; switching.Service.SetPreferredWindowsAddress("192.0.2.22");
Check(switching.Bridge.Stopped, "source switch must kill synchronously");
a.Ready.TrySetResult(true);
await Until(() => switching.Factory.Observers.Count >= 2, "switch observer");
switching.Factory.Observers.Last().Demand("start", 1);
await Until(() => switching.Factory.Bridges.Count >= 2, "new peer bridge");
var newBridge = switching.Factory.Bridges.Last(); newBridge.Demand("start", 1);
await Until(() => switching.Control.Calls.Any(c => c.Address == "192.0.2.22"), "new peer prepare");
switching.Control.Calls.Last().Ready.TrySetResult(true);
await Until(() => newBridge.HasWrite("pairedReady"), "new peer ACK");
Check(!switching.Bridge.HasWrite("pairedReady"), "old peer ACK accepted");
var prior = newBridge.Generation; newBridge.Generation = Guid.NewGuid().ToString(); newBridge.Demand("start", 1);
await Until(() => switching.Control.Calls.Count >= 3, "new helper epoch");
newBridge.Demand("stop", 0, generation: prior); await Task.Delay(35);
Check(!newBridge.Stopped && switching.Service.ConsumerCount == 1, "old native STOP stopped newer epoch");
await switching.Service.DisposeAsync(); Console.WriteLine("PASS peer switch / current generation / stale helper STOP rejection");

var hung = await Begin();
await Until(() => hung.Control.Calls.Count >= 6, "hung ACK must expire and renewal continue");
Check(hung.Control.Peak <= 3, $"request pressure exceeds cap: {hung.Control.Peak}");
var dispose = hung.Service.DisposeAsync(); Check(hung.Bridge.Stopped, "Dispose sync prefix must kill helper");
await dispose; foreach (var c in hung.Control.Calls) c.Ready.TrySetResult(true);
await Task.Delay(40); Check(!hung.Bridge.HasWrite("pairedReady"), "post-dispose ACK accepted");
Console.WriteLine("PASS hung control bounded max 3 / timed cancellation / synchronous disposal kill");

var blocked = await Begin(true); blocked.Control.Calls.Last().Ready.TrySetResult(true);
await Until(() => blocked.Bridge.Stopped && blocked.Control.Stops.Count > 0, "blocked writer closed-loop cleanup");
await blocked.Service.DisposeAsync(); Console.WriteLine("PASS blocked helper stdin deadline / signed STOP / owned process kill");

var invalid = await Begin(); invalid.Bridge.Emit(new string('x', 4097));
await Until(() => invalid.Bridge.Stopped && invalid.Control.Stops.Count > 0, "oversized stdout teardown");
await invalid.Service.DisposeAsync(); Console.WriteLine("PASS bounded helper stdout malformed input cleanup");
var eof = await Begin(); eof.Bridge.Eof();
await Until(() => eof.Bridge.Stopped && eof.Control.Stops.Count > 0, "helper EOF teardown");
await eof.Service.DisposeAsync(); Console.WriteLine("PASS helper EOF closes paired source");

using (var read = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 4097) + "\n"))))
{
    try { _ = await MacCameraProcessHost.ReadBoundedAsync(read, default); throw new Exception("bounded native reader accepted oversized line"); }
    catch (InvalidDataException) { }
}
if (!OperatingSystem.IsMacOS()) Check(!(await new InstalledMacCameraHostFactory().ProbeAsync(default)).Available, "Windows production factory cannot launch Mac host");
Console.WriteLine("PASS actual native stdout reader limit / platform installation gate");
Console.WriteLine("ALL PASS: actual MacSharedCameraCoordinator actor; no GUI/settings/hardware/network ports used");

sealed record Request(string Address, TaskCompletionSource<bool> Ready);
sealed class Control : IMacCameraControl
{
    public volatile string? Peer = "192.0.2.11";
    public ConcurrentQueue<Request> Calls { get; } = new();
    public ConcurrentQueue<string> Stops { get; } = new();
    private int _active; public int Peak;
    public string? FindWindowsAddress(string? preferred) => preferred is null || preferred == Peer ? Peer : null;
    public async Task<bool> PrepareAsync(string address, int port, CancellationToken cancellation)
    {
        var active = Interlocked.Increment(ref _active); int peak;
        while (active > (peak = Volatile.Read(ref Peak)) && Interlocked.CompareExchange(ref Peak, active, peak) != peak) { }
        var request = new Request(address, new(TaskCreationOptions.RunContinuationsAsynchronously)); Calls.Enqueue(request);
        try { return await request.Ready.Task.WaitAsync(cancellation); } finally { Interlocked.Decrement(ref _active); }
    }
    public Task StopAsync(string address, CancellationToken cancellation) { Stops.Enqueue(address); return Task.CompletedTask; }
}
sealed class Factory : IMacCameraHostFactory
{
    public volatile bool Available; public bool BlockBridgeWrites; public int Probes;
    public ConcurrentQueue<Host> Observers { get; } = new(); public ConcurrentQueue<Host> Bridges { get; } = new();
    public Task<MacCameraAvailability> ProbeAsync(CancellationToken cancellation) { Interlocked.Increment(ref Probes); return Task.FromResult(new MacCameraAvailability(Available, "missing legal deployment")); }
    public Task<IMacCameraHost> StartObserverAsync(CancellationToken cancellation) { var h = new Host(0); Observers.Enqueue(h); return Task.FromResult<IMacCameraHost>(h); }
    public Task<IMacCameraHost> StartBridgeAsync(string peer, int port, CancellationToken cancellation) { var h = new Host(port) { BlockWrites = BlockBridgeWrites }; Bridges.Enqueue(h); return Task.FromResult<IMacCameraHost>(h); }
}
sealed class Host(int port) : IMacCameraHost
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    public string Generation = Guid.NewGuid().ToString(); public volatile bool Stopped; public bool BlockWrites;
    public ConcurrentQueue<string> Writes { get; } = new();
    public void Demand(string kind, int count, bool fresh = false, string? generation = null) => Emit(JsonSerializer.Serialize(new { v = 1, @event = kind, generation = generation ?? Generation, consumers = count, width = 3840, height = 2160, fps = 30, port, freshFrames = fresh }));
    public void Emit(string line) => _lines.Writer.TryWrite(line);
    public void Eof() => _lines.Writer.TryComplete();
    public bool HasWrite(string kind) => Writes.Any(line => JsonDocument.Parse(line).RootElement.GetProperty("event").GetString() == kind);
    public async Task<string?> ReadLineAsync(CancellationToken cancellation) { try { return await _lines.Reader.ReadAsync(cancellation); } catch (ChannelClosedException) { return null; } }
    public async Task WriteLineAsync(string line, CancellationToken cancellation)
    {
        if (Stopped) throw new IOException("closed fake process");
        // Deliberately ignore cancellation: coordinator must independently bound blocked pipes.
        if (BlockWrites) await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        else Writes.Enqueue(line);
    }
    public void StopSync() { Stopped = true; _lines.Writer.TryComplete(); }
    public ValueTask DisposeAsync() { StopSync(); return ValueTask.CompletedTask; }
}
