using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace DogSC.Windows;

public sealed class ServicesWindow : Window
{
    public ServicesWindow()
    {
        Title = "关于DogSC · Windows使用与权限"; Width = 600; Height = 500;
        var panel = new StackPanel { Margin = new Thickness(22) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "DogSC Windows", FontSize = 25 });
        panel.Children.Add(new TextBlock { Text = $"免费本地录屏与演示视频编辑 · {Assembly.GetExecutingAssembly().GetName().Version}\n" +
            "原项目：laogou717/dogsc，MIT许可证。Windows移植沿用原图标与算法参考。\n" +
            "Ctrl+Shift+S：录制条；Ctrl+Shift+空格：暂停/继续；Ctrl+Alt+M：录制标记。\n" +
            "声音和摄像头由Windows权限控制。选择某个窗口不代表排除了整机声音；应用声音需要显式选择进程树录音。\n" +
            "自有录制条、备忘录和区域框通过Windows采集排除；其它软件弹窗仍可能进入屏幕录制。\n" +
            "中断录制可尝试重新打开项目，系统会将完整媒体分片复制到新恢复项目。没有完整索引的素材无法恢复。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        Add("麦克风权限", "ms-settings:privacy-microphone");
        Add("摄像头权限", "ms-settings:privacy-webcam");
        Add("Windows免打扰设置", "ms-settings:notifications");
        Add("上游源码和许可证", "https://github.com/laogou717/dogsc");
        Add("上游反馈页面（macOS原项目）", "https://github.com/laogou717/dogsc/issues");
        var guide = new Button { Content = "再次查看首次使用引导", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        guide.Click += (_, _) => FirstRunWindow.ShowReview(); panel.Children.Add(guide);
        panel.Children.Add(new TextBlock { Text = "Windows移植版尚无独立签名发布服务器。当前不自动下载或替换程序；上游macOS发布不作为Windows更新源。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        void Add(string text, string location)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) =>
            {
                if (location.StartsWith("ms-settings:", StringComparison.Ordinal)) { OpenWindowsPermission(this, location); return; }
                try { Process.Start(new ProcessStartInfo(location) { UseShellExecute = true }); }
                catch (Exception error) { MessageBox.Show(this, error.Message, "无法打开页面"); }
            };
            panel.Children.Add(button);
        }
    }

    internal static string PermissionLocation(string location) => location switch
    {
        "ms-settings:privacy-microphone" or "ms-settings:privacy-webcam" or "ms-settings:notifications" => location,
        _ => throw new ArgumentOutOfRangeException(nameof(location), "未知的Windows权限设置入口。")
    };
    internal static void OpenWindowsPermission(Window owner, string location)
    {
        var validated = PermissionLocation(location);
        try { Process.Start(new ProcessStartInfo(validated) { UseShellExecute = true }); }
        catch (Exception error) { MessageBox.Show(owner, error.Message, Localization.Text("无法打开页面")); }
    }
}
