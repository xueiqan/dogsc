using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DogSC.Core;

namespace DogSC.Windows;

public sealed class RecordingRegionOverlay : Window
{
    private const int BorderPixels = 2;
    private const long PassThroughStyles = 0x08000020; // WS_EX_NOACTIVATE | WS_EX_TRANSPARENT
    private readonly int left, top, width, height;
    private readonly Border outline;

    public RecordingRegionOverlay(CaptureTarget target, CaptureRegion region)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(region);
        region.Validate(target.Width, target.Height);
        left = checked(target.Left + region.X);
        top = checked(target.Top + region.Y);
        width = region.Width;
        height = region.Height;
        Title = "DogSC录制区域";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Height = 1;
        Opacity = 0;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        outline = new Border { Background = Brushes.Transparent, SnapsToDevicePixels = true };
        Content = outline;
        UpdateState(RecordingState.Starting);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            long styles = GetWindowLongPtr(handle, -20).ToInt64();
            Marshal.SetLastPInvokeError(0);
            if (SetWindowLongPtr(handle, -20, new nint(styles | PassThroughStyles)) == 0
                && Marshal.GetLastPInvokeError() != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!SetWindowDisplayAffinity(handle, 0x11))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将区域提示框排除在录屏外。");
        };
    }

    public void ShowSafely()
    {
        try
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            Show();
            var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
            if (previousDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!SetWindowPos(handle, new nint(-1), left, top, width, height, 0x0010 | 0x0020)
                    || !GetWindowRect(handle, out var bounds))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (bounds.Left != left || bounds.Top != top || bounds.Right - bounds.Left != width
                    || bounds.Bottom - bounds.Top != height)
                    throw new InvalidOperationException("区域提示框未能对齐录制区域，已停止开始录制。");
            }
            finally { SetThreadDpiAwarenessContext(previousDpi); }
            if ((GetWindowLongPtr(handle, -20).ToInt64() & PassThroughStyles) != PassThroughStyles
                || !GetWindowDisplayAffinity(handle, out uint affinity) || affinity != 0x11)
                throw new InvalidOperationException("区域提示框的鼠标穿透或录屏排除未生效。");
            var dpi = GetDpiForWindow(handle);
            if (dpi == 0) throw new InvalidOperationException("无法读取区域提示框的显示比例。");
            // Keep screen-edge selections visible; capture exclusion removes the outline from the video.
            outline.BorderThickness = new Thickness(BorderPixels * 96.0 / dpi);
            Opacity = 1;
        }
        catch
        {
            Close();
            throw;
        }
    }

    public void UpdateState(RecordingState state)
    {
        outline.BorderBrush = state switch
        {
            RecordingState.Recording => Brushes.LimeGreen,
            RecordingState.Paused => Brushes.Gold,
            _ => Brushes.Orange
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
