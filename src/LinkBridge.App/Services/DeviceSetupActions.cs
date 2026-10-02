using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Platform;
using LinkBridge.Input;

namespace LinkBridge.App.Services;

public enum DeviceSetupAction
{
    AccessibilityPermissions, MicrophonePermissions, CameraPermissions, AudioOutputs,
    RevealApplication, OpenPreparedBackendDirectory, OpenBackendSource, OpenPermissionHelp
}

/// <summary>Explicit navigation only: no installation, elevation, signing, restart or permission mutation.</summary>
public static class DeviceSetupActions
{
    public static bool IsMac => OperatingSystem.IsMacOS();
    public static string PlatformGuide => IsMac
        ? "本机是 Mac。键鼠控制需要‘设备控制和数据访问’许可（旧系统称辅助功能）；采集麦克风/BlackHole需要麦克风许可；采集相机需要相机许可。请在本机的系统设置完成。"
        : "本机是 Windows。这里的权限按钮只打开本机设置。Mac 键鼠待授权时，请在 Mac 端打开同一指引，点击‘Mac 设备控制’，允许联桥；旧系统该页面称辅助功能。";
    public static string PermissionGuide => IsMac
        ? "在‘设备控制和数据访问（旧系统称辅助功能）’列表允许‘联桥 LinkBridge’。没有该项时，点‘+’，选下方显示的当前 .app；相机/麦克风只允许列表中实际发起采集的联桥或其采集引擎。列表尚无请求时先尝试相应设备；授权后按系统提示重新打开应用。"
        : "在相机与麦克风页面检查设备访问及桌面应用访问。Mac 的设备控制许可（旧系统称辅助功能）必须在 Mac 上完成，Windows 开关不能代替它。";
    public static string BackendGuide => IsMac
        ? "安装要求：共享麦克风应用输入需独立 BlackHole 16ch；音乐使用 2ch，不能互相代替。若当前检查提示缺失，使用官方 16ch 包，安装后按要求重启，再检查应用能否选到 BlackHole 16ch。"
        : "安装要求：共享麦克风需 VB-CABLE。若当前检查提示缺失，保留完整解压目录，以管理员身份运行 x64 安装器，按要求重启。安装可能改变默认设备，请检查并恢复你原来的音响和麦克风；应用输入选择 CABLE Output。";
    public static string CameraGuide => IsMac
        ? "接入要求：Mac 应用摄像头入口需要有效签名的摄像头扩展并获系统允许。仅打开相机隐私开关不能完成扩展注册。若状态提示待签名，请向发布者获取签名版本；勿关闭系统保护。"
        : "接入要求：应用需要选到联桥共享摄像头入口，来源还必须确认实际 4K 和首帧。来源不达 4K、对端扩展未注册或未授权时，以当前检测错误为准。";

    public static string CurrentApplication
    {
        get
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前联桥应用。");
            if (!IsMac) return executable;
            var directory = new DirectoryInfo(Path.GetDirectoryName(executable)!);
            while (directory is not null) { if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return directory.FullName; directory = directory.Parent; }
            return executable + "（开发运行，未发现 .app 包）";
        }
    }

    public static void Run(DeviceSetupAction action)
    {
        if (!OperatingSystem.IsWindows() && !IsMac) throw new PlatformNotSupportedException("设备准备操作仅支持 Windows/macOS。");
        switch (action)
        {
            case DeviceSetupAction.AccessibilityPermissions:
                if (!IsMac) throw new PlatformNotSupportedException("辅助功能设备控制许可需要在 Mac 端打开。");
                MacInputInjector.RequestTrust();
                OpenUri("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"); break;
            case DeviceSetupAction.MicrophonePermissions:
                OpenUri(IsMac ? "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone" : "ms-settings:privacy-microphone"); break;
            case DeviceSetupAction.CameraPermissions:
                OpenUri(IsMac ? "x-apple.systempreferences:com.apple.preference.security?Privacy_Camera" : "ms-settings:privacy-webcam"); break;
            case DeviceSetupAction.AudioOutputs:
                OpenUri(IsMac ? "x-apple.systempreferences:com.apple.Sound-Settings.extension" : "ms-settings:sound"); break;
            case DeviceSetupAction.RevealApplication:
                if (IsMac) Start("/usr/bin/open", "-R", ApplicationPath());
                else Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select," + ApplicationPath());
                break;
            case DeviceSetupAction.OpenPreparedBackendDirectory:
                var directory = VerifyPreparedBackendDirectory();
                if (IsMac) Start("/usr/bin/open", directory);
                else Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), directory);
                break;
            case DeviceSetupAction.OpenBackendSource:
                OpenUri(IsMac ? "https://existential.audio/blackhole/" : "https://vb-audio.com/Cable/"); break;
            case DeviceSetupAction.OpenPermissionHelp:
                OpenUri(IsMac ? "https://support.apple.com/guide/mac-help/change-privacy-security-settings-mchl211c911f/mac"
                    : "https://support.microsoft.com/windows/privacy/windows-camera-microphone-and-privacy"); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private static string ApplicationPath()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前应用。");
        if (!IsMac) return path;
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (directory is not null) { if (directory.Name.EndsWith(".app",StringComparison.OrdinalIgnoreCase)) return directory.FullName; directory=directory.Parent; }
        throw new InvalidOperationException("当前是开发运行，未发现可加入授权列表的 .app；请使用正式联桥应用包。");
    }
    private static void OpenUri(string uri)
    {
        if (IsMac) Start("/usr/bin/open", uri);
        else
        {
            using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            if (process is { HasExited: true } && process.ExitCode != 0) throw new InvalidOperationException("系统未接受页面打开请求。");
        }
    }
    private static void Start(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("系统未接受打开请求。");
        if (process.WaitForExit(1500) && process.ExitCode != 0) throw new InvalidOperationException($"系统打开请求失败，退出码 {process.ExitCode}。");
    }
    private static string VerifyPreparedBackendDirectory()
    {
        if (IsMac)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","Caches","LinkBridge","BackendSetup");
            VerifyHash(Path.Combine(directory,"BlackHole16ch-0.7.1.pkg"),"57254E2F76CD40DB7F3F715238B1A2CB2BD08819D38ABF4087F2944F71A3641A");
            return directory;
        }
        var cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LinkBridge","BackendSetup");
        VerifyHash(Path.Combine(cache,"VBCABLE_Driver_Pack45.zip"),"B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB");
        var reviewed=Path.Combine(cache,"VBCABLE45-reviewed");
        var extracted=Directory.Exists(reviewed)?reviewed:Path.Combine(cache,"VBCABLE45");
        using var stream=AssetLoader.Open(new Uri("avares://LinkBridge.App/Assets/BackendSetup/vbcable-pack45-sha256.json"));
        var entries=JsonSerializer.Deserialize<HashEntry[]>(stream)??throw new InvalidDataException("安装准备校验清单缺失。");
        if(!Directory.Exists(extracted)||Directory.EnumerateFiles(extracted,"*",SearchOption.AllDirectories).Count()!=entries.Length)
            throw new InvalidDataException("准备目录文件集合与已审核安装包不一致；未打开目录。");
        foreach(var entry in entries)
        {
            if(Path.GetFileName(entry.Name)!=entry.Name)throw new InvalidDataException("安装校验清单路径无效。");
            VerifyHash(Path.Combine(extracted,entry.Name),entry.Sha256);
        }
        return extracted;
    }
    private static void VerifyHash(string path,string expected)
    {
        if(!File.Exists(path))throw new FileNotFoundException("尚未找到已校验的准备包；请先通过官方来源准备完整安装包。",path);
        using var file=File.OpenRead(path);var actual=Convert.ToHexString(SHA256.HashData(file));
        if(!actual.Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("准备包与已审核版本不一致；未打开目录，请重新核验官方包。");
    }
    private sealed record HashEntry(string Name,string Sha256);
}
