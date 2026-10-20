using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DogSC.Core;

namespace DogSC.Windows.SelfTest;

internal static class MotionContinuityChecks
{
    private static readonly Guid FirstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly ScreenMotionState ScreenBasis = new(new(.45, .55), .9, -5, 10, 15, .6);
    private static readonly ScreenMotionState ScreenFirst = new(new(.12, .3), 1.15, 25, -30, 40, 1.1);
    private static readonly ScreenMotionState ScreenSecond = new(new(.78, .68), 1.45, -20, 35, -15, .3);
    private static readonly CameraMotionState CameraBasis = new(new(.45, .55), .24, "方形", .35, .9, 10);
    private static readonly CameraMotionState CameraFirst = new(new(.12, .3), .42, "横向", .2, .8, 40);
    private static readonly CameraMotionState CameraSecond = new(new(.78, .68), .7, "全屏", .9, .7, -20);
    private static readonly ScreenAnchorViewport Viewport = new(1920, 1080, 1280, 720, 4, 64, 11, 24, 17);
    private sealed record Scenario(string Name, TransitionTiming First, TransitionTiming Second);
    private static readonly Scenario[] Scenarios =
    [
        new("takeover during lead", Timing(1, 4, 4, 2), Timing(3, 2, 1, 1)),
        new("takeover during return", Timing(1, 2, 1, 4), Timing(5, 2, 1, 1)),
        new("zero return holds target", Timing(1, 2, 1, 0), Timing(5, 2, 1, 0)),
        new("adjacent clip inherits target", Timing(1, 2, 1, 1), Timing(3, 2, 1, 1))
    ];

    public static int RunCore(string root)
    {
        var checks = new Checks("core", root);
        foreach (var scenario in Scenarios)
        {
            var first = new ScreenMotionClip(FirstId, scenario.First, ScreenFirst);
            var second = new ScreenMotionClip(SecondId, scenario.Second, ScreenSecond);
            ScreenMotionClip[] track = [first, second];
            var inherited = ScreenMotionInterpolator.Sample([first], second.Timing.StartTime, ScreenBasis);
            checks.Equal(ScreenValues(ScreenMotionInterpolator.Sample(track, second.Timing.StartTime, ScreenBasis)),
                ScreenValues(inherited), scenario.Name + " start inherits every state component");
            checks.Equal(ScreenValues(ScreenMotionInterpolator.Sample(track, second.Timing.StartTime + .5, ScreenBasis)),
                ScreenValues(MixScreen(inherited, ScreenSecond, .5)), scenario.Name + " latest clip advances from inherited state");
            var settled = ScreenMotionInterpolator.Sample(track, second.Timing.EffectEndTime + .2, ScreenBasis);
            checks.Equal(ScreenValues(settled), ScreenValues(second.Timing.ReturnDuration == 0 ? ScreenSecond : ScreenBasis),
                scenario.Name + " completed return or hold has the correct endpoint");
            var forward = new[] { second.Timing.StartTime, second.Timing.StartTime + .25, second.Timing.StartTime + .75 }
                .Select(time => (Time: time, State: ScreenValues(ScreenMotionInterpolator.Sample(track, time, ScreenBasis)))).ToArray();
            foreach (var item in forward.Reverse())
            {
                checks.Equal(ScreenValues(ScreenMotionInterpolator.Sample(track, item.Time, ScreenBasis)), item.State,
                    scenario.Name + " reverse time queries match forward snapshots");
                checks.Equal(ScreenValues(ScreenMotionInterpolator.Sample(track.Reverse(), item.Time, ScreenBasis)), item.State,
                    scenario.Name + " reverse input order is deterministic");
            }
        }
        var single = new ScreenMotionClip(FirstId, Timing(1, 2, 1, 1), ScreenFirst);
        foreach (var (time, expected) in new[]
        {
            (0d, ScreenBasis), (1d, ScreenBasis), (1.5, MixScreen(ScreenBasis, ScreenFirst, .5)),
            (2d, ScreenFirst), (2.75, ScreenFirst), (3.5, MixScreen(ScreenFirst, ScreenBasis, .5)), (4.2, ScreenBasis)
        }) checks.Equal(ScreenValues(ScreenMotionInterpolator.Sample([single], time, ScreenBasis)), ScreenValues(expected), $"single clip at {time}s keeps the nonoverlap contract");
        var far = new ScreenMotionClip(SecondId, Timing(6, 2, 1, 1), ScreenSecond);
        checks.Equal(ScreenValues(ScreenMotionInterpolator.Sample([single, far], 6.5, ScreenBasis)),
            ScreenValues(ScreenMotionInterpolator.Sample([far], 6.5, ScreenBasis)), "completed previous return does not affect a separated clip");
        var tiedTiming = Timing(2, 2, 0, 0);
        var tiedA = new ScreenMotionClip(FirstId, tiedTiming, ScreenFirst);
        var tiedB = new ScreenMotionClip(SecondId, tiedTiming, ScreenSecond);
        foreach (ScreenMotionClip[] tied in new[] { new[] { tiedA, tiedB }, new[] { tiedB, tiedA } })
            checks.Equal(ScreenValues(ScreenMotionInterpolator.Sample(tied, 2.5, ScreenBasis)), ScreenValues(ScreenSecond), "equal starts choose the largest fixed ID");
        var phaseFirst = new ScreenMotionClip(FirstId, Timing(1, 2, 1, 4), ScreenFirst);
        var phaseSecond = new ScreenMotionClip(SecondId, Timing(5, 2, 1, 1) with { LeadInProgressOffset = .3, ReturnProgressOffset = .4 }, ScreenSecond);
        var phaseOrigin = ScreenMotionInterpolator.Sample([phaseFirst], 5, ScreenBasis);
        checks.Equal(ScreenValues(ScreenMotionInterpolator.Sample([phaseFirst, phaseSecond], 5.25, ScreenBasis)),
            ScreenValues(MixScreen(phaseOrigin, ScreenSecond, .475)), "nonzero inherited lead phase is preserved");
        checks.Equal(ScreenValues(ScreenMotionInterpolator.Sample([phaseFirst, phaseSecond], 7.5, ScreenBasis)),
            ScreenValues(MixScreen(ScreenSecond, ScreenBasis, .7)), "nonzero return phase is preserved");
        var phased = single with { Timing = single.Timing with { LeadInProgressOffset = .25 } };
        checks.Equal(ScreenValues(ScreenMotionInterpolator.Sample([phased], 1.5, ScreenBasis)),
            ScreenValues(MixScreen(ScreenBasis, ScreenFirst, .625)), "first clip keeps its authored lead phase");
        foreach (double time in InvalidTimes())
            checks.Equal(ScreenValues(ScreenMotionInterpolator.Sample([single with { Timing = Timing(1, 2, 1, 0) }], time, ScreenBasis)),
                ScreenValues(ScreenBasis), "negative or nonfinite time returns basis");
        var large = Enumerable.Range(0, 10000).Select(i => new ScreenMotionClip(Id(i), Timing(i * .1, .5, .4, .5),
            i % 2 == 0 ? ScreenFirst : ScreenSecond)).ToArray();
        checks.Benchmark(time => ScreenValues(ScreenMotionInterpolator.Sample(large, time, ScreenBasis)));
        return checks.Finish();
    }

    public static int RunCamera(string root)
    {
        var checks = new Checks("camera", root);
        CameraMotionSample Sample(IEnumerable<CameraMotionClip> clips, double time, CameraMotionState? basis = null)
            => SceneMotionTimeline.Camera(clips, time, basis ?? CameraBasis, 16d / 9, 16d / 9);
        var basisSample = ResolveCamera(CameraBasis);
        var secondSample = ResolveCamera(CameraSecond);
        foreach (var scenario in Scenarios)
        {
            var first = new CameraMotionClip(FirstId, scenario.First, CameraFirst);
            var second = new CameraMotionClip(SecondId, scenario.Second, CameraSecond);
            CameraMotionClip[] track = [first, second];
            var inherited = Sample([first], second.Timing.StartTime);
            checks.Equal(CameraValues(Sample(track, second.Timing.StartTime)), CameraValues(inherited),
                scenario.Name + " start inherits every camera component");
            checks.Equal(CameraValues(Sample(track, second.Timing.StartTime + .5)),
                CameraValues(MixCamera(inherited, secondSample, .5)), scenario.Name + " latest camera clip advances from inherited state");
            checks.Equal(CameraValues(Sample(track, second.Timing.EffectEndTime + .2)),
                CameraValues(second.Timing.ReturnDuration == 0 ? secondSample : basisSample), scenario.Name + " completed camera return or hold endpoint");
            var forward = new[] { second.Timing.StartTime, second.Timing.StartTime + .25, second.Timing.StartTime + .75 }
                .Select(time => (Time: time, State: CameraValues(Sample(track, time)))).ToArray();
            foreach (var item in forward.Reverse())
            {
                checks.Equal(CameraValues(Sample(track, item.Time)), item.State, scenario.Name + " reverse camera time queries match forward snapshots");
                checks.Equal(CameraValues(Sample(track.Reverse(), item.Time)), item.State, scenario.Name + " reverse camera input order is deterministic");
            }
        }
        var single = new CameraMotionClip(FirstId, Timing(1, 2, 1, 1), CameraFirst);
        foreach (var (time, expected) in new[]
        {
            (0d, basisSample), (1d, basisSample), (1.5, MixCamera(basisSample, ResolveCamera(CameraFirst), .5)),
            (2d, ResolveCamera(CameraFirst)), (2.75, ResolveCamera(CameraFirst)),
            (3.5, MixCamera(ResolveCamera(CameraFirst), basisSample, .5)), (4.2, basisSample)
        }) checks.Equal(CameraValues(Sample([single], time)), CameraValues(expected), $"single camera clip at {time}s keeps the nonoverlap contract");
        var far = new CameraMotionClip(SecondId, Timing(6, 2, 1, 1), CameraSecond);
        checks.Equal(CameraValues(Sample([single, far], 6.5)), CameraValues(Sample([far], 6.5)), "separated camera clips do not retain an expired return");
        var tiedA = new CameraMotionClip(FirstId, Timing(2, 2, 0, 0), CameraFirst);
        var tiedB = new CameraMotionClip(SecondId, Timing(2, 2, 0, 0), CameraSecond);
        foreach (CameraMotionClip[] tied in new[] { new[] { tiedA, tiedB }, new[] { tiedB, tiedA } })
            checks.Equal(CameraValues(Sample(tied, 2.5)), CameraValues(secondSample), "equal camera starts choose the largest fixed ID");
        var phaseFirst = new CameraMotionClip(FirstId, Timing(1, 2, 1, 4), CameraFirst);
        var phaseSecond = new CameraMotionClip(SecondId, Timing(5, 2, 1, 1) with { LeadInProgressOffset = .3, ReturnProgressOffset = .4 }, CameraSecond);
        checks.Equal(CameraValues(Sample([phaseFirst, phaseSecond], 5.25)),
            CameraValues(MixCamera(Sample([phaseFirst], 5), secondSample, .475)), "camera inherited lead phase is preserved");
        checks.Equal(CameraValues(Sample([phaseFirst, phaseSecond], 7.5)),
            CameraValues(MixCamera(secondSample, basisSample, .7)), "camera return phase is preserved");
        var phased = single with { Timing = single.Timing with { LeadInProgressOffset = .25 } };
        checks.Equal(CameraValues(Sample([phased], 1.5)), CameraValues(MixCamera(basisSample, ResolveCamera(CameraFirst), .625)),
            "first camera clip keeps its authored lead phase");
        var hidden = CameraSecond with { Opacity = 0 };
        var fadeOut = new CameraMotionClip(FirstId, Timing(1, 2, 1, 0), hidden);
        checks.Equal(CameraValues(Sample([fadeOut], 1.5)), CameraValues(basisSample with { Opacity = basisSample.Opacity / 2 }),
            "hidden camera fades at the existing geometry");
        checks.Equal(CameraValues(Sample([single], 1.5, CameraBasis with { Opacity = 0 })),
            CameraValues(ResolveCamera(CameraFirst) with { Opacity = CameraFirst.Opacity / 2 }), "reappearing camera fades at the destination geometry");
        foreach (double time in InvalidTimes())
            checks.Equal(CameraValues(Sample([single with { Timing = Timing(1, 2, 1, 0) }], time)), CameraValues(basisSample),
                "negative or nonfinite camera time returns basis");
        var large = Enumerable.Range(0, 10000).Select(i => new CameraMotionClip(Id(i), Timing(i * .1, .5, .4, .5),
            i % 2 == 0 ? CameraFirst : CameraSecond)).ToArray();
        checks.Benchmark(time => CameraValues(Sample(large, time)));
        return checks.Finish();
    }

    public static int RunLayout(string root)
    {
        var checks = new Checks("layout", root);
        ScreenLayoutSample Sample(IEnumerable<ScreenMotionClip> clips, double time)
            => ScreenMotionLayoutTimeline.Sample(clips, time, ScreenBasis, Viewport);
        var basisLayout = DirectLayout(ScreenBasis, false);
        var secondLayout = DirectLayout(ScreenSecond, true);
        foreach (var scenario in Scenarios)
        {
            var first = new ScreenMotionClip(FirstId, scenario.First, ScreenFirst);
            var second = new ScreenMotionClip(SecondId, scenario.Second, ScreenSecond);
            ScreenMotionClip[] track = [first, second];
            var inherited = Sample([first], second.Timing.StartTime);
            checks.Equal(LayoutValues(Sample(track, second.Timing.StartTime)), LayoutValues(inherited),
                scenario.Name + " start inherits state offsets and projection anchor");
            checks.Equal(LayoutValues(Sample(track, second.Timing.StartTime + .5)),
                LayoutValues(MixLayout(inherited, secondLayout, .5)), scenario.Name + " latest layout interpolates the complete inherited sample");
            checks.Equal(LayoutValues(Sample(track, second.Timing.EffectEndTime + .2)),
                LayoutValues(second.Timing.ReturnDuration == 0 ? secondLayout : basisLayout), scenario.Name + " completed layout return or hold endpoint");
            var forward = new[] { second.Timing.StartTime, second.Timing.StartTime + .25, second.Timing.StartTime + .75 }
                .Select(time => (Time: time, State: LayoutValues(Sample(track, time)))).ToArray();
            foreach (var item in forward.Reverse())
            {
                checks.Equal(LayoutValues(Sample(track, item.Time)), item.State, scenario.Name + " reverse layout time queries match forward snapshots");
                checks.Equal(LayoutValues(Sample(track.Reverse(), item.Time)), item.State, scenario.Name + " reverse layout input order is deterministic");
            }
        }
        var single = new ScreenMotionClip(FirstId, Timing(1, 2, 1, 1), ScreenFirst);
        var targetLayout = DirectLayout(ScreenFirst, true);
        foreach (var (time, expected) in new[]
        {
            (0d, basisLayout), (1d, basisLayout with { ProjectionAnchor = ScreenFirst.Position }),
            (1.5, MixLayout(basisLayout, targetLayout, .5) with { ProjectionAnchor = ScreenFirst.Position }),
            (2d, targetLayout), (2.75, targetLayout),
            (3.5, MixLayout(targetLayout, basisLayout, .5) with { ProjectionAnchor = ScreenFirst.Position }), (4.2, basisLayout)
        }) checks.Equal(LayoutValues(Sample([single], time)), LayoutValues(expected), $"single layout at {time}s keeps the nonoverlap focus contract");
        var far = new ScreenMotionClip(SecondId, Timing(6, 2, 1, 1), ScreenSecond);
        checks.Equal(LayoutValues(Sample([single, far], 6.5)), LayoutValues(Sample([far], 6.5)), "separated layout clip agrees with the same clip alone");
        var tiedA = new ScreenMotionClip(FirstId, Timing(2, 2, 0, 0), ScreenFirst);
        var tiedB = new ScreenMotionClip(SecondId, Timing(2, 2, 0, 0), ScreenSecond);
        foreach (ScreenMotionClip[] tied in new[] { new[] { tiedA, tiedB }, new[] { tiedB, tiedA } })
            checks.Equal(LayoutValues(Sample(tied, 2.5)), LayoutValues(secondLayout), "equal layout starts choose the largest fixed ID");
        var phaseFirst = new ScreenMotionClip(FirstId, Timing(1, 2, 1, 4), ScreenFirst);
        var phaseSecond = new ScreenMotionClip(SecondId, Timing(5, 2, 1, 1) with { LeadInProgressOffset = .3, ReturnProgressOffset = .4 }, ScreenSecond);
        checks.Equal(LayoutValues(Sample([phaseFirst, phaseSecond], 5.25)), LayoutValues(MixLayout(Sample([phaseFirst], 5), secondLayout, .475)),
            "layout inherited lead phase is preserved");
        checks.Equal(LayoutValues(Sample([phaseFirst, phaseSecond], 7.5)),
            LayoutValues(MixLayout(secondLayout, basisLayout, .7) with { ProjectionAnchor = ScreenSecond.Position }), "layout return phase and existing return anchor are preserved");
        var phased = single with { Timing = single.Timing with { LeadInProgressOffset = .25 } };
        checks.Equal(LayoutValues(Sample([phased], 1.5)),
            LayoutValues(MixLayout(basisLayout, targetLayout, .625) with { ProjectionAnchor = ScreenFirst.Position }),
            "first layout clip keeps its authored lead phase");
        var angleBasis = ScreenBasis with { RotationZ = 170 };
        var angleTarget = ScreenFirst with { RotationZ = -170 };
        var wrapped = ScreenMotionLayoutTimeline.Sample([single with { Target = angleTarget }], 1.5, angleBasis, Viewport);
        checks.Equal(LayoutValues(wrapped), LayoutValues(MixLayout(DirectLayout(angleBasis, false), DirectLayout(angleTarget, true), .5)
            with { ProjectionAnchor = angleTarget.Position }), "layout preserves shortest-angle interpolation");
        foreach (double time in InvalidTimes())
            checks.Equal(LayoutValues(Sample([single with { Timing = Timing(1, 2, 1, 0) }], time)), LayoutValues(basisLayout),
                "negative or nonfinite layout time returns basis");
        var large = Enumerable.Range(0, 10000).Select(i => new ScreenMotionClip(Id(i), Timing(i * .1, .5, .4, .5),
            i % 2 == 0 ? ScreenFirst : ScreenSecond)).ToArray();
        checks.Benchmark(time => LayoutValues(Sample(large, time)));
        return checks.Finish();
    }

    private static TransitionTiming Timing(double start, double duration, double lead, double back)
        => new(start, duration, lead, back, ZoomEasingPreset.Linear);
    private static Guid Id(int index) => Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}");
    private static double[] InvalidTimes() => [-1, double.NaN, double.NegativeInfinity, double.PositiveInfinity];
    private static double Lerp(double from, double to, double progress) => from + (to - from) * progress;
    private static double[] ScreenValues(ScreenMotionState s) => [s.Position.X, s.Position.Y, s.Scale, s.RotationX, s.RotationY, s.RotationZ, s.Perspective];
    private static double[] CameraValues(CameraMotionSample s) => [s.Position.X, s.Position.Y, s.Size, s.AspectRatio, s.CornerFraction, s.FullscreenProgress, s.Opacity, s.Rotation];
    private static double[] LayoutValues(ScreenLayoutSample s) => [.. ScreenValues(s.State), s.OffsetX, s.OffsetY, s.ProjectionAnchor.X, s.ProjectionAnchor.Y];
    private static ScreenMotionState MixScreen(ScreenMotionState from, ScreenMotionState to, double p) => new(
        new(Lerp(from.Position.X, to.Position.X, p), Lerp(from.Position.Y, to.Position.Y, p)), Lerp(from.Scale, to.Scale, p),
        Lerp(from.RotationX, to.RotationX, p), Lerp(from.RotationY, to.RotationY, p), Lerp(from.RotationZ, to.RotationZ, p), Lerp(from.Perspective, to.Perspective, p));
    private static CameraMotionSample ResolveCamera(CameraMotionState s) => new(s.Position, s.Size,
        s.Layout switch { "圆形" or "方形" => 1, "横向" => 1.5, "纵向" => .72, "原始比例" or "全屏" => 16d / 9, _ => throw new Exception("Invalid camera fixture layout.") },
        s.Layout == "圆形" ? .5 : s.Layout == "全屏" ? 0 : s.Roundness / 2, s.Layout == "全屏" ? 1 : 0, s.Opacity, s.Rotation);
    private static CameraMotionSample MixCamera(CameraMotionSample origin, CameraMotionSample to, double p)
    {
        if (to.Opacity < .01) return origin with { Opacity = Lerp(origin.Opacity, 0, p) };
        if (origin.Opacity < .01) return to with { Opacity = Lerp(0, to.Opacity, p) };
        return new(new(Lerp(origin.Position.X, to.Position.X, p), Lerp(origin.Position.Y, to.Position.Y, p)), Lerp(origin.Size, to.Size, p),
            Lerp(origin.AspectRatio, to.AspectRatio, p), Lerp(origin.CornerFraction, to.CornerFraction, p), Lerp(origin.FullscreenProgress, to.FullscreenProgress, p),
            Lerp(origin.Opacity, to.Opacity, p), Lerp(origin.Rotation, to.Rotation, p));
    }
    private static ScreenLayoutSample DirectLayout(ScreenMotionState s, bool focus)
    {
        var v = Viewport; double w = v.FittedWidth * s.Scale, h = v.FittedHeight * s.Scale;
        double left = (v.BorderWidthAtScaleOne + v.LeftAtScaleOne) * s.Scale, right = (v.BorderWidthAtScaleOne + v.RightAtScaleOne) * s.Scale;
        double top = (v.BorderWidthAtScaleOne + v.TopAtScaleOne) * s.Scale, bottom = (v.BorderWidthAtScaleOne + v.BottomAtScaleOne) * s.Scale;
        double Center(double canvas, double content, double begin, double end, double point)
        {
            var bounded = begin + Math.Clamp(point, 0, 1) * Math.Max(canvas - begin - end, 0);
            return bounded + ((canvas + begin - end) / 2 - bounded) * Math.Clamp((s.Scale - 1) / 2, 0, 1) * .62
                + (.5 - point) * content - canvas / 2;
        }
        return new(s, focus ? Center(v.CanvasWidth, w, left, right, s.Position.X)
            : s.Position.X * (v.CanvasWidth - w - left - right) + w / 2 + left - v.CanvasWidth / 2,
            focus ? Center(v.CanvasHeight, h, top, bottom, s.Position.Y)
            : s.Position.Y * (v.CanvasHeight - h - top - bottom) + h / 2 + top - v.CanvasHeight / 2, s.Position);
    }
    private static ScreenLayoutSample MixLayout(ScreenLayoutSample from, ScreenLayoutSample to, double p)
    {
        double Position(double a, double b, double fitted, double canvas)
        {
            var w0 = from.State.Scale * fitted; var w1 = to.State.Scale * fitted; var denominator = canvas - Lerp(w0, w1, p);
            return Math.Abs(denominator) > .001 ? Lerp(a * (canvas - w0), b * (canvas - w1), p) / denominator : Lerp(a, b, p);
        }
        double Angle(double a, double b)
        { double delta = (b - a) % 360; if (delta > 180) delta -= 360; if (delta < -180) delta += 360; return a + delta * p; }
        var a = from.State; var b = to.State;
        var state = new ScreenMotionState(new(Position(a.Position.X, b.Position.X, Viewport.FittedWidth, Viewport.CanvasWidth),
            Position(a.Position.Y, b.Position.Y, Viewport.FittedHeight, Viewport.CanvasHeight)), Lerp(a.Scale, b.Scale, p),
            Angle(a.RotationX, b.RotationX), Angle(a.RotationY, b.RotationY), Angle(a.RotationZ, b.RotationZ), Lerp(a.Perspective, b.Perspective, p));
        return new(state, Lerp(from.OffsetX, to.OffsetX, p), Lerp(from.OffsetY, to.OffsetY, p),
            new(Lerp(from.ProjectionAnchor.X, to.ProjectionAnchor.X, p), Lerp(from.ProjectionAnchor.Y, to.ProjectionAnchor.Y, p)));
    }
    private sealed class Checks(string name, string root)
    {
        private readonly List<string> _cases = [];
        private readonly List<object> _benchmark = [];
        public void Equal(double[] actual, double[] expected, string description)
        {
            if (actual.Length != expected.Length || actual.Where((value, i) => !double.IsFinite(value) || Math.Abs(value - expected[i]) > 1e-7).Any())
                throw new Exception($"FAIL motion {name}: {description}; expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");
            _cases.Add(description);
        }
        public void Benchmark(Func<double, double[]> sample)
        {
            foreach (double time in new[] { 0d, 11.15, 499.95, 999.95, 1001d })
            {
                var watch = Stopwatch.StartNew(); var state = sample(time); watch.Stop();
                if (state.Any(value => !double.IsFinite(value))) throw new Exception("FAIL motion benchmark: nonfinite sample.");
                _benchmark.Add(new { time, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, state });
                _cases.Add("10000 clips finite sample at " + time);
            }
        }
        public int Finish()
        {
            var directory = Path.GetFullPath(root); MediaExport.RejectLinks(directory); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"motion-{name}-continuity-result.json"); MediaExport.RejectLinks(path);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            { success = true, path = name, checks = _cases.Count, cases = _cases, benchmarkClips = 10000, benchmark = _benchmark,
                benchmarkPolicy = "Timing only; no invented FPS or wall-clock pass threshold.", recordedAtUtc = DateTimeOffset.UtcNow },
                new JsonSerializerOptions { WriteIndented = true }));
            return _cases.Count;
        }
    }
}
