using System.Text;
using System.Threading.Channels;

namespace LinkBridge.App.Services;

/// <summary>Bounded background logging; filesystem work never runs on the UI or media callback.</summary>
internal sealed class ProductLog : IAsyncDisposable
{
    private const long MaximumBytes = 1024 * 1024;
    private readonly string _path;
    private readonly Channel<string> _pending = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _worker;
    private string? _error;
    public string? Error => Volatile.Read(ref _error);
    public event Action<string>? WriteFailed;
    public ProductLog(string path) { _path = path; _worker = Task.Run(WriteAsync); }
    public void Append(string line) => _pending.Writer.TryWrite(line.Length > 4096 ? line[..4096] : line);

    private async Task WriteAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await foreach (var line in _pending.Reader.ReadAllAsync())
            {
                var lines = new StringBuilder().AppendLine(line);
                while (lines.Length < 32768 && _pending.Reader.TryRead(out var next)) lines.AppendLine(next);
                if (File.Exists(_path) && new FileInfo(_path).Length >= MaximumBytes)
                    File.Move(_path, _path + ".1", overwrite: true);
                await File.AppendAllTextAsync(_path, lines.ToString(), Encoding.UTF8).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _error, ex.Message);
            _pending.Writer.TryComplete();
            try { WriteFailed?.Invoke(ex.Message); } catch { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}
