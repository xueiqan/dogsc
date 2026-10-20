using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DogSC.Windows;

public sealed class CameraPreviewWindow : Window
{
    public const int PixelWidth = 560, PixelHeight = 380;
    private readonly Image picture = new()
    {
        Stretch = Stretch.Uniform,
        RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(-1, 1)
    };
    private readonly System.Windows.Shapes.Path lockIcon = new()
    {
        Width = 18, Height = 18, Stretch = Stretch.Uniform, Stroke = Brushes.White, StrokeThickness = 1.8,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = .8,
        Effect = new DropShadowEffect { BlurRadius = 2, ShadowDepth = 0, Opacity = .6 }
    };
    private readonly ToggleButton pin = new()
    {
        Width = 32, Height = 32, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand
    };
    private readonly System.Windows.Shapes.Path eyeIcon = new()
    {
        Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.8,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        Effect = new DropShadowEffect { BlurRadius = 2, ShadowDepth = 0, Opacity = .6 }
    };
    private readonly ToggleButton eye = new()
    {
        Width = 32, Height = 32, Margin = new Thickness(8), IsChecked = true,
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly Grid layout = new() { ClipToBounds = true };
    private Window? visibilityControl;
    private readonly object frameLock = new();
    private BitmapSource? pendingFrame;
    private bool frameQueued, positioned, closing, toolbarVisible = true;
    private bool _captureExcluded;

    public bool IsCaptureSafe { get; private set; }
    public bool IsFixed => pin.IsChecked == true;
    public bool IsPreviewVisible { get; private set; } = true;
    public event EventHandler? PositionChanged;

    public void RestorePlacement(Rect bounds, bool fixedPosition, bool previewVisible)
    {
        Dispatcher.VerifyAccess();
        if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
            || bounds.X < int.MinValue || bounds.X > int.MaxValue - PixelWidth
            || bounds.Y < int.MinValue || bounds.Y > int.MaxValue - PixelHeight)
            throw new ArgumentException("摄像头窗口位置无效。", nameof(bounds));
        pin.IsChecked = fixedPosition;
        IsPreviewVisible = previewVisible; eye.IsChecked = previewVisible; UpdateEyeIcon();
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        if (previousDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var proposed = new NativeRect { Left = (int)bounds.X, Top = (int)bounds.Y,
                Right = (int)bounds.X + PixelWidth, Bottom = (int)bounds.Y + PixelHeight };
            ClampToScreen(ref proposed);
            if (!SetWindowPos(handle, new nint(-1), proposed.Left, proposed.Top, PixelWidth, PixelHeight, 0x0010 | 0x0020))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            positioned = true;
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    public CameraPreviewWindow()
    {
        Title = "DogSC摄像头预览";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = PixelWidth;
        Height = PixelHeight;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        layout.Children.Add(new Viewbox { Stretch = Stretch.UniformToFill, Child = picture });
        var surface = new FrameworkElementFactory(typeof(Grid));
        surface.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(presenter);
        pin.Template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = surface };
        pin.Content = lockIcon;
        AutomationProperties.SetAutomationId(pin, "PinButton");
        UpdateLockIcon();
        pin.HorizontalAlignment = HorizontalAlignment.Left;
        pin.VerticalAlignment = VerticalAlignment.Top;
        pin.Margin = new Thickness(8);
        pin.Checked += (_, _) => UpdateLockIcon();
        pin.Unchecked += (_, _) => UpdateLockIcon();
        layout.Children.Add(pin);
        eye.Template = pin.Template;
        eye.Content = eyeIcon;
        AutomationProperties.SetAutomationId(eye, "CameraVisibilityButton");
        eye.Checked += (_, _) => SetPreviewVisible(true);
        eye.Unchecked += (_, _) => SetPreviewVisible(false);
        UpdateEyeIcon();
        layout.Children.Add(eye);
        Content = layout;
        MouseLeftButtonDown += (_, e) =>
        {
            if (IsFixed || IsControl(e.OriginalSource as DependencyObject)) return;
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        };
        MouseRightButtonUp += (_, e) => { pin.IsChecked = !IsFixed; e.Handled = true; };
        LocationChanged += (_, _) => { if (positioned) PositionChanged?.Invoke(this, EventArgs.Empty); };
        Closing += (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            SetPreviewVisible(false);
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle).AddHook(WindowMessage);
            MakeCaptureSafe(handle);
            IsCaptureSafe = _captureExcluded;
        };
    }

    public void SetCaptureExcluded(bool excluded)
    {
        Dispatcher.VerifyAccess();
        _captureExcluded = excluded;
        foreach (var button in new[] { pin, eye })
        {
            if (excluded && button.ToolTip is ToolTip tip) tip.IsOpen = false;
            ToolTipService.SetIsEnabled(button, !excluded);
        }
        IsCaptureSafe = false;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0) MakeCaptureSafe(handle);
        if (visibilityControl is not null)
        {
            var controlHandle = new WindowInteropHelper(visibilityControl).Handle;
            if (controlHandle != 0) MakeCaptureSafe(controlHandle);
        }
        IsCaptureSafe = excluded && handle != 0;
    }

    public void ShowSafely()
    {
        Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (toolbarVisible && IsPreviewVisible && picture.Source is not null)
        {
            if (visibilityControl is not null) { visibilityControl.Hide(); visibilityControl.Content = null; }
            if (!layout.Children.Contains(eye)) layout.Children.Add(eye);
            eye.Margin = new Thickness(8);
            base.Show();
        }
        else base.Hide();
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        if (previousDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!GetWindowRect(handle, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
            int x = bounds.Left, y = bounds.Top;
            if (!positioned)
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                x = Math.Max(info.Work.Left, info.Work.Right - PixelWidth - 24);
                y = Math.Max(info.Work.Top, info.Work.Bottom - PixelHeight - 24);
            }
            var proposed = new NativeRect { Left = x, Top = y, Right = x + PixelWidth, Bottom = y + PixelHeight };
            ClampToScreen(ref proposed);
            x = proposed.Left; y = proposed.Top;
            if (!SetWindowPos(handle, new nint(-1), x, y, PixelWidth, PixelHeight, 0x0010 | 0x0020)
                || !GetWindowRect(handle, out bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (bounds.Right - bounds.Left != PixelWidth || bounds.Bottom - bounds.Top != PixelHeight)
                throw new InvalidOperationException("摄像头预览窗口未能设置为560×380物理像素。");
            MakeCaptureSafe(handle);
            IsCaptureSafe = _captureExcluded;
            positioned = true;
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
        if (toolbarVisible && !IsPreviewVisible) ShowVisibilityControl();
        else visibilityControl?.Hide();
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetToolbarVisible(bool visible)
    {
        Dispatcher.VerifyAccess();
        if (toolbarVisible == visible) return;
        toolbarVisible = visible;
        if (positioned) ShowSafely();
    }

    public void SetPreviewVisible(bool visible)
    {
        Dispatcher.VerifyAccess();
        if (IsPreviewVisible == visible) return;
        IsPreviewVisible = visible;
        eye.IsChecked = visible;
        UpdateEyeIcon();
        ShowSafely();
    }

    public new void Hide()
    {
        visibilityControl?.Hide();
        base.Hide();
    }

    private void UpdateEyeIcon()
    {
        eyeIcon.Data = Geometry.Parse(IsPreviewVisible
            ? "M 1,10 Q 10,1 19,10 Q 10,19 1,10 Z M 13,10 A 3,3 0 1 1 7,10 A 3,3 0 1 1 13,10"
            : "M 2,8 Q 10,16 18,8 M 4,10 L 2,13 M 10,12 L 10,15 M 16,10 L 18,13");
        eyeIcon.Stroke = IsPreviewVisible ? new SolidColorBrush(Color.FromRgb(170, 255, 214)) : Brushes.White;
        eyeIcon.Opacity = IsPreviewVisible ? 1 : .65;
        AutomationProperties.SetName(eye, IsPreviewVisible ? "隐藏摄像头窗口" : "显示摄像头窗口");
        var tip = eye.ToolTip as ToolTip ?? new ToolTip();
        tip.Content = IsPreviewVisible ? "隐藏预览；摄像头继续录制" : "显示摄像头预览";
        eye.ToolTip = tip;
    }

    private void ShowVisibilityControl()
    {
        // Keep only the eye clickable while the full preview is hidden; both windows share the capture policy.
        if (visibilityControl is null)
        {
            visibilityControl = new Window
            {
                Title = "DogSC摄像头显示开关", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Width = 32, Height = 32
            };
            visibilityControl.SourceInitialized += (_, _) => MakeCaptureSafe(new WindowInteropHelper(visibilityControl).Handle);
            visibilityControl.Closing += (_, e) => { if (!closing) { e.Cancel = true; SetPreviewVisible(true); } };
        }
        layout.Children.Remove(eye);
        eye.Margin = new Thickness(0);
        visibilityControl.Content = eye;
        var bounds = GetScreenBounds();
        var dpi = GetDpiForWindow(new WindowInteropHelper(this).Handle);
        if (dpi == 0) throw new InvalidOperationException("无法读取摄像头预览窗口DPI，窗口句柄已失效。");
        int size = (int)Math.Round(32 * dpi / 96d), margin = (int)Math.Round(8 * dpi / 96d);
        visibilityControl.Show();
        var handle = new WindowInteropHelper(visibilityControl).Handle;
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        if (previousDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!SetWindowPos(handle, new nint(-1), (int)bounds.Right - margin - size, (int)bounds.Top + margin,
                size, size, 0x0010 | 0x0020)) throw new Win32Exception(Marshal.GetLastWin32Error());
            MakeCaptureSafe(handle);
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    private void MakeCaptureSafe(nint handle)
    {
        uint desired = _captureExcluded ? 0x11u : 0;
        if (!SetWindowDisplayAffinity(handle, desired)
            || !GetWindowDisplayAffinity(handle, out uint affinity) || affinity != desired)
        {
            IsCaptureSafe = false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), _captureExcluded
                ? "无法将摄像头预览及控制按钮排除在屏幕采集中。" : "无法恢复摄像头预览及控制按钮的截图可见性。");
        }
    }

    private void UpdateLockIcon()
    {
        lockIcon.Data = Geometry.Parse(IsFixed
            ? "M 5,10 L 5,6 A 5,5 0 0 1 15,6 L 15,10 M 3,10 L 17,10 17,20 3,20 Z M 10,14 L 10,17"
            : "M 5,10 L 5,6 A 5,5 0 0 1 10,1 M 3,10 L 17,10 17,20 3,20 Z M 10,14 L 10,17");
        lockIcon.Stroke = IsFixed ? new SolidColorBrush(Color.FromRgb(170, 255, 214)) : Brushes.White;
        AutomationProperties.SetName(pin, IsFixed ? "解锁摄像头窗口" : "固定摄像头窗口");
        var tip = pin.ToolTip as ToolTip ?? new ToolTip();
        tip.Content = IsFixed ? "已锁定；点击或右键解锁" : "未锁定；点击或右键固定";
        pin.ToolTip = tip;
    }

    private static void ClampToScreen(ref NativeRect bounds)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromRect(ref bounds, 2), ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        int x = Math.Max(info.Work.Left, Math.Min(bounds.Left, info.Work.Right - width));
        int y = Math.Max(info.Work.Top, Math.Min(bounds.Top, info.Work.Bottom - height));
        bounds = new NativeRect { Left = x, Top = y, Right = x + width, Bottom = y + height };
    }

    private nint WindowMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0216) // WM_MOVING clamps the proposed rectangle before native dragging displays it.
        {
            var bounds = Marshal.PtrToStructure<NativeRect>(lParam);
            ClampToScreen(ref bounds);
            Marshal.StructureToPtr(bounds, lParam, false);
            handled = true;
            return 1;
        }
        if (message == 0x0046 && positioned) // Also contain programmatic moves and restored positions.
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            if ((position.Flags & 0x0002) == 0)
            {
                var bounds = new NativeRect { Left = position.X, Top = position.Y,
                    Right = position.X + PixelWidth, Bottom = position.Y + PixelHeight };
                ClampToScreen(ref bounds);
                position.X = bounds.Left; position.Y = bounds.Top;
                Marshal.StructureToPtr(position, lParam, false);
            }
        }
        if (message == 0x007e && (IsVisible || visibilityControl?.IsVisible == true) && !closing)
            Dispatcher.BeginInvoke(new Action(ShowSafely));
        return 0;
    }

    public System.Windows.Rect GetScreenBounds()
    {
        Dispatcher.VerifyAccess();
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        if (previousDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!GetWindowRect(new WindowInteropHelper(this).Handle, out var bounds))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new System.Windows.Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    public void ShowFrame(BitmapSource frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.IsFrozen) throw new ArgumentException("摄像头帧必须冻结后跨线程传递。", nameof(frame));
        lock (frameLock)
        {
            if (closing) return;
            pendingFrame = frame;
            if (frameQueued) return;
            frameQueued = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            BitmapSource? newest;
            lock (frameLock) { newest = pendingFrame; pendingFrame = null; frameQueued = false; }
            if (!closing)
            {
                bool firstFrame = picture.Source is null;
                picture.Source = newest;
                if (firstFrame && newest is not null) ShowSafely();
            }
        }));
    }

    public void ClearFrame()
    {
        Dispatcher.VerifyAccess();
        lock (frameLock) pendingFrame = null;
        picture.Source = null;
        Hide();
    }

    public void CloseWindow()
    {
        Dispatcher.VerifyAccess();
        lock (frameLock) { closing = true; pendingFrame = null; }
        IsCaptureSafe = false;
        picture.Source = null;
        visibilityControl?.Close();
        Close();
    }

    private static bool IsControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public nint Window, After; public int X, Y, Width, Height; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect bounds, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
