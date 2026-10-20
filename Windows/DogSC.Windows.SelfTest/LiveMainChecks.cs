using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using DogSC.Windows;

namespace DogSC.Windows.SelfTest;

internal static class LiveMainChecks
{
    public static int Run(string root, bool capture = true, int frameRate = 30, int recordingHeight = 720,
        int phaseMilliseconds = 15_000, bool composeFinal = true, bool closeDuringSave = false)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        if (capture && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("真实测试目录必须为空，避免覆盖已有证据。");
        var application = CreateApplication();
        using var watchdog = new CancellationTokenSource();
        var timeout = TimeSpan.FromMinutes(frameRate > 60 && recordingHeight == 0 ? 12 : 6);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(timeout, watchdog.Token); }
            catch (OperationCanceledException) { return; }
            Console.Error.WriteLine($"Only the owned live test process exits after its {timeout.TotalMinutes}-minute watchdog; generated test media remains.");
            Environment.Exit(2);
        });
        int exit = 1;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (capture) await SimpleRecordingCheck.RunAsync(camera: true, frameRate: frameRate, phaseMilliseconds: phaseMilliseconds,
                    pixPin: false, recordingHeight: recordingHeight, composeFinal: composeFinal, outputRoot: root, closeDuringSave: closeDuringSave);
                await Task.Run(() => DecodeAll(root));
                await CheckIdleCameraClose(root); exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { watchdog.Cancel(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        });
        Dispatcher.Run(); return exit;
    }

    internal static Application CreateApplication()
    {
        var application = new Application();
        // Embed the production resource source directly. The generated App BAML rejects a subclass root,
        // and constructing the production Application would invoke its normal startup integration.
        using (var source = typeof(LiveMainChecks).Assembly.GetManifestResourceStream("LiveProductionApp.xaml")!)
        {
            var document = XDocument.Load(source);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resources = document.Root!.Element(presentation + "Application.Resources")!;
            var dictionary = new XElement(presentation + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), resources.Elements());
            application.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        }
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        return application;
    }

    private static async Task CheckIdleCameraClose(string root)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var window = new MainWindow();
        await ((Task)typeof(MainWindow).GetField("_deviceDiscovery", flags)!.GetValue(window)!).WaitAsync(TimeSpan.FromSeconds(20));
        bool closed = false; window.Closed += (_, _) => closed = true;
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(MainWindow).GetField("_cameraConfiguration", flags)!.SetValue(window, hung.Task);
        try
        {
            window.RequestExit(); await Task.Delay(6100);
            bool visible = window.IsVisible && ((Button)window.FindName("ForceExitButton")).Visibility == Visibility.Visible;
            if (closed || !visible) throw new IOException("空闲摄像头配置收尾挂住后未展示可强制退出的自有窗口。");
            hung.SetResult();
            var cleanupElapsed = Stopwatch.StartNew();
            var cleanupTasks = (List<Task>)typeof(MainWindow).GetField("_pendingMediaCleanup", flags)!.GetValue(window)!;
            while (cleanupTasks.Any(task => !task.IsCompletedSuccessfully) && cleanupElapsed.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(30);
            if (cleanupTasks.Any(task => !task.IsCompletedSuccessfully))
                throw new IOException("空闲摄像头配置完成后，其收尾任务仍未完成。");
            window.RequestExit();
            var elapsed = Stopwatch.StartNew();
            while (!closed && elapsed.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(30);
            if (!closed) throw new IOException("空闲摄像头配置完成后仍不能关闭自有窗口。");
            File.WriteAllText(Path.Combine(root, "idle-camera-close-result.json"), JsonSerializer.Serialize(
                new { success = true, controlledHungConfiguration = true, realDeviceHang = false, recoveryExitVisible = visible,
                    closedAfterConfigurationCompletion = closed, checks = 2 }));
            Console.WriteLine("PASS idle camera configuration timeout displays exit control and permits later normal close: 2 checks.");
        }
        finally { hung.TrySetResult(); if (!closed) window.RequestExit(); }
    }

    private static void DecodeAll(string root)
    {
        string recordings = Path.Combine(root, "recordings");
        var audioVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.EnumerateDirectories(recordings))
        {
            string sync = Path.Combine(folder, "素材.dogscproject", "media", "audio-sync.json");
            if (!File.Exists(sync)) continue;
            using var audioData = JsonDocument.Parse(File.ReadAllText(sync));
            if (audioData.RootElement.GetProperty("tracks").GetArrayLength() > 0)
                foreach (var video in Directory.EnumerateFiles(folder, "*.mp4")) audioVideos.Add(video);
        }
        var paths = Directory.EnumerateFiles(recordings, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".mp4", ".m4a", ".wav" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
        var decoded = new List<object>();
        Mf.Check(Mf.MFStartup(0x20070, 0));
        try
        {
            foreach (string path in paths)
            {
                bool video = Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
                Decode(path, !video);
                if (audioVideos.Contains(path)) Decode(path, true);
            }
        }
        finally { Mf.MFShutdown(); }
        File.WriteAllText(Path.Combine(root, "complete-media-decode.json"), JsonSerializer.Serialize(new
            { success = true, actualCapture = true, checkedAt = DateTimeOffset.Now, mediaFiles = paths.Length, decoded }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"完整解码通过：{paths.Length}个真实媒体文件，{decoded.Count}条音视频流；测试媒体保留。");

        void Decode(string path, bool audio)
        {
            var timer = Stopwatch.StartNew();
            using var reader = new MfReader(path, audio);
            int samples = 0; long byteCount = 0; double last = -1;
            while (reader.Read() is { } sample)
            {
                if (sample.Bytes.Length == 0 || sample.Time < last) throw new IOException("实际录制解码时间或样本无效：" + path);
                if (!audio && sample.Bytes.Length != reader.Width * reader.Height * 4) throw new IOException("实际画面数据不足：" + path);
                samples++; byteCount += sample.Bytes.Length; last = sample.Time;
            }
            if (samples < 2 || reader.Duration <= 0) throw new IOException("实际录制没有完整有效媒体流：" + path);
            decoded.Add(new { path, audio, samples, byteCount, reader.Duration, lastTimestamp = last,
                reader.Width, reader.Height, reader.LastDecodedPitch, decodedSeconds = timer.Elapsed.TotalSeconds });
            Console.WriteLine($"DECODE {(audio ? "audio" : "video")}: {samples}样本，{reader.Duration:F3}s，{path}");
        }
    }
}
