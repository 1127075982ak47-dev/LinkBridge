using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LinkBridge.App.Services;
using LinkBridge.App.ViewModels;
using LinkBridge.Core.Workspace;

namespace LinkBridge.App.Views;
public sealed class WorkspaceWindowsWindow : Window
{
    public WorkspaceWindowsWindow(MainViewModel model)
    {
        Title="LinkBridge · 窗口跨机";Width=720;Height=460;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var p=new StackPanel{Margin=new Thickness(24),Spacing=16};Content=p;
        p.Children.Add(new TextBlock{Text="同一个窗口，两个独立桌面",FontSize=24,FontWeight=FontWeight.SemiBold});
        p.Children.Add(new TextBlock{Text="选择源窗口和相邻设备。接管后源程序仍在本机运行，两端只显示各自桌面内的窗口片段；拖动标题区域移动，Esc 或归还按钮恢复原窗口。",TextWrapping=TextWrapping.Wrap});
        var targets=new ComboBox{ItemsSource=WindowsWindowCapture.Enumerate(),HorizontalAlignment=HorizontalAlignment.Stretch,PlaceholderText="选择本机应用窗口"};
        var peers=new ComboBox{ItemsSource=model.Workspace.Devices.Where(x=>x.Id!=model.Workspace.LocalDeviceId).ToArray(),PlaceholderText="选择相邻设备"};
        p.Children.Add(targets);p.Children.Add(peers);
        var actions=new WrapPanel();var send=new Button{Content="接管并跨机显示",Margin=new Thickness(0,0,12,0)};var back=new Button{Content="归还窗口"};var refresh=new Button{Content="刷新窗口列表",Margin=new Thickness(12,0,0,0)};
        actions.Children.Add(send);actions.Children.Add(back);actions.Children.Add(refresh);p.Children.Add(actions);
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap};p.Children.Add(status);
        p.Children.Add(new TextBlock{Text="当前为 Windows 源端实验功能。需要实际应用支持离屏绘制和窗口消息输入；受保护、提权或部分 GPU 应用可能不可用，失败会归还原窗口。Mac/Linux 源端没有通过验收，不会假装已支持。",TextWrapping=TextWrapping.Wrap,Foreground=Brush.Parse("#74552E"),FontSize=12});
        refresh.Click+=(_,_)=>targets.ItemsSource=WindowsWindowCapture.Enumerate();
        back.Click+=(_,_)=>model.WorkspaceWindows?.ReturnAll();
        send.Click+=async(_,_)=>
        {
            send.IsEnabled=false;
            try
            {
                if(targets.SelectedItem is not WindowCaptureTarget window||peers.SelectedItem is not WorkspaceDevice peer)throw new InvalidOperationException("先选择窗口和目标设备。");
                if(model.WorkspaceWindows is null)throw new InvalidOperationException("请先保存并启用通用多机模式。");
                await model.WorkspaceWindows.SendAsync(window,peer);
            }catch(Exception e){status.Text=e.Message;}finally{send.IsEnabled=true;}
        };
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};timer.Tick+=(_,_)=>{if(model.WorkspaceWindows is {} service)status.Text=service.Status;};Opened+=(_,_)=>timer.Start();Closed+=(_,_)=>timer.Stop();
    }
}
