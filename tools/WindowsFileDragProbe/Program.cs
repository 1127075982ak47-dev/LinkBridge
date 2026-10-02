using LinkBridge.App.Services;

if (!OperatingSystem.IsWindows()) return 2;
var seconds = args.Length == 0 ? 8 : int.Parse(args[0]);
using var edge = new WindowsFileDragEdge(paths =>
{
    Console.WriteLine($"NATIVE_CF_HDROP_CANCELLED count={paths.Count}");
    foreach (var path in paths) Console.WriteLine(path);
}, message => Console.Error.WriteLine(message));
edge.SetEnabled(true);
Console.WriteLine("NATIVE_OLE_EDGE_READY. Drag a disposable Explorer file into the primary screen's rightmost 10 px; source is preserved.");
Thread.Sleep(TimeSpan.FromSeconds(seconds));
edge.SetEnabled(false);
Console.WriteLine("NATIVE_OLE_EDGE_STOPPED");
return 0;
