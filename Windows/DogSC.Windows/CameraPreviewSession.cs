using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenRecorderLib;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using PreviewReader = global::Windows.Media.Capture.Frames.MediaFrameReader;

namespace DogSC.Windows;

/// <summary>One unencoded camera source shared by the desktop preview and live recording overlay.</summary>
public sealed class CameraPreviewSession : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private MediaCapture? capture;
    private PreviewReader? reader;
    private int disposed, rendering;
    private long lastFrame;
    private BitmapSource? latestFrame;
    public BitmapSource? LatestFrame => Volatile.Read(ref latestFrame);
    public event Action<BitmapSource>? FrameReady;
    public event Action<BitmapSource>? CaptureFrameReady;
    public event Action<Exception>? Failed;

    public async Task StartAsync(string deviceId, VideoCaptureFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 4096)
            throw new ArgumentException("请选择有效的摄像头。", nameof(deviceId));
        if (format.FrameSize is null || format.FrameSize.Width is < 2 or > 8192
            || format.FrameSize.Height is < 2 or > 8192 || !double.IsFinite(format.Framerate)
            || format.Framerate is < 1 or > 120)
            throw new ArgumentException("摄像头预览格式无效。", nameof(format));
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (capture is not null) throw new InvalidOperationException("摄像头预览已启动。");
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            var group = groups.FirstOrDefault(candidate => candidate.SourceInfos.Any(source =>
                source.SourceKind == MediaFrameSourceKind.Color
                && string.Equals(source.DeviceInformation?.Id, deviceId, StringComparison.OrdinalIgnoreCase)))
                ?? throw new InvalidOperationException("所选摄像头没有可用的实时预览源。");
            capture = new MediaCapture();
            capture.Failed += OnCaptureFailed;
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group, SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu, StreamingCaptureMode = StreamingCaptureMode.Video
            });
            var source = capture.FrameSources.Values.FirstOrDefault(candidate =>
                candidate.Info.SourceKind == MediaFrameSourceKind.Color
                && string.Equals(candidate.Info.DeviceInformation?.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("所选摄像头没有可用的彩色画面。");
            var selected = source.SupportedFormats.FirstOrDefault(candidate =>
                candidate.VideoFormat.Width == format.FrameSize.Width && candidate.VideoFormat.Height == format.FrameSize.Height
                && candidate.FrameRate.Denominator != 0
                && Math.Abs((double)candidate.FrameRate.Numerator / candidate.FrameRate.Denominator - format.Framerate) < .001)
                ?? throw new InvalidOperationException("摄像头预览不支持所选原生分辨率和帧率。");
            await source.SetFormatAsync(selected);
            var scale = Math.Min(1, Math.Min(560.0 / selected.VideoFormat.Width, 380.0 / selected.VideoFormat.Height));
            reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Argb32, new BitmapSize
            {
                Width = (uint)Math.Max(2, Math.Round(selected.VideoFormat.Width * scale)),
                Height = (uint)Math.Max(2, Math.Round(selected.VideoFormat.Height * scale))
            });
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;
            if (await reader.StartAsync() != MediaFrameReaderStartStatus.Success)
                throw new InvalidOperationException("摄像头预览启动失败，请检查摄像头占用和Windows权限。");
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        }
        catch
        {
            await ReleaseAsync();
            throw;
        }
        finally { gate.Release(); }
    }

    private void OnFrameArrived(PreviewReader sender, MediaFrameArrivedEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.CompareExchange(ref rendering, 1, 0) != 0) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            bool showPreview = now - lastFrame >= Stopwatch.Frequency / 30;
            if (!showPreview && CaptureFrameReady is null) return;
            using var frame = sender.TryAcquireLatestFrame();
            using var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) return;
            using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            var length = checked(converted.PixelWidth * converted.PixelHeight * 4);
            if (length > 560 * 380 * 4) throw new InvalidDataException("摄像头预览返回了过大的画面。");
            var pixels = new byte[length];
            var buffer = new global::Windows.Storage.Streams.Buffer((uint)length);
            converted.CopyToBuffer(buffer);
            using var data = DataReader.FromBuffer(buffer);
            data.ReadBytes(pixels);
            var image = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
                PixelFormats.Bgr32, null, pixels, checked(converted.PixelWidth * 4));
            image.Freeze();
            Volatile.Write(ref latestFrame, image);
            if (Volatile.Read(ref disposed) == 0)
            {
                CaptureFrameReady?.Invoke(image);
                if (showPreview) { lastFrame = now; FrameReady?.Invoke(image); }
            }
        }
        catch (Exception error) { ReportFailure(error); }
        finally { Volatile.Write(ref rendering, 0); }
    }

    public static BitmapSource CreatePreviewFrame(nint data, int width, int height, int stride)
    {
        if (data == 0 || width is < 2 or > 8192 || height is < 2 or > 8192
            || stride < width * 4 || stride > 131072)
            throw new InvalidDataException("摄像头预览画面无效。");
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null,
            data, checked(stride * height), stride);
        image.Freeze();
        return image;
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args) =>
        ReportFailure(new InvalidOperationException($"摄像头预览失败：{args.Message}"));

    private void ReportFailure(Exception error)
    {
        try { if (Volatile.Read(ref disposed) == 0) Failed?.Invoke(error); }
        catch (Exception subscriberError) { Trace.TraceError($"Camera preview subscriber failed: {subscriberError}"); }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        await gate.WaitAsync();
        try { await ReleaseAsync(); }
        finally { gate.Release(); }
    }

    private async Task ReleaseAsync()
    {
        Volatile.Write(ref latestFrame, null);
        var activeReader = reader; reader = null;
        try
        {
            if (activeReader is not null)
            {
                activeReader.FrameArrived -= OnFrameArrived;
                try { await activeReader.StopAsync(); }
                finally { activeReader.Dispose(); }
            }
        }
        finally
        {
            if (capture is { } activeCapture)
            {
                capture = null;
                activeCapture.Failed -= OnCaptureFailed;
                activeCapture.Dispose();
            }
        }
    }
}
