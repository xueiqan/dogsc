using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DogSC.Core;

namespace DogSC.Windows.SelfTest;

internal static class MediaChecks
{
    public static async Task<int> RunExtractionAsync(string video, string root)
    {
        Directory.CreateDirectory(root);
        string audio = Path.Combine(root, "actual-audio.m4a");
        var result = await RecordingFiles.PrepareOptionalAudioAsync(video, audio);
        if (!result.HasAudio || result.Warning is not null) throw new Exception("实际M4A抽取失败：" + result.Warning);
        Mf.Check(Mf.MFStartup(0x20070, 0));
        try
        {
            using var reader = new MfReader(audio, true);
            int samples = 0; while (reader.Read() is not null) samples++;
            if (samples < 2) throw new Exception("实际M4A无法完整解码。");
            File.WriteAllText(Path.Combine(root, "audio-extraction-result.json"), JsonSerializer.Serialize(new
                { success = true, actualSourceVideo = true, mediaWorkerApartment = "STA", samples, reader.Duration }));
        }
        finally { Mf.MFShutdown(); }
        return 0;
    }

    public static async Task<int> RunAsync(string package, string root)
    {
        Directory.CreateDirectory(root);
        var project = ProjectStore.Open(package);
        var state = EditorState.Initial(project.DurationSeconds) with { Style = new SceneStyle { Padding = 0, Corner = 0, Shadow = 0, Border = 0 } };
        string output = Path.Combine(root, "export.mp4");
        await MediaExport.ExportAsync(project, state, output, new(project.Capture.FrameRate, 0));
        byte[] first = File.ReadAllBytes(output);
        if (first.Length < 1024) throw new Exception("真实导出未生成完整视频。");
        await MediaExport.ExportAsync(project, state, output, new(project.Capture.FrameRate, 0));
        Mf.Check(Mf.MFStartup(0x20070, 0));
        int frames = 0;
        try
        {
            using var reader = new MfReader(output, false);
            while (reader.Read() is not null) frames++;
            if (frames < 2 || reader.Width < 2 || reader.Height < 2) throw new Exception("覆盖导出无法完整解码。");
            File.WriteAllText(Path.Combine(root, "media-result.json"), JsonSerializer.Serialize(new { success = true, frames, reader.Width, reader.Height, sourceStride = reader.Stride, reader.LastDecodedPitch }));
        }
        finally { Mf.MFShutdown(); }
        if (Directory.EnumerateFiles(root, ".dogsc-render-*").Any()) throw new Exception("正常覆盖留下暂存文件。");
        var silent = ProjectStore.Create(root, "real media all audio failed", project.Capture with { SystemAudio = true, Microphone = false });
        File.Copy(project.MediaPath, silent.MediaPath);
        silent = ProjectStore.Complete(silent, TimeSpan.FromSeconds(project.DurationSeconds));
        File.WriteAllText(Path.Combine(silent.PackagePath, "media", "audio-sync.json"),
            "{\"format\":\"dogsc.windows.audio\",\"version\":1,\"tracks\":[]}");
        File.WriteAllText(Path.Combine(silent.PackagePath, "capture-options.json"),
            "{\"format\":\"dogsc.windows.capture-options\",\"version\":1,\"processAudio\":true,\"codec\":\"H264\"}");
        var saved = await RecordingFiles.PrepareAsync(silent, root, DateTimeOffset.Now, composeFinal: false);
        if (!File.ReadAllBytes(saved.Video).SequenceEqual(File.ReadAllBytes(project.MediaPath))) throw new Exception("全部独立声轨失败破坏了真实原片。");
        var retained = ProjectStore.Open(saved.Project!);
        string silentExport = Path.Combine(root, "empty-tracks-render.mp4");
        await MediaExport.ExportAsync(retained, state, silentExport, new(project.Capture.FrameRate, 0));
        Mf.Check(Mf.MFStartup(0x20070, 0));
        try
        {
            using var reader = new MfReader(silentExport, false);
            int silentFrames = 0; while (reader.Read() is not null) silentFrames++;
            if (silentFrames != frames) throw new Exception("空声轨成片丢失画面。");
            File.WriteAllText(Path.Combine(root, "empty-tracks-media-result.json"), JsonSerializer.Serialize(new
                { success = true, actualSourceVideo = true, controlledEmptyTracks = true, saved.Video, saved.Audio, silentFrames, reader.LastDecodedPitch }));
        }
        finally { Mf.MFShutdown(); }
        Console.WriteLine($"真实成片导出和覆盖通过，完整解码{frames}帧。");
        return 5;
    }
}
