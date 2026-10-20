using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DogSC.Core;

namespace DogSC.Windows;

public sealed class MemoWindow : Window
{
    private const int MaxTextLength = 50_000;
    private const int MaxSettingsBytes = MaxTextLength * 6 + 512;
    private readonly TextBox text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = MaxTextLength, FontSize = 20 };
    private readonly Slider font = new() { Minimum = 12, Maximum = 48, Value = 20, Width = 110 };
    private readonly Slider opacity = new() { Minimum = .25, Maximum = 1, Value = .9, Width = 100 };
    private readonly string path;
    private bool readFailed;
    private bool discardChanges;
    public bool IsCaptureSafe { get; private set; }
    private sealed record Settings(string Text, double FontSize, double Opacity);

    public MemoWindow() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DogSC", "memo.json")) { }

    internal MemoWindow(string settingsPath)
    {
        path = Path.GetFullPath(settingsPath);
        Title = "DogSC备忘录"; Width = 420; Height = 360; MinWidth = 320; MinHeight = 200;
        Topmost = true; ShowInTaskbar = false;
        var panel = new DockPanel { Margin = new Thickness(10) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        controls.Children.Add(new TextBlock { Text = "字号", VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(font);
        controls.Children.Add(new TextBlock { Text = "透明度", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        controls.Children.Add(opacity); DockPanel.SetDock(controls, Dock.Top);
        panel.Children.Add(controls); panel.Children.Add(text); Content = panel;
        font.ValueChanged += (_, _) => text.FontSize = font.Value;
        opacity.ValueChanged += (_, _) => Opacity = opacity.Value;
        SourceInitialized += (_, _) =>
        {
            try { IsCaptureSafe = ScreenRecorderLib.Recorder.SetExcludeFromCapture(new WindowInteropHelper(this).Handle, true); }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or System.ComponentModel.Win32Exception)
            { IsCaptureSafe = false; }
            if (!IsCaptureSafe) Title += " · 无法排除录屏，请关闭后录制";
        };
        try
        {
            RejectLinks();
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > MaxSettingsBytes) throw new InvalidDataException("备忘录设置超过大小限制。");
                var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(path), new JsonSerializerOptions { MaxDepth = 4 });
                if (saved is not null && saved.Text is not null && saved.Text.Length <= MaxTextLength
                    && double.IsFinite(saved.FontSize) && saved.FontSize is >= 12 and <= 48
                    && double.IsFinite(saved.Opacity) && saved.Opacity is >= .25 and <= 1)
                { text.Text = saved.Text; font.Value = saved.FontSize; opacity.Value = saved.Opacity; }
                else throw new InvalidDataException("备忘录设置字段无效，原文件已保留。");
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            readFailed = true;
            text.IsReadOnly = true; font.IsEnabled = false; opacity.IsEnabled = false;
            Title = $"DogSC备忘录 · 读取失败，原文件已保护：{error.Message}";
        }
        Closing += (_, e) =>
        {
            try { Save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                e.Cancel = true;
                if (MessageBox.Show(this, $"备忘录尚未保存：{error.Message}\n\n是否放弃未保存的修改并关闭？选择“否”可保留窗口，解除占用后再次关闭以重试保存。",
                    "DogSC备忘录保存失败", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                { discardChanges = true; e.Cancel = false; }
            }
        };
        Deactivated += (_, _) =>
        {
            try { Save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Title = $"DogSC备忘录 · 保存失败：{error.Message}"; }
        };
    }

    private void Save()
    {
        if (readFailed || discardChanges) return;
        RejectLinks();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".memo-{Guid.NewGuid():N}.tmp");
        bool publicationAttempted = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, new Settings(text.Text, font.Value, opacity.Value)); stream.Flush(true); }
            publicationAttempted = true;
            SafeFilePublication.Publish(temp, path);
            Title = "DogSC备忘录" + (IsCaptureSafe ? "" : " · 无法排除录屏，请关闭后录制");
        }
        finally { if (!publicationAttempted) File.Delete(temp); }
    }

    private void RejectLinks()
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("备忘录配置目录不能经过链接。");
    }
}
