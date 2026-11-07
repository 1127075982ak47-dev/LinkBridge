using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LinkBridge.App.ViewModels;
using LinkBridge.Core.Workspace;
using System.Text.Json;

namespace LinkBridge.App.Views;

public sealed class WorkspaceWindow : Window
{
    private MainViewModel _model;
    private WorkspaceProfile _draft;
    private readonly Func<WorkspaceProfile,Task<MainViewModel>> _apply;
    private readonly Canvas _canvas=new(){Height=330,Background=Brush.Parse("#EDF3FB"),ClipToBounds=true};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Foreground=Brush.Parse("#325884")};
    private readonly TextBlock _links=new(){TextWrapping=TextWrapping.Wrap,FontSize=12};
    private readonly ComboBox _controller=new(){MinWidth=180};
    private readonly ComboBox _selected=new(){MinWidth=180};
    private readonly CheckBox _enabled=new(){Content="启用通用多机模式"};
    private readonly CheckBox _clipboard=new(){Content="同步文字与图片剪贴板"};
    private readonly TextBox _name=new(),_address=new(),_fingerprint=new(),_engine=new();
    private readonly NumericUpDown _x=Number(-100000,100000),_y=Number(-100000,100000),_width=Number(320,32768),_height=Number(200,32768),_delay=Number(0,2000),_port=Number(1024,65535);
    private WorkspaceDevice? _device;
    private bool _loading;
    private double _scale=.15,_originX=20,_originY=20;
    private Point _dragStart;
    private int _dragX,_dragY;
    private string? _dragId;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(1)};
    private static NumericUpDown Number(decimal min,decimal max)=>new(){Minimum=min,Maximum=max,Increment=1,FormatString="0",MinWidth=85};
    public WorkspaceWindow(MainViewModel model,Func<WorkspaceProfile,Task<MainViewModel>> apply)
    {
        _model=model;_apply=apply;_draft=WorkspaceStore.Parse(WorkspaceStore.Export(model.Workspace));
        Title="LinkBridge · 设备与布局";Width=1120;Height=860;MinWidth=850;MinHeight=650;
        Background=Brush.Parse("#F8FAFD");FontFamily=new FontFamily("Microsoft YaHei, PingFang SC, Noto Sans CJK SC");
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var content=new StackPanel{Spacing=14,Margin=new Thickness(24)};
        content.Children.Add(new TextBlock{Text="你的电脑，按你的桌面排列",FontSize=25,FontWeight=FontWeight.SemiBold});
        content.Children.Add(new TextBlock{Text="最多 4 台 · 系统和位置互相独立 · 拖动卡片让边缘相接，重叠部分就是鼠标可以穿越的范围",TextWrapping=TextWrapping.Wrap,Foreground=Brush.Parse("#596D89")});
        var top=new WrapPanel{Orientation=Orientation.Horizontal};
        Add(top,_enabled,16);Add(top,new TextBlock{Text="键鼠接在哪台电脑",VerticalAlignment=VerticalAlignment.Center},10);Add(top,_controller,16);Add(top,_clipboard,8);
        content.Children.Add(top);
        var tools=new WrapPanel();
        ActionButton(tools,"导出本机身份",ExportIdentity);ActionButton(tools,"添加设备",ImportIdentity);ActionButton(tools,"导出布局",ExportLayout);ActionButton(tools,"导入布局",ImportLayout);
        ActionButton(tools,"横向排列",()=>Arrange(0));ActionButton(tools,"纵向排列",()=>Arrange(1));ActionButton(tools,"两行排列",()=>Arrange(2));
        content.Children.Add(tools);
        content.Children.Add(_canvas);content.Children.Add(_links);
        var detail=new Grid{ColumnDefinitions=new ColumnDefinitions("*,*"),ColumnSpacing=22};
        var left=new StackPanel{Spacing=8};var right=new StackPanel{Spacing=8};Grid.SetColumn(right,1);detail.Children.Add(left);detail.Children.Add(right);
        Row(left,"当前设备",_selected);Row(left,"设备名称",_name);Row(left,"可达地址",_address);
        var geometry=new WrapPanel();foreach(var pair in new[]{("位置 X",_x),("位置 Y",_y),("范围宽",_width),("范围高",_height)})
        {var stack=new StackPanel{Margin=new Thickness(0,0,10,0),Spacing=3};stack.Children.Add(new TextBlock{Text=pair.Item1,FontSize=12});stack.Children.Add(pair.Item2);geometry.Children.Add(stack);}left.Children.Add(geometry);
        var localTools=new WrapPanel();ActionButton(localTools,"采用本机屏幕范围",LocalBounds);ActionButton(localTools,"移除选中设备",RemoveDevice);left.Children.Add(localTools);
        Row(right,"已核对的身份指纹",_fingerprint);_fingerprint.IsReadOnly=true;_fingerprint.TextWrapping=TextWrapping.Wrap;
        Row(right,"边缘停留毫秒",_delay);Row(right,"键鼠连接端口",_port);
        Row(right,"通用组件位置",_engine);_engine.PlaceholderText="自动查找随包或已安装的 Deskflow";
        var networkTools=new WrapPanel();ActionButton(networkTools,"选择组件",SelectEngine);ActionButton(networkTools,"检测网络",Diagnose);right.Children.Add(networkTools);
        content.Children.Add(detail);
        content.Children.Add(new TextBlock{Text="跨网络：填写已经可达的 VPN／组网地址、IPv6 或域名。局域网 IP 不能直接用于外网；CGNAT 环境可先使用 Tailscale、ZeroTier 或 WireGuard。网络检测通过仍需双方身份核对与系统权限。",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brush.Parse("#596D89")});
        var actions=new WrapPanel();ActionButton(actions,"保存并应用",Apply);ActionButton(actions,"连接工作区",()=>_model.StartWorkspaceAsync());ActionButton(actions,"停止键鼠共享",()=>_model.WorkspaceRuntime.StopAsync());
        ActionButton(actions,"窗口跨机",OpenWindows);content.Children.Add(actions);content.Children.Add(_status);
        content.Children.Add(new TextBlock{Text="通用键鼠由独立的 Deskflow 开源组件提供，保留上游 GPL 许可。Ctrl + Alt + M 返回键鼠来源电脑。",FontSize=11,Foreground=Brush.Parse("#6D7C91"),TextWrapping=TextWrapping.Wrap});
        Content=new ScrollViewer{Content=content,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
        _controller.SelectionChanged+=(_,_)=>{if(!_loading && _controller.SelectedItem is WorkspaceDevice d)_draft.ControllerId=d.Id;};
        _selected.SelectionChanged+=(_,_)=>{if(!_loading){CommitFields();Select(_selected.SelectedItem as WorkspaceDevice);}};
        foreach(var box in new[]{_name,_address})box.LostFocus+=(_,_)=>{CommitFields();Draw();};
        foreach(var number in new[]{_x,_y,_width,_height})number.LostFocus+=(_,_)=>{CommitFields();Draw();};
        _canvas.SizeChanged+=(_,_)=>Draw();
        _timer.Tick+=(_,_)=>{if(_dragId is null && _model.Workspace.Enabled)_status.Text=_model.WorkspaceRuntime.Status;};
        Opened+=(_,_)=>{RefreshLists(_draft.LocalDeviceId);_timer.Start();};Closed+=(_,_)=>_timer.Stop();
        _enabled.IsChecked=_draft.Enabled;_clipboard.IsChecked=_draft.ShareClipboard;_delay.Value=_draft.SwitchDelayMs;_port.Value=_draft.InputPort;_engine.Text=_draft.EnginePath;
    }
    private static void Add(Panel p,Control c,int margin){c.Margin=new Thickness(0,0,margin,5);p.Children.Add(c);}
    private void ActionButton(Panel panel,string text,Action action)=>ActionButton(panel,text,()=>{action();return Task.CompletedTask;});
    private void ActionButton(Panel panel,string text,Func<Task> action)
    {
        var b=new Button{Content=text,Margin=new Thickness(0,0,8,6),Padding=new Thickness(12,7)};
        b.Click+=async(_,_)=>{b.IsEnabled=false;_timer.Stop();try{await action();}catch(Exception e){_status.Text=e.Message;}finally{b.IsEnabled=true;}};panel.Children.Add(b);
    }
    private static void Row(Panel panel,string label,Control c)
    {var g=new Grid{ColumnDefinitions=new ColumnDefinitions("118,*"),ColumnSpacing=8};g.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,FontSize=12});Grid.SetColumn(c,1);g.Children.Add(c);panel.Children.Add(g);}
    private void CommitFields()
    {
        if(_loading||_device is null)return;
        _device.Name=_name.Text?.Trim()??"";_device.Address=_address.Text?.Trim()??"";
        _device.X=(int)(_x.Value??0);_device.Y=(int)(_y.Value??0);_device.Width=(int)(_width.Value??1920);_device.Height=(int)(_height.Value??1080);
    }
    private void Select(WorkspaceDevice? d)
    {
        _loading=true;_device=d;
        if(d is not null){_name.Text=d.Name;_address.Text=d.Address;_fingerprint.Text=d.Fingerprint;_x.Value=d.X;_y.Value=d.Y;_width.Value=d.Width;_height.Value=d.Height;}
        _loading=false;Draw();
    }
    private void RefreshLists(string? selected=null)
    {
        _loading=true;_controller.ItemsSource=_draft.Devices.ToArray();_selected.ItemsSource=_draft.Devices.ToArray();
        _controller.SelectedItem=_draft.Devices.FirstOrDefault(d=>d.Id==_draft.ControllerId);_selected.SelectedItem=_draft.Devices.FirstOrDefault(d=>d.Id==selected)??_draft.Local;
        _loading=false;Select(_selected.SelectedItem as WorkspaceDevice);
    }
    private void Draw()
    {
        _canvas.Children.Clear();if(_draft.Devices.Count==0)return;
        var minX=_draft.Devices.Min(x=>x.X);var minY=_draft.Devices.Min(x=>x.Y);
        var maxX=_draft.Devices.Max(x=>x.X+x.Width);var maxY=_draft.Devices.Max(x=>x.Y+x.Height);
        if(_dragId is null)
        {_scale=Math.Min((Math.Max(650,_canvas.Bounds.Width)-60)/Math.Max(1920,maxX-minX),(330d-60)/Math.Max(1080,maxY-minY));_originX=30-minX*_scale;_originY=30-minY*_scale;}
        foreach(var d in _draft.Devices)
        {
            var text=new StackPanel{Margin=new Thickness(10),Spacing=4,VerticalAlignment=VerticalAlignment.Center};
            text.Children.Add(new TextBlock{Text=d.Name+(d.Id==_draft.LocalDeviceId?" · 本机":""),FontWeight=FontWeight.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis});
            text.Children.Add(new TextBlock{Text=d.Platform+(d.Id==_draft.ControllerId?" · 键鼠来源":""),FontSize=11});
            text.Children.Add(new TextBlock{Text=$"{d.Width} × {d.Height}",FontSize=11});
            var card=new Border{Width=Math.Max(60,d.Width*_scale),Height=Math.Max(55,d.Height*_scale),Background=Brush.Parse(d.Id==_device?.Id?"#D5E6FF":"#FFFFFF"),BorderBrush=Brush.Parse("#5B82BC"),BorderThickness=new Thickness(1.5),CornerRadius=new CornerRadius(8),Child=text};
            Canvas.SetLeft(card,_originX+d.X*_scale);Canvas.SetTop(card,_originY+d.Y*_scale);_canvas.Children.Add(card);
            card.PointerPressed+=(_,e)=>
            {if(!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)return;CommitFields();_dragId=d.Id;_dragStart=e.GetPosition(_canvas);_dragX=d.X;_dragY=d.Y;e.Pointer.Capture(card);_loading=true;_selected.SelectedItem=d;_loading=false;_device=d;_name.Text=d.Name;_address.Text=d.Address;_fingerprint.Text=d.Fingerprint;e.Handled=true;};
            card.PointerMoved+=(_,e)=>
            {if(_dragId!=d.Id)return;var pt=e.GetPosition(_canvas);d.X=_dragX+(int)Math.Round((pt.X-_dragStart.X)/_scale);d.Y=_dragY+(int)Math.Round((pt.Y-_dragStart.Y)/_scale);Canvas.SetLeft(card,_originX+d.X*_scale);Canvas.SetTop(card,_originY+d.Y*_scale);};
            card.PointerReleased+=(_,e)=>
            {if(_dragId!=d.Id)return;e.Pointer.Capture(null);_dragId=null;Snap(d);Select(d);};
        }
        _links.Text=string.Join("   |   ",WorkspaceTopology.Links(_draft).Where(l=>l.Direction is "right" or "down").Select(l=>$"{_draft.Devices.Single(d=>d.Id==l.SourceId).Name} {(l.Direction=="right"?"→":"↓")} {_draft.Devices.Single(d=>d.Id==l.TargetId).Name} · 接缝 {l.Start:0}%–{l.End:0}%"));
    }
    private void Snap(WorkspaceDevice d)
    {
        var threshold=28/_scale;
        foreach(var b in _draft.Devices.Where(b=>b.Id!=d.Id))
        {foreach(var x in new[]{b.X+b.Width,b.X-d.Width,b.X})if(Math.Abs(d.X-x)<threshold){d.X=x;break;}
         foreach(var y in new[]{b.Y+b.Height,b.Y-d.Height,b.Y})if(Math.Abs(d.Y-y)<threshold){d.Y=y;break;}}
    }
    private void Arrange(int kind)
    {
        CommitFields();int x=0,y=0,rowHeight=0;
        for(int i=0;i<_draft.Devices.Count;i++){var d=_draft.Devices[i];if(kind==2&&i==2){x=0;y=rowHeight;}d.X=x;d.Y=y;if(kind==1)y+=d.Height;else x+=d.Width;rowHeight=Math.Max(rowHeight,d.Height);}
        Select(_device);
    }
    private void LocalBounds()
    {
        if(_device?.Id!=_draft.LocalDeviceId)throw new InvalidOperationException("只能在本机读取本机显示范围；其他设备请在自身客户端导出身份。");
        var screens=Screens.All;if(screens.Count==0)return;
        _draft.Local.Width=screens.Max(s=>s.Bounds.Right)-screens.Min(s=>s.Bounds.X);
        _draft.Local.Height=screens.Max(s=>s.Bounds.Bottom)-screens.Min(s=>s.Bounds.Y);Select(_device);
    }
    private void RemoveDevice()
    {if(_device is null||_device.Id==_draft.LocalDeviceId)throw new InvalidOperationException("本机不能移除。");_draft.Devices.Remove(_device);if(!_draft.Devices.Any(d=>d.Id==_draft.ControllerId))_draft.ControllerId=_draft.LocalDeviceId;RefreshLists();}
    private async Task<string?> ReadJson()
    {var f=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title="选择设备或布局文件",AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("LinkBridge JSON"){Patterns=["*.json"]}]});if(f.Count==0)return null;await using var s=await f[0].OpenReadAsync();if(s.CanSeek&&s.Length>256*1024)throw new InvalidDataException("文件过大。");using var reader=new StreamReader(s);return await reader.ReadToEndAsync();}
    private async Task SaveJson(string name,string json)
    {var f=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions{Title="保存 LinkBridge 配置",SuggestedFileName=name,DefaultExtension="json"});if(f is null)return;await using var s=await f.OpenWriteAsync();s.SetLength(0);await using var writer=new StreamWriter(s);await writer.WriteAsync(json);_status.Text="已导出，仅含设备公开身份和布局，不含私钥。";}
    private Task ExportIdentity(){CommitFields();return SaveJson("LinkBridge-device.json",JsonSerializer.Serialize(_draft.Local,new JsonSerializerOptions{WriteIndented=true}));}
    private async Task ImportIdentity()
    {
        CommitFields();var text=await ReadJson();if(text is null)return;var d=JsonSerializer.Deserialize<WorkspaceDevice>(text)??throw new InvalidDataException("设备文件无效。");
        var test=new WorkspaceProfile{LocalDeviceId=d.Id,ControllerId=d.Id,Devices=[d]};WorkspaceTopology.Validate(test,false);if(d.Fingerprint.Length!=64)throw new InvalidDataException("设备文件没有有效指纹。");
        if(d.Id==_draft.LocalDeviceId)throw new InvalidOperationException("这是本机身份，请从另一台电脑导出。");
        var old=_draft.Devices.FirstOrDefault(x=>x.Id==d.Id);
        if(old is not null){d.X=old.X;d.Y=old.Y;_draft.Devices.Remove(old);}else{if(_draft.Devices.Count>=4)throw new InvalidOperationException("本版最多 4 台设备。");d.X=_draft.Devices.Max(x=>x.X+x.Width);d.Y=_draft.Local.Y;}
        _draft.Devices.Add(d);RefreshLists(d.Id);_status.Text="已添加设备。请通过可信渠道核对指纹，填写可达地址，保存并应用后才会建立连接。";
    }
    private Task ExportLayout(){CommitFields();WorkspaceTopology.Validate(_draft);return SaveJson("LinkBridge-layout.json",WorkspaceStore.Export(_draft));}
    private async Task ImportLayout()
    {
        var text=await ReadJson();if(text is null)return;var p=WorkspaceStore.Parse(text);var local=p.Devices.SingleOrDefault(d=>d.Id==_draft.LocalDeviceId)??throw new InvalidOperationException("布局中没有本机，请先把本机身份添加到控制端布局，再导出。");
        if(local.Fingerprint!=_draft.Local.Fingerprint)throw new InvalidDataException("布局中的本机身份指纹不符。");
        p.LocalDeviceId=_draft.LocalDeviceId;p.EnginePath=_draft.EnginePath;p.Enabled=_draft.Enabled;_draft=p;RefreshLists();_port.Value=p.InputPort;_delay.Value=p.SwitchDelayMs;_clipboard.IsChecked=p.ShareClipboard;_status.Text="已导入布局，请检查键鼠来源、各设备地址及指纹，再保存并应用。";
    }
    private async Task SelectEngine()
    {var f=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title="选择 deskflow-core 程序",AllowMultiple=false});if(f.Count>0)_engine.Text=f[0].TryGetLocalPath();}
    private async Task Diagnose(){CommitFields();var d=_device??_draft.Controller;_status.Text="正在检测地址和端口…";_status.Text=await DeskflowRuntime.DiagnoseAsync(d.Address,(int)(_port.Value??45690));}
    private async Task Apply()
    {
        CommitFields();_draft.Enabled=_enabled.IsChecked==true;_draft.ShareClipboard=_clipboard.IsChecked==true;_draft.SwitchDelayMs=(int)(_delay.Value??150);_draft.InputPort=(int)(_port.Value??45690);_draft.EnginePath=_engine.Text?.Trim()??"";
        WorkspaceTopology.Validate(_draft,_draft.Enabled);_model=await _apply(_draft);_draft=WorkspaceStore.Parse(WorkspaceStore.Export(_model.Workspace));RefreshLists(_device?.Id);_status.Text=_draft.Enabled?_model.WorkspaceRuntime.Status:"已恢复经典双机模式，原配对设置保留。";_timer.Start();
    }
    private void OpenWindows()=>new WorkspaceWindowsWindow(_model).Show(this);
}
