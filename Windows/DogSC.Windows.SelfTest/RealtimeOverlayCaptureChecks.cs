using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DogSC.Core;

namespace DogSC.Windows.SelfTest;

internal static class RealtimeOverlayCaptureChecks
{
    private const int Width = 640, Height = 480, ScreenRate = 120;
    private sealed record Rgb(int R, int G, int B);
    private sealed record Rectangle(int X, int Y, int Width, int Height);
    private sealed record Observation(int Index, double Time, Rgb InitialLeft, Rgb InitialRight,
        Rgb MovedLeft, Rgb MovedRight, int InitialMode, int MovedMode, Rectangle? DetectedRectangle);
    private static readonly Rgb Background = new(32, 48, 64);
    private static readonly Rgb[] LeftColors = [new(220, 30, 30), new(20, 200, 30), new(240, 200, 20)];
    private static readonly Rgb[] RightColors = [new(30, 70, 230), new(210, 30, 220), new(10, 205, 215)];
    private static readonly Rectangle InitialRectangle = new(40, 40, 224, 152);
    private static readonly Rectangle MovedRectangle = new(360, 280, 224, 152);

    public static int Run(string root)
    {
        root = Path.GetFullPath(root);
        MediaExport.RejectLinks(root);
        Directory.CreateDirectory(root);
        MediaExport.RejectLinks(root);
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("实时叠加验收目录必须为空，已有素材不会覆盖。");
        var application = LiveMainChecks.CreateApplication();
        var dispatcher = Dispatcher.CurrentDispatcher;
        int exit = 1;
        _ = dispatcher.InvokeAsync(async () =>
        {
            Window? canvas = null;
            RecordingSession? session = null;
            RealtimeCameraOverlay? overlay = null;
            DispatcherTimer? timer = null;
            Exception? failure = null;
            var cleanupErrors = new List<string>();
            var checks = new List<string>();
            var observations = new List<Observation>();
            string video = Path.Combine(root, "recording.mp4");
            double movedAt = 0, activeSeconds = 0, decodedDuration = 0, saveSeconds = 0;
            bool startIssued = false, recordingObserved = false;
            int expectedFrames = 0;
            long submittedFrames = 0, appliedFrames = 0;
            void Check(bool valid, string name)
            {
                if (!valid) throw new IOException("FAIL: " + name);
                checks.Add(name);
            }
            try
            {
                var display = CaptureCatalog.GetDisplays().FirstOrDefault(item => item.Width >= Width + 80 && item.Height >= Height + 80)
                    ?? throw new IOException("没有足够容纳自有640×480图案的显示器。");
                var label = new TextBlock { Text = "DogSC live overlay fixture", Foreground = Brushes.White,
                    FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb((byte)Background.R, (byte)Background.G, (byte)Background.B)) };
                grid.Children.Add(label);
                canvas = new Window { Title = "DogSC owned realtime camera overlay pattern", WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                    Width = Width, Height = Height, Content = grid };
                canvas.Show();
                nint handle = new WindowInteropHelper(canvas).Handle;
                Check(SetWindowPos(handle, -1, display.Left + 40, display.Top + 40, Width, Height, 0x0040),
                    "owned generated window is positioned at exact physical crop dimensions");
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(200);
                Check(GetWindowRect(handle, out var bounds) && bounds.Left == display.Left + 40 && bounds.Top == display.Top + 40
                    && bounds.Right - bounds.Left == Width && bounds.Bottom - bounds.Top == Height,
                    "owned generated window covers the whole recording crop before capture");
                overlay = new RealtimeCameraOverlay(30, Placement(InitialRectangle, 0));
                overlay.SubmitFrame(ColorFrame(0));
                await overlay.Ready.WaitAsync(TimeSpan.FromSeconds(5));
                var overlayFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
                overlay.Failed += error => overlayFailure.TrySetResult(error);
                session = new RecordingSession();
                session.Start(display, new CaptureRegion(40, 40, Width, Height), ScreenRate, false, false, video,
                    Path.Combine(root, "native.log"), embeddedCursor: false, cameraOverlay: overlay);
                startIssued = true;
                var startup = Stopwatch.StartNew();
                while (session.State != RecordingState.Recording && startup.Elapsed < TimeSpan.FromSeconds(10))
                {
                    if (session.Completion.IsCompleted) await session.Completion;
                    await Task.Delay(10);
                }
                Check(session.State == RecordingState.Recording, "native screen recording starts with the prepared camera frame");
                recordingObserved = true;
                int cameraFrame = 0;
                timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 30) };
                timer.Tick += (_, _) =>
                {
                    overlay.SubmitFrame(ColorFrame(++cameraFrame / 8 % LeftColors.Length));
                    label.Text = $"DogSC generated frame {cameraFrame}";
                };
                timer.Start();
                await Task.Delay(1500);
                movedAt = session.Duration.TotalSeconds;
                overlay.UpdatePlacement(Placement(MovedRectangle, movedAt));
                await Task.Delay(1500);
                if (overlayFailure.Task.IsCompleted) throw await overlayFailure.Task;
                timer.Stop();
                var saving = Stopwatch.StartNew();
                session.Stop();
                await session.Completion.WaitAsync(TimeSpan.FromSeconds(65));
                await session.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                saving.Stop();
                saveSeconds = saving.Elapsed.TotalSeconds;
                activeSeconds = session.Duration.TotalSeconds;
                submittedFrames = overlay.SubmittedFrames;
                appliedFrames = overlay.AppliedFrames;
                Check(submittedFrames >= 60 && appliedFrames >= 30, "camera frames are submitted and actually applied throughout the screen recording");
                await Task.Run(() =>
                {
                    Mf.Check(Mf.MFStartup(0x20070, 0));
                    try
                    {
                        using var reader = new MfReader(video, false);
                        Check(reader.Width == Width && reader.Height == Height, "recorded native crop remains 640 by 480 pixels");
                        decodedDuration = reader.Duration;
                        while (reader.Read() is { } frame)
                        {
                            if (observations.Count >= 1000) throw new IOException("三秒录制返回超过1000帧，停止验收并保留素材。");
                            var initialLeft = Pixel(frame.Bytes, InitialRectangle.X + InitialRectangle.Width / 4, InitialRectangle.Y + InitialRectangle.Height / 2);
                            var initialRight = Pixel(frame.Bytes, InitialRectangle.X + InitialRectangle.Width * 3 / 4, InitialRectangle.Y + InitialRectangle.Height / 2);
                            var movedLeft = Pixel(frame.Bytes, MovedRectangle.X + MovedRectangle.Width / 4, MovedRectangle.Y + MovedRectangle.Height / 2);
                            var movedRight = Pixel(frame.Bytes, MovedRectangle.X + MovedRectangle.Width * 3 / 4, MovedRectangle.Y + MovedRectangle.Height / 2);
                            int initialMode = Mode(initialLeft, initialRight), movedMode = Mode(movedLeft, movedRight);
                            var area = initialMode >= 0 ? InitialRectangle : movedMode >= 0 ? MovedRectangle : null;
                            observations.Add(new(observations.Count, frame.Time, initialLeft, initialRight, movedLeft, movedRight,
                                initialMode, movedMode, area is null ? null : DetectRectangle(frame.Bytes, area)));
                        }
                    }
                    finally { Mf.MFShutdown(); }
                });
                Check(observations.Count > 2 && decodedDuration is > 2.9 and < 3.5, "original MP4 is decoded completely and has the actual three-second duration");
                expectedFrames = (int)Math.Round(decodedDuration * ScreenRate);
                Check(Math.Abs(observations.Count - expectedFrames) <= 1, "native frame count agrees with 120 fps and container duration within one frame");
                double frameSpan = observations[^1].Time - observations[0].Time;
                Check(Math.Abs(frameSpan - (observations.Count - 1.0) / ScreenRate) <= 1.0 / ScreenRate + .000001,
                    "decoded sample timestamps span 120 fps within one native frame");
                var before = observations.Where(item => item.Time < movedAt - .15).ToArray();
                var after = observations.Where(item => item.Time > movedAt + .15).ToArray();
                Check(before.Length > 20 && after.Length > 20, "decoded samples cover both positions away from the asynchronous move boundary");
                Check(before.All(item => item.InitialMode >= 0 && item.MovedMode < 0)
                    && after.All(item => item.MovedMode >= 0 && item.InitialMode < 0),
                    "camera pixels are mirrored at the initial position and move to the new position without a stale rectangle");
                Check(before.Select(item => item.InitialMode).Distinct().Count() == LeftColors.Length
                    && after.Select(item => item.MovedMode).Distinct().Count() == LeftColors.Length,
                    "camera pixels continue changing through all color modes before and after movement");
                Check(before.All(item => MatchesRectangle(item.DetectedRectangle, InitialRectangle))
                    && after.All(item => MatchesRectangle(item.DetectedRectangle, MovedRectangle)),
                    "decoded camera rectangle dimensions and physical placement match the requested normalized crop coordinates");
                var early = before[0];
                var middle = before[^1];
                var late = after[^1];
                Check(early.InitialMode >= 0 && middle.InitialMode >= 0 && late.MovedMode >= 0,
                    "the first, middle and last decoded camera frames contain clean mirrored video without lock or eye controls");
                Check(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Any(path => Path.GetFileName(path).Equals("camera-sync.json", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(path).Equals("camera.mp4", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(path).Equals("camera.mov", StringComparison.OrdinalIgnoreCase)),
                    "real-time overlay recording creates no independent camera video or synchronization file");
                exit = 0;
            }
            catch (Exception error) { failure = error; Console.Error.WriteLine(error); }
            finally
            {
                timer?.Stop();
                if (session is not null)
                {
                    try
                    {
                        session.Dispose();
                        if (startIssued || session.Completion.IsCompleted) await session.Completion.WaitAsync(TimeSpan.FromSeconds(65));
                    }
                    catch (Exception error) { cleanupErrors.Add(error.ToString()); }
                    try { await session.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (Exception error) { cleanupErrors.Add(error.ToString()); }
                }
                if (overlay is not null)
                {
                    try { await overlay.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (Exception error) { cleanupErrors.Add(error.ToString()); }
                }
                bool nativeReleased = session is null || session.ReleaseCompletion.IsCompletedSuccessfully;
                if (cleanupErrors.Count > 0 || !nativeReleased) exit = 1;
                File.WriteAllText(Path.Combine(root, "realtime-overlay-capture-result.json"), JsonSerializer.Serialize(new
                {
                    success = exit == 0, realNativeScreenRecording = recordingObserved, generatedCameraFrames = true, physicalCameraUsed = false,
                    video, targetFrameRate = ScreenRate, cameraInputFrameRate = 30, activeSeconds, decodedDuration,
                    frames = observations.Count, expectedFrames, submittedFrames, appliedFrames, movedAt, saveSeconds,
                    initialRectangle = InitialRectangle, movedRectangle = MovedRectangle, colorTolerancePerChannel = 30,
                    error = failure?.ToString(), nativeReleased, cleanupErrors, checks, samples = observations
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (!nativeReleased)
                {
                    Console.Error.WriteLine("Native release was not confirmed; only this fixture process exits while its owned pattern still covers the recording crop. Media is retained.");
                    Environment.Exit(2);
                }
                canvas?.Close();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
        GC.KeepAlive(application);
        return exit;
    }

    private static RecordedCameraPlacement Placement(Rectangle area, double time) =>
        new(time, (double)area.X / Width, (double)area.Y / Height, (double)area.Width / Width, (double)area.Height / Height, (double)Width / Height);

    private static BitmapSource ColorFrame(int mode)
    {
        const int width = 280, height = 190;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var color = x < width / 2 ? LeftColors[mode] : RightColors[mode];
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)color.B; pixels[offset + 1] = (byte)color.G; pixels[offset + 2] = (byte)color.R;
            }
        var frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        frame.Freeze();
        return frame;
    }

    private static Rgb Pixel(byte[] frame, int x, int y)
    {
        int offset = (y * Width + x) * 4;
        return new(frame[offset + 2], frame[offset + 1], frame[offset]);
    }
    private static bool Near(Rgb actual, Rgb expected) => Math.Abs(actual.R - expected.R) <= 30
        && Math.Abs(actual.G - expected.G) <= 30 && Math.Abs(actual.B - expected.B) <= 30;
    private static int Mode(Rgb left, Rgb right) => Enumerable.Range(0, LeftColors.Length)
        .FirstOrDefault(mode => Near(left, RightColors[mode]) && Near(right, LeftColors[mode]), -1);
    private static Rectangle? DetectRectangle(byte[] frame, Rectangle area)
    {
        bool IsCamera(Rgb color) => LeftColors.Concat(RightColors).Any(expected => Near(color, expected));
        var columns = Enumerable.Range(0, Width).Where(x => IsCamera(Pixel(frame, x, area.Y + area.Height / 2))).ToArray();
        var rows = Enumerable.Range(0, Height).Where(y => IsCamera(Pixel(frame, area.X + area.Width / 4, y))).ToArray();
        return columns.Length == 0 || rows.Length == 0 ? null : new(columns[0], rows[0], columns[^1] - columns[0] + 1, rows[^1] - rows[0] + 1);
    }
    private static bool MatchesRectangle(Rectangle? actual, Rectangle expected) => actual is not null
        && Math.Abs(actual.X - expected.X) <= 2 && Math.Abs(actual.Y - expected.Y) <= 2
        && Math.Abs(actual.Width - expected.Width) <= 2 && Math.Abs(actual.Height - expected.Height) <= 2;

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);
}
