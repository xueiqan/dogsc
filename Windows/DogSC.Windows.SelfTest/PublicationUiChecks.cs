using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DogSC.Windows;

namespace DogSC.Windows.SelfTest;

public static class PublicationUiChecks
{
    public static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var cases = new List<string>();
        var windows = new List<Window>();
        using var watchdog = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), watchdog.Token); }
            catch (OperationCanceledException) { return; }
            Console.Error.WriteLine("Own publication UI fixture timed out; terminating only its test process.");
            Environment.Exit(2);
        });
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + name);
            cases.Add(name);
            Console.WriteLine("PASS publication UI: " + name);
        }
        MemoWindow Memo(string path)
        {
            var window = (MemoWindow)Activator.CreateInstance(typeof(MemoWindow), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [path], null)!;
            windows.Add(window); window.Show(); return window;
        }
        TextBox Text(MemoWindow window) => (TextBox)typeof(MemoWindow).GetField("text", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Task<string> Select(string title, string id) => Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                AutomationElement? dialog = null;
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out uint process);
                    if (process != Environment.ProcessId) return true;
                    var name = new StringBuilder(512); GetWindowText(handle, name, name.Capacity);
                    if (name.ToString() != title) return true;
                    dialog = AutomationElement.FromHandle(handle); return false;
                }, 0);
                if (dialog is not null)
                {
                    var button = dialog.FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.AutomationIdProperty, id)));
                    if (button is not null)
                    {
                        string contents = string.Join("\n", dialog.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                            .Cast<AutomationElement>().Select(element => element.Current.Name));
                        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return contents;
                    }
                }
                Thread.Sleep(30);
            }
            throw new TimeoutException("No matching own-process publication dialog/button found.");
        });
        try
        {
            string memoPath = Path.Combine(root, "memo-retry.json");
            const string original = "{\"Text\":\"old memo\",\"FontSize\":20,\"Opacity\":0.9}";
            File.WriteAllText(memoPath, original);
            var memo = Memo(memoPath); Text(memo).Text = "新的未保存备忘录";
            using (var held = new FileStream(memoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var select = Select("DogSC备忘录保存失败", "7");
                memo.Close(); string shown = select.GetAwaiter().GetResult();
                Check(memo.IsVisible && Text(memo).Text == "新的未保存备忘录", "memo No preserves open window and edited text");
                Check(shown.Contains("可恢复文件保留在") && shown.Contains(root), "memo failure dialog reports Chinese recovery location");
                Check(File.ReadAllText(memoPath) == original, "memo publication failure preserves old settings");
                Check(Directory.GetFiles(root, ".memo-*.tmp").Any(path => File.ReadAllText(path).Contains("\\u65B0")),
                    "memo publication failure retains completed new settings");
            }
            memo.Close();
            using (var saved = JsonDocument.Parse(File.ReadAllText(memoPath)))
                Check(saved.RootElement.GetProperty("Text").GetString() == "新的未保存备忘录", "memo retry after unlock saves edited text");

            string discardPath = Path.Combine(root, "memo-discard.json"); File.WriteAllText(discardPath, original);
            var discard = Memo(discardPath); Text(discard).Text = "explicit discard fixture";
            using (var held = new FileStream(discardPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var select = Select("DogSC备忘录保存失败", "6");
                discard.Close(); _ = select.GetAwaiter().GetResult();
                Check(!discard.IsVisible, "memo closes only after explicit Yes to discard");
                Check(File.ReadAllText(discardPath) == original, "memo explicit discard does not overwrite old saved settings");
            }

            string firstPath = Path.Combine(root, "first-run.json");
            const string firstOriginal = "{\"Version\":1,\"Completed\":false}"; File.WriteAllText(firstPath, firstOriginal);
            var first = (FirstRunWindow)Activator.CreateInstance(typeof(FirstRunWindow), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [new Action(() => { }), firstPath, null], null)!;
            windows.Add(first); first.Show();
            using (var held = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var no = Select("DogSC引导状态保存失败", "7");
                first.Close(); string shown = no.GetAwaiter().GetResult();
                Check(first.IsVisible && shown.Contains("可恢复文件保留在"), "first-run No preserves guide and reports recovery location");
                Check(File.ReadAllText(firstPath) == firstOriginal, "first-run failed close preserves old settings");
                var yes = Select("DogSC引导状态保存失败", "6");
                first.Close(); _ = yes.GetAwaiter().GetResult();
                Check(!first.IsVisible, "first-run explicit Yes permits closing without successful publication");
            }
            File.WriteAllText(Path.Combine(root, "publication-ui-result.json"), JsonSerializer.Serialize(
                new { success = true, ownProcessOnly = true, processId = Environment.ProcessId, checks = cases.Count, cases }));
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, checks = cases.Count, root }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            watchdog.Cancel();
            foreach (var window in windows.Where(window => window.IsVisible))
            {
                if (window is MemoWindow) typeof(MemoWindow).GetField("discardChanges", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                if (window is FirstRunWindow) typeof(FirstRunWindow).GetField("entered", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close();
            }
            application.Shutdown();
        }
    }

    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
}
