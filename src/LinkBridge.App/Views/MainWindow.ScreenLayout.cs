using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LinkBridge.App.ViewModels;
using LinkBridge.Core;
using LinkBridge.Core.DisplayLayout;
using LinkBridge.Input;

namespace LinkBridge.App.Views;

public partial class MainWindow
{
    private LayoutTransport? _screenLayoutServer;
    private ScreenLayoutWindow? _screenLayoutEditor;
    private readonly DispatcherTimer _screenLayoutTimer = new() {Interval=TimeSpan.FromSeconds(5)};
    private bool _screenLayoutBusy, _screenLayoutNeedsReview;
    private readonly SemaphoreSlim _screenApplyGate = new(1,1);
    private string _nativeScreenStamp = "";
    private void ShowScreenLayoutEditor()
    {
        if (_screenLayoutEditor is { } existing) { existing.Activate(); return; }
        var editor = new ScreenLayoutWindow(ReadScreenLayoutAsync, ApplyScreenLayoutAsync, IdentifyBothScreensAsync, RestoreScreenLayoutAsync,
            () => { ShowWorkspaceEditor(); });
        _screenLayoutEditor = editor; editor.Closed += (_,_) => _screenLayoutEditor = null;
        editor.Show(this);
    }
    private void StartScreenLayoutService(MainViewModel vm)
    {
        if (vm.Settings.InputPairingSecret.Length != 32 || _screenLayoutServer is not null) return;
        try {
            _screenLayoutServer = new(vm.Settings.InputPairingSecret,
                address => _layoutPeerCheck?.Invoke(address) == true,
                async (request,ct) => await Dispatcher.UIThread.InvokeAsync(() => HandleScreenLayoutAsync(request,ct)));
            _layoutPeerCheck = vm.IsLayoutPeer;
            _screenLayoutServer.Start();
            _nativeScreenStamp = System.Text.Json.JsonSerializer.Serialize(NativeScreens.Read());
            _screenLayoutTimer.Tick += async (_,_) => {
                if (_screenLayoutBusy || DataContext is not MainViewModel model || model.Workspace.Enabled) return;
                _screenLayoutBusy = true;
                try {
                    var stamp = System.Text.Json.JsonSerializer.Serialize(NativeScreens.Read());
                    if (stamp != _nativeScreenStamp) {
                        _nativeScreenStamp = stamp; _screenLayoutNeedsReview = true; model.PauseForScreenChange();
                        if (OperatingSystem.IsMacOS())
                            await PeerLayoutRequestAsync(new("screens-changed"));
                    }
                    if (OperatingSystem.IsWindows() && !_screenLayoutNeedsReview && model.ActiveScreenPortal is null) {
                        var state = await ReadScreenLayoutAsync();
                        var plan = state.Windows.Layout ?? MigrateScreenLayout(state.Windows.Screens,state.Mac.Screens,model.Settings);
                        if (plan is not null) await ApplyScreenLayoutCoreAsync(plan,state.Windows.Screens,state.Mac.Screens,save:ScreenLayoutStore.Load() is null);
                    }
                } catch { /* Offline peers are retried without discarding the saved layout. */ }
                finally { _screenLayoutBusy=false; }
            };
            _screenLayoutTimer.Start();
        } catch (Exception ex) { vm.ReportWindowStartupError(new IOException("屏幕布局服务未启动："+ex.Message)); }
    }
    private Func<string,bool>? _layoutPeerCheck;
    private LayoutSnapshot LocalLayoutSnapshot()
    {
        var vm = (MainViewModel)DataContext!;
        return new(OperatingSystem.IsWindows()?"Windows":"macOS",vm.Settings.DisplayName,NativeScreens.Read(),
            ScreenLayoutStore.Load(),_screenLayoutNeedsReview?"屏幕发生变化，请重新确认布局":"");
    }
    private async Task<LayoutResponse> PeerLayoutRequestAsync(LayoutRequest request)
    {
        var vm=(MainViewModel)DataContext!; var address=vm.LayoutPeerAddress;
        if(string.IsNullOrWhiteSpace(address))throw new InvalidOperationException("请先在两台电脑上打开联桥并完成配对，再刷新屏幕。");
        return await LayoutTransport.RequestAsync(address,vm.Settings.InputPairingSecret,request);
    }
    private async Task<LayoutResponse> HandleScreenLayoutAsync(LayoutRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch(request.Action) {
            case "get": return new(true,LocalLayoutSnapshot());
            case "identify": IdentifyLocalScreens(); return new(true,LocalLayoutSnapshot());
            case "screens-changed": _screenLayoutNeedsReview=true; ((MainViewModel)DataContext!).PauseForScreenChange(); return new(true,LocalLayoutSnapshot());
            case "apply" when OperatingSystem.IsWindows() && request.Layout is not null:
                await ApplyScreenLayoutAsync(request.Layout); return new(true,LocalLayoutSnapshot());
            case "restore" when OperatingSystem.IsWindows():
                await RestoreScreenLayoutAsync(); return new(true,LocalLayoutSnapshot());
            default: throw new InvalidDataException("此电脑不支持该布局操作，请在两端安装相同版本。");
        }
    }
    private async Task<(LayoutSnapshot Windows,LayoutSnapshot Mac)> ReadScreenLayoutAsync()
    {
        var local=LocalLayoutSnapshot(); var remote=(await PeerLayoutRequestAsync(new("get"))).Snapshot
            ?? throw new InvalidDataException("没有收到另一台电脑的屏幕信息。");
        if(local.Platform==remote.Platform)throw new InvalidOperationException("当前双机布局支持 Windows 控制 Mac。更多电脑请使用高级多机工作区。");
        var w=OperatingSystem.IsWindows()?local:remote;var m=OperatingSystem.IsMacOS()?local:remote;
        if(w.Screens.Length==0||m.Screens.Length==0)throw new InvalidOperationException("没有读取到屏幕，请检查显示器连接后刷新。");
        if(w.Layout is null)w=w with {Layout=MigrateScreenLayout(w.Screens,m.Screens,((MainViewModel)DataContext!).Settings)};
        return (w,m);
    }
    private static ScreenLayout? MigrateScreenLayout(ScreenInfo[] windows,ScreenInfo[] mac,AppSettings settings)
    {
        if(windows.Length==0||mac.Length==0)return null;
        var local=(settings.InputScreenEdge=="bottom" ? windows.FirstOrDefault(s=>s.X==settings.InputScreenX&&s.Y==settings.InputScreenY) : null)
            ?? windows.OrderByDescending(s=>s.X+s.Width).First();
        var remote=mac.FirstOrDefault(s=>s.Primary)??mac[0];
        return new(){WindowsScreenId=local.Id,MacScreenId=remote.Id,Edge=settings.InputScreenEdge=="bottom"?"bottom":"right",
            Offset=settings.InputScreenEdge=="bottom"?(local.Width-remote.Width)/2:(local.Height-remote.Height)/2};
    }
    private async Task ApplyScreenLayoutAsync(ScreenLayout layout)
    {
        if(OperatingSystem.IsMacOS()) {await PeerLayoutRequestAsync(new("apply",layout));ScreenLayoutStore.Save(layout);return;}
        var remote=(await PeerLayoutRequestAsync(new("get"))).Snapshot??throw new IOException("Mac 未返回屏幕信息。");
        await ApplyScreenLayoutCoreAsync(layout,NativeScreens.Read(),remote.Screens,true);
    }
    private async Task ApplyScreenLayoutCoreAsync(ScreenLayout layout,ScreenInfo[] windows,ScreenInfo[] mac,bool save)
    {
        if(!save && ((!string.IsNullOrEmpty(layout.WindowsDisplaySignature)&&layout.WindowsDisplaySignature!=ScreenLayoutStore.Signature(windows)) ||
            (!string.IsNullOrEmpty(layout.MacDisplaySignature)&&layout.MacDisplaySignature!=ScreenLayoutStore.Signature(mac)))) {
            _screenLayoutNeedsReview=true;((MainViewModel)DataContext!).PauseForScreenChange();
            throw new InvalidOperationException("屏幕与上次保存时不同，请打开屏幕布局重新确认。");
        }
        if(save)layout=layout with{WindowsDisplaySignature=ScreenLayoutStore.Signature(windows),MacDisplaySignature=ScreenLayoutStore.Signature(mac)};
        var portal=ScreenPortal.Create(layout,windows,mac); // Validate before stopping anything.
        if(!await _screenApplyGate.WaitAsync(0))throw new InvalidOperationException("正在应用另一套布局，请稍候再试。");
        var old=(MainViewModel)DataContext!;
        try {
            if(old.Workspace.Enabled)throw new InvalidOperationException("当前启用了高级多机模式，请先在高级工作区关闭通用多机模式，再设置双机布局。");
            if (save) ScreenLayoutStore.Save(layout);
            await old.ShutdownAsync();
            var replacement=new MainViewModel {ActiveScreenPortal=portal};
            DataContext=replacement;_layoutPeerCheck=replacement.IsLayoutPeer;
            await replacement.InitializeAsync();
            _screenLayoutNeedsReview=false;
        } finally {_screenApplyGate.Release();}
    }
    private async Task RestoreScreenLayoutAsync()
    {
        if(OperatingSystem.IsMacOS()){await PeerLayoutRequestAsync(new("restore"));return;}
        var previous=ScreenLayoutStore.Load(previous:true)??throw new InvalidOperationException("还没有上一套布局。请先保存一次新布局。");
        await ApplyScreenLayoutAsync(previous);
    }
    private async Task IdentifyBothScreensAsync()
    {
        IdentifyLocalScreens();await PeerLayoutRequestAsync(new("identify"));
    }
    private void IdentifyLocalScreens()
    {
        var screens=NativeScreens.Read();
        for(int i=0;i<screens.Length;i++){
            var screen=screens[i];
            var badge=new Window {Width=280,Height=150,CanResize=false,ShowInTaskbar=false,Topmost=true,
                WindowDecorations=Avalonia.Controls.WindowDecorations.None,Background=Brush.Parse("#173D70"),
                Content=new StackPanel {VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Spacing=8,
                    Children={new TextBlock{Text=screen.Name,FontSize=32,Foreground=Brushes.White},
                        new TextBlock{Text=$"{screen.Width} × {screen.Height}",Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Center}}}};
            badge.Position=new PixelPoint(screen.X+80,screen.Y+80);badge.Show();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2.5)};timer.Tick+=(_,_)=>{timer.Stop();badge.Close();};timer.Start();
        }
    }
}
