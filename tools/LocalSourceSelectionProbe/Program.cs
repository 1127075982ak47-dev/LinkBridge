using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LinkBridge.App.ViewModels;
using LinkBridge.Core.Models;

// Constructor bypass deliberately avoids App initialization, settings files,
// discovery listeners, camera and microphone capture. Call the real restore method.
var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
var collection = new ObservableCollection<DeviceTile>();
typeof(MainViewModel).GetField("<Devices>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, collection);
var cameraA = new MediaDevice(MediaDeviceKind.Camera, "0", "Camera A", MediaBackend.AvFoundation);
var cameraB = new MediaDevice(MediaDeviceKind.Camera, "1", "Camera B", MediaBackend.AvFoundation);
var micA = new MediaDevice(MediaDeviceKind.Microphone, "Mic A", "Mic A", MediaBackend.DirectShow);
DeviceTile Tile(TileKind kind, string name, MediaDevice media)
{
    var tile = (DeviceTile)RuntimeHelpers.GetUninitializedObject(typeof(DeviceTile));
    foreach (var item in new (string Name, object Value)[] { ("Kind", kind), ("Name", name), ("Media", media) })
        typeof(DeviceTile).GetField("<" + item.Name + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tile, item.Value);
    return tile;
}
void Reset()
{
    collection.Clear();
    collection.Add(Tile(TileKind.Camera, "Camera A", cameraA));
    collection.Add(Tile(TileKind.Camera, "Camera B", cameraB));
    collection.Add(Tile(TileKind.Camera, "LinkBridge App", cameraA with { Id = "app", IsApplicationEndpoint = true }));
    collection.Add(Tile(TileKind.Microphone, "Mic A", micA));
}
void Restore(TileKind kind, bool enabled, string key) => typeof(MainViewModel).GetMethod("RestoreLocalSourceSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [kind, enabled, key]);
void Assert(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
string Key(MediaDevice d) => JsonSerializer.Serialize(new[] { d.Backend.ToString(), d.Id, d.Name });
Reset(); Restore(TileKind.Camera, true, "");
Assert(collection.Count(d => d.IsSelected) == 1 && collection[0].IsSelected, "first setup selects one actual camera, never every camera or app endpoint");
Reset(); Restore(TileKind.Camera, true, Key(cameraB));
Assert(collection.Count(d => d.IsSelected) == 1 && collection[1].IsSelected, "refresh restores the exact saved camera");
Reset(); Restore(TileKind.Camera, false, Key(cameraA));
Assert(!collection.Any(d => d.IsSelected), "disabled source remains disabled after refresh");
Reset(); Restore(TileKind.Microphone, true, Key(micA));
Assert(collection.Count(d => d.IsSelected) == 1 && collection[3].IsSelected, "microphone restores independently of camera choice");
// Missing-choice path also writes a diagnostic; inject only a bounded isolated log.
typeof(MainViewModel).GetField("_uiLogGate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, new object());
typeof(MainViewModel).GetField("_pendingUiLogs", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, new Queue<string>());
typeof(MainViewModel).GetField("_shuttingDown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
var logType = typeof(MainViewModel).Assembly.GetType("LinkBridge.App.Services.ProductLog")!;
var dir = Path.Combine(Path.GetTempPath(), "linkbridge-source-choice-" + Guid.NewGuid().ToString("N"));
var log = Activator.CreateInstance(logType, [Path.Combine(dir, "probe.log")])!;
typeof(MainViewModel).GetField("_productLog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, log);
try
{
    Reset(); Restore(TileKind.Camera, true, Key(cameraB with { Id = "2" }));
    Assert(!collection.Any(d => d.IsSelected), "a moved AVFoundation index cannot silently select another device");
    Reset(); Restore(TileKind.Camera, true, Key(cameraB with { Name = "Different Camera" }));
    Assert(!collection.Any(d => d.IsSelected), "changed identity remains unavailable instead of falling back");
}
finally { await ((IAsyncDisposable)log).DisposeAsync(); }
Console.WriteLine("No user settings were read/written, no native source or listener started. Test log: " + dir);
