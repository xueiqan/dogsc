using System.Buffers.Binary;
using System.IO;
using DogSC.Core;

namespace DogSC.Windows.SelfTest;

internal static class OptionalAudioChecks
{
    public static async Task<int> RunAsync(string root)
    {
        var project = ProjectStore.Create(root, "optional audio fixture", new("screen", "fixture", 30, false, false, null));
        var bytes = new byte[56];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 24); "ftyp"u8.CopyTo(bytes.AsSpan(4)); "isom"u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 16); "moov"u8.CopyTo(bytes.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32), 8); "free"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(40), 16); "mdat"u8.CopyTo(bytes.AsSpan(44));
        File.WriteAllBytes(project.MediaPath, bytes);
        project = ProjectStore.Complete(project, TimeSpan.FromSeconds(1));
        var saved = await RecordingFiles.PrepareCoreAsync(project, root, DateTimeOffset.Now, composeFinal: false,
            extractAudio: (_, destination) => { File.WriteAllText(destination, "incomplete"); throw new IOException("controlled M4A failure"); });
        if (!File.ReadAllBytes(saved.Video).SequenceEqual(bytes) || saved.Audio is not null || saved.Warnings.Length != 1
            || File.Exists(Path.ChangeExtension(saved.Video, ".m4a"))) throw new Exception("M4A失败破坏主视频或留下伪音频。");
        if (ProjectStore.Open(saved.Project!).State != "complete" || Directory.Exists(project.PackagePath)) throw new Exception("降级保存素材事务未完成。");
        var lockedProject = ProjectStore.Create(root, "locked publication fixture", project.Capture);
        File.WriteAllBytes(lockedProject.MediaPath, bytes);
        lockedProject = ProjectStore.Complete(lockedProject, TimeSpan.FromSeconds(1));
        FileStream? held = null;
        string? completedVideo = null;
        try
        {
            await RecordingFiles.PrepareCoreAsync(lockedProject, root, DateTimeOffset.Now.AddSeconds(2), composeFinal: false,
                extractAudio: (video, _) => { completedVideo = video; held = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.None); return false; });
            throw new Exception("视频占用未触发保存失败。");
        }
        catch (IOException error) when (error.Message.Contains("已经完成的视频保留在") && error.Message.Contains(lockedProject.PackagePath)) { }
        finally { held?.Dispose(); }
        if (completedVideo is null || !File.ReadAllBytes(completedVideo).SequenceEqual(bytes)
            || ProjectStore.Open(lockedProject.PackagePath).State != "complete") throw new Exception("发布前失败删除了完整视频或素材。");
        var noTracks = ProjectStore.Create(root, "all audio tracks failed fixture", new("window", "fixture", 30, true, true, null));
        File.WriteAllBytes(noTracks.MediaPath, bytes);
        noTracks = ProjectStore.Complete(noTracks, TimeSpan.FromSeconds(1));
        File.WriteAllText(Path.Combine(noTracks.PackagePath, "media", "audio-sync.json"),
            "{\"format\":\"dogsc.windows.audio\",\"version\":1,\"tracks\":[]}");
        File.WriteAllText(Path.Combine(noTracks.PackagePath, "capture-options.json"),
            "{\"format\":\"dogsc.windows.capture-options\",\"version\":1,\"processAudio\":true,\"codec\":\"H264\"}");
        if (EditedAudioReader.GetTracks(noTracks).Length != 0) throw new Exception("失败的空声轨清单被当作损坏或伪造声轨。");
        var silentSaved = await RecordingFiles.PrepareCoreAsync(noTracks, root, DateTimeOffset.Now.AddSeconds(4), composeFinal: false,
            extractAudio: (_, _) => false);
        if (silentSaved.Audio is not null || !File.ReadAllBytes(silentSaved.Video).SequenceEqual(bytes)
            || ProjectStore.Open(silentSaved.Project!).State != "complete") throw new Exception("全部应用声轨失败仍阻断主视频保存。");
        return 6;
    }
}
