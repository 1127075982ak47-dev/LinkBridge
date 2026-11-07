using Avalonia.Threading;
using LinkBridge.App.Services;
using LinkBridge.Core;
using LinkBridge.Core.FileTransfer;

namespace LinkBridge.App.ViewModels;

public partial class MainViewModel
{
    private FileTransferServer? _fileClipboardServer;

    // Receiving uses a separate endpoint: older peers must fail instead of starting a Finder drag.
    private void InitializeFileClipboard(string address)
    {
        if (_fileClipboardServer is not null) return;
        var cache = Path.Combine(Path.GetDirectoryName(SettingsStore.FilePath)!, "ClipboardFiles");
        FileTransferManifest? incoming = null;
        long incomingRevision = 0;
        _fileClipboardServer = new FileTransferServer(address, Settings.InputPairingSecret,
            Path.Combine(cache, "Pending"), async (manifest, staging, ct) =>
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_shuttingDown || !ShareClipboardAutomatically) throw new IOException("对端文件共享已暂停。");
                    lock (_clipboardGate)
                    {
                        _clipboardSendCancellation?.Cancel();
                        _clipboardReceiveCancellation?.Cancel();
                        incomingRevision = ++_clipboardRevision;
                        _lastLocalClipboard = null;
                        _clipboardDigest = null;
                        incoming = manifest;
                    }
                    ClipboardStatus = $"正在接收 {manifest.Roots.Count} 项文件…";
                });
            }, async (id, staging, ct) =>
            {
                if (incoming?.Id != id) throw new IOException("文件接收状态已失效。");
                using var receiving = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
                lock (_clipboardGate)
                {
                    if (_shuttingDown || !ShareClipboardAutomatically || incomingRevision != _clipboardRevision)
                        throw new IOException("剪贴板已有新内容，本次文件未覆盖剪贴板。");
                    _clipboardReceiveCancellation = receiving;
                }
                // Move verified bytes out of server-owned staging before publishing native file URLs.
                // Keep successful cache files across restarts, so repeated paste and delayed paste work.
                var ready = Path.Combine(cache, "Ready", id.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ready)!);
                    Directory.Move(staging, ready);
                    var paths = incoming.Roots.Select(root => Path.Combine(ready, root.Name)).ToArray();
                    var content = SharedClipboardFiles.Encode(paths);
                    if (_windowsClipboard is not null) await _windowsClipboard.ApplyAsync(content, receiving.Token);
                    else if (_macClipboard is not null) await _macClipboard.ApplyAsync(content, receiving.Token);
                    else throw new IOException("系统文件剪贴板未启动。");
                    _fileClipboardServer!.ReportCommittedDestinations(id, paths);
                    AppendLog($"[文件剪贴板] 已校验并写入系统剪贴板 · {paths.Length} 项 · {incoming.TotalBytes} 字节");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (incomingRevision != _clipboardRevision) return;
                        ClipboardStatus = $"收到 {paths.Length} 项文件 · 到目标文件夹粘贴";
                        HasClipboardSendError = false;
                    });
                }
                finally
                {
                    lock (_clipboardGate)
                        if (ReferenceEquals(_clipboardReceiveCancellation, receiving)) _clipboardReceiveCancellation = null;
                }
            }, async (id, error) =>
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (incoming?.Id == id && incomingRevision == _clipboardRevision)
                        ClipboardStatus = "文件未接收：" + error;
                });
            }, port: SharedClipboardFiles.Port);
        _fileClipboardServer.Diagnostic += message => AppendLog("[文件剪贴板] " + message);
        _fileClipboardServer.ProgressChanged += value => Dispatcher.UIThread.Post(() =>
        {
            if (incoming?.Id != value.Id || incomingRevision != _clipboardRevision || value.Phase != "接收中") return;
            ClipboardStatus = $"接收文件 · {FormatFileBytes(value.Bytes)} / {FormatFileBytes(value.TotalBytes)}";
        });
        try { _fileClipboardServer.Start(); }
        catch { _ = _fileClipboardServer.DisposeAsync(); _fileClipboardServer = null; throw; }
        AppendLog("[文件剪贴板] 双向文件与文件夹复制已就绪 · TLS / SHA-256");
    }

    public bool CanSendFiles => !_shuttingDown && ShareClipboardAutomatically && _clipboardAddress is not null;

    public void SendFilesToPeer(IReadOnlyList<string> paths)
    {
        if (!CanSendFiles) { ClipboardStatus = "请连接另一台电脑并开启共享后再拖入文件"; return; }
        try
        {
            OnLocalClipboardChanged(SharedClipboardFiles.Encode(paths));
            ClipboardStatus = $"准备发送 {paths.Count} 项文件…";
        }
        catch (Exception error) { ClipboardStatus = error.Message; }
    }
}
