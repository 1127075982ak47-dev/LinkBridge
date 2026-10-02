using System.Management;
using LinkBridge.Input;
using LinkBridge.Media;
using NAudio.CoreAudioApi;
using LinkBridge.Core;

if (args.Contains("--audio-receiver-probe"))
{
    var settings = SettingsStore.Load();
    await using var receiver = new SharedAudioReceiver(settings.InputPairingSecret,
        ip => ip == settings.AllowedControllerAddress, () => settings.PlaybackDeviceId);
    receiver.LogEmitted += Console.WriteLine;
    receiver.Start();
    await Task.Delay(TimeSpan.FromSeconds(12));
    Console.WriteLine($"PCM received: {receiver.ReceivedBytes} bytes");
    if (receiver.ReceivedBytes == 0) Environment.ExitCode = 1;
    return;
}

if (args.Contains("--default-speaker"))
{
    using var enumerator = new MMDeviceEnumerator();
    using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    Console.WriteLine($"Default speaker: {endpoint.FriendlyName}");
    Console.WriteLine($"Endpoint ID: {endpoint.ID}");
    return;
}

// --recv：只做接收验证（本机自收，用来确认接收链路通不通）
if (args.Contains("--recv"))
{
    var frames = 0;
    using var receiver = new MediaStreamReceiver { ExpectedSourceAddress = "127.0.0.1" };

    receiver.FrameReady += (data, width, height) =>
    {
        frames++;
        if (frames is 1 or 10 or 30 or 60 or 120)
        {
            Console.WriteLine($"  第 {frames} 帧  {width}x{height}  {data.Length / 1024} KB");
        }
    };

    receiver.LogEmitted += line => Console.WriteLine($"  [ffmpeg] {line}");

    receiver.Start(45679);
    Console.WriteLine("已开始接收，等 20 秒…");

    await Task.Delay(TimeSpan.FromSeconds(20));
    receiver.Stop();

    Console.WriteLine($"共收到 {frames} 帧");
    return;
}

// --disk：只做磁盘接口判定验证
if (args.Contains("--disk") && OperatingSystem.IsWindows())
{
    Console.WriteLine("盘符 -> 分区 -> 物理盘 / 接口类型");
    Console.WriteLine();

    foreach (var drive in DriveInfo.GetDrives())
    {
        if (drive.DriveType != DriveType.Fixed)
        {
            Console.WriteLine($"{drive.Name}  DriveType={drive.DriveType}（跳过，非 Fixed）");
            continue;
        }

        var letter = drive.Name.TrimEnd('\\', '/');

        try
        {
            using var partitions = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{letter}'}} " +
                "WHERE AssocClass=Win32_LogicalDiskToPartition");

            var found = false;

            foreach (var partition in partitions.Get().Cast<ManagementBaseObject>())
            {
                var partitionId = partition["DeviceID"]?.ToString();

                using var disks = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionId}'}} " +
                    "WHERE AssocClass=Win32_DiskDriveToDiskPartition");

                foreach (var disk in disks.Get().Cast<ManagementBaseObject>())
                {
                    var busType = disk["InterfaceType"]?.ToString();
                    var isUsb = string.Equals(busType, "USB", StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine($"{letter,-4} -> {partitionId,-22} -> {disk["DeviceID"],-22} / {busType,-6} => {(isUsb ? "外置 ✅" : "内置 ✗")}");
                    found = true;
                }
            }

            if (!found)
            {
                Console.WriteLine($"{letter,-4} -> 没查到关联磁盘");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{letter,-4} -> 查询失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    return;
}

var endpoints = AudioEndpointCatalog.Enumerate();
Console.WriteLine($"音频端点 {endpoints.Count} 个，错误：{AudioEndpointCatalog.LastError ?? "无"}");
foreach (var endpoint in endpoints)
{
    Console.WriteLine($"  [{endpoint.KindLabel}]  {endpoint.Name}");
}

var catalog = new MediaDeviceCatalog();
var devices = await catalog.EnumerateAsync();

Console.WriteLine();
Console.WriteLine($"合计 {devices.Count} 个设备：");
foreach (var device in devices)
{
    Console.WriteLine($"  [{device.KindLabel}]  {device.Name}");
}

Console.WriteLine();
var inputs = InputDeviceCatalog.Enumerate();
Console.WriteLine($"键鼠 {inputs.Count} 个：");
foreach (var device in inputs)
{
    Console.WriteLine($"  [{device.KindLabel}]  {device.Name}");
}
