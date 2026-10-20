using DogSC.Core;

internal static class RegressionChecks
{
    private static readonly Guid First = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid Last = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid SplitRight = Guid.Parse("10000000-0000-4000-8000-000000000003");
    private static readonly Guid ZoomId = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid MarkerId = Guid.Parse("30000000-0000-4000-8000-000000000001");

    public static int TailZoomClipping()
    {
        var checks = 0;
        void Check(bool condition, string name) { Require(condition, name); checks++; }
        var crossing = Zoom(1.5, 3) with { EnterProgressOffset = .2, ExitProgressOffset = .3 };
        var original = Timeline(crossing);
        var shortened = ProjectTimelineEditing.Remove(original, Last, 4);
        Check(Near(new TimelineMap(shortened.SourceSequence, 4).OutputDuration, 2), "tail deletion shortens the screen timeline");
        Check(shortened.ZoomClips.Length == 1, "tail deletion retains the visible part of the crossing zoom");
        var clipped = shortened.ZoomClips[0];
        Check(clipped.Id == crossing.Id && clipped.Focus == crossing.Focus && clipped.Easing == ZoomEasingPreset.Linear,
            "tail clipping retains the authored zoom identity and target");
        Check(Near(clipped.StartTime, 1.5) && Near(clipped.EndTime, 2), "tail clipping clamps the authored range to the new end");
        Check(Near(clipped.EnterDuration, .5) && Near(clipped.ExitDuration, 0), "tail clipping fits the remaining entry and exit");
        Check(clipped.PreferredEnterDuration == crossing.PreferredEnterDuration && clipped.PreferredExitDuration == crossing.PreferredExitDuration,
            "tail clipping retains the preferred transition lengths");
        Check(clipped.EnterProgressOffset == crossing.EnterProgressOffset && clipped.ExitProgressOffset == crossing.ExitProgressOffset,
            "tail clipping retains the existing transition phase offsets");
        Check(original.ZoomClips[0] == crossing && original.SourceSequence.EditedSegments!.Length == 2,
            "tail deletion leaves the undo source intact");

        var trimmed = ProjectTimelineEditing.Trim(Timeline(Zoom(2.5, 3.5)), Last, RecordingSegmentTrimEdge.Right, 3, 4);
        Check(trimmed.ZoomClips.Length == 1 && Near(trimmed.ZoomClips[0].StartTime, 2.5) && Near(trimmed.ZoomClips[0].EndTime, 3),
            "right-edge trimming also retains and clips a crossing zoom");
        Check(Near(trimmed.ZoomClips[0].EnterDuration, .5) && Near(trimmed.ZoomClips[0].ExitDuration, 0),
            "right-edge trimming fits the retained transition");
        var exitOnly = ProjectTimelineEditing.Remove(Timeline(Zoom(.5, 1.75)), Last, 4).ZoomClips.Single();
        Check(Near(exitOnly.EndTime, 1.75) && Near(exitOnly.ExitDuration, .25), "tail deletion clips only the exit when the authored range is retained");
        Check(exitOnly.PreferredExitDuration == .5, "a clipped exit retains its restoration preference");
        Check(ProjectTimelineEditing.Remove(Timeline(Zoom(2.5, 3)), Last, 4).ZoomClips.Length == 0,
            "a zoom entirely inside the deleted tail is removed");

        var kept = Zoom(.25, 1);
        Check(ProjectTimelineEditing.Remove(Timeline(kept), Last, 4).ZoomClips.Single() == kept,
            "tail deletion does not rewrite a fully retained zoom");
        var outside = Timeline(Zoom(3.5, 4.1));
        var rejected = false;
        try { _ = ProjectTimelineEditing.Split(outside, 1, SplitRight, 4); }
        catch (ArgumentException error)
        {
            rejected = true;
            Check(error.Message.Contains("缩放", StringComparison.Ordinal), "out-of-range authored zoom reports a Chinese domain error");
        }
        Check(rejected, "timeline edits reject an authored zoom ending beyond the output duration");
        Check(Near(outside.ZoomClips[0].EndTime, 4.1), "rejecting an invalid zoom preserves the input for recovery");

        // Only authored Start/End are globally bounded; an exit can run beyond the final frame.
        var legalExit = Zoom(3, 4);
        var split = ProjectTimelineEditing.Split(Timeline(legalExit), 1, SplitRight, 4);
        Check(split.ZoomClips.Single() == legalExit, "splitting accepts a valid authored range whose exit extends beyond the video");
        return checks;
    }

    public static int MovePreservesZoom()
    {
        var checks = 0;
        void Check(bool condition, string name) { Require(condition, name); checks++; }
        void RejectMove(ProjectTimeline original, double duration, string name)
        {
            var savedSegments = original.SourceSequence.EditedSegments!.ToArray();
            var savedClips = original.ZoomClips.ToArray();
            var rejected = false;
            try { _ = ProjectTimelineEditing.Move(original, First, 1, duration); }
            catch (ArgumentException error)
            {
                rejected = true;
                Check(error.Message.Contains("缩放", StringComparison.Ordinal), name + " explains the affected zoom");
            }
            Check(rejected, name + " is rejected instead of silently changing authored parameters");
            Check(original.SourceSequence.EditedSegments!.SequenceEqual(savedSegments) && original.ZoomClips.SequenceEqual(savedClips),
                name + " leaves the undo source intact");
        }
        RejectMove(Timeline(Zoom(1.5, 3)), 4, "a crossing zoom moved past the video end");
        RejectMove(Timeline(Zoom(.5, 1.75, .25, .5)), 4, "a retained zoom whose return would be cut by the video end");

        var threeSegments = new SourceSequence([new(First, 0, 2), new(Last, 2, 2), new(SplitRight, 4, 2)]);
        var secondZoomId = Guid.Parse("20000000-0000-4000-8000-000000000002");
        RejectMove(new(threeSegments, [Zoom(1, 2.5, .4, .2), Zoom(4.25, 5, .3, .2) with { Id = secondZoomId }]),
            6, "a move creating overlapping authored zoom ranges");
        RejectMove(new(threeSegments, [Zoom(1, 2.25, .4, 1), Zoom(4.5, 5.5, .3, .2) with { Id = secondZoomId }]),
            6, "a move creating overlapping return transitions");

        var contained = Zoom(.25, 1.25, .4, .3) with { PreferredEnterDuration = null, PreferredExitDuration = null,
            EnterProgressOffset = .2, ExitProgressOffset = .3 };
        var safe = Timeline(contained);
        var forward = ProjectTimelineEditing.Move(safe, First, 1, 4);
        Check(forward.SourceSequence.EditedSegments!.Select(s => s.Id).SequenceEqual(new[] { Last, First }),
            "a non-destructive move still changes the screen order");
        Check(forward.ZoomClips.Single() == (contained with { StartTime = 2.25, EndTime = 3.25 }),
            "a contained zoom follows its owning screen segment without losing properties");
        var back = ProjectTimelineEditing.Move(forward, First, 0, 4);
        Check(back.SourceSequence.EditedSegments!.SequenceEqual(safe.SourceSequence.EditedSegments!) && back.ZoomClips.Single() == contained,
            "a contained zoom survives an exact move round trip");

        var tailExit = Zoom(4.5, 6, .3, .5);
        var unaffected = new ProjectTimeline(threeSegments, [tailExit]);
        var movedUnaffected = ProjectTimelineEditing.Move(unaffected, First, 1, 6);
        Check(movedUnaffected.ZoomClips.Single() == tailExit, "an unaffected exit beyond the video end remains legal and unchanged");
        var restoredUnaffected = ProjectTimelineEditing.Move(movedUnaffected, First, 0, 6);
        Check(restoredUnaffected.SourceSequence.EditedSegments!.SequenceEqual(unaffected.SourceSequence.EditedSegments!)
            && restoredUnaffected.ZoomClips.Single() == tailExit, "an unaffected tail exit survives a move round trip");
        return checks;
    }

    public static int TerminalRecordingMarkers()
    {
        var checks = 0;
        void Check(bool condition, string name) { Require(condition, name); checks++; }
        var marker = new RecordingMarker(MarkerId, 4, 1);
        var timeline = Timeline();
        var map = new TimelineMap(timeline.SourceSequence, 4);
        var mapped = RecordingMarkerDomain.Map([marker], map);
        Check(mapped.Length == 1 && mapped[0].Marker == marker, "the exact recording-end marker remains addressable");
        Check(map.SourceTime(mapped[0].OutputTime) is { } source && source > 3.999 && source < 4,
            "the terminal marker seeks inside its final retained source frame");
        Check(map.OutputTime(4) is null && map.SegmentAtOutputTime(4) is null,
            "marker handling leaves the shared half-open timeline contract intact");

        var reversed = new TimelineMap(new([new(Last, 2, 2), new(First, 0, 2)]), 4);
        var reversedMarker = RecordingMarkerDomain.Map([marker], reversed);
        Check(reversedMarker.Length == 1 && reversedMarker[0].OutputTime < 2 && reversedMarker[0].OutputTime > 1.999,
            "a reordered recording-end marker follows its source segment rather than the overall output end");
        Check(reversed.SourceTime(reversedMarker[0].OutputTime) is { } reorderedSource && reorderedSource > 3.999,
            "a reordered terminal marker does not seek to the following source segment");

        var withoutTail = new TimelineMap(new([new(First, 0, 2)]), 4);
        Check(RecordingMarkerDomain.Map([marker], withoutTail).Length == 0, "a genuinely deleted terminal source is not falsely mapped");
        Check(RecordingMarkerDomain.Map([marker with { SourceTime = 2 }], withoutTail).Length == 0,
            "an ordinary cut boundary is not mistaken for the recording end");
        Check(RecordingMarkerDomain.Map([marker with { SourceTime = 4.1 }], map).Length == 0,
            "a source anchor materially beyond the recording is not mapped");
        var interior = marker with { SourceTime = 1.25 };
        Check(Near(RecordingMarkerDomain.Map([interior], map).Single().OutputTime, 1.25), "interior marker mapping remains unchanged");
        Check(marker.SourceTime == 4 && marker.Number == 1, "mapping does not rewrite the original marker");
        return checks;
    }

    private static ProjectTimeline Timeline(params ZoomAnimationClip[] clips) =>
        new(new([new(First, 0, 2), new(Last, 2, 2)]), clips);

    private static ZoomAnimationClip Zoom(double start, double end, double enter = .7, double exit = .5) =>
        new(ZoomId, start, end, 1.6, new(.5, .5), Easing: ZoomEasingPreset.Linear,
            EnterDuration: enter, ExitDuration: exit, PreferredEnterDuration: enter, PreferredExitDuration: exit);

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= TimelineMap.Epsilon;
    private static void Require(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("FAIL: " + name); }
}
