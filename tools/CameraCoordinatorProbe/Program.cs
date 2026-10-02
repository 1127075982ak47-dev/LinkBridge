using System.Collections.Concurrent;
using LinkBridge.App.Services;
using LinkBridge.Core.Discovery;
using LinkBridge.Core.Models;

var demand = 1; string? binding = null; Action? pulse = null;
var peer = new PeerInfo("fake Mac", "127.0.0.1", "Mac", 49000, DateTimeOffset.Now)
{ Cameras = [new(MediaDeviceKind.Camera, "0", "synthetic source", MediaBackend.AvFoundation)] };
var requests = new ConcurrentQueue<long>(); var stops = 0;
var oldReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
await using (var actor = new SharedCameraCoordinator(() => demand, () => [peer],
    (address, token) => { requests.Enqueue(Environment.TickCount64); return oldReady.Task; },
    (address, token) => { Interlocked.Increment(ref stops); return Task.FromResult(true); },
    address => binding = address, handler => pulse = handler, handler => pulse = null,
    CancellationToken.None, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(90), TimeSpan.FromMilliseconds(10)))
{
    actor.Start(); await Task.Delay(240);
    if (requests.Count < 5) throw new Exception("A hung READY blocked fresh lease renewals.");
    if (binding is not null) throw new Exception("Unconfirmed source was bound.");
    demand = 0; pulse?.Invoke(); await Task.Delay(35);
    oldReady.SetResult(true); await Task.Delay(40);
    if (binding is not null || stops == 0) throw new Exception("Late READY revived a stopped camera demand.");
}
if (pulse is not null) throw new Exception("Demand subscription survived disposal.");
Console.WriteLine($"PASS: {requests.Count} fresh requests despite hung READY; last reader STOP; late READY ignored; callback removed.");

var count = 1; string? live = null; var completedStops = 0; Action? notify = null;
var healthy = new SharedCameraCoordinator(() => count, () => [peer],
    (_, _) => Task.FromResult(true), (_, _) => { completedStops++; return Task.FromResult(true); },
    address => live = address, handler => notify = handler, _ => notify = null, CancellationToken.None,
    TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(90), TimeSpan.FromMilliseconds(10));
healthy.Start(); await Task.Delay(50);
if (live != peer.Address) throw new Exception("Confirmed live source did not bind.");
count = 0; notify?.Invoke(); await Task.Delay(30);
if (live is not null || completedStops != 1) throw new Exception("Last reader did not release source exactly once.");
await healthy.DisposeAsync();
Console.WriteLine("PASS: verified source bind, zero readers release, bounded disposal without input hooks.");
