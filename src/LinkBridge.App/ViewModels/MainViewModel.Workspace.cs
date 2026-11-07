using LinkBridge.Core.Workspace;
using Avalonia.Threading;

namespace LinkBridge.App.ViewModels;
public partial class MainViewModel
{
    public WorkspaceProfile Workspace { get; } = WorkspaceStore.Load();
    public DeskflowRuntime WorkspaceRuntime { get; } = new();
    public LinkBridge.App.Services.WorkspaceWindowService? WorkspaceWindows { get; private set; }
    private bool _workspaceInitialized;
    public string LocalComputerLabel => Workspace.Enabled ? Workspace.Local.Name : "Windows";
    public string RemoteComputerLabel => Workspace.Enabled ? $"{Math.Max(0,Workspace.Devices.Count-1)} 台设备" : "Mac";
    public async Task StartWorkspaceAsync()
    {
        if (!_workspaceInitialized)
        {
            _workspaceInitialized=true;
            WorkspaceRuntime.Diagnostic += line=>AppendLog("[通用键鼠] "+line);
            WorkspaceRuntime.StatusChanged += s=>Dispatcher.UIThread.Post(()=>
            {InputStatusText=s;ConnectionActionStatus=s;IsPeerConnected=WorkspaceRuntime.Connected.Count>0;});
            WorkspaceRuntime.ConnectionsChanged += _=>Dispatcher.UIThread.Post(()=>IsPeerConnected=WorkspaceRuntime.Connected.Count>0);
        }
        try
        {
            if(WorkspaceWindows is null)
            {
                WorkspaceWindows=new(Workspace);WorkspaceWindows.StatusChanged+=s=>AppendLog("[窗口接力] "+s);
                WorkspaceWindows.Start();
            }
            if(Workspace.Devices.Count<2){InputStatusText="打开设备与布局，添加并信任另一台电脑";return;}
            await WorkspaceRuntime.StartAsync(Workspace,_lifetimeCts.Token);
        }
        catch(Exception e){InputStatusText="通用连接未启动 · "+e.Message;AppendLog("[工作区] "+e.Message);}
    }
}
