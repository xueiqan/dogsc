using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using DogSC.Core;
using ScreenRecorderLib;

namespace DogSC.Windows;

public enum RecordingState { Starting, Recording, Paused, Finishing, Complete, Failed }

public sealed class RecordingSession : IDisposable
{
    private readonly object gate = new();
    private readonly Stopwatch clock = new();
    private readonly TaskCompletionSource<TimeSpan> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource releaseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource initializationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim nativeOperation = new(1, 1);
    private readonly CancellationTokenSource finishingCancellation = new();
    private readonly TimeSpan finishingTimeout;
    private readonly Action? stopOverride, releaseOverride;
    private Recorder? recorder;
    private FileStream? output;
    private FileStream? diagnosticLog;
    private RecordingState state = RecordingState.Starting;
    private bool started, terminal, disposed, stopRequested, transitionPending, disposeRequested;
    private bool windowWarming, windowResumeIssued, nativeRecordBegun, initializationRunning;
    private WindowRecordingSource? warmupSource;
    private readonly CancellationTokenSource startupCancellation = new();
    private readonly TaskCompletionSource sourceFrameReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? startupError;
    private bool finishingWatchStarted, nativeStopQueued, releaseQueued;
    private RealtimeCameraOverlay? cameraOverlay;
    private long cameraOverlayVersion;
    private bool audioTrackEnabled;

    public RecordingSession() : this(TimeSpan.FromSeconds(60)) { }

    internal RecordingSession(TimeSpan finishingTimeout, Action? stopOverride = null, Action? releaseOverride = null)
    {
        if (finishingTimeout <= TimeSpan.Zero || finishingTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(finishingTimeout));
        this.finishingTimeout = finishingTimeout;
        this.stopOverride = stopOverride;
        this.releaseOverride = releaseOverride;
    }

    // Native callbacks may run on a worker thread. UI subscribers must queue work through their Dispatcher.
    public event Action<RecordingState>? StateChanged;
    public Task<TimeSpan> Completion => completion.Task;
    // A failed Completion does not guarantee that a blocked native encoder released its handles.
    public Task ReleaseCompletion => releaseCompletion.Task;
    public RecordingState State { get { lock (gate) return state; } }
    public TimeSpan Duration { get { lock (gate) return clock.Elapsed; } }
    public bool TryGetRecordingTime(out double seconds)
    {
        seconds = 0;
        // A system-wide input hook must not wait for native recorder initialization.
        if (!Monitor.TryEnter(gate)) return false;
        try
        {
            if (disposed || terminal || state != RecordingState.Recording) return false;
            seconds = clock.Elapsed.TotalSeconds;
            return true;
        }
        finally { Monitor.Exit(gate); }
    }
    public long? StartedTimestamp { get; private set; }

    public void Start(CaptureTarget target, CaptureRegion? region, int frameRate,
        bool systemAudio, bool microphone, string outputPath, string? diagnosticLogPath = null, int quality = 90,
        string codecId = "H264", int recordingHeight = 0, string? outputDevice = null, string? inputDevice = null,
        bool embeddedCursor = true, bool recoverableOutput = false, RealtimeCameraOverlay? cameraOverlay = null,
        bool liveAudioControls = false, float systemVolume = 1, float microphoneVolume = 1)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("每个录制会话只能开始一次。");
        }
        if (frameRate is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(frameRate), "帧率必须在1到120之间。");
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality), "画质必须在1到100之间。");
        ValidateAudioVolume(systemVolume, nameof(systemVolume));
        ValidateAudioVolume(microphoneVolume, nameof(microphoneVolume));
        if (recordingHeight is not (0 or 720 or 1080 or 1440)) throw new ArgumentOutOfRangeException(nameof(recordingHeight));
        var encoder = CaptureCapabilities.CreateEncoder(codecId);
        if (codecId != "H264")
        {
            var capability = CaptureCapabilities.GetCodecs().Single(c => c.Id == codecId);
            if (!capability.IsAvailable || !capability.CanDecode)
                throw new NotSupportedException("本机没有所选格式的可用编码器或回放解码器，不会自动替换编码格式。");
        }
        if (systemAudio && outputDevice is not null && !CaptureCapabilities.GetAudioOutputs().Any(d => d.Id == outputDevice)
            || microphone && inputDevice is not null && !CaptureCapabilities.GetMicrophones().Any(d => d.Id == inputDevice))
            throw new InvalidOperationException("所选音频设备已断开，请重新选择。");
        var current = target.IsWindow
            ? (CaptureCatalog.IsAvailableWindow(target.WindowHandle) ? target : null)
            : CaptureCatalog.GetDisplays().FirstOrDefault(x => x.DeviceName == target.DeviceName);
        if (current == null) throw new InvalidOperationException("录制目标已关闭、最小化或断开，请重新选择。");
        if (current.Width < 2 || current.Height < 2) throw new ArgumentException("录制目标尺寸无效。", nameof(target));
        if (!target.IsWindow && (current.Left != target.Left || current.Top != target.Top
            || current.Width != target.Width || current.Height != target.Height))
            throw new InvalidOperationException("显示器分辨率或位置已改变，请刷新后重新选择。");
        if (target.IsWindow && region != null) throw new ArgumentException("区域录制必须选择显示器。");
        region?.Validate(current.Width, current.Height);
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("请选择输出文件。", nameof(outputPath));
        outputPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(outputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("录制文件必须使用.mp4扩展名。", nameof(outputPath));
        if (diagnosticLogPath != null)
        {
            diagnosticLogPath = Path.GetFullPath(diagnosticLogPath);
            if (!string.Equals(Path.GetDirectoryName(diagnosticLogPath), Path.GetDirectoryName(outputPath),
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(diagnosticLogPath), ".log", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("诊断日志必须是录制文件同目录中的.log文件。", nameof(diagnosticLogPath));
        }

        if (systemAudio && CaptureCapabilities.GetAudioOutputs().Count == 0)
            throw new InvalidOperationException("没有可用的默认系统声音输出设备。");
        if (microphone && CaptureCapabilities.GetMicrophones().Count == 0)
            throw new InvalidOperationException("没有可用的默认麦克风，请检查设备与Windows麦克风权限。");
        RecordingSourceBase videoSource = target.IsWindow
            ? new WindowRecordingSource(target.WindowHandle) { IsCursorCaptureEnabled = embeddedCursor }
            : new DisplayRecordingSource(target.DeviceName)
            {
                RecorderApi = RecorderApi.WindowsGraphicsCapture,
                IsCursorCaptureEnabled = embeddedCursor
            };
        if (videoSource is WindowRecordingSource windowSource)
        {
            // A source preview is emitted only after WGC successfully copies a source frame.
            // It is needed only for startup and is disabled before ordinary recording continues.
            windowSource.IsVideoFramePreviewEnabled = true;
            windowSource.VideoFramePreviewSize = new ScreenSize(12, 8);
            warmupSource = windowSource;
            windowSource.OnFrameRecorded += OnSourceFrameReady;
        }
        if (region is { } crop) videoSource.SourceRect = new ScreenRect(crop.X, crop.Y, crop.Width, crop.Height);
        ScreenSize? outputSize = recordingHeight == 0 ? null : new ScreenSize(recordingHeight * 16 / 9, recordingHeight);
        ImageOverlay? liveCamera = null;
        if (cameraOverlay is not null)
        {
            int width = recordingHeight == 0 ? ((region?.Width ?? current.Width) + 1) & ~1 : recordingHeight * 16 / 9;
            int height = recordingHeight == 0 ? ((region?.Height ?? current.Height) + 1) & ~1 : recordingHeight;
            liveCamera = cameraOverlay.Bind(width, height, frameRate, ApplyCameraOverlayAsync);
        }
        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = new List<RecordingSourceBase> { videoSource } },
            OverlayOptions = new OverLayOptions { Overlays = liveCamera is null ? [] : [liveCamera] },
            OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video, OutputFrameSize = outputSize, Stretch = StretchMode.Uniform },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = encoder, Framerate = frameRate, IsFixedFramerate = true,
                IsHardwareEncodingEnabled = true, IsLowLatencyEnabled = true, Quality = quality,
                IsFragmentedMp4Enabled = recoverableOutput
            },
            AudioOptions = new AudioOptions
            {
                // The AAC stream must exist from the start to allow unmuting later without restarting the video.
                IsAudioEnabled = liveAudioControls || systemAudio || microphone,
                IsOutputDeviceEnabled = systemAudio, IsInputDeviceEnabled = microphone,
                OutputVolume = systemVolume, InputVolume = microphoneVolume,
                // Null device IDs select the current system defaults in ScreenRecorderLib 6.6.
                AudioOutputDevice = outputDevice, AudioInputDevice = inputDevice,
                Bitrate = AudioBitrate.bitrate_192kbps, Channels = AudioChannels.Stereo
            },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = embeddedCursor, IsMouseClicksDetected = false },
            LogOptions = new LogOptions
            {
                IsLogEnabled = diagnosticLogPath != null,
                LogFilePath = diagnosticLogPath,
                LogSeverityLevel = LogLevel.Info
            }
        };

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("每个录制会话只能开始一次。");
            // CreateNew prevents a save-path race from overwriting somebody else's recording.
            output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            try
            {
                if (diagnosticLogPath != null)
                    // Keep this new log reserved while native logging appends to it.
                    diagnosticLog = new FileStream(diagnosticLogPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.ReadWrite);
                if (!target.IsWindow) recorder = Recorder.CreateRecorder(options);
            }
            catch
            {
                diagnosticLog?.Dispose(); diagnosticLog = null;
                output.Dispose(); output = null;
                throw;
            }
            if (recorder is not null) AttachCallbacks(recorder);
            this.cameraOverlay = cameraOverlay;
            audioTrackEnabled = liveAudioControls || systemAudio || microphone;
            if (cameraOverlay is not null) cameraOverlay.Failed += OnCameraOverlayFailed;
            started = true;
            windowWarming = target.IsWindow;
            initializationRunning = target.IsWindow;
            NotifyStateChanged();
        }
        if (target.IsWindow)
        {
            _ = Task.Run(() => StartWindowAsync(options, target.WindowHandle));
            return;
        }
        nativeOperation.Wait();
        try { recorder!.Record(output); }
        catch (Exception error)
        {
            Finish(recorder, error);
            throw;
        }
        finally { nativeOperation.Release(); }
    }

    public Task<bool> UpdateAudioAsync(bool systemAudio, bool microphone, float systemVolume,
        float microphoneVolume, CancellationToken cancellation = default)
    {
        ValidateAudioVolume(systemVolume, nameof(systemVolume));
        ValidateAudioVolume(microphoneVolume, nameof(microphoneVolume));
        return Task.Run(async () =>
        {
            await nativeOperation.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                Recorder? active;
                lock (gate)
                {
                    if (disposed || terminal || stopRequested
                        || state is not (RecordingState.Recording or RecordingState.Paused)) return false;
                    if (!audioTrackEnabled)
                        throw new InvalidOperationException("本场录制没有建立音轨，不能中途开启声音；请在开始录制时启用实时声音控制。");
                    active = recorder;
                }
                cancellation.ThrowIfCancellationRequested();
                return active is not null && active.GetDynamicOptionsBuilder().SetDynamicAudioOptions(new DynamicAudioOptions
                {
                    IsOutputDeviceEnabled = systemAudio, IsInputDeviceEnabled = microphone,
                    OutputVolume = systemVolume, InputVolume = microphoneVolume
                }).Apply();
            }
            finally { nativeOperation.Release(); }
        }, cancellation);
    }

    internal static void ValidateAudioVolume(float volume, string name)
    {
        if (!float.IsFinite(volume) || volume is < 0 or > 1)
            throw new ArgumentOutOfRangeException(name, "录制音量必须在0到1之间。");
    }

    private async Task<bool> ApplyCameraOverlayAsync(ImageOverlay image, CancellationToken cancellation)
    {
        await nativeOperation.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            Recorder? active;
            lock (gate)
            {
                if (disposed || terminal || stopRequested || transitionPending || state != RecordingState.Recording) return false;
                active = recorder;
            }
            cancellation.ThrowIfCancellationRequested();
            if (active is null) return false;
            // SRL 6.6 compares both SourcePath and the native IStream address. SafeRelease/new can
            // reuse that address, so change the logical path too. The path-only update retains the
            // existing stream, and both entries are applied by one builder without disk access.
            var version = new ImageOverlay(image)
            {
                SourceStream = null, SourcePath = "dogsc-live-camera:" + Interlocked.Increment(ref cameraOverlayVersion)
            };
            if (!active.GetDynamicOptionsBuilder().SetUpdatedOverlay(version).SetUpdatedOverlay(image).Apply())
            {
                lock (gate)
                    if (terminal || stopRequested || transitionPending || state != RecordingState.Recording) return false;
                throw new IOException("摄像头实时叠加更新失败，已停止录制并保留现有素材。");
            }
            return true;
        }
        finally { nativeOperation.Release(); }
    }

    private void OnCameraOverlayFailed(Exception error)
    {
        lock (gate)
        {
            if (terminal || stopRequested) return;
            startupError ??= new IOException("摄像头实时叠加失败，已停止录制并保留现有素材。", error);
        }
        Stop();
    }

    private void AttachCallbacks(Recorder active)
    {
        active.OnStatusChanged += OnStatusChanged;
        active.OnRecordingComplete += OnRecordingComplete;
        active.OnRecordingFailed += OnRecordingFailed;
        if (warmupSource is not null) active.OnFrameRecorded += OnWindowFrameRecorded;
    }

    private void OnSourceFrameReady(object? sender, FrameDataRecordedEventArgs args)
    {
        var bitmap = args.BitmapData;
        if (bitmap is null || bitmap.Data == 0 || bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Stride <= 0)
        {
            sourceFrameReady.TrySetException(new IOException("窗口采集未返回有效源帧；已保留现有素材。"));
            return;
        }
        sourceFrameReady.TrySetResult();
    }

    private void OnWindowFrameRecorded(object? sender, FrameRecordedEventArgs args)
    {
        lock (gate)
        {
            if (terminal || stopRequested || !ReferenceEquals(sender, recorder) || !windowWarming) return;
            if (!windowResumeIssued)
            {
                startupError ??= new IOException("窗口预热完成前已出现编码帧，启动未通过校验；已保留现有素材。请重试。");
                sourceFrameReady.TrySetException(startupError);
                return;
            }
            // Resume's status callback precedes the native encoder's first sample.
            // Auxiliary audio/camera anchors must use this actual output boundary instead.
            windowWarming = false;
            StartedTimestamp ??= Stopwatch.GetTimestamp();
            clock.Start();
            ChangeState(RecordingState.Recording);
        }
    }

    private async Task StartWindowAsync(RecorderOptions options, nint window)
    {
        Recorder? active = null;
        await nativeOperation.WaitAsync().ConfigureAwait(false);
        try
        {
            int initialized = RoInitialize(1);
            try
            {
                Marshal.ThrowExceptionForHR(initialized);
                startupCancellation.Token.ThrowIfCancellationRequested();
                active = Recorder.CreateRecorder(options);
                lock (gate) { recorder = active; AttachCallbacks(active); }
                startupCancellation.Token.ThrowIfCancellationRequested();
                active.Record(output!);
                bool stopping;
                lock (gate) { nativeRecordBegun = true; stopping = stopRequested; }
                if (stopping) { StopNative(active); return; }
                active.Pause();
            }
            finally
            {
                // Balance initialization on this same thread, before any async continuation.
                if (initialized >= 0) RoUninitialize();
            }

            var wait = Stopwatch.StartNew();
            while (!sourceFrameReady.Task.IsCompleted)
            {
                startupCancellation.Token.ThrowIfCancellationRequested();
                if (!CaptureCatalog.IsAvailableWindow(window))
                    throw new IOException("录制窗口在启动期间已关闭、隐藏或最小化；已保留现有素材。" );
                if (wait.Elapsed >= TimeSpan.FromSeconds(4))
                    throw new TimeoutException("窗口未在4秒内返回首个有效采集帧，未开始录制；已保留现有素材。" );
                if (!RedrawWindow(window, 0, 0, 0x81))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法请求目标窗口重绘，未开始录制；已保留现有素材。" );
                await Task.WhenAny(sourceFrameReady.Task, Task.Delay(30, startupCancellation.Token)).ConfigureAwait(false);
            }
            await sourceFrameReady.Task.ConfigureAwait(false);
            startupCancellation.Token.ThrowIfCancellationRequested();
            if (!CaptureCatalog.IsAvailableWindow(window))
                throw new IOException("录制窗口在预热完成前已关闭、隐藏或最小化；已保留现有素材。" );
            lock (gate)
            {
                if (startupError is not null) throw startupError;
                if (terminal || stopRequested) return;
                windowResumeIssued = true;
            }
            active.Resume();
            startupCancellation.Token.ThrowIfCancellationRequested();
            warmupSource!.IsVideoFramePreviewEnabled = false;
            // SRL 6.6 rejects dynamic updates while Paused; check the actual result after Resume.
            if (!active.GetDynamicOptionsBuilder().SetUpdatedRecordingSource(warmupSource).Apply())
                throw new IOException("窗口预览未能关闭，启动已停止；已保留现有素材。" );
        }
        catch (Exception error)
        {
            bool nativeStarted;
            lock (gate)
            {
                startupError ??= error is OperationCanceledException
                    ? new OperationCanceledException("已取消窗口录制启动；已保留现有素材。", error) : error;
                nativeStarted = nativeRecordBegun;
                clock.Stop();
                stopRequested = true;
                if (!terminal) ChangeState(RecordingState.Finishing);
            }
            if (nativeStarted && active is not null) StopNative(active);
            else Finish(active, startupError);
        }
        finally
        {
            bool release;
            lock (gate) { initializationRunning = false; release = terminal && disposeRequested; }
            initializationCompletion.TrySetResult();
            nativeOperation.Release();
            if (release) BeginRelease();
        }
    }

    public void PauseOrResume()
    {
        Recorder active;
        bool resume;
        lock (gate)
        {
            if (!started || terminal || stopRequested || transitionPending
                || state is not (RecordingState.Recording or RecordingState.Paused)) return;
            active = recorder!;
            resume = state == RecordingState.Paused;
            transitionPending = true;
        }
        _ = Task.Run(async () =>
        {
            await nativeOperation.WaitAsync().ConfigureAwait(false);
            try
            {
                bool stopping;
                lock (gate)
                {
                    if (terminal || !ReferenceEquals(active, recorder)) return;
                    stopping = stopRequested;
                    if (stopping) transitionPending = false;
                }
                if (stopping) { StopNative(active); return; }
                if (resume) active.Resume(); else active.Pause();
            }
            catch (Exception error)
            {
                lock (gate) transitionPending = false;
                Finish(active, error);
            }
            finally { nativeOperation.Release(); }
        });
    }

    public void Stop()
    {
        Recorder? active;
        lock (gate)
        {
            if (!started || terminal || stopRequested) return;
            stopRequested = true;
            clock.Stop();
            active = windowWarming ? (nativeRecordBegun ? recorder : null)
                : state == RecordingState.Starting || transitionPending ? null : recorder;
            ChangeState(RecordingState.Finishing);
        }
        cameraOverlay?.Stop();
        startupCancellation.Cancel();
        // An early stop is deferred until the native recorder acknowledges that capture has started.
        if (active != null || stopOverride is not null) StopNative(active);
    }

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs args)
    {
        Recorder? pendingStop = null;
        lock (gate)
        {
            if (terminal || !ReferenceEquals(sender, recorder)) return;
            if (windowWarming && args.Status is RecorderStatus.Recording or RecorderStatus.Paused)
            {
                if (stopRequested && nativeRecordBegun) pendingStop = recorder;
            }
            else
            switch (args.Status)
            {
                case RecorderStatus.Recording:
                    transitionPending = false;
                    if (stopRequested) pendingStop = recorder;
                    else { StartedTimestamp ??= Stopwatch.GetTimestamp(); clock.Start(); ChangeState(RecordingState.Recording); }
                    break;
                case RecorderStatus.Paused:
                    transitionPending = false;
                    clock.Stop();
                    if (stopRequested) pendingStop = recorder;
                    else ChangeState(RecordingState.Paused);
                    break;
                case RecorderStatus.Finishing:
                    stopRequested = true;
                    clock.Stop();
                    ChangeState(RecordingState.Finishing);
                    break;
            }
        }
        if (State == RecordingState.Recording) cameraOverlay?.Refresh();
        else if (State == RecordingState.Finishing) cameraOverlay?.Stop();
        if (pendingStop != null) StopNative(pendingStop);
    }

    private void StopNative(Recorder? active)
    {
        lock (gate)
        {
            if (nativeStopQueued || terminal) return;
            nativeStopQueued = true;
        }
        _ = Task.Run(async () =>
        {
            await nativeOperation.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (gate) { if (terminal) return; }
                if (stopOverride is not null) stopOverride();
                else active!.Stop();
            }
            catch (Exception error) { Finish(active, error); }
            finally { nativeOperation.Release(); }
        });
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs args) => Finish(sender, null);
    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs args) =>
        Finish(sender, new InvalidOperationException($"录制失败：{args.Error}。已保留现有素材。"));

    private void Finish(object? sender, Exception? error)
    {
        lock (gate)
        {
            if (terminal || !ReferenceEquals(sender, recorder)) return;
            clock.Stop();
            error ??= startupError;
            terminal = true;
            disposeRequested = true;
            if (error == null) ChangeState(RecordingState.Finishing);
            else
            {
                ChangeState(RecordingState.Failed);
                completion.TrySetException(error);
                finishingCancellation.Cancel();
            }
        }
        cameraOverlay?.Stop();
        startupCancellation.Cancel();
        BeginRelease();
    }

    private void ChangeState(RecordingState next)
    {
        if (state == next) return;
        state = next;
        if (next == RecordingState.Finishing && !finishingWatchStarted)
        {
            finishingWatchStarted = true;
            _ = WatchFinishingAsync();
        }
        NotifyStateChanged();
    }

    private async Task WatchFinishingAsync()
    {
        try { await Task.Delay(finishingTimeout, finishingCancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        lock (gate)
        {
            if (completion.Task.IsCompleted) return;
            terminal = true;
            disposeRequested = true;
            clock.Stop();
            ChangeState(RecordingState.Failed);
            completion.TrySetException(new TimeoutException("录制编码器收尾超时，已保留现有素材。正在尝试释放资源；未确认释放前不能开始新录制或移动素材。"));
        }
        cameraOverlay?.Stop();
        startupCancellation.Cancel();
        BeginRelease();
    }

    private void NotifyStateChanged()
    {
        // A UI subscriber error must not strand native finalization or suppress Completion.
        try { StateChanged?.Invoke(state); }
        catch (Exception error) { Trace.TraceError($"Recording state subscriber failed: {error}"); }
    }

    public void Dispose()
    {
        bool release;
        lock (gate)
        {
            disposeRequested = true;
            if (!started) disposed = true;
            release = terminal || !started;
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
        bool initializing;
        lock (gate) initializing = initializationRunning;
        if (initializing) await initializationCompletion.Task.ConfigureAwait(false);
        cameraOverlay?.Stop();
        Exception? releaseError = null, recordingError = null;
        await nativeOperation.WaitAsync().ConfigureAwait(false);
        try { recordingError = ReleaseResources(); }
        catch (Exception error) { releaseError = error; }
        finally { nativeOperation.Release(); }
        // Native recorder disposal has now released its ManagedIStream roots. The worker cannot
        // enter native code after Stop; join it outside nativeOperation so a queued update can exit.
        if (cameraOverlay is { } overlay)
        {
            overlay.Failed -= OnCameraOverlayFailed;
            try { await overlay.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (overlay.Completion.IsFaulted)
            { Trace.TraceError($"Camera overlay worker failed after native resources were released: {error}"); }
            catch (Exception error) { releaseError ??= error; }
            cameraOverlay = null;
        }
        lock (gate)
        {
            if (releaseError is null)
            {
                if (recordingError is not null)
                {
                    terminal = true;
                    ChangeState(RecordingState.Failed);
                    completion.TrySetException(recordingError);
                }
                releaseCompletion.TrySetResult();
                if (started && !completion.Task.IsCompleted)
                {
                    ChangeState(RecordingState.Complete);
                    completion.TrySetResult(clock.Elapsed);
                }
            }
            else
            {
                terminal = true;
                ChangeState(RecordingState.Failed);
                completion.TrySetException(releaseError);
                releaseCompletion.TrySetException(releaseError);
            }
        }
        finishingCancellation.Cancel();
    }

    private Exception? ReleaseResources()
    {
        Recorder? finishedRecorder;
        FileStream? finishedOutput;
        FileStream? finishedLog;
        lock (gate)
        {
            disposed = true;
            finishedRecorder = recorder;
            finishedOutput = output;
            finishedLog = diagnosticLog;
            recorder = null;
            output = null;
            diagnosticLog = null;
        }
        Exception? releaseError = null, recordingError = null;
        try
        {
            if (releaseOverride is not null) releaseOverride();
            else
            {
                try
                {
                    if (!completion.Task.IsFaulted && started)
                    {
                        if (finishedOutput is null) throw new IOException("录制没有生成视频数据。");
                        finishedOutput.Flush();
                        if (finishedOutput.Length == 0) throw new IOException("录制没有生成视频数据。");
                    }
                }
                catch (Exception failure) { recordingError = failure; }
                if (finishedRecorder is not null)
                {
                    finishedRecorder.OnStatusChanged -= OnStatusChanged;
                    finishedRecorder.OnRecordingComplete -= OnRecordingComplete;
                    finishedRecorder.OnRecordingFailed -= OnRecordingFailed;
                    finishedRecorder.OnFrameRecorded -= OnWindowFrameRecorded;
                    finishedRecorder.Dispose();
                }
            }
        }
        catch (Exception failure) { releaseError = failure; }
        finally
        {
            try { finishedOutput?.Dispose(); }
            catch (Exception failure) { releaseError ??= failure; }
            try { finishedLog?.Dispose(); }
            catch (Exception failure) { releaseError ??= failure; }
            if (warmupSource is not null)
            {
                warmupSource.OnFrameRecorded -= OnSourceFrameReady;
                warmupSource = null;
            }
        }
        if (releaseError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(releaseError).Throw();
        return recordingError;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RedrawWindow(nint window, nint rectangle, nint region, uint flags);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint mode);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
}
