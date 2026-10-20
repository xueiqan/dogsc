using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DogSC.Core;

namespace DogSC.Windows;

public sealed record ExportSettings(int FrameRate = 30, int Height = 1080, bool AudioOnly = false, string Codec = "H264");

// Media Foundation stays in this process; source files are read-only and publishing is atomic.
public static class MediaExport
{
    private sealed class FrameRenderer : IDisposable
    {
        private readonly BlockingCollection<(BitmapSource Screen, BitmapSource? Camera, double Time, TaskCompletionSource<BitmapSource> Result)> jobs;
        private readonly Thread[] workers;
        public int Capacity => workers.Length;
        public FrameRenderer(EditorState state, int width, int height)
        {
            workers = new Thread[Math.Clamp(Environment.ProcessorCount / 4, 1, 4)];
            jobs = new(workers.Length);
            int started = 0;
            try
            {
                for (int i = 0; i < workers.Length; i++)
                {
                    workers[i] = new Thread(() =>
                    {
                        foreach (var job in jobs.GetConsumingEnumerable())
                        {
                            try { job.Result.SetResult(SceneCompositor.Render(job.Screen, state, job.Time, width, height, job.Camera)); }
                            catch (Exception error) { job.Result.SetException(error); }
                        }
                    }) { IsBackground = true, Name = "DogSC frame render" };
                    workers[i].SetApartmentState(ApartmentState.STA); workers[i].Start(); started++;
                }
            }
            catch { jobs.CompleteAdding(); foreach (var worker in workers.Take(started)) worker.Join(); jobs.Dispose(); throw; }
        }
        public Task<BitmapSource> Render(BitmapSource screen, BitmapSource? camera, double time, CancellationToken token)
        {
            var result = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            jobs.Add((screen, camera, time, result), token); return result.Task;
        }
        public void Dispose()
        {
            jobs.CompleteAdding();
            foreach (var worker in workers) worker.Join();
            jobs.Dispose();
        }
    }
    private static int _active;
    private static int _stalled;
    public static bool IsBusy => Volatile.Read(ref _active) != 0;
    public static bool IsStalled => IsBusy && Volatile.Read(ref _stalled) != 0;
    public static Task ExportAsync(ProjectDocument project, EditorState state, string output,
        ExportSettings settings, IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (System.Windows.Application.Current?.MainWindow is MainWindow main && main.HasLiveCapture)
            throw new InvalidOperationException("正在录制时可编辑旧项目；请停止录制后再导出，以保障录制性能。");
        return RunWorkerAsync((report, cancellation) => Export(project, state, output, settings, report, cancellation), "DogSC offline export", progress, token);
    }

    public static Task MuxOriginalAudioAsync(ProjectDocument project, string output,
        IProgress<double>? progress = null, CancellationToken token = default, bool overwrite = false)
    {
        if (System.Windows.Application.Current?.MainWindow is MainWindow main && main.HasLiveCapture)
            throw new InvalidOperationException("请在录制结束后合并独立声音。");
        return RunWorkerAsync((report, cancellation) => MuxOriginalAudio(project, output, report, cancellation, overwrite), "DogSC original video audio mux", progress, token);
    }

    private sealed class WorkerProgress(Action<double> report) : IProgress<double>
    { public void Report(double value) => report(value); }

    internal static async Task RunWorkerAsync(Action<IProgress<double>, CancellationToken> action, string name,
        IProgress<double>? progress = null, CancellationToken token = default, TimeSpan? inactivityTimeout = null)
    {
        token.ThrowIfCancellationRequested();
        var timeout = inactivityTimeout ?? TimeSpan.FromMinutes(2);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new InvalidOperationException(IsStalled ? "上次媒体任务已超时且原生线程尚未退出，请强制退出后重新打开；已写素材保留。" : "已有导出任务正在运行。");
        Volatile.Write(ref _stalled, 0);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        long lastProgress = Stopwatch.GetTimestamp();
        var monitoredProgress = new WorkerProgress(value =>
        { Interlocked.Exchange(ref lastProgress, Stopwatch.GetTimestamp()); progress?.Report(value); });
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try { action(monitoredProgress, cancellation.Token); }
            catch (Exception error) { failure = error; }
            finally { cancellation.Dispose(); Volatile.Write(ref _stalled, 0); Volatile.Write(ref _active, 0); }
            if (failure is OperationCanceledException) completion.TrySetCanceled();
            else if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult();
        }) { IsBackground = true, Name = name };
        try { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
        catch { cancellation.Dispose(); Volatile.Write(ref _active, 0); throw; }
        while (!completion.Task.IsCompleted)
        {
            await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, timeout.TotalMilliseconds / 4)))).ConfigureAwait(false);
            if (completion.Task.IsCompleted) break;
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastProgress)) < timeout) continue;
            if (!IsBusy) { await completion.Task.ConfigureAwait(false); return; }
            Volatile.Write(ref _stalled, 1);
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            _ = completion.Task.ContinueWith(done => _ = done.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException("媒体任务连续2分钟没有进展，已请求取消并保留素材。原生线程尚未确认退出时不能启动新任务；请使用强制退出后重新打开。");
        }
        await completion.Task.ConfigureAwait(false);
    }
    private static void MuxOriginalAudio(ProjectDocument project, string output, IProgress<double>? progress, CancellationToken token, bool overwrite)
    {
        var destination = Path.GetFullPath(output);
        var package = Path.GetFullPath(project.PackagePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(package, StringComparison.OrdinalIgnoreCase) || !Path.GetExtension(destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new IOException("原片声音合并必须保存到项目目录外的MP4。");
        RejectLinks(destination); RejectLinks(project.MediaPath);
        if (File.Exists(destination) && !overwrite) throw new IOException("目标MP4已存在，需要确认后才能覆盖。");
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".dogsc-render-{Guid.NewGuid():N}.mp4");
        bool publicationAttempted = false;
        IntPtr reader = IntPtr.Zero, writer = IntPtr.Zero;
        Mf.Check(Mf.MFStartup(0x20070, 0));
        try
        {
            token.ThrowIfCancellationRequested();
            using var audio = EditedAudioReader.Open(project, new AudioStyle());
            if (audio is null) throw new InvalidOperationException("此项目没有可合并的声音。");
            Mf.Check(Mf.MFCreateSourceReaderFromURL(project.MediaPath, IntPtr.Zero, out reader));
            Mf.Check(Mf.Call<Mf.SelectStream>(reader, 4)(reader, -2, false));
            Mf.Check(Mf.Call<Mf.SelectStream>(reader, 4)(reader, -4, true));
            Mf.Check(Mf.Call<Mf.GetCurrentType>(reader, 6)(reader, -4, out var native));
            using var encoded = new Mf.Object(native);
            Mf.Check(Mf.Call<Mf.GetGuid>(native, 10)(native, in Mf.Subtype, out var subtype));
            if (subtype != Mf.H264 && subtype != Mf.Hevc)
                throw new InvalidDataException("原片无重编码合并仅支持H.264或HEVC。");
            Mf.Check(Mf.MFCreateSinkWriterFromURL(temporary, IntPtr.Zero, IntPtr.Zero, out writer));
            Mf.Check(Mf.Call<Mf.AddStream>(writer, 3)(writer, native, out var videoStream));
            Mf.Check(Mf.Call<Mf.SetInput>(writer, 4)(writer, videoStream, native, IntPtr.Zero));
            using var aac = Mf.AudioType(true); using var pcm = Mf.AudioType(false);
            Mf.Check(Mf.Call<Mf.AddStream>(writer, 3)(writer, aac.Pointer, out var audioStream));
            Mf.Check(Mf.Call<Mf.SetInput>(writer, 4)(writer, audioStream, pcm.Pointer, IntPtr.Zero));
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 5)(writer));
            var timeline = new TimelineMap(new SourceSequence(), project.DurationSeconds);
            var fragmentSchedule = FragmentedVideoSchedule.Read(project.MediaPath);
            var maxAudio = checked((long)Math.Floor(project.DurationSeconds * 48000)); long audioFrame = 0;
            void AudioUntil(double time)
            {
                var limit = Math.Min(maxAudio, (long)Math.Floor(Math.Max(0, time) * 48000));
                while (audioFrame < limit)
                {
                    token.ThrowIfCancellationRequested(); var count = (int)Math.Min(1024, limit - audioFrame);
                    var stamp = (long)Math.Round(audioFrame * 10_000_000.0 / 48000); var end = (long)Math.Round((audioFrame + count) * 10_000_000.0 / 48000);
                    Mf.Write(writer, audioStream, audio.Read(timeline, audioFrame, count), stamp, end - stamp); audioFrame += count;
                }
            }
            for (var frame = 0; ; frame++)
            {
                if (frame >= 10_000_000) throw new InvalidDataException("合并超过1000万样本。"); token.ThrowIfCancellationRequested();
                Mf.Check(Mf.Call<Mf.ReadSample>(reader, 9)(reader, -4, 0, out _, out var flags, out var stamp, out var sample));
                using var item = new Mf.Object(sample);
                if (sample != IntPtr.Zero)
                {
                    Mf.Check(Mf.Call<Mf.GetCount>(sample, 45)(sample, out var length));
                    if (length is <= 0 or > 268_435_456) throw new InvalidDataException("压缩视频样本长度无效。");
                    Mf.Check(Mf.Call<Mf.GetSampleTime>(sample, 37)(sample, out var sampleDuration));
                    if (fragmentSchedule is not null)
                    {
                        if (frame >= fragmentSchedule.Count) throw new InvalidDataException("压缩视频样本多于片段时间表。");
                        (stamp, sampleDuration) = fragmentSchedule[frame];
                        Mf.Check(Mf.Call<Mf.SetTime>(sample, 36)(sample, stamp)); Mf.Check(Mf.Call<Mf.SetTime>(sample, 38)(sample, sampleDuration));
                    }
                    if (stamp < 0 || sampleDuration <= 0) throw new InvalidDataException("压缩视频样本时间无效。");
                    // Both media types stay compressed. SinkWriter copies video access units unchanged.
                    Mf.Check(Mf.Call<Mf.WriteSample>(writer, 6)(writer, videoStream, sample));
                    AudioUntil((stamp + sampleDuration) / 10_000_000.0);
                    progress?.Report(Math.Clamp(100 * stamp / 10_000_000.0 / project.DurationSeconds, 0, 99));
                }
                if ((flags & 2) != 0)
                {
                    if (fragmentSchedule is not null && frame + (sample != IntPtr.Zero ? 1 : 0) != fragmentSchedule.Count) throw new InvalidDataException("压缩视频样本少于片段时间表。");
                    break;
                }
            }
            AudioUntil(project.DurationSeconds); token.ThrowIfCancellationRequested();
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 11)(writer)); Marshal.Release(writer); writer = IntPtr.Zero;
            token.ThrowIfCancellationRequested(); RejectLinks(destination);
            publicationAttempted = true;
            SafeFilePublication.Publish(temporary, destination, overwrite);
            progress?.Report(100);
        }
        finally
        {
            try
            {
                if (reader != IntPtr.Zero) Marshal.Release(reader); if (writer != IntPtr.Zero) Marshal.Release(writer);
                if (!publicationAttempted) DeleteScratch(temporary);
            }
            finally { Mf.MFShutdown(); }
        }
    }

    private static void Export(ProjectDocument project, EditorState state, string output,
        ExportSettings settings, IProgress<double>? progress, CancellationToken token)
    {
        state.Validate(project.DurationSeconds);
        state = SceneCompositor.LoadRecordingData(project, state);
        state.ValidateAssets();
        if (settings.FrameRate is < 1 or > 120 || settings.Height is not (0 or 1080 or 1440 or 2160))
            throw new ArgumentOutOfRangeException(nameof(settings));
        if (settings.Codec is not ("H264" or "HEVC"))
            throw new ArgumentException("导出编码仅支持H264或HEVC，不会自动替换编码格式。", nameof(settings));
        var destination = Path.GetFullPath(output);
        var package = Path.GetFullPath(project.PackagePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(package, StringComparison.OrdinalIgnoreCase))
            throw new IOException("导出不能覆盖项目素材，请选择项目目录外的位置。");
        if (!string.Equals(Path.GetExtension(destination), settings.AudioOnly ? ".m4a" : ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new IOException("导出扩展名与选定格式不一致。");
        RejectLinks(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".dogsc-render-{Guid.NewGuid():N}" + Path.GetExtension(destination));
        bool publicationAttempted = false;
        Mf.Check(Mf.MFStartup(0x20070, 0));
        IntPtr writer = IntPtr.Zero;
        try
        {
            token.ThrowIfCancellationRequested();
            using var video = settings.AudioOnly ? null : new MediaFrameReader(project.MediaPath);
            using var camera = !settings.AudioOnly && state.Camera.Enabled ? new CameraFrameReader(project) : null;
            using var audio = EditedAudioReader.Open(project, state.Audio);
            if (settings.AudioOnly && audio is null) throw new InvalidOperationException("此项目未录制声音，无法导出M4A。");
            var size = video is null ? (Width: 2, Height: 2) : SceneCompositor.OutputSize(state, video.Width, video.Height, settings.Height);
            using var attributes = Mf.Attributes();
            attributes.U32(Mf.Hardware, 1);
            Mf.Check(Mf.MFCreateSinkWriterFromURL(temporary, IntPtr.Zero, attributes.Pointer, out writer));
            int videoStream = -1, audioStream = -1;
            if (video is not null)
            {
                using var encoded = Mf.VideoType(size.Width, size.Height, settings.FrameRate, true, settings.Codec);
                Mf.Check(Mf.Call<Mf.AddStream>(writer, 3)(writer, encoded.Pointer, out videoStream));
                using var raw = Mf.VideoType(size.Width, size.Height, settings.FrameRate, false);
                Mf.Check(Mf.Call<Mf.SetInput>(writer, 4)(writer, videoStream, raw.Pointer, IntPtr.Zero));
            }
            if (audio is not null)
            {
                using var encoded = Mf.AudioType(true);
                Mf.Check(Mf.Call<Mf.AddStream>(writer, 3)(writer, encoded.Pointer, out audioStream));
                using var pcm = Mf.AudioType(false);
                Mf.Check(Mf.Call<Mf.SetInput>(writer, 4)(writer, audioStream, pcm.Pointer, IntPtr.Zero));
            }
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 5)(writer));
            var duration = state.Segments.Sum(s => s.SourceDuration / s.PlaybackRate);
            if (duration * settings.FrameRate > 10_000_000) throw new InvalidDataException("一次离线导出最多1000万帧，请缩短时间线。");
            var outputStart = 0.0;
            long audioFrame = 0;
            var totalAudioFrames = (long)Math.Floor(duration * 48000);
            var pixels = video is null ? [] : new byte[checked(size.Width * size.Height * 4)];
            using var renderer = video is null ? null : new FrameRenderer(state, size.Width, size.Height);
            var timeline = new TimelineMap(new(state.Segments), project.DurationSeconds);
            void WriteAudioUntil(double outputLimit)
            {
                if (audio is null) return;
                var limit = Math.Min(totalAudioFrames, (long)Math.Floor(outputLimit * 48000 + 1e-8));
                while (audioFrame < limit)
                {
                    token.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(1024, limit - audioFrame);
                    var data = audio.Read(timeline, audioFrame, count);
                    var stamp = (long)Math.Round(audioFrame * 10_000_000.0 / 48000);
                    var end = (long)Math.Round((audioFrame + count) * 10_000_000.0 / 48000);
                    Mf.Write(writer, audioStream, data, stamp, end - stamp);
                    audioFrame += count;
                    if (settings.AudioOnly) progress?.Report(100 * audioFrame / totalAudioFrames);
                }
            }
            foreach (var segment in state.Segments)
            {
                var segmentEnd = outputStart + segment.SourceDuration / segment.PlaybackRate;
                if (video is not null)
                {
                    video.Seek(segment.SourceStart);
                    var firstFrame = (long)Math.Ceiling(outputStart * settings.FrameRate - 1e-8);
                    var lastFrame = (long)Math.Ceiling(segmentEnd * settings.FrameRate - 1e-8);
                    var pending = new Queue<(long Frame, double Time, Task<BitmapSource> Image)>();
                    void WriteFrame()
                    {
                        var ready = pending.Dequeue(); token.ThrowIfCancellationRequested();
                        ready.Image.GetAwaiter().GetResult().CopyPixels(pixels, size.Width * 4, 0);
                        var stamp = (long)Math.Round(ready.Time * 10_000_000);
                        var end = (long)Math.Round(Math.Min(duration, (double)(ready.Frame + 1) / settings.FrameRate) * 10_000_000);
                        Mf.Write(writer, videoStream, pixels, stamp, Math.Max(1, end - stamp));
                        WriteAudioUntil((double)(ready.Frame + 1) / settings.FrameRate);
                        progress?.Report(100 * ready.Time / duration);
                    }
                    for (var frame = firstFrame; frame < lastFrame; frame++)
                    {
                        token.ThrowIfCancellationRequested();
                        var time = (double)frame / settings.FrameRate;
                        var sourceTime = segment.SourceStart + (time - outputStart) * segment.PlaybackRate;
                        var image = video.FrameAt(sourceTime);
                        pending.Enqueue((frame, time, renderer!.Render(image, camera?.FrameAt(sourceTime + state.Camera.TimeOffset), time, token)));
                        if (pending.Count >= renderer.Capacity) WriteFrame();
                    }
                    while (pending.Count > 0) WriteFrame();
                }
                WriteAudioUntil(segmentEnd);
                outputStart = segmentEnd;
            }
            token.ThrowIfCancellationRequested();
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 11)(writer));
            Marshal.Release(writer); writer = IntPtr.Zero;
            token.ThrowIfCancellationRequested();
            RejectLinks(destination);
            publicationAttempted = true;
            SafeFilePublication.Publish(temporary, destination);
            progress?.Report(100);
        }
        catch (COMException error) when (!settings.AudioOnly && settings.Codec == "HEVC")
        {
            throw new IOException($"HEVC成片导出失败（0x{error.HResult:X8}）；请检查本机HEVC编码器支持。未改用H.264，独立原片已保留。", error);
        }
        finally
        {
            try
            {
                if (writer != IntPtr.Zero) Marshal.Release(writer);
                if (!publicationAttempted) DeleteScratch(temporary);
            }
            finally { Mf.MFShutdown(); }
        }
    }

    internal static void DeleteScratch(string path, string? logDirectory = null)
    {
        path = Path.GetFullPath(path);
        try
        {
            RejectLinks(path);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
            else File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ExceptionReporting.Write(error, $"清理临时文件失败，已保留：{path}。", logDirectory); }
    }

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("为保护素材，拒绝通过链接或重解析点读写项目。");
    }
}

public sealed class MediaFrameReader : IDisposable
{
    private readonly MfReader _reader;
    private BitmapSource? _frame;
    private double _time = -1;
    private (byte[] Bytes, double Time)? _next;
    public int Width => _reader.Width;
    public int Height => _reader.Height;
    public double Duration => _reader.Duration;
    public double LastDecodedTime => _time;
    public MediaFrameReader(string path) { _reader = new MfReader(path, false); }
    public void Seek(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (Duration > 0) seconds = Math.Min(seconds, Math.Max(0, Duration - 1e-6));
        _reader.Seek(seconds); _frame = null; _next = null; _time = -1;
    }
    public BitmapSource FrameAt(double seconds)
    {
        while (true)
        {
            var sample = _next ?? _reader.Read();
            if (sample is null) break;
            if (_frame is not null && sample.Value.Time > seconds + 1e-7) { _next = sample; break; }
            _next = null;
            var bytes = sample.Value.Bytes;
            var stride = _reader.Stride;
            if (Math.Abs((long)stride) < Width * 4L || bytes.Length < Math.Abs((long)stride) * Height)
                throw new InvalidDataException("解码画面长度或行跨度无效。");
            if (stride < 0)
            {
                var reversed = new byte[Width * Height * 4];
                for (var y = 0; y < Height; y++) Buffer.BlockCopy(bytes, (Height - y - 1) * -stride, reversed, y * Width * 4, Width * 4);
                bytes = reversed; stride = Width * 4;
            }
            _frame = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, bytes, stride);
            _frame.Freeze(); _time = sample.Value.Time;
        }
        return _frame ?? throw new InvalidDataException("视频没有可解码画面。");
    }
    public void Dispose() => _reader.Dispose();
}

public sealed class CameraFrameReader : IDisposable
{
    private readonly MediaFrameReader _reader;
    private readonly MediaSynchronization _sync;
    private double _lastTime = -1;
    public CameraFrameReader(ProjectDocument project)
    {
        var syncPath = Path.Combine(project.PackagePath, "media", "camera-sync.json"); MediaExport.RejectLinks(syncPath);
        if (!File.Exists(syncPath)) throw new IOException($"摄像头同步文件已丢失：{syncPath}。请恢复素材，或关闭项目摄像头后继续编辑。");
        using var metadata = MediaSynchronization.ReadJson(syncPath);
        var relative = metadata.RootElement.GetProperty("relativePath").GetString();
        if (relative is not ("media/camera.mp4" or "media/camera.mov")) throw new InvalidDataException("摄像头素材路径无效。");
        var path = Path.Combine(project.PackagePath, relative.Replace('/', Path.DirectorySeparatorChar)); MediaExport.RejectLinks(path);
        if (!File.Exists(path)) throw new IOException($"摄像头素材已丢失：{path}。请恢复素材，或关闭项目摄像头后继续编辑。");
        _sync = MediaSynchronization.Camera(project);
        _reader = new MediaFrameReader(path);
    }
    public BitmapSource? FrameAt(double sourceTime)
    {
        var time = _sync.MediaTime(sourceTime);
        if (time < 0 || time >= _sync.End) return null;
        if (time < _lastTime || time - _lastTime > 0.5) _reader.Seek(time);
        _lastTime = time; return _reader.FrameAt(time);
    }
    public void Dispose() => _reader.Dispose();
}

internal sealed class MediaSynchronization(double offset, double end, (double Source, double Media)[] anchors, double sourceStart = 0, double sourceScale = 1, (double Source, double Offset)[]? corrections = null)
{
    public double End { get; } = end;
    public double MediaTime(double source)
    {
        if (corrections is not null)
        {
            if (source < offset) return -1;
            var previous = Array.FindLastIndex(corrections, a => a.Source <= source);
            var correctionLeft = previous < 0 ? (Source: 0.0, Offset: 0.0) : corrections[previous];
            var correction = correctionLeft.Offset;
            if (previous + 1 < corrections.Length)
            {
                var correctionRight = corrections[previous + 1];
                correction += (correctionRight.Offset - correctionLeft.Offset) * Math.Clamp((source - correctionLeft.Source) / Math.Max(1e-9, correctionRight.Source - correctionLeft.Source), 0, 1);
            }
            return sourceStart + (source - offset) * sourceScale + correction;
        }
        if (anchors.Length == 0) return sourceStart + (source - offset) * sourceScale;
        var index = Array.FindLastIndex(anchors, a => a.Source <= source);
        if (index < 0) return source - offset;
        if (index + 1 >= anchors.Length) return anchors[index].Media + source - anchors[index].Source;
        var left = anchors[index]; var right = anchors[index + 1];
        return left.Media + (right.Media - left.Media) * (source - left.Source) / (right.Source - left.Source);
    }
    public static MediaSynchronization Camera(ProjectDocument project)
    {
        var path = Path.Combine(project.PackagePath, "media", "camera-sync.json"); MediaExport.RejectLinks(path);
        if (!File.Exists(path)) throw new IOException("摄像头素材缺少同步文件，无法自动对齐屏幕时间。");
        using var document = ReadJson(path); var item = document.RootElement;
        if (item.GetProperty("format").GetString() != "dogsc.windows.camera" || item.GetProperty("version").GetInt32() != 1
            || item.GetProperty("relativePath").GetString() is not ("media/camera.mp4" or "media/camera.mov")) throw new InvalidDataException("摄像头同步文件版本或路径无效。");
        return FromJson(item, "cameraTime");
    }
    public static System.Text.Json.JsonDocument ReadJson(string path)
    {
        if (new FileInfo(path).Length is <= 0 or > 1_048_576) throw new InvalidDataException("同步文件长度无效。");
        return System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), new() { MaxDepth = 16 });
    }
    public static MediaSynchronization FromJson(System.Text.Json.JsonElement item, string timeKey)
    {
        var offset = item.GetProperty("startOffset").GetDouble(); var end = item.GetProperty("sourceEndTime").GetDouble();
        var sourceStart = item.TryGetProperty("sourceStartTime", out var start) ? start.GetDouble() : 0;
        var sourceScale = item.TryGetProperty("sourceTimeScale", out var scale) ? scale.GetDouble() : 1;
        if (!double.IsFinite(offset) || Math.Abs(offset) > 3600 || !double.IsFinite(end) || end is <= 0 or > 604800)
            throw new InvalidDataException("媒体同步时长或偏移无效。");
        if (!double.IsFinite(sourceStart) || sourceStart is < 0 or > 604800 || !double.IsFinite(sourceScale) || sourceScale is < .1 or > 10) throw new InvalidDataException("媒体时间尺度无效。");
        if (item.TryGetProperty("syncAnchors", out var legacyAnchors) && legacyAnchors.EnumerateArray().Any(a => a.TryGetProperty("legacyOffset", out _)))
        {
            var corrections = legacyAnchors.EnumerateArray().Select(a => (Source: a.GetProperty("sourceTime").GetDouble(), Offset: a.GetProperty("legacyOffset").GetDouble())).ToArray();
            if (corrections.Length > 10000 || corrections.Any(a => !double.IsFinite(a.Source) || !double.IsFinite(a.Offset) || a.Source is < 0 or > 604800 || Math.Abs(a.Offset) > 3600)
                || corrections.Select(a => a.Source).Distinct().Count() != corrections.Length) throw new InvalidDataException("原版媒体同步锚点无效。");
            return new(offset, end, [], sourceStart, sourceScale, corrections.OrderBy(a => a.Source).ToArray());
        }
        var anchors = item.GetProperty("syncAnchors").EnumerateArray().Select(a =>
            (Source: a.GetProperty("sourceTime").GetDouble(), Media: a.GetProperty(timeKey).GetDouble(), Host: a.GetProperty("hostTimestamp").GetInt64())).ToArray();
        if (anchors.Length > 10000 || anchors.Any(a => !double.IsFinite(a.Source) || !double.IsFinite(a.Media) || a.Source is < 0 or > 604800 || a.Media < 0 || a.Media > end + 5))
            throw new InvalidDataException("媒体同步锚点无效。");
        var normalized = anchors.GroupBy(a => a.Source).Select(g => g.OrderBy(a => a.Host).Last()).OrderBy(a => a.Source).Select(a => (a.Source, a.Media)).ToArray();
        return new(offset, end, normalized, sourceStart, sourceScale, item.TryGetProperty("sourceStartTime", out _) ? [] : null);
    }
}

public sealed record AudioTrackInfo(string Role, string Path, double Offset)
{
    internal MediaSynchronization? Synchronization { get; init; }
}

public sealed class EditedAudioReader : IDisposable
{
    private sealed class Track : IDisposable
    {
        private readonly MfReader _reader;
        private readonly MediaSynchronization _sync;
        private readonly double _offset;
        private (byte[] Bytes, double Time)? _sample;
        private double _last = -1;
        private bool _ended;
        public double Gain { get; }
        public Track(string path, MediaSynchronization sync, double gain, double offset)
        { _reader = new MfReader(path, true); _sync = sync; Gain = gain; _offset = offset; }
        public (short Left, short Right) Sample(double sourceTime)
        {
            if (Gain == 0) return (0, 0);
            var time = _sync.MediaTime(sourceTime + _offset);
            if (time < 0 || time >= _sync.End) return (0, 0);
            if (time < _last - 1e-8 || time - _last > 0.5) { _reader.Seek(Math.Max(0, time - 0.02)); _sample = null; _ended = false; }
            _last = time;
            if (_ended) return (0, 0);
            while (true)
            {
                _sample ??= _reader.Read();
                if (_sample is null) { _ended = true; return (0, 0); }
                if (_sample.Value.Bytes.Length % 4 != 0) throw new InvalidDataException("声音PCM未按双声道16位对齐。");
                var frame = (int)Math.Floor((time - _sample.Value.Time) * 48000 + 1e-5);
                if (frame < 0) return (0, 0);
                if (frame < _sample.Value.Bytes.Length / 4)
                    return (System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(_sample.Value.Bytes.AsSpan(frame * 4, 2)),
                        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(_sample.Value.Bytes.AsSpan(frame * 4 + 2, 2)));
                _sample = _reader.Read(); if (_sample is null) { _ended = true; return (0, 0); }
            }
        }
        public void Dispose() => _reader.Dispose();
    }
    private readonly Track[] _tracks;
    private EditedAudioReader(Track[] tracks) => _tracks = tracks;
    public static AudioTrackInfo[] GetTracks(ProjectDocument project)
    {
        var syncPath = Path.Combine(project.PackagePath, "media", "audio-sync.json"); MediaExport.RejectLinks(syncPath);
        if (!File.Exists(syncPath)) return [];
        using var document = MediaSynchronization.ReadJson(syncPath); var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "dogsc.windows.audio" || root.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("声音同步文件版本无效。");
        var tracks = root.GetProperty("tracks").EnumerateArray().Select(t =>
        {
            var role = t.GetProperty("role").GetString(); if (role is not ("system" or "microphone")) throw new InvalidDataException("独立声音轨道身份无效。");
            var expected = t.GetProperty("relativePath").GetString() ?? "";
            if (!(role == "system" && expected == "media/recording.mp4")
                && !(new[] { ".wav", ".m4a", ".mp4", ".mov" }).Any(ext => expected == "media/" + role + ext)) throw new InvalidDataException("独立声音素材路径无效。");
            var path = Path.Combine(project.PackagePath, expected.Replace('/', Path.DirectorySeparatorChar)); MediaExport.RejectLinks(path);
            _ = MediaSynchronization.FromJson(t, "audioTime");
            return new AudioTrackInfo(role!, path, t.GetProperty("startOffset").GetDouble()) { Synchronization = MediaSynchronization.FromJson(t, "audioTime") };
        }).ToArray();
        if (tracks.Length > 2 || tracks.Select(t => t.Role).Distinct().Count() != tracks.Length)
            throw new InvalidDataException("独立声音轨道重复或数量无效。");
        if (tracks.Length == 0) return [];
        if (project.Capture.SystemAudio && !tracks.Any(t => t.Role == "system"))
            return [new("system", project.MediaPath, 0) { Synchronization = new(0, project.DurationSeconds, []) }, .. tracks];
        return tracks;
    }
    public static EditedAudioReader? Open(ProjectDocument project, AudioStyle style)
    {
        var infos = GetTracks(project); var tracks = new List<Track>();
        try
        {
            if (infos.Length == 0)
            {
                if (!project.Capture.SystemAudio && !project.Capture.Microphone) return null;
                try { tracks.Add(new(project.MediaPath, new(0, project.DurationSeconds, []), style.MasterVolume, 0)); }
                catch (COMException error) when (error.HResult == unchecked((int)0xC00D36B3)) { return null; }
            }
            else
            {
                foreach (var info in infos)
                {
                    var gain = info.Role == "system" ? (style.SystemMuted ? 0 : style.SystemVolume) : (style.MicrophoneMuted ? 0 : style.MicrophoneVolume);
                    try { tracks.Add(new(info.Path, info.Synchronization!, gain * style.MasterVolume, info.Role == "system" ? style.SystemOffset : style.MicrophoneOffset)); }
                    catch (COMException error) when (info.Path == project.MediaPath && error.HResult == unchecked((int)0xC00D36B3)) { }
                }
            }
            return new(tracks.ToArray());
        }
        catch { foreach (var track in tracks) track.Dispose(); throw; }
    }
    public byte[] Read(TimelineMap timeline, long firstFrame, int count)
    {
        var bytes = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            var source = timeline.SourceTime((firstFrame + i) / 48000.0);
            double left = 0, right = 0;
            if (source is not null)
                foreach (var track in _tracks) { var sample = track.Sample(source.Value); left += sample.Left * track.Gain; right += sample.Right * track.Gain; }
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4, 2), (short)Math.Clamp((int)Math.Round(left), short.MinValue, short.MaxValue));
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4 + 2, 2), (short)Math.Clamp((int)Math.Round(right), short.MinValue, short.MaxValue));
        }
        return bytes;
    }
    public void Dispose() { foreach (var track in _tracks) track.Dispose(); }
}

// MF's fragmented MP4 reader rounds fragment origins to whole seconds on this host.
// Read the authored tfdt/trun clock rather than allowing SinkWriter to discard backwards samples.
internal static class FragmentedVideoSchedule
{
    private sealed record Box(string Type, long Data, long End);
    public static List<(long Stamp, long Duration)>? Read(string path)
    {
        MediaExport.RejectLinks(path); using var file = File.OpenRead(path);
        var top = Boxes(file, 0, file.Length).ToArray(); if (!top.Any(b => b.Type == "moof")) return null;
        var moov = top.SingleOrDefault(b => b.Type == "moov") ?? throw new InvalidDataException("片段MP4缺少moov。");
        uint track = 0, timescale = 0, defaultDuration = 0;
        foreach (var trak in Boxes(file, moov.Data, moov.End).Where(b => b.Type == "trak"))
        {
            var children = Boxes(file, trak.Data, trak.End).ToArray(); var media = children.Single(b => b.Type == "mdia");
            var mdia = Boxes(file, media.Data, media.End).ToArray(); var handler = Payload(file, mdia.Single(b => b.Type == "hdlr"));
            if (handler.Length < 12 || System.Text.Encoding.ASCII.GetString(handler, 8, 4) != "vide") continue;
            if (track != 0) throw new InvalidDataException("多视频轨片段MP4不能自动合并声音。");
            var header = Payload(file, children.Single(b => b.Type == "tkhd")); var clock = Payload(file, mdia.Single(b => b.Type == "mdhd"));
            if (header[0] > 1 || clock[0] > 1) throw new InvalidDataException("MP4轨道时间表版本无效。");
            track = U32(header, header[0] == 1 ? 20 : 12); timescale = U32(clock, clock[0] == 1 ? 20 : 12);
            if (children.Any(b => b.Type == "edts")) throw new InvalidDataException("含编辑列表的片段MP4时间表需要单独解析。");
        }
        if (track == 0 || timescale == 0) throw new InvalidDataException("片段MP4视频轨或时间尺度无效。");
        foreach (var mvex in Boxes(file, moov.Data, moov.End).Where(b => b.Type == "mvex"))
            foreach (var trex in Boxes(file, mvex.Data, mvex.End).Where(b => b.Type == "trex"))
            { var defaults = Payload(file, trex); if (U32(defaults, 4) == track) defaultDuration = U32(defaults, 12); }
        var result = new List<(long, long)>(); long previous = -1; ulong previousDecode = 0;
        foreach (var moof in top.Where(b => b.Type == "moof"))
            foreach (var traf in Boxes(file, moof.Data, moof.End).Where(b => b.Type == "traf"))
            {
                var boxes = Boxes(file, traf.Data, traf.End).ToArray(); var tfhd = Payload(file, boxes.Single(b => b.Type == "tfhd"));
                if (U32(tfhd, 4) != track) continue;
                uint flags = U32(tfhd, 0) & 0xffffff; int pos = 8; if ((flags & 1) != 0) pos += 8; if ((flags & 2) != 0) pos += 4;
                if (tfhd[0] != 0 || (flags & ~0x03003bu) != 0 || pos > tfhd.Length) throw new InvalidDataException("MP4片段轨道标志无效。");
                var duration = (flags & 8) != 0 ? U32(tfhd, pos) : defaultDuration;
                var decodeBox = boxes.SingleOrDefault(b => b.Type == "tfdt"); ulong decode = previousDecode;
                if (decodeBox is not null) { var tfdt = Payload(file, decodeBox); if (tfdt[0] > 1) throw new InvalidDataException("MP4解码原点版本无效。"); decode = tfdt[0] == 1 ? U64(tfdt, 4) : U32(tfdt, 4); }
                foreach (var run in boxes.Where(b => b.Type == "trun"))
                {
                    var trun = Payload(file, run); var runFlags = U32(trun, 0) & 0xffffff; var count = U32(trun, 4); int p = 8;
                    if (trun[0] > 1 || (runFlags & ~0x000f05u) != 0) throw new InvalidDataException("MP4片段样本标志无效。");
                    if (count > 1_000_000 || result.Count + count > 10_000_000) throw new InvalidDataException("片段MP4样本数量过大。");
                    if ((runFlags & 1) != 0) { _ = U32(trun, p); p += 4; } if ((runFlags & 4) != 0) { _ = U32(trun, p); p += 4; }
                    for (var i = 0; i < count; i++)
                    {
                        uint sampleDuration = duration; if ((runFlags & 0x100) != 0) { sampleDuration = U32(trun, p); p += 4; }
                        if ((runFlags & 0x200) != 0) { _ = U32(trun, p); p += 4; } if ((runFlags & 0x400) != 0) { _ = U32(trun, p); p += 4; }
                        long composition = 0; if ((runFlags & 0x800) != 0) { var raw = U32(trun, p); composition = trun[0] == 1 ? unchecked((int)raw) : raw; p += 4; }
                        if (sampleDuration == 0 || decode > long.MaxValue || composition != 0) throw new InvalidDataException("片段MP4样本时间无效或含重排帧，需要完整解码时钟支持。");
                        var stamp = checked((long)Math.Round((decode + (double)composition) * 10_000_000 / timescale)); var end = checked((long)Math.Round((decode + (double)sampleDuration) * 10_000_000 / timescale));
                        if (stamp < previous || stamp < 0 || end <= stamp) throw new InvalidDataException("片段MP4样本时钟倒退。");
                        result.Add((stamp, end - stamp)); previous = stamp; decode = checked(decode + sampleDuration);
                    }
                    if (p != trun.Length) throw new InvalidDataException("片段MP4样本时间表长度不匹配。");
                }
                previousDecode = decode;
            }
        if (result.Count == 0) throw new InvalidDataException("片段MP4没有视频样本。"); return result;
    }
    private static IEnumerable<Box> Boxes(FileStream file, long begin, long end)
    {
        int count = 0; for (long pos = begin; pos < end;)
        {
            if (++count > 100000 || end - pos < 8) throw new InvalidDataException("MP4 box结构无效。");
            file.Position = pos; var header = new byte[8]; file.ReadExactly(header); ulong size = U32(header, 0); long length = 8;
            if (size == 1) { var extended = new byte[8]; file.ReadExactly(extended); size = U64(extended, 0); length = 16; }
            if (size == 0) size = checked((ulong)(end - pos));
            if (size < (ulong)length || size > (ulong)(end - pos)) throw new InvalidDataException("MP4 box离开文件边界。");
            yield return new(System.Text.Encoding.ASCII.GetString(header, 4, 4), pos + length, pos + checked((long)size)); pos += checked((long)size);
        }
    }
    private static byte[] Payload(FileStream file, Box box)
    { if (box.End - box.Data is < 4 or > 8388608) throw new InvalidDataException("MP4元数据长度无效。"); file.Position = box.Data; var bytes = new byte[checked((int)(box.End - box.Data))]; file.ReadExactly(bytes); return bytes; }
    private static uint U32(byte[] bytes, int index)
    { if (index < 0 || index > bytes.Length - 4) throw new InvalidDataException("MP4元数据被截断。"); return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(index, 4)); }
    private static ulong U64(byte[] bytes, int index)
    { if (index < 0 || index > bytes.Length - 8) throw new InvalidDataException("MP4元数据被截断。"); return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(index, 8)); }
    public static void RemuxVideo(string source, string target, IReadOnlyList<(long Stamp, long Duration)> schedule)
    {
        MediaExport.RejectLinks(source); MediaExport.RejectLinks(target); if (File.Exists(target)) throw new IOException("临时视频已存在。");
        IntPtr reader = IntPtr.Zero, writer = IntPtr.Zero;
        try
        {
            Mf.Check(Mf.MFCreateSourceReaderFromURL(source, IntPtr.Zero, out reader));
            Mf.Check(Mf.Call<Mf.SelectStream>(reader, 4)(reader, -2, false)); Mf.Check(Mf.Call<Mf.SelectStream>(reader, 4)(reader, -4, true));
            Mf.Check(Mf.Call<Mf.GetCurrentType>(reader, 6)(reader, -4, out var native)); using var format = new Mf.Object(native);
            Mf.Check(Mf.MFCreateSinkWriterFromURL(target, IntPtr.Zero, IntPtr.Zero, out writer));
            Mf.Check(Mf.Call<Mf.AddStream>(writer, 3)(writer, native, out var stream)); Mf.Check(Mf.Call<Mf.SetInput>(writer, 4)(writer, stream, native, IntPtr.Zero)); Mf.Check(Mf.Call<Mf.NoArgs>(writer, 5)(writer));
            int count = 0;
            for (;;)
            {
                Mf.Check(Mf.Call<Mf.ReadSample>(reader, 9)(reader, -4, 0, out _, out var flags, out _, out var sample)); using var owned = new Mf.Object(sample);
                if (sample != IntPtr.Zero)
                {
                    if (count >= schedule.Count) throw new InvalidDataException("解码临时视频样本多于容器时间表。");
                    Mf.Check(Mf.Call<Mf.GetCount>(sample, 45)(sample, out var length));
                    if (length is < 1 or > 268_435_456) throw new InvalidDataException("压缩视频样本长度超过256MiB或为空。");
                    var timing = schedule[count++]; Mf.Check(Mf.Call<Mf.SetTime>(sample, 36)(sample, timing.Stamp)); Mf.Check(Mf.Call<Mf.SetTime>(sample, 38)(sample, timing.Duration));
                    Mf.Check(Mf.Call<Mf.WriteSample>(writer, 6)(writer, stream, sample));
                }
                if ((flags & 2) != 0) break;
            }
            if (count != schedule.Count) throw new InvalidDataException("解码临时视频样本少于容器时间表。");
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 11)(writer));
        }
        finally { if (reader != IntPtr.Zero) Marshal.Release(reader); if (writer != IntPtr.Zero) Marshal.Release(writer); }
    }
}

internal sealed class MfReader : IDisposable
{
    private IntPtr _reader;
    private readonly int _stream;
    private int _nativeStride;
    private double? _seekTarget;
    private string? _temporaryDirectory;
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    internal int? LastDecodedPitch { get; private set; }
    public double Duration { get; }
    public MfReader(string path, bool audio)
    {
        MediaExport.RejectLinks(path);
        _stream = audio ? -3 : -4;
        try
        {
            if (!audio && FragmentedVideoSchedule.Read(path) is { } schedule)
            {
                _temporaryDirectory = Path.Combine(Path.GetTempPath(), "DogSC-clock-" + Guid.NewGuid().ToString("N")); MediaExport.RejectLinks(_temporaryDirectory);
                Directory.CreateDirectory(_temporaryDirectory); var corrected = Path.Combine(_temporaryDirectory, "clock.mp4");
                FragmentedVideoSchedule.RemuxVideo(path, corrected, schedule); path = corrected;
            }
            using var attributes = Mf.Attributes();
            if (!audio) { attributes.U32(Mf.AdvancedVideoProcessing, 1); attributes.U32(Mf.Hardware, 1); }
            Mf.Check(Mf.MFCreateSourceReaderFromURL(path, attributes.Pointer, out _reader));
            var durationKey = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
            var durationHr = Mf.Call<Mf.GetPresentationAttribute>(_reader, 12)(_reader, -1, in durationKey, out var duration);
            // Fragmented interrupted media can have no aggregate duration until finalization.
            Duration = durationHr >= 0 && duration.Type is 20 or 21 && duration.Value > 0 ? duration.Value / 10_000_000.0 : 0;
            Mf.Check(Mf.Call<Mf.SelectStream>(_reader, 4)(_reader, -2, false));
            Mf.Check(Mf.Call<Mf.SelectStream>(_reader, 4)(_reader, _stream, true));
            using var raw = audio ? Mf.AudioType(false) : Mf.Type(Mf.Video, Mf.Rgb32);
            Mf.Check(Mf.Call<Mf.SetCurrent>(_reader, 7)(_reader, _stream, IntPtr.Zero, raw.Pointer));
            Mf.Check(Mf.Call<Mf.GetCurrentType>(_reader, 6)(_reader, _stream, out var current));
            using var format = new Mf.Object(current);
            if (!audio)
            {
                Mf.Check(Mf.Call<Mf.GetU64>(current, 8)(current, in Mf.FrameSize, out var dimensions));
                Width = (int)(dimensions >> 32); Height = (int)(dimensions & 0xffffffff);
                if (Width is < 2 or > 8192 || Height is < 2 or > 8192) throw new InvalidDataException("源视频尺寸超出2至8192范围。");
                Stride = Width * 4; _nativeStride = Stride;
                if (Mf.Call<Mf.GetU32>(current, 7)(current, in Mf.DefaultStride, out var stride) >= 0) _nativeStride = unchecked((int)stride);
            }
        }
        catch { Dispose(); throw; }
    }
    public void Seek(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var value = new Mf.Variant { Type = 20, Value = (long)Math.Round(seconds * 10_000_000) };
        Mf.Check(Mf.Call<Mf.SetPosition>(_reader, 8)(_reader, in Mf.Empty, in value));
        _seekTarget = _stream == -4 ? seconds : null;
    }
    public (byte[] Bytes, double Time)? Read()
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            Mf.Check(Mf.Call<Mf.ReadSample>(_reader, 9)(_reader, _stream, 0, out _, out var flags, out var time, out var sample));
            if (_stream == -4 && (flags & 32) != 0)
            {
                Mf.Check(Mf.Call<Mf.GetCurrentType>(_reader, 6)(_reader, _stream, out var changed));
                using var updatedFormat = new Mf.Object(changed);
                if (Mf.Call<Mf.GetU32>(changed, 7)(changed, in Mf.DefaultStride, out var updatedStride) >= 0)
                    _nativeStride = unchecked((int)updatedStride);
            }
            if ((flags & 2) != 0 && sample == IntPtr.Zero) return null;
            if (sample == IntPtr.Zero) continue;
            using var ownedSample = new Mf.Object(sample);
            if (_seekTarget is { } requested)
            {
                _seekTarget = null;
                // Some MP4 byte-stream handlers choose the following keyframe. Recover
                // by decoding from the beginning rather than displaying a future frame.
                if (time / 10_000_000.0 > requested + 1e-6)
                {
                    var beginning = new Mf.Variant { Type = 20, Value = 0 };
                    Mf.Check(Mf.Call<Mf.SetPosition>(_reader, 8)(_reader, in Mf.Empty, in beginning));
                    continue;
                }
            }
            Mf.Check(Mf.Call<Mf.GetCount>(sample, 39)(sample, out var bufferCount));
            IntPtr buffer;
            if (bufferCount == 1) Mf.Check(Mf.Call<Mf.GetBufferAt>(sample, 40)(sample, 0, out buffer));
            else Mf.Check(Mf.Call<Mf.GetBuffer>(sample, 41)(sample, out buffer));
            using var ownedBuffer = new Mf.Object(buffer);
            var iid = new Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
            if (_stream == -4 && Marshal.QueryInterface(buffer, in iid, out var buffer2D) >= 0)
            {
                using var owned2D = new Mf.Object(buffer2D);
                Mf.Check(Mf.Call<Mf.Lock2D>(buffer2D, 3)(buffer2D, out var scanline, out var pitch));
                LastDecodedPitch = pitch;
                try
                {
                    if (Math.Abs((long)pitch) < Width * 4L || Math.Abs((long)pitch) > 131072) throw new InvalidDataException("解码表面行跨度无效。");
                    var pixels = new byte[Width * Height * 4];
                    // Lock2D already points to the displayed top row, even when pitch is negative.
                    for (var y = 0; y < Height; y++) Marshal.Copy(IntPtr.Add(scanline, checked(y * pitch)), pixels, y * Width * 4, Width * 4);
                    return (pixels, time / 10_000_000.0);
                }
                finally { Mf.Check(Mf.Call<Mf.NoArgs>(buffer2D, 4)(buffer2D)); }
            }
            Mf.Check(Mf.Call<Mf.LockBuffer>(buffer, 3)(buffer, out var pointer, out _, out var length));
            try
            {
                if (length is < 0 or > 268_435_456) throw new InvalidDataException("解码样本长度超过256MiB。");
                if (_stream == -4)
                {
                    var row = Width * 4;
                    var pitch = Math.Abs(_nativeStride);
                    LastDecodedPitch = _nativeStride;
                    if (pitch < row || length < (long)pitch * Height) throw new InvalidDataException("解码画面数据不足。");
                    var bytes = new byte[row * Height];
                    for (var y = 0; y < Height; y++)
                        Marshal.Copy(IntPtr.Add(pointer, (_nativeStride < 0 ? Height - y - 1 : y) * pitch), bytes, y * row, row);
                    return (bytes, time / 10_000_000.0);
                }
                var audioBytes = new byte[length]; Marshal.Copy(pointer, audioBytes, 0, length); return (audioBytes, time / 10_000_000.0);
            }
            finally { Mf.Check(Mf.Call<Mf.NoArgs>(buffer, 4)(buffer)); }
        }
        throw new InvalidDataException("媒体解码连续未返回样本。");
    }
    public void Dispose()
    {
        if (_reader != IntPtr.Zero) Marshal.Release(_reader); _reader = IntPtr.Zero;
        if (_temporaryDirectory is { } directory)
        {
            _temporaryDirectory = null;
            MediaExport.DeleteScratch(Path.Combine(directory, "clock.mp4")); MediaExport.DeleteScratch(directory);
        }
    }
}

internal static class Mf
{
    internal static readonly Guid Empty = Guid.Empty;
    internal static readonly Guid Major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71"), Audio = new("73647561-0000-0010-8000-00aa00389b71");
    internal static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71"), H264 = new("34363248-0000-0010-8000-00aa00389b71"), Hevc = new("43564548-0000-0010-8000-00aa00389b71");
    internal static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71"), Aac = new("00001610-0000-0010-8000-00aa00389b71");
    internal static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d"), DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    internal static readonly Guid Hardware = new("a634a91c-822b-41b9-a494-4de4643612b0"), AdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    internal static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    internal static T Call<T>(IntPtr instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    internal sealed class Object(IntPtr pointer) : IDisposable
    {
        public IntPtr Pointer { get; private set; } = pointer;
        public void U32(Guid key, int value) => Check(Call<SetU32>(Pointer, 21)(Pointer, in key, value));
        public void U64(Guid key, long value) => Check(Call<SetU64>(Pointer, 22)(Pointer, in key, value));
        public void Guid(Guid key, Guid value) => Check(Call<SetGuid>(Pointer, 24)(Pointer, in key, in value));
        public void Dispose() { if (Pointer != IntPtr.Zero) Marshal.Release(Pointer); Pointer = IntPtr.Zero; }
    }
    internal static Object Attributes() { Check(MFCreateAttributes(out var p, 4)); return new Object(p); }
    internal static Object Type(Guid major, Guid subtype) { Check(MFCreateMediaType(out var p)); var type = new Object(p); type.Guid(Major, major); type.Guid(Subtype, subtype); return type; }
    internal static Object VideoType(int width, int height, int fps, bool encoded, string codec = "H264")
    {
        if (codec is not ("H264" or "HEVC")) throw new ArgumentException("视频编码仅支持H264或HEVC。", nameof(codec));
        var type = Type(Video, encoded ? codec == "HEVC" ? Hevc : H264 : Rgb32);
        type.U64(FrameSize, ((long)width << 32) | (uint)height);
        type.U64(new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), ((long)fps << 32) | 1);
        type.U64(new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), (1L << 32) | 1);
        type.U32(new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), 2);
        if (encoded) type.U32(new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), (int)Math.Min(80_000_000L, (long)width * height * fps / 5));
        else type.U32(DefaultStride, width * 4);
        return type;
    }
    internal static Object AudioType(bool encoded)
    {
        var type = Type(Audio, encoded ? Aac : Pcm);
        type.U32(new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), 2);
        type.U32(new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), 48000);
        type.U32(new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), 16);
        type.U32(new("1aab75c8-cfef-451c-ab95-ac034b8e1731"), encoded ? 24000 : 192000);
        if (!encoded) type.U32(new("322de230-9eeb-43bd-ab7a-ff412251541d"), 4);
        else type.U32(new("bfbabe79-7434-4d1c-94f0-72a3b9e17188"), 0);
        return type;
    }
    internal static void Write(IntPtr writer, int stream, byte[] bytes, long time, long duration)
    {
        Check(MFCreateSample(out var sample));
        using var s = new Object(sample);
        Check(MFCreateMemoryBuffer(bytes.Length, out var buffer));
        using var b = new Object(buffer);
        Check(Call<LockBuffer>(buffer, 3)(buffer, out var data, out _, out _));
        try { Marshal.Copy(bytes, 0, data, bytes.Length); } finally { Check(Call<NoArgs>(buffer, 4)(buffer)); }
        Check(Call<SetLength>(buffer, 6)(buffer, bytes.Length));
        Check(Call<AddBuffer>(sample, 42)(sample, buffer));
        Check(Call<SetTime>(sample, 36)(sample, time));
        Check(Call<SetTime>(sample, 38)(sample, duration));
        Check(Call<WriteSample>(writer, 6)(writer, stream, sample));
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct Variant { [FieldOffset(0)] public short Type; [FieldOffset(8)] public long Value; }
    internal delegate int NoArgs(IntPtr self);
    internal delegate int SetU32(IntPtr self, in Guid key, int value);
    internal delegate int SetU64(IntPtr self, in Guid key, long value);
    internal delegate int GetGuid(IntPtr self, in Guid key, out Guid value);
    internal delegate int GetSampleTime(IntPtr self, out long value);
    internal delegate int GetU32(IntPtr self, in Guid key, out uint value);
    internal delegate int GetU64(IntPtr self, in Guid key, out ulong value);
    internal delegate int SetGuid(IntPtr self, in Guid key, in Guid value);
    internal delegate int AddStream(IntPtr self, IntPtr type, out int stream);
    internal delegate int SetInput(IntPtr self, int stream, IntPtr type, IntPtr attributes);
    internal delegate int SelectStream(IntPtr self, int stream, [MarshalAs(UnmanagedType.Bool)] bool enabled);
    internal delegate int GetCurrentType(IntPtr self, int stream, out IntPtr type);
    internal delegate int GetPresentationAttribute(IntPtr self, int stream, in Guid key, out Variant value);
    internal delegate int SetCurrent(IntPtr self, int stream, IntPtr reserved, IntPtr type);
    internal delegate int SetPosition(IntPtr self, in Guid format, in Variant position);
    internal delegate int ReadSample(IntPtr self, int stream, int control, out int actualStream, out int flags, out long time, out IntPtr sample);
    internal delegate int GetBuffer(IntPtr self, out IntPtr buffer);
    internal delegate int GetCount(IntPtr self, out int count);
    internal delegate int GetBufferAt(IntPtr self, int index, out IntPtr buffer);
    internal delegate int Lock2D(IntPtr self, out IntPtr scanline, out int pitch);
    internal delegate int LockBuffer(IntPtr self, out IntPtr data, out int max, out int length);
    internal delegate int SetLength(IntPtr self, int length);
    internal delegate int AddBuffer(IntPtr self, IntPtr buffer);
    internal delegate int SetTime(IntPtr self, long value);
    internal delegate int WriteSample(IntPtr self, int stream, IntPtr sample);
    [DllImport("mfplat.dll")] internal static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] internal static extern int MFShutdown();
    [DllImport("mfplat.dll")] internal static extern int MFCreateAttributes(out IntPtr attributes, int count);
    [DllImport("mfplat.dll")] internal static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll")] internal static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] internal static extern int MFCreateMemoryBuffer(int length, out IntPtr buffer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] internal static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] internal static extern int MFCreateSinkWriterFromURL(string url, IntPtr stream, IntPtr attributes, out IntPtr writer);
}
