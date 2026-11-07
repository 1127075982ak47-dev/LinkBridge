using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LinkBridge.Core.WindowRoaming;

namespace LinkBridge.App.Views;

/// <summary>The complete source window supplies its chrome; the host adds no viewer toolbar.</summary>
public sealed class RoamingWindow : Window
{
    private const string Quartz = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct QuartzPoint { public double X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport(Quartz)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGEventSourceButtonState(int sourceState, uint button);
    [DllImport(Quartz)] private static extern nint CGEventCreate(nint source);
    [DllImport(Quartz)] private static extern QuartzPoint CGEventGetLocation(nint evt);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);

    private sealed class PendingPress
    {
        public required int Id, X, Y;
        public required PixelPoint ScreenPoint;
        public required IPointer Pointer;
        public required long Started;
        public PixelPoint LatestPoint;
        public int ReleaseX, ReleaseY;
        public bool Released;
    }

    private readonly WindowDescriptor _descriptor;
    private readonly Func<bool>? _windowsLeftHeld;
    private readonly Image _image;
    private readonly TextBox _textInput;
    private readonly TextBlock _stateText;
    private readonly Border _statePill;
    private readonly DispatcherTimer _gestureTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Dictionary<Key, int> _pressedKeys = [];
    private readonly HashSet<int> _modifierKeys = [];
    private readonly HashSet<int> _pressedButtons = [];
    private WriteableBitmap? _bitmapA, _bitmapB;
    private WindowFrame? _pendingFrame;
    private PendingPress? _pendingPress;
    private IPointer? _capturedPointer;
    private PixelPoint _lastPointer, _dragStartPointer, _dragStartWindow;
    private double _dragOffsetX, _dragOffsetY, _resizeStartWidth, _resizeStartHeight;
    private bool _dragging, _incomingDrag, _remoteDragRelease, _awaitingIncomingHold, _returnEdgeArmed, _normalDragGuard;
    private bool _resizing, _bufferIndex, _connected = true, _closed, _opened, _settingGeometry, _releasingCapture;
    private long _incomingHoldDeadline, _lastFrameTick, _lastResizeSentTick;
    private int _width, _height, _sequence = -1, _renderQueued, _returning, _hitRequest, _resizeHit;
    private int _lastX, _lastY, _lastResizeWidth, _lastResizeHeight;
    private string? _sourceStatus;
    private bool _arrivalSettled;

    public event Action<WindowRemoteInput>? InputRequested;
    public event Action? ReturnRequested;
    /// <summary>Source pixel anchor and normalized pointer Y, before ReturnRequested.</summary>
    public event Action<int, int, double>? ReturnDragRequested;
    public event Action<WindowFrame>? FramePresented;
    /// <summary>Routing metadata only; never includes typed text or printable key identity.</summary>
    public event Action<string>? Diagnostic;
    public Guid SessionId => _descriptor.SessionId;
    private double SourceScale => Math.Clamp(_descriptor.SourceScale, 0.25, 8);
    private bool SourceIsMac => string.Equals(_descriptor.SourcePlatform, "Mac", StringComparison.OrdinalIgnoreCase);
    // Avalonia.Native 12.1.3 uses global macOS points for Screen/Position (Scaling=1).
    // Windows Position is physical pixels; RenderScaling converts those to local DIPs.
    private double PositionScale => OperatingSystem.IsMacOS() ? 1 : Math.Max(RenderScaling, 0.25);
    private bool CanInteract => !_closed && Volatile.Read(ref _returning) == 0 && _connected && _lastFrameTick != 0;

    public RoamingWindow(WindowDescriptor descriptor, Func<bool>? windowsLeftHeld = null)
    {
        WindowRoamingLimits.Validate(descriptor);
        _descriptor = descriptor; _windowsLeftHeld = windowsLeftHeld; _width = descriptor.Width; _height = descriptor.Height;
        Title = descriptor.Title;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        CanResize = false; // Native source hit tests drive resizing; receiver scaling is never the action.
        Width = descriptor.Width / SourceScale; Height = descriptor.Height / SourceScale;
        MinWidth = 48; MinHeight = 32;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual;
        _image = new Image { Stretch = Stretch.Fill, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch, Cursor = new Cursor(StandardCursorType.Arrow) };
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);
        _textInput = new TextBox { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false,
            AcceptsReturn = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        _stateText = new TextBlock { FontSize = 11, Foreground = Brush.Parse("#F4F7FD"),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 280, MaxHeight = 40,
            TextTrimming = TextTrimming.CharacterEllipsis };
        _statePill = new Border { Child = _stateText, Background = Brush.Parse("#ED24334D"),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6),
            Margin = new Thickness(12), IsVisible = false, IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
        var viewport = new Grid { Background = Brushes.Transparent, ClipToBounds = true };
        viewport.Children.Add(_image); viewport.Children.Add(_textInput); viewport.Children.Add(_statePill);
        Content = viewport;
        _image.PointerPressed += Press;
        _image.PointerMoved += Move;
        _image.PointerReleased += Release;
        _image.PointerWheelChanged += Wheel;
        _image.PointerCaptureLost += (_, _) =>
        {
            if (_releasingCapture) return;
            _pendingPress = null; EndGesture(); ReleaseButtons();
        };
        AddHandler(KeyDownEvent, OnProxyKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnProxyKeyUp, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnProxyText, RoutingStrategies.Tunnel);
        Activated += (_, _) => { if (CanInteract && !_dragging && !_resizing) _textInput.Focus(); };
        Opened += (_, _) => { _opened = true; PlaceAtEntry(); };
        SizeChanged += (_, _) =>
        {
            if (_opened && !_settingGeometry && !_resizing && CanInteract) SendResize(true);
        };
        Deactivated += (_, _) =>
        {
            if (!_dragging && !_resizing) { _pendingPress = null; ReleaseAllInput(); UpdateGestureTimer(); }
        };
        Closing += (_, _) => RequestReturn();
        Closed += (_, _) => Cleanup();
        _gestureTimer.Tick += TickGesture;
    }

    public void SetSourceStatus(string? value)
    {
        void Apply()
        {
            if (_closed) return;
            _sourceStatus = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            UpdateStatePill();
            TraceInput("source-state", 0, _sourceStatus is null ? "active" : "reported");
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    public void SetConnectionState(bool connected)
    {
        void Apply()
        {
            if (_closed) return;
            _connected = connected;
            if (!connected) { _pendingPress = null; EndGesture(); ReleaseAllInput(); }
            UpdateStatePill();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    private void UpdateStatePill()
    {
        var text = !_connected ? "连接已断开 · 窗口正在返回" : _sourceStatus;
        _stateText.Text = text;
        _statePill.IsVisible = !string.IsNullOrEmpty(text);
    }

    public void ApplyFrame(WindowFrame frame)
    {
        if (frame.SessionId != SessionId || _closed || Volatile.Read(ref _returning) != 0) return;
        if (frame.Pixels.Length != WindowRoamingLimits.FrameBytes(frame.Width, frame.Height))
            throw new InvalidDataException("代理窗口帧尺寸与数据不符。");
        Interlocked.Exchange(ref _pendingFrame, frame);
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0) Dispatcher.UIThread.Post(RenderLatest);
    }

    private void RenderLatest()
    {
        var frame = Interlocked.Exchange(ref _pendingFrame, null);
        try
        {
            if (frame is null || _closed || !_connected || Volatile.Read(ref _returning) != 0 || frame.Sequence <= _sequence) return;
            var first = _sequence < 0;
            var changedSize = frame.Width != _width || frame.Height != _height;
            var size = new PixelSize(frame.Width, frame.Height);
            if (_bitmapA?.PixelSize != size)
            {
                ReleaseAllInput();
                _image.Source = null; _bitmapA?.Dispose(); _bitmapB?.Dispose();
                var alpha = SourceIsMac ? AlphaFormat.Premul : AlphaFormat.Opaque;
                _bitmapA = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, alpha);
                _bitmapB = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, alpha);
            }
            _bufferIndex = !_bufferIndex;
            var bitmap = _bufferIndex ? _bitmapA! : _bitmapB!;
            using (var locked = bitmap.Lock())
            {
                var rowBytes = frame.Width * 4;
                if (locked.RowBytes == rowBytes) Marshal.Copy(frame.Pixels, 0, locked.Address, frame.Pixels.Length);
                else for (var row = 0; row < frame.Height; row++)
                    Marshal.Copy(frame.Pixels, row * rowBytes, locked.Address + row * locked.RowBytes, rowBytes);
            }
            _width = frame.Width; _height = frame.Height; _sequence = frame.Sequence;
            if ((first || changedSize) && !_resizing)
            {
                _settingGeometry = true;
                try { Width = _width / SourceScale; Height = _height / SourceScale; }
                finally { _settingGeometry = false; }
            }
            _image.Source = bitmap; _lastFrameTick = Environment.TickCount64;
            if (first && IsActive && !_dragging) _textInput.Focus();
            FramePresented?.Invoke(frame);
        }
        catch
        {
            _connected = false; UpdateStatePill(); ReleaseAllInput(); RequestReturn();
        }
        finally
        {
            Volatile.Write(ref _renderQueued, 0);
            if (!_closed && _pendingFrame is not null && Interlocked.Exchange(ref _renderQueued, 1) == 0)
                Dispatcher.UIThread.Post(RenderLatest);
        }
    }

    private WindowRemoteInput Input(string kind, int x = 0, int y = 0, int code = 0, bool down = false, string? text = null) =>
        new(SessionId, kind, x, y, code, down, text, _width, _height);

    private void Emit(WindowRemoteInput value)
    {
        try { InputRequested?.Invoke(value); }
        catch { _connected = false; UpdateStatePill(); EndGesture(); }
    }

    private bool Coordinates(PointerEventArgs e, bool clamp, out int x, out int y)
    {
        var point = e.GetPosition(_image); var size = _image.Bounds.Size; x = y = 0;
        if (size.Width <= 0 || size.Height <= 0) return false;
        if (!clamp && (point.X < 0 || point.Y < 0 || point.X >= size.Width || point.Y >= size.Height)) return false;
        x = Math.Clamp((int)Math.Floor(point.X * _width / size.Width), 0, _width - 1);
        y = Math.Clamp((int)Math.Floor(point.Y * _height / size.Height), 0, _height - 1);
        _lastX = x; _lastY = y; return true;
    }

    private void Move(object? sender, PointerEventArgs e)
    {
        _lastPointer = _image.PointToScreen(e.GetPosition(_image));
        if (_pendingPress is { } pending) { pending.LatestPoint = _lastPointer; return; }
        if (_dragging || _resizing) { MoveGesture(_lastPointer); e.Handled = true; return; }
        if (CanInteract && Coordinates(e, _pressedButtons.Count != 0, out var x, out var y)) Emit(Input("move", x, y));
    }

    private void Press(object? sender, PointerPressedEventArgs e)
    {
        if (!CanInteract || _dragging || _resizing || !Coordinates(e, false, out var x, out var y)) return;
        if (_pendingPress is { } previous) ApplyHitTest(1, previous.Id);
        var properties = e.GetCurrentPoint(_image).Properties;
        var button = properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => 0, PointerUpdateKind.RightButtonPressed => 1,
            PointerUpdateKind.MiddleButtonPressed => 2, _ => -1,
        };
        if (button < 0) return;
        _lastPointer = _image.PointToScreen(e.GetPosition(_image));
        _textInput.Margin = new Thickness(e.GetPosition(_image).X, e.GetPosition(_image).Y, 0, 0);
        _textInput.Focus(); CapturePointer(e.Pointer);
        if (button == 0 && e.ClickCount == 1 && _pressedButtons.Count == 0)
        {
            // Source native hit test avoids stealing browser tabs/custom title-bar controls.
            var id = _hitRequest = _hitRequest == int.MaxValue ? 1 : _hitRequest + 1;
            _pendingPress = new() { Id = id, X = x, Y = y, ScreenPoint = _lastPointer, LatestPoint = _lastPointer,
                Pointer = e.Pointer, Started = Environment.TickCount64 };
            Emit(Input("hit-test", x, y, id)); UpdateGestureTimer();
        }
        else ForwardPress(button, x, y);
        e.Handled = true;
    }

    /// <summary>The source's HT result, correlated with the original press request.</summary>
    public void ApplyHitTest(int hit, int requestId = 0)
    {
        void Apply()
        {
            if (_pendingPress is not { } pending || (requestId != 0 && pending.Id != requestId)) return;
            _pendingPress = null;
            if (!CanInteract) { ReleasePointer(); UpdateGestureTimer(); return; }
            if (!pending.Released && hit == 2)
            {
                StartDrag(pending.ScreenPoint, pending.X / SourceScale, pending.Y / SourceScale, false, false);
                MoveGesture(pending.LatestPoint);
                TraceInput("source-caption-drag", hit, "native-hit");
            }
            else if (!pending.Released && hit is >= 10 and <= 17)
            {
                StartResize(hit, pending.ScreenPoint); MoveGesture(pending.LatestPoint);
            }
            else
            {
                ForwardPress(0, pending.X, pending.Y);
                if (pending.Released)
                {
                    if (_pressedButtons.Remove(0)) Emit(Input("button", pending.ReleaseX, pending.ReleaseY, 0));
                    ReleasePointer();
                }
                else if (pending.LatestPoint != pending.ScreenPoint && TryCoordinates(pending.LatestPoint, out var x, out var y))
                    Emit(Input("move", x, y));
            }
            UpdateGestureTimer();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    private void ForwardPress(int button, int x, int y)
    {
        _pressedButtons.Add(button); Emit(Input("move", x, y)); Emit(Input("button", x, y, button, true));
    }

    private void Release(object? sender, PointerReleasedEventArgs e)
    {
        _lastPointer = _image.PointToScreen(e.GetPosition(_image)); Coordinates(e, true, out var x, out var y);
        if (_pendingPress is { } pending)
        {
            pending.Released = true; pending.ReleaseX = x; pending.ReleaseY = y; pending.LatestPoint = _lastPointer;
            ReleasePointer(); e.Handled = true; return;
        }
        if (_dragging || _resizing)
        {
            MoveGesture(_lastPointer, false); EndGesture(); e.Handled = true; return;
        }
        var button = e.InitialPressMouseButton switch { MouseButton.Left => 0, MouseButton.Right => 1, MouseButton.Middle => 2, _ => -1 };
        if (button >= 0 && _pressedButtons.Remove(button)) Emit(Input("button", x, y, button));
        if (_pressedButtons.Count == 0) ReleasePointer();
        e.Handled = true;
    }

    private void Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (!CanInteract || _pendingPress is not null || _dragging || _resizing || !Coordinates(e, false, out var x, out var y)) return;
        var delta = Math.Clamp((int)Math.Round(e.Delta.Y * 120), -12000, 12000);
        if (delta != 0) Emit(Input("wheel", x, y, delta));
        e.Handled = true;
    }

    /// <summary>After KVM pointer handoff; remoteRelease waits for the authenticated drag-end packet.</summary>
    public void BeginIncomingDrag(bool remoteRelease = false)
    {
        void Apply()
        {
            if (!CanInteract) return;
            _pendingPress = null;
            var pointer = TryReadPointer(out var actual, out _) ? actual : EntryPointer();
            StartDrag(pointer, _descriptor.DragOffsetX / SourceScale, _descriptor.DragOffsetY / SourceScale, true, remoteRelease);
            _awaitingIncomingHold = !remoteRelease; _incomingHoldDeadline = Environment.TickCount64 + 750;
            MoveGesture(pointer); Activate();
            TraceInput("incoming-drag", 0, remoteRelease ? "remote-release" : "native-release");
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    public void EndIncomingDrag()
    {
        void Apply()
        {
            if (_arrivalSettled && !_incomingDrag) return;
            if (_incomingDrag && TryReadPointer(out var pointer, out _)) MoveGesture(pointer, allowReturn: false);
            else if (!_arrivalSettled && _descriptor.ContinueDrag)
            {
                var point = TryReadPointer(out var actual, out _) ? actual : EntryPointer();
                Position = new PixelPoint(point.X - (int)Math.Round(_descriptor.DragOffsetX / SourceScale * PositionScale),
                    point.Y - (int)Math.Round(_descriptor.DragOffsetY / SourceScale * PositionScale));
            }
            EndGesture();
            // A release during first-frame startup must leave a usable title bar and content.
            var area = (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.WorkingArea;
            if (area is { } work)
            {
                var width = (int)Math.Ceiling(Width * PositionScale);
                var height = (int)Math.Ceiling(Height * PositionScale);
                Position = new PixelPoint(Math.Clamp(Position.X, work.X, Math.Max(work.X, work.Right - width)),
                    Math.Clamp(Position.Y, work.Y, Math.Max(work.Y, work.Bottom - height)));
            }
            _arrivalSettled = true;
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    private void StartDrag(PixelPoint pointer, double offsetX, double offsetY, bool incoming, bool remoteRelease)
    {
        ReleaseAllInput();
        _dragging = true; _incomingDrag = incoming; _remoteDragRelease = remoteRelease; _awaitingIncomingHold = false;
        _returnEdgeArmed = !incoming;
        _dragStartPointer = pointer; _dragStartWindow = Position; _dragOffsetX = offsetX; _dragOffsetY = offsetY;
        // A fresh receiver caption gesture must hold KVM ownership before reaching the return edge.
        // The initial incoming drag is already guarded by the source and must never echo a second start.
        _normalDragGuard = !incoming;
        if (_normalDragGuard) Emit(Input("drag-start"));
        UpdateGestureTimer();
    }

    private void StartResize(int hit, PixelPoint pointer)
    {
        ReleaseAllInput();
        _resizing = true; _resizeHit = hit;
        _dragStartPointer = pointer; _dragStartWindow = Position; _resizeStartWidth = Width; _resizeStartHeight = Height;
        _lastResizeWidth = _lastResizeHeight = 0; UpdateGestureTimer();
        TraceInput("source-resize", hit, "native-hit");
    }

    private void MoveGesture(PixelPoint pointer, bool allowReturn = true)
    {
        if (!CanInteract) { EndGesture(); return; }
        _lastPointer = pointer;
        if (_dragging)
        {
            var factor = PositionScale;
            if (_incomingDrag)
                Position = new PixelPoint(pointer.X - (int)Math.Round(_dragOffsetX * factor),
                    pointer.Y - (int)Math.Round(_dragOffsetY * factor));
            else
                Position = new PixelPoint(_dragStartWindow.X + pointer.X - _dragStartPointer.X,
                    _dragStartWindow.Y + pointer.Y - _dragStartPointer.Y);
            // Arrival starts exactly on this edge; first move inward before a return can be armed.
            if (!_returnEdgeArmed && DistanceFromSourceEdge(pointer) > 12) _returnEdgeArmed = true;
            if (allowReturn && _returnEdgeArmed && IsAtSourceEdge(pointer)) RequestReturn(true);
        }
        else if (_resizing)
        {
            var factor = PositionScale;
            var dx = (pointer.X - _dragStartPointer.X) / factor; var dy = (pointer.Y - _dragStartPointer.Y) / factor;
            var left = _resizeHit is 10 or 13 or 16; var right = _resizeHit is 11 or 14 or 17;
            var top = _resizeHit is 12 or 13 or 14; var bottom = _resizeHit is 15 or 16 or 17;
            var width = ClampResize(_resizeStartWidth + (left ? -dx : right ? dx : 0), MinWidth, _resizeStartHeight);
            var height = ClampResize(_resizeStartHeight + (top ? -dy : bottom ? dy : 0), MinHeight, width);
            _settingGeometry = true;
            try
            {
                Width = width; Height = height;
                Position = new PixelPoint(_dragStartWindow.X + (left ? (int)Math.Round((_resizeStartWidth - width) * factor) : 0),
                    _dragStartWindow.Y + (top ? (int)Math.Round((_resizeStartHeight - height) * factor) : 0));
            }
            finally { _settingGeometry = false; }
            SendResize(false);
        }
    }

    private double ClampResize(double value, double minimum, double otherDimension) =>
        Math.Clamp(value, minimum, Math.Max(minimum, Math.Min(8192d / SourceScale,
            WindowRoamingLimits.MaximumPixelsBytes / (4d * Math.Max(otherDimension * SourceScale, 1) * SourceScale))));

    private void SendResize(bool force)
    {
        if (!CanInteract) return;
        var now = Environment.TickCount64;
        if (!force && now - _lastResizeSentTick < 50) return;
        var width = Math.Clamp((int)Math.Round(Width * SourceScale), 1, 8192);
        var height = Math.Clamp((int)Math.Round(Height * SourceScale), 1, 8192);
        if ((long)width * height * 4 > WindowRoamingLimits.MaximumPixelsBytes)
            height = Math.Max(1, WindowRoamingLimits.MaximumPixelsBytes / (4 * width));
        // Layout notifications can arrive after the frame's geometry guard has unwound.
        // A source-sized client area is already acknowledged and needs no round trip.
        if (width == _width && height == _height) return;
        if (width == _lastResizeWidth && height == _lastResizeHeight) return;
        _lastResizeWidth = width; _lastResizeHeight = height; _lastResizeSentTick = now;
        Emit(new WindowRemoteInput(SessionId, "resize", Width: width, Height: height));
    }

    private void EndGesture()
    {
        var resized = _resizing;
        var releaseDragGuard = _normalDragGuard;
        _normalDragGuard = false;
        _dragging = _incomingDrag = _remoteDragRelease = _awaitingIncomingHold = _resizing = false;
        // return-drag must keep the guard until source session teardown restores its continued move.
        if (releaseDragGuard && _connected && Volatile.Read(ref _returning) == 0) Emit(Input("drag-end"));
        if (resized) SendResize(true);
        ReleasePointer(); UpdateGestureTimer();
        if (CanInteract && IsActive) _textInput.Focus();
    }

    private void TickGesture(object? sender, EventArgs args)
    {
        if (_closed || !CanInteract) { _pendingPress = null; EndGesture(); return; }
        if (_pendingPress is { } pending && Environment.TickCount64 - pending.Started >= 250)
            ApplyHitTest(1, pending.Id);
        if (!_dragging && !_resizing) { UpdateGestureTimer(); return; }
        if (!TryReadPointer(out var pointer, out var held))
        {
            if (_incomingDrag && !_remoteDragRelease && Environment.TickCount64 >= _incomingHoldDeadline) EndIncomingDrag();
            return;
        }
        if (_awaitingIncomingHold)
        {
            if (held) _awaitingIncomingHold = false;
            else if (Environment.TickCount64 >= _incomingHoldDeadline) { EndIncomingDrag(); return; }
            else return;
        }
        if (!_remoteDragRelease && !held)
        {
            if (_incomingDrag) EndIncomingDrag(); else EndGesture();
            return;
        }
        MoveGesture(pointer);
    }

    private void UpdateGestureTimer()
    {
        if (!_closed && (_pendingPress is not null || _dragging || _resizing)) _gestureTimer.Start();
        else _gestureTimer.Stop();
    }

    private void CapturePointer(IPointer pointer) { _capturedPointer = pointer; pointer.Capture(_image); }
    private void ReleasePointer()
    {
        var pointer = _capturedPointer; _capturedPointer = null; _releasingCapture = true;
        try { pointer?.Capture(null); } finally { _releasingCapture = false; }
    }

    private bool TryCoordinates(PixelPoint point, out int x, out int y)
    {
        x = y = 0; var size = _image.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return false;
        var local = _image.PointToClient(point);
        x = Math.Clamp((int)Math.Floor(local.X * _width / size.Width), 0, _width - 1);
        y = Math.Clamp((int)Math.Floor(local.Y * _height / size.Height), 0, _height - 1);
        _lastX = x; _lastY = y; return true;
    }

    private bool TryReadPointer(out PixelPoint point, out bool held)
    {
        point = _lastPointer; held = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                held = _windowsLeftHeld?.Invoke() ?? (GetAsyncKeyState(1) & 0x8000) != 0;
                if (!GetCursorPos(out var native)) return false;
                point = new PixelPoint(native.X, native.Y); return true;
            }
            if (OperatingSystem.IsMacOS())
            {
                held = CGEventSourceButtonState(0, 0); var evt = CGEventCreate(0);
                if (evt == 0) return false;
                try
                {
                    var native = CGEventGetLocation(evt);
                    if (!double.IsFinite(native.X) || !double.IsFinite(native.Y)) return false;
                    point = new PixelPoint((int)Math.Round(native.X), (int)Math.Round(native.Y)); return true;
                }
                finally { CFRelease(evt); }
            }
        }
        catch { }
        return false;
    }

    private PixelPoint EntryPointer()
    {
        var screen = Screens.Primary ?? Screens.ScreenFromWindow(this);
        if (screen is null) return Position;
        return new PixelPoint(SourceIsMac ? screen.Bounds.Right - 1 : screen.Bounds.X + 1,
            screen.Bounds.Y + (int)Math.Round(Math.Clamp(_descriptor.EntryY, 0, 1) * Math.Max(screen.Bounds.Height - 1, 1)));
    }

    private void PlaceAtEntry()
    {
        var screen = Screens.Primary ?? Screens.ScreenFromWindow(this);
        if (screen is null) return;
        _settingGeometry = true;
        try
        {
            if (_descriptor.ContinueDrag)
            {
                var entry = EntryPointer();
                Position = new PixelPoint(entry.X - (int)Math.Round(_descriptor.DragOffsetX / SourceScale * PositionScale),
                    entry.Y - (int)Math.Round(_descriptor.DragOffsetY / SourceScale * PositionScale));
            }
            else
            {
                var width = (int)Math.Ceiling(Width * PositionScale); var height = (int)Math.Ceiling(Height * PositionScale);
                var x = SourceIsMac ? screen.WorkingArea.Right - width - 32 : screen.WorkingArea.X + 32;
                Position = new PixelPoint(Math.Max(screen.WorkingArea.X, x),
                    screen.WorkingArea.Y + Math.Max(0, (screen.WorkingArea.Height - height) / 3));
            }
        }
        finally { _settingGeometry = false; }
    }

    private bool IsAtSourceEdge(PixelPoint pointer)
    {
        return DistanceFromSourceEdge(pointer) <= 2;
    }

    private double DistanceFromSourceEdge(PixelPoint pointer)
    {
        var screen = Screens.ScreenFromPoint(pointer) ?? Screens.ScreenFromWindow(this) ?? Screens.Primary;
        return screen is null ? double.PositiveInfinity :
            SourceIsMac ? screen.Bounds.Right - 1 - pointer.X : pointer.X - screen.Bounds.X;
    }

    private void OnProxyKeyDown(object? sender, KeyEventArgs e)
    {
        ReconcileModifiers(e.KeyModifiers);
        TraceInput("key-down", IsPrintable(e.Key) ? 0 : VirtualKey(e.Key),
            $"type={(IsPrintable(e.Key) ? "printable" : "control")} modifiers={e.KeyModifiers}");
        if (e.Key == Key.M && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        { RequestReturn(); e.Handled = true; return; }
        if (!CanInteract) { e.Handled = true; return; }
        if (IsModifier(e.Key)) { e.Handled = true; return; }
        var shortcut = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        if (IsPrintable(e.Key) && !shortcut) return; // Let the native IME produce only committed text.
        var code = VirtualKey(e.Key);
        if (code == 0) return;
        _pressedKeys[e.Key] = code;
        Emit(Input("key", code: code, down: true)); e.Handled = true;
    }
    private void OnProxyKeyUp(object? sender, KeyEventArgs e)
    {
        ReconcileModifiers(e.KeyModifiers);
        if (IsModifier(e.Key)) { e.Handled = true; return; }
        TraceInput("key-up", IsPrintable(e.Key) ? 0 : VirtualKey(e.Key), $"modifiers={e.KeyModifiers}");
        if (_pressedKeys.Remove(e.Key, out var code) && !_pressedKeys.Values.Contains(code))
        { Emit(Input("key", code: code)); e.Handled = true; }
    }
    private void OnProxyText(object? sender, TextInputEventArgs e)
    {
        TraceInput("text-commit", 0, $"length={e.Text?.Length ?? 0} permitted={CanInteract}");
        if (!CanInteract || string.IsNullOrEmpty(e.Text)) return;
        if (_modifierKeys.Any(code => code is 0x11 or 0x12))
        { TraceInput("text-blocked-modifier", 0, "shortcut-held"); e.Handled = true; return; }
        if (e.Text.Length <= 4096) Emit(Input("text", text: e.Text));
        e.Handled = true;
    }

    private void TraceInput(string phase, int code, string detail)
    {
        try { Diagnostic?.Invoke($"{phase} code={code} active={IsActive} imeFocused={_textInput.IsFocused} held={_pressedKeys.Count} {detail}"); }
        catch { }
    }

    private void ReconcileModifiers(KeyModifiers modifiers)
    {
        if (!CanInteract) return;
        // AppKit can report Command/Control only as event flags, without a separate
        // key-down. Reconcile both directions before sending the actual shortcut key.
        foreach (var (code, down) in new[] {
            (0x11, (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0),
            (0x12, modifiers.HasFlag(KeyModifiers.Alt)),
            (0x10, modifiers.HasFlag(KeyModifiers.Shift)) })
        {
            if (down ? _modifierKeys.Add(code) : _modifierKeys.Remove(code))
                Emit(Input("key", code: code, down: down));
        }
    }
    private static bool IsModifier(Key key) => key is Key.LWin or Key.RWin or Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift;
    private static bool IsPrintable(Key key) => key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or
        >= Key.NumPad0 and <= Key.NumPad9 or Key.Space or Key.Oem1 or Key.OemPlus or Key.OemComma or Key.OemMinus or
        Key.OemPeriod or Key.Oem2 or Key.Oem3 or Key.Oem4 or Key.Oem5 or Key.Oem6 or Key.Oem7 or Key.Oem8 or
        Key.Add or Key.Subtract or Key.Multiply or Key.Divide or Key.Decimal;
    private static int VirtualKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return 0x41 + key - Key.A;
        if (key is >= Key.D0 and <= Key.D9) return 0x30 + key - Key.D0;
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return 0x60 + key - Key.NumPad0;
        if (key is >= Key.F1 and <= Key.F24) return 0x70 + key - Key.F1;
        return key switch
        {
            Key.Back => 0x08, Key.Tab => 0x09, Key.Enter => 0x0D, Key.Pause => 0x13, Key.CapsLock => 0x14,
            Key.Escape => 0x1B, Key.Space => 0x20, Key.PageUp => 0x21, Key.PageDown => 0x22, Key.End => 0x23,
            Key.Home => 0x24, Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28,
            Key.PrintScreen => 0x2C, Key.Insert => 0x2D, Key.Delete => 0x2E,
            Key.LWin or Key.RWin => 0x11, // Mac Command shortcuts become Windows Control shortcuts.
            Key.LeftCtrl => 0xA2, Key.RightCtrl => 0xA3, Key.LeftAlt => 0xA4, Key.RightAlt => 0xA5,
            Key.LeftShift => 0xA0, Key.RightShift => 0xA1, Key.NumLock => 0x90, Key.Scroll => 0x91,
            Key.Multiply => 0x6A, Key.Add => 0x6B, Key.Subtract => 0x6D, Key.Decimal => 0x6E, Key.Divide => 0x6F,
            Key.Oem1 => 0xBA, Key.OemPlus => 0xBB, Key.OemComma => 0xBC, Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE, Key.Oem2 => 0xBF, Key.Oem3 => 0xC0, Key.Oem4 => 0xDB,
            Key.Oem5 => 0xDC, Key.Oem6 => 0xDD, Key.Oem7 => 0xDE, Key.Oem8 => 0xDF, _ => 0,
        };
    }

    private void ReleaseButtons()
    {
        var buttons = _pressedButtons.ToArray(); _pressedButtons.Clear();
        foreach (var code in buttons) Emit(Input("button", _lastX, _lastY, code));
        ReleasePointer();
    }

    private void ReleaseAllInput()
    {
        var hadInput = _pressedKeys.Count != 0 || _pressedButtons.Count != 0 || _modifierKeys.Count != 0;
        var keys = _pressedKeys.Values.Concat(_modifierKeys).Distinct().ToArray(); _pressedKeys.Clear(); _modifierKeys.Clear();
        foreach (var code in keys) Emit(Input("key", code: code));
        ReleaseButtons();
        if (hadInput) Emit(Input("release"));
    }

    private void RequestReturn(bool continuingDrag = false)
    {
        if (Interlocked.Exchange(ref _returning, 1) != 0) return;
        _pendingPress = null;
        if (continuingDrag)
        {
            var screen = Screens.ScreenFromPoint(_lastPointer) ?? Screens.Primary;
            var entryY = screen is null ? 0.5 : Math.Clamp(
                (_lastPointer.Y - screen.Bounds.Y) / (double)Math.Max(screen.Bounds.Height - 1, 1), 0, 1);
            var offsetX = Math.Clamp((int)Math.Round(_dragOffsetX * SourceScale), 0, _width - 1);
            var offsetY = Math.Clamp((int)Math.Round(_dragOffsetY * SourceScale), 0, _height - 1);
            try { ReturnDragRequested?.Invoke(offsetX, offsetY, entryY); } catch { }
        }
        EndGesture(); ReleaseAllInput(); Emit(Input("release"));
        try { ReturnRequested?.Invoke(); } catch { }
    }

    private void Cleanup()
    {
        if (_closed) return;
        RequestReturn(); _closed = true;
        _gestureTimer.Stop(); _gestureTimer.Tick -= TickGesture;
        Interlocked.Exchange(ref _pendingFrame, null); _image.Source = null;
        _bitmapA?.Dispose(); _bitmapB?.Dispose(); _bitmapA = _bitmapB = null;
    }
}
