using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DogSC.Windows;
using DogSC.Core;

internal static class SimpleRecordingCheck
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<string> Checks = [];
    private sealed record VisibilityChange(bool Visible, string? State, DateTimeOffset At);
    private sealed record SessionResult(bool SystemAudio, bool Microphone, bool Camera, DateTimeOffset StartedAt, SavedRecording Saved,
        double ActiveSeconds, double DecodedSeconds, double PauseDriftMilliseconds, long VideoBytes, long? AudioBytes,
        string VideoSha256, string SourceVideoSha256, string? AudioSha256, int Width, int Height, VisibilityChange[] VisibilityChanges,
        RecordedCameraPlacement[] CameraPlacements, double? HiddenCameraStartSeconds, double? HiddenCameraEndSeconds,
        double SaveSeconds, int CameraPreviewFrames);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Checks.Add(message);
    }
    private static T? Get<T>(MainWindow main, string name) => (T?)typeof(MainWindow).GetField(name, Private)!.GetValue(main);
    private static T Control<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void ClickStartStop(RecordingToolbarWindow bar) =>
        Control<Button>(bar, "StartStopButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static async Task RunAsync(bool camera = false, int frameRate = 30, bool processAudio = false, bool recoverable = false,
        bool microphone = false, int phaseMilliseconds = 1400, bool pixPin = false, bool composeFinal = true, int recordingHeight = 0,
        string? outputRoot = null, bool closeDuringSave = false)
    {
        if (phaseMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(phaseMilliseconds));
        Checks.Clear();
        var display = CaptureCatalog.GetDisplays().FirstOrDefault() ?? throw new InvalidOperationException("没有可验证的显示器。");
        string root = outputRoot is not null ? Path.GetFullPath(outputRoot) : Path.Combine(@"D:\github\dogsc\Windows\artifacts\simple-recording",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        string recordings = Path.Combine(root, "recordings");
        Directory.CreateDirectory(recordings);
        var text = new TextBlock { Foreground = Brushes.White, FontSize = 44, Margin = new Thickness(50), TextWrapping = TextWrapping.Wrap };
        var canvas = new Window
        {
            Title = processAudio ? "DogSC编辑器 · owned simple process audio pattern" : "DogSC owned simple recording pattern",
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Background = Brushes.DarkSlateBlue, Content = text
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        int frame = 0;
        timer.Tick += (_, _) =>
        {
            text.Text = $"DogSC · 程序生成录制验证画面\nFrame {++frame}\n系统声音分别关闭/开启；麦克风{(microphone ? "开启" : "关闭")}。";
            canvas.Background = frame % 30 < 15 ? Brushes.DarkSlateBlue : Brushes.Teal;
        };
        var previousMain = Application.Current.MainWindow;
        string tone = CreateTone(root);
        var main = new MainWindow();
        Application.Current.MainWindow = main;
        bool mainClosed = false;
        main.Closed += (_, _) => mainClosed = true;
        RecordingToolbarWindow? bar = null;
        var results = new List<SessionResult>();
        var visibility = new List<VisibilityChange>();
        string? selectedCameraId = null;
        try
        {
            canvas.Show();
            nint handle = new WindowInteropHelper(canvas).Handle;
            Check(SetWindowPos(handle, -1, display.Left, display.Top, display.Width, display.Height, 0x0040),
                "generated pattern is placed on the physical test display");
            CheckCoverage(canvas, display);
            timer.Start();
            await Task.Delay(150);
            Check(Control<TextBox>(main, "FolderBox").Text == @"D:\视频", "production default recording folder is the requested D drive directory");
            Control<TextBox>(main, "FolderBox").Text = recordings;
            Control<TextBox>(main, "TitleBox").Text = "旧标题不能出现在正常录制名称中";
            Control<CheckBox>(main, "RecoverableCheck").IsChecked = recoverable;
            Control<CheckBox>(main, "ProcessAudioCheck").IsChecked = processAudio;
            await Get<Task>(main, "_deviceDiscovery")!.WaitAsync(TimeSpan.FromSeconds(20));
            main.ShowRecordingToolbar();
            bar = Get<RecordingToolbarWindow>(main, "_toolbar")!;
            await DrainAsync();
            Check(!bar.IsCaptureSafe && !bar.QuietMode, "idle toolbar allows screenshots and quiet mode is not enabled");
            var toolbarError = typeof(RecordingToolbarWindow).GetField("_error", Private)!;
            toolbarError.SetValue(bar, "悬浮条排除录屏失败，controlled");
            bar.SetCaptureExcluded(false);
            Check(toolbarError.GetValue(bar) is null, "successful capture exclusion recovery clears only its own stale error");
            toolbarError.SetValue(bar, "来源载入失败 controlled");
            bar.SetCaptureExcluded(false);
            Check((string?)toolbarError.GetValue(bar) == "来源载入失败 controlled", "capture exclusion recovery retains unrelated source errors");
            toolbarError.SetValue(bar, null); bar.Update(TimeSpan.Zero, null);
            var area = SystemParameters.WorkArea;
            bar.Left = area.Left + 40;
            bar.Top = area.Top + 80;
            await DrainAsync();
            bar.IsVisibleChanged += (_, _) => visibility.Add(new(bar.IsVisible,
                Get<RecordingState?>(main, "_recordingState")?.ToString(), DateTimeOffset.Now));
            if (camera)
            {
                var cameraButton = Control<CheckBox>(bar, "CameraButton");
                cameraButton.IsChecked = true;
                cameraButton.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                await Get<Task>(main, "_cameraToggleConfiguration")!.WaitAsync(TimeSpan.FromSeconds(20));
                await Get<Task>(main, "_cameraConfiguration")!.WaitAsync(TimeSpan.FromSeconds(20));
                Check(Control<ComboBox>(main, "CameraCombo").SelectedItem is CaptureDeviceInfo { Id.Length: > 0 },
                    "optional actual camera has an available selected device");
                selectedCameraId = ((CaptureDeviceInfo)Control<ComboBox>(main, "CameraCombo").SelectedItem).Id;
                await Until(main, () => PreviewPicture(main)?.Source is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true },
                    TimeSpan.FromSeconds(15), "selected camera does not deliver an idle preview frame");
                var preview = Get<CameraPreviewWindow>(main, "_cameraPreviewWindow")!;
                Check(preview.IsVisible && !preview.IsCaptureSafe && preview.GetScreenBounds().Width == 560 && preview.GetScreenBounds().Height == 380,
                    "selected actual camera automatically shows a screenshot-visible 560 by 380 physical pixel preview");
                main.ToggleRecordingToolbar();
                await Until(main, () => Get<CameraPreviewSession>(main, "_idleCameraPreview") is null
                    && Get<CameraPreviewWindow>(main, "_cameraPreviewWindow") is null, TimeSpan.FromSeconds(10),
                    "idle toolbar hiding closes the camera window and releases the preview device", duringRecording: false);
                Check(((CaptureDeviceInfo)Control<ComboBox>(main, "CameraCombo").SelectedItem).Id == selectedCameraId
                    && Control<CheckBox>(bar, "CameraButton").IsChecked == true,
                    "idle camera shutdown retains the selected device and enabled preference");
                main.ToggleRecordingToolbar();
                await Get<Task>(main, "_cameraConfiguration")!.WaitAsync(TimeSpan.FromSeconds(20));
                await Until(main, () => PreviewPicture(main)?.Source is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true }
                    && Get<CameraPreviewWindow>(main, "_cameraPreviewWindow")!.IsVisible, TimeSpan.FromSeconds(15),
                    "idle toolbar reopening restores live preview from the selected camera", duringRecording: false);
            }

            var target = processAudio ? CaptureCatalog.GetWindows().Single(item => item.WindowHandle == handle) : display;
            if (processAudio) Check(CaptureCatalog.GetWindowProcessId(target) == (uint)Environment.ProcessId,
                "process audio fixture selects only its own generated window and PID");
            bar.Configure(processAudio ? "window" : "screen", target, frameRate, recordingHeight, false, microphone, composeFinal);
            await Get<Task>(main, "_microphoneConfiguration")!.WaitAsync(TimeSpan.FromSeconds(20));
            await DrainAsync();
            var position = Bounds(bar);
            void LogToolbar(string stage)
            {
                var bounds = Bounds(bar);
                Console.WriteLine($"TOOLBAR {stage}: same={ReferenceEquals(bar, Get<RecordingToolbarWindow>(main, "_toolbar"))}, "
                    + $"positioned={typeof(RecordingToolbarWindow).GetField("_positioned", Private)!.GetValue(bar)}, "
                    + $"visible={bar.IsVisible}, dip=({bar.Left},{bar.Top}), physical=({bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom})");
            }

            foreach (bool systemAudio in new[] { false, true })
            {
                Console.WriteLine($"LIVE systemAudio={systemAudio}: starting actual {2 * phaseMilliseconds / 1000}s capture; camera={camera}, fps={frameRate}");
                bool audioExpected = systemAudio || microphone;
                if (camera && bar.IsVisible && Get<CameraPreviewSession>(main, "_idleCameraPreview") is null)
                    main.ToggleRecordingToolbar();
                main.ShowRecordingToolbar();
                CameraPreviewSession? previewSession = null;
                int previews = 0;
                if (camera)
                {
                    await Get<Task>(main, "_cameraConfiguration")!.WaitAsync(TimeSpan.FromSeconds(20));
                    await Until(main, () => PreviewPicture(main)?.Source is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true }
                        && Get<CameraPreviewSession>(main, "_idleCameraPreview") is not null, TimeSpan.FromSeconds(15),
                        "selected camera delivers live preview before each recording", duringRecording: false);
                    previewSession = Get<CameraPreviewSession>(main, "_idleCameraPreview")!;
                    previewSession.FrameReady += _ => Interlocked.Increment(ref previews);
                    await Until(main, () => Volatile.Read(ref previews) >= 2, TimeSpan.FromSeconds(10),
                        "camera preview delivers frames before native screen capture", duringRecording: false);
                }
                LogToolbar($"systemAudio={systemAudio} before-configure");
                bar.Configure(processAudio ? "window" : "screen", target, frameRate, recordingHeight, systemAudio, microphone, composeFinal);
                await Get<Task>(main, "_microphoneConfiguration")!.WaitAsync(TimeSpan.FromSeconds(20));
                await DrainAsync();
                LogToolbar($"systemAudio={systemAudio} before-start");
                Check(bar.SystemAudio == systemAudio && bar.Microphone == microphone && bar.CaptureKind == (processAudio ? "window" : "screen")
                    && bar.FrameRate == frameRate,
                    $"system audio {systemAudio}: toolbar configuration is applied");
                if (microphone) Check(Control<ComboBox>(main, "InputDeviceCombo").SelectedItem is CaptureDeviceInfo { Id.Length: > 0 }
                    || !string.IsNullOrEmpty(Get<string>(main, "_preferredMicrophoneId")), "requested default microphone resolves to an available device");
                CheckCoverage(canvas, display);
                visibility.Clear();
                DateTimeOffset requestedAt = DateTimeOffset.Now;
                ClickStartStop(bar);
                await Until(main, () => visibility.Any(change => !change.Visible && change.State == nameof(RecordingState.Starting)),
                    TimeSpan.FromSeconds(30), "configuration does not finish before automatic startup hiding");
                Check(visibility.Any(change => !change.Visible && change.State == nameof(RecordingState.Starting)),
                    $"system audio {systemAudio}: toolbar automatically hides in Starting");
                await Until(main, () => Get<RecordingState?>(main, "_recordingState") == RecordingState.Recording,
                    TimeSpan.FromSeconds(30), "normal recording does not reach Recording");
                Check(!main.IsVisible && !bar.IsVisible, $"system audio {systemAudio}: native start leaves both controls hidden");
                var session = Get<RecordingSession>(main, "_session")!;
                Check(bar.ComposeFinal == composeFinal && Control<CheckBox>(main, "ComposeFinalCheck").IsChecked == composeFinal,
                    "native startup preserves the synchronized composition selection");
                Check(!Control<CheckBox>(bar, "ComposeFinalCheck").IsEnabled && !Control<CheckBox>(main, "ComposeFinalCheck").IsEnabled,
                    "both composition controls are disabled during capture");
                DateTimeOffset startedAt = Get<DateTimeOffset?>(main, "_recordingStartedAt")
                    ?? throw new InvalidOperationException("实际录制开始时间没有建立。");
                Check(startedAt >= requestedAt.AddSeconds(-.1) && startedAt <= DateTimeOffset.Now,
                    $"system audio {systemAudio}: naming timestamp belongs to this actual start");
                double? hiddenCameraStart = null, hiddenCameraEnd = null;
                if (systemAudio)
                {
                    // SoundPlayer's 200 ms start cue shares PlaySound's process channel.
                    await Task.Delay(250);
                    Check(PlaySound(tone, 0, 0x00020009), "owned loopback tone begins after the startup cue");
                }
                if (camera)
                {
                    Check(ReferenceEquals(previewSession, Get<CameraPreviewSession>(main, "_idleCameraPreview"))
                        && Get<object>(main, "_cameraOverlay") is not null,
                        "screen recording retains the same live camera preview and enables its native overlay");
                    int beforeRecordingFrames = Volatile.Read(ref previews);
                    await Until(main, () => Volatile.Read(ref previews) >= beforeRecordingFrames + 2, TimeSpan.FromSeconds(10),
                        "the preexisting camera session continues delivering frames after native screen capture starts");
                    var preview = Get<CameraPreviewWindow>(main, "_cameraPreviewWindow")!;
                    Check(!bar.IsVisible && preview.IsVisible && preview.IsPreviewVisible,
                        "automatic startup hides only the toolbar and keeps the selected camera preview visible");
                    Check(Control<CheckBox>(bar, "CameraButton").IsChecked == true
                        && Control<ComboBox>(main, "CameraCombo").SelectedItem is CaptureDeviceInfo { Id.Length: > 0 },
                        "starting capture preserves the enabled camera in both settings and toolbar");
                    var startupCameraBounds = preview.GetScreenBounds();
                    main.ShowRecordingToolbar();
                    await DrainAsync();
                    Check(bar.IsVisible && preview.IsVisible && preview.GetScreenBounds() == startupCameraBounds,
                        "showing the recording toolbar leaves the independent camera preview at its recorded position");
                    Check(SetWindowPos(new WindowInteropHelper(preview).Handle, -1, display.Left + 140, display.Top + 220, 560, 380, 0x0010),
                        "camera preview moves to the first recording position");
                    await DrainAsync();
                    var cameraBounds = preview.GetScreenBounds();
                    var placementsBeforeHide = Get<List<RecordedCameraPlacement>>(main, "_cameraPlacements")!.ToArray();
                    hiddenCameraStart = session.Duration.TotalSeconds;
                    preview.SetPreviewVisible(false);
                    await DrainAsync();
                    Check(!preview.IsVisible && !preview.IsPreviewVisible && VisibilityControl(preview)?.IsVisible == true
                        && preview.GetScreenBounds() == cameraBounds,
                        "eye hides the active camera preview without changing its recorded rectangle");
                    main.ToggleRecordingToolbar();
                    await DrainAsync();
                    Check(!bar.IsVisible && !preview.IsVisible && VisibilityControl(preview)?.IsVisible == true
                        && !preview.IsPreviewVisible && preview.GetScreenBounds() == cameraBounds,
                        "hiding the toolbar during capture preserves the manually hidden camera and its closed-eye control");
                    main.ToggleRecordingToolbar();
                    await DrainAsync();
                    Check(bar.IsVisible && !preview.IsVisible && VisibilityControl(preview)?.IsVisible == true
                        && !preview.IsPreviewVisible && preview.GetScreenBounds() == cameraBounds,
                        "reopening the toolbar preserves the hidden-preview choice and restores only its closed-eye control");
                    int beforeHiddenFrames = Volatile.Read(ref previews);
                    await Task.Delay(350);
                    await Until(main, () => Volatile.Read(ref previews) >= beforeHiddenFrames + 3,
                        TimeSpan.FromSeconds(5), "hidden camera continues delivering actual capture frames");
                    hiddenCameraEnd = session.Duration.TotalSeconds;
                    Check(ReferenceEquals(previewSession, Get<CameraPreviewSession>(main, "_idleCameraPreview"))
                        && Get<object>(main, "_cameraOverlay") is not null && hiddenCameraEnd - hiddenCameraStart >= .3
                        && Get<List<RecordedCameraPlacement>>(main, "_cameraPlacements")!.SequenceEqual(placementsBeforeHide),
                        "hiding only the desktop camera leaves live overlay frames and recorded placement unchanged");
                    preview.SetPreviewVisible(true);
                    await DrainAsync();
                    Check(preview.IsVisible && preview.IsPreviewVisible && preview.GetScreenBounds() == cameraBounds,
                        "eye restores the active preview at its original recorded rectangle");
                    main.ToggleRecordingToolbar();
                    await DrainAsync();
                    Check(!bar.IsVisible && preview.IsVisible && preview.IsPreviewVisible && VisibilityControl(preview)?.IsVisible != true,
                        "hiding the toolbar keeps the restored camera preview visible while actual recording continues");
                }
                main.ToggleRecordingToolbar(); await DrainAsync();
                var restoredBounds = Bounds(bar);
                LogToolbar($"systemAudio={systemAudio} restored");
                Check(bar.IsVisible && restoredBounds.Equals(position),
                    $"system audio {systemAudio}: toggle shows original position while recording; visible={bar.IsVisible}, "
                    + $"expected=({position.Left},{position.Top},{position.Right},{position.Bottom}), "
                    + $"actual=({restoredBounds.Left},{restoredBounds.Top},{restoredBounds.Right},{restoredBounds.Bottom})");
                if (camera) Check(Get<CameraPreviewWindow>(main, "_cameraPreviewWindow")!.IsVisible,
                    "recording toolbar toggle preserves the independently visible camera preview");
                main.ToggleRecordingToolbar(); await DrainAsync();
                Check(!bar.IsVisible, $"system audio {systemAudio}: toggle hides while recording");
                if (camera) Check(Get<CameraPreviewWindow>(main, "_cameraPreviewWindow")!.IsVisible,
                    "recording toolbar toggle leaves the camera preview visible");
                main.ToggleRecordingToolbar(); await DrainAsync();
                Check(bar.IsVisible && Bounds(bar).Equals(position), $"system audio {systemAudio}: another toggle restores original position");
                if (camera) Check(Get<CameraPreviewWindow>(main, "_cameraPreviewWindow")!.IsVisible,
                    "another toolbar toggle preserves the camera preview while capture remains active");
                await Task.Delay(phaseMilliseconds);
                if (pixPin)
                {
                    if (!systemAudio) { main.ToggleRecordingToolbar(); await DrainAsync(); }
                    bool screenshotToolbarVisible = bar.IsVisible;
                    bool screenshotCameraVisible = camera && Get<CameraPreviewWindow>(main, "_cameraPreviewWindow")!.IsVisible;
                    var guard = Get<PixPinScreenshotGuard>(main, "_screenshotGuard")!;
                    File.WriteAllText(Path.Combine(root, "pixpin-before-shortcut.json"), JsonSerializer.Serialize(guard.Snapshot()));
                    Check(guard.IsStarted && (bool)typeof(PixPinScreenshotGuard).GetField("printEnabled", Private)!.GetValue(guard)!
                        && (bool)typeof(PixPinScreenshotGuard).GetField("copyEnabled", Private)!.GetValue(guard)!, "both configured PixPin shortcuts have an active guard");
                    Check(new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c }.All(key => (GetAsyncKeyState(key) & 0x8000) == 0),
                        "no physical modifier is held before the owned PixPin shortcut");
                    Check(!PixPinScreenshotGuard.IsScreenshotVisible(), "no existing PixPin screenshot page is open before the owned check");
                    CheckCoverage(canvas, display);
                    if (systemAudio) SendCopyShortcut(); else SendKey(0x2c, true);
                    await Until(main, () => Get<RecordingState?>(main, "_recordingState") == RecordingState.Paused
                        && PixPinScreenshotGuard.IsScreenshotVisible(), TimeSpan.FromSeconds(8), "PixPin shortcut did not pause capture before opening the screenshot page");
                    Check(!bar.IsCaptureSafe && (!camera || !Get<CameraPreviewWindow>(main, "_cameraPreviewWindow")!.IsCaptureSafe),
                        "PixPin screenshot phase allows toolbar and camera capture");
                    Check(bar.IsVisible == screenshotToolbarVisible
                        && (!camera || Get<CameraPreviewWindow>(main, "_cameraPreviewWindow")!.IsVisible == screenshotCameraVisible),
                        "automatic screenshot pause preserves hidden or visible toolbar and camera without popping them up");
                    TimeSpan screenshotAt = session.Duration;
                    await Task.Delay(750);
                    Check(Math.Abs((session.Duration - screenshotAt).TotalMilliseconds) < 50, "automatic screenshot pause excludes screenshot time");
                    SendKey(0x1b);
                    await Until(main, () => Get<RecordingState?>(main, "_recordingState") == RecordingState.Recording
                        && !PixPinScreenshotGuard.IsScreenshotVisible(), TimeSpan.FromSeconds(8), "capture did not automatically resume after cancelling the owned PixPin screenshot");
                    Check(bar.IsCaptureSafe && (!camera || Get<CameraPreviewWindow>(main, "_cameraPreviewWindow")!.IsCaptureSafe),
                        "automatic screenshot completion restores clean recording exclusion");
                    Check(bar.IsVisible == screenshotToolbarVisible
                        && (!camera || Get<CameraPreviewWindow>(main, "_cameraPreviewWindow")!.IsVisible == screenshotCameraVisible),
                        "automatic screenshot resume preserves the original toolbar and camera visibility");
                    if (camera) Check(ReferenceEquals(previewSession, Get<CameraPreviewSession>(main, "_idleCameraPreview"))
                        && Get<object>(main, "_cameraOverlay") is not null,
                        "automatic screenshot pause/resume preserves the camera session and native overlay");
                    if (!screenshotToolbarVisible) { main.ToggleRecordingToolbar(); await DrainAsync(); }
                }
                main.TogglePause();
                await Until(main, () => Get<RecordingState?>(main, "_recordingState") == RecordingState.Paused,
                    TimeSpan.FromSeconds(5), "pause does not arrive");
                Check(Control<Button>(bar, "PauseButton").Content.ToString()!.Contains("继续"), "paused toolbar displays Continue");
                TimeSpan pausedAt = session.Duration;
                int beforePauseFrames = Volatile.Read(ref previews);
                await Task.Delay(650);
                double drift = (session.Duration - pausedAt).TotalMilliseconds;
                Check(Math.Abs(drift) < 150, "paused duration excludes the pause interval");
                if (camera) Check(ReferenceEquals(previewSession, Get<CameraPreviewSession>(main, "_idleCameraPreview"))
                    && Get<object>(main, "_cameraOverlay") is not null && Volatile.Read(ref previews) >= beforePauseFrames + 2,
                    "manual recording pause retains the device session and continuous live preview frames");
                if (pixPin)
                {
                    SendKey(0x2c, true);
                    await Until(main, PixPinScreenshotGuard.IsScreenshotVisible, TimeSpan.FromSeconds(5), "manually paused recording does not allow PixPin screenshot");
                    SendKey(0x1b);
                    await Until(main, () => !PixPinScreenshotGuard.IsScreenshotVisible(), TimeSpan.FromSeconds(5), "owned manually paused screenshot did not close");
                    await Task.Delay(450);
                    Check(Get<RecordingState?>(main, "_recordingState") == RecordingState.Paused,
                        "a screenshot while manually paused never auto-resumes the recording");
                }
                main.TogglePause();
                await Until(main, () => Get<RecordingState?>(main, "_recordingState") == RecordingState.Recording,
                    TimeSpan.FromSeconds(5), "resume does not arrive");
                Check(bar.IsVisible && Bounds(bar).Equals(position), "resume does not repeat the startup auto-hide");
                Check(!Control<Button>(bar, "PauseButton").Content.ToString()!.Contains("继续"), "resumed toolbar no longer displays Continue");
                Check(Get<DateTimeOffset?>(main, "_recordingStartedAt") == startedAt, "resume retains the first real start timestamp");
                if (camera)
                {
                    var preview = Get<CameraPreviewWindow>(main, "_cameraPreviewWindow")!;
                    Check(SetWindowPos(new WindowInteropHelper(preview).Handle, -1, display.Left + 980, display.Top + 660, 560, 380, 0x0010),
                        "camera preview moves to a second recording position after resume");
                }
                await Task.Delay(phaseMilliseconds);
                CheckCoverage(canvas, display);
                var saving = Stopwatch.StartNew();
                ClickStartStop(bar);
                if (camera)
                {
                    await DrainAsync();
                    Check(Get<CameraPreviewSession>(main, "_idleCameraPreview") is null
                        && Get<CameraPreviewWindow>(main, "_cameraPreviewWindow") is null,
                        "stop immediately closes the camera window and clears the active device session before saving finishes");
                }
                var savingTask = Get<Task>(main, "_recordingTask")!;
                if (closeDuringSave && systemAudio)
                {
                    Check(!savingTask.IsCompleted && Get<bool>(main, "_recordingActive"), "close is requested while the actual recording is still being saved");
                    await DogSC.Windows.SelfTest.SaveCloseChecks.CloseDuringSavingAsync(main);
                }
                await savingTask.WaitAsync(TimeSpan.FromSeconds(120));
                if (closeDuringSave && systemAudio)
                {
                    var closing = Stopwatch.StartNew();
                    while (!mainClosed && closing.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
                    Check(mainClosed && Get<bool>(main, "_recordingSaved"), "closing during real saving waits for complete files and then exits normally");
                }
                saving.Stop();
                Console.WriteLine($"SAVE systemAudio={systemAudio}: {saving.Elapsed.TotalSeconds:F3}s after stop");
                Console.WriteLine($"LIVE systemAudio={systemAudio}: recording task completed, validating actual saved media");
                if (systemAudio) PlaySound(null, 0, 0);
                Check(Get<bool>(main, "_recordingSaved") && !Get<bool>(main, "_recordingActive"),
                    $"system audio {systemAudio}: complete normal recording unlocks the UI");
                Check(Get<ProjectDocument>(main, "_project") is { State: "complete" }, "normal completion retains a complete independent source project");
                if (camera)
                {
                    Check(Get<CameraPreviewSession>(main, "_idleCameraPreview") is null
                        && Get<CameraPreviewWindow>(main, "_cameraPreviewWindow") is null,
                        "completed camera recording does not automatically reopen the device or preview");
                    Check(((CaptureDeviceInfo)Control<ComboBox>(main, "CameraCombo").SelectedItem).Id == selectedCameraId
                        && Control<CheckBox>(bar, "CameraButton").IsChecked == true,
                        "stop and save retain camera selection without restarting it");
                    Check(Get<List<RecordedCameraPlacement>>(main, "_cameraPlacements")!.Count >= 3,
                        "recording retains the initial and dragged native camera overlay positions");
                }
                var saved = Get<SavedRecording>(main, "_savedRecording") ?? throw new InvalidOperationException("正常录制保存结果缺失。");
                using (var options = JsonDocument.Parse(File.ReadAllText(Path.Combine(saved.Project!, "capture-options.json"))))
                    Check(options.RootElement.GetProperty("composeFinal").GetBoolean() == composeFinal, "source capture options retain the selected composition mode");
                using (var events = JsonDocument.Parse(File.ReadAllText(Path.Combine(saved.Project!, "events", "recording-events.json"))))
                {
                    Check(events.RootElement.GetProperty("embeddedCursor").GetBoolean(), "both recording modes embed the actual system cursor without redrawing it");
                    Check(events.RootElement.GetProperty("cursorTimerTicks").GetInt64() == 0, "native cursor recording does not poll cursor appearance");
                }
                var savedScene = JsonSerializer.Deserialize<EditorState>(File.ReadAllText(Path.Combine(saved.Project!, "editor.json")))!;
                Check(composeFinal || savedScene.ZoomClips.Length == 0, "basic mode does not generate automatic zoom");
                Check(!savedScene.Camera.Enabled, "native embedded camera does not enable a second independent camera layer");
                Check(savedScene.CameraPlacements.SequenceEqual(Get<List<RecordedCameraPlacement>>(main, "_cameraPlacements")!),
                    "saved scene preserves the native camera placement history");
                Check(!File.Exists(Path.Combine(saved.Project!, "media", "camera.mp4"))
                    && !File.Exists(Path.Combine(saved.Project!, "media", "camera.mov"))
                    && !File.Exists(Path.Combine(saved.Project!, "media", "camera-sync.json")),
                    "native camera recording writes no independent camera media or camera synchronization file");
                ValidateFiles(saved, recordings, startedAt, audioExpected);
                var original = ProjectStore.Open(saved.Project!);
                string sourceVideoHash = Hash(original.MediaPath), videoHash = Hash(saved.Video);
                Check(original.Capture.FrameRate == frameRate, "retained source capture metadata agrees with the requested screen frame rate");
                if (!composeFinal && !processAudio) Check(sourceVideoHash == videoHash,
                    "basic full-screen saving copies the native screen/camera/audio MP4 byte for byte without composition");
                Check(Control<TextBlock>(bar, "ClockText").Text == "00:00:00"
                    && Control<TextBlock>(main, "ClockText").Text == "00:00:00", "completion resets both clocks to zero");
                Check(!Control<TextBlock>(bar, "StatusText").Text.Contains("已保存")
                    && !Control<TextBlock>(bar, "StatusText").Text.Contains("已取消"), "idle toolbar clears old save and cancel hints");
                Check(Control<Grid>(bar, "Configuration").IsEnabled && !main.IsVisible, "normal completion restores configuration and leaves settings hidden");
                Marshal.ThrowExceptionForHR(MFStartup(0x20070, 0));
                double decoded; int width, height;
                try
                {
                    using var reader = new MediaFrameReader(saved.Video);
                    _ = reader.FrameAt(0);
                    decoded = reader.Duration;
                    Check(decoded > 0, "saved MP4 exposes a positive playable duration");
                    _ = reader.FrameAt(decoded / 2);
                    width = reader.Width; height = reader.Height;
                    if (!processAudio && recordingHeight == 0) Check(width == display.Width && height == display.Height,
                        "highest full-screen setting preserves the physical display pixel dimensions");
                    Check(Math.Abs(decoded - session.Duration.TotalSeconds) < .5, "decoded MP4 duration agrees with active recording time");
                }
                finally { MFShutdown(); }
                results.Add(new(systemAudio, microphone, camera, startedAt, saved, session.Duration.TotalSeconds, decoded, drift,
                    new FileInfo(saved.Video).Length, saved.Audio is null ? null : new FileInfo(saved.Audio).Length,
                    videoHash, sourceVideoHash, saved.Audio is null ? null : Hash(saved.Audio), width, height, visibility.ToArray(),
                    Get<List<RecordedCameraPlacement>>(main, "_cameraPlacements")!.ToArray(), hiddenCameraStart, hiddenCameraEnd,
                    saving.Elapsed.TotalSeconds, Volatile.Read(ref previews)));
            }
            Check(results[0].Saved.Folder != results[1].Saved.Folder, "successive recordings have distinct start-time directories");
            Check(Directory.GetFileSystemEntries(recordings).Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(results.Select(result => result.Saved.Folder).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
                "normal output parent contains only the two completed timestamp directories");
            if (!mainClosed) main.RequestExit();
            var exiting = Stopwatch.StartNew();
            while (!mainClosed && exiting.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
            Check(mainClosed && !bar.IsVisible, "explicit exit closes all owned recorder controls");
            var report = new { success = true, checkedAt = DateTimeOffset.Now, camera, frameRate, processAudio, recoverable, microphone, phaseMilliseconds,
                composeFinal, recordingHeight, pixPin, closeDuringSave, testToneAfterStartCue = true, recordings, tone,
                source = new { display.DeviceName, display.Left, display.Top, display.Width, display.Height },
                scope = (processAudio ? "Owned generated window recording; own process loopback off/on"
                    : "Physical screen recording with an owned generated window covering the selected display; default system loopback off/on")
                    + (microphone ? "; microphone on" : "; microphone off"),
                checks = Checks.ToArray(), sessions = results.ToArray(), applicationSha256 = Hash(typeof(MainWindow).Assembly.Location) };
            string reportPath = Path.Combine(root, "simple-recording-result.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {Checks.Count}; {reportPath}");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            File.WriteAllText(Path.Combine(root, "failure.json"), JsonSerializer.Serialize(new { error = error.ToString(), camera, frameRate, processAudio, recoverable, microphone, phaseMilliseconds,
                screenshotGuard = Get<PixPinScreenshotGuard>(main, "_screenshotGuard")?.Snapshot(),
                status = Control<TextBlock>(main, "StatusText").Text, state = Get<RecordingState?>(main, "_recordingState")?.ToString(),
                task = Get<Task>(main, "_recordingTask")?.Status.ToString(), checks = Checks.ToArray(), sessions = results.ToArray(), visibility },
                new JsonSerializerOptions { WriteIndented = true }));
            if (pixPin) typeof(MainWindow).GetField("_recordingStartedAt", Private)!.SetValue(main, null);
            throw;
        }
        finally
        {
            var preparing = Stopwatch.StartNew();
            while (Get<Task>(main, "_recordingTask") is { IsCompleted: false } && !Get<bool>(main, "_recordingActive")
                && preparing.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
            if (Get<bool>(main, "_recordingActive"))
            {
                typeof(MainWindow).GetMethod("RequestStop", Private)!.Invoke(main, null);
                try { if (Get<Task>(main, "_recordingTask") is { } pending) await pending.WaitAsync(TimeSpan.FromSeconds(60)); }
                catch (Exception error) { Console.Error.WriteLine($"Cleanup failed with generated screen still covering the display: {error.Message}"); }
                if (Get<bool>(main, "_recordingActive"))
                {
                    Console.Error.WriteLine("Only this fixture process exits while its generated screen still covers the display; incomplete media is preserved.");
                    Environment.Exit(1);
                }
            }
            PlaySound(null, 0, 0);
            if (Get<Task>(main, "_recordingTask") is { IsCompleted: false }) Environment.Exit(1);
            timer.Stop();
            if (!mainClosed) main.RequestExit();
            canvas.Close();
            Application.Current.MainWindow = previousMain;
        }
    }

    private static void ValidateFiles(SavedRecording saved, string parent, DateTimeOffset startedAt, bool audio)
    {
        string name = startedAt.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        Check(saved.Folder == Path.Combine(parent, name) && saved.Video == Path.Combine(saved.Folder, name + ".mp4"),
            "folder and MP4 use the exact actual start second without a title, suffix or GUID");
        Check(audio ? saved.Audio == Path.Combine(saved.Folder, name + ".m4a") : saved.Audio is null,
            "M4A uses the same name only when audio was enabled");
        string[] expected = audio ? [name + ".m4a", name + ".mp4", "素材.dogscproject"] : [name + ".mp4", "素材.dogscproject"];
        Check(Directory.GetFileSystemEntries(saved.Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Order(StringComparer.Ordinal)), "completed recording folder contains final media and one independent source subfolder");
        Check(saved.Project == Path.Combine(saved.Folder, "素材.dogscproject") && ProjectStore.Open(saved.Project).State == "complete",
            "retained source project can be reopened at its final path");
        Check(new FileInfo(saved.Video).Length > 0 && (!audio || new FileInfo(saved.Audio!).Length > 0), "final media files are nonempty");
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static async Task DrainAsync() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static Image? PreviewPicture(MainWindow main) => Get<CameraPreviewWindow>(main, "_cameraPreviewWindow") is { } window
        ? (Image?)typeof(CameraPreviewWindow).GetField("picture", Private)!.GetValue(window) : null;
    private static Window? VisibilityControl(CameraPreviewWindow preview) =>
        (Window?)typeof(CameraPreviewWindow).GetField("visibilityControl", Private)!.GetValue(preview);
    private static async Task Until(MainWindow main, Func<bool> condition, TimeSpan timeout, string message, bool duringRecording = true)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout)
        {
            if (duringRecording && Get<Task>(main, "_recordingTask") is { IsCompleted: true })
                throw new InvalidOperationException(message + ": " + Control<TextBlock>(main, "StatusText").Text);
            await Task.Delay(20);
        }
        Check(condition(), message);
    }
    private static NativeRect Bounds(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds)) throw new InvalidOperationException("Cannot read fixture bounds.");
        return bounds;
    }
    private static void CheckCoverage(Window canvas, CaptureTarget display)
    {
        var bounds = Bounds(canvas);
        Check(bounds.Left == display.Left && bounds.Top == display.Top && bounds.Right - bounds.Left == display.Width
            && bounds.Bottom - bounds.Top == display.Height, "generated window covers the whole physical display before capture");
    }
    private static string CreateTone(string directory)
    {
        string path = Path.Combine(directory, "owned-440hz.wav");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);
        const int samples = 48000 * 10;
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        for (int i = 0; i < samples; i++) writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / 48000) * 4000));
        return path;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private static void SendKey(ushort key, bool extended = false)
    {
        Input[] inputs = [new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = extended ? 1u : 0 } } },
            new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = (extended ? 1u : 0) | 2 } } }];
        Check(SendInput(2, inputs, Marshal.SizeOf<Input>()) == 2, "owned screenshot test key is delivered");
    }
    private static void SendCopyShortcut()
    {
        Input[] inputs = [Key(0x11), Key(0x12), Key(0x41), Key(0x41, 2), Key(0x12, 2), Key(0x11, 2)];
        Check(SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length, "owned Ctrl+Alt+A screenshot shortcut is delivered");
        static Input Key(ushort key, uint flags = 0) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = flags } } };
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? file, nint module, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
}
