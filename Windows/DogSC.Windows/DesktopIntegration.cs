using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DogSC.Windows;

public sealed class DesktopIntegration : IDisposable
{
    private const int ShowToolbarId = 0x4D01;
    private const int PauseOrResumeId = 0x4D02;
    private const int MarkerId = 0x4D03;
    private const int HotKeyMessage = 0x0312;
    private const uint ControlShiftNoRepeat = 0x0002 | 0x0004 | 0x4000;
    private readonly Dispatcher _dispatcher;
    private readonly HwndSource _source;
    private readonly nint _handle;
    private readonly Action _showToolbar;
    private readonly Action _toggleToolbar;
    private readonly Action _pauseOrResume;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _menu;
    private System.Drawing.Icon? _trayIcon;
    private bool _showRegistered;
    private bool _pauseRegistered;
    private bool _disposed;
    private readonly Action? _addMarker;
    private bool _markerRegistered;
    private bool _markerRequested;
    private Forms.ToolStripItem? _markerMenu;

    public DesktopIntegration(Window window, Action showToolbar, Action pauseOrResume,
        Action showSettings, Action openFolder, Action exit, Action? addMarker = null, Action? showMemo = null, Action? toggleToolbar = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(showToolbar);
        ArgumentNullException.ThrowIfNull(pauseOrResume);
        ArgumentNullException.ThrowIfNull(showSettings);
        ArgumentNullException.ThrowIfNull(openFolder);
        ArgumentNullException.ThrowIfNull(exit);
        _dispatcher = window.Dispatcher;
        _dispatcher.VerifyAccess();
        _showToolbar = showToolbar;
        _toggleToolbar = toggleToolbar ?? showToolbar;
        _pauseOrResume = pauseOrResume;
        _addMarker = addMarker;
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle)
            ?? throw new InvalidOperationException("无法建立DogSC的桌面消息窗口。");
        try
        {
            _source.AddHook(WindowMessage);
            Register(ShowToolbarId, 0x53, "Ctrl+Shift+S");
            _showRegistered = true;
            Register(PauseOrResumeId, 0x20, "Ctrl+Shift+空格");
            _pauseRegistered = true;
            _menu = new Forms.ContextMenuStrip();
            _menu.Opened += (_, _) =>
            {
                if (!ScreenRecorderLib.Recorder.SetExcludeFromCapture(_menu.Handle, true)) _menu.Close();
            };
            AddMenuItem("呼出录制悬浮条 (Ctrl+Shift+S)", showToolbar);
            AddMenuItem("暂停 / 继续 (Ctrl+Shift+空格)", pauseOrResume);
            if (addMarker is not null)
            {
                _markerMenu = _menu.Items.Add("添加录制标记 (Ctrl+Alt+M)", null, (_, _) => Queue(addMarker));
                _markerMenu.Enabled = false;
            }
            if (showMemo is not null) AddMenuItem("备忘录", showMemo);
            _menu.Items.Add(new Forms.ToolStripSeparator());
            AddMenuItem("录制设置", showSettings);
            AddMenuItem("打开保存目录", openFolder);
            _menu.Items.Add(new Forms.ToolStripSeparator());
            AddMenuItem("退出DogSC", exit);
            using (var resource = Application.GetResourceStream(new Uri("pack://application:,,,/DogSC.Windows;component/Assets/DogSC.ico")).Stream)
            using (var icon = new System.Drawing.Icon(resource, 32, 32))
                _trayIcon = (System.Drawing.Icon)icon.Clone();
            _tray = new Forms.NotifyIcon
            {
                Icon = _trayIcon,
                Text = "DogSC · Ctrl+Shift+S呼出录制条",
                ContextMenuStrip = _menu
            };
            _tray.DoubleClick += (_, _) => Queue(showSettings);
            _tray.Visible = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Register(int id, uint key, string shortcut)
    {
        if (!RegisterHotKey(_handle, id, ControlShiftNoRepeat, key))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"无法注册全局快捷键{shortcut}。该组合可能已被其他程序占用，请释放它后重新启动DogSC。");
    }

    private void AddMenuItem(string text, Action action) =>
        _menu!.Items.Add(text, null, (_, _) => Queue(action));

    public void SetMarkerRegistration(bool active)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _addMarker is null || _markerRequested == active) return;
        _markerRequested = active;
        if (!active)
        {
            if (_markerRegistered) UnregisterHotKey(_handle, MarkerId);
            _markerRegistered = false;
        }
        else _markerRegistered = RegisterHotKey(_handle, MarkerId, 0x0002 | 0x0001 | 0x4000, 0x4D);
        _markerMenu!.Text = active && !_markerRegistered ? "添加录制标记（快捷键被占用）" : "添加录制标记 (Ctrl+Alt+M)";
        _markerMenu.Enabled = active;
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != HotKeyMessage || _disposed) return 0;
        if (wParam == ShowToolbarId)
            Queue(_toggleToolbar);
        else if (wParam == PauseOrResumeId)
            Queue(_pauseOrResume);
        else if (wParam == MarkerId && _markerRegistered && _addMarker is not null)
            Queue(_addMarker);
        else
            return 0;
        handled = true;
        return 0;
    }

    private void Queue(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!_disposed) action();
        }));
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        if (_showRegistered) UnregisterHotKey(_handle, ShowToolbarId);
        if (_pauseRegistered) UnregisterHotKey(_handle, PauseOrResumeId);
        if (_markerRegistered) UnregisterHotKey(_handle, MarkerId);
        if (!_source.IsDisposed) _source.RemoveHook(WindowMessage);
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _menu?.Dispose();
        _trayIcon?.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
