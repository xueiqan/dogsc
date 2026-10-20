using System.Globalization;
using System.Text.Json;

namespace DogSC.Core;

public sealed record LegacyMediaReference(string Role, string RelativePath, string FullPath,
    bool Exists, double StartOffset, double SourceStartTime, double? SourceTimeScale,
    double? SourceEndTime, JsonElement SyncAnchors);

/// <summary>Read-only macOS contract. Original bytes and unknown fields are never rewritten.</summary>
public sealed class LegacyProjectDocument
{
    internal readonly byte[] OriginalBytes;
    internal LegacyProjectDocument(string packagePath, byte[] bytes, JsonElement root,
        IReadOnlyList<LegacyMediaReference> references, IReadOnlyList<string> externalReferences)
    {
        PackagePath = packagePath;
        OriginalBytes = bytes;
        Root = root;
        MediaReferences = references;
        ExternalReferences = externalReferences;
    }

    public string PackagePath { get; }
    public JsonElement Root { get; }
    public int Version => Root.GetProperty("version").GetInt32();
    public string Title => Root.TryGetProperty("title", out var title) ? title.GetString()! : "未命名录制";
    public IReadOnlyList<LegacyMediaReference> MediaReferences { get; }
    public IEnumerable<LegacyMediaReference> MissingReferences => MediaReferences.Where(reference => !reference.Exists);
    // System wallpapers belong to the original machine; loading a project grants no external-file access.
    public IReadOnlyList<string> ExternalReferences { get; }
    public JsonElement Section(string name) => Root.TryGetProperty(name, out var value) ? value : default;
    public byte[] GetOriginalJson() => (byte[])OriginalBytes.Clone();
    public SourceSequence GetSourceSequence()
    {
        var timeline = Section("timeline");
        if (!timeline.TryGetProperty("sourceSequence", out var sequence)
            || sequence.GetProperty("kind").GetString() == "fullRecording") return new SourceSequence();
        return new SourceSequence(sequence.GetProperty("segments").EnumerateArray().Select(segment =>
            new RecordingSegment(Guid.Parse(segment.GetProperty("id").GetString()!),
                segment.GetProperty("sourceStart").GetDouble(), segment.GetProperty("sourceDuration").GetDouble(),
                segment.TryGetProperty("playbackRate", out var rate) && rate.ValueKind != JsonValueKind.Null
                    ? rate.GetDouble() : 1)).ToArray());
    }
}

public static class LegacyProjectCodec
{
    public const int MinimumVersion = 23;
    public const int CurrentVersion = 27;
    private const int MaxManifestBytes = 8 * 1024 * 1024;
    private const int MaxItems = 100_000;

    public static LegacyProjectDocument Open(string packagePath)
    {
        var package = CheckedPath(packagePath);
        if (!Directory.Exists(package) || !package.EndsWith(".dogscproject", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择.dogscproject项目目录。");
        var manifest = CheckedPath(Path.Combine(package, "project.json"));
        using var file = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaxManifestBytes)
            throw new InvalidDataException("原版项目清单为空或超过8MiB。");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        try
        {
            using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = parsed.RootElement;
            var count = 0;
            ValidateJson(root, ref count);
            RequireObject(root, "项目");
            if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var schema))
                throw new InvalidDataException("原版项目缺少整数version字段。");
            if (schema is < MinimumVersion or > CurrentVersion)
                throw new InvalidDataException($"原版项目版本{schema}不在支持范围23–27；原文件不会被改写。");
            RequireObject(root.GetProperty("timeline"), "timeline");
            RequireObject(root.GetProperty("exportSettings"), "exportSettings");
            foreach (var name in new[] { "capture", "canvas", "camera", "audio", "motion", "openingSequence", "cursorStyle" })
                if (root.TryGetProperty(name, out var section)) RequireObject(section, name);
            if (root.TryGetProperty("title", out var title)) RequireString(title, "title", 4096);
            if (root.TryGetProperty("createdAt", out var date)
                && (!DateTimeOffset.TryParse(RequireString(date, "createdAt", 128), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out _)))
                throw new InvalidDataException("项目createdAt不是ISO8601日期。");
            ValidateExport(root.GetProperty("exportSettings"));
            ValidateSourceSequence(root.GetProperty("timeline"));
            var references = ReadReferences(root, package);
            var external = new List<string>();
            if (root.TryGetProperty("canvas", out var canvas)
                && canvas.TryGetProperty("backgroundSource", out var background))
            {
                RequireObject(background, "backgroundSource");
                var kind = RequireString(background.GetProperty("kind"), "backgroundSource.kind", 64);
                if (kind is "systemImage" or "systemVideo")
                    external.Add(RequireString(background.GetProperty("absolutePath"), "absolutePath", 4096));
            }
            return new LegacyProjectDocument(package, bytes, root.Clone(), references.AsReadOnly(), external.AsReadOnly());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("原版项目JSON的字段、类型或结构无效；原文件未改写。", error);
        }
    }

    /// <summary>Resolve only package-owned paths. Missing files are reported without searching outside the package.</summary>
    public static string ResolvePackagePath(string packagePath, string relativePath)
    {
        var package = CheckedPath(packagePath);
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 2048
            || relativePath.Contains('\\') || relativePath.Contains(':') || relativePath.StartsWith('/')
            || relativePath.Any(char.IsControl))
            throw new InvalidDataException("项目素材必须使用包内相对路径。");
        var segments = relativePath.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(segment))
                throw new InvalidDataException("项目素材路径包含非法文件名或目录穿越。");
        }
        var fullPath = CheckedPath(Path.Combine(package, Path.Combine(segments)));
        if (!IsWithin(fullPath, package)) throw new InvalidDataException("项目素材路径不能离开项目包。");
        return fullPath;
    }

    /// <summary>Copy all package-owned files to a new package; never overwrite the source or an existing target.</summary>
    public static async Task CopyPackageAsync(LegacyProjectDocument project, string destinationPath,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        token.ThrowIfCancellationRequested();
        // Re-read before copying so a replaced manifest or changed reparse point is not silently trusted.
        var current = Open(project.PackagePath);
        if (!current.OriginalBytes.AsSpan().SequenceEqual(project.OriginalBytes))
            throw new IOException("原项目在打开后已改变，请重新打开再复制。");
        var destination = CheckedPath(destinationPath);
        if (!destination.EndsWith(".dogscproject", StringComparison.OrdinalIgnoreCase)
            || IsWithin(destination, project.PackagePath) || IsWithin(project.PackagePath, destination)
            || string.Equals(destination, project.PackagePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("副本必须是原项目包外的新.dogscproject目录。");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("副本目标已存在，不会覆盖。");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("副本父目录不存在。");
        var files = EnumeratePackageEntries(project.PackagePath, token);
        var staged = CheckedPath(Path.Combine(parent, $".dogsc-import-{Guid.NewGuid():N}.dogscproject"));
        Directory.CreateDirectory(staged);
        try
        {
            foreach (var source in files)
            {
                token.ThrowIfCancellationRequested();
                CheckedPath(source);
                var relative = Path.GetRelativePath(project.PackagePath, source).Replace('\\', '/');
                var output = ResolvePackagePath(staged, relative);
                if (Directory.Exists(source)) { Directory.CreateDirectory(output); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                CheckedPath(output);
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    131_072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var writer = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    131_072, FileOptions.Asynchronous);
                if (relative == "project.json") await writer.WriteAsync(project.OriginalBytes, token);
                else await input.CopyToAsync(writer, token);
                await writer.FlushAsync(token);
                writer.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            CheckedPath(destination);
            Directory.Move(staged, destination);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Keep partial bytes recoverable; no recursive deletion of a tree that another process can mutate.
            error.Data["PartialCopyPath"] = staged;
            throw;
        }
    }

    private static List<LegacyMediaReference> ReadReferences(JsonElement root, string package)
    {
        var references = new List<LegacyMediaReference>();
        if (root.TryGetProperty("media", out var media) && media.ValueKind != JsonValueKind.Null)
        {
            RequireObject(media, "media");
            if (!media.TryGetProperty("screen", out var screen) || screen.ValueKind == JsonValueKind.Null)
                throw new InvalidDataException("media缺少screen屏幕素材，无法打开项目。");
            foreach (var role in new[] { "screen", "camera", "microphone", "pointerEvents" })
            {
                if (!media.TryGetProperty(role, out var reference) || reference.ValueKind == JsonValueKind.Null) continue;
                RequireObject(reference, role);
                var anchors = reference.TryGetProperty("syncAnchors", out var value) ? value : default;
                if (anchors.ValueKind != JsonValueKind.Undefined && anchors.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("syncAnchors必须是数组。");
                AddReference(role, reference.GetProperty("relativePath"), reference,
                    anchors.ValueKind == JsonValueKind.Undefined ? default : anchors.Clone());
            }
        }
        if (root.TryGetProperty("canvas", out var canvas)
            && canvas.TryGetProperty("backgroundSource", out var background)
            && background.ValueKind == JsonValueKind.Object
            && background.TryGetProperty("kind", out var kind)
            && kind.ValueKind == JsonValueKind.String && kind.GetString() is "projectImage" or "projectVideo")
            AddReference("background", background.GetProperty("relativePath"), default, default);
        var timeline = root.GetProperty("timeline");
        if (timeline.TryGetProperty("stickerClips", out var stickers))
        {
            if (stickers.ValueKind != JsonValueKind.Array) throw new InvalidDataException("stickerClips必须是数组。");
            foreach (var sticker in stickers.EnumerateArray())
                AddReference("sticker", sticker.GetProperty("relativePath"), default, default);
        }
        return references;

        void AddReference(string role, JsonElement path, JsonElement reference, JsonElement anchors)
        {
            var relative = RequireString(path, "relativePath", 2048);
            var full = ResolvePackagePath(package, relative);
            if (Directory.Exists(full)) throw new InvalidDataException("项目素材引用不能指向目录。");
            references.Add(new LegacyMediaReference(role, relative, full, File.Exists(full),
                ReadNumber(reference, "startOffset", 0)!.Value, ReadNumber(reference, "sourceStartTime", 0)!.Value,
                ReadNumber(reference, "sourceTimeScale", null, positive: true),
                ReadNumber(reference, "sourceEndTime", null), anchors));
        }
    }

    private static void ValidateSourceSequence(JsonElement timeline)
    {
        if (!timeline.TryGetProperty("sourceSequence", out var sequence)) return;
        RequireObject(sequence, "sourceSequence");
        var kind = RequireString(sequence.GetProperty("kind"), "sourceSequence.kind", 64);
        if (kind == "fullRecording") return;
        if (kind != "edited") throw new InvalidDataException("未知sourceSequence.kind。");
        var segments = sequence.GetProperty("segments");
        if (segments.ValueKind != JsonValueKind.Array) throw new InvalidDataException("segments必须是数组。");
        var ids = new HashSet<Guid>();
        foreach (var segment in segments.EnumerateArray())
        {
            if (!Guid.TryParse(RequireString(segment.GetProperty("id"), "segment.id", 40), out var id) || !ids.Add(id))
                throw new InvalidDataException("剪辑片段ID无效或重复。");
            ReadNumber(segment, "sourceStart", null, required: true);
            ReadNumber(segment, "sourceDuration", null, positive: true, required: true);
            var rate = ReadNumber(segment, "playbackRate", 1, positive: true)!.Value;
            if (rate is < 1 or > 100) throw new InvalidDataException("剪辑速率必须在1至100之间。");
        }
    }

    private static void ValidateExport(JsonElement settings)
    {
        if (!settings.GetProperty("frameRate").TryGetInt32(out var fps) || fps is not (30 or 60 or 90 or 120))
            throw new InvalidDataException("原版exportSettings.frameRate无效。");
        if (RequireString(settings.GetProperty("resolution"), "resolution", 64) is not ("原始素材" or "1080p" or "1440p" or "4K"))
            throw new InvalidDataException("原版exportSettings.resolution无效。");
    }

    private static double? ReadNumber(JsonElement owner, string name, double? fallback, bool positive = false, bool required = false)
    {
        if (owner.ValueKind == JsonValueKind.Undefined || !owner.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new InvalidDataException($"缺少{name}。");
            return fallback;
        }
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0 || (positive && number <= 0))
            throw new InvalidDataException($"{name}必须为有效非负数。");
        return number;
    }

    private static void ValidateJson(JsonElement value, ref int count)
    {
        if (++count > MaxItems) throw new InvalidDataException("项目JSON节点数量超过100000。");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"项目JSON存在重复字段：{property.Name}。");
                ValidateJson(property.Value, ref count);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateJson(item, ref count);
        else if (value.ValueKind == JsonValueKind.String)
            _ = value.GetString(); // Decode escaped strings too; reject malformed Unicode instead of repairing it.
    }

    private static void RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{name}必须是对象。");
    }

    private static string RequireString(JsonElement value, string name, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maximum)
            throw new InvalidDataException($"{name}必须是长度不超过{maximum}的字符串。");
        return text;
    }

    private static bool IsDeviceName(string segment)
    {
        var basename = segment.Split('.')[0].ToUpperInvariant();
        return basename is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (basename.Length == 4 && (basename.StartsWith("COM") || basename.StartsWith("LPT"))
                && (basename[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
    }

    private static bool IsWithin(string path, string directory) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string CheckedPath(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("项目路径不能包含链接或重解析点。");
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
        return full;
    }

    internal static List<string> EnumeratePackageEntries(string package, CancellationToken token = default)
    {
        var files = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((package, 0));
        long bytes = 0;
        var entries = 0;
        while (pending.TryPop(out var entry))
        {
            token.ThrowIfCancellationRequested();
            if (entry.Depth > 32) throw new IOException("项目目录深度超过32层。");
            foreach (var child in Directory.EnumerateFileSystemEntries(entry.Path))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 4096) throw new IOException("项目文件与目录超过4096项。");
                CheckedPath(child);
                ResolvePackagePath(package, Path.GetRelativePath(package, child).Replace('\\', '/'));
                if (Directory.Exists(child)) pending.Push((child, entry.Depth + 1));
                else
                {
                    bytes = checked(bytes + new FileInfo(child).Length);
                    if (bytes > 128L * 1024 * 1024 * 1024) throw new IOException("项目副本超过128GiB，请分离归档素材。");
                }
                files.Add(child);
            }
        }
        return files;
    }
}
