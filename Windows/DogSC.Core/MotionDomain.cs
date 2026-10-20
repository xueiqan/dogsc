namespace DogSC.Core;

public readonly record struct NormalizedPoint(double X, double Y)
{
    public NormalizedPoint Constrained(double safeInset = 0)
    {
        double inset = Math.Clamp(safeInset, 0, 0.5);
        return new(Math.Clamp(X, inset, 1 - inset), Math.Clamp(Y, inset, 1 - inset));
    }
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && X is >= 0 and <= 1 && Y is >= 0 and <= 1;
}

public enum PointerEventKind { Move, LeftClick, RightClick }
public sealed record PointerEventRecord(double Time, NormalizedPoint Location, PointerEventKind Kind,
    string[]? Modifiers = null, string? CursorAssetId = null);
public enum ZoomOrigin { Automatic, Manual }
public enum ZoomEasingPreset { Linear, Quadratic, Cubic, Quintic, Spring, Custom }
public readonly record struct ZoomBezierCurve(double X1, double Y1, double X2, double Y2)
{
    public static ZoomBezierCurve Cubic => new(0.65, 0, 0.35, 1);
}

public sealed record ZoomAnimationClip(Guid Id, double StartTime, double EndTime, double Scale,
    NormalizedPoint Focus, ZoomOrigin Origin = ZoomOrigin.Manual, ZoomEasingPreset Easing = ZoomEasingPreset.Cubic,
    double EnterDuration = 0.7, double ExitDuration = 0.7,
    double EnterProgressOffset = 0, double ExitProgressOffset = 0,
    double? PreferredEnterDuration = null, double? PreferredExitDuration = null,
    ZoomBezierCurve? CustomCurve = null, FocusEffectStyle? FocusEffect = null)
{
    public double Duration => Math.Max(EndTime - StartTime, 0);
    public double EffectEndTime => EndTime + ExitDuration;
    public double RequestedEnterDuration => PreferredEnterDuration ?? EnterDuration;
    public double RequestedExitDuration => PreferredExitDuration ?? (ExitDuration > 0.0001 ? ExitDuration : 0.7);
    public void Validate()
    {
        FocusEffect?.Validate();
        if (!double.IsFinite(StartTime) || !double.IsFinite(EndTime) || StartTime < 0 || EndTime < StartTime
            || !double.IsFinite(Scale) || Scale < 1 || !Focus.IsValid
            || !double.IsFinite(EnterDuration) || EnterDuration is < 0 or > 5
            || !double.IsFinite(ExitDuration) || ExitDuration is < 0 or > 5
            || !double.IsFinite(EnterProgressOffset) || EnterProgressOffset is < 0 or > 1
            || !double.IsFinite(ExitProgressOffset) || ExitProgressOffset is < 0 or > 1
            || PreferredEnterDuration is { } preferredEnter && (!double.IsFinite(preferredEnter) || preferredEnter is < 0 or > 5)
            || PreferredExitDuration is { } preferredExit && (!double.IsFinite(preferredExit) || preferredExit is < 0 or > 5)
            || CustomCurve is { } curve && new[] { curve.X1, curve.Y1, curve.X2, curve.Y2 }.Any(v => !double.IsFinite(v) || v is < 0 or > 1))
            throw new ArgumentException("缩放片段参数无效。");
    }
}

public static class ZoomTransitionResolution
{
    public static ZoomAnimationClip[] Resolve(IEnumerable<ZoomAnimationClip> animations, double outputDuration)
    {
        if (!double.IsFinite(outputDuration) || outputDuration <= 0) return [];
        var ordered = animations.Where(c => c.StartTime < outputDuration && c.Duration > 0.001)
            .OrderBy(c => c.StartTime).ThenBy(c => c.Id).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            var clip = ordered[i];
            double enter = Math.Max(clip.RequestedEnterDuration, 0), exit = Math.Max(clip.RequestedExitDuration, 0);
            if (i + 1 < ordered.Length)
            {
                double gap = Math.Max(ordered[i + 1].StartTime - clip.EndTime, 0);
                clip = clip with { ExitDuration = gap <= AutoZoomPlanner.AdjacencyTolerance ? 0 : Math.Min(exit, gap) };
            }
            else
            {
                double available = Math.Max(outputDuration - clip.StartTime, 0);
                double resolvedExit = Math.Min(exit, Math.Max(available - Math.Min(enter, available * 0.5), 0));
                double end = Math.Min(clip.EndTime, outputDuration - resolvedExit);
                clip = clip with { EndTime = end, ExitDuration = Math.Min(exit, Math.Max(outputDuration - end, 0)),
                    ExitProgressOffset = end < clip.EndTime - 0.000001 ? 0 : clip.ExitProgressOffset };
            }
            ordered[i] = clip with { EnterDuration = Math.Min(enter, clip.Duration) };
        }
        return ordered;
    }
}

public static class AutoZoomPlanner
{
    public const double AdjacencyTolerance = 1.0 / 120;

    public static ZoomAnimationClip[] MakeAnimations(IEnumerable<PointerEventRecord> pointerEvents,
        double zoomScale = 1.6, ZoomEasingPreset easing = ZoomEasingPreset.Spring, double transitionDuration = 0.7)
    {
        if (!double.IsFinite(zoomScale) || zoomScale < 1 || !double.IsFinite(transitionDuration))
            throw new ArgumentException("自动缩放参数无效。");
        var events = pointerEvents.Where(e => double.IsFinite(e.Time) && e.Time >= 0 && e.Location.IsValid)
            .OrderBy(e => e.Time).ToArray();
        var clicks = events.Where(e => e.Kind is PointerEventKind.LeftClick or PointerEventKind.RightClick).ToArray();
        double transition = Math.Clamp(transitionDuration, 0, 5);
        var planned = MakeClickAnimations(clicks, zoomScale, enterDuration: transition,
            exitDuration: transition, easing: easing).ToArray();
        for (int i = 0; i < planned.Length; i++)
        {
            var clip = planned[i];
            if (clip.ExitDuration <= 0.001) continue;
            var lastClick = clicks.LastOrDefault(c => c.Time <= clip.EndTime + 0.0001);
            if (lastClick is null || lastClick.Time < clip.StartTime - 0.0001) continue;
            double nextStart = i + 1 < planned.Length ? planned[i + 1].StartTime : double.PositiveInfinity;
            double activityEnd = lastClick.Time;
            foreach (var e in events.Where(e => e.Time > lastClick.Time))
            {
                if (e.Time >= nextStart - 0.0001 || e.Time - activityEnd > 1.4) break;
                activityEnd = e.Time;
            }
            if (activityEnd <= lastClick.Time + 0.0001) continue;
            double extended = Math.Min(Math.Max(clip.EndTime, activityEnd + 0.55), nextStart);
            if (extended <= clip.EndTime + 0.0001) continue;
            planned[i] = clip with { EndTime = extended,
                ExitDuration = Math.Min(clip.ExitDuration, Math.Max(nextStart - extended, 0)) };
        }
        var merged = new List<ZoomAnimationClip>();
        foreach (var clip in planned)
        {
            var previous = merged.LastOrDefault();
            if (previous is not null && previous.ExitDuration <= AdjacencyTolerance
                && Math.Abs(previous.EndTime - clip.StartTime) <= AdjacencyTolerance
                && Math.Abs(previous.Scale - clip.Scale) <= 0.000001
                && previous.Easing == clip.Easing && previous.CustomCurve == clip.CustomCurve)
                merged[^1] = previous with { EndTime = Math.Max(previous.EndTime, clip.EndTime), ExitDuration = clip.ExitDuration };
            else merged.Add(clip);
        }
        return merged.ToArray();
    }

    public static ZoomAnimationClip[] MakeClickAnimations(IEnumerable<PointerEventRecord> clicks,
        double zoomScale = 1.6, double enterDuration = 0.7, double holdDuration = 0.9,
        double exitDuration = 0.7, double clickGroupGap = 1.4, double safeInset = 0.12,
        ZoomEasingPreset easing = ZoomEasingPreset.Spring)
    {
        if (!double.IsFinite(zoomScale) || zoomScale < 1
            || new[] { enterDuration, holdDuration, exitDuration, clickGroupGap, safeInset }.Any(v => !double.IsFinite(v) || v < 0))
            throw new ArgumentException("自动缩放规划参数无效。");
        var temporal = new List<List<PointerEventRecord>>();
        foreach (var click in clicks.Where(c => double.IsFinite(c.Time) && c.Time >= 0 && c.Location.IsValid).OrderBy(c => c.Time))
        {
            if (temporal.Count > 0 && click.Time - temporal[^1][^1].Time <= clickGroupGap) temporal[^1].Add(click);
            else temporal.Add([click]);
        }
        var result = new List<ZoomAnimationClip>();
        double earliestStart = 0;
        foreach (var group in temporal)
        {
            var focusGroups = new List<List<PointerEventRecord>>();
            double maximumXSpan = 0.5 / zoomScale, maximumYSpan = 0.7 / zoomScale;
            double minX = 0, maxX = 0, minY = 0, maxY = 0;
            foreach (var click in group)
            {
                if (focusGroups.Count > 0 && Math.Max(maxX, click.Location.X) - Math.Min(minX, click.Location.X) <= maximumXSpan
                    && Math.Max(maxY, click.Location.Y) - Math.Min(minY, click.Location.Y) <= maximumYSpan)
                {
                    focusGroups[^1].Add(click);
                    minX = Math.Min(minX, click.Location.X); maxX = Math.Max(maxX, click.Location.X);
                    minY = Math.Min(minY, click.Location.Y); maxY = Math.Max(maxY, click.Location.Y);
                }
                else
                {
                    focusGroups.Add([click]); minX = maxX = click.Location.X; minY = maxY = click.Location.Y;
                }
            }
            var starts = new double[focusGroups.Count];
            starts[0] = Math.Max(Math.Max(group[0].Time - enterDuration * 0.18, earliestStart), 0);
            for (int i = 1; i < starts.Length; i++) starts[i] = Math.Max(focusGroups[i][0].Time - enterDuration, starts[i - 1] + 0.16);
            for (int i = 0; i < starts.Length; i++)
            {
                bool last = i == starts.Length - 1;
                double end = last ? Math.Max(group[^1].Time + holdDuration, starts[i] + enterDuration) : starts[i + 1];
                if (end - starts[i] <= 0.01) continue;
                var focus = focusGroups[i];
                var center = new NormalizedPoint((focus.Min(c => c.Location.X) + focus.Max(c => c.Location.X)) / 2,
                    (focus.Min(c => c.Location.Y) + focus.Max(c => c.Location.Y)) / 2).Constrained(safeInset);
                result.Add(new(Guid.NewGuid(), starts[i], end, zoomScale, center, ZoomOrigin.Automatic, easing,
                    Math.Clamp(enterDuration, 0, 5), last ? Math.Clamp(exitDuration, 0, 5) : 0));
            }
            earliestStart = result.LastOrDefault()?.EffectEndTime ?? group[^1].Time;
        }
        return result.ToArray();
    }

    public static ZoomAnimationClip[] Clipped(IEnumerable<ZoomAnimationClip> animations, double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0) return [];
        return animations.Where(c => c.StartTime < duration).Select(c => c with
        {
            EndTime = Math.Min(c.EndTime, duration),
            ExitDuration = Math.Min(c.ExitDuration, Math.Max(duration - Math.Min(c.EndTime, duration), 0))
        }).Where(c => c.Duration > 0.01).ToArray();
    }
}
