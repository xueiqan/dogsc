namespace DogSC.Core;

public readonly record struct CompositionPoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}
public readonly record struct CompositionRect(double X, double Y, double Width, double Height)
{
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
}
public sealed record ProjectedScreenQuad(CompositionPoint TopLeft, CompositionPoint TopRight,
    CompositionPoint BottomRight, CompositionPoint BottomLeft)
{
    public CompositionPoint[] Corners => [TopLeft, TopRight, BottomRight, BottomLeft];
    public CompositionRect Bounds
    {
        get
        {
            var corners = Corners;
            double minX = corners.Min(p => p.X), minY = corners.Min(p => p.Y);
            return new(minX, minY, corners.Max(p => p.X) - minX, corners.Max(p => p.Y) - minY);
        }
    }
    public ProjectedScreenPointMapper? Projector(CompositionRect sourceRect) => ProjectedScreenPointMapper.Create(this, sourceRect);
    public bool Contains(CompositionPoint point)
    {
        if (!point.IsFinite || Corners.Any(p => !p.IsFinite)) return false;
        var points = Corners; double sign = 0, twiceArea = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = points[i]; var b = points[(i + 1) % 4];
            twiceArea += a.X * b.Y - b.X * a.Y;
            double cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
            if (Math.Abs(cross) <= .000001) continue;
            if (sign != 0 && sign * cross < 0) return false;
            sign = cross;
        }
        return Math.Abs(twiceArea) > .000001;
    }
}

public sealed class ProjectedScreenPointMapper
{
    private readonly CompositionRect _source;
    private readonly CompositionPoint _topLeft;
    private readonly double _a, _b, _d, _e, _px, _py;
    private ProjectedScreenPointMapper(ProjectedScreenQuad quad, CompositionRect source, double px, double py)
    {
        _source = source; _topLeft = quad.TopLeft; _px = px; _py = py;
        _a = quad.TopRight.X - quad.TopLeft.X + px * quad.TopRight.X;
        _b = quad.BottomLeft.X - quad.TopLeft.X + py * quad.BottomLeft.X;
        _d = quad.TopRight.Y - quad.TopLeft.Y + px * quad.TopRight.Y;
        _e = quad.BottomLeft.Y - quad.TopLeft.Y + py * quad.BottomLeft.Y;
    }
    public static ProjectedScreenPointMapper? Create(ProjectedScreenQuad quad, CompositionRect source)
    {
        if (!double.IsFinite(source.X) || !double.IsFinite(source.Y) || !double.IsFinite(source.Width)
            || !double.IsFinite(source.Height) || source.Width <= 0 || source.Height <= 0 || quad.Corners.Any(p => !p.IsFinite)) return null;
        double dx1 = quad.TopRight.X - quad.BottomRight.X, dx2 = quad.BottomLeft.X - quad.BottomRight.X;
        double dx3 = quad.TopLeft.X - quad.TopRight.X + quad.BottomRight.X - quad.BottomLeft.X;
        double dy1 = quad.TopRight.Y - quad.BottomRight.Y, dy2 = quad.BottomLeft.Y - quad.BottomRight.Y;
        double dy3 = quad.TopLeft.Y - quad.TopRight.Y + quad.BottomRight.Y - quad.BottomLeft.Y;
        double denominator = dx1 * dy2 - dx2 * dy1;
        if (Math.Abs(dx3) <= .000000001 && Math.Abs(dy3) <= .000000001) return new(quad, source, 0, 0);
        if (Math.Abs(denominator) <= .000000001) return null;
        return new(quad, source, (dx3 * dy2 - dx2 * dy3) / denominator, (dx1 * dy3 - dx3 * dy1) / denominator);
    }
    public CompositionPoint? Project(CompositionPoint point)
    {
        if (!point.IsFinite) return null;
        double x = (point.X - _source.X) / _source.Width, y = (point.Y - _source.Y) / _source.Height;
        double divisor = _px * x + _py * y + 1;
        if (!double.IsFinite(divisor) || Math.Abs(divisor) <= .000000001) return null;
        var result = new CompositionPoint((_a * x + _b * y + _topLeft.X) / divisor,
            (_d * x + _e * y + _topLeft.Y) / divisor);
        return result.IsFinite ? result : null;
    }
    public CompositionPoint? Unproject(CompositionPoint point)
    {
        if (!point.IsFinite) return null;
        double a = _a - point.X * _px, b = _b - point.X * _py, d = _d - point.Y * _px, e = _e - point.Y * _py;
        double determinant = a * e - b * d;
        if (Math.Abs(determinant) <= .000000001) return null;
        double x = point.X - _topLeft.X, y = point.Y - _topLeft.Y;
        var result = new CompositionPoint(_source.X + (x * e - b * y) / determinant * _source.Width,
            _source.Y + (a * y - x * d) / determinant * _source.Height);
        return result.IsFinite ? result : null;
    }
}

public static class ScreenProjection
{
    public static ProjectedScreenQuad Project(CompositionRect rect, double rotationX, double rotationY,
        double rotationZ, double perspective, CompositionPoint? anchor = null)
    {
        if (new[] { rect.X, rect.Y, rect.Width, rect.Height, rotationX, rotationY, rotationZ, perspective }.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("屏幕投影参数必须为有限数。");
        double width = Math.Max(rect.Width, 0), height = Math.Max(rect.Height, 0);
        var pivot = anchor is { IsFinite: true } p ? p : new CompositionPoint(rect.MidX, rect.MidY);
        double depth = Math.Clamp(perspective, 0, 2), maximum = Math.Max(Math.Max(rect.Width, rect.Height), 1);
        double camera = depth > 0 ? maximum * 2 / Math.Max(depth, .0001) : double.PositiveInfinity;
        double rx = rotationX * Math.PI / 180, ry = rotationY * Math.PI / 180, rz = rotationZ * Math.PI / 180;
        CompositionPoint Point(double x, double y)
        {
            double afterXy = y * Math.Cos(rx), afterXz = y * Math.Sin(rx);
            double afterYx = x * Math.Cos(ry) + afterXz * Math.Sin(ry), afterYz = -x * Math.Sin(ry) + afterXz * Math.Cos(ry);
            double afterZx = afterYx * Math.Cos(rz) - afterXy * Math.Sin(rz), afterZy = afterYx * Math.Sin(rz) + afterXy * Math.Cos(rz);
            double scale = double.IsFinite(camera) ? camera / Math.Max(camera - afterYz, camera * .12) : 1;
            return new(pivot.X + afterZx * scale, pivot.Y + afterZy * scale);
        }
        return new(Point(rect.X - pivot.X, rect.Y - pivot.Y), Point(rect.X + width - pivot.X, rect.Y - pivot.Y),
            Point(rect.X + width - pivot.X, rect.Y + height - pivot.Y), Point(rect.X - pivot.X, rect.Y + height - pivot.Y));
    }
}

public sealed record TransitionTiming(double StartTime, double Duration, double LeadInDuration = .7,
    double ReturnDuration = .7, ZoomEasingPreset Easing = ZoomEasingPreset.Cubic,
    double LeadInProgressOffset = 0, double ReturnProgressOffset = 0, ZoomBezierCurve? CustomCurve = null,
    double? PreferredLeadInDuration = null, double? PreferredReturnDuration = null)
{
    public double EndTime => StartTime + Duration;
    public double EffectEndTime => EndTime + ReturnDuration;
}
public sealed record ScreenMotionState(NormalizedPoint Position, double Scale = 1, double RotationX = 0,
    double RotationY = 0, double RotationZ = 0, double Perspective = .65);
public sealed record ScreenMotionClip(Guid Id, TransitionTiming Timing, ScreenMotionState Target, Guid? GroupId = null, FocusEffectStyle? FocusEffect = null);

public static class ScreenMotionInterpolator
{
    public static ScreenMotionState Sample(IEnumerable<ScreenMotionClip> clips, double time,
        ScreenMotionState? basis = null, MotionStyle? motion = null)
    {
        basis ??= new(new(.5, .5));
        if (!double.IsFinite(time) || time < 0) return basis;
        var ordered = clips.OrderBy(c => c.Timing.StartTime).ThenBy(c => c.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        var inherited = basis; int previous = -1;
        bool Touches(int i) => i + 1 < ordered.Length && Math.Abs(ordered[i + 1].Timing.StartTime - ordered[i].Timing.EndTime) <= AutoZoomPlanner.AdjacencyTolerance;
        ScreenMotionState At(int i, ScreenMotionState origin, double sampleTime)
        {
            var clip = ordered[i]; var timing = clip.Timing;
            if (sampleTime >= timing.EndTime && (Touches(i) || timing.ReturnDuration <= .0001)) return clip.Target;
            double duration = Math.Min(timing.LeadInDuration, timing.Duration), progress;
            if (duration > .001 && sampleTime < timing.StartTime + duration)
                progress = ZoomInterpolator.EasedProgress(timing.LeadInProgressOffset + (1 - timing.LeadInProgressOffset)
                    * (sampleTime - timing.StartTime) / duration, timing.Easing, timing.CustomCurve, motion);
            else if (sampleTime <= timing.EndTime) progress = 1;
            else
            {
                origin = basis;
                progress = timing.ReturnDuration > .001 ? 1 - ZoomInterpolator.EasedProgress(timing.ReturnProgressOffset
                    + (1 - timing.ReturnProgressOffset) * (sampleTime - timing.EndTime) / timing.ReturnDuration, timing.Easing, timing.CustomCurve, motion) : 0;
            }
            double Lerp(double a, double b) => a + (b - a) * progress;
            return new(ZoomAnimationTrack.Lerp(origin.Position, clip.Target.Position, progress), Lerp(origin.Scale, clip.Target.Scale),
                Lerp(origin.RotationX, clip.Target.RotationX), Lerp(origin.RotationY, clip.Target.RotationY),
                Lerp(origin.RotationZ, clip.Target.RotationZ), Lerp(origin.Perspective, clip.Target.Perspective));
        }
        for (int i = 0; i < ordered.Length; i++)
        {
            var timing = ordered[i].Timing;
            if (time < timing.StartTime) break;
            if (i + 1 < ordered.Length && ordered[i + 1].Timing.StartTime == timing.StartTime) continue;
            if (previous >= 0) inherited = At(previous, inherited, timing.StartTime);
            previous = i;
        }
        return previous >= 0 ? At(previous, inherited, time) : basis;
    }
}
