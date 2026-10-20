using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;

namespace DogSC.Windows.SelfTest;

internal static class LiveAudioGainChecks
{
    public static async Task<int> RunAsync()
    {
        var cases = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            cases.Add(name);
        }
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (ArgumentOutOfRangeException) { Check(true, name); return; }
            catch (System.IO.InvalidDataException) { Check(true, name); return; }
            throw new Exception("FAIL: " + name);
        }
        foreach (int bits in new[] { 8, 16, 24, 32, 64 })
        {
            int size = bits / 8;
            var format = new WasapiAudioFormat(48000, 2, bits, 2 * size, 1);
            var data = new byte[4 * size];
            long[] samples = bits == 8 ? [-128, -64, 64, 127]
                : bits == 64 ? [long.MinValue, -10000, 10000, long.MaxValue]
                : [-(1L << (bits - 1)), -1000, 1000, (1L << (bits - 1)) - 1];
            for (int n = 0; n < samples.Length; n++) Write(data.AsSpan(n * size, size), samples[n], bits);
            byte[] identity = data.ToArray();
            WasapiAudioSession.ApplyRecordingGain(data, format, false, 1);
            Check(data.SequenceEqual(identity), $"PCM{bits}: unity gain preserves every byte");
            WasapiAudioSession.ApplyRecordingGain(data, format, false, .5f);
            for (int n = 0; n < samples.Length; n++)
                Check(Read(data.AsSpan(n * size, size), bits) == (long)decimal.Round(samples[n] / 2m),
                    $"PCM{bits}: sample {n} is halved without overflow or sign loss");
            WasapiAudioSession.ApplyRecordingGain(data, format, false, 0);
            Check(data.All(x => x == (bits == 8 ? 128 : 0)), $"PCM{bits}: mute writes valid silence");
        }
        foreach (int bits in new[] { 32, 64 })
        {
            int size = bits / 8;
            var format = new WasapiAudioFormat(16000, 2, bits, size * 2, 0xfffe);
            var data = new byte[size * 4];
            double[] samples = [-1, -.25, .5, 1];
            for (int n = 0; n < 4; n++)
                if (bits == 32) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(n * size), (float)samples[n]);
                else BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(n * size), samples[n]);
            byte[] original = data.ToArray();
            WasapiAudioSession.ApplyRecordingGain(data, format, true, 1);
            Check(data.SequenceEqual(original), $"float{bits}: unity gain is byte identical");
            WasapiAudioSession.ApplyRecordingGain(data, format, true, .5f);
            for (int n = 0; n < 4; n++)
                Check((bits == 32 ? BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(n * size))
                    : BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(n * size))) == samples[n] / 2,
                    $"float{bits}: stereo sample {n} is halved");
            WasapiAudioSession.ApplyRecordingGain(data, format, true, 0);
            Check(data.All(x => x == 0), $"float{bits}: mute writes positive zero");
        }
        var pcm = new WasapiAudioFormat(48000, 2, 16, 4, 1);
        foreach (float invalid in new[] { -.1f, 1.1f, float.NaN, float.PositiveInfinity })
            Reject(() => WasapiAudioSession.ApplyRecordingGain(new byte[4], pcm, false, invalid), "invalid gain rejected: " + invalid);
        Reject(() => WasapiAudioSession.ApplyRecordingGain(new byte[3], pcm, false, .5f), "partial PCM frame rejected");
        Reject(() => WasapiAudioSession.ApplyRecordingGain(new byte[3], pcm with { BlockAlign = 3 }, false, .5f), "inconsistent block alignment rejected");
        Reject(() => WasapiAudioSession.ApplyRecordingGain(new byte[4], pcm, true, .5f), "float16 rejected");
        using (var audio = new WasapiAudioSession())
        {
            var field = typeof(WasapiAudioSession).GetField("recordingGain", BindingFlags.NonPublic | BindingFlags.Instance)!;
            audio.SetRecordingAudio(true, .25f);
            Check((float)field.GetValue(audio)! == .25f, "enabled source stores app-only gain");
            audio.SetRecordingAudio(false, .75f);
            Check((float)field.GetValue(audio)! == 0, "disabled source atomically stores mute");
            Reject(() => audio.SetRecordingAudio(true, float.NaN), "invalid source gain rejected");
        }
        using (var session = new RecordingSession())
        {
            Check(!await session.UpdateAudioAsync(true, true, .5f, .5f), "inactive recorder ignores audio update without starting capture");
            Reject(() => session.UpdateAudioAsync(true, true, float.NaN, .5f), "native update validates gains before queueing");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool cancellationObserved = false;
            try { await session.UpdateAudioAsync(true, true, .5f, .5f, cancelled.Token); }
            catch (OperationCanceledException) { cancellationObserved = true; }
            Check(cancellationObserved, "cancelled update does not stop recorder");
            Check(session.State == RecordingState.Starting, "audio update does not change recording lifecycle");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, checks = cases.Count, cases }));
        return cases.Count;
    }

    private static void Write(Span<byte> bytes, long sample, int bits)
    {
        if (bits == 8) { bytes[0] = (byte)(sample + 128); return; }
        for (int n = 0; n < bytes.Length; n++) bytes[n] = (byte)(sample >> (n * 8));
    }
    private static long Read(ReadOnlySpan<byte> bytes, int bits)
    {
        if (bits == 8) return bytes[0] - 128;
        long value = 0;
        for (int n = 0; n < bytes.Length; n++) value |= (long)bytes[n] << (n * 8);
        int shift = 64 - bits;
        return value << shift >> shift;
    }
}
