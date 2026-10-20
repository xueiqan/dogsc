using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DogSC.Core;

public sealed record CaptureRegion(int X, int Y, int Width, int Height)
{
    public void Validate(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth < 2 || sourceHeight < 2 || X < 0 || Y < 0 || Width < 2 || Height < 2
            || (long)X + Width > sourceWidth || (long)Y + Height > sourceHeight)
            throw new ArgumentOutOfRangeException(nameof(CaptureRegion), "录制区域必须至少为2×2像素，并完整位于录制源内。");
    }
}

public sealed record CaptureMetadata(string Kind, string TargetName, int FrameRate,
    bool SystemAudio, bool Microphone, CaptureRegion? Region);

public sealed record ProjectDocument(string PackagePath, string Title, DateTimeOffset CreatedAt,
    CaptureMetadata Capture, double DurationSeconds, string State, string MediaPath);

public static class ProjectStore
{
    private const string Format = "dogsc.windows";
    private const int Version = 1;
    private const int MaxManifestBytes = 1_048_576;
    private const string MediaRelativePath = "media/recording.mp4";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16,
        WriteIndented = true
    };

    private sealed record Manifest
    {
        public required string Format { get; init; }
        public required int Version { get; init; }
        public required string Title { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required CaptureMetadata Capture { get; init; }
        public required double DurationSeconds { get; init; }
        public required string State { get; init; }
        public required string MediaPath { get; init; }
    }

    public static ProjectDocument Create(string parentDirectory, string title, CaptureMetadata capture)
    {
        ValidateMetadata(title, capture);
        var parent = CheckedPath(parentDirectory);
        Directory.CreateDirectory(parent);
        CheckedPath(parent);
        var name = new string(title.Trim().Take(60).Select(c =>
            char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        name = name.Trim(' ', '.');
        if (name.Length == 0) name = "录制";
        var package = Path.Combine(parent, $"{name}-{Guid.NewGuid():N}.dogscproject");
        Directory.CreateDirectory(Path.Combine(package, "media"));
        var document = new ProjectDocument(package, title.Trim(), DateTimeOffset.UtcNow,
            capture with { Kind = capture.Kind.ToLowerInvariant() }, 0, "recording",
            Path.Combine(package, "media", "recording.mp4"));
        WriteManifest(document);
        return document;
    }

    public static ProjectDocument Open(string packagePath)
    {
        var document = ReadManifest(packagePath);
        if (document.State != "complete")
            throw new InvalidDataException("这次录制尚未完成，素材已原地保留；请保留项目目录以便恢复。");
        ValidateMedia(document.MediaPath);
        return document;
    }

    public static void DeleteCompletedRecordingPackage(ProjectDocument project)
    {
        var current = Open(project.PackagePath);
        var package = CheckedPath(current.PackagePath);
        if (!package.EndsWith(".dogscproject", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.MediaPath, project.MediaPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("只允许清理已确认完成的录制项目。");
        var entries = LegacyProjectCodec.EnumeratePackageEntries(package);
        foreach (var entry in entries.OrderByDescending(path => path.Length))
        {
            CheckedPath(entry);
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: false);
            else File.Delete(entry);
        }
        Directory.Delete(package, recursive: false);
    }

    public static ProjectDocument Inspect(string packagePath) => ReadManifest(packagePath);

    // Keep interrupted originals intact; recover the screen prefix and preserve all auxiliary files.
    public static ProjectDocument CreateRecoveryCopy(string packagePath, string parentDirectory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var original = ReadManifest(packagePath);
        if (original.State != "recording") throw new InvalidOperationException("只有未完成的录制需要恢复。");
        var parent = CheckedPath(parentDirectory);
        if (string.Equals(parent, original.PackagePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || IsWithin(parent, original.PackagePath))
            throw new InvalidDataException("恢复副本不能创建在原项目包内。");
        var entries = LegacyProjectCodec.EnumeratePackageEntries(original.PackagePath, token);
        var files = entries.Where(path => !Directory.Exists(path)).Select(path => (Path: path, Length: new FileInfo(path).Length)).ToArray();
        using var source = new FileStream(original.MediaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = source.Length;
        long playableEnd = 0;
        bool movie = false, media = false;
        Span<byte> header = stackalloc byte[16];
        for (var boxes = 0; boxes < 65_536 && source.Position + 8 <= length; boxes++)
        {
            token.ThrowIfCancellationRequested();
            long start = source.Position;
            source.ReadExactly(header[..8]);
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            int headerLength = size == 1 ? 16 : 8;
            if (headerLength == 16)
            {
                if (source.Position + 8 > length) break;
                source.ReadExactly(header[8..]); size = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            }
            if (size == 0) size = (ulong)(length - start);
            if (size < (ulong)headerLength || size > (ulong)(length - start)) break;
            if (start == 0 && !header[4..8].SequenceEqual("ftyp"u8)) break;
            if (header[4..8].SequenceEqual("moov"u8)) movie = true;
            if (header[4..8].SequenceEqual("mdat"u8) && size > (ulong)headerLength) media = true;
            source.Position = start + (long)size;
            if (movie && media && (header[4..8].SequenceEqual("moov"u8) || header[4..8].SequenceEqual("mdat"u8))) playableEnd = source.Position;
        }
        if (playableEnd == 0) throw new InvalidDataException("没有可恢复的完整电影索引与媒体分片；原素材保留，不会伪造时长。");
        var recovered = Create(parent, original.Title[..Math.Min(190, original.Title.Length)] + "（恢复副本）", original.Capture);
        source.Position = 0;
        var buffer = new byte[131_072];
        try
        {
            using (var output = new FileStream(recovered.MediaPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Copy(source, output, playableEnd);
            foreach (var directory in entries.Where(Directory.Exists))
                Directory.CreateDirectory(Destination(directory));
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(original.PackagePath, file.Path);
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (string.Equals(file.Path, original.MediaPath, comparison) || string.Equals(relative, "project.json", comparison)) continue;
                using var input = new FileStream(CheckedPath(file.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != file.Length) throw new IOException("恢复期间辅助素材长度改变。");
                using var output = new FileStream(Destination(file.Path), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                Copy(input, output, file.Length);
                if (relative.Replace('\\', '/') is "media/system.wav" or "media/microphone.wav" && WaveRecovery.RepairCapturedWave(output)) output.Flush(true);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            throw new IOException($"恢复尚未完成，原包保持不变；不完整副本保留在：{recovered.PackagePath}", error);
        }
        return recovered;

        string Destination(string path)
        {
            var destination = CheckedPath(Path.Combine(recovered.PackagePath, Path.GetRelativePath(original.PackagePath, path)));
            if (!IsWithin(destination, recovered.PackagePath)) throw new InvalidDataException("恢复目标路径无效。");
            return destination;
        }
        void Copy(FileStream input, FileStream output, long remaining)
        {
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) throw new IOException("恢复期间素材长度改变。");
                output.Write(buffer, 0, count); remaining -= count;
            }
            output.Flush(true);
        }
    }

    public static ProjectDocument Complete(ProjectDocument project, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(project);
        var document = ReadManifest(project.PackagePath);
        if (!double.IsFinite(duration.TotalSeconds) || duration.TotalSeconds <= 0
            || duration.TotalDays > 7)
            throw new ArgumentOutOfRangeException(nameof(duration), "录制时长必须大于0且不超过7天。");
        ValidateMedia(document.MediaPath);
        document = document with { DurationSeconds = duration.TotalSeconds, State = "complete" };
        WriteManifest(document);
        return document;
    }

    public static async Task ExportAsync(ProjectDocument project, string outputPath, bool overwrite,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        token.ThrowIfCancellationRequested();
        var current = Open(project.PackagePath);
        var output = CheckedPath(outputPath);
        if (!string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("导出文件必须使用.mp4扩展名。", nameof(outputPath));
        if (IsWithin(output, current.PackagePath))
            throw new IOException("导出位置不能位于项目包内，以免覆盖录制素材或项目数据。");
        var folder = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("导出目录不存在，请重新选择目录。");
        if (File.Exists(output) && !overwrite)
            throw new IOException("导出目标已存在；请另选文件名或明确确认覆盖。");
        var temporary = Path.Combine(folder, $".dogsc-export-{Guid.NewGuid():N}.tmp");
        bool publicationAttempted = false;
        try
        {
            await using (var source = new FileStream(current.MediaPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 131_072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 131_072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, token);
                await destination.FlushAsync(token);
                destination.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            CheckedPath(output);
            publicationAttempted = true;
            SafeFilePublication.Publish(temporary, output, overwrite);
        }
        finally
        {
            if (!publicationAttempted) File.Delete(temporary);
        }
    }

    private static ProjectDocument ReadManifest(string packagePath)
    {
        var package = CheckedPath(packagePath);
        if (!Directory.Exists(package) || !package.EndsWith(".dogscproject", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择Windows版DogSC的.dogscproject项目目录。");
        var manifestPath = CheckedPath(Path.Combine(package, "project.json"));
        using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaxManifestBytes)
            throw new InvalidDataException("项目清单为空或超过1MiB，已停止打开。");
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        Manifest manifest;
        try
        {
            using var json = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(json.RootElement);
            if (!json.RootElement.TryGetProperty("format", out var format)
                || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
                throw new InvalidDataException("该项目不是Windows版DogSC项目；首版不支持macOS编辑项目。");
            manifest = JsonSerializer.Deserialize<Manifest>(data, JsonOptions)
                ?? throw new InvalidDataException("项目清单无效。");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("项目清单的字段、类型或JSON结构无效。", error);
        }
        if (manifest.Version != Version)
            throw new InvalidDataException($"不支持Windows项目版本{manifest.Version}；当前支持版本为{Version}，已停止打开以保护项目。");
        ValidateMetadata(manifest.Title, manifest.Capture);
        if (manifest.CreatedAt == default || !double.IsFinite(manifest.DurationSeconds)
            || manifest.DurationSeconds < 0 || manifest.DurationSeconds > 604_800
            || manifest.State is not ("recording" or "complete")
            || (manifest.State == "complete" && manifest.DurationSeconds <= 0))
            throw new InvalidDataException("项目日期、时长或录制状态无效。");
        if (manifest.MediaPath != MediaRelativePath)
            throw new InvalidDataException("项目素材路径无效；Windows首版仅接受包内media/recording.mp4。");
        var mediaPath = CheckedPath(Path.Combine(package, "media", "recording.mp4"));
        if (!IsWithin(mediaPath, package))
            throw new InvalidDataException("项目素材路径不能离开项目目录。");
        return new ProjectDocument(package, manifest.Title, manifest.CreatedAt, manifest.Capture,
            manifest.DurationSeconds, manifest.State, mediaPath);
    }

    private static void ValidateMetadata(string title, CaptureMetadata capture)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new InvalidDataException("项目标题不能为空或超过200个字符。");
        if (capture is null || string.IsNullOrWhiteSpace(capture.TargetName)
            || capture.TargetName.Length > 512 || capture.FrameRate is < 1 or > 120)
            throw new InvalidDataException("录制源名称或帧率无效；帧率范围为1至120。");
        var kind = capture.Kind?.ToLowerInvariant();
        if (kind is not ("screen" or "window" or "region")
            || (kind == "region") != (capture.Region is not null))
            throw new InvalidDataException("录制源类型必须为screen、window或region，且只有区域录制提供Region。");
        capture.Region?.Validate(int.MaxValue, int.MaxValue);
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("项目清单中的对象结构无效。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new InvalidDataException($"项目清单包含重复字段：{property.Name}。");
            if (property.Value.ValueKind == JsonValueKind.Object)
                RejectDuplicateProperties(property.Value);
        }
    }

    private static void ValidateMedia(string mediaPath)
    {
        CheckedPath(mediaPath);
        using var stream = OpenMediaStream(mediaPath);
        Span<byte> header = stackalloc byte[16];
        var length = stream.Length;
        var hasMovie = false;
        var hasMedia = false;
        var boxCount = 0;
        while (stream.Position < length)
        {
            var position = stream.Position;
            var remaining = (ulong)(length - position);
            // Normal recorder output has few boxes; cap parsing work for untrusted files.
            if (++boxCount > 65_536 || remaining < 8)
                throw new InvalidDataException("录制MP4的容器结构截断或过于复杂，素材仍保留在项目内。");
            stream.ReadExactly(header[..8]);
            var shortSize = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerSize = shortSize == 1 ? 16UL : 8UL;
            ulong size = shortSize == 0 ? remaining : shortSize;
            if (shortSize == 1)
            {
                if (remaining < headerSize)
                    throw new InvalidDataException("录制MP4的64位容器头尚未完整写入，素材仍保留在项目内。");
                stream.ReadExactly(header[8..16]);
                size = BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
            }
            if (size < headerSize || size > remaining)
                throw new InvalidDataException("录制MP4的容器长度无效或尚未完整写入，素材仍保留在项目内。");
            var type = header[4..8];
            if (position == 0 && (!type.SequenceEqual("ftyp"u8) || size - headerSize < 8))
                throw new InvalidDataException("录制文件的MP4基础头无效，素材仍保留在项目内。");
            if (position == 0)
            {
                stream.ReadExactly(header[..4]);
                if (header[..4].SequenceEqual("qt  "u8))
                    throw new InvalidDataException("录制文件实际为QuickTime MOV，不能仅改名为MP4；请通过导入换封装，原素材已保留。");
            }
            if (type.SequenceEqual("moov"u8))
            {
                if (size - headerSize < 8)
                    throw new InvalidDataException("录制MP4的电影索引为空，素材仍保留在项目内。");
                hasMovie = true;
            }
            if (type.SequenceEqual("mdat"u8) && size > headerSize)
                hasMedia = true;
            stream.Position = position + (long)size;
        }
        if (!hasMovie || !hasMedia)
            throw new InvalidDataException("录制MP4缺少完整电影索引或视频数据，素材仍保留在项目内。");
        // ponytail: Box bounds are checked, not track tables or decoding; validate imported media with a decoder.
    }

    private static FileStream OpenMediaStream(string mediaPath)
    {
        try { return new FileStream(mediaPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidDataException($"项目录制素材缺失，请恢复包内media/recording.mp4后再打开。素材位置：{mediaPath}", error);
        }
    }

    private static void WriteManifest(ProjectDocument project)
    {
        var package = CheckedPath(project.PackagePath);
        var path = CheckedPath(Path.Combine(package, "project.json"));
        var temporary = Path.Combine(package, $".project-save-{Guid.NewGuid():N}.tmp");
        var manifest = new Manifest
        {
            Format = Format, Version = Version, Title = project.Title, CreatedAt = project.CreatedAt,
            Capture = project.Capture, DurationSeconds = project.DurationSeconds, State = project.State,
            MediaPath = MediaRelativePath
        };
        var data = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        bool publicationAttempted = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            CheckedPath(path);
            publicationAttempted = true;
            SafeFilePublication.Publish(temporary, path);
        }
        finally
        {
            if (!publicationAttempted) File.Delete(temporary);
        }
    }

    private static string CheckedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        for (string? current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("项目和导出路径不能包含符号链接或重解析点，请使用实际目录。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return fullPath;
    }

    private static bool IsWithin(string candidate, string directory) => candidate.StartsWith(
        Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
