using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkBridge.App.Services;
using LinkBridge.Core;
using LinkBridge.Core.FileTransfer;
using LinkBridge.Input;

namespace LinkBridge.App.ViewModels;

public partial class MainViewModel
{
    private WindowsFileDragEdge? _fileDragEdge;
    private FileTransferServer? _fileTransferServer;
    private MacFileDragBroker? _macFileDragBroker;
    private readonly FileTransferSender _fileTransferSender = new();
    private CancellationTokenSource? _fileTransferCancellation;
    private string? _filePeerAddress;
    private Guid? _incomingFileId;
    private bool _fileDragReleased;
    private bool _nativeFileDragStarted;
    private Task? _outgoingFileTask;

    [ObservableProperty] private bool _hasFileTask;
    [ObservableProperty] private bool _isFileTransferBusy;
    [ObservableProperty] private string _fileTransferStatus = string.Empty;
    [ObservableProperty] private double _fileTransferPercent;

    private void InitializeWindowsFileDrag()
    {
        if (_inputCapture is null || _fileDragEdge is not null) return;
        _fileDragEdge = new WindowsFileDragEdge(
            paths => Dispatcher.UIThread.Post(() => _outgoingFileTask = BeginFileHandoffAsync(paths)),
            message => AppendLog("[文件] " + message));
        _inputCapture.RemoteEntered += UpdateFileEdge;
        _inputCapture.RemoteExited += UpdateFileEdge;
        _inputCapture.FileDragReleased += () => Dispatcher.UIThread.Post(() =>
        {
            _fileDragReleased = true;
            if (!Volatile.Read(ref _nativeFileDragStarted)) _fileTransferCancellation?.Cancel();
        });
        _inputCapture.FileDragCancelled += () => Dispatcher.UIThread.Post(() => _fileTransferCancellation?.Cancel());
        UpdateFileEdge();
    }

    private void UpdateFileEdge() => _fileDragEdge?.SetEnabled(!_shuttingDown &&
        _inputClient?.IsConnected == true && _inputCapture?.IsRemote == false && !IsFileTransferBusy);

    private void InitializeMacFileDrag()
    {
        if (_fileTransferServer is not null || !OperatingSystem.IsMacOS()) return;
        _macFileDragBroker = new MacFileDragBroker();
        _macFileDragBroker.Diagnostic += message => AppendLog("[文件] " + message);
        var cache = Path.Combine(Path.GetDirectoryName(SettingsStore.FilePath)!, "FileTransfers");
        _fileTransferServer = new FileTransferServer(Settings.AllowedControllerAddress, Settings.InputPairingSecret, cache,
            async (manifest, staging, ct) =>
            {
                if (!MacInputInjector.IsTrusted()) throw new InvalidOperationException("请先允许联桥控制 Mac。");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _incomingFileId = manifest.Id;
                    HasFileTask = IsFileTransferBusy = true;
                    FileTransferStatus = "拖到 Finder 文件夹后松手";
                    FileTransferPercent = 0;
                });
                await _macFileDragBroker.BeginAsync(manifest.Id,
                    manifest.Roots.Select(root => (root.Name, root.IsDirectory)).ToArray(), staging, ct);
            },
            async (id, staging, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                await _macFileDragBroker.MarkReadyAsync(id, ct);
                var destinations = await _macFileDragBroker.WaitForCompletionAsync(id, ct);
                if (destinations.Count == 0) throw new IOException("Finder 没有保存文件。");
                _fileTransferServer!.ReportCommittedDestinations(id, destinations);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsFileTransferBusy = false;
                    FileTransferPercent = 100;
                    FileTransferStatus = $"已保存 {destinations.Count} 项到 {Path.GetDirectoryName(destinations[0])}";
                    _incomingFileId = null;
                });
                AppendLog("[文件] Finder 已保存并校验：" + string.Join("；", destinations));
                // Finder's promise callback has completed; only our private staging is removed.
                try { Directory.Delete(staging, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    AppendLog("[文件] 文件已保存，暂存清理稍后重试：" + error.Message);
                }
            },
            async (id, error) =>
            {
                await _macFileDragBroker.CancelAsync(id, error);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsFileTransferBusy = false;
                    FileTransferStatus = "文件未复制：" + error;
                    _incomingFileId = null;
                });
            });
        _fileTransferServer.Diagnostic += message => AppendLog("[文件] " + message);
        _fileTransferServer.ProgressChanged += progress => Dispatcher.UIThread.Post(() =>
        {
            if (_shuttingDown || _incomingFileId != progress.Id) return;
            FileTransferPercent = progress.TotalBytes > 0 ? 100d * progress.Bytes / progress.TotalBytes : 0;
            FileTransferStatus = progress.Phase == "已保存" ? FileTransferStatus :
                $"{progress.Phase} · {FormatFileBytes(progress.Bytes)} / {FormatFileBytes(progress.TotalBytes)}";
        });
        _fileTransferServer.Start();
        AppendLog("[文件] Finder 跨屏拖放已就绪 · LBF1 TLS");
    }

    private async Task BeginFileHandoffAsync(IReadOnlyList<string> paths)
    {
        if (_shuttingDown || IsFileTransferBusy || _inputClient?.IsConnected != true || _filePeerAddress is null) return;
        if (_inputCapture?.BeginFileDrag() != true)
        {
            HasFileTask = true;
            FileTransferStatus = "请按住文件滑过右侧边缘，再到 Mac 松手";
            return;
        }
        HasFileTask = IsFileTransferBusy = true;
        var address = _filePeerAddress;
        _fileDragReleased = false;
        Volatile.Write(ref _nativeFileDragStarted, false);
        FileTransferPercent = 0;
        FileTransferStatus = "正在接力文件拖放…";
        UpdateFileEdge();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _fileTransferCancellation = cancellation;
        try
        {
            var manifest = await Task.Run(() => FileTransferSender.CreateManifest(paths), cancellation.Token);
            if (_fileDragReleased) throw new IOException("请稍候文件图标出现，再在 Finder 中松手。");
            var progress = new ImmediateFileProgress(value =>
            {
                // This phase follows the native willBeginAt acknowledgement, not just a socket connection.
                if (value.Phase is "传输中" or "等待放入目标文件夹") Volatile.Write(ref _nativeFileDragStarted, true);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_shuttingDown || _fileTransferCancellation != cancellation) return;
                    FileTransferPercent = value.TotalBytes > 0 ? 100d * value.Bytes / value.TotalBytes : 0;
                    FileTransferStatus = $"{value.Phase} · {FormatFileBytes(value.Bytes)} / {FormatFileBytes(value.TotalBytes)}";
                });
            });
            var receipt = await Task.Run(() => _fileTransferSender.SendAsync(address, Settings.InputPairingSecret,
                manifest, paths, progress, cancellation.Token), cancellation.Token);
            if (receipt.Destinations.Count == 0) throw new IOException("对端没有返回实际保存位置。");
            FileTransferPercent = 100;
            FileTransferStatus = $"已复制到 Mac · {receipt.Destinations.Count} 项";
            AppendLog("[文件] 跨屏复制已完成并校验：" + string.Join("；", receipt.Destinations));
        }
        catch (OperationCanceledException) { FileTransferStatus = "已取消 · 源文件保留"; }
        catch (Exception error)
        {
            FileTransferStatus = "文件未复制：" + error.Message;
            AppendLog("[文件] " + FileTransferStatus);
            _inputClient?.TrySend(new InputPacket(InputEventType.FileDrag, 0, 0, 0, 0));
        }
        finally
        {
            _fileTransferCancellation = null;
            IsFileTransferBusy = false;
            UpdateFileEdge();
        }
    }

    private static string FormatFileBytes(long bytes) => bytes >= 1024 * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024):0.0} MB" : $"{bytes / 1024d:0.#} KB";

    [RelayCommand]
    private async Task CancelFileTransferAsync()
    {
        if (_fileTransferCancellation is not null)
        {
            _inputClient?.TrySend(new InputPacket(InputEventType.Key, 0, 0x1B, 0, 0));
            _inputClient?.TrySend(new InputPacket(InputEventType.Key, 1, 0x1B, 0, 0));
            _inputClient?.TrySend(new InputPacket(InputEventType.FileDrag, 0, 0, 0, 0));
        }
        _fileTransferCancellation?.Cancel();
        if (_incomingFileId is { } id && _macFileDragBroker is not null)
            await _macFileDragBroker.CancelAsync(id, "用户已取消文件复制");
        if (!IsFileTransferBusy) HasFileTask = false;
    }

    private sealed class ImmediateFileProgress(Action<FileTransferProgress> report) : IProgress<FileTransferProgress>
    {
        public void Report(FileTransferProgress value) => report(value);
    }

    private async Task StopFileDragAsync()
    {
        _fileDragEdge?.Dispose(); _fileDragEdge = null;
        _fileTransferCancellation?.Cancel();
        if (_fileTransferServer is not null) await _fileTransferServer.DisposeAsync();
        if (_macFileDragBroker is not null) await _macFileDragBroker.DisposeAsync();
        if (_outgoingFileTask is not null) try { await _outgoingFileTask; } catch { }
    }
}
