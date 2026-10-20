using System.ComponentModel;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using DogSC.Core;

namespace DogSC.Windows;

// Only the active recording interval is observed; no input or text is collected outside it.
public sealed class RecordingEvents : IAsyncDisposable
{
    private readonly RecordingSession session;
    private readonly CaptureTarget target;
    private readonly CaptureRegion? region;
    private readonly Channel<object> pending = Channel.CreateBounded<object>(new BoundedChannelOptions(8192)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writer;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly Hook callback;
    private readonly string folder;
    private readonly string package;
    private readonly bool embeddedCursor;
    private readonly double sampleInterval;
    private readonly int recordingHeight;
    private nint cursorHandle;
    private bool cursorVisible;
    private bool cursorKnown;
    private uint threadId;
    private long overflow;
    private int disposing;
    private int incomplete;
    private readonly ConcurrentQueue<string> warnings = new();
    private long inactiveOrBusySamplesSkipped;
    private long cursorTimerTicks;
    private double lastMove = -1;
    private double lastMarker = double.NegativeInfinity;

    public RecordingEvents(RecordingSession session, CaptureTarget target, CaptureRegion? region, string package,
        int frameRate = 120, bool embeddedCursor = true, int recordingHeight = 0)
    {
        this.session = session; this.target = target; this.region = region;
        if (frameRate is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(frameRate));
        if (recordingHeight is not (0 or 720 or 1080 or 1440)) throw new ArgumentOutOfRangeException(nameof(recordingHeight));
        this.recordingHeight = recordingHeight;
        this.package = Path.GetFullPath(package); this.embeddedCursor = embeddedCursor; sampleInterval = 1d / frameRate;
        folder = Path.Combine(Path.GetFullPath(package), "events");
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("鼠标事件保存目录不能经过链接。");
        Directory.CreateDirectory(folder);
        var stream = new FileStream(Path.Combine(folder, "pointer.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        FileStream markers;
        try { markers = new FileStream(Path.Combine(folder, "markers.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch { stream.Dispose(); throw; }
        writer = WriteAsync(stream, markers);
        _ = writer.ContinueWith(failed =>
        {
            Warn($"鼠标事件写入失败，本场已停用自动运镜；屏幕和声音继续录制：{failed.Exception?.GetBaseException().Message}");
            pending.Writer.TryComplete(failed.Exception?.GetBaseException());
            while (pending.Reader.TryRead(out var remaining))
                if (remaining is PointerEntry pointer) pointer.Snapshot?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        callback = OnMouse;
        thread = new Thread(Run) { IsBackground = true, Name = "DogSC pointer events" };
        thread.Start();
    }

    public Task Ready => ready.Task;
    public event Action<string>? Warning;
    public string[] Warnings => warnings.ToArray();
    public bool Incomplete => Volatile.Read(ref incomplete) != 0;

    private void Warn(string message)
    {
        if (!warnings.Contains(message)) warnings.Enqueue(message);
        if (Interlocked.Exchange(ref incomplete, 1) != 0) return;
        // A mouse hook must not wait for the UI or a subscriber.
        _ = Task.Run(() =>
        {
            foreach (Action<string> subscriber in Warning?.GetInvocationList() ?? [])
                try { subscriber(message); } catch { /* Diagnostic listeners cannot stop recording. */ }
        });
    }
    public void Mark()
    {
        if (TryGetTime(out double time) && time - lastMarker >= .3)
        {
            Enqueue(new Marker(time, "marker"));
            lastMarker = time;
        }
    }

    private bool TryGetTime(out double time)
    {
        time = 0;
        if (Volatile.Read(ref disposing) != 0 || Incomplete) return false;
        if (session.TryGetRecordingTime(out time)) return true;
        Interlocked.Increment(ref inactiveOrBusySamplesSkipped);
        return false;
    }

    private bool Enqueue(object entry)
    {
        if (Incomplete) return false;
        if (pending.Writer.TryWrite(entry)) return true;
        Interlocked.Increment(ref overflow);
        Warn("鼠标事件队列已满或不可用，本场已停用自动运镜；屏幕和声音继续录制，已有事件保留。");
        return false;
    }

    private void Run()
    {
        threadId = GetCurrentThreadId();
        PeekMessage(out _, 0, 0, 0, 0);
        nint hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (hook == 0) { ready.TrySetException(new Win32Exception(Marshal.GetLastWin32Error(), "无法建立鼠标事件记录。")); return; }
        ready.TrySetResult();
        // The native capture already records cursor appearance; only independent cursor tracks need polling.
        nuint timer = embeddedCursor ? 0 : SetTimer(0, 1, (uint)Math.Max(8, Math.Round(sampleInterval * 1000)), 0);
        try
        {
            int result;
            while ((result = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                if (message.Id == 0x113) Interlocked.Increment(ref cursorTimerTicks);
                if (message.Id == 0x113 && TryGetTime(out double time))
                {
                    var cursor = CursorCaptureAssets.Peek();
                    if (cursor is not null && (!cursorKnown || cursor.CursorHandle != cursorHandle || cursor.Visible != cursorVisible))
                        RecordPointer(time, "move", cursor.ScreenX, cursor.ScreenY);
                }
                TranslateMessage(ref message); DispatchMessage(ref message);
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception error) { pending.Writer.TryComplete(error); }
        finally { if (timer != 0) KillTimer(0, timer); UnhookWindowsHookEx(hook); }
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && TryGetTime(out double time))
            {
                string? kind = (int)message switch { 0x200 => "move", 0x201 => "leftClick", 0x204 => "rightClick", _ => null };
                if (kind is not null && (kind != "move" || time - lastMove >= sampleInterval))
                {
                    if (kind == "move") lastMove = time;
                    var mouse = Marshal.PtrToStructure<MouseData>(data);
                    RecordPointer(time, kind, mouse.Position.X, mouse.Position.Y);
                }
            }
        }
        catch (Exception error) { pending.Writer.TryComplete(error); }
        return CallNextHookEx(0, code, message, data);
    }

    private sealed record Marker(double time, string kind);
    private sealed record PointerEntry(double Time, string Kind, double X, double Y, bool Outside,
        bool CursorChanged, bool Visible, NativeCursorSnapshot? Snapshot);

    private void RecordPointer(double time, string kind, int screenX, int screenY)
    {
        int left = target.Left, top = target.Top, width = target.Width, height = target.Height;
        if (target.IsWindow)
        {
            if (DwmGetWindowAttribute(target.WindowHandle, 9, out Bounds bounds, Marshal.SizeOf<Bounds>()) != 0) return;
            left = bounds.Left; top = bounds.Top; width = bounds.Right - left; height = bounds.Bottom - top;
        }
        if (region is not null) { left += region.X; top += region.Y; width = region.Width; height = region.Height; }
        if (width <= 0 || height <= 0) return;
        var cursor = CursorCaptureAssets.Peek();
        bool visible = cursor?.Visible == true;
        nint handle = cursor?.CursorHandle ?? 0;
        bool changed = !cursorKnown || cursorHandle != handle || cursorVisible != visible;
        NativeCursorSnapshot? snapshot = null;
        if (changed && visible && !embeddedCursor) snapshot = CursorCaptureAssets.Snapshot();
        double x = (screenX - left) / (double)width, y = (screenY - top) / (double)height;
        bool outside = x < 0 || x > 1 || y < 0 || y > 1;
        if (recordingHeight != 0)
        {
            double outputWidth = recordingHeight * 16 / 9;
            double scale = Math.Min(outputWidth / width, recordingHeight / (double)height);
            x = .5 + (x - .5) * width * scale / outputWidth;
            y = .5 + (y - .5) * height * scale / recordingHeight;
        }
        var entry = new PointerEntry(time, kind, x, y, outside,
            changed, visible, snapshot);
        if (!Enqueue(entry)) { snapshot?.Dispose(); return; }
        cursorHandle = handle; cursorVisible = visible; cursorKnown = true;
    }

    private async Task WriteAsync(FileStream stream, FileStream markers)
    {
        await using (stream)
        await using (markers)
        await using (var text = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 65536, leaveOpen: true))
        await using (var marks = new StreamWriter(markers, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true))
        {
            var assets = new Dictionary<string, CursorAssetRecord>(StringComparer.Ordinal);
            string cursorId = "system.automatic";
            await foreach (var entry in pending.Reader.ReadAllAsync())
            {
                if (entry is Marker marker) await marks.WriteLineAsync(JsonSerializer.Serialize(marker));
                else if (entry is PointerEntry pointer)
                {
                    using (pointer.Snapshot)
                    {
                        if (pointer.CursorChanged)
                        {
                            cursorId = pointer.Visible ? "system.automatic" : "hidden";
                            if (pointer.Snapshot is not null)
                            {
                                var asset = CursorCaptureAssets.PersistSnapshot(pointer.Snapshot, package);
                                if (assets.Count >= 4096 && !assets.ContainsKey(asset.Id)) throw new IOException("一次录制超过4096种光标素材，素材已保留。");
                                assets[asset.Id] = asset; cursorId = asset.Id;
                            }
                        }
                        await text.WriteLineAsync(JsonSerializer.Serialize(new { time = pointer.Time, kind = pointer.Kind,
                            location = new { x = pointer.X, y = pointer.Y }, outside = pointer.Outside,
                            cursorAssetID = cursorId, modifiers = Array.Empty<string>() }));
                    }
                }
            }
            if (!embeddedCursor)
            {
                string assetFolder = Path.Combine(package, "assets"); Directory.CreateDirectory(assetFolder);
                await using var manifest = new FileStream(Path.Combine(assetFolder, "cursors.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await JsonSerializer.SerializeAsync(manifest, new { format = "dogsc.windows.cursor-assets", version = 1, assets = assets.Values.ToArray() },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                await manifest.FlushAsync(); manifest.Flush(true);
            }
            await text.FlushAsync(); await stream.FlushAsync(); stream.Flush(true);
            await marks.FlushAsync(); await markers.FlushAsync(); markers.Flush(true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposing, 1) != 0) return;
        try
        {
            if (thread.IsAlive)
            {
                await ready.Task;
                if (!PostThreadMessage(threadId, 0x12, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                await Task.Run(() => { if (!thread.Join(3000)) throw new TimeoutException("鼠标记录线程尚未退出。"); });
            }
        }
        catch (Exception error) { Warn($"鼠标事件监听收尾失败，本场已停用自动运镜：{error.Message}"); }
        pending.Writer.TryComplete();
        try { await writer; }
        catch (Exception error) { Warn($"鼠标事件写入失败，本场已停用自动运镜；视频素材已保留：{error.Message}"); }
        var report = new { format = "dogsc.windows.pointer", version = 1, embeddedCursor, recordingHeight, pointerCoordinates = "encodedVideo",
            incomplete = Incomplete, warnings = Warnings,
            droppedEvents = Interlocked.Read(ref overflow), inactiveOrBusySamplesSkipped = Interlocked.Read(ref inactiveOrBusySamplesSkipped),
            cursorTimerTicks = Interlocked.Read(ref cursorTimerTicks),
            source = new { target.Left, target.Top, target.Width, target.Height, region } };
        try { await Task.Run(() => EditorWindow.AtomicJson(Path.Combine(folder, "recording-events.json"), report)); }
        catch (Exception error) { Warn($"鼠标事件报告无法保存，本场已停用自动运镜；视频素材已保留：{error.Message}"); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public Point Position; public uint MouseDataValue, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Position; public uint Private; }
    private delegate nint Hook(int code, nint message, nint data);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] private static extern int GetMessage(out Message message, nint window, uint low, uint high);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Message message, nint window, uint low, uint high, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)] private static extern bool PostThreadMessage(uint thread, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint interval, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out Bounds bounds, int size);
}
