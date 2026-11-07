using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Security.Cryptography;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkBridge.App.Services;
using LinkBridge.App.Views;
using LinkBridge.Core.WindowRoaming;

namespace LinkBridge.App.ViewModels;

public partial class MainViewModel
{
    private WindowsWindowEdge? _windowEdge;
    private MacWindowEdge? _macWindowEdge;
    private TcpListener? _windowListener;
    private Task? _windowAcceptTask, _windowSessionTask;
    private CancellationTokenSource? _windowSessionCancellation;
    private WindowRoamingConnection? _windowConnection;
    private RoamingWindow? _roamingWindow;
    private int _windowBusy;
    private Guid _outgoingWindowSession;
    private ChannelWriter<WindowRemoteInput>? _windowControlWriter;
    [ObservableProperty] private bool _windowRoamingEnabled = true;
    [ObservableProperty] private string _windowRoamingStatus = "拖动窗口标题栏，穿过屏幕边缘";
    [ObservableProperty] private WindowCaptureTarget? _selectedRoamingWindow;
    public ObservableCollection<WindowCaptureTarget> RoamingWindows { get; } = [];
    public bool IsWindowsWindowSender => OperatingSystem.IsWindows();
    // The old mirrored-window/input-warp design cannot provide a continuous desktop
    // and interferes with KVM. Keep it quarantined while the display path is replaced.
    public bool LegacyWindowMirroringAvailable => false;
    public bool CanReturnRoamingWindow => Volatile.Read(ref _windowBusy) != 0;
    private string? WindowPeerAddress => OperatingSystem.IsWindows() ? _filePeerAddress : Settings.AllowedControllerAddress;
    partial void OnWindowRoamingEnabledChanged(bool value)
    {
        Settings.WindowRoamingEnabled = value; SaveSettings();
        if (!value) _windowSessionCancellation?.Cancel();
        WindowRoamingStatus = value ? "按住标题栏，直接拖过屏幕边缘" : "窗口跨屏已暂停";
    }
    private bool CanStartWindowDrag() => LegacyWindowMirroringAvailable && !_shuttingDown && WindowRoamingEnabled && IsPeerConnected &&
        Volatile.Read(ref _windowBusy) == 0 && !IsFileTransferBusy;
    private void InitializeWindowRoaming()
    {
        if (!LegacyWindowMirroringAvailable)
        {
            WindowRoamingEnabled = false;
            WindowRoamingStatus = "旧窗口镜像已停用 · 连续扩展屏尚未就绪";
            AppendLog("[窗口] 已停用旧镜像与输入接力，普通键鼠和文件通道独立运行。");
            return;
        }
        if (Settings.InputPairingSecret.Length != 32) return;
        WindowRoamingEnabled = Settings.WindowRoamingEnabled;
        try
        {
            if (OperatingSystem.IsWindows() && _windowEdge is null)
            {
                _windowEdge = new WindowsWindowEdge(CanStartWindowDrag, () => _inputCapture?.IsLeftButtonHeld == true);
                _windowEdge.Diagnostic += phase => AppendLog("[窗口拖动] " + phase);
                _windowEdge.HandoffRequested += BeginNativeWindowHandoff;
                if (_inputCapture is not null)
                {
                    _inputCapture.LocalLeftPressed += _windowEdge.RecordCaptionPress;
                    _inputCapture.LocalLeftReleased += _windowEdge.ClearCaptionPress;
                    _inputCapture.WindowDragReleased += OnOutgoingWindowDragReleased;
                    _inputCapture.WindowDragCancelled += OnOutgoingWindowDragReleased;
                }
            }
            if (_windowListener is null)
            {
                _windowListener = new TcpListener(IPAddress.IPv6Any, WindowRoamingLimits.DefaultPort);
                _windowListener.Server.DualMode = true; _windowListener.Start(4);
                _windowAcceptTask = AcceptPeerWindowsAsync();
            }
            if (OperatingSystem.IsMacOS() && _macWindowEdge is null)
            {
                try
                {
                    _macWindowEdge = new MacWindowEdge(CanStartWindowDrag);
                    _macWindowEdge.Diagnostic += message => AppendLog("[窗口拖动] " + message);
                    _macWindowEdge.HandoffRequested += BeginNativeWindowHandoff;
                }
                catch (Exception error) { AppendLog("[窗口] Mac 拖动监听待就绪：" + error.Message); }
            }
            WindowRoamingStatus = "按住标题栏，直接拖过屏幕边缘";
            AppendLog("[窗口] 双向窗口跨屏通道就绪");
            if (OperatingSystem.IsMacOS())
                AppendLog("[窗口] Mac 屏幕录制权限：" + (MacWindowCapture.HasScreenRecordingPermission ? "已允许" : "待允许"));
        }
        catch (Exception error)
        {
            WindowRoamingStatus = "窗口跨屏暂不可用";
            AppendLog("[窗口] 启动失败：" + error.GetType().Name);
        }
    }
    private void BeginNativeWindowHandoff(nint hwnd, string title, int offsetX, int offsetY, double scale)
    {
        var entryY = WindowHandoffPosition.ReadNormalizedY();
        // The native edge callback already ended the source's move loop. Capture physical
        // ownership immediately; delaying this to another dispatcher turn can lose mouse-up.
        if (!CanStartWindowDrag() || (OperatingSystem.IsWindows() && _inputCapture?.BeginWindowDrag(allowReleasedHandoff: true) != true))
        { _windowEdge?.Restore(hwnd); _macWindowEdge?.Restore(hwnd); return; }
        _windowSessionTask = StartWindowHandoffAsync(hwnd, title, true, offsetX, offsetY, scale, entryY);
    }
    private void OnOutgoingWindowDragReleased()
    {
        var session = _outgoingWindowSession;
        if (session != Guid.Empty && _windowControlWriter?.TryWrite(new(session, "drag-end")) == false)
            _windowSessionCancellation?.Cancel();
    }
    private async Task AcceptPeerWindowsAsync()
    {
        while (!_lifetimeCts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _windowListener!.AcceptTcpClientAsync(_lifetimeCts.Token).ConfigureAwait(false); }
            catch when (_lifetimeCts.IsCancellationRequested || _shuttingDown) { break; }
            var peer = WindowPeerAddress;
            if (!IPAddress.TryParse(peer, out var expected) ||
                (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv6().Equals(expected.MapToIPv6()) != true ||
                Interlocked.CompareExchange(ref _windowBusy, 1, 0) != 0)
            { client.Dispose(); continue; }
            _windowSessionTask = ReceivePeerWindowAsync(client);
        }
    }
    private async Task ReceivePeerWindowAsync(TcpClient client)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _windowSessionCancellation = cancellation;
        WindowRoamingConnection? connection = null;
        Task? heartbeat = null, writer = null;
        Guid session = Guid.Empty; var previousSequence = -1;
        var firstPresented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WindowRemoteInput? returnDrag = null;
        var returnRequested = 0;
        var source = OperatingSystem.IsWindows() ? "Mac" : "Windows";
        try
        {
            connection = await WindowRoamingConnection.FromAcceptedAsync(client, Settings.InputPairingSecret,
                () => WindowRoamingEnabled, cancellation.Token).ConfigureAwait(false);
            _windowConnection = connection;
            var hello = await connection.ReadAsync(cancellation.Token).ConfigureAwait(false);
            if (hello.Type != WindowMessageType.Descriptor) throw new InvalidDataException();
            var descriptor = hello.Descriptor!; session = descriptor.SessionId;
            if (descriptor.SourcePlatform != source) throw new InvalidDataException();
            var inputs = Channel.CreateBounded<WindowRemoteInput>(new BoundedChannelOptions(256)
                { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
            writer = Task.Run(async () =>
            {
                await foreach (var input in inputs.Reader.ReadAllAsync(cancellation.Token))
                    await connection.SendInputAsync(input, cancellation.Token).ConfigureAwait(false);
            }, cancellation.Token);
            _ = writer.ContinueWith(_ => CancelWindowSafely(cancellation), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            async Task ReturnAsync()
            {
                if (Interlocked.Exchange(ref returnRequested, 1) != 0) return;
                try
                {
                    if (returnDrag is not null)
                    {
                        // Windows is the physical keyboard/mouse host. A Mac-origin window
                        // returning to the right must carry the still-held pointer back as well.
                        var canContinue = !OperatingSystem.IsWindows() ||
                            await Dispatcher.UIThread.InvokeAsync(() => _inputCapture?.BeginWindowDrag() == true);
                        if (canContinue) await connection.SendInputAsync(returnDrag, cancellation.Token).ConfigureAwait(false);
                    }
                }
                catch { }
                finally { CancelWindowSafely(cancellation); }
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var proxy = _roamingWindow = new RoamingWindow(descriptor,
                    OperatingSystem.IsWindows() ? () => _inputCapture?.IsLeftButtonHeld == true : null);
                if (descriptor.Title == "LinkBridge Window Roaming Test")
                    proxy.Diagnostic += message => AppendLog("[窗口输入测试] " + message);
                proxy.FramePresented += _ => firstPresented.TrySetResult();
                proxy.InputRequested += input =>
                {
                    if (!inputs.Writer.TryWrite(input)) CancelWindowSafely(cancellation);
                };
                proxy.ReturnDragRequested += (x, y, normalizedY) =>
                    returnDrag = new(session, "return-drag", x, y, (int)Math.Round(normalizedY * 10000));
                proxy.ReturnRequested += () => _ = ReturnAsync();
                proxy.Show(); WindowRoamingStatus = $"正在接入 {source} 窗口…";
                OnPropertyChanged(nameof(CanReturnRoamingWindow));
            });
            heartbeat = HeartbeatWindowAsync(connection, cancellation.Token);
            var ready = false;
            while (!cancellation.IsCancellationRequested)
            {
                var message = await connection.ReadAsync(cancellation.Token).ConfigureAwait(false);
                if (message.Type == WindowMessageType.End) break;
                if (message.Type == WindowMessageType.Heartbeat) continue;
                if (message.Type == WindowMessageType.Status) { _roamingWindow?.SetSourceStatus(message.Status); continue; }
                if (message.Type == WindowMessageType.Input && message.Input!.SessionId == session)
                {
                    var control = message.Input;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (control.Kind == "hit-result") _roamingWindow?.ApplyHitTest(control.Code, control.X);
                        else if (control.Kind == "drag-start") _roamingWindow?.BeginIncomingDrag(OperatingSystem.IsMacOS());
                        else if (control.Kind == "drag-end") _roamingWindow?.EndIncomingDrag();
                        else throw new InvalidDataException();
                    });
                    continue;
                }
                if (message.Type != WindowMessageType.Frame || message.Frame!.SessionId != session ||
                    message.Frame.Sequence <= previousSequence) throw new InvalidDataException();
                var frame = message.Frame; previousSequence = frame.Sequence;
                _roamingWindow?.ApplyFrame(frame);
                if (!ready)
                {
                    await firstPresented.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellation.Token).ConfigureAwait(false);
                    ready = true;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (OperatingSystem.IsWindows()) _inputCapture?.LeaveRemote();
                        WindowRoamingStatus = $"{source} 窗口已跨屏";
                    });
                    await connection.SendReadyAsync(cancellation.Token).ConfigureAwait(false);
                    AppendLog($"[窗口] {source} 真实首帧已呈现 · {frame.Width}×{frame.Height}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppendLog("[窗口] 接收结束：" + error.GetType().Name); }
        finally
        {
            await cancellation.CancelAsync();
            if (connection is not null)
            {
                try { using var end = new CancellationTokenSource(500); await connection.SendEndAsync(end.Token); } catch { }
                await connection.DisposeAsync();
            }
            client.Dispose(); if (heartbeat is not null) try { await heartbeat; } catch { }
            if (writer is not null) try { await writer; } catch { }
            if (ReferenceEquals(_windowConnection, connection)) _windowConnection = null;
            if (ReferenceEquals(_windowSessionCancellation, cancellation)) _windowSessionCancellation = null;
            Interlocked.Exchange(ref _windowBusy, 0);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _roamingWindow?.Close(); _roamingWindow = null;
                WindowRoamingStatus = $"窗口已返回 {source}";
                OnPropertyChanged(nameof(CanReturnRoamingWindow));
            });
        }
    }
    private async Task StartWindowHandoffAsync(nint hwnd, string title, bool continueDrag = false,
        int dragOffsetX = 0, int dragOffsetY = 0, double sourceScale = 1, double entryY = .5)
    {
        var peer = WindowPeerAddress;
        if (!WindowRoamingEnabled || string.IsNullOrWhiteSpace(peer) || Interlocked.CompareExchange(ref _windowBusy, 1, 0) != 0)
        { _windowEdge?.Restore(hwnd); _macWindowEdge?.Restore(hwnd); _inputCapture?.EndWindowDrag(); return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _windowSessionCancellation = cancellation;
        IDisposable? capture = null, input = null;
        Action<int, int>? updateGeometry = null;
        Func<WindowRemoteInput, bool>? applyInput = null;
        Func<bool>? focusUnavailable = null;
        WindowRoamingConnection? connection = null; Task? send = null, heartbeat = null, controlWriter = null;
        var controls = Channel.CreateBounded<WindowRemoteInput>(new BoundedChannelOptions(64)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _windowControlWriter = controls.Writer;
        var frames = Channel.CreateBounded<WindowCaptureFrame>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }, dropped => dropped.Dispose());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = Guid.NewGuid(); _outgoingWindowSession = session;
        var frameCount = 0; var inputEvents = 0; var focusPaused = false;
        WindowRemoteInput? returning = null;
        string? failure = null;
        var isWindows = OperatingSystem.IsWindows();
        var target = isWindows ? "Mac" : "Windows";
        try
        {
            _windowEdge?.MarkBorrowed(hwnd, true); _macWindowEdge?.MarkBorrowed(hwnd, true);
            WindowRoamingStatus = $"窗口正在跨入 {target}…"; OnPropertyChanged(nameof(CanReturnRoamingWindow));
            void Frame(WindowCaptureFrame frame) { if (!frames.Writer.TryWrite(frame)) frame.Dispose(); }
            void Failed(string reason) { failure = reason; CancelWindowSafely(cancellation); }
            if (isWindows)
            {
                var windowsCapture = new WindowsWindowCapture(); capture = windowsCapture;
                windowsCapture.Frame += Frame; windowsCapture.Failed += Failed;
                await Task.Run(() => windowsCapture.Start(hwnd, 30), cancellation.Token);
                var windowsInput = new WindowsWindowInput(session, hwnd, _inputCapture); input = windowsInput;
                updateGeometry = windowsInput.UpdateFrameGeometry; applyInput = windowsInput.Apply;
                focusUnavailable = () => windowsInput.FocusUnavailable;
            }
            else
            {
                var macCapture = new MacWindowCapture(); capture = macCapture;
                macCapture.Frame += Frame; macCapture.Failed += Failed;
                await Task.Run(() => macCapture.Start(hwnd, 30), cancellation.Token);
            }
            using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            firstTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var first = await frames.Reader.ReadAsync(firstTimeout.Token);
            if (!isWindows)
            {
                // Wait for ScreenCaptureKit's real first frame after native placement
                // settles before binding AX input to that same window.
                var macInput = new MacWindowInput(session, hwnd); input = macInput;
                updateGeometry = macInput.UpdateFrameGeometry; applyInput = macInput.Apply;
                focusUnavailable = () => macInput.FocusUnavailable;
            }
            updateGeometry!(first.Width, first.Height);
            connection = await WindowRoamingConnection.ConnectAsync(peer, Settings.InputPairingSecret, ct: cancellation.Token);
            _windowConnection = connection;
            await connection.SendDescriptorAsync(new WindowDescriptor(session, title.Length > 512 ? title[..512] : title,
                first.Width, first.Height, first.Stride, isWindows ? "Windows" : "Mac", sourceScale, continueDrag,
                Math.Clamp(dragOffsetX, 0, first.Width - 1), Math.Clamp(dragOffsetY, 0, first.Height - 1), entryY), cancellation.Token);
            await connection.SendFrameAsync(new WindowFrame(session, first.Sequence, first.Width, first.Height,
                first.Data.AsSpan(0, first.Length).ToArray()), cancellation.Token);
            // Descriptor and first pixels must precede even a mouse-up queued during capture startup.
            controlWriter = Task.Run(async () =>
            {
                await foreach (var control in controls.Reader.ReadAllAsync(cancellation.Token))
                    await connection.SendInputAsync(control, cancellation.Token);
            }, cancellation.Token);
            _ = controlWriter.ContinueWith(_ => CancelWindowSafely(cancellation), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var activeConnection = connection;
            send = Task.Run(async () =>
            {
                byte[]? lastDigest = null; var lastSent = 0L;
                await foreach (var frame in frames.Reader.ReadAllAsync(cancellation.Token))
                {
                    using (frame)
                    {
                        var digest = SHA256.HashData(frame.Data.AsSpan(0, frame.Length));
                        if (lastDigest is not null && digest.AsSpan().SequenceEqual(lastDigest) &&
                            Environment.TickCount64 - lastSent < 2000) continue;
                        updateGeometry(frame.Width, frame.Height);
                        await activeConnection.SendFrameAsync(new WindowFrame(session, frame.Sequence, frame.Width, frame.Height,
                            frame.Data.AsSpan(0, frame.Length).ToArray()), cancellation.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref frameCount); lastDigest = digest; lastSent = Environment.TickCount64;
                    }
                }
            }, cancellation.Token);
            _ = send.ContinueWith(_ => CancelWindowSafely(cancellation), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            heartbeat = HeartbeatWindowAsync(connection, cancellation.Token);
            using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            readyTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (!cancellation.IsCancellationRequested)
            {
                var message = await connection.ReadAsync(ready.Task.IsCompleted ? cancellation.Token : readyTimeout.Token);
                if (message.Type == WindowMessageType.End) break;
                if (message.Type == WindowMessageType.Heartbeat) continue;
                if (message.Type == WindowMessageType.Ready)
                {
                    if (ready.Task.IsCompleted) continue;
                    ready.TrySetResult();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        WindowRoamingStatus = $"窗口已到 {target}";
                        var stillDragging = continueDrag && (!isWindows || _inputCapture?.IsWindowDragHeld == true);
                        // Sampling held state and queueing start share the hook/UI dispatcher
                        // with release. A later physical up can never overtake this start.
                        if (!controls.Writer.TryWrite(new(session, stillDragging ? "drag-start" : "drag-end")))
                            CancelWindowSafely(cancellation);
                        AppendLog("[窗口] 对端首帧就绪 · 连续拖动=" + stillDragging);
                    });
                }
                else if (message.Type == WindowMessageType.Input && message.Input!.SessionId == session)
                {
                    var command = message.Input;
                    if (command.Kind == "return-drag") { returning = command; continue; }
                    if (command.Kind == "hit-test")
                    {
                        var hit = await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            var result = isWindows ? WindowsWindowInput.HitTest(hwnd, command.X, command.Y)
                                : MacWindowInput.HitTest(hwnd, command.X, command.Y);
                            if (isWindows && result == 2) _inputCapture?.GuardRemoteWindowDrag(true);
                            return result;
                        });
                        await connection.SendInputAsync(new(session, "hit-result", command.Code, Code: hit), cancellation.Token);
                        continue;
                    }
                    if (command.Kind is "drag-start" or "drag-end")
                    {
                        if (isWindows) await Dispatcher.UIThread.InvokeAsync(() =>
                            _inputCapture?.GuardRemoteWindowDrag(command.Kind == "drag-start"));
                        continue;
                    }
                    if (command.Kind == "hit-result") throw new InvalidDataException();
                    var accepted = await Dispatcher.UIThread.InvokeAsync(() => applyInput!(command)); inputEvents++;
                    if (!accepted)
                    {
                        if (!focusUnavailable!()) throw new InvalidOperationException("源窗口无法接收输入");
                        if (!focusPaused) await connection.SendStatusAsync("窗口焦点暂停 · 点击重试", cancellation.Token);
                        focusPaused = true;
                    }
                    else if (focusPaused)
                    { focusPaused = false; await connection.SendStatusAsync(string.Empty, cancellation.Token); }
                }
                else throw new InvalidDataException();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            failure ??= error is UnauthorizedAccessException || (!isWindows && error is InvalidOperationException)
                ? error.Message : error.GetType().Name;
        }
        finally
        {
            _windowControlWriter = null; controls.Writer.TryComplete();
            await cancellation.CancelAsync(); capture?.Dispose(); frames.Writer.TryComplete();
            if (controlWriter is not null) try { await controlWriter; } catch { }
            if (send is not null) try { await send; } catch { }
            while (frames.Reader.TryRead(out var pending)) pending.Dispose();
            if (connection is not null)
            {
                try { using var end = new CancellationTokenSource(500); await connection.SendEndAsync(end.Token); } catch { }
                await connection.DisposeAsync();
            }
            if (heartbeat is not null) try { await heartbeat; } catch { }
            _outgoingWindowSession = Guid.Empty;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                input?.Dispose();
                if (isWindows)
                {
                    _inputCapture?.EndWindowDrag(); _inputCapture?.LeaveRemote();
                    if (returning is null || _windowEdge?.ResumeDrag(hwnd, returning.X, returning.Y) != true)
                        _windowEdge?.Restore(hwnd);
                }
                else
                {
                    if (returning is null || _macWindowEdge?.ResumeDrag(hwnd, returning.X, returning.Y) != true)
                        _macWindowEdge?.Restore(hwnd);
                }
                _windowEdge?.MarkBorrowed(hwnd, false); _macWindowEdge?.MarkBorrowed(hwnd, false);
                WindowRoamingStatus = failure is null ? "窗口已归还 · 继续拖动即可跨屏" : "窗口未能跨屏 · " + failure;
                Interlocked.Exchange(ref _windowBusy, 0); OnPropertyChanged(nameof(CanReturnRoamingWindow));
            });
            if (ReferenceEquals(_windowConnection, connection)) _windowConnection = null;
            if (ReferenceEquals(_windowSessionCancellation, cancellation)) _windowSessionCancellation = null;
            AppendLog($"[窗口] 会话归还 · {frameCount} 帧 / {inputEvents} 次输入" + (failure is null ? "" : " · " + failure));
        }
    }
    private static async Task HeartbeatWindowAsync(WindowRoamingConnection connection, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) await connection.SendHeartbeatAsync(ct).ConfigureAwait(false);
    }
    [RelayCommand] private void ReturnRoamingWindow() => _windowSessionCancellation?.Cancel();
    [RelayCommand] public void RefreshRoamingWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = SelectedRoamingWindow?.Hwnd; RoamingWindows.Clear();
        foreach (var window in WindowsWindowCapture.Enumerate()) RoamingWindows.Add(window);
        SelectedRoamingWindow = RoamingWindows.FirstOrDefault(window => window.Hwnd == previous);
    }
    [RelayCommand] private async Task SendRoamingWindowAsync()
    {
        if (SelectedRoamingWindow is not { } window) return;
        _windowSessionTask = StartWindowHandoffAsync(window.Hwnd, window.Title);
        await _windowSessionTask;
    }
    private static void CancelWindowSafely(CancellationTokenSource cancellation)
    { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
    private async Task StopWindowRoamingAsync()
    {
        _windowEdge?.Dispose(); _windowEdge = null; _macWindowEdge?.Dispose(); _macWindowEdge = null;
        _windowListener?.Stop(); _windowSessionCancellation?.Cancel();
        if (_windowConnection is not null) await _windowConnection.DisposeAsync();
        if (_windowAcceptTask is not null) try { await _windowAcceptTask; } catch { }
        if (_windowSessionTask is not null) try { await _windowSessionTask; } catch { }
    }
}
