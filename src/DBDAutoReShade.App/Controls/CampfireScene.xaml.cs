using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>The state of the fire in a <see cref="CampfireScene"/> (the prototype's campfire modes).</summary>
public enum FireState
{
    /// <summary>No fire (the prototype's "cold": Dead by Daylight is not running). Cold logs and a thread of smoke.</summary>
    Out,

    /// <summary>Burned down to glowing logs (the prototype's "embers": the scan is paused). A faint warm light.</summary>
    Embers,

    /// <summary>The fire burns (the prototype's "lit"). Live scenes flicker and send up embers.</summary>
    Burning,
}

/// <summary>
/// The campfire clearing: a vector scene of the lobby fire in the fog, used when no map is known (the Status plate)
/// and for Global / Menu. Set <see cref="FireState"/> (a change animates when <see cref="AnimateChanges"/>), or call
/// <see cref="Catch"/>, <see cref="DieOut"/>, <see cref="BurnDown"/> and <see cref="Rekindle"/> to tell the change.
/// A live scene (<see cref="IsLive"/>, the Status plate only) flickers and sends up 12 embers while the
/// <see cref="MotionPolicy"/> allows ambient motion; otherwise it shows still sparks.
/// </summary>
public partial class CampfireScene : UserControl
{
    /// <summary>Decorative: the campfire clearing is hidden from assistive technology (the prototype's aria-hidden).</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new DecorativeAutomationPeer(this);

    /// <summary>The fire's state.</summary>
    public static readonly DependencyProperty FireStateProperty = DependencyProperty.Register(
        nameof(FireState), typeof(FireState), typeof(CampfireScene),
        new PropertyMetadata(FireState.Burning, (d, e) => ((CampfireScene)d).OnFireStateChanged((FireState)e.OldValue, (FireState)e.NewValue)));

    /// <summary>Live scene: the fire flickers and embers rise (ambient). Static copies leave it false.</summary>
    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(
        nameof(IsLive), typeof(bool), typeof(CampfireScene),
        new PropertyMetadata(false, (d, _) => ((CampfireScene)d).ApplyRest()));

    /// <summary>The fire's x position in the 400 x 200 scene (304 on the Status plate, 200 for the Global row, 292 on the Presets hero).</summary>
    public static readonly DependencyProperty FireXProperty = DependencyProperty.Register(
        nameof(FireX), typeof(double), typeof(CampfireScene),
        new PropertyMetadata(304.0, (d, _) => ((CampfireScene)d).ApplyFireX()));

    /// <summary>When true (default), changing <see cref="FireState"/> plays the matching moment.</summary>
    public static readonly DependencyProperty AnimateChangesProperty = DependencyProperty.Register(
        nameof(AnimateChanges), typeof(bool), typeof(CampfireScene), new PropertyMetadata(true));

    // The rising embers, from the prototype (seeded): x relative to the fire, y, radius, colour, period s,
    // phase s (negative delay), drift x, rise, peak opacity.
    private static readonly (double X, double Y, double R, uint Color, double Period, double Phase, double Dx, double Rise, double Opacity)[] RiserTable =
    {
        (-7.6, 172.5, 0.71, 0xFFF08A3C, 3.60, 1.86, 11.5, 107, 0.52),
        (8.6, 175.3, 0.63, 0xFFFFB36B, 3.76, 2.86, -17.1, 117, 0.69),
        (-4.7, 175.3, 0.60, 0xFFFFB36B, 5.86, 5.52, -8.4, 97, 0.88),
        (-3.0, 175.1, 0.75, 0xFFF08A3C, 4.25, 3.81, 9.5, 95, 0.78),
        (-0.5, 174.9, 0.68, 0xFFFFB36B, 3.98, 2.44, -2.1, 120, 0.65),
        (-0.7, 172.4, 0.82, 0xFFFFB36B, 5.44, 1.07, -9.9, 113, 0.69),
        (0.5, 174.6, 1.16, 0xFFF08A3C, 4.87, 1.81, 0.9, 91, 0.57),
        (5.9, 171.1, 1.21, 0xFFFFB36B, 3.83, 0.17, 5.6, 84, 0.90),
        (-3.4, 171.5, 1.16, 0xFFFFB36B, 5.24, 2.00, -8.6, 110, 0.91),
        (-3.1, 172.5, 0.85, 0xFFF08A3C, 5.25, 0.89, 2.1, 114, 0.89),
        (7.0, 173.0, 0.81, 0xFFFFB36B, 5.45, 4.99, 0.6, 125, 0.72),
        (0.8, 171.4, 0.92, 0xFFFFB36B, 6.35, 5.60, 16.5, 100, 0.60),
    };

    private readonly MotionPolicyWatch _watch;
    private readonly AmbientLoop _ambient;
    private readonly List<(Path Dot, TranslateTransform Move, int Index)> _risers = new();
    private readonly RadialGradientBrush _groundLitBrush;
    private readonly double _smokeLength;
    private MotionMoment? _moment;
    private bool _silent;

    /// <summary>Creates the scene (burning, static, fire at 304).</summary>
    public CampfireScene()
    {
        InitializeComponent();
        _groundLitBrush = ((RadialGradientBrush)GroundGlowLit.Fill).Clone();
        GroundGlowLit.Fill = _groundLitBrush;
        _smokeLength = Length(Smoke.Data) / Smoke.StrokeThickness;
        BuildRisers();
        _ambient = new AmbientLoop(this, "CampfireScene.flicker", BuildAmbient, AmbientLoop.LivelyFrameRate);
        _watch = new MotionPolicyWatch(this, OnPolicyChanged);

        // a clearing that is not drawn does not flicker: one collapsed, or one just taken off a plate (until its Unloaded
        // arrives it would follow the policy it falls back to, not its window's)
        IsVisibleChanged += (_, _) => UpdateAmbient();
        ApplyFireX();
        ApplyRest();
    }

    /// <summary>The fire's state.</summary>
    public FireState FireState
    {
        get => (FireState)GetValue(FireStateProperty);
        set => SetValue(FireStateProperty, value);
    }

    /// <summary>Live scene (flicker + rising embers, ambient-gated).</summary>
    public bool IsLive
    {
        get => (bool)GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    /// <summary>The fire's x position in the 400 x 200 scene.</summary>
    public double FireX
    {
        get => (double)GetValue(FireXProperty);
        set => SetValue(FireXProperty, value);
    }

    /// <summary>Changing <see cref="FireState"/> plays the matching moment (default true).</summary>
    public bool AnimateChanges
    {
        get => (bool)GetValue(AnimateChangesProperty);
        set => SetValue(AnimateChangesProperty, value);
    }

    /// <summary>The ambient loop (flicker and embers) is running.</summary>
    public bool IsAmbientRunning => _ambient.IsRunning;

    /// <summary>The fire catches (Dead by Daylight launched): a flare that overshoots and settles, the light swells once,
    /// embers start rising. Ends <see cref="FireState.Burning"/>.</summary>
    public void Catch() => SetFireState(FireState.Burning, FireTransition.Catch);

    /// <summary>The fire dies (Dead by Daylight closed): the flame sinks into the logs, the warm light lingers and goes,
    /// one thread of smoke rises. Ends <see cref="FireState.Out"/>.</summary>
    public void DieOut() => SetFireState(FireState.Out, FireTransition.DieOut);

    /// <summary>The fire burns down to embers (scan paused). Ends <see cref="FireState.Embers"/>.</summary>
    public void BurnDown() => SetFireState(FireState.Embers, FireTransition.BurnDown);

    /// <summary>The embers flare back up (scan resumed). Ends <see cref="FireState.Burning"/>.</summary>
    public void Rekindle() => SetFireState(FireState.Burning, FireTransition.Rekindle);

    /// <summary>Sets the state; <paramref name="animate"/> false applies it at once.</summary>
    public void SetFireState(FireState state, bool animate) => SetFireState(state, animate ? FireTransition.Auto : FireTransition.None);

    /// <summary>Freezes the running fire moment at <paramref name="time"/> (frame captures).</summary>
    public void FreezeMomentAt(TimeSpan time) => _moment?.FreezeAt(time);

    private enum FireTransition
    {
        Auto,
        None,
        Catch,
        DieOut,
        BurnDown,
        Rekindle,
    }

    private static double Length(Geometry geometry)
    {
        double length = 0;
        foreach (PathFigure figure in geometry.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute).Figures)
        {
            Point last = figure.StartPoint;
            foreach (PathSegment segment in figure.Segments)
            {
                IEnumerable<Point> points = segment switch
                {
                    PolyLineSegment poly => poly.Points,
                    LineSegment line => new[] { line.Point },
                    _ => Array.Empty<Point>(),
                };
                foreach (Point p in points)
                {
                    length += (p - last).Length;
                    last = p;
                }
            }
        }

        return length;
    }

    private void SetFireState(FireState state, FireTransition transition)
    {
        FireState old = FireState;
        _silent = true;
        try
        {
            FireState = state;
        }
        finally
        {
            _silent = false;
        }

        Transition(old, state, transition);
    }

    private void OnFireStateChanged(FireState oldState, FireState newState)
    {
        if (_silent)
        {
            return;
        }

        // a property change animates only on screen (a control being set up just takes its state)
        Transition(oldState, newState, AnimateChanges && IsLoaded ? FireTransition.Auto : FireTransition.None);
    }

    private void Transition(FireState from, FireState to, FireTransition kind)
    {
        // the current look of every layer (mid-moment values included), before the new rest pose is applied
        var before = Layers().ToDictionary(l => l, l => l.Opacity);
        _moment?.Stop();
        _moment = null;
        Smoke.StrokeDashArray = null;
        ApplyRest();

        MotionPolicy policy = _watch.Policy;
        if (kind == FireTransition.None || (from == to && kind == FireTransition.Auto) || !policy.ShouldAnimate)
        {
            return;
        }

        if (kind == FireTransition.Auto)
        {
            kind = (from, to) switch
            {
                (FireState.Out, FireState.Burning) => FireTransition.Catch,
                (FireState.Embers, FireState.Burning) => FireTransition.Rekindle,
                (FireState.Burning, FireState.Out) => FireTransition.DieOut,
                (FireState.Burning, FireState.Embers) => FireTransition.BurnDown,
                _ => FireTransition.Auto,
            };
        }

        var moment = new MotionMoment(this, "CampfireScene." + kind);
        bool reduced = policy.ReducedMotion;
        double fade = kind switch
        {
            FireTransition.Catch => 240,
            FireTransition.Rekindle => 420,
            FireTransition.DieOut => 1100,
            FireTransition.BurnDown => 650,
            _ => 220,
        };
        double delay = kind is FireTransition.DieOut or FireTransition.BurnDown ? 100 : 0;
        KeySpline curve = kind == FireTransition.DieOut ? Ease.InOut : Ease.Out;
        if (reduced)
        {
            fade = Math.Min(fade, 200);
            delay = Math.Min(delay, 100);
        }

        // The cold and warm pit are drawn one over the other (warm on top). The one underneath stays solid until the
        // one on top has finished fading, so the pit never turns see-through mid-change.
        var special = new HashSet<UIElement> { PitBackCold, PitBackWarm, PitFrontCold, PitFrontWarm, FlameWrap, Risers };
        if (!reduced && kind == FireTransition.DieOut)
        {
            special.Add(Smoke);
        }

        CrossfadePair(moment, PitBackCold, PitBackWarm, before, fade, delay, curve);
        CrossfadePair(moment, PitFrontCold, PitFrontWarm, before, fade, delay, curve);
        foreach ((UIElement layer, double was) in before)
        {
            if (!special.Contains(layer) && Math.Abs(layer.Opacity - was) > 0.001)
            {
                moment.Add(layer, OpacityProperty, Anim.FromTo(was, layer.Opacity, fade, curve, delay));
            }
        }

        if (reduced)
        {
            Fade(moment, FlameWrap, before[FlameWrap], fade);
            Fade(moment, Risers, before[Risers], fade);
        }
        else
        {
            AddFireMotion(moment, kind, before[FlameWrap], before[Risers]);
        }

        _moment = moment;
        moment.Ended += (_, _) =>
        {
            if (ReferenceEquals(_moment, moment))
            {
                _moment = null;
                Smoke.StrokeDashArray = null;
            }
        };
        moment.Begin();
    }

    private static void Fade(MotionMoment moment, UIElement layer, double was, double ms)
    {
        if (Math.Abs(layer.Opacity - was) > 0.001)
        {
            moment.Add(layer, OpacityProperty, Anim.FromTo(was, layer.Opacity, ms));
        }
    }

    private static void CrossfadePair(MotionMoment moment, UIElement under, UIElement over, Dictionary<UIElement, double> before, double fade, double delay, KeySpline curve)
    {
        double overWas = before[over];
        double underWas = before[under];
        if (Math.Abs(over.Opacity - overWas) > 0.001)
        {
            moment.Add(over, OpacityProperty, Anim.FromTo(overWas, over.Opacity, fade, curve, delay));
        }

        if (under.Opacity < underWas - 0.001)
        {
            // leaving from underneath: hold until the layer on top has arrived
            moment.Add(under, OpacityProperty, Anim.Keys(0, new[] { new Kf(0, underWas), new Kf(delay + fade, underWas), new Kf(delay + fade + 1, under.Opacity) }));
        }
    }

    private void AddFireMotion(MotionMoment moment, FireTransition kind, double flameWas, double risersWas)
    {
        switch (kind)
        {
            case FireTransition.Catch:
            case FireTransition.Rekindle:
            {
                // a quick flare that overshoots and settles (620 ms from 80 ms), the light swells once, embers start rising
                moment.Add(FlameWrap, OpacityProperty, Anim.Keys(80, new[] { new Kf(0, 0), new Kf(279, 1, Ease.Out), new Kf(620, 1) }));
                moment.Add(FlameWrapScale, ScaleTransform.ScaleXProperty, Anim.Keys(80, new[] { new Kf(0, 0.6), new Kf(279, 1.08, Ease.Out), new Kf(620, 1, Ease.InOut) }));
                moment.Add(FlameWrapScale, ScaleTransform.ScaleYProperty, Anim.Keys(80, new[] { new Kf(0, 0.25), new Kf(279, 1.22, Ease.Out), new Kf(620, 1, Ease.InOut) }));
                moment.Add(Flare, OpacityProperty, Anim.Keys(80, new[] { new Kf(0, 0), new Kf(270, 1, Ease.Out), new Kf(900, 0, Ease.InOut) }));
                if (Risers.Opacity > 0)
                {
                    moment.Add(Risers, OpacityProperty, Anim.FromTo(0, Risers.Opacity, 700, Ease.Out, 300));
                }

                break;
            }

            case FireTransition.DieOut:
            {
                // the flame sinks into the logs; the embers stop; the light lingers, then goes; one thread of smoke rises
                moment.Add(FlameWrap, OpacityProperty, Anim.FromTo(flameWas, 0, 700, Ease.InOut));
                moment.Add(FlameWrapScale, ScaleTransform.ScaleXProperty, Anim.FromTo(1, 0.7, 700, Ease.InOut));
                moment.Add(FlameWrapScale, ScaleTransform.ScaleYProperty, Anim.FromTo(1, 0.2, 700, Ease.InOut));
                Fade(moment, Risers, risersWas, 300);
                Smoke.StrokeDashArray = new DoubleCollection { _smokeLength, _smokeLength };
                moment.Add(Smoke, OpacityProperty, Anim.Keys(450, new[] { new Kf(0, 0), new Kf(120, Smoke.Opacity), new Kf(1500, Smoke.Opacity) }));
                moment.Add(Smoke, Shape.StrokeDashOffsetProperty, Anim.Keys(450, new[] { new Kf(0, _smokeLength), new Kf(120, _smokeLength), new Kf(1500, 0, Ease.Out) }));
                break;
            }

            case FireTransition.BurnDown:
            {
                // the fire burns down to embers
                moment.Add(FlameWrap, OpacityProperty, Anim.FromTo(flameWas, 0, 600));
                moment.Add(FlameWrapScale, ScaleTransform.ScaleXProperty, Anim.FromTo(1, 0.85, 600));
                moment.Add(FlameWrapScale, ScaleTransform.ScaleYProperty, Anim.FromTo(1, 0.35, 600));
                Fade(moment, Risers, risersWas, 300);
                break;
            }

            default:
            {
                Fade(moment, FlameWrap, flameWas, 220);
                Fade(moment, Risers, risersWas, 220);
                break;
            }
        }
    }

    private IEnumerable<UIElement> Layers()
    {
        yield return GlowLit;
        yield return GlowEmbers;
        yield return GroundGlowLit;
        yield return GroundGlowEmbers;
        yield return PitBackCold;
        yield return PitBackWarm;
        yield return BackHighlights;
        yield return EmberLogs;
        yield return FlameWrap;
        yield return PitFrontCold;
        yield return PitFrontWarm;
        yield return FrontHighlights;
        yield return Smoke;
        yield return Sparks;
        yield return Risers;
    }

    /// <summary>Sets every layer to the rest pose of the current state.</summary>
    private void ApplyRest()
    {
        FireState state = FireState;
        bool burning = state == FireState.Burning;
        bool embers = state == FireState.Embers;
        bool warm = state != FireState.Out;
        MotionPolicy policy = _watch?.Policy ?? MotionPolicy.For(this);
        bool risers = burning && IsLive && policy.AnimationsEnabled && !policy.ReducedMotion;

        GlowLit.Opacity = burning ? 1 : 0;
        GroundGlowLit.Opacity = burning ? 1 : 0;
        GlowEmbers.Opacity = embers ? 1 : 0;
        GroundGlowEmbers.Opacity = embers ? 1 : 0;
        PitBackCold.Opacity = warm ? 0 : 1;
        PitFrontCold.Opacity = warm ? 0 : 1;
        PitBackWarm.Opacity = warm ? 1 : 0;
        PitFrontWarm.Opacity = warm ? 1 : 0;
        BackHighlights.Opacity = burning ? 1 : 0;
        FrontHighlights.Opacity = burning ? 1 : 0;
        EmberLogs.Opacity = embers ? 1 : 0;
        FlameWrap.Opacity = burning ? 1 : 0;
        FlameWrapScale.ScaleX = 1;
        FlameWrapScale.ScaleY = 1;
        Flare.Opacity = 0;
        Smoke.Opacity = state switch { FireState.Out => 0.5, FireState.Embers => 0.35, _ => 0 };
        Smoke.StrokeDashOffset = 0;
        Risers.Opacity = risers ? 1 : 0;
        Sparks.Opacity = burning && !risers ? 1 : 0;
        if (!risers && _ambient is not null)
        {
            _ambient.Reset();
        }

        UpdateAmbient();
    }

    private void ApplyFireX()
    {
        double x = FireX;
        PitShift.X = x;
        var centre = new Point(x / 400.0, 0.92);
        GlowLitBrush.Center = centre;
        GlowLitBrush.GradientOrigin = centre;
        GlowEmbersBrush.Center = centre;
        GlowEmbersBrush.GradientOrigin = centre;
    }

    private void OnPolicyChanged()
    {
        MotionPolicy policy = _watch.Policy;
        if (!policy.ShouldAnimate && _moment is not null)
        {
            _moment.Stop();
            _moment = null;
            Smoke.StrokeDashArray = null;
        }

        bool risers = FireState == FireState.Burning && IsLive && policy.AnimationsEnabled && !policy.ReducedMotion;
        if (risers != Risers.Opacity > 0)
        {
            ApplyRest();
            return;
        }

        UpdateAmbient();
    }

    private void UpdateAmbient()
    {
        if (_ambient is null || _watch is null)
        {
            return;
        }

        bool run = IsLoaded && IsVisible && IsLive && FireState == FireState.Burning && _watch.Policy.ShouldRunAmbient;
        _ambient.Set(run);
    }

    private void BuildRisers()
    {
        for (int i = 0; i < RiserTable.Length; i++)
        {
            var r = RiserTable[i];
            var move = new TranslateTransform();
            var dot = new Path
            {
                Data = new EllipseGeometry(new Point(r.X, r.Y), r.R, r.R),
                Fill = CampfirePalette.Brush(Color.FromArgb((byte)(r.Color >> 24), (byte)(r.Color >> 16), (byte)(r.Color >> 8), (byte)r.Color)),
                Opacity = 0,
                RenderTransform = move,
            };
            Risers.Children.Add(dot);
            _risers.Add((dot, move, i));
        }
    }

    private IEnumerable<(DependencyObject, DependencyProperty, AnimationTimeline)> BuildAmbient()
    {
        // the flame flickers (1.9 s, CSS ease-in-out per segment, origin bottom centre)
        double p = 1900;
        yield return (FlameFlicker, ScaleTransform.ScaleXProperty, Anim.Loop(p, new[]
        {
            new Kf(0, 1), new Kf(p * 0.22, 1.03, Ease.CssInOut), new Kf(p * 0.47, 0.985, Ease.CssInOut),
            new Kf(p * 0.71, 1.02, Ease.CssInOut), new Kf(p, 1, Ease.CssInOut),
        }));
        yield return (FlameFlicker, ScaleTransform.ScaleYProperty, Anim.Loop(p, new[]
        {
            new Kf(0, 1), new Kf(p * 0.22, 0.95, Ease.CssInOut), new Kf(p * 0.47, 1.045, Ease.CssInOut),
            new Kf(p * 0.71, 0.965, Ease.CssInOut), new Kf(p, 1, Ease.CssInOut),
        }));

        // the light flickers with it (2.7 s, linear)
        double g = 2700;
        Kf[] glow =
        {
            new(0, 1), new(g * 0.11, 0.84), new(g * 0.19, 0.97), new(g * 0.33, 0.82), new(g * 0.46, 1),
            new(g * 0.58, 0.88), new(g * 0.71, 0.98), new(g * 0.84, 0.86), new(g, 1),
        };
        yield return (GlowLitBrush, Brush.OpacityProperty, Anim.Loop(g, glow));
        yield return (_groundLitBrush, Brush.OpacityProperty, Anim.Loop(g, glow));

        // twelve embers rise, each on its own slow loop
        foreach ((Path dot, TranslateTransform move, int index) in _risers)
        {
            var r = RiserTable[index];
            double d = r.Period * 1000;
            double phase = r.Phase * 1000;
            yield return (move, TranslateTransform.XProperty, Anim.Loop(d, new[] { new Kf(0, 0), new Kf(d, r.Dx) }, phaseMs: phase));
            yield return (move, TranslateTransform.YProperty, Anim.Loop(d, new[] { new Kf(0, 0), new Kf(d, -r.Rise) }, phaseMs: phase));
            yield return (dot, OpacityProperty, Anim.Loop(d, new[]
            {
                new Kf(0, 0), new Kf(d * 0.12, r.Opacity), new Kf(d * 0.68, r.Opacity * 0.55), new Kf(d, 0),
            }, phaseMs: phase));
        }
    }
}
