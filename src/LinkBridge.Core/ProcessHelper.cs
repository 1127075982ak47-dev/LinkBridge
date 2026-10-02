using System.Diagnostics;
using System.Text;

namespace LinkBridge.Core;

/// <summary>进程调用的公共封装：跑一个外部程序并把 stdout + stderr 一起收回来。</summary>
public static class ProcessHelper
{
    /// <summary>运行外部程序并等待结束，返回合并后的输出。</summary>
    public static async Task<string> RunAndCaptureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var buffer = new StringBuilder();
        var gate = new object();

        process.OutputDataReceived += (_, e) => Append(buffer, gate, e.Data);
        process.ErrorDataReceived += (_, e) => Append(buffer, gate, e.Data);

        if (!process.Start())
        {
            return string.Empty;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
        }

        lock (gate)
        {
            return buffer.ToString();
        }
    }

    /// <summary>结束进程树，忽略已经退出的情况。</summary>
    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能刚好自己退了
        }
    }

    private static void Append(StringBuilder buffer, object gate, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (gate)
        {
            buffer.AppendLine(line);
        }
    }
}
