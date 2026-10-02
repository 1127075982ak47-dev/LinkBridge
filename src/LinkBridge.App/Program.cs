using Avalonia;
using System;
using LinkBridge.Core;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace LinkBridge.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsMacOS() && args.Contains("--probe-camera"))
        {
            var directory = Path.GetDirectoryName(SettingsStore.FilePath)!;
            Directory.CreateDirectory(directory);
            var output = ProcessHelper.RunAndCaptureAsync(
                FfmpegLocator.ExecutablePath!,
                ["-hide_banner", "-loglevel", "info", "-nostdin", "-f", "avfoundation", "-pixel_format", "uyvy422", "-framerate", "30", "-video_size", "3840x2160", "-i", "0:none", "-t", "3", "-f", "null", "-"],
                TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(directory, "camera-probe.log"), output);
            return;
        }

        using var instance = new Mutex(true, "LinkBridge.Desktop.CurrentUser.v2", out var ownsInstance);
        if (!ownsInstance)
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("LinkBridge.App"))
                {
                    using (process)
                    {
                        try
                        {
                            if (process.Id == Environment.ProcessId || !string.Equals(process.MainModule?.FileName,
                                Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                            if (process.MainWindowHandle == IntPtr.Zero) continue;
                            ShowWindow(process.MainWindowHandle, 9);
                            SetForegroundWindow(process.MainWindowHandle);
                            break;
                        }
                        catch (System.ComponentModel.Win32Exception) { }
                        catch (InvalidOperationException) { }
                    }
                }
            }
            return;
        }
        // macOS updates send SIGTERM. Closing the actual window runs the same
        // bounded cleanup as its close button, including owned FFmpeg and routes.
        using var termination = OperatingSystem.IsMacOS()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime && lifetime.MainWindow is { } window)
                        window.Close();
                    else Environment.Exit(0);
                });
            }) : null;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { instance.ReleaseMutex(); }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
