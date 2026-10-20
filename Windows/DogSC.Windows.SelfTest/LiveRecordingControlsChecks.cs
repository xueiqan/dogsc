using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DogSC.Windows;

namespace DogSC.Windows.SelfTest;

internal static class LiveRecordingControlsChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record AudioRange(string Name, double Start, double End);

    public static int Run(string root, bool startSilent = false)
    {
        root = Path.GetFullPath(root);
        string allowed = Path.GetFullPath(@"D:\github\dogsc\Windows\artifacts\live-controls-20261015") + Path.DirectorySeparatorChar;
        if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("实时控制测试只能写入专用验证目录的子目录。");
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("实时控制验证目录必须为空。");
        _ = LiveMainChecks.CreateApplication();
        using var watchdog = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromMinutes(4), watchdog.Token); }
            catch (OperationCanceledException) { return; }
            Console.Error.WriteLine("Owned live-control fixture timed out; generated media is retained.");
            Environment.Exit(2);
        });
        int exit = 1;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = dispatcher.InvokeAsync(async () =>
        {
            try { await RunAsync(root, startSilent); exit = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { watchdog.Cancel(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        });
        Dispatcher.Run();
        return exit;
    }

    private static async Task RunAsync(string root, bool startSilent)
    {
        var cases = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new IOException("FAIL: " + name);
            cases.Add(name); Console.WriteLine("PASS: " + name);
        }
        var display = CaptureCatalog.GetDisplays().FirstOrDefault() ?? throw new IOException("测试显示器不可用。");
        var text = new TextBlock { Foreground = Brushes.White, FontSize = 44, Margin = new Thickness(45) };
        var pattern = new Window { Title = "DogSC owned live-controls pattern", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, Content = text, Background = Brushes.Teal };
        int frame = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) => { text.Text = $"DogSC · 实时控制验证\nFrame {++frame}"; pattern.Background = frame % 60 < 30 ? Brushes.Teal : Brushes.DarkSlateBlue; };
        var previousMain = Application.Current.MainWindow;
        var main = new MainWindow(); Application.Current.MainWindow = main;
        bool closed = false; main.Closed += (_, _) => closed = true;
        RecordingToolbarWindow? bar = null;
        var ranges = new List<AudioRange>();
        CameraCaptureCase? cameraCase = null;
        double pauseDrift = 0, activeSeconds = 0;
        string tone = CreateTone(root);
        try
        {
            pattern.Show();
            Check(SetWindowPos(new WindowInteropHelper(pattern).Handle, -1, display.Left, display.Top,
                display.Width, display.Height, 0x0040), "self-owned animated pattern covers the physical display");
            timer.Start();
            Control<TextBox>(main, "FolderBox").Text = Path.Combine(root, "recordings");
            Control<CheckBox>(main, "ProcessAudioCheck").IsChecked = false;
            Control<CheckBox>(main, "RecoverableCheck").IsChecked = false;
            await Field<Task>(main, "_deviceDiscovery")!.WaitAsync(TimeSpan.FromSeconds(20));
            main.ShowRecordingToolbar(); bar = Field<RecordingToolbarWindow>(main, "_toolbar")!;
            bar.Configure("screen", display, 30, 720, !startSilent, false, false);
            await Drain();
            var systemVolume = Control<Slider>(bar, "SystemVolumeSlider");
            var microphoneVolume = Control<Slider>(bar, "MicrophoneVolumeSlider");
            Check(systemVolume.Minimum == 0 && systemVolume.Maximum == 1 && systemVolume.Value == 1
                && microphoneVolume.Minimum == 0 && microphoneVolume.Maximum == 1 && microphoneVolume.Value == 1,
                "both app-only recording sliders default to 100 percent and span 0 through 100 percent");
            Click(Control<Button>(bar, "AudioSettingsButton"));
            Check(Control<FrameworkElement>(bar, "AudioPanel").Visibility == Visibility.Visible,
                "sound settings expand within the same capture-excluded toolbar window");
            Click(Control<Button>(bar, "StartStopButton"));
            await Until(() => Field<RecordingState?>(main, "_recordingState") == RecordingState.Recording, "native screen capture starts");
            var session = Field<RecordingSession>(main, "_session")!;
            main.ShowRecordingToolbar(); await Drain();
            CheckControls();
            Check(Field<CameraPreviewWindow>(main, "_cameraPreviewWindow") is null, "camera is initially off");
            if (startSilent)
            {
                Check(!bar.SystemAudio && !bar.Microphone, "both recording audio inputs start disabled");
                Control<CheckBox>(bar, "SystemAudioCheck").IsChecked = true; await Applied();
                Check(bar.SystemAudio && Control<CheckBox>(main, "SystemAudioCheck").IsChecked == true,
                    "system audio can be enabled after starting with both audio inputs disabled");
            }
            await Task.Delay(300);
            Check(PlaySound(tone, 0, 0x00020009), "owned steady tone plays without changing system endpoint volume");
            await Measure("unity", 4000);
            systemVolume.Value = .25; await Applied();
            Check(Math.Abs((double)typeof(RecordingToolbarWindow).GetProperty("SystemVolume")!.GetValue(bar)! - .25) < 1e-8,
                "public system recording volume follows the toolbar slider");
            await Measure("quarter", 4000);
            Control<CheckBox>(bar, "SystemAudioCheck").IsChecked = false; await Applied();
            await Measure("muted", 4000);
            Control<CheckBox>(bar, "SystemAudioCheck").IsChecked = true;
            systemVolume.Value = .5; await Applied();
            Control<CheckBox>(bar, "MicrophoneCheck").IsChecked = true; await Applied();
            Check(Control<CheckBox>(main, "MicrophoneCheck").IsChecked == true,
                "enabling the microphone during capture synchronizes main settings");
            Control<CheckBox>(bar, "CameraButton").IsChecked = true;
            await CameraApplied();
            await Until(() => Field<CameraPreviewWindow>(main, "_cameraPreviewWindow") is { IsVisible: true }
                && Field<object>(main, "_cameraOverlay") is not null, "camera opens and joins the existing native screen recording");
            double withCamera = session.Duration.TotalSeconds;
            await Task.Delay(4000);
            Check(session.Duration.TotalSeconds >= withCamera + 3.8, "screen time continues while camera and microphone are enabled");
            Control<CheckBox>(bar, "CameraButton").IsChecked = false; await CameraApplied();
            Control<CheckBox>(bar, "MicrophoneCheck").IsChecked = false; await Applied();
            Check(Field<CameraPreviewWindow>(main, "_cameraPreviewWindow") is null
                && Control<CheckBox>(main, "MicrophoneCheck").IsChecked == false,
                "disabling camera releases its preview and disabling microphone synchronizes settings");
            if (startSilent)
            {
                Control<CheckBox>(bar, "CameraButton").IsChecked = true; await CameraApplied();
                await Until(() => Field<CameraPreviewWindow>(main, "_cameraPreviewWindow") is { IsVisible: true }
                    && Field<CameraPreviewSession>(main, "_idleCameraPreview")?.LatestFrame is not null,
                    "camera reopens with an actual frame before paused camera-off test");
                var preview = Field<CameraPreviewWindow>(main, "_cameraPreviewWindow")!;
                Check(SetWindowPos(new WindowInteropHelper(preview).Handle, -1,
                    display.Left + Math.Max(0, display.Width - 600), display.Top + Math.Max(0, display.Height - 420),
                    560, 380, 0x0010), "camera pixel-check rectangle is positioned over owned plain background");
                await Drain();
                typeof(MainWindow).GetMethod("RecordCameraPlacement", Private)!.Invoke(main, null);
                await Task.Delay(1000);
                var placement = Field<List<RecordedCameraPlacement>>(main, "_cameraPlacements")!.Last();
                cameraCase = new(session.Duration.TotalSeconds - .3, 0, placement);
            }
            main.TogglePause();
            await Until(() => Field<RecordingState?>(main, "_recordingState") == RecordingState.Paused, "capture pauses");
            CheckControls();
            double pausedAt = session.Duration.TotalSeconds;
            if (startSilent)
            {
                Control<CheckBox>(bar, "CameraButton").IsChecked = false; await CameraApplied();
                Check(Field<CameraPreviewWindow>(main, "_cameraPreviewWindow") is null
                    && Field<CameraPreviewSession>(main, "_idleCameraPreview") is null,
                    "camera toggled off during pause releases its actual preview and device");
            }
            systemVolume.Value = .75; microphoneVolume.Value = .4; await Applied();
            await Task.Delay(700);
            pauseDrift = (session.Duration.TotalSeconds - pausedAt) * 1000;
            Check(Math.Abs(pauseDrift) < 150, "adjusting volumes while paused does not advance recording time");
            Check(Math.Abs((double)typeof(RecordingToolbarWindow).GetProperty("MicrophoneVolume")!.GetValue(bar)! - .4) < 1e-8,
                "microphone recording volume follows its independent app slider");
            main.TogglePause();
            await Until(() => Field<RecordingState?>(main, "_recordingState") == RecordingState.Recording, "same capture resumes");
            if (cameraCase is not null) cameraCase = cameraCase with { OffTime = session.Duration.TotalSeconds + 1 };
            await Measure("three-quarter", 4000);
            Check(ReferenceEquals(session, Field<RecordingSession>(main, "_session")), "all live changes retain one native screen session");
            activeSeconds = session.Duration.TotalSeconds;
            Click(Control<Button>(bar, "StartStopButton"));
            var saving = Stopwatch.StartNew();
            await Field<Task>(main, "_recordingTask")!.WaitAsync(TimeSpan.FromSeconds(90));
            saving.Stop();
            Check(Field<bool>(main, "_recordingSaved") && !Field<bool>(main, "_recordingActive"), "one recording saves normally after live changes");
            var saved = Field<SavedRecording>(main, "_savedRecording") ?? throw new IOException("实际录制没有保存结果。");
            Check(!Directory.EnumerateFiles(saved.Folder, "camera.*", SearchOption.AllDirectories).Any(), "no independent camera media is saved");
            var decoded = await Task.Run(() => Decode(saved, ranges, cameraCase));
            Check(decoded.VideoFrames > 10 && Math.Abs(decoded.Duration - activeSeconds) < .6,
                "complete video decode retains continuous active recording duration");
            Check(decoded.AudioSamples > 10, "main MP4 audio stream decodes completely");
            Check(decoded.Rms["unity"] > .001, "actual decoded baseline contains audible loopback test signal");
            double quarterRatio = decoded.Rms["quarter"] / decoded.Rms["unity"];
            double mutedRatio = decoded.Rms["muted"] / decoded.Rms["unity"];
            double restoredRatio = decoded.Rms["three-quarter"] / decoded.Rms["unity"];
            Check(quarterRatio is > .12 and < .38, "recording-only 25 percent gain measurably attenuates decoded audio");
            Check(mutedRatio < .03, "system-audio off mutes the recorded tone");
            Check(restoredRatio is > .55 and < .95, "75 percent volume applied during pause is heard after resume");
            if (cameraCase is not null)
            {
                Check(decoded.CameraPixels is { OnNonBackgroundFraction: > .2 },
                    "actual saved MP4 contains camera image in its camera-on rectangle before pause");
                Check(decoded.CameraPixels is { OffBackgroundFraction: > .95 },
                    "camera disabled during pause restores owned screen background after resume without a frozen camera image");
            }
            File.WriteAllText(Path.Combine(root, "live-controls-result.json"), JsonSerializer.Serialize(new
            {
                success = true, checkedAt = DateTimeOffset.Now, actualCapture = true, requestedFps = 30, requestedHeight = 720,
                composeFinal = false, startSilent, checks = cases.Count, cases, activeSeconds, pauseDriftMilliseconds = pauseDrift,
                saveSeconds = saving.Elapsed.TotalSeconds, saved.Video, saved.Audio, saved.Project, decoded, ranges,
                microphoneSignalGainAudibilityMeasured = false,
                microphoneLimit = "Real microphone enable/disable is exercised; its input signal and perceived gain are not asserted."
            }, new JsonSerializerOptions { WriteIndented = true }));

            async Task Measure(string name, int milliseconds)
            {
                double start = session.Duration.TotalSeconds;
                await Task.Delay(milliseconds);
                ranges.Add(new(name, start + .8, session.Duration.TotalSeconds - .8));
            }
            async Task Applied()
            {
                await Drain();
                await Field<Task>(main, "_liveAudioConfiguration")!.WaitAsync(TimeSpan.FromSeconds(15));
                Check(ReferenceEquals(session, Field<RecordingSession>(main, "_session")), "audio update preserves active screen session");
            }
            async Task CameraApplied()
            {
                await Drain();
                await Field<Task>(main, "_cameraToggleConfiguration")!.WaitAsync(TimeSpan.FromSeconds(20));
                await Field<Task>(main, "_cameraConfiguration")!.WaitAsync(TimeSpan.FromSeconds(20));
                Check(ReferenceEquals(session, Field<RecordingSession>(main, "_session")), "camera update preserves active screen session");
            }
            void CheckControls()
            {
                foreach (string name in new[] { "CameraButton", "MicrophoneCheck", "SystemAudioCheck", "AudioSettingsButton", "SystemVolumeSlider", "MicrophoneVolumeSlider" })
                    Check(Control<FrameworkElement>(bar, name).IsEnabled, name + " remains interactive during recording or pause");
                foreach (string name in new[] { "FrameRateCombo", "ResolutionCombo", "ModeCombo", "SourceCombo", "ComposeFinalCheck" })
                    Check(!Control<FrameworkElement>(bar, name).IsEnabled, name + " stays frozen until recording ends");
            }
        }
        finally
        {
            PlaySound(null, 0, 0);
            if (Field<bool>(main, "_recordingActive"))
            {
                typeof(MainWindow).GetMethod("RequestStop", Private)!.Invoke(main, null);
                try { await Field<Task>(main, "_recordingTask")!.WaitAsync(TimeSpan.FromSeconds(90)); }
                catch (Exception error) { Console.Error.WriteLine("Owned capture cleanup: " + error.Message); }
                if (Field<bool>(main, "_recordingActive")) Environment.Exit(1);
            }
            timer.Stop();
            if (!closed) main.RequestExit();
            pattern.Close(); Application.Current.MainWindow = previousMain;
        }
    }

    private sealed record CameraCaptureCase(double OnTime, double OffTime, RecordedCameraPlacement Placement);
    private sealed record CameraPixelResult(CameraCaptureCase Timing, Int32Rect Rectangle, double OnNonBackgroundFraction, double OffBackgroundFraction);
    private sealed record DecodeResult(int VideoFrames, int AudioSamples, double Duration, Dictionary<string, double> Rms, CameraPixelResult? CameraPixels);
    private static DecodeResult Decode(SavedRecording saved, List<AudioRange> ranges, CameraCaptureCase? cameraCase)
    {
        Mf.Check(Mf.MFStartup(0x20070, 0));
        try
        {
            int frames = 0, audioSamples = 0; double last = -1, duration;
            using (var video = new MfReader(saved.Video, false))
            {
                duration = video.Duration;
                while (video.Read() is { } sample)
                {
                    if (sample.Time < last || sample.Bytes.Length != video.Width * video.Height * 4) throw new IOException("实际视频解码不连续。");
                    last = sample.Time; frames++;
                }
            }
            var squares = ranges.ToDictionary(range => range.Name, _ => 0d);
            var counts = ranges.ToDictionary(range => range.Name, _ => 0L);
            using (var audio = new MfReader(saved.Video, true))
                while (audio.Read() is { } sample)
                {
                    audioSamples++;
                    if (sample.Bytes.Length % 4 != 0) throw new IOException("实际声音不是预期48kHz双声道16位PCM。");
                    for (int i = 0; i < sample.Bytes.Length / 4; i++)
                    {
                        double time = sample.Time + i / 48000d;
                        foreach (var range in ranges)
                            if (time >= range.Start && time < range.End)
                            {
                                double value = BinaryPrimitives.ReadInt16LittleEndian(sample.Bytes.AsSpan(i * 4)) / 32768d;
                                squares[range.Name] += value * value; counts[range.Name]++;
                            }
                    }
                }
            if (counts.Values.Any(count => count < 48000)) throw new IOException("各音量分段没有足够的实际解码样本。");
            if (saved.Audio is not null)
            {
                using var audio = new MfReader(saved.Audio, true);
                int count = 0; while (audio.Read() is not null) count++;
                if (count < 10) throw new IOException("独立纯音频未完整解码。");
            }
            CameraPixelResult? cameraPixels = null;
            if (cameraCase is not null)
            {
                using var video = new MediaFrameReader(saved.Video);
                var rectangle = RealtimeCameraOverlay.Layout(cameraCase.Placement, video.Width, video.Height).Rectangle;
                if (rectangle.Width < 20 || rectangle.Height < 20) throw new IOException("摄像头验证矩形没有足够的可见像素。");
                double onBackground = BackgroundFraction(video.FrameAt(cameraCase.OnTime), rectangle);
                double offBackground = BackgroundFraction(video.FrameAt(cameraCase.OffTime), rectangle);
                cameraPixels = new(cameraCase, rectangle, 1 - onBackground, offBackground);
            }
            return new(frames, audioSamples, duration, squares.ToDictionary(pair => pair.Key, pair => Math.Sqrt(pair.Value / counts[pair.Key])), cameraPixels);
        }
        finally { Mf.MFShutdown(); }
    }
    private static double BackgroundFraction(System.Windows.Media.Imaging.BitmapSource image, Int32Rect rectangle)
    {
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight]; image.CopyPixels(pixels, stride, 0);
        int background = 0, total = 0;
        // Keep away from H.264 rectangle edges, camera controls and the generated top-left text.
        for (int y = rectangle.Y + rectangle.Height / 6; y < rectangle.Y + rectangle.Height * 5 / 6; y += 3)
            for (int x = rectangle.X + rectangle.Width / 6; x < rectangle.X + rectangle.Width * 5 / 6; x += 3)
            {
                int offset = y * stride + x * 4;
                int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                bool teal = Math.Abs(r) <= 30 && Math.Abs(g - 128) <= 30 && Math.Abs(b - 128) <= 30;
                bool slate = Math.Abs(r - 72) <= 30 && Math.Abs(g - 61) <= 30 && Math.Abs(b - 139) <= 30;
                if (teal || slate) background++;
                total++;
            }
        if (total < 100) throw new IOException("摄像头窗口内取样不足。");
        return (double)background / total;
    }
    private static T? Field<T>(MainWindow main, string name) => (T?)typeof(MainWindow).GetField(name, Private)!.GetValue(main);
    private static T Control<T>(Window window, string name) where T : FrameworkElement => (T)(window.FindName(name) ?? throw new IOException("缺少控件：" + name));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Drain() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static async Task Until(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(25)) await Task.Delay(25);
        if (!condition()) throw new IOException(message);
    }
    private static string CreateTone(string root)
    {
        string path = Path.Combine(root, "owned-loopback-tone.wav");
        using var writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        const int samples = 48000 * 10;
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        for (int i = 0; i < samples; i++) writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / 48000) * 4000));
        return path;
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? path, nint module, uint flags);
}
