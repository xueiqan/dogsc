using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DogSC.Core;

namespace DogSC.Windows.SelfTest;

internal static class AudioRateChecks
{
    private sealed record Signal(int Frames, double Duration, double LeftHz, double RightHz, double Rms,
        int SampleRate, int Channels, int BitsPerSample);

    public static async Task<int> RunAsync(string root)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        int checks = 0;
        var results = new List<object>();
        const double seconds = 1.2;
        ProjectDocument? windowsFixture = null;
        Mf.Check(Mf.MFStartup(0x20070, 0));
        try
        {
            string screen = Path.Combine(root, "silent-screen.mp4");
            WriteSilentVideo(screen, seconds);
            var expectedVideo = ReadVideo(screen);
            Require(expectedVideo.Frames == 36, "Actual fixture encoder must preserve 36 authored video frames.");
            checks++;
            foreach (int rate in new[] { 8000, 16000, 44100, 96000 })
            {
                int channels = rate < 44100 ? 1 : 2;
                bool floating = rate == 96000;
                var project = ProjectStore.Create(root, "rate-" + rate,
                    new CaptureMetadata("screen", "controlled audio sample-rate fixture", 30, false, true, null))
                    with { DurationSeconds = seconds };
                File.Copy(screen, project.MediaPath);
                project = ProjectStore.Complete(project, TimeSpan.FromSeconds(seconds));
                windowsFixture ??= project;
                string wave = Path.Combine(project.PackagePath, "media", "microphone.wav");
                WriteWave(wave, rate, channels, floating, seconds, 437, channels == 1 ? 437 : 659);
                WriteSync(project, [new { role = "microphone", relativePath = "media/microphone.wav",
                    startOffset = 0, sourceEndTime = seconds, syncAnchors = Array.Empty<object>() }]);
                var decoded = Decode(wave);
                Require(decoded.SampleRate == 48000 && decoded.Channels == 2 && decoded.BitsPerSample == 16,
                    "MF must actually negotiate 48 kHz stereo 16-bit PCM for " + rate);
                Require(Math.Abs(decoded.Frames / 48000.0 - seconds) < .02,
                    "Resampled WAV duration must not run fast or slow for " + rate);
                CheckTone(decoded, channels == 1 ? 437 : 659, "WAV " + rate);
                checks += 3;

                var timeline = new TimelineMap(new SourceSequence(), seconds);
                Signal mixed;
                using (var audio = EditedAudioReader.Open(project, new AudioStyle())!)
                    mixed = Analyze(audio.Read(timeline, 0, (int)(seconds * 48000)), seconds, 48000, 2, 16);
                CheckTone(mixed, channels == 1 ? 437 : 659, "mixed " + rate);
                Require(mixed.Frames == (int)(seconds * 48000), "Edited reader must preserve the screen timeline sample count.");
                checks += 2;

                string exported = Path.Combine(root, "rate-" + rate + ".m4a");
                await MediaExport.ExportAsync(project, EditorState.Initial(seconds), exported,
                    new ExportSettings(30, 0, AudioOnly: true));
                var encoded = Decode(exported);
                Require(Math.Abs(encoded.Duration - seconds) < .1,
                    "AAC export duration must match source timing for " + rate);
                CheckTone(encoded, channels == 1 ? 437 : 659, "AAC " + rate);
                checks += 2;

                string originalExport = Path.Combine(root, "original-rate-" + rate + ".mp4");
                await MainWindow.ExportOriginalMp4Async(project, originalExport, false, CancellationToken.None);
                var originalAudio = Decode(originalExport);
                var originalVideo = ReadVideo(originalExport);
                CheckTone(originalAudio, channels == 1 ? 437 : 659, "original export " + rate);
                Require(originalVideo == expectedVideo,
                    "Imported original export must preserve actual video frames and decoded pixels.");
                Require(File.ReadAllBytes(screen).SequenceEqual(File.ReadAllBytes(project.MediaPath)),
                    "Original export must not change the independent screen source.");
                checks += 3;
                results.Add(new { inputSampleRate = rate, inputChannels = channels,
                    inputFormat = floating ? "IEEE float32" : "PCM16", packagePath = project.PackagePath,
                    project.MediaPath, decoded, mixed, encoded, originalAudio,
                    originalVideo = new { originalVideo.Frames, originalVideo.PixelsHash } });
            }

            var dual = ProjectStore.Create(root, "dual-mixed-rate",
                new CaptureMetadata("screen", "controlled 8k and 96k mixing fixture", 30, true, true, null))
                with { DurationSeconds = seconds };
            WriteWave(Path.Combine(dual.PackagePath, "media", "system.wav"), 8000, 1, false, seconds, 437, 437);
            WriteWave(Path.Combine(dual.PackagePath, "media", "microphone.wav"), 96000, 2, true, seconds, 437, 437);
            WriteSync(dual, [
                new { role = "system", relativePath = "media/system.wav", startOffset = 0, sourceEndTime = seconds, syncAnchors = Array.Empty<object>() },
                new { role = "microphone", relativePath = "media/microphone.wav", startOffset = 0, sourceEndTime = seconds, syncAnchors = Array.Empty<object>() }]);
            Signal dualSignal;
            using (var mixed = EditedAudioReader.Open(dual, new AudioStyle())!)
                dualSignal = Analyze(mixed.Read(new TimelineMap(new SourceSequence(), seconds), 0,
                    (int)(seconds * 48000)), seconds, 48000, 2, 16);
            CheckTone(dualSignal, 437, "mixed 8k mono + 96k float stereo");
            Require(dualSignal.Rms > 8500, "Two native-rate tracks must both contribute to the mix.");
            checks += 2;
            var optionsPath = Path.Combine(windowsFixture!.PackagePath, "capture-options.json");
            File.WriteAllText(optionsPath, "{\"format\":\"dogsc.windows.capture-options\",\"version\":1,\"processAudio\":false}");
            string windowsCopy = Path.Combine(root, "windows-copy.mp4");
            await MainWindow.ExportOriginalMp4Async(windowsFixture, windowsCopy, false, CancellationToken.None);
            Require(File.ReadAllBytes(windowsCopy).SequenceEqual(File.ReadAllBytes(windowsFixture.MediaPath)),
                "Windows processAudio=false contract must preserve byte-copy behavior.");
            checks++;
            File.WriteAllText(optionsPath, "{\"format\":\"dogsc.windows.capture-options\",\"version\":1,\"processAudio\":true}");
            string windowsMux = Path.Combine(root, "windows-process-audio.mp4");
            await MainWindow.ExportOriginalMp4Async(windowsFixture, windowsMux, false, CancellationToken.None);
            CheckTone(Decode(windowsMux), 437, "Windows processAudio=true original export");
            Require(ReadVideo(windowsMux) == expectedVideo, "Windows process-audio mux must preserve video frames.");
            checks += 2;
            checks += await RunImportedSystemAsync(root, seconds, expectedVideo);
            using var importedEmbeddedReport = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "imported-system-audio-result.json")));
            var microphones = CaptureCapabilities.GetMicrophones();
            File.WriteAllText(Path.Combine(root, "audio-rate-result.json"), JsonSerializer.Serialize(new
            {
                success = true, checks, controlledFixtures = true, liveBluetoothMicrophoneTested = false,
                activeMicrophones = microphones.Select(device => new { device.Name, device.IsDefault, device.IsBluetooth }),
                cases = results, dualSignal, importedEmbedded = importedEmbeddedReport.RootElement,
                officialReference = "https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-imfsourcereader-setcurrentmediatype"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Audio-rate checks passed: {checks}; Bluetooth device recording was not tested.");
            return checks;
        }
        finally { Mf.MFShutdown(); }
    }

    private static async Task<int> RunImportedSystemAsync(string root, double seconds,
        (int Frames, string PixelsHash) expectedVideo)
    {
        var project = ProjectStore.Create(root, "imported-embedded-system-and-microphone",
            new CaptureMetadata("screen", "imported embedded system audio and separate microphone", 30, true, true, null));
        File.Copy(Path.Combine(root, "original-rate-44100.mp4"), project.MediaPath);
        project = ProjectStore.Complete(project, TimeSpan.FromSeconds(seconds));
        WriteWave(Path.Combine(project.PackagePath, "media", "microphone.wav"), 16000, 1, false, seconds, 437, 437);
        object[] tracks = [
            new { role = "system", relativePath = "media/recording.mp4", startOffset = 0, sourceEndTime = seconds, syncAnchors = Array.Empty<object>() },
            new { role = "microphone", relativePath = "media/microphone.wav", startOffset = 0, sourceEndTime = seconds, syncAnchors = Array.Empty<object>() }];
        WriteSync(project, tracks);
        var accepted = EditedAudioReader.GetTracks(project);
        Require(accepted.Length == 2 && accepted.Single(track => track.Role == "system").Path == project.MediaPath,
            "Mac imported embedded system audio must resolve to the fixed screen recording path.");
        var timeline = new TimelineMap(new SourceSequence(), seconds);
        int frames = (int)(seconds * 48000);
        byte[] mixed, systemOnly, microphoneOnly;
        using (var reader = EditedAudioReader.Open(project, new AudioStyle())!) mixed = reader.Read(timeline, 0, frames);
        using (var reader = EditedAudioReader.Open(project, new AudioStyle { MicrophoneVolume = 0 })!) systemOnly = reader.Read(timeline, 0, frames);
        using (var reader = EditedAudioReader.Open(project, new AudioStyle { SystemVolume = 0 })!) microphoneOnly = reader.Read(timeline, 0, frames);
        Require(mixed.Length == frames * 4, "Imported mixed audio must preserve the screen timeline duration.");
        double microphoneContribution = DifferenceRms(mixed, systemOnly);
        double systemContribution = DifferenceRms(mixed, microphoneOnly);
        Require(microphoneContribution > 2000 && systemContribution > 2000,
            "Both embedded system audio and native 16 kHz microphone must contribute to edited audio.");
        string output = Path.Combine(root, "original-imported-system-and-microphone.mp4");
        await MainWindow.ExportOriginalMp4Async(project, output, false, CancellationToken.None);
        var encoded = Decode(output);
        Require(encoded.Frames > 0 && Math.Abs(encoded.Frames / 48000.0 - seconds) < .1,
            "Imported original export must include a complete mixed audio track.");
        var video = ReadVideo(output);
        Require(video == expectedVideo, "Imported embedded system export must preserve 36 video frames and pixels.");
        string[] rejectedPaths = ["media/../recording.mp4", "media/unexpected.mp4", "media/recording.mp4"];
        for (int i = 0; i < rejectedPaths.Length; i++)
        {
            WriteSync(project, [new { role = i == 2 ? "microphone" : "system", relativePath = rejectedPaths[i],
                startOffset = 0, sourceEndTime = seconds, syncAnchors = Array.Empty<object>() }]);
            bool rejected = false;
            try { _ = EditedAudioReader.GetTracks(project); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "The fixed imported system path exception must not broaden other role or path acceptance.");
        }
        WriteSync(project, tracks);
        File.WriteAllText(Path.Combine(root, "imported-system-audio-result.json"), JsonSerializer.Serialize(new
        {
            success = true, checks = 8, captureOptionsPresent = false, embeddedSystemPath = "media/recording.mp4",
            microphoneNativeRate = 16000, durationSeconds = seconds, project.PackagePath, output, editedFrames = mixed.Length / 4,
            microphoneContribution, systemContribution, encoded,
            video = new { video.Frames, video.PixelsHash }, rejectedPaths
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 8;
    }

    private static double DifferenceRms(byte[] mixed, byte[] solo)
    {
        Require(mixed.Length == solo.Length, "Compared audio tracks must have identical timelines.");
        double sum = 0;
        for (int i = 0; i < mixed.Length; i += 2)
        {
            double difference = BinaryPrimitives.ReadInt16LittleEndian(mixed.AsSpan(i, 2)) -
                BinaryPrimitives.ReadInt16LittleEndian(solo.AsSpan(i, 2));
            sum += difference * difference;
        }
        return Math.Sqrt(sum / (mixed.Length / 2));
    }

    public static async Task<int> RunBluetoothAsync(string root)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var microphones = CaptureCapabilities.GetMicrophones();
        var selected = microphones.Where(device => device.IsBluetooth && !string.IsNullOrEmpty(device.Id))
            .OrderByDescending(device => device.IsDefault).FirstOrDefault();
        string stage = "enumeration";
        bool captureStarted = false, captureCompleted = false;
        WasapiAudioFormat? nativeFormat = null;
        long recordedFrames = 0;
        double? duration = null;
        var inventory = microphones.Select(device => new { device.Name, device.IsDefault, device.IsBluetooth }).ToArray();
        string report = Path.Combine(root, "bluetooth-audio-result.json");
        using var session = new WasapiAudioSession();
        try
        {
            if (selected is null)
                throw new InvalidOperationException("本机没有检测到活动蓝牙麦克风，无法实机蓝牙10秒测试。");
            var project = ProjectStore.Create(root, "live-bluetooth-microphone",
                new CaptureMetadata("screen", "actual Bluetooth microphone capture", 30, false, true, null));
            string wave = Path.Combine(project.PackagePath, "media", "microphone.wav");
            stage = "WASAPI start";
            session.Start(AudioCaptureKind.Microphone, selected.Id, wave);
            captureStarted = true;
            nativeFormat = await session.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            stage = "10-second microphone capture";
            await Task.Delay(TimeSpan.FromSeconds(10));
            session.Stop();
            stage = "WASAPI completion";
            var elapsed = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            captureCompleted = true;
            recordedFrames = session.RecordedFrames;
            duration = elapsed.TotalSeconds;
            Require(session.State == RecordingState.Complete && recordedFrames > 0,
                "Actual Bluetooth capture must finish with native audio frames.");
            Require(duration is >= 9.5 and <= 11.5 &&
                Math.Abs(duration.Value - recordedFrames / (double)nativeFormat.SampleRate) < .000001,
                "Bluetooth capture duration must use the endpoint's actual sample rate.");
            var waveFormat = ReadWaveFormat(wave);
            Require(waveFormat.SampleRate == nativeFormat.SampleRate && waveFormat.Channels == nativeFormat.Channels &&
                waveFormat.BitsPerSample == nativeFormat.BitsPerSample && waveFormat.BlockAlign == nativeFormat.BlockAlign &&
                waveFormat.DataFrames == recordedFrames,
                "Actual WAV header and payload must preserve the WASAPI endpoint format and frame count.");
            WriteSync(project, [new { role = "microphone", relativePath = "media/microphone.wav",
                startOffset = 0, sourceEndTime = duration.Value, syncAnchors = Array.Empty<object>() }]);
            stage = "Media Foundation WAV decode and AAC export";
            Mf.Check(Mf.MFStartup(0x20070, 0));
            try
            {
                WriteSilentVideo(project.MediaPath, duration.Value);
                project = ProjectStore.Complete(project, elapsed);
                var decoded = Decode(wave, maxSeconds: 12);
                Require(decoded.SampleRate == 48000 && decoded.Channels == 2 && decoded.BitsPerSample == 16 &&
                    Math.Abs(decoded.Frames / 48000.0 - duration.Value) < .05,
                    "Actual Bluetooth WAV must resample without changing duration.");
                string output = Path.Combine(root, "bluetooth-microphone.m4a");
                await MediaExport.ExportAsync(project, EditorState.Initial(duration.Value), output,
                    new ExportSettings(30, 0, AudioOnly: true));
                var encoded = Decode(output, maxSeconds: 12);
                Require(encoded.Frames > 0 && Math.Abs(encoded.Frames / 48000.0 - duration.Value) < .15,
                    "Bluetooth AAC output must fully decode at the original captured duration.");
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    success = true, checks = 5, controlledFixtures = false, liveBluetoothMicrophoneTested = true,
                    requestedCaptureSeconds = 10, systemDeviceSettingsChanged = false, screenFixtureGenerated = true,
                    selectedDevice = new { selected.Name, selected.IsDefault, selected.IsBluetooth },
                    activeMicrophones = inventory, nativeFormat, recordedFrames, duration, waveFormat,
                    decoded, encoded, project.PackagePath, wave, output,
                    amplitudeRequirement = "无响度或固定频率断言；静音也按真实帧数和时长验证"
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Actual Bluetooth microphone checks passed: 5; {selected.Name}; " +
                    $"native {nativeFormat.SampleRate} Hz/{nativeFormat.Channels} channels; {duration:F3}s/{recordedFrames} frames.");
                return 5;
            }
            finally { Mf.MFShutdown(); }
        }
        catch (Exception error)
        {
            session.Stop();
            if (captureStarted && !session.Completion.IsCompleted)
                try { await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                success = false, liveBluetoothMicrophoneTested = captureCompleted, stage, captureStarted,
                captureCompleted, systemDeviceSettingsChanged = false,
                selectedDevice = selected is null ? null : new { selected.Name, selected.IsDefault, selected.IsBluetooth },
                activeMicrophones = inventory, nativeFormat = session.Format ?? nativeFormat,
                recordedFrames = session.RecordedFrames, duration, error = error.ToString(),
                captureFailure = session.Failure?.ToString(), captureWorkerCompleted = session.Completion.IsCompleted,
                attemptedOnce = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
    }

    public static async Task<int> RunBluetoothVerificationAsync(string root)
    {
        root = Path.GetFullPath(root);
        using var captureReport = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "bluetooth-audio-result.json")));
        var capture = captureReport.RootElement;
        Require(capture.GetProperty("captureCompleted").GetBoolean() &&
            capture.GetProperty("captureWorkerCompleted").GetBoolean() &&
            capture.GetProperty("selectedDevice").GetProperty("IsBluetooth").GetBoolean(),
            "Verification requires the previously completed actual Bluetooth capture.");
        var project = ProjectStore.Open(Directory.EnumerateDirectories(root, "*.dogscproject").Single());
        string wave = Path.Combine(project.PackagePath, "media", "microphone.wav");
        var waveFormat = ReadWaveFormat(wave);
        var capturedFormat = capture.GetProperty("nativeFormat");
        long recordedFrames = capture.GetProperty("recordedFrames").GetInt64();
        double duration = capture.GetProperty("duration").GetDouble();
        Require(waveFormat.SampleRate == capturedFormat.GetProperty("SampleRate").GetInt32() &&
            waveFormat.Channels == capturedFormat.GetProperty("Channels").GetInt32() &&
            waveFormat.BitsPerSample == capturedFormat.GetProperty("BitsPerSample").GetInt32() &&
            waveFormat.BlockAlign == capturedFormat.GetProperty("BlockAlign").GetInt32(),
            "Existing actual WAV header must match the captured endpoint format.");
        Require(waveFormat.DataFrames == recordedFrames && recordedFrames > 0,
            "Existing actual WAV payload must preserve all recorded frames.");
        Require(duration is >= 9.5 and <= 11.5 &&
            Math.Abs(recordedFrames / (double)waveFormat.SampleRate - duration) < .000001 &&
            Math.Abs(project.DurationSeconds - duration) < .000001,
            "Existing capture duration must use the endpoint sample rate, not an assumed 48 kHz rate.");
        Mf.Check(Mf.MFStartup(0x20070, 0));
        try
        {
            var decoded = Decode(wave, maxSeconds: 12);
            Require(decoded.SampleRate == 48000 && decoded.Channels == 2 && decoded.BitsPerSample == 16 &&
                Math.Abs(decoded.Frames / 48000.0 - duration) < .05,
                "Existing actual Bluetooth WAV must normalize without changing duration.");
            string output = Path.Combine(root, "bluetooth-microphone-verified.m4a");
            await MediaExport.ExportAsync(project, EditorState.Initial(duration), output,
                new ExportSettings(30, 0, AudioOnly: true));
            var encoded = Decode(output, maxSeconds: 12);
            Require(encoded.Frames > 0 && Math.Abs(encoded.Frames / 48000.0 - duration) < .15,
                "Existing Bluetooth audio must export to AAC and fully decode at captured duration.");
            File.WriteAllText(Path.Combine(root, "bluetooth-audio-verification-result.json"), JsonSerializer.Serialize(new
            {
                success = true, checks = 5, liveBluetoothMicrophoneTested = true,
                captureSource = "existing completed actual capture; no second microphone start",
                requestedCaptureSeconds = 10, systemDeviceSettingsChanged = false, screenFixtureGenerated = true,
                selectedDevice = capture.GetProperty("selectedDevice"),
                activeMicrophones = capture.GetProperty("activeMicrophones"), nativeFormat = capturedFormat,
                recordedFrames, duration, waveFormat, decoded, encoded, project.PackagePath, wave, output,
                firstFailureReportRetained = "bluetooth-audio-result.json",
                firstFailureCause = "test helper byte bound, not native capture or production media failure",
                amplitudeRequirement = "无响度或固定频率断言；静音也按真实帧数和时长验证"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Existing actual Bluetooth verification passed: 5; {waveFormat.SampleRate} Hz/" +
                $"{waveFormat.Channels} channels; {duration:F6}s/{recordedFrames} frames; no second capture.");
            return 5;
        }
        finally { Mf.MFShutdown(); }
    }

    private sealed record WaveFormat(int SampleRate, int Channels, int BitsPerSample, int BlockAlign, long DataFrames);

    private static WaveFormat ReadWaveFormat(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        Require(new string(reader.ReadChars(4)) == "RIFF", "10-second native WAV must be a RIFF container.");
        reader.ReadUInt32();
        Require(new string(reader.ReadChars(4)) == "WAVE", "Native capture must produce a WAV container.");
        int rate = 0, channels = 0, bits = 0, align = 0;
        long bytes = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            string chunk = new(reader.ReadChars(4));
            uint count = reader.ReadUInt32();
            long next = checked(stream.Position + count + (count & 1));
            Require(next <= stream.Length, "Native WAV contains an incomplete chunk.");
            if (chunk == "fmt ")
            {
                Require(count >= 16, "Native WAV format chunk is incomplete.");
                reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                reader.ReadUInt32(); align = reader.ReadUInt16(); bits = reader.ReadUInt16();
            }
            else if (chunk == "data") bytes = count;
            stream.Position = next;
        }
        Require(rate > 0 && align > 0 && bytes >= 0 && bytes % align == 0, "Native WAV sample accounting is invalid.");
        return new(rate, channels, bits, align, bytes / align);
    }

    private static void WriteSync(ProjectDocument project, object[] tracks) =>
        File.WriteAllText(Path.Combine(project.PackagePath, "media", "audio-sync.json"), JsonSerializer.Serialize(new
            { format = "dogsc.windows.audio", version = 1, tracks }));

    internal static void WriteSilentVideo(string path, double seconds)
    {
        IntPtr writer = IntPtr.Zero;
        try
        {
            Mf.Check(Mf.MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out writer));
            using var encoded = Mf.VideoType(64, 48, 30, true);
            encoded.U32(new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), 250000);
            using var pixels = Mf.VideoType(64, 48, 30, false);
            Mf.Check(Mf.Call<Mf.AddStream>(writer, 3)(writer, encoded.Pointer, out var stream));
            Mf.Check(Mf.Call<Mf.SetInput>(writer, 4)(writer, stream, pixels.Pointer, IntPtr.Zero));
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 5)(writer));
            var frame = new byte[64 * 48 * 4];
            for (int i = 0; i < (int)(seconds * 30); i++)
            {
                for (int pixel = 0; pixel < frame.Length; pixel += 4)
                {
                    frame[pixel] = (byte)(i * 3);
                    frame[pixel + 1] = (byte)(pixel / 4 % 64 * 3);
                    frame[pixel + 2] = (byte)(pixel / 4 / 64 * 4);
                }
                long stamp = (long)Math.Round(i * 10_000_000.0 / 30);
                long end = (long)Math.Round((i + 1) * 10_000_000.0 / 30);
                Mf.Write(writer, stream, frame, stamp, end - stamp);
            }
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 11)(writer));
        }
        finally { if (writer != IntPtr.Zero) Marshal.Release(writer); }
    }

    private static (int Frames, string PixelsHash) ReadVideo(string path)
    {
        using var reader = new MfReader(path, false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int frames = 0;
        while (reader.Read() is { } sample)
        {
            hash.AppendData(sample.Bytes);
            if (++frames > 100) throw new InvalidOperationException("Fixture video unexpectedly exceeded its frame count.");
        }
        return (frames, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void WriteWave(string path, int rate, int channels, bool floating, double seconds, double leftHz, double rightHz)
    {
        int bits = floating ? 32 : 16, align = channels * bits / 8, frames = (int)(rate * seconds);
        var rawFormat = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(rawFormat, (ushort)(floating ? 3 : 1));
        BinaryPrimitives.WriteUInt16LittleEndian(rawFormat.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(rawFormat.AsSpan(4), rate);
        BinaryPrimitives.WriteInt32LittleEndian(rawFormat.AsSpan(8), rate * align);
        BinaryPrimitives.WriteUInt16LittleEndian(rawFormat.AsSpan(12), (ushort)align);
        BinaryPrimitives.WriteUInt16LittleEndian(rawFormat.AsSpan(14), (ushort)bits);
        IntPtr native = Marshal.AllocCoTaskMem(rawFormat.Length);
        try
        {
            Marshal.Copy(rawFormat, 0, native, rawFormat.Length);
            object?[] args = [native, null];
            typeof(WasapiAudioSession).GetMethod("ReadFormat", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
            Require(args[1] is WasapiAudioFormat actual && actual.SampleRate == rate && actual.Channels == channels,
                "WASAPI format parser must preserve the native format.");
        }
        finally { Marshal.FreeCoTaskMem(native); }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        using var header = new WaveAudioFileHeader(stream, rawFormat);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        for (int i = 0; i < frames; i++)
            for (int channel = 0; channel < channels; channel++)
            {
                double value = .25 * Math.Sin(2 * Math.PI * (channel == 0 ? leftHz : rightHz) * i / rate);
                if (floating) writer.Write((float)value); else writer.Write((short)Math.Round(value * short.MaxValue));
            }
        writer.Flush(); header.Complete(frames); stream.Flush(true);
    }

    private static Signal Decode(string path, double? maxSeconds = null)
    {
        if (maxSeconds is not null && (!double.IsFinite(maxSeconds.Value) || maxSeconds is <= 0 or > 12))
            throw new ArgumentOutOfRangeException(nameof(maxSeconds));
        long byteLimit = maxSeconds is null ? 2_000_000 : checked((long)Math.Ceiling(maxSeconds.Value * 48000 * 4));
        using var reader = new MfReader(path, true);
        var native = (IntPtr)typeof(MfReader).GetField("_reader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        Mf.Check(Mf.Call<Mf.GetCurrentType>(native, 6)(native, -3, out var pointer));
        using var format = new Mf.Object(pointer);
        var rateKey = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        var channelsKey = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        var bitsKey = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        Mf.Check(Mf.Call<Mf.GetU32>(pointer, 7)(pointer, in rateKey, out var rate));
        Mf.Check(Mf.Call<Mf.GetU32>(pointer, 7)(pointer, in channelsKey, out var channels));
        Mf.Check(Mf.Call<Mf.GetU32>(pointer, 7)(pointer, in bitsKey, out var bits));
        using var data = new MemoryStream();
        while (reader.Read() is { } sample)
        {
            Require(sample.Bytes.Length % 4 == 0, "Decoded audio must have four bytes per stereo frame.");
            data.Write(sample.Bytes);
            Require(data.Length < byteLimit, "Audio decode unexpectedly exceeded its test duration bound.");
        }
        return Analyze(data.ToArray(), reader.Duration, checked((int)rate), checked((int)channels), checked((int)bits));
    }

    private static Signal Analyze(byte[] bytes, double duration, int rate, int channels, int bits)
    {
        int frames = bytes.Length / 4, first = rate / 10, last = Math.Min(frames - rate / 10, rate);
        Require(last > first, "Not enough audio samples for tone measurement.");
        int leftCrossings = 0, rightCrossings = 0;
        double squares = 0;
        short previousLeft = 0, previousRight = 0;
        for (int i = first; i < last; i++)
        {
            short left = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 4, 2));
            short right = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 4 + 2, 2));
            if (i > first && previousLeft <= 0 && left > 0) leftCrossings++;
            if (i > first && previousRight <= 0 && right > 0) rightCrossings++;
            previousLeft = left; previousRight = right;
            squares += (double)left * left;
        }
        double window = (last - first) / (double)rate;
        return new(frames, duration, leftCrossings / window, rightCrossings / window,
            Math.Sqrt(squares / (last - first)), rate, channels, bits);
    }

    private static void CheckTone(Signal signal, double rightHz, string stage)
    {
        Require(Math.Abs(signal.LeftHz - 437) < 4 && Math.Abs(signal.RightHz - rightHz) < 4,
            stage + " changed pitch or channel identity.");
        Require(signal.Rms > 3000, stage + " unexpectedly muted the tone.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
