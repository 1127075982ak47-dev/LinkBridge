using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkBridge.Core;
using LinkBridge.Core.Discovery;
using LinkBridge.Core.Models;
using LinkBridge.Input;
using LinkBridge.Media;
using LinkBridge.Media.Camera;
using LinkBridge.Media.Audio;
using LinkBridge.App.Services;

namespace LinkBridge.App.ViewModels;

/// <summary>雷达区上的设备类别。</summary>
public enum TileKind
{
    Camera,
    Microphone,
    Speaker,
    Keyboard,
    Mouse,
    Disk,
}

/// <summary>设备图标：白色线描图形，画在彩色渐变球上。</summary>
internal static class DeviceIcons
{
    private static Geometry Camera()
    {
        var group = new GeometryGroup();
        group.Children.Add(Geometry.Parse("M3 8.8 H6.2 L7.7 6 H16.3 L17.8 8.8 H21 V19 H3 Z"));
        group.Children.Add(new EllipseGeometry(new Rect(8.6, 10.6, 6.8, 6.8)));
        return group;
    }

    private static Geometry Microphone()
    {
        var group = new GeometryGroup();
        group.Children.Add(Geometry.Parse("M12 3.6 A2.6 2.6 0 0 1 14.6 6.2 V11.4 A2.6 2.6 0 0 1 9.4 11.4 V6.2 A2.6 2.6 0 0 1 12 3.6 Z"));
        group.Children.Add(Geometry.Parse("M6.8 11.2 V11.8 A5.2 5.2 0 0 0 17.2 11.8 V11.2"));
        group.Children.Add(Geometry.Parse("M12 17 V20.4"));
        group.Children.Add(Geometry.Parse("M9.2 20.4 H14.8"));
        return group;
    }

    private static Geometry Speaker()
    {
        var group = new GeometryGroup();
        group.Children.Add(Geometry.Parse("M11 4.6 L6.6 8.6 H3.4 V15.4 H6.6 L11 19.4 Z"));
        group.Children.Add(Geometry.Parse("M14.6 9.4 A3.8 3.8 0 0 1 14.6 14.6"));
        group.Children.Add(Geometry.Parse("M17.2 6.8 A7.4 7.4 0 0 1 17.2 17.2"));
        return group;
    }

    private static Geometry Keyboard()
    {
        var group = new GeometryGroup();
        group.Children.Add(Geometry.Parse("M3 7 H21 V17 H3 Z"));
        group.Children.Add(Geometry.Parse("M6.4 10.6 H8 M10.4 10.6 H12 M14.4 10.6 H16"));
        group.Children.Add(Geometry.Parse("M6.4 13.6 H8 M10.4 13.6 H12 M14.4 13.6 H16"));
        group.Children.Add(Geometry.Parse("M9 16 H15"));
        return group;
    }

    private static Geometry Mouse()
    {
        var group = new GeometryGroup();
        group.Children.Add(
            Geometry.Parse("M9 4 H15 A3 3 0 0 1 18 7 V16 A4 4 0 0 1 14 20 H10 A4 4 0 0 1 6 16 V7 A3 3 0 0 1 9 4 Z"));
        group.Children.Add(Geometry.Parse("M12 7.5 V10.8"));
        return group;
    }

    private static Geometry Disk()
    {
        var group = new GeometryGroup();
        group.Children.Add(Geometry.Parse("M2.5 6 H21.5 V18 H2.5 Z"));
        group.Children.Add(Geometry.Parse("M2.5 13.4 H21.5"));
        group.Children.Add(Geometry.Parse("M5.4 9.7 H6.6"));
        group.Children.Add(Geometry.Parse("M18.4 15.7 H19.6"));
        return group;
    }

    public static Geometry For(TileKind kind) => kind switch
    {
        TileKind.Camera => Camera(),
        TileKind.Microphone => Microphone(),
        TileKind.Speaker => Speaker(),
        TileKind.Keyboard => Keyboard(),
        TileKind.Mouse => Mouse(),
        _ => Disk(),
    };

    public static string OutputType(string name)
    {
        if (name.Contains("Digital", StringComparison.OrdinalIgnoreCase) || name.Contains("数字", StringComparison.OrdinalIgnoreCase) || name.Contains("SPDIF", StringComparison.OrdinalIgnoreCase)) return "数字输出";
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("HDMI", StringComparison.OrdinalIgnoreCase) || name.Contains("DisplayPort", StringComparison.OrdinalIgnoreCase)) return "显示器音频";
        if (name.Contains("Head", StringComparison.OrdinalIgnoreCase) || name.Contains("耳机", StringComparison.OrdinalIgnoreCase)) return "耳机";
        if (name.Contains("USB", StringComparison.OrdinalIgnoreCase)) return "USB 音响";
        return "音响";
    }

    public static Geometry OutputIcon(string name) => OutputType(name) switch
    {
        "显示器音频" => Geometry.Parse("M2 4 H22 V17 H2 Z M12 17 V21 M7 21 H17 M8 8 L11 6 V14 L8 12 H5 V8 Z M14 8 Q18 10 14 12"),
        "数字输出" => Geometry.Parse("M4 4 H20 V20 H4 Z M8 8 H16 V14 H8 Z M10 14 V17 M14 14 V17"),
        "耳机" => Geometry.Parse("M4 14 V11 A8 8 0 0 1 20 11 V14 M3 12 H7 V20 H3 Z M17 12 H21 V20 H17 Z"),
        _ => Speaker(),
    };

    /// <summary>每类设备一组对角渐变（照设计稿取色）。</summary>
    public static (string From, string To, string Glow) Palette(TileKind kind) => kind switch
    {
        TileKind.Camera => ("#00C6FF", "#0072FF", "#0091FF"),
        TileKind.Microphone => ("#FF5E62", "#FF2A54", "#FF2A54"),
        TileKind.Speaker => ("#7F75F0", "#5949E2", "#6D61EA"),
        TileKind.Keyboard => ("#34D399", "#059669", "#10B981"),
        TileKind.Mouse => ("#FBBF24", "#EA580C", "#F59E0B"),
        _ => ("#94A3B8", "#475569", "#64748B"),
    };
}

/// <summary>雷达区上的一个设备节点（极坐标定位）。</summary>
public partial class DeviceTile : ObservableObject
{
    /// <summary>雷达画布尺寸，与 XAML 中保持一致。</summary>
    public const double RadarWidth = 760;

    public const double RadarHeight = 760;

    private const double OrbitRadius = 268;
    private const double BallSize = 76;
    private const double NodeWidth = 154;

    private static readonly IBrush NameOn = new SolidColorBrush(Color.Parse("#1D1D1F"));
    private static readonly IBrush NameOff = new SolidColorBrush(Color.Parse("#AEB4C0"));

    private readonly IBrush _live;
    private readonly IBrush _dim;
    private readonly Color _glow;

    public DeviceTile(TileKind kind, string name, string subtitle, int index, int total, MediaDevice? media = null, string? owner = null)
    {
        Kind = kind;
        Name = name;
        Subtitle = subtitle;
        Media = media;
        RemoteOwner = owner;

        IconGeometry = kind == TileKind.Speaker ? DeviceIcons.OutputIcon(name) : DeviceIcons.For(kind);

        var (from, to, glow) = DeviceIcons.Palette(kind);
        _live = MakeGradient(from, to);
        _glow = Color.Parse(glow);

        // 未选中用同一套明暗关系的灰阶，比单纯降透明度干净
        _dim = MakeGradient("#D3D8E0", "#A8B0BD");

        AccentBrush = new SolidColorBrush(_glow);

        var step = 360.0 / Math.Max(total, 1);
        AngleDeg = -90 + step * index;

        // 每个节点给一点点相位差，浮动起来更自然
        FloatSeconds = 2.6 + index % 3 * 0.35;
    }

    public TileKind Kind { get; }
    public bool IsMediaShareable => !IsRemote && Media?.IsApplicationEndpoint != true && Kind is TileKind.Camera or TileKind.Microphone;
    public string? RemoteOwner { get; }
    public bool IsRemote => RemoteOwner is not null;

    public string Name { get; }

    public string Subtitle { get; }

    public string OwnerLabel => RemoteOwner ?? $"{PlatformInfo.DisplayName} · {PlatformInfo.MachineName} · 本机";
    public string DisplaySubtitle => IsSpeaker ? DeviceIcons.OutputType(Name) + " · 本机" : Subtitle;
    public string OutputKindLabel => DeviceIcons.OutputType(Name);
    public string FullDescription => $"{Name}\n{Subtitle} · {PlatformInfo.MachineName}\n{PlaybackBadge}";
    public bool IsSpeaker => Kind == TileKind.Speaker;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullDescription))]
    public partial string PlaybackBadge { get; set; } = string.Empty;

    public MediaDevice? Media { get; }

    public Geometry IconGeometry { get; }

    public IBrush AccentBrush { get; }

    /// <summary>浮动动画时长（秒），按索引错开一点。</summary>
    public double FloatSeconds { get; }

    public double AngleDeg { get; }

    private double CenterX => RadarWidth / 2;

    private double CenterY => RadarHeight / 2;

    private double Rad => AngleDeg * Math.PI / 180;

    public double NodeLeft => CenterX + OrbitRadius * Math.Cos(Rad) - NodeWidth / 2;

    /// <summary>节点块的左上坐标，带上浮动位移。</summary>
    public double NodeTop => CenterY + OrbitRadius * Math.Sin(Rad) - BallSize / 2 + FloatOffset;

    /// <summary>浮动偏移（像素），由主视图模型的定时器逐帧刷新。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NodeTop))]
    public partial double FloatOffset { get; set; }

    public double LineEndX => CenterX + OrbitRadius * Math.Cos(Rad);

    public double LineEndY => CenterY + OrbitRadius * Math.Sin(Rad);

    public double LineStartX => CenterX + 70 * Math.Cos(Rad);

    public double LineStartY => CenterY + 70 * Math.Sin(Rad);

    public Point LineStart => new(LineStartX, LineStartY);

    public Point LineEnd => new(LineEndX, LineEndY);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconBrush))]
    [NotifyPropertyChangedFor(nameof(GlowColor))]
    [NotifyPropertyChangedFor(nameof(NameBrush))]
    [NotifyPropertyChangedFor(nameof(LineOpacity))]
    [NotifyPropertyChangedFor(nameof(CheckOpacity))]
    public partial bool IsSelected { get; set; } = true;

    /// <summary>选中是彩色渐变，未选中整体换成灰阶（不是简单压透明）。</summary>
    public IBrush IconBrush => IsSelected ? _live : _dim;

    /// <summary>只有选中时才有彩色投影。</summary>
    public Color GlowColor => IsSelected ? _glow : Colors.Transparent;

    public IBrush NameBrush => IsSelected || IsSpeaker ? NameOn : NameOff;

    public double LineOpacity => IsSelected ? 0.55 : 0.0;

    /// <summary>右上角蓝色小勾的显隐。</summary>
    public double CheckOpacity => IsSelected ? 1.0 : 0.0;

    private static IBrush MakeGradient(string from, string to) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse(from), 0),
            new GradientStop(Color.Parse(to), 1),
        },
    };
}

public partial class MainViewModel : ViewModelBase
{
    private readonly MediaDeviceCatalog _catalog = new();
    private readonly MediaStreamSession _session = new();
    private readonly MediaStreamReceiver _receiver = new();
    private InputBridgeServer? _inputServer;
    private InputBridgeClient? _inputClient;
    private WindowsInputCapture? _inputCapture;
    private Avalonia.Threading.DispatcherTimer? _inputTimer;
    private bool _connectingInput;
    private string? _lastInputError;

    private PeerDiscovery? _discovery;
    private bool _initialized;
    private bool _receivedFirstFrame;
    private Avalonia.Threading.DispatcherTimer? _playbackTimer;
    private bool _updatingPlayback;
    private bool _playbackRefreshPending;
    private Task? _playbackRefreshTask;
    private volatile bool _previewVisible = true;
    private SharedAudioReceiver? _audioReceiver;
    private readonly SharedAudioSender _audioSender = new();
    private string? _lastPlaybackEndpoint;
    private PreviewFrame? _pendingPreviewFrame;
    private int _previewUpdateQueued;
    private WriteableBitmap? _previewBitmapA, _previewBitmapB;
    private bool _previewBufferIndex;
    private volatile bool _shuttingDown;
    private volatile bool _receiveAllowed;
    private bool _readingVolume;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _initializationTask;
    private Task? _shutdownTask;
    private SharedCameraBroker? _cameraBroker;
    private readonly SharedCameraProducer _cameraProducer = new();
    private SharedCameraCoordinator? _cameraCoordinator;
    private MacSharedCameraCoordinator? _macCameraCoordinator;
    private SharedMicrophoneCoordinator? _microphoneCoordinator;
    private SharedSpeakerCoordinator? _speakerCoordinator;
    private bool _updatingSystemAudioPreference;
    private bool _updatingLocalSourceSelection;
    private MediaDevice? _selectedCameraSource;
    private sealed record OwnedCameraSource(string Address, int Port, long CoreVersion, long ProducerGeneration);
    private OwnedCameraSource? _ownedCameraSource;
    private readonly int _activeMediaPort;
    private CancellationTokenSource? _shareStartCancellation;
    private long _shareGeneration;
    private bool _startupPreference;
    private readonly ProductLog _productLog;
    private readonly object _uiLogGate = new();
    private readonly Queue<string> _pendingUiLogs = new();
    private bool _logDispatchScheduled;
    private readonly List<DeviceTile> _remoteCameras = [];
    [ObservableProperty]
    public partial string SharedCameraStatusText { get; set; } = "应用共享摄像头尚未启用";
    [ObservableProperty]
    public partial string SharedCameraSourceStatusText { get; set; } = "本机相机按需提供 · 等待对端应用调用";
    [ObservableProperty]
    public partial string SharedMicrophoneStatusText { get; set; } = "正在检查应用麦克风入口…";

    /// <summary>驱动节点浮动的小时钟。</summary>
    private readonly Avalonia.Threading.DispatcherTimer _floatTimer;

    private double _phase;

    public MainViewModel()
    {
        Settings = SettingsStore.Load();
        _activeMediaPort = Settings.MediaPort;
        _productLog = new ProductLog(Path.Combine(Path.GetDirectoryName(SettingsStore.FilePath)!, "linkbridge.log"));
        void ReportLogFailure(string error) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_shuttingDown) return;
            var message = "运行日志保存失败：" + error;
            if (PersistentLogError == message) return;
            PersistentLogError = message; StatusText = message;
            AppendLog(message);
        });
        _productLog.WriteFailed += ReportLogFailure;
        // The worker can fail before the event subscription is attached.
        if (_productLog.Error is { } existingLogError) ReportLogFailure(existingLogError);
        _startupPreference = Settings.LaunchAtStartup;
        _updatingSystemAudioPreference = true;
        ShareSystemAudioAutomatically = Settings.ShareSystemAudioAutomatically;
        _updatingSystemAudioPreference = false;
        LocalSummary = $"{Settings.DisplayName} · {PlatformInfo.DisplayName}";
        _shareClipboardAutomatically = Settings.ShareClipboardAutomatically;
        _isMotionReduced = Settings.ReduceMotion;
        MediaPortText = Settings.MediaPort.ToString();

        Peers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPeers));
        _session.LogEmitted += AppendLog;
        _session.StreamEnded += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_shuttingDown || _session.IsRunning) return;
            IsSharing = false;
            StatusText = "推流中断，请查看日志";
        });
        _receiver.LogEmitted += AppendLog;
        _receiver.StreamEnded += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_shuttingDown || _receiver.IsRunning) return;
            IsReceiving = _receiver.IsListeningRequested;
            PreviewImage = null;
            StatusText = IsReceiving ? "接收画面已中断 · 等待配对发送端重连" : "接收器已退出，请查看日志";
        });
        _receiver.FrameDecoded += OnFrameDecoded;

        // 25fps 足够顺，也不会让布局系统太累
        _floatTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _floatTimer.Tick += (_, _) => TickFloat();
        // 设备图只在真实状态变化时重绘，避免空闲时持续触发布局。
    }

    /// <summary>让每个节点按自己的相位轻轻上下浮动。</summary>
    private void TickFloat()
    {
        _phase += 0.045;

        for (var i = 0; i < Devices.Count; i++)
        {
            Devices[i].FloatOffset = Math.Sin(_phase * 1.5 + i * 0.8) * 5.5;
        }
    }

    public AppSettings Settings { get; }

    public int[] FrameRateOptions { get; } = [15, 24, 30, 60];

    public int[] VideoBitrateOptions { get; } = [2, 4, 6, 8, 12];

    public int[] AudioBitrateOptions { get; } = [64, 96, 128, 192];

    public ObservableCollection<DeviceTile> Devices { get; } = [];
    public DeviceTile? PrimaryCamera => Devices.FirstOrDefault(d => d.Kind == TileKind.Camera);
    public DeviceTile? SecondaryCamera => Devices.Where(d => d.Kind == TileKind.Camera).Skip(1).FirstOrDefault() ?? _remoteCameras.FirstOrDefault();
    public DeviceTile? TertiaryCamera => Devices.Where(d => d.Kind == TileKind.Camera).Skip(2).FirstOrDefault() ?? _remoteCameras.Skip(1).FirstOrDefault();
    public DeviceTile? MicrophoneDevice => Devices.FirstOrDefault(d => d.Kind == TileKind.Microphone);
    public DeviceTile? PrimarySpeaker => Devices.FirstOrDefault(d => d.IsSpeaker && d.Media?.Id == _lastPlaybackEndpoint)
        ?? Devices.FirstOrDefault(d => d.IsSpeaker);
    public DeviceTile? FirstExtraSpeaker => Devices.Where(d => d.IsSpeaker && !ReferenceEquals(d, PrimarySpeaker)).FirstOrDefault();
    public DeviceTile? SecondExtraSpeaker => Devices.Where(d => d.IsSpeaker && !ReferenceEquals(d, PrimarySpeaker)).Skip(1).FirstOrDefault();
    public bool HasPrimaryCamera => PrimaryCamera is not null;
    public bool HasSecondaryCamera => SecondaryCamera is not null;
    public bool HasTertiaryCamera => TertiaryCamera is not null;
    public bool HasMicrophone => MicrophoneDevice is not null;
    public bool HasPrimarySpeaker => PrimarySpeaker is not null;
    public bool HasFirstExtraSpeaker => FirstExtraSpeaker is not null;
    public bool HasSecondExtraSpeaker => SecondExtraSpeaker is not null;
    public string MicrophoneSelectionText => MicrophoneDevice?.IsSelected == true ? "已选择" : "待选择";
    public Geometry LayoutIcon => DeviceIcons.OutputIcon("HDMI");
    public Geometry KeyboardIcon => DeviceIcons.For(TileKind.Keyboard);
    public Geometry ConnectIcon => Geometry.Parse("M8 3 H3 V8 M16 3 H21 V8 M3 16 V21 H8 M21 16 V21 H16 M8 12 H16 M12 8 V16");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeLabel))]
    public partial double VolumePercent { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeLabel))]
    public partial bool IsVolumeAvailable { get; set; }
    public string VolumeLabel => IsVolumeAvailable ? $"{VolumePercent:0}%" : "不可调";
    partial void OnVolumePercentChanged(double value)
    {
        if (_readingVolume || !IsVolumeAvailable) return;
        try { PlaybackVolumeController.Write(Settings.PlaybackDeviceId, value); }
        catch (Exception ex) { AppendLog("[错误] 音量调整失败：" + ex.Message); }
    }
    private void ApplyPlaybackVolume(double? value, bool writable)
    {
        _readingVolume = true;
        try
        {
            IsVolumeAvailable = value.HasValue && writable;
            if (value is { } percent) VolumePercent = percent;
        }
        finally { _readingVolume = false; }
    }
    [RelayCommand] private void PreviousPlayback() => CyclePlayback(-1);
    [RelayCommand] private void NextPlayback() => CyclePlayback(1);
    [RelayCommand] private void SelectPlayback(DeviceTile? tile)
    {
        if (tile?.Media is not { } media || tile.IsRemote) return;
        var option = PlaybackDevices.FirstOrDefault(d => d.Id == media.Id && d.IsAvailable);
        if (option is not null) SelectedPlaybackDevice = option;
    }
    private void CyclePlayback(int direction)
    {
        var choices = PlaybackDevices.Where(d => d.IsAvailable).ToList();
        if (choices.Count == 0) return;
        var index = choices.FindIndex(d => d.Id == SelectedPlaybackDevice?.Id);
        SelectedPlaybackDevice = choices[(Math.Max(index, 0) + direction + choices.Count) % choices.Count];
    }
    private void NotifyDeviceSlots()
    {
        foreach (var name in new[] { nameof(PrimaryCamera), nameof(SecondaryCamera), nameof(TertiaryCamera), nameof(MicrophoneDevice),
            nameof(PrimarySpeaker), nameof(FirstExtraSpeaker), nameof(SecondExtraSpeaker), nameof(HasPrimaryCamera), nameof(HasSecondaryCamera),
            nameof(HasTertiaryCamera), nameof(HasMicrophone), nameof(HasPrimarySpeaker), nameof(HasFirstExtraSpeaker), nameof(HasSecondExtraSpeaker), nameof(MicrophoneSelectionText) })
            OnPropertyChanged(name);
    }
    public ObservableCollection<PlaybackDeviceOption> PlaybackDevices { get; } = [];
    public ObservableCollection<PlaybackDeviceOption> RemoteAudioDevices { get; } = [];
    public bool CanSendSystemAudio => PlatformInfo.IsMacOS;
    [ObservableProperty]
    public partial bool IsSendingSystemAudio { get; set; }
    [ObservableProperty]
    public partial string SharedAudioStatus { get; set; } = "共享音响未连接";

    [ObservableProperty]
    public partial bool ShareSystemAudioAutomatically { get; set; } = true;
    partial void OnShareSystemAudioAutomaticallyChanged(bool value)
    {
        if (_updatingSystemAudioPreference || _shuttingDown) return;
        Settings.ShareSystemAudioAutomatically = value;
        SaveSettings();
        if (_speakerCoordinator is null) return;
        if (value) _speakerCoordinator.Enable();
        else _ = _speakerCoordinator.DisableAsync();
    }

    [RelayCommand]
    private Task StartSystemAudioAsync()
    {
        if (_speakerCoordinator is null) { SharedAudioStatus = "共享音响服务尚未初始化"; return Task.CompletedTask; }
        ShareSystemAudioAutomatically = true;
        _speakerCoordinator.Enable();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task StopSystemAudioAsync()
    {
        ShareSystemAudioAutomatically = false;
        return Task.CompletedTask;
    }

    [ObservableProperty]
    public partial PlaybackDeviceOption? SelectedPlaybackDevice { get; set; }
    [ObservableProperty]
    public partial string DefaultPlaybackText { get; set; } = "正在读取默认输出…";
    [ObservableProperty]
    public partial string PlaybackSelectionText { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string ActualPlaybackText { get; set; } = "共享音频尚未播放";

    partial void OnSelectedPlaybackDeviceChanged(PlaybackDeviceOption? value)
    {
        if (_updatingPlayback || value is null) return;
        Settings.PlaybackDeviceId = value.Id;
        SaveSettings();
        UpdatePlaybackDevices();
        AppendLog($"[LinkBridge] 播放输出选择：{value.Label}");
    }

    [RelayCommand]
    private async Task TestPlaybackAsync()
    {
        if (_shuttingDown) return;
        var requestedId = Settings.PlaybackDeviceId;
        var requestedLabel = SelectedPlaybackDevice?.Label ?? "系统默认";
        try
        {
            await PlaybackDeviceTest.PlayAsync(requestedId, _lifetimeCts.Token);
            if (_shuttingDown) return;
            AppendLog("[LinkBridge] 选定输出的提示音已播放：" + requestedLabel);
            if (Settings.PlaybackDeviceId == requestedId)
                PlaybackSelectionText = "提示音已播放 · " + requestedLabel;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_shuttingDown) return;
            if (Settings.PlaybackDeviceId == requestedId)
                PlaybackSelectionText = "测试失败：" + ex.Message;
            AppendLog("[错误] 音响测试失败（" + requestedLabel + "）：" + ex.Message);
        }
    }

    private sealed record PlaybackSnapshot(string RequestedId, MediaDevice[] Endpoints, string? EffectiveId,
        double? Volume, bool Writable, string? ActualId, string? ActualName, string? SwitchError, bool IsPlaying);

    private void UpdatePlaybackDevices()
    {
        if (_shuttingDown) return;
        _playbackRefreshPending = true;
        if (_playbackRefreshTask is { IsCompleted: false }) return;
        _playbackRefreshTask = RefreshPlaybackDevicesAsync();
    }

    private async Task RefreshPlaybackDevicesAsync()
    {
        try
        {
            while (_playbackRefreshPending && !_shuttingDown)
            {
                _playbackRefreshPending = false;
                var requestedId = Settings.PlaybackDeviceId;
                var previousId = _lastPlaybackEndpoint;
                var receiver = _audioReceiver;
                var token = _lifetimeCts.Token;
                var snapshot = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var endpoints = AudioEndpointCatalog.Enumerate().ToArray();
                    var defaultEndpoint = endpoints.FirstOrDefault(d => d.IsDefaultPlayback);
                    var effectiveId = requestedId.Length == 0 ? defaultEndpoint?.Id : requestedId;
                    token.ThrowIfCancellationRequested();
                    if (previousId != effectiveId || receiver?.OutputNeedsRetry == true) receiver?.RefreshOutput();
                    var volume = PlaybackVolumeController.Read(requestedId);
                    // Windows scalar volume support is already established by Read;
                    // CanWrite would open and read the same endpoint a second time.
                    var writable = OperatingSystem.IsMacOS() ? PlaybackVolumeController.CanWrite(requestedId)
                        : OperatingSystem.IsWindows() && volume.HasValue;
                    return new PlaybackSnapshot(requestedId, endpoints, effectiveId, volume, writable,
                        receiver?.ActualOutputId, receiver?.ActualOutputName, receiver?.OutputSwitchError, receiver?.IsPlaying == true);
                }, token).ConfigureAwait(true);
                if (_shuttingDown || token.IsCancellationRequested) return;
                if (Settings.PlaybackDeviceId != snapshot.RequestedId)
                { _playbackRefreshPending = true; continue; }
                ApplyPlaybackDevices(snapshot);
            }
        }
        catch (OperationCanceledException) when (_shuttingDown || _lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_shuttingDown) return;
            SharedAudioStatus = "播放输出刷新失败：" + ex.Message;
            AppendLog("[错误] " + SharedAudioStatus);
        }
    }

    private void ApplyPlaybackDevices(PlaybackSnapshot snapshot)
    {
        var endpoints = snapshot.Endpoints;
        if (_discovery is not null) _discovery.AudioOutputs = endpoints;
        var defaultEndpoint = endpoints.FirstOrDefault(d => d.IsDefaultPlayback);
        DefaultPlaybackText = defaultEndpoint is null ? "系统默认输出：当前不可用" : $"系统默认：{defaultEndpoint.Name}";
        var options = new List<PlaybackDeviceOption> { new("", $"系统默认 · {defaultEndpoint?.Name ?? "暂无可用输出"}", true, defaultEndpoint is not null) };
        options.AddRange(endpoints.OrderByDescending(d => d.IsDefaultPlayback).Select(PlaybackDeviceOption.From));
        if (snapshot.RequestedId.Length != 0 && options.All(o => o.Id != snapshot.RequestedId))
            options.Add(new(snapshot.RequestedId, "已指定的输出设备（离线）", false, false));
        var optionsChanged = !PlaybackDevices.SequenceEqual(options);
        var slotsChanged = optionsChanged || _lastPlaybackEndpoint != snapshot.EffectiveId;
        _lastPlaybackEndpoint = snapshot.EffectiveId;
        _updatingPlayback = true;
        try
        {
            if (optionsChanged)
            {
                PlaybackDevices.Clear();
                foreach (var option in options) PlaybackDevices.Add(option);
            }
            SelectedPlaybackDevice = PlaybackDevices.First(o => o.Id == snapshot.RequestedId);
            PlaybackSelectionText = snapshot.RequestedId.Length == 0
                ? $"联桥跟随系统默认 · {defaultEndpoint?.Name ?? "无可用设备"}"
                : $"联桥指定：{SelectedPlaybackDevice.Name}";
            foreach (var tile in Devices.Where(t => t.IsSpeaker))
            {
                var endpoint = endpoints.FirstOrDefault(d => d.Id == tile.Media?.Id);
                var isTarget = snapshot.RequestedId.Length == 0 ? endpoint?.IsDefaultPlayback == true : endpoint?.Id == snapshot.RequestedId;
                var badges = new List<string>();
                if (endpoint?.IsDefaultPlayback == true) badges.Add("系统默认");
                if (isTarget) badges.Add("已选输出");
                if (endpoint?.Id == snapshot.ActualId && endpoint is not null) badges.Add(snapshot.IsPlaying ? "正在播放" : "通道已就绪");
                tile.PlaybackBadge = string.Join(" · ", badges);
            }
            ActualPlaybackText = snapshot.ActualName is { } actual
                ? (snapshot.IsPlaying ? "共享音频正在播放：" : "共享输出已打开 · 等待音频首帧：") + actual : "共享音频尚未播放";
            if (snapshot.SwitchError is { } switchError) ActualPlaybackText += " · 切换未生效：" + switchError;
        }
        finally { _updatingPlayback = false; }
        ApplyPlaybackVolume(snapshot.Volume, snapshot.Writable);
        if (slotsChanged) NotifyDeviceSlots();
    }
    public ObservableCollection<PeerInfo> Peers { get; } = [];

    public ObservableCollection<string> Logs { get; } = [];

    public bool HasPeers => Peers.Count > 0;

    public bool HasDevices => Devices.Count > 0;

    public string DevicesSummary => $"已选择 {Devices.Count(d => d.IsSelected)} 个可发送设备";

    public string CenterSubtitle => $"{Devices.Count(d => d.IsSelected)} 个设备待发送";

    public string ShareableRatio => $"已选择 {Devices.Count(d => d.IsMediaShareable && d.IsSelected)} / {Devices.Count(d => d.IsMediaShareable)} 个可发送设备";

    [ObservableProperty]
    public partial bool IsSharing { get; set; }
    [ObservableProperty]
    public partial bool IsStartingShare { get; set; }

    [ObservableProperty]
    public partial bool IsReceiving { get; set; }

    [ObservableProperty]
    public partial bool IsControlling { get; set; }

    [ObservableProperty]
    public partial string InputStatusText { get; set; } = "跨屏连接中…";

    public void ReportMacInputAccess(bool allowed)
    {
        InputStatusText = allowed ? "Mac 在右侧 · 已允许 Windows 跨屏键鼠" : "Mac 键鼠控制待授权 · 请允许设备控制权限";
        AppendLog(allowed ? "[LinkBridge] 当前 Mac 应用已获设备控制权限。" : "[LinkBridge] 正在显示当前 Mac 应用的键鼠授权指引。" );
    }

    public void ReportWindowStartupError(Exception error)
    {
        StatusText = "界面启动失败：" + error.Message;
        AppendLog("[错误] " + StatusText);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial WriteableBitmap? PreviewImage { get; set; }
    public bool HasPreview => PreviewImage is not null;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "正在初始化…";

    [ObservableProperty]
    public partial string EngineText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SettingsFileHint { get; set; } = SettingsStore.FilePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsSaveError))]
    public partial string? SettingsSaveError { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPersistentLogError))]
    public partial string? PersistentLogError { get; set; }
    public bool HasSettingsSaveError => !string.IsNullOrWhiteSpace(SettingsSaveError);
    public bool HasPersistentLogError => !string.IsNullOrWhiteSpace(PersistentLogError);

    [ObservableProperty]
    public partial string MediaPortText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PeerInfo? SelectedPeer { get; set; }
    partial void OnSelectedPeerChanged(PeerInfo? value)
    {
        _macCameraCoordinator?.SetPreferredWindowsAddress(value?.Platform.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true ? value.Address : null);
    }

    public Task InitializeAsync() => _initializationTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        if (_shuttingDown) return;
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        var ffmpeg = FfmpegLocator.ExecutablePath;
        if (SettingsStore.LastLoadError is { } settingsError) AppendLog("[提示] " + settingsError);
        EngineText = ffmpeg is null ? "未找到 ffmpeg 引擎" : $"引擎就绪 · {PlatformInfo.DisplayName}";
        AppendLog(ffmpeg is null
            ? "[LinkBridge] 没有找到 ffmpeg，请先安装后再试。"
            : $"[LinkBridge] 使用引擎：{ffmpeg}");

        {
            _discovery = new PeerDiscovery(_activeMediaPort) { DisplayName = Settings.DisplayName, PairingSecret = Settings.InputPairingSecret, IsDiscoverable = Settings.AutoDiscover };
            _discovery.LogEmitted += AppendLog;
            if (PlatformInfo.IsMacOS || PlatformInfo.IsWindows)
            {
                _cameraProducer.LogEmitted += AppendLog;
                _cameraProducer.SourceEnded += (address, port, generation) =>
                {
                    var owned = Volatile.Read(ref _ownedCameraSource);
                    if (owned is null || owned.Address != address || owned.Port != port ||
                        (owned.ProducerGeneration != 0 && owned.ProducerGeneration != generation) || _cameraProducer.SourceGeneration != generation) return;
                    if (_discovery?.ReportSharedCameraSourceEnded(address, port, owned.CoreVersion) == true)
                    {
                        var current = Volatile.Read(ref _ownedCameraSource);
                        if (current?.CoreVersion == owned.CoreVersion && current.Address == address && current.Port == port)
                            Interlocked.CompareExchange(ref _ownedCameraSource, null, current);
                    }
                    var error = _cameraProducer.LastError;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (_shuttingDown || _cameraProducer.SourceGeneration != generation || _cameraProducer.IsRunning) return;
                        SharedCameraSourceStatusText = error is null ? "本机相机提供已停止" : "本机相机未就绪：" + error;
                    });
                };
                _discovery.SharedCameraStartRequested += (address, port) =>
                {
                    var camera = Volatile.Read(ref _selectedCameraSource);
                    var version = _discovery.GetSharedCameraSourceVersion(address, port);
                    if (camera is null || _shuttingDown || version is null) return Task.FromResult(false);
                    // Publish epoch ownership before Start can produce an immediate EOF.
                    var pending = new OwnedCameraSource(address, port, version.Value, 0);
                    Volatile.Write(ref _ownedCameraSource, pending);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        var current = Volatile.Read(ref _ownedCameraSource);
                        if (!_shuttingDown && current?.CoreVersion == pending.CoreVersion && current.Address == pending.Address && current.Port == pending.Port)
                            SharedCameraSourceStatusText = "本机相机正在确认原生 4K 与首帧…";
                    });
                    try
                    {
                        var ready = _cameraProducer.StartAsync(camera, address, port, _lifetimeCts.Token);
                        Interlocked.CompareExchange(ref _ownedCameraSource, pending with { ProducerGeneration = _cameraProducer.SourceGeneration }, pending);
                        return ReportCameraSourceReadinessAsync(ready, pending);
                    }
                    catch { Interlocked.CompareExchange(ref _ownedCameraSource, null, pending); throw; }
                };
                _discovery.SharedCameraStopRequested += address =>
                {
                    if (_cameraProducer.TargetAddress != address) return;
                    _cameraProducer.ProducerStopSync();
                    var generation = _cameraProducer.SourceGeneration;
                    var current = Volatile.Read(ref _ownedCameraSource);
                    if (current?.Address == address) Interlocked.CompareExchange(ref _ownedCameraSource, null, current);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (!_shuttingDown && _cameraProducer.SourceGeneration == generation && !_cameraProducer.IsRunning)
                            SharedCameraSourceStatusText = "本机相机提供已停止 · 等待对端应用调用";
                    });
                };
            }
            _discovery.AuthenticatedMediaStartRequested += address =>
            {
                if (_shuttingDown || !_receiveAllowed || !_receiver.TryAcceptAuthenticatedSource(address, _activeMediaPort, forceRestart: true)) return false;
                _receivedFirstFrame = false;
                AppendLog("[LinkBridge] 对端准备新流，接收器已重置");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_shuttingDown || !_receiveAllowed || !_receiver.IsListeningRequested || !_receiver.IsRunning || _receiver.ExpectedSourceAddress != address) return;
                    IsReceiving = true;
                    StatusText = "接收器已就绪 · 等待新流";
                });
                return true;
            };
            _discovery.PeersChanged += list =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => SyncPeers(list));
            _discovery.Start();
            AppendLog(Settings.AutoDiscover ? $"[LinkBridge] 已在局域网广播，本机名 {Settings.DisplayName}" : "[LinkBridge] 已隐藏发现广播，配对连接服务仍可用");
        }

        if (PlatformInfo.IsMacOS && Settings.EnableInputServer)
        {
            try
            {
                _inputServer = new InputBridgeServer(Settings.AllowedControllerAddress, Settings.InputPairingSecret);
                _inputServer.LogEmitted += AppendLog;
                _inputServer.ConnectionChanged += connected => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                { if (!_shuttingDown) IsPeerConnected = connected; });
                _inputServer.Start();
                InitializeMacFileDrag();
                InitializeSharedClipboard(Settings.AllowedControllerAddress);
                InputStatusText = MacInputInjector.IsTrusted()
                    ? "Mac 在右侧 · 接收 Windows 跨屏键鼠"
                    : "Mac 键鼠控制待授权 · 请允许设备控制权限";
            }
            catch (Exception ex) { AppendLog($"[错误] 键鼠接收服务启动失败：{ex.Message}"); }
        }

        // 发现对端后即可被动接收。否则发送方点「开始共享」时，目标端没有
        // 监听 45679，界面却宣称已经连上，造成单向的假成功。
        if (ffmpeg is not null)
        {
            StartReceiving();
        }

        await RefreshDevicesAsync().ConfigureAwait(true);
        if (_shuttingDown) return;
        var microphoneEndpoint = SharedMicrophoneEndpoints.Diagnose();
        SharedMicrophoneStatusText = microphoneEndpoint.Available
            ? $"应用麦克风入口：{microphoneEndpoint.ApplicationInputName} · 按需连接待验证"
            : "应用共享麦克风：独立虚拟音频后端尚未安装";
        if (!microphoneEndpoint.Available) AppendLog("[提示] " + microphoneEndpoint.Diagnostic);
        if (_discovery is not null && Settings.InputPairingSecret.Length == 32)
        {
            _microphoneCoordinator = new SharedMicrophoneCoordinator(_discovery, Settings.InputPairingSecret);
            _microphoneCoordinator.StatusChanged += status => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            { if (!_shuttingDown) SharedMicrophoneStatusText = status; });
            _microphoneCoordinator.SelectLocalSource(Devices.FirstOrDefault(d => d.IsMediaShareable && d.Kind == TileKind.Microphone && d.IsSelected)?.Media);
            _microphoneCoordinator.Start();
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _cameraBroker = new SharedCameraBroker();
                _cameraBroker.BrokerLogEmitted += AppendLog;
                _cameraBroker.StatusChanged += status => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    SharedCameraStatusText = status == SharedCameraStatus.Connected ? "共享摄像头正在输出原生 4K"
                        : status == SharedCameraStatus.WaitingForFrames ? "应用已调用 · 等待 Mac 原生 4K" : "应用共享摄像头待调用");
                _cameraBroker.Start();
                _cameraCoordinator = new SharedCameraCoordinator(_discovery!, _cameraBroker, _lifetimeCts.Token);
                _cameraCoordinator.StatusChanged += status => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                { if (!_shuttingDown) SharedCameraStatusText = status; });
                _cameraCoordinator.Start();
                SharedCameraStatusText = "共享摄像头服务就绪 · 等待应用入口";
            }
            catch (Exception ex) { SharedCameraStatusText = "摄像头服务不可用：" + ex.Message; AppendLog("[错误] " + SharedCameraStatusText); }
        }
        _playbackTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        if (PlatformInfo.IsMacOS && _discovery is not null && Settings.InputPairingSecret.Length == 32)
        {
            _macCameraCoordinator = new MacSharedCameraCoordinator(_discovery, lifetime: _lifetimeCts.Token);
            _macCameraCoordinator.StatusChanged += status => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_shuttingDown && _macCameraCoordinator.Status == status) SharedCameraStatusText = status;
            });
            _macCameraCoordinator.SetPreferredWindowsAddress(SelectedPeer?.Platform.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true ? SelectedPeer.Address : null);
            _macCameraCoordinator.Start();
        }
        _playbackTimer.Tick += (_, _) => UpdatePlaybackDevices();
        _playbackTimer.Start();
        _audioSender.LogEmitted += AppendLog;
        _audioSender.StreamEnded += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_shuttingDown || _speakerCoordinator is not null || _audioSender.IsRunning) return;
            IsSendingSystemAudio = false;
            SharedAudioStatus = "音频发送已断开，请重新连接";
        });
        if (PlatformInfo.IsMacOS && _discovery is not null && Settings.InputPairingSecret.Length == 32)
        {
            _speakerCoordinator = new SharedSpeakerCoordinator(_discovery, _audioSender, Settings.InputPairingSecret, _lifetimeCts.Token);
            _speakerCoordinator.StateChanged += state => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_shuttingDown || _speakerCoordinator.State.Revision != state.Revision) return;
                if (state.ManuallyOverridden && ShareSystemAudioAutomatically)
                {
                    _updatingSystemAudioPreference = true;
                    ShareSystemAudioAutomatically = false;
                    _updatingSystemAudioPreference = false;
                    Settings.ShareSystemAudioAutomatically = false;
                    SaveSettings();
                }
                IsSendingSystemAudio = state.IsSending;
                SharedAudioStatus = state.Message;
            });
            _speakerCoordinator.Start(ShareSystemAudioAutomatically);
        }
        if (PlatformInfo.IsWindows && Settings.InputPairingSecret.Length == 32)
        {
            try
            {
                _audioReceiver = new SharedAudioReceiver(Settings.InputPairingSecret,
                    ip => _discovery?.Snapshot().Any(p => p.Address == ip) == true,
                    () => Settings.PlaybackDeviceId);
                _audioReceiver.LogEmitted += AppendLog;
                _audioReceiver.ConnectionChanged += connected => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_shuttingDown) return;
                    UpdatePlaybackDevices();
                    SharedAudioStatus = _audioReceiver.ActualOutputId is not null
                        ? (_audioReceiver.IsPlaying ? "正在播放 Mac 音频" : "共享输出已打开 · 等待 Mac 音频首帧")
                        : "Windows 音响就绪 · 等待 Mac 音频";
                });
                _audioReceiver.Start();
                SharedAudioStatus = "Windows 音响就绪 · 等待 Mac 音频";
            }
            catch (Exception ex) { SharedAudioStatus = "共享音响启动失败：" + ex.Message; AppendLog("[错误] " + SharedAudioStatus); }
        }

        if (PlatformInfo.IsWindows && Settings.InputPairingSecret.Length == 32)
        {
            try
            {
                _inputCapture = new WindowsInputCapture(
                    packet => _inputClient?.TrySend(packet),
                    () => _inputClient?.IsConnected == true,
                    Settings.RemoteDisplayWidth,
                    Settings.RemoteDisplayHeight);
                _inputCapture.RemoteEntered += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    IsControlling = true;
                    InputStatusText = "正在控制 Mac · 向左滑回 Windows";
                    AppendLog($"[LinkBridge] 鼠标跨入 Mac，键盘已跟随（Windows X={_inputCapture?.LastEntryX}）");
                });
                _inputCapture.RemoteExited += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    IsControlling = false;
                    InputStatusText = "跨屏已就绪 · 鼠标向右进入 Mac";
                    AppendLog("[LinkBridge] 鼠标返回 Windows");
                });
                _inputCapture.ExitRequested += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    _inputCapture?.LeaveRemote());
                _inputCapture.Start();
                InitializeWindowsFileDrag();
                AppendLog($"[LinkBridge] Windows 屏幕边界 {_inputCapture.LocalWidth}×{_inputCapture.LocalHeight}");
                _inputTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _inputTimer.Tick += async (_, _) => await EnsureInputConnectionAsync();
                _inputTimer.Start();
                await EnsureInputConnectionAsync();
            }
            catch (Exception ex) { AppendLog($"[错误] 自动跨屏启动失败：{ex.Message}"); }
        }
    }

    private async Task<bool> ReportCameraSourceReadinessAsync(Task<bool> readiness, OwnedCameraSource source)
    {
        var ready = await readiness.ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var current = Volatile.Read(ref _ownedCameraSource);
            if (_shuttingDown || current is null || current.CoreVersion != source.CoreVersion ||
                current.Address != source.Address || current.Port != source.Port) return;
            SharedCameraSourceStatusText = ready && _cameraProducer.IsRunning
                ? "本机相机正在向对端提供原生 4K"
                : "本机相机未就绪：" + (_cameraProducer.LastError ?? "采集已停止或没有真实首帧");
        });
        return ready;
    }

    private async Task EnsureInputConnectionAsync()
    {
        if (_shuttingDown) return;
        if (_inputClient is not null)
        {
            _inputClient.TrySend(default); // keepalive; Mac ignores packet type 0
            return;
        }
        if (_connectingInput) return;
        var peer = Peers.FirstOrDefault(p => p.Platform.Contains("Mac", StringComparison.OrdinalIgnoreCase));
        if (peer is null) return;
        _connectingInput = true;
        try
        {
            var client = await InputBridgeClient.ConnectAsync(peer.Address, Settings.InputPairingSecret, _lifetimeCts.Token);
            if (_shuttingDown || !client.IsConnected) { await client.DisposeAsync(); return; }
            client.Disconnected += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_inputClient, client)) return;
                _inputCapture?.LeaveRemote();
                _inputClient = null;
                _filePeerAddress = null;
                IsPeerConnected = false;
                UpdateFileEdge();
                _ = client.DisposeAsync();
                InputStatusText = "连接已断开，正在重连…";
                AppendLog("[LinkBridge] Mac 键鼠连接断开，正在重连：" + client.DisconnectReason);
            });
            if (!client.IsConnected) { await client.DisposeAsync(); return; }
            _inputClient = client;
            _filePeerAddress = peer.Address;
            IsPeerConnected = true;
            InitializeSharedClipboard(peer.Address);
            UpdateFileEdge();
            _lastInputError = null;
            InputStatusText = "跨屏已就绪 · 鼠标向右进入 Mac";
            AppendLog($"[LinkBridge] Mac 跨屏键鼠已就绪：{peer.Address}");
        }
        catch (Exception ex)
        {
            if (_shuttingDown) return;
            InputStatusText = ex.Message.Contains("未允许此服务", StringComparison.Ordinal)
                ? "Mac 键鼠控制待授权 · 请允许设备控制权限"
                : "等待 Mac 跨屏连接…";
            if (_lastInputError != ex.Message)
            {
                _lastInputError = ex.Message;
                AppendLog($"[提示] Mac 键鼠暂未就绪：{ex.Message}");
            }
        }
        finally { _connectingInput = false; }
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        if (IsBusy || _shuttingDown)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在扫描设备…";

        try
        {
            var pending = new List<(TileKind Kind, string Name, string Subtitle, MediaDevice? Media)>();

            var enumerated = await _catalog.EnumerateAsync(_lifetimeCts.Token).ConfigureAwait(true);
            if (_shuttingDown) return;
            foreach (var device in enumerated)
            {
                var kind = device.Kind switch
                {
                    MediaDeviceKind.Camera => TileKind.Camera,
                    MediaDeviceKind.Microphone => TileKind.Microphone,
                    _ => TileKind.Speaker,
                };

                pending.Add((kind, device.Name, device.KindLabel, device));
            }
            if (_discovery is not null)
            {
                _discovery.Cameras = enumerated.Where(d => d.Kind == MediaDeviceKind.Camera).ToArray();
                _discovery.Microphones = enumerated.Where(d => d.Kind == MediaDeviceKind.Microphone).ToArray();
            }

            var extraDevices = await Task.Run(() =>
            {
                _lifetimeCts.Token.ThrowIfCancellationRequested();
                var inputs = InputDeviceCatalog.Enumerate().ToArray();
                _lifetimeCts.Token.ThrowIfCancellationRequested();
                var disks = EnumerateDisks().ToArray();
                return (Inputs: inputs, Disks: disks);
            }, _lifetimeCts.Token).ConfigureAwait(true);
            if (_shuttingDown) return;
            foreach (var input in extraDevices.Inputs)
            {
                var kind = input.Kind == InputDeviceKind.Keyboard ? TileKind.Keyboard : TileKind.Mouse;
                pending.Add((kind, input.Name, input.KindLabel, null));
            }

            foreach (var disk in extraDevices.Disks)
            {
                pending.Add((TileKind.Disk, disk.Name, disk.Subtitle, null));
            }

            Devices.Clear();
            for (var i = 0; i < pending.Count; i++)
            {
                var item = pending[i];
                var tile = new DeviceTile(item.Kind, item.Name, item.Subtitle, i, pending.Count, item.Media) { IsSelected = false };
                tile.PropertyChanged += (_, changed) =>
                {
                    if (changed.PropertyName != nameof(DeviceTile.IsSelected) || _updatingLocalSourceSelection) return;
                    UpdateLocalSourceSelection(tile);
                };
                Devices.Add(tile);
            }
            _updatingLocalSourceSelection = true;
            try
            {
                RestoreLocalSourceSelection(TileKind.Camera, Settings.ShareLocalCamera, Settings.PreferredCameraSource);
                RestoreLocalSourceSelection(TileKind.Microphone, Settings.ShareLocalMicrophone, Settings.PreferredMicrophoneSource);
            }
            finally { _updatingLocalSourceSelection = false; }

            // Pin a valid first-use choice once. Subsequent hot-plugs must not
            // silently swap to a different device that inherited the same index.
            if (SettingsStore.LastLoadError is null)
            {
                var pinned = false;
                var initialCamera = Devices.FirstOrDefault(d => d.Kind == TileKind.Camera && d.IsMediaShareable && d.IsSelected)?.Media;
                var initialMicrophone = Devices.FirstOrDefault(d => d.Kind == TileKind.Microphone && d.IsMediaShareable && d.IsSelected)?.Media;
                if (Settings.PreferredCameraSource.Length == 0 && initialCamera is not null)
                { Settings.PreferredCameraSource = LocalSourceKey(initialCamera); pinned = true; }
                if (Settings.PreferredMicrophoneSource.Length == 0 && initialMicrophone is not null)
                { Settings.PreferredMicrophoneSource = LocalSourceKey(initialMicrophone); pinned = true; }
                if (pinned) SaveSettings();
            }

            NotifyCounters();
            UpdatePlaybackDevices();
            StatusText = $"就绪 · 共 {Devices.Count} 个设备";
            AppendLog($"[LinkBridge] 扫描完成，共 {Devices.Count} 个设备");
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败";
            AppendLog($"[错误] 扫描设备失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string LocalSourceKey(MediaDevice device) => System.Text.Json.JsonSerializer.Serialize(
        new[] { device.Backend.ToString(), device.Id, device.Name });
    private void RestoreLocalSourceSelection(TileKind kind, bool enabled, string key)
    {
        if (!enabled) return;
        var candidates = Devices.Where(d => d.IsMediaShareable && !d.IsRemote && d.Kind == kind && d.Media is not null).ToArray();
        var chosen = key.Length == 0 ? candidates.FirstOrDefault() : candidates.FirstOrDefault(d => LocalSourceKey(d.Media!) == key);
        if (chosen is not null) chosen.IsSelected = true;
        else if (key.Length != 0) AppendLog(kind == TileKind.Camera
            ? "[提示] 之前选择的相机已不可用，请重新选择；不会自动改用另一相机。"
            : "[提示] 之前选择的麦克风已不可用，请重新选择；不会自动改用另一麦克风。");
    }
    private void UpdateLocalSourceSelection(DeviceTile changed)
    {
        if (changed.IsMediaShareable && !changed.IsRemote && changed.Media is not null && changed.Kind is TileKind.Camera or TileKind.Microphone)
        {
            _updatingLocalSourceSelection = true;
            try
            {
                if (changed.IsSelected)
                    foreach (var other in Devices.Where(d => !ReferenceEquals(d, changed) && d.Kind == changed.Kind && d.IsMediaShareable))
                        other.IsSelected = false;
                if (changed.Kind == TileKind.Camera)
                {
                    Settings.ShareLocalCamera = Devices.Any(d => d.Kind == TileKind.Camera && d.IsMediaShareable && d.IsSelected);
                    if (changed.IsSelected) Settings.PreferredCameraSource = LocalSourceKey(changed.Media);
                }
                else
                {
                    Settings.ShareLocalMicrophone = Devices.Any(d => d.Kind == TileKind.Microphone && d.IsMediaShareable && d.IsSelected);
                    if (changed.IsSelected) Settings.PreferredMicrophoneSource = LocalSourceKey(changed.Media);
                }
                SaveSettings();
            }
            finally { _updatingLocalSourceSelection = false; }
        }
        NotifyCounters();
    }

    /// <summary>
    /// 只列出「外置」存储，内置盘不参与共享。
    /// Windows 上 USB 移动硬盘的 DriveType 同样是 Fixed，必须靠 WMI 查总线类型区分。
    /// </summary>
    private static IEnumerable<(string Name, string Subtitle)> EnumerateDisks()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            if (!IsExternalDrive(drive))
            {
                continue;
            }

            var letter = drive.Name.TrimEnd('\\', '/');
            var name = drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? drive.VolumeLabel
                : letter;

            var subtitle = drive.IsReady
                ? $"存储设备 · {drive.TotalSize / 1024d / 1024 / 1024:F0} GB"
                : "存储设备 · 未就绪";

            yield return (name, subtitle);
        }
    }

    private static bool IsExternalDrive(DriveInfo drive)
    {
        if (drive.DriveType == DriveType.Removable)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows() || drive.DriveType != DriveType.Fixed)
        {
            return false;
        }

        try
        {
            var letter = drive.Name.TrimEnd('\\', '/');

            using var partitions = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{letter}'}} " +
                "WHERE AssocClass=Win32_LogicalDiskToPartition");

            partitions.Options.Timeout = TimeSpan.FromSeconds(2);
            using var partitionResults = partitions.Get();
            foreach (var partition in partitionResults.Cast<ManagementBaseObject>())
            using (partition)
            {
                var partitionId = partition["DeviceID"]?.ToString();
                if (partitionId is null)
                {
                    continue;
                }

                using var disks = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionId}'}} " +
                    "WHERE AssocClass=Win32_DiskDriveToDiskPartition");

                disks.Options.Timeout = TimeSpan.FromSeconds(2);
                using var diskResults = disks.Get();
                foreach (var disk in diskResults.Cast<ManagementBaseObject>())
                using (disk)
                {
                    if (string.Equals(disk["InterfaceType"]?.ToString(), "USB", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // 查不出来就当内置，宁可不列
        }

        return false;
    }

    [RelayCommand]
    private void ToggleDevice(DeviceTile tile)
    {
        if (tile.Media?.IsApplicationEndpoint == true) { SharedCameraStatusText = "应用中选择联桥共享摄像头，会按需接入 Mac 原生 4K"; return; }
        if (tile.IsRemote) { SharedCameraStatusText = $"{tile.Name} · {tile.OwnerLabel} · 可在共享摄像头服务中使用"; return; }
        if (tile.Kind is not (TileKind.Camera or TileKind.Microphone))
        {
            AppendLog($"[提示] {tile.Name} 当前仅能识别，尚未提供可用的跨机共享通道。");
            return;
        }
        tile.IsSelected = !tile.IsSelected;
        NotifyCounters();
        AppendLog(tile.IsSelected ? $"{tile.Name} 已选择" : $"{tile.Name} 已取消选择");
    }

    [RelayCommand]
    private async Task StartSharing()
    {
        if (_shuttingDown) return;
        var camera = Devices.FirstOrDefault(d => d.IsMediaShareable && d.Kind == TileKind.Camera && d.IsSelected)?.Media;
        var microphone = Devices.FirstOrDefault(d => d.IsMediaShareable && d.Kind == TileKind.Microphone && d.IsSelected)?.Media;

        if (camera is null && microphone is null)
        {
            AppendLog("[提示] 请至少选中一个相机或麦克风。");
            return;
        }

        var peer = SelectedPeer ?? Peers.FirstOrDefault();
        if (peer is null)
        {
            AppendLog("[提示] 还没有发现对端。请确认两台机器在同一局域网，并且都开着 LinkBridge。");
            return;
        }

        _shareStartCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _shareStartCancellation = cancellation;
        var generation = ++_shareGeneration;
        IsStartingShare = true;
        try
        {
            if (_discovery is null ||
                !await _discovery.PrepareRemoteReceiverAsync(peer.Address, cancellation.Token))
            {
                AppendLog("[错误] 对端接收器未确认就绪，请更新两端版本并检查局域网发现。");
                StatusText = "对端未确认接收";
                return;
            }
            if (_shuttingDown || cancellation.IsCancellationRequested || generation != _shareGeneration) return;
            _session.Start(camera, microphone, peer.Address, peer.MediaPort, Settings);
            SelectedPeer = peer;
            IsSharing = true;
            StatusText = $"正在发送给 {peer.Name} · 请在对端确认收到画面";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppendLog($"[错误] 启动失败：{ex.Message}");
            StatusText = "启动失败";
        }
        finally
        {
            if (ReferenceEquals(_shareStartCancellation, cancellation))
            { _shareStartCancellation = null; IsStartingShare = false; }
        }
    }

    [RelayCommand]
    private void StopSharing()
    {
        _shareGeneration++;
        _shareStartCancellation?.Cancel();
        IsStartingShare = false;
        _session.Stop();
        IsSharing = false;
        StatusText = "已停止共享";
    }

    [RelayCommand]
    private void StartReceiving()
    {
        try
        {
            if (_shuttingDown) return;
            _receiveAllowed = true;
            _receiver.Start(_activeMediaPort);
            _receivedFirstFrame = false;
            IsReceiving = true;
            StatusText = $"接收器就绪 · 等待端口 {_activeMediaPort} 的配对媒体流";
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 启动接收失败：{ex.Message}");
            StatusText = "接收启动失败";
        }
    }

    [RelayCommand]
    private void StopReceiving()
    {
        _receiveAllowed = false;
        _discovery?.ReportMediaReceiverStopped();
        _receiver.Stop();
        IsReceiving = false;
        PreviewImage = null;
        StatusText = "已停止接收";
    }

    /// <summary>Called by the view when no preview is visible; decoding/4K device use stays independent.</summary>
    public void SetPreviewVisible(bool visible)
    {
        _previewVisible = visible;
        if (!visible) Interlocked.Exchange(ref _pendingPreviewFrame, null)?.Dispose();
    }
    private void OnFrameDecoded(PreviewFrame frame)
    {
        if (_shuttingDown || !IsReceiving) { frame.Dispose(); return; }
        if (!_receivedFirstFrame)
        {
            _receivedFirstFrame = true;
            AppendLog($"[LinkBridge] 已收到对端视频首帧 · {frame.Width}×{frame.Height}");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = "已收到对端视频");
        }
        if (!_previewVisible) { frame.Dispose(); return; }
        Interlocked.Exchange(ref _pendingPreviewFrame, frame)?.Dispose();
        if (!_previewVisible) { Interlocked.Exchange(ref _pendingPreviewFrame, null)?.Dispose(); return; }
        if (Interlocked.Exchange(ref _previewUpdateQueued, 1) == 0)
            Avalonia.Threading.Dispatcher.UIThread.Post(RenderLatestPreview);
    }

    private void RenderLatestPreview()
    {
        using var frame = Interlocked.Exchange(ref _pendingPreviewFrame, null);
        try
        {
            if (frame is not null && IsReceiving && !_shuttingDown && _previewVisible)
            {
                var size = new PixelSize(frame.Width, frame.Height);
                if (_previewBitmapA?.PixelSize != size)
                {
                    PreviewImage = null;
                    _previewBitmapA?.Dispose(); _previewBitmapB?.Dispose();
                    _previewBitmapA = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                    _previewBitmapB = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                }
                _previewBufferIndex = !_previewBufferIndex;
                var bitmap = _previewBufferIndex ? _previewBitmapA! : _previewBitmapB!;
                using (var buffer = bitmap.Lock())
                {
                    if (buffer.RowBytes == frame.Width * 4)
                        Marshal.Copy(frame.Data, 0, buffer.Address, frame.Length);
                    else
                        for (var row = 0; row < frame.Height; row++)
                            Marshal.Copy(frame.Data, row * frame.Width * 4, buffer.Address + row * buffer.RowBytes, frame.Width * 4);
                }
                PreviewImage = bitmap;
            }
        }
        catch (Exception ex) { AppendLog($"[错误] 渲染预览失败：{ex.Message}"); }
        finally
        {
            Volatile.Write(ref _previewUpdateQueued, 0);
            if (!_previewVisible) Interlocked.Exchange(ref _pendingPreviewFrame, null)?.Dispose();
            if (!_shuttingDown && _previewVisible && _pendingPreviewFrame is not null && Interlocked.Exchange(ref _previewUpdateQueued, 1) == 0)
                Avalonia.Threading.Dispatcher.UIThread.Post(RenderLatestPreview);
        }
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private void ClearLogs() => Logs.Clear();

    [RelayCommand]
    private void RevealSettingsFile()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsStore.FilePath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);

            if (PlatformInfo.IsMacOS)
            {
                Process.Start("open", directory);
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 打开目录失败：{ex.Message}");
        }
    }

    partial void OnIsSettingsOpenChanged(bool value)
    {
        if (!value)
        {
            if (Settings.LaunchAtStartup != _startupPreference)
            {
                if (StartupRegistration.Apply(Settings.LaunchAtStartup, out var error))
                    _startupPreference = Settings.LaunchAtStartup;
                else
                {
                    Settings.LaunchAtStartup = _startupPreference;
                    AppendLog("[错误] 登录启动设置未生效：" + error);
                }
            }
            var saved = SaveSettings();
            if (_discovery is not null)
            {
                _discovery.DisplayName = Settings.DisplayName;
                _discovery.IsDiscoverable = Settings.AutoDiscover;
            }
            LocalSummary = $"{Settings.DisplayName} · {PlatformInfo.DisplayName}";
            if (saved) AppendLog("[LinkBridge] 设置已保存");
        }
    }

    partial void OnMediaPortTextChanged(string value)
    {
        if (int.TryParse(value, out var port) && port is > 1024 and < 65536)
        {
            Settings.MediaPort = port;
        }
    }

    private bool SaveSettings()
    {
        if (SettingsStore.TrySave(Settings, out var error)) { SettingsSaveError = null; return true; }
        SettingsSaveError = "设置未能保存：" + error;
        StatusText = "设置未能保存 · 请查看运行日志";
        AppendLog("[错误] 设置保存失败：" + error);
        return false;
    }

    private void NotifyCounters()
    {
        var selected = Devices.FirstOrDefault(d => d.IsMediaShareable && d.Kind == TileKind.Camera && d.IsSelected)?.Media;
        var previous = Interlocked.Exchange(ref _selectedCameraSource, selected);
        if (previous?.Id != selected?.Id || previous?.Backend != selected?.Backend || previous?.Name != selected?.Name)
        {
            _cameraProducer.ProducerStopSync();
            // A source callback may have published a new epoch while Stop waited
            // for the producer lock; invalidate the owner captured after the stop.
            var owned = Interlocked.Exchange(ref _ownedCameraSource, null);
            if (owned is not null) _discovery?.ReportSharedCameraSourceEnded(owned.Address, owned.Port, owned.CoreVersion);
            SharedCameraSourceStatusText = selected is not null
                ? "本机相机已选择 · 等待对端应用调用"
                : Settings.ShareLocalCamera ? "之前选择的本机相机不可用 · 请重新选择" : "本机相机提供已暂停";
        }
        _microphoneCoordinator?.SelectLocalSource(Devices.FirstOrDefault(d => d.IsMediaShareable && d.Kind == TileKind.Microphone && d.IsSelected)?.Media);
        OnPropertyChanged(nameof(MicrophoneSelectionText));
        OnPropertyChanged(nameof(DevicesSummary));
        OnPropertyChanged(nameof(CenterSubtitle));
        OnPropertyChanged(nameof(ShareableRatio));
    }

    private void SyncPeers(IReadOnlyList<PeerInfo> list)
    {
        if (_shuttingDown) return;
        var remoteCameraInventory = list.SelectMany(p => p.Cameras.Select(d => (Device: d, Peer: p))).Take(16).ToArray();
        if (!_remoteCameras.Select(c => c.Name + c.RemoteOwner).SequenceEqual(remoteCameraInventory.Select(c => c.Device.Name + c.Peer.Platform + " · " + c.Peer.Name)))
        {
            _remoteCameras.Clear();
            foreach (var item in remoteCameraInventory)
                _remoteCameras.Add(new DeviceTile(TileKind.Camera, item.Device.Name, "对端摄像头", 0, 1, item.Device, item.Peer.Platform + " · " + item.Peer.Name) { IsSelected = false });
            NotifyDeviceSlots();
        }
        var remoteAudio = list.SelectMany(p => p.AudioOutputs.Where(d => d is not null).Select(d => new PlaybackDeviceOption(
            d.Id, d.Name, d.IsDefaultPlayback, true, p.Platform + " · " + p.Name))).ToArray();
        if (!RemoteAudioDevices.SequenceEqual(remoteAudio))
        {
            RemoteAudioDevices.Clear();
            foreach (var device in remoteAudio) RemoteAudioDevices.Add(device);
        }
        if (Peers.Count == list.Count && Peers.Zip(list).All(pair =>
            pair.First.Key == pair.Second.Key && pair.First.Platform == pair.Second.Platform &&
            pair.First.MediaPort == pair.Second.MediaPort && pair.First.HasSharedMicrophoneInput == pair.Second.HasSharedMicrophoneInput &&
            pair.First.CameraControlProtocolVersion == pair.Second.CameraControlProtocolVersion &&
            pair.First.AudioOutputs.SequenceEqual(pair.Second.AudioOutputs) && pair.First.Cameras.SequenceEqual(pair.Second.Cameras) &&
            pair.First.Microphones.SequenceEqual(pair.Second.Microphones)))
        {
            return;
        }

        var selectedKey = SelectedPeer?.Key;

        Peers.Clear();
        foreach (var peer in list)
        {
            Peers.Add(peer);
        }

        SelectedPeer = Peers.FirstOrDefault(p => p.Key == selectedKey) ?? Peers.FirstOrDefault();
    }

    private void AppendLog(string line)
    {
        var entry = $"{DateTime.Now:HH:mm:ss}  {line}";
        _productLog.Append(entry);
        lock (_uiLogGate)
        {
            if (_shuttingDown) return;
            if (_pendingUiLogs.Count >= 200) _pendingUiLogs.Dequeue();
            _pendingUiLogs.Enqueue(entry);
            if (_logDispatchScheduled) return;
            _logDispatchScheduled = true;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            string[] batch;
            lock (_uiLogGate)
            {
                batch = _pendingUiLogs.ToArray(); _pendingUiLogs.Clear(); _logDispatchScheduled = false;
            }
            if (_shuttingDown) return;
            foreach (var item in batch) { Logs.Add(item); if (Logs.Count > 200) Logs.RemoveAt(0); }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public ValueTask ShutdownAsync() => new(_shutdownTask ??= ShutdownCoreAsync());

    private async Task ShutdownCoreAsync()
    {
        _shuttingDown = true;
        _receiveAllowed = false;
        _discovery?.ReportMediaReceiverStopped();
        _shareGeneration++;
        _shareStartCancellation?.Cancel();
        _lifetimeCts.Cancel();
        _cameraProducer.ProducerStopSync();
        if (OperatingSystem.IsWindows() && _cameraBroker is not null) _cameraBroker.ExpectedSourceAddress = null;
        Interlocked.Exchange(ref _pendingPreviewFrame, null)?.Dispose();
        PreviewImage = null;
        _previewBitmapA?.Dispose(); _previewBitmapB?.Dispose();
        _playbackTimer?.Stop();
        _floatTimer.Stop();
        _inputTimer?.Stop();
        _inputCapture?.Dispose();
        _inputCapture = null;
        // 窗口关闭后应用可能立即退出，必须在第一个 await 前结束子进程。
        _session.Dispose();
        _receiver.Dispose();
        // Invoke all synchronous cancellation prefixes before awaiting any slow cleanup.
        var stops = new List<Task>();
        stops.Add(StopFileDragAsync());
        stops.Add(StopSharedClipboardAsync());
        if (_speakerCoordinator is not null) stops.Add(_speakerCoordinator.DisposeAsync().AsTask());
        stops.Add(_audioSender.DisposeAsync().AsTask());
        stops.Add(_cameraProducer.DisposeAsync().AsTask());
        if (_playbackRefreshTask is not null) stops.Add(_playbackRefreshTask);
        if (_cameraCoordinator is not null) stops.Add(_cameraCoordinator.DisposeAsync().AsTask());
        if (_macCameraCoordinator is not null) stops.Add(_macCameraCoordinator.DisposeAsync().AsTask());
        if (_microphoneCoordinator is not null) stops.Add(_microphoneCoordinator.DisposeAsync().AsTask());
        if (OperatingSystem.IsWindows() && _cameraBroker is not null) stops.Add(_cameraBroker.DisposeAsync().AsTask());
        if (_audioReceiver is not null) stops.Add(_audioReceiver.DisposeAsync().AsTask());
        if (_inputClient is not null) stops.Add(_inputClient.DisposeAsync().AsTask());
        if (_inputServer is not null) stops.Add(_inputServer.DisposeAsync().AsTask());
        _inputClient = null;
        if (_initializationTask is not null) stops.Add(_initializationTask.WaitAsync(TimeSpan.FromSeconds(3)));
        var discovery = _discovery;
        if (discovery is not null) stops.Add(discovery.DisposeAsync().AsTask());
        try { await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(7)); }
        catch (Exception ex) { AppendLog("[提示] 连接清理：" + ex.Message); }
        _discovery = null;
        await _productLog.DisposeAsync();
    }
}

