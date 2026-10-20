using DogSC.Core;

namespace DogSC.Windows;

public sealed record ScreenAnchorViewport(double CanvasWidth, double CanvasHeight, double FittedWidth, double FittedHeight,
    double BorderWidthAtScaleOne, double TopAtScaleOne = 0, double RightAtScaleOne = 0, double BottomAtScaleOne = 0, double LeftAtScaleOne = 0);
public sealed record ScreenLayoutSample(ScreenMotionState State, double OffsetX, double OffsetY, NormalizedPoint ProjectionAnchor);

/// <summary>RecorderCore ScreenMotionTrack.sampleLayout: pixel translation, semantic focus and shortest-angle interpolation.</summary>
public static class ScreenMotionLayoutTimeline
{
    public static ScreenLayoutSample Sample(IEnumerable<ScreenMotionClip> clips, double time, ScreenMotionState basis,
        ScreenAnchorViewport viewport, MotionStyle? motion = null)
    {
        if (!double.IsFinite(time) || time < 0) return Layout(basis, false, viewport);
        var ordered = clips.OrderBy(c => c.Timing.StartTime).ThenBy(c => c.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        var inherited = basis; bool inheritedFocus = false; int previous = -1;
        ScreenLayoutSample? inheritedSample = null;
        bool Touches(int i) => i + 1 < ordered.Length && Math.Abs(ordered[i + 1].Timing.StartTime - ordered[i].Timing.EndTime) <= AutoZoomPlanner.AdjacencyTolerance;
        ScreenLayoutSample At(int i, ScreenMotionState origin, bool originFocus, ScreenLayoutSample? originSample, double sampleTime)
        {
            var clip = ordered[i]; var t = clip.Timing;
            if (sampleTime < t.EndTime)
            {
                var lead = Math.Min(t.LeadInDuration, t.Duration);
                if (lead <= .0001) return Layout(clip.Target, true, viewport);
                var linear = t.LeadInProgressOffset + (1 - t.LeadInProgressOffset) * Math.Min((sampleTime - t.StartTime) / lead, 1);
                return Mix(origin, clip.Target, originFocus, true, ZoomInterpolator.EasedProgress(linear, t.Easing, t.CustomCurve, motion),
                    viewport, clip.Target.Position, originFocus ? origin.Position : null, originSample);
            }
            if (t.ReturnDuration <= .0001 || Touches(i)) return Layout(clip.Target, true, viewport);
            if (sampleTime > t.EffectEndTime) return Layout(basis, false, viewport);
            var p = t.ReturnProgressOffset + (1 - t.ReturnProgressOffset) * Math.Clamp((sampleTime - t.EndTime) / t.ReturnDuration, 0, 1);
            return Mix(clip.Target, basis, true, false, ZoomInterpolator.EasedProgress(p, t.Easing, t.CustomCurve, motion), viewport, clip.Target.Position, null);
        }
        for (int i = 0; i < ordered.Length; i++)
        {
            var t = ordered[i].Timing;
            if (time < t.StartTime) break;
            if (i + 1 < ordered.Length && ordered[i + 1].Timing.StartTime == t.StartTime) continue;
            if (previous >= 0)
            {
                var earlier = ordered[previous];
                bool holds = Touches(previous) || earlier.Timing.ReturnDuration <= .0001;
                bool overlaps = t.StartTime < earlier.Timing.EndTime || !holds && t.StartTime < earlier.Timing.EffectEndTime;
                inheritedSample = overlaps ? At(previous, inherited, inheritedFocus, inheritedSample, t.StartTime) : null;
                inheritedFocus = overlaps || holds;
                inherited = inheritedSample?.State ?? (holds ? earlier.Target : basis);
            }
            previous = i;
        }
        return previous >= 0 ? At(previous, inherited, inheritedFocus, inheritedSample, time) : Layout(basis, false, viewport);
    }
    private static ScreenLayoutSample Layout(ScreenMotionState state, bool focus, ScreenAnchorViewport viewport)
    {
        var offset = DirectOffset(state, focus, viewport); return new(state, offset.X, offset.Y, state.Position);
    }
    private static ScreenLayoutSample Mix(ScreenMotionState from, ScreenMotionState to, bool fromFocus, bool toFocus, double progress,
        ScreenAnchorViewport v, NormalizedPoint anchor, NormalizedPoint? inheritedAnchor, ScreenLayoutSample? inheritedSample = null)
    {
        double t = Math.Clamp(progress, 0, 1);
        double Lerp(double a, double b) => a + (b - a) * t;
        double Angle(double a, double b) { double delta = (b - a) % 360; if (delta > 180) delta -= 360; if (delta < -180) delta += 360; return a + delta * t; }
        double Position(double a, double b, double fitted, double canvas)
        {
            var w0 = from.Scale * fitted; var w1 = to.Scale * fitted; var denom = canvas - Lerp(w0, w1);
            return Math.Abs(denom) > .001 ? Lerp(a * (canvas - w0), b * (canvas - w1)) / denom : Lerp(a, b);
        }
        var state = new ScreenMotionState(new(Position(from.Position.X, to.Position.X, v.FittedWidth, v.CanvasWidth), Position(from.Position.Y, to.Position.Y, v.FittedHeight, v.CanvasHeight)),
            Lerp(from.Scale, to.Scale), Angle(from.RotationX, to.RotationX), Angle(from.RotationY, to.RotationY), Angle(from.RotationZ, to.RotationZ), Lerp(from.Perspective, to.Perspective));
        var a = inheritedSample is { } sample ? new NormalizedPoint(sample.OffsetX, sample.OffsetY) : DirectOffset(from, fromFocus, v);
        var b = DirectOffset(to, toFocus, v);
        inheritedAnchor = inheritedSample?.ProjectionAnchor ?? inheritedAnchor;
        return new(state, Lerp(a.X, b.X), Lerp(a.Y, b.Y), inheritedAnchor is { } point ? new(Lerp(point.X, anchor.X), Lerp(point.Y, anchor.Y)) : anchor);
    }
    private static NormalizedPoint DirectOffset(ScreenMotionState state, bool focus, ScreenAnchorViewport v)
    {
        double size = state.Scale, w = v.FittedWidth * size, h = v.FittedHeight * size;
        double left = (v.BorderWidthAtScaleOne + v.LeftAtScaleOne) * size, right = (v.BorderWidthAtScaleOne + v.RightAtScaleOne) * size;
        double top = (v.BorderWidthAtScaleOne + v.TopAtScaleOne) * size, bottom = (v.BorderWidthAtScaleOne + v.BottomAtScaleOne) * size;
        if (!focus) return new(state.Position.X * (v.CanvasWidth - w - left - right) + w / 2 + left - v.CanvasWidth / 2,
            state.Position.Y * (v.CanvasHeight - h - top - bottom) + h / 2 + top - v.CanvasHeight / 2);
        double recenter = Math.Clamp((size - 1) / 2, 0, 1) * .62;
        double Center(double canvas, double content, double start, double end, double point)
        {
            point = Math.Clamp(point, 0, 1); double bounded = start + point * Math.Max(canvas - start - end, 0);
            return bounded + ((canvas + start - end) / 2 - bounded) * recenter + (.5 - point) * content - canvas / 2;
        }
        return new(Center(v.CanvasWidth, w, left, right, state.Position.X), Center(v.CanvasHeight, h, top, bottom, state.Position.Y));
    }
}
