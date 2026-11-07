using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using LinkBridge.App.ViewModels;
using LinkBridge.App.Services;
using LinkBridge.Input;
using LinkBridge.App.Controls;
using Avalonia.Threading;
using Avalonia.Platform.Storage;

namespace LinkBridge.App.Views;

public partial class MainWindow : Window
{
    private bool _closing, _canClose;
    private Border? _layoutPanel;
    private bool _adjustingSize, _geometryQueued, _sliderResizing, _resizePreview;
    private double _previewBaseSide, _previewSide;
    private readonly ScaleTransform _previewTransform = new(1, 1);
    private PixelRect? _lastRoundRegion;
    private bool _fixedWindowsHost;
    private double _workspaceSide = 640;
    private double WorkspaceSide => _fixedWindowsHost ? _workspaceSide : Width;
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double? _pendingSide;
    private Point? _resizeCenter;
    private IPointer? _resizePointer;
    private PixelPoint _resizeStartPointer;
    private double _resizeStartSide, _resizeScale;
    private bool _updatingWorkArea, _syncingSizeControl;
    private PixelRect? _lastWorkArea;
    private double _lastWorkAreaScaling;
    private DispatcherTimer? _macAccessTimer;
    private bool? _lastMacAccess;
    private readonly List<(CompositionVisual Visual, Vector3D Offset)> _motion = [];
    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DataContext is MainViewModel { CanSendFiles: true } && e.DataTransfer.TryGetFiles()?.Any() == true
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath())
                .Where(path => path is not null).Cast<string>().ToArray();
            if (paths is { Length: > 0 } && DataContext is MainViewModel model) model.SendFilesToPeer(paths);
            e.Handled = true;
        });
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += async (_, _) => { _macAccessTimer?.Stop(); _resizeTimer.Stop(); _screenLayoutTimer.Stop(); _screenLayoutEditor?.Close(); if(_screenLayoutServer is not null)await _screenLayoutServer.DisposeAsync(); };
        _resizeTimer.Tick += (_, _) => ApplyPendingResize();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == WindowStateProperty && WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                SetWorkspaceSize(double.IsFinite(MaxWidth) ? MaxWidth : 900);
            }
            if (change.Property == IsVisibleProperty || change.Property == WindowStateProperty)
                UpdatePreviewVisibility();
        };
        SizeChanged += (_, _) => QueueGeometryRefresh();
        PositionChanged += (_, _) => { UpdateWorkArea(); QueueGeometryRefresh(); };
        ScalingChanged += (_, _) => UpdateWorkArea();
        Screens.Changed += (_, _) => UpdateWorkArea();
        ResizeGrip.AddHandler(PointerPressedEvent, ResizeWorkspace, RoutingStrategies.Tunnel);
        ResizeGrip.PointerMoved += MoveWorkspaceResize;
        ResizeGrip.PointerReleased += EndWorkspaceResize;
        ResizeGrip.PointerCaptureLost += (_, _) => FinishWorkspaceResize();
        WorkspaceSizeSlider.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            ApplyPendingResize();
            _resizeCenter = GetWorkspaceCenter();
            _sliderResizing = true;
            BeginResizePreview();
        }, RoutingStrategies.Tunnel);
        WorkspaceSizeSlider.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _sliderResizing = false;
            ApplyPendingResize();
            FinishResizePreview();
            _resizeCenter = null;
            if (IsActive) StartSceneMotion();
        }, RoutingStrategies.Tunnel);
        WorkspaceSizeSlider.PointerCaptureLost += (_, _) =>
        {
            if (!_sliderResizing) return;
            _sliderResizing = false;
            ApplyPendingResize();
            FinishResizePreview();
            _resizeCenter = null;
            if (IsActive) StartSceneMotion();
        };
        WorkspaceSizeSlider.PropertyChanged += (_, change) =>
        {
            if (change.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && !_syncingSizeControl)
                SetWorkspaceSize(WorkspaceSizeSlider.Value);
        };
        Activated += (_, _) => StartSceneMotion();
        Deactivated += (_, _) => StopSceneMotion();
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseButton.Click += (_, _) => Close();
        DevicesButton.Click += (_, _) => ToggleClipboard();
        LayoutButton.Click += (_, _) => ShowScreenLayoutEditor();
        ReduceMotionToggle.PropertyChanged += (_, change) =>
        {
            if (change.Property == Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty)
            {
                StopSceneMotion();
                if (ReduceMotionToggle.IsChecked != true && IsActive) StartSceneMotion();
            }
        };
        AddHandler(PointerPressedEvent, DragWorkspace, RoutingStrategies.Bubble);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { ClosePanels(); e.Handled = true; }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if(e.Key==Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Shift)){ShowScreenLayoutEditor();e.Handled=true;}
                else if (e.Key is Key.OemPlus or Key.Add) { SetWorkspaceSize((_resizePreview ? _previewSide : WorkspaceSide) + 32); e.Handled = true; }
                else if (e.Key is Key.OemMinus or Key.Subtract) { SetWorkspaceSize((_resizePreview ? _previewSide : WorkspaceSide) - 32); e.Handled = true; }
                else if (e.Key == Key.D0) { SetWorkspaceSize(640); e.Handled = true; }
            }
        };
        if (Environment.GetCommandLineArgs().Contains("--topmost")) Topmost = true;
    }
    private async void OnOpened(object? sender, EventArgs e)
    {
        UpdateWorkArea();
        InitializeFixedWindowsHost();
        UpdateRoundWindow();
        UpdateOverlayLayout();
        if (DataContext is not MainViewModel vm) return;
        if (vm.Workspace.Enabled || Environment.GetCommandLineArgs().Contains("--workspace-ui"))
            Dispatcher.UIThread.Post(ShowWorkspaceEditor);
        UpdatePreviewVisibility();
        vm.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(MainViewModel.HasPrimaryCamera) or nameof(MainViewModel.IsSettingsOpen))
                UpdatePreviewVisibility();
            if (change.PropertyName == nameof(MainViewModel.IsSettingsOpen) && !vm.IsSettingsOpen)
            {
                StopSceneMotion();
                if (IsActive) StartSceneMotion();
            }
        };
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                RefreshMacAccess();
                _macAccessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _macAccessTimer.Tick += (_, _) => RefreshMacAccess();
                _macAccessTimer.Start();
                if (vm.Settings.EnableInputServer && _lastMacAccess == false)
                    ShowDeviceSetup(this, new RoutedEventArgs());
            }
        }
        catch (Exception ex) { vm.ReportWindowStartupError(ex); }
        try
        {
            StartScreenLayoutService(vm);
            await vm.InitializeAsync();
            UpdatePreviewVisibility();
            if (IsActive) StartSceneMotion();
            var snapshot = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--snapshot=", StringComparison.Ordinal));
            if (snapshot is not null)
            {
                await System.Threading.Tasks.Task.Delay(500);
                using var rendered = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1254, 1254), new Vector(96, 96));
                rendered.Render(CircularSurface);
                rendered.Save(System.IO.Path.GetFullPath(snapshot[11..]), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        catch (Exception ex) { vm.ReportWindowStartupError(ex); }
    }

    private void RefreshMacAccess()
    {
        if (_closing || !OperatingSystem.IsMacOS() || DataContext is not MainViewModel vm) return;
        SetupMacAccessState.IsVisible = true;
        try
        {
            var allowed = MacInputInjector.IsTrusted();
            if (_lastMacAccess == allowed) return;
            _lastMacAccess = allowed;
            SetupMacAccessState.Text = allowed ? "设备控制已获准 · 连接状态以上方为准" : "设备控制尚未获准";
            vm.ReportMacInputAccess(allowed);
        }
        catch (Exception ex) { SetupMacAccessState.Text = "权限状态读取失败：" + ex.Message; }
    }

    private void UpdatePreviewVisibility()
    {
        if (DataContext is MainViewModel vm)
            vm.SetPreviewVisible(!_closing && IsVisible && WindowState != WindowState.Minimized
                && (vm.HasPrimaryCamera || vm.IsSettingsOpen));
    }

    private void StartSceneMotion()
    {
        if (_closing || _resizePreview || _resizePointer is not null || _sliderResizing || _resizeTimer.IsEnabled || _motion.Count != 0 || (DataContext as MainViewModel)?.Settings.ReduceMotion == true) return;
        var index = 0;
        foreach (var node in this.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("pod") && b.Width >= 140 && b.IsEffectivelyVisible))
        {
            var visual = ElementComposition.GetElementVisual(node);
            if (visual is null) continue;
            var origin = visual.Offset;
            var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
            animation.Duration = TimeSpan.FromSeconds(4.8 + index % 3 * .45);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            for (var step = 0; step <= 16; step++)
                animation.InsertKeyFrame(step / 16f, origin + new Vector3D(0, Math.Sin(step * Math.PI / 8) * (index % 2 == 0 ? 3 : -3), 0));
            visual.StartAnimation("Offset", animation);
            _motion.Add((visual, origin)); index++;
        }
    }
    private void StopSceneMotion()
    {
        foreach (var item in _motion) { item.Visual.StopAnimation("Offset"); item.Visual.Offset = item.Offset; }
        _motion.Clear();
    }

    private void UpdateRoundWindow()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        if (!GetClientRect(hwnd, out var area)) return;
        var diameter = _fixedWindowsHost
            ? Math.Min(Math.Min(area.Right, area.Bottom), (int)Math.Round((_resizePreview ? _previewSide : _workspaceSide) * RenderScaling))
            : Math.Min(area.Right, area.Bottom);
        var left = (area.Right - diameter) / 2; var top = (area.Bottom - diameter) / 2;
        var regionBounds = new PixelRect(left, top, diameter, diameter);
        if (_lastRoundRegion == regionBounds) return;
        var region = CreateEllipticRgn(left, top, left + diameter, top + diameter);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
        else _lastRoundRegion = regionBounds;
    }

    private void UpdateWorkArea()
    {
        if (_updatingWorkArea || !IsVisible) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null || (_lastWorkArea == screen.WorkingArea && Math.Abs(_lastWorkAreaScaling - screen.Scaling) < .001)) return;
        _updatingWorkArea = true;
        try
        {
            _lastWorkArea = screen.WorkingArea;
            var scale = screen.Scaling;
            _lastWorkAreaScaling = scale;
            var side = Math.Min(screen.WorkingArea.Width, screen.WorkingArea.Height) / scale - 8;
            var currentCenter = GetWorkspaceCenter();
            MinWidth = MinHeight = Math.Min(480, side);
            MaxWidth = MaxHeight = side;
            var bounded = Math.Min(Math.Max(WorkspaceSide, MinWidth), side);
            if (_fixedWindowsHost)
            {
                _workspaceSide = bounded;
                ResizeFixedHost(side, currentCenter);
                SetFixedViewportSize(bounded);
            }
            else ApplyWorkspaceSize(bounded, currentCenter);
            ClampPositionToWorkArea();
            UpdateRoundWindow();
            UpdateOverlayLayout();
        }
        finally { _updatingWorkArea = false; }
    }

    private void ClampPositionToWorkArea()
    {
        if (!IsVisible || WindowState != WindowState.Normal) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var center = GetWorkspaceCenter();
        var visibleSide = _resizePreview ? _previewSide : WorkspaceSide;
        var half = visibleSide * screen.Scaling / 2;
        var x = Math.Clamp(center.X, area.X + half, Math.Max(area.X + half, area.Right - half));
        var y = Math.Clamp(center.Y, area.Y + half, Math.Max(area.Y + half, area.Bottom - half));
        var position = new PixelPoint(Position.X + (int)Math.Round(x - center.X),
            Position.Y + (int)Math.Round(y - center.Y));
        if (Position != position) Position = position;
    }

    private void UpdateOverlayLayout()
    {
        var side = _resizePreview ? _previewSide : _fixedWindowsHost ? _workspaceSide : Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) side = WorkspaceSide;
        // Logical-font pages stay centered independently of the illustration's preview transform.
        OverlayCanvas.Width = OverlayCanvas.Height = side;
        OverlayCanvas.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        OverlayCanvas.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        var offsetX = 0d;
        var offsetY = 0d;
        foreach (var panel in new[] { DevicesFlyout, AudioFlyout, SettingsFlyout, SetupFlyout, SizeFlyout, ClipboardFlyout, _layoutPanel })
        {
            if (panel is null || !panel.IsVisible) continue;
            var diameter = Math.Min(1100, Math.Max(0, side - 20));
            panel.Width = panel.Height = diameter;
            panel.Padding = new Thickness(0);
            panel.CornerRadius = new CornerRadius(diameter / 2);
            var clipRect = new Rect(0, 0, diameter, diameter);
            if (panel.Clip is not EllipseGeometry ellipse || ellipse.Rect != clipRect)
                panel.Clip = new EllipseGeometry(clipRect);
            if (panel.Child is Panel layers)
                foreach (var content in layers.Children.OfType<Grid>())
                    content.Margin = new Thickness(diameter * .16);
            Canvas.SetLeft(panel, offsetX + (side - panel.Width) / 2);
            Canvas.SetTop(panel, offsetY + (side - panel.Height) / 2);
        }
        // At 4 o'clock, inside the circular edge and clear of the speaker node.
        Canvas.SetLeft(ResizeGrip, offsetX + side * .88 - ResizeGrip.Width / 2);
        Canvas.SetTop(ResizeGrip, offsetY + side * .74 - ResizeGrip.Height / 2);
        _syncingSizeControl = true;
        try
        {
            WorkspaceSizeSlider.Minimum = MinWidth;
            WorkspaceSizeSlider.Maximum = GetCenteredMaximum(_resizeCenter ?? GetWorkspaceCenter());
            var displaySide = _resizePreview ? _previewSide : WorkspaceSide;
            WorkspaceSizeSlider.Value = displaySide;
            WorkspaceSizeLabel.Text = $"{displaySide:0} × {displaySide:0}";
        }
        finally { _syncingSizeControl = false; }
    }
    private void QueueGeometryRefresh()
    {
        if (_geometryQueued || _closing) return;
        _geometryQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _geometryQueued = false;
            if (_closing) return;
            if (!_resizePreview || _fixedWindowsHost) UpdateRoundWindow();
            UpdateOverlayLayout();
        }, DispatcherPriority.Render);
    }
    private Point GetWorkspaceCenter()
    {
        var scale = Screens.ScreenFromWindow(this)?.Scaling ?? RenderScaling;
        return new Point(Position.X + Width * scale / 2, Position.Y + Height * scale / 2);
    }
    private double GetCenteredMaximum(Point center)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return double.IsFinite(MaxWidth) ? MaxWidth : 1200;
        var area = screen.WorkingArea;
        var distance = Math.Min(Math.Min(center.X - area.X, area.Right - center.X),
            Math.Min(center.Y - area.Y, area.Bottom - center.Y));
        return Math.Max(MinWidth, Math.Min(MaxWidth, Math.Floor(distance * 2 / screen.Scaling)));
    }
    internal static PixelPoint CenteredPosition(Point center, double side, double scale) =>
        new((int)Math.Round(center.X - side * scale / 2), (int)Math.Round(center.Y - side * scale / 2));
    private void SetWorkspaceSize(double side)
    {
        if (_closing) return;
        _resizeCenter ??= GetWorkspaceCenter();
        _pendingSide = Math.Clamp(side, MinWidth, GetCenteredMaximum(_resizeCenter.Value));
        if (!_resizePreview) StopSceneMotion();
        _resizeTimer.Start();
    }
    private void ApplyPendingResize()
    {
        var pending = _pendingSide;
        _pendingSide = null;
        if (pending is { } side && _resizeCenter is { } center && !_closing)
        {
            if (_resizePreview) UpdateResizePreview(side);
            else ApplyWorkspaceSize(side, center);
        }
        if (_pendingSide is null)
        {
            _resizeTimer.Stop();
            if (_resizePointer is null && !_sliderResizing && !_resizePreview)
            {
                _resizeCenter = null;
                if (IsActive) StartSceneMotion();
            }
        }
    }
    private void BeginResizePreview()
    {
        if (_resizePreview || _resizeCenter is not { } center) return;
        _previewBaseSide = _previewSide = WorkspaceSide;
        _resizePreview = true;
        WorkspaceViewport.Width = WorkspaceViewport.Height = _previewBaseSide;
        WorkspaceViewport.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        WorkspaceViewport.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        WorkspaceViewport.RenderTransformOrigin = RelativePoint.Center;
        _previewTransform.ScaleX = _previewTransform.ScaleY = 1;
        WorkspaceViewport.RenderTransform = _previewTransform;
        WorkspaceViewport.Clip = new EllipseGeometry(new Rect(0, 0, _previewBaseSide, _previewBaseSide));
        // Reserve drawing room once. While captured, neither native client size nor position changes.
        if (!_fixedWindowsHost) ApplyWorkspaceSize(GetCenteredMaximum(center), center);
        UpdateRoundWindow();
        UpdateOverlayLayout();
    }
    private void UpdateResizePreview(double side)
    {
        if (Math.Abs(_previewSide - side) < .01) return;
        _previewSide = side;
        _previewTransform.ScaleX = _previewTransform.ScaleY = side / _previewBaseSide;
        QueueGeometryRefresh();
    }
    private void FinishResizePreview()
    {
        if (!_resizePreview) return;
        var finalSide = _previewSide;
        var center = _resizeCenter ?? GetWorkspaceCenter();
        _resizePreview = false;
        WorkspaceViewport.RenderTransform = null;
        if (!_fixedWindowsHost)
        {
            WorkspaceViewport.Clip = null;
            WorkspaceViewport.Width = WorkspaceViewport.Height = double.NaN;
            WorkspaceViewport.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            WorkspaceViewport.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        }
        ApplyWorkspaceSize(finalSide, center);
        QueueGeometryRefresh();
    }
    private void ApplyWorkspaceSize(double side, Point center)
    {
        if (_adjustingSize) return;
        if (_fixedWindowsHost)
        {
            _workspaceSide = side;
            SetFixedViewportSize(side);
            QueueGeometryRefresh();
            return;
        }
        var scale = Screens.ScreenFromWindow(this)?.Scaling ?? RenderScaling;
        side = Math.Round(side * scale) / scale;
        var position = CenteredPosition(center, side, scale);
        if (Math.Abs(Width - side) < .01 && Math.Abs(Height - side) < .01 && Position == position) return;
        _adjustingSize = true;
        try
        {
            // First commit the native rectangle atomically, then synchronize Avalonia's layout.
            // Cocoa's resize notification updates ClientSize; the guarded managed writes cannot
            // resize back to the previous intermediate frame. No native animation is requested.
            TryCommitNativeFrame(side, position);
            // Commit a single square client size; SizeChanged never resizes the window again.
            Width = side;
            Height = side;
            ClientSize = new Size(side, side);
            if (Position != position) Position = position;
            // Complete the viewport's new centered layout within the same UI transaction,
            // before returning to AppKit's next display cycle.
            if (OperatingSystem.IsMacOS()) UpdateLayout();
        }
        finally { _adjustingSize = false; }
        QueueGeometryRefresh();
    }
    private void ResizeWorkspace(object? sender, PointerPressedEventArgs e)
    {
        if (_closing || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        ApplyPendingResize();
        _resizeCenter = GetWorkspaceCenter();
        _resizeStartSide = WorkspaceSide;
        _resizeScale = Screens.ScreenFromWindow(this)?.Scaling ?? RenderScaling;
        _resizeStartPointer = this.PointToScreen(e.GetPosition(this));
        _resizePointer = e.Pointer;
        BeginResizePreview();
        e.Pointer.Capture(ResizeGrip);
        e.Handled = true;
    }
    private void MoveWorkspaceResize(object? sender, PointerEventArgs e)
    {
        if (_resizePointer != e.Pointer) return;
        var pointer = this.PointToScreen(e.GetPosition(this));
        SetWorkspaceSize(_resizeStartSide + (pointer.X - _resizeStartPointer.X) * 2 / _resizeScale);
        e.Handled = true;
    }
    private void EndWorkspaceResize(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizePointer != e.Pointer) return;
        FinishWorkspaceResize();
        e.Handled = true;
    }
    private void FinishWorkspaceResize()
    {
        var pointer = _resizePointer;
        _resizePointer = null;
        ApplyPendingResize();
        FinishResizePreview();
        _resizeCenter = null;
        pointer?.Capture(null);
        if (!_closing && IsActive) StartSceneMotion();
    }
    private void ShowWorkspaceSize(object? sender, RoutedEventArgs e) { ClosePanels(); SizeFlyout.IsVisible = true; UpdateOverlayLayout(); }
    private void DecreaseWorkspace(object? sender, RoutedEventArgs e) => SetWorkspaceSize((_resizePreview ? _previewSide : WorkspaceSide) - 32);
    private void IncreaseWorkspace(object? sender, RoutedEventArgs e) => SetWorkspaceSize((_resizePreview ? _previewSide : WorkspaceSide) + 32);
    private void ResetWorkspace(object? sender, RoutedEventArgs e) => SetWorkspaceSize(640);

    private void InitializeFixedWindowsHost()
    {
        // Win32 regions provide true outside-circle click-through. macOS is intentionally
        // excluded until equivalent transparent-host hit testing is verified.
        if (!OperatingSystem.IsWindows() || _fixedWindowsHost) return;
        var center = GetWorkspaceCenter();
        _workspaceSide = Width;
        _fixedWindowsHost = true;
        SetFixedViewportSize(_workspaceSide);
        ResizeFixedHost(MaxWidth, center);
    }
    private void SetFixedViewportSize(double side)
    {
        WorkspaceViewport.Width = WorkspaceViewport.Height = side;
        WorkspaceViewport.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        WorkspaceViewport.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        WorkspaceViewport.Clip = new EllipseGeometry(new Rect(0, 0, side, side));
    }
    private void ResizeFixedHost(double side, Point center)
    {
        if (!double.IsFinite(side) || side <= 0) return;
        var scale = Screens.ScreenFromWindow(this)?.Scaling ?? RenderScaling;
        var position = CenteredPosition(center, side, scale);
        _adjustingSize = true;
        try
        {
            TryCommitNativeFrame(side, position);
            Width = Height = side;
            ClientSize = new Size(side, side);
            if (Position != position) Position = position;
            // Complete the viewport's new centered layout within the same UI transaction,
            // before returning to AppKit's next display cycle.
            if (OperatingSystem.IsMacOS()) UpdateLayout();
        }
        finally { _adjustingSize = false; }
        QueueGeometryRefresh();
    }
    private bool TryCommitNativeFrame(double side, PixelPoint position)
    {
        var handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return false;
        if (OperatingSystem.IsWindows() && handle.HandleDescriptor == "HWND")
        {
            var pixels = (int)Math.Round(side * RenderScaling);
            var committed = SetWindowPos(handle.Handle, IntPtr.Zero, position.X, position.Y, pixels, pixels,
                0x0004 | 0x0010 | 0x0008); // NOZORDER | NOACTIVATE | NOREDRAW
            if (committed) UpdateRoundWindow(); // Establish the new shape before any repaint.
            return committed;
        }
        if (!OperatingSystem.IsMacOS() || handle.HandleDescriptor != "NSWindow") return false;
        var frameSelector = SelRegisterName("frame");
        CocoaRect frame;
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            GetCocoaFrameX64(out frame, handle.Handle, frameSelector);
        else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            frame = GetCocoaFrameArm64(handle.Handle, frameSelector);
        else return false;
        // Borderless windows use AppKit point units for their client bounds.
        if (!double.IsFinite(frame.Width) || !double.IsFinite(frame.Height)
            || Math.Abs(frame.Width - Width) > 2 || Math.Abs(frame.Height - Height) > 2) return false;
        frame.X += (frame.Width - side) / 2;
        frame.Y += (frame.Height - side) / 2;
        frame.Width = frame.Height = side;
        SetCocoaFrame(handle.Handle, SelRegisterName("setFrame:display:"), frame, false);
        return true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CocoaRect { public double X, Y, Width, Height; }
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern CocoaRect GetCocoaFrameArm64(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend_stret")]
    private static extern void GetCocoaFrameX64(out CocoaRect result, IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SetCocoaFrame(IntPtr receiver, IntPtr selector, CocoaRect frame,
        [MarshalAs(UnmanagedType.I1)] bool display);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    private void DragWorkspace(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source) return;
        if (source.GetVisualAncestors().Prepend(source).Any(v => v is Button or Slider or TextBox or ComboBox or CheckBox or ToggleSwitch
            || v is ScrollViewer scroll && scroll != WorkspaceViewport)) return;
        BeginMoveDrag(e); e.Handled = true;
    }
    private void ClosePanels()
    {
        if (DataContext is MainViewModel vm) vm.IsSettingsOpen = false;
        AudioFlyout.IsVisible = DevicesFlyout.IsVisible = false;
        SetupFlyout.IsVisible = false;
        SizeFlyout.IsVisible = false;
        ClipboardFlyout.IsVisible = false;
        if (_layoutPanel is not null) _layoutPanel.IsVisible = false;
    }
    private void ShowWorkspace(object? sender, RoutedEventArgs e) => ClosePanels();
    private void ShowDeviceSetup(object? sender, RoutedEventArgs e)
    {
        ClosePanels();
        SetupPlatformText.Text = DeviceSetupActions.IsMac
            ? "首次使用请允许设备控制。"
            : "想控制 Mac？请在 Mac 端允许设备控制。";
        SetupPermissionText.Text = DeviceSetupActions.PermissionGuide;
        SetupBackendText.Text = DeviceSetupActions.BackendGuide;
        SetupCameraText.Text = DeviceSetupActions.CameraGuide;
        SetupAccessibilityButton.IsVisible = DeviceSetupActions.IsMac;
        SetupMacAccessState.IsVisible = DeviceSetupActions.IsMac;
        RefreshMacAccess();
        try { SetupApplicationText.Text = DeviceSetupActions.CurrentApplication; }
        catch (Exception ex) { SetupApplicationText.Text = "无法定位当前应用：" + ex.Message; }
        SetupFlyout.IsVisible = true;
        UpdateOverlayLayout();
    }
    private async void RunDeviceSetupAction(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag
            || !Enum.TryParse<DeviceSetupAction>(tag, out var action)) return;
        button.IsEnabled = false;
        try
        {
            await Task.Run(() => DeviceSetupActions.Run(action));
            SetupActionFeedback.Foreground = Brush.Parse("#536685");
            SetupActionFeedback.Text = "请在系统页面完成设置，再返回查看状态。";
            RefreshMacAccess();
        }
        catch (Exception ex)
        {
            SetupActionFeedback.Foreground = Brush.Parse("#963D3D");
            SetupActionFeedback.Text = "未能完成打开请求：" + ex.Message;
            ToolTip.SetTip(SetupActionFeedback, SetupActionFeedback.Text);
        }
        finally { button.IsEnabled = true; }
    }
    private void ShowSettings(object? sender, RoutedEventArgs e)
    {
        var visible = (DataContext as MainViewModel)?.IsSettingsOpen == true;
        ClosePanels();
        if (DataContext is MainViewModel vm) vm.IsSettingsOpen = !visible;
        UpdateOverlayLayout();
    }
    private void ToggleAudio(object? sender, RoutedEventArgs e)
    {
        var visible = AudioFlyout.IsVisible;
        ClosePanels(); AudioFlyout.IsVisible = !visible;
        UpdateOverlayLayout();
    }
    private void ToggleDevices()
    {
        var visible = DevicesFlyout.IsVisible;
        ClosePanels(); DevicesFlyout.IsVisible = !visible;
        UpdateOverlayLayout();
    }
    private void ShowDevices(object? sender, RoutedEventArgs e) => ToggleDevices();
    private void ToggleClipboard()
    {
        var visible = ClipboardFlyout.IsVisible;
        ClosePanels(); ClipboardFlyout.IsVisible = !visible;
        UpdateOverlayLayout();
    }
    private void ToggleLayout()
    {
        var wasVisible = _layoutPanel?.IsVisible == true;
        ClosePanels();
        if (_layoutPanel is null)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 17, Foreground = Brush.Parse("#315B92") };
            text.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("InputStatusText"));
            var body = new StackPanel { Spacing = 16 };
            body.Children.Add(new TextBlock { Text = "Windows  ←  →  Mac", FontSize = 21, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center });
            body.Children.Add(text);
            body.Children.Add(new TextBlock { Text = "键盘跟随指针 · Ctrl + Alt + M 返回", TextWrapping = TextWrapping.Wrap, FontSize = 16, Foreground = Brush.Parse("#536685") });
            body.Children.Add(new Avalonia.Controls.Shapes.Rectangle { Height = 1, Fill = Brush.Parse("#DDE7F5"), Margin = new Thickness(0,4) });
            var roaming = new ToggleSwitch { Content = "旧窗口镜像 · 已停用", IsEnabled = false };
            roaming.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Avalonia.Data.Binding("WindowRoamingEnabled") { Mode = Avalonia.Data.BindingMode.TwoWay });
            body.Children.Add(roaming);
            var roamingState = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 17, Foreground = Brush.Parse("#315B92") };
            roamingState.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("WindowRoamingStatus")); body.Children.Add(roamingState);
            var picker = new StackPanel { Spacing = 8 };
            picker.Bind(IsVisibleProperty, new Avalonia.Data.Binding("IsWindowsWindowSender"));
            var choices = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                PlaceholderText = "选择一个窗口" };
            choices.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<WindowCaptureTarget>((item, _) =>
                new TextBlock { Text = item?.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            choices.Bind(ItemsControl.ItemsSourceProperty, new Avalonia.Data.Binding("RoamingWindows"));
            choices.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
                new Avalonia.Data.Binding("SelectedRoamingWindow") { Mode = Avalonia.Data.BindingMode.TwoWay });
            picker.Children.Add(choices);
            var sendWindow = new Button { Content = "移到 Mac" }; sendWindow.Classes.Add("secondary");
            sendWindow.Bind(Button.CommandProperty, new Avalonia.Data.Binding("SendRoamingWindowCommand")); picker.Children.Add(sendWindow);
            // Window crossing is a titlebar gesture. The legacy picker is no longer a product entry.
            var returnWindow = new Button { Content = "归还窗口" }; returnWindow.Classes.Add("secondary");
            returnWindow.Bind(Button.CommandProperty, new Avalonia.Data.Binding("ReturnRoamingWindowCommand"));
            returnWindow.Bind(IsVisibleProperty, new Avalonia.Data.Binding("CanReturnRoamingWindow")); body.Children.Add(returnWindow);
            var setup = new Button { Content = "连接与权限" }; setup.Classes.Add("secondary"); setup.Click += ShowDeviceSetup; body.Children.Add(setup);
            _layoutPanel = CreateDiscPage("跨屏", body);
            OverlayCanvas.Children.Add(_layoutPanel);
        }
        _layoutPanel.IsVisible = !wasVisible;
        if (!wasVisible && DataContext is MainViewModel current) current.RefreshRoamingWindows();
        UpdateOverlayLayout();
    }
    private Border CreateDiscPage(string title, Control body)
    {
        var layers = new Panel();
        layers.Children.Add(new CircularGlassSurface { StyleKind = CircularGlassStyleKind.Pearl, IsHitTestVisible = false });
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*,40") };
        var back = new Button { Content = "‹" }; back.Classes.Add("discNav"); back.Click += ShowWorkspace; ToolTip.SetTip(back, "返回主页");
        var close = new Button { Content = "×" }; close.Classes.Add("discNav"); close.Click += ShowWorkspace; ToolTip.SetTip(close, "关闭页面"); Grid.SetColumn(close, 2);
        var heading = new TextBlock { Text = title }; heading.Classes.Add("discTitle"); Grid.SetColumn(heading, 1);
        header.Children.Add(back); header.Children.Add(heading); header.Children.Add(close); content.Children.Add(header);
        var scroll = new ScrollViewer { Content = body, Margin = new Thickness(0,14,0,0), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); content.Children.Add(scroll);
        layers.Children.Add(content);
        var page = new Border { Child = layers }; page.Classes.Add("discPage"); return page;
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_canClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _macAccessTimer?.Stop();
        _pendingSide = null;
        _resizeTimer.Stop();
        FinishWorkspaceResize();
        UpdatePreviewVisibility();
        StopSceneMotion();
        try
        {
            if (DataContext is MainViewModel vm)
                await vm.ShutdownAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("LinkBridge cleanup: " + ex.Message); }
        finally { _canClose = true; Close(); }
    }
}

