using System.Runtime.Versioning;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace LinkBridge.Core;

/// <summary>
/// Registers only LinkBridge for the current user's next sign-in.
/// Does not launch the app, run launchctl, or change other startup entries.
/// The caller must invoke this only after the user changes the startup setting.
/// </summary>
public static class StartupRegistration
{
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsValueName = "LinkBridge";
    private const string MacLabel = "com.linkbridge.app";

    /// <summary>
    /// Applies the registration. Success confirms the registry value or plist change,
    /// not a tested sign-in. An unsupported platform, development host, or write failure
    /// returns false and a concrete error message.
    /// </summary>
    public static bool Apply(bool enabled, out string? error)
    {
        error = null;
        try
        {
            if (OperatingSystem.IsWindows()) ApplyWindows(enabled);
            else if (OperatingSystem.IsMacOS()) ApplyMac(enabled);
            else throw new PlatformNotSupportedException("当前平台不支持联桥登录时启动。");
            return true;
        }
        catch (Exception ex)
        {
            error = $"登录启动项{(enabled ? "启用" : "关闭")}失败：{ex.Message}";
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: true);
            existing?.DeleteValue(WindowsValueName, throwOnMissingValue: false);
            existing?.Flush();
            return;
        }

        var executable = CurrentExecutable();
        var name = Path.GetFileNameWithoutExtension(executable);
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从联桥已构建的桌面应用启用登录启动，不能登记 dotnet 开发宿主。");
        if (executable.Contains('"'))
            throw new InvalidOperationException("应用路径包含无法登记的引号字符。");

        // Windows Run values are command lines: quote the executable itself, with no shell.
        var command = "\"" + executable + "\"";
        using var run = Registry.CurrentUser.CreateSubKey(WindowsRunKey, writable: true)
            ?? throw new IOException("无法打开当前用户的登录启动注册表项。");
        run.SetValue(WindowsValueName, command, RegistryValueKind.String);
        run.Flush();
        if (!string.Equals(run.GetValue(WindowsValueName) as string, command, StringComparison.Ordinal))
            throw new IOException("联桥启动注册表值写入后校验失败。");
    }

    [SupportedOSPlatform("macos")]
    private static void ApplyMac(bool enabled)
    {
        var userDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userDirectory))
            throw new InvalidOperationException("无法确定当前用户的主目录。");
        var directory = Path.Combine(userDirectory, "Library", "LaunchAgents");
        var plistPath = Path.Combine(directory, MacLabel + ".plist");
        if (!enabled)
        {
            // Delete only the one owned file. File.Delete is also safe when it is absent.
            File.Delete(plistPath);
            return;
        }

        var executable = CurrentExecutable();
        var bundle = FindBundle(executable)
            ?? throw new InvalidOperationException("当前不是 .app 包运行。请从已安装的联桥应用启用登录启动。");
        if (!File.Exists(Path.Combine(bundle, "Contents", "Info.plist")))
            throw new InvalidOperationException("联桥应用包缺少 Contents/Info.plist，无法登记登录启动。");
        var macOsDirectory = Path.Combine(bundle, "Contents", "MacOS") + Path.DirectorySeparatorChar;
        if (!executable.StartsWith(macOsDirectory, StringComparison.Ordinal))
            throw new InvalidOperationException("当前进程不在应用包的 Contents/MacOS 中，拒绝登记开发宿主。");

        var dict = new XElement("dict",
            new XElement("key", "Label"), new XElement("string", MacLabel),
            new XElement("key", "ProgramArguments"), new XElement("array",
                new XElement("string", "/usr/bin/open"),
                new XElement("string", "-a"),
                new XElement("string", bundle)),
            new XElement("key", "RunAtLoad"), new XElement("true"),
            new XElement("key", "LimitLoadToSessionType"), new XElement("string", "Aqua"),
            new XElement("key", "ProcessType"), new XElement("string", "Interactive"));
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN",
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dict));

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, "." + MacLabel + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // XElement escapes '&', '<', and Unicode paths. Arguments are plist strings,
            // not a command line, so no shell quoting or interpolation is involved.
            using (var writer = XmlWriter.Create(temporaryPath, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = true
            })) document.Save(writer);
            File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, plistPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string CurrentExecutable()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("无法读取当前应用可执行文件路径。");
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("当前应用可执行文件不存在。", path);
        return path;
    }

    private static string? FindBundle(string executable)
    {
        for (var directory = Directory.GetParent(executable); directory is not null; directory = directory.Parent)
        {
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return directory.FullName;
        }
        return null;
    }
}
