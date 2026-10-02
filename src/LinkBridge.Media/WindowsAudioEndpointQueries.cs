using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LinkBridge.Core.Models;

namespace LinkBridge.Media;

/// <summary>Query-only raw COM ownership. No RCW is created: the system enumerator
/// identity must never acquire a managed type that conflicts with NAudio's coclass.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAudioEndpointQueries
{
    private static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EnumeratorInterface = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid VolumeInterface = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly PropertyKey FriendlyName = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    public static IReadOnlyList<MediaDevice> EnumerateSpeakers()
    {
        using var apartment = new Apartment();
        IntPtr enumerator = IntPtr.Zero, collection = IntPtr.Zero, defaultDevice = IntPtr.Zero;
        try
        {
            enumerator = CreateEnumerator(); string? defaultId = null;
            if (Method<DefaultEndpoint>(enumerator, 4)(enumerator, 0, 1, out defaultDevice) >= 0 && defaultDevice != IntPtr.Zero)
            { defaultId = DeviceId(defaultDevice); Release(ref defaultDevice); }
            Check(Method<EnumEndpoints>(enumerator, 3)(enumerator, 0, 1, out collection));
            Check(Method<Count>(collection, 3)(collection, out var count));
            if (count > 4096) throw new InvalidDataException("音频端点数量超过限额。");
            var devices = new List<MediaDevice>((int)count);
            for (uint i = 0; i < count; i++)
            {
                IntPtr device = IntPtr.Zero, properties = IntPtr.Zero;
                try
                {
                    Check(Method<Item>(collection, 4)(collection, i, out device)); var id = DeviceId(device);
                    Check(Method<OpenProperties>(device, 4)(device, 0, out properties)); var key = FriendlyName;
                    var result = Method<PropertyValue>(properties, 5)(properties, ref key, out var value);
                    string name;
                    try { Check(result); name = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "Unknown" : "Unknown"; }
                    finally { Check(PropVariantClear(ref value)); }
                    devices.Add(new(MediaDeviceKind.Speaker, id, name, MediaBackend.CoreAudio) { IsDefaultPlayback = id == defaultId });
                }
                finally { Release(ref properties); Release(ref device); }
            }
            return devices;
        }
        finally { Release(ref defaultDevice); Release(ref collection); Release(ref enumerator); }
    }
    public static double ReadVolume(string id)
    {
        using var apartment = new Apartment();
        IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero, volume = IntPtr.Zero;
        try
        {
            enumerator = CreateEnumerator();
            Check(id.Length == 0 ? Method<DefaultEndpoint>(enumerator, 4)(enumerator, 0, 1, out device)
                : Method<DeviceById>(enumerator, 5)(enumerator, id, out device));
            var iid = VolumeInterface; Check(Method<Activate>(device, 3)(device, ref iid, 23, IntPtr.Zero, out volume));
            Check(Method<Scalar>(volume, 9)(volume, out var scalar));
            if (!float.IsFinite(scalar) || scalar is < 0 or > 1) throw new InvalidDataException("系统返回无效音量。");
            return scalar * 100d;
        }
        finally { Release(ref volume); Release(ref device); Release(ref enumerator); }
    }
    private static string DeviceId(IntPtr device)
    {
        IntPtr text = IntPtr.Zero;
        try { Check(Method<GetId>(device, 5)(device, out text)); return Marshal.PtrToStringUni(text) ?? throw new InvalidDataException("系统端点 ID 为空。"); }
        finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
    }
    private static IntPtr CreateEnumerator()
    {
        var clsid = EnumeratorClass; var iid = EnumeratorInterface;
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var enumerator)); return enumerator;
    }
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate
    {
        if (instance == IntPtr.Zero) throw new InvalidDataException("系统未返回音频 COM 接口。");
        var entry = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), checked(slot * IntPtr.Size));
        return Marshal.GetDelegateForFunctionPointer<T>(entry);
    }
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    // Every pointer is an owned native reference, never a shared managed RCW.
    private static void Release(ref IntPtr value) { var owned = value; value = IntPtr.Zero; if (owned != IntPtr.Zero) Marshal.Release(owned); }
    private readonly struct Apartment : IDisposable
    {
        private readonly bool _balance;
        public Apartment()
        {
            var hr = CoInitializeEx(IntPtr.Zero, 0); // MTA; an existing STA is also valid.
            if (hr < 0 && hr != unchecked((int)0x80010106)) Check(hr);
            _balance = hr >= 0;
        }
        public void Dispose() { if (_balance) CoUninitialize(); }
    }
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr enumerator);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int PropVariantClear(ref PropVariant value);
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
    { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }

    // Slot numbers include IUnknown's QueryInterface/AddRef/Release (0,1,2).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumEndpoints(IntPtr self, int flow, uint stateMask, out IntPtr devices);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DefaultEndpoint(IntPtr self, int flow, int role, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DeviceById(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Count(IntPtr self, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Item(IntPtr self, uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetId(IntPtr self, out IntPtr id);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OpenProperties(IntPtr self, uint mode, out IntPtr store);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PropertyValue(IntPtr self, ref PropertyKey key, out PropVariant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Activate(IntPtr self, ref Guid iid, uint context, IntPtr parameters, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Scalar(IntPtr self, out float value);
}
