using System;
using System.Buffers.Binary;
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
using System.Windows.Input;
using DogSC.Core;
using DogSC.Windows;

namespace DogSC.Windows.SelfTest;

public static class EditorFailureChecks
{
    public static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var cases = new List<string>();
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var watchdog = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), watchdog.Token); }
            catch (OperationCanceledException) { return; }
            Console.Error.WriteLine("Own editor fixture timed out; terminating only its test process."); Environment.Exit(2);
        });
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + name);
            cases.Add(name); Console.WriteLine("PASS editor: " + name);
        }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var project = ProjectStore.Create(root, "editor failure fixture", new CaptureMetadata("screen", "fixture", 30, false, false, null));
        // Synthetic MP4 boxes certify transactions only; this fixture makes no decoding claim.
        var bytes = new byte[56];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 24); "ftyp"u8.CopyTo(bytes.AsSpan(4)); "isom"u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 16); "moov"u8.CopyTo(bytes.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32), 8); "free"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(40), 16); "mdat"u8.CopyTo(bytes.AsSpan(44));
        File.WriteAllBytes(project.MediaPath, bytes); project = ProjectStore.Complete(project, TimeSpan.FromSeconds(3));
        var editor = new EditorWindow(project); editor.Save(); editor.Show();
        string savedPath = Path.Combine(project.PackagePath, "editor.json");
        var panel = (StackPanel)typeof(EditorWindow).GetField("_properties", flags)!.GetValue(editor)!;
        var undo = (Stack<EditorState>)typeof(EditorWindow).GetField("_undo", flags)!.GetValue(editor)!;
        var commit = typeof(EditorWindow).GetMethod("Commit", flags)!;
        var text = typeof(EditorWindow).GetMethod("Text", flags)!;
        var number = typeof(EditorWindow).GetMethod("Number", flags, null, [typeof(string), typeof(double), typeof(Action<double>)], null)!;
        TextBox LastFieldBox() => ((Grid)panel.Children[^1]).Children.OfType<TextBox>().Single();
        void LoseFocus(TextBox box) => box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, box, null)
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        Task<string> Select(string id) => Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                AutomationElement? dialog = null;
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out uint process);
                    if (process != Environment.ProcessId) return true;
                    var title = new StringBuilder(512); GetWindowText(handle, title, title.Capacity);
                    if (title.ToString() != "DogSC编辑保存失败") return true;
                    dialog = AutomationElement.FromHandle(handle); return false;
                }, 0);
                if (dialog is not null)
                {
                    var button = dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                    if (button is not null)
                    {
                        string message = string.Join("\n", dialog.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>().Select(e => e.Current.Name));
                        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return message;
                    }
                }
                Thread.Sleep(30);
            }
            throw new TimeoutException("Own editor save-failure dialog/button was not found.");
        });
        try
        {
            int calls = 0; int originalUndo = undo.Count;
            text.Invoke(editor, ["focus fixture", editor.State.Style.FrameTitle,
                new Action<string>(value => { calls++; commit.Invoke(editor, [editor.State with { Style = editor.State.Style with { FrameTitle = value } }]); })]);
            var textBox = LastFieldBox();
            using (var held = new FileStream(savedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                for (int i = 0; i < 3; i++) LoseFocus(textBox);
                Check(calls == 0 && undo.Count == originalUndo, "unchanged Text focus does not invoke action or append undo");
                textBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), Environment.TickCount, Key.Enter)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                Check(calls == 0 && undo.Count == originalUndo, "unchanged Text Enter does not save or append undo");
                number.Invoke(editor, ["numeric fixture", editor.State.Style.Ratio,
                    new Action<double>(value => commit.Invoke(editor, [editor.State with { Style = editor.State.Style with { Ratio = value } }]))]);
                var numberBox = LastFieldBox(); numberBox.Text = "0.00"; LoseFocus(numberBox); LoseFocus(numberBox);
                Check(undo.Count == originalUndo && editor.State.Style.Ratio == 0, "same numeric value in new formatting is a Commit no-op");
                Check(!((TextBlock)typeof(EditorWindow).GetField("_status", flags)!.GetValue(editor)!).Text.Contains(savedPath),
                    "unchanged focus never attempts saving a locked editor file");
            }
            textBox.Text = "changed title fixture"; LoseFocus(textBox);
            Check(calls == 1 && undo.Count == originalUndo + 1 && editor.State.Style.FrameTitle == "changed title fixture",
                "changed Text commits exactly once");
            LoseFocus(textBox); Check(calls == 1 && undo.Count == originalUndo + 1, "successful Text repeated focus remains a no-op");
            byte[] previous = File.ReadAllBytes(savedPath);
            using (var held = new FileStream(savedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failedSave = false;
                try { commit.Invoke(editor, [editor.State with { Style = editor.State.Style with { FrameTitle = "unsaved close fixture" } }]); }
                catch (TargetInvocationException error) when (error.InnerException is IOException or UnauthorizedAccessException) { failedSave = true; }
                Check(failedSave && editor.State.Style.FrameTitle == "unsaved close fixture", "failed commit retains unsaved state for retry or explicit abandon");
                var no = Select("7"); editor.Close(); string shown = no.GetAwaiter().GetResult();
                Check(editor.IsVisible, "save-failure No retains the editor window");
                Check(shown.Contains("修改尚未保存") && shown.Contains(project.PackagePath), "close failure explains Chinese error and save location");
                Check(File.ReadAllBytes(savedPath).SequenceEqual(previous), "failed close preserves previous editor file");
                Check(editor.State.Style.FrameTitle == "unsaved close fixture", "failed close preserves edited state");
                var yes = Select("6"); editor.Close(); _ = yes.GetAwaiter().GetResult();
                Check(!editor.IsVisible, "explicit Yes permits abandoning changes and closing");
                Check(File.ReadAllBytes(savedPath).SequenceEqual(previous), "explicit abandon preserves previously saved edits");
            }
            File.WriteAllText(Path.Combine(root, "editor-failure-result.json"), JsonSerializer.Serialize(
                new { success = true, ownProcessOnly = true, syntheticMedia = true, checks = cases.Count, cases }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            if (editor.IsVisible) editor.Close();
            application.Shutdown();
            watchdog.Cancel();
        }
    }
    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
}
