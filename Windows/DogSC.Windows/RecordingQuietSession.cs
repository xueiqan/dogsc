using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace DogSC.Windows;

public sealed class RecordingQuietSession : IAsyncDisposable
{
    // ponytail: Win11 Settings UI is not a stable OS setter; use an official setter when available.
    private const string ToggleId = "SystemSettings_Notifications_QuietHours_MuteNotification_Enabled_ToggleSwitch";
    private const int Minimized = 6;
    private const int Maximized = 3;
    private const int Normal = 9;
    private readonly object _gate = new();
    private bool _changed;
    private Task? _restoration;

    private RecordingQuietSession() { }

    public static Task<RecordingQuietSession> StartAsync() => Task.Run(() =>
    {
        var session = new RecordingQuietSession();
        try
        {
            WithNotificationSettings(pattern =>
            {
                var state = pattern.Current.ToggleState;
                if (state == ToggleState.On) return;
                if (state != ToggleState.Off)
                    throw new InvalidOperationException("Windows请勿打扰开关的状态无法确定。");
                session._changed = true;
                pattern.Toggle();
                WaitForState(pattern, ToggleState.On);
            });
            return session;
        }
        catch (Exception startError)
        {
            try { session.Restore(); }
            catch (Exception restoreError)
            {
                throw new AggregateException($"开启录制免打扰失败：{startError.Message}；恢复也失败：{restoreError.Message}。请到系统通知设置检查。",
                    startError, restoreError);
            }
            throw new InvalidOperationException($"无法开启Windows录制免打扰，已停止开始录制：{startError.Message}", startError);
        }
    });

    public ValueTask DisposeAsync()
    {
        lock (_gate)
            return new ValueTask(_restoration ??= Task.Run(Restore));
    }

    private void Restore()
    {
        if (!_changed) return;
        WithNotificationSettings(pattern =>
        {
            var state = pattern.Current.ToggleState;
            if (state == ToggleState.Off) return;
            if (state != ToggleState.On)
                throw new InvalidOperationException("无法恢复Windows请勿打扰：开关状态无法确定。");
            pattern.Toggle();
            WaitForState(pattern, ToggleState.Off);
        });
        _changed = false;
    }

    private static void WithNotificationSettings(Action<TogglePattern> action)
    {
        var foreground = GetForegroundWindow();
        var before = SettingsWindows().ToDictionary(window => window, WindowState);
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                foreach (var window in SettingsWindows())
                {
                    var root = AutomationElement.FromHandle(window);
                    var controls = root.FindAll(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.AutomationIdProperty, ToggleId),
                        new PropertyCondition(AutomationElement.IsTogglePatternAvailableProperty, true)));
                    if (controls.Count == 0) continue;
                    if (controls.Count != 1 || !controls[0].Current.IsEnabled)
                        throw new InvalidOperationException("Windows通知设置中的请勿打扰开关不唯一或不可用。");
                    GetWindowThreadProcessId(window, out var pid);
                    if (controls[0].Current.ProcessId != pid)
                        throw new InvalidOperationException("请勿打扰开关不属于已确认的Windows设置进程。");
                    action((TogglePattern)controls[0].GetCurrentPattern(TogglePattern.Pattern));
                    return;
                }
                Thread.Sleep(100);
            }
            throw new TimeoutException("5秒内未找到Windows通知设置的请勿打扰开关。");
        }
        finally
        {
            var windows = SettingsWindows();
            var settingsWasForeground = windows.Contains(GetForegroundWindow());
            foreach (var window in windows)
            {
                var state = before.GetValueOrDefault(window, Minimized);
                if (WindowState(window) == state) continue;
                ShowWindowAsync(window, state);
                var timer = Stopwatch.StartNew();
                while (IsWindow(window) && WindowState(window) != state
                    && timer.Elapsed < TimeSpan.FromSeconds(1)) Thread.Sleep(20);
                if (IsWindow(window) && WindowState(window) != state)
                    throw new InvalidOperationException("Windows通知设置窗口未能恢复原状态，请检查系统设置窗口。");
            }
            if (settingsWasForeground && IsWindow(foreground)
                && GetForegroundWindow() != foreground && !SetForegroundWindow(foreground))
                throw new InvalidOperationException("无法恢复操作前的前景窗口。");
        }
    }

    private static List<nint> SettingsWindows()
    {
        var expectedPath = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "ImmersiveControlPanel", "SystemSettings.exe"));
        var windows = new List<nint>();
        foreach (var process in Process.GetProcessesByName("SystemSettings"))
        {
            using (process)
            {
                string? actualPath;
                nint window;
                try { actualPath = process.MainModule?.FileName; window = process.MainWindowHandle; }
                catch (InvalidOperationException) { continue; }
                if (actualPath is null || !Path.GetFullPath(actualPath).Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("无法确认设置进程来自Windows系统目录，已停止操作请勿打扰。");
                if (window != 0) windows.Add(window);
            }
        }
        return windows;
    }

    private static int WindowState(nint window) => IsIconic(window) ? Minimized : IsZoomed(window) ? Maximized : Normal;

    private static void WaitForState(TogglePattern pattern, ToggleState desired)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (pattern.Current.ToggleState == desired) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException($"Windows请勿打扰未切换到{desired}，请检查系统通知设置。");
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
