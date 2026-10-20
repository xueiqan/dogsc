using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DogSC.Core;
using Microsoft.Win32.SafeHandles;

namespace DogSC.Windows;

public enum AudioCaptureKind { SystemLoopback, Microphone }
public sealed record WasapiAudioFormat(int SampleRate, int Channels, int BitsPerSample, int BlockAlign, int FormatTag);

/// <summary>One selected endpoint captured to its own lossless WAV and QPC packet log.</summary>
public sealed class WasapiAudioSession : IDisposable
{
    private sealed record Packet(byte[]? Buffer, int ByteCount, long StartFrame, uint Frames,
        ulong DevicePosition, ulong Qpc100ns, uint Flags);
    private readonly object gate = new();
    private readonly Stopwatch clock = new();
    private readonly TaskCompletionSource<WasapiAudioFormat> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<TimeSpan> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool stopping, paused;
    private float recordingGain = 1;
    private long pauseTransitionQpc;
    private bool started, disposed;
    private AutoResetEvent? wake;
    private RecordingState state = RecordingState.Starting;
    public event Action<RecordingState>? StateChanged;
    public Task<WasapiAudioFormat> Ready => ready.Task;
    public Task<TimeSpan> Completion => completion.Task;
    public Exception? Failure { get; private set; }
    public RecordingState State { get { lock (gate) return state; } }
    public TimeSpan Duration { get { lock (gate) return clock.Elapsed; } }
    public ulong? FirstPacketQpc100ns { get; private set; }
    public long? TimelineOriginQpc100ns { get; private set; }
    public long RecordedFrames { get; private set; }
    public WasapiAudioFormat? Format { get; private set; }
    public string? TimestampPath { get; private set; }
    public uint? TargetProcessId { get; private set; }

    public void Start(AudioCaptureKind kind, string? deviceId, string outputPath)
        => StartCore(kind, deviceId, outputPath, null);

    public void StartProcessLoopback(uint targetProcessId, string outputPath)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new PlatformNotSupportedException("进程树声音回环要求Windows build20348或更新版本。");
        if (targetProcessId == 0 || targetProcessId > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(targetProcessId));
        using var target = Process.GetProcessById((int)targetProcessId);
        if (target.HasExited) throw new InvalidOperationException("所选窗口的应用进程已结束，请刷新来源。");
        StartCore(AudioCaptureKind.SystemLoopback, null, outputPath, targetProcessId);
    }

    private void StartCore(AudioCaptureKind kind, string? deviceId, string outputPath, uint? processId)
    {
        if (kind is not (AudioCaptureKind.SystemLoopback or AudioCaptureKind.Microphone))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (deviceId is not null && (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 4096))
            throw new ArgumentException("音频设备ID无效。", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("请选择音频素材路径。", nameof(outputPath));
        var output = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(output), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("独立音频素材必须使用.wav扩展名。", nameof(outputPath));
        output = LegacyProjectCodec.ResolvePackagePath(Path.GetDirectoryName(output)!, Path.GetFileName(output));
        var timestamps = output + ".timestamps.jsonl";
        LegacyProjectCodec.ResolvePackagePath(Path.GetDirectoryName(timestamps)!, Path.GetFileName(timestamps));
        if (File.Exists(output) || File.Exists(timestamps)) throw new IOException("音频素材或时间戳文件已存在，不会覆盖。");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("每个音频会话只能开始一次。");
            started = true;
            TargetProcessId = processId;
            TimestampPath = timestamps;
            new Thread(() => Capture(kind, deviceId, output, timestamps, processId))
                { IsBackground = true, Name = "DogSC WASAPI " + kind }.Start();
        }
    }

    public void SetPaused(bool value)
    {
        lock (gate)
        {
            if (disposed || completion.Task.IsCompleted || paused == value) return;
            pauseTransitionQpc = Qpc100ns();
            paused = value;
            wake?.Set();
        }
    }

    public void SetRecordingAudio(bool enabled, float volume)
    {
        RecordingSession.ValidateAudioVolume(volume, nameof(volume));
        Volatile.Write(ref recordingGain, enabled ? volume : 0);
    }

    public void Stop()
    {
        lock (gate) { if (!started || completion.Task.IsCompleted) return; stopping = true; wake?.Set(); }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Stop(); // COM objects and event handles remain owned by the capture thread until native stop completes.
    }

    private void Capture(AudioCaptureKind kind, string? deviceId, string outputPath, string timestampPath, uint? processId)
    {
        IMMDeviceEnumerator? devices = null;
        IMMDevice? device = null;
        IAudioClient? audio = null;
        IAudioCaptureClient? capture = null;
        SafeProcessHandle? processIdentity = null;
        Task? writing = null;
        Channel<Packet>? queue = null;
        Exception? failure = null;
        bool comInitialized = false, running = false;
        IntPtr formatPointer = IntPtr.Zero;
        using var signal = new AutoResetEvent(false);
        try
        {
            Check(CoInitializeEx(IntPtr.Zero, 0)); comInitialized = true;
            if (processId is { } targetId)
            {
                processIdentity = OpenProcess(0x1000, false, targetId);
                if (processIdentity.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法访问所选窗口应用，未改成整机声音。");
                if (!GetExitCodeProcess(processIdentity, out var exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (exitCode != 259) throw new InvalidOperationException("所选窗口应用已结束，未改成整机声音。");
                // A query-only handle pins the process identity for the whole capture, preventing PID reuse from widening the source.
                audio = ActivateProcessClient(targetId);
                // A virtual process endpoint has no physical-device mix format; request full-precision float PCM explicitly.
                var requested = CreateProcessFormat();
                formatPointer = Marshal.AllocCoTaskMem(requested.Length);
                Marshal.Copy(requested, 0, formatPointer, requested.Length);
            }
            else
            {
                devices = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
                if (deviceId is null) Check(devices.GetDefaultAudioEndpoint(kind == AudioCaptureKind.SystemLoopback ? 0 : 1, 1, out device));
                else Check(devices.GetDevice(deviceId, out device));
                var endpoint = (IMMEndpoint)device;
                Check(endpoint.GetDataFlow(out var flow));
                if (flow != (kind == AudioCaptureKind.SystemLoopback ? 0 : 1))
                    throw new InvalidOperationException("所选音频端点类型与系统声音/麦克风不匹配。");
                var audioId = typeof(IAudioClient).GUID;
                Check(device.Activate(ref audioId, 23, IntPtr.Zero, out var audioObject));
                audio = (IAudioClient)audioObject;
                Check(audio.GetMixFormat(out formatPointer));
            }
            var rawFormat = ReadFormat(formatPointer, out var format);
            bool floating = format.FormatTag == 3 || format.FormatTag == 0xfffe
                && new Guid(rawFormat.AsSpan(24, 16)) == new Guid("00000003-0000-0010-8000-00aa00389b71");
            Format = format;
            var flags = 0x00040000 | (kind == AudioCaptureKind.SystemLoopback ? 0x00020000u : 0);
            if (processId is not null) flags |= 0x88000000; // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY
            Check(audio.Initialize(0, flags, processId is null ? 1_000_000 : 0, 0, formatPointer, IntPtr.Zero));
            Check(audio.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle()));
            var captureId = typeof(IAudioCaptureClient).GUID;
            Check(audio.GetService(ref captureId, out var captureObject));
            capture = (IAudioCaptureClient)captureObject;
            queue = Channel.CreateBounded<Packet>(new BoundedChannelOptions(32)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            var data = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                131_072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            FileStream times;
            try { times = new FileStream(timestampPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16_384, FileOptions.Asynchronous); }
            catch { data.Dispose(); throw; }
            writing = Task.Run(() => WritePackets(queue.Reader, data, times, rawFormat, format));
            lock (gate) { wake = signal; clock.Start(); }
            var originQpc = Qpc100ns();
            TimelineOriginQpc100ns = originQpc;
            long pausedQpc = 0, pauseOrigin = 0;
            long earliestPacketQpc = originQpc;
            long lastQueuedEnd = 0;
            bool wasPaused = false;
            Check(audio.Start()); running = true;
            ChangeState(RecordingState.Recording);
            ready.TrySetResult(format);
            while (!stopping)
            {
                if (writing.IsFaulted) writing.GetAwaiter().GetResult();
                bool requestedPaused; long transitionQpc;
                lock (gate) { requestedPaused = paused; transitionQpc = pauseTransitionQpc; }
                if (requestedPaused != wasPaused)
                {
                    if (requestedPaused)
                    {
                        pauseOrigin = Math.Max(originQpc, transitionQpc);
                        ReadPackets(latestQpc: pauseOrigin);
                        // Stop/Reset breaks an already-playing process loopback stream on this host.
                        // Keep the client alive; paused buffers are released without copying or saving audio.
                        lock (gate) clock.Stop();
                        Queue(new Packet(null, 0, FramesAt(pauseOrigin), 0, 0, (ulong)pauseOrigin, 0));
                        ChangeState(RecordingState.Paused);
                    }
                    else
                    {
                        ReadPackets(discard: true);
                        earliestPacketQpc = Qpc100ns();
                        pausedQpc += earliestPacketQpc - pauseOrigin;
                        lock (gate) clock.Start();
                        ChangeState(RecordingState.Recording);
                    }
                    wasPaused = requestedPaused;
                }
                signal.WaitOne(25);
                if (!stopping)
                {
                    ReadPackets(discard: wasPaused);
                    if (wasPaused || paused) continue;
                    // Stay two native buffers behind the host clock so quiet periods write steadily without masking late packets.
                    var watermark = Qpc100ns() - 2_000_000;
                    var frame = FramesAt(watermark);
                    if (frame - lastQueuedEnd >= format.SampleRate / 10)
                        Queue(new Packet(null, 0, frame, 0, 0, (ulong)watermark, 0));
                }
            }
            ChangeState(RecordingState.Finishing);
            if (running) ReadPackets(discard: wasPaused);
            var endQpc = wasPaused ? pauseOrigin : Qpc100ns();
            Queue(new Packet(null, 0, FramesAt(endQpc), 0, 0, (ulong)endQpc, 0));

            long FramesAt(long qpc) => Math.Max(0, checked((long)Math.Round((qpc - originQpc - pausedQpc) * (double)format.SampleRate / 10_000_000)));
            void Queue(Packet packet)
            {
                QueuePacket(queue, packet);
                lastQueuedEnd = Math.Max(lastQueuedEnd, checked(packet.StartFrame + packet.Frames));
            }
            void ReadPackets(bool discard = false, long? latestQpc = null)
            {
                Check(capture.GetNextPacketSize(out var available));
                while (available != 0)
                {
                    Check(capture.GetBuffer(out var pointer, out var frames, out var flags, out var devicePosition, out var qpc));
                    byte[]? bytes = null;
                    try
                    {
                        var length = checked((int)frames * format.BlockAlign);
                        if (length > 1_048_576) throw new IOException("音频端点数据包超过1MiB限制。");
                        if ((flags & 4) != 0) throw new IOException("音频端点返回无效QPC时间戳，已保留素材；不能宣称音画同步。");
                        var packetQpc = checked((long)qpc);
                        if (discard) continue;
                        // A pause request can arrive while the capture thread drains a batch.
                        // Its QPC also limits that batch, rather than saving the paused tail.
                        long? cutoff = latestQpc;
                        lock (gate)
                            if (paused && !wasPaused) cutoff = cutoff is { } bound ? Math.Min(bound, pauseTransitionQpc) : pauseTransitionQpc;
                        var first = FramesBefore(earliestPacketQpc);
                        var end = cutoff is { } limit ? FramesBefore(limit) : frames;
                        if (end <= first) continue;
                        var kept = end - first;
                        length = checked((int)kept * format.BlockAlign);
                        bytes = ArrayPool<byte>.Shared.Rent(length);
                        if ((flags & 2) != 0) Array.Fill(bytes, format.BitsPerSample == 8 ? (byte)128 : (byte)0, 0, length);
                        else Marshal.Copy(pointer + checked((int)first * format.BlockAlign), bytes, 0, length);
                        ApplyRecordingGain(bytes.AsSpan(0, length), format, floating, Volatile.Read(ref recordingGain));
                        var trimmedQpc = checked(packetQpc + (long)Math.Ceiling(first * (10_000_000.0 / format.SampleRate)));
                        FirstPacketQpc100ns ??= (ulong)trimmedQpc;
                        var packet = new Packet(bytes, length, FramesAt(trimmedQpc), kept, checked(devicePosition + first), (ulong)trimmedQpc, flags);
                        bytes = null;
                        Queue(packet);

                        uint FramesBefore(long boundary) => (uint)Math.Clamp(Math.Ceiling(checked(boundary - packetQpc) * (double)format.SampleRate / 10_000_000), 0, frames);
                    }
                    finally
                    {
                        if (bytes is not null) ArrayPool<byte>.Shared.Return(bytes);
                        Check(capture.ReleaseBuffer(frames));
                        Check(capture.GetNextPacketSize(out available));
                    }
                }
            }
        }
        catch (Exception error) { failure = error; ready.TrySetException(error); }
        finally
        {
            lock (gate) { clock.Stop(); wake = null; }
            if (audio is not null && running)
            {
                var stopResult = audio.Stop();
                if (stopResult < 0 && failure is null) failure = Marshal.GetExceptionForHR(stopResult);
            }
            queue?.Writer.TryComplete(failure);
            if (writing is not null)
                try { writing.GetAwaiter().GetResult(); } catch (Exception error) { failure ??= error; }
            if (formatPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(formatPointer);
            Release(capture); Release(audio); Release(device); Release(devices);
            processIdentity?.Dispose();
            if (comInitialized) CoUninitialize();
            Failure = failure;
            ChangeState(failure is null ? RecordingState.Complete : RecordingState.Failed);
            if (failure is null) completion.TrySetResult(TimeSpan.FromSeconds(RecordedFrames / (double)Format!.SampleRate));
            else completion.TrySetException(new InvalidOperationException("独立音频录制失败，已保留可恢复素材：" + failure.Message, failure));
        }
    }

    private static void QueuePacket(Channel<Packet> queue, Packet packet)
    {
        if (queue.Writer.TryWrite(packet)) return;
        if (packet.Buffer is not null) ArrayPool<byte>.Shared.Return(packet.Buffer);
        throw new IOException("独立音频写入跟不上采集，已停止该声轨并保留已写素材；屏幕录制继续，没有静默丢弃音频。");
    }

    private async Task WritePackets(ChannelReader<Packet> packets, FileStream data, FileStream times,
        byte[] rawFormat, WasapiAudioFormat format)
    {
        await using var ownedData = data;
        await using var ownedTimes = times;
        using var header = new WaveAudioFileHeader(data, rawFormat);
        await using var timestamps = new StreamWriter(times, new UTF8Encoding(false), 16_384, leaveOpen: true);
        var silence = new byte[32_768];
        if (format.BitsPerSample == 8) Array.Fill(silence, (byte)128);
        try
        {
            await foreach (var packet in packets.ReadAllAsync())
            {
                try
                {
                    var gap = checked((packet.StartFrame - RecordedFrames) * format.BlockAlign);
                    while (gap > 0)
                    {
                        var count = (int)Math.Min(gap, silence.Length - silence.Length % format.BlockAlign);
                        await Write(silence.AsMemory(0, count)); gap -= count;
                    }
                    if (packet.Buffer is not null)
                    {
                        var skipped = checked((int)Math.Min(packet.Frames, Math.Max(0, RecordedFrames - packet.StartFrame))) * format.BlockAlign;
                        await Write(packet.Buffer.AsMemory(skipped, packet.ByteCount - skipped));
                    }
                    await timestamps.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        startFrame = packet.StartFrame, frames = packet.Frames, devicePosition = packet.DevicePosition,
                        qpc100ns = packet.Qpc100ns, flags = packet.Flags, clockBoundary = packet.Buffer is null
                    }));
                }
                finally { if (packet.Buffer is not null) ArrayPool<byte>.Shared.Return(packet.Buffer); }
            }
        }
        finally
        {
            while (packets.TryRead(out var queued)) if (queued.Buffer is not null) ArrayPool<byte>.Shared.Return(queued.Buffer);
            header.Complete(RecordedFrames);
            await timestamps.FlushAsync(); await data.FlushAsync(); data.Flush(flushToDisk: true);
        }

        async Task Write(ReadOnlyMemory<byte> bytes)
        {
            header.EnsureCapacity(checked(data.Length + bytes.Length), checked(RecordedFrames + bytes.Length / format.BlockAlign));
            await data.WriteAsync(bytes);
            RecordedFrames += bytes.Length / format.BlockAlign;
        }
    }

    internal static void ApplyRecordingGain(Span<byte> bytes, WasapiAudioFormat format, bool floating, float gain)
    {
        RecordingSession.ValidateAudioVolume(gain, nameof(gain));
        int size = format.BitsPerSample / 8;
        if (format.BlockAlign < 1 || format.BlockAlign != format.Channels * size || bytes.Length % format.BlockAlign != 0
            || format.BitsPerSample is not (8 or 16 or 24 or 32 or 64)
            || floating && format.BitsPerSample is not (32 or 64))
            throw new InvalidDataException("录制音量处理遇到无效音频格式或不完整的采样帧。");
        if (gain == 1) return;
        if (gain == 0)
        {
            bytes.Fill(!floating && size == 1 ? (byte)128 : (byte)0);
            return;
        }
        for (int offset = 0; offset < bytes.Length; offset += size)
        {
            var sample = bytes.Slice(offset, size);
            if (floating)
            {
                if (size == 4) BinaryPrimitives.WriteSingleLittleEndian(sample, BinaryPrimitives.ReadSingleLittleEndian(sample) * gain);
                else BinaryPrimitives.WriteDoubleLittleEndian(sample, BinaryPrimitives.ReadDoubleLittleEndian(sample) * gain);
            }
            else switch (size)
            {
                case 1: sample[0] = (byte)(128 + Math.Round((sample[0] - 128) * (double)gain)); break;
                case 2: BinaryPrimitives.WriteInt16LittleEndian(sample, (short)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(sample) * (double)gain)); break;
                case 3:
                    int value = (sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8;
                    int scaled = (int)Math.Round(value * (double)gain);
                    sample[0] = (byte)scaled; sample[1] = (byte)(scaled >> 8); sample[2] = (byte)(scaled >> 16);
                    break;
                case 4: BinaryPrimitives.WriteInt32LittleEndian(sample, (int)Math.Round(BinaryPrimitives.ReadInt32LittleEndian(sample) * (double)gain)); break;
                case 8:
                    long wide = BinaryPrimitives.ReadInt64LittleEndian(sample);
                    BinaryPrimitives.WriteInt64LittleEndian(sample, (long)decimal.Round(wide * (decimal)gain));
                    break;
            }
        }
    }

    private static byte[] ReadFormat(IntPtr pointer, out WasapiAudioFormat format)
    {
        var extra = (ushort)Marshal.ReadInt16(pointer, 16);
        var tag = (ushort)Marshal.ReadInt16(pointer, 0);
        var channels = (ushort)Marshal.ReadInt16(pointer, 2);
        var rate = Marshal.ReadInt32(pointer, 4);
        var align = (ushort)Marshal.ReadInt16(pointer, 12);
        var bits = (ushort)Marshal.ReadInt16(pointer, 14);
        if (extra > 256 || channels is < 1 or > 32 || rate is < 8000 or > 384000 || align is < 1 or > 256
            || bits is not (8 or 16 or 24 or 32 or 64) || tag is not (1 or 3 or 0xfffe)
            || align != channels * (bits / 8) || Marshal.ReadInt32(pointer, 8) != rate * align
            || (tag == 3 && bits is not (32 or 64)) || (tag == 0xfffe && extra < 22))
            throw new InvalidDataException("音频设备的原生混音格式无效或不属于可直接保存的PCM/浮点格式。");
        if (tag == 0xfffe)
        {
            var subformat = Marshal.PtrToStructure<Guid>(pointer + 24);
            if (subformat != new Guid("00000001-0000-0010-8000-00aa00389b71")
                && subformat != new Guid("00000003-0000-0010-8000-00aa00389b71"))
                throw new InvalidDataException("不支持此WAVEFORMATEXTENSIBLE子格式，不会伪装成PCM。");
            if (subformat == new Guid("00000003-0000-0010-8000-00aa00389b71") && bits is not (32 or 64))
                throw new InvalidDataException("浮点音频必须使用32位或64位容器。");
        }
        var bytes = new byte[18 + extra]; Marshal.Copy(pointer, bytes, 0, bytes.Length);
        format = new WasapiAudioFormat(rate, channels, bits, align, tag);
        return bytes;
    }

    private void ChangeState(RecordingState value)
    {
        lock (gate) state = value;
        try { StateChanged?.Invoke(value); } catch (Exception error) { Trace.TraceError("Audio state subscriber failed: " + error); }
    }
    private static long Qpc100ns() => checked((long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency)));
    private static byte[] CreateProcessFormat()
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write((ushort)3); writer.Write((ushort)2); writer.Write(48000); writer.Write(384000);
        writer.Write((ushort)8); writer.Write((ushort)32); writer.Write((ushort)0);
        return bytes.ToArray();
    }

    private static IAudioClient ActivateProcessClient(uint processId)
    {
        var parameters = Marshal.AllocCoTaskMem(12);
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            Marshal.WriteInt32(parameters, 0, 1); // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(parameters, 4, checked((int)processId));
            Marshal.WriteInt32(parameters, 8, 0); // INCLUDE_TARGET_PROCESS_TREE
            var variant = new ActivationVariant { Type = 65, BlobSize = 12, Blob = parameters };
            var callback = new ActivationCompletion();
            var id = typeof(IAudioClient).GUID;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref id, ref variant, callback, out operation));
            var audio = callback.Result.Task.GetAwaiter().GetResult();
            GC.KeepAlive(callback);
            return audio;
        }
        finally { Release(operation); Marshal.FreeCoTaskMem(parameters); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct ActivationVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint BlobSize;
        [FieldOffset(16)] public IntPtr Blob;
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationCompletion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        internal readonly TaskCompletionSource<IAudioClient> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                Check(operation.GetActivateResult(out var result, out var activated));
                Check(result);
                Result.TrySetResult((IAudioClient)activated);
            }
            catch (Exception error) { Result.TrySetException(error); }
            return 0;
        }
    }
    [ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }
    [ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }
    [DllImport("mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path,
        ref Guid id, ref ActivationVariant parameters, IActivateAudioInterfaceCompletionHandler completion,
        out IActivateAudioInterfaceAsyncOperation operation);
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMEndpoint { [PreserveSig] int GetDataFlow(out int flow); }
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long period, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig] int Start(); [PreserveSig] int Stop(); [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpc);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}

/// <summary>RIFF starts with reserved header space; RF64 extends lengths without moving recorded PCM.</summary>
internal sealed class WaveAudioFileHeader : IDisposable
{
    private readonly Stream data;
    private readonly BinaryWriter writer;
    private readonly long riffLimit;
    private readonly long samplesOffset, sizeOffset;
    internal long AudioOffset { get; }
    internal bool IsRf64 { get; private set; }

    // The reduced threshold is internal and used only by the focused container test.
    internal WaveAudioFileHeader(Stream data, byte[] rawFormat, long riffLimit = uint.MaxValue)
    {
        if (!data.CanWrite || !data.CanSeek || data.Length != 0) throw new ArgumentException("WAV header requires a new seekable stream.", nameof(data));
        if (riffLimit is < 128 or > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(riffLimit));
        this.data = data; this.riffLimit = riffLimit;
        writer = new BinaryWriter(data, Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(0u); writer.Write("WAVEJUNK"u8);
        writer.Write(72u); writer.Write(new byte[72]); // 80 bytes permit in-place ds64 + padding JUNK.
        writer.Write("fmt "u8); writer.Write(rawFormat.Length); writer.Write(rawFormat);
        if ((rawFormat.Length & 1) != 0) writer.Write((byte)0);
        writer.Write("fact"u8); writer.Write(4u); samplesOffset = data.Position; writer.Write(0u);
        writer.Write("data"u8); sizeOffset = data.Position; writer.Write(0u);
        AudioOffset = data.Position;
    }

    internal void EnsureCapacity(long nextLength, long frames)
    {
        if (nextLength < AudioOffset || frames < 0) throw new ArgumentOutOfRangeException(nameof(nextLength));
        if (!IsRf64 && (nextLength - 8 >= riffLimit || frames > uint.MaxValue))
        {
            IsRf64 = true;
            Update(nextLength, nextLength - AudioOffset, frames);
        }
    }

    internal void Complete(long frames)
    {
        var audioBytes = data.Length - AudioOffset;
        if (audioBytes < 0 || frames < 0) throw new InvalidDataException("WAV sample accounting is invalid.");
        data.Position = data.Length;
        if ((audioBytes & 1) != 0) writer.Write((byte)0);
        EnsureCapacity(data.Length, frames);
        Update(data.Length, audioBytes, frames);
    }

    private void Update(long fileLength, long audioBytes, long frames)
    {
        var resume = data.Position;
        try
        {
            data.Position = 0; writer.Write(IsRf64 ? "RF64"u8 : "RIFF"u8);
            writer.Write(IsRf64 ? uint.MaxValue : checked((uint)(fileLength - 8)));
            if (IsRf64)
            {
                data.Position = 12; writer.Write("ds64"u8); writer.Write(28u);
                writer.Write(checked((ulong)(fileLength - 8))); writer.Write(checked((ulong)audioBytes));
                writer.Write(checked((ulong)frames)); writer.Write(0u);
                writer.Write("JUNK"u8); writer.Write(36u); writer.Write(new byte[36]);
            }
            data.Position = samplesOffset; writer.Write((uint)Math.Min(frames, uint.MaxValue));
            data.Position = sizeOffset; writer.Write(IsRf64 ? uint.MaxValue : checked((uint)audioBytes));
        }
        finally { data.Position = resume; }
    }

    public void Dispose() => writer.Dispose();
}
