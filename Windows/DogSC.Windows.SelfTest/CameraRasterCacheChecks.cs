using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DogSC.Windows.SelfTest;

internal static class CameraRasterCacheChecks
{
    public static async Task<int> RunAsync(string root)
    {
        var cases = new List<string>();
        var hashes = new Dictionary<string, string>();
        await MediaExport.RunWorkerAsync((progress, _) =>
        {
            var source = Image(640, 360, PixelFormats.Bgr32, Pixels(640, 360, false)); source.Freeze();
            var cameraPixels = Pixels(80, 60, true);
            var camera = Image(80, 60, PixelFormats.Pbgra32, cameraPixels); camera.Freeze();
            var coldCamera = Image(80, 60, PixelFormats.Pbgra32, cameraPixels);
            var state = EditorState.Initial(1) with
            {
                Style = new SceneStyle { Padding = 0, Corner = 0, Border = 0, Shadow = 0 },
                Camera = new CameraStyle { Enabled = true, Shape = "横向", Mirror = true, Corner = 0, Shadow = 0,
                    X = .13, Y = .2, Width = .35, Height = .3 },
                Pointer = new PointerAppearance(ClickFeedback: "关闭")
            };
            var first = Render(source, state, camera); var firstRaster = CachedRaster(camera);
            var warm = Render(source, state, camera);
            Check(firstRaster is not null && ReferenceEquals(firstRaster, CachedRaster(camera)), "warm raster reused");
            Check(Hash(first) == Hash(warm) && Hash(first) == Hash(Render(source, state, coldCamera)), "cold warm pixels identical");
            Check(Hash(first) == Hash(LegacyBlend(source, firstRaster!)), "opaque transparent alpha blend unchanged");
            hashes["baseline"] = Hash(first);

            foreach (var (name, style) in new[]
            {
                ("mirror off", state.Camera with { Mirror = false }),
                ("position", state.Camera with { X = .56, Y = .3 }),
                ("shape crop alpha", state.Camera with { Shape = "圆形", CropLeft = .1, CropTop = .1, Corner = .1 }),
                ("clipped", state.Camera with { X = .95, Y = .93 }),
                ("empty", state.Camera with { X = 1, Y = 1 })
            })
            {
                var altered = state with { Camera = style };
                var actual = Render(source, altered, camera);
                Check(Hash(actual) == Hash(Render(source, altered, coldCamera)), name + " pixels identical");
                Check(Hash(actual) == Hash(LegacyBlend(source, CachedRaster(camera)!)), name + " legacy blend identical");
                hashes[name] = Hash(actual);
            }
            Check(hashes["mirror off"] != hashes["baseline"] && hashes["position"] != hashes["baseline"], "mirror and position invalidate cache");

            var tracked = state with { CameraPlacements = [new(0, .1, .1, .25, .3, 16.0 / 9), new(.5, .6, .5, .25, .3, 16.0 / 9)] };
            var beforeMove = Render(source, tracked, camera, .2);
            var afterMove = Render(source, tracked, camera, .7);
            Check(Hash(beforeMove) == Hash(Render(source, tracked, coldCamera, .2))
                && Hash(afterMove) == Hash(Render(source, tracked, coldCamera, .7)) && Hash(beforeMove) != Hash(afterMove), "recorded placement invalidates cache");
            var nextPixels = Pixels(80, 60, true, 91);
            var nextCamera = Image(80, 60, PixelFormats.Pbgra32, nextPixels); nextCamera.Freeze();
            Check(Hash(Render(source, state, nextCamera)) == Hash(Render(source, state, Image(80, 60, PixelFormats.Pbgra32, nextPixels)))
                && Hash(Render(source, state, nextCamera)) != hashes["baseline"], "next camera frame has fresh pixels");
            Check(Hash(Render(source, state with { Camera = state.Camera with { Hidden = true } }, camera)) == Hash(Bytes(source)), "hidden camera preserves screen");
            Check(Hash(Render(source, state with { Camera = state.Camera with { Enabled = false } }, camera)) == Hash(Bytes(source)), "disabled camera preserves screen");
            var smaller = Image(320, 180, PixelFormats.Bgr32, Pixels(320, 180, false)); smaller.Freeze();
            Check(Hash(Render(smaller, state, camera)) == Hash(Render(smaller, state, coldCamera)), "output dimensions invalidate cache");

            var mutable = new WriteableBitmap(coldCamera);
            var oldPixels = Render(source, state, mutable);
            mutable.WritePixels(new Int32Rect(0, 0, 80, 60), nextPixels, 80 * 4, 0);
            Check(CachedRaster(mutable) is null && Hash(oldPixels) != Hash(Render(source, state, mutable))
                && Hash(Render(source, state, mutable)) == Hash(Render(source, state, nextCamera)), "mutable frames never cached");

            var expected = new[] { state, state with { Camera = state.Camera with { Mirror = false } }, tracked };
            var expectedHashes = expected.Select((item, index) => Hash(Render(source, item, coldCamera, index == 2 ? .7 : .2))).ToArray();
            var errors = new ConcurrentQueue<Exception>();
            using var start = new ManualResetEventSlim();
            var threads = Enumerable.Range(0, 4).Select(worker => new Thread(() =>
            {
                try
                {
                    start.Wait();
                    for (int iteration = 0; iteration < 9; iteration++)
                    {
                        int index = (worker + iteration) % expected.Length;
                        if (Hash(Render(source, expected[index], camera, index == 2 ? .7 : .2)) != expectedHashes[index])
                            throw new Exception("并发摄像头缓存返回了其他布局的像素。");
                    }
                }
                catch (Exception error) { errors.Enqueue(error); }
            }) { IsBackground = true, Name = "camera raster cache fixture" }).ToArray();
            foreach (var thread in threads) { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
            start.Set();
            foreach (var thread in threads) if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("摄像头栅格缓存并发检查超时。");
            if (errors.TryDequeue(out var failure)) throw failure;
            Check(true, "four STA workers preserve pixels across layouts");

            void Check(bool condition, string name)
            {
                if (!condition) throw new Exception("FAIL camera raster cache: " + name);
                cases.Add(name); progress.Report(cases.Count);
            }
        }, "camera raster cache fixture");
        root = Path.GetFullPath(root); MediaExport.RejectLinks(root); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "camera-raster-cache-result.json"), JsonSerializer.Serialize(new
        { success = true, checks = cases.Count, cases, hashes, concurrentWorkers = 4, concurrentRenders = 36 }, new JsonSerializerOptions { WriteIndented = true }));
        return cases.Count;
    }

    private static byte[] Render(BitmapSource source, EditorState state, BitmapSource camera, double time = .2)
        => Bytes(SceneCompositor.Render(source, state, time, source.PixelWidth, source.PixelHeight, camera));
    private static BitmapSource Image(int width, int height, PixelFormat format, byte[] pixels)
        => BitmapSource.Create(width, height, 96, 96, format, null, pixels, width * 4);
    private static byte[] Bytes(BitmapSource image)
    { var result = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(result, image.PixelWidth * 4, 0); return result; }
    private static string Hash(byte[] pixels) => Convert.ToHexString(SHA256.HashData(pixels));
    private static byte[] Pixels(int width, int height, bool transparency, int phase = 0)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            int alpha = transparency ? (x % 3) switch { 0 => 0, 1 => 128, _ => 255 } : 255;
            pixels[offset] = (byte)((x * 7 + y * 3 + phase) % 256 * alpha / 255);
            pixels[offset + 1] = (byte)((x * 11 + y + phase) % 256 * alpha / 255);
            pixels[offset + 2] = (byte)((x * 3 + y * 13 + phase) % 256 * alpha / 255);
            pixels[offset + 3] = (byte)alpha;
        }
        return pixels;
    }
    private static object? CachedRaster(BitmapSource camera)
    {
        var table = typeof(SceneCompositor).GetField("CameraRasters", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var arguments = new object?[] { camera, null };
        if (!(bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, arguments)!) return null;
        return arguments[1]!.GetType().GetField("Raster")!.GetValue(arguments[1]);
    }
    private static byte[] LegacyBlend(BitmapSource source, object raster)
    {
        var type = raster.GetType();
        var region = (Int32Rect)type.GetProperty("Region")!.GetValue(raster)!;
        var foreground = (byte[])type.GetProperty("Foreground")!.GetValue(raster)!;
        var pixels = Bytes(source);
        for (int y = 0; y < region.Height; y++) for (int x = 0; x < region.Width; x++)
        {
            int from = (y * region.Width + x) * 4, to = ((y + region.Y) * source.PixelWidth + x + region.X) * 4;
            int alpha = foreground[from + 3];
            for (int channel = 0; channel < 3; channel++) pixels[to + channel] = (byte)Math.Min(255,
                foreground[from + channel] + (pixels[to + channel] * (255 - alpha) + 127) / 255);
        }
        return pixels;
    }
}
