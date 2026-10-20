using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DogSC.Core;

namespace DogSC.Windows;

public enum InterfaceLanguage { Chinese, English }

/// <summary>Translates owned UI labels while preserving input fields and stored choice values.</summary>
public static class Localization
{
    private static readonly ResourceManager Strings = new("DogSC.Windows.LocalizationResources", typeof(Localization).Assembly);
    private static readonly Dictionary<Window, WindowLabels> Windows = new();
    private static readonly ChoiceConverter Converter = new();
    public static readonly DependencyProperty IgnoreProperty = DependencyProperty.RegisterAttached(
        "Ignore", typeof(bool), typeof(Localization), new PropertyMetadata(false));
    public static InterfaceLanguage Language { get; private set; } = InterfaceLanguage.Chinese;
    public static event Action? LanguageChanged;
    public static void SetIgnore(DependencyObject control, bool value) => control.SetValue(IgnoreProperty, value);
    public static bool GetIgnore(DependencyObject control) => (bool)control.GetValue(IgnoreProperty);

    static Localization()
    {
        // WPF may omit Loaded broadcasts with class handlers alone; rebuilt panels call Apply explicitly.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is FrameworkElement element && !InsideDataItems(element) && Window.GetWindow(element) is { } owner && Windows.TryGetValue(owner, out var labels))
                    Walk(element, labels, insideChoices: false);
            }), handledEventsToo: true);
    }

    public static string Text(string source)
        => Language == InterfaceLanguage.English ? Strings.GetString(source, CultureInfo.InvariantCulture) ?? source : source;

    public static void SetLanguage(InterfaceLanguage language)
    {
        if (language is not (InterfaceLanguage.Chinese or InterfaceLanguage.English)) throw new ArgumentOutOfRangeException(nameof(language));
        if (Application.Current is { } app) app.Dispatcher.VerifyAccess();
        Language = language;
        foreach (var labels in Windows.Values.ToArray())
        {
            labels.Window.Dispatcher.VerifyAccess();
            foreach (var label in labels.Properties.Values) label.Update();
            Walk(labels.Window, labels, insideChoices: false);
            // A new display template refreshes selection and dropdown visuals without changing Items or selection.
            foreach (var combo in labels.Choices) InstallChoiceTemplate(combo);
        }
        LanguageChanged?.Invoke();
    }

    private static string PreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DogSC", "interface-settings.json");

    public static void LoadPreference() => LoadPreference(PreferencePath);
    internal static void LoadPreference(string settingsPath)
    {
        string path = Path.GetFullPath(settingsPath);
        try { SetLanguage(ReadPreference(path)); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { throw new IOException($"界面语言设置读取失败，原文件已保留。设置位置：{path}。{error.Message}", error); }
    }

    public static void SetPreferredLanguage(InterfaceLanguage language) => SetPreferredLanguage(language, PreferencePath);
    internal static void SetPreferredLanguage(InterfaceLanguage language, string settingsPath)
    {
        if (language is not (InterfaceLanguage.Chinese or InterfaceLanguage.English)) throw new ArgumentOutOfRangeException(nameof(language));
        if (Application.Current is { } app) app.Dispatcher.VerifyAccess();
        string path = Path.GetFullPath(settingsPath);
        string? temporary = null; bool publicationAttempted = false;
        try
        {
            // Reject a malformed existing preference instead of overwriting user data with a guessed schema.
            _ = ReadPreference(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = Path.Combine(Path.GetDirectoryName(path)!, ".interface-settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { format = "dogsc.windows.interface", version = 1,
                language = language == InterfaceLanguage.English ? "en" : "zh-CN" });
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            publicationAttempted = true;
            SafeFilePublication.Publish(temporary, path);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { throw new IOException($"界面语言设置未保存，当前语言保持不变。设置位置：{path}。{error.Message}", error); }
        finally
        {
            if (!publicationAttempted && temporary is not null && File.Exists(temporary))
            { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        SetLanguage(language);
    }

    private static InterfaceLanguage ReadPreference(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("界面语言设置不能经过链接或重解析点。");
        if (!File.Exists(path)) return InterfaceLanguage.Chinese;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4096) throw new InvalidDataException("界面语言设置超过4KB限制。");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.String || format.GetString() != "dogsc.windows.interface"
            || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int number) || number != 1
            || !root.TryGetProperty("language", out var language) || language.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("界面语言设置格式或版本无效。");
        return language.GetString() switch
        {
            "zh-CN" => InterfaceLanguage.Chinese, "en" => InterfaceLanguage.English,
            _ => throw new InvalidDataException("界面语言设置仅支持中文或英文。")
        };
    }

    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window); window.Dispatcher.VerifyAccess();
        if (!Windows.TryGetValue(window, out var labels))
        {
            labels = new WindowLabels(window); Windows.Add(window, labels);
            window.Closed += (_, _) => { labels.Dispose(); Windows.Remove(window); };
        }
        var controls = new HashSet<DependencyObject>();
        Walk(window, labels, insideChoices: false, controls);
        labels.Prune(controls);
    }

    private static void Walk(DependencyObject control, WindowLabels labels, bool insideChoices, HashSet<DependencyObject>? controls = null)
    {
        controls?.Add(control);
        if (GetIgnore(control) || BindingOperations.IsDataBound(control, IgnoreProperty)) return;
        if (control is FrameworkElement element)
        {
            labels.Watch(element, FrameworkElement.ToolTipProperty);
            labels.Watch(element, AutomationProperties.NameProperty);
            labels.Watch(element, AutomationProperties.HelpTextProperty);
            if (element.ToolTip is ToolTip tooltip) Walk(tooltip, labels, insideChoices: false, controls);
        }
        if (control is Window window) labels.Watch(window, Window.TitleProperty, title: true);
        if (control is TextBox or PasswordBox or RichTextBox) return;
        if (insideChoices) return;
        if (control is ComboBox combo)
        {
            if (IsStaticChoice(combo) && labels.Choices.Add(combo)) InstallChoiceTemplate(combo);
            return; // Device names and user preset names are not label text.
        }
        if (control is ComboBoxItem or ListBox or ListBoxItem or TreeView or TreeViewItem or DataGrid) return;
        if (control is TextBlock text) labels.Watch(text, TextBlock.TextProperty);
        if (control is ContentControl content) labels.Watch(content, ContentControl.ContentProperty);
        if (control is HeaderedContentControl header) labels.Watch(header, HeaderedContentControl.HeaderProperty);
        if (control is HeaderedItemsControl itemsHeader) labels.Watch(itemsHeader, HeaderedItemsControl.HeaderProperty);
        foreach (var child in LogicalTreeHelper.GetChildren(control).OfType<DependencyObject>().ToArray())
            Walk(child, labels, insideChoices: false, controls);
    }

    private static bool InsideDataItems(DependencyObject control)
    {
        for (var node = control; node is not null; node = LogicalTreeHelper.GetParent(node)
            ?? (node is Visual ? VisualTreeHelper.GetParent(node) : null))
            if (node is ComboBoxItem or ListBoxItem or TreeViewItem or DataGridRow) return true;
        return false;
    }

    private static bool IsStaticChoice(ComboBox combo)
    {
        if (!string.IsNullOrEmpty(combo.DisplayMemberPath) || combo.Items.Count == 0) return false;
        if (combo.Items.Cast<object>().All(item => item is ComboBoxItem { Content: string })) return true;
        // Exact enum signatures distinguish semantic options from user-authored style/preset names.
        var values = combo.Items.Cast<object>().OfType<string>().ToArray();
        if (values.Length != combo.Items.Count) return false;
        return ChoiceSignatures.Any(signature => signature.SequenceEqual(values));
    }

    private static readonly string[][] ChoiceSignatures =
    [
        ["6帧缩略图", "12帧缩略图"], ["遮盖", "柔化", "贴图", "缩放", "聚光"],
        ["1080p", "1440p", "4K", "源分辨率"],
        ["适应", "16:9", "4:3", "9:16", "1:1", "3:4", "2.39:1", "1:2.39"],
        ["颜色", "图案", "动态", "图片", "视频"], ["无", "浅色窗口", "深色窗口", "浏览器"],
        ["无", "浅色窗口", "深色窗口", "浅色浏览器", "深色浏览器", "手机", "平板"],
        ["黑曜石网格", "工程白网格", "暗夜星阵", "建筑极简点", "立体等角网"],
        ["流金缓移网格", "暖星漫步点阵", "蓝白流体云幕"],
        ["屏幕,摄像头,贴图", "屏幕,贴图,摄像头", "摄像头,屏幕,贴图", "摄像头,贴图,屏幕", "贴图,屏幕,摄像头", "贴图,摄像头,屏幕"],
        ["无", "聚拢", "侧滑", "轻 3D"], ["平滑", "迅捷", "柔和"], ["MP4", "M4A"],
        ["录制光标", "箭头", "手势", "十字"],
        ["直接", "快速", "中等", "平滑"], ["关闭", "涟漪", "柔和光晕", "弹性双环", "聚焦微闪"],
        ["圆形", "方形", "横向", "纵向", "原始比例"],
        ["圆形", "方形", "横向", "纵向", "原始比例", "全屏"],
        ["线性", "二次", "三次", "五次", "弹簧", "自定义"],
        ["圆形焦点", "线性清晰带"], ["随运镜", "随鼠标", "固定"]
    ];

    private static void InstallChoiceTemplate(ComboBox combo)
    {
        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding { Converter = Converter });
        template.VisualTree = text;
        if (combo.Items.Cast<object>().All(item => item is ComboBoxItem))
            foreach (var item in combo.Items.Cast<ComboBoxItem>()) item.ContentTemplate = template;
        combo.ItemTemplate = template;
        combo.ApplyTemplate();
        // WPF caches SelectionBoxItemTemplate when selection changes. Refresh only its presenter,
        // because changing SelectedIndex to refresh the cache would alter the recording/editor state.
        foreach (var presenter in VisualChildren(combo).OfType<ContentPresenter>())
            if (presenter.TemplatedParent == combo) presenter.SetCurrentValue(ContentPresenter.ContentTemplateProperty, template);
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in VisualChildren(child)) yield return descendant;
        }
    }

    private sealed class ChoiceConverter : IValueConverter
    {
        public object Convert(object value, Type target, object parameter, CultureInfo culture)
            => value is string text ? Text(text) : value;
        public object ConvertBack(object value, Type target, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class WindowLabels(Window window) : IDisposable
    {
        internal Window Window { get; } = window;
        internal Dictionary<(DependencyObject, DependencyProperty), LabelProperty> Properties { get; } = new();
        internal HashSet<ComboBox> Choices { get; } = new();
        internal void Watch(DependencyObject control, DependencyProperty property, bool title = false)
        {
            if (BindingOperations.IsDataBound(control, property)) return;
            var key = (control, property);
            if (!Properties.TryGetValue(key, out var label))
            {
                label = new LabelProperty(control, property, title); Properties.Add(key, label);
            }
            label.Update();
        }
        public void Dispose() { foreach (var label in Properties.Values) label.Dispose(); Properties.Clear(); Choices.Clear(); }
        internal void Prune(HashSet<DependencyObject> controls)
        {
            foreach (var key in Properties.Keys.Where(key => !controls.Contains(key.Item1)).ToArray())
            { Properties[key].Dispose(); Properties.Remove(key); }
            Choices.RemoveWhere(combo => !controls.Contains(combo));
        }
    }

    private sealed class LabelProperty : IDisposable
    {
        private readonly DependencyObject control;
        private readonly DependencyProperty property;
        private readonly DependencyPropertyDescriptor descriptor;
        private readonly bool title;
        private string? source, displayed;
        private bool updating;
        internal LabelProperty(DependencyObject control, DependencyProperty property, bool title)
        {
            this.control = control; this.property = property; this.title = title;
            descriptor = DependencyPropertyDescriptor.FromProperty(property, control.GetType());
            descriptor.AddValueChanged(control, Changed);
        }
        private void Changed(object? sender, EventArgs args) => Update();
        internal void Update()
        {
            if (updating || GetIgnore(control) || BindingOperations.IsDataBound(control, property)) return;
            if (control.GetValue(property) is not string current) return;
            if (source is null || current != displayed) source = current;
            displayed = title ? TranslateTitle(source) : Text(source);
            if (current == displayed) return;
            updating = true;
            try { control.SetCurrentValue(property, displayed); }
            finally { updating = false; }
        }
        public void Dispose() => descriptor.RemoveValueChanged(control, Changed);
    }

    private static string TranslateTitle(string source)
    {
        foreach (var prefix in new[] { "DogSC编辑器 · ", "DogSC原版项目检查 · ", "DogSC备忘录 · " })
            if (source.StartsWith(prefix, StringComparison.Ordinal)) return Text(prefix) + source[prefix.Length..];
        return Text(source);
    }
}
