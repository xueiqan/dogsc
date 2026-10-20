using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DogSC.Windows;

public sealed record CaptureTarget(string Name, string DeviceName, nint WindowHandle,
    int Left, int Top, int Width, int Height, int RefreshRate = 0)
{
    public bool IsWindow => WindowHandle != 0;
    public override string ToString() => Name;
}

public static class CaptureCatalog
{
    public static IReadOnlyList<CaptureTarget> GetDisplays()
    {
        var targets = new List<CaptureTarget>();
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        if (previousDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Rect bounds, nint data) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), DeviceName = "" };
                if (GetMonitorInfo(monitor, ref info) && info.Bounds.Right > info.Bounds.Left
                    && info.Bounds.Bottom > info.Bounds.Top)
                {
                    int width = info.Bounds.Right - info.Bounds.Left;
                    int height = info.Bounds.Bottom - info.Bounds.Top;
                    int refreshRate = ReadDisplayRefreshRate(info.DeviceName);
                    string name = $"{info.DeviceName} · {width}×{height}";
                    if (refreshRate > 0) name += $" · {refreshRate}Hz";
                    if ((info.Flags & 1) != 0) name += "（主屏幕）";
                    targets.Add(new(name, info.DeviceName, 0, info.Bounds.Left, info.Bounds.Top, width, height,
                        refreshRate));
                }
                return true;
            }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return targets;
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    public static IReadOnlyList<CaptureTarget> GetWindows()
    {
        var targets = new List<CaptureTarget>();
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        if (previousDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!EnumWindows((handle, _) =>
            {
                if (!IsAvailableWindow(handle)) return true;
                GetWindowThreadProcessId(handle, out uint pid);
                if (pid == 0) return true;
                int titleLength = GetWindowTextLength(handle);
                if (titleLength <= 0) return true;
                var title = new StringBuilder(Math.Min(titleLength + 1, 32_768));
                if (GetWindowText(handle, title, title.Capacity) == 0 || string.IsNullOrWhiteSpace(title.ToString()))
                    return true;
                if (pid == Environment.ProcessId && !title.ToString().StartsWith("DogSC编辑器", StringComparison.Ordinal))
                    return true;
                if (DwmGetWindowAttribute(handle, 9, out Rect bounds, Marshal.SizeOf<Rect>()) != 0
                    && !GetWindowRect(handle, out bounds)) return true;
                int width = bounds.Right - bounds.Left;
                int height = bounds.Bottom - bounds.Top;
                if (width <= 0 || height <= 0 || !IsWindow(handle)) return true;
                targets.Add(new($"{title} · {width}×{height}", "", handle,
                    bounds.Left, bounds.Top, width, height, GetWindowRefreshRate(handle)));
                return true;
            }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return targets.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    public static bool TryGetWindowBounds(nint handle, out System.Windows.Rect rectangle)
    {
        rectangle = default;
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        if (previousDpi == 0) return false;
        try
        {
            if (!IsWindow(handle) || DwmGetWindowAttribute(handle, 9, out Rect bounds, Marshal.SizeOf<Rect>()) != 0
                && !GetWindowRect(handle, out bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
            rectangle = new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            return true;
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    // Read the current display mode when recording starts; this is nominal Hz, not instantaneous VRR.
    public static uint GetWindowProcessId(CaptureTarget target)
    {
        if (!target.IsWindow || !IsAvailableWindow(target.WindowHandle)) throw new InvalidOperationException("应用声音必须选择当前可用的窗口。");
        GetWindowThreadProcessId(target.WindowHandle, out var pid);
        if (pid == 0) throw new InvalidOperationException("无法确定所选窗口的应用进程。");
        return pid;
    }

    public static int GetRefreshRate(CaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.IsWindow ? GetWindowRefreshRate(target.WindowHandle) : ReadDisplayRefreshRate(target.DeviceName);
    }

    private static int GetWindowRefreshRate(nint handle)
    {
        if (!IsWindow(handle)) return 0;
        // MonitorFromWindow selects the largest intersection, or the nearest display for an off-screen window.
        nint monitor = MonitorFromWindow(handle, 2);
        if (monitor == 0) return 0;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), DeviceName = "" };
        return GetMonitorInfo(monitor, ref info) ? ReadDisplayRefreshRate(info.DeviceName) : 0;
    }

    private static int ReadDisplayRefreshRate(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return 0;
        var mode = new DevMode
        {
            DeviceName = "", FormName = "", Size = checked((ushort)Marshal.SizeOf<DevMode>())
        };
        // ENUM_CURRENT_SETTINGS is DWORD(-1); 0/1 Hz denote a default or unspecified rate.
        if (!EnumDisplaySettingsEx(deviceName, uint.MaxValue, ref mode, 0)
            || (mode.Fields & 0x00400000) == 0 || mode.DisplayFrequency is < 2 or > 1000)
            return 0;
        return (int)mode.DisplayFrequency;
    }

    internal static bool IsAvailableWindow(nint handle) => handle != 0 && IsWindow(handle)
        && IsWindowVisible(handle) && !IsIconic(handle)
        && (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) != 0 || cloaked == 0)
        && GetWindowRect(handle, out Rect bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Bounds, WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    // DEVMODEW, 220 bytes: the display arm occupies the same 16 bytes as the printer union.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    private delegate bool MonitorCallback(nint monitor, nint dc, ref Rect bounds, nint data);
    private delegate bool WindowCallback(nint window, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsEx(string deviceName, uint modeNumber, ref DevMode mode, uint flags);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maximumLength);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out Rect bounds);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, int attribute, out Rect value, int size);
}
