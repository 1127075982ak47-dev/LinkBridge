using LinkBridge.App.Services;
using System.Diagnostics;

var root = Path.Combine(Path.GetTempPath(), "linkbridge-productlog-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "runtime.log");
var log = new ProductLog(path);
var watch = Stopwatch.StartNew();
Parallel.For(0, 100000, i => log.Append($"flood {i}: " + new string('x', 200)));
var appendMilliseconds = watch.Elapsed.TotalMilliseconds;
if (appendMilliseconds > 5000) throw new Exception("Append unexpectedly blocked.");
for (var batch = 0; batch < 220; batch++)
{
    for (var i = 0; i < 120; i++) log.Append($"rotation {batch}/{i}: " + new string('y', 500));
    await Task.Delay(2);
}
log.Append("LATEST_MARKER");
await log.DisposeAsync();
if (log.Error is not null) throw new Exception(log.Error);
var files = Directory.GetFiles(root, "runtime.log*");
if (files.Length != 2) throw new Exception("Expected active log and exactly one retained backup.");
if (files.Any(f => new FileInfo(f).Length > 1200000)) throw new Exception("Log rotation was not bounded.");
if (!File.ReadAllText(path).Contains("LATEST_MARKER")) throw new Exception("Latest message was lost during shutdown.");
var obstruction = Path.Combine(root, "existing-file");
File.WriteAllText(obstruction, "not a directory");
var failed = new ProductLog(Path.Combine(obstruction, "cannot-write.log"));
failed.Append("failure test"); await failed.DisposeAsync();
if (failed.Error is null) throw new Exception("Filesystem failure was not captured.");
Console.WriteLine($"PASS: 100000 concurrent appends in {appendMilliseconds:F1}ms; active+backup size bounded; shutdown flushed latest; inaccessible storage reported.\nEvidence directory: {root}");
