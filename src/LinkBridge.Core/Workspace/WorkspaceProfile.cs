using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinkBridge.Core.Workspace;

public sealed class WorkspaceDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = Environment.MachineName;
    public string Platform { get; set; } = PlatformInfo.DisplayName;
    public string Address { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public string EngineName => "lb_" + Id;
    public override string ToString() => Name + " · " + Platform;
}
public sealed class WorkspaceProfile
{
    public int SchemaVersion { get; set; } = 1;
    public bool Enabled { get; set; }
    public string LocalDeviceId { get; set; } = "";
    public string ControllerId { get; set; } = "";
    public int InputPort { get; set; } = 45690;
    public int SharingPort { get; set; } = 45692;
    public int SwitchDelayMs { get; set; } = 150;
    public bool ShareClipboard { get; set; } = true;
    public bool AcceptWindows { get; set; } = true;
    public string EnginePath { get; set; } = "";
    public List<WorkspaceDevice> Devices { get; set; } = [];
    public WorkspaceDevice Local => Devices.Single(d => d.Id == LocalDeviceId);
    public WorkspaceDevice Controller => Devices.Single(d => d.Id == ControllerId);
    public bool IsController => LocalDeviceId == ControllerId;
}
public sealed record WorkspaceLink(string SourceId, string TargetId, string Direction,
    double Start, double End, double TargetStart, double TargetEnd);

public static class WorkspaceTopology
{
    public static void Validate(WorkspaceProfile p, bool requirePeers = true)
    {
        if (p.SchemaVersion != 1 || p.Devices is null || p.Devices.Count is < 1 or > 4) throw new InvalidDataException("工作区支持 1 至 4 台设备。");
        if (requirePeers && p.Devices.Count < 2) throw new InvalidDataException("请先添加另一台设备。");
        if (p.InputPort is < 1024 or > 65535 || p.SharingPort is < 1024 or > 65535 || p.InputPort == p.SharingPort)
            throw new InvalidDataException("端口应为 1024 至 65535，且两个服务不能使用同一端口。");
        if (p.SwitchDelayMs is < 0 or > 2000) throw new InvalidDataException("边缘停留时间应为 0 至 2000 毫秒。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in p.Devices)
        {
            if (d.Id is null || !Regex.IsMatch(d.Id, "^[a-f0-9]{32}$") || !ids.Add(d.Id)) throw new InvalidDataException("设备身份重复或无效。");
            if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 80 || d.Name.Any(char.IsControl)) throw new InvalidDataException("设备名称无效。");
            if (d.Width is < 320 or > 32768 || d.Height is < 200 or > 32768 || Math.Abs((long)d.X) > 100000 || Math.Abs((long)d.Y) > 100000)
                throw new InvalidDataException("显示范围或位置超出支持范围。");
            if (d.Fingerprint.Length != 0 && !Regex.IsMatch(d.Fingerprint, "^[A-Fa-f0-9]{64}$")) throw new InvalidDataException("设备身份指纹应为 64 位十六进制。");
            if (d.Address.Length != 0) ValidateHost(d.Address);
        }
        if (!ids.Contains(p.LocalDeviceId) || !ids.Contains(p.ControllerId)) throw new InvalidDataException("本机或键鼠来源不在当前工作区。");
        for (var a = 0; a < p.Devices.Count; a++) for (var b = a + 1; b < p.Devices.Count; b++)
        {
            var x = p.Devices[a]; var y = p.Devices[b];
            if (Math.Max(x.X, y.X) < Math.Min(x.X + x.Width, y.X + y.Width) && Math.Max(x.Y, y.Y) < Math.Min(x.Y + x.Height, y.Y + y.Height))
                throw new InvalidDataException($"{x.Name} 与 {y.Name} 重叠，请把卡片边缘相接。");
        }
        if (requirePeers)
        {
            var seen = new HashSet<string> { p.ControllerId }; var links = Links(p);
            for (var i = 0; i < p.Devices.Count; i++) foreach (var l in links) if (seen.Contains(l.SourceId)) seen.Add(l.TargetId);
            if (seen.Count != p.Devices.Count) throw new InvalidDataException("有设备没有接缝连接，请把设备边缘相接或使用排列预设。");
        }
    }
    public static void ValidateHost(string host)
    {
        if (host.Length is < 1 or > 253 || host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', '"', '\r', '\n']) >= 0 ||
            (!IPAddress.TryParse(host.Trim('[', ']'), out _) && Uri.CheckHostName(host) != UriHostNameType.Dns))
            throw new InvalidDataException("请填写 IPv4、IPv6 或主机名，不要包含协议、端口和路径。");
    }
    public static List<WorkspaceLink> Links(WorkspaceProfile p)
    {
        List<WorkspaceLink> result = [];
        foreach (var a in p.Devices) foreach (var b in p.Devices.Where(b => a.Id != b.Id))
        {
            void Add(string dir, int start, int end, int ao, int al, int bo, int bl)
            {
                if (end <= start) return;
                result.Add(new(a.Id, b.Id, dir, (start-ao)*100d/al, (end-ao)*100d/al, (start-bo)*100d/bl, (end-bo)*100d/bl));
            }
            if (a.X + a.Width == b.X) Add("right", Math.Max(a.Y,b.Y), Math.Min(a.Y+a.Height,b.Y+b.Height),a.Y,a.Height,b.Y,b.Height);
            if (b.X + b.Width == a.X) Add("left", Math.Max(a.Y,b.Y), Math.Min(a.Y+a.Height,b.Y+b.Height),a.Y,a.Height,b.Y,b.Height);
            if (a.Y + a.Height == b.Y) Add("down", Math.Max(a.X,b.X), Math.Min(a.X+a.Width,b.X+b.Width),a.X,a.Width,b.X,b.Width);
            if (b.Y + b.Height == a.Y) Add("up", Math.Max(a.X,b.X), Math.Min(a.X+a.Width,b.X+b.Width),a.X,a.Width,b.X,b.Width);
        }
        return result;
    }
    public static string EngineConfig(WorkspaceProfile p)
    {
        Validate(p);
        var s = new StringBuilder("section: links\n");
        var links = Links(p);
        // Deskflow text-config accepts integer percentages. Reject ambiguous ranges after rounding.
        foreach (var d in p.Devices)
        {
            s.AppendLine($"  {d.EngineName}:");
            foreach (var l in links.Where(l => l.SourceId == d.Id))
            {
                var start=(int)Math.Round(l.Start);var end=(int)Math.Round(l.End);
                var ts=(int)Math.Round(l.TargetStart);var te=(int)Math.Round(l.TargetEnd);
                if (start>=end || ts>=te) throw new InvalidDataException("接缝太小，请扩大接触范围。");
                s.AppendLine($"    {l.Direction}({start},{end}) = {p.Devices.Single(x=>x.Id==l.TargetId).EngineName}({ts},{te})");
            }
        }
        s.AppendLine("end\nsection: options");
        s.AppendLine($"  switchDelay = {p.SwitchDelayMs}");
        s.AppendLine("  heartbeat = 2000\n  win32KeepForeground = false");
        s.AppendLine($"  clipboardSharing = {p.ShareClipboard.ToString().ToLowerInvariant()}");
        s.AppendLine($"  keystroke(Control+Alt+M) = switchToScreen({p.Controller.EngineName})");
        s.AppendLine("end");
        return s.ToString();
    }
}

public static class WorkspaceStore
{
    public static string Root => Environment.GetEnvironmentVariable("LINKBRIDGE_WORKSPACE_HOME") is { Length: > 0 } test
        ? Path.GetFullPath(test) : Path.Combine(Path.GetDirectoryName(SettingsStore.FilePath)!, "Workspace");
    public static string ProfilePath => Path.Combine(Root, "workspace.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static void PrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public static WorkspaceProfile Load()
    {
        if (File.Exists(ProfilePath))
        {
            var p = JsonSerializer.Deserialize<WorkspaceProfile>(File.ReadAllText(ProfilePath)) ?? throw new InvalidDataException("工作区配置为空。");
            WorkspaceTopology.Validate(p, false); return p;
        }
        var cert=Identity();
        var node=new WorkspaceDevice {Fingerprint=cert.GetCertHashString(HashAlgorithmName.SHA256)};
        var result=new WorkspaceProfile {LocalDeviceId=node.Id,ControllerId=node.Id,Devices=[node]};
        Save(result);return result;
    }
    public static void Save(WorkspaceProfile p)
    {
        WorkspaceTopology.Validate(p,false);PrivateDirectory(Root);
        if (File.Exists(ProfilePath)) File.Copy(ProfilePath,ProfilePath+".previous",true);
        WritePrivate(ProfilePath,JsonSerializer.Serialize(p,Json));
    }
    public static void WritePrivate(string path,string text)
    {
        PrivateDirectory(Path.GetDirectoryName(path)!);
        var tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {File.WriteAllText(tmp,text,new UTF8Encoding(false)); if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp,UnixFileMode.UserRead|UnixFileMode.UserWrite);File.Move(tmp,path,true);}
        finally {if(File.Exists(tmp)) File.Delete(tmp);}
    }
    public static X509Certificate2 Identity()
    {
        PrivateDirectory(Root);var path=Path.Combine(Root,"identity.pem");
        if(File.Exists(path)) return X509Certificate2.CreateFromPemFile(path,path);
        using var rsa=RSA.Create(3072);
        var request=new CertificateRequest("CN=LinkBridge Workspace",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,false));
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(5));
        WritePrivate(path,certificate.ExportCertificatePem()+"\n"+rsa.ExportPkcs8PrivateKeyPem()+"\n");
        return X509Certificate2.CreateFromPemFile(path,path);
    }
    public static string Export(WorkspaceProfile p)=>JsonSerializer.Serialize(p,Json);
    public static WorkspaceProfile Parse(string text)
    {if(text.Length>256*1024)throw new InvalidDataException("配置过大。");var p=JsonSerializer.Deserialize<WorkspaceProfile>(text)??throw new InvalidDataException();WorkspaceTopology.Validate(p,false);return p;}
}
