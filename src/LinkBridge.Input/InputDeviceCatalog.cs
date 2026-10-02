using System.Management;
using System.Runtime.Versioning;

namespace LinkBridge.Input;

/// <summary>输入设备类型。</summary>
public enum InputDeviceKind
{
    /// <summary>键盘。</summary>
    Keyboard,

    /// <summary>鼠标。</summary>
    Mouse,
}

/// <summary>一个本机输入设备。</summary>
public sealed record InputDevice(InputDeviceKind Kind, string Name, string Detail)
{
    public string KindLabel => Kind == InputDeviceKind.Keyboard ? "键盘" : "鼠标";
}

/// <summary>
/// 枚举本机键盘与鼠标。
/// Windows 走 WMI（Win32_Keyboard / Win32_PointingDevice），
/// macOS 走 IOKit 的 IOHIDManager（Generic Desktop 页 + Keyboard/Mouse usage）。
/// </summary>
public static class InputDeviceCatalog
{
    public static IReadOnlyList<InputDevice> Enumerate()
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacHidDevices.Enumerate();
        }

        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var result = new List<InputDevice>();

        Collect("SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Keyboard'",
            InputDeviceKind.Keyboard, result);
        Collect("SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Mouse'",
            InputDeviceKind.Mouse, result);

        // WMI 会把同一个物理设备的多个接口各报一条，按类型 + 名字去重
        return result
            .GroupBy(d => $"{d.Kind}|{d.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static void Collect(string query, InputDeviceKind kind, List<InputDevice> sink)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);

            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var id = item["DeviceID"]?.ToString() ?? string.Empty;
                    var name = item["Name"]?.ToString();

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = kind == InputDeviceKind.Keyboard ? "键盘" : "鼠标";
                    }

                    sink.Add(new InputDevice(kind, name!.Trim(), Shorten(id)));
                }
            }
        }
        catch
        {
            // 某一类查不到不影响另一类
        }
    }

    private static string Shorten(string value)
    {
        const int limit = 34;
        return value.Length <= limit ? value : string.Concat("…", value.AsSpan(value.Length - limit + 1));
    }
}
