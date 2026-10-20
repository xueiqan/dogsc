using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DogSC.Core;

namespace DogSC.Windows;

public sealed record LegacyImportResult(ProjectDocument Project, EditorState State,
    string[] UnsupportedFeatures, string PreservedOriginalPath);

/// <summary>Import a copy. The preserved Mac manifest is never serialized through Windows models.</summary>
public static class LegacyProjectImport
{
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static async Task<LegacyImportResult> ImportAsync(string sourcePackage, string parentDirectory,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var original = LegacyProjectCodec.Open(sourcePackage);
        var missing = original.MissingReferences.Select(r => r.RelativePath).ToArray();
        if (missing.Length > 0) throw new FileNotFoundException("原项目缺少素材，导入已停止：" + string.Join("、", missing));
        var screen = original.MediaReferences.SingleOrDefault(r => r.Role == "screen")
            ?? throw new InvalidDataException("原项目没有screen素材，无法创建可编辑视频。");
        if (screen.StartOffset != 0 || screen.SourceStartTime != 0 || screen.SourceTimeScale is { } rate && rate != 1
            || screen.SyncAnchors.ValueKind == JsonValueKind.Array && screen.SyncAnchors.GetArrayLength() > 0)
            throw new InvalidDataException("主屏幕素材含非默认时钟变换，当前无法准确迁移；原包已保留。");
        var parent = Path.GetFullPath(parentDirectory); MediaExport.RejectLinks(parent);
        if (parent.Equals(original.PackagePath, StringComparison.OrdinalIgnoreCase)
            || parent.StartsWith(original.PackagePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("导入目录不能位于原Mac项目包内。");
        var fps = (int)Number(original.Section("exportSettings"), "frameRate", 60);
        var title = original.Title;
        if (title.Length > 190) title = title[..190];
        bool embeddedAudio = HasEmbeddedAudio(screen.FullPath);
        var project = ProjectStore.Create(parent, title + "（Windows副本）", new("screen", "原Mac项目屏幕素材", fps, embeddedAudio,
            original.MediaReferences.Any(r => r.Role == "microphone"), null));
        try
        {
            var legacyFolder = Path.Combine(project.PackagePath, "legacy"); Directory.CreateDirectory(legacyFolder);
            var preserved = Path.Combine(legacyFolder, "original.dogscproject");
            var copyTimer = System.Diagnostics.Stopwatch.StartNew();
            await LegacyProjectCodec.CopyPackageAsync(original, preserved, token);
            var clone = LegacyProjectCodec.Open(preserved);
            var cloneScreen = clone.MediaReferences.Single(r => r.Role == "screen");
            if (IsQuickTime(cloneScreen.FullPath))
                await MediaExport.RunWorkerAsync((progress, cancellation) => RemuxScreen(cloneScreen.FullPath, project.MediaPath, embeddedAudio, progress, cancellation),
                    "DogSC Mac screen container import", token: token);
            else await CopyNew(cloneScreen.FullPath, project.MediaPath, token);
            copyTimer.Stop();
            double duration;
            Mf.Check(Mf.MFStartup(0x20070, 0));
            try
            {
                using var reader = new MediaFrameReader(project.MediaPath);
                duration = reader.Duration; _ = reader.FrameAt(0);
            }
            finally { Mf.MFShutdown(); }
            if (screen.SourceEndTime is { } end && end < duration - TimelineMap.Epsilon)
                throw new InvalidDataException("主屏幕素材有额外sourceEndTime边界，当前不应默默丢弃此时钟约束。");
            var unsupported = new List<string>();
            var map = new TimelineMap(clone.GetSourceSequence(), duration);
            var style = await Canvas(clone, project.PackagePath, unsupported, token);
            var zoom = Zoom(clone.Section("timeline"), unsupported);
            var pointer = Pointer(clone, duration, unsupported);
            LegacyCursorAssets.Import(clone, project.PackagePath, pointer.Select(e => e.CursorAssetId), unsupported, token);
            var motion = SceneMotionTimeline.ReadLegacy(clone.Section("timeline"), unsupported);
            await ImportAuxiliaryMedia(clone, project, duration, embeddedAudio, token);
            var state = new EditorState(1, map.MaterializedSourceSequence().EditedSegments!, style, [])
            { ZoomClips = zoom, ScreenMotionClips = motion.Screen, CameraMotionClips = motion.Camera,
                Pointer = PointerStyle(clone, unsupported), Camera = Camera(clone), Audio = Audio(clone), Opening = Opening(clone), Motion = Motion(clone),
                AssetRoot = project.PackagePath, SourceDuration = duration,
                RecordingMarkers = RecordingMarkerDomain.ReadLegacy(clone.Section("recordingMarkers")) };
            foreach (var property in clone.Section("timeline").EnumerateObject())
                if (property.Name is not ("sourceSequence" or "zoomClips" or "screenMotionClips" or "cameraMotionClips") && HasContent(property.Value))
                    unsupported.Add("timeline." + property.Name + "：原轨道保留，尚未映射到Windows合成器。");
            foreach (var section in clone.Root.EnumerateObject())
                if (section.Name is not ("version" or "title" or "createdAt" or "timeline" or "exportSettings" or "capture" or "canvas" or "camera" or "audio" or "motion" or "openingSequence" or "cursorStyle" or "media" or "recordingMarkers"))
                    unsupported.Add(section.Name + "：未知扩展字段已原样保留，不参与Windows渲染。");
            foreach (var external in clone.ExternalReferences)
                unsupported.Add("外部背景素材未读取：" + external);
            state.Validate(duration); state.ValidateAssets();
            var eventsFolder = Path.Combine(project.PackagePath, "events"); Directory.CreateDirectory(eventsFolder);
            using (var writer = new StreamWriter(new FileStream(Path.Combine(eventsFolder, "pointer.jsonl"), FileMode.CreateNew)))
                foreach (var e in pointer) writer.WriteLine(JsonSerializer.Serialize(new { time = e.Time, location = new { x = e.Location.X, y = e.Location.Y },
                    kind = e.Kind switch { PointerEventKind.LeftClick => "leftClick", PointerEventKind.RightClick => "rightClick", _ => "move" }, cursorAssetID = e.CursorAssetId, modifiers = e.Modifiers }));
            WriteNew(Path.Combine(eventsFolder, "recording-events.json"), new { format = "dogsc.windows.pointer", version = 1, embeddedCursor = pointer.Length == 0 });
            WriteNew(Path.Combine(project.PackagePath, "editor.json"), state, new JsonSerializerOptions { WriteIndented = true, MaxDepth = 32 });
            project = ProjectStore.Complete(project, TimeSpan.FromSeconds(duration));
            var result = new LegacyImportResult(project, SceneCompositor.LoadRecordingData(project, state), unsupported.Distinct().ToArray(), preserved);
            WriteNew(Path.Combine(project.PackagePath, "import-report.json"), new { format = "dogsc.windows.legacy-import", version = 1, originalVersion = clone.Version,
                preservedOriginal = "legacy/original.dogscproject", copySeconds = copyTimer.Elapsed.TotalSeconds, result.UnsupportedFeatures,
                evidence = "源时间线与缩放字段按Swift契约映射；没有真实Mac黄金输出，未宣称像素或声音等价。" });
            return result;
        }
        catch (Exception error)
        {
            error.Data["PartialImportPath"] = project.PackagePath;
            throw;
        }
    }

    private static CameraStyle Camera(LegacyProjectDocument project)
    {
        var c = project.Section("camera");
        var position = c.ValueKind == JsonValueKind.Object && c.TryGetProperty("position", out var p) ? Point(p) : new NormalizedPoint(.96, .47);
        var content = c.ValueKind == JsonValueKind.Object && c.TryGetProperty("contentPosition", out var v) ? Point(v) : new NormalizedPoint(.5, .5);
        string shape = Text(c, "shape", "方形") switch { "圆角矩形" => "横向", "连续圆角" => "方形", var value => value };
        return new CameraStyle { Enabled = project.MediaReferences.Any(r => r.Role == "camera"), Shape = shape,
            Mirror = Boolean(c, "isMirrored", true), Hidden = Boolean(c, "isHidden", false), LegacySize = Number(c, "size", .29),
            PositionX = position.X, PositionY = position.Y, Roundness = Number(c, "roundness", .5),
            BorderWidth = Number(c, "borderWidth", 3), ShadowStrength = Number(c, "shadowStrength", .25), ScaleDuringZoom = Number(c, "scaleDuringZoom", .7),
            ContentPositionX = content.X, ContentPositionY = content.Y, ContentScale = Number(c, "contentScale", 1) };
    }
    private static AudioStyle Audio(LegacyProjectDocument project)
    {
        var a = project.Section("audio");
        return new(SystemVolume: Number(a, "systemVolume", .82), MicrophoneVolume: Number(a, "microphoneVolume", .9),
            SystemMuted: Boolean(a, "isSystemMuted", false), MicrophoneMuted: Boolean(a, "isMicrophoneMuted", false));
    }
    private static MotionStyle Motion(LegacyProjectDocument project)
    {
        var m = project.Section("motion");
        var style = new MotionStyle(Number(m, "screenSpringMass", 2.4), Number(m, "screenSpringStiffness", 210), Number(m, "screenSpringDamping", 42),
            Number(m, "cursorSpringMass", 2.8), Number(m, "cursorSpringStiffness", 450), Number(m, "cursorSpringDamping", 66),
            Text(m, "cursor", "平滑") switch { "平滑" => CursorMotion.Smooth, "适中" => CursorMotion.Medium, "快速" => CursorMotion.Rapid, "无" => CursorMotion.None,
                _ => throw new InvalidDataException("未知鼠标运动预设。") }, Number(m, "defaultZoomTransitionDuration", .7));
        style.Validate(); return style;
    }
    private static OpeningSequenceStyle Opening(LegacyProjectDocument project)
    {
        var o = project.Section("openingSequence");
        string[]? Elements(string key)
        {
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(key, out var values) || values.ValueKind == JsonValueKind.Null) return null;
            if (values.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"openingSequence.{key}必须是数组。");
            return values.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String && e.GetString() is { } text
                ? text : throw new InvalidDataException($"openingSequence.{key}的元素必须是字符串。")).Distinct().ToArray();
        }
        double duration = Math.Clamp(Number(o, "duration", 2.2), .4, 8);
        var included = Elements("includedElements");
        double maximum = (included?.Length ?? 3) <= 1 ? 1.2 : Math.Min(1.2, (duration - .18) / ((included?.Length ?? 3) - 1));
        var result = new OpeningSequenceStyle(Boolean(o, "isEnabled", false), Text(o, "preset", "轻 3D"), Text(o, "motionCurve", "swift"),
            duration, Math.Clamp(Number(o, "stagger", .16), 0, maximum), included, Elements("elementOrder"));
        OpeningSequenceTimeline.Validate(result); return result;
    }
    private static async Task ImportAuxiliaryMedia(LegacyProjectDocument original, ProjectDocument project, double duration,
        bool embeddedAudio, CancellationToken token)
    {
        var tracks = new List<object>();
        if (embeddedAudio) tracks.Add(new { role = "system", relativePath = "media/recording.mp4", startOffset = 0.0,
            sourceStartTime = 0.0, sourceTimeScale = 1.0, sourceEndTime = duration, syncAnchors = Array.Empty<object>() });
        foreach (var reference in original.MediaReferences.Where(r => r.Role is "camera" or "microphone"))
        {
            string relative = "media/" + reference.Role + Path.GetExtension(reference.RelativePath).ToLowerInvariant();
            string path = LegacyProjectCodec.ResolvePackagePath(project.PackagePath, relative);
            await CopyNew(reference.FullPath, path, token);
            double mediaDuration;
            Mf.Check(Mf.MFStartup(0x20070, 0));
            try { using var reader = new MfReader(path, reference.Role == "microphone"); mediaDuration = reader.Duration; }
            finally { Mf.MFShutdown(); }
            var end = Math.Min(reference.SourceEndTime ?? mediaDuration, mediaDuration);
            if (!double.IsFinite(mediaDuration) || end <= reference.SourceStartTime)
                throw new InvalidDataException("辅助素材没有可播放的源时间范围。");
            var anchors = reference.SyncAnchors.ValueKind == JsonValueKind.Array
                ? reference.SyncAnchors.EnumerateArray().Select(a => new { sourceTime = a.GetProperty("sourceTime").GetDouble(), legacyOffset = a.GetProperty("offset").GetDouble() }).ToArray() : [];
            if (reference.Role == "camera")
                WriteNew(Path.Combine(project.PackagePath, "media", "camera-sync.json"), new { format = "dogsc.windows.camera", version = 1,
                    relativePath = relative, startOffset = reference.StartOffset, sourceStartTime = reference.SourceStartTime,
                    sourceTimeScale = reference.SourceTimeScale ?? 1, sourceEndTime = end, syncAnchors = anchors });
            else tracks.Add(new { role = "microphone", relativePath = relative, startOffset = reference.StartOffset,
                sourceStartTime = reference.SourceStartTime, sourceTimeScale = reference.SourceTimeScale ?? 1, sourceEndTime = end, syncAnchors = anchors });
        }
        if (tracks.Count > 0) WriteNew(Path.Combine(project.PackagePath, "media", "audio-sync.json"),
            new { format = "dogsc.windows.audio", version = 1, tracks });
    }

    private static async Task<SceneStyle> Canvas(LegacyProjectDocument project, string package, List<string> unsupported, CancellationToken token)
    {
        var c = project.Section("canvas");
        var s = new SceneStyle { Padding = 0, Corner = 0, Border = 0, Shadow = 0, Background = "#FF293954", BackgroundKind = "图案", PatternPreset = "黑曜石网格" };
        if (c.ValueKind != JsonValueKind.Object) return s with { LegacyCanvas = new(), BackgroundKind = "图案", PatternPreset = "黑曜石网格" };
        s = s with { Ratio = Text(c, "aspectRatio", "适应") switch
        { "适应" => 0, "16:9" => 16.0 / 9, "4:3" => 4.0 / 3, "9:16" => 9.0 / 16, "1:1" => 1, "3:4" => .75, "2.39:1" => 2.39,
            "1:2.39" => 1 / 2.39, "自定义" => CustomRatio(c), _ => throw new InvalidDataException("未知Mac画布宽高比。") } };
        var position = c.TryGetProperty("contentPosition", out var content) ? Point(content) : new NormalizedPoint(.5, .5);
        string frame = Text(c, "screenFrame", "none") switch { "paperWhite" or "graphite" => "none", "cyberNeon" or "titaniumBevel" or "studioRim" => "windowDark",
            "frostedGlass" or "vintageMac" or "galleryArt" => "windowLight", var value => value };
        var legacy = new LegacyCanvasStyle(Number(c, "padding", 100), Number(c, "contentScale", 1), position.X, position.Y,
            Number(c, "cornerRadius", 24), Number(c, "borderWidth", 0), Color(Text(c, "borderHex", "#FFFFFF")), Number(c, "shadowStrength", .28),
            Number(c, "backgroundBlur", 0), Number(c, "insetOpacity", .125), frame, Math.Clamp(Number(c, "screenFrameScale", 1), .6, 1.6),
            Math.Clamp(Number(c, "screenFrameToolbarScale", 1), .65, 1.6), Optional(c, "screenFrameOuterCornerRadius"), Optional(c, "screenFrameContentCornerRadius"),
            Text(c, "screenFrameTitle", ""), Text(c, "screenFrameBrowserAddress", ""), Math.Clamp(Number(c, "patternScale", 1), .4, 6), Math.Clamp(Number(c, "patternOpacity", 1), 0, 1));
        LegacyCanvasGeometry.Validate(legacy); s = s with { LegacyCanvas = legacy, BorderColor = legacy.BorderColor };
        if (c.TryGetProperty("crop", out var crop))
            s = s with { CropLeft = Number(crop, "x", 0), CropTop = Number(crop, "y", 0),
                CropRight = 1 - Number(crop, "x", 0) - Number(crop, "width", 1), CropBottom = 1 - Number(crop, "y", 0) - Number(crop, "height", 1) };
        if (c.TryGetProperty("backgroundSource", out var b))
        {
            if (Text(b, "kind", "") is "projectImage" or "projectVideo")
            {
                var reference = project.MediaReferences.Single(r => r.Role == "background");
                var relative = "assets/import/background" + Path.GetExtension(reference.RelativePath);
                var path = LegacyProjectCodec.ResolvePackagePath(package, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await CopyNew(reference.FullPath, path, token);
                s = s with { BackgroundKind = Text(b, "kind", "") == "projectVideo" ? "视频" : "图片", BackgroundAsset = relative };
            }
            else if (Text(b, "kind", "") == "pattern") s = s with { BackgroundKind = "图案", PatternPreset = Text(b, "patternPreset", "黑曜石网格") };
            else if (Text(b, "kind", "") == "dynamicFlow") s = s with { BackgroundKind = "动态", DynamicPreset = Text(b, "dynamicPreset", "赛博微流网格") };
            else unsupported.Add("canvas.backgroundSource：原机器外部素材路径保留；不会未经授权读取此电脑上同名路径。");
        }
        return s;
    }
    private static double CustomRatio(JsonElement canvas)
    {
        if (!canvas.TryGetProperty("customAspectRatio", out var custom)) return 16.0 / 9;
        double width = Number(custom, "width", 16), height = Number(custom, "height", 9);
        return width > 0 && height > 0 ? Math.Clamp(width / height, .1, 10) : 16.0 / 9;
    }
    private static string Color(string hex) => hex.Length == 7 && hex[0] == '#' ? "#FF" + hex[1..] : hex;

    private static ZoomAnimationClip[] Zoom(JsonElement timeline, List<string> unsupported)
    {
        if (!timeline.TryGetProperty("zoomClips", out var array)) return [];
        if (array.GetArrayLength() > 10000) throw new InvalidDataException("缩放轨道超过10000条。");
        return array.EnumerateArray().Select(c =>
        {
            ZoomBezierCurve? curve = c.TryGetProperty("customCurve", out var v) ? new(Number(v, "x1", .65), Number(v, "y1", 0), Number(v, "x2", .35), Number(v, "y2", 1)) : null;
            var point = c.TryGetProperty("focus", out var focus) ? Point(focus) : new NormalizedPoint(.5, .5);
            return new ZoomAnimationClip(c.TryGetProperty("id", out var id) ? Guid.Parse(id.GetString()!) : Guid.NewGuid(),
                Number(c, "startTime", 0), Number(c, "endTime", 0), Number(c, "scale", 1.6), point,
                Text(c, "origin", "manual") switch { "manual" => ZoomOrigin.Manual, "automatic" => ZoomOrigin.Automatic, _ => throw new InvalidDataException("未知缩放origin。") },
                Text(c, "easing", "三次方缓动") switch { "线性" => ZoomEasingPreset.Linear, "二次方缓动" => ZoomEasingPreset.Quadratic,
                    "三次方缓动" => ZoomEasingPreset.Cubic, "五次平滑" => ZoomEasingPreset.Quintic, "弹簧" => ZoomEasingPreset.Spring,
                    "自定义曲线" => ZoomEasingPreset.Custom, _ => throw new InvalidDataException("未知缩放easing。") },
                Number(c, "enterDuration", .55), Number(c, "exitDuration", .55), Number(c, "enterProgressOffset", 0), Number(c, "exitProgressOffset", 0),
                Optional(c, "preferredEnterDuration"), Optional(c, "preferredExitDuration"), curve, SceneMotionTimeline.ReadFocus(c));
        }).ToArray();
    }

    private static PointerEventRecord[] Pointer(LegacyProjectDocument project, double duration, List<string> unsupported)
    {
        var reference = project.MediaReferences.SingleOrDefault(r => r.Role == "pointerEvents");
        if (reference is null) return [];
        if (new FileInfo(reference.FullPath).Length > 100_000_000) throw new InvalidDataException("鼠标轨迹超过100MB。");
        if (reference.SyncAnchors.ValueKind == JsonValueKind.Array && reference.SyncAnchors.GetArrayLength() > 0)
            throw new InvalidDataException("鼠标素材含syncAnchors，当前不应默默使用错误时间。");
        var events = new List<PointerEventRecord>();
        foreach (var line in File.ReadLines(reference.FullPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > 65536 || events.Count >= 2_000_000) throw new InvalidDataException("鼠标记录超过单行64KiB或200万条限制。");
            using var json = JsonDocument.Parse(line, new() { MaxDepth = 16 });
            var e = json.RootElement;
            double time = reference.StartOffset + (Number(e, "time", 0) - reference.SourceStartTime) / (reference.SourceTimeScale ?? 1);
            if (!double.IsFinite(time)) throw new InvalidDataException("鼠标时间无效。");
            if (time < reference.StartOffset || time < 0 || time > duration || reference.SourceEndTime is { } end && Number(e, "time", 0) >= end) continue;
            var point = Point(e.GetProperty("location")); if (!point.IsValid) throw new InvalidDataException("鼠标归一化位置无效。");
            string? assetId = e.TryGetProperty("cursorAssetID", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            if (assetId?.Length > 200) throw new InvalidDataException("鼠标素材标识过长。");
            events.Add(new(time, point, Text(e, "kind", "") switch { "move" => PointerEventKind.Move, "leftClick" => PointerEventKind.LeftClick,
                "rightClick" => PointerEventKind.RightClick, _ => throw new InvalidDataException("未知鼠标事件类型。") }, CursorAssetId: assetId));
        }
        return events.OrderBy(e => e.Time).ToArray();
    }

    private static PointerAppearance PointerStyle(LegacyProjectDocument project, List<string> unsupported)
    {
        var p = project.Section("cursorStyle");
        string id = LegacyCursorAssets.StyleId(p);
        bool usesAssetColor = p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("clickColor", out var authoredColor) || authoredColor.ValueKind == JsonValueKind.Null;
        string effect = Text(p, "clickEffectStyle", Boolean(p, "showsClickEffect", true) ? "ripple" : "none");
        var feedback = effect switch { "none" => "关闭", "ripple" => "涟漪", "glow" => "柔和光晕", "pulse" => "弹性双环", "burst" => "聚焦微闪", _ => "关闭" };
        if (feedback == "关闭" && effect != "none") unsupported.Add("cursorStyle.clickEffectStyle：该Mac点击动画保留，未渲染。");
        unsupported.Add("cursorStyle：身份、热点、大小和隐藏策略已迁移；点击反馈外观未做Mac像素等价验证。");
        return new("录制光标", Math.Clamp(Number(p, "size", 1.25), .25, 6), id == "system.hidden", Boolean(p, "hideWhenIdle", false), Math.Clamp(Number(p, "idleDelay", 1.2), .2, 8),
            Tilt: true, Smoothing: Motion(project).Cursor, ClickFeedback: feedback, ClickColor: Color(Text(p, "clickColor", id == "system.not-allowed" ? "#FF6464" : "#EDA647")),
            ClickOpacity: Math.Clamp(Number(p, "clickOpacity", .8), .05, 1), ClickScale: Math.Clamp(Number(p, "clickScale", 1), .4, 3), AssetId: id,
            UseAssetClickColor: usesAssetColor, MotionTiltStrength: Math.Clamp(Number(p, "motionTiltStrength", 2), 2, 4));
    }

    private static NormalizedPoint Point(JsonElement p) => new(Number(p, "x", .5), Number(p, "y", .5));
    private static bool HasEmbeddedAudio(string path)
    {
        Mf.Check(Mf.MFStartup(0x20070, 0));
        try
        {
            try { using var audio = new MfReader(path, true); return true; }
            catch (COMException error) when (error.HResult == unchecked((int)0xC00D36B3)) { return false; }
        }
        finally { Mf.MFShutdown(); }
    }
    private static bool HasContent(JsonElement value) => value.ValueKind switch { JsonValueKind.Undefined or JsonValueKind.Null => false,
        JsonValueKind.Array => value.GetArrayLength() > 0, JsonValueKind.Object => value.EnumerateObject().Any(), _ => true };
    private static double Number(JsonElement e, string name, double fallback) => Optional(e, name) ?? fallback;
    private static double? Optional(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new InvalidDataException($"{name}必须是有限数字。");
        return number;
    }
    private static string Text(JsonElement e, string name, string fallback)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return fallback;
        if (v.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name}必须是字符串。");
        return v.GetString()!;
    }
    private static bool Boolean(JsonElement e, string name, bool fallback)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return fallback;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException($"{name}必须是布尔值。");
        return v.GetBoolean();
    }
    private static void WriteNew<T>(string path, T value, JsonSerializerOptions? options = null)
    { MediaExport.RejectLinks(path); using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); JsonSerializer.Serialize(stream, value, options ?? Json); stream.Flush(true); }
    private static async Task CopyNew(string source, string output, CancellationToken token)
    {
        MediaExport.RejectLinks(source); MediaExport.RejectLinks(output);
        await using var reader = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
        await using var writer = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
        await reader.CopyToAsync(writer, token); await writer.FlushAsync(token); writer.Flush(true);
    }

    private static bool IsQuickTime(string path)
    {
        MediaExport.RejectLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[16]; input.ReadExactly(header);
        if (!header[4..8].SequenceEqual("ftyp"u8)) throw new InvalidDataException("主屏幕素材缺少容器品牌头，原包已保留。");
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header) == 1) input.ReadExactly(header[..4]);
        else header[8..12].CopyTo(header[..4]);
        return header[..4].SequenceEqual("qt  "u8);
    }

    private static void RemuxScreen(string source, string output, bool hasAudio, IProgress<double> progress, CancellationToken token)
    {
        MediaExport.RejectLinks(source); MediaExport.RejectLinks(output);
        if (File.Exists(output)) throw new IOException("导入视频目标已存在，不能覆盖。");
        IntPtr reader = IntPtr.Zero, writer = IntPtr.Zero;
        Mf.Check(Mf.MFStartup(0x20070, 0));
        try
        {
            token.ThrowIfCancellationRequested();
            Mf.Check(Mf.MFCreateSourceReaderFromURL(source, IntPtr.Zero, out reader));
            Mf.Check(Mf.Call<Mf.SelectStream>(reader, 4)(reader, -2, false));
            Mf.Check(Mf.MFCreateSinkWriterFromURL(output, IntPtr.Zero, IntPtr.Zero, out writer));
            var streams = new List<(int Source, int Target)>();
            foreach (int sourceStream in hasAudio ? new[] { -4, -3 } : new[] { -4 })
            {
                Mf.Check(Mf.Call<Mf.GetCurrentType>(reader, 6)(reader, sourceStream, out var native));
                using var format = new Mf.Object(native);
                Mf.Check(Mf.Call<Mf.AddStream>(writer, 3)(writer, native, out var target));
                Mf.Check(Mf.Call<Mf.SetInput>(writer, 4)(writer, target, native, IntPtr.Zero));
                streams.Add((sourceStream, target));
            }
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 5)(writer));
            Marshal.Release(reader); reader = IntPtr.Zero;
            foreach (var stream in streams)
            {
                // An unread selected stream can accumulate the entire file in the source reader queue.
                Mf.Check(Mf.MFCreateSourceReaderFromURL(source, IntPtr.Zero, out reader));
                Mf.Check(Mf.Call<Mf.SelectStream>(reader, 4)(reader, -2, false));
                Mf.Check(Mf.Call<Mf.SelectStream>(reader, 4)(reader, stream.Source, true));
                for (int count = 0; ; count++)
                {
                    if (count >= 10_000_000) throw new InvalidDataException("MOV换封装超过1000万样本，原包已保留。");
                    token.ThrowIfCancellationRequested();
                    Mf.Check(Mf.Call<Mf.ReadSample>(reader, 9)(reader, stream.Source, 0, out _, out var flags, out _, out var sample));
                    using var item = new Mf.Object(sample);
                    if ((flags & 0x35) != 0) throw new InvalidDataException("MOV流读取失败、出现新流或中途改变格式，原包已保留。");
                    if (sample != IntPtr.Zero)
                    {
                        Mf.Check(Mf.Call<Mf.GetCount>(sample, 45)(sample, out var length));
                        if (length is <= 0 or > 268_435_456) throw new InvalidDataException("MOV压缩样本为空或超过256MiB，原包已保留。");
                        Mf.Check(Mf.Call<Mf.WriteSample>(writer, 6)(writer, stream.Target, sample));
                        progress.Report(0);
                    }
                    if ((flags & 2) != 0) break;
                }
                Marshal.Release(reader); reader = IntPtr.Zero;
            }
            token.ThrowIfCancellationRequested();
            Mf.Check(Mf.Call<Mf.NoArgs>(writer, 11)(writer)); progress.Report(100);
        }
        catch (COMException error)
        { throw new InvalidDataException($"MOV换封装失败，编码或容器不受当前Windows媒体组件支持。原包与导入副本均保留，副本位置：{output}。", error); }
        finally
        {
            try { if (reader != IntPtr.Zero) Marshal.Release(reader); if (writer != IntPtr.Zero) Marshal.Release(writer); }
            finally { Mf.MFShutdown(); }
        }
    }
}
