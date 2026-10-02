using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

// 用 Windows 官方的 IShellLink COM 接口生成快捷方式。
// 之前手写二进制的那版格式不完整（少了 LinkTargetIDList），Windows 认不出来。

if (args.Length < 2)
{
    Console.WriteLine("用法: MakeShortcut <目标exe> <输出的 .lnk 路径> [工作目录]");
    return 1;
}

var target = Path.GetFullPath(args[0]);
var lnkPath = Path.GetFullPath(args[1]);
var workdir = args.Length > 2
    ? Path.GetFullPath(args[2])
    : Path.GetDirectoryName(target)!;

if (!File.Exists(target))
{
    Console.WriteLine($"目标不存在: {target}");
    return 1;
}

var directory = Path.GetDirectoryName(lnkPath);
if (!string.IsNullOrEmpty(directory))
{
    Directory.CreateDirectory(directory);
}

var link = (IShellLinkW)new ShellLinkCoClass();
link.SetPath(target);
link.SetWorkingDirectory(workdir);
link.SetDescription("联桥 LinkBridge — 跨机外设共享");
link.SetIconLocation(target, 0);

((IPersistFile)link).Save(lnkPath, true);

Console.WriteLine($"已创建快捷方式: {lnkPath}");
Console.WriteLine($"        指向: {target}");
return 0;

[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLinkCoClass
{
}

[ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

    void GetIDList(out IntPtr ppidl);

    void SetIDList(IntPtr pidl);

    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

    void GetHotkey(out short pwHotkey);

    void SetHotkey(short wHotkey);

    void GetShowCmd(out int piShowCmd);

    void SetShowCmd(int iShowCmd);

    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

    void Resolve(IntPtr hwnd, uint fFlags);

    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}
