using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DogSC.Core;

namespace DogSC.Windows;

public sealed class FirstRunWindow : Window
{
    private readonly FirstRunState state;
    private readonly Action enter;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button continueButton = new() { Content = "进入DogSC", Padding = new Thickness(18, 10, 18, 10), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
    private bool entered;
    public bool IsCaptureSafe { get; private set; }
    public string? PersistenceWarning { get; private set; }
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DogSC", "first-run.json");

    public static bool ShowIfNeeded(bool backgroundStartup, Action enter)
        => ShowIfNeeded(backgroundStartup, enter, SettingsPath);

    internal static bool ShowIfNeeded(bool backgroundStartup, Action enter, string settingsPath)
    {
        if (backgroundStartup) return false;
        var state = new FirstRunState(settingsPath);
        if (state.Completed) return false;
        new FirstRunWindow(enter, state).Show(); return true;
    }

    public static void ShowReview()
    {
        var shown = Application.Current?.Windows.OfType<FirstRunWindow>().FirstOrDefault(window => window.IsVisible);
        if (shown is not null) { shown.Activate(); return; }
        new FirstRunWindow(() => { }, new FirstRunState(SettingsPath)).Show();
    }

    internal FirstRunWindow(Action enter, string settingsPath, Action<string>? openSettings = null)
        : this(enter, new FirstRunState(settingsPath), openSettings) { }

    private FirstRunWindow(Action enter, FirstRunState state, Action<string>? openSettings = null)
    {
        this.enter = enter ?? throw new ArgumentNullException(nameof(enter)); this.state = state;
        if (TryFindResource(typeof(Window)) is Style windowStyle) Style = windowStyle;
        Title = "开始使用DogSC"; Width = 640; Height = 580; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = new SolidColorBrush(Color.FromRgb(249, 250, 251));
        var root = new DockPanel { Margin = new Thickness(32, 24, 32, 26) }; Content = root;
        var footer = new DockPanel { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var language = new ComboBox { Width = 150, SelectedIndex = (int)Localization.Language, VerticalAlignment = VerticalAlignment.Center };
        language.Items.Add(new ComboBoxItem { Content = "简体中文", Tag = "zh" }); language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedIndex < 0 || language.SelectedIndex == (int)Localization.Language) return;
            try { Localization.SetPreferredLanguage((InterfaceLanguage)language.SelectedIndex); }
            catch (Exception error) { language.SelectedIndex = (int)Localization.Language; MessageBox.Show(this, error.Message, "DogSC界面语言设置", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        DockPanel.SetDock(language, Dock.Left); footer.Children.Add(language);
        DockPanel.SetDock(continueButton, Dock.Right); footer.Children.Add(continueButton);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var brand = new StackPanel { Orientation = Orientation.Horizontal }; body.Children.Add(brand);
        var logo = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 9, 0) }; logo.SetBinding(Image.SourceProperty, new Binding(nameof(Icon)) { Source = this }); brand.Children.Add(logo);
        brand.Children.Add(new TextBlock { Text = "DogSC", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(new TextBlock { Text = "准备好录制", FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 5) });
        body.Children.Add(new TextBlock { Text = "了解Windows录制与设备权限，开始你的第一次录制。", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
        var cards = new StackPanel { Margin = new Thickness(0, 20, 0, 0) }; body.Children.Add(cards);
        Card("屏幕与窗口", "选择屏幕、窗口或框选区域即可录制。受保护内容或Windows策略可能限制采集。");
        Card("鼠标移动与点击", "只在录制时记录鼠标轨迹与点击，不收集输入文字。");
        var devices = Card("麦克风与摄像头（可选）", "需要时在录制设置中选择设备。若无法访问，请在Windows中允许桌面应用使用麦克风或摄像头。");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) }; devices.Children.Add(buttons);
        PermissionButton("麦克风权限", "ms-settings:privacy-microphone"); PermissionButton("摄像头权限", "ms-settings:privacy-webcam");
        body.Children.Add(new TextBlock { Text = "Ctrl+Shift+S显示/隐藏悬浮条；Ctrl+Shift+空格暂停/继续。默认保存到D:\\视频。", FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 0) });
        body.Children.Add(status);
        if (state.ReadWarning is not null) status.Text = state.ReadWarning;
        continueButton.Click += (_, _) =>
        {
            if (entered) { Close(); return; }
            entered = true;
            if (!state.TryComplete(out var warning)) { PersistenceWarning = warning; status.Text = warning; continueButton.Content = "关闭引导"; status.BringIntoView(); }
            enter();
            if (PersistenceWarning is null) Close();
        };
        Closing += (_, args) =>
        {
            if (!entered && !state.TryComplete(out var warning))
            {
                PersistenceWarning = warning; status.Text = warning;
                args.Cancel = MessageBox.Show(this, warning + "\n\n是否不保存引导状态并关闭？选择“否”可保留窗口并稍后重试。",
                    "DogSC引导状态保存失败", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
            }
        };
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        SourceInitialized += (_, _) =>
        {
            IsCaptureSafe = SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11);
            if (!IsCaptureSafe) status.Text = "Windows未能排除引导窗口，请先关闭引导再录制。";
        };
        Localization.Apply(this);

        StackPanel Card(string title, string description)
        {
            var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
            cards.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12), BorderBrush = new SolidColorBrush(Color.FromRgb(230, 234, 237)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 7), Child = panel }); return panel;
        }
        void PermissionButton(string text, string location)
        {
            var button = new Button { Content = text, Tag = location, FontSize = 12, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 10, 0) };
            button.Click += (_, _) => { if (openSettings is not null) openSettings(location); else ServicesWindow.OpenWindowsPermission(this, location); };
            buttons.Children.Add(button);
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}

internal sealed class FirstRunState
{
    private const int MaxBytes = 8192;
    private readonly string path;
    private byte[]? originalHash;
    private readonly Settings settings = new();
    internal bool Completed => settings.Completed == true;
    internal string? ReadWarning { get; }
    private sealed class Settings
    {
        public int Version { get; set; } = 1;
        public bool? Completed { get; set; } = false;
        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; } = new();
    }
    internal FirstRunState(string settingsPath)
    {
        path = Path.GetFullPath(settingsPath);
        try
        {
            RejectLinks();
            if (!File.Exists(path)) return;
            var bytes = ReadBytes(); originalHash = SHA256.HashData(bytes);
            using (var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 }))
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("Version", out var version)
                    || !version.TryGetInt32(out var number) || number != 1 || !document.RootElement.TryGetProperty("Completed", out var completed)
                    || completed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException();
            settings = JsonSerializer.Deserialize<Settings>(bytes, new JsonSerializerOptions { MaxDepth = 4 }) ?? throw new InvalidDataException();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException)
        { ReadWarning = "首次引导状态读取失败，原文件已保留。仍可使用录制功能。"; }
    }
    internal bool TryComplete(out string? warning)
    {
        warning = null;
        if (ReadWarning is not null) { warning = ReadWarning; return false; }
        if (Completed) return true;
        string? temporary = null;
        bool publicationAttempted = false;
        try
        {
            RejectLinks();
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException();
            if (File.Exists(path) ? originalHash is null || !SHA256.HashData(ReadBytes()).SequenceEqual(originalHash) : originalHash is not null)
                throw new IOException("引导状态已被其它操作更改。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = Path.Combine(Path.GetDirectoryName(path)!, ".first-run-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Settings { Completed = true, Extra = settings.Extra });
            if (bytes.Length > MaxBytes) throw new InvalidDataException();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            publicationAttempted = true;
            SafeFilePublication.Publish(temporary, path);
            settings.Completed = true; originalHash = SHA256.HashData(bytes); return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        { warning = $"首次引导状态未能保存：{error.Message} 录制仍可使用，下次手动启动可能再次显示引导。"; return false; }
        finally { if (!publicationAttempted && temporary is not null && File.Exists(temporary)) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }
    private byte[] ReadBytes()
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new InvalidDataException();
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        _ = new UTF8Encoding(false, true).GetCharCount(bytes); return bytes;
    }
    private void RejectLinks()
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("首次引导状态不能经过链接。");
    }
}
