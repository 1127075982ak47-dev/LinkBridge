using System.Security.Cryptography;
using System.Threading.Channels;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkBridge.App.Services;
using LinkBridge.Core.Clipboard;
using LinkBridge.Input;

namespace LinkBridge.App.ViewModels;

public partial class MainViewModel
{
    private WindowsSharedClipboard? _windowsClipboard;
    private MacSharedClipboard? _macClipboard;
    private ClipboardBridgeServer? _clipboardServer;
    private readonly ClipboardBridgeSender _clipboardSender = new();
    private readonly Channel<SharedClipboardContent> _clipboardOutgoing = Channel.CreateBounded<SharedClipboardContent>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly object _clipboardGate = new();
    private string? _clipboardAddress, _clipboardDigest;
    private CancellationTokenSource? _clipboardSendCancellation;
    private CancellationTokenSource? _clipboardReceiveCancellation;
    private Task? _clipboardWorker;
    private long _clipboardRevision;
    private SharedClipboardContent? _lastLocalClipboard;

    [ObservableProperty] private bool _isPeerConnected;
    [ObservableProperty] private bool _shareClipboardAutomatically = true;
    [ObservableProperty] private string _clipboardStatus = "等待配对电脑";
    [ObservableProperty] private bool _hasClipboardSendError;
    [ObservableProperty] private bool _isMotionReduced;
    public string ProductVersion => "0.0.1";
    public double PeerIllustrationOpacity => IsPeerConnected ? 1 : .35;
    public bool CanCaptureMac => ShareClipboardAutomatically && (OperatingSystem.IsMacOS() || IsPeerConnected);
    public Geometry ClipboardIcon => Geometry.Parse("M8 4 H5 V21 H19 V4 H16 M8 2 H16 V6 H8 Z M8 10 H16 M8 14 H16 M8 18 H13");
    partial void OnIsMotionReducedChanged(bool value) { Settings.ReduceMotion = value; SaveSettings(); }
    partial void OnIsPeerConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCaptureMac));
        OnPropertyChanged(nameof(PeerIllustrationOpacity));
    }
    partial void OnShareClipboardAutomaticallyChanged(bool value)
    {
        Settings.ShareClipboardAutomatically = value;
        SaveSettings();
        lock (_clipboardGate)
        {
            _clipboardRevision++; _clipboardSendCancellation?.Cancel(); _clipboardReceiveCancellation?.Cancel();
            if (!value) { _lastLocalClipboard = null; _clipboardDigest = null; }
            while (_clipboardOutgoing.Reader.TryRead(out _)) { }
        }
        HasClipboardSendError = false;
        ClipboardStatus = value ? (_clipboardServer is null ? "等待配对电脑" : "已开启 · 复制文字或图片即可") : "取材已暂停";
        OnPropertyChanged(nameof(CanCaptureMac));
    }

    private void InitializeSharedClipboard(string address)
    {
        if (_shuttingDown || _clipboardServer is not null || Settings.InputPairingSecret.Length != 32) return;
        try
        {
            _clipboardAddress = address;
            if (OperatingSystem.IsWindows())
            {
                _windowsClipboard = new WindowsSharedClipboard();
                _windowsClipboard.Changed += OnLocalClipboardChanged;
                _windowsClipboard.Diagnostic += message => AppendLog("[取材] " + message);
                _windowsClipboard.Start();
            }
            else if (OperatingSystem.IsMacOS())
            {
                _macClipboard = new MacSharedClipboard();
                _macClipboard.Changed += OnLocalClipboardChanged;
                _macClipboard.Diagnostic += message => AppendLog("[取材] " + message);
                _macClipboard.Start();
            }
            else return;
            _clipboardServer = new ClipboardBridgeServer(address, Settings.InputPairingSecret, ApplyRemoteClipboardAsync);
            _clipboardServer.Diagnostic += message => AppendLog("[取材] " + message);
            _clipboardServer.Start();
            _clipboardWorker = Task.Run(SendClipboardLoopAsync);
            ClipboardStatus = ShareClipboardAutomatically ? "已开启 · 复制文字或图片即可" : "取材已暂停";
            AppendLog("[取材] 文字与无损图片通道已就绪 · LBC1 TLS");
        }
        catch (Exception error)
        {
            _windowsClipboard?.Dispose(); _windowsClipboard = null;
            _macClipboard?.Dispose(); _macClipboard = null;
            if (_clipboardServer is not null) _ = _clipboardServer.DisposeAsync();
            _clipboardServer = null;
            ClipboardStatus = "取材暂不可用";
            AppendLog("[取材] 服务启动失败：" + error.GetType().Name);
        }
    }

    private static string ClipboardDigest(SharedClipboardContent content) => content.Kind + ":" + Convert.ToHexString(SHA256.HashData(content.Data));
    private void OnLocalClipboardChanged(SharedClipboardContent content)
    {
        if (_shuttingDown || !ShareClipboardAutomatically || _clipboardAddress is null) return;
        var digest = ClipboardDigest(content);
        lock (_clipboardGate)
        {
            if (_shuttingDown || !ShareClipboardAutomatically) return;
            _clipboardDigest = digest;
            _lastLocalClipboard = content;
            _clipboardRevision++;
            _clipboardSendCancellation?.Cancel();
            _clipboardReceiveCancellation?.Cancel();
            _clipboardOutgoing.Writer.TryWrite(content);
        }
    }

    private async Task SendClipboardLoopAsync()
    {
        try
        {
            await foreach (var content in _clipboardOutgoing.Reader.ReadAllAsync(_lifetimeCts.Token))
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                long revision;
                lock (_clipboardGate)
                {
                    if (!ShareClipboardAutomatically || ClipboardDigest(content) != _clipboardDigest) continue;
                    revision = _clipboardRevision;
                    _clipboardSendCancellation = cancellation;
                }
                try
                {
                    await _clipboardSender.SendAsync(_clipboardAddress!, Settings.InputPairingSecret,
                        new ClipboardPayload(Guid.NewGuid(), content.Kind, content.Data), cancellation.Token).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_shuttingDown || revision != _clipboardRevision) return;
                        ClipboardStatus = content.Kind == ClipboardKinds.Png ? "图片已送达 · 可在另一台粘贴" : "文字已送达 · 可在另一台粘贴";
                        HasClipboardSendError = false;
                    });
                }
                catch (OperationCanceledException) { }
                catch
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!_shuttingDown && revision == _clipboardRevision)
                        {
                            ClipboardStatus = "未送达 · 确认另一台联桥已打开";
                            HasClipboardSendError = true;
                        }
                    });
                    AppendLog("[取材] 本次内容未送达，系统剪贴板保留");
                }
                finally { lock (_clipboardGate) if (ReferenceEquals(_clipboardSendCancellation, cancellation)) _clipboardSendCancellation = null; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ApplyRemoteClipboardAsync(ClipboardPayload payload, CancellationToken ct)
    {
        if (_shuttingDown || !ShareClipboardAutomatically) throw new InvalidOperationException("取材已暂停");
        var content = new SharedClipboardContent(payload.Kind, payload.Data);
        var digest = ClipboardDigest(content);
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
        long revision;
        lock (_clipboardGate)
        {
            if (_shuttingDown || !ShareClipboardAutomatically) throw new InvalidOperationException("取材已暂停");
            _clipboardDigest = digest;
            revision = ++_clipboardRevision;
            _clipboardSendCancellation?.Cancel();
            _clipboardReceiveCancellation = receiving;
            _lastLocalClipboard = null;
        }
        try
        {
            receiving.Token.ThrowIfCancellationRequested();
            if (_windowsClipboard is not null) await _windowsClipboard.ApplyAsync(content, receiving.Token).ConfigureAwait(false);
            else if (_macClipboard is not null) await _macClipboard.ApplyAsync(content, receiving.Token).ConfigureAwait(false);
            else throw new InvalidOperationException("系统剪贴板服务未启动");
        }
        catch
        {
            lock (_clipboardGate) if (_clipboardDigest == digest) _clipboardDigest = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_shuttingDown || !ShareClipboardAutomatically || revision != _clipboardRevision) return;
                ClipboardStatus = "本次取材未接收 · 原剪贴板保留";
                HasClipboardSendError = false;
            });
            throw;
        }
        finally { lock (_clipboardGate) if (ReferenceEquals(_clipboardReceiveCancellation, receiving)) _clipboardReceiveCancellation = null; }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_shuttingDown || !ShareClipboardAutomatically || revision != _clipboardRevision) return;
            ClipboardStatus = payload.Kind == ClipboardKinds.Png ? "收到图片 · 直接粘贴" : "收到文字 · 直接粘贴";
            HasClipboardSendError = false;
        });
    }

    [RelayCommand]
    private void RetryClipboardSend()
    {
        lock (_clipboardGate)
        {
            if (!ShareClipboardAutomatically || _lastLocalClipboard is null || ClipboardDigest(_lastLocalClipboard) != _clipboardDigest) return;
            _clipboardRevision++;
            _clipboardOutgoing.Writer.TryWrite(_lastLocalClipboard);
        }
        ClipboardStatus = "正在重新送出…";
        HasClipboardSendError = false;
    }

    [RelayCommand]
    private void CaptureMacMaterial()
    {
        if (!CanCaptureMac || IsFileTransferBusy) return;
        if (_macClipboard is not null) _macClipboard.BeginScreenshot();
        else
        {
            if (_inputCapture?.TryEnterRemoteForScreenshot() != true)
            { ClipboardStatus = "请先松开鼠标和键盘，再开始取材"; return; }
            ushort[] keys = [0x11, 0x10, 0x5B, 0x34];
            foreach (var key in keys) _inputClient?.TrySend(new InputPacket(InputEventType.Key, 0, key, 0, 0));
            foreach (var key in keys.Reverse()) _inputClient?.TrySend(new InputPacket(InputEventType.Key, 1, key, 0, 0));
        }
        ClipboardStatus = "在 Mac 划选区域 · Esc 取消";
    }

    private async Task StopSharedClipboardAsync()
    {
        _clipboardOutgoing.Writer.TryComplete();
        lock (_clipboardGate) { _clipboardSendCancellation?.Cancel(); _clipboardReceiveCancellation?.Cancel(); }
        _windowsClipboard?.Dispose(); _macClipboard?.Dispose();
        if (_clipboardServer is not null) await _clipboardServer.DisposeAsync();
        if (_clipboardWorker is not null) await _clipboardWorker;
    }
}
