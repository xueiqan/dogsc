using System.IO;
using System.Media;

namespace DogSC.Windows;

internal sealed class RecordingStartCue : IDisposable
{
    private readonly MemoryStream _wave = new();
    private readonly SoundPlayer _player;

    public RecordingStartCue()
    {
        const int sampleRate = 22_050, samples = 4_410;
        using (var writer = new BinaryWriter(_wave, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(samples * 2);
            for (int i = 0; i < samples; i++)
            {
                double time = (double)i / sampleRate;
                double local = time < 0.1 ? time : time - 0.1;
                double envelope = Math.Min(local / 0.01, Math.Max(0, (0.09 - local) / 0.02));
                double frequency = time < 0.1 ? 880 : 1174.66;
                writer.Write((short)(Math.Sin(2 * Math.PI * frequency * local) * Math.Clamp(envelope, 0, 1) * 8000));
            }
        }
        _wave.Position = 0;
        _player = new SoundPlayer(_wave);
        _player.Load();
    }

    public void Play() => _player.Play();
    public void Dispose() { _player.Dispose(); _wave.Dispose(); }
}
