using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenRecorderLib;

namespace DogSC.Windows.SelfTest;

internal static class LiveCameraToggleChecks
{
    public static async Task<int> RunAsync(string root)
    {
        var cases = new List<string>();
        var placement = new RecordedCameraPlacement(0, .1, .1, .5, .5, 4.0 / 3);
        var applied = new ConcurrentQueue<(string Id, int Width, byte Blue, byte Alpha)>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int block = 0;
        var overlay = new RealtimeCameraOverlay(120, placement, startDisabled: true);
        try
        {
            Check(overlay.Ready.IsCompletedSuccessfully, "disabled start is ready without acquiring a camera");
            var initial = overlay.Bind(640, 480, 120, async (image, cancellation) =>
            {
                if (Interlocked.Exchange(ref block, 0) != 0)
                { entered.TrySetResult(); await release.Task.WaitAsync(cancellation); }
                var pixel = Decode(image.SourceStream);
                applied.Enqueue((image.ID, pixel.Width, pixel.Blue, pixel.Alpha));
                return true;
            });
            var clear = Decode(initial.SourceStream);
            Check(clear.Width == 2 && clear.Alpha == 0 && initial.Size.Width == 2 && initial.Size.Height == 2,
                "disabled binding supplies a transparent two pixel placeholder");
            overlay.SubmitFrame(Frame(33));
            overlay.Refresh();
            await Task.Delay(50);
            Check(overlay.SubmittedFrames == 0 && applied.IsEmpty, "disabled frame callbacks do not encode or apply");

            overlay.SetEnabled(true);
            overlay.Refresh();
            await Task.Delay(50);
            Check(applied.IsEmpty, "enabling waits for a new camera frame");
            overlay.SubmitFrame(Frame(70));
            await Until(() => applied.Count == 1);
            Check(applied.Last().Width == 560 && applied.Last().Blue == 70 && applied.Last().Alpha == 255,
                "enabled first frame is opaque live camera content");
            overlay.SetEnabled(false);
            await Until(() => applied.Count == 2);
            Check(applied.Last().Width == 2 && applied.Last().Alpha == 0,
                "disable clears the native overlay without stopping the worker");
            Check(typeof(RealtimeCameraOverlay).GetField("latestFrame", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)!.GetValue(overlay) is null,
                "disable releases the pending camera frame");
            overlay.SetEnabled(false);
            overlay.UpdatePlacement(placement with { X = .2 });
            overlay.Refresh();
            await Task.Delay(50);
            Check(applied.Count == 2, "repeated disable and placement changes do not republish transparent images");
            overlay.SetEnabled(true);
            overlay.Refresh();
            await Task.Delay(50);
            Check(applied.Count == 2, "reenabling cannot flash the previous prepared frame");
            overlay.SubmitFrame(Frame(150));
            await Until(() => applied.Count == 3);
            Check(applied.Last().Blue == 150, "reenabling accepts only newly submitted camera pixels");

            Interlocked.Exchange(ref block, 1);
            overlay.SubmitFrame(Frame(190));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            overlay.SetEnabled(false);
            overlay.SetEnabled(true);
            overlay.SubmitFrame(Frame(220));
            release.TrySetResult();
            await Until(() => applied.Count >= 6);
            var last = applied.ToArray()[^3..];
            Check(last[0].Blue == 190 && last[1].Width == 2 && last[1].Alpha == 0 && last[2].Blue == 220,
                "disable during a native update is followed by transparent clear then fresh content");
            Check(applied.All(value => value.Id == initial.ID), "all toggle updates preserve native overlay identity");

            var race = Task.Run(() => { for (int i = 0; i < 100; i++) overlay.SetEnabled((i & 1) == 0); });
            overlay.Stop();
            await race;
            await overlay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            long submitted = overlay.SubmittedFrames, count = overlay.AppliedFrames;
            overlay.SetEnabled(true); overlay.SubmitFrame(Frame(17)); overlay.SetEnabled(false);
            Check(overlay.SubmittedFrames == submitted && overlay.AppliedFrames == count,
                "stop racing with toggles completes and rejects subsequent changes");
        }
        finally { release.TrySetResult(); await overlay.DisposeAsync(); }

        var early = new RealtimeCameraOverlay(30, placement);
        try
        {
            early.SetEnabled(false);
            Check(early.Ready.IsCompletedSuccessfully, "disabling before the original first frame unblocks startup");
            early.Stop(); await early.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await early.DisposeAsync(); }

        int clearAttempts = 0, allowClear = 0;
        var resumedFrames = new ConcurrentQueue<byte>();
        var paused = new RealtimeCameraOverlay(120, placement, startDisabled: true);
        try
        {
            paused.Bind(640, 480, 120, (image, _) =>
            {
                var pixel = Decode(image.SourceStream);
                if (pixel.Width == 2)
                { Interlocked.Increment(ref clearAttempts); return Task.FromResult(Volatile.Read(ref allowClear) != 0); }
                resumedFrames.Enqueue(pixel.Blue); return Task.FromResult(true);
            });
            paused.SetEnabled(true); paused.SubmitFrame(Frame(50));
            await Until(() => resumedFrames.Count == 1);
            paused.SetEnabled(false);
            await Until(() => Volatile.Read(ref clearAttempts) == 1);
            await Task.Delay(50);
            Check(clearAttempts == 1 && !paused.Completion.IsCompleted,
                "rejected paused clear remains pending without a retry loop");
            paused.Refresh();
            await Until(() => Volatile.Read(ref clearAttempts) == 2);
            await Task.Delay(50);
            Check(clearAttempts == 2, "one explicit refresh retries a rejected clear once");
            paused.SetEnabled(true); paused.SubmitFrame(Frame(230));
            paused.UpdatePlacement(placement with { X = .3 });
            await Task.Delay(50);
            Check(clearAttempts == 2 && resumedFrames.Count == 1,
                "new frames and placement cannot bypass a deferred clear after reopening");
            Volatile.Write(ref allowClear, 1); paused.Refresh();
            await Until(() => resumedFrames.Count == 2);
            Check(clearAttempts == 3 && resumedFrames.Last() == 230,
                "resume refresh clears stale native content before applying the reopened camera");
        }
        finally { await paused.DisposeAsync(); }

        var clearEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int racingAttempts = 0;
        var refreshRace = new RealtimeCameraOverlay(120, placement, startDisabled: true);
        try
        {
            refreshRace.Bind(640, 480, 120, async (_, cancellation) =>
            {
                if (Interlocked.Increment(ref racingAttempts) == 1)
                { clearEntered.TrySetResult(); await clearRelease.Task.WaitAsync(cancellation); return false; }
                return true;
            });
            refreshRace.SetEnabled(true); refreshRace.SetEnabled(false);
            await clearEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            refreshRace.Refresh(); clearRelease.TrySetResult();
            await Until(() => refreshRace.AppliedFrames == 1);
            Check(racingAttempts == 2, "resume refresh arriving during a rejected clear is retained");
        }
        finally { clearRelease.TrySetResult(); await refreshRace.DisposeAsync(); }

        root = Path.GetFullPath(root); MediaExport.RejectLinks(root); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "live-camera-toggle-result.json"), JsonSerializer.Serialize(
            new { success = true, checks = cases.Count, cases }, new JsonSerializerOptions { WriteIndented = true }));
        return cases.Count;

        void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAIL live camera toggle: " + name); cases.Add(name); }
    }

    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("摄像头开关回归等待超时。");
            await Task.Delay(10);
        }
    }

    private static BitmapSource Frame(byte blue)
    {
        var pixels = new byte[16 * 16 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = blue; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgr32, null, pixels, 16 * 4);
        image.Freeze(); return image;
    }

    private static (int Width, byte Blue, byte Alpha) Decode(Stream stream)
    {
        stream.Position = 0;
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(decoded.Frames[0], PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        return (frame.PixelWidth, pixels[0], pixels[3]);
    }
}
