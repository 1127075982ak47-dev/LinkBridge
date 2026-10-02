using System.Text.Json;
using LinkBridge.Core;
using LinkBridge.Core.FileTransfer;
using LinkBridge.Input;

if (args.Length != 4) throw new ArgumentException("IP source-folder Mac-X Mac-Y required; only use a dedicated observed Finder test folder.");
using var doc = JsonDocument.Parse(File.ReadAllText(SettingsStore.FilePath));
var secret = doc.RootElement.GetProperty("inputPairingSecret").GetString()!;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
await using var input = await InputBridgeClient.ConnectAsync(args[0], secret, deadline.Token);
input.TrySend(new InputPacket(InputEventType.Move,0,0,-100000,-100000));
input.TrySend(new InputPacket(InputEventType.Move,0,0,64,620));
input.TrySend(new InputPacket(InputEventType.FileDrag,1,0,0,0));
var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var progress = new InlineProgress(value =>
{
    if (value.Phase == "等待放入目标文件夹") ready.TrySetResult();
    Console.WriteLine($"{value.Phase} {value.Bytes}/{value.TotalBytes}");
});
var sender = new FileTransferSender();
var transfer = sender.SendAsync(args[0],secret,Directory.GetFileSystemEntries(Path.GetFullPath(args[1])),progress,deadline.Token);
try
{
    await Task.WhenAny(ready.Task,transfer).WaitAsync(deadline.Token);
    if (!ready.Task.IsCompletedSuccessfully) await transfer;
    // Native AppKit has started and data is verified. Move its real promised-file drag into the observed Finder body.
    input.TrySend(new InputPacket(InputEventType.Move,0,0,int.Parse(args[2])-64,int.Parse(args[3])-620));
    await Task.Delay(800,deadline.Token);
    input.TrySend(new InputPacket(InputEventType.Button,0,0,0,0));
    var receipt = await transfer;
    Console.WriteLine(JsonSerializer.Serialize(receipt));
}
finally
{
    input.TrySend(new InputPacket(InputEventType.Key,0,0x1B,0,0));
    input.TrySend(new InputPacket(InputEventType.Key,1,0x1B,0,0));
    input.TrySend(new InputPacket(InputEventType.FileDrag,0,0,0,0));
}
sealed class InlineProgress(Action<FileTransferProgress> action) : IProgress<FileTransferProgress>
{ public void Report(FileTransferProgress value) => action(value); }
