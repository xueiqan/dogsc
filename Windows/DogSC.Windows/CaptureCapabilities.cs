using System.Runtime.InteropServices;
using System.Text;
using ScreenRecorderLib;

namespace DogSC.Windows;

public sealed record CaptureDeviceInfo(string Id, string Name)
{
    public bool IsDefault { get; init; }
    public bool IsBluetooth { get; init; }
    public override string ToString() => Name;
}

public sealed record CaptureCodecInfo(string Id, string Name, bool LibrarySupported,
    int HardwareEncoders, int SoftwareEncoders, string Detail, int HardwareDecoders = 0, int SoftwareDecoders = 0)
{
    public bool IsAvailable => LibrarySupported && HardwareEncoders + SoftwareEncoders > 0;
    public bool CanDecode => LibrarySupported && HardwareDecoders + SoftwareDecoders > 0;
}

public static class CaptureCapabilities
{
    public static IReadOnlyList<CaptureDeviceInfo> GetCameras() => Recorder.GetSystemVideoCaptureDevices()
        .Select(device => new CaptureDeviceInfo(device.DeviceName, device.FriendlyName)).ToArray();

    public static IReadOnlyList<CaptureDeviceInfo> GetMicrophones() => AudioDevices(AudioDeviceSource.InputDevices);
    public static IReadOnlyList<CaptureDeviceInfo> GetAudioOutputs() => AudioDevices(AudioDeviceSource.OutputDevices);

    public static CaptureDeviceInfo? GetPreferredMicrophone(IReadOnlyList<CaptureDeviceInfo> microphones) =>
        microphones.Where(device => !string.IsNullOrEmpty(device.Id))
            .OrderByDescending(device => device.IsBluetooth).ThenByDescending(device => device.IsDefault).FirstOrDefault();

    public static VideoCaptureFormat? GetPreferredCameraFormat(IReadOnlyList<VideoCaptureFormat> formats) =>
        formats.Where(format => format.FrameSize is not null
                && format.FrameSize.Width is >= 2 and <= 8192 && format.FrameSize.Height is >= 2 and <= 8192
                && double.IsFinite(format.Framerate) && format.Framerate is >= 1 and <= 120)
            .OrderByDescending(format => format.FrameSize.Width * format.FrameSize.Height)
            .ThenByDescending(format => format.Framerate).FirstOrDefault();

    public static int GetCameraEncoderFrameRate(VideoCaptureFormat format)
    {
        if (!double.IsFinite(format.Framerate) || format.Framerate is < 1 or > 120)
            throw new InvalidOperationException("所选摄像头格式帧率不在1–120fps支持范围内。");
        return Math.Clamp((int)Math.Round(format.Framerate), 1, 120);
    }

    // Enumerating devices does not start capture. Formats are queried only for an explicitly selected camera.
    public static IReadOnlyList<VideoCaptureFormat> GetCameraFormats(string deviceId)
    {
        int com = CoInitializeEx(IntPtr.Zero, 0);
        if (com < 0 && com != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(com);
        try
        {
            Marshal.ThrowExceptionForHR(MFStartup(0x00020070, 0));
            try
            {
                RequireCamera(deviceId);
                return Recorder.GetSupportedVideoCaptureFormatsForDevice(deviceId).ToArray();
            }
            finally { MFShutdown(); }
        }
        finally { if (com >= 0) CoUninitialize(); }
    }

    public static void RequireCamera(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 4096
            || !GetCameras().Any(device => string.Equals(device.Id, deviceId, StringComparison.Ordinal)))
            throw new InvalidOperationException("所选摄像头不可用或已断开，请刷新设备；Windows摄像头权限也可能阻止访问。");
    }

    public static IReadOnlyList<CaptureCodecInfo> GetCodecs()
    {
        var com = CoInitializeEx(IntPtr.Zero, 0);
        if (com < 0 && com != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(com);
        try
        {
            Marshal.ThrowExceptionForHR(MFStartup(0x00020070, 0));
            try
            {
                return new[]
                {
                    Codec("H264", "H.264", new Guid("34363248-0000-0010-8000-00aa00389b71")),
                    Codec("HEVC", "H.265 / HEVC", new Guid("43564548-0000-0010-8000-00aa00389b71")),
                    new CaptureCodecInfo("ProRes", "ProRes 422", false, 0, 0,
                        "现有进程内录制库没有ProRes编码器；不会自动替换为H.264。")
                };
            }
            finally { MFShutdown(); }
        }
        finally { if (com >= 0) CoUninitialize(); }
    }

    public static IVideoEncoder CreateEncoder(string codecId) => codecId switch
    {
        "H264" => new H264VideoEncoder(),
        "HEVC" => new H265VideoEncoder(),
        _ => throw new NotSupportedException($"不支持录制编码{codecId}，不会自动替换编码格式。")
    };

    public const bool SupportsAudioOnlyRecording = true;
    public static bool SupportsProcessAudioLoopback => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    private static IReadOnlyList<CaptureDeviceInfo> AudioDevices(AudioDeviceSource source)
    {
        var devices = Recorder.GetSystemAudioDevices(source);
        if (devices.Count > 1024) throw new InvalidOperationException("音频设备数量超过1024个限制。");
        int com = CoInitializeEx(IntPtr.Zero, 0);
        if (com < 0 && com != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(com);
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            string? defaultId = DefaultAudioDeviceId(enumerator, source == AudioDeviceSource.InputDevices ? 1 : 0);
            var result = new List<CaptureDeviceInfo>();
            foreach (var candidate in devices)
            {
                IMMDevice? device = null;
                try
                {
                    if (string.IsNullOrEmpty(candidate.DeviceName) || candidate.DeviceName.Length > 4096
                        || enumerator.GetDevice(candidate.DeviceName, out device) < 0
                        || device.GetState(out var state) < 0 || state != 1) continue;
                    result.Add(new CaptureDeviceInfo(candidate.DeviceName, candidate.FriendlyName)
                    {
                        IsDefault = string.Equals(candidate.DeviceName, defaultId, StringComparison.Ordinal),
                        IsBluetooth = source == AudioDeviceSource.InputDevices && IsBluetoothEndpoint(device, enumerator)
                    });
                }
                finally { Release(device); }
            }
            return result;
        }
        finally { Release(enumerator); if (com >= 0) CoUninitialize(); }
    }

    private static string? DefaultAudioDeviceId(IMMDeviceEnumerator enumerator, int flow)
    {
        IMMDevice? device = null;
        IntPtr id = IntPtr.Zero;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, 1, out device) < 0 || device.GetId(out id) < 0) return null;
            return ReadNativeString(id);
        }
        finally { if (id != IntPtr.Zero) Marshal.FreeCoTaskMem(id); Release(device); }
    }

    private static bool IsBluetoothEndpoint(IMMDevice endpoint, IMMDeviceEnumerator enumerator)
    {
        object? topologyObject = null;
        IConnector? connector = null;
        IMMDevice? adapter = null;
        IPropertyStore? properties = null;
        IntPtr adapterId = IntPtr.Zero;
        PropVariant instance = default;
        try
        {
            // Endpoint IDs are opaque; follow the audio topology to the physical adapter's PnP properties.
            var topologyId = typeof(IDeviceTopology).GUID;
            if (endpoint.Activate(ref topologyId, 23, IntPtr.Zero, out topologyObject) < 0
                || topologyObject is not IDeviceTopology topology
                || topology.GetConnector(0, out connector) < 0
                || connector.GetDeviceIdConnectedTo(out adapterId) < 0) return false;
            var id = ReadNativeString(adapterId);
            if (id is null || enumerator.GetDevice(id, out adapter) < 0
                || adapter.OpenPropertyStore(0, out properties) < 0) return false;
            var instanceKey = new PropertyKey(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
            if (properties.GetValue(ref instanceKey, out instance) < 0 || instance.Type != 31) return false;
            var instanceId = ReadNativeString(instance.Pointer);
            return instanceId is not null && HasBluetoothBus(instanceId);
        }
        finally
        {
            PropVariantClear(ref instance);
            if (adapterId != IntPtr.Zero) Marshal.FreeCoTaskMem(adapterId);
            Release(properties); Release(adapter); Release(connector); Release(topologyObject);
        }
    }

    private static bool HasBluetoothBus(string instanceId)
    {
        if (CM_Locate_DevNode(out var node, instanceId, 0) != 0) return false;
        var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
        var buffer = new byte[512];
        var visited = new HashSet<uint>();
        for (int depth = 0; depth < 32 && visited.Add(node); depth++)
        {
            uint size = (uint)buffer.Length;
            if (CM_Get_DevNode_Property(node, ref key, out var type, buffer, ref size, 0) == 0
                && type == 18 && size is >= 2 and <= 512 && (size & 1) == 0 && buffer[size - 1] == 0 && buffer[size - 2] == 0)
            {
                string bus = Encoding.Unicode.GetString(buffer, 0, checked((int)size - 2));
                if (IsBluetoothEnumerator(bus)) return true;
            }
            if (CM_Get_Parent(out var parent, node, 0) != 0) break;
            node = parent;
        }
        // shortcut: drivers hiding their Bluetooth PnP ancestry use the Windows default; add a driver-specific path when evidenced.
        return false;
    }

    private static bool IsBluetoothEnumerator(string bus) =>
        string.Equals(bus, "BTHENUM", StringComparison.OrdinalIgnoreCase)
        || string.Equals(bus, "BTHHFENUM", StringComparison.OrdinalIgnoreCase)
        || string.Equals(bus, "BTHLEDEVICE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(bus, "BTHLEENUM", StringComparison.OrdinalIgnoreCase);

    private static string? ReadNativeString(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return null;
        for (int length = 0; length <= 4096; length++)
            if (Marshal.ReadInt16(pointer, length * 2) == 0) return Marshal.PtrToStringUni(pointer, length);
        return null;
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }
    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
    }
    [ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector connector);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector connector);
        [PreserveSig] int GetConnectorIdConnectedTo(out IntPtr id);
        [PreserveSig] int GetDeviceIdConnectedTo(out IntPtr id);
    }
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Property(uint node, ref PropertyKey key, out uint type, byte[] buffer, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);

    private static CaptureCodecInfo Codec(string id, string name, Guid subtype)
    {
        var hardware = CountTransforms(subtype, 0x4, decoder: false);
        var software = CountTransforms(subtype, 0x3, decoder: false);
        var hardwareDecoders = CountTransforms(subtype, 0x4, decoder: true);
        var softwareDecoders = CountTransforms(subtype, 0x3, decoder: true);
        return new CaptureCodecInfo(id, name, true, hardware, software,
            hardware + software > 0
                ? "已检测Media Foundation编码器；实际所选尺寸、帧率和设备必须在开始录制时验证。"
                : "系统未检测到此格式的Media Foundation编码器；不会自动替换编码格式。", hardwareDecoders, softwareDecoders);
    }

    private static int CountTransforms(Guid subtype, uint flags, bool decoder)
    {
        var output = new MftRegisterTypeInfo
        {
            MajorType = new Guid("73646976-0000-0010-8000-00aa00389b71"), Subtype = subtype
        };
        IntPtr activate = IntPtr.Zero;
        uint count = 0;
        try
        {
            var result = decoder
                ? MFTEnumExInput(new Guid("d6c02d4b-6833-45b4-971a-05a4b04bab91"), flags,
                    ref output, IntPtr.Zero, out activate, out count)
                : MFTEnumEx(new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a"), flags,
                    IntPtr.Zero, ref output, out activate, out count);
            Marshal.ThrowExceptionForHR(result);
            return checked((int)count);
        }
        finally
        {
            if (activate != IntPtr.Zero)
            {
                for (uint index = 0; index < count; index++)
                {
                    var pointer = Marshal.ReadIntPtr(activate, checked((int)index * IntPtr.Size));
                    if (pointer != IntPtr.Zero) Marshal.Release(pointer);
                }
                Marshal.FreeCoTaskMem(activate);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterTypeInfo { public Guid MajorType; public Guid Subtype; }
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFTEnumEx(Guid category,
        uint flags, IntPtr inputType, ref MftRegisterTypeInfo outputType, out IntPtr activate, out uint count);
    [DllImport("mfplat.dll", EntryPoint = "MFTEnumEx", ExactSpelling = true)] private static extern int MFTEnumExInput(Guid category,
        uint flags, ref MftRegisterTypeInfo inputType, IntPtr outputType, out IntPtr activate, out uint count);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
}
