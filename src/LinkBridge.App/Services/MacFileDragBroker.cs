using System.Diagnostics;
using System.Text.Json;
using LinkBridge.Input;

namespace LinkBridge.App.Services;

/// <summary>Hands paired incoming files to AppKit's real Finder file-promise drag.</summary>
public sealed class MacFileDragBroker : IAsyncDisposable
{
    private sealed class Drag(Guid id)
    {
        public Guid Id { get; } = id;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<string>> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool OwnPress { get; set; }
    }

    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _helper;
    private Drag? _drag;
    private readonly Dictionary<Guid, Drag> _results = [];
    private Task? _reader;
    private Task? _errors;
    private int _disposed;

    public event Action<string>? Diagnostic;

    public async Task BeginAsync(Guid id, IReadOnlyList<(string Name, bool IsDirectory)> roots,
        string privateStagingPath, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (roots.Count is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(roots));
        foreach (var root in roots)
            if (string.IsNullOrWhiteSpace(root.Name) || root.Name is "." or ".." ||
                root.Name.IndexOfAny(['/', '\\', '\0']) >= 0)
                throw new ArgumentException("Invalid file-promise root name.", nameof(roots));
        var staging = Path.GetFullPath(privateStagingPath);
        if (!Directory.Exists(staging) || new DirectoryInfo(staging).LinkTarget is not null)
            throw new ArgumentException("A real private staging directory is required.", nameof(privateStagingPath));
        Drag drag;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_drag is not null) throw new InvalidOperationException("另一个文件拖放尚未完成");
            foreach (var retired in _results.Where(item => item.Value.Completed.Task.IsCompleted).Take(Math.Max(0, _results.Count - 7)).Select(item => item.Key).ToArray())
                _results.Remove(retired);
            drag = _drag = new Drag(id);
            _results.Add(id, drag);
        }
        try
        {
            await SendAsync(new { command = "begin", id, stagingPath = staging,
                roots = roots.Select(root => new { name = root.Name, isDirectory = root.IsDirectory }) }, ct).ConfigureAwait(false);
            await drag.Started.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        catch
        {
            await CancelAsync(id, "文件拖放未能开始").ConfigureAwait(false);
            throw;
        }
    }

    public async Task MarkReadyAsync(Guid id, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        lock (_gate)
            if (_drag is not { } drag || drag.Id != id || drag.Completed.Task.IsCompleted)
                throw new InvalidOperationException("文件拖放已结束或取消");
        await SendAsync(new { command = "ready", id }, linked.Token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> WaitForCompletionAsync(Guid id, CancellationToken ct)
    {
        Drag drag;
        lock (_gate)
        {
            if (!_results.TryGetValue(id, out drag!))
                throw new InvalidOperationException("Unknown file drag.");
        }
        try { return await drag.Completed.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally { lock (_gate) if (drag.Completed.Task.IsCompleted) _results.Remove(id); }
    }

    public async Task CancelAsync(Guid id, string error)
    {
        Drag? drag;
        lock (_gate) drag = _drag?.Id == id ? _drag : null;
        if (drag is null) return;
        try
        {
            await SendAsync(new { command = "cancel", id, error }, _lifetime.Token).ConfigureAwait(false);
            if (drag.OwnPress) { MacInputInjector.ReleaseFileDragPress(); drag.OwnPress = false; }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { Fail(drag, new IOException(error, ex)); }
    }

    private async Task SendAsync(object command, CancellationToken ct)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_helper is null || _helper.HasExited)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "LinkBridge.FileDragHelper");
                if (!File.Exists(path)) throw new FileNotFoundException("Mac 原生文件拖放组件未安装", path);
                var process = new Process { StartInfo = new ProcessStartInfo(path)
                { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true } };
                if (!process.Start()) throw new IOException("Mac 原生文件拖放组件未能启动");
                _helper = process;
                _reader = ReadAsync(process, _lifetime.Token);
                _errors = DrainErrorsAsync(process, _lifetime.Token);
            }
            ct.ThrowIfCancellationRequested();
            await _helper.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), ct).ConfigureAwait(false);
            await _helper.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }

    private async Task ReadAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 1024 * 1024) throw new IOException("Invalid native drag response.");
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (!root.TryGetProperty("id", out var idText) || !Guid.TryParse(idText.GetString(), out var id)) continue;
                Drag? drag;
                lock (_gate) drag = _drag?.Id == id ? _drag : null;
                if (drag is null) continue;
                var kind = root.GetProperty("event").GetString();
                try { Diagnostic?.Invoke($"native {kind} · {id:N}" +
                    (root.TryGetProperty("operation", out var operation) ? $" · operation={operation.GetUInt64()}" : "") +
                    (root.TryGetProperty("detail", out var geometry) ? " · " + geometry.GetString() : "") +
                    (root.TryGetProperty("error", out var detail) ? " · " + detail.GetString() : "")); } catch { }
                switch (kind)
                {
                    case "source-ready":
                        var window = root.GetProperty("window").GetInt32();
                        var x = root.GetProperty("x").GetDouble();
                        var y = root.GetProperty("y").GetDouble();
                        var posted = false;
                        for (var attempt = 0; attempt < 5 && !posted; attempt++)
                        {
                            await Task.Delay(40, ct).ConfigureAwait(false);
                            posted = MacInputInjector.BeginFileDragOnOwnedWindow(process.Id, window, x, y, out var verification);
                            try { Diagnostic?.Invoke(verification + $"; posted={posted}"); } catch { }
                            if (posted) drag.OwnPress = true;
                        }
                        if (!posted)
                        {
                            var blocked = "文件拖放源窗口已被其他窗口遮挡，已安全取消";
                            await SendAsync(new { command = "cancel", id, error = blocked }, ct).ConfigureAwait(false);
                            Fail(drag, new IOException(blocked));
                        }
                        break;
                    case "started": drag.Started.TrySetResult(); break;
                    case "native-ended":
                        if (drag.OwnPress) MacInputInjector.ReleaseFileDragPress();
                        drag.OwnPress = false;
                        break;
                    case "completed":
                        var paths = root.GetProperty("paths").EnumerateArray().Select(item => item.GetString()
                            ?? throw new IOException("Invalid destination path.")).ToArray();
                        if (paths.Length == 0 || paths.Any(path => !Path.IsPathFullyQualified(path)))
                            throw new IOException("Native drag completed without actual destinations.");
                        drag.Completed.TrySetResult(paths);
                        // Keep the result accessible until the consumer has obtained its Task.
                        break;
                    case "failed":
                        Fail(drag, new IOException(root.TryGetProperty("error", out var error)
                            ? error.GetString() : "Mac 未接受文件拖放"));
                        break;
                    case "idle":
                        lock (_gate) if (ReferenceEquals(_drag, drag)) _drag = null;
                        break;
                }
            }
            if (!ct.IsCancellationRequested) throw new IOException("Mac 文件拖放组件已退出");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Drag? drag;
            lock (_gate) { drag = _drag; _drag = null; }
            if (drag is not null)
            {
                if (drag.OwnPress) { MacInputInjector.ReleaseFileDragPress(); drag.OwnPress = false; }
                Fail(drag, ex);
            }
        }
    }

    private async Task DrainErrorsAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                try { Diagnostic?.Invoke(line.Length <= 1000 ? line : line[..1000]); } catch { }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { }
    }

    private static void Fail(Drag drag, Exception error)
    {
        drag.Started.TrySetException(error);
        drag.Completed.TrySetException(error);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        Drag? drag;
        lock (_gate) drag = _drag;
        if (drag is not null) Fail(drag, new OperationCanceledException("文件拖放已关闭"));
        if (drag is { OwnPress: true }) { MacInputInjector.ReleaseFileDragPress(); drag.OwnPress = false; }
        if (_helper is { } process)
        {
            try { process.StandardInput.Close(); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { try { process.Kill(); } catch { } }
            process.Dispose();
        }
        if (_reader is not null) try { await _reader.ConfigureAwait(false); } catch { }
        if (_errors is not null) try { await _errors.ConfigureAwait(false); } catch { }
        _lifetime.Dispose();
    }
}
