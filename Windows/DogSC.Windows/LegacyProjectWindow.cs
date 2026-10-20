using System.IO;
using System.Windows;
using System.Windows.Controls;
using DogSC.Core;
using Microsoft.Win32;

namespace DogSC.Windows;

public sealed class LegacyProjectWindow : Window
{
    public LegacyProjectWindow(LegacyProjectDocument project)
    {
        Title = $"DogSC原版项目检查 · {project.Title}"; Width = 780; Height = 500;
        var panel = new DockPanel { Margin = new Thickness(20) };
        var heading = new TextBlock { Text = $"{project.Title}\nSchema{project.Version} · 只读\n原版复杂效果尚未全部接入；此窗口检查素材和制作无损副本，原项目不被改写。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "制作完整项目副本", Margin = new Thickness(0, 0, 10, 0) };
        var cancel = new Button { Content = "取消复制", IsEnabled = false };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        var bottom = new StackPanel(); bottom.Children.Add(actions); bottom.Children.Add(status);
        actions.Children.Add(copy); actions.Children.Add(cancel); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        var lines = project.MediaReferences.Select(r => $"{r.Role}: {r.RelativePath} — {(r.Exists ? "已找到" : "缺失")}\n偏移{r.StartOffset:0.###}s，时钟比例{r.SourceTimeScale ?? 1:0.########}")
            .Concat(project.ExternalReferences.Select(r => $"外部资源（不会自动读取）：{r}"));
        panel.Children.Add(new ListBox { ItemsSource = lines }); Content = panel;
        CancellationTokenSource? cancellation = null;
        cancel.Click += (_, _) => cancellation?.Cancel();
        Closing += (_, e) => { if (cancellation is not null) { e.Cancel = true; cancellation.Cancel(); status.Text = "正在取消，请等待文件句柄释放。"; } };
        copy.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "选择新副本的父目录", Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            var destination = Path.Combine(dialog.FolderName, $"DogSC-import-{Guid.NewGuid():N}.dogscproject");
            cancellation = new CancellationTokenSource(); copy.IsEnabled = false; cancel.IsEnabled = true;
            try { await LegacyProjectCodec.CopyPackageAsync(project, destination, cancellation.Token); status.Text = $"已复制：{destination}"; }
            catch (OperationCanceledException error) { status.Text = $"复制已取消。原项目未改变。部分副本：{error.Data["PartialCopyPath"]}"; }
            catch (Exception error) { status.Text = $"复制失败：{error.Message}\n部分副本：{error.Data["PartialCopyPath"]}"; }
            finally { cancellation.Dispose(); cancellation = null; copy.IsEnabled = true; cancel.IsEnabled = false; }
        };
    }
}
