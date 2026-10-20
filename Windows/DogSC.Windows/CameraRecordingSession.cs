using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using DogSC.Core;
using ScreenRecorderLib;

namespace DogSC.Windows;

/// <summary>Legacy/diagnostic independent camera capture; new recordings use RealtimeCameraOverlay.</summary>
public sealed class CameraRecordingSession : IDisposable
{
    private readonly SemaphoreSlim nativeOperation = new(1, 1);
    private readonly object gate = new();
    private readonly Stopwatch clock = new();
    private readonly TaskCompletionSource<TimeSpan> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource releaseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action? releaseOverride;
    private Recorder? recorder;
    private FileStream? output;
    private RecordingState state = RecordingState.Starting;
    private bool started, terminal, stopRequested, desiredPaused, transitionPending, disposed;
    private bool finalizationDone, releaseQueued;
    private long lastPreview;
    private VideoCaptureRecordingSource? previewSource;

    public event Action<RecordingState>? StateChanged;
    public event Action<BitmapSource>? FrameReady;
    public event Action<Exception>? PreviewFailed;
    public Task<TimeSpan> Completion => completion.Task;
    // Completion failure does not prove that a stuck native encoder released its handles.
    public Task ReleaseCompletion => releaseCompletion.Task;
    public RecordingState State { get { lock (gate) return state; } }
    public TimeSpan Duration { get { lock (gate) return clock.Elapsed; } }
    public long? StartedTimestamp { get; private set; }
    public VideoCaptureFormat? CaptureFormat { get; private set; }

    public CameraRecordingSession() { }
    internal CameraRecordingSession(Action releaseOverride) => this.releaseOverride = releaseOverride;

    public void Start(string deviceId, string outputPath, int frameRate = 30, int quality = 90,
        string codecId = "H264", VideoCaptureFormat? captureFormat = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("每个摄像头会话只能开始一次。");
        }
        if (frameRate is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(frameRate));
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("请选择摄像头素材路径。", nameof(outputPath));
        var fullPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("摄像头素材必须使用.mp4扩展名。", nameof(outputPath));
        fullPath = LegacyProjectCodec.ResolvePackagePath(Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath));
        var encoder = CaptureCapabilities.CreateEncoder(codecId);
        var capability = CaptureCapabilities.GetCodecs().Single(codec => codec.Id == codecId);
        if (!capability.IsAvailable) throw new NotSupportedException(capability.Detail);
        if (!capability.CanDecode) throw new NotSupportedException($"系统未检测到{capability.Name}播放解码器，不能预览此格式；请选择可用编码，不会自动替换。");
        CaptureCapabilities.RequireCamera(deviceId);
        var formats = CaptureCapabilities.GetCameraFormats(deviceId);
        // Prefer full native detail while preserving the requested camera cadence; never invent a device format.
        var format = captureFormat is null
            ? formats.Where(format => format.Framerate >= frameRate && ValidSize(format))
                .OrderByDescending(format => format.FrameSize.Width * format.FrameSize.Height).FirstOrDefault()
            : formats.FirstOrDefault(format => format.Index == captureFormat.Index
                && format.VideoFormat == captureFormat.VideoFormat && format.Framerate == captureFormat.Framerate
                && format.FrameSize.Width == captureFormat.FrameSize.Width && format.FrameSize.Height == captureFormat.FrameSize.Height);
        if (format is null || !ValidSize(format) || !SupportsFrameRate(format, frameRate, captureFormat is not null))
            throw new InvalidOperationException("所选摄像头没有可满足此帧率的格式，请选择摄像头实际支持的分辨率与帧率。");
        var previewScale = Math.Min(1, Math.Min(560.0 / format.FrameSize.Width, 380.0 / format.FrameSize.Height));
        previewSource = new VideoCaptureRecordingSource(deviceId, format)
        {
            IsVideoFramePreviewEnabled = true,
            VideoFramePreviewSize = new ScreenSize(Math.Max(2, (int)Math.Round(format.FrameSize.Width * previewScale)),
                Math.Max(2, (int)Math.Round(format.FrameSize.Height * previewScale)))
        };
        previewSource.OnFrameRecorded += OnPreviewFrame;
        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = new List<RecordingSourceBase> { previewSource }
            },
            OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = encoder, Framerate = frameRate, Quality = quality, IsFixedFramerate = true,
                IsHardwareEncodingEnabled = true, IsLowLatencyEnabled = true
            },
            AudioOptions = new AudioOptions { IsAudioEnabled = false, IsInputDeviceEnabled = false, IsOutputDeviceEnabled = false },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = false, IsMouseClicksDetected = false }
        };
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("每个摄像头会话只能开始一次。");
            output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            try { recorder = Recorder.CreateRecorder(options); }
            catch { output.Dispose(); output = null; throw; }
            recorder.OnStatusChanged += OnStatusChanged;
            recorder.OnRecordingComplete += OnRecordingComplete;
            recorder.OnRecordingFailed += OnRecordingFailed;
            CaptureFormat = format;
            started = true;
        }
        NotifyStateChanged();
        nativeOperation.Wait();
        try { recorder.Record(output); }
        catch (Exception error) { Finish(recorder, error); throw; }
        finally { nativeOperation.Release(); }
    }

    private void OnPreviewFrame(object? sender, FrameDataRecordedEventArgs args)
    {
        if (FrameReady is null || Stopwatch.GetTimestamp() - lastPreview < Stopwatch.Frequency / 30) return;
        try
        {
            var data = args.BitmapData;
            if (data is null) return;
            var frame = CameraPreviewSession.CreatePreviewFrame(data.Data, data.Width, data.Height, data.Stride);
            lastPreview = Stopwatch.GetTimestamp();
            FrameReady?.Invoke(frame);
        }
        catch (Exception error) { PreviewFailed?.Invoke(error); }
    }

    public void SetPaused(bool paused)
    {
        lock (gate)
        {
            if (terminal || stopRequested || disposed) return;
            desiredPaused = paused;
        }
        ApplyRequestedState();
    }

    public void Stop()
    {
        lock (gate)
        {
            if (!started || terminal || stopRequested) return;
            stopRequested = true;
            clock.Stop();
        }
        ApplyRequestedState();
    }

    private void ApplyRequestedState()
    {
        Recorder? active;
        bool stop, pause;
        lock (gate)
        {
            if (!started || terminal || transitionPending || state is RecordingState.Starting or RecordingState.Finishing) return;
            stop = stopRequested;
            pause = desiredPaused;
            if (!stop && (state == RecordingState.Paused) == pause) return;
            active = recorder;
            transitionPending = true;
            if (stop) state = RecordingState.Finishing;
        }
        if (stop) NotifyStateChanged();
        if (stop)
        {
            _ = Task.Run(async () =>
            {
                await nativeOperation.WaitAsync();
                try
                {
                    lock (gate) { if (terminal || disposed) return; }
                    active!.Stop();
                }
                catch (Exception error) { Finish(active, error); }
                finally { nativeOperation.Release(); }
            });
            return;
        }
        try { if (pause) active!.Pause(); else active!.Resume(); }
        catch (Exception error) { Finish(active, error); }
    }

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs args)
    {
        lock (gate)
        {
            if (terminal || !ReferenceEquals(sender, recorder)) return;
            transitionPending = false;
            switch (args.Status)
            {
                case RecorderStatus.Recording:
                    StartedTimestamp ??= Stopwatch.GetTimestamp();
                    clock.Start(); state = RecordingState.Recording; break;
                case RecorderStatus.Paused:
                    clock.Stop(); state = RecordingState.Paused; break;
                case RecorderStatus.Finishing:
                    clock.Stop(); state = RecordingState.Finishing; stopRequested = true; break;
                default: return;
            }
        }
        NotifyStateChanged();
        ApplyRequestedState();
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs args) => Finish(sender, null);
    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs args) =>
        Finish(sender, new InvalidOperationException($"摄像头录制失败：{args.Error}。现有素材已保留，未替换编码格式。"));

    private void Finish(object? sender, Exception? error)
    {
        FileStream? file;
        lock (gate)
        {
            if (terminal || !ReferenceEquals(sender, recorder)) return;
            clock.Stop();
            terminal = true;
            state = RecordingState.Finishing;
            file = output;
        }
        NotifyStateChanged();
        _ = Task.Run(() =>
        {
            if (error is null)
            {
                try
                {
                    file!.Flush(flushToDisk: true);
                    if (file.Length == 0) error = new IOException("摄像头没有生成视频素材。");
                }
                catch (Exception flushError) { error = flushError; }
            }
            lock (gate)
            {
                finalizationDone = true;
                if (error is not null)
                {
                    state = RecordingState.Failed;
                    completion.TrySetException(error);
                }
            }
            NotifyStateChanged();
            BeginRelease();
        });
    }

    private void NotifyStateChanged()
    {
        try { StateChanged?.Invoke(State); }
        catch (Exception error) { Trace.TraceError($"Camera state subscriber failed: {error}"); }
    }

    public void Dispose()
    {
        bool release;
        lock (gate)
        {
            if (!started) disposed = true;
            release = !started || terminal && finalizationDone;
        }
        if (release) BeginRelease();
        else Stop();
    }

    private void BeginRelease()
    {
        lock (gate)
        {
            if (releaseQueued) return;
            releaseQueued = true;
        }
        _ = Task.Run(ReleaseAsync);
    }

    private async Task ReleaseAsync()
    {
        await nativeOperation.WaitAsync().ConfigureAwait(false);
        Exception? error = null;
        Recorder? native;
        FileStream? file;
        lock (gate)
        {
            disposed = true;
            native = recorder;
            file = output;
            recorder = null;
            output = null;
        }
        try
        {
            if (previewSource is not null) { previewSource.OnFrameRecorded -= OnPreviewFrame; previewSource = null; }
            if (releaseOverride is not null) releaseOverride();
            else if (native is not null)
            {
                native.OnStatusChanged -= OnStatusChanged;
                native.OnRecordingComplete -= OnRecordingComplete;
                native.OnRecordingFailed -= OnRecordingFailed;
                native.Dispose();
            }
        }
        catch (Exception failure) { error = failure; }
        finally
        {
            try { file?.Dispose(); } catch (Exception failure) { error ??= failure; }
            nativeOperation.Release();
        }
        lock (gate)
        {
            if (error is null)
            {
                releaseCompletion.TrySetResult();
                if (started && !completion.Task.IsCompleted)
                {
                    state = RecordingState.Complete;
                    completion.TrySetResult(clock.Elapsed);
                }
            }
            else
            {
                state = RecordingState.Failed;
                releaseCompletion.TrySetException(error);
                completion.TrySetException(error);
            }
        }
        NotifyStateChanged();
    }

    private static bool ValidSize(VideoCaptureFormat format) => format.FrameSize is not null
        && format.FrameSize.Width is >= 2 and <= 8192 && format.FrameSize.Height is >= 2 and <= 8192;

    private static bool SupportsFrameRate(VideoCaptureFormat format, int frameRate, bool explicitFormat) =>
        double.IsFinite(format.Framerate) && (explicitFormat
            // The camera keeps its native fractional cadence; the library encoder accepts only an integer rate.
            ? format.Framerate is >= 1 and <= 120 && frameRate == CaptureCapabilities.GetCameraEncoderFrameRate(format)
            : format.Framerate >= frameRate);

}
