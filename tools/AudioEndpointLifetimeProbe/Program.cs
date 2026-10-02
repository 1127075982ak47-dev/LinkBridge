using System.Diagnostics;
using LinkBridge.Media;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using LinkBridge.Media.Audio;

if (OperatingSystem.IsMacOS())
{
    var baseline = AudioEndpointCatalog.Enumerate();
    if (AudioEndpointCatalog.LastError is not null || baseline.Count == 0)
        throw new Exception("Mac physical output inventory unavailable.");
    var expected = baseline.Select(d => (d.Id, d.Name, d.IsDefaultPlayback)).OrderBy(d => d.Id).ToArray();
    var macWatch = Stopwatch.StartNew();
    for (var i = 0; i < 200; i++)
    {
        var actual = AudioEndpointCatalog.Enumerate();
        if (AudioEndpointCatalog.LastError is not null || !expected.SequenceEqual(actual.Select(d => (d.Id, d.Name, d.IsDefaultPlayback)).OrderBy(d => d.Id)))
            throw new Exception("Mac output inventory changed or CFString conversion failed.");
        foreach (var endpoint in actual) _ = PlaybackVolumeController.Read(endpoint.Id);
    }
    Console.WriteLine($"PASS 200 Mac native readonly polls; stable ID/name/default for {baseline.Count} outputs; elapsedMs={macWatch.Elapsed.TotalMilliseconds:F1}; no capture/playback/default/volume writes.");
    return;
}
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
static int Handles() { using var process = Process.GetCurrentProcess(); return process.HandleCount; }
static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
static void LegacyRead()
{
    using var enumerator = new MMDeviceEnumerator();
    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        using (device) { _ = device.ID; _ = device.FriendlyName; }
    using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    _ = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
}
// No playback/capture/mute/default changes. Suppress GC only in this isolated probe to
// distinguish deferred COM ownership from a permanent leak; never in production.
// Production order: native query MUST be the first CoreAudio object in a fresh process.
var first = await Task.Run(()=> (Inventory:AudioEndpointCatalog.Enumerate(), Volume:PlaybackVolumeController.Read("")));
var inventory = first.Inventory; var actualVolume = first.Volume;
Console.WriteLine($"Current inventory count={inventory.Count}, error={AudioEndpointCatalog.LastError ?? "none"}, volumeAvailable={actualVolume.HasValue}");
using (var enumerator = new MMDeviceEnumerator())
{
    using var defaultEndpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var baseline = new List<(string Id, string Name)>();
    foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        using (endpoint) baseline.Add((endpoint.ID, endpoint.FriendlyName));
    if (!baseline.OrderBy(d=>d.Id).SequenceEqual(inventory.Select(d=>(d.Id,d.Name)).OrderBy(d=>d.Id))) throw new Exception("Native inventory mismatch.");
    if (inventory.Single(d=>d.IsDefaultPlayback).Id != defaultEndpoint.ID) throw new Exception("Default playback identity mismatch.");
    var scalar = defaultEndpoint.AudioEndpointVolume.MasterVolumeLevelScalar * 100d;
    if (!actualVolume.HasValue || Math.Abs(actualVolume.Value - scalar) > 0.001 ||
        Math.Abs((PlaybackVolumeController.Read(defaultEndpoint.ID) ?? -1) - scalar) > 0.001) throw new Exception("Actual volume mismatch.");
    if (PlaybackVolumeController.Read("LinkBridge.Invalid.Test.Endpoint") is not null) throw new Exception("Invalid endpoint falsely falls back to default.");
}
Console.WriteLine("PASS complete endpoint identity/name/default and actual volume match NAudio; invalid ID returns unknown"); Collect();
var microphone = SharedMicrophoneEndpoints.Diagnose();
if (microphone.Diagnostic.Contains("Unable to cast",StringComparison.OrdinalIgnoreCase)) throw new Exception("Native-first query polluted microphone metadata COM identity.");
using (var enumerator = new MMDeviceEnumerator())
{
    using var defaultEndpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
    using var selectedEndpoint = enumerator.GetDevice(inventory.Single(d=>d.IsDefaultPlayback).Id);
    foreach (var endpoint in new[]{defaultEndpoint,selectedEndpoint})
    {
        using var output = new WasapiOut(endpoint,AudioClientShareMode.Shared,false,80);
        output.Init(new BufferedWaveProvider(new WaveFormat(48000,16,2)) { ReadFully=true });
        output.Play(); Thread.Sleep(40); output.Stop(); // zeros only; no nonzero PCM or default changes.
    }
}
Console.WriteLine("PASS native-first then NAudio default/selected zero-only WASAPI output and microphone metadata; no RCW coclass pollution"); Collect();
var noGc = GC.TryStartNoGCRegion(64 * 1024 * 1024);
var before = Handles(); for (var i = 0; i < 80; i++) LegacyRead(); var legacyDelta = Handles() - before;
if (noGc) GC.EndNoGCRegion(); Collect();
Console.WriteLine($"LEGACY 80 readonly polls handleDeltaNoGC={legacyDelta}; afterGC={Handles()-before}");
noGc = GC.TryStartNoGCRegion(64 * 1024 * 1024);
before = Handles(); var watch = Stopwatch.StartNew();
for (var i = 0; i < 80; i++) { _ = AudioEndpointCatalog.Enumerate(); _ = PlaybackVolumeController.Read(""); }
var currentDelta = Handles() - before; watch.Stop();
if (noGc) GC.EndNoGCRegion(); Collect();
Console.WriteLine($"CURRENT 80 readonly polls handleDeltaNoGC={currentDelta}; elapsedMs={watch.Elapsed.TotalMilliseconds:F1}; afterGC={Handles()-before}");
if (args.Contains("--require-fixed") && (currentDelta > 16 || currentDelta >= legacyDelta / 2)) throw new Exception("Periodic audio queries still defer native handle release.");
await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Task.Run(()=> { if(AudioEndpointCatalog.Enumerate().Count != inventory.Count || !PlaybackVolumeController.Read("").HasValue) throw new Exception("Concurrent native query failed."); LegacyRead(); })));
Console.WriteLine("PASS 12 concurrent mixed raw-COM and NAudio catalog/volume reads without identity pollution");
Console.WriteLine("PASS bounded isolated CoreAudio lifecycle probe; no GUI/settings/network/capture/nonzeroPCM/default/volume changes.");
