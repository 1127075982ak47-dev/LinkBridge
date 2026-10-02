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

namespace LinkBridge.App.Views;

public partial class MainWindow : Window
{
    private bool _closing, _canClose;
    private Border? _layoutPanel;
    private bool _adjustingSize, _geometryQueued, _sliderResizing, _resizePreview;
    private double _previewBaseSide, _previewSide;
    private readonly ScaleTransform _previewTransform = new(1, 1);
    private PixelSize? _lastRoundSize;
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
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += (_, _) => { _macAccessTimer?.Stop(); _resizeTimer.Stop(); };
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
            StopSceneMotion();
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
        LayoutButton.Click += (_, _) => ToggleLayout();
        InputStatusButton.Click += (_, _) => ToggleLayout();
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
                if (e.Key is Key.OemPlus or Key.Add) { SetWorkspaceSize((_resizePreview ? _previewSide : Width) + 32); e.Handled = true; }
                else if (e.Key is Key.OemMinus or Key.Subtract) { SetWorkspaceSize((_resizePreview ? _previewSide : Width) - 32); e.Handled = true; }
                else if (e.Key == Key.D0) { SetWorkspaceSize(640); e.Handled = true; }
            }
        };
        if (Environment.GetCommandLineArgs().Contains("--topmost")) Topmost = true;
    }
    private async void OnOpened(object? sender, EventArgs e)
    {
        UpdateWorkArea();
        UpdateRoundWindow();
        UpdateOverlayLayout();
        if (DataContext is not MainViewModel vm) return;
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
        var size = new PixelSize(area.Right, area.Bottom);
        if (_lastRoundSize == size) return;
        var diameter = Math.Min(area.Right, area.Bottom);
        var left = (area.Right - diameter) / 2; var top = (area.Bottom - diameter) / 2;
        var region = CreateEllipticRgn(left, top, left + diameter, top + diameter);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
        else _lastRoundSize = size;
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
            MinWidth = MinHeight = Math.Min(480, side);
            MaxWidth = MaxHeight = side;
            var bounded = Math.Min(Math.Max(Width, MinWidth), side);
            ApplyWorkspaceSize(bounded, GetWorkspaceCenter());
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
        var physicalWidth = (int)Math.Ceiling(Width * screen.Scaling);
        var physicalHeight = (int)Math.Ceiling(Height * screen.Scaling);
        var position = new PixelPoint(Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - physicalWidth)),
            Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - physicalHeight)));
        if (Position != position) Position = position;
    }

    private void UpdateOverlayLayout()
    {
        var side = _resizePreview ? _previewSide : Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) side = Math.Min(Width, Height);
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
        Canvas.SetLeft(ResizeGrip, offsetX + side - ResizeGrip.Width - 14);
        Canvas.SetTop(ResizeGrip, offsetY + (side - ResizeGrip.Height) / 2);
        _syncingSizeControl = true;
        try
        {
            WorkspaceSizeSlider.Minimum = MinWidth;
            WorkspaceSizeSlider.Maximum = GetCenteredMaximum(_resizeCenter ?? GetWorkspaceCenter());
            var displaySide = _resizePreview ? _previewSide : Width;
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
            if (!_resizePreview) UpdateRoundWindow();
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
        StopSceneMotion();
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
        _previewBaseSide = _previewSide = Width;
        _resizePreview = true;
        WorkspaceViewport.Width = WorkspaceViewport.Height = _previewBaseSide;
        WorkspaceViewport.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        WorkspaceViewport.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        WorkspaceViewport.RenderTransformOrigin = RelativePoint.Center;
        _previewTransform.ScaleX = _previewTransform.ScaleY = 1;
        WorkspaceViewport.RenderTransform = _previewTransform;
        WorkspaceViewport.Clip = new EllipseGeometry(new Rect(0, 0, _previewBaseSide, _previewBaseSide));
        // Reserve drawing room once. While captured, neither native client size nor position changes.
        ApplyWorkspaceSize(GetCenteredMaximum(center), center);
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
        WorkspaceViewport.Clip = null;
        WorkspaceViewport.Width = WorkspaceViewport.Height = double.NaN;
        WorkspaceViewport.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        WorkspaceViewport.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        ApplyWorkspaceSize(finalSide, center);
        QueueGeometryRefresh();
    }
    private void ApplyWorkspaceSize(double side, Point center)
    {
        if (_adjustingSize) return;
        var scale = Screens.ScreenFromWindow(this)?.Scaling ?? RenderScaling;
        side = Math.Round(side * scale) / scale;
        var position = CenteredPosition(center, side, scale);
        if (Math.Abs(Width - side) < .01 && Math.Abs(Height - side) < .01 && Position == position) return;
        _adjustingSize = true;
        try
        {
            // Commit a single square client size; SizeChanged never resizes the window again.
            Width = side;
            Height = side;
            ClientSize = new Size(side, side);
            if (Position != position) Position = position;
        }
        finally { _adjustingSize = false; }
        QueueGeometryRefresh();
    }
    private void ResizeWorkspace(object? sender, PointerPressedEventArgs e)
    {
        if (_closing || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        ApplyPendingResize();
        _resizeCenter = GetWorkspaceCenter();
        _resizeStartSide = Width;
        _resizeScale = Screens.ScreenFromWindow(this)?.Scaling ?? RenderScaling;
        _resizeStartPointer = this.PointToScreen(e.GetPosition(this));
        _resizePointer = e.Pointer;
        BeginResizePreview();
        e.Pointer.Capture(ResizeGrip);
        StopSceneMotion();
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
    private void DecreaseWorkspace(object? sender, RoutedEventArgs e) => SetWorkspaceSize((_resizePreview ? _previewSide : Width) - 32);
    private void IncreaseWorkspace(object? sender, RoutedEventArgs e) => SetWorkspaceSize((_resizePreview ? _previewSide : Width) + 32);
    private void ResetWorkspace(object? sender, RoutedEventArgs e) => SetWorkspaceSize(640);

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
            body.Children.Add(new TextBlock { Text = "右滑进入 Mac，左滑返回。\n键盘跟随指针。", TextWrapping = TextWrapping.Wrap, FontSize = 17 });
            body.Children.Add(new TextBlock { Text = "无法返回？按 Ctrl + Alt + M", TextWrapping = TextWrapping.Wrap, FontSize = 16, Foreground = Brush.Parse("#536685") });
            var setup = new Button { Content = "连接与权限" }; setup.Classes.Add("secondary"); setup.Click += ShowDeviceSetup; body.Children.Add(setup);
            _layoutPanel = CreateDiscPage("跨屏", body);
            OverlayCanvas.Children.Add(_layoutPanel);
        }
        _layoutPanel.IsVisible = !wasVisible;
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

