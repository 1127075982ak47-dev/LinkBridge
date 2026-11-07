using LinkBridge.App.ViewModels;
using LinkBridge.Core.Workspace;
using Avalonia.Threading;

namespace LinkBridge.App.Views;
public partial class MainWindow
{
    private WorkspaceWindow? _workspaceEditor;
    private void ShowWorkspaceEditor()
    {
        if(_workspaceEditor is {} existing){existing.Activate();return;}
        if(DataContext is not MainViewModel model)return;
        var window=new WorkspaceWindow(model,async profile=>
        {
            WorkspaceStore.Save(profile);
            if(DataContext is MainViewModel old)await old.ShutdownAsync();
            var replacement=new MainViewModel();DataContext=replacement;
            await replacement.InitializeAsync();
            return replacement;
        });
        _workspaceEditor=window;window.Closed+=(_,_)=>_workspaceEditor=null;
        Closed+=(_,_)=>window.Close();
        window.Show(this);
    }
}
