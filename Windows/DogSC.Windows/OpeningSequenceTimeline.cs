using System.IO;

namespace DogSC.Windows;

public sealed record OpeningSequenceStyle(bool Enabled = false, string Preset = "无", string Curve = "swift",
    double Duration = 2.2, double Stagger = .16, string[]? IncludedElements = null, string[]? ElementOrder = null);
public sealed record OpeningSequenceSample(double Opacity, double Scale, double OffsetX, double OffsetY,
    double RotationX, double RotationY, double Perspective, double Remaining)
{
    public static readonly OpeningSequenceSample Identity = new(1, 1, 0, 0, 0, 0, 0, 0);
}

/// <summary>RecorderCore OpeningSequence and FrameScene.openingSample authored timing and paths.</summary>
public static class OpeningSequenceTimeline
{
    private static readonly string[] Elements = ["屏幕", "摄像头", "贴图"];
    public static void Validate(OpeningSequenceStyle style)
    {
        if (style is null || style.Preset is not ("无" or "聚拢" or "侧滑" or "轻 3D") || style.Enabled && style.Preset == "无"
            || style.Curve is not ("smooth" or "swift" or "gentle")
            || !double.IsFinite(style.Duration) || style.Duration is < .4 or > 8 || !double.IsFinite(style.Stagger) || style.Stagger is < 0 or > 1.2
            || new[] { style.IncludedElements, style.ElementOrder }.Any(a => a is not null && (a.Length > 3 || a.Any(e => !Elements.Contains(e)) || a.Distinct().Count() != a.Length)))
            throw new InvalidDataException("开场顺序、曲线或时间参数无效。");
    }
    public static string[] ActiveElements(OpeningSequenceStyle style, IEnumerable<string> available)
    {
        Validate(style);
        var included = style.IncludedElements ?? Elements;
        var order = (style.ElementOrder ?? Elements).Concat(Elements).Distinct();
        var present = available.ToHashSet();
        return order.Where(e => included.Contains(e) && present.Contains(e)).ToArray();
    }
    public static OpeningSequenceSample Sample(OpeningSequenceStyle style, string element, IEnumerable<string> available,
        double time, double width, double height)
    {
        var active = ActiveElements(style, available);
        int index = Array.IndexOf(active, element);
        if (!style.Enabled || time >= style.Duration || index < 0) return OpeningSequenceSample.Identity;
        if (!double.IsFinite(time) || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidDataException("开场采样时间或画布尺寸无效。");
        var stagger = Math.Min(style.Stagger, active.Length <= 1 ? 1.2 : Math.Min(1.2, Math.Max(style.Duration - .18, 0) / (active.Length - 1)));
        var duration = Math.Max(style.Duration - stagger * Math.Max(active.Length - 1, 0), .18);
        var linear = Math.Clamp((time - stagger * index) / duration, 0, 1);
        double Progress(double x) => style.Curve switch { "smooth" => x * x * x * (x * (x * 6 - 15) + 10), "swift" => 1 - Math.Pow(1 - x, 4), _ => 1 - Math.Pow(1 - x, 3) };
        var eased = Progress(linear); var opacity = Progress(Math.Min(linear * 1.65, 1)); var remaining = 1 - eased;
        var rotationRemaining = Math.Pow(1 - linear, 1.5);
        var overshoot = 1 + 1.35 * Math.Pow(linear - 1, 3) + .35 * Math.Pow(linear - 1, 2);
        (double Scale, double X, double Y) path = (style.Preset, element) switch
        {
            ("聚拢", "屏幕") => (.72, -1.05, -.62), ("聚拢", "摄像头") => (.58, 1.12, .72), ("聚拢", _) => (.62, -1.30, -.70),
            ("侧滑", "屏幕") => (.94, -1.12, 0), ("侧滑", "摄像头") => (.88, 1.15, 0), ("侧滑", _) => (.9, -1.35, 0),
            ("轻 3D", "屏幕") => (.80, -1.05, .08), ("轻 3D", "摄像头") => (.72, 1.10, .16), _ => (.76, -1.30, -.12)
        };
        bool three = style.Preset == "轻 3D" && element == "屏幕";
        return new(opacity, path.Scale + (Math.Clamp(overshoot, 0, 1.04) - path.Scale) * eased,
            width * path.X * remaining, height * path.Y * remaining, three ? 9 * rotationRemaining : 0,
            three ? -22 * rotationRemaining : 0, three ? .72 * rotationRemaining : 0, remaining);
    }
}
