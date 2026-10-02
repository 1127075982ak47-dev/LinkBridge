using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LinkBridge.Core.FileTransfer;
using LinkBridge.Core.Security;

var scratch = Path.Combine(Path.GetTempPath(), "LinkBridge-FileTransferProbe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
var results = new List<object>();
try
{
    await Check("binary_directory_empty_unicode_duplicate_roots_and_receipt", RoundTrip);
    await Check("success_waits_for_destination_and_second_transfer_rejected", CommitGate);
    await Check("wrong_psk_and_context_rejected", WrongAuthentication);
    await Check("unpaired_ip_rejected", WrongAddress);
    await Check("remote_traversal_parent_conflicts_and_size_limits_rejected", InvalidManifests);
    await Check("tampered_hash_never_commits_and_partial_staging_removed", TamperedHash);
    await Check("client_disconnect_mid_upload_removes_partial_staging", CancelMidUpload);
    await Check("client_disconnect_cancels_native_commit", CancelDuringCommit);
    await Check("changed_source_before_transfer_rejected", ChangedSource);
    await Check("symbolic_links_not_traversed", SymbolicLink);
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    // This exact, newly generated scratch root is the only recursive deletion target.
    if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(scratch, true);
}

async Task Check(string name, Func<Task> action)
{
    var started = DateTime.UtcNow;
    await action();
    results.Add(new { Test = name, Passed = true, Milliseconds = (DateTime.UtcNow - started).TotalMilliseconds });
}
static void Require(bool condition, string description)
{ if (!condition) throw new InvalidOperationException(description); }
static async Task ExpectFailure(Func<Task> action)
{
    var failed = false;
    try { await action(); }
    catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or SocketException or OperationCanceledException or InvalidDataException)
    { failed = true; }
    Require(failed, "The unsafe operation unexpectedly succeeded.");
}
string Work(string name) { var path = Path.Combine(scratch, name); Directory.CreateDirectory(path); return path; }

async Task RoundTrip()
{
    var source = Work("source");
    var directory = Path.Combine(source, "文件夹 café");
    Directory.CreateDirectory(Path.Combine(directory, "empty"));
    Directory.CreateDirectory(Path.Combine(directory, "nested"));
    var payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
    await File.WriteAllBytesAsync(Path.Combine(directory, "nested", "binary.bin"), payload);
    await File.WriteAllBytesAsync(Path.Combine(directory, "empty.dat"), []);
    await File.WriteAllTextAsync(Path.Combine(directory, "nested", "你好.txt"), "Windows → Mac；原文件保留。");
    var duplicateA = Path.Combine(Work("duplicate-a"), "same.txt");
    var duplicateB = Path.Combine(Work("duplicate-b"), "same.txt");
    await File.WriteAllTextAsync(duplicateA, "A"); await File.WriteAllTextAsync(duplicateB, "B");
    var destination = Work("destination");
    FileTransferManifest? accepted = null; string? staging = null;
    var progress = new List<FileTransferProgress>();
    FileTransferServer? server = null;
    await using (server = new FileTransferServer("127.0.0.1", secret, Work("roundtrip-cache"), (manifest, path, ct) =>
    { accepted = manifest; staging = path; return Task.CompletedTask; }, (id, path, ct) =>
    {
        Require(accepted is not null && path == staging, "Callback state mismatch.");
        foreach (var entry in accepted!.Entries)
        {
            var target = FileTransferPaths.ResolveWithin(destination, entry.RelativePath);
            if (entry.IsDirectory) Directory.CreateDirectory(target);
            else File.Copy(FileTransferPaths.ResolveWithin(path, entry.RelativePath), target, false);
        }
        server!.ReportCommittedDestinations(id, accepted.Roots.Select(root => Path.Combine(destination, root.Name)).ToArray());
        return Task.CompletedTask;
    }, port: 0))
    {
        server.Start();
        var manifest = FileTransferSender.CreateManifest([directory, duplicateA, duplicateB]);
        var receipt = await new FileTransferSender().SendAsync("127.0.0.1", secret, manifest,
            [directory, duplicateA, duplicateB], new InlineProgress(value => progress.Add(value)), port: server.Port);
        Require(receipt.Id == manifest.Id && receipt.Bytes == manifest.TotalBytes && receipt.Files == manifest.FileCount && receipt.Destinations.Count == 3,
            "Receipt must contain actual committed roots and byte/file counts.");
        foreach (var entry in manifest.Entries)
        {
            var target = FileTransferPaths.ResolveWithin(destination, entry.RelativePath);
            if (entry.IsDirectory) Require(Directory.Exists(target), "Empty directory missing.");
            else Require(File.Exists(target), "File missing.");
        }
        Require(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(destination, "文件夹 café", "nested", "binary.bin")))
            .SequenceEqual(SHA256.HashData(payload)), "Binary corruption.");
        Require(new FileInfo(Path.Combine(destination, "文件夹 café", "empty.dat")).Length == 0, "Empty file corruption.");
        Require(await File.ReadAllTextAsync(Path.Combine(destination, "same.txt")) == "A" &&
            await File.ReadAllTextAsync(Path.Combine(destination, "same (2).txt")) == "B", "Root collision lost a file.");
        Require(File.Exists(duplicateA) && File.Exists(duplicateB) && File.Exists(Path.Combine(directory, "nested", "binary.bin")), "Source was modified.");
        Require(progress.Last().Phase == "已保存" && progress.Last().Bytes == manifest.TotalBytes, "Progress must follow real destination receipt.");
    }
}

async Task CommitGate()
{
    var file = Path.Combine(Work("commit-source"), "file.txt"); await File.WriteAllTextAsync(file, "commit-gate");
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var allow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var server = new FileTransferServer("127.0.0.1", secret, Work("commit-cache"), (_, _, _) => Task.CompletedTask,
        async (_, _, ct) => { ready.TrySetResult(); await allow.Task.WaitAsync(ct); }, port: 0);
    server.Start();
    var sending = new FileTransferSender().SendAsync("127.0.0.1", secret, [file], port: server.Port);
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Require(!sending.IsCompleted, "Upload must not report success before native destination writes.");
    await ExpectFailure(async () => await new FileTransferSender().SendAsync("127.0.0.1", secret, [file], port: server.Port));
    allow.TrySetResult(); var receipt = await sending;
    Require(receipt.Files == 1, "First upload did not complete after commit.");
}

async Task WrongAuthentication()
{
    var file = Path.Combine(Work("auth-source"), "file.txt"); await File.WriteAllTextAsync(file, "auth");
    var accepted = false;
    await using var server = new FileTransferServer("127.0.0.1", secret, Work("auth-cache"), (_, _, _) =>
    { accepted = true; return Task.CompletedTask; }, (_, _, _) => Task.CompletedTask, port: 0);
    server.Start();
    await ExpectFailure(async () => await new FileTransferSender().SendAsync("127.0.0.1", new string('x', 32), [file], port: server.Port));
    await ExpectFailure(async () =>
    {
        using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", server.Port);
        using var stream = new SslStream(client.GetStream(), false);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await PairedTlsTransport.AuthenticateClientAsync(stream, PairedTlsTransport.SecretBytes(secret), "LBM1", ct.Token);
    });
    Require(!accepted && !Directory.EnumerateFileSystemEntries(Work("auth-cache")).Any(), "Invalid peer reached staging.");
}

async Task WrongAddress()
{
    var file = Path.Combine(Work("ip-source"), "file.txt"); await File.WriteAllTextAsync(file, "address");
    await using var server = new FileTransferServer("127.0.0.2", secret, Work("ip-cache"), (_, _, _) => Task.CompletedTask,
        (_, _, _) => Task.CompletedTask, port: 0); server.Start();
    await ExpectFailure(async () => await new FileTransferSender().SendAsync("127.0.0.1", secret, [file], port: server.Port));
}

async Task InvalidManifests()
{
    var accepted = false;
    var cache = Work("unsafe-cache");
    await using var server = new FileTransferServer("127.0.0.1", secret, cache, (_, _, _) =>
    { accepted = true; return Task.CompletedTask; }, (_, _, _) => Task.CompletedTask, port: 0); server.Start();
    var cases = new[]
    {
        new FileTransferManifest(Guid.NewGuid(), [new("../escape.txt", false, 1)], [new("escape.txt", false)]),
        new FileTransferManifest(Guid.NewGuid(), [new("/absolute.txt", false, 1)], [new("absolute.txt", false)]),
        new FileTransferManifest(Guid.NewGuid(), [new("C:\\escape.txt", false, 1)], [new("escape.txt", false)]),
        new FileTransferManifest(Guid.NewGuid(), [new("root", false, 1), new("root/child", false, 1)], [new("root", false)]),
        new FileTransferManifest(Guid.NewGuid(), [new("root", true, 0), new("root/File", false, 1), new("root/file", false, 1)], [new("root", true)]),
        new FileTransferManifest(Guid.NewGuid(), [new("huge.bin", false, FileTransferLimits.MaximumFileBytes + 1)], [new("huge.bin", false)]),
        new FileTransferManifest(Guid.NewGuid(), [new("NUL", false, 0)], [new("NUL", false)]),
        new FileTransferManifest(Guid.NewGuid(), [new("root", true, 0), new("root/../escape", false, 0)], [new("root", true)])
    };
    foreach (var manifest in cases)
    {
        using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", server.Port);
        using var stream = new SslStream(client.GetStream(), false);
        await Authenticate(stream);
        await WriteJson(stream, manifest);
        var reply = await ReadJson(stream);
        Require(!reply.GetProperty("Accepted").GetBoolean(), "Unsafe manifest accepted remotely.");
        await Task.Delay(20);
    }
    Require(!accepted && !Directory.EnumerateFileSystemEntries(cache).Any() && !File.Exists(Path.Combine(scratch, "escape.txt")), "Unsafe paths reached callback/filesystem.");
}

async Task TamperedHash()
{
    var cache = Work("tamper-cache"); var committed = false;
    var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var server = new FileTransferServer("127.0.0.1", secret, cache, (_, _, _) => Task.CompletedTask,
        (_, _, _) => { committed = true; return Task.CompletedTask; }, (_, _) => { failed.TrySetResult(); return Task.CompletedTask; }, port: 0);
    server.Start();
    using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", server.Port);
    using var stream = new SslStream(client.GetStream(), false); await Authenticate(stream);
    await WriteJson(stream, new FileTransferManifest(Guid.NewGuid(), [new("tamper.bin", false, 4)], [new("tamper.bin", false)]));
    Require((await ReadJson(stream)).GetProperty("Accepted").GetBoolean(), "Manifest was not accepted.");
    await WriteNumber(stream, 0); await stream.WriteAsync(new byte[] { 1, 2, 3, 4 }); await stream.WriteAsync(new byte[32]);
    var reply = await ReadJson(stream);
    Require(!reply.GetProperty("Accepted").GetBoolean(), "Tampered SHA-256 accepted.");
    await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(50);
    Require(!committed && !Directory.EnumerateFileSystemEntries(cache).Any(), "Tampered upload committed or partial staging survived.");
}

async Task CancelDuringCommit()
{
    var file = Path.Combine(Work("cancel-source"), "cancel.txt"); await File.WriteAllTextAsync(file, "cancel");
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cache = Work("cancel-cache");
    await using var server = new FileTransferServer("127.0.0.1", secret, cache, (_, _, _) => Task.CompletedTask,
        async (_, _, ct) =>
        {
            ready.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
        }, (_, _) => { failed.TrySetResult(); return Task.CompletedTask; }, port: 0); server.Start();
    using var senderCt = new CancellationTokenSource();
    var sender = new FileTransferSender().SendAsync("127.0.0.1", secret, [file], ct: senderCt.Token, port: server.Port);
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); senderCt.Cancel();
    await ExpectFailure(async () => await sender);
    await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)); await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(50);
    Require(!Directory.EnumerateFileSystemEntries(cache).Any() && File.Exists(file), "Cancellation left partials or altered source.");
}

async Task CancelMidUpload()
{
    var cache = Work("cancel-upload-cache"); var committed = false;
    var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var server = new FileTransferServer("127.0.0.1", secret, cache, (_, _, _) => Task.CompletedTask,
        (_, _, _) => { committed = true; return Task.CompletedTask; }, (_, _) => { failed.TrySetResult(); return Task.CompletedTask; }, port: 0);
    server.Start();
    using (var client = new TcpClient())
    {
        await client.ConnectAsync("127.0.0.1", server.Port);
        using var stream = new SslStream(client.GetStream(), false); await Authenticate(stream);
        await WriteJson(stream, new FileTransferManifest(Guid.NewGuid(), [new("partial.bin", false, 1024 * 1024)], [new("partial.bin", false)]));
        Require((await ReadJson(stream)).GetProperty("Accepted").GetBoolean(), "Cancellation manifest not accepted.");
        await WriteNumber(stream, 0); await stream.WriteAsync(RandomNumberGenerator.GetBytes(32768));
    }
    await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(50);
    Require(!committed && !Directory.EnumerateFileSystemEntries(cache).Any(), "Partial canceled upload survived.");
}

async Task ChangedSource()
{
    var file = Path.Combine(Work("changed-source"), "change.txt"); await File.WriteAllTextAsync(file, "original");
    var manifest = FileTransferSender.CreateManifest([file]); await File.AppendAllTextAsync(file, " changed");
    await ExpectFailure(async () => await new FileTransferSender().SendAsync("127.0.0.1", secret, manifest, [file], port: 9));
}

Task SymbolicLink()
{
    var source = Work("link-source"); var outside = Work("link-outside");
    File.WriteAllText(Path.Combine(outside, "private.txt"), "must-not-traverse");
    var link = Path.Combine(source, "alias");
    try { Directory.CreateSymbolicLink(link, outside); }
    catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
    {
        results.Add(new { Test = "symlink_creation_available", Skipped = true, Reason = ex.GetType().Name });
        return Task.CompletedTask;
    }
    var rejected = false;
    try { FileTransferSender.CreateManifest([source]); } catch (IOException) { rejected = true; }
    Require(rejected, "Source symbolic link was traversed.");
    return Task.CompletedTask;
}

async Task Authenticate(SslStream stream)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await PairedTlsTransport.AuthenticateClientAsync(stream, PairedTlsTransport.SecretBytes(secret), FileTransferLimits.ProtocolContext, deadline.Token);
}
static async Task WriteNumber(Stream stream, uint number)
{ var prefix = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, number); await stream.WriteAsync(prefix); }
static async Task WriteJson(Stream stream, object value)
{ var bytes = JsonSerializer.SerializeToUtf8Bytes(value); await WriteNumber(stream, (uint)bytes.Length); await stream.WriteAsync(bytes); }
static async Task<JsonElement> ReadJson(Stream stream)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, deadline.Token);
    var bytes = new byte[BinaryPrimitives.ReadUInt32LittleEndian(prefix)]; await stream.ReadExactlyAsync(bytes, deadline.Token);
    return JsonSerializer.Deserialize<JsonElement>(bytes);
}
sealed class InlineProgress(Action<FileTransferProgress> report) : IProgress<FileTransferProgress>
{ public void Report(FileTransferProgress value) => report(value); }
