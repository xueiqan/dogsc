using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using DogSC.Core;

namespace DogSC.Windows;

public sealed record SavedRecording(string Folder, string Video, string? Audio, string? Project = null)
{
    public string[] Warnings { get; init; } = [];
}

public static class RecordingFiles
{
    public static Task<SavedRecording> PrepareAsync(ProjectDocument project, string parent, DateTimeOffset startedAt,
        RecordedCameraPlacement[]? cameraPlacements = null, bool automaticZoom = true, bool composeFinal = true)
        => PrepareCoreAsync(project, parent, startedAt, cameraPlacements, automaticZoom, composeFinal);

    internal static async Task<SavedRecording> PrepareCoreAsync(ProjectDocument project, string parent, DateTimeOffset startedAt,
        RecordedCameraPlacement[]? cameraPlacements = null, bool automaticZoom = true, bool composeFinal = true,
        Func<string, string, bool>? extractAudio = null)
    {
        project = ProjectStore.Open(project.PackagePath);
        ValidatePackageForMove(project.PackagePath);
        string codec = ReadRecordingCodec(project);
        parent = Path.GetFullPath(parent); MediaExport.RejectLinks(parent);
        Directory.CreateDirectory(parent);
        MediaExport.RejectLinks(parent);
        string name = startedAt.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        string folder = Path.Combine(parent, name);
        MediaExport.RejectLinks(folder);
        if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException($"这一秒的录制目录已存在，原文件未覆盖：{folder}");
        string staging = Path.Combine(parent, $".dogsc-save-{Guid.NewGuid():N}");
        if (IsWithin(staging, project.PackagePath)) throw new IOException("成片保存目录不能位于独立素材项目内。");
        MediaExport.RejectLinks(staging);
        Directory.CreateDirectory(staging);
        string video = Path.Combine(staging, name + ".mp4"), audio = Path.Combine(staging, name + ".m4a");
        string retainedProject = Path.GetFullPath(Path.Combine(staging, "素材.dogscproject"));
        bool movedProject = false;
        bool preserveStaging = false;
        var warnings = new List<string>();
        try
        {
            bool hasCamera = new[] { "camera.mp4", "camera.mov" }.Any(name => File.Exists(Path.Combine(project.PackagePath, "media", name)));
            var firstPlacement = cameraPlacements?.FirstOrDefault();
            var scene = EditorState.Initial(project.DurationSeconds) with
            {
                Style = new SceneStyle { Padding = 0, Corner = 0, Border = 0, Shadow = 0 },
                Camera = new CameraStyle
                {
                    Enabled = hasCamera, Mirror = true, Shape = firstPlacement is not null ? "横向" : "圆形", Corner = 0, Shadow = 0,
                    X = Math.Clamp(firstPlacement?.X ?? .72, 0, 1), Y = Math.Clamp(firstPlacement?.Y ?? .6, 0, 1),
                    Width = Math.Clamp(firstPlacement?.Width ?? .22, .01, 1), Height = Math.Clamp(firstPlacement?.Height ?? .3, .01, 1)
                },
                CameraPlacements = cameraPlacements ?? [],
                Pointer = new PointerAppearance(Hidden: false, ClickFeedback: "关闭")
            };
            scene = SceneCompositor.LoadRecordingData(project, scene);
            if (automaticZoom && composeFinal) scene = SceneCompositor.WithAutomaticZooms(scene);
            scene.Validate(project.DurationSeconds); scene.ValidateAssets();
            EditorWindow.AtomicJson(Path.Combine(project.PackagePath, "editor.json"), scene);
            if (!hasCamera && scene.ZoomClips.Length == 0 && scene.EmbeddedCursor)
                await MainWindow.ExportOriginalMp4Async(project, video, false, CancellationToken.None);
            else await MediaExport.ExportAsync(project, scene, video, new ExportSettings(project.Capture.FrameRate, 0, Codec: codec));
            preserveStaging = true;
            var optionalAudio = await PrepareOptionalAudioAsync(video, audio, extractAudio);
            bool hasAudio = optionalAudio.HasAudio;
            if (optionalAudio.Warning is not null) warnings.Add(optionalAudio.Warning);
            using (var file = new FileStream(video, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) file.Flush(true);
            MediaExport.RejectLinks(staging); MediaExport.RejectLinks(folder);
            ValidatePackageForMove(project.PackagePath);
            if (!IsWithin(retainedProject, staging) || !IsWithin(staging, parent)) throw new IOException("独立素材保存路径不能离开录制目录。");
            MediaExport.RejectLinks(retainedProject);
            Directory.Move(project.PackagePath, retainedProject);
            movedProject = true;
            MediaExport.RejectLinks(staging); MediaExport.RejectLinks(folder);
            Directory.Move(staging, folder);
            movedProject = false;
            return new(folder, Path.Combine(folder, name + ".mp4"), hasAudio ? Path.Combine(folder, name + ".m4a") : null,
                Path.Combine(folder, "素材.dogscproject")) { Warnings = warnings.ToArray() };
        }
        catch (TimeoutException error)
        {
            preserveStaging = true;
            throw new TimeoutException($"媒体保存超时，未移动或删除素材。原片位置：{project.PackagePath}；暂存目录：{staging}。未确认媒体线程退出前请勿移动这些目录。{error.Message}", error);
        }
        catch (FilePublicationException) { preserveStaging = true; throw; }
        catch (Exception error) when (movedProject)
        {
            try
            {
                ValidatePackageForMove(retainedProject); MediaExport.RejectLinks(project.PackagePath);
                Directory.Move(retainedProject, project.PackagePath);
                movedProject = false;
            }
            catch (Exception rollbackError)
            {
                throw new IOException($"保存目录发布失败，独立素材已保留在：{retainedProject}。恢复原位置失败：{rollbackError.Message}", error);
            }
            throw new IOException($"保存目录发布失败；独立素材保留在：{project.PackagePath}，已经完成的视频保留在：{staging}。{error.Message}", error);
        }
        catch (Exception error) when (preserveStaging)
        {
            throw new IOException($"保存未完成；独立素材保留在：{project.PackagePath}，已经完成的视频保留在：{staging}。{error.Message}", error);
        }
        finally
        {
            // Only our two known staging files are removed; an unexpected entry prevents directory deletion.
            if (Directory.Exists(staging) && !movedProject && !preserveStaging)
            {
                MediaExport.RejectLinks(staging); MediaExport.RejectLinks(video); MediaExport.RejectLinks(audio);
                File.Delete(video); File.Delete(audio);
                if (!Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging, false);
            }
        }
    }

    internal static async Task<(bool HasAudio, string? Warning)> PrepareOptionalAudioAsync(string video, string audio, Func<string, string, bool>? extract = null)
    {
        bool present = false;
        var work = MediaExport.RunWorkerAsync((progress, token) =>
        {
            token.ThrowIfCancellationRequested();
            present = extract is null ? ExtractAudio(video, audio, progress) : extract(video, audio);
            if (present) using (var file = new FileStream(audio, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) file.Flush(true);
            progress.Report(100);
        }, "DogSC extract optional audio");
        try
        {
            await work;
            if (!present) File.Delete(audio);
            return (present, null);
        }
        catch (Exception error) when (!MediaExport.IsStalled)
        {
            File.Delete(audio);
            return (false, $"纯音频M4A未生成，MP4及独立素材已保留：{error.Message}");
        }
        catch (TimeoutException error)
        {
            _ = work.ContinueWith(done => _ = done.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException($"抽取音频超时，未移动或删除素材；完整MP4保留在：{video}。请退出后再处理此目录。", error);
        }
    }

    private static string ReadRecordingCodec(ProjectDocument project)
    {
        string path = Path.Combine(project.PackagePath, "capture-options.json");
        MediaExport.RejectLinks(path);
        if (!File.Exists(path)) return "H264";
        using var document = MediaSynchronization.ReadJson(path);
        var options = document.RootElement;
        if (options.GetProperty("format").GetString() != "dogsc.windows.capture-options" || options.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("录制配置版本无效，独立原片已保留。");
        string? codec = options.TryGetProperty("codec", out var selected) ? selected.GetString() : "H264";
        if (codec is not ("H264" or "HEVC")) throw new InvalidDataException("录制编码仅支持H264或HEVC，未替换编码，独立原片已保留。");
        return codec;
    }

    private static bool IsWithin(string child, string parent) => Path.GetFullPath(child).StartsWith(
        Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    private static void ValidatePackageForMove(string package)
    {
        package = Path.GetFullPath(package); MediaExport.RejectLinks(package);
        var pending = new Stack<string>(); pending.Push(package);
        int entries = 0;
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++entries > 100_000 || !IsWithin(entry, package)) throw new IOException("独立素材目录数量或路径无效，原片已保留。");
                MediaExport.RejectLinks(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
    }

    private static bool ExtractAudio(string video, string destination, IProgress<double> progress)
    {
        // Match the media worker's STA apartment instead of trying to switch it to MTA.
        int com = CoInitializeEx(IntPtr.Zero, 2);
        Marshal.ThrowExceptionForHR(com);
        IntPtr reader = IntPtr.Zero, writer = IntPtr.Zero;
        bool mediaStarted = false;
        try
        {
            Mf.Check(Mf.MFStartup(0x20070, 0)); mediaStarted = true;
            Mf.Check(Mf.MFCreateSourceReaderFromURL(video, IntPtr.Zero, out reader));
            int result = Mf.Call<Mf.GetCurrentType>(reader, 6)(reader, -3, out var native);
            using var format = new Mf.Object(native);
            if (result == unchecked((int)0xC00D36B3)) return false;
            Mf.Check(result);
            Mf.Check(Mf.Call<Mf.GetGuid>(native, 10)(native, in Mf.Subtype, out var subtype));
            if (subtype != Mf.Aac) throw new InvalidDataException("原视频声音不是AAC，未清理原项目。");
            Mf.Check(Mf.Call<Mf.SelectStream>(reader, 4)(reader, -2, false));
            Mf.Check(Mf.Call<Mf.SelectStream>(reader, 4)(reader, -3, true));
            Mf.Check(Mf.MFCreateSinkWriterFromURL(destination, IntPtr.Zero, IntPtr.Zero, out writer));
            Mf.Check(Mf.Call<Mf.AddStream>(writer, 3)(writer, native, out var stream));
            Mf.Check(Mf.Call<Mf.SetInput>(writer, 4)(writer, stream, native, IntPtr.Zero));
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 5)(writer));
            int samples = 0, reads = 0;
            while (true)
            {
                if (reads++ >= 10_000_000) throw new InvalidDataException("音频样本数超过1000万限制。");
                Mf.Check(Mf.Call<Mf.ReadSample>(reader, 9)(reader, -3, 0, out _, out var flags, out _, out var sample));
                using var item = new Mf.Object(sample);
                if (sample != IntPtr.Zero) { Mf.Check(Mf.Call<Mf.WriteSample>(writer, 6)(writer, stream, sample)); samples++; }
                if (reads % 100 == 0) progress.Report(reads);
                if ((flags & 2) != 0) break;
            }
            if (samples == 0) return false;
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 11)(writer));
            return true;
        }
        finally
        { if (reader != IntPtr.Zero) Marshal.Release(reader); if (writer != IntPtr.Zero) Marshal.Release(writer); if (mediaStarted) Mf.MFShutdown(); CoUninitialize(); }
    }

    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
}
