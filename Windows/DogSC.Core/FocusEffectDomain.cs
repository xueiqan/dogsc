namespace DogSC.Core;

/// <summary>Per-animation focus in uncropped source-screen coordinates.</summary>
public sealed record FocusEffectStyle(string? Shape = null, double? AngleDegrees = null,
    string Target = "animation", NormalizedPoint? Center = null, double Size = .45,
    double Blur = .35, double Softness = .5, double Dimming = .18)
{
    public NormalizedPoint ResolvedCenter => Center ?? new(.5, .5);
    public bool IsValid => Shape is null or "radial" or "linear" && Target is "animation" or "pointer" or "fixed"
        && ResolvedCenter.IsValid && double.IsFinite(Size) && Size is >= .1 and <= 1
        && new[] { Blur, Softness, Dimming }.All(v => double.IsFinite(v) && v is >= 0 and <= 1)
        && (AngleDegrees is null || double.IsFinite(AngleDegrees.Value) && AngleDegrees.Value is >= 0 and <= 360);
    public void Validate() { if (!IsValid) throw new ArgumentException("聚焦效果参数无效。"); }
    public static FocusEffectStyle Linear => new("linear", 0, "fixed", Size: .32, Dimming: 0);
}
public sealed record FrameFocusSample(FocusEffectStyle Effect, NormalizedPoint Center, double Progress);

public static class FocusEffectEvaluator
{
    public static ScreenMotionClip? ActiveScreen(IEnumerable<ScreenMotionClip> clips, double time)
    {
        var clip = clips.OrderBy(c => c.Timing.StartTime).ThenBy(c => c.Id.ToString("D"), StringComparer.Ordinal).LastOrDefault(c => c.Timing.StartTime <= time);
        return clip is null || clip.Timing.ReturnDuration > 0 && time >= clip.Timing.EffectEndTime ? null : clip;
    }
    public static FrameFocusSample? Scene(ZoomAnimationClip? zoom, ScreenMotionClip? screen,
        NormalizedPoint zoomFocus, CompositionRect crop, NormalizedPoint? pointer, double time, MotionStyle motion)
    {
        FocusEffectStyle effect; NormalizedPoint anchor; TransitionTiming timing; bool holds;
        if (screen?.FocusEffect is { } selected)
        {
            effect = selected.Shape is null ? selected with { Shape = "linear", Target = "fixed", Dimming = 0 } : selected;
            anchor = new(crop.X + screen.Target.Position.X * crop.Width, crop.Y + screen.Target.Position.Y * crop.Height);
            timing = screen.Timing; holds = timing.ReturnDuration == 0;
        }
        else if (zoom?.FocusEffect is { } selectedZoom)
        {
            effect = selectedZoom; anchor = zoomFocus; holds = false;
            timing = new(zoom.StartTime, zoom.Duration, zoom.EnterDuration, zoom.ExitDuration, zoom.Easing,
                zoom.EnterProgressOffset, zoom.ExitProgressOffset, zoom.CustomCurve);
        }
        else return null;
        if (!effect.IsValid || effect.Blur <= 0 && effect.Dimming <= 0 || time < timing.StartTime) return null;
        var enter = Math.Min(timing.LeadInDuration, timing.Duration); double amount;
        if (enter > 0 && time < timing.StartTime + enter)
            amount = ZoomInterpolator.EasedProgress(timing.LeadInProgressOffset + (1 - timing.LeadInProgressOffset) * (time - timing.StartTime) / enter, timing.Easing, timing.CustomCurve, motion);
        else if (time < timing.EndTime) amount = 1;
        else if (timing.ReturnDuration > 0)
            amount = 1 - ZoomInterpolator.EasedProgress(Math.Clamp(timing.ReturnProgressOffset + (1 - timing.ReturnProgressOffset) * (time - timing.EndTime) / timing.ReturnDuration, 0, 1), timing.Easing, timing.CustomCurve, motion);
        else amount = holds ? 1 : 0;
        amount = Math.Clamp(amount, 0, 1); if (amount <= 0) return null;
        var center = effect.Target switch { "animation" => anchor, "pointer" => pointer ?? anchor, _ => effect.ResolvedCenter };
        return new(effect, center.Constrained(), amount);
    }
}
