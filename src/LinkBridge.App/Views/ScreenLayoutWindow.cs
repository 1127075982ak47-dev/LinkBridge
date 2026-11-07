using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LinkBridge.Core.DisplayLayout;

namespace LinkBridge.App.Views;

/// <summary>The paired Windows/Mac editor. OS-owned monitor positions stay fixed.</summary>
public sealed class ScreenLayoutWindow : Window
{
    private readonly Func<Task<(LayoutSnapshot Windows,LayoutSnapshot Mac)>> _read;
    private readonly Func<ScreenLayout,Task> _apply;
    private readonly Func<Task> _identify,_restore;
    private readonly Canvas _canvas=new(){Height=340,Background=Brush.Parse("#EEF3F9"),ClipToBounds=true};
    private readonly TextBlock _status=Text("正在读取两台电脑的屏幕…"),_seam=Text(""),_connection=Text("");
    private readonly ComboBox _local=new(){MinWidth=170},_remote=new(){MinWidth=170},_edge=new(){MinWidth=170},_delay=new(){MinWidth=140};
    private readonly Slider _coverage=new(){Minimum=20,Maximum=100,Value=100,Width=210};
    private readonly Button _save=new(){Content="保存并应用",Padding=new Thickness(22,10),IsEnabled=false};
    private readonly Button _refresh=new(){Content="刷新屏幕"},_identifyButton=new(){Content="识别两端屏幕"},_restoreButton=new(){Content="恢复上一套布局"};
    private LayoutSnapshot? _windows,_mac;
    private ScreenLayout? _draft;
    private bool _loading,_busy;
    private double _scale,_ox,_oy;
    private Point _dragStart;
    private double _remoteX,_remoteY,_startX,_startY;
    private Border? _remoteCard;
    private bool _dragging;
    private static TextBlock Text(string value)=>new(){Text=value,TextWrapping=TextWrapping.Wrap,Foreground=Brush.Parse("#415776")};
    private static readonly string[] Edges=["右侧","下方","左侧","上方"];
    private static readonly string[] EdgeCodes=["right","bottom","left","top"];
    private static readonly int[] Delays=[0,150,300,500];
    public ScreenLayoutWindow(Func<Task<(LayoutSnapshot Windows,LayoutSnapshot Mac)>> read,Func<ScreenLayout,Task> apply,
        Func<Task> identify,Func<Task> restore,Action advanced)
    {
        _read=read;_apply=apply;_identify=identify;_restore=restore;
        Title="LinkBridge · 屏幕布局";Width=980;Height=850;MinWidth=780;MinHeight=650;
        Background=Brush.Parse("#F8FAFD");FontFamily=new FontFamily("Microsoft YaHei, PingFang SC, Noto Sans CJK SC");
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Spacing=14,Margin=new Thickness(26)};
        body.Children.Add(new TextBlock{Text="把屏幕摆成你桌上的样子",FontSize=26,FontWeight=FontWeight.SemiBold,Foreground=Brush.Parse("#183754")});
        body.Children.Add(Text("① 识别屏幕　② 拖动 Mac 卡片，让边缘接到 Windows 外侧　③ 保存并应用"));
        var toolbar=new WrapPanel();Add(toolbar,_identifyButton);Add(toolbar,_refresh);Add(toolbar,_restoreButton);body.Children.Add(toolbar);
        body.Children.Add(_connection);body.Children.Add(_canvas);body.Children.Add(_seam);
        var controls=new WrapPanel();Field(controls,"Windows 接入口",_local);Field(controls,"Mac 目标屏幕",_remote);Field(controls,"Mac 放在",_edge);body.Children.Add(controls);
        var options=new WrapPanel();Field(options,"可穿越范围",_coverage);Field(options,"边缘停留",_delay);body.Children.Add(options);
        body.Children.Add(Text("绿色接缝可以穿越，其他边缘保持在当前电脑。缩小可穿越范围可减少误切；边缘停留可避免鼠标一碰就切走。"));
        var actions=new WrapPanel();Add(actions,_save);var close=new Button{Content="关闭",Padding=new Thickness(20,10)};close.Click+=(_,_)=>Close();Add(actions,close);body.Children.Add(actions);body.Children.Add(_status);
        var help=new StackPanel{Spacing=8};
        help.Children.Add(Text("• 键鼠接在 Windows 上：从绿色接缝进入 Mac，从相对边缘返回。Ctrl + Alt + M 随时返回 Windows。"));
        help.Children.Add(Text("• Windows 自己的多块屏幕跟随系统排列，在系统“显示设置”中调整。联桥只设置两台电脑之间的穿越位置。"));
        help.Children.Add(Text("• 在任意一端修改后保存，会应用到同一套布局。换屏、拔屏后请刷新并重新确认；离线时不会覆盖已保存的设置。"));
        help.Children.Add(Text("• 当前为 Windows 键鼠控制 Mac 的双机模式。多台电脑和其他控制来源可在高级多机工作区设置，需要相应组件。"));
        var advancedButton=new Button{Content="打开高级多机工作区"};advancedButton.Click+=(_,_)=>advanced();help.Children.Add(advancedButton);
        body.Children.Add(new Expander{Header="怎么设置与返回",Content=help,IsExpanded=true});
        Content=new ScrollViewer{Content=body,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
        _local.ItemTemplate=_remote.ItemTemplate=new FuncDataTemplate<ScreenInfo>((s,_)=>new TextBlock{Text=s is null?"":$"{s.Name} · {s.Width}×{s.Height}"});
        _edge.ItemsSource=Edges;_delay.ItemsSource=new[]{"立即切换","停留 150 毫秒","停留 300 毫秒","停留 500 毫秒"};
        _local.SelectionChanged+=(_,_)=>EditPlacement(true);_remote.SelectionChanged+=(_,_)=>EditPlacement(true);_edge.SelectionChanged+=(_,_)=>EditPlacement(true);
        _delay.SelectionChanged+=(_,_)=>EditPlacement(false);
        _coverage.PropertyChanged+=(_,e)=>{if(e.Property==Slider.ValueProperty)EditPlacement(false);};
        _canvas.SizeChanged+=(_,_)=>{if(!_dragging)Draw();};
        _refresh.Click+=async(_,_)=>await RunAsync(async()=>{await LoadAsync();_status.Text="已刷新。尚未保存的排列会被重新读取的布局替换。";});
        _identifyButton.Click+=async(_,_)=>await RunAsync(async()=>{await _identify();_status.Text="两端屏幕正在显示编号，对照编号选择或拖动卡片。";});
        _restoreButton.Click+=async(_,_)=>await RunAsync(async()=>{await _restore();await LoadAsync();_status.Text="已恢复并应用上一套布局。";});
        _save.Click+=async(_,_)=>await SaveLayoutAsync();
        KeyDown+=async(_,e)=>{if(e.Key==Key.S&&e.KeyModifiers.HasFlag(KeyModifiers.Control)){e.Handled=true;await SaveLayoutAsync();}};
        ToolTip.SetTip(_save,"保存并应用 · Ctrl + S");
        Closing+=(_,e)=>{if(_busy){e.Cancel=true;_status.Text="正在应用设置，请稍候。";}};
        Opened+=async(_,_)=>await RunAsync(LoadAsync);
    }
    private Task SaveLayoutAsync()=>RunAsync(async()=>{
            if(_draft is null)throw new InvalidOperationException("请先刷新屏幕。");
            _=ScreenPortal.Create(_draft,_windows!.Screens,_mac!.Screens);await _apply(_draft);
            await LoadAsync();_status.Text="已保存并应用。现在可沿绿色接缝试着移动鼠标；Ctrl + Alt + M 返回 Windows。";
        });
    private static void Add(Panel panel,Control control){control.Margin=new Thickness(0,0,10,6);panel.Children.Add(control);}
    private static void Field(Panel panel,string label,Control control){var stack=new StackPanel{Spacing=5,Margin=new Thickness(0,0,20,6)};stack.Children.Add(Text(label));stack.Children.Add(control);panel.Children.Add(stack);}
    private async Task RunAsync(Func<Task> action)
    {
        if(_busy)return;_busy=true;SetEnabled(false);
        try{await action();}catch(Exception e){_status.Text=e is InvalidOperationException or InvalidDataException?e.Message:"没有完成操作。请确认两端联桥均已更新、已配对并处于同一网络，再刷新重试。";}
        finally{_busy=false;SetEnabled(true);}
    }
    private void SetEnabled(bool enabled)
    {
        foreach(var c in new Control[]{_canvas,_local,_remote,_edge,_coverage,_delay,_refresh,_identifyButton,_restoreButton})c.IsEnabled=enabled;
        _save.IsEnabled=enabled&&Valid();
    }
    private async Task LoadAsync()
    {
        var state=await _read();_windows=state.Windows;_mac=state.Mac;
        _draft=state.Windows.Layout??new ScreenLayout{WindowsScreenId=state.Windows.Screens.Last().Id,MacScreenId=state.Mac.Screens.First().Id};
        _connection.Text=$"{state.Windows.Name}（Windows，{state.Windows.Screens.Length} 块屏幕）  ↔  {state.Mac.Name}（Mac，{state.Mac.Screens.Length} 块屏幕）";
        _loading=true;_local.ItemsSource=_windows.Screens;_remote.ItemsSource=_mac.Screens;_loading=false;SyncControls();Draw();
        if(!string.IsNullOrEmpty(state.Windows.Status))_status.Text=state.Windows.Status;
        else _status.Text="拖动蓝色 Mac 卡片或使用下方选项。保存前，当前连接保持原来的布局。";
    }
    private void SyncControls()
    {
        if(_draft is null)return;_loading=true;
        _local.SelectedItem=_windows?.Screens.FirstOrDefault(s=>s.Id==_draft.WindowsScreenId);
        _remote.SelectedItem=_mac?.Screens.FirstOrDefault(s=>s.Id==_draft.MacScreenId);
        _edge.SelectedIndex=Array.IndexOf(EdgeCodes,_draft.Edge);_coverage.Value=_draft.CoveragePercent;
        _delay.SelectedIndex=Math.Max(0,Array.IndexOf(Delays,_draft.SwitchDelayMs));_loading=false;
    }
    private void EditPlacement(bool center)
    {
        if(_loading||_draft is null||_local.SelectedItem is not ScreenInfo w||_remote.SelectedItem is not ScreenInfo m)return;
        var edge=EdgeCodes[Math.Max(0,_edge.SelectedIndex)];int offset=_draft.Offset;
        if(center)offset=edge is "top" or "bottom"?(w.Width-m.Width)/2:(w.Height-m.Height)/2;
        _draft=_draft with{WindowsScreenId=w.Id,MacScreenId=m.Id,Edge=edge,Offset=offset,CoveragePercent=(int)_coverage.Value,SwitchDelayMs=Delays[Math.Max(0,_delay.SelectedIndex)]};
        Draw();
    }
    private bool Valid()
    {
        if(_draft is null||_windows is null||_mac is null)return false;
        try{_=ScreenPortal.Create(_draft,_windows.Screens,_mac.Screens);return true;}catch{return false;}
    }
    private (double X,double Y) RemotePosition(ScreenInfo w,ScreenInfo m,ScreenLayout p)=>p.Edge switch{
        "bottom"=>(w.X+p.Offset,w.Y+w.Height),"top"=>(w.X+p.Offset,w.Y-m.Height),
        "left"=>(w.X-m.Width,w.Y+p.Offset),_=>(w.X+w.Width,w.Y+p.Offset)};
    private void Draw()
    {
        if(_draft is null||_windows is null||_mac is null)return;
        _canvas.Children.Clear();var w=_windows.Screens.FirstOrDefault(s=>s.Id==_draft.WindowsScreenId);var m=_mac.Screens.FirstOrDefault(s=>s.Id==_draft.MacScreenId);
        if(w is null||m is null){_seam.Text="原来的屏幕已断开，请在下方选择当前屏幕。";_save.IsEnabled=false;return;}
        (_remoteX,_remoteY)=RemotePosition(w,m,_draft);
        var minX=Math.Min(_windows.Screens.Min(s=>s.X),_remoteX);var minY=Math.Min(_windows.Screens.Min(s=>s.Y),_remoteY);
        var maxX=Math.Max(_windows.Screens.Max(s=>s.X+s.Width),_remoteX+m.Width);var maxY=Math.Max(_windows.Screens.Max(s=>s.Y+s.Height),_remoteY+m.Height);
        _scale=Math.Min((Math.Max(680,_canvas.Bounds.Width)-100)/Math.Max(1000,maxX-minX),260/Math.Max(800,maxY-minY));
        _ox=(Math.Max(680,_canvas.Bounds.Width)-(maxX-minX)*_scale)/2-minX*_scale;_oy=(340-(maxY-minY)*_scale)/2-minY*_scale;
        foreach(var screen in _windows.Screens)MakeCard(screen,screen.X,screen.Y,false);
        _remoteCard=MakeCard(m,_remoteX,_remoteY,true);
        _remoteCard.PointerPressed+=(_,e)=>{if(_busy||!e.GetCurrentPoint(_remoteCard).Properties.IsLeftButtonPressed)return;_dragging=true;_dragStart=e.GetPosition(_canvas);_startX=_remoteX;_startY=_remoteY;e.Pointer.Capture(_remoteCard);e.Handled=true;};
        _remoteCard.PointerMoved+=(_,e)=>{if(!_dragging)return;var p=e.GetPosition(_canvas);_remoteX=_startX+(p.X-_dragStart.X)/_scale;_remoteY=_startY+(p.Y-_dragStart.Y)/_scale;Canvas.SetLeft(_remoteCard,_ox+_remoteX*_scale);Canvas.SetTop(_remoteCard,_oy+_remoteY*_scale);};
        _remoteCard.PointerReleased+=(_,e)=>{if(!_dragging)return;_dragging=false;e.Pointer.Capture(null);Snap(m);};
        _remoteCard.PointerCaptureLost+=(_,_)=>{if(_dragging){_dragging=false;Snap(m);}};
        try{
            var portal=ScreenPortal.Create(_draft,_windows.Screens,_mac.Screens);
            double x1,y1,x2,y2;
            if(portal.Horizontal){x1=portal.Start;x2=portal.End;y1=y2=_draft.Edge=="bottom"?w.Y+w.Height:w.Y;}
            else{y1=portal.Start;y2=portal.End;x1=x2=_draft.Edge=="right"?w.X+w.Width:w.X;}
            var line=new Line{StartPoint=new(_ox+x1*_scale,_oy+y1*_scale),EndPoint=new(_ox+x2*_scale,_oy+y2*_scale),Stroke=Brush.Parse("#159A72"),StrokeThickness=5,IsHitTestVisible=false};_canvas.Children.Add(line);
            _seam.Text=$"绿色接缝：{w.Name} → {m.Name} · {Edges[Array.IndexOf(EdgeCodes,_draft.Edge)]} · 范围 {_draft.CoveragePercent}% · {_draft.SwitchDelayMs} 毫秒";
            _save.IsEnabled=!_busy;
        }catch(Exception e){_seam.Text=e.Message;_save.IsEnabled=false;}
    }
    private Border MakeCard(ScreenInfo screen,double x,double y,bool remote)
    {
        var label=new StackPanel{VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Spacing=4};
        label.Children.Add(new TextBlock{Text=screen.Name,FontWeight=FontWeight.SemiBold,FontSize=14,Foreground=remote?Brushes.White:Brush.Parse("#244364"),HorizontalAlignment=HorizontalAlignment.Center});
        label.Children.Add(new TextBlock{Text=$"{screen.Width} × {screen.Height}",FontSize=11,Foreground=remote?Brushes.White:Brush.Parse("#596E86"),HorizontalAlignment=HorizontalAlignment.Center});
        if(remote)label.Children.Add(new TextBlock{Text="拖动调整位置",FontSize=11,Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Center});
        var card=new Border{Width=Math.Max(95,screen.Width*_scale),Height=Math.Max(65,screen.Height*_scale),Background=Brush.Parse(remote?"#3567AA":"#FFFFFF"),BorderBrush=Brush.Parse("#A9BDD7"),BorderThickness=new Thickness(1.5),CornerRadius=new CornerRadius(8),Child=label};
        Canvas.SetLeft(card,_ox+x*_scale);Canvas.SetTop(card,_oy+y*_scale);_canvas.Children.Add(card);return card;
    }
    private void Snap(ScreenInfo remote)
    {
        if(_windows is null||_draft is null)return;
        var candidates=new List<(ScreenLayout Plan,double Distance)>();
        foreach(var w in _windows.Screens)foreach(var edge in EdgeCodes){
            int offset=edge is "top" or "bottom"?(int)Math.Clamp(_remoteX-w.X,-remote.Width+64,w.Width-64):(int)Math.Clamp(_remoteY-w.Y,-remote.Height+64,w.Height-64);
            var plan=_draft with{WindowsScreenId=w.Id,Edge=edge,Offset=offset};
            try{_=ScreenPortal.Create(plan,_windows.Screens,_mac!.Screens);}catch{continue;}
            var p=RemotePosition(w,remote,plan);candidates.Add((plan,Math.Pow(p.X-_remoteX,2)+Math.Pow(p.Y-_remoteY,2)));
        }
        if(candidates.Count>0){_draft=candidates.MinBy(c=>c.Distance).Plan;SyncControls();_status.Text="排列尚未保存。确认绿色接缝后，点击“保存并应用”。";}Draw();
    }
}
