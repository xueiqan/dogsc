using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DogSC.Core;

namespace DogSC.Windows.SelfTest;

// Real encoded samples with a synthetic QuickTime major brand, not an authentic Mac golden package.
internal static class ContainerImportChecks
{
    private sealed record EncodedSample(long Time, long Duration, int Bytes, string Sha256);
    private sealed record EncodedTrack(Guid Subtype, EncodedSample[] Samples);
    private sealed record DecodedTrack(int Samples, long Bytes, string Sha256, double Duration,
        int Width, int Height, double FirstTime, double LastTime, double Rms);

    public static async Task<int> RunAsync(string root)
    {
        root = Path.GetFullPath(root);
        MediaExport.RejectLinks(root);
        Directory.CreateDirectory(root);
        var cases = new List<string>();
        var results = new List<object>();
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAIL container import: " + name);
            cases.Add(name);
            Console.WriteLine("PASS container import: " + name);
        }

        string silent = Path.Combine(root, "fixture-h264.mp4");
        string dual = Path.Combine(root, "fixture-h264-aac.mp4");
        await MediaExport.RunWorkerAsync((_, _) =>
        {
            Mf.Check(Mf.MFStartup(0x20070, 0));
            try
            {
                AudioRateChecks.WriteSilentVideo(silent, 2);
                WriteAudioVideo(dual);
            }
            finally { Mf.MFShutdown(); }
        }, "Synthetic container-import fixtures");
        Check(IsMp4Brand(ReadBrand(silent)) && IsMp4Brand(ReadBrand(dual)),
            "fixture writer produces actual MP4-branded H264 and H264/AAC files");

        foreach (var fixture in new[]
        {
            (Name: "qt-h264", File: silent, QuickTime: true, Audio: false),
            (Name: "qt-h264-aac", File: dual, QuickTime: true, Audio: true),
            (Name: "mp4-brand-mov-extension", File: dual, QuickTime: false, Audio: true)
        })
        {
            string source = Path.Combine(root, fixture.Name + ".dogscproject");
            Directory.CreateDirectory(Path.Combine(source, "media"));
            string screen = Path.Combine(source, "media", "screen.mov");
            File.Copy(fixture.File, screen);
            if (fixture.QuickTime) SetQuickTimeBrand(screen);
            string manifest = Path.Combine(source, "project.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new
            {
                version = 27, title = fixture.Name,
                timeline = new { sourceSequence = new { kind = "fullRecording" } },
                exportSettings = new { frameRate = 30, resolution = "原始素材" },
                media = new { screen = new { relativePath = "media/screen.mov" } }
            }));
            var before = Snapshot(source);
            string inputBrand = ReadBrand(screen);
            Check((inputBrand == "qt  ") == fixture.QuickTime,
                fixture.Name + ": the branch is determined by ftyp major brand rather than extension");

            EncodedTrack sourceVideo;
            EncodedTrack? sourceAudio;
            DecodedTrack decodedSourceVideo;
            DecodedTrack? decodedSourceAudio;
            Mf.Check(Mf.MFStartup(0x20070, 0));
            try
            {
                sourceVideo = ReadEncoded(screen, false)!;
                sourceAudio = ReadEncoded(screen, true);
                decodedSourceVideo = Decode(screen, false);
                decodedSourceAudio = fixture.Audio ? Decode(screen, true) : null;
            }
            finally { Mf.MFShutdown(); }
            Check(sourceVideo.Subtype == Mf.H264 && sourceVideo.Samples.Length == 60,
                fixture.Name + ": the source contains 60 real H264 compressed samples");
            Check((sourceAudio is not null) == fixture.Audio
                && (sourceAudio is null || sourceAudio.Subtype == Mf.Aac && sourceAudio.Samples.Length > 0),
                fixture.Name + ": the actual embedded AAC stream presence matches the authored fixture");
            Check(decodedSourceVideo.Samples == 60 && decodedSourceVideo.Width == 64 && decodedSourceVideo.Height == 48
                && Math.Abs(decodedSourceVideo.Duration - 2) < .1,
                fixture.Name + ": every source video frame actually decodes at the authored size and duration");
            if (decodedSourceAudio is not null)
                Check(decodedSourceAudio.Samples > 0 && decodedSourceAudio.Bytes >= 48000 * 4L * 1.9
                    && Math.Abs(decodedSourceAudio.Duration - 2) < .12 && decodedSourceAudio.Rms > 3000,
                    fixture.Name + ": the source AAC fully decodes into a non-silent two-second PCM signal");

            LegacyImportResult imported;
            // FileShare.Read prevents this real importer from modifying or deleting either source file.
            using (var sourceLease = new FileStream(screen, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var manifestLease = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                imported = await LegacyProjectImport.ImportAsync(source, Path.Combine(root, "imported-" + fixture.Name));
                Check(!sourceLease.SafeFileHandle.IsClosed && !manifestLease.SafeFileHandle.IsClosed,
                    fixture.Name + ": source files remain held without write or delete sharing during import");
            }
            Check(SameSnapshot(before, Snapshot(source)), fixture.Name + ": the complete source package remains byte-identical");
            Check(SameSnapshot(before, Snapshot(imported.PreservedOriginalPath)),
                fixture.Name + ": every preserved legacy file remains byte-identical to the source package");
            Check(IsMp4Brand(ReadBrand(imported.Project.MediaPath)),
                fixture.Name + ": the imported screen is MP4-branded rather than renamed QuickTime data");
            Check(ProjectStore.Open(imported.Project.PackagePath).State == "complete",
                fixture.Name + ": Core accepts the real imported MP4 as a complete project");
            Check(imported.Project.Capture.SystemAudio == fixture.Audio,
                fixture.Name + ": project metadata preserves whether the screen contains embedded audio");

            EncodedTrack outputVideo;
            EncodedTrack? outputAudio;
            DecodedTrack decodedOutputVideo;
            DecodedTrack? decodedOutputAudio;
            Mf.Check(Mf.MFStartup(0x20070, 0));
            try
            {
                outputVideo = ReadEncoded(imported.Project.MediaPath, false)!;
                outputAudio = ReadEncoded(imported.Project.MediaPath, true);
                decodedOutputVideo = Decode(imported.Project.MediaPath, false);
                decodedOutputAudio = fixture.Audio ? Decode(imported.Project.MediaPath, true) : null;
            }
            finally { Mf.MFShutdown(); }
            Check(SamePayloads(sourceVideo, outputVideo),
                fixture.Name + ": every compressed video sample preserves its payload hash, length and count");
            Check(SameTimes(sourceVideo, outputVideo),
                fixture.Name + ": video sample timestamps and durations remain within one 100ns tick");
            Check((sourceAudio is null && outputAudio is null)
                || sourceAudio is not null && outputAudio is not null && SamePayloads(sourceAudio, outputAudio),
                fixture.Name + ": the embedded audio sample payload hashes and count are preserved without re-encoding");
            if (sourceAudio is not null && outputAudio is not null)
                Check(SameTimes(sourceAudio, outputAudio),
                    fixture.Name + ": embedded audio timestamps and durations remain within one 100ns tick");
            Check(decodedOutputVideo.Samples == decodedSourceVideo.Samples
                && decodedOutputVideo.Width == decodedSourceVideo.Width && decodedOutputVideo.Height == decodedSourceVideo.Height
                && decodedOutputVideo.Sha256 == decodedSourceVideo.Sha256
                && Math.Abs(decodedOutputVideo.Duration - decodedSourceVideo.Duration) < .001,
                fixture.Name + ": all imported video frames fully decode with identical pixels and duration");
            if (decodedOutputAudio is not null && decodedSourceAudio is not null)
                Check(decodedOutputAudio.Samples > 0 && decodedOutputAudio.Bytes == decodedSourceAudio.Bytes
                    && decodedOutputAudio.Sha256 == decodedSourceAudio.Sha256
                    && Math.Abs(decodedOutputAudio.Duration - decodedSourceAudio.Duration) < .001
                    && decodedOutputAudio.Rms > 3000,
                    fixture.Name + ": the imported AAC fully decodes with identical PCM bytes and duration");
            if (!fixture.QuickTime)
                Check(Hash(screen) == Hash(imported.Project.MediaPath),
                    fixture.Name + ": an MP4 brand with a MOV extension uses the existing byte-copy path");
            results.Add(new
            {
                fixture.Name, source, inputBrand, outputBrand = ReadBrand(imported.Project.MediaPath),
                imported.Project.PackagePath, imported.PreservedOriginalPath,
                sourceVideo, outputVideo, sourceAudio, outputAudio,
                decodedSourceVideo, decodedOutputVideo, decodedSourceAudio, decodedOutputAudio
            });
        }

        string quickTimeScreen = Path.Combine(root, "qt-h264.dogscproject", "media", "screen.mov");
        string quickTimeHash = Hash(quickTimeScreen);
        var counterfeit = ProjectStore.Create(Path.Combine(root, "counterfeit"), "QuickTime伪MP4",
            new CaptureMetadata("screen", "synthetic QuickTime-major validation fixture", 30, false, false, null));
        File.Copy(quickTimeScreen, counterfeit.MediaPath);
        InvalidDataException? rejected = null;
        try { ProjectStore.Complete(counterfeit, TimeSpan.FromSeconds(2)); }
        catch (InvalidDataException error) { rejected = error; }
        Check(rejected is not null && rejected.Message.Any(c => c is >= '\u4e00' and <= '\u9fff')
            && rejected.Message.Contains("保留"), "Core rejects QuickTime-branded data named recording.mp4 with a Chinese preservation message");
        Check(Hash(counterfeit.MediaPath) == quickTimeHash
            && ProjectStore.Inspect(counterfeit.PackagePath).State != "complete",
            "Core validation failure retains the original video and leaves the project incomplete");

        string canceledSource = Path.Combine(root, "qt-h264.dogscproject");
        var canceledSnapshot = Snapshot(canceledSource);
        string canceledOutput = Path.Combine(root, "pre-canceled-import");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool canceled = false;
        try { await LegacyProjectImport.ImportAsync(canceledSource, canceledOutput, cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && !Directory.Exists(canceledOutput), "a pre-canceled import deterministically creates no output package");
        Check(SameSnapshot(canceledSnapshot, Snapshot(canceledSource)), "pre-cancellation leaves the original package byte-identical");

        File.WriteAllText(Path.Combine(root, "container-import-result.json"), JsonSerializer.Serialize(new
        {
            success = true, checks = cases.Count, cases, fixtures = results,
            actualImporter = true, realH264AndAac = true, completeDecoding = true,
            compressedPayloadHashes = true, sampleTimeTolerance100nsTicks = 1,
            syntheticQuickTimeBrand = true, authenticMacGolden = false,
            noReencodingContract = true, sourcePackagesUntouched = true,
            evidence = "实际Windows编码/导入/解码回归；仅模拟qt容器品牌，未使用真实Mac黄金包，不证明全部MOV变种兼容。"
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Container import checks passed: {cases.Count}; synthetic QuickTime brand, no authentic Mac golden package.");
        return cases.Count;
    }

    private static void WriteAudioVideo(string path)
    {
        IntPtr writer = IntPtr.Zero;
        try
        {
            Mf.Check(Mf.MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out writer));
            using var h264 = Mf.VideoType(64, 48, 30, true);
            h264.U32(new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), 250000);
            using var pixels = Mf.VideoType(64, 48, 30, false);
            using var aac = Mf.AudioType(true);
            using var pcm = Mf.AudioType(false);
            Mf.Check(Mf.Call<Mf.AddStream>(writer, 3)(writer, h264.Pointer, out var videoStream));
            Mf.Check(Mf.Call<Mf.SetInput>(writer, 4)(writer, videoStream, pixels.Pointer, IntPtr.Zero));
            Mf.Check(Mf.Call<Mf.AddStream>(writer, 3)(writer, aac.Pointer, out var audioStream));
            Mf.Check(Mf.Call<Mf.SetInput>(writer, 4)(writer, audioStream, pcm.Pointer, IntPtr.Zero));
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 5)(writer));
            var frame = new byte[64 * 48 * 4];
            int audioFrame = 0;
            for (int i = 0; i < 60; i++)
            {
                for (int pixel = 0; pixel < frame.Length; pixel += 4)
                {
                    frame[pixel] = (byte)(i * 3);
                    frame[pixel + 1] = (byte)(pixel / 4 % 64 * 3);
                    frame[pixel + 2] = (byte)(pixel / 4 / 64 * 4);
                }
                long stamp = (long)Math.Round(i * 10_000_000.0 / 30);
                long end = (long)Math.Round((i + 1) * 10_000_000.0 / 30);
                Mf.Write(writer, videoStream, frame, stamp, end - stamp);
                int until = (i + 1) * 1600;
                while (audioFrame < until)
                {
                    int count = Math.Min(1024, until - audioFrame);
                    var audio = new byte[count * 4];
                    for (int j = 0; j < count; j++)
                    {
                        double time = (audioFrame + j) / 48000.0;
                        BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(j * 4),
                            (short)Math.Round(.25 * short.MaxValue * Math.Sin(2 * Math.PI * 437 * time)));
                        BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(j * 4 + 2),
                            (short)Math.Round(.25 * short.MaxValue * Math.Sin(2 * Math.PI * 659 * time)));
                    }
                    long audioStamp = (long)Math.Round(audioFrame * 10_000_000.0 / 48000);
                    long audioEnd = (long)Math.Round((audioFrame + count) * 10_000_000.0 / 48000);
                    Mf.Write(writer, audioStream, audio, audioStamp, audioEnd - audioStamp);
                    audioFrame += count;
                }
            }
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 11)(writer));
        }
        finally { if (writer != IntPtr.Zero) Marshal.Release(writer); }
    }

    private static EncodedTrack? ReadEncoded(string path, bool audio)
    {
        Mf.Check(Mf.MFCreateSourceReaderFromURL(path, IntPtr.Zero, out var reader));
        using var ownedReader = new Mf.Object(reader);
        int stream = audio ? -3 : -4;
        IntPtr native;
        int typeHr = Mf.Call<Mf.GetCurrentType>(reader, 6)(reader, stream, out native);
        if (audio && typeHr == unchecked((int)0xC00D36B3)) return null;
        Mf.Check(typeHr);
        using var ownedType = new Mf.Object(native);
        Mf.Check(Mf.Call<Mf.GetGuid>(native, 10)(native, in Mf.Subtype, out var subtype));
        Mf.Check(Mf.Call<Mf.SelectStream>(reader, 4)(reader, -2, false));
        Mf.Check(Mf.Call<Mf.SelectStream>(reader, 4)(reader, stream, true));
        var samples = new List<EncodedSample>();
        for (int attempt = 0; attempt < 10000; attempt++)
        {
            Mf.Check(Mf.Call<Mf.ReadSample>(reader, 9)(reader, stream, 0, out _, out var flags, out var time, out var sample));
            using var ownedSample = new Mf.Object(sample);
            if (sample != IntPtr.Zero)
            {
                Mf.Check(Mf.Call<Mf.GetSampleTime>(sample, 37)(sample, out var duration));
                Mf.Check(Mf.Call<Mf.GetBuffer>(sample, 41)(sample, out var buffer));
                using var ownedBuffer = new Mf.Object(buffer);
                Mf.Check(Mf.Call<Mf.LockBuffer>(buffer, 3)(buffer, out var pointer, out _, out var length));
                try
                {
                    if (length is <= 0 or > 16_777_216) throw new InvalidDataException("Fixture compressed sample size is invalid.");
                    var bytes = new byte[length];
                    Marshal.Copy(pointer, bytes, 0, length);
                    samples.Add(new(time, duration, length, Convert.ToHexString(SHA256.HashData(bytes))));
                }
                finally { Mf.Check(Mf.Call<Mf.NoArgs>(buffer, 4)(buffer)); }
            }
            if ((flags & 2) != 0) return new(subtype, samples.ToArray());
        }
        throw new InvalidDataException("Fixture compressed reader exceeded 10000 reads without EOS.");
    }

    private static DecodedTrack Decode(string path, bool audio)
    {
        using var reader = new MfReader(path, audio);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int samples = 0;
        long bytes = 0;
        double first = 0, last = 0, squares = 0;
        long values = 0;
        while (reader.Read() is { } sample)
        {
            if (samples == 0) first = sample.Time;
            last = sample.Time;
            hash.AppendData(sample.Bytes);
            bytes += sample.Bytes.Length;
            if (audio)
                for (int i = 0; i < sample.Bytes.Length - 1; i += 2)
                {
                    int value = BinaryPrimitives.ReadInt16LittleEndian(sample.Bytes.AsSpan(i));
                    squares += (double)value * value;
                    values++;
                }
            if (++samples > 1000) throw new InvalidDataException("Fixture decoder exceeded 1000 samples.");
        }
        return new(samples, bytes, Convert.ToHexString(hash.GetHashAndReset()), reader.Duration,
            reader.Width, reader.Height, first, last, values == 0 ? 0 : Math.Sqrt(squares / values));
    }

    private static bool SamePayloads(EncodedTrack before, EncodedTrack after) =>
        before.Subtype == after.Subtype && before.Samples.Length == after.Samples.Length
        && before.Samples.Zip(after.Samples).All(pair => pair.First.Bytes == pair.Second.Bytes && pair.First.Sha256 == pair.Second.Sha256);

    private static bool SameTimes(EncodedTrack before, EncodedTrack after) =>
        before.Samples.Length == after.Samples.Length && before.Samples.Zip(after.Samples)
            .All(pair => Math.Abs(pair.First.Time - pair.Second.Time) <= 1 && Math.Abs(pair.First.Duration - pair.Second.Duration) <= 1);

    private static string ReadBrand(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        file.ReadExactly(header);
        if (!header[4..8].SequenceEqual("ftyp"u8)) throw new InvalidDataException("Fixture has no leading ftyp box.");
        int offset = BinaryPrimitives.ReadUInt32BigEndian(header) == 1 ? 16 : 8;
        file.Position = offset;
        file.ReadExactly(header[..4]);
        return Encoding.ASCII.GetString(header[..4]);
    }

    private static void SetQuickTimeBrand(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Span<byte> header = stackalloc byte[16];
        file.ReadExactly(header);
        if (!header[4..8].SequenceEqual("ftyp"u8)) throw new InvalidDataException("Fixture has no leading ftyp box.");
        file.Position = BinaryPrimitives.ReadUInt32BigEndian(header) == 1 ? 16 : 8;
        file.Write("qt  "u8);
        file.Flush(true);
    }

    private static bool IsMp4Brand(string brand) => brand is "isom" or "iso2" or "mp41" or "mp42" or "MSNV" or "M4V " or "avc1";

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static Dictionary<string, string> Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(directory, file), Hash, StringComparer.OrdinalIgnoreCase);

    private static bool SameSnapshot(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
}
