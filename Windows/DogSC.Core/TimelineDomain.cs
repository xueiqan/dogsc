namespace DogSC.Core;

public sealed record RecordingSegment(Guid Id, double SourceStart, double SourceDuration, double PlaybackRate = 1)
{
    public double SourceEnd => SourceStart + SourceDuration;
    public double OutputDuration => SourceDuration / PlaybackRate;
}

public sealed record SourceSequence(RecordingSegment[]? EditedSegments = null);
public sealed record MediaTimeRange(double Start, double Duration)
{
    public double End => Start + Duration;
}

public sealed record ResolvedRecordingSegment(Guid Id, double SourceStart, double SourceDuration,
    double PlaybackRate, double OutputStart)
{
    public double SourceEnd => SourceStart + SourceDuration;
    public double OutputDuration => SourceDuration / PlaybackRate;
    public double OutputEnd => OutputStart + OutputDuration;
    public RecordingSegment Authored => new(Id, SourceStart, SourceDuration, PlaybackRate);
}

public sealed record TimelineMappedPointerSegment(Guid Id, MediaTimeRange OutputRange, PointerEventRecord[] Events);

public sealed class TimelineMap
{
    public static readonly Guid FullRecordingSegmentId = Guid.Parse("00000000-0000-4000-8000-000000000001");
    public const double Epsilon = 1.0 / 120_000;
    public IReadOnlyList<ResolvedRecordingSegment> Segments { get; }
    public double FullSourceDuration { get; }
    public double OutputDuration => Segments[^1].OutputEnd;

    public TimelineMap(SourceSequence sequence, double fullSourceDuration)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (!double.IsFinite(fullSourceDuration) || fullSourceDuration <= 0)
            throw new ArgumentOutOfRangeException(nameof(fullSourceDuration), "主录屏时长必须为正的有限数。");
        FullSourceDuration = fullSourceDuration;
        var authored = sequence.EditedSegments ?? [new(FullRecordingSegmentId, 0, fullSourceDuration)];
        if (authored.Length == 0 || authored.Length > 100_000)
            throw new ArgumentException("时间线必须保留1到100000个片段。");
        var identifiers = new HashSet<Guid>();
        var resolved = new List<ResolvedRecordingSegment>(authored.Length);
        double output = 0;
        foreach (var segment in authored)
        {
            if (!identifiers.Add(segment.Id)) throw new ArgumentException("片段标识重复。");
            if (!double.IsFinite(segment.SourceStart) || !double.IsFinite(segment.SourceDuration)
                || !double.IsFinite(segment.PlaybackRate) || segment.SourceStart < 0
                || segment.SourceDuration <= 0 || segment.PlaybackRate <= 0 || !double.IsFinite(segment.SourceEnd))
                throw new ArgumentException("片段时间范围或速度无效。");
            if (segment.SourceStart >= fullSourceDuration || segment.SourceEnd > fullSourceDuration + Epsilon)
                throw new ArgumentException("片段超出原始录制范围。");
            double duration = Math.Min(segment.SourceDuration, fullSourceDuration - segment.SourceStart);
            resolved.Add(new(segment.Id, segment.SourceStart, duration, segment.PlaybackRate, output));
            output += duration / segment.PlaybackRate;
            if (!double.IsFinite(output)) throw new ArgumentException("成片时长超出有效范围。");
        }
        var sourceOrdered = resolved.OrderBy(s => s.SourceStart).ToArray();
        for (int i = 1; i < sourceOrdered.Length; i++)
            if (sourceOrdered[i].SourceStart < sourceOrdered[i - 1].SourceEnd - Epsilon)
                throw new ArgumentException("主片段不能重复使用同一段源素材。");
        Segments = resolved.AsReadOnly();
    }

    public ResolvedRecordingSegment? SegmentAtOutputTime(double time)
    {
        if (!double.IsFinite(time) || time < 0 || time >= OutputDuration) return null;
        int low = 0, high = Segments.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (Segments[mid].OutputEnd <= time) low = mid + 1; else high = mid;
        }
        return low < Segments.Count ? Segments[low] : null;
    }

    public double? SourceTime(double outputTime)
    {
        var segment = SegmentAtOutputTime(outputTime);
        return segment is null ? null : segment.SourceStart + (outputTime - segment.OutputStart) * segment.PlaybackRate;
    }

    public double? OutputTime(double sourceTime)
    {
        if (!double.IsFinite(sourceTime)) return null;
        var segment = Segments.FirstOrDefault(s => sourceTime >= s.SourceStart && sourceTime < s.SourceEnd);
        return segment is null ? null : segment.OutputStart + (sourceTime - segment.SourceStart) / segment.PlaybackRate;
    }

    public SourceSequence MaterializedSourceSequence() => new(Segments.Select(s => s.Authored).ToArray());

    public TimelineMappedPointerSegment[] MapPointerEvents(IEnumerable<PointerEventRecord> sourceEvents)
    {
        var ordered = sourceEvents.Where(e => double.IsFinite(e.Time)).OrderBy(e => e.Time).ToArray();
        int LowerBound(double time)
        {
            int low = 0, high = ordered.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (ordered[mid].Time < time) low = mid + 1; else high = mid;
            }
            return low;
        }
        return Segments.Select(segment =>
        {
            int low = LowerBound(segment.SourceStart), high = LowerBound(segment.SourceEnd);
            var events = new List<PointerEventRecord>(high - low + 1);
            if (low > 0 && (low == high || Math.Abs(ordered[low].Time - segment.SourceStart) >= 0.000001))
                events.Add(ordered[low - 1] with { Time = segment.OutputStart, Kind = PointerEventKind.Move });
            for (int i = low; i < high; i++)
                events.Add(ordered[i] with { Time = segment.OutputStart + (ordered[i].Time - segment.SourceStart) / segment.PlaybackRate });
            return new TimelineMappedPointerSegment(segment.Id, new(segment.OutputStart, segment.OutputDuration), events.ToArray());
        }).ToArray();
    }
}

public sealed record ProjectTimeline(SourceSequence SourceSequence, ZoomAnimationClip[] ZoomClips)
{
    public static ProjectTimeline FullRecording => new(new(), []);
}

public enum RecordingSegmentTrimEdge { Left, Right }

public static class ProjectTimelineEditing
{
    public static ProjectTimeline Split(ProjectTimeline timeline, double outputTime, Guid newRightId, double fullDuration)
    {
        var map = new TimelineMap(timeline.SourceSequence, fullDuration);
        var selected = map.SegmentAtOutputTime(outputTime);
        if (selected is null || outputTime <= selected.OutputStart + TimelineMap.Epsilon
            || outputTime >= selected.OutputEnd - TimelineMap.Epsilon)
            throw new ArgumentException("分割位置必须位于片段内部。");
        var segments = map.Segments.Select(s => s.Authored).ToList();
        int index = segments.FindIndex(s => s.Id == selected.Id);
        double sourceOffset = (outputTime - selected.OutputStart) * selected.PlaybackRate;
        segments[index] = segments[index] with { SourceDuration = sourceOffset };
        segments.Insert(index + 1, new(newRightId, selected.SourceStart + sourceOffset,
            selected.SourceDuration - sourceOffset, selected.PlaybackRate));
        return Checked(timeline with { SourceSequence = new(segments.ToArray()) }, fullDuration);
    }

    public static ProjectTimeline Trim(ProjectTimeline timeline, Guid id, RecordingSegmentTrimEdge edge,
        double outputTime, double fullDuration)
    {
        var map = new TimelineMap(timeline.SourceSequence, fullDuration);
        var selected = Find(map, id);
        if (!double.IsFinite(outputTime)) throw new ArgumentException("裁剪位置无效。");
        double boundary = edge == RecordingSegmentTrimEdge.Left ? selected.OutputStart : selected.OutputEnd;
        if (Math.Abs(outputTime - boundary) <= TimelineMap.Epsilon) return timeline;
        var segments = map.Segments.Select(s => s.Authored).ToArray();
        int index = Array.FindIndex(segments, s => s.Id == id);
        bool extends = edge == RecordingSegmentTrimEdge.Left ? outputTime < boundary : outputTime > boundary;
        if (extends)
        {
            double amount = Math.Abs(outputTime - boundary), sourceAmount = amount * selected.PlaybackRate;
            double sourceLimit = edge == RecordingSegmentTrimEdge.Left
                ? map.Segments.Where(s => s.Id != id && s.SourceEnd <= selected.SourceStart + TimelineMap.Epsilon)
                    .Select(s => s.SourceEnd).DefaultIfEmpty(0).Max()
                : map.Segments.Where(s => s.Id != id && s.SourceStart >= selected.SourceEnd - TimelineMap.Epsilon)
                    .Select(s => s.SourceStart).DefaultIfEmpty(fullDuration).Min();
            double available = edge == RecordingSegmentTrimEdge.Left ? selected.SourceStart - sourceLimit : sourceLimit - selected.SourceEnd;
            if (sourceAmount > available + TimelineMap.Epsilon) throw new ArgumentException("裁剪恢复范围与保留素材重叠。");
            segments[index] = segments[index] with
            {
                SourceStart = selected.SourceStart - (edge == RecordingSegmentTrimEdge.Left ? sourceAmount : 0),
                SourceDuration = selected.SourceDuration + sourceAmount
            };
            return Insert(timeline with { SourceSequence = new(segments) }, boundary, amount, fullDuration);
        }
        if (outputTime <= selected.OutputStart + TimelineMap.Epsilon || outputTime >= selected.OutputEnd - TimelineMap.Epsilon)
            throw new ArgumentException("裁剪位置必须位于片段内部。");
        double deletionStart = edge == RecordingSegmentTrimEdge.Left ? selected.OutputStart : outputTime;
        double deletionEnd = edge == RecordingSegmentTrimEdge.Left ? outputTime : selected.OutputEnd;
        segments[index] = segments[index] with
        {
            SourceStart = selected.SourceStart + (edge == RecordingSegmentTrimEdge.Left ? (outputTime - selected.OutputStart) * selected.PlaybackRate : 0),
            SourceDuration = selected.SourceDuration - (deletionEnd - deletionStart) * selected.PlaybackRate
        };
        return Ripple(timeline with { SourceSequence = new(segments) }, deletionStart, deletionEnd,
            deletionEnd >= map.OutputDuration - TimelineMap.Epsilon, fullDuration);
    }

    public static ProjectTimeline Remove(ProjectTimeline timeline, Guid id, double fullDuration)
    {
        var map = new TimelineMap(timeline.SourceSequence, fullDuration);
        var removed = Find(map, id);
        if (map.Segments.Count == 1) throw new ArgumentException("至少需要保留一个主片段。");
        return Ripple(timeline with { SourceSequence = new(map.Segments.Where(s => s.Id != id).Select(s => s.Authored).ToArray()) },
            removed.OutputStart, removed.OutputEnd, removed.OutputEnd >= map.OutputDuration - TimelineMap.Epsilon, fullDuration);
    }

    public static ProjectTimeline Move(ProjectTimeline timeline, Guid id, int requestedIndex, double fullDuration)
    {
        var oldMap = new TimelineMap(timeline.SourceSequence, fullDuration);
        var selected = Find(oldMap, id);
        var segments = oldMap.Segments.Select(s => s.Authored).ToList();
        int oldIndex = segments.FindIndex(s => s.Id == id), target = Math.Clamp(requestedIndex, 0, segments.Count - 1);
        if (oldIndex == target) return timeline;
        segments.RemoveAt(oldIndex); segments.Insert(target, selected.Authored);
        var sequence = new SourceSequence(segments.ToArray());
        var newMap = new TimelineMap(sequence, fullDuration);
        var starts = newMap.Segments.ToDictionary(s => s.Id, s => s.OutputStart);
        var clips = timeline.ZoomClips.Select(clip =>
        {
            double probe = Math.Clamp(clip.StartTime, 0, Math.Max(oldMap.OutputDuration - TimelineMap.Epsilon, 0));
            var owner = oldMap.SegmentAtOutputTime(probe)!;
            double offset = starts[owner.Id] - owner.OutputStart;
            double start = clip.StartTime + offset, end = clip.EndTime + offset;
            if (start < 0 || end > newMap.OutputDuration + TimelineMap.Epsilon)
                throw new ArgumentException("移动会截短缩放片段，请先调整缩放范围。");
            double oldExit = Math.Min(clip.ExitDuration, Math.Max(oldMap.OutputDuration - clip.EndTime, 0));
            double newExit = Math.Min(clip.ExitDuration, Math.Max(newMap.OutputDuration - end, 0));
            if (newExit < oldExit - TimelineMap.Epsilon)
                throw new ArgumentException("移动会截短缩放片段的返回过渡，请先调整缩放范围。");
            return clip with { StartTime = start, EndTime = end };
        }).OrderBy(c => c.StartTime).ToArray();
        return Checked(timeline with { SourceSequence = sequence, ZoomClips = clips }, fullDuration);
    }

    public static ProjectTimeline SetPlaybackRate(ProjectTimeline timeline, Guid id, double requestedRate, double fullDuration)
    {
        var map = new TimelineMap(timeline.SourceSequence, fullDuration);
        var selected = Find(map, id);
        double rate = Math.Clamp(double.IsFinite(requestedRate) ? requestedRate : 1, 1, 100);
        double newEnd = selected.OutputStart + selected.SourceDuration / rate;
        double Anchor(double t) => t <= selected.OutputStart ? t : t >= selected.OutputEnd
            ? t + newEnd - selected.OutputEnd : selected.OutputStart + (t - selected.OutputStart) * selected.PlaybackRate / rate;
        var clips = timeline.ZoomClips.Select(c => c with { StartTime = Anchor(c.StartTime), EndTime = Anchor(c.StartTime) + c.Duration }).ToArray();
        return Checked(timeline with { SourceSequence = new(map.Segments.Select(s => s.Id == id
            ? s.Authored with { PlaybackRate = rate } : s.Authored).ToArray()), ZoomClips = ResolveOverlaps(clips) }, fullDuration);
    }

    public static ProjectTimeline RestoreGap(ProjectTimeline timeline, Guid previousId, Guid nextId, Guid restoredId, double fullDuration)
    {
        var map = new TimelineMap(timeline.SourceSequence, fullDuration);
        int index = map.Segments.ToList().FindIndex(s => s.Id == previousId);
        if (index < 0 || index + 1 >= map.Segments.Count || map.Segments[index + 1].Id != nextId)
            throw new ArgumentException("恢复接点不存在。");
        var previous = map.Segments[index]; var next = map.Segments[index + 1];
        return Restore(timeline, map, index + 1, previous.SourceEnd, next.SourceStart - previous.SourceEnd, restoredId, fullDuration);
    }

    public static ProjectTimeline RestoreLeadingGap(ProjectTimeline timeline, Guid restoredId, double fullDuration)
    {
        var map = new TimelineMap(timeline.SourceSequence, fullDuration);
        return Restore(timeline, map, 0, 0, map.Segments[0].SourceStart, restoredId, fullDuration);
    }

    public static ProjectTimeline RestoreTrailingGap(ProjectTimeline timeline, Guid restoredId, double fullDuration)
    {
        var map = new TimelineMap(timeline.SourceSequence, fullDuration);
        return Restore(timeline, map, map.Segments.Count, map.Segments[^1].SourceEnd,
            fullDuration - map.Segments[^1].SourceEnd, restoredId, fullDuration);
    }

    public static ProjectTimeline Merge(ProjectTimeline timeline, Guid previousId, Guid nextId, double fullDuration)
    {
        var map = new TimelineMap(timeline.SourceSequence, fullDuration);
        var segments = map.Segments.Select(s => s.Authored).ToList();
        int index = segments.FindIndex(s => s.Id == previousId);
        if (index < 0 || index + 1 >= segments.Count || segments[index + 1].Id != nextId)
            throw new ArgumentException("合并接点不存在。");
        var previous = segments[index]; var next = segments[index + 1];
        if (Math.Abs(next.SourceStart - previous.SourceEnd) > TimelineMap.Epsilon
            || Math.Abs(next.PlaybackRate - previous.PlaybackRate) > TimelineMap.Epsilon)
            throw new ArgumentException("只能合并源连续且速度相同的片段。");
        segments[index] = previous with { SourceDuration = next.SourceEnd - previous.SourceStart };
        segments.RemoveAt(index + 1);
        return Checked(timeline with { SourceSequence = new(segments.ToArray()) }, fullDuration);
    }

    private static ProjectTimeline Restore(ProjectTimeline timeline, TimelineMap map, int index, double start,
        double duration, Guid id, double fullDuration)
    {
        if (duration <= TimelineMap.Epsilon) throw new ArgumentException("该接点没有可恢复素材。");
        var segments = map.Segments.Select(s => s.Authored).ToList();
        segments.Insert(index, new(id, start, duration));
        double insertion = index == map.Segments.Count ? map.OutputDuration : map.Segments[index].OutputStart;
        return Insert(timeline with { SourceSequence = new(segments.ToArray()) }, insertion, duration, fullDuration);
    }

    private static ProjectTimeline Insert(ProjectTimeline timeline, double time, double duration, double fullDuration) =>
        Checked(timeline with { ZoomClips = timeline.ZoomClips.Select(c => c.StartTime >= time - TimelineMap.Epsilon
            ? c with { StartTime = c.StartTime + duration, EndTime = c.EndTime + duration }
            : c.EndTime > time + TimelineMap.Epsilon ? c with { EndTime = c.EndTime + duration } : c).ToArray() }, fullDuration);

    private static ProjectTimeline Ripple(ProjectTimeline timeline, double start, double end, bool tail, double fullDuration)
    {
        double Map(double t) => t <= start ? t : t >= end ? t - end + start : start;
        var clips = timeline.ZoomClips.Select(c =>
        {
            c = c with { PreferredEnterDuration = c.PreferredEnterDuration ?? c.EnterDuration,
                PreferredExitDuration = c.PreferredExitDuration ?? RequestedExit(c) };
            if (c.EffectEndTime <= start + TimelineMap.Epsilon) return c;
            if (tail)
            {
                if (c.StartTime >= start - TimelineMap.Epsilon) return null;
                double clippedEnd = Math.Min(c.EndTime, start);
                return c with { EndTime = clippedEnd,
                    EnterDuration = Math.Min(c.EnterDuration, clippedEnd - c.StartTime),
                    ExitDuration = Math.Min(c.ExitDuration, Math.Max(start - clippedEnd, 0)) };
            }
            if (c.StartTime >= end - TimelineMap.Epsilon)
                return c with { StartTime = c.StartTime - end + start, EndTime = c.EndTime - end + start };
            if (c.StartTime >= start - TimelineMap.Epsilon && c.EndTime <= end + TimelineMap.Epsilon) return null;
            double newStart = Map(c.StartTime), newEnd = Map(c.EndTime);
            if (newEnd - newStart <= TimelineMap.Epsilon) return null;
            double entry = Math.Max(0, Math.Min(Map(Math.Min(c.StartTime + c.EnterDuration, c.EndTime)) - newStart, newEnd - newStart));
            double Offset(double phaseStart, double phaseDuration, double original) =>
                phaseDuration > TimelineMap.Epsilon && phaseStart >= start - TimelineMap.Epsilon && phaseStart < end - TimelineMap.Epsilon
                ? original + (1 - original) * Math.Clamp((start - phaseStart) / phaseDuration, 0, 1) : original;
            return c with { StartTime = newStart, EndTime = newEnd, EnterDuration = entry,
                ExitDuration = Math.Max(0, Map(c.EffectEndTime) - newEnd),
                EnterProgressOffset = Offset(c.StartTime, Math.Min(c.EnterDuration, c.Duration), c.EnterProgressOffset),
                ExitProgressOffset = Offset(c.EndTime, c.ExitDuration, c.ExitProgressOffset) };
        }).OfType<ZoomAnimationClip>().OrderBy(c => c.StartTime).ToArray();
        if (!tail)
            for (int i = 0; i < clips.Length; i++)
            {
                var old = timeline.ZoomClips.First(c => c.Id == clips[i].Id);
                var clip = clips[i];
                double oldEntry = Math.Min(old.EnterDuration, old.Duration);
                if (oldEntry > TimelineMap.Epsilon && old.StartTime < end - TimelineMap.Epsilon
                    && old.StartTime + oldEntry > start + TimelineMap.Epsilon)
                    clip = clip with { EnterDuration = Math.Min(old.PreferredEnterDuration ?? old.EnterDuration, clip.Duration),
                        EnterProgressOffset = old.StartTime >= start - TimelineMap.Epsilon ? 0 : old.EnterProgressOffset };
                double requested = RequestedExit(old);
                double gap = i + 1 < clips.Length ? Math.Max(clips[i + 1].StartTime - clip.EndTime, 0) : requested;
                clip = clip with { PreferredExitDuration = requested };
                if (gap > AutoZoomPlanner.AdjacencyTolerance || i == clips.Length - 1)
                    clip = clip with { ExitDuration = Math.Min(requested, gap),
                        ExitProgressOffset = old.ExitDuration <= TimelineMap.Epsilon
                            || (old.EndTime >= start - TimelineMap.Epsilon && old.EndTime < end - TimelineMap.Epsilon) ? 0 : old.ExitProgressOffset };
                clips[i] = clip;
            }
        return Checked(timeline with { ZoomClips = clips }, fullDuration);
    }

    private static ZoomAnimationClip[] ResolveOverlaps(ZoomAnimationClip[] clips)
    {
        var result = clips.OrderBy(c => c.StartTime).ToArray();
        for (int i = 1; i < result.Length; i++)
        {
            var previous = result[i - 1];
            bool touches = Math.Abs(result[i].StartTime - previous.EndTime) <= AutoZoomPlanner.AdjacencyTolerance;
            double minimum = touches ? previous.EndTime : result[i].StartTime < previous.EndTime ? previous.EndTime : previous.EffectEndTime;
            if (result[i].StartTime < minimum)
            {
                double offset = minimum - result[i].StartTime;
                result[i] = result[i] with { StartTime = minimum, EndTime = result[i].EndTime + offset };
            }
        }
        return result;
    }

    private static double RequestedExit(ZoomAnimationClip clip) => clip.RequestedExitDuration;

    private static ResolvedRecordingSegment Find(TimelineMap map, Guid id) =>
        map.Segments.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("主片段不存在。");

    private static ProjectTimeline Checked(ProjectTimeline timeline, double fullDuration)
    {
        var map = new TimelineMap(timeline.SourceSequence, fullDuration);
        var ids = new HashSet<Guid>();
        foreach (var clip in timeline.ZoomClips)
        {
            clip.Validate();
            if (clip.StartTime > map.OutputDuration + TimelineMap.Epsilon
                || clip.EndTime > map.OutputDuration + TimelineMap.Epsilon)
                throw new ArgumentException("缩放片段超出成片时长。");
            if (!ids.Add(clip.Id)) throw new ArgumentException("缩放片段标识重复。");
        }
        var ordered = timeline.ZoomClips.OrderBy(c => c.StartTime).ToArray();
        for (int i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1]; var next = ordered[i];
            if (next.StartTime < previous.EndTime - 0.0001
                || next.StartTime - previous.EndTime > AutoZoomPlanner.AdjacencyTolerance
                    && next.StartTime < previous.EffectEndTime - 0.0001)
                throw new ArgumentException("缩放片段重叠。");
        }
        return timeline;
    }
}
