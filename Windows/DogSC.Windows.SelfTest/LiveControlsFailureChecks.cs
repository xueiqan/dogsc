using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DogSC.Core;
using DogSC.Windows;

namespace DogSC.Windows.SelfTest;

internal static class LiveControlsFailureChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run(string root)
    {
        root = Path.GetFullPath(root);
        string allowed = Path.GetFullPath(@"D:\github\dogsc\Windows\artifacts\live-controls-20261015") + Path.DirectorySeparatorChar;
        if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("实时控制故障验证只能写入专用目录子目录。");
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("实时控制故障验证目录必须为空。");
        var application = LiveMainChecks.CreateApplication();
        var dispatcher = Dispatcher.CurrentDispatcher;
        int exit = 1;
        _ = dispatcher.InvokeAsync(async () =>
        {
            var cases = new List<string>();
            MainWindow? main = null;
            RecordingSession? fake = null;
            var discovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Check(bool valid, string name)
            {
                if (!valid) throw new IOException("FAIL: " + name);
                cases.Add(name); Console.WriteLine("PASS: " + name);
            }
            try
            {
                main = new MainWindow();
                await Field<Task>(main, "_deviceDiscovery")!.WaitAsync(TimeSpan.FromSeconds(20));
                Control<TextBox>(main, "FolderBox").Text = root;
                main.ShowRecordingToolbar();
                var bar = Field<RecordingToolbarWindow>(main, "_toolbar")!;
                var camera = Control<ComboBox>(main, "CameraCombo");
                camera.SelectedIndex = 0;
                await Field<Task>(main, "_cameraConfiguration")!.WaitAsync(TimeSpan.FromSeconds(5));
                // Avoid opening real hardware if the stale-selection regression returns.
                Set(main, "_initialized", false);
                camera.ItemsSource = null;
                camera.Items.Clear();
                var off = new CaptureDeviceInfo("", "关闭摄像头");
                camera.Items.Add(off); camera.Items.Add(new CaptureDeviceInfo("owned-nonexistent-camera", "controlled camera"));
                camera.SelectedItem = off;
                Set(main, "_deviceDiscovery", discovery.Task);
                Set(main, "_recordingActive", true); Set(main, "_recordingState", RecordingState.Recording);
                var enabling = (Task)Invoke(main, "SetCameraEnabledAsync", true)!;
                Check(!enabling.IsCompleted && Field<bool>(main, "_cameraTogglePending"), "camera enable is waiting on controlled discovery");
                int oldRevision = Field<int>(main, "_cameraToggleRevision");
                await ((Task)Invoke(main, "CloseRecordingCameraAsync")!).WaitAsync(TimeSpan.FromSeconds(5));
                Check(Field<int>(main, "_cameraToggleRevision") > oldRevision
                    && !Field<bool>(main, "_cameraTogglePending") && !Field<bool>(main, "_cameraConfiguring"),
                    "recording camera shutdown invalidates pending camera requests and clears pending state");
                Set(main, "_recordingActive", false); Set(main, "_recordingState", null);
                main.ShowRecordingToolbar();
                discovery.SetResult();
                await enabling.WaitAsync(TimeSpan.FromSeconds(5));
                await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(ReferenceEquals(camera.SelectedItem, off) && Field<CameraPreviewWindow>(main, "_cameraPreviewWindow") is null
                    && Field<CameraPreviewSession>(main, "_idleCameraPreview") is null,
                    "late camera enable cannot change selection or reopen preview after stop and toolbar reopening");
                Set(main, "_initialized", true);
                main.RequestExit(); main = null;

                main = new MainWindow();
                await Field<Task>(main, "_deviceDiscovery")!.WaitAsync(TimeSpan.FromSeconds(20));
                Control<TextBox>(main, "FolderBox").Text = root;
                Set(main, "_syncingConfiguration", true);
                Control<CheckBox>(main, "SystemAudioCheck").IsChecked = false;
                Control<CheckBox>(main, "MicrophoneCheck").IsChecked = false;
                Control<Slider>(main, "SystemVolumeSlider").Value = 1;
                Control<Slider>(main, "MicrophoneVolumeSlider").Value = 1;
                Set(main, "_syncingConfiguration", false);
                main.ShowRecordingToolbar(); bar = Field<RecordingToolbarWindow>(main, "_toolbar")!;
                fake = new RecordingSession();
                Set(fake, "state", RecordingState.Recording);
                Set(fake, "audioTrackEnabled", true);
                Set(main, "_session", fake); Set(main, "_recordingState", RecordingState.Recording); Set(main, "_recordingActive", true);
                Set(main, "_project", ProjectStore.Create(root, "controlled-audio-rejection", new("screen", "controlled", 30, true, false, null)));
                Set(main, "_appliedAudio", (true, false, 1d, 1d));
                main.RefreshMediaControls();
                Check(!bar.SystemAudio && !bar.Microphone, "requested sound change disables both sources before native rejection");
                Check(!await fake.UpdateAudioAsync(false, false, 1, 1), "controlled recorder with no native instance rejects its dynamic audio update");
                Invoke(main, "QueueLiveAudioUpdate");
                await Field<Task>(main, "_liveAudioConfiguration")!.WaitAsync(TimeSpan.FromSeconds(5));
                Check(Control<CheckBox>(main, "SystemAudioCheck").IsChecked == true && bar.SystemAudio,
                    "rejected native update restores the previously applied system-audio selection in both windows");
                Check(!Field<bool>(main, "_audioConfiguring") && !string.IsNullOrWhiteSpace(Field<string>(main, "_systemRecordingError")),
                    "audio request finishes without clearing its persistent failure state");
                Check(Equals(Control<CheckBox>(bar, "SystemAudioCheck").Tag, "configuring"),
                    "failed source remains gray instead of falsely reporting green readiness");
                Check(Control<TextBlock>(main, "StatusText").Text.Contains("声音设置")
                    && !string.IsNullOrWhiteSpace(Control<TextBlock>(bar, "StatusText").Text),
                    "native sound-setting rejection remains visible to the user");
                Invoke(main, "UpdateDeviceStates");
                Check(Equals(Control<CheckBox>(bar, "SystemAudioCheck").Tag, "configuring")
                    && !string.IsNullOrWhiteSpace(Field<string>(main, "_systemRecordingError")),
                    "a later device refresh does not erase the failed source indicator");
                Check(ReferenceEquals(Field<RecordingSession>(main, "_session"), fake)
                    && Field<RecordingState?>(main, "_recordingState") == RecordingState.Recording,
                    "sound rejection leaves the screen recording session and lifecycle intact");
                File.WriteAllText(Path.Combine(root, "live-controls-failure-result.json"), JsonSerializer.Serialize(new
                { success = true, checkedAt = DateTimeOffset.Now, actualCapture = false, controlledDeviceDiscovery = true,
                    controlledNativeRecorderUnavailable = true, wpfControlsUsed = true, checks = cases.Count, cases },
                    new JsonSerializerOptions { WriteIndented = true }));
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                discovery.TrySetResult();
                if (main is not null)
                {
                    // Remove only fixture state so teardown cannot start native stop or auxiliary capture.
                    Set(main, "_recordingActive", false); Set(main, "_recordingState", null); Set(main, "_session", null);
                    main.RequestExit();
                }
                fake?.Dispose();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run(); GC.KeepAlive(application); return exit;
    }

    private static T? Field<T>(object target, string name) => (T?)target.GetType().GetField(name, Private)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Invoke(MainWindow main, string name, params object[] arguments) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(main, arguments);
    private static T Control<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
}
