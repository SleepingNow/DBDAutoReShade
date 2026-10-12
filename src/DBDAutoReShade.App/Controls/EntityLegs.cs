using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>
/// The Entity: three jointed black limbs with a thin red rim light, reaching in from the top right behind the title
/// bar and the content (the prototype's exact geometry, <c>Themes/EntityGeometry.xaml</c>, in a 640 x 760 space pinned
/// top right; size the control's width, e.g. clamp(470, 50 % of the window, 800)). A soft red glow, and a fade into the
/// fog towards the bottom.
/// <list type="bullet">
/// <item>Idle sway: almost imperceptible (±0.4°, 13-19 s), ambient, only while idle and <see cref="IsSwayAllowed"/>;
/// it pauses in place.</item>
/// <item><see cref="IsLeaning"/> / <see cref="Lean"/>: while the app reads, the legs creep toward the map (900 ms).</item>
/// <item>One-shots: <see cref="Flex"/> (the flinch when the ember flares), <see cref="Twitch"/>, <see cref="Retract"/>
/// (the yank up out of frame at match end, a held beat, then back), <see cref="RimRun"/> (a red light runs down the
/// legs: restart needed).</item>
/// </list>
/// Reduced motion: no pose, no sway; the rim run becomes a short brightening of the rim.
/// </summary>
public sealed class EntityLegs : UserControl
{
    /// <summary>Decorative: the Entity's legs is hidden from assistive technology (the prototype's aria-hidden).</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new DecorativeAutomationPeer(this);

    /// <summary>The lean-in pose (scanning, waiting for focus).</summary>
    public static readonly DependencyProperty IsLeaningProperty = DependencyProperty.Register(
        nameof(IsLeaning), typeof(bool), typeof(EntityLegs), new PropertyMetadata(false, (d, _) => ((EntityLegs)d).ApplyPose(animate: ((EntityLegs)d).IsLoaded)));

    /// <summary>The idle sway may run (the ambient budget: off while another loop owns the screen, e.g. the live campfire).</summary>
    public static readonly DependencyProperty IsSwayAllowedProperty = DependencyProperty.Register(
        nameof(IsSwayAllowed), typeof(bool), typeof(EntityLegs), new PropertyMetadata(true, (d, _) => ((EntityLegs)d).UpdateAmbient()));

    private const double ViewWidth = 640, ViewHeight = 760;
    // (PublicationOnly: the XAML load never runs under the Lazy's lock; the geometry is frozen, so any thread may use it)
    private static readonly Lazy<Geometry[]> LegGeometry = new(LoadLegs, LazyThreadSafetyMode.PublicationOnly);

    // hips (rotation centres), lean poses and sway timings per leg: far, front, right
    private static readonly Point[] Hips = { new(480, -150), new(820, -190), new(720, 420) };
    private static readonly (double X, double Y, double Angle)[] LeanPoses = { (-8, 12, 1.2), (-10, 18, 1), (-10, 4, -2) };
    private static readonly (double Period, double Phase)[] SwayTimings = { (15000, 5000), (19000, 7000), (13000, 3000) };

    private readonly Canvas _canvas = new() { Width = ViewWidth, Height = ViewHeight };
    private readonly RotateTransform _yankTurn = new(0, ViewWidth, 0);
    private readonly TranslateTransform _yankMove = new();
    private readonly Leg[] _legs = new Leg[3];
    private readonly TranslateTransform _band = new();
    private readonly MotionPolicyWatch _watch;
    private readonly AmbientLoop _sway;
    private MotionMoment? _pose;
    private MotionMoment? _flex;
    private MotionMoment? _retract;
    private MotionMoment? _rim;

    /// <summary>Creates the legs (idle).</summary>
    public EntityLegs()
    {
        Focusable = false;
        IsTabStop = false;
        IsHitTestVisible = false;

        // the red light the Entity stands in
        var light = new Path
        {
            Data = new RectangleGeometry(new Rect(160, -60, 520, 520)),
            Fill = CampfirePalette.Radial(1, 0, 0.75, 0.75,
                (0, CampfirePalette.Argb(0.28, CampfirePalette.EntityColor)), (0.55, CampfirePalette.Argb(0.06, CampfirePalette.EntityColor)), (1, CampfirePalette.Argb(0, CampfirePalette.EntityColor))),
        };
        _canvas.Children.Add(light);

        // a band of light that runs down the legs (WPF: an OpacityMask whose gradient moves)
        var band = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, -560),
            EndPoint = new Point(0, -260),
            Transform = _band,
        };
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
        band.GradientStops.Add(new GradientStop(Colors.White, 0.5));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));

        Geometry[] legs = LegGeometry.Value;
        for (int i = 0; i < 3; i++)
        {
            _legs[i] = new Leg(legs[i], Hips[i], band, far: i == 0);
            _canvas.Children.Add(_legs[i].Pose);
        }

        _canvas.RenderTransform = new TransformGroup { Children = { _yankTurn, _yankMove } };

        // drop-shadow(0 0 9px rgba(176,24,45,.22)), then the fade into the fog at the bottom
        var glow = new DropShadowEffect { Color = CampfirePalette.EntityColor, ShadowDepth = 0, BlurRadius = 18, Opacity = 0.32 };
        glow.Freeze();
        var glowHost = new Grid { Width = ViewWidth, Height = ViewHeight, Effect = glow };
        glowHost.Children.Add(_canvas);
        var maskHost = new Grid
        {
            Width = ViewWidth,
            Height = ViewHeight,
            OpacityMask = CampfirePalette.Linear(new Point(0, 0), new Point(0, 1),
                (0, Colors.Black), (0.62, Colors.Black), (0.84, Color.FromArgb(115, 0, 0, 0)), (1, Color.FromArgb(20, 0, 0, 0))),
        };
        maskHost.Children.Add(glowHost);
        Content = new Viewbox
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = maskHost,
        };
        _sway = new AmbientLoop(this, "EntityLegs.sway", BuildSway, AmbientLoop.SlowFrameRate);
        _watch = new MotionPolicyWatch(this, OnPolicyChanged);
    }

    /// <summary>The lean-in pose.</summary>
    public bool IsLeaning
    {
        get => (bool)GetValue(IsLeaningProperty);
        set => SetValue(IsLeaningProperty, value);
    }

    /// <summary>The idle sway may run.</summary>
    public bool IsSwayAllowed
    {
        get => (bool)GetValue(IsSwayAllowedProperty);
        set => SetValue(IsSwayAllowedProperty, value);
    }

    /// <summary>The sway's clocks are running.</summary>
    public bool IsSwaying => _sway.IsRunning;

    /// <summary>Leans in (true) or settles back to idle (false), 900 ms ease-in-out.</summary>
    public void Lean(bool lean) => IsLeaning = lean;

    /// <summary>The flinch when the ember flares (preset confirmed): each leg contracts toward its hip, then settles (1.1 s).</summary>
    public void Flex()
    {
        _flex?.Stop();
        _flex = null;
        MotionPolicy policy = _watch.Policy;
        if (!policy.ShouldAnimate || policy.ReducedMotion)
        {
            return;
        }

        // keyframes none (ease-out) → rotate(-1.6deg) scale(.972) at 20 % (ease-in-out) → none, the whole iteration
        // eased out (the prototype's A() default)
        const double ms = 1100;
        var moment = new MotionMoment(this, "EntityLegs.flex");
        for (int i = 0; i < 3; i++)
        {
            double delay = 140 + i * 50;
            Leg leg = _legs[i];
            moment.Add(leg.FxTurn, RotateTransform.AngleProperty, Anim.Waapi(ms, Ease.Out, new[] { new WaapiKf(0, 0, Ease.Out), new WaapiKf(0.2, -1.6, Ease.InOut), new WaapiKf(1, 0) }, delay));
            var scale = new[] { new WaapiKf(0, 1, Ease.Out), new WaapiKf(0.2, 0.972, Ease.InOut), new WaapiKf(1, 1) };
            moment.Add(leg.FxScale, ScaleTransform.ScaleXProperty, Anim.Waapi(ms, Ease.Out, scale, delay));
            moment.Add(leg.FxScale, ScaleTransform.ScaleYProperty, Anim.Waapi(ms, Ease.Out, scale, delay));
        }

        _flex = moment;
        moment.Begin();
    }

    /// <summary>A short nervous twitch of every leg (about 320 ms, staggered 40 ms).</summary>
    public void Twitch()
    {
        _flex?.Stop();
        _flex = null;
        MotionPolicy policy = _watch.Policy;
        if (!policy.ShouldAnimate || policy.ReducedMotion)
        {
            return;
        }

        var moment = new MotionMoment(this, "EntityLegs.twitch");
        for (int i = 0; i < 3; i++)
        {
            double delay = i * 40;
            double sign = i == 1 ? -1 : 1;
            moment.Add(_legs[i].FxTurn, RotateTransform.AngleProperty, Anim.Keys(delay, new[]
            {
                new Kf(0, 0), new Kf(60, 1.4 * sign, Ease.Out), new Kf(140, -0.8 * sign, Ease.InOut), new Kf(220, 0.4 * sign, Ease.InOut), new Kf(320, 0, Ease.Out),
            }));
        }

        _flex = moment;
        moment.Begin();
    }

    /// <summary>
    /// Match end: the Entity takes the realm. A yank up out of frame (2 %, -36 %, -3°), a held beat, then it eases back
    /// down (2 s in all).
    /// </summary>
    public void Retract()
    {
        _retract?.Stop();
        _retract = null;
        MotionPolicy policy = _watch.Policy;
        if (!policy.ShouldAnimate || policy.ReducedMotion)
        {
            return;
        }

        // keyframes 0 (ease-in-out) → up at .28 → held to .46 (ease-out) → 0 of a 2 s iteration that is itself eased
        // out (the prototype's A() default): up within about 140 ms, back by about 500 ms
        const double ms = 2000;
        double dx = 0.02 * ViewWidth, dy = -0.36 * ViewHeight;
        WaapiKf[] Yank(double to) => new[] { new WaapiKf(0, 0, Ease.InOut), new WaapiKf(0.28, to), new WaapiKf(0.46, to, Ease.Out), new WaapiKf(1, 0) };
        var moment = new MotionMoment(this, "EntityLegs.retract");
        moment.Add(_yankMove, TranslateTransform.XProperty, Anim.Waapi(ms, Ease.Out, Yank(dx)));
        moment.Add(_yankMove, TranslateTransform.YProperty, Anim.Waapi(ms, Ease.Out, Yank(dy)));
        moment.Add(_yankTurn, RotateTransform.AngleProperty, Anim.Waapi(ms, Ease.Out, Yank(-3)));
        _retract = moment;
        moment.Begin();
    }

    /// <summary>Restart needed: a red rim light runs down the legs once (1.3 s after 220 ms).</summary>
    public void RimRun()
    {
        _rim?.Stop();
        _rim = null;
        MotionPolicy policy = _watch.Policy;
        if (!policy.ShouldAnimate)
        {
            return;
        }

        var moment = new MotionMoment(this, "EntityLegs.rim");
        if (policy.ReducedMotion)
        {
            foreach (Leg leg in _legs)
            {
                moment.Add(leg.Rim, OpacityProperty, Anim.Keys(0, new[] { new Kf(0, 0.7), new Kf(80, 1), new Kf(200, 0.7) }));
            }
        }
        else
        {
            moment.Add(_band, TranslateTransform.YProperty, Anim.Keys(220, new[] { new Kf(0, 0), new Kf(1300, 1320, Ease.InOut) }));
            foreach (Leg leg in _legs)
            {
                moment.Add(leg.Hot, OpacityProperty, Anim.Keys(0, new[] { new Kf(0, 0), new Kf(219, 0), new Kf(220, 1), new Kf(1520, 1), new Kf(1521, 0) }));
            }
        }

        _rim = moment;
        moment.Begin();
    }

    /// <summary>Freezes every running one-shot at <paramref name="time"/> (frame captures).</summary>
    public void FreezeAt(TimeSpan time)
    {
        foreach (MotionMoment? moment in new[] { _pose, _flex, _retract, _rim })
        {
            moment?.FreezeAt(time);
        }
    }

    private static Geometry[] LoadLegs()
    {
        var dictionary = (ResourceDictionary)Application.LoadComponent(new Uri("/DBDAutoReShade;component/Themes/EntityGeometry.xaml", UriKind.Relative));
        var result = new Geometry[3];
        string[] keys = { "EntityLegFar", "EntityLegFront", "EntityLegRight" };
        for (int i = 0; i < 3; i++)
        {
            var pieces = (string[])dictionary[keys[i]];
            Geometry union = Geometry.Parse(pieces[0]);
            for (int p = 1; p < pieces.Length; p++)
            {
                union = Geometry.Combine(union, Geometry.Parse(pieces[p]), GeometryCombineMode.Union, null, 0.05, ToleranceType.Absolute);
            }

            union.Freeze();
            result[i] = union;
        }

        return result;
    }

    private void OnPolicyChanged()
    {
        if (!_watch.Policy.ShouldAnimate)
        {
            foreach (MotionMoment? moment in new[] { _pose, _flex, _retract, _rim })
            {
                moment?.Stop();
            }
        }

        ApplyPose(animate: false);
        UpdateAmbient();
    }

    private void ApplyPose(bool animate)
    {
        MotionPolicy policy = _watch?.Policy ?? MotionPolicy.For(this);
        bool lean = IsLeaning && !policy.ReducedMotion;
        var from = _legs.Select(l => (l.PoseMove.X, l.PoseMove.Y, l.PoseTurn.Angle)).ToArray();
        _pose?.Stop();
        _pose = null;
        for (int i = 0; i < 3; i++)
        {
            _legs[i].PoseMove.X = lean ? LeanPoses[i].X : 0;
            _legs[i].PoseMove.Y = lean ? LeanPoses[i].Y : 0;
            _legs[i].PoseTurn.Angle = lean ? LeanPoses[i].Angle : 0;
        }

        UpdateAmbient();
        if (!animate || _watch is null || !policy.ShouldAnimate || policy.ReducedMotion)
        {
            return;
        }

        var moment = new MotionMoment(this, "EntityLegs.pose");
        for (int i = 0; i < 3; i++)
        {
            Leg leg = _legs[i];
            moment.Add(leg.PoseMove, TranslateTransform.XProperty, Anim.FromTo(from[i].X, leg.PoseMove.X, 900, Ease.InOut));
            moment.Add(leg.PoseMove, TranslateTransform.YProperty, Anim.FromTo(from[i].Y, leg.PoseMove.Y, 900, Ease.InOut));
            moment.Add(leg.PoseTurn, RotateTransform.AngleProperty, Anim.FromTo(from[i].Angle, leg.PoseTurn.Angle, 900, Ease.InOut));
        }

        _pose = moment;
        moment.Begin();
    }

    private void UpdateAmbient()
    {
        if (_sway is null || _watch is null)
        {
            return;
        }

        MotionPolicy policy = _watch.Policy;
        if (!policy.AnimationsEnabled || policy.ReducedMotion)
        {
            _sway.Reset();
            return;
        }

        bool run = IsLoaded && IsSwayAllowed && !IsLeaning && policy.ShouldRunAmbient;
        _sway.Set(run);
    }

    private IEnumerable<(DependencyObject, DependencyProperty, AnimationTimeline)> BuildSway()
    {
        // legSway: rotate(-.4deg) ↔ rotate(.5deg), CSS ease-in-out, alternate; 15 / 19 / 13 s, started 5 / 7 / 3 s in
        for (int i = 0; i < 3; i++)
        {
            (double period, double phase) = SwayTimings[i];
            yield return (_legs[i].SwayTurn, RotateTransform.AngleProperty,
                Anim.Loop(period, new[] { new Kf(0, -0.4), new Kf(period, 0.5, Ease.CssInOut) }, alternate: true, phaseMs: phase));
        }
    }

    /// <summary>One leg: pose > sway > one-shot transforms, then the rim, the limb and the hot rim light.</summary>
    private sealed class Leg
    {
        public Leg(Geometry geometry, Point hip, Brush band, bool far)
        {
            PoseTurn = new RotateTransform(0, hip.X, hip.Y);
            SwayTurn = new RotateTransform(0, hip.X, hip.Y);
            FxScale = new ScaleTransform(1, 1, hip.X, hip.Y);
            FxTurn = new RotateTransform(0, hip.X, hip.Y);
            Rim = new Path { Data = geometry, Fill = CampfirePalette.Entity, Opacity = 0.7, RenderTransform = new TranslateTransform(-1.6, 1.3) };
            var limb = new Path { Data = geometry, Fill = CampfirePalette.Brush(Color.FromRgb(3, 6, 8)) };
            Hot = new Canvas { Width = ViewWidth, Height = ViewHeight, Opacity = 0, OpacityMask = band, RenderTransform = new TranslateTransform(-1.6, 1.3) };
            Hot.Children.Add(new Path { Data = geometry, Stroke = CampfirePalette.Brush(0.32, Color.FromRgb(0xFF, 0x2A, 0x44)), StrokeThickness = 9, StrokeLineJoin = PenLineJoin.Round });
            Hot.Children.Add(new Path { Data = geometry, Stroke = CampfirePalette.Brush(Color.FromRgb(0xFF, 0x52, 0x66)), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round });
            var fx = new Canvas { Width = ViewWidth, Height = ViewHeight, RenderTransform = new TransformGroup { Children = { FxScale, FxTurn } } };
            fx.Children.Add(Rim);
            fx.Children.Add(limb);
            fx.Children.Add(Hot);
            var sway = new Canvas { Width = ViewWidth, Height = ViewHeight, RenderTransform = SwayTurn };
            sway.Children.Add(fx);
            Pose = new Canvas
            {
                Width = ViewWidth,
                Height = ViewHeight,
                Opacity = far ? 0.42 : 1,
                RenderTransform = new TransformGroup { Children = { PoseTurn, PoseMove } },
            };
            Pose.Children.Add(sway);
        }

        public Canvas Pose { get; }

        public RotateTransform PoseTurn { get; }

        public TranslateTransform PoseMove { get; } = new();

        public RotateTransform SwayTurn { get; }

        public ScaleTransform FxScale { get; }

        public RotateTransform FxTurn { get; }

        public Path Rim { get; }

        public Canvas Hot { get; }
    }
}
