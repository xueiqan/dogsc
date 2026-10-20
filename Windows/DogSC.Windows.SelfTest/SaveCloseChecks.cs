using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DogSC.Windows.SelfTest;

internal static class SaveCloseChecks
{
    internal static async Task CloseDuringSavingAsync(MainWindow main)
    {
        var selected = Task.Run(() =>
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(8))
            {
                AutomationElement? dialog = null;
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out uint process);
                    if (process != Environment.ProcessId) return true;
                    var title = new StringBuilder(256); GetWindowText(handle, title, title.Capacity);
                    if (title.ToString() != "关闭DogSC") return true;
                    dialog = AutomationElement.FromHandle(handle); return false;
                }, 0);
                var yes = dialog?.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "6"));
                if (yes is not null) { ((InvokePattern)yes.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return; }
                Thread.Sleep(25);
            }
            throw new TimeoutException("未找到本测试进程的停止并保存确认窗口。");
        });
        main.RequestExit();
        await selected;
    }

    public static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var application = LiveMainChecks.CreateApplication();
        var dispatcher = Dispatcher.CurrentDispatcher;
        int exit = 1;
        _ = dispatcher.InvokeAsync(async () =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var main = new MainWindow();
            var checks = new List<string>();
            bool closed = false;
            main.Closed += (_, _) => closed = true;
            using var allowSave = new ManualResetEventSlim();
            Task? saving = null;
            void Check(bool valid, string name)
            {
                if (!valid) throw new Exception("FAIL: " + name);
                checks.Add(name);
            }
            try
            {
                await ((Task)typeof(MainWindow).GetField("_deviceDiscovery", flags)!.GetValue(main)!).WaitAsync(TimeSpan.FromSeconds(20));
                main.ShowSettings();
                var elapsed = Stopwatch.StartNew();
                saving = MediaExport.RunWorkerAsync((progress, token) =>
                {
                    while (!allowSave.Wait(50))
                    {
                        token.ThrowIfCancellationRequested();
                        progress.Report(elapsed.Elapsed.TotalSeconds);
                    }
                    progress.Report(100);
                }, "DogSC save-close controlled progress");
                typeof(MainWindow).GetField("_recordingTask", flags)!.SetValue(main, saving);
                main.RequestExit();
                Check(!closed, "closing during a pending save leaves the owned window alive");
                var cleanup = main.StopAfterFailureAsync();
                await Task.Delay(TimeSpan.FromSeconds(66));
                Check(!cleanup.IsCompleted && !saving.IsCompleted && MediaExport.IsBusy && !MediaExport.IsStalled,
                    "exception cleanup waits beyond 65 seconds while media export reports progress");
                Check(((Button)main.FindName("ForceExitButton")).Visibility != System.Windows.Visibility.Visible,
                    "healthy save is not presented as an unreleased resource");
                allowSave.Set();
                await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!MediaExport.IsBusy, "completed controlled export releases its media gate");
                main.RequestExit();
                Check(closed, "normal close succeeds after saving completes");
                File.WriteAllText(Path.Combine(root, "save-close-result.json"), JsonSerializer.Serialize(new
                {
                    success = true, controlledProgress = true, actualRecording = false, checks = checks.Count,
                    saveSeconds = elapsed.Elapsed.TotalSeconds, cases = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Save-close checks passed: {checks.Count}.");
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                allowSave.Set();
                if (saving is not null) try { await saving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                if (!closed) main.RequestExit();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
        GC.KeepAlive(application);
        return exit;
    }
    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
}
