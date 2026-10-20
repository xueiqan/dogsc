namespace DogSC.Core;

public sealed record CursorStyle(bool Hidden = false, bool HideWhenIdle = false, double IdleDelay = 2,
    double ClickEffectDuration = .18, bool Tilt = false, double TiltStrength = 2)
{
    public void Validate()
    {
        if (!double.IsFinite(IdleDelay) || IdleDelay is < 0 or > 60 || !double.IsFinite(ClickEffectDuration)
            || ClickEffectDuration is < 0 or > 10 || !double.IsFinite(TiltStrength))
            throw new ArgumentException("光标显示参数无效。");
    }
}
public sealed record PointerClickPhase(double Progress, double Elapsed, double Duration, PointerEventKind Kind);
public sealed record PointerSample(NormalizedPoint Location, bool IsClicking, PointerClickPhase? ClickPhase = null,
    double RotationRadians = 0, string? RecordedCursorAssetId = null);
public sealed record PointerTrackEvaluation(NormalizedPoint? Position, PointerSample? Cursor);

public sealed class PointerTrack
{
    private readonly PointerEventRecord[] _events;
    private readonly PointerEventRecord[] _clicks;
    private readonly MotionStyle _motion;
    private readonly Lazy<SpringSample[]> _spring;
    public IReadOnlyList<PointerEventRecord> Events => Array.AsReadOnly(_events);
    private readonly record struct SpringSample(double Time, NormalizedPoint Location);

    public PointerTrack(IEnumerable<PointerEventRecord> events, MotionStyle? motion = null)
    {
        _motion = motion ?? new(); _motion.Validate();
        _events = events.Where(e => double.IsFinite(e.Time) && e.Time >= 0 && e.Location.IsValid).OrderBy(e => e.Time).ToArray();
        _clicks = _events.Where(e => e.Kind is PointerEventKind.LeftClick or PointerEventKind.RightClick).ToArray();
        _spring = new(BuildSpringSamples, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public void Prewarm() { if (_motion.Cursor == CursorMotion.Smooth) _ = _spring.Value; }
    public NormalizedPoint? SamplePosition(double time) => Evaluation(time).Position;
    public PointerSample? Sample(double time, CursorStyle? style = null) => Evaluation(time, style).Cursor;

    public PointerTrackEvaluation Evaluation(double time, CursorStyle? style = null)
    {
        style ??= new(); style.Validate();
        if (!double.IsFinite(time) || _events.Length == 0 || time < _events[0].Time) return new(null, null);
        int upper = UpperBound(_events, time), lower = Math.Max(upper - 1, 0);
        var point = _motion.Cursor == CursorMotion.Smooth ? SpringPosition(time) : InterpolatedPosition(time, lower, upper);
        bool visible = !style.Hidden && (!style.HideWhenIdle || time - _events[lower].Time <= style.IdleDelay);
        if (!visible) return new(point, null);
        PointerClickPhase? phase = null;
        int clickIndex = UpperBound(_clicks, time) - 1;
        if (clickIndex >= 0 && style.ClickEffectDuration > 0 && time - _clicks[clickIndex].Time <= style.ClickEffectDuration)
        {
            var click = _clicks[clickIndex]; double elapsed = time - click.Time;
            phase = new(Math.Clamp(elapsed / style.ClickEffectDuration, 0, 1), elapsed, style.ClickEffectDuration, click.Kind);
        }
        double rotation = 0;
        if (style.Tilt && _motion.Cursor != CursorMotion.None)
        {
            var (window, maximumDegrees, retainedWeight, span) = _motion.Cursor switch
            {
                CursorMotion.Smooth => (.08, 9.0, .38, .18),
                CursorMotion.Medium => (.065, 8.0, .24, .22),
                _ => (.05, 7.0, .12, .28)
            };
            var previous = SamplePositionCore(time - window); var older = SamplePositionCore(time - window * 2);
            if (previous is { } p && older is { } o)
            {
                double vx = (point.X - p.X) / window * (1 - retainedWeight) + (p.X - o.X) / window * retainedWeight;
                double vy = (point.Y - p.Y) / window * (1 - retainedWeight) + (p.Y - o.Y) / window * retainedWeight;
                double speed = Math.Sqrt(vx * vx + vy * vy);
                double strength = Math.Clamp(style.TiltStrength, 2, 4), threshold = .006 / strength;
                if (double.IsFinite(speed) && speed > threshold)
                {
                    double torque = Math.Clamp((vx - vy) / (Math.Max(speed, .000001) * Math.Sqrt(2)), -1, 1);
                    double response = Math.Clamp((speed - threshold) / (span / strength), 0, 1);
                    double decay = Math.Clamp(1 - Math.Max(time - _events[lower].Time, 0) / .18, 0, 1);
                    rotation = -maximumDegrees * (.35 + .65 * strength) * response * response * (3 - 2 * response)
                        * torque * decay * Math.PI / 180;
                }
            }
        }
        return new(point, new(point, phase is not null, phase, rotation, _events[lower].CursorAssetId));
    }

    private NormalizedPoint? SamplePositionCore(double time)
    {
        if (_events.Length == 0 || time < _events[0].Time) return null;
        int upper = UpperBound(_events, time);
        return _motion.Cursor == CursorMotion.Smooth ? SpringPosition(time) : InterpolatedPosition(time, Math.Max(upper - 1, 0), upper);
    }

    private NormalizedPoint InterpolatedPosition(double time, int lower, int upper)
    {
        var first = _events[lower];
        if (upper >= _events.Length || !Continuous(first, _events[upper])) return first.Location;
        var last = _events[upper]; double duration = last.Time - first.Time;
        double t = Math.Clamp((time - first.Time) / duration, 0, 1);
        if (_motion.Cursor == CursorMotion.None) return ZoomAnimationTrack.Lerp(first.Location, last.Location, t);
        var startTangent = Tangent(lower); var endTangent = Tangent(upper);
        double scale = _motion.Cursor == CursorMotion.Medium ? .78 : 1;
        double t2 = t * t, t3 = t2 * t;
        double Axis(double a, double b, double da, double db) => Math.Clamp((2 * t3 - 3 * t2 + 1) * a
            + (t3 - 2 * t2 + t) * duration * da * scale + (-2 * t3 + 3 * t2) * b
            + (t3 - t2) * duration * db * scale, 0, 1);
        return new(Axis(first.Location.X, last.Location.X, startTangent.X, endTangent.X),
            Axis(first.Location.Y, last.Location.Y, startTangent.Y, endTangent.Y));
    }

    private NormalizedPoint Tangent(int index)
    {
        var current = _events[index]; int previous = index - 1, next = index + 1;
        while (previous >= 0 && _events[previous].Time >= current.Time) previous--;
        while (next < _events.Length && _events[next].Time <= current.Time) next++;
        if (previous >= 0 && next < _events.Length)
        {
            var before = _events[previous]; var after = _events[next];
            double firstDuration = current.Time - before.Time, secondDuration = after.Time - current.Time;
            double Axis(double a, double b, double c)
            {
                double firstSlope = (b - a) / firstDuration, secondSlope = (c - b) / secondDuration;
                if (firstSlope * secondSlope <= 0) return 0;
                double w1 = 2 * secondDuration + firstDuration, w2 = secondDuration + 2 * firstDuration;
                return (w1 + w2) / (w1 / firstSlope + w2 / secondSlope);
            }
            return new(Axis(before.Location.X, current.Location.X, after.Location.X),
                Axis(before.Location.Y, current.Location.Y, after.Location.Y));
        }
        if (next < _events.Length) return Slope(current, _events[next]);
        if (previous >= 0) return Slope(_events[previous], current);
        return new(0, 0);
    }

    private static NormalizedPoint Slope(PointerEventRecord a, PointerEventRecord b) =>
        new((b.Location.X - a.Location.X) / (b.Time - a.Time), (b.Location.Y - a.Location.Y) / (b.Time - a.Time));

    private static bool Continuous(PointerEventRecord a, PointerEventRecord b)
    {
        double gap = b.Time - a.Time;
        if (gap <= 0) return false;
        if (gap <= .12) return true;
        double dx = b.Location.X - a.Location.X, dy = b.Location.Y - a.Location.Y;
        return gap <= .5 && dx * dx + dy * dy <= .03 * .03;
    }

    private SpringSample[] BuildSpringSamples()
    {
        if (_events.Length == 0) return [];
        double mass = Math.Max(_motion.CursorSpringMass, .01), stiffness = Math.Max(_motion.CursorSpringStiffness, .01), damping = Math.Max(_motion.CursorSpringDamping, .01);
        double firstTime = _events[0].Time, endTime = _events[^1].Time + Math.Max(.65, 6 * Math.Sqrt(mass / stiffness));
        double sampleCount = Math.Ceiling((endTime - firstTime) * 240) + 2;
        if (sampleCount > 10_000_000) throw new InvalidOperationException("光标弹簧轨迹超过1000万采样点；请拆分长片段或选直接光标模式。");
        var result = new List<SpringSample>((int)sampleCount);
        var position = _events[0].Location; var target = position; double vx = 0, vy = 0, time = firstTime;
        result.Add(new(time, position)); int eventIndex = 1;
        while (time < endTime)
        {
            double next = Math.Min(time + 1.0 / 240, endTime);
            while (eventIndex < _events.Length && _events[eventIndex].Time <= next) eventIndex++;
            var lower = _events[Math.Max(eventIndex - 1, 0)];
            var raw = eventIndex < _events.Length && Continuous(lower, _events[eventIndex])
                ? ZoomAnimationTrack.Lerp(lower.Location, _events[eventIndex].Location,
                    Math.Clamp((next - lower.Time) / (_events[eventIndex].Time - lower.Time), 0, 1)) : lower.Location;
            double dx = raw.X - target.X, dy = raw.Y - target.Y;
            if (dx * dx + dy * dy >= .00035 * .00035) target = raw;
            double dt = next - time;
            vx += (stiffness * (target.X - position.X) - damping * vx) / mass * dt;
            vy += (stiffness * (target.Y - position.Y) - damping * vy) / mass * dt;
            position = new(Math.Clamp(position.X + vx * dt, 0, 1), Math.Clamp(position.Y + vy * dt, 0, 1));
            time = next; result.Add(new(time, position));
        }
        if (_clicks.Length == 0) return result.ToArray();
        var merged = new List<SpringSample>(result.Count + _clicks.Length); int baseIndex = 0;
        foreach (var click in _clicks)
        {
            if (merged.Count > 0 && Math.Abs(merged[^1].Time - click.Time) <= .0000001) continue;
            while (baseIndex < result.Count && result[baseIndex].Time < click.Time - .0000001) merged.Add(result[baseIndex++]);
            if (baseIndex < result.Count && Math.Abs(result[baseIndex].Time - click.Time) <= .0000001)
                merged.Add(new(click.Time, result[baseIndex++].Location));
            else if (merged.Count > 0 && baseIndex < result.Count)
            {
                var a = merged[^1]; var b = result[baseIndex];
                merged.Add(new(click.Time, ZoomAnimationTrack.Lerp(a.Location, b.Location, (click.Time - a.Time) / (b.Time - a.Time))));
            }
        }
        merged.AddRange(result.Skip(baseIndex));
        var samples = merged.ToArray();
        for (int i = 0; i < _clicks.Length; i++)
        {
            var click = _clicks[i]; int anchor = Bound(samples, click.Time, false);
            if (anchor >= samples.Length) continue;
            double start = Math.Max(firstTime, Math.Max(click.Time - .18, i > 0 ? (_clicks[i - 1].Time + click.Time) * .5 : double.NegativeInfinity));
            double end = Math.Min(endTime, Math.Min(click.Time + .18, i + 1 < _clicks.Length ? (click.Time + _clicks[i + 1].Time) * .5 : double.PositiveInfinity));
            double dx = click.Location.X - samples[anchor].Location.X, dy = click.Location.Y - samples[anchor].Location.Y;
            for (int j = Bound(samples, start, false), last = Bound(samples, end, true); j < last; j++)
            {
                double t = samples[j].Time;
                double weight = t <= click.Time ? SmootherStep(click.Time > start ? (t - start) / (click.Time - start) : 1)
                    : 1 - SmootherStep(end > click.Time ? (t - click.Time) / (end - click.Time) : 1);
                samples[j] = new(t, new(Math.Clamp(samples[j].Location.X + dx * weight, 0, 1),
                    Math.Clamp(samples[j].Location.Y + dy * weight, 0, 1)));
            }
            samples[anchor] = new(samples[anchor].Time, click.Location);
        }
        return samples;
    }

    private NormalizedPoint SpringPosition(double time)
    {
        var samples = _spring.Value;
        if (time <= samples[0].Time) return samples[0].Location;
        if (time >= samples[^1].Time) return samples[^1].Location;
        int upper = Bound(samples, time, false);
        var a = samples[upper - 1]; var b = samples[upper];
        return ZoomAnimationTrack.Lerp(a.Location, b.Location, (time - a.Time) / (b.Time - a.Time));
    }
    internal static double SmootherStep(double value) { double t = Math.Clamp(value, 0, 1); return t * t * t * (t * (t * 6 - 15) + 10); }
    private static int UpperBound(PointerEventRecord[] events, double time)
    {
        int low = 0, high = events.Length;
        while (low < high) { int mid = low + (high - low) / 2; if (events[mid].Time <= time) low = mid + 1; else high = mid; }
        return low;
    }
    private static int Bound(SpringSample[] samples, double time, bool inclusive)
    {
        int low = 0, high = samples.Length;
        while (low < high) { int mid = low + (high - low) / 2; if (inclusive ? samples[mid].Time <= time : samples[mid].Time < time) low = mid + 1; else high = mid; }
        return low;
    }
}

public sealed class ProjectPointerTrack
{
    private readonly TimelineMap _map;
    private readonly PointerTrack[] _tracks;
    private readonly double?[] _firstClick;
    private readonly double[] _activity;
    private readonly Dictionary<Guid, int> _indices;
    private readonly MotionStyle _motion;
    private readonly List<(ZoomAnimationClip Clip, FocusSample[] Samples)> _focusCache = [];
    private readonly object _focusLock = new();
    private readonly record struct FocusSample(double Time, NormalizedPoint Location);
    public ProjectPointerTrack(TimelineMap map, IEnumerable<PointerEventRecord> sourceEvents, MotionStyle? motion = null)
    {
        _map = map; _motion = motion ?? new(); _motion.Validate(); var groups = map.MapPointerEvents(sourceEvents);
        _indices = map.Segments.Select((s, i) => (s.Id, i)).ToDictionary(s => s.Id, s => s.i);
        _tracks = groups.Select(g => new PointerTrack(g.Events, motion)).ToArray();
        _firstClick = groups.Select(g => g.Events.FirstOrDefault(e => e.Kind is PointerEventKind.LeftClick or PointerEventKind.RightClick)?.Time).ToArray();
        var activity = new List<double>(); NormalizedPoint? previous = null;
        foreach (var group in groups)
            foreach (var e in group.Events)
            {
                if (previous is null || Math.Abs(previous.Value.X - e.Location.X) > .000001
                    || Math.Abs(previous.Value.Y - e.Location.Y) > .000001 || e.Kind != PointerEventKind.Move) activity.Add(e.Time);
                previous = e.Location;
            }
        _activity = activity.ToArray();
    }
    public void Prewarm() { foreach (var track in _tracks) track.Prewarm(); }
    public PointerSample? Sample(double outputTime, CursorStyle? style = null) => Evaluation(outputTime, style).Cursor;
    public NormalizedPoint? SamplePosition(double outputTime) => Evaluation(outputTime).Position;

    public PointerTrackEvaluation Evaluation(double outputTime, CursorStyle? style = null, ZoomAnimationClip? automaticEntry = null)
    {
        style ??= new(); style.Validate(); var segment = _map.SegmentAtOutputTime(outputTime);
        if (segment is null) return new(null, null);
        int index = _indices[segment.Id];
        var current = _tracks[index].Evaluation(outputTime, style with { HideWhenIdle = false });
        if (style.HideWhenIdle)
        {
            int activity = Array.BinarySearch(_activity, outputTime);
            if (activity < 0) activity = ~activity - 1;
            if (activity >= 0 && outputTime - _activity[activity] > style.IdleDelay) current = current with { Cursor = null };
        }
        if (index == 0 || automaticEntry is { Origin: ZoomOrigin.Automatic }
            && Math.Abs(automaticEntry.StartTime - segment.OutputStart) <= .0001
            && Math.Abs(segment.SourceStart - _map.Segments[index - 1].SourceEnd) > .0001) return current;
        double bridge = Math.Min(Math.Min(.28, _firstClick[index] is { } click ? Math.Max(click - segment.OutputStart, 0) : .28), segment.OutputDuration);
        if (bridge <= .0001 || outputTime >= segment.OutputStart + bridge) return current;
        var previousSegment = _map.Segments[index - 1];
        var from = _tracks[index - 1].SamplePosition(Math.Max(previousSegment.OutputStart, previousSegment.OutputEnd - .0001));
        var to = _tracks[index].SamplePosition(segment.OutputStart + bridge);
        if (from is null || to is null) return current;
        var point = ZoomAnimationTrack.Lerp(from.Value, to.Value, PointerTrack.SmootherStep((outputTime - segment.OutputStart) / bridge));
        return new(point, current.Cursor is null ? null : current.Cursor with { Location = point });
    }

    public NormalizedPoint AutomaticCameraFocus(double outputTime, ZoomAnimationClip clip)
    {
        var basis = clip.Focus.Constrained();
        if (clip.Origin != ZoomOrigin.Automatic || !double.IsFinite(outputTime) || clip.EndTime <= clip.StartTime) return basis;
        FocusSample[] samples;
        lock (_focusLock)
        {
            int existing = _focusCache.FindIndex(e => e.Clip == clip);
            if (existing >= 0)
            {
                var entry = _focusCache[existing]; _focusCache.RemoveAt(existing); _focusCache.Insert(0, entry); samples = entry.Samples;
            }
            else
            {
                samples = MakeCameraFocusSamples(clip, basis);
                _focusCache.Insert(0, (clip, samples));
                if (_focusCache.Count > 4) _focusCache.RemoveAt(4);
            }
        }
        double time = Math.Clamp(outputTime, clip.StartTime, clip.EndTime);
        if (time <= samples[0].Time) return samples[0].Location;
        if (time >= samples[^1].Time) return samples[^1].Location;
        int low = 0, high = samples.Length;
        while (low < high) { int mid = low + (high - low) / 2; if (samples[mid].Time <= time) low = mid + 1; else high = mid; }
        var a = samples[low - 1]; var b = samples[low];
        return ZoomAnimationTrack.Lerp(a.Location, b.Location, (time - a.Time) / (b.Time - a.Time));
    }

    private FocusSample[] MakeCameraFocusSamples(ZoomAnimationClip clip, NormalizedPoint basis)
    {
        var owner = _map.SegmentAtOutputTime(clip.StartTime);
        if (owner is not null)
        {
            int index = _indices[owner.Id];
            bool discontinuity = Math.Abs(owner.SourceStart - (index > 0 ? _map.Segments[index - 1].SourceEnd : 0)) > .0001;
            if (clip.EnterProgressOffset <= .0001 || discontinuity && Math.Abs(clip.StartTime - owner.OutputStart) <= .0001)
            {
                double entryEnd = Math.Min(clip.StartTime + Math.Min(Math.Max(clip.EnterDuration, 0), clip.Duration), owner.OutputEnd);
                var entryClick = _tracks[index].Events.FirstOrDefault(e => e.Time >= clip.StartTime && e.Time <= entryEnd
                    && e.Time < owner.OutputEnd && e.Kind is PointerEventKind.LeftClick or PointerEventKind.RightClick);
                basis = (entryClick?.Location ?? RawPosition(clip.StartTime, _tracks[index].Events.ToArray()) ?? basis).Constrained();
            }
        }
        var events = _tracks.SelectMany(t => t.Events).Where(e => e.Time >= clip.StartTime && e.Time <= clip.EndTime).ToArray();
        double count = Math.Ceiling(clip.Duration * 120) + 2;
        if (count > 10_000_000) throw new InvalidOperationException("自动镜头超过1000万采样点；请拆分长镜头。");
        var samples = new List<FocusSample>((int)count);
        var position = basis; var target = basis; double vx = 0, vy = 0, time = clip.StartTime;
        samples.Add(new(time, position));
        double half = 1 / (2 * Math.Max(clip.Scale, 1)), rest = Math.Max(half - 2 * half * .22, .01);
        double followStart = Math.Min(clip.StartTime + Math.Min(Math.Max(clip.EnterDuration, 0), clip.Duration), clip.EndTime);
        double mass = Math.Max(_motion.ScreenSpringMass, .01), stiffness = Math.Max(_motion.ScreenSpringStiffness, .01), damping = Math.Max(_motion.ScreenSpringDamping, .01);
        while (time < clip.EndTime)
        {
            double next = Math.Min(time + 1.0 / 120, clip.EndTime);
            if (next >= followStart && RawPosition(next, events) is { } cursor)
                target = new NormalizedPoint(cursor.X < target.X - rest || cursor.X > target.X + rest ? cursor.X : target.X,
                    cursor.Y < target.Y - rest || cursor.Y > target.Y + rest ? cursor.Y : target.Y).Constrained();
            double dt = next - time;
            vx += (stiffness * (target.X - position.X) - damping * vx) / mass * dt;
            vy += (stiffness * (target.Y - position.Y) - damping * vy) / mass * dt;
            position = new NormalizedPoint(position.X + vx * dt, position.Y + vy * dt).Constrained();
            time = next; samples.Add(new(time, position));
        }
        return samples.ToArray();
    }

    private static NormalizedPoint? RawPosition(double time, PointerEventRecord[] events)
    {
        if (events.Length == 0) return null;
        if (time <= events[0].Time) return events[0].Location;
        if (time >= events[^1].Time) return events[^1].Location;
        int low = 0, high = events.Length;
        while (low < high) { int mid = low + (high - low) / 2; if (events[mid].Time <= time) low = mid + 1; else high = mid; }
        var a = events[low - 1]; var b = events[low];
        return ZoomAnimationTrack.Lerp(a.Location, b.Location, Math.Clamp((time - a.Time) / (b.Time - a.Time), 0, 1));
    }
}
