using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LinkBridge.Core.Workspace;

/// <summary>Owns only the Deskflow child it launches; separate executable and upstream license.</summary>
public sealed class DeskflowRuntime : IAsyncDisposable
{
    private Process? _process;
    private readonly HashSet<string> _connected = [];
    private readonly object _gate=new();
    private WorkspaceProfile? _profile;
    private Task? _stdout,_stderr;
    public event Action<string>? StatusChanged;
    public event Action<string>? Diagnostic;
    public event Action<IReadOnlyList<string>>? ConnectionsChanged;
    public bool Running => _process is {HasExited:false};
    public IReadOnlyList<string> Connected { get {lock(_gate)return _connected.ToArray();} }
    public string Status {get;private set;}="尚未启动";
    private void State(string s){Status=s;StatusChanged?.Invoke(s);}
    public static string? Locate(string configured="")
    {
        if(!string.IsNullOrWhiteSpace(configured))return File.Exists(configured)?Path.GetFullPath(configured):null;
        var exe=OperatingSystem.IsWindows()?"deskflow-core.exe":"deskflow-core";
        var paths=new List<string> {Path.Combine(AppContext.BaseDirectory,"Engines","Deskflow",exe),
            Path.Combine(AppContext.BaseDirectory,"..","Frameworks","Deskflow.app","Contents","MacOS",exe)};
        if(OperatingSystem.IsMacOS()){paths.Add("/Applications/Deskflow.app/Contents/MacOS/deskflow-core");paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Applications/Deskflow.app/Contents/MacOS/deskflow-core"));}
        if(OperatingSystem.IsWindows())paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Deskflow",exe));
        paths.AddRange((Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>Path.Combine(x,exe)));
        return paths.FirstOrDefault(File.Exists);
    }
    public static string Prepare(WorkspaceProfile p,string? directory=null)
    {
        WorkspaceTopology.Validate(p);
        foreach(var d in p.Devices) if(d.Fingerprint.Length!=64)throw new InvalidOperationException("请导入并核对每台设备的身份指纹。");
        if(!p.IsController)WorkspaceTopology.ValidateHost(p.Controller.Address);
        var root=directory??Path.Combine(WorkspaceStore.Root,"Engine");WorkspaceStore.PrivateDirectory(root);
        var tls=Path.Combine(root,"tls");WorkspaceStore.PrivateDirectory(tls);
        // Certificate keys remain in this device's private workspace; exported layouts contain only public fingerprints.
        using var identity=WorkspaceStore.Identity();
        if(!string.Equals(identity.GetCertHashString(HashAlgorithmName.SHA256),p.Local.Fingerprint,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("本机配置与本机身份不一致，请重新导入设备资料，不能使用他人的私钥。");
        var certificate=Path.Combine(WorkspaceStore.Root,"identity.pem").Replace('\\','/');
        var trusted=p.Devices.Where(x=>x.Id!=p.LocalDeviceId).Select(x=>"v2:sha256:"+x.Fingerprint.ToLowerInvariant());
        WorkspaceStore.WritePrivate(Path.Combine(tls,"trusted-servers"),string.Join('\n',trusted)+"\n");
        WorkspaceStore.WritePrivate(Path.Combine(tls,"trusted-clients"),string.Join('\n',trusted)+"\n");
        var config=Path.Combine(root,"screens.conf");WorkspaceStore.WritePrivate(config,WorkspaceTopology.EngineConfig(p));
        var host=p.Controller.Address.Contains(':')?"["+p.Controller.Address.Trim('[',']')+"]":p.Controller.Address;
        var ini=$"[core]\ncomputerName={p.Local.EngineName}\nport={p.InputPort}\nprocessMode=1\npreventSleep=false\nuseHooks=true\n"+
            $"[client]\nremoteHost={host}\n[security]\ntlsEnabled=true\ncheckPeerFingerprints=true\ncertificate={certificate}\n"+
            $"[server]\nexternalConfig=true\nexternalConfigFile={config.Replace('\\','/')}\nenableClipboard={p.ShareClipboard.ToString().ToLowerInvariant()}\n"+
            "[log]\nlevel=DEBUG\ntoFile=false\n[gui]\nenableUpdateCheck=false\nstartCoreWithGui=false\n";
        foreach(var d in p.Devices)ini+=$"[computer_{d.EngineName}]\nname={d.EngineName}\n";
        var settings=Path.Combine(root,"deskflow.conf");WorkspaceStore.WritePrivate(settings,ini);return settings;
    }
    public async Task StartAsync(WorkspaceProfile profile,CancellationToken ct=default)
    {
        await StopAsync();_profile=profile;
        var executable=Locate(profile.EnginePath)??throw new FileNotFoundException("尚未安装通用键鼠组件 Deskflow，请在设备与布局页面选择组件路径。");
        var settings=Prepare(profile);
        var start=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(executable)!};
        foreach(var arg in new[]{profile.IsController?"server":"client","--new-instance","--settings",settings}) start.ArgumentList.Add(arg);
        start.Environment["XDG_CONFIG_HOME"]=Path.Combine(WorkspaceStore.Root,"EngineDefaults");
        start.Environment["XDG_STATE_HOME"]=Path.Combine(WorkspaceStore.Root,"EngineState");
        _process=new Process{StartInfo=start,EnableRaisingEvents=true};
        var own=_process;
        own.Exited+=(_,_)=>{lock(_gate)_connected.Clear();ConnectionsChanged?.Invoke([]);if(ReferenceEquals(_process,own))State("通用键鼠已停止，请查看连接诊断");};
        if(!own.Start())throw new IOException("无法启动通用键鼠组件。");
        _stdout=Read(own.StandardOutput,own);_stderr=Read(own.StandardError,own);
        State(profile.IsController?"监听中 · 等待已信任设备连接":"正在连接键鼠来源…");
        await Task.Delay(400,ct);
        if(own.HasExited)throw new IOException("键鼠组件未能保持运行，请查看诊断和系统权限。");
    }
    private async Task Read(StreamReader reader,Process owner)
    {
        try {while(await reader.ReadLineAsync() is {} line)
        {
            if(!ReferenceEquals(_process,owner))continue;
            // No key logging level is enabled. Debug messages concern lifecycle, layout and connection only.
            Diagnostic?.Invoke(line);
            if(line.Contains("connected to server",StringComparison.OrdinalIgnoreCase))
            {lock(_gate)_connected.Add(_profile!.ControllerId);State("已加密连接 · 键鼠跟随当前屏幕");ConnectionsChanged?.Invoke(Connected);}
            if(_profile is {} p)foreach(var d in p.Devices)
            {
                if(line.Contains(d.EngineName,StringComparison.Ordinal) && line.Contains("has connected",StringComparison.OrdinalIgnoreCase))
                {lock(_gate)_connected.Add(d.Id);State($"已连接 {Connected.Count} 台设备 · Ctrl + Alt + M 返回");ConnectionsChanged?.Invoke(Connected);}
                if(line.Contains(d.EngineName,StringComparison.Ordinal) && (line.Contains("disconnected",StringComparison.OrdinalIgnoreCase)||line.Contains("disconnecting client",StringComparison.OrdinalIgnoreCase)))
                {lock(_gate)_connected.Remove(d.Id);ConnectionsChanged?.Invoke(Connected);State($"在线 {Connected.Count} 台 · 断开的设备会重连");}
            }
            if(line.Contains("failed to connect",StringComparison.OrdinalIgnoreCase)||line.Contains("server disconnected",StringComparison.OrdinalIgnoreCase))
            {lock(_gate)_connected.Clear();ConnectionsChanged?.Invoke([]);State("连接中断 · 正在重试，请检查地址与网络");}
            if(line.Contains("fingerprint",StringComparison.OrdinalIgnoreCase) && (line.Contains("not trusted",StringComparison.OrdinalIgnoreCase)||line.Contains("not in",StringComparison.OrdinalIgnoreCase))) State("身份未信任 · 请核对双方的设备指纹");
            if(line.Contains("accessibility",StringComparison.OrdinalIgnoreCase)||line.Contains("permission denied",StringComparison.OrdinalIgnoreCase))State("等待系统控制权限 · 请在系统设置中允许当前键鼠组件");
        }} catch(Exception e)when(e is IOException or ObjectDisposedException){ }
    }
    public async Task StopAsync()
    {
        var p=_process;_process=null;
        if(p is not null)
        {
            try {if(!p.HasExited)
            {
                if(!OperatingSystem.IsWindows()) {try{using var signal=Process.Start(new ProcessStartInfo("/bin/kill"){ArgumentList={"-TERM",p.Id.ToString()},UseShellExecute=false});if(signal is not null)await signal.WaitForExitAsync();}catch{} }
                else p.CloseMainWindow();
                try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(1200));}catch(TimeoutException){p.Kill(true);await p.WaitForExitAsync();}
            }}finally{p.Dispose();}
        }
        lock(_gate)_connected.Clear();ConnectionsChanged?.Invoke([]);State("已停止 · 本机键鼠可独立使用");
    }
    public static async Task<string> DiagnoseAsync(string host,int port,CancellationToken ct=default)
    {
        WorkspaceTopology.ValidateHost(host);var clock=Stopwatch.StartNew();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try {var addresses=await Dns.GetHostAddressesAsync(host.Trim('[',']'),timeout.Token);
            using var client=new TcpClient();await client.ConnectAsync(host.Trim('[',']'),port,timeout.Token);
            return $"TCP 可达 · {clock.ElapsedMilliseconds} ms · 解析 {addresses.Length} 个地址。下一步仍需确认加密身份与系统权限。";
        }catch(OperationCanceledException){return "连接超时：确认主机已启动、地址正确、端口可达。跨网络可使用已有 Tailscale/ZeroTier/WireGuard 组网地址；CGNAT 下仅填公网 IP 通常不能直连。";}
        catch(SocketException e){return $"网络不可达（{e.SocketErrorCode}）：检查 DNS、主机地址、目标端口、防火墙和 VPN 路由。";}
    }
    public async ValueTask DisposeAsync()=>await StopAsync();
}
