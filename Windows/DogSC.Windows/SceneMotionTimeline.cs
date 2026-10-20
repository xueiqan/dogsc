using System.IO;
using System.Text.Json;
using DogSC.Core;

namespace DogSC.Windows;

public sealed record CameraMotionState(NormalizedPoint Position, double Size = .35, string Layout = "方形",
    double Roundness = .5, double Opacity = 1, double Rotation = 0);
public sealed record CameraMotionClip(Guid Id, TransitionTiming Timing, CameraMotionState Target, Guid? GroupId = null);
public sealed record CameraMotionSample(NormalizedPoint Position, double Size, double AspectRatio,
    double CornerFraction, double FullscreenProgress, double Opacity, double Rotation, CompositionRect? Bounds = null);
public sealed record MotionTracks(ScreenMotionClip[] Screen, CameraMotionClip[] Camera);

/// <summary>Time-based, seek-independent motion, with Swift camera layout and hidden-transition semantics.</summary>
public static class SceneMotionTimeline
{
    public static MotionTracks ReadLegacy(JsonElement timeline, ICollection<string> unsupported)
    {
        var screen = timeline.TryGetProperty("screenMotionClips", out var screens) ? screens.EnumerateArray().Select(c =>
        {
            var t = c.GetProperty("target");
            return new ScreenMotionClip(Identity(c, "id") ?? Guid.NewGuid(), Timing(c.GetProperty("timing")),
                new(Point(t.GetProperty("position")), Number(t, "scale", 1), Number(t, "rotationX", 0), Number(t, "rotationY", 0), Number(t, "rotationZ", 0), Number(t, "perspective", .65)), Identity(c, "groupID"), ReadFocus(c));
        }).ToArray() : [];
        var camera = timeline.TryGetProperty("cameraMotionClips", out var cameras) ? cameras.EnumerateArray().Select(c =>
        {
            var t = c.GetProperty("target"); var layout = t.GetProperty("layout");
            string kind = layout.GetProperty("kind").GetString()!;
            return new CameraMotionClip(Identity(c, "id") ?? Guid.NewGuid(), Timing(c.GetProperty("timing")),
                new(Point(t.GetProperty("position")), Number(t, "size", .35), kind switch { "fullscreen" => "全屏", "shape" => layout.GetProperty("shape").GetString()!, _ => throw new InvalidDataException("未知摄像头布局kind。") },
                    Number(t, "roundness", .5), Number(t, "opacity", 1)), Identity(c, "groupID"));
        }).ToArray() : [];
        return new(screen, camera);
    }
    public static FocusEffectStyle? ReadFocus(JsonElement clip)
    {
        if (!clip.TryGetProperty("focusEffect", out var effect) || effect.ValueKind == JsonValueKind.Null) return null;
        if (effect.ValueKind != JsonValueKind.Object) throw new InvalidDataException("聚焦效果格式无效。");
        var result = new FocusEffectStyle(Text(effect, "shape", null), Optional(effect, "angleDegrees"), Text(effect, "target", "animation")!,
            effect.TryGetProperty("center", out var center) ? Point(center) : null, Number(effect, "size", .45), Number(effect, "blur", .35), Number(effect, "softness", .5), Number(effect, "dimming", .18));
        result.Validate(); return result;
    }
    private static TransitionTiming Timing(JsonElement t)
    {
        ZoomBezierCurve? curve = t.TryGetProperty("customCurve", out var c) ? new(Number(c, "x1", .65), Number(c, "y1", 0), Number(c, "x2", .35), Number(c, "y2", 1)) : null;
        var easing = Text(t, "easing", "三次方缓动");
        return new(Number(t, "startTime", 0), Number(t, "duration", .7), Number(t, "leadInDuration", .55), Number(t, "returnDuration", 0), easing switch
        { "线性" => ZoomEasingPreset.Linear, "二次方缓动" => ZoomEasingPreset.Quadratic, "三次方缓动" => ZoomEasingPreset.Cubic, "五次平滑" => ZoomEasingPreset.Quintic,
            "弹簧" => ZoomEasingPreset.Spring, "自定义曲线" => ZoomEasingPreset.Custom, _ => throw new InvalidDataException("未知运动缓动曲线。") },
            Number(t, "leadInProgressOffset", 0), Number(t, "returnProgressOffset", 0), curve, Optional(t, "preferredLeadInDuration"), Optional(t, "preferredReturnDuration"));
    }
    private static Guid? Identity(JsonElement e, string name) => e.TryGetProperty(name, out var id) && id.ValueKind != JsonValueKind.Null ? Guid.Parse(id.GetString()!) : null;
    private static NormalizedPoint Point(JsonElement e) => new(Number(e, "x", .5), Number(e, "y", .5));
    private static double Number(JsonElement e, string name, double fallback) => Optional(e, name) ?? fallback;
    private static double? Optional(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException("运动参数必须是对象。");
        if (!e.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new InvalidDataException($"{name}必须是有限数字。");
        return number;
    }
    private static string? Text(JsonElement e, string name, string? fallback)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException("运动参数必须是对象。");
        if (!e.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name}必须是字符串。");
        return value.GetString();
    }

    public static ScreenMotionState Screen(IEnumerable<ScreenMotionClip> clips, double time, ScreenMotionState basis, MotionStyle? motion = null)
        => ScreenMotionInterpolator.Sample(clips, time, basis, motion);

    public static CameraMotionSample Camera(IEnumerable<CameraMotionClip> clips, double time,
        CameraMotionState basis, double cameraAspectRatio, double canvasAspectRatio, MotionStyle? motion = null,
        CameraMotionSample? basisSample = null, Func<CameraMotionSample, CompositionRect>? resolveBounds = null)
    {
        var baseSample = basisSample ?? Resolve(basis, cameraAspectRatio, canvasAspectRatio);
        if (!double.IsFinite(time) || time < 0) return baseSample;
        var ordered = clips.OrderBy(c => c.Timing.StartTime).ThenBy(c => c.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        var inherited = baseSample; int previous = -1;
        bool Touches(int i) => i + 1 < ordered.Length && Math.Abs(ordered[i + 1].Timing.StartTime - ordered[i].Timing.EndTime) <= AutoZoomPlanner.AdjacencyTolerance;
        CameraMotionSample At(int i, CameraMotionSample origin, double sampleTime)
        {
            var clip = ordered[i]; var t = clip.Timing;
            var target = Resolve(clip.Target, cameraAspectRatio, canvasAspectRatio);
            if (resolveBounds is not null) target = target with { Bounds = resolveBounds(target) };
            if (sampleTime < t.EndTime)
            {
                double lead = Math.Min(t.LeadInDuration, t.Duration);
                var linear = lead > .0001 ? t.LeadInProgressOffset + (1 - t.LeadInProgressOffset) * Math.Clamp((sampleTime - t.StartTime) / lead, 0, 1) : 1;
                return Transition(origin, target, ZoomInterpolator.EasedProgress(linear, t.Easing, t.CustomCurve, motion));
            }
            if (t.ReturnDuration <= .0001 || Touches(i)) return target;
            if (sampleTime > t.EffectEndTime) return baseSample;
            var p = t.ReturnProgressOffset + (1 - t.ReturnProgressOffset) * Math.Clamp((sampleTime - t.EndTime) / t.ReturnDuration, 0, 1);
            return Transition(target, baseSample, ZoomInterpolator.EasedProgress(p, t.Easing, t.CustomCurve, motion));
        }
        for (int i = 0; i < ordered.Length; i++)
        {
            var t = ordered[i].Timing;
            if (time < t.StartTime) break;
            if (i + 1 < ordered.Length && ordered[i + 1].Timing.StartTime == t.StartTime) continue;
            if (previous >= 0) inherited = At(previous, inherited, t.StartTime);
            previous = i;
        }
        return previous >= 0 ? At(previous, inherited, time) : baseSample;
    }

    public static CompositionRect CameraRect(CameraMotionSample sample, double canvasWidth, double canvasHeight,
        double screenZoomScale = 1, double scaleDuringZoom = 1)
    {
        if (sample.Bounds is { } bounds) return bounds;
        var shortEdge = Math.Min(canvasWidth, canvasHeight);
        var zoomScale = 1 + (scaleDuringZoom - 1) * Math.Clamp((screenZoomScale - 1) / .565, 0, 1);
        double height = Math.Clamp(shortEdge * sample.Size * zoomScale, 72 * Math.Max(shortEdge / 1080, .0001), shortEdge * 1.2);
        double width = height * Math.Max(sample.AspectRatio, .01);
        var fit = Math.Min(1, Math.Min(canvasWidth * 1.2 / Math.Max(width, .0001), canvasHeight * 1.2 / Math.Max(height, .0001)));
        width *= fit; height *= fit;
        width += (canvasWidth - width) * sample.FullscreenProgress;
        height += (canvasHeight - height) * sample.FullscreenProgress;
        return new((canvasWidth - width) * sample.Position.X, (canvasHeight - height) * sample.Position.Y, width, height);
    }

    private static CameraMotionSample Resolve(CameraMotionState state, double cameraAspect, double canvasAspect)
    {
        double aspect = state.Layout switch { "圆形" or "方形" => 1, "横向" => 1.5, "纵向" => .72,
            "原始比例" => Math.Max(cameraAspect, .01), "全屏" => Math.Max(canvasAspect, .01), _ => throw new InvalidDataException("摄像头运动形状未知。") };
        double corner = state.Layout switch { "圆形" => .5, "全屏" => 0, _ => .5 * state.Roundness };
        return new(state.Position, state.Size, aspect, corner, state.Layout == "全屏" ? 1 : 0, state.Opacity, state.Rotation);
    }
    private static CameraMotionSample Transition(CameraMotionSample origin, CameraMotionSample to, double progress)
    {
        double p = Math.Clamp(progress, 0, 1);
        double Mix(double a, double b) => a + (b - a) * p;
        // A hidden camera fades at its existing geometry; reappearing camera fades at the destination geometry.
        if (to.Opacity < .01) return origin with { Opacity = Mix(origin.Opacity, 0) };
        if (origin.Opacity < .01) return to with { Opacity = Mix(0, to.Opacity) };
        return new(new(Mix(origin.Position.X, to.Position.X), Mix(origin.Position.Y, to.Position.Y)), Mix(origin.Size, to.Size),
            Mix(origin.AspectRatio, to.AspectRatio), Mix(origin.CornerFraction, to.CornerFraction),
            Mix(origin.FullscreenProgress, to.FullscreenProgress), Mix(origin.Opacity, to.Opacity), Mix(origin.Rotation, to.Rotation),
            origin.Bounds is { } a && to.Bounds is { } b
                ? new(Mix(a.X, b.X), Mix(a.Y, b.Y), Mix(a.Width, b.Width), Mix(a.Height, b.Height)) : null);
    }

    public static void Validate(MotionTracks tracks, double outputDuration)
    {
        if (!double.IsFinite(outputDuration) || outputDuration <= 0) throw new InvalidDataException("运动时间线时长无效。");
        if (tracks.Screen is null || tracks.Camera is null || tracks.Screen.Length > 10000 || tracks.Camera.Length > 10000)
            throw new InvalidDataException("运动轨道不能超过10000条。");
        foreach (var clip in tracks.Screen)
        {
            if (clip is null || clip.Timing is null || clip.Target is null) throw new InvalidDataException("屏幕运动片段为空。");
            clip.FocusEffect?.Validate();
            ValidateTiming(clip.Timing, outputDuration);
            var s = clip.Target;
            if (!s.Position.IsValid || new[] { s.Scale, s.RotationX, s.RotationY, s.RotationZ, s.Perspective }.Any(v => !double.IsFinite(v))
                || s.Scale is < .05 or > 5 || Math.Abs(s.RotationX) > 75 || Math.Abs(s.RotationY) > 75 || Math.Abs(s.RotationZ) > 180 || s.Perspective is < 0 or > 2)
                throw new InvalidDataException("屏幕运动参数无效。");
        }
        foreach (var clip in tracks.Camera)
        {
            if (clip is null || clip.Timing is null || clip.Target is null) throw new InvalidDataException("摄像头运动片段为空。");
            ValidateTiming(clip.Timing, outputDuration);
            var c = clip.Target;
            if (!c.Position.IsValid || new[] { c.Size, c.Roundness, c.Opacity, c.Rotation }.Any(v => !double.IsFinite(v))
                || c.Size is < .01 or > 2 || c.Roundness is < 0 or > 1 || c.Opacity is < 0 or > 1 || Math.Abs(c.Rotation) > 180)
                throw new InvalidDataException("摄像头运动参数无效。");
            _ = Resolve(c, 1, 1);
        }
        foreach (var group in tracks.Screen.Where(c => c.GroupId is not null).Select(c => (Group: c.GroupId!.Value, Timing: c.Timing))
            .Concat(tracks.Camera.Where(c => c.GroupId is not null).Select(c => (Group: c.GroupId!.Value, Timing: c.Timing))).GroupBy(c => c.Group))
            if (group.Select(c => (c.Timing.StartTime, c.Timing.Duration)).Distinct().Count() != 1)
                throw new InvalidDataException("组合屏幕/摄像头片段必须使用同一时间范围。");
    }

    public static void ValidateTiming(TransitionTiming t, double duration)
    {
        if (new[] { t.StartTime, t.Duration, t.LeadInDuration, t.ReturnDuration, t.LeadInProgressOffset, t.ReturnProgressOffset }.Any(v => !double.IsFinite(v))
            || t.StartTime < 0 || t.Duration <= 0 || t.EndTime > duration + .01 || t.LeadInDuration is < 0 or > 5 || t.ReturnDuration is < 0 or > 5
            || t.LeadInProgressOffset is < 0 or > 1 || t.ReturnProgressOffset is < 0 or > 1 || !Enum.IsDefined(t.Easing)
            || t.CustomCurve is { } curve && new[] { curve.X1, curve.Y1, curve.X2, curve.Y2 }.Any(v => !double.IsFinite(v) || v is < 0 or > 1)
            || t.PreferredLeadInDuration is { } lead && (!double.IsFinite(lead) || lead is < 0 or > 5)
            || t.PreferredReturnDuration is { } back && (!double.IsFinite(back) || back is < 0 or > 5))
            throw new InvalidDataException("运动片段时间或曲线参数无效。");
    }

    public static MotionTracks RemoveGroup(MotionTracks tracks, Guid groupId) => new(tracks.Screen.Where(c => c.GroupId != groupId).ToArray(), tracks.Camera.Where(c => c.GroupId != groupId).ToArray());
    public static MotionTracks Remap(MotionTracks tracks, TimelineMap before, TimelineMap after)
    {
        // Splitting a source segment without changing its clock must not split effects or invent an entrance.
        if (Math.Abs(before.OutputDuration - after.OutputDuration) <= TimelineMap.Epsilon && after.Segments.All(s =>
            before.SourceTime(s.OutputStart) is { } start && Math.Abs(start - s.SourceStart) <= TimelineMap.Epsilon
            && before.SourceTime(Math.Max(s.OutputStart, s.OutputEnd - TimelineMap.Epsilon)) is { } end
            && Math.Abs(end - (s.SourceEnd - TimelineMap.Epsilon * s.PlaybackRate)) <= TimelineMap.Epsilon)) return tracks;
        var groups = new Dictionary<(Guid Group, int Part), Guid>();
        Guid? Group(Guid? original, int index, int count)
        {
            if (original is null || count == 1) return original;
            var key = (original.Value, index);
            if (!groups.TryGetValue(key, out var value)) groups[key] = value = Guid.NewGuid();
            return value;
        }
        TransitionTiming[] Parts(TransitionTiming timing)
        {
            var parts = new List<(double Start, double End, bool KeepsStart, bool KeepsEnd)>();
            foreach (var old in before.Segments)
            {
                double lower = Math.Max(timing.StartTime, old.OutputStart), upper = Math.Min(timing.EndTime, old.OutputEnd);
                if (upper - lower <= TimelineMap.Epsilon) continue;
                double sourceStart = old.SourceStart + (lower - old.OutputStart) * old.PlaybackRate;
                double sourceEnd = old.SourceStart + (upper - old.OutputStart) * old.PlaybackRate;
                foreach (var current in after.Segments)
                {
                    double begin = Math.Max(sourceStart, current.SourceStart), end = Math.Min(sourceEnd, current.SourceEnd);
                    if (end - begin <= TimelineMap.Epsilon) continue;
                    parts.Add((current.OutputStart + (begin - current.SourceStart) / current.PlaybackRate,
                        current.OutputStart + (end - current.SourceStart) / current.PlaybackRate,
                        Math.Abs(lower - timing.StartTime) <= TimelineMap.Epsilon && Math.Abs(begin - sourceStart) <= TimelineMap.Epsilon,
                        Math.Abs(upper - timing.EndTime) <= TimelineMap.Epsilon && Math.Abs(end - sourceEnd) <= TimelineMap.Epsilon));
                }
            }
            var merged = new List<(double Start, double End, bool KeepsStart, bool KeepsEnd)>();
            foreach (var part in parts.OrderBy(p => p.Start))
                if (merged.Count > 0 && Math.Abs(merged[^1].End - part.Start) <= TimelineMap.Epsilon)
                    merged[^1] = (merged[^1].Start, part.End, merged[^1].KeepsStart, part.KeepsEnd);
                else merged.Add(part);
            return merged.Select(p => timing with
            {
                StartTime = p.Start, Duration = p.End - p.Start,
                LeadInDuration = Math.Min(timing.PreferredLeadInDuration ?? timing.LeadInDuration, p.End - p.Start),
                PreferredLeadInDuration = timing.PreferredLeadInDuration ?? timing.LeadInDuration,
                PreferredReturnDuration = timing.PreferredReturnDuration ?? timing.ReturnDuration,
                LeadInProgressOffset = p.KeepsStart ? timing.LeadInProgressOffset : 0,
                ReturnProgressOffset = p.KeepsEnd ? timing.ReturnProgressOffset : 0
            }).ToArray();
        }
        var screens = tracks.Screen.SelectMany(c => { var parts = Parts(c.Timing); return parts.Select((t, i) => c with
            { Id = i == 0 ? c.Id : Guid.NewGuid(), Timing = t, GroupId = Group(c.GroupId, i, parts.Length) }); }).ToArray();
        var cameras = tracks.Camera.SelectMany(c => { var parts = Parts(c.Timing); return parts.Select((t, i) => c with
            { Id = i == 0 ? c.Id : Guid.NewGuid(), Timing = t, GroupId = Group(c.GroupId, i, parts.Length) }); }).ToArray();
        var result = new MotionTracks(screens, cameras); Validate(result, after.OutputDuration); return result;
    }
    public static MotionTracks MoveGroup(MotionTracks tracks, Guid groupId, double startTime)
    {
        var first = tracks.Screen.Where(c => c.GroupId == groupId).Select(c => c.Timing).Concat(tracks.Camera.Where(c => c.GroupId == groupId).Select(c => c.Timing)).FirstOrDefault()
            ?? throw new InvalidOperationException("组合片段不存在。");
        double delta = startTime - first.StartTime;
        return new(tracks.Screen.Select(c => c.GroupId == groupId ? c with { Timing = c.Timing with { StartTime = c.Timing.StartTime + delta } } : c).ToArray(),
            tracks.Camera.Select(c => c.GroupId == groupId ? c with { Timing = c.Timing with { StartTime = c.Timing.StartTime + delta } } : c).ToArray());
    }
    public static MotionTracks ResizeGroup(MotionTracks tracks, Guid groupId, double duration) => new(
        tracks.Screen.Select(c => c.GroupId == groupId ? c with { Timing = c.Timing with { Duration = duration } } : c).ToArray(),
        tracks.Camera.Select(c => c.GroupId == groupId ? c with { Timing = c.Timing with { Duration = duration } } : c).ToArray());
    public static ScreenMotionClip CopyProperties(ScreenMotionClip source, ScreenMotionClip destination) => destination with
    { Target = source.Target, Timing = CopyCurve(source.Timing, destination.Timing) };
    public static CameraMotionClip CopyProperties(CameraMotionClip source, CameraMotionClip destination) => destination with
    { Target = source.Target, Timing = CopyCurve(source.Timing, destination.Timing) };
    private static TransitionTiming CopyCurve(TransitionTiming source, TransitionTiming destination) => destination with
    { Easing = source.Easing, CustomCurve = source.CustomCurve, LeadInDuration = source.LeadInDuration, ReturnDuration = source.ReturnDuration,
        PreferredLeadInDuration = source.PreferredLeadInDuration, PreferredReturnDuration = source.PreferredReturnDuration };
}
