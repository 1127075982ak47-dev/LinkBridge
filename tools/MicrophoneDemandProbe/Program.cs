using LinkBridge.Media;
using System.Text.Json;

if (args.Length == 0)
{
    Console.WriteLine("Usage: MicrophoneDemandProbe <virtual Capture endpoint ID | CoreAudio UID> [seconds]");
    Console.WriteLine("       MicrophoneDemandProbe --missing");
    Console.WriteLine("Read-only metadata. Does not open streams, install devices, or change defaults.");
    return;
}
var missing = args[0] == "--missing";
var id = missing ? "__linkbridge_missing_virtual_backend__" : args[0];
var duration = args.Length > 1 && int.TryParse(args[1], out var parsed) ? Math.Clamp(parsed, 1, 60) : 4;
var first = new TaskCompletionSource<MicrophoneDemandSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
var ownerThread = Environment.CurrentManagedThreadId;
using var monitor = new MicrophoneDemandMonitor(snapshot =>
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = snapshot.Status.ToString(), count = snapshot.ActiveConsumers,
        pids = snapshot.ProcessIds, snapshot.Detail,
        workerThread = Environment.CurrentManagedThreadId, initialThread = ownerThread
    }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    first.TrySetResult(snapshot);
});
monitor.Start(id);
var observation = await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
if (missing && (observation.Status != MicrophoneDemandStatus.MissingEndpoint || observation.ActiveConsumers is not null))
    throw new InvalidOperationException("A missing virtual endpoint must be explicit MissingEndpoint with null demand.");
await Task.Delay(TimeSpan.FromSeconds(duration));
monitor.Stop();
if (monitor.LastObservation.Status != MicrophoneDemandStatus.Stopped)
    throw new InvalidOperationException("Stop did not terminate monitoring.");
monitor.Dispose();
Console.WriteLine("STOPPED; no stream/default-device changes were made.");
