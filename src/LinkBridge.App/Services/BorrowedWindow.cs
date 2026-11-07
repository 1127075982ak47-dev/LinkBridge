using System.Diagnostics;
using System.Runtime.InteropServices;
using LinkBridge.Core.WindowRoaming;

namespace LinkBridge.App.Services;
/// <summary>Transactional window placement. The parent and a bounded redraw helper both restore on termination.</summary>
internal sealed class BorrowedWindow : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] internal struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] internal struct Placement {public int Length,Flags,ShowCmd;public Point Min,Max;public Rect Normal;}
    private readonly nint _hwnd;private readonly uint _pid;private readonly Placement _placement;private readonly Rect _original;
    private Process? _helper;private bool _disposed;private int _buttons;private readonly HashSet<int> _keys=[];
    public int X=>_original.Left;public int Y=>_original.Top;
    public int Width=>_original.Right-_original.Left;public int Height=>_original.Bottom-_original.Top;
    public BorrowedWindow(nint hwnd)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("本版窗口接管源端先支持 Windows；其他平台可接收画面，源端需要相应原生后端验收。");
        if(!IsWindow(hwnd)||IsIconic(hwnd))throw new InvalidOperationException("窗口已关闭或最小化。");
        _hwnd=hwnd;GetWindowThreadProcessId(hwnd,out _pid);
        if(_pid==Environment.ProcessId)throw new InvalidOperationException("不能接管 LinkBridge 自身窗口。");
        _placement=new Placement{Length=Marshal.SizeOf<Placement>()};
        if(!GetWindowPlacement(hwnd,ref _placement)||!GetWindowRect(hwnd,out _original))throw new InvalidOperationException("无法读取窗口恢复位置。");
    }
    public void Begin()
    {
        // This never adds a monitor or modifies the display layout.
        var offscreen=GetSystemMetrics(76)+GetSystemMetrics(78)+Width+256;
        if(!SetWindowPos(_hwnd,0,offscreen,_original.Top,0,0,0x0001|0x0004|0x0010))throw new InvalidOperationException("系统拒绝移动源窗口。");
        if(!GetWindowRect(_hwnd,out var actual)||actual.Left<GetSystemMetrics(76)+GetSystemMetrics(78))
        {Restore();throw new InvalidOperationException("此应用限制离屏位置，窗口已归还。");}
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
        foreach(var value in new[]{"--window-redraw",_hwnd.ToInt64().ToString(),_pid.ToString(),Environment.ProcessId.ToString(),
            _original.Left.ToString(),_original.Top.ToString(),Width.ToString(),Height.ToString(),_placement.ShowCmd.ToString()})start.ArgumentList.Add(value);
        _helper=Process.Start(start)??throw new IOException("窗口恢复保护进程未启动。");
    }
    public bool Apply(WindowRemoteInput input)
    {
        if(_disposed||!IsWindow(_hwnd))return false;GetWindowThreadProcessId(_hwnd,out var pid);if(pid!=_pid)return false;
        if(input.Kind=="release"){Release();return true;}
        var target=_hwnd;var point=new Point{X=input.X,Y=input.Y};
        if(input.Kind is "move" or "button" or "wheel")
        {
            if(input.X<0||input.Y<0||input.X>=Width||input.Y>=Height)return false;
            if(!GetWindowRect(_hwnd,out var r))return false;point.X+=r.Left;point.Y+=r.Top;ScreenToClient(_hwnd,ref point);
            for(int depth=0;depth<8;depth++)
            {
                var child=ChildWindowFromPointEx(target,point,1|2|4);if(child==0||child==target)break;
                ClientToScreen(target,ref point);ScreenToClient(child,ref point);target=child;
            }
        }
        var packed=(nint)((point.Y&0xffff)<<16|(point.X&0xffff));
        if(input.Kind=="move")return PostMessageW(target,0x200,(nuint)_buttons,packed);
        if(input.Kind=="button")
        {
            var bit=input.Code==0?1:input.Code==1?2:16;
            if(input.IsDown)_buttons|=bit;else _buttons&=~bit;
            uint message=input.Code==0?(input.IsDown?0x201u:0x202u):input.Code==1?(input.IsDown?0x204u:0x205u):(input.IsDown?0x207u:0x208u);
            // Native message delivery stays inside the selected source process; no desktop cursor warp.
            return PostMessageW(target,message,(nuint)_buttons,packed);
        }
        if(input.Kind=="wheel")return PostMessageW(target,0x20A,unchecked((nuint)((input.Code<<16)|_buttons)),packed);
        target=GetFocusedChild(_hwnd);
        if(input.Kind=="text"){foreach(var c in input.Text??"")if(!PostMessageW(target,0x102,c,1))return false;return true;}
        if(input.Kind=="key")
        {
            if(input.IsDown)_keys.Add(input.Code);else _keys.Remove(input.Code);
            var data=1u|(MapVirtualKeyW((uint)input.Code,0)<<16)|(input.IsDown?0u:0xC0000000u);
            return PostMessageW(target,input.IsDown?0x100u:0x101u,(nuint)input.Code,(nint)data);
        }
        return false;
    }
    private static nint GetFocusedChild(nint hwnd)
    {
        var thread=GetWindowThreadProcessId(hwnd,out _);var info=new GuiInfo{Size=Marshal.SizeOf<GuiInfo>()};
        return GetGUIThreadInfo(thread,ref info)&&info.Focus!=0&&IsChild(hwnd,info.Focus)?info.Focus:hwnd;
    }
    private void Release(){var child=GetFocusedChild(_hwnd);foreach(var k in _keys)PostMessageW(child,0x101,(nuint)k,(nint)(0xC0000001u|(MapVirtualKeyW((uint)k,0)<<16)));_keys.Clear();_buttons=0;PostMessageW(child,0x202,0,0);PostMessageW(child,0x205,0,0);}
    public void Restore()
    {
        if(!IsWindow(_hwnd))return;GetWindowThreadProcessId(_hwnd,out var pid);if(pid!=_pid)return;
        var placement=_placement;SetWindowPlacement(_hwnd,ref placement);
    }
    public void Dispose(){if(_disposed)return;Release();Restore();_disposed=true;if(_helper is {} p){try{if(!p.HasExited)p.Kill();}catch{}p.Dispose();_helper=null;}}
    public static int RunRedrawHelper(string[] args)
    {
        if(!OperatingSystem.IsWindows()||args.Length!=9)return 2;
        var hwnd=(nint)long.Parse(args[1]);var source=uint.Parse(args[2]);using var parent=Process.GetProcessById(int.Parse(args[3]));
        var x=int.Parse(args[4]);var y=int.Parse(args[5]);var width=int.Parse(args[6]);var height=int.Parse(args[7]);var show=int.Parse(args[8]);
        if(width<1||height<1||width>8192||height>8192)return 2;
        void RestoreSource(){if(IsWindow(hwnd)){GetWindowThreadProcessId(hwnd,out var actual);if(actual==source){SetWindowPos(hwnd,0,x,y,width,height,0x0004|0x0010);ShowWindow(hwnd,show);}}}
        var watchdog=new Thread(()=>{try{parent.WaitForExit();RestoreSource();}finally{Environment.Exit(0);}}){IsBackground=true};watchdog.Start();
        var dc=GetDC(0);var mem=CreateCompatibleDC(dc);var bitmap=CreateCompatibleBitmap(dc,width,height);var old=SelectObject(mem,bitmap);
        try{while(!parent.HasExited&&IsWindow(hwnd)){GetWindowThreadProcessId(hwnd,out var actual);if(actual!=source)break;PrintWindow(hwnd,mem,2);Thread.Sleep(40);}return 0;}
        finally{RestoreSource();SelectObject(mem,old);DeleteObject(bitmap);DeleteDC(mem);ReleaseDC(0,dc);}
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo {public int Size,Flags;public nint Active,Focus,Capture,Menu,Move,Caret;public Rect CaretRect;}
    [DllImport("user32.dll")]static extern bool GetGUIThreadInfo(uint thread,ref GuiInfo info);
    [DllImport("user32.dll")]static extern bool IsChild(nint parent,nint child);
    [DllImport("user32.dll")]static extern bool IsWindow(nint h);
    [DllImport("user32.dll")]static extern bool IsIconic(nint h);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(nint h,out uint pid);
    [DllImport("user32.dll")]static extern bool GetWindowPlacement(nint h,ref Placement p);
    [DllImport("user32.dll")]static extern bool SetWindowPlacement(nint h,ref Placement p);
    [DllImport("user32.dll")]static extern bool GetWindowRect(nint h,out Rect r);
    [DllImport("user32.dll")]static extern bool SetWindowPos(nint h,nint insert,int x,int y,int w,int hgt,uint flags);
    [DllImport("user32.dll")]static extern int GetSystemMetrics(int n);
    [DllImport("user32.dll")]static extern bool ShowWindow(nint h,int cmd);
    [DllImport("user32.dll")]static extern bool PostMessageW(nint h,uint m,nuint w,nint l);
    [DllImport("user32.dll")]static extern nint ChildWindowFromPointEx(nint h,Point p,uint f);
    [DllImport("user32.dll")]static extern bool ScreenToClient(nint h,ref Point p);
    [DllImport("user32.dll")]static extern bool ClientToScreen(nint h,ref Point p);
    [DllImport("user32.dll")]static extern uint MapVirtualKeyW(uint code,uint map);
    [DllImport("user32.dll")]static extern bool PrintWindow(nint h,nint dc,uint flags);
    [DllImport("user32.dll")]static extern nint GetDC(nint h);
    [DllImport("user32.dll")]static extern int ReleaseDC(nint h,nint dc);
    [DllImport("gdi32.dll")]static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]static extern nint CreateCompatibleBitmap(nint dc,int w,int h);
    [DllImport("gdi32.dll")]static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32.dll")]static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")]static extern bool DeleteDC(nint dc);
}
