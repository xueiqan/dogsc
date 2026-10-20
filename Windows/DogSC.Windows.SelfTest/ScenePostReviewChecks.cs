using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DogSC.Core;

namespace DogSC.Windows.SelfTest;

internal static class ScenePostReviewChecks
{
    public static async Task<int> RunAsync(string root, string phase)
    {
        if (phase is not ("tiny" or "crop-motion" or "camera-basis-boundary")) throw new ArgumentException("Unknown scene post-review phase: " + phase);
        root = Path.GetFullPath(root);
        MediaExport.RejectLinks(root); Directory.CreateDirectory(root); MediaExport.RejectLinks(root);
        var cases = new List<string>();
        var measurements = new List<object>();
        await MediaExport.RunWorkerAsync((progress, _) =>
        {
            const int width = 640, height = 360;
            var source = Solid(width, height, Colors.Black);
            if (phase == "tiny")
            {
                var camera = Image(2, 2, (x, y) => (x, y) switch
                { (0, 0) => Colors.Red, (1, 0) => Colors.Blue, (0, 1) => Colors.Lime, _ => Colors.White });
                var crops = new[]
                {
                    (Left: .6, Right: .3, Top: .6, Bottom: .3, Color: Colors.White, Corner: "bottom-right"),
                    (Left: .6, Right: .3, Top: .3, Bottom: .6, Color: Colors.Blue, Corner: "top-right"),
                    (Left: .3, Right: .6, Top: .6, Bottom: .3, Color: Colors.Lime, Corner: "bottom-left"),
                    (Left: .3, Right: .6, Top: .3, Bottom: .6, Color: Colors.Red, Corner: "top-left")
                };
                foreach (var crop in crops)
                {
                    var state = State() with
                    {
                        Camera = Camera() with { X = .1, Y = .1, CropLeft = crop.Left, CropRight = crop.Right,
                            CropTop = crop.Top, CropBottom = crop.Bottom }
                    };
                    state.Validate(3);
                    Check(true, "a ninety-percent two-pixel crop is accepted by the camera settings contract: " + crop.Corner);
                    var image = Render(source, state, .5, camera);
                    Check(image.PixelWidth == width && image.PixelHeight == height,
                        "the valid two-by-two crop renders without throwing or resizing the canvas: " + crop.Corner);
                    var actual = ColorAt(image, 128, 100);
                    Check(actual.R == crop.Color.R && actual.G == crop.Color.G && actual.B == crop.Color.B,
                        "the remaining source pixel fills the camera and retains its asymmetric corner color: " + crop.Corner);
                    if (crop.Color == Colors.White)
                        Check(WhitePixels(image) > 100, "the tiny bottom-right crop produces a visible white camera instead of a blank output");
                    measurements.Add(new { sample = crop.Corner, centerRed = actual.R, centerGreen = actual.G, centerBlue = actual.B });
                }
            }
            else if (phase == "crop-motion")
            {
                var camera = Solid(96, 72, Colors.White);
                var state = State() with
                {
                    Camera = Camera() with { CropLeft = .125, CropRight = .125, CropTop = .25, CropBottom = .125 }
                };
                state.Validate(3); Check(true, "the original-aspect fixture has a legal seventy-two-by-forty-five camera crop");
                var baseline = Render(source, state, .5, camera);
                var baselineRect = new Rect(32, 28.8, 128, 80);
                CheckBounds(baseline, baselineRect, "the static cropped camera uses the visible one-point-six aspect ratio");
                CheckAspect(baseline, "the static original-aspect camera retains the cropped source ratio");

                var tracked = state with
                {
                    CameraMotionClips = [new(Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000007"),
                        new(2, .5, .2, .3, ZoomEasingPreset.Linear), new(new(.7, .6), .4, "原始比例", 0))]
                };
                tracked.Validate(3); Check(true, "the future camera motion remains a valid authored three-second timeline");
                Check(Equal(baseline, Render(source, tracked, .5, camera)),
                    "adding a future camera motion does not change any cropped-camera pixel before its start");
                var active = Render(source, tracked, 2.35, camera);
                CheckBounds(active, new Rect(286.72, 129.6, 230.4, 144),
                    "the active original-aspect camera motion sizes and positions the cropped image");
                CheckAspect(active, "the active camera target preserves the visible one-point-six ratio");
                Check(Equal(baseline, Render(source, tracked, 2.9, camera)),
                    "the completed camera return restores the same cropped basis pixels");

                var legacy = state with
                {
                    Camera = state.Camera with { LegacySize = .4, PositionX = .3, PositionY = .4, ShadowStrength = 0 }
                };
                legacy.Validate(3); Check(true, "the compatible legacy camera size remains valid with original-aspect cropping");
                var legacyImage = Render(source, legacy, .5, camera);
                CheckBounds(legacyImage, new Rect(122.88, 86.4, 230.4, 144),
                    "the legacy camera-size path uses the same cropped aspect and remaining-space position contract");
                CheckAspect(legacyImage, "the compatible legacy camera size retains the visible one-point-six ratio");

                var shapes = new[]
                {
                    (Shape: "横向", Aspect: 1.5, TargetRect: new Rect(296.8, 129.6, 216, 144),
                        Id: Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000008")),
                    (Shape: "纵向", Aspect: .72, TargetRect: new Rect(375.424, 129.6, 103.68, 144),
                        Id: Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000009"))
                };
                foreach (var shape in shapes)
                {
                    var shaped = state with { Camera = state.Camera with { Shape = shape.Shape } };
                    var shapedBaseline = Render(source, shaped, .5, camera);
                    var shapedTrack = shaped with
                    {
                        CameraMotionClips = [new(shape.Id, new(2, .5, .2, .3, ZoomEasingPreset.Linear),
                            new(new(.7, .6), .4, shape.Shape, 0))]
                    };
                    shapedTrack.Validate(3);
                    Check(Equal(shapedBaseline, Render(source, shapedTrack, .5, camera)),
                        shape.Shape + " future motion retains every pixel of the native static shape before its start");
                    Check(Equal(shapedBaseline, Render(source, shapedTrack, 2.9, camera)),
                        shape.Shape + " completed return restores the native static shape pixels");
                    var shapedTarget = Render(source, shapedTrack, 2.35, camera);
                    CheckBounds(shapedTarget, shape.TargetRect, shape.Shape + " active motion retains the original Swift target geometry");
                    var targetBounds = ColoredBounds(shapedTarget);
                    Check(!targetBounds.IsEmpty && Math.Abs(targetBounds.Width / targetBounds.Height - shape.Aspect) <= .03,
                        shape.Shape + " active target retains the compatible " + shape.Aspect + " aspect ratio");
                }
            }
            else
            {
                var camera = Solid(96, 72, Colors.White);
                var fixtures = new[]
                {
                    (Name: "very-small-width", Width: .01, X: .05, Y: .08, StaticRect: new Rect(32, 28.8, 6.4, 3.6),
                        Id: Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000010")),
                    (Name: "partly-outside-right-edge", Width: .2, X: .9, Y: .08, StaticRect: new Rect(576, 28.8, 128, 72),
                        Id: Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000011")),
                    (Name: "large-contained-camera", Width: .65, X: .05, Y: .25, StaticRect: new Rect(32, 90, 416, 234),
                        Id: Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000012")),
                    (Name: "partly-outside-bottom-edge", Width: .3, X: .05, Y: .9, StaticRect: new Rect(32, 324, 192, 108),
                        Id: Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000013"))
                };
                foreach (var fixture in fixtures)
                {
                    var state = State() with
                    {
                        Camera = Camera() with { Shape = "横向", Width = fixture.Width, X = fixture.X, Y = fixture.Y }
                    };
                    var tracked = state with
                    {
                        CameraMotionClips = [new(fixture.Id, new(2, .5, .2, .3, ZoomEasingPreset.Linear),
                            new(new(.7, .6), .4, "横向", 0))]
                    };
                    tracked.Validate(3); Check(true, fixture.Name + " is accepted by the existing camera and motion settings contract");
                    var baseline = Render(source, state, .5, camera);
                    var visibleRect = Rect.Intersect(fixture.StaticRect, new Rect(0, 0, width, height));
                    CheckBounds(baseline, visibleRect, fixture.Name + " has the expected visible static camera geometry");
                    Check(Equal(baseline, Render(source, tracked, .5, camera)),
                        fixture.Name + " keeps the original pixels when a camera motion is authored in the future");
                    Check(Equal(baseline, Render(source, tracked, 2.9, camera)),
                        fixture.Name + " restores the exact original pixels after the camera return finishes");
                    Check(Equal(baseline, Render(source, tracked, 2, camera)),
                        fixture.Name + " starts an active camera motion at the exact existing static frame");
                    var initialBounds = ColoredBounds(baseline);
                    var nextBounds = ColoredBounds(Render(source, tracked, 2.00001, camera));
                    Check(!nextBounds.IsEmpty && Math.Abs(nextBounds.Left - initialBounds.Left) <= 1.5
                        && Math.Abs(nextBounds.Top - initialBounds.Top) <= 1.5 && Math.Abs(nextBounds.Right - initialBounds.Right) <= 1.5
                        && Math.Abs(nextBounds.Bottom - initialBounds.Bottom) <= 1.5,
                        fixture.Name + " remains geometrically continuous immediately after the motion start");
                    Check(!Equal(baseline, Render(source, tracked, 2.35, camera)),
                        fixture.Name + " still performs the authored motion after its lead-in");
                    measurements.Add(new { sample = fixture.Name, initialLeft = initialBounds.Left, initialTop = initialBounds.Top,
                        initialWidth = initialBounds.Width, initialHeight = initialBounds.Height,
                        nextLeft = nextBounds.Left, nextTop = nextBounds.Top, nextWidth = nextBounds.Width, nextHeight = nextBounds.Height });
                }

                var recorded = State() with
                {
                    Camera = Camera() with { Shape = "方形", UseRecordedPlacement = true },
                    CameraPlacements = [new(0, .1, .15, .3, .35, 16d / 9)]
                };
                var recordedTrack = recorded with
                {
                    CameraMotionClips = [new(Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000014"),
                        new(2, .5, .2, .3, ZoomEasingPreset.Linear), new(new(.7, .6), .4, "横向", 0))]
                };
                recordedTrack.Validate(3); Check(true, "a fixed recorded camera placement and future motion remain valid together");
                var recordedBaseline = Render(source, recorded, .5, camera);
                CheckBounds(recordedBaseline, new Rect(64, 54, 192, 126),
                    "the enabled recorded placement determines the actual camera rectangle before authored motion");
                Check(Equal(recordedBaseline, Render(source, recordedTrack, .5, camera)),
                    "a future camera motion preserves the enabled recorded placement pixels before its start");
                Check(Equal(recordedBaseline, Render(source, recordedTrack, 2, camera)),
                    "camera motion starts at the exact frame of the enabled recorded placement");
                Check(Equal(recordedBaseline, Render(source, recordedTrack, 2.9, camera)),
                    "camera motion returns to the enabled recorded placement after finishing");
                Check(!Equal(recordedBaseline, Render(source, recordedTrack, 2.35, camera)),
                    "the enabled recorded placement still permits its authored camera motion after the lead-in");

                var zoomed = State() with
                {
                    Camera = Camera() with { ScaleDuringZoom = .7 },
                    ZoomClips = [new(Guid.Parse("3d40e026-7d26-4e21-a2d7-000000000020"), .1, 1, 1.6,
                        new(.6, .4), EnterDuration: .1, ExitDuration: .1)]
                };
                var zoomedTrack = zoomed with { CameraMotionClips = recordedTrack.CameraMotionClips };
                zoomedTrack.Validate(3);
                Check(new ZoomAnimationTrack(zoomed.ZoomClips).Sample(.5).Scale > 1 && zoomed.Camera.ScaleDuringZoom == .7,
                    "the current screen zoom is active while the future camera motion has a seventy-percent zoom-size setting");
                Check(Equal(Render(source, zoomed, .5, camera), Render(source, zoomedTrack, .5, camera)),
                    "adding a future camera motion during screen zoom leaves the existing static camera pixels unchanged");
            }

            void Check(bool condition, string name)
            {
                if (!condition) throw new Exception("FAIL scene post-review regression: " + name);
                cases.Add(name); progress.Report(cases.Count); Console.WriteLine("PASS scene post-review: " + name);
            }
            void CheckBounds(BitmapSource image, Rect expected, string name)
            {
                var actual = ColoredBounds(image);
                Check(!actual.IsEmpty && Math.Abs(actual.Left - expected.Left) <= 1.5 && Math.Abs(actual.Top - expected.Top) <= 1.5
                    && Math.Abs(actual.Right - expected.Right) <= 1.5 && Math.Abs(actual.Bottom - expected.Bottom) <= 1.5,
                    name + $"; expected {expected}, actual {actual}");
                measurements.Add(new { sample = name, left = actual.Left, top = actual.Top, width = actual.Width, height = actual.Height });
            }
            void CheckAspect(BitmapSource image, string name)
            {
                var bounds = ColoredBounds(image);
                Check(!bounds.IsEmpty && Math.Abs(bounds.Width / bounds.Height - 1.6) <= .03, name);
            }
        }, "scene post-review " + phase + " WPF pixel fixture");
        var report = Path.Combine(root, "scene-post-review-" + phase + "-result.json");
        MediaExport.RejectLinks(report);
        File.WriteAllText(report, JsonSerializer.Serialize(new { success = true, actualWpfPixels = true, checks = cases.Count, cases, measurements },
            new JsonSerializerOptions { WriteIndented = true }));
        return cases.Count;
    }

    private static EditorState State() => EditorState.Initial(3) with
    {
        Style = new SceneStyle { Padding = 0, Corner = 0, Border = 0, Shadow = 0, Background = "#FF000000" },
        Pointer = new PointerAppearance(ClickFeedback: "关闭")
    };
    private static CameraStyle Camera() => new()
    { Enabled = true, UseRecordedPlacement = false, Shape = "原始比例", X = .05, Y = .08, Width = .2, Corner = 0, Shadow = 0 };
    private static BitmapSource Render(BitmapSource source, EditorState state, double time, BitmapSource camera)
        => SceneCompositor.Render(source, state, time, source.PixelWidth, source.PixelHeight, camera);
    private static byte[] Pixels(BitmapSource image)
    { var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels; }
    private static bool Equal(BitmapSource first, BitmapSource second) => first.PixelWidth == second.PixelWidth
        && first.PixelHeight == second.PixelHeight && Pixels(first).AsSpan().SequenceEqual(Pixels(second));
    private static Color ColorAt(BitmapSource image, int x, int y)
    { var pixels = Pixels(image); int offset = (y * image.PixelWidth + x) * 4; return Color.FromRgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]); }
    private static int WhitePixels(BitmapSource image)
    {
        var pixels = Pixels(image); int count = 0;
        for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] > 240 && pixels[i + 1] > 240 && pixels[i + 2] > 240) count++;
        return count;
    }
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
    private static BitmapSource Solid(int width, int height, Color color) => Image(width, height, (_, _) => color);
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
