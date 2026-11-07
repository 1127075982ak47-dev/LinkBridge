using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkBridge.Core;

namespace LinkBridge.App.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private bool _isConnectingPeer;
    [ObservableProperty] private string _connectionActionStatus = "";
    public string ConnectPeerButtonText => IsConnectingPeer ? "连接中…" : "一键连接";
    public string ConnectionSummaryText => string.IsNullOrEmpty(ConnectionActionStatus) ? InputStatusText : ConnectionActionStatus;
    partial void OnIsConnectingPeerChanged(bool value) => OnPropertyChanged(nameof(ConnectPeerButtonText));
    partial void OnConnectionActionStatusChanged(string value) => OnPropertyChanged(nameof(ConnectionSummaryText));
    partial void OnInputStatusTextChanged(string value)
    {
        if (!IsConnectingPeer && IsPeerConnected) ConnectionActionStatus = "";
        OnPropertyChanged(nameof(ConnectionSummaryText));
    }
    private bool _handlingConnectionRequest;

    private async Task HandleConnectionRequestAsync(string address)
    {
        if (_shuttingDown || _handlingConnectionRequest || _discovery is null) return;
        if (PlatformInfo.IsMacOS && address != Settings.AllowedControllerAddress) return;
        _handlingConnectionRequest = true;
        try
        {
            await _discovery.AnnounceToAsync(address, _lifetimeCts.Token);
            if (PlatformInfo.IsWindows) await EnsureInputConnectionAsync(address);
            else if (ShareSystemAudioAutomatically && !_audioSender.IsRunning) _speakerCoordinator?.Enable();
            AppendLog("[连接] 已响应另一台电脑的一键连接请求");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_shuttingDown) AppendLog("[连接] 连接请求未完成：" + ex.Message); }
        finally { _handlingConnectionRequest = false; }
    }

    [RelayCommand]
    private async Task ConnectPeerAsync()
    {
        if (_shuttingDown || IsConnectingPeer) return;
        if (Workspace.Enabled)
        {
            IsConnectingPeer = true;
            try { await StartWorkspaceAsync(); }
            finally { IsConnectingPeer = false; }
            return;
        }
        IsConnectingPeer = true;
        ConnectionActionStatus = "正在寻找另一台电脑并建立连接…";
        AppendLog("[连接] 用户点击一键连接");
        try
        {
            if (_initializationTask is { IsCompleted: false })
                await _initializationTask.WaitAsync(TimeSpan.FromSeconds(8), _lifetimeCts.Token);
            if (_discovery is null) throw new InvalidOperationException("连接服务尚未就绪，请稍后再试。");
            if (Settings.InputPairingSecret.Length != 32) throw new InvalidOperationException("尚未配对，请先完成两台电脑的配对。");
            if (PlatformInfo.IsMacOS && _inputServer is null) throw new InvalidOperationException("Mac 键鼠接收服务未启用，请检查连接设置。");
            var knownAddress = PlatformInfo.IsMacOS ? Settings.AllowedControllerAddress : Settings.LastConnectedPeerAddress;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            attempt.CancelAfter(TimeSpan.FromSeconds(15));
            if (PlatformInfo.IsMacOS && ShareSystemAudioAutomatically && !_audioSender.IsRunning) _speakerCoordinator?.Enable();
            do
            {
                await _discovery.RequestConnectionAsync(knownAddress, attempt.Token);
                if (PlatformInfo.IsWindows)
                {
                    var discovered = _discovery.Snapshot().FirstOrDefault(p => p.Platform.Contains("Mac", StringComparison.OrdinalIgnoreCase));
                    await EnsureInputConnectionAsync(discovered?.Address ?? knownAddress, attempt.Token);
                }
                if (IsPeerConnected)
                {
                    ConnectionActionStatus = "已连接 · 可以跨屏使用键鼠";
                    AppendLog("[连接] 一键连接成功 · 已确认加密键鼠连接");
                    return;
                }
                await Task.Delay(750, attempt.Token);
            } while (!attempt.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            if (!_shuttingDown)
            {
                ConnectionActionStatus = "未连上 · 请确认另一台已打开联桥且网络互通，再点一次";
                AppendLog("[连接] 一键连接超时，未报告连接成功");
            }
        }
        catch (TimeoutException) { ConnectionActionStatus = "连接服务仍在启动，请稍后再点一次"; }
        catch (Exception ex)
        {
            if (!_shuttingDown) { ConnectionActionStatus = "未连上 · " + ex.Message; AppendLog("[连接] 一键连接未完成：" + ex.Message); }
        }
        finally { IsConnectingPeer = false; }
    }
}
