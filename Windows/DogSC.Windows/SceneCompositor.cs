using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DogSC.Core;

namespace DogSC.Windows;

public sealed record SceneLayer(string Kind, double Start, double End, double X, double Y,
    double Width, double Height, string Color = "#FF000000", string? Asset = null, double Opacity = 1, bool SourceSpace = true);
public sealed record SceneStyle
{
    public double Ratio { get; init; }
    public double Padding { get; init; } = 0.06;
    public double Corner { get; init; } = 0.02;
    public double Border { get; init; } = 0.002;
    public string BorderColor { get; init; } = "#FFFFFFFF";
    public double Shadow { get; init; } = 0.02;
    public string Background { get; init; } = "#FF293954";
    public string BackgroundKind { get; init; } = "颜色";
    public string? BackgroundAsset { get; init; }
    public double BackgroundBlur { get; init; }
    public double PatternScale { get; init; } = 0.05;
    public double PatternOpacity { get; init; } = 0.2;
    public string PatternPreset { get; init; } = "暗夜星阵";
    public string DynamicPreset { get; init; } = "赛博微流网格";
    public double CropLeft { get; init; }
    public double CropTop { get; init; }
    public double CropRight { get; init; }
    public double CropBottom { get; init; }
    public double Zoom { get; init; } = 1;
    public double FocusX { get; init; } = 0.5;
    public double FocusY { get; init; } = 0.5;
    public double RotationX { get; init; }
    public double RotationY { get; init; }
    public double RotationZ { get; init; }
    public double Perspective { get; init; } = 3;
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public string Frame { get; init; } = "无";
    public string FrameTitle { get; init; } = "DogSC";
    public string Intro { get; init; } = "无";
    public double IntroSeconds { get; init; } = 0.6;
    public double FrameScale { get; init; } = 1;
    public double FrameToolbarScale { get; init; } = 1;
    public LegacyCanvasStyle? LegacyCanvas { get; init; }
}

public sealed record CameraStyle
{
    public bool Enabled { get; init; }
    public string Shape { get; init; } = "圆形";
    public bool Mirror { get; init; }
    public bool UseRecordedPlacement { get; init; } = true;
    public double X { get; init; } = 0.72;
    public double Y { get; init; } = 0.6;
    public double Width { get; init; } = 0.22;
    public double Height { get; init; } = 0.3;
    public double Corner { get; init; } = 0.08;
    public double Shadow { get; init; } = 0.015;
    public double Rotation { get; init; }
    public double CropLeft { get; init; }
    public double CropTop { get; init; }
    public double CropRight { get; init; }
    public double CropBottom { get; init; }
    public double TimeOffset { get; init; }
    public double? LegacySize { get; init; }
    public double PositionX { get; init; } = .96;
    public double PositionY { get; init; } = .47;
    public double? Roundness { get; init; }
    public double BorderWidth { get; init; }
    public double ShadowStrength { get; init; } = .25;
    public double ScaleDuringZoom { get; init; } = .7;
    public double ContentPositionX { get; init; } = .5;
    public double ContentPositionY { get; init; } = .5;
    public double ContentScale { get; init; } = 1;
    public bool Hidden { get; init; }
}

public sealed record AudioStyle(double MasterVolume = 1, double SystemVolume = 1, double MicrophoneVolume = 1,
    bool SystemMuted = false, bool MicrophoneMuted = false, double SystemOffset = 0, double MicrophoneOffset = 0);

public sealed record PointerAppearance(string Identity = "录制光标", double Scale = 1, bool Hidden = false,
    bool HideWhenIdle = false, double IdleDelay = 2, bool Tilt = false, CursorMotion Smoothing = CursorMotion.Smooth,
    double TimeOffset = 0, string ClickFeedback = "涟漪", string ClickColor = "#FFF59E0B", double ClickOpacity = 0.7, double ClickScale = 1, string? AssetId = null,
    bool UseAssetClickColor = false, double MotionTiltStrength = 2);
public sealed record CursorImage(string Id, string RelativePath, int Width, int Height, int HotspotX, int HotspotY, string? InvertMaskRelativePath, double? ScaleReferenceHeight = null);

public sealed record RecordedCameraPlacement(double Time, double X, double Y, double Width, double Height, double SourceAspect);

public sealed record EditorState(int Version, RecordingSegment[] Segments, SceneStyle Style, SceneLayer[] Layers)
{
    [System.Text.Json.Serialization.JsonIgnore] public string? AssetRoot { get; init; }
    public ZoomAnimationClip[] ZoomClips { get; init; } = [];
    public ScreenMotionClip[] ScreenMotionClips { get; init; } = [];
    public CameraMotionClip[] CameraMotionClips { get; init; } = [];
    public CameraStyle Camera { get; init; } = new();
    public RecordedCameraPlacement[] CameraPlacements { get; init; } = [];
    public OpeningSequenceStyle Opening { get; init; } = new();
    public MotionStyle Motion { get; init; } = new();
    public AudioStyle Audio { get; init; } = new();
    public PointerAppearance Pointer { get; init; } = new();
    public RecordingMarker[]? RecordingMarkers { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public PointerEventRecord[] PointerEvents { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public bool EmbeddedCursor { get; init; } = true;
    [System.Text.Json.Serialization.JsonIgnore] public CursorImage[] CursorImages { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public double SourceDuration { get; init; }
    public static EditorState Initial(double duration) => new(1, [new(Guid.NewGuid(), 0, duration)], new(), []);
    public const int MaxFileBytes = 33_554_432;
    public EditorState WithCameraStyle(CameraStyle camera)
    {
        bool layoutChanged = camera.X != Camera.X || camera.Y != Camera.Y || camera.Width != Camera.Width
            || camera.PositionX != Camera.PositionX
            || camera.PositionY != Camera.PositionY || camera.LegacySize != Camera.LegacySize;
        return this with { Camera = layoutChanged ? camera with { UseRecordedPlacement = false } : camera };
    }
    public double Duration => Segments.Sum(s => s.SourceDuration / s.PlaybackRate);
    public string? ResolveAsset(string? asset)
    {
        if (asset is null) return null;
        if (AssetRoot is null || Path.IsPathRooted(asset) || asset.Length > 512)
            throw new InvalidDataException("项目素材必须使用包内相对路径。");
        var root = Path.GetFullPath(Path.Combine(AssetRoot, "assets")) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(AssetRoot, asset));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("编辑素材路径不能离开assets目录。");
        MediaExport.RejectLinks(path); return path;
    }
    public void ValidateAssets()
    {
        _ = ResolveAsset(Style.BackgroundAsset);
        foreach (var layer in Layers) _ = ResolveAsset(layer.Asset);
    }
    public void Validate(double sourceDuration)
    {
        if (Version != 1 || Segments is null || Style is null || Layers is null || Segments.Length is < 1 or > 10000 || Layers.Length > 1000)
            throw new InvalidDataException("编辑文件版本或结构不受支持，原始素材已保留。");
        var timelineMap = new TimelineMap(new SourceSequence(Segments), sourceDuration);
        RecordingMarkerDomain.Validate(RecordingMarkers);
        if (Segments.Any(segment => segment.PlaybackRate is < .25 or > 8)) throw new InvalidDataException("片段速度必须为0.25至8倍。");
        SceneMotionTimeline.Validate(new(ScreenMotionClips, CameraMotionClips), Duration);
        OpeningSequenceTimeline.Validate(Opening);
        Motion.Validate();
        var s = Style;
        if (s.LegacyCanvas is not null) LegacyCanvasGeometry.Validate(s.LegacyCanvas);
        double[] numbers = [s.Ratio, s.Padding, s.Corner, s.Border, s.Shadow, s.CropLeft, s.CropTop, s.CropRight, s.CropBottom,
            s.Zoom, s.FocusX, s.FocusY, s.RotationX, s.RotationY, s.RotationZ, s.Perspective, s.OffsetX, s.OffsetY,
            s.BackgroundBlur, s.PatternScale, s.PatternOpacity, s.IntroSeconds, s.FrameScale, s.FrameToolbarScale];
        if (numbers.Any(n => !double.IsFinite(n)) || s.Ratio < 0 || s.Ratio > 10 || s.Padding is < 0 or > 0.4
            || s.Corner is < 0 or > 0.5 || s.Border is < 0 or > 0.1 || s.Shadow is < 0 or > 0.2
            || new[] { s.CropLeft, s.CropTop, s.CropRight, s.CropBottom }.Any(n => n is < 0 or > 0.9)
            || s.CropLeft + s.CropRight >= 0.95 || s.CropTop + s.CropBottom >= 0.95
            || s.Zoom is < 1 or > 5 || s.FocusX is < 0 or > 1 || s.FocusY is < 0 or > 1
            || Math.Abs(s.RotationX) > 75 || Math.Abs(s.RotationY) > 75 || Math.Abs(s.RotationZ) > 180
            || s.Perspective is < 1.5 or > 10 || Math.Abs(s.OffsetX) > 1 || Math.Abs(s.OffsetY) > 1
            || s.BackgroundBlur is < 0 or > 50 || s.PatternScale is < 0.01 or > 0.3 || s.PatternOpacity is < 0 or > 1
            || s.IntroSeconds is < 0.1 or > 10 || s.FrameScale is < .1 or > 2 || s.FrameToolbarScale is < .1 or > 3 || s.FrameTitle.Length > 500)
            throw new InvalidDataException("画面参数超出安全范围。");
        if (s.BackgroundKind is not ("颜色" or "图案" or "动态" or "图片" or "视频") || s.Frame is not ("无" or "浅色窗口" or "深色窗口" or "浏览器" or "浅色浏览器" or "深色浏览器" or "手机" or "平板") || s.Intro is not ("无" or "聚拢" or "侧滑" or "轻3D")) throw new InvalidDataException("画面类型无效。");
        if (s.PatternPreset is not ("黑曜石网格" or "工程白网格" or "暗夜星阵" or "建筑极简点" or "立体等角网") || s.DynamicPreset is not ("赛博微流网格" or "星辉漫步点阵" or "极光流体星云")) throw new InvalidDataException("背景样式无效。");
        _ = ColorConverter.ConvertFromString(s.Background);
        _ = ColorConverter.ConvertFromString(s.BorderColor);
        foreach (var layer in Layers)
        {
            double[] values = [layer.Start, layer.End, layer.X, layer.Y, layer.Width, layer.Height, layer.Opacity];
            if (values.Any(v => !double.IsFinite(v)) || layer.Kind is not ("遮盖" or "柔化" or "贴图" or "缩放" or "自动缩放" or "聚光")
                || layer.Start < 0 || layer.End <= layer.Start || layer.End > Duration + 0.01
                || layer.X is < 0 or > 1 || layer.Y is < 0 or > 1 || layer.Width <= 0 || layer.Height <= 0
                || layer.Width > 5 || layer.Height > 1 || layer.Opacity is < 0 or > 1)
                throw new InvalidDataException("效果轨道参数无效。");
            _ = ColorConverter.ConvertFromString(layer.Color);
        }
        if (ZoomClips is null || ZoomClips.Length > 10000) throw new InvalidDataException("缩放轨道数量无效。");
        foreach (var clip in ZoomClips)
        {
            clip.Validate();
            if (clip.StartTime > timelineMap.OutputDuration + TimelineMap.Epsilon
                || clip.EndTime > timelineMap.OutputDuration + TimelineMap.Epsilon)
                throw new InvalidDataException("缩放片段超出成片时长，原始素材和编辑文件已保留。");
        }
        var camera = Camera;
        if (CameraPlacements is null || CameraPlacements.Length > 100_000) throw new InvalidDataException("摄像头录制位置数量无效。");
        double previousPlacementTime = -1;
        foreach (var p in CameraPlacements)
        {
            if (p is null || new[] { p.Time, p.X, p.Y, p.Width, p.Height, p.SourceAspect }.Any(n => !double.IsFinite(n))
                || p.Time < previousPlacementTime || p.Time < 0 || p.Time > sourceDuration + .1 || Math.Abs(p.X) > 100_000 || Math.Abs(p.Y) > 100_000
                || p.Width is <= 0 or > 10_000 || p.Height is <= 0 or > 10_000 || p.SourceAspect is <= 0 or > 10_000)
                throw new InvalidDataException("摄像头录制位置无效。");
            previousPlacementTime = p.Time;
        }
        if (camera is null || new[] { camera.X, camera.Y, camera.Width, camera.Height, camera.Corner, camera.Shadow, camera.Rotation,
                camera.CropLeft, camera.CropTop, camera.CropRight, camera.CropBottom, camera.TimeOffset, camera.PositionX, camera.PositionY,
                camera.BorderWidth, camera.ShadowStrength, camera.ScaleDuringZoom, camera.ContentPositionX, camera.ContentPositionY, camera.ContentScale }.Any(n => !double.IsFinite(n))
            || camera.Shape is not ("圆形" or "方形" or "横向" or "纵向" or "原始比例")
            || camera.X is < 0 or > 1 || camera.Y is < 0 or > 1 || camera.Width is < 0.01 or > 1 || camera.Height is < 0.01 or > 1
            || camera.Corner is < 0 or > 0.5 || camera.Shadow is < 0 or > 0.2 || Math.Abs(camera.Rotation) > 180
            || new[] { camera.CropLeft, camera.CropTop, camera.CropRight, camera.CropBottom }.Any(v => v is < 0 or > 0.9)
            || camera.CropLeft + camera.CropRight >= 0.95 || camera.CropTop + camera.CropBottom >= 0.95 || Math.Abs(camera.TimeOffset) > 60
            || camera.LegacySize is { } size && (!double.IsFinite(size) || size is < .01 or > 1.2)
            || camera.Roundness is { } roundness && (!double.IsFinite(roundness) || roundness is < 0 or > 1)
            || new[] { camera.PositionX, camera.PositionY, camera.ShadowStrength, camera.ScaleDuringZoom, camera.ContentPositionX, camera.ContentPositionY }.Any(n => n is < 0 or > 1)
            || camera.BorderWidth is < 0 or > 60 || camera.ContentScale is < .2 or > 4)
            throw new InvalidDataException("摄像头布局参数无效。");
        if (Audio is null || new[] { Audio.MasterVolume, Audio.SystemVolume, Audio.MicrophoneVolume, Audio.SystemOffset, Audio.MicrophoneOffset }.Any(n => !double.IsFinite(n))
            || Audio.MasterVolume is < 0 or > 1 || Audio.SystemVolume is < 0 or > 1 || Audio.MicrophoneVolume is < 0 or > 1
            || Math.Abs(Audio.SystemOffset) > 60 || Math.Abs(Audio.MicrophoneOffset) > 60)
            throw new InvalidDataException("音量或声音校准参数无效。");
        if (Pointer is null || new[] { Pointer.Scale, Pointer.IdleDelay, Pointer.TimeOffset, Pointer.ClickOpacity, Pointer.ClickScale, Pointer.MotionTiltStrength }.Any(n => !double.IsFinite(n))
            || Pointer.Scale is < 0.25 or > 6 || Pointer.IdleDelay is < 0 or > 60 || Math.Abs(Pointer.TimeOffset) > 60
            || Pointer.ClickOpacity is < 0 or > 1 || Pointer.ClickScale is < 0.1 or > 5 || Pointer.MotionTiltStrength is < 2 or > 4
            || Pointer.Identity is not ("录制光标" or "箭头" or "手势" or "十字")
            || Pointer.ClickFeedback is not ("关闭" or "涟漪" or "柔和光晕" or "弹性双环" or "聚焦微闪") || !Enum.IsDefined(Pointer.Smoothing)
            || Pointer.AssetId is { } assetId && (string.IsNullOrWhiteSpace(assetId) || assetId.Length > 200 || assetId.Any(char.IsControl)))
            throw new InvalidDataException("光标参数无效。");
        _ = ColorConverter.ConvertFromString(Pointer.ClickColor);
    }
}

public static class SceneCompositor
{
    private static readonly ThreadLocal<Dictionary<string, (DateTime Modified, BitmapSource Image)>> Images = new(() => new());
    private static readonly ThreadLocal<Dictionary<string, BitmapSource>> FallbackCursors = new(() => new());
    private sealed class Playback
    {
        public readonly TimelineMap Timeline;
        public readonly ProjectPointerTrack Pointer;
        public readonly ZoomAnimationTrack Zoom;
        public Playback(EditorState state)
        {
            Timeline = new(new(state.Segments), state.SourceDuration > 0 ? state.SourceDuration : state.Segments.Max(s => s.SourceEnd));
            Pointer = new(Timeline, state.PointerEvents, state.Motion with { Cursor = state.Pointer.Smoothing });
            Zoom = new(state.ZoomClips, state.Motion);
        }
    }
    private static readonly ConditionalWeakTable<EditorState, Playback> PlaybackCache = new();
    private sealed record CameraRaster(CameraStyle Style, int Width, int Height, Rect? Placement,
        Int32Rect Region, byte[] Foreground);
    private sealed class CameraRasterCache { public CameraRaster? Raster; }
    private static readonly ConditionalWeakTable<BitmapSource, CameraRasterCache> CameraRasters = new();

    public static EditorState WithAutomaticZooms(EditorState state)
    {
        var map = new TimelineMap(new(state.Segments), state.SourceDuration > 0 ? state.SourceDuration : state.Segments.Max(s => s.SourceEnd));
        var mapped = map.MapPointerEvents(state.PointerEvents).SelectMany(s => s.Events).ToArray();
        var clips = AutoZoomPlanner.Clipped(AutoZoomPlanner.MakeAnimations(mapped), state.Duration);
        var layers = clips.Select(c => new SceneLayer("自动缩放", c.StartTime, c.EndTime, c.Focus.X, c.Focus.Y, c.Scale, 1)).ToArray();
        return state with { Layers = [.. state.Layers.Where(l => l.Kind != "自动缩放"), .. layers], ZoomClips = clips };
    }

    public static EditorState LoadRecordingData(ProjectDocument project, EditorState state)
    {
        var embedded = true;
        bool incomplete = true;
        var report = Path.Combine(project.PackagePath, "events", "recording-events.json"); MediaExport.RejectLinks(report);
        if (File.Exists(report))
        {
            using var json = MediaSynchronization.ReadJson(report);
            if (json.RootElement.GetProperty("format").GetString() != "dogsc.windows.pointer" || json.RootElement.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("鼠标事件报告版本无效。");
            embedded = json.RootElement.GetProperty("embeddedCursor").GetBoolean();
            incomplete = json.RootElement.TryGetProperty("incomplete", out var status) && status.GetBoolean();
        }
        // Missing or degraded reports cannot certify pointer files for replay or automatic zoom.
        if (incomplete) return state with { AssetRoot = project.PackagePath, SourceDuration = project.DurationSeconds,
            PointerEvents = [], EmbeddedCursor = embedded, CursorImages = [] };
        var events = new List<PointerEventRecord>();
        var path = Path.Combine(project.PackagePath, "events", "pointer.jsonl"); MediaExport.RejectLinks(path);
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 100_000_000) throw new IOException("鼠标轨迹超过100MB。");
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length > 65536) throw new InvalidDataException("鼠标事件记录过长。");
                using var json = JsonDocument.Parse(line); var item = json.RootElement;
                if (item.TryGetProperty("outside", out var outside) && outside.GetBoolean()) continue;
                var location = item.GetProperty("location"); var kind = item.GetProperty("kind").GetString();
                var time = item.GetProperty("time").GetDouble(); var point = new NormalizedPoint(location.GetProperty("x").GetDouble(), location.GetProperty("y").GetDouble());
                if (!double.IsFinite(time) || time < 0 || time > project.DurationSeconds + 0.1 || !point.IsValid) throw new InvalidDataException("鼠标事件时间或位置无效。");
                var id = item.TryGetProperty("cursorAssetID", out var asset) ? asset.GetString() : null;
                if (id?.Length > 200) throw new InvalidDataException("光标素材标识过长。");
                events.Add(new(time, point, kind switch { "leftClick" => PointerEventKind.LeftClick, "rightClick" => PointerEventKind.RightClick, "move" => PointerEventKind.Move,
                    _ => throw new InvalidDataException("鼠标事件身份无效。") }, CursorAssetId: id));
                if (events.Count > 2_000_000) throw new InvalidDataException("鼠标事件超过200万条。");
            }
        }
        var images = Array.Empty<CursorImage>();
        var manifest = Path.Combine(project.PackagePath, "assets", "cursors.json"); MediaExport.RejectLinks(manifest);
        if (File.Exists(manifest))
        {
            if (new FileInfo(manifest).Length > 8_388_608) throw new InvalidDataException("光标素材清单超过8MiB。");
            using var json = JsonDocument.Parse(File.ReadAllText(manifest)); var root = json.RootElement;
            if (root.GetProperty("format").GetString() != "dogsc.windows.cursor-assets" || root.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("光标素材清单版本无效。");
            images = root.GetProperty("assets").EnumerateArray().Select(a => new CursorImage(a.GetProperty("id").GetString()!, a.GetProperty("relativePath").GetString()!,
                a.GetProperty("width").GetInt32(), a.GetProperty("height").GetInt32(), a.GetProperty("hotspotX").GetInt32(), a.GetProperty("hotspotY").GetInt32(),
                a.TryGetProperty("invertMaskRelativePath", out var mask) && mask.ValueKind == JsonValueKind.String ? mask.GetString() : null,
                a.TryGetProperty("scaleReferenceHeight", out var reference) && reference.ValueKind != JsonValueKind.Null ? reference.GetDouble() : null)).ToArray();
            if (images.Length > 4096 || images.Select(i => i.Id).Distinct().Count() != images.Length || images.Any(i => string.IsNullOrWhiteSpace(i.Id) || i.Id.Length > 200 || i.Width is < 1 or > 512
                || i.Height is < 1 or > 512 || i.HotspotX < 0 || i.HotspotX >= i.Width || i.HotspotY < 0 || i.HotspotY >= i.Height
                || i.ScaleReferenceHeight is { } referenceHeight && (!double.IsFinite(referenceHeight) || referenceHeight is < 1 or > 1024)))
                throw new InvalidDataException("光标素材清单参数无效。");
        }
        state = state with { AssetRoot = project.PackagePath, SourceDuration = project.DurationSeconds, PointerEvents = events.ToArray(), EmbeddedCursor = embedded, CursorImages = images,
            RecordingMarkers = EditorRecordingMarkers.Load(project, state.RecordingMarkers) };
        foreach (var image in images) { _ = state.ResolveAsset(image.RelativePath); _ = state.ResolveAsset(image.InvertMaskRelativePath); }
        return state;
    }
    public static (int Width, int Height) OutputSize(EditorState state, int sourceWidth, int sourceHeight, int targetHeight)
    {
        var ratio = AspectRatio(state.Style, sourceWidth, sourceHeight);
        var height = targetHeight == 0 ? sourceHeight : targetHeight;
        height = checked((height + 1) / 2 * 2);
        var width = (int)Math.Round(height * ratio / 2) * 2;
        if (width < 2 || width > 8192 || height < 2 || height > 8192) throw new InvalidDataException("输出尺寸超出2至8192像素范围。");
        return (width, height);
    }

    public static double AspectRatio(SceneStyle style, int sourceWidth, int sourceHeight) => style.Ratio > 0 ? style.Ratio
        : sourceWidth * (1 - style.CropLeft - style.CropRight) / (sourceHeight * (1 - style.CropTop - style.CropBottom));

    public static BitmapSource Render(BitmapSource source, EditorState state, double time, int width, int height, BitmapSource? cameraFrame = null)
    {
        var s = state.Style;
        if (source.Format == PixelFormats.Bgr32 && source.PixelWidth == width && source.PixelHeight == height
            && s == new SceneStyle { Padding = 0, Corner = 0, Border = 0, Shadow = 0 }
            && state.EmbeddedCursor && state.Pointer.ClickFeedback == "关闭" && !state.Opening.Enabled
            && state.ZoomClips.Length == 0 && state.Layers.Length == 0 && state.ScreenMotionClips.Length == 0
            && state.CameraMotionClips.Length == 0 && state.Camera.LegacySize is null && state.Camera.Shadow == 0
            && state.Camera.Rotation == 0 && state.Camera.BorderWidth == 0)
        {
            if (!state.Camera.Enabled || state.Camera.Hidden || cameraFrame is null) return source;
            Rect? recordedRect = null;
            if (state.Camera.UseRecordedPlacement && state.CameraPlacements.Length > 0)
            {
                var timeline = PlaybackCache.GetValue(state, key => new(key)).Timeline;
                var sourceTime = timeline.SourceTime(time) ?? timeline.Segments[^1].SourceEnd;
                var p = state.CameraPlacements[Math.Max(0, Array.FindLastIndex(state.CameraPlacements, p => p.Time <= sourceTime))];
                var fittedHeight = Math.Min(height, width / p.SourceAspect); var fittedWidth = fittedHeight * p.SourceAspect;
                recordedRect = new((width - fittedWidth) / 2 + p.X * fittedWidth, (height - fittedHeight) / 2 + p.Y * fittedHeight,
                    p.Width * fittedWidth, p.Height * fittedHeight);
            }
            var raster = GetCameraRaster(cameraFrame, state.Camera, width, height, recordedRect);
            var region = raster.Region;
            if (region.Width == 0 || region.Height == 0) return source;
            var foreground = raster.Foreground;
            var pixels = new byte[checked(width * height * 4)]; source.CopyPixels(pixels, width * 4, 0);
            for (int y = 0; y < region.Height; y++) for (int x = 0; x < region.Width; x++)
            {
                int from = (y * region.Width + x) * 4, to = ((y + region.Y) * width + x + region.X) * 4;
                int alpha = foreground[from + 3];
                if (alpha == 0) continue;
                if (alpha == 255)
                {
                    pixels[to] = foreground[from]; pixels[to + 1] = foreground[from + 1]; pixels[to + 2] = foreground[from + 2];
                    continue;
                }
                for (int channel = 0; channel < 3; channel++) pixels[to + channel] = (byte)Math.Min(255,
                    foreground[from + channel] + (pixels[to + channel] * (255 - alpha) + 127) / 255);
            }
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4); result.Freeze(); return result;
        }
        var openingElements = new List<string> { "屏幕" }; if (state.Camera.Enabled && !state.Camera.Hidden) openingElements.Add("摄像头");
        if (state.Layers.Any(l => l.Kind == "贴图" && l.Start <= .0001 && l.End > .0001)) openingElements.Add("贴图");
        var opening = OpeningSequenceTimeline.Sample(state.Opening, "屏幕", openingElements, time, width, height);
        var basis = new ScreenMotionState(new(s.LegacyCanvas?.PositionX ?? .5, s.LegacyCanvas?.PositionY ?? .5), s.LegacyCanvas?.ContentScale ?? 1, s.RotationX, s.RotationY, s.RotationZ, 2 / s.Perspective);
        ScreenLayoutSample? screenLayout = null;
        if (s.LegacyCanvas is { } authoredCanvas)
        {
            var fitted = LegacyCanvasGeometry.Evaluate(authoredCanvas, width, height, source.PixelWidth * (1 - s.CropLeft - s.CropRight) / (source.PixelHeight * (1 - s.CropTop - s.CropBottom)), basis with { Scale = 1 });
            screenLayout = ScreenMotionLayoutTimeline.Sample(state.ScreenMotionClips, time, basis, new(width, height, fitted.FittedRect.Width, fitted.FittedRect.Height, fitted.BorderWidth, fitted.TopInset, fitted.RightInset, fitted.BottomInset, fitted.LeftInset), state.Motion);
        }
        var motion = screenLayout?.State ?? SceneMotionTimeline.Screen(state.ScreenMotionClips, time, basis, state.Motion);
        s = s with { RotationX = motion.RotationX, RotationY = motion.RotationY, RotationZ = motion.RotationZ, Perspective = motion.Perspective > .00001 ? 2 / motion.Perspective : 1_000_000 };
        var playback = PlaybackCache.GetValue(state, key => new(key));
        var pointerSample = playback.Pointer.Sample(time + state.Pointer.TimeOffset,
            new CursorStyle(state.Pointer.Hidden, state.Pointer.HideWhenIdle, state.Pointer.IdleDelay,
                state.Pointer.ClickFeedback switch { "涟漪" => .36, "柔和光晕" => .32, "弹性双环" => .38, "聚焦微闪" => .22, _ => 0 }, state.Pointer.Tilt, state.Pointer.MotionTiltStrength));
        var privacyLayers = state.Layers.Where(l => l.SourceSpace && l.Kind is "遮盖" or "柔化" && time >= l.Start && time < l.End).ToArray();
        if (privacyLayers.Length > 0)
        {
            var privacy = new DrawingVisual();
            using (var draw = privacy.RenderOpen())
            {
                draw.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
                foreach (var layer in privacyLayers)
                {
                    var rect = new Rect(layer.X * source.PixelWidth, layer.Y * source.PixelHeight, layer.Width * source.PixelWidth, layer.Height * source.PixelHeight);
                    draw.PushOpacity(layer.Opacity);
                    if (layer.Kind == "遮盖") draw.DrawRectangle(new SolidColorBrush(Parse(layer.Color)), null, rect);
                    else
                    {
                        var crop = Int32RectFrom(rect, source.PixelWidth, source.PixelHeight);
                        if (crop.Width > 0 && crop.Height > 0) draw.DrawImage(Blur(new CroppedBitmap(source, crop), 18), new Rect(crop.X, crop.Y, crop.Width, crop.Height));
                    }
                    draw.Pop();
                }
            }
            source = Snapshot(privacy, source.PixelWidth, source.PixelHeight);
        }
        FrameFocusSample? focusEffect = null;
        Rect focusPlaced = default, focusCrop = new(s.CropLeft, s.CropTop, 1 - s.CropLeft - s.CropRight, 1 - s.CropTop - s.CropBottom), focusProjectionRect = default;
        ProjectedScreenQuad? focusQuad = null;
        double pointerOpacity = 1;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            DrawBackground(dc, s, time, width, height);
            var backgroundAsset = state.ResolveAsset(s.BackgroundAsset);
            if (s.BackgroundKind is "图片" or "视频" && backgroundAsset is not null && File.Exists(backgroundAsset))
            {
                BitmapSource image;
                if (s.BackgroundKind == "视频") { using var reader = new MediaFrameReader(backgroundAsset); var loopTime = reader.Duration > 0 ? time % reader.Duration : time; reader.Seek(loopTime); image = reader.FrameAt(loopTime); }
                else image = LoadImage(backgroundAsset);
                var backgroundBlur = s.LegacyCanvas?.BackgroundBlur ?? s.BackgroundBlur;
                if (backgroundBlur > 0) image = Blur(image, backgroundBlur);
                var backgroundScale = Math.Max((double)width / image.PixelWidth, (double)height / image.PixelHeight);
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
                dc.DrawImage(image, new Rect((width - image.PixelWidth * backgroundScale) / 2, (height - image.PixelHeight * backgroundScale) / 2, image.PixelWidth * backgroundScale, image.PixelHeight * backgroundScale)); dc.Pop();
            }
            var crop = new Int32Rect((int)(source.PixelWidth * s.CropLeft), (int)(source.PixelHeight * s.CropTop),
                Math.Max(2, (int)(source.PixelWidth * (1 - s.CropLeft - s.CropRight))), Math.Max(2, (int)(source.PixelHeight * (1 - s.CropTop - s.CropBottom))));
            var cropped = new CroppedBitmap(source, crop);
            var inset = Math.Min(width, height) * s.Padding;
            var device = s.Frame is "手机" or "平板";
            var bezel = device ? Math.Min(width, height) * (s.Frame == "手机" ? .026 : .035) * s.FrameScale : 0;
            var header = s.Frame == "无" || device ? 0 : Math.Min(width, height) * 46 / 1080 * s.FrameScale * s.FrameToolbarScale;
            var scale = Math.Max(2d / Math.Min(cropped.PixelWidth, cropped.PixelHeight),
                Math.Min((width - 2 * inset) / cropped.PixelWidth, (height - 2 * inset - header) / cropped.PixelHeight));
            var cardWidth = Math.Max(2, (int)Math.Round(cropped.PixelWidth * scale + 2 * bezel));
            var cardHeight = Math.Max(2, (int)Math.Round(cropped.PixelHeight * scale + header + 2 * bezel));
            var legacyLayout = s.LegacyCanvas is null ? null : LegacyCanvasGeometry.Evaluate(s.LegacyCanvas, width, height, (double)cropped.PixelWidth / cropped.PixelHeight, motion, screenLayout);
            var contentRect = new Rect(bezel, header + bezel, Math.Max(2, cardWidth - 2 * bezel), Math.Max(2, cardHeight - header - 2 * bezel));
            if (legacyLayout is not null)
            {
                cardWidth = Math.Max(2, (int)Math.Round(legacyLayout.CardRect.Width)); cardHeight = Math.Max(2, (int)Math.Round(legacyLayout.CardRect.Height));
                contentRect = new Rect(legacyLayout.ContentRect.X - legacyLayout.CardRect.X, legacyLayout.ContentRect.Y - legacyLayout.CardRect.Y, legacyLayout.ContentRect.Width, legacyLayout.ContentRect.Height);
                header = legacyLayout.TopInset - legacyLayout.BottomInset;
            }
            var card = new DrawingVisual();
            var zoom = s.Zoom;
            var focusX = s.FocusX; var focusY = s.FocusY;
            var evaluatedZoom = new ZoomSample(zoom, new(focusX, focusY), 1);
            if (playback.Zoom.ActiveClip(time) is { } active)
            {
                var evaluated = playback.Zoom.Sample(time, playback.Pointer.AutomaticCameraFocus(time, active));
                evaluatedZoom = evaluated;
                zoom *= evaluated.Scale; focusX = evaluated.Focus.X; focusY = evaluated.Focus.Y;
            }
            foreach (var layer in state.Layers.Where(l => l.Kind == "缩放" && time >= l.Start && time < l.End))
            {
                var ramp = Math.Min(1, Math.Min((time - layer.Start) / 0.2, (layer.End - time) / 0.2));
                zoom = 1 + (layer.Width - 1) * (ramp * ramp * (3 - 2 * ramp)); focusX = layer.X; focusY = layer.Y;
                evaluatedZoom = new(zoom, new(focusX, focusY), ramp);
            }
            bool keepsCanvasCovered = legacyLayout is not null && s.LegacyCanvas!.ScreenFrame == "none" && !state.ScreenMotionClips.Any(c => c.Timing.StartTime <= time && (time <= c.Timing.EffectEndTime || c.Timing.ReturnDuration <= .0001))
                && legacyLayout.ContentRect.X <= 1e-6 && legacyLayout.ContentRect.Y <= 1e-6 && legacyLayout.ContentRect.X + legacyLayout.ContentRect.Width >= width - 1e-6 && legacyLayout.ContentRect.Y + legacyLayout.ContentRect.Height >= height - 1e-6;
            var viewport = legacyLayout is null ? null : ZoomViewportTransform.Make(evaluatedZoom, new(s.CropLeft, s.CropTop, 1 - s.CropLeft - s.CropRight, 1 - s.CropTop - s.CropBottom), keepsCanvasCovered ? new CompositionRect(-legacyLayout.ContentRect.X / legacyLayout.ContentRect.Width, -legacyLayout.ContentRect.Y / legacyLayout.ContentRect.Height, width / legacyLayout.ContentRect.Width, height / legacyLayout.ContentRect.Height) : null);
            var wholeCardZoom = viewport is not null && !keepsCanvasCovered ? viewport.Scale : 1;
            var activeZoom = playback.Zoom.ActiveClip(time);
            focusEffect = FocusEffectEvaluator.Scene(activeZoom, FocusEffectEvaluator.ActiveScreen(state.ScreenMotionClips, time),
                evaluatedZoom.Focus, new(focusCrop.X, focusCrop.Y, focusCrop.Width, focusCrop.Height), playback.Pointer.SamplePosition(time + state.Pointer.TimeOffset), time, state.Motion);
            var sourceRect = viewport is not null && !keepsCanvasCovered ? contentRect
                : viewport is not null ? new Rect(contentRect.X + contentRect.Width * (.5 - viewport.Scale / 2 + viewport.Translation.X), contentRect.Y + contentRect.Height * (.5 - viewport.Scale / 2 + viewport.Translation.Y), contentRect.Width * viewport.Scale, contentRect.Height * viewport.Scale)
                : new Rect(contentRect.X - contentRect.Width * (zoom - 1) * focusX, contentRect.Y - contentRect.Height * (zoom - 1) * focusY, contentRect.Width * zoom, contentRect.Height * zoom);
            using (var c = card.RenderOpen())
            {
                var corner = s.LegacyCanvas is { } canvas ? (canvas.OuterCornerRadius ?? canvas.CornerRadius) * legacyLayout!.StyleScale * motion.Scale : Math.Min(cardWidth, cardHeight) * (device ? .065 : s.Corner);
                c.PushClip(new RectangleGeometry(new Rect(0, 0, cardWidth, cardHeight), corner, corner));
                var dark = s.Frame is "深色窗口" or "深色浏览器" or "手机" or "平板" || s.LegacyCanvas?.ScreenFrame is "windowDark" or "browserDark" || s.LegacyCanvas?.ScreenFrame.StartsWith("device", StringComparison.Ordinal) == true;
                c.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(40, 40, 40) : Colors.White), null, new Rect(0, 0, cardWidth, cardHeight));
                if (header > 0)
                {
                    for (var i = 0; i < 3; i++) c.DrawEllipse(new SolidColorBrush(new[] { Colors.IndianRed, Colors.Goldenrod, Colors.MediumSeaGreen }[i]), null,
                        new Point(header * (0.5 + i * 0.6), header / 2), header * 0.15, header * 0.15);
                    var browser = s.Frame.Contains("浏览器") || s.Frame == "浏览器" || s.LegacyCanvas?.ScreenFrame is "browserLight" or "browserDark";
                    if (browser) c.DrawRoundedRectangle(new SolidColorBrush(dark ? Color.FromRgb(62, 62, 62) : Color.FromRgb(235, 236, 238)), null, new Rect(header * 2.4, header * .15, Math.Max(2, cardWidth - header * 3), header * .7), header * .15, header * .15);
                    var text = new FormattedText(s.LegacyCanvas is { } savedCanvas ? (browser ? savedCanvas.BrowserAddress : savedCanvas.Title) : s.FrameTitle, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), Math.Max(8, header * 0.35), new SolidColorBrush(dark ? Colors.White : Colors.Black), 1);
                    c.DrawText(text, new Point(header * 2.5, header * 0.25));
                }
                var contentCorner = s.LegacyCanvas?.ContentCornerRadius is { } contentRadius ? contentRadius * legacyLayout!.StyleScale * motion.Scale : device ? corner * .7 : 0;
                c.PushClip(new RectangleGeometry(contentRect, contentCorner, contentCorner));
                if (viewport is not null && !keepsCanvasCovered) c.DrawImage(cropped, contentRect);
                else if (viewport is not null) c.DrawImage(cropped, new Rect(contentRect.X + contentRect.Width * (.5 - viewport.Scale / 2 + viewport.Translation.X), contentRect.Y + contentRect.Height * (.5 - viewport.Scale / 2 + viewport.Translation.Y), contentRect.Width * viewport.Scale, contentRect.Height * viewport.Scale));
                else
                {
                    var viewWidth = contentRect.Width * zoom; var viewHeight = contentRect.Height * zoom;
                    c.DrawImage(cropped, new Rect(contentRect.X - (viewWidth - contentRect.Width) * focusX, contentRect.Y - (viewHeight - contentRect.Height) * focusY, viewWidth, viewHeight));
                }
                c.Pop(); c.Pop();
                var borderWidth = legacyLayout?.BorderWidth ?? Math.Min(width, height) * s.Border;
                if (borderWidth > 0)
                {
                    var borderColor = Parse(s.LegacyCanvas?.BorderColor ?? s.BorderColor);
                    if (s.LegacyCanvas is { } authoredBorder) borderColor.A = (byte)Math.Round(borderColor.A * authoredBorder.InsetOpacity);
                    c.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(borderColor), borderWidth), new Rect(0, 0, cardWidth, cardHeight), corner, corner);
                }
            }
            var cardScale = (legacyLayout is null ? motion.Scale : wholeCardZoom) * opening.Scale;
            // Keep the drawing vectorial until the final raster pass when no bitmap projection is needed.
            bool directCard = Math.Abs(cardScale - 1) <= .00001 && s.Intro != "轻3D"
                && Math.Abs(s.RotationX + opening.RotationX) + Math.Abs(s.RotationY + opening.RotationY) + Math.Abs(s.RotationZ) <= .001;
            BitmapSource? cardBitmap = directCard ? null : Snapshot(card, cardWidth, cardHeight);
            if (Math.Abs(cardScale - 1) > .00001)
            {
                var scaled = new TransformedBitmap(cardBitmap!, new ScaleTransform(cardScale, cardScale)); scaled.Freeze(); cardBitmap = scaled;
                cardWidth = cardBitmap.PixelWidth; cardHeight = cardBitmap.PixelHeight;
            }
            var progress = s.Intro == "无" ? 1 : Math.Clamp(time / s.IntroSeconds, 0, 1);
            progress = 1 - Math.Pow(1 - progress, 3);
            pointerOpacity = progress * opening.Opacity;
            var x = (legacyLayout is null ? (width - cardWidth) * motion.Position.X : legacyLayout.CardRect.X + (legacyLayout.CardRect.Width - cardWidth) / 2) + s.OffsetX * width + opening.OffsetX;
            var y = (legacyLayout is null ? (height - cardHeight) * motion.Position.Y : legacyLayout.CardRect.Y + (legacyLayout.CardRect.Height - cardHeight) / 2) + s.OffsetY * height + opening.OffsetY;
            if (legacyLayout is not null && viewport is not null && !keepsCanvasCovered)
            {
                var finalContent = LegacyCanvasGeometry.ZoomRect(legacyLayout, viewport, motion.Scale);
                x = finalContent.X - contentRect.X * wholeCardZoom + (legacyLayout.CardRect.Width * wholeCardZoom - cardWidth) / 2 + opening.OffsetX;
                y = finalContent.Y - contentRect.Y * wholeCardZoom + (legacyLayout.CardRect.Height * wholeCardZoom - cardHeight) / 2 + opening.OffsetY;
            }
            if (s.Intro == "侧滑") x += (1 - progress) * width;
            if (s.Intro == "聚拢") { dc.PushTransform(new ScaleTransform(0.5 + 0.5 * progress, 0.5 + 0.5 * progress, width / 2.0, height / 2.0)); }
            dc.PushOpacity(progress * opening.Opacity);
            if (s.Shadow > 0 || legacyLayout?.ShadowOpacity > 0)
            {
                var shadowRadius = legacyLayout?.ShadowRadius ?? Math.Min(width, height) * s.Shadow;
                var shadow = new Border { Width = cardWidth, Height = cardHeight, Background = Brushes.Black,
                    CornerRadius = new CornerRadius(legacyLayout?.CornerRadius ?? Math.Min(cardWidth, cardHeight) * s.Corner), Effect = new BlurEffect { Radius = shadowRadius } };
                shadow.Measure(new Size(cardWidth, cardHeight)); shadow.Arrange(new Rect(0, 0, cardWidth, cardHeight));
                var shadowBitmap = new RenderTargetBitmap(cardWidth, cardHeight, 96, 96, PixelFormats.Pbgra32); shadowBitmap.Render(shadow);
                dc.PushOpacity(legacyLayout?.ShadowOpacity ?? .35); dc.DrawImage(shadowBitmap, new Rect(x, y + (legacyLayout?.ShadowOffsetY ?? Math.Min(width, height) * s.Shadow / 2), cardWidth, cardHeight)); dc.Pop();
            }
            var rx = s.RotationX + opening.RotationX; var ry = s.RotationY + opening.RotationY + (s.Intro == "轻3D" ? (1 - progress) * 45 : 0);
            focusPlaced = new Rect(x + sourceRect.X * cardScale, y + sourceRect.Y * cardScale, sourceRect.Width * cardScale, sourceRect.Height * cardScale);
            focusProjectionRect = new Rect(x, y, cardWidth, cardHeight);
            if (Math.Abs(rx) + Math.Abs(ry) + Math.Abs(s.RotationZ) > 0.001)
            {
                var anchor = screenLayout?.ProjectionAnchor ?? new NormalizedPoint(.5, .5);
                var pivot = new CompositionPoint(x + (contentRect.X + contentRect.Width * anchor.X) * cardScale, y + (contentRect.Y + contentRect.Height * anchor.Y) * cardScale);
                focusQuad = ScreenProjection.Project(new(x, y, cardWidth, cardHeight), rx, ry, s.RotationZ, Math.Clamp(motion.Perspective + opening.Perspective, 0, 2), pivot);
                var projected = Project(cardBitmap!, width, height, x, y, rx, ry, s.RotationZ, Math.Clamp(motion.Perspective + opening.Perspective, 0, 2), pivot);
                dc.DrawImage(projected, new Rect(0, 0, width, height));
            }
            else if (directCard)
            {
                dc.PushTransform(new TranslateTransform(x, y)); dc.DrawDrawing(card.Drawing); dc.Pop();
            }
            else dc.DrawImage(cardBitmap, new Rect(x, y, cardWidth, cardHeight));
            dc.Pop(); if (s.Intro == "聚拢") dc.Pop();
        }
        bool needsPointer = pointerSample is not null && (!state.EmbeddedCursor || state.Pointer.ClickFeedback != "关闭");
        bool needsFocus = focusEffect is not null && focusEffect.Progress > .001 && focusPlaced.Width > 1
            && focusPlaced.Height > 1 && focusCrop.Contains(new Point(focusEffect.Center.X, focusEffect.Center.Y));
        BitmapSource? scene = needsPointer || needsFocus || state.Layers.Any(l => !l.SourceSpace && l.Kind == "柔化" && time >= l.Start && time < l.End)
            ? Snapshot(visual, width, height) : null;
        BitmapSource? focusedScene = scene is null ? null : ApplyFocus(scene, focusEffect, focusPlaced, focusCrop, focusQuad, focusProjectionRect);
        if (needsPointer)
        {
            var placed = focusPlaced; var projection = focusProjectionRect; var quad = focusQuad;
            if (s.Intro == "聚拢")
            {
                double p = 1 - Math.Pow(1 - Math.Clamp(time / s.IntroSeconds, 0, 1), 3), factor = .5 + .5 * p;
                CompositionPoint Transform(CompositionPoint point) => new(width / 2.0 + (point.X - width / 2.0) * factor, height / 2.0 + (point.Y - height / 2.0) * factor);
                Rect TransformRect(Rect rect) { var origin = Transform(new(rect.X, rect.Y)); return new(origin.X, origin.Y, rect.Width * factor, rect.Height * factor); }
                placed = TransformRect(placed); projection = TransformRect(projection);
                if (quad is not null) quad = new(Transform(quad.TopLeft), Transform(quad.TopRight), Transform(quad.BottomRight), Transform(quad.BottomLeft));
            }
            focusedScene = DrawPointer(focusedScene!, state, pointerSample!, placed, focusCrop, quad, projection, source.PixelWidth, pointerOpacity);
        }
        if ((!state.Camera.Enabled || state.Camera.Hidden || cameraFrame is null)
            && !state.Layers.Any(l => time >= l.Start && time < l.End && l.Kind is not ("缩放" or "自动缩放") && !(l.SourceSpace && l.Kind is "遮盖" or "柔化")))
            return focusedScene ?? Snapshot(visual, width, height);
        var layers = new DrawingVisual();
        using (var dc = layers.RenderOpen())
        {
            if (focusedScene is null) dc.DrawDrawing(visual.Drawing);
            else dc.DrawImage(focusedScene, new Rect(0, 0, width, height));
            if (state.Camera.Enabled && !state.Camera.Hidden && cameraFrame is not null)
            {
                var cameraCrop = CameraCropRect(cameraFrame, state.Camera);
                var staticCameraRect = CameraLayoutRect(state.Camera, width, height, cameraCrop);
                bool recordedPlacement = state.Camera.UseRecordedPlacement && state.CameraPlacements.Length > 0;
                if (recordedPlacement)
                {
                    var sourceTime = playback.Timeline.SourceTime(time) ?? playback.Timeline.Segments[^1].SourceEnd;
                    var index = Array.FindLastIndex(state.CameraPlacements, p => p.Time <= sourceTime);
                    var p = state.CameraPlacements[Math.Max(0, index)];
                    var fittedHeight = Math.Min(height, width / p.SourceAspect); var fittedWidth = fittedHeight * p.SourceAspect;
                    staticCameraRect = new((width - fittedWidth) / 2 + p.X * fittedWidth, (height - fittedHeight) / 2 + p.Y * fittedHeight,
                        p.Width * fittedWidth, p.Height * fittedHeight);
                }
                CameraMotionSample? cameraMotion = null;
                if (state.CameraMotionClips.Length > 0 || state.Camera.LegacySize is not null)
                {
                    var cameraWidth = staticCameraRect.Width; var cameraHeight = staticCameraRect.Height;
                    var position = new NormalizedPoint(Math.Clamp(state.Camera.X * width / Math.Max(1, width - cameraWidth), 0, 1), Math.Clamp(state.Camera.Y * height / Math.Max(1, height - cameraHeight), 0, 1));
                    var cameraBasis = new CameraMotionState(state.Camera.LegacySize is null ? position : new(state.Camera.PositionX, state.Camera.PositionY), state.Camera.LegacySize ?? cameraHeight / Math.Min(width, height), state.Camera.Shape, state.Camera.Roundness ?? (state.Camera.Shape == "圆形" ? 1 : state.Camera.Corner * 2), 1, state.Camera.Rotation);
                    var basisSample = state.Camera.LegacySize is null || recordedPlacement ? new CameraMotionSample(position, cameraHeight / Math.Min(width, height),
                        cameraWidth / cameraHeight, state.Camera.Shape == "圆形" ? .5 : state.Camera.Corner, 0, 1, state.Camera.Rotation,
                        new(staticCameraRect.X, staticCameraRect.Y, cameraWidth, cameraHeight)) : null;
                    // Native editor placement can be smaller or outside the legacy motion layout limits.
                    var screenZoomScale = playback.Zoom.Sample(time).Scale;
                    cameraMotion = SceneMotionTimeline.Camera(state.CameraMotionClips, time, cameraBasis, (double)cameraCrop.Width / cameraCrop.Height,
                        (double)width / height, state.Motion, basisSample, basisSample is null ? null : sample =>
                            SceneMotionTimeline.CameraRect(sample, width, height, screenZoomScale, state.Camera.ScaleDuringZoom));
                }
                var cameraOpening = OpeningSequenceTimeline.Sample(state.Opening, "摄像头", openingElements, time, width, height);
                dc.PushOpacity(cameraOpening.Opacity); dc.PushTransform(new TranslateTransform(cameraOpening.OffsetX, cameraOpening.OffsetY));
                var cameraRect = cameraMotion is null ? staticCameraRect : ToRect(SceneMotionTimeline.CameraRect(cameraMotion, width, height, playback.Zoom.Sample(time).Scale, state.Camera.ScaleDuringZoom));
                dc.PushTransform(new ScaleTransform(cameraOpening.Scale, cameraOpening.Scale, cameraRect.X + cameraRect.Width / 2, cameraRect.Y + cameraRect.Height / 2));
                DrawCamera(dc, cameraFrame, state.Camera, width, height, cameraMotion, cameraRect); dc.Pop(); dc.Pop(); dc.Pop();
            }
            foreach (var layer in state.Layers.Where(l => time >= l.Start && time < l.End && l.Kind is not ("缩放" or "自动缩放") && !(l.SourceSpace && l.Kind is "遮盖" or "柔化")))
            {
                var rect = new Rect(layer.X * width, layer.Y * height, layer.Width * width, layer.Height * height);
                dc.PushOpacity(layer.Opacity);
                if (layer.Kind == "贴图")
                {
                    var stickerOpening = OpeningSequenceTimeline.Sample(state.Opening, "贴图", openingElements, time, width, height);
                    dc.PushOpacity(stickerOpening.Opacity); dc.PushTransform(new TranslateTransform(stickerOpening.OffsetX, stickerOpening.OffsetY));
                    dc.PushTransform(new ScaleTransform(stickerOpening.Scale, stickerOpening.Scale, rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
                }
                if (layer.Kind == "遮盖") dc.DrawRectangle(new SolidColorBrush(Parse(layer.Color)), null, rect);
                var asset = state.ResolveAsset(layer.Asset);
                if (layer.Kind == "贴图" && asset is not null && File.Exists(asset)) dc.DrawImage(LoadImage(asset), rect);
                if (layer.Kind == "柔化")
                {
                    var clip = Int32RectFrom(rect, width, height);
                    if (clip.Width > 0 && clip.Height > 0) dc.DrawImage(Blur(new CroppedBitmap(scene!, clip), 18), new Rect(clip.X, clip.Y, clip.Width, clip.Height));
                }
                if (layer.Kind == "聚光")
                {
                    var geometry = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, width, height)), new EllipseGeometry(rect));
                    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), null, geometry);
                }
                if (layer.Kind == "贴图") { dc.Pop(); dc.Pop(); dc.Pop(); } dc.Pop();
            }
        }
        return Snapshot(layers, width, height);
    }

    private static CameraRaster GetCameraRaster(BitmapSource frame, CameraStyle style, int width, int height, Rect? placement)
    {
        if (!frame.IsFrozen) return CreateCameraRaster(frame, style, width, height, placement);
        var cache = CameraRasters.GetValue(frame, _ => new());
        lock (cache)
        {
            if (cache.Raster is { } current && current.Style == style && current.Width == width
                && current.Height == height && current.Placement == placement) return current;
            // A camera frame repeats across screen frames; keep only its latest layout until the frame is collected.
            return cache.Raster = CreateCameraRaster(frame, style, width, height, placement);
        }
    }

    private static CameraRaster CreateCameraRaster(BitmapSource frame, CameraStyle style, int width, int height, Rect? placement)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) DrawCamera(dc, frame, style, width, height, evaluatedRect: placement);
        var bounds = visual.ContentBounds; bounds.Intersect(new Rect(0, 0, width, height));
        if (bounds.IsEmpty) return new(style, width, height, placement, Int32Rect.Empty, []);
        int left = Math.Max(0, (int)Math.Floor(bounds.Left) - 1), top = Math.Max(0, (int)Math.Floor(bounds.Top) - 1);
        int right = Math.Min(width, (int)Math.Ceiling(bounds.Right) + 1), bottom = Math.Min(height, (int)Math.Ceiling(bounds.Bottom) + 1);
        var region = new Int32Rect(left, top, right - left, bottom - top);
        if (region.Width == 0 || region.Height == 0) return new(style, width, height, placement, region, []);
        var local = new DrawingVisual();
        using (var dc = local.RenderOpen()) { dc.PushTransform(new TranslateTransform(-region.X, -region.Y)); dc.DrawDrawing(visual.Drawing); dc.Pop(); }
        var overlay = Snapshot(local, region.Width, region.Height);
        var foreground = new byte[checked(region.Width * region.Height * 4)]; overlay.CopyPixels(foreground, region.Width * 4, 0);
        return new(style, width, height, placement, region, foreground);
    }

    private static void DrawBackground(DrawingContext dc, SceneStyle style, double time, int width, int height)
    {
        if (style.LegacyCanvas is { } authored) style = style with { PatternOpacity = authored.PatternOpacity };
        var canvas = new Rect(0, 0, width, height);
        dc.DrawRectangle(new SolidColorBrush(Parse(style.Background)), null, canvas);
        if (style.BackgroundKind is not ("图案" or "动态")) return;
        if (style.BackgroundKind == "动态" && style.DynamicPreset == "极光流体星云")
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(6, 22, 51), Color.FromRgb(4, 7, 19), 45), null, canvas);
            for (var i = 0; i < 3; i++)
            {
                var colors = new[] { Color.FromRgb(20, 107, 245), Color.FromRgb(20, 212, 245), Color.FromRgb(230, 247, 255) };
                var radii = new[] { .56, .42, .32 }; var phases = new[] { .19, .14, .11 };
                var gradient = new RadialGradientBrush(colors[i], Colors.Transparent) { RadiusX = radii[i], RadiusY = radii[i], Center = new Point(.5 + Math.Sin(time * phases[i] + i * 2) * .3, .5 + Math.Cos(time * phases[i] * 1.4 + i * 2) * .3), GradientOrigin = new Point(.5 + Math.Sin(time * phases[i] + i * 2) * .3, .5 + Math.Cos(time * phases[i] * 1.4 + i * 2) * .3), Opacity = style.PatternOpacity * new[] { .82, .62, .7 }[i] };
                dc.DrawRectangle(gradient, null, canvas);
            }
            return;
        }
        var preset = style.BackgroundKind == "图案" ? style.PatternPreset : style.DynamicPreset == "星辉漫步点阵" ? "暗夜星阵" : "黑曜石网格";
        var white = preset is "工程白网格" or "建筑极简点";
        var dots = preset is "暗夜星阵" or "建筑极简点";
        var baseColor = white ? (preset == "工程白网格" ? 247 : 249) : preset == "黑曜石网格" ? 15 : preset == "暗夜星阵" ? 17 : 19;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)baseColor, (byte)baseColor, (byte)baseColor)), null, canvas);
        var color = preset switch { "工程白网格" => Color.FromRgb(201, 201, 201), "建筑极简点" => Color.FromRgb(148, 148, 148), "暗夜星阵" => Color.FromRgb(194, 173, 138), "立体等角网" => Color.FromRgb(79, 74, 66), _ => Color.FromRgb(61, 61, 61) };
        var brush = new SolidColorBrush(color) { Opacity = style.PatternOpacity * (preset == "暗夜星阵" ? .9 : preset == "建筑极简点" ? .75 : 1) };
        var fraction = preset switch { "黑曜石网格" => .0425, "工程白网格" => .04, "暗夜星阵" => .032, "建筑极简点" => .03, _ => .05 };
        var step = Math.Max(9, Math.Min(width, height) * (style.LegacyCanvas is { } legacy ? fraction * legacy.PatternScale : style.PatternScale));
        var dx = style.BackgroundKind == "动态" ? time * step * (dots ? .07 : .1) % step : 0;
        var dy = style.BackgroundKind == "动态" ? time * step * (dots ? .04 : .065) % step : 0;
        var pen = new Pen(brush, Math.Max(.75, step * .008));
        dc.PushClip(new RectangleGeometry(canvas));
        for (var y = -step + dy; y < height; y += step)
            for (var x = -step + dx; x < width; x += step)
            {
                if (dots) dc.DrawEllipse(brush, null, new Point(x + step / 2, y + step / 2), Math.Max(.8, step * .026), Math.Max(.8, step * .026));
                else if (preset == "立体等角网") { dc.DrawLine(pen, new(x, y), new(x + step, y + step)); dc.DrawLine(pen, new(x, y + step), new(x + step, y)); }
                else dc.DrawRectangle(null, pen, new Rect(x + pen.Thickness / 2, y + pen.Thickness / 2, step - pen.Thickness, step - pen.Thickness));
            }
        dc.Pop();
    }

    private static Int32Rect Int32RectFrom(Rect rect, int width, int height)
    {
        var left = Math.Clamp((int)rect.Left, 0, width); var top = Math.Clamp((int)rect.Top, 0, height);
        var right = Math.Clamp((int)rect.Right, left, width); var bottom = Math.Clamp((int)rect.Bottom, top, height);
        return new(left, top, right - left, bottom - top);
    }
    public static BitmapSource LoadImage(string path) => LoadImage(path, null, null);

    private static BitmapSource LoadImage(string path, int? cursorWidth, int? cursorHeight)
    {
        MediaExport.RejectLinks(path);
        var info = new FileInfo(path);
        if (info.Length > 100_000_000) throw new InvalidDataException("图片素材不能超过100MB。");
        if (Images.Value!.TryGetValue(path, out var cached) && cached.Modified == info.LastWriteTimeUtc)
        { ValidateSize(cached.Image); return cached.Image; }
        using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        ValidateSize(decoder.Frames[0]);
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit();
        ValidateSize(image);
        image.Freeze();
        if (Images.Value.Count >= 16) Images.Value.Clear();
        Images.Value[path] = (info.LastWriteTimeUtc, image); return image;

        void ValidateSize(BitmapSource frame)
        {
            if (frame.PixelWidth is < 1 or > 8192 || frame.PixelHeight is < 1 or > 8192)
                throw new InvalidDataException("图片尺寸必须为1至8192像素。");
            if (cursorWidth is not null && (cursorWidth is < 1 or > 512 || cursorHeight is < 1 or > 512
                || frame.PixelWidth != cursorWidth || frame.PixelHeight != cursorHeight))
                throw new InvalidDataException("光标素材实际尺寸必须与清单一致且不超过512像素。");
        }
    }
    private static BitmapSource ApplyFocus(BitmapSource scene, FrameFocusSample? focus, Rect placed, Rect crop,
        ProjectedScreenQuad? quad, Rect projectionRect)
    {
        if (focus is null || focus.Progress <= .001 || placed.Width <= 1 || placed.Height <= 1 || !crop.Contains(new Point(focus.Center.X, focus.Center.Y))) return scene;
        int width = scene.PixelWidth, height = scene.PixelHeight;
        var outside = scene; var radius = focus.Effect.Blur * 28 * Math.Min(width, height) / 1080.0;
        if (radius > .01)
        {
            var workingScale = Math.Min(1, 1600.0 / Math.Max(width, height));
            BitmapSource working = workingScale < 1 ? new TransformedBitmap(scene, new ScaleTransform(workingScale, workingScale)) : scene;
            var blurred = BlurClamped(working, radius * workingScale);
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawImage(blurred, new Rect(0, 0, width, height));
            outside = Snapshot(visual, width, height);
        }
        var clear = new byte[width * height * 4]; var defocused = new byte[clear.Length];
        scene.CopyPixels(clear, width * 4, 0); outside.CopyPixels(defocused, width * 4, 0);
        var center = new Point(placed.X + (focus.Center.X - crop.X) / crop.Width * placed.Width,
            placed.Y + (focus.Center.Y - crop.Y) / crop.Height * placed.Height);
        var shortEdge = Math.Min(placed.Width, placed.Height); var halfWidth = shortEdge * focus.Effect.Size / 2;
        bool linear = focus.Effect.Shape == "linear";
        var feather = linear ? Math.Max(shortEdge * focus.Effect.Softness * .4, 1) : Math.Max(halfWidth * focus.Effect.Softness * 1.5, .5);
        var angle = (focus.Effect.AngleDegrees ?? 0) * Math.PI / 180; var normalX = -Math.Sin(angle); var normalY = Math.Cos(angle);
        var mapper = quad?.Projector(new(projectionRect.X, projectionRect.Y, projectionRect.Width, projectionRect.Height));
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            CompositionPoint? point = quad is null ? new(x + .5, y + .5) : mapper?.Unproject(new(x + .5, y + .5));
            var coverage = 0.0;
            if (point is { } p && (quad is null || projectionRect.Contains(new Point(p.X, p.Y))))
            {
                var dx = p.X - center.X; var dy = p.Y - center.Y;
                var distance = linear ? Math.Abs(dx * normalX + dy * normalY) : Math.Sqrt(dx * dx + dy * dy);
                coverage = 1 - Math.Clamp((distance - halfWidth) / feather, 0, 1);
            }
            var amount = focus.Progress * (1 - coverage); var index = (y * width + x) * 4;
            for (int channel = 0; channel < 4; channel++)
            {
                var value = defocused[index + channel] * (channel == 3 ? 1 : 1 - focus.Effect.Dimming);
                clear[index + channel] = (byte)Math.Clamp(Math.Round(clear[index + channel] + (value - clear[index + channel]) * amount), 0, 255);
            }
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, clear, width * 4); result.Freeze(); return result;
    }
    private static BitmapSource BlurClamped(BitmapSource source, double radius)
    {
        var margin = (int)Math.Ceiling(radius * 3); int w = source.PixelWidth, h = source.PixelHeight;
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(margin, margin, w, h));
            dc.DrawImage(new CroppedBitmap(source, new(0, 0, w, 1)), new Rect(margin, 0, w, margin));
            dc.DrawImage(new CroppedBitmap(source, new(0, h - 1, w, 1)), new Rect(margin, margin + h, w, margin));
            dc.DrawImage(new CroppedBitmap(source, new(0, 0, 1, h)), new Rect(0, margin, margin, h));
            dc.DrawImage(new CroppedBitmap(source, new(w - 1, 0, 1, h)), new Rect(margin + w, margin, margin, h));
            foreach (var corner in new[] { (X: 0, Y: 0), (X: w - 1, Y: 0), (X: 0, Y: h - 1), (X: w - 1, Y: h - 1) })
                dc.DrawImage(new CroppedBitmap(source, new(corner.X, corner.Y, 1, 1)), new Rect(corner.X == 0 ? 0 : margin + w, corner.Y == 0 ? 0 : margin + h, margin, margin));
        }
        var extended = Blur(Snapshot(visual, w + margin * 2, h + margin * 2), radius);
        var cropped = new CroppedBitmap(extended, new(margin, margin, w, h)); cropped.Freeze(); return cropped;
    }
    private static BitmapSource Blur(BitmapSource source, double radius)
    {
        var image = new Image { Source = source, Width = source.PixelWidth, Height = source.PixelHeight, Effect = new BlurEffect { Radius = radius } };
        image.Measure(new Size(source.PixelWidth, source.PixelHeight)); image.Arrange(new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        var bitmap = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(image); bitmap.Freeze(); return bitmap;
    }
    private static RenderTargetBitmap Snapshot(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    private static Color Parse(string color) => (Color)ColorConverter.ConvertFromString(color);

    private static BitmapSource DrawPointer(BitmapSource source, EditorState state, PointerSample sample, Rect placed, Rect crop,
        ProjectedScreenQuad? quad, Rect projectionRect, int nativeWidth, double opacity)
    {
        var style = state.Pointer;
        double localX = (sample.Location.X - crop.X) / crop.Width, localY = (sample.Location.Y - crop.Y) / crop.Height;
        if (localX is < 0 or > 1 || localY is < 0 or > 1 || opacity <= 0) return source;
        double x = placed.X + localX * placed.Width, y = placed.Y + localY * placed.Height;
        var mapper = quad?.Projector(new(projectionRect.X, projectionRect.Y, projectionRect.Width, projectionRect.Height));
        var assetId = style.AssetId is { } selectedId && selectedId != "system.automatic" ? selectedId : sample.RecordedCursorAssetId;
        bool hidden = assetId is "hidden" or "system.hidden";
        if (assetId == "system.hidden") return source;
        var asset = !hidden && (style.AssetId is not null || style.Identity == "录制光标")
            ? state.CursorImages.FirstOrDefault(i => i.Id == assetId) ?? state.CursorImages.FirstOrDefault(i => i.Id == "system.arrow") : null;
        var imagePath = state.ResolveAsset(asset?.RelativePath);
        if (asset is not null && (imagePath is null || !File.Exists(imagePath)))
        {
            asset = state.CursorImages.FirstOrDefault(i => i.Id == "system.arrow"); imagePath = state.ResolveAsset(asset?.RelativePath);
        }
        double canonicalHeight = Math.Max(Math.Min(source.PixelWidth, source.PixelHeight) * 44.0 / 1080 * style.Scale, 1);
        double nativeScale = placed.Width / Math.Max(1, nativeWidth * crop.Width);
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4]; converted.CopyPixels(pixels, source.PixelWidth * 4, 0);
        if (sample.ClickPhase is { } click && style.ClickFeedback != "关闭")
        {
            var clickImage = CursorClickImage(style.UseAssetClickColor ? style with { ClickColor = (asset?.Id ?? assetId) == "system.not-allowed" ? "#FFFF6464" : "#FFEDA647" } : style, click.Progress, canonicalHeight);
            Composite(clickImage, clickImage.PixelWidth / 2.0, clickImage.PixelHeight / 2.0, canonicalHeight / 44, 0, null);
        }
        if (!state.EmbeddedCursor && !hidden)
        {
            BitmapSource image; double hotspotX, hotspotY, imageScale; BitmapSource? mask = null;
            if (asset is not null && imagePath is not null && File.Exists(imagePath))
            {
                image = LoadImage(imagePath, asset.Width, asset.Height);
                if (image.PixelWidth != asset.Width || image.PixelHeight != asset.Height) throw new InvalidDataException("光标素材尺寸与清单不符。");
                hotspotX = asset.HotspotX; hotspotY = asset.HotspotY;
                imageScale = asset.ScaleReferenceHeight is { } reference ? canonicalHeight / reference : style.Scale * nativeScale;
                var maskPath = state.ResolveAsset(asset.InvertMaskRelativePath);
                if (maskPath is not null && File.Exists(maskPath))
                {
                    mask = LoadImage(maskPath, asset.Width, asset.Height);
                    if (mask.PixelWidth != asset.Width || mask.PixelHeight != asset.Height) throw new InvalidDataException("光标反色素材尺寸与清单不符。");
                }
            }
            else
            {
                bool cross = style.Identity == "十字", hand = style.Identity == "手势";
                hotspotX = cross ? 16 : hand ? 7 : 0; hotspotY = cross ? 16 : 0;
                string identity = cross ? "十字" : hand ? "手势" : "箭头";
                if (!FallbackCursors.Value!.TryGetValue(identity, out image!))
                {
                    var glyph = new DrawingVisual();
                    using (var drawing = glyph.RenderOpen())
                    {
                        drawing.PushTransform(new TranslateTransform(hotspotX, hotspotY));
                        if (cross)
                        {
                            drawing.DrawLine(new Pen(Brushes.White, 4), new(-12, 0), new(12, 0)); drawing.DrawLine(new Pen(Brushes.White, 4), new(0, -12), new(0, 12));
                            drawing.DrawLine(new Pen(Brushes.Black, 2), new(-12, 0), new(12, 0)); drawing.DrawLine(new Pen(Brushes.Black, 2), new(0, -12), new(0, 12));
                        }
                        else drawing.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.5), Geometry.Parse(hand
                            ? "M0,0 L0,16 L-4,12 L-7,15 L0,28 L14,28 L18,17 L15,14 L12,17 L12,9 L9,7 L6,10 L6,2 L3,0 Z"
                            : "M0,0 L0,26 L7,20 L12,31 L17,29 L12,18 L22,18 Z"));
                        drawing.Pop();
                    }
                    image = Snapshot(glyph, 32, 32); FallbackCursors.Value[identity] = image;
                }
                imageScale = style.AssetId is null ? style.Scale * nativeScale : canonicalHeight / 32;
            }
            Composite(image, hotspotX, hotspotY, imageScale, sample.RotationRadians, mask);
        }
        var bitmap = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, source.PixelWidth * 4); bitmap.Freeze(); return bitmap;

        void Composite(BitmapSource image, double hotspotX, double hotspotY, double imageScale, double angle, BitmapSource? mask)
        {
            if (!double.IsFinite(imageScale) || imageScale <= 0) return;
            var rgba = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
            var input = new byte[image.PixelWidth * image.PixelHeight * 4]; rgba.CopyPixels(input, image.PixelWidth * 4, 0);
            byte[]? maskBytes = null;
            if (mask is not null) { maskBytes = new byte[input.Length]; new FormatConvertedBitmap(mask, PixelFormats.Bgra32, null, 0).CopyPixels(maskBytes, image.PixelWidth * 4, 0); }
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            CompositionPoint Corner(double ix, double iy)
            {
                double dx = (ix - hotspotX) * imageScale, dy = (iy - hotspotY) * imageScale;
                return new(x + dx * cosine - dy * sine, y + dx * sine + dy * cosine);
            }
            CompositionPoint[] corners = [Corner(0, 0), Corner(image.PixelWidth, 0), Corner(image.PixelWidth, image.PixelHeight), Corner(0, image.PixelHeight)];
            var bounds = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
            if (mapper is null)
            {
                double left = corners.Min(c => c.X) - 1, top = corners.Min(c => c.Y) - 1;
                bounds = new(left, top, corners.Max(c => c.X) - left + 1, corners.Max(c => c.Y) - top + 1);
            }
            // The homography can project sprite pixels beyond the card; inverse sampling visits each visible pixel once.
            var visible = Int32RectFrom(bounds, source.PixelWidth, source.PixelHeight);
            for (int py = visible.Y; py < visible.Y + visible.Height; py++)
                for (int px = visible.X; px < visible.X + visible.Width; px++)
                {
                    CompositionPoint? point = mapper is null ? new CompositionPoint(px + .5, py + .5) : mapper.Unproject(new(px + .5, py + .5));
                    if (point is null) continue;
                    double dx = point.Value.X - x, dy = point.Value.Y - y;
                    double tx = (dx * cosine + dy * sine) / imageScale + hotspotX, ty = (-dx * sine + dy * cosine) / imageScale + hotspotY;
                    if (tx < 0 || ty < 0 || tx >= image.PixelWidth || ty >= image.PixelHeight) continue;
                    int target = (py * source.PixelWidth + px) * 4;
                    if (maskBytes is not null)
                    {
                        int mi = ((int)ty * image.PixelWidth + (int)tx) * 4;
                        if (maskBytes[mi + 3] >= 128)
                            for (int channel = 0; channel < 3; channel++) pixels[target + channel] = (byte)Math.Round(pixels[target + channel] * (1 - opacity) + (pixels[target + channel] ^ maskBytes[mi + channel]) * opacity);
                    }
                    double u = tx - .5, v = ty - .5; int ix = (int)Math.Floor(u), iy = (int)Math.Floor(v); double fx = u - ix, fy = v - iy;
                    double Channel(int channel)
                    {
                        double total = 0;
                        for (int oy = 0; oy <= 1; oy++) for (int ox = 0; ox <= 1; ox++)
                        { int sx = ix + ox, sy = iy + oy; if (sx >= 0 && sy >= 0 && sx < image.PixelWidth && sy < image.PixelHeight) total += input[(sy * image.PixelWidth + sx) * 4 + channel] * (ox == 0 ? 1 - fx : fx) * (oy == 0 ? 1 - fy : fy); }
                        return total * opacity;
                    }
                    double alpha = Channel(3), inverse = 1 - alpha / 255;
                    for (int channel = 0; channel < 3; channel++) pixels[target + channel] = (byte)Math.Clamp(Math.Round(Channel(channel) + pixels[target + channel] * inverse), 0, 255);
                    pixels[target + 3] = (byte)Math.Clamp(Math.Round(alpha + pixels[target + 3] * inverse), 0, 255);
                }
        }
    }

    private static BitmapSource CursorClickImage(PointerAppearance style, double p, double canonicalHeight)
    {
        var visual = new DrawingVisual();
        int size = (int)Math.Ceiling(Math.Max(256, 44 * style.ClickScale * 2.2 + 2)); double center = size / 2.0;
        using (var drawing = visual.RenderOpen())
        {
            double h = 44 * style.ClickScale, opacity = style.ClickOpacity, minimumPixel = 44 / canonicalHeight;
            var color = Parse(style.ClickColor);
            void Circle(double diameter, double strength, bool filled, double line = 0)
            {
                if (diameter <= 0 || strength <= 0) return;
                drawing.PushOpacity(strength);
                bool solid = filled || line >= diameter;
                drawing.DrawEllipse(solid ? new SolidColorBrush(color) : null, solid ? null : new Pen(new SolidColorBrush(color), line), new Point(center, center), (diameter - (solid ? 0 : line)) / 2, (diameter - (solid ? 0 : line)) / 2);
                drawing.Pop();
            }
            if (style.ClickFeedback == "涟漪")
            {
                if (p < .35) Circle(h * .28 * (1 - p / .35), opacity * .85 * (1 - p / .35), true);
                Circle(h * (.45 + 1.45 * (1 - Math.Pow(1 - p, 3))), opacity * .95 * (1 - Math.Pow(p, 1.3)), false, Math.Max(h * (.075 - .04 * p), 1.2 * minimumPixel));
            }
            else if (style.ClickFeedback == "柔和光晕")
            {
                double diameter = p < .18 ? h * (.75 + .5 * (1 - Math.Pow(1 - p / .18, 2))) : h * (1.25 + .55 * (p - .18) / .82);
                double strength = p < .18 ? opacity * .78 * p / .18 : opacity * .78 * (1 - Math.Pow((p - .18) / .82, 1.4));
                var gradient = new RadialGradientBrush(color, Colors.Transparent); drawing.PushOpacity(strength); drawing.DrawEllipse(gradient, null, new Point(center, center), diameter / 2, diameter / 2); drawing.Pop();
            }
            else if (style.ClickFeedback == "弹性双环")
            {
                if (p <= .55) Circle(h * (.25 + .45 * Math.Sin(p / .55 * Math.PI)), opacity * .55 * (1 - p / .55), true);
                Circle(h * (.5 + 1.25 * (1 - Math.Pow(1 - p, 2.5))), opacity * .9 * (1 - p), false, Math.Max(h * .065, 1.2 * minimumPixel));
            }
            else if (style.ClickFeedback == "聚焦微闪")
            {
                Circle(Math.Max(h * (.35 - .18 * p), minimumPixel), opacity * .95 * (1 - Math.Pow(p, 2)), true);
                Circle(h * (.45 + .55 * p), opacity * .55 * (1 - p), false, Math.Max(h * .035, minimumPixel));
                double offset = h * (.28 + .38 * (1 - Math.Pow(1 - p, 2))), dotSize = Math.Max(h * .075 * (1 - p), 1.2 * minimumPixel);
                drawing.PushOpacity(opacity * .85 * (1 - p));
                foreach (var point in new[] { new Point(center, center - offset), new Point(center, center + offset), new Point(center - offset, center), new Point(center + offset, center) }) drawing.DrawEllipse(new SolidColorBrush(color), null, point, dotSize / 2, dotSize / 2);
                drawing.Pop();
            }
        }
        // shortcut: Core Image and WPF antialiasing differ; compare Mac golden frames before claiming pixel equivalence.
        return Snapshot(visual, size, size);
    }


    private static Int32Rect CameraCropRect(BitmapSource source, CameraStyle style)
    {
        int left = Math.Clamp((int)(style.CropLeft * source.PixelWidth), 0, source.PixelWidth - 1);
        int top = Math.Clamp((int)(style.CropTop * source.PixelHeight), 0, source.PixelHeight - 1);
        return new(left, top,
            Math.Clamp((int)((1 - style.CropLeft - style.CropRight) * source.PixelWidth), 1, source.PixelWidth - left),
            Math.Clamp((int)((1 - style.CropTop - style.CropBottom) * source.PixelHeight), 1, source.PixelHeight - top));
    }

    private static Rect CameraLayoutRect(CameraStyle style, int width, int height, Int32Rect crop)
    {
        double cameraWidth = style.Width * width;
        double cameraHeight = style.Shape switch
        {
            "圆形" or "方形" => cameraWidth, "横向" => cameraWidth * 9 / 16,
            "纵向" => cameraWidth * 16 / 9, "原始比例" => cameraWidth * crop.Height / crop.Width,
            _ => style.Height * height
        };
        return new(style.X * width, style.Y * height, cameraWidth, cameraHeight);
    }

    private static void DrawCamera(DrawingContext dc, BitmapSource source, CameraStyle style, int width, int height, CameraMotionSample? motion = null, Rect? evaluatedRect = null)
    {
        var crop = CameraCropRect(source, style);
        var cropped = new CroppedBitmap(source, crop);
        var rect = evaluatedRect ?? CameraLayoutRect(style, width, height, crop);
        var cameraWidth = rect.Width; var cameraHeight = rect.Height;
        var radius = style.Shape == "圆形" ? Math.Min(cameraWidth, cameraHeight) / 2 : Math.Min(cameraWidth, cameraHeight) * style.Corner;
        if (motion is not null)
        {
            rect = evaluatedRect ?? ToRect(SceneMotionTimeline.CameraRect(motion, width, height));
            cameraWidth = rect.Width; cameraHeight = rect.Height; radius = Math.Min(cameraWidth, cameraHeight) * motion.CornerFraction;
        }
        dc.PushOpacity(motion?.Opacity ?? 1);
        dc.PushTransform(new RotateTransform(motion?.Rotation ?? style.Rotation, rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
        if (style.LegacySize is not null && style.ShadowStrength > 0)
        {
            var styleScale = Math.Min(width, height) / 1080.0;
            var blur = 14 * styleScale; var margin = Math.Ceiling(blur * 2);
            var shape = new System.Windows.Controls.Border { Width = rect.Width, Height = rect.Height, Background = Brushes.Black,
                CornerRadius = new CornerRadius(radius), Effect = new BlurEffect { Radius = blur } };
            shape.Measure(new Size(rect.Width, rect.Height)); shape.Arrange(new Rect(0, 0, rect.Width, rect.Height));
            var shadowVisual = new DrawingVisual(); using (var context = shadowVisual.RenderOpen())
                context.DrawRectangle(new VisualBrush(shape), null, new Rect(margin, margin, rect.Width, rect.Height));
            var shadow = Snapshot(shadowVisual, (int)Math.Ceiling(rect.Width + margin * 2), (int)Math.Ceiling(rect.Height + margin * 2));
            dc.PushOpacity(style.ShadowStrength); dc.DrawImage(shadow, new Rect(rect.X - margin, rect.Y + 8 * styleScale - margin, shadow.PixelWidth, shadow.PixelHeight)); dc.Pop();
        }
        else if (style.LegacySize is null && style.Shadow > 0)
        {
            var size = Math.Min(width, height) * style.Shadow;
            var shadow = new Rect(rect.X + size / 2, rect.Y + size, rect.Width, rect.Height);
            if (style.Shape == "圆形" && motion is null) dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), null, new Point(shadow.X + shadow.Width / 2, shadow.Y + shadow.Height / 2), shadow.Width / 2, shadow.Height / 2);
            else dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), null, shadow, radius, radius);
        }
        dc.PushClip(style.Shape == "圆形" && motion is null ? new EllipseGeometry(rect) : new RectangleGeometry(rect, radius, radius));
        var scale = Math.Max(cameraWidth / cropped.PixelWidth, cameraHeight / cropped.PixelHeight) * style.ContentScale;
        var content = new Rect(rect.X + (cameraWidth - cropped.PixelWidth * scale) * style.ContentPositionX, rect.Y + (cameraHeight - cropped.PixelHeight * scale) * style.ContentPositionY, cropped.PixelWidth * scale, cropped.PixelHeight * scale);
        if (style.Mirror) dc.PushTransform(new ScaleTransform(-1, 1, rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
        dc.DrawImage(cropped, content); if (style.Mirror) dc.Pop(); dc.Pop();
        if (style.BorderWidth > 0) dc.DrawRoundedRectangle(null, new Pen(Brushes.White, style.BorderWidth * Math.Min(width, height) / 1080), rect, radius, radius);
        dc.Pop(); dc.Pop();
    }
    private static Rect ToRect(CompositionRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    // Deterministic perspective projection is shared by preview and export; this CPU path trades speed for portability.
    private static BitmapSource Project(BitmapSource card, int width, int height, double x, double y, double rx, double ry, double rz, double perspective, CompositionPoint anchor)
    {
        var quad = ScreenProjection.Project(new(x, y, card.PixelWidth, card.PixelHeight), rx, ry, rz, perspective, anchor);
        var mapper = quad.Projector(new(0, 0, card.PixelWidth, card.PixelHeight));
        var input = new byte[card.PixelWidth * card.PixelHeight * 4]; card.CopyPixels(input, card.PixelWidth * 4, 0);
        var result = new byte[width * height * 4];
        for (var py = 0; py < height; py++)
            for (var px = 0; px < width; px++)
            {
                var source = mapper?.Unproject(new(px, py)); if (source is null) continue;
                var sx = source.Value.X; var sy = source.Value.Y;
                if (sx < 0 || sy < 0 || sx >= card.PixelWidth || sy >= card.PixelHeight) continue;
                var from = ((int)sy * card.PixelWidth + (int)sx) * 4;
                var to = (py * width + px) * 4;
                Buffer.BlockCopy(input, from, result, to, 4);
            }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, result, width * 4); bitmap.Freeze(); return bitmap;
    }
}
