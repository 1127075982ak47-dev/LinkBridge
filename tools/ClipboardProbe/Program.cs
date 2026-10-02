using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkBridge.Core.Clipboard;
using LinkBridge.Core.Security;

var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
var results = new List<object>();
await Check("utf8_text_exact_png_bytes_source_buffer_preserved_and_replay_refused", RoundTrip);
await Check("ack_waits_for_clipboard_apply_and_apply_failure_refused", ApplyGate);
await Check("wrong_psk_context_and_allowed_ip_refused", Authentication);
await Check("remote_oversize_invalid_utf8_png_and_hash_refused", UnsafePayloads);
await Check("sender_cancel_reaches_clipboard_apply", CancelApply);
await Check("half_upload_disconnect_never_applies", HalfUpload);
Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Results = results }, new JsonSerializerOptions { WriteIndented = true }));

async Task Check(string name, Func<Task> test)
{ var started = DateTime.UtcNow; await test(); results.Add(new { Test = name, Passed = true, Milliseconds = (DateTime.UtcNow - started).TotalMilliseconds }); }
static void Require(bool test, string description) { if (!test) throw new InvalidOperationException(description); }
static async Task ExpectFailure(Func<Task> action)
{
    var failed = false;
    try { await action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or System.Security.Authentication.AuthenticationException or SocketException or OperationCanceledException)
    { failed = true; }
    Require(failed, "Unsafe request unexpectedly succeeded.");
}
static ClipboardPayload Text(string value) => new(Guid.NewGuid(), ClipboardKinds.Text, Encoding.UTF8.GetBytes(value));

async Task RoundTrip()
{
    var received = new List<ClipboardPayload>();
    await using var server = new ClipboardBridgeServer("127.0.0.1", secret, (payload, _) =>
    { received.Add(payload); return Task.CompletedTask; }, port: 0); server.Start();
    var sender = new ClipboardBridgeSender();
    var original = Text("跨屏取材 🐈\n第二行：引号\" 和制表符\t保持原样。");
    var expected = original.Data.ToArray();
    var sending = sender.SendAsync("127.0.0.1", secret, original, port: server.Port);
    original.Data.AsSpan().Fill(0); // Sender must already have taken its immutable snapshot.
    await sending;
    Require(received.Count == 1 && received[0].Data.SequenceEqual(expected), "UTF8 or snapshot corruption.");
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1kAAAAASUVORK5CYII=");
    var image = new ClipboardPayload(Guid.NewGuid(), ClipboardKinds.Png, png);
    await sender.SendAsync("127.0.0.1", secret, image, port: server.Port);
    Require(received[1].Kind == ClipboardKinds.Png && received[1].Data.SequenceEqual(png), "PNG was changed.");
    await ExpectFailure(() => sender.SendAsync("127.0.0.1", secret, image, port: server.Port));
    Require(received.Count == 2 && png[0] == 137, "Replayed payload applied or source buffer erased.");
}

async Task ApplyGate()
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var allow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var server = new ClipboardBridgeServer("127.0.0.1", secret, async (_, ct) =>
    { entered.TrySetResult(); await allow.Task.WaitAsync(ct); }, port: 0); server.Start();
    var sending = new ClipboardBridgeSender().SendAsync("127.0.0.1", secret, Text("gate"), port: server.Port);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Require(!sending.IsCompleted, "Sender returned before actual clipboard apply.");
    allow.TrySetResult(); await sending;
    await using var failing = new ClipboardBridgeServer("127.0.0.1", secret, (_, _) => throw new UnauthorizedAccessException("private content must not be logged"), port: 0);
    var diagnostics = new List<string>(); failing.Diagnostic += diagnostics.Add; failing.Start();
    await ExpectFailure(() => new ClipboardBridgeSender().SendAsync("127.0.0.1", secret, Text("failure"), port: failing.Port));
    Require(!diagnostics.Any(message => message.Contains("private content", StringComparison.Ordinal)), "Content leaked in diagnostic.");
}

async Task Authentication()
{
    var count = 0;
    await using var server = new ClipboardBridgeServer("127.0.0.1", secret, (_, _) => { count++; return Task.CompletedTask; }, port: 0); server.Start();
    await ExpectFailure(() => new ClipboardBridgeSender().SendAsync("127.0.0.1", new string('x', 32), Text("auth"), port: server.Port));
    await ExpectFailure(async () =>
    {
        using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", server.Port);
        using var stream = new SslStream(client.GetStream(), false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await PairedTlsTransport.AuthenticateClientAsync(stream, PairedTlsTransport.SecretBytes(secret), "LBF1", deadline.Token);
    });
    await using var unpaired = new ClipboardBridgeServer("127.0.0.2", secret, (_, _) => { count++; return Task.CompletedTask; }, port: 0); unpaired.Start();
    await ExpectFailure(() => new ClipboardBridgeSender().SendAsync("127.0.0.1", secret, Text("ip"), port: unpaired.Port));
    Require(count == 0, "Unauthenticated data applied.");
}

async Task UnsafePayloads()
{
    var count = 0;
    await using var server = new ClipboardBridgeServer("127.0.0.1", secret, (_, _) => { count++; return Task.CompletedTask; }, port: 0); server.Start();
    // Oversize is refused from the header, before allocation or a payload read.
    foreach (var (kind, length) in new[] { ((byte)1, ClipboardLimits.MaximumTextBytes + 1), ((byte)2, ClipboardLimits.MaximumImageBytes + 1), ((byte)3, 0), ((byte)1, -1) })
        await RawRequest(kind, length, [], true, server.Port);
    await RawRequest(1, 2, [0xC0, 0xAF], true, server.Port); // Invalid overlong UTF8.
    await RawRequest(2, 4, [1, 2, 3, 4], true, server.Port);
    await RawRequest(1, 4, Encoding.UTF8.GetBytes("hash"), false, server.Port);
    Require(count == 0, "Unsafe data reached clipboard callback.");
    await ExpectFailure(() => new ClipboardBridgeSender().SendAsync("127.0.0.1", secret,
        new ClipboardPayload(Guid.NewGuid(), ClipboardKinds.Text, new byte[ClipboardLimits.MaximumTextBytes + 1]), port: server.Port));
}

async Task CancelApply()
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var server = new ClipboardBridgeServer("127.0.0.1", secret, async (_, ct) =>
    {
        entered.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
    }, port: 0); server.Start();
    using var stop = new CancellationTokenSource();
    var sending = new ClipboardBridgeSender().SendAsync("127.0.0.1", secret, Text("cancel"), stop.Token, server.Port);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
    await ExpectFailure(async () => await sending);
    await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
}

async Task HalfUpload()
{
    var count = 0;
    await using var server = new ClipboardBridgeServer("127.0.0.1", secret, (_, _) => { count++; return Task.CompletedTask; }, port: 0); server.Start();
    using (var client = new TcpClient())
    {
        await client.ConnectAsync("127.0.0.1", server.Port); using var stream = new SslStream(client.GetStream(), false);
        await Authenticate(stream); await stream.WriteAsync(Header(1, 4096, SHA256.HashData(new byte[4096])));
        await stream.WriteAsync(new byte[128]);
    }
    await Task.Delay(50); Require(count == 0, "Partial payload applied.");
}

async Task RawRequest(byte kind, int length, byte[] body, bool goodHash, int port)
{
    using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", port);
    using var stream = new SslStream(client.GetStream(), false); await Authenticate(stream);
    await stream.WriteAsync(Header(kind, length, goodHash ? SHA256.HashData(body) : new byte[32]));
    if (body.Length > 0) await stream.WriteAsync(body);
    var reply = new byte[17]; using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await stream.ReadExactlyAsync(reply, deadline.Token); Require(reply[16] == 0, "Unsafe payload accepted.");
}
static byte[] Header(byte kind, int length, byte[] hash)
{
    var header = new byte[53]; Guid.NewGuid().TryWriteBytes(header.AsSpan(0, 16)); header[16] = kind;
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(17, 4), length); hash.CopyTo(header, 21); return header;
}
async Task Authenticate(SslStream stream)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await PairedTlsTransport.AuthenticateClientAsync(stream, PairedTlsTransport.SecretBytes(secret), ClipboardLimits.ProtocolContext, deadline.Token);
}
