using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DogSC.Core;

namespace DogSC.Windows.SelfTest;

public static class LocalizationChecks
{
    // The contracts UI harness supplies the STA thread and Application resources.
    public static int Run(string root)
    {
        root = Path.GetFullPath(Path.Combine(root, "localization")); Directory.CreateDirectory(root);
        string settings = Path.Combine(root, "interface-settings.json");
        var previousLanguage = Localization.Language;
        var cases = new List<string>(); EditorWindow? editor = null; Window? formatWindow = null, customWindow = null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Check(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException("FAIL localization: " + name);
            cases.Add(name);
        }
        IOException Failure(Action action)
        {
            try { action(); } catch (IOException error) { return error; }
            throw new InvalidOperationException("Expected preference failure.");
        }
        void RejectState(EditorState state)
        {
            try { state.Validate(3); } catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid cursor scale was accepted.");
        }
        static IEnumerable<DependencyObject> Children(DependencyObject node)
        {
            yield return node;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                foreach (var descendant in Children(child)) yield return descendant;
        }
        static bool Signature(ComboBox combo, params string[] expected)
            => combo.Items.Cast<object>().OfType<string>().SequenceEqual(expected) && combo.Items.Count == expected.Length;
        static string Display(ComboBox combo, string value)
        {
            var template = combo.ItemTemplate ?? throw new InvalidOperationException("Translated choice template is missing.");
            template.Seal();
            if (template.LoadContent() is not TextBlock diagnostic) throw new InvalidOperationException("Translated choice content is missing.");
            diagnostic.DataContext = value;
            var binding = BindingOperations.GetBindingExpression(diagnostic, TextBlock.TextProperty)!; binding.UpdateTarget();
            Console.WriteLine($"CHOICE unshown: value={value}, actual='{diagnostic.Text}', language={Localization.Language}, binding={binding.Status}, "
                + $"dataContext={diagnostic.DataContext}, items={string.Join('|', combo.Items.Cast<object>())}");

            // Render the editor's real control, without showing the editor or starting its media readers.
            var previousParent = combo.Parent as Panel;
            var previousOwner = combo.Parent as ContentControl;
            int previousIndex = previousParent?.Children.IndexOf(combo) ?? -1;
            previousParent?.Children.Remove(combo);
            if (previousOwner is not null) previousOwner.Content = null;
            var host = new Window { Title = "DogSC localization choice fixture", ShowInTaskbar = false,
                Width = 420, Height = 150, Content = combo };
            try
            {
                Localization.Apply(host); host.Show(); host.UpdateLayout(); Drain();
                string selectedValue = (string)combo.SelectedItem;
                var selectedTexts = VisualChildren(combo).OfType<TextBlock>().Select(text => text.Text).ToArray();
                if (!selectedTexts.Contains(Localization.Text(selectedValue)))
                    throw new InvalidOperationException($"Selected choice is not rendered: language={Localization.Language}, value={selectedValue}, actual={string.Join('|', selectedTexts)}");
                int selectedIndex = combo.SelectedIndex;
                combo.IsDropDownOpen = true; host.UpdateLayout(); Drain();
                var container = combo.ItemContainerGenerator.ContainerFromItem(value) as ComboBoxItem
                    ?? throw new InvalidOperationException("Actual dropdown item was not generated: " + value);
                container.ApplyTemplate(); container.UpdateLayout(); Drain();
                var rendered = VisualChildren(container).OfType<TextBlock>().Select(text => text.Text).ToArray();
                Console.WriteLine($"CHOICE rendered: value={value}, actual={string.Join('|', rendered)}, selectedIndex={combo.SelectedIndex}, language={Localization.Language}");
                if (combo.SelectedIndex != selectedIndex) throw new InvalidOperationException("Rendering changed the selected semantic option.");
                return rendered.Single();
            }
            finally
            {
                combo.IsDropDownOpen = false; host.Content = null; host.Close();
                if (previousParent is not null) previousParent.Children.Insert(previousIndex, combo);
                else if (previousOwner is not null) previousOwner.Content = combo;
            }

            static void Drain()
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            static IEnumerable<DependencyObject> VisualChildren(DependencyObject node)
            {
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                {
                    var child = VisualTreeHelper.GetChild(node, index); yield return child;
                    foreach (var descendant in VisualChildren(child)) yield return descendant;
                }
            }
        }
        try
        {
            Localization.SetLanguage(InterfaceLanguage.English); Localization.LoadPreference(settings);
            Check(Localization.Language == InterfaceLanguage.Chinese && !File.Exists(settings), "missing preference defaults to Chinese without creating a file");
            Localization.SetPreferredLanguage(InterfaceLanguage.English, settings);
            using (var saved = JsonDocument.Parse(File.ReadAllText(settings)))
                Check(saved.RootElement.GetProperty("version").GetInt32() == 1 && saved.RootElement.GetProperty("language").GetString() == "en"
                    && Localization.Language == InterfaceLanguage.English, "English preference is committed before switching the interface");
            Localization.SetLanguage(InterfaceLanguage.Chinese); Localization.LoadPreference(settings);
            Check(Localization.Language == InterfaceLanguage.English, "reloading persisted preference restores English after in-memory reset");
            Localization.SetPreferredLanguage(InterfaceLanguage.Chinese, settings);
            Localization.SetLanguage(InterfaceLanguage.English); Localization.LoadPreference(settings);
            Check(Localization.Language == InterfaceLanguage.Chinese, "Chinese preference also survives in-memory reset");
            byte[] valid = File.ReadAllBytes(settings);
            try { Localization.SetPreferredLanguage((InterfaceLanguage)99, settings); throw new InvalidOperationException("Invalid language accepted."); }
            catch (ArgumentOutOfRangeException) { }
            Check(File.ReadAllBytes(settings).SequenceEqual(valid) && Localization.Language == InterfaceLanguage.Chinese, "unsupported language does not change preference or current language");

            File.WriteAllText(settings, "{broken preference"); byte[] malformed = File.ReadAllBytes(settings);
            var readError = Failure(() => Localization.LoadPreference(settings));
            Check(readError.Message.Contains(settings, StringComparison.Ordinal) && File.ReadAllBytes(settings).SequenceEqual(malformed)
                && Localization.Language == InterfaceLanguage.Chinese, "malformed preference read reports its Chinese recovery location and preserves file");
            var saveError = Failure(() => Localization.SetPreferredLanguage(InterfaceLanguage.English, settings));
            Check(saveError.Message.Contains(settings, StringComparison.Ordinal) && File.ReadAllBytes(settings).SequenceEqual(malformed)
                && Localization.Language == InterfaceLanguage.Chinese, "saving does not overwrite malformed preference or change language");
            foreach (var text in new[]
            {
                "{\"format\":\"dogsc.windows.interface\",\"version\":\"1\",\"language\":\"en\"}",
                "{\"format\":\"dogsc.windows.interface\",\"version\":2,\"language\":\"en\"}",
                "{\"format\":\"dogsc.windows.interface\",\"version\":1,\"language\":\"xx\"}",
                new string('x', 4097)
            })
            {
                File.WriteAllText(settings, text); _ = Failure(() => Localization.LoadPreference(settings));
                Check(File.ReadAllText(settings) == text && Localization.Language == InterfaceLanguage.Chinese, "invalid type, version, language or oversized preference is rejected without replacement");
            }
            File.WriteAllBytes(settings, valid);
            using (var held = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Failure(() => Localization.SetPreferredLanguage(InterfaceLanguage.English, settings));
                Check(error.Message.Contains(settings, StringComparison.Ordinal) && File.ReadAllBytes(settings).SequenceEqual(valid)
                    && Localization.Language == InterfaceLanguage.Chinese, "occupied preference retains old bytes, language and a visible recovery location");
            }
            var retained = Directory.EnumerateFiles(root, ".interface-settings-*.tmp").ToArray();
            Check(retained.Length == 1, "failed publication retains one complete new preference for recovery");
            using (var recovery = JsonDocument.Parse(File.ReadAllText(retained[0])))
                Check(recovery.RootElement.GetProperty("language").GetString() == "en", "retained preference contains the intended new language");

            var project = ProjectStore.Create(root, "localization fixture", new CaptureMetadata("screen", "fixture", 30, false, false, null));
            // These boxes exercise editor UI/contracts only, not decoding or real capture.
            var media = new byte[56]; BinaryPrimitives.WriteUInt32BigEndian(media, 24); "ftyp"u8.CopyTo(media.AsSpan(4)); "isom"u8.CopyTo(media.AsSpan(8));
            BinaryPrimitives.WriteUInt32BigEndian(media.AsSpan(24), 16); "moov"u8.CopyTo(media.AsSpan(28)); BinaryPrimitives.WriteUInt32BigEndian(media.AsSpan(32), 8); "free"u8.CopyTo(media.AsSpan(36));
            BinaryPrimitives.WriteUInt32BigEndian(media.AsSpan(40), 16); "mdat"u8.CopyTo(media.AsSpan(44)); File.WriteAllBytes(project.MediaPath, media);
            project = ProjectStore.Complete(project, TimeSpan.FromSeconds(3)); editor = new EditorWindow(project);
            var selectPage = typeof(EditorWindow).GetMethod("SelectPropertyPage", flags)!;
            selectPage.Invoke(editor, ["开场"]); Localization.SetLanguage(InterfaceLanguage.English);
            var opening = Children(editor).OfType<ComboBox>().Single(combo => Signature(combo, "无", "聚拢", "侧滑", "轻 3D"));
            var curve = Children(editor).OfType<ComboBox>().Single(combo => Signature(combo, "平滑", "迅捷", "柔和"));
            Check(Display(opening, "轻 3D") == "Light 3D" && Signature(opening, "无", "聚拢", "侧滑", "轻 3D"), "actual opening choice displays English without rewriting stored preset names");
            Check(Display(curve, "迅捷") == "Swift" && Signature(curve, "平滑", "迅捷", "柔和"), "actual opening curve displays English without rewriting semantic choices");
            opening.SelectedIndex = 3;
            Check(editor.State.Opening.Enabled && editor.State.Opening.Preset == "轻 3D", "translated opening selection keeps the native preset contract");
            Localization.SetLanguage(InterfaceLanguage.Chinese);
            Check(opening.SelectedIndex == 3 && Display(opening, "轻 3D") == "轻 3D", "language switch restores Chinese without changing selected preset");
            opening.SelectedIndex = 0;
            using (var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(project.PackagePath, "editor.json"))))
                Check(!editor.State.Opening.Enabled && editor.State.Opening.Preset == "无"
                    && state.RootElement.GetProperty("Opening").GetProperty("Preset").GetString() == "无", "actual opening index zero disables and saves the None preset");
            Check(OpeningSequenceTimeline.Sample(editor.State.Opening, "屏幕", ["屏幕"], .2, 640, 360) == OpeningSequenceSample.Identity,
                "disabled None preset samples the identity transform");
            try { OpeningSequenceTimeline.Validate(editor.State.Opening with { Enabled = true }); throw new InvalidOperationException("Enabled None accepted."); }
            catch (InvalidDataException) { }
            Check(true, "enabled None preset is rejected");
            Check(new OpeningSequenceStyle() is { Enabled: false, Preset: "无" }, "new default opening agrees with the None selector");
            OpeningSequenceTimeline.Validate(new OpeningSequenceStyle(false, "轻 3D"));
            Check(true, "older disabled Light 3D preset remains valid");

            var exportFormat = (ComboBox)typeof(EditorWindow).GetField("_exportFormat", flags)!.GetValue(editor)!;
            if (exportFormat.Parent is Panel previousParent) previousParent.Children.Remove(exportFormat);
            formatWindow = new Window { Content = exportFormat }; Localization.Apply(formatWindow); Localization.SetLanguage(InterfaceLanguage.English);
            exportFormat.SelectedIndex = 1;
            Check(Display(exportFormat, "MP4") == "MP4 video" && Display(exportFormat, "M4A") == "M4A audio"
                && exportFormat.SelectedItem as string == "M4A", "actual export format translation retains M4A selection and values");
            var userChoices = new ComboBox(); userChoices.Items.Add("无"); userChoices.Items.Add("聚拢");
            customWindow = new Window { Content = userChoices }; Localization.Apply(customWindow);
            Check(userChoices.ItemTemplate is null && (string)userChoices.Items[1] == "聚拢", "user-authored choice lists are not inferred as translatable enums");

            typeof(EditorWindow).GetField("_state", flags)!.SetValue(editor, editor.State with { EmbeddedCursor = false });
            typeof(EditorWindow).GetMethod("BuildProperties", flags)!.Invoke(editor, null); selectPage.Invoke(editor, ["光标"]);
            Check(Children(editor).OfType<TextBlock>().Any(text => text.Text == "Cursor size 0.25—6"), "actual editable cursor label agrees with the maximum accepted scale");
            var small = editor.State with { Pointer = editor.State.Pointer with { Scale = .25 } }; small.Validate(3);
            var large = editor.State with { Pointer = editor.State.Pointer with { Scale = 6 } }; large.Validate(3);
            Check(true, "both stated cursor scale boundaries are accepted");
            RejectState(editor.State with { Pointer = editor.State.Pointer with { Scale = .24 } });
            RejectState(editor.State with { Pointer = editor.State.Pointer with { Scale = 6.01 } });
            Check(true, "cursor scale outside stated boundaries is rejected");
            Check(Localization.Text("请在录制进行时添加标记；暂停时不能添加。") != "请在录制进行时添加标记；暂停时不能添加。"
                && Localization.Text("DogSC界面语言设置") == "DogSC interface language settings", "new marker and language-setting UI labels have English resources");
            File.WriteAllText(Path.Combine(root, "localization-result.json"), JsonSerializer.Serialize(new { success = true, checks = cases.Count, cases }, new JsonSerializerOptions { WriteIndented = true }));
            return cases.Count;
        }
        finally
        {
            formatWindow?.Close(); customWindow?.Close(); editor?.Close(); Localization.SetLanguage(previousLanguage);
        }
    }
}
