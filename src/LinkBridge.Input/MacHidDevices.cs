using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LinkBridge.Input;

/// <summary>
/// macOS 的键盘 / 鼠标枚举，走 IOKit 的 IOHIDManager（纯 P/Invoke，无第三方依赖）。
///
/// 分类**必须**用 <c>IOHIDDeviceConformsTo</c>，不能用设备的主 usage：
/// 实测一个「键鼠套装」接收器会把鼠标那一路的主 usage 报成厂商自定义页（0xFFB5/0x01），
/// 但 <c>IOHIDDeviceConformsTo(dev, 1, 2)</c> 仍能正确识别成鼠标。
/// （另外 kIOHIDDeviceUsagePageKey / kIOHIDDeviceUsageKey 在 macOS 27 上取到的是 null，
///  要问 usage 得用 kIOHIDPrimaryUsagePageKey / kIOHIDPrimaryUsageKey。）
///
/// 名字优先取 kIOHIDProductKey，拿不到再退回「厂商 + ProductID」拼一个。
/// Detail 里放 Transport + VID:PID + LocationID（稳定，不随插拔顺序变），方便排查。
/// 同一个物理设备若同时符合键盘与鼠标（一体式接收器很常见），会各出一条记录。
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacHidDevices
{
    private const string IOKitFramework = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint EncodingUtf8 = 0x08000100;

    private const int CfNumberSInt32Type = 3;
    private const int CfNumberSInt64Type = 4;

    /// <summary>HID usage page：Generic Desktop。</summary>
    private const uint PageGenericDesktop = 0x01;

    private const uint UsagePointer = 0x01;
    private const uint UsageMouse = 0x02;
    private const uint UsageKeyboard = 0x06;
    private const uint UsageKeypad = 0x07;

    [DllImport(IOKitFramework)]
    private static extern IntPtr IOHIDManagerCreate(IntPtr allocator, uint options);

    [DllImport(IOKitFramework)]
    private static extern void IOHIDManagerSetDeviceMatching(IntPtr manager, IntPtr matching);

    [DllImport(IOKitFramework)]
    private static extern IntPtr IOHIDManagerCopyDevices(IntPtr manager);

    [DllImport(IOKitFramework)]
    private static extern IntPtr IOHIDDeviceGetProperty(IntPtr device, IntPtr key);

    [DllImport(IOKitFramework)]
    private static extern bool IOHIDDeviceConformsTo(IntPtr device, uint usagePage, uint usage);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFStringCreateWithCString(
        IntPtr allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr,
        uint encoding);

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFSetGetCount(IntPtr set);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFSetGetValues(IntPtr set, IntPtr[] values);

    [DllImport(CoreFoundationFramework)]
    private static extern ulong CFGetTypeID(IntPtr cf);

    [DllImport(CoreFoundationFramework)]
    private static extern ulong CFStringGetTypeID();

    [DllImport(CoreFoundationFramework)]
    private static extern ulong CFNumberGetTypeID();

    [DllImport(CoreFoundationFramework)]
    private static extern bool CFStringGetCString(IntPtr theString, IntPtr buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundationFramework)]
    private static extern bool CFNumberGetValue(IntPtr number, int theType, IntPtr valuePtr);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr cf);

    /// <summary>列出本机键盘与鼠标。</summary>
    public static IReadOnlyList<InputDevice> Enumerate()
    {
        var result = new List<InputDevice>();

        try
        {
            var manager = IOHIDManagerCreate(IntPtr.Zero, 0);
            if (manager == IntPtr.Zero)
            {
                return result;
            }

            IOHIDManagerSetDeviceMatching(manager, IntPtr.Zero);

            var devices = IOHIDManagerCopyDevices(manager);
            if (devices == IntPtr.Zero)
            {
                return result;
            }

            var count = (int)CFSetGetCount(devices);
            if (count <= 0)
            {
                return result;
            }

            var handles = new IntPtr[count];
            CFSetGetValues(devices, handles);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var handle in handles)
            {
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                foreach (var device in ReadDevices(handle))
                {
                    // 同一个物理设备常常暴露多个 HID 接口，按 类型 + 商品名 + 位置 去重
                    if (seen.Add($"{device.Kind}|{device.Name}|{device.Detail}"))
                    {
                        result.Add(device);
                    }
                }
            }
        }
        catch
        {
            // 枚举失败不该拖垮界面，返回已拿到的部分
        }

        return result;
    }

    /// <summary>一个 HID 设备可能同时是键盘又是鼠标（一体式接收器），所以返回列表。</summary>
    private static IEnumerable<InputDevice> ReadDevices(IntPtr handle)
    {
        var kinds = new List<InputDeviceKind>();

        if (IOHIDDeviceConformsTo(handle, PageGenericDesktop, UsageKeyboard)
            || IOHIDDeviceConformsTo(handle, PageGenericDesktop, UsageKeypad))
        {
            kinds.Add(InputDeviceKind.Keyboard);
        }

        if (IOHIDDeviceConformsTo(handle, PageGenericDesktop, UsageMouse)
            || IOHIDDeviceConformsTo(handle, PageGenericDesktop, UsagePointer))
        {
            kinds.Add(InputDeviceKind.Mouse);
        }

        if (kinds.Count == 0)
        {
            yield break;
        }

        var product = ReadString(handle, "Product");
        var manufacturer = ReadString(handle, "Manufacturer");
        var vendorId = ReadNumber(handle, "VendorID");
        var productId = ReadNumber(handle, "ProductID");
        var locationId = ReadNumber(handle, "LocationID");
        var transport = ReadString(handle, "Transport");

        var detail = string.Join(
            " · ",
            new[]
            {
                transport,
                vendorId is not null && productId is not null ? $"{vendorId.Value:X4}:{productId.Value:X4}" : null,
                locationId is not null ? $"位置 0x{locationId.Value:X8}" : null,
            }.Where(part => !string.IsNullOrWhiteSpace(part)));

        if (detail.Length == 0)
        {
            detail = "—";
        }

        foreach (var kind in kinds)
        {
            var name = !string.IsNullOrWhiteSpace(product)
                ? product!.Trim()
                : manufacturer is { Length: > 0 } && productId is not null
                    ? $"{manufacturer.Trim()} 0x{productId.Value:X4}"
                    : kind == InputDeviceKind.Keyboard ? "键盘" : "鼠标";

            yield return new InputDevice(kind, name, detail);
        }
    }

    private static long? ReadNumber(IntPtr handle, string key)
    {
        var value = ReadProperty(handle, key);
        if (value == IntPtr.Zero || CFGetTypeID(value) != CFNumberGetTypeID())
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            if (CFNumberGetValue(value, CfNumberSInt64Type, buffer))
            {
                return Marshal.ReadInt64(buffer);
            }

            return CFNumberGetValue(value, CfNumberSInt32Type, buffer)
                ? Marshal.ReadInt32(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadString(IntPtr handle, string key)
    {
        var value = ReadProperty(handle, key);
        if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID())
        {
            return null;
        }

        const int bufferSize = 512;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            return CFStringGetCString(value, buffer, bufferSize, EncodingUtf8)
                ? Marshal.PtrToStringUTF8(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr ReadProperty(IntPtr handle, string key)
    {
        var cfKey = CFStringCreateWithCString(IntPtr.Zero, key, EncodingUtf8);
        if (cfKey == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            return IOHIDDeviceGetProperty(handle, cfKey);
        }
        finally
        {
            CFRelease(cfKey);
        }
    }
}
