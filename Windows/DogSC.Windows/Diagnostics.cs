using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using DogSC.Core;
using Microsoft.Win32;
using ScreenRecorderLib;

namespace DogSC.Windows;

internal static class Diagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (2 or 3) || (args[0] == "--probe" && args.Length != 2))
            return 2;
        string testCase = args.Length == 3 ? args[2] : "window";
        if (args[0] == "--smoke-test" && testCase is not ("window" or "screen" or "region" or "region-odd"
            or "system-audio" or "microphone" or "both-audio" or "static-audio" or "60fps" or "120fps"
            or "30fps-screen" or "60fps-screen" or "120fps-screen" or "quality-fast" or "quality-standard" or "quality-clear"
            or "stop-paused" or "--no-pause"))
            return 2;
        string destination = Path.GetFullPath(args[1]);
        string reportPath = args[0] == "--probe"
            ? destination : Path.Combine(destination, "smoke-result.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            object result = args[0] switch
            {
                "--probe" => Probe(),
                "--smoke-test" => await SmokeTestAsync(destination, testCase),
                _ => throw new ArgumentException("未知验证命令。")
            };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                success = false,
                error = error.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static object Probe()
    {
        using var version = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return new
        {
            success = true,
            osVersion = Environment.OSVersion.Version.ToString(),
            displayVersion = version?.GetValue("DisplayVersion"),
            build = $"{version?.GetValue("CurrentBuildNumber")}.{version?.GetValue("UBR")}",
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            displays = CaptureCatalog.GetDisplays().Select(display => new
            {
                display.Name, display.DeviceName, display.Left, display.Top,
                display.Width, display.Height, display.RefreshRate
            }).ToArray(),
            availableWindows = CaptureCatalog.GetWindows().Count,
            outputAudioDevices = CaptureCapabilities.GetAudioOutputs().Count,
            inputAudioDevices = CaptureCapabilities.GetMicrophones().Count
        };
    }

    private static async Task<object> SmokeTestAsync(string directory, string testCase)
    {
        Directory.CreateDirectory(directory);
        bool qualityTest = testCase is "quality-fast" or "quality-standard" or "quality-clear";
        bool profileScreen = testCase is "30fps-screen" or "60fps-screen" or "120fps-screen" || qualityTest;
        bool displayCapture = testCase is "screen" or "region" or "region-odd" || profileScreen;
        bool systemAudio = testCase is "system-audio" or "both-audio" or "static-audio";
        bool microphone = testCase is "microphone" or "both-audio";
        bool testPause = testCase != "--no-pause";
        bool stopPaused = testCase == "stop-paused";
        int frameRate = testCase is "60fps" or "60fps-screen" || qualityTest ? 60 : testCase is "120fps" or "120fps-screen" ? 120 : 30;
        int quality = testCase == "quality-fast" ? 65 : testCase == "quality-standard" ? 80 : 90;
        CaptureTarget? display = displayCapture ? CaptureCatalog.GetDisplays().FirstOrDefault()
            ?? throw new InvalidOperationException("没有可验证的显示器。") : null;
        var label = new TextBlock
        {
            FontSize = 32,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var surface = new Grid { Background = Brushes.DarkSlateBlue };
        ImageBrush? detailPattern = qualityTest ? CreateDetailPattern() : null;
        if (detailPattern is not null) surface.Background = detailPattern;
        surface.Children.Add(label);
        var window = new Window
        {
            Title = "DogSC录制验证 · 程序生成画面",
            Width = 720,
            Height = 480,
            Content = surface,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = displayCapture ? WindowStyle.None : WindowStyle.SingleBorderWindow,
            Topmost = true
        };
        int tick = 0;
        int renderPhase = 0, previousRenderPhase = 0;
        double previousRenderTime = 0;
        var baselineRenderGaps = new List<double>();
        var recordingRenderGaps = new List<double>();
        EventHandler renderSample = (_, args) =>
        {
            double now = ((RenderingEventArgs)args).RenderingTime.TotalMilliseconds;
            if (renderPhase > 0 && renderPhase == previousRenderPhase && now > previousRenderTime)
                (renderPhase == 1 ? baselineRenderGaps : recordingRenderGaps).Add(now - previousRenderTime);
            previousRenderTime = now;
            previousRenderPhase = renderPhase;
        };
        var memorySamples = new List<long>();
        using var process = Process.GetCurrentProcess();
        var memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        memoryTimer.Tick += (_, _) => { process.Refresh(); memorySamples.Add(process.PrivateMemorySize64); };
        var animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(profileScreen ? 8 : 100) };
        animation.Tick += (_, _) =>
        {
            label.Text = $"DogSC Windows\n测试帧 {++tick}";
            if (detailPattern is null) surface.Background = tick % 2 == 0 ? Brushes.DarkSlateBlue : Brushes.Teal;
        };
        window.Show();
        if (profileScreen) CompositionTarget.Rendering += renderSample;
        if (testCase != "static-audio") animation.Start();
        else label.Text = "DogSC Windows · 静态画面与静音录制验证";
        try
        {
            nint handle = new WindowInteropHelper(window).Handle;
            if (display is not null && !SetWindowPos(handle, new nint(-1), display.Left, display.Top,
                display.Width, display.Height, 0x0040))
                throw new InvalidOperationException("无法用程序生成画面覆盖测试显示器。");
            await Task.Delay(500);
            if (!GetWindowRect(handle, out NativeRect bounds))
                throw new InvalidOperationException("无法读取测试窗口范围。");
            if (display is not null && (bounds.Left != display.Left || bounds.Top != display.Top
                || bounds.Right - bounds.Left != display.Width || bounds.Bottom - bounds.Top != display.Height))
                throw new InvalidOperationException("测试窗口未完整覆盖显示器，已停止采集。");
            if (profileScreen)
            {
                renderPhase = 1;
                await Task.Delay(3000);
                renderPhase = 0;
            }
            var target = display ?? new CaptureTarget(window.Title, "", handle, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            if (testCase == "window") CheckStartGuards(target, Path.Combine(directory, "guards"));
            CaptureRegion? region = testCase is "region" or "region-odd" ? new CaptureRegion(0, 0,
                Math.Min(testCase == "region-odd" ? 641 : 640, target.Width),
                Math.Min(testCase == "region-odd" ? 479 : 480, target.Height)) : null;
            var capture = new CaptureMetadata(displayCapture ? region is not null ? "region" : "screen" : "window", target.Name,
                frameRate, systemAudio, microphone, region);
            ProjectDocument project = ProjectStore.Create(directory, "录制验证", capture);
            using var recording = new RecordingSession();
            var states = new ConcurrentQueue<string>();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            recording.StateChanged += state =>
            {
                states.Enqueue(state.ToString());
                if (state == RecordingState.Recording)
                    ready.TrySetResult();
            };
            string? tonePath = systemAudio && testCase != "static-audio" ? Path.Combine(project.PackagePath, "test-tone.wav") : null;
            if (tonePath is not null)
            {
                WriteTestTone(tonePath);
                if (!PlaySound(tonePath, 0, 0x00020009))
                    throw new InvalidOperationException("无法播放系统声音验证信号。");
            }
            string nativeLog = Path.Combine(Path.GetDirectoryName(project.MediaPath)!, "native-recording.log");
            recording.Start(target, region, frameRate, systemAudio, microphone, project.MediaPath,
                diagnosticLogPath: nativeLog, quality: quality);
            Task first = await Task.WhenAny(ready.Task, recording.Completion, Task.Delay(15000));
            if (first != ready.Task)
            {
                recording.Stop();
                if (recording.Completion.IsCompleted)
                    await recording.Completion;
                throw new TimeoutException("录制后端未在15秒内启动。");
            }
            memoryTimer.Start();
            renderPhase = 2;
            await Task.Delay(1800);
            TimeSpan pauseDrift = TimeSpan.Zero;
            if (testPause)
            {
                renderPhase = 0;
                recording.PauseOrResume();
                TimeSpan pausedAt = recording.Duration;
                await Task.Delay(1200);
                pauseDrift = recording.Duration - pausedAt;
                if (Math.Abs(pauseDrift.TotalMilliseconds) > 150)
                    throw new InvalidOperationException("暂停时录制计时仍在增长。");
                if (!stopPaused) { recording.PauseOrResume(); renderPhase = 2; }
            }
            if (!stopPaused) await Task.Delay(qualityTest ? 8200 : profileScreen ? 18_200 : testCase is "60fps" or "120fps" ? 8200 : testCase == "static-audio" ? 30_000 : 1800);
            renderPhase = 0;
            recording.Stop();
            memoryTimer.Stop();
            TimeSpan duration;
            try { duration = await recording.Completion.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                throw new TimeoutException($"录制收尾超时。当前状态{recording.State}，状态序列{string.Join(",", states)}。");
            }
            recording.Dispose();
            project = ProjectStore.Complete(project, duration);
            string exportPath = Path.Combine(directory, $"export-{Guid.NewGuid():N}.mp4");
            await ProjectStore.ExportAsync(project, exportPath, overwrite: false);
            ProjectDocument reopened = ProjectStore.Open(project.PackagePath);
            if (new FileInfo(project.MediaPath).Length != new FileInfo(exportPath).Length)
                throw new InvalidOperationException("导出文件长度与项目媒体不一致。");
            return new
            {
                success = true,
                testCase,
                capture = displayCapture ? "display-covered-by-generated-window" : "generated-window-only",
                frameRate,
                quality,
                expectedWidth = region?.Width ?? (display?.Width),
                expectedHeight = region?.Height ?? (display?.Height),
                systemAudio,
                microphone,
                tonePath,
                nativeLog,
                privateMemorySamples = memorySamples,
                baselineRender = SummarizeRenderGaps(baselineRenderGaps),
                recordingRender = SummarizeRenderGaps(recordingRenderGaps),
                project = reopened.PackagePath,
                media = reopened.MediaPath,
                exported = exportPath,
                durationSeconds = duration.TotalSeconds,
                pauseDriftMilliseconds = pauseDrift.TotalMilliseconds,
                frameChanges = tick,
                states = states.ToArray()
            };
        }
        finally
        {
            if (profileScreen) CompositionTarget.Rendering -= renderSample;
            if (systemAudio) PlaySound(null, 0, 0);
            memoryTimer.Stop();
            animation.Stop();
            window.Close();
        }
    }

    private static object? SummarizeRenderGaps(List<double> gaps)
    {
        if (gaps.Count == 0) return null;
        var sorted = gaps.Order().ToArray();
        return new { samples = sorted.Length, meanMilliseconds = sorted.Average(),
            p95Milliseconds = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1],
            p99Milliseconds = sorted[(int)Math.Ceiling(sorted.Length * 0.99) - 1],
            maxMilliseconds = sorted[^1] };
    }

    private static ImageBrush CreateDetailPattern()
    {
        const int width = 1024, height = 640, stride = width * 4;
        var pixels = new byte[stride * height];
        new Random(1729).NextBytes(pixels);
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        bitmap.Freeze();
        return new ImageBrush(bitmap) { Stretch = Stretch.Fill };
    }

    private static void WriteTestTone(string path)
    {
        const int sampleRate = 48_000;
        const int samples = sampleRate * 10;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
        writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (int i = 0; i < samples; i++)
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 4_000));
    }

    private static void CheckStartGuards(CaptureTarget target, string directory)
    {
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, $"reserved-{Guid.NewGuid():N}.mp4");
        File.WriteAllText(output, "existing recording");
        using (var session = new RecordingSession())
        {
            ExpectRejected<ArgumentOutOfRangeException>(() => session.Start(target, null, 0, false, false, output));
            ExpectRejected<ArgumentOutOfRangeException>(() => session.Start(target, null, 121, false, false, output));
            ExpectRejected<ArgumentOutOfRangeException>(() => session.Start(target, null, 30, false, false, output, quality: 0));
            ExpectRejected<ArgumentOutOfRangeException>(() => session.Start(target, null, 30, false, false, output, quality: 101));
            ExpectRejected<ArgumentException>(() => session.Start(target, null, 30, false, false,
                output, Path.Combine(directory, "outside", "native.log")));
            ExpectRejected<IOException>(() => session.Start(target, null, 30, false, false, output));
        }
        if (File.ReadAllText(output) != "existing recording")
            throw new InvalidOperationException("录制覆盖了已有文件。");
        string log = Path.Combine(directory, $"reserved-{Guid.NewGuid():N}.log");
        File.WriteAllText(log, "existing log");
        using (var session = new RecordingSession())
            ExpectRejected<IOException>(() => session.Start(target, null, 30, false, false,
                Path.Combine(directory, $"new-{Guid.NewGuid():N}.mp4"), log));
        if (File.ReadAllText(log) != "existing log")
            throw new InvalidOperationException("录制覆盖了已有日志。");
    }

    private static void ExpectRejected<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"录制未拒绝无效输入：{typeof(T).Name}。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? sound, nint module, uint flags);
}
