using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DogSC.Core;

namespace DogSC.Windows.SelfTest;

internal static class SceneRegressionChecks
{
    private static readonly string[] CameraShapes = ["圆形", "方形", "横向", "纵向", "原始比例"];

    public static async Task<int> RunFocusAsync()
    {
        int checks = 0;
        await MediaExport.RunWorkerAsync((progress, _) =>
        {
            var source = Pattern(320, 180);
            var basis = State(8) with { Style = PlainStyle() with { Zoom = 1.25, FocusX = .2, FocusY = .25 } };
            var tracked = basis with
            {
                ZoomClips = [new(Guid.NewGuid(), 2, 4, 1.8, new(.75, .65), EnterDuration: .3, ExitDuration: .3)]
            };
            var changed = tracked with { Style = tracked.Style with { FocusX = .8, FocusY = .75 } };
            foreach (double time in new[] { 1d, 6d })
            {
                Check(Equal(Render(source, tracked, time), Render(source, basis, time)),
                    $"inactive zoom at {time}s preserves manual focus pixels");
                Check(!Equal(Render(source, tracked, time), Render(source, changed, time)),
                    $"manual focus changes visible pixels outside zoom at {time}s");
            }
            Check(Equal(Render(source, tracked, 3), Render(source, changed, 3)),
                "active zoom has priority over the manual focus");
            Check(!Equal(Render(source, tracked, 3), Render(source, basis, 3)),
                "active zoom actually changes the image");

            void Check(bool condition, string name)
            { Assert(condition, name); progress.Report(++checks); }
        }, "scene focus regression fixture");
        return checks;
    }

    public static async Task<int> RunCameraHeightAsync()
    {
        int checks = 0;
        await MediaExport.RunWorkerAsync((progress, _) =>
        {
            var source = Solid(640, 360, Colors.Black);
            var camera = Pattern(96, 72);
            foreach (string shape in CameraShapes)
            {
                var state = State(2) with
                {
                    Camera = Camera(shape) with { UseRecordedPlacement = true },
                    CameraPlacements = [new(0, .12, .18, .3, .35, 16d / 9)]
                };
                var changed = state.WithCameraStyle(state.Camera with { Height = .8 });
                Check(changed.Camera.UseRecordedPlacement, shape + " ignored height preserves recorded placement choice");
                Check(ReferenceEquals(changed.CameraPlacements, state.CameraPlacements), shape + " recorded placement track retained");
                Check(Equal(Render(source, state, .5, camera), Render(source, changed, .5, camera)),
                    shape + " ignored height preserves recorded placement pixels");
                var manual = state with { Camera = state.Camera with { UseRecordedPlacement = false }, CameraPlacements = [] };
                Check(Equal(Render(source, manual, .5, camera), Render(source,
                    manual with { Camera = manual.Camera with { Height = .8 } }, .5, camera)),
                    shape + " shape determines height from width and aspect");
            }
            var layout = State(2) with { Camera = Camera("方形") with { UseRecordedPlacement = true } };
            Check(!layout.WithCameraStyle(layout.Camera with { Width = .3 }).Camera.UseRecordedPlacement,
                "actual width edit still switches to manual layout");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(layout.WithCameraStyle(layout.Camera with { Height = .8 })));
            Check(json.RootElement.GetProperty("Camera").GetProperty("Height").GetDouble() == .8,
                "legacy height field remains serializable");

            void Check(bool condition, string name)
            { Assert(condition, name); progress.Report(++checks); }
        }, "scene camera height regression fixture");
        return checks;
    }

    public static async Task<int> RunCameraOpeningAsync()
    {
        int checks = 0;
        await MediaExport.RunWorkerAsync((progress, _) =>
        {
            const int width = 640, height = 360;
            const double time = .7;
            var source = Solid(width, height, Colors.Black);
            var camera = Solid(96, 72, Colors.White);
            var opening = new OpeningSequenceStyle(true, "聚拢", "swift", 2, 0, ["摄像头"], ["摄像头"]);
            var sample = OpeningSequenceTimeline.Sample(opening, "摄像头", ["摄像头"], time, width, height);
            foreach (string shape in CameraShapes)
            {
                var style = Camera(shape) with
                {
                    Height = .8, CropLeft = .125, CropRight = .125, CropTop = .25, CropBottom = .125,
                    UseRecordedPlacement = false
                };
                var state = State(3) with { Camera = style, Opening = opening };
                // The white fixture has a 72x45 crop; original aspect must use that image, not 96x72.
                double cameraWidth = style.Width * width;
                double cameraHeight = shape switch
                {
                    "圆形" or "方形" => cameraWidth, "横向" => cameraWidth * 9 / 16,
                    "纵向" => cameraWidth * 16 / 9, "原始比例" => cameraWidth * 45 / 72,
                    _ => throw new InvalidOperationException()
                };
                var actualRect = new Rect(style.X * width, style.Y * height, cameraWidth, cameraHeight);
                CheckBounds(Render(source, state, time, camera), OpeningRect(actualRect, sample),
                    shape + " opening scales around the actual cropped camera rectangle");
                Check(Equal(Render(source, state, 2.5, camera), Render(source,
                    state with { Opening = opening with { Enabled = false } }, 2.5, camera)),
                    shape + " completed opening preserves normal camera pixels");
            }

            var recorded = State(3) with
            {
                Camera = Camera("方形") with { Height = .8, UseRecordedPlacement = true },
                Opening = opening,
                CameraPlacements = [new(0, .1, .15, .3, .35, 16d / 9)]
            };
            var placement = recorded.CameraPlacements[0];
            double fittedHeight = Math.Min(height, width / placement.SourceAspect), fittedWidth = fittedHeight * placement.SourceAspect;
            var recordedRect = new Rect((width - fittedWidth) / 2 + placement.X * fittedWidth,
                (height - fittedHeight) / 2 + placement.Y * fittedHeight, placement.Width * fittedWidth, placement.Height * fittedHeight);
            CheckBounds(Render(source, recorded, time, camera), OpeningRect(recordedRect, sample),
                "recorded placement keeps its own opening pivot and rectangle");

            void Check(bool condition, string name)
            { Assert(condition, name); progress.Report(++checks); }
            void CheckBounds(BitmapSource image, Rect expected, string name)
            {
                var bounds = ColoredBounds(image);
                Check(!bounds.IsEmpty && Math.Abs(bounds.Left - expected.Left) <= 1.5
                    && Math.Abs(bounds.Top - expected.Top) <= 1.5 && Math.Abs(bounds.Right - expected.Right) <= 1.5
                    && Math.Abs(bounds.Bottom - expected.Bottom) <= 1.5,
                    name + $"; expected {expected}, actual {bounds}");
            }
        }, "scene camera opening regression fixture");
        return checks;
    }

    private static EditorState State(double duration) => EditorState.Initial(duration) with
    { Style = PlainStyle(), Pointer = new PointerAppearance(ClickFeedback: "关闭") };
    private static SceneStyle PlainStyle() => new() { Padding = 0, Corner = 0, Border = 0, Shadow = 0, Background = "#FF000000" };
    private static CameraStyle Camera(string shape) => new()
    { Enabled = true, Shape = shape, X = .05, Y = .08, Width = .2, Height = .3, Corner = 0, Shadow = 0 };
    private static BitmapSource Render(BitmapSource source, EditorState state, double time, BitmapSource? camera = null)
        => SceneCompositor.Render(source, state, time, source.PixelWidth, source.PixelHeight, camera);
    private static byte[] Pixels(BitmapSource image)
    { var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels; }
    private static bool Equal(BitmapSource first, BitmapSource second) => Pixels(first).AsSpan().SequenceEqual(Pixels(second));
    private static void Assert(bool condition, string name)
    { if (!condition) throw new Exception("FAIL scene regression: " + name); }
    private static Rect OpeningRect(Rect rect, OpeningSequenceSample sample) => new(
        rect.X + sample.OffsetX + rect.Width * (1 - sample.Scale) / 2,
        rect.Y + sample.OffsetY + rect.Height * (1 - sample.Scale) / 2,
        rect.Width * sample.Scale, rect.Height * sample.Scale);
    private static Rect ColoredBounds(BitmapSource image)
    {
        var pixels = Pixels(image); int left = image.PixelWidth, top = image.PixelHeight, right = -1, bottom = -1;
        for (int y = 0; y < image.PixelHeight; y++) for (int x = 0; x < image.PixelWidth; x++)
        {
            int offset = (y * image.PixelWidth + x) * 4;
            if (pixels[offset] <= 16 && pixels[offset + 1] <= 16 && pixels[offset + 2] <= 16) continue;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return right < left ? Rect.Empty : new(left, top, right - left + 1, bottom - top + 1);
    }
    private static BitmapSource Solid(int width, int height, Color color) => Image(width, height, (x, y) => color);
    private static BitmapSource Pattern(int width, int height) => Image(width, height, (x, y)
        => Color.FromRgb((byte)(x * 3 + y * 11), (byte)(x * 7 + y * 5), (byte)(x * 13 + y * 17)));
    private static BitmapSource Image(int width, int height, Func<int, int, Color> colorAt)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            var color = colorAt(x, y); int offset = (y * width + x) * 4;
            pixels[offset] = color.B; pixels[offset + 1] = color.G; pixels[offset + 2] = color.R; pixels[offset + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        image.Freeze(); return image;
    }
}
