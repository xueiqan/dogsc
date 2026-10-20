using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DogSC.Windows.SelfTest;

internal static class ReleaseDataChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var application = LiveMainChecks.CreateApplication();
        var dispatcher = Dispatcher.CurrentDispatcher;
        int exit = 1;
        _ = dispatcher.InvokeAsync(async () =>
        {
            MainWindow? main = null;
            var cases = new List<string>();
            void Check(bool valid, string name)
            {
                if (!valid) throw new Exception("FAIL: " + name);
                cases.Add(name);
            }
            try
            {
                foreach (string scenario in new[] { "empty", "missing", "flush" })
                {
                    string path = Path.Combine(root, scenario + ".fixture");
                    string logPath = Path.Combine(root, scenario + ".log.fixture");
                    var output = scenario == "missing" ? null : new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                    var log = new FileStream(logPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                    log.WriteByte(1);
                    using var session = Create();
                    Set(session, "output", output); Set(session, "diagnosticLog", log);
                    if (scenario == "flush") { output!.WriteByte(1); output.Dispose(); }
                    Finish(session);
                    var failure = scenario == "flush"
                        ? await Failure<ObjectDisposedException>(session.Completion)
                        : await Failure<IOException>(session.Completion);
                    await session.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(3));
                    Check(session.State == RecordingState.Failed && session.Completion.IsFaulted,
                        scenario + ": invalid recording remains a failed recording");
                    Check(session.ReleaseCompletion.IsCompletedSuccessfully,
                        scenario + ": data validation failure does not pretend resources are still owned");
                    if (scenario != "flush") Check(failure.Message.Contains("没有生成视频数据"), scenario + ": data failure explains missing video in Chinese");
                    if (output is not null)
                    {
                        using var reopened = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        Check(reopened.Length == (scenario == "flush" ? 1 : 0), scenario + ": output handle is closed and its file is retained");
                    }
                    using var reopenedLog = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    Check(reopenedLog.Length == 1, scenario + ": diagnostic handle is closed without deleting its file");
                }

                using (var closeFailure = Create())
                {
                    string path = Path.Combine(root, "close-failed.fixture");
                    var output = new FailingCloseStream(path); output.WriteByte(1);
                    Set(closeFailure, "output", output); Finish(closeFailure);
                    _ = await Failure<IOException>(closeFailure.Completion);
                    _ = await Failure<IOException>(closeFailure.ReleaseCompletion);
                    Check(closeFailure.State == RecordingState.Failed && closeFailure.ReleaseCompletion.IsFaulted,
                        "an actual stream Dispose exception is retained as a release failure");
                    Check(File.Exists(path), "stream release failure retains the file");
                }

                main = new MainWindow();
                await ((Task)typeof(MainWindow).GetField("_deviceDiscovery", Private)!.GetValue(main)!).WaitAsync(TimeSpan.FromSeconds(20));
                ((TextBox)main.FindName("FolderBox")).Text = root;
                ((CheckBox)main.FindName("SystemAudioCheck")).IsChecked = false;
                ((CheckBox)main.FindName("MicrophoneCheck")).IsChecked = false;
                ((ComboBox)main.FindName("CameraCombo")).SelectedIndex = 0;
                var mode = (ComboBox)main.FindName("ModeCombo");
                mode.SelectedItem = mode.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == "screen");
                main.RefreshMediaControls();
                main.ShowSettings();
                var start = (Button)main.FindName("StartButton");
                var forceExit = (Button)main.FindName("ForceExitButton");
                Check(start.IsEnabled, "the owned WPF window starts ready for recording");
                var observe = typeof(MainWindow).GetMethod("ObserveBlockedSessionAsync", Private)!;
                using (var entered = new ManualResetEventSlim())
                using (var allow = new ManualResetEventSlim())
                using (var late = Create(() =>
                {
                    entered.Set();
                    if (!allow.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Release fixture was not unblocked.");
                }, TimeSpan.FromMilliseconds(150)))
                {
                    try
                    {
                        Finish(late);
                        if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Release fixture did not enter.");
                        _ = await Failure<TimeoutException>(late.Completion);
                        Set(main, "_blockedSession", late); main.RefreshMediaControls();
                        var observer = (Task)observe.Invoke(main, [late])!;
                        Check(!start.IsEnabled && forceExit.Visibility == Visibility.Visible && !observer.IsCompleted,
                            "a real pending fixture release disables recording and exposes the force-exit control");
                        Check(ReferenceEquals(typeof(MainWindow).GetField("_blockedSession", Private)!.GetValue(main), late),
                            "the blocked session remains tracked until release is confirmed");
                        allow.Set();
                        await late.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(3));
                        await observer.WaitAsync(TimeSpan.FromSeconds(3));
                        Check(typeof(MainWindow).GetField("_blockedSession", Private)!.GetValue(main) is null
                            && start.IsEnabled && forceExit.Visibility == Visibility.Collapsed,
                            "late successful release clears the blocked session and restores WPF controls");
                        Check(late.State == RecordingState.Failed && late.Completion.IsFaulted,
                            "late resource release does not rewrite the failed recording as a successful recording");
                        Check(((TextBlock)main.FindName("StatusText")).Text.Contains("资源已释放"),
                            "the WPF status explains that resource release has recovered");
                    }
                    finally { allow.Set(); }

                    using var nativeFailure = Create(() => throw new InvalidOperationException("native release fixture"));
                    Finish(nativeFailure);
                    _ = await Failure<InvalidOperationException>(nativeFailure.Completion);
                    _ = await Failure<InvalidOperationException>(nativeFailure.ReleaseCompletion);
                    Set(main, "_blockedSession", nativeFailure); main.RefreshMediaControls();
                    await (Task)observe.Invoke(main, [nativeFailure])!;
                    Check(!start.IsEnabled && forceExit.Visibility == Visibility.Visible
                        && ReferenceEquals(typeof(MainWindow).GetField("_blockedSession", Private)!.GetValue(main), nativeFailure),
                        "unconfirmed native Dispose failure keeps recording blocked and force exit available");
                    await (Task)observe.Invoke(main, [late])!;
                    Check(ReferenceEquals(typeof(MainWindow).GetField("_blockedSession", Private)!.GetValue(main), nativeFailure),
                        "completion from an older session cannot clear a newer blocked session");
                }
                File.WriteAllText(Path.Combine(root, "release-data-result.json"), JsonSerializer.Serialize(new
                    { success = true, controlledRelease = true, actualCapture = false, actualDriverHang = false,
                        fileHandlesUsed = true, wpfControlsUsed = true, checks = cases.Count, cases },
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Release data and WPF recovery checks passed: {cases.Count}.");
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                if (main is not null)
                {
                    // Only the fixture reference is cleared for teardown; production never clears failed native release.
                    Set(main, "_blockedSession", null);
                    main.RequestExit();
                }
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
        GC.KeepAlive(application);
        return exit;
    }

    private static RecordingSession Create(Action? release = null, TimeSpan? timeout = null)
    {
        var session = new RecordingSession(timeout ?? TimeSpan.FromSeconds(3), releaseOverride: release);
        Set(session, "started", true); Set(session, "state", RecordingState.Recording);
        return session;
    }
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Finish(RecordingSession session) => typeof(RecordingSession).GetMethod("Finish", Private)!.Invoke(session, [null, null]);
    private static async Task<Exception> Failure<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (T error) when (task.IsFaulted) { return error; }
        throw new Exception("Expected session failure: " + typeof(T).Name);
    }
    private sealed class FailingCloseStream(string path) : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) throw new IOException("stream close fixture");
        }
    }
}
