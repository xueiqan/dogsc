using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenRecorderLib;

namespace DogSC.Windows.SelfTest;

internal static class RealtimeCameraOverlayChecks
{
    public static async Task<int> RunAsync(string root)
    {
        var cases = new List<string>();
        var hashes = new Dictionary<string, string>();
        await MediaExport.RunWorkerAsync((_, _) =>
        {
            var source = ColorCard(160, 80);
            var image = RealtimeCameraOverlay.MirrorAndFill(source);
            Check(image.IsFrozen && image.PixelWidth == 560 && image.PixelHeight == 380, "mirrored image keeps 560 by 380");
            var pixels = Bytes(image);
            Check(Pixel(pixels, 560, 20, 180) == (255, 0, 0, 255)
                && Pixel(pixels, 560, 540, 180) == (0, 0, 255, 255), "horizontal mirror preserves left right colors");
            Check(Pixel(pixels, 560, 280, 2).Item4 == 255 && Pixel(pixels, 560, 280, 377).Item4 == 255,
                "UniformToFill has no top bottom letterbox");
            // A wide frame has 16-pixel green strips at each edge. UniformToFill crops both away.
            var cropped = RealtimeCameraOverlay.MirrorAndFill(ColorCard(320, 80, greenEdges: true));
            var croppedBytes = Bytes(cropped);
            Check(Pixel(croppedBytes, 560, 2, 180) == (255, 0, 0, 255)
                && Pixel(croppedBytes, 560, 557, 180) == (0, 0, 255, 255), "UniformToFill crops camera side edges before mirror");
            hashes["mirror"] = Hash(pixels); hashes["uniformCrop"] = Hash(croppedBytes);

            var native = new RecordedCameraPlacement(0, 100.0 / 2560, 200.0 / 1600, 560.0 / 2560, 380.0 / 1600, 1.6);
            var original = RealtimeCameraOverlay.Layout(native, 2560, 1600);
            Check(original.Rectangle == new Int32Rect(100, 200, 560, 380)
                && original.Crop == new Int32Rect(0, 0, 560, 380), "native resolution placement is exact");
            var hd = RealtimeCameraOverlay.Layout(native, 1920, 1080);
            Check(hd.Rectangle == new Int32Rect(164, 135, 378, 257)
                && hd.Crop == original.Crop, "1080 output follows Uniform content and camera scale");
            var partial = RealtimeCameraOverlay.Layout(native with { X = -280.0 / 2560, Y = -190.0 / 1600 }, 2560, 1600);
            Check(partial.Rectangle == new Int32Rect(0, 0, 280, 190)
                && partial.Crop == new Int32Rect(280, 190, 280, 190), "partially outside capture is clipped with matching camera pixels");
            Check(RealtimeCameraOverlay.Layout(native with { X = 1 }, 2560, 1600).Rectangle.IsEmpty,
                "outside capture becomes transparent");
            var letterbox = RealtimeCameraOverlay.Layout(native with { X = -.1 }, 1920, 1080);
            Check(letterbox.Rectangle.X == 96, "camera cannot spill into source letterbox");
            try
            {
                _ = RealtimeCameraOverlay.Layout(native with { Width = double.NaN }, 640, 480);
                throw new Exception("FAIL live camera overlay accepted NaN");
            }
            catch (ArgumentException) { Check(true, "malformed placement rejected"); }
        }, "live camera overlay pixels");

        var placement = new RecordedCameraPlacement(0, .1, .1, .5, .5, 4.0 / 3);
        var overlay = new RealtimeCameraOverlay(120, placement);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new ConcurrentQueue<(ImageOverlay Image, string Hash)>();
        string? id = null;
        long callbackMilliseconds;
        try
        {
            overlay.SubmitFrame(Solid(160, 80, 17));
            await overlay.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            Check(overlay.Ready.IsCompletedSuccessfully, "Ready requires a prepared first frame");
            var initial = overlay.Bind(640, 480, 120, async (image, cancellation) =>
            {
                if (applied.IsEmpty) { first.TrySetResult(); await release.Task.WaitAsync(cancellation); }
                var decoded = Decode(image.SourceStream);
                applied.Enqueue((image, Hash(Bytes(decoded))));
                return true;
            });
            id = initial.ID;
            Check(initial.SourceStream.ReadByte() == 'B' && initial.SourceStream.ReadByte() == 'M', "live frames use lossless BMP");
            initial.SourceStream.Position = 0;
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var callbackWatch = Stopwatch.StartNew();
            for (int index = 0; index < 40; index++) overlay.SubmitFrame(Solid(160, 80, (byte)(40 + index)));
            var newest = Solid(160, 80, 190);
            overlay.SubmitFrame(newest);
            callbackWatch.Stop(); callbackMilliseconds = callbackWatch.ElapsedMilliseconds;
            Check(!release.Task.IsCompleted && overlay.SubmittedFrames == 42 && overlay.AppliedFrames == 0,
                "camera callbacks complete while native update is blocked");
            var pending = typeof(RealtimeCameraOverlay).GetField("latestFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(overlay);
            Check(ReferenceEquals(pending, newest), "bounded queue retains newest frame only");
            release.TrySetResult();
            await WaitUntilAsync(() => applied.Count >= 2);
            var snapshots = applied.ToArray();
            Check(snapshots[0].Hash != snapshots[^1].Hash && snapshots.All(item => item.Image.ID == id),
                "subsequent camera frames change pixels and preserve overlay identity");
            int beforeMove = applied.Count;
            overlay.UpdatePlacement(placement with { Time = 1, X = .4 });
            await WaitUntilAsync(() => applied.Count > beforeMove);
            var moved = applied.ToArray()[^1].Image;
            Check(moved.Offset.Width == 256 && moved.Offset.Height == 48, "placement changes redraw current frame");
            // Old streams remain readable; callers must not Dispose them while a native wrapper can still read.
            Check(snapshots[0].Image.SourceStream.CanRead && Decode(snapshots[0].Image.SourceStream).PixelWidth == 560,
                "previous stream remains readable after next update");
            overlay.Stop();
            await overlay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            long submitted = overlay.SubmittedFrames, updated = overlay.AppliedFrames;
            overlay.SubmitFrame(newest); overlay.UpdatePlacement(placement);
            Check(overlay.SubmittedFrames == submitted && overlay.AppliedFrames == updated, "stopped overlay rejects later camera callbacks");
        }
        finally { release.TrySetResult(); await overlay.DisposeAsync(); }

        var failed = new RealtimeCameraOverlay(30, placement);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        failed.Failed += error => failure.TrySetResult(error);
        try
        {
            failed.SubmitFrame(Solid(160, 80, 20)); await failed.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            _ = failed.Bind(640, 480, 30, (_, _) => throw new IOException("fixture overlay failure"));
            var error = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(error is IOException, "native update failure is reported");
            try { await failed.Completion; throw new Exception("FAIL live overlay swallowed failure"); }
            catch (IOException) { Check(true, "native update failure faults completion"); }
        }
        finally { try { await failed.DisposeAsync(); } catch (IOException) { } }

        var empty = new RealtimeCameraOverlay(30, placement);
        try
        {
            Check(!empty.Ready.IsCompleted, "no source frame does not pretend Ready");
            var invalid = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8);
            try { empty.SubmitFrame(invalid); throw new Exception("FAIL live overlay accepted mutable frame"); }
            catch (ArgumentException) { Check(true, "mutable callback frame rejected"); }
            empty.Stop(); await empty.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(empty.Ready.IsCanceled, "stop before first frame cancels Ready and releases worker");
        }
        finally { await empty.DisposeAsync(); }

        root = Path.GetFullPath(root); MediaExport.RejectLinks(root); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "realtime-camera-overlay-result.json"), JsonSerializer.Serialize(new
        { success = true, checks = cases.Count, cases, hashes, callbackMilliseconds, frameQueueCapacity = 1, overlayId = id }, new JsonSerializerOptions { WriteIndented = true }));
        return cases.Count;

        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL live camera overlay: " + name);
            cases.Add(name);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("实时摄像头自测等待更新超时。");
            await Task.Delay(10);
        }
    }
    private static BitmapSource ColorCard(int width, int height, bool greenEdges = false)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            if (greenEdges && (x < 16 || x >= width - 16)) pixels[i + 1] = 255;
            else pixels[i + (x < width / 2 ? 2 : 0)] = 255;
            pixels[i + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4); image.Freeze(); return image;
    }
    private static BitmapSource Solid(int width, int height, byte color)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = color; pixels[i + 2] = (byte)(255 - color); pixels[i + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4); image.Freeze(); return image;
    }
    private static BitmapSource Decode(Stream stream)
    {
        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var result = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0); result.Freeze(); return result;
    }
    private static byte[] Bytes(BitmapSource image)
    { var result = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(result, image.PixelWidth * 4, 0); return result; }
    private static (byte, byte, byte, byte) Pixel(byte[] bytes, int width, int x, int y)
    { int i = (y * width + x) * 4; return (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]); }
    private static string Hash(byte[] pixels) => Convert.ToHexString(SHA256.HashData(pixels));
}
