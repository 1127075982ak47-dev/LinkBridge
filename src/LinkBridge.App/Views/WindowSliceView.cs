using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LinkBridge.App.Services;
using LinkBridge.Core.Workspace;
using LinkBridge.Core.WindowRoaming;

namespace LinkBridge.App.Views;
/// <summary>Only the intersection with this device's desktop is rendered; no complete duplicate window.</summary>
public sealed class WindowSliceView : Window
{
    private readonly WorkspaceDevice _device;
    private SharedWindowLayout _layout;
    private readonly Canvas _surface=new(){ClipToBounds=true};
    private readonly Image _image=new(){Stretch=Stretch.Fill};
    private readonly TextBox _text=new(){Width=1,Height=1,Opacity=0,AcceptsReturn=true};
    private WriteableBitmap? _bitmap;
    private bool _closing,_dragging;
    private PixelPoint _dragPoint;
    private int _dragX,_dragY;
    private double _cropX,_cropY,_ratioX=1,_ratioY=1,_unit=1;
    public event Action<WindowRemoteInput>? InputRequested;
    public event Action<SharedWindowLayout>? LayoutRequested;
    public event Action? ReturnRequested;
    public WindowSliceView(WorkspaceDevice device,SharedWindowLayout layout)
    {
        _device=device;_layout=layout;Title=layout.Title+" · LinkBridge";WindowDecorations=Avalonia.Controls.WindowDecorations.None;CanResize=false;
        Background=Brushes.Black;ShowInTaskbar=true;MinWidth=1;MinHeight=1;
        _surface.Children.Add(_image);_surface.Children.Add(_text);Content=_surface;
        PointerPressed+=(_,e)=>
        {
            var p=e.GetPosition(this);var current=e.GetCurrentPoint(this);
            if(current.Properties.IsLeftButtonPressed&&FramePoint(p).Y<32)
            {_dragging=true;_dragPoint=this.PointToScreen(p);_dragX=_layout.X;_dragY=_layout.Y;e.Pointer.Capture(this);e.Handled=true;return;}
            _text.Focus();Send("button",p,current.Properties.IsLeftButtonPressed?0:current.Properties.IsRightButtonPressed?1:2,true);e.Handled=true;
        };
        PointerMoved+=(_,e)=>
        {
            var p=e.GetPosition(this);
            if(_dragging){var now=this.PointToScreen(p);var next=_layout with{X=_dragX+(int)((now.X-_dragPoint.X)/_ratioX),Y=_dragY+(int)((now.Y-_dragPoint.Y)/_ratioY)};SetLayout(next);LayoutRequested?.Invoke(next);}
            else Send("move",p);
        };
        PointerReleased+=(_,e)=>
        {
            if(_dragging){_dragging=false;e.Pointer.Capture(null);e.Handled=true;return;}
            var button=e.InitialPressMouseButton==MouseButton.Left?0:e.InitialPressMouseButton==MouseButton.Right?1:2;Send("button",e.GetPosition(this),button,false);e.Handled=true;
        };
        PointerWheelChanged+=(_,e)=>{Send("wheel",e.GetPosition(this),(int)Math.Clamp(e.Delta.Y*120,-12000,12000));e.Handled=true;};
        AddHandler(KeyDownEvent,(_,e)=>
        {
            if(e.Key==Key.Escape||e.Key==Key.M&&e.KeyModifiers.HasFlag(KeyModifiers.Control)&&e.KeyModifiers.HasFlag(KeyModifiers.Alt)){ReturnRequested?.Invoke();e.Handled=true;return;}
            var vk=VirtualKey(e.Key);if(vk!=0&&(vk<48||vk>90||e.KeyModifiers.HasFlag(KeyModifiers.Control)||e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
            {InputRequested?.Invoke(Input("key",code:vk,down:true));e.Handled=true;}
        },Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent,(_,e)=>{var vk=VirtualKey(e.Key);if(vk!=0)InputRequested?.Invoke(Input("key",code:vk,down:false));},Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _text.TextInput+=(_,e)=>{if(!string.IsNullOrEmpty(e.Text))InputRequested?.Invoke(Input("text",text:e.Text));e.Handled=true;};
        Deactivated+=(_,_)=>InputRequested?.Invoke(Input("release"));
        Closing+=(_,_)=>{if(!_closing)ReturnRequested?.Invoke();};
        Closed+=(_,_)=>{_bitmap?.Dispose();_bitmap=null;};
        Opened+=(_,_)=>{SetLayout(_layout);_text.Focus();};
        ToolTip.SetTip(_surface,"拖动标题区域移动窗口 · Esc 归还原窗口");
    }
    private WindowRemoteInput Input(string kind,int x=0,int y=0,int code=0,bool down=false,string? text=null)=>new(_layout.SessionId,kind,x,y,code,down,text,_layout.FrameWidth,_layout.FrameHeight);
    private (int X,int Y) FramePoint(Point p)=>((int)Math.Clamp((_cropX+p.X*_unit/_ratioX)*_layout.FrameWidth/_layout.Width,0,_layout.FrameWidth-1),(int)Math.Clamp((_cropY+p.Y*_unit/_ratioY)*_layout.FrameHeight/_layout.Height,0,_layout.FrameHeight-1));
    private void Send(string kind,Point p,int code=0,bool down=false){if(_bitmap is null)return;var fp=FramePoint(p);InputRequested?.Invoke(Input(kind,fp.X,fp.Y,code,down));}
    public void SetLayout(SharedWindowLayout geometry)
    {
        _layout=geometry;var left=Math.Max(geometry.X,_device.X);var top=Math.Max(geometry.Y,_device.Y);var right=Math.Min(geometry.X+geometry.Width,_device.X+_device.Width);var bottom=Math.Min(geometry.Y+geometry.Height,_device.Y+_device.Height);
        if(right<=left||bottom<=top){Hide();return;}
        var screens=Screens.All;if(screens.Count==0)return;
        var dx=screens.Min(x=>x.Bounds.X);var dy=screens.Min(x=>x.Bounds.Y);var dw=screens.Max(x=>x.Bounds.Right)-dx;var dh=screens.Max(x=>x.Bounds.Bottom)-dy;
        _ratioX=dw/(double)_device.Width;_ratioY=dh/(double)_device.Height;_unit=OperatingSystem.IsMacOS()?1:Math.Max(.5,RenderScaling);
        _cropX=left-geometry.X;_cropY=top-geometry.Y;
        Width=Math.Max(1,(right-left)*_ratioX/_unit);Height=Math.Max(1,(bottom-top)*_ratioY/_unit);
        Position=new PixelPoint(dx+(int)((left-_device.X)*_ratioX),dy+(int)((top-_device.Y)*_ratioY));
        _image.Width=geometry.Width*_ratioX/_unit;_image.Height=geometry.Height*_ratioY/_unit;
        Canvas.SetLeft(_image,-_cropX*_ratioX/_unit);Canvas.SetTop(_image,-_cropY*_ratioY/_unit);
        if(!IsVisible)Show();
    }
    public void SetFrame(byte[] bgra,int width,int height)
    {
        if(_closing)return;
        if(_bitmap is null||_bitmap.PixelSize.Width!=width||_bitmap.PixelSize.Height!=height)
        {_bitmap?.Dispose();_bitmap=new WriteableBitmap(new PixelSize(width,height),new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Premul);_image.Source=_bitmap;}
        using(var locked=_bitmap.Lock()){for(int y=0;y<height;y++)Marshal.Copy(bgra,y*width*4,locked.Address+y*locked.RowBytes,width*4);}
        _image.InvalidateVisual();
    }
    public void CloseSession(){_closing=true;Close();}
    private static int VirtualKey(Key key)=>key switch
    {
        >=Key.A and <=Key.Z => 65+(int)key-(int)Key.A,
        >=Key.D0 and <=Key.D9 =>48+(int)key-(int)Key.D0,
        Key.Back=>8,Key.Tab=>9,Key.Enter=>13,Key.LeftShift=>0xA0,Key.RightShift=>0xA1,Key.LeftCtrl=>0xA2,Key.RightCtrl=>0xA3,Key.LeftAlt=>0xA4,Key.RightAlt=>0xA5,
        Key.Left=>37,Key.Up=>38,Key.Right=>39,Key.Down=>40,Key.Delete=>46,Key.Home=>36,Key.End=>35,Key.PageUp=>33,Key.PageDown=>34,
        _=>0
    };
}
