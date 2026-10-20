using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DogSC.Windows.SelfTest;

internal static class SceneBoundaryChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        await MediaExport.RunWorkerAsync((report, _) =>
        {
            var state = EditorState.Initial(1);
            var size = SceneCompositor.OutputSize(state, 640, 479, 0);
            if (size.Height != 480 || size.Width != 642) throw new Exception("奇数高度补偶后宽度未按同一高度计算。");
            checks++;
            var source = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgr32, null, new byte[640 * 360 * 4], 640 * 4);
            source.Freeze();
            foreach (string frame in new[] { "浅色窗口", "手机", "平板" })
            {
                var extreme = state with { Style = new SceneStyle { Frame = frame, Padding = .4, FrameScale = 2, FrameToolbarScale = 3 } };
                extreme.Validate(1);
                var image = SceneCompositor.Render(source, extreme, .5, 640, 360);
                if (image.PixelWidth != 640 || image.PixelHeight != 360) throw new Exception("极值窗口框未保持输出规格。");
                checks++; report.Report(checks);
            }
        }, "scene bounds fixture");
        return checks;
    }
}
