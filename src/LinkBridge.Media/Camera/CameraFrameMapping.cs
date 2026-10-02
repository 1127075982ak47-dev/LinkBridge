using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LinkBridge.Media.Camera;

/// <summary>Writer for the native camera's versioned, double-buffered BGRA frame mapping.</summary>
[SupportedOSPlatform("windows")]
internal sealed class CameraFrameMapping : IDisposable
{
    public const int Width=3840, Height=2160, Stride=15360, HeaderBytes=4096;
    public const int FrameBytes=33177600;
    public const long MappingBytes=HeaderBytes+(long)FrameBytes*2;
    private readonly SafeMappingHandle _handle;
    private readonly IntPtr _view;
    private bool _disposed;
    public CameraFrameMapping(string name)
    {
        _handle=OpenFileMappingW(6,false,name);
        if(_handle.IsInvalid) { _handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(),"共享摄像头帧通道尚未创建。"); }
        _view=MapViewOfFile(_handle,6,0,0,(nuint)MappingBytes);
        if(_view==IntPtr.Zero) { var error=Marshal.GetLastWin32Error();_handle.Dispose();throw new Win32Exception(error,"无法映射共享摄像头帧通道。"); }
        try
        {
            var magic=new byte[8]; Marshal.Copy(_view,magic,0,8);
            if(!magic.SequenceEqual(Encoding.ASCII.GetBytes("LBCAM01\0"))||Marshal.ReadInt32(_view,8)!=1||Marshal.ReadInt32(_view,12)!=HeaderBytes||
                Marshal.ReadInt32(_view,16)!=Width||Marshal.ReadInt32(_view,20)!=Height||Marshal.ReadInt32(_view,24)!=Stride||
                Marshal.ReadInt32(_view,28)!=0x41524742||Marshal.ReadInt64(_view,32)!=FrameBytes)
                throw new InvalidDataException("共享摄像头帧通道版本或4K格式不兼容。");
        }
        catch { UnmapViewOfFile(_view); _handle.Dispose(); throw; }
    }
    public void Publish(byte[] frame)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(frame.Length<FrameBytes) throw new ArgumentException("A complete native 4K BGRA frame is required.",nameof(frame));
        var slot=Marshal.ReadInt32(_view,48);
        if(slot is <0 or >1) throw new InvalidDataException("Invalid camera frame slot.");
        var inactive=1-slot;
        var sequence=Marshal.ReadInt64(_view,40);
        var odd=(sequence&1)==0?sequence+1:sequence+2;
        // Aligned Int64 stores are atomic on the supported Windows x64/arm64 targets.
        // Explicit full barriers match the consumer's Interlocked seqlock reads.
        Marshal.WriteInt64(_view,40,odd); Thread.MemoryBarrier();
        Marshal.Copy(frame,0,IntPtr.Add(_view,HeaderBytes+inactive*FrameBytes),FrameBytes);
        Marshal.WriteInt32(_view,48,inactive);
        Marshal.WriteInt32(_view,52,1);
        Marshal.WriteInt64(_view,56,(long)(Stopwatch.GetTimestamp()*(10000000d/Stopwatch.Frequency)));
        Marshal.WriteInt64(_view,64,Environment.ProcessId);
        Marshal.WriteInt64(_view,72,Environment.TickCount64);
        Thread.MemoryBarrier(); Marshal.WriteInt64(_view,40,odd+1);
    }
    public void MarkOffline()
    {
        if(_disposed)return;
        Marshal.WriteInt32(_view,52,0); Marshal.WriteInt64(_view,72,0); Thread.MemoryBarrier();
    }
    public void Dispose()
    {
        if(_disposed)return; _disposed=true;
        UnmapViewOfFile(_view); _handle.Dispose();
    }
    private sealed class SafeMappingHandle():SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle()=>CloseHandle(handle);
    }
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    private static extern SafeMappingHandle OpenFileMappingW(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,string name);
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern IntPtr MapViewOfFile(SafeMappingHandle handle,uint access,uint high,uint low,nuint bytes);
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}


