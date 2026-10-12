using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>The look of a <see cref="StatusDot"/>.</summary>
public enum DotKind
{
    /// <summary>A lit ember with its glow (live, running, confirmed).</summary>
    Ember,

    /// <summary>An ember ring (waiting: detected, next preset, waiting for focus).</summary>
    Ring,

    /// <summary>An ash ring (off: Dead by Daylight not running).</summary>
    Off,

    /// <summary>Entity red with a glow (attention: restart needed, override).</summary>
    Red,
}

/// <summary>
/// The 8 px status dot (EmberDot): ember, ember ring, ash ring or red. <see cref="IsHeartbeat"/> plays the terror-radius
/// lub-dub (scale pulse plus an expanding ring, 1.2 s, ambient: it stops when the <see cref="MotionPolicy"/> forbids
/// ambient motion). <see cref="Catch"/> (DBD launched: the dot catches like a match) and <see cref="Pop"/> (confirmed)
/// are one-shots.
/// </summary>
public sealed class StatusDot : FrameworkElement
{
    /// <summary>The dot's look.</summary>
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(DotKind), typeof(StatusDot),
        new FrameworkPropertyMetadata(DotKind.Ember, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((StatusDot)d).Rebuild()));

    /// <summary>Diameter in DIPs (default 8).</summary>
    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(StatusDot),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((StatusDot)d).Rebuild()));

    /// <summary>The waiting-for-focus heartbeat (ambient).</summary>
    public static readonly DependencyProperty IsHeartbeatProperty = DependencyProperty.Register(
        nameof(IsHeartbeat), typeof(bool), typeof(StatusDot),
        new PropertyMetadata(false, (d, _) => ((StatusDot)d).UpdateAmbient()));

    private readonly Grid _root = new();
    private readonly Ellipse _glow = new() { IsHitTestVisible = false };
    private readonly Ellipse _dot = new();
    private readonly Ellipse _ring = new() { Opacity = 0, StrokeThickness = 1.5, Stroke = CampfirePalette.Ember };
    private readonly ScaleTransform _dotScale = new(1, 1);
    private readonly ScaleTransform _ringScale = new(1, 1);
    private readonly MotionPolicyWatch _watch;
    private readonly AmbientLoop _heartbeat;
    private MotionMoment? _moment;

    /// <summary>Creates an ember dot.</summary>
    public StatusDot()
    {
        _dot.RenderTransform = _dotScale;
        _dot.RenderTransformOrigin = new Point(0.5, 0.5);
        _ring.RenderTransform = _ringScale;
        _ring.RenderTransformOrigin = new Point(0.5, 0.5);
        _root.Children.Add(_glow);
        _root.Children.Add(_dot);
        _root.Children.Add(_ring);
        AddVisualChild(_root);
        Focusable = false;
        _heartbeat = new AmbientLoop(this, "StatusDot.heartbeat", BuildHeartbeat, AmbientLoop.LivelyFrameRate);
        _watch = new MotionPolicyWatch(this, UpdateAmbient);

        // a dot that is not drawn (e.g. in a collapsed line) does not beat
        IsVisibleChanged += (_, _) => UpdateAmbient();
        Rebuild();
    }

    /// <summary>The dot's look.</summary>
    public DotKind Kind
    {
        get => (DotKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Diameter in DIPs.</summary>
    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    /// <summary>The waiting-for-focus heartbeat (lub-dub, then rest; ambient).</summary>
    public bool IsHeartbeat
    {
        get => (bool)GetValue(IsHeartbeatProperty);
        set => SetValue(IsHeartbeatProperty, value);
    }

    /// <summary>The heartbeat's clocks are running.</summary>
    public bool IsHeartbeatRunning => _heartbeat.IsRunning;

    /// <inheritdoc />
    protected override int VisualChildrenCount => 1;

    /// <summary>Dead by Daylight launched: the dot catches like a match (scale .4 → 1.35 → 1 with a fade in, 420 ms after 80 ms).</summary>
    public void Catch() => PlayPop(0.4, 1.35, 420, 80);

    /// <summary>Confirmed: the dot pops in (scale .3 → 1.5 → 1, 420 ms) after <paramref name="delayMs"/>.</summary>
    public void Pop(double delayMs = 0) => PlayPop(0.3, 1.5, 420, delayMs);

    /// <inheritdoc />
    protected override Visual GetVisualChild(int index) => index == 0 ? _root : throw new ArgumentOutOfRangeException(nameof(index));

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        _root.Measure(new Size(Diameter, Diameter));
        return new Size(Diameter, Diameter);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        double d = Diameter;
        _root.Arrange(new Rect((finalSize.Width - d) / 2, (finalSize.Height - d) / 2, d, d));
        return finalSize;
    }

    private void Rebuild()
    {
        double d = Diameter;
        _dot.Width = d;
        _dot.Height = d;

        // the heartbeat ring sits 1.5 px outside the dot (CSS inset: -1.5px)
        _ring.Width = d + 3;
        _ring.Height = d + 3;
        _ring.Margin = new Thickness(-1.5);

        // the glow (CSS box-shadow 0 0 10px 2px): a soft disc 2.75 x the dot, no Effect
        double g = d * 2.75;
        _glow.Width = g;
        _glow.Height = g;
        _glow.Margin = new Thickness(-(g - d) / 2);
        _dot.Fill = null;
        _dot.Stroke = null;
        _dot.StrokeThickness = 0;
        _glow.Fill = null;
        switch (Kind)
        {
            case DotKind.Ember:
                _dot.Fill = CampfirePalette.Ember;
                _glow.Fill = CampfirePalette.Radial(0.5, 0.5, 0.5, 0.5, (0, CampfirePalette.Argb(0.6, CampfirePalette.EmberColor)), (0.45, CampfirePalette.Argb(0.35, CampfirePalette.EmberColor)), (1, CampfirePalette.Argb(0, CampfirePalette.EmberColor)));
                break;
            case DotKind.Ring:
                _dot.Stroke = CampfirePalette.Ember;
                _dot.StrokeThickness = 1.5;
                break;
            case DotKind.Off:
                _dot.Stroke = CampfirePalette.Ash;
                _dot.StrokeThickness = 1.5;
                break;
            case DotKind.Red:
                _dot.Fill = CampfirePalette.Entity;
                _glow.Fill = CampfirePalette.Radial(0.5, 0.5, 0.5, 0.5, (0, CampfirePalette.Argb(0.8, CampfirePalette.EntityColor)), (0.45, CampfirePalette.Argb(0.4, CampfirePalette.EntityColor)), (1, CampfirePalette.Argb(0, CampfirePalette.EntityColor)));
                break;
        }

        _ring.Stroke = Kind == DotKind.Red ? CampfirePalette.Entity : CampfirePalette.Ember;
    }

    private void PlayPop(double from, double overshoot, double ms, double delay)
    {
        _moment?.Stop();
        _moment = null;

        // the rest pose the pop lands on (a halted heartbeat may have left the dot mid-beat)
        _dot.Opacity = 1;
        _dotScale.ScaleX = 1;
        _dotScale.ScaleY = 1;
        MotionPolicy policy = _watch.Policy;
        if (!policy.ShouldAnimate)
        {
            return;
        }

        // keyframes (opacity 0, scale from) → (1, overshoot) at 50 % → (1, 1), the whole iteration eased out (the
        // prototype's A() default): the overshoot comes early
        var moment = new MotionMoment(this, "StatusDot.pop");
        moment.Add(_dot, OpacityProperty, Anim.Waapi(ms, Ease.Out, new[] { new WaapiKf(0, 0), new WaapiKf(0.5, 1), new WaapiKf(1, 1) }, delay));
        if (!policy.ReducedMotion)
        {
            var scale = new[] { new WaapiKf(0, from), new WaapiKf(0.5, overshoot), new WaapiKf(1, 1) };
            moment.Add(_dotScale, ScaleTransform.ScaleXProperty, Anim.Waapi(ms, Ease.Out, scale, delay));
            moment.Add(_dotScale, ScaleTransform.ScaleYProperty, Anim.Waapi(ms, Ease.Out, scale, delay));
        }

        _moment = moment;
        moment.Begin();
    }

    private void UpdateAmbient()
    {
        MotionPolicy policy = _watch.Policy;
        bool run = IsHeartbeat && IsLoaded && IsVisible && policy.ShouldRunAmbient;
        if (run)
        {
            _heartbeat.Run();
        }
        else if (!IsHeartbeat || !IsVisible || !policy.AnimationsEnabled || !policy.WindowVisible)
        {
            _heartbeat.Reset();
        }
        else
        {
            // shown but behind the game: it holds mid-beat and resumes from there
            _heartbeat.Halt();
        }
    }

    private IEnumerable<(DependencyObject, DependencyProperty, AnimationTimeline)> BuildHeartbeat()
    {
        // hbDot: 0 % 1, 7 % 1.5, 16 % 1, 24 % 1.32, 36 %-100 % 1 (CSS ease-out per segment), 1.2 s, first beat after 1 s
        const double p = 1200;
        Kf[] dot =
        {
            new(0, 1), new(p * 0.07, 1.5, Ease.Out), new(p * 0.16, 1, Ease.Out), new(p * 0.24, 1.32, Ease.Out),
            new(p * 0.36, 1, Ease.Out), new(p, 1),
        };
        DoubleAnimationUsingKeyFrames sx = Anim.Loop(p, dot);
        DoubleAnimationUsingKeyFrames sy = Anim.Loop(p, dot);
        Kf[] ringScale = { new(0, 1), new(p * 0.38, 2.7, Ease.Out), new(p, 2.7) };
        Kf[] ringFade = { new(0, 0.75), new(p * 0.38, 0, Ease.Out), new(p, 0) };
        DoubleAnimationUsingKeyFrames rx = Anim.Loop(p, ringScale);
        DoubleAnimationUsingKeyFrames ry = Anim.Loop(p, ringScale);
        DoubleAnimationUsingKeyFrames ro = Anim.Loop(p, ringFade);
        foreach (AnimationTimeline t in new AnimationTimeline[] { sx, sy, rx, ry, ro })
        {
            t.BeginTime = TimeSpan.FromSeconds(1);
        }

        yield return (_dotScale, ScaleTransform.ScaleXProperty, sx);
        yield return (_dotScale, ScaleTransform.ScaleYProperty, sy);
        yield return (_ringScale, ScaleTransform.ScaleXProperty, rx);
        yield return (_ringScale, ScaleTransform.ScaleYProperty, ry);
        yield return (_ring, OpacityProperty, ro);
    }
}
