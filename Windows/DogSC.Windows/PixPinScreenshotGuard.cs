using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace DogSC.Windows;

public sealed class PixPinScreenshotGuard : IDisposable
{
    private const nuint ReplayMarker = 0x444f4753;
    private readonly Dispatcher dispatcher;
    private readonly Func<bool> recordingActive;
    private readonly Func<Task<bool>> prepareScreenshot;
    private readonly Func<Task> resumeScreenshot;
    private readonly Action<Exception> failed;
    private readonly HookCallback callback;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer refreshTimer;
    private nint hook;
    private volatile int[] pixPinIds = [];
    private volatile bool printEnabled, copyEnabled;
    private bool disposed, busy, triggerHeld;
    private uint swallowedKey;
    private int resumeGeneration;
    private long candidateCalls, candidateDownCalls, acceptedCalls, rejectedCalls, replayCalls, refreshCount;
    private string lastCandidate = "None", lastDisposition = "None", lastRefresh = "NotStarted";
    private int lastModifiers;
    private bool lastRecordingActive;
    private DateTime? refreshCompletedUtc;

    public bool IsStarted => hook != 0 && !disposed;

    // Diagnostics contain only the two screenshot shortcuts, never ordinary typed characters.
    public sealed record GuardDiagnostic(bool IsStarted, bool PrintEnabled, bool CopyEnabled, int[] PixPinIds,
        bool Busy, bool TriggerHeld, uint SwallowedKey, int ResumeGeneration,
        long CandidateCalls, long CandidateDownCalls, long AcceptedCalls, long RejectedCalls, long ReplayCalls,
        string LastCandidate, string LastDisposition, int LastModifiers, bool LastRecordingActive,
        long RefreshCount, string LastRefresh, DateTime? RefreshCompletedUtc);

    public GuardDiagnostic Snapshot()
    {
        dispatcher.VerifyAccess();
        return new(IsStarted, printEnabled, copyEnabled, (int[])pixPinIds.Clone(), busy, triggerHeld,
            swallowedKey, resumeGeneration, candidateCalls, candidateDownCalls, acceptedCalls, rejectedCalls,
            replayCalls, lastCandidate, lastDisposition, lastModifiers, lastRecordingActive,
            refreshCount, lastRefresh, refreshCompletedUtc);
    }

    public PixPinScreenshotGuard(Dispatcher dispatcher, Func<bool> recordingActive,
        Func<Task<bool>> prepareScreenshot, Func<Task> resumeScreenshot, Action<Exception> failed)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.recordingActive = recordingActive ?? throw new ArgumentNullException(nameof(recordingActive));
        this.prepareScreenshot = prepareScreenshot ?? throw new ArgumentNullException(nameof(prepareScreenshot));
        this.resumeScreenshot = resumeScreenshot ?? throw new ArgumentNullException(nameof(resumeScreenshot));
        this.failed = failed ?? throw new ArgumentNullException(nameof(failed));
        callback = KeyboardHook;
        refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => Refresh(), dispatcher);
        refreshTimer.Stop();
    }

    public void Start()
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (hook != 0) return;
        Refresh();
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法建立截图前暂停保护，请先手动暂停再截图。");
        refreshTimer.Start();
    }

    // shortcut: only these verified PixPin actions are guarded; custom shortcuts and tray actions require manual pause.
    private void Refresh()
    {
        int[] nextIds = [];
        bool nextPrint = false, nextCopy = false;
        string result = "NoPixPin";
        try
        {
            nextIds = RunningPixPinIds();
            if (nextIds.Length == 0) return;
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixPin", "Config");
            string path = Path.Combine(folder, "PixPinConfig.json");
            result = "InvalidConfigPath";
            MediaExport.RejectLinks(path);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 1024 * 1024) return;
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var config = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 12 });
            nextPrint = HasAction(config.RootElement, "Action.Screenshot#s.win", "Print", "pixpin.screenShotAndEdit()");
            nextCopy = HasAction(config.RootElement, "Action.Screenshot and copy#s.win", "Ctrl+Alt+A", "pixpin.screenShot(ShotAction.Copy)");
            result = "ReadComplete";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A changing or unreadable PixPin config cannot authorize intercepting its keys.
            nextPrint = nextCopy = false;
            result = error.GetType().Name;
        }
        finally
        {
            // Keep the previous verified state while native process queries or file reads are in progress.
            // These assignments run together on the dispatcher, with no calls that can pump native messages.
            pixPinIds = nextIds;
            printEnabled = nextPrint;
            copyEnabled = nextCopy;
            refreshCount++;
            lastRefresh = result;
            refreshCompletedUtc = DateTime.UtcNow;
        }
    }

    private static bool HasAction(JsonElement root, string name, string shortcut, string script) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var action)
        && action.ValueKind == JsonValueKind.Object && action.TryGetProperty("v", out var value)
        && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("shortCut", out var key)
        && key.ValueKind == JsonValueKind.String && string.Equals(key.GetString(), shortcut, StringComparison.OrdinalIgnoreCase)
        && value.TryGetProperty("script", out var body) && body.ValueKind == JsonValueKind.String && body.GetString() == script;

    private static bool IsScreenshotKey(uint key, bool control, bool alt, bool shift, bool windows,
        bool printConfigured, bool copyConfigured) => !shift && !windows
        && (key == 0x2c && !control && !alt && printConfigured
            || key == 0x41 && control && alt && copyConfigured);

    private nint KeyboardHook(int code, nint message, nint data)
    {
        if (code < 0 || disposed) return CallNextHookEx(hook, code, message, data);
        uint key = 0;
        bool candidate = false;
        try
        {
            var input = Marshal.PtrToStructure<KeyboardData>(data);
            if (input.Extra == ReplayMarker)
            {
                if (input.Key == 0x2c || input.Key == 0x41) replayCalls++;
                return CallNextHookEx(hook, code, message, data);
            }
            key = input.Key;
            bool down = message == 0x100 || message == 0x104;
            bool up = message == 0x101 || message == 0x105;
            if (!down && !up) return CallNextHookEx(hook, code, message, data);
            bool control = false, alt = false, shift = false, windows = false;
            if (key is 0x2c or 0x41)
            {
                control = Pressed(0x11); alt = Pressed(0x12); shift = Pressed(0x10);
                windows = Pressed(0x5b) || Pressed(0x5c);
            }
            candidate = key == 0x2c || key == 0x41 && (control && alt || swallowedKey == key);
            if (candidate)
            {
                candidateCalls++;
                if (down)
                {
                    candidateDownCalls++;
                    lastCandidate = key == 0x2c ? "Print" : "CtrlAltA";
                    lastModifiers = (control ? 1 : 0) | (alt ? 2 : 0) | (shift ? 4 : 0) | (windows ? 8 : 0);
                }
            }
            if (swallowedKey == key)
            {
                if (candidate && down) lastDisposition = "SwallowedRepeat";
                if (up) { triggerHeld = false; if (!busy) swallowedKey = 0; }
                return 1;
            }
            if (!candidate || !down) return CallNextHookEx(hook, code, message, data);
            lastRecordingActive = false;
            string? rejection = busy ? "Busy" : pixPinIds.Length == 0 ? "NoPixPin" : null;
            if (rejection == null && !(lastRecordingActive = recordingActive())) rejection = "RecordingInactive";
            if (rejection == null && (key == 0x2c ? !printEnabled : !copyEnabled)) rejection = "ShortcutNotConfigured";
            if (rejection == null && !IsScreenshotKey(key, control, alt, shift, windows, printEnabled, copyEnabled))
                rejection = "ModifiersMismatch";
            if (rejection != null)
            {
                rejectedCalls++;
                lastDisposition = rejection;
                return CallNextHookEx(hook, code, message, data);
            }
            acceptedCalls++;
            lastDisposition = "Accepted";
            busy = triggerHeld = true;
            swallowedKey = key;
            dispatcher.BeginInvoke(new Action(() => _ = ScreenshotAsync(key)));
            return 1;
        }
        catch (Exception error)
        {
            if (candidate) { rejectedCalls++; lastDisposition = "HookException"; }
            busy = false;
            if (!triggerHeld) swallowedKey = 0;
            Report(error);
            return key != 0 && swallowedKey == key ? 1 : CallNextHookEx(hook, code, message, data);
        }
    }

    private async Task ScreenshotAsync(uint key)
    {
        int generation = resumeGeneration;
        try
        {
            if (disposed || !await prepareScreenshot())
                throw new InvalidOperationException("截图准备未完成，未回放截图快捷键；请确认录制已暂停。");
            if (disposed || generation != resumeGeneration) return;
            int[] processIds = RunningPixPinIds();
            if (processIds.Length == 0) throw new InvalidOperationException("PixPin已退出，录制保持暂停。");
            if (await WatchScreenshotCloseAsync(generation, processIds, () => Replay(key))
                && !disposed && generation == resumeGeneration) await resumeScreenshot();
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error) { Report(error); }
        finally
        {
            busy = false;
            if (!triggerHeld) swallowedKey = 0;
        }
    }

    private Task<bool> WaitForScreenshotCloseAsync(int generation, int[] processIds) =>
        WatchScreenshotCloseAsync(generation, processIds, null);

    private async Task<bool> WatchScreenshotCloseAsync(int generation, int[] processIds, Action? trigger)
    {
        bool eventSeen = false;
        var shownWindows = new HashSet<nint>();
        WinEventCallback windowShown = (_, eventId, window, objectId, childId, _, _) =>
        {
            try
            {
                // SHOW can be delivered after HIDE; inspect the verified window without requiring current visibility.
                if (objectId != 0 || childId != 0) return;
                if (eventId == 0x8002 && IsScreenshotWindow(window, processIds, false, false)) shownWindows.Add(window);
                if (eventId is 0x8002 or 0x800b && shownWindows.Contains(window)
                    && IsScreenshotWindow(window, processIds, false)) eventSeen = true;
            }
            catch { }
        };
        var windowHooks = new List<nint>();
        var callbackRoot = GCHandle.Alloc(windowShown);
        try
        {
            foreach (int processId in processIds)
            {
                nint windowHook = SetWinEventHook(0x8002, 0x800b, 0, windowShown, (uint)processId, 0, 0);
                if (windowHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监视PixPin截图窗口，录制保持暂停。");
                windowHooks.Add(windowHook);
            }
            if (disposed || generation != resumeGeneration) return false;
            trigger?.Invoke();
            var waiting = Stopwatch.StartNew();
            bool seen = false;
            int hiddenSamples = 0;
            while (!disposed && generation == resumeGeneration)
            {
                bool visible = ScreenshotVisible(processIds);
                seen |= eventSeen;
                if (visible) { seen = true; hiddenSamples = 0; }
                else if (seen && ++hiddenSamples >= 3) return true;
                else if (!seen && waiting.Elapsed >= TimeSpan.FromSeconds(5))
                    throw new TimeoutException("未确认PixPin截图界面已打开，录制保持暂停；关闭截图界面后可手动继续。");
                await Task.Delay(100, lifetime.Token);
            }
            return false;
        }
        finally
        {
            bool removed = true;
            foreach (nint windowHook in windowHooks)
                if (!UnhookWinEvent(windowHook))
                {
                    removed = false;
                    Report(new Win32Exception(Marshal.GetLastWin32Error(), "截图窗口监视挂钩解除失败。"));
                }
            // A native hook that failed to detach must retain its delegate until process exit.
            if (removed) callbackRoot.Free();
            GC.KeepAlive(windowShown);
        }
    }

    private static void Replay(uint key)
    {
        var inputs = new List<Input>();
        bool addControl = key == 0x41 && !Pressed(0x11), addAlt = key == 0x41 && !Pressed(0x12);
        if (addControl) inputs.Add(Key(0x11));
        if (addAlt) inputs.Add(Key(0x12));
        inputs.Add(Key((ushort)key, key == 0x2c ? 1u : 0));
        inputs.Add(Key((ushort)key, (key == 0x2c ? 1u : 0) | 2));
        if (addAlt) inputs.Add(Key(0x12, 2));
        if (addControl) inputs.Add(Key(0x11, 2));
        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        if (sent == inputs.Count) return;
        int error = Marshal.GetLastWin32Error();
        // Release only modifiers introduced by this replay, including after a partial SendInput.
        var release = new List<Input>();
        int modifiers = (addControl ? 1 : 0) + (addAlt ? 1 : 0);
        if (sent > modifiers) release.Add(Key((ushort)key, (key == 0x2c ? 1u : 0) | 2));
        if (addAlt && sent > (addControl ? 1 : 0)) release.Add(Key(0x12, 2));
        if (addControl && sent > 0) release.Add(Key(0x11, 2));
        if (release.Count > 0) SendInput((uint)release.Count, release.ToArray(), Marshal.SizeOf<Input>());
        throw new Win32Exception(error, "无法回放PixPin截图快捷键，录制保持暂停。");
    }

    public void CancelAutoResume()
    {
        dispatcher.VerifyAccess();
        resumeGeneration++;
    }

    public static bool IsScreenshotVisible() => ScreenshotVisible(RunningPixPinIds());

    private static bool ScreenshotVisible(int[] processIds)
    {
        if (processIds.Length == 0) return false;
        bool found = false;
        EnumWindows((window, _) =>
        {
            try
            {
                if (!IsScreenshotWindow(window, processIds, true)) return true;
                found = true;
                return false;
            }
            catch { return true; }
        }, 0);
        return found;
    }

    private static bool IsScreenshotWindow(nint window, int[] processIds, bool requireVisible, bool requireFullScreen = true)
    {
        if (window == 0 || requireVisible && !IsWindowVisible(window) || GetAncestor(window, 2) != window) return false;
        GetWindowThreadProcessId(window, out uint processId);
        if (!processIds.Contains((int)processId) || !GetWindowRect(window, out var bounds)) return false;
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor)
            && (requireFullScreen ? IsOverlay(name.ToString(), bounds, monitor.Monitor) : IsQtWindow(name.ToString()));
    }

    // shortcut: a full-screen PixPin image pin is conservatively treated as screenshot UI; shrink it before resuming.
    private static bool IsOverlay(string className, Rect bounds, Rect monitor) =>
        IsQtWindow(className) && bounds.Right - bounds.Left > 100 && bounds.Bottom - bounds.Top > 100
        && bounds.Left <= monitor.Left + 4 && bounds.Top <= monitor.Top + 4
        && bounds.Right >= monitor.Right - 4 && bounds.Bottom >= monitor.Bottom - 4;

    private static bool IsQtWindow(string className) =>
        className.StartsWith("Qt", StringComparison.Ordinal) && className.Contains("QWindow", StringComparison.Ordinal)
        && !className.Contains("Tray", StringComparison.Ordinal);

    private static int[] RunningPixPinIds()
    {
        var result = new List<int>();
        foreach (var process in Process.GetProcessesByName("PixPin"))
        {
            using (process)
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path is not null && string.Equals(Path.GetFileName(path), "PixPin.exe", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(path)) result.Add(process.Id);
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return result.ToArray();
    }

    private void Report(Exception error)
    {
        try
        {
            if (dispatcher.CheckAccess()) failed(error);
            else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => { try { failed(error); } catch { } }));
        }
        catch { }
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        resumeGeneration++;
        refreshTimer.Stop();
        lifetime.Cancel();
        if (hook != 0 && !UnhookWindowsHookEx(hook)) Report(new Win32Exception(Marshal.GetLastWin32Error(), "截图保护挂钩解除失败。"));
        hook = 0;
        swallowedKey = 0;
        lifetime.Dispose();
        GC.KeepAlive(callback);
    }

    private static bool Pressed(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private static Input Key(ushort key, uint flags = 0) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = flags, Extra = ReplayMarker } } };
    private delegate nint HookCallback(int code, nint message, nint data);
    private delegate bool WindowCallback(nint window, nint argument);
    private delegate void WinEventCallback(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowCallback callback, nint argument);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
