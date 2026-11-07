using LinkBridge.Core;
using LinkBridge.Input;
using System.Runtime.InteropServices;

if (args is ["--bottom-layout-self-test"])
{
    void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    var seam = new BottomScreenSeam(3840, 0, 2560, 1440, 1920);
    Check(seam.Start == 4160 && seam.End == 6080, "Mac must be centered under the second Windows monitor");
    Check(!seam.ContainsEdge(3839, 800) && !seam.ContainsEdge(3840, 800), "Windows internal boundary must never enter Mac");
    Check(!seam.ContainsEdge(2000, 2159), "Primary monitor bottom must remain local");
    Check(!seam.ContainsEdge(4159, 1439) && !seam.ContainsEdge(6080, 1439), "Outside seam must remain local");
    Check(seam.ContainsEdge(4160, 1439) && seam.ContainsEdge(6079, 1439), "Both seam endpoints must work");
    Check(!seam.ContainsEdge(5000, 1440) && !seam.ContainsEdge(5000, 1437), "Only the selected monitor edge may switch");
    foreach (var x in new[] {4160, 4321, 5120, 6079})
        Check(seam.LocalX(seam.RemoteX(x, 1920), 1920) == x, "Entry and return must preserve horizontal position");
    Check(seam.Returns(32, -40) && !seam.Returns(32, 40) && !seam.Returns(32, -4), "Return only across Mac top edge");
    var negative = new BottomScreenSeam(-2560, -1440, 2560, 1440, 1920);
    Check(negative.ContainsEdge(-1280, -1), "Negative monitor coordinates must work");
    Console.WriteLine("PASS bottom seam: dual Windows internal boundary, selected bottom, centered range, entry/return mapping, top return and negative coordinates");
    return;
}

if (args is ["--file-release-self-test"])
{
    var gate = new FileDragReleaseGate();
    gate.Arm();
    if (gate.Release() || !gate.WaitingForReady || !gate.Ready() || gate.Ready() || gate.Release())
        throw new Exception("Early release must wait for ready and flush exactly once.");
    gate.Arm();
    if (gate.Ready() || !gate.Release() || gate.WaitingForReady)
        throw new Exception("Normal release must flush after ready.");
    gate.Arm(); gate.Release(); gate.Cancel();
    if (gate.Ready() || gate.Release() || gate.WaitingForReady)
        throw new Exception("Cancelled pending release must never reach the peer.");
    Console.WriteLine("PASS early release, normal release, exactly once, cancellation");
    return;
}

if (args.Length == 4 && args[0] == "--native-roundtrip")
{
    // Run only with a dedicated foreground scratch editor on the receiving Mac.
    var marker = args[2];
    if (marker.Length is < 1 or > 40 || marker.Any(c => !char.IsAsciiLetterOrDigit(c)))
        throw new ArgumentException("Use a short ASCII letter/digit test marker.");
    var alias = args[3];
    if (alias.Length is < 1 or > 80 || alias.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
        throw new ArgumentException("Provide the configured SSH alias for the test Mac.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    async Task ConfirmScratchFocus()
    {
        var start = new System.Diagnostics.ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(alias);
        start.ArgumentList.Add("osascript -e 'tell application \"System Events\" to return name of first process whose frontmost is true'");
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Focus check unavailable.");
        var front = (await process.StandardOutput.ReadToEndAsync(timeout.Token)).Trim();
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0 || front != "TextEdit") throw new IOException("Scratch editor lost focus; native test stopped.");
    }
    await ConfirmScratchFocus();
    await using var client = await InputBridgeClient.ConnectAsync(args[1], SettingsStore.Load().InputPairingSecret, timeout.Token);
    if (!client.TrySend(new InputPacket(InputEventType.Move, 0, 0, 80, 30))) throw new IOException("Move enqueue failed.");
    foreach (var character in marker)
    {
        await ConfirmScratchFocus();
        var vk = (ushort)char.ToUpperInvariant(character);
        if (!client.TrySend(new InputPacket(InputEventType.Key, 0, vk, 0, 0))) throw new IOException("Key enqueue failed.");
        await Task.Delay(60, timeout.Token);
        if (!client.TrySend(new InputPacket(InputEventType.Key, 1, vk, 0, 0))) throw new IOException("Key release enqueue failed.");
        await Task.Delay(60, timeout.Token);
    }
    await Task.Delay(300, timeout.Token);
    Console.WriteLine("Authenticated native mouse/key packets sent; confirm the marker in the Mac scratch editor independently.");
    return;
}

if (args.Length == 1 && args[0] == "--hook-check")
{
    using var capture = new WindowsInputCapture(_ => { }, () => false, 2560, 1440);
    capture.Start();
    Console.WriteLine(capture.IsActive ? "Windows input hooks installed" : "Windows input hooks unavailable");
    return;
}

if (args.Length == 1 && args[0] == "--edge-self-test")
{
    InputProbeNative.SetProcessDpiAwarenessContext((nint)(-4));
    var oldContext = InputProbeNative.SetThreadDpiAwarenessContext((nint)(-4));
    InputProbeNative.GetCursorPos(out var original);
    var entered = 0;
    var exited = 0;
    using var capture = new WindowsInputCapture(_ => { }, () => true, 2560, 1440);
    capture.RemoteEntered += () => entered++;
    capture.RemoteExited += () => exited++;
    capture.ExitRequested += capture.LeaveRemote;
    try
    {
        InputProbeNative.SetCursorPos(1000, 800);
        capture.Start();
        Console.WriteLine($"local={capture.LocalWidth}x{capture.LocalHeight}");
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            InputProbeNative.mouse_event(1, 5000, 0, 0, 0);
            await Task.Delay(250);
            InputProbeNative.mouse_event(1, unchecked((uint)-5000), 0, 0, 0);
        });
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            while (InputProbeNative.PeekMessageW(out _, 0, 0, 0, 1)) { }
            Thread.Sleep(5);
        }
        Console.WriteLine($"edge_entered={entered} edge_exited={exited}");
    }
    finally
    {
        capture.Stop();
        InputProbeNative.SetCursorPos(original.X, original.Y);
        InputProbeNative.SetThreadDpiAwarenessContext(oldContext);
    }
    return;
}

if (args.Length == 4 && args[0] == "--move")
{
    await using var client = await InputBridgeClient.ConnectAsync(args[1], SettingsStore.Load().InputPairingSecret);
    client.TrySend(new InputPacket(InputEventType.Move, 0, 0, int.Parse(args[2]), int.Parse(args[3])));
    await Task.Delay(300);
    Console.WriteLine("Remote pointer move sent");
    return;
}

if (args.Length == 4 && args[0] == "--hold-key")
{
    await using var client = await InputBridgeClient.ConnectAsync(args[1], SettingsStore.Load().InputPairingSecret);
    var key = ushort.Parse(args[2]);
    client.TrySend(new InputPacket(InputEventType.Key, 0, key, 0, 0));
    await Task.Delay(int.Parse(args[3]));
    client.TrySend(new InputPacket(InputEventType.Key, 1, key, 0, 0));
    await Task.Delay(200);
    Console.WriteLine("Remote key down and up sent");
    return;
}

if (args.Length == 3 && args[0] == "--click")
{
    await using var client = await InputBridgeClient.ConnectAsync(args[1], SettingsStore.Load().InputPairingSecret);
    var button = byte.Parse(args[2]);
    client.TrySend(new InputPacket(InputEventType.Button, button, 0, 1, 0));
    await Task.Delay(80);
    client.TrySend(new InputPacket(InputEventType.Button, button, 0, 0, 0));
    await Task.Delay(200);
    Console.WriteLine("Remote button click sent");
    return;
}

if (args.Length == 3 && args[0] == "--wheel")
{
    await using var client = await InputBridgeClient.ConnectAsync(args[1], SettingsStore.Load().InputPairingSecret);
    client.TrySend(new InputPacket(InputEventType.Wheel, 0, 0, int.Parse(args[2]), 0));
    await Task.Delay(200);
    Console.WriteLine("Remote wheel event sent");
    return;
}

Console.WriteLine("Usage: InputProbe --hook-check | --edge-self-test | --move <Mac IP> <dx> <dy> | --hold-key <Mac IP> <Windows VK> <ms> | --click <Mac IP> <button 0-2> | --wheel <Mac IP> <delta>");

internal static class InputProbeNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    {
        public nint Hwnd;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll")] internal static extern bool PeekMessageW(out Message message, nint window, uint min, uint max, uint remove);
}
