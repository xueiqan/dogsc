using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenRecorderLib;

namespace DogSC.Windows;

/// <summary>A latest-frame camera feed for the existing screen encoder, with no second device or video encoder.</summary>
public sealed class RealtimeCameraOverlay : IAsyncDisposable
{
    private sealed record PreparedFrame(BitmapSource Source, BitmapSource Image, MemoryStream Stream);
    private sealed record Binding(int Width, int Height, double FrameRate,
        Func<ImageOverlay, CancellationToken, Task<bool>> Apply);
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly double cameraFrameRate;
    private readonly ImageOverlay identity;
    private readonly byte[] transparentImage;
    private BitmapSource? latestFrame;
    private PreparedFrame? prepared;
    private RecordedCameraPlacement placement;
    private Binding? binding;
    private bool enabled, clearPending, clearDeferred;
    private long generation, refreshGeneration;
    private int stopped, disposed;
    private long submittedFrames, appliedFrames;

    public RealtimeCameraOverlay(double cameraFrameRate, RecordedCameraPlacement initialPlacement, bool startDisabled = false)
    {
        if (!double.IsFinite(cameraFrameRate) || cameraFrameRate is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(cameraFrameRate));
        ValidatePlacement(initialPlacement);
        this.cameraFrameRate = cameraFrameRate;
        enabled = !startDisabled;
        placement = initialPlacement;
        using var transparent = TransparentImage();
        transparentImage = transparent.ToArray();
        identity = new ImageOverlay(new MemoryStream(transparentImage, false))
        {
            AnchorPoint = Anchor.TopLeft, Stretch = StretchMode.Fill,
            Offset = new ScreenSize(0, 0), Size = new ScreenSize(2, 2)
        };
        if (startDisabled) ready.TrySetResult();
        var worker = new Thread(Work) { IsBackground = true, Name = "DogSC live camera overlay", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// <summary>The first camera image, or an explicitly disabled transparent placeholder, is ready for binding.</summary>
    public Task Ready => ready.Task;
    public Task Completion => completion.Task;
    public event Action<Exception>? Failed;
    internal long SubmittedFrames => Interlocked.Read(ref submittedFrames);
    internal long AppliedFrames => Interlocked.Read(ref appliedFrames);

    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            if (Volatile.Read(ref stopped) != 0 || enabled == value) return;
            enabled = value;
            generation++;
            latestFrame = null;
            prepared = null;
            if (!value) { clearPending = true; clearDeferred = false; ready.TrySetResult(); }
        }
        Signal();
    }

    // Called by the camera callback: retain just the newest small frozen frame and wake the worker.
    public void SubmitFrame(BitmapSource frame)
    {
        if (Volatile.Read(ref stopped) != 0) return;
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.IsFrozen || frame.PixelWidth is < 1 or > 560 || frame.PixelHeight is < 1 or > 380)
            throw new ArgumentException("摄像头叠加需要不超过560×380的冻结预览帧。", nameof(frame));
        lock (gate)
        {
            if (Volatile.Read(ref stopped) != 0 || !enabled) return;
            latestFrame = frame;
            Interlocked.Increment(ref submittedFrames);
        }
        Signal();
    }

    public void UpdatePlacement(RecordedCameraPlacement value)
    {
        if (Volatile.Read(ref stopped) != 0) return;
        ValidatePlacement(value);
        Interlocked.Exchange(ref placement, value);
        Signal();
    }

    internal ImageOverlay Bind(int width, int height, int screenFrameRate,
        Func<ImageOverlay, CancellationToken, Task<bool>> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (width is < 2 or > 32768 || height is < 2 or > 32768 || screenFrameRate is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(width));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref stopped) != 0, this);
        if (!Ready.IsCompletedSuccessfully)
            throw new InvalidOperationException("摄像头首帧尚未准备好，未开始录制。请等待画面后重试。");
        PreparedFrame? first;
        Binding configured;
        lock (gate)
        {
            if (binding is not null) throw new InvalidOperationException("摄像头叠加只能绑定一个录制会话。");
            first = prepared;
            configured = new(width, height, Math.Min(cameraFrameRate, screenFrameRate), apply);
            binding = configured;
        }
        // Disabled recording starts with a transparent placeholder; enabled recordings retain the prepared first frame.
        var initial = first is null ? ClearOverlay() : CreateOverlay(first, Volatile.Read(ref placement), width, height);
        Signal();
        return initial;
    }

    internal void Refresh()
    {
        lock (gate) { clearDeferred = false; refreshGeneration++; }
        Signal();
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        stopping.Cancel();
        Interlocked.Exchange(ref latestFrame, null);
        wake.Set();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        try { await Completion.ConfigureAwait(false); }
        finally
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) { wake.Dispose(); stopping.Dispose(); }
        }
    }

    private void Signal()
    {
        if (Volatile.Read(ref stopped) != 0) return;
        try { wake.Set(); }
        catch (ObjectDisposedException) when (Volatile.Read(ref stopped) != 0) { }
    }

    private void Work()
    {
        PreparedFrame? cached = null;
        long lastUpdate = 0;
        try
        {
            while (Volatile.Read(ref stopped) == 0)
            {
                wake.WaitOne();
                stopping.Token.ThrowIfCancellationRequested();
                Binding? configured;
                bool clear;
                long clearGeneration, clearRefreshGeneration;
                lock (gate)
                {
                    configured = binding;
                    clear = clearPending && !clearDeferred;
                    clearGeneration = generation;
                    clearRefreshGeneration = refreshGeneration;
                    if (clear) clearPending = false;
                }
                if (clear)
                {
                    cached = null;
                    if (configured is not null)
                    {
                        if (configured.Apply(ClearOverlay(), stopping.Token).GetAwaiter().GetResult())
                            Interlocked.Increment(ref appliedFrames);
                        else
                        {
                            lock (gate)
                            {
                                clearPending = true;
                                // Paused native capture rejects updates; retry only after Resume's Refresh.
                                if (generation == clearGeneration && refreshGeneration == clearRefreshGeneration) clearDeferred = true;
                            }
                            continue;
                        }
                    }
                    SignalIfFramePending();
                    continue;
                }
                double rate = configured?.FrameRate ?? cameraFrameRate;
                long waitTicks = (long)(Stopwatch.Frequency / rate) - (Stopwatch.GetTimestamp() - lastUpdate);
                if (lastUpdate != 0 && waitTicks > 0
                    && wake.WaitOne(TimeSpan.FromSeconds((double)waitTicks / Stopwatch.Frequency)))
                { Signal(); continue; }
                BitmapSource? frame;
                long version;
                lock (gate) { frame = enabled && !clearPending ? latestFrame : null; version = generation; }
                if (frame is null) continue;
                if (!ReferenceEquals(frame, cached?.Source))
                    cached = new(frame, MirrorAndFill(frame), new MemoryStream());
                if (cached.Stream.Length == 0) cached = cached with { Stream = EncodeOpaque(cached.Image) };
                lock (gate)
                {
                    if (!enabled || clearPending || generation != version) { cached = null; continue; }
                    prepared = cached;
                    ready.TrySetResult();
                }
                if (configured is null) continue;
                var next = CreateOverlay(cached, Volatile.Read(ref placement), configured.Width, configured.Height);
                stopping.Token.ThrowIfCancellationRequested();
                if (configured.Apply(next, stopping.Token).GetAwaiter().GetResult())
                    Interlocked.Increment(ref appliedFrames);
                lastUpdate = Stopwatch.GetTimestamp();
            }
            ready.TrySetCanceled();
            completion.TrySetResult();
        }
        catch (OperationCanceledException) when (Volatile.Read(ref stopped) != 0)
        {
            ready.TrySetCanceled(); completion.TrySetResult();
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref stopped, 1);
            ready.TrySetException(error);
            try { Failed?.Invoke(error); }
            catch (Exception subscriberError) { Trace.TraceError($"Camera overlay failure subscriber failed: {subscriberError}"); }
            completion.TrySetException(error);
        }
        finally
        {
            Interlocked.Exchange(ref latestFrame, null);
            lock (gate) prepared = null;
            // Never Dispose a stream given to SRL: ManagedIStream holds a GC root until its native Release.
            // This worker retains one prepared frame only; old stream roots are owned and released by SRL.
        }
    }

    private void SignalIfFramePending()
    {
        lock (gate) { if ((enabled && latestFrame is not null) || clearPending) Signal(); }
    }

    private ImageOverlay ClearOverlay() => new(identity)
    {
        SourceStream = new MemoryStream(transparentImage, false), SourcePath = null, AnchorPoint = Anchor.TopLeft,
        Stretch = StretchMode.Fill, Offset = new ScreenSize(0, 0), Size = new ScreenSize(2, 2)
    };

    internal static BitmapSource MirrorAndFill(BitmapSource frame)
    {
        const int width = 560, height = 380;
        double scale = Math.Max((double)width / frame.PixelWidth, (double)height / frame.PixelHeight);
        double imageWidth = frame.PixelWidth * scale, imageHeight = frame.PixelHeight * scale;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
            drawing.PushTransform(new ScaleTransform(-1, 1, width / 2.0, height / 2.0));
            drawing.DrawImage(frame, new Rect((width - imageWidth) / 2, (height - imageHeight) / 2, imageWidth, imageHeight));
            drawing.Pop(); drawing.Pop();
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze();
        return image;
    }

    internal ImageOverlay CreateOverlay(BitmapSource image, RecordedCameraPlacement value, int width, int height)
        => CreateOverlay(new PreparedFrame(image, image, EncodeOpaque(image)), value, width, height);

    private ImageOverlay CreateOverlay(PreparedFrame frame, RecordedCameraPlacement value, int width, int height)
    {
        var (rectangle, crop) = Layout(value, width, height);
        MemoryStream stream;
        if (rectangle.IsEmpty) stream = new MemoryStream(transparentImage, false);
        else if (crop == new Int32Rect(0, 0, 560, 380))
            // Each native wrapper needs its own seek position even when only placement changed.
            stream = new MemoryStream(frame.Stream.GetBuffer(), 0, checked((int)frame.Stream.Length), false, false);
        else
        {
            var clipped = new CroppedBitmap(frame.Image, crop); clipped.Freeze();
            stream = EncodeOpaque(clipped);
        }
        return new ImageOverlay(identity)
        {
            SourceStream = stream, SourcePath = null, AnchorPoint = Anchor.TopLeft, Stretch = StretchMode.Fill,
            Offset = new ScreenSize(rectangle.IsEmpty ? 0 : rectangle.X, rectangle.IsEmpty ? 0 : rectangle.Y),
            Size = new ScreenSize(rectangle.IsEmpty ? 2 : rectangle.Width, rectangle.IsEmpty ? 2 : rectangle.Height)
        };
    }

    internal static (Int32Rect Rectangle, Int32Rect Crop) Layout(RecordedCameraPlacement value, int width, int height)
    {
        ValidatePlacement(value);
        if (width is < 2 or > 32768 || height is < 2 or > 32768) throw new ArgumentOutOfRangeException(nameof(width));
        double contentWidth = Math.Min(width, height * value.SourceAspect);
        double contentHeight = Math.Min(height, width / value.SourceAspect);
        double contentLeft = (width - contentWidth) / 2, contentTop = (height - contentHeight) / 2;
        double left = contentLeft + value.X * contentWidth, top = contentTop + value.Y * contentHeight;
        double cameraWidth = value.Width * contentWidth, cameraHeight = value.Height * contentHeight;
        double clipLeft = Math.Max(contentLeft, left), clipTop = Math.Max(contentTop, top);
        double clipRight = Math.Min(contentLeft + contentWidth, left + cameraWidth);
        double clipBottom = Math.Min(contentTop + contentHeight, top + cameraHeight);
        if (clipRight <= clipLeft || clipBottom <= clipTop) return (Int32Rect.Empty, Int32Rect.Empty);
        int x = Math.Clamp((int)Math.Round(clipLeft), 0, width), y = Math.Clamp((int)Math.Round(clipTop), 0, height);
        int right = Math.Clamp((int)Math.Round(clipRight), x, width), bottom = Math.Clamp((int)Math.Round(clipBottom), y, height);
        if (right == x || bottom == y) return (Int32Rect.Empty, Int32Rect.Empty);
        int cropLeft = Math.Clamp((int)Math.Round((clipLeft - left) * 560 / cameraWidth), 0, 559);
        int cropTop = Math.Clamp((int)Math.Round((clipTop - top) * 380 / cameraHeight), 0, 379);
        int cropRight = Math.Clamp((int)Math.Round((clipRight - left) * 560 / cameraWidth), cropLeft + 1, 560);
        int cropBottom = Math.Clamp((int)Math.Round((clipBottom - top) * 380 / cameraHeight), cropTop + 1, 380);
        return (new(x, y, right - x, bottom - y), new(cropLeft, cropTop, cropRight - cropLeft, cropBottom - cropTop));
    }

    private static MemoryStream EncodeOpaque(BitmapSource image)
    {
        // BMP is lossless and avoids PNG compression on every camera frame. The camera is an opaque rectangle.
        var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0); opaque.Freeze();
        var encoder = new BmpBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(opaque));
        var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        return stream;
    }

    private static MemoryStream TransparentImage()
    {
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Pbgra32, null, new byte[16], 8); image.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        return stream;
    }

    private static void ValidatePlacement(RecordedCameraPlacement value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!double.IsFinite(value.Time) || value.Time < 0 || !double.IsFinite(value.X) || Math.Abs(value.X) > 1_000_000
            || !double.IsFinite(value.Y) || Math.Abs(value.Y) > 1_000_000 || !double.IsFinite(value.Width)
            || value.Width is <= 0 or > 1_000_000 || !double.IsFinite(value.Height) || value.Height is <= 0 or > 1_000_000
            || !double.IsFinite(value.SourceAspect) || value.SourceAspect is < .0001 or > 10000)
            throw new ArgumentException("摄像头录制位置无效。", nameof(value));
    }
}
