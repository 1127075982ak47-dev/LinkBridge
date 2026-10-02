using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using LinkBridge.App.ViewModels;
using LinkBridge.Input;
using LinkBridge.Media;

static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Verified Windows probe.");
await HiddenPreview();
await MergedPlayback();
await CancelPlaybackAtShutdown();
MeasureNativeEnumeration();
Console.WriteLine("PASS UI performance regression probe; no GUI, user settings, physical capture or playback.");

static async Task HiddenPreview()
{
    await using var f = new Fixture();
    f.Vm.IsReceiving = true; Fixture.Set(f.Vm, "_receivedFirstFrame", true);
    f.Vm.SetPreviewVisible(false);
    var constructor = typeof(PreviewFrame).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
        null, [typeof(byte[]), typeof(int), typeof(int)], null)!;
    var stopwatch = Stopwatch.StartNew();
    for (var i = 0; i < 10000; i++)
    {
        var frame = (PreviewFrame)constructor.Invoke([ArrayPool<byte>.Shared.Rent(1024), 16, 16]);
        Fixture.Invoke(f.Vm, "OnFrameDecoded", frame);
        try { _ = frame.Data; throw new Exception("Hidden frame retained."); }
        catch (ObjectDisposedException) { }
    }
    Assert(Fixture.Field(f.Vm, "_pendingPreviewFrame") is null && (int)Fixture.Field(f.Vm, "_previewUpdateQueued")! == 0 &&
        Fixture.Field(f.Vm, "_previewBitmapA") is null && Fixture.Field(f.Vm, "_previewBitmapB") is null && f.Vm.IsReceiving,
        "Hidden preview queued UI work, allocated bitmaps or lost receive state.");
    Console.WriteLine($"PASS 10000 hidden frames released; pending/UI-queue/bitmaps=0; receive state retained; elapsed={stopwatch.Elapsed.TotalMilliseconds:F1}ms (reflection/dispose checks included).");
    f.Vm.SetPreviewVisible(true);
    var queued = (PreviewFrame)constructor.Invoke([ArrayPool<byte>.Shared.Rent(1024), 16, 16]);
    Fixture.Invoke(f.Vm, "OnFrameDecoded", queued);
    Assert(Fixture.Field(f.Vm, "_pendingPreviewFrame") is not null, "Visible preview did not accept a new frame.");
    f.Vm.SetPreviewVisible(false);
    Fixture.Invoke(f.Vm, "RenderLatestPreview");
    Assert(Fixture.Field(f.Vm, "_pendingPreviewFrame") is null && Fixture.Field(f.Vm, "_previewBitmapA") is null,
        "Visibility change allowed an already queued render to allocate a bitmap.");
    Console.WriteLine("PASS visible frame accepted; hiding before queued render releases it and skips Bitmap/Marshal.Copy.");
}

static async Task MergedPlayback()
{
    await using var f = new Fixture();
    Fixture.Set(f.Vm, "<PlaybackDevices>k__BackingField", new ObservableCollection<PlaybackDeviceOption>());
    using var context = new PumpContext();
    var stopwatch = Stopwatch.StartNew();
    Fixture.Invoke(f.Vm, "UpdatePlaybackDevices");
    var first = (Task)Fixture.Field(f.Vm, "_playbackRefreshTask")!;
    for (var i = 0; i < 1000; i++)
    {
        Fixture.Invoke(f.Vm, "UpdatePlaybackDevices");
        Assert(ReferenceEquals(first, Fixture.Field(f.Vm, "_playbackRefreshTask")), "Overlapping refresh task created.");
    }
    var callMs = stopwatch.Elapsed.TotalMilliseconds;
    // Change only the in-memory requested id while the old snapshot is awaiting
    // application; no SelectedPlaybackDevice setter, SettingsStore.Save or playback.
    f.Vm.Settings.PlaybackDeviceId = "__LinkBridge_Offline_Output_Probe__";
    Fixture.Invoke(f.Vm, "UpdatePlaybackDevices");
    context.Complete(first);
    Assert(f.Vm.SelectedPlaybackDevice?.Id == f.Vm.Settings.PlaybackDeviceId &&
        f.Vm.SelectedPlaybackDevice.IsAvailable == false && f.Vm.PlaybackDevices.Any(p => p.Id.Length == 0),
        "Requested offline choice was lost or old snapshot applied.");
    var sameChoice = f.Vm.SelectedPlaybackDevice;
    var collectionChanges = 0;
    f.Vm.PlaybackDevices.CollectionChanged += (_, _) => collectionChanges++;
    Fixture.Invoke(f.Vm, "UpdatePlaybackDevices");
    context.Complete((Task)Fixture.Field(f.Vm, "_playbackRefreshTask")!);
    Assert(ReferenceEquals(sameChoice, f.Vm.SelectedPlaybackDevice) && collectionChanges == 0,
        "Unchanged playback refresh recreated options/selection.");
    Console.WriteLine($"PASS 1000 overlapping refresh requests merged into one task; scheduling={callMs:F1}ms; latest offline choice retained; stable poll collection changes=0.");
}

static async Task CancelPlaybackAtShutdown()
{
    await using var f = new Fixture();
    Fixture.Set(f.Vm, "<PlaybackDevices>k__BackingField", new ObservableCollection<PlaybackDeviceOption>());
    using var context = new PumpContext();
    Fixture.Invoke(f.Vm, "UpdatePlaybackDevices");
    var changedAfterClose = 0;
    var closing = true;
    f.Vm.PlaybackDevices.CollectionChanged += (_, _) => { if (closing) changedAfterClose++; };
    var shutdown = f.Vm.ShutdownAsync().AsTask();
    context.Complete(shutdown);
    Assert(changedAfterClose == 0 && f.Vm.PlaybackDevices.Count == 0, "Background refresh applied collections after shutdown.");
    closing = false;
    Console.WriteLine("PASS shutdown cancels in-flight native playback snapshot; no collection apply to closed UI.");
}

static void MeasureNativeEnumeration()
{
    var stopwatch = Stopwatch.StartNew();
    var inputs = InputDeviceCatalog.Enumerate();
    var inputMs = stopwatch.Elapsed.TotalMilliseconds;
    stopwatch.Restart();
    var disks = (IEnumerable)typeof(MainViewModel).GetMethod("EnumerateDisks", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
    var count = 0; foreach (var item in disks) count++;
    var diskMs = stopwatch.Elapsed.TotalMilliseconds;
    stopwatch.Restart();
    var outputs = AudioEndpointCatalog.Enumerate();
    Console.WriteLine($"Read-only native enumeration sample: input/WMI={inputMs:F1}ms ({inputs.Count} devices), disk/WMI={diskMs:F1}ms ({count} external), CoreAudio={stopwatch.Elapsed.TotalMilliseconds:F1}ms ({outputs.Count} outputs). No device names/IDs logged.");
}

sealed class PumpContext : SynchronizationContext, IDisposable
{
    private readonly SynchronizationContext? _previous = Current;
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
    public PumpContext() => SetSynchronizationContext(this);
    public override void Post(SendOrPostCallback d, object? state) => _pending.Enqueue((d, state));
    public void Complete(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Probe UI context timed out.");
            if (_pending.TryDequeue(out var work)) work.Callback(work.State); else Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }
    public void Dispose() => SetSynchronizationContext(_previous);
}
