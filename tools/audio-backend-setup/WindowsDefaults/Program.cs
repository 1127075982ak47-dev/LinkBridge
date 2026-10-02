using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using NAudio.CoreAudioApi;

// This independent tool enumerates endpoints only; it never starts an audio client or microphone.
var json = new JsonSerializerOptions { WriteIndented = true };
try
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows only.");
    if (args.Length < 2 || args[0] is not ("snapshot" or "restore"))
        throw new ArgumentException("snapshot <new.json> | restore <saved.json> [--apply-private-policyconfig]");
    using var enumerator = new MMDeviceEnumerator();
    string identity = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Missing user SID.");
    var devices = new List<Endpoint>();
    foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.All))
            using (device) devices.Add(new(device.ID, device.FriendlyName, flow.ToString(), device.State.ToString()));
    var defaults = new List<Default>();
    foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            try
            {
                using var d = enumerator.GetDefaultAudioEndpoint(flow, role);
                defaults.Add(new(flow.ToString(), role.ToString(), d.ID, d.FriendlyName, null));
            }
            catch (COMException e) { defaults.Add(new(flow.ToString(), role.ToString(), null, null, $"0x{e.HResult:X8}")); }
        }
    var current = new Snapshot(1, "Windows", Environment.MachineName, identity, DateTimeOffset.UtcNow, defaults, devices);
    if (args[0] == "snapshot")
    {
        if (args.Length != 2) throw new ArgumentException("snapshot takes one path.");
        var path = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write); // Never overwrite pre-install backup.
        JsonSerializer.Serialize(output, current, json);
        Console.WriteLine(JsonSerializer.Serialize(current, json));
        return 0;
    }
    if (args.Length > 3 || args.Length == 3 && args[2] != "--apply-private-policyconfig")
        throw new ArgumentException("Unknown restore option; no changes made.");
    var saved = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(args[1]), json) ?? throw new InvalidDataException("Invalid snapshot.");
    if (saved.Schema != 1 || saved.Platform != "Windows" || saved.Machine != current.Machine || saved.Identity != identity)
        throw new InvalidDataException("Snapshot does not belong to this machine/user/schema.");
    if (saved.Defaults is null || saved.Defaults.Count != 6) throw new InvalidDataException("Six default roles required.");
    var seen = new HashSet<string>();
    var plan = new List<Default>();
    foreach (var target in saved.Defaults)
    {
        if (!Enum.TryParse<DataFlow>(target.Flow, out var f) || f is not (DataFlow.Render or DataFlow.Capture)
            || !Enum.TryParse<Role>(target.Role, out var r) || r is not (Role.Console or Role.Multimedia or Role.Communications)
            || !seen.Add(target.Flow + "/" + target.Role)) throw new InvalidDataException("Invalid/duplicate role.");
        if (target.Id is null) throw new InvalidDataException("Saved role was unavailable; cannot safely restore it.");
        var endpoint = devices.SingleOrDefault(d => d.Id == target.Id);
        if (endpoint is null || endpoint.State != "Active" || endpoint.Flow != target.Flow || endpoint.Name != target.Name)
            throw new InvalidDataException($"Saved endpoint unavailable/renamed: {target.Name}; no fallback chosen.");
        var actual = defaults.Single(d => d.Flow == target.Flow && d.Role == target.Role);
        Console.WriteLine($"{target.Flow}/{target.Role}: {actual.Name} => {target.Name}; changed={actual.Id != target.Id}");
        if (actual.Id != target.Id) plan.Add(target);
    }
    if (args.Length != 3) { Console.WriteLine($"DRY RUN: {plan.Count} changed roles; no defaults written."); return 0; }
    // PolicyConfig is an undocumented Windows COM ABI. Explicit opt-in only; never used by installation scripts.
    // No blanket registry edit, role fallback, or automatic escalation. Recheck all six roles after applying.
    IPolicyConfig? policy = null;
    try
    {
        if (plan.Count > 0) policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"))!)!;
        foreach (var target in plan)
        {
            Marshal.ThrowExceptionForHR(policy!.SetDefaultEndpoint(target.Id!, Enum.Parse<Role>(target.Role)));
            Console.WriteLine($"Applied {target.Flow}/{target.Role} to {target.Name}.");
        }
        foreach (var target in saved.Defaults)
        {
            using var actual = enumerator.GetDefaultAudioEndpoint(Enum.Parse<DataFlow>(target.Flow), Enum.Parse<Role>(target.Role));
            if (actual.ID != target.Id) throw new InvalidOperationException($"Verification failed: {target.Flow}/{target.Role}.");
        }
        Console.WriteLine("All six default endpoint IDs match snapshot.");
    }
    finally { if (policy is not null) Marshal.FinalReleaseComObject(policy); }
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine($"FAILED 0x{ex.HResult:X8}: {ex.Message}"); return 1; }

record Endpoint(string Id, string Name, string Flow, string State);
record Default(string Flow, string Role, string? Id, string? Name, string? Error);
record Snapshot(int Schema, string Platform, string Machine, string Identity, DateTimeOffset CapturedUtc, List<Default> Defaults, List<Endpoint> Devices);

[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, out IntPtr format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpoint, IntPtr mix);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultPeriod, out long period, out long minimum);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
}
