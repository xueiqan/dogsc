namespace DogSC.Core;

public sealed record ZoomViewportTransform(double Scale, NormalizedPoint Translation)
{
    public NormalizedPoint Apply(NormalizedPoint point) => new(.5 + Scale * (point.X - .5) + Translation.X,
        .5 + Scale * (point.Y - .5) + Translation.Y);
    public static ZoomViewportTransform Make(ZoomSample sample, CompositionRect? crop = null, CompositionRect? covering = null)
    {
        if (sample.ManualViewportTransition is { } transition)
        {
            var from = Make(new(transition.FromScale, transition.FromFocus, 1), crop, covering);
            var to = Make(new(transition.ToScale, transition.ToFocus, 1), crop, covering);
            double p = Math.Clamp(transition.Progress, 0, 1);
            return new(from.Scale + (to.Scale - from.Scale) * p,
                new(from.Translation.X + (to.Translation.X - from.Translation.X) * p, from.Translation.Y + (to.Translation.Y - from.Translation.Y) * p));
        }
        var bounds = crop ?? new(0, 0, 1, 1);
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
            throw new ArgumentOutOfRangeException(nameof(crop), "裁剪范围必须为有限数值。");
        double safeX = Math.Clamp(bounds.X, 0, .98), safeY = Math.Clamp(bounds.Y, 0, .98);
        bounds = new(safeX, safeY, Math.Clamp(bounds.Width, .02, 1 - safeX),
            Math.Clamp(bounds.Height, .02, 1 - safeY));
        double scale = Math.Clamp(sample.Scale, 1, 6);
        double x = Math.Clamp((sample.Focus.X - bounds.X) / bounds.Width, 0, 1), y = Math.Clamp((sample.Focus.Y - bounds.Y) / bounds.Height, 0, 1);
        double progress = Math.Clamp(sample.FramingProgress, 0, 1);
        double tx = x + (Math.Clamp(x, .3, .7) - x) * progress - (.5 + scale * (x - .5));
        double ty = y + (Math.Clamp(y, .3, .7) - y) * progress - (.5 + scale * (y - .5));
        if (covering is { } visible)
        {
            tx = Math.Clamp(tx, visible.X + visible.Width - .5 - scale / 2, visible.X - .5 + scale / 2);
            ty = Math.Clamp(ty, visible.Y + visible.Height - .5 - scale / 2, visible.Y - .5 + scale / 2);
        }
        return new(scale, new(tx, ty));
    }
}
