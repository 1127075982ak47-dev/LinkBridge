using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkBridge.App.Services;

internal static class Program
{
    private const int TextLimit = 1024 * 1024, ImageLimit = 32 * 1024 * 1024;
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        try
        {
            if (args is ["--session"]) return RunSession();
            if (args is [var mode, var fixture])
            {
                if (mode is "--live-read-text" or "--live-read-png") return ReadExpected(mode.EndsWith("text") ? "text" : "png", fixture);
                if (mode is "--live-write-text" or "--live-write-png")
                {
                    using var adapter = new WindowsSharedClipboard(); adapter.Start();
                    WriteFixture(adapter, mode.EndsWith("text") ? "text" : "png", fixture); return 0;
                }
            }
            Output(new { result = "USAGE", modes = new[] { "--live-read-text FILE", "--live-write-text FILE", "--live-read-png FILE", "--live-write-png FILE", "--session" } });
            return 2;
        }
        catch { Output(new { result = "ERROR", reason = "Native clipboard operation failed; no clipboard contents emitted." }); return 1; }
    }

    private static int RunSession()
    {
        // Original user contents are retained only in this process's memory, never stdout or disk.
        var original = ReadRecognized();
        if (original is null && CountClipboardFormats() != 0)
        {
            Output(new { result = "SESSION_BLOCKED_UNSUPPORTED_HISTORY", reason = "Existing clipboard formats cannot be restored safely; no contents changed." });
            return 4;
        }
        using var adapter = new WindowsSharedClipboard(); adapter.Start();
        Output(new { result = "SESSION_READY", backupRecognized = original is not null,
            commands = "JSON arrays: [write-text,FILE], [read-text,FILE], [write-png,FILE], [read-png,FILE], [restore], [quit]" });
        var restored = false;
        try
        {
            while (Console.ReadLine() is { } line)
            {
                try
                {
                    var command = JsonSerializer.Deserialize<string[]>(line) ?? throw new ArgumentException();
                    if (command is ["restore"] or ["quit"])
                    {
                        if (LinkBridgeIsRunning())
                        {
                            Output(new { result = "RESTORE_BLOCKED_APP_RUNNING", reason = "Stop LinkBridge.App first so original history cannot be synchronized." });
                            continue;
                        }
                        if (!restored)
                        {
                            if (original is not null) adapter.ApplyAsync(original).GetAwaiter().GetResult();
                            else ClearClipboard(adapter);
                            restored = true;
                            Output(new { result = "ORIGINAL_CLIPBOARD_RESTORED", recognized = original is not null });
                        }
                        if (command[0] == "quit") return 0;
                    }
                    else if (command is [var mode, var file] && mode is "write-text" or "write-png")
                    { WriteFixture(adapter, mode.EndsWith("text") ? "text" : "png", file); restored = false; }
                    else if (command is [var mode2, var file2] && mode2 is "read-text" or "read-png")
                        ReadExpected(mode2.EndsWith("text") ? "text" : "png", file2);
                    else Output(new { result = "INVALID_COMMAND" });
                }
                catch { Output(new { result = "ERROR", reason = "Native clipboard command failed; no clipboard contents emitted." }); }
            }
            if (!restored && !LinkBridgeIsRunning())
            {
                if (original is not null) adapter.ApplyAsync(original).GetAwaiter().GetResult(); else ClearClipboard(adapter);
                restored = true; Output(new { result = "ORIGINAL_CLIPBOARD_RESTORED_ON_EOF" });
            }
            if (!restored) Output(new { result = "RESTORE_SKIPPED_APP_RUNNING", reason = "Original history was not broadcast." });
            return restored ? 0 : 4;
        }
        finally { if (original is not null) CryptographicOperations.ZeroMemory(original.Data); }
    }

    private static void WriteFixture(WindowsSharedClipboard adapter, string kind, string file)
    {
        var payload = Fixture(kind, file);
        adapter.ApplyAsync(payload).GetAwaiter().GetResult();
        var dimensions = Dimensions(payload);
        Output(new { result = "FIXTURE_WRITTEN", kind, expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(payload.Data)),
            fixtureBytes = payload.Data.Length, width = dimensions.Width, height = dimensions.Height });
    }

    private static int ReadExpected(string kind, string file)
    {
        var expected = Fixture(kind, file);
        var actual = ReadRecognized();
        var matches = actual is not null && actual.Kind == kind && actual.Data.AsSpan().SequenceEqual(expected.Data);
        var dimensions = Dimensions(expected);
        Output(new { result = matches ? "MATCH" : "NOT_MATCH", kind,
            expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(expected.Data)), fixtureBytes = expected.Data.Length,
            width = dimensions.Width, height = dimensions.Height, clipboardHasExpectedKind = actual?.Kind == kind });
        if (actual is not null) CryptographicOperations.ZeroMemory(actual.Data);
        return matches ? 0 : 3;
    }

    private static SharedClipboardContent Fixture(string kind, string file)
    {
        var bytes = File.ReadAllBytes(file);
        if (kind == "text")
        {
            if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes.AsSpan(3).ToArray();
            if (bytes.Length > TextLimit || bytes.AsSpan().Contains((byte)0)) throw new ArgumentException();
            _ = new UTF8Encoding(false, true).GetString(bytes);
        }
        else
        {
            if (bytes.Length > ImageLimit || bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new ArgumentException();
        }
        return new(kind, bytes);
    }

    private static SharedClipboardContent? ReadRecognized()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero)) { Thread.Sleep(25); continue; }
            try
            {
                if (IsClipboardFormatAvailable(15)) return null;
                var png = RegisterClipboardFormatW("PNG");
                if (IsClipboardFormatAvailable(png))
                {
                    var bytes = ReadGlobal(GetClipboardData(png), ImageLimit);
                    var method = typeof(WindowsSharedClipboard).GetMethod("TrimPng", BindingFlags.Static | BindingFlags.NonPublic)!;
                    return new("png", (byte[])method.Invoke(null, [bytes])!);
                }
                if (IsClipboardFormatAvailable(17) || IsClipboardFormatAvailable(8))
                {
                    var format = IsClipboardFormatAvailable(17) ? 17u : 8u;
                    var dib = ReadGlobal(GetClipboardData(format), 256 * 1024 * 1024);
                    var method = typeof(WindowsSharedClipboard).GetMethod("DibToPng", BindingFlags.Static | BindingFlags.NonPublic)!;
                    return new("png", (byte[])method.Invoke(null, [dib])!);
                }
                if (IsClipboardFormatAvailable(13))
                {
                    var bytes = ReadGlobal(GetClipboardData(13), TextLimit * 2 + 2);
                    var length = 0;
                    while (length + 1 < bytes.Length && (bytes[length] != 0 || bytes[length + 1] != 0)) length += 2;
                    var utf8 = Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(bytes, 0, length));
                    CryptographicOperations.ZeroMemory(bytes);
                    return utf8.Length <= TextLimit ? new("text", utf8) : null;
                }
                return null;
            }
            finally { CloseClipboard(); }
        }
        throw new IOException("clipboard busy");
    }

    private static byte[] ReadGlobal(IntPtr handle, int limit)
    {
        if (handle == IntPtr.Zero) throw new IOException();
        var size = GlobalSize(handle).ToUInt64();
        if (size == 0 || size > (ulong)limit) throw new IOException();
        var address = GlobalLock(handle); if (address == IntPtr.Zero) throw new IOException();
        try { var bytes = new byte[(int)size]; Marshal.Copy(address, bytes, 0, bytes.Length); return bytes; }
        finally { GlobalUnlock(handle); }
    }
    private static (int Width, int Height) Dimensions(SharedClipboardContent content) => content.Kind == "png"
        ? (checked((int)BinaryPrimitives.ReadUInt32BigEndian(content.Data.AsSpan(16))), checked((int)BinaryPrimitives.ReadUInt32BigEndian(content.Data.AsSpan(20)))) : (0, 0);
    private static bool LinkBridgeIsRunning()
    {
        var processes = Process.GetProcessesByName("LinkBridge.App");
        try { return processes.Length != 0; } finally { foreach (var process in processes) process.Dispose(); }
    }
    private static void ClearClipboard(WindowsSharedClipboard adapter)
    {
        var hwnd = (IntPtr)typeof(WindowsSharedClipboard).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        if (!OpenClipboard(hwnd)) throw new IOException();
        try { if (!EmptyClipboard()) throw new IOException(); } finally { CloseClipboard(); }
    }
    private static void Output(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern int CountClipboardFormats();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
}
