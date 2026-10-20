using System.IO;
using DogSC.Core;

namespace DogSC.Windows;

public sealed record LegacyCanvasStyle(double Padding = 100, double ContentScale = 1, double PositionX = .5, double PositionY = .5,
    double CornerRadius = 24, double BorderWidth = 0, string BorderColor = "#FFFFFFFF", double ShadowStrength = .28,
    double BackgroundBlur = 0, double InsetOpacity = .125, string ScreenFrame = "none", double FrameScale = 1,
    double ToolbarScale = 1, double? OuterCornerRadius = null, double? ContentCornerRadius = null,
    string Title = "", string BrowserAddress = "", double PatternScale = 1, double PatternOpacity = 1);
public sealed record LegacyCanvasLayout(CompositionRect FittedRect, CompositionRect ContentRect, CompositionRect CardRect,
    double TopInset, double RightInset, double BottomInset, double LeftInset, double StyleScale, double CornerRadius,
    double BorderWidth, double ShadowRadius, double ShadowOffsetY, double ShadowOpacity);

public static class LegacyCanvasGeometry
{
    public static CompositionRect ZoomRect(LegacyCanvasLayout layout, ZoomViewportTransform viewport, double manualScale)
    {
        var basis = layout.ContentRect; var growth = manualScale * (viewport.Scale - 1) / 2;
        return new(basis.X + basis.Width / 2 - basis.Width * viewport.Scale / 2 + viewport.Translation.X * basis.Width
            + (layout.LeftInset - layout.RightInset) / manualScale * growth,
            basis.Y + basis.Height / 2 - basis.Height * viewport.Scale / 2 + viewport.Translation.Y * basis.Height
            + (layout.TopInset - layout.BottomInset) / manualScale * growth,
            basis.Width * viewport.Scale, basis.Height * viewport.Scale);
    }
    public static void Validate(LegacyCanvasStyle c)
    {
        if (c is null || new[] { c.Padding, c.ContentScale, c.PositionX, c.PositionY, c.CornerRadius, c.BorderWidth, c.ShadowStrength,
                c.BackgroundBlur, c.InsetOpacity, c.FrameScale, c.ToolbarScale, c.PatternScale, c.PatternOpacity }.Any(v => !double.IsFinite(v))
            || c.Padding is < 0 or > 2000 || c.ContentScale is < .2 or > 4 || c.PositionX is < 0 or > 1 || c.PositionY is < 0 or > 1
            || c.CornerRadius is < 0 or > 160 || c.BorderWidth is < 0 or > 60 || c.ShadowStrength is < 0 or > 1
            || c.BackgroundBlur is < 0 or > 500 || c.InsetOpacity is < 0 or > 1 || c.FrameScale is < .6 or > 1.6 || c.ToolbarScale is < .65 or > 1.6
            || c.PatternScale is < .4 or > 6 || c.PatternOpacity is < 0 or > 1 || c.Title.Length > 120 || c.BrowserAddress.Length > 240
            || new[] { c.OuterCornerRadius, c.ContentCornerRadius }.Any(v => v is not null && (!double.IsFinite(v.Value) || v < 0 || v > 160))
            || c.ScreenFrame is not ("none" or "windowLight" or "windowDark" or "browserLight" or "browserDark" or "devicePhone" or "deviceTablet"
                or "devicePhonePortrait" or "devicePhoneLandscape" or "deviceTabletPortrait" or "deviceTabletLandscape"))
            throw new InvalidDataException("原版画布像素尺寸或样机参数无效。");
    }
    public static LegacyCanvasLayout Evaluate(LegacyCanvasStyle style, double width, double height, double croppedAspect,
        ScreenMotionState? motion = null, ScreenLayoutSample? sampledLayout = null)
    {
        Validate(style);
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(croppedAspect) || width < 2 || height < 2 || croppedAspect <= 0)
            throw new InvalidDataException("原版画布采样尺寸无效。");
        var canonical = Math.Max(Math.Min(width, height), 1) / 1080;
        var padding = Math.Clamp(style.Padding * canonical, 0, Math.Min(width, height) * .42);
        var availableWidth = Math.Max(width - 2 * padding, 2); var availableHeight = Math.Max(height - 2 * padding, 2);
        var fittedWidth = Math.Min(availableWidth, availableHeight * croppedAspect); var fittedHeight = fittedWidth / croppedAspect;
        var fitted = new CompositionRect((width - fittedWidth) / 2, (height - fittedHeight) / 2, fittedWidth, fittedHeight);
        var manual = Math.Clamp(motion?.Scale ?? style.ContentScale, .2, 4);
        var border = Math.Clamp(style.BorderWidth, 0, 60) * canonical * manual;
        var w = fittedWidth * manual; var h = fittedHeight * manual;
        var px = motion?.Position.X ?? style.PositionX; var py = motion?.Position.Y ?? style.PositionY;
        var content = new CompositionRect((width - w) / 2 + (sampledLayout?.OffsetX ?? (px - .5) * (width - w - 2 * border)),
            (height - h) / 2 + (sampledLayout?.OffsetY ?? (py - .5) * (height - h - 2 * border)), w, h);
        double top = 0, bezel = 0;
        if (style.ScreenFrame is "windowLight" or "windowDark" or "browserLight" or "browserDark")
            top = Math.Clamp(46 * canonical * style.FrameScale * style.ToolbarScale, 4 * canonical, Math.Max(fittedHeight * .25, 4 * canonical)) * manual;
        else if (style.ScreenFrame.StartsWith("device", StringComparison.Ordinal))
        {
            bool phone = style.ScreenFrame.StartsWith("devicePhone", StringComparison.Ordinal);
            double shortEdge = Math.Max(Math.Min(fittedWidth, fittedHeight), 1);
            bezel = Math.Clamp(shortEdge * (phone ? .026 : .035) * style.FrameScale, 2 * canonical, shortEdge * (phone ? .07 : .08)) * manual;
        }
        var card = new CompositionRect(content.X - bezel - border, content.Y - top - bezel - border,
            content.Width + 2 * (bezel + border), content.Height + top + 2 * (bezel + border));
        bool covered = style.ScreenFrame == "none" && content.X <= 1e-7 && content.Y <= 1e-7 && content.X + w >= width - 1e-7 && content.Y + h >= height - 1e-7;
        return new(fitted, content, card, top + bezel, bezel, bezel, bezel, canonical,
            covered ? 0 : style.CornerRadius * canonical * manual, border, 22 * canonical * manual,
            12 * canonical * manual, style.ShadowStrength);
    }
}
