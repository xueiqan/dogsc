namespace DogSC.Core;

public enum CursorMotion { None, Rapid, Medium, Smooth }
public sealed record MotionStyle(double ScreenSpringMass = 2.4, double ScreenSpringStiffness = 210,
    double ScreenSpringDamping = 42, double CursorSpringMass = 2.8, double CursorSpringStiffness = 450,
    double CursorSpringDamping = 66, CursorMotion Cursor = CursorMotion.Smooth,
    double DefaultZoomTransitionDuration = 0.7)
{
    public void Validate()
    {
        if (new[] { ScreenSpringMass, ScreenSpringStiffness, ScreenSpringDamping,
                CursorSpringMass, CursorSpringStiffness, CursorSpringDamping }.Any(v => !double.IsFinite(v) || v <= 0 || v > 10000)
            || !double.IsFinite(DefaultZoomTransitionDuration) || DefaultZoomTransitionDuration is < 0 or > 5)
            throw new ArgumentException("弹簧参数无效。");
    }
}

public sealed record ManualZoomViewportTransition(double FromScale, NormalizedPoint FromFocus, double ToScale, NormalizedPoint ToFocus, double Progress);
public sealed record ZoomSample(double Scale, NormalizedPoint Focus, double FramingProgress = 0, double TargetScale = 1,
    ManualZoomViewportTransition? ManualViewportTransition = null);

public sealed class ZoomAnimationTrack
{
    private readonly ZoomAnimationClip[] _clips;
    private readonly MotionStyle _motion;
    public ZoomAnimationTrack(IEnumerable<ZoomAnimationClip> clips, MotionStyle? motion = null)
    {
        _motion = motion ?? new(); _motion.Validate();
        _clips = clips.OrderBy(c => c.StartTime).ThenBy(c => c.Id).ToArray();
        foreach (var clip in _clips) clip.Validate();
    }

    public ZoomAnimationClip? ActiveClip(double time)
    {
        if (!double.IsFinite(time)) return null;
        for (int i = UpperBound(time) - 1; i >= 0; i--)
            if (time <= _clips[i].EffectEndTime) return _clips[i];
        return null;
    }

    public ZoomSample Sample(double time, NormalizedPoint? automaticFocus = null,
        NormalizedPoint? inheritedAutomaticFocus = null, bool reanchorAutomaticEntry = false)
    {
        var clip = ActiveClip(time);
        if (clip is null) return new(1, new(.5, .5));
        int index = Array.IndexOf(_clips, clip);
        var previous = index > 0 ? _clips[index - 1] : null;
        bool adjacent = previous is not null && Math.Abs(previous.EndTime - clip.StartTime) <= AutoZoomPlanner.AdjacencyTolerance;
        var focus = clip.Origin == ZoomOrigin.Automatic ? automaticFocus ?? clip.Focus : clip.Focus;
        focus = focus.Constrained();
        double enter = Math.Min(adjacent && clip.EnterDuration <= .001 ? _motion.DefaultZoomTransitionDuration : clip.EnterDuration, clip.Duration);
        double progress;
        if (enter > .001 && time < clip.StartTime + enter)
        {
            double offset = reanchorAutomaticEntry ? 0 : clip.EnterProgressOffset;
            progress = ZoomInterpolator.EasedProgress(offset + (1 - offset) * (time - clip.StartTime) / enter,
                clip.Easing, clip.CustomCurve, _motion);
        }
        else if (time <= clip.EndTime) progress = 1;
        else if (clip.ExitDuration > .001)
            progress = 1 - ZoomInterpolator.EasedProgress(clip.ExitProgressOffset + (1 - clip.ExitProgressOffset)
                * (time - clip.EndTime) / clip.ExitDuration, clip.Easing, clip.CustomCurve, _motion);
        else progress = 0;
        if (adjacent && time <= clip.EndTime)
        {
            var from = previous!.Origin == ZoomOrigin.Automatic ? inheritedAutomaticFocus ?? previous.Focus : previous.Focus;
            double scale = previous.Scale + (clip.Scale - previous.Scale) * progress;
            return new(scale, Lerp(from, focus, progress), 1, scale,
                clip.Origin == ZoomOrigin.Manual ? new(previous.Scale, from, clip.Scale, clip.Focus, progress) : null);
        }
        return new(1 + (clip.Scale - 1) * progress, focus, progress, clip.Scale);
    }

    private int UpperBound(double time)
    {
        int low = 0, high = _clips.Length;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (_clips[mid].StartTime <= time) low = mid + 1; else high = mid;
        }
        return low;
    }
    internal static NormalizedPoint Lerp(NormalizedPoint a, NormalizedPoint b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}

public static class ZoomInterpolator
{
    public static ZoomSample Sample(IEnumerable<ZoomAnimationClip> clips, double time, MotionStyle? motion = null,
        NormalizedPoint? automaticFocus = null) => new ZoomAnimationTrack(clips, motion).Sample(time, automaticFocus);

    public static double EasedProgress(double value, ZoomEasingPreset preset,
        ZoomBezierCurve? customCurve = null, MotionStyle? motion = null)
    {
        double t = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        if (preset == ZoomEasingPreset.Linear) return t;
        if (preset == ZoomEasingPreset.Spring)
        {
            var settings = motion ?? new(); settings.Validate();
            if (t is <= 0 or >= 1) return t;
            double mass = Math.Max(settings.ScreenSpringMass, .01), stiffness = Math.Max(settings.ScreenSpringStiffness, .01), damping = Math.Max(settings.ScreenSpringDamping, .01);
            double omega = Math.Sqrt(stiffness / mass), zeta = Math.Min(damping / (2 * Math.Sqrt(stiffness * mass)), .9999);
            double omegaD = omega * Math.Sqrt(1 - zeta * zeta);
            double Response(double seconds) => 1 - Math.Exp(-zeta * omega * seconds)
                * (Math.Cos(omegaD * seconds) + zeta * omega / omegaD * Math.Sin(omegaD * seconds));
            return Math.Clamp(Response(t * .72) / Response(.72), 0, 1);
        }
        var curve = preset switch { ZoomEasingPreset.Quadratic => new ZoomBezierCurve(.45, 0, .55, 1),
            ZoomEasingPreset.Quintic => new(.82, 0, .18, 1), ZoomEasingPreset.Custom => customCurve ?? ZoomBezierCurve.Cubic,
            _ => ZoomBezierCurve.Cubic };
        return BezierValue(curve, t);
    }

    public static double BezierValue(ZoomBezierCurve curve, double time)
    {
        double target = Math.Clamp(time, 0, 1);
        if (target is <= 0 or >= 1) return target;
        NormalizedPoint Point(double p)
        {
            double t = Math.Clamp(p, 0, 1), inverse = 1 - t;
            return new(3 * inverse * inverse * t * curve.X1 + 3 * inverse * t * t * curve.X2 + t * t * t,
                3 * inverse * inverse * t * curve.Y1 + 3 * inverse * t * t * curve.Y2 + t * t * t);
        }
        double parameter = target;
        for (int i = 0; i < 7; i++)
        {
            var point = Point(parameter); double error = point.X - target;
            if (Math.Abs(error) < .00001) return point.Y;
            double inverse = 1 - parameter;
            double derivative = 3 * inverse * inverse * curve.X1 + 6 * inverse * parameter * (curve.X2 - curve.X1)
                + 3 * parameter * parameter * (1 - curve.X2);
            if (Math.Abs(derivative) < .00001) break;
            double next = parameter - error / derivative;
            if (next is < 0 or > 1) break;
            parameter = next;
        }
        double low = 0, high = 1; parameter = target;
        for (int i = 0; i < 18; i++)
        {
            var point = Point(parameter);
            if (Math.Abs(point.X - target) < .00001) return point.Y;
            if (point.X < target) low = parameter; else high = parameter;
            parameter = (low + high) / 2;
        }
        return Point(parameter).Y;
    }
}
