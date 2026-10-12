using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>What a <see cref="SkillCheck"/> shows.</summary>
public enum SkillCheckState
{
    /// <summary>Nothing (the dial rests invisible).</summary>
    Hidden,

    /// <summary>The app reads: the needle spins, one revolution per read; each revolution is a new check.</summary>
    Scanning,

    /// <summary>The scan is paused: the needle freezes where it is and the check goes grey.</summary>
    Paused,
}

/// <summary>
/// The DBD skill check, the hero of a scan, centred on the map plate (it fills the plate; the dial is 46 % of the
/// plate's height, 40 % on a short plate, slightly above centre). A thin light-grey ring, a hollow white good zone with
/// a solid white great block at its leading edge, a red spindle needle crossing the ring and, in the centre, a black key
/// with the hotkey (modifiers on a small line above the key).
/// <list type="bullet">
/// <item><see cref="State"/> <see cref="SkillCheckState.Scanning"/>: one linear revolution per read
/// (<see cref="RevolutionDuration"/>, ~1.4 s); the zone relocates every revolution (a deterministic sequence) with a
/// quick crossfade while the needle is far; <see cref="Revolution"/> is raised per revolution. Ambient: the spin holds
/// in place whenever the <see cref="MotionPolicy"/> forbids ambient motion. Reduced motion / animations off: no spin,
/// the needle steps once per revolution.</item>
/// <item><see cref="SkillCheckState.Paused"/>: frozen and grey; back to Scanning resumes from the same angle.</item>
/// <item><see cref="LandGreat"/>: map found. The needle runs on at 0.3°/ms and stops dead 4° into the white block;
/// at that moment <see cref="Hit"/> fires (white flash, burst ring, three sparks; the caller plays the Great sound);
/// the dial holds 0.3 s, lifts away (scale 1.06 + fade, 220 ms) and <see cref="Finished"/> fires (then change the
/// rest of the screen). <see cref="ComputeHitDelay"/> gives the delay without animating. Every landing raises exactly
/// one Hit and then one Finished: a landing cut short (the state changes to Scanning or Paused, or another
/// <see cref="LandGreat"/> comes in) raises whichever of them it still owes at once, so the sound and the map
/// reveal are never lost.</item>
/// </list>
/// The score event (<see cref="Score"/>) sits top right of the same area.
/// </summary>
public sealed class SkillCheck : UserControl
{
    /// <summary>Decorative: the skill-check dial (its score event still speaks) is hidden from assistive technology (the prototype's aria-hidden).</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new DecorativeAutomationPeer(this, keepChildren: true);

    /// <summary>What the check shows.</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(SkillCheckState), typeof(SkillCheck),
        new PropertyMetadata(SkillCheckState.Hidden, (d, e) => ((SkillCheck)d).OnStateChanged((SkillCheckState)e.OldValue, (SkillCheckState)e.NewValue)));

    /// <summary>The hotkey in the centre key, e.g. "F8" or "Ctrl+Alt+Shift+NumPad5".</summary>
    public static readonly DependencyProperty KeyTextProperty = DependencyProperty.Register(
        nameof(KeyText), typeof(string), typeof(SkillCheck), new PropertyMetadata("F8", (d, _) => ((SkillCheck)d).UpdateKey()));

    /// <summary>One revolution = one read (default 1.4 s).</summary>
    public static readonly DependencyProperty RevolutionDurationProperty = DependencyProperty.Register(
        nameof(RevolutionDuration), typeof(TimeSpan), typeof(SkillCheck), new PropertyMetadata(TimeSpan.FromMilliseconds(1400), (d, _) => ((SkillCheck)d).RestartSpinClock()));

    /// <summary>A delay before the dial appears when a scan starts (e.g. 480 ms while a map sinks into the fog first).</summary>
    public static readonly DependencyProperty EnterDelayProperty = DependencyProperty.Register(
        nameof(EnterDelay), typeof(TimeSpan), typeof(SkillCheck), new PropertyMetadata(TimeSpan.Zero));

    private const double FlashMs = 340, SparksMs = 300, BurstMs = 380, HoldMs = 300, LiftMs = 220, ScoreDelayMs = 150;

    /// <summary>How long after its hit a landing raises <see cref="Finished"/>: the 0.3 s hold and the 220 ms lift.</summary>
    public static readonly TimeSpan FinishAfterHit = TimeSpan.FromMilliseconds(HoldMs + LiftMs);

    private static readonly Color RingColor = Color.FromRgb(0xCB, 0xC7, 0xBE);
    private static readonly Color RingPausedColor = Color.FromRgb(0x6C, 0x74, 0x76);
    private static readonly Color GreyZoneColor = Color.FromRgb(0xA3, 0xA9, 0xAA);
    private static readonly Color KeyBorderColor = Color.FromRgb(0xF2, 0xEE, 0xE6);
    private static readonly Color KeyBorderPausedColor = Color.FromRgb(0x8A, 0x91, 0x93);
    private static readonly Color KeyTextPausedColor = Color.FromRgb(0xB4, 0xB9, 0xBA);
    private static readonly Geometry GoodGeometry = SkillCheckMath.Frozen(SkillCheckMath.SectorPath(SkillCheckMath.GreatDegrees, SkillCheckMath.ZoneDegrees));
    private static readonly Geometry GreatGeometry = SkillCheckMath.Frozen(SkillCheckMath.SectorPath(0, SkillCheckMath.GreatDegrees));
    private static readonly Geometry FlashGreatGeometry = SkillCheckMath.Frozen(SkillCheckMath.SectorPath(-0.5, SkillCheckMath.GreatDegrees + 0.5, SkillCheckMath.InnerRadius - 1, SkillCheckMath.OuterRadius + 1));

    private readonly Grid _root = new();
    private readonly Rectangle _dim = new() { IsHitTestVisible = false, Margin = new Thickness(1) };
    private readonly Grid _dial = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly ScaleTransform _dialScale = new(1, 1);
    private readonly Canvas[] _zones = new Canvas[2];
    private readonly RotateTransform[] _zoneTurns = { new(0, 100, 100), new(0, 100, 100) };
    private readonly Canvas _flash = new() { Opacity = 0 };
    private readonly RotateTransform _flashTurn = new(0, 100, 100);
    private readonly Canvas _sparks = new() { Opacity = 0 };
    private readonly ScaleTransform _sparksScale = new(1, 1, 100, 100);
    private readonly RotateTransform _sparkTurn = new(0, 100, 100);
    private readonly Path _burst;
    private readonly ScaleTransform _burstScale = new(1, 1, 100, 100);
    private readonly Canvas _needle = new();
    private readonly RotateTransform _needleTurn = new(0, 100, 100);
    private readonly Path _needleGlow;
    private readonly Path _needleGrey;
    private readonly SolidColorBrush _ringBrush = new(RingColor);
    private readonly SolidColorBrush _greatBrush = new(Colors.White);
    private readonly SolidColorBrush _goodStroke = new(Colors.White);
    private readonly SolidColorBrush _keyBorder = new(KeyBorderColor);
    private readonly SolidColorBrush _keyText = new(Colors.White);
    private readonly Border _key;
    private readonly TextBlock _keyMods;
    private readonly TextBlock _keyName;
    private readonly MotionPolicyWatch _watch;
    private readonly AmbientLoop _spin;
    private readonly DispatcherTimer _stepTimer = new(DispatcherPriority.Normal);
    private readonly DispatcherTimer _hitTimer = new(DispatcherPriority.Send);
    private readonly DispatcherTimer _finishTimer = new(DispatcherPriority.Normal);
    private readonly DispatcherTimer _flashTimer = new(DispatcherPriority.Normal);
    private MotionMoment? _visibility;
    private MotionMoment? _zoneFade;
    private MotionMoment? _tint;
    private MotionMoment? _landing;
    private int _zoneShown;
    private int _rev = -1;
    private double _zoneStart;
    private bool _landingActive;
    private bool _hitPending;
    private bool _finishPending;
    private int _landingId;
    private bool _silent;

    /// <summary>Creates a hidden check.</summary>
    public SkillCheck()
    {
        Focusable = false;
        IsTabStop = false;
        IsHitTestVisible = false;

        // the dial: a 200-unit canvas in a Viewbox, centre (100, 100)
        var canvas = new Canvas { Width = 200, Height = 200, ClipToBounds = false };
        canvas.Children.Add(new Path { Data = new EllipseGeometry(new Point(100, 100), SkillCheckMath.RingRadius, SkillCheckMath.RingRadius), Stroke = _ringBrush, StrokeThickness = 1.8 });
        var zoneFill = CampfirePalette.Brush(0.9, Color.FromRgb(6, 10, 12));
        for (int i = 0; i < 2; i++)
        {
            var zone = new Canvas { Opacity = 0, RenderTransform = _zoneTurns[i] };
            zone.Children.Add(new Path { Data = GoodGeometry, Fill = zoneFill, Stroke = _goodStroke, StrokeThickness = 1.6 });
            zone.Children.Add(new Path { Data = GreatGeometry, Fill = _greatBrush });
            _zones[i] = zone;
            canvas.Children.Add(zone);
        }

        // the hit: the zone flashes white, three sparks fly off, a white ring leaves the dial (all rest invisible)
        _flash.RenderTransform = _flashTurn;
        _flash.Children.Add(new Path { Data = GoodGeometry, Fill = Brushes.White, Opacity = 0.5 });
        _flash.Children.Add(new Path { Data = FlashGreatGeometry, Fill = Brushes.White, Stroke = Brushes.White, StrokeThickness = 3, Effect = Frozen(new BlurEffect { Radius = 6 }) });
        canvas.Children.Add(_flash);
        var sparkRot = new Canvas { RenderTransform = _sparkTurn };
        _sparks.RenderTransform = _sparksScale;
        foreach (double a in new[] { SkillCheckMath.GreatDegrees / 2 - 7, SkillCheckMath.GreatDegrees / 2, SkillCheckMath.GreatDegrees / 2 + 7 })
        {
            Point p0 = SkillCheckMath.PointAt(71, a), p1 = SkillCheckMath.PointAt(78.5, a);
            _sparks.Children.Add(new Line
            {
                X1 = p0.X,
                Y1 = p0.Y,
                X2 = p1.X,
                Y2 = p1.Y,
                Stroke = CampfirePalette.Brush(Color.FromRgb(0xFF, 0xF6, 0xEA)),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }

        sparkRot.Children.Add(_sparks);
        canvas.Children.Add(sparkRot);
        _burst = new Path
        {
            Data = new EllipseGeometry(new Point(100, 100), SkillCheckMath.RingRadius, SkillCheckMath.RingRadius),
            Stroke = Brushes.White,
            StrokeThickness = 2.6,
            Opacity = 0,
            RenderTransform = _burstScale,
        };
        canvas.Children.Add(_burst);

        // the needle: a red spindle with a soft glow (grey when paused)
        var core = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(97.4, 0), EndPoint = new Point(102.6, 0) };
        core.GradientStops.Add(new GradientStop(Color.FromRgb(0xB4, 0x0F, 0x1E), 0));
        core.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x6A, 0x3E), 0.5));
        core.GradientStops.Add(new GradientStop(Color.FromRgb(0xB4, 0x0F, 0x1E), 1));
        core.Freeze();
        _needleGlow = new Path
        {
            Data = SkillCheckMath.Frozen(SkillCheckMath.NeedleGlowPath),
            Fill = CampfirePalette.Brush(Color.FromRgb(0xFF, 0x2B, 0x3D)),
            Opacity = 0.55,
            Effect = Frozen(new BlurEffect { Radius = 6 }),
        };
        _needleGrey = new Path { Data = SkillCheckMath.Frozen(SkillCheckMath.NeedlePath), Fill = CampfirePalette.Brush(Color.FromRgb(0x8E, 0x73, 0x77)), Opacity = 0 };
        _needle.Children.Add(_needleGlow);
        _needle.Children.Add(new Path { Data = SkillCheckMath.Frozen(SkillCheckMath.NeedlePath), Fill = core });
        _needle.Children.Add(_needleGrey);
        _needle.RenderTransform = _needleTurn;
        canvas.Children.Add(_needle);

        // DBD's centre key prompt: black key, white border, the configured hotkey (modifiers on their own line)
        _keyMods = new TextBlock
        {
            FontFamily = CampfireFonts.Body,
            FontWeight = FontWeights.SemiBold,
            Foreground = _keyText,
            Opacity = 0.86,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _keyName = new TextBlock
        {
            FontFamily = CampfireFonts.Display,
            FontWeight = FontWeights.ExtraBold,
            Foreground = _keyText,
            HorizontalAlignment = HorizontalAlignment.Center,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        var keyStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        keyStack.Children.Add(_keyMods);
        keyStack.Children.Add(_keyName);
        _key = new Border
        {
            Background = CampfirePalette.Brush(Color.FromRgb(4, 6, 7)),
            BorderBrush = _keyBorder,
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = keyStack,
        };

        _dial.Children.Add(new Viewbox { Stretch = Stretch.Fill, Child = canvas });
        _dial.Children.Add(_key);
        _dial.RenderTransform = _dialScale;
        _dial.RenderTransformOrigin = new Point(0.5, 0.5);
        _dial.Opacity = 0;
        _dim.Opacity = 0;
        Score = new ScoreEvent { Margin = new Thickness(0, 16, 18, 0) };
        _root.Children.Add(_dim);
        _root.Children.Add(_dial);
        _root.Children.Add(Score);
        Content = _root;
        AutomationProperties.SetName(this, "Skill check");

        _spin = new AmbientLoop(this, "SkillCheck.spin", BuildSpin);
        _stepTimer.Tick += (_, _) => Step();
        _hitTimer.Tick += (_, _) =>
        {
            _hitTimer.Stop();
            RaiseHit();
        };
        _finishTimer.Tick += (_, _) =>
        {
            _finishTimer.Stop();
            EndLanding();
        };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            _flash.Opacity = 0;
        };
        SizeChanged += (_, _) => UpdateLayoutMetrics();
        _watch = new MotionPolicyWatch(this, OnPolicyChanged);
        UpdateKey();
        ApplyVisibility();
    }

    /// <summary>Raised once per revolution while scanning (one read each), with the 0-based revolution index.</summary>
    public event EventHandler<int>? Revolution;

    /// <summary>The landing's hit (the needle stops in the white): play the Great sound now. Raised from a timer, so it
    /// fires even while the window is minimized. Exactly once per <see cref="LandGreat"/> (at once if the landing is cut
    /// short before it).</summary>
    public event EventHandler? Hit;

    /// <summary>The landing is over (the dial has lifted away): change the rest of the screen now. Exactly once per
    /// <see cref="LandGreat"/>, always after its <see cref="Hit"/> (at once if the landing is cut short).</summary>
    public event EventHandler? Finished;

    /// <summary>What the check shows.</summary>
    public SkillCheckState State
    {
        get => (SkillCheckState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>The hotkey in the centre key.</summary>
    public string KeyText
    {
        get => (string)GetValue(KeyTextProperty);
        set => SetValue(KeyTextProperty, value);
    }

    /// <summary>One revolution = one read.</summary>
    public TimeSpan RevolutionDuration
    {
        get => (TimeSpan)GetValue(RevolutionDurationProperty);
        set => SetValue(RevolutionDurationProperty, value);
    }

    /// <summary>Delay before the dial appears when a scan starts.</summary>
    public TimeSpan EnterDelay
    {
        get => (TimeSpan)GetValue(EnterDelayProperty);
        set => SetValue(EnterDelayProperty, value);
    }

    /// <summary>The score event (top right).</summary>
    public ScoreEvent Score { get; }

    /// <summary>The needle's angle now, degrees clockwise from 12 o'clock (0..360).</summary>
    public double NeedleAngle => ((_needleTurn.Angle % 360) + 360) % 360;

    /// <summary>The current check's great-block start, degrees.</summary>
    public double ZoneStart => _zoneStart;

    /// <summary>The current revolution (0-based; -1 before the first).</summary>
    public int CurrentRevolution => _rev;

    /// <summary>A landing is playing.</summary>
    public bool IsLanding => _landingActive;

    /// <summary>The spin's clocks are running.</summary>
    public bool IsSpinning => _spin.IsRunning;

    /// <summary>How visible the dial is drawn now, 0..1 (its animated opacity; 0 while it is hidden).</summary>
    public double DialOpacity => Visible();

    /// <summary>
    /// The hit delay a landing would have right now, without animating (0 when hidden, when the landing would not be
    /// seen — window hidden, minimized or inactive — with animations off, or under reduced motion).
    /// </summary>
    public TimeSpan ComputeHitDelay()
    {
        MotionPolicy policy = _watch.Policy;
        if (State == SkillCheckState.Hidden || _landingActive || !policy.CanPresentLanding || policy.ReducedMotion || !IsSpinMode())
        {
            return TimeSpan.Zero;
        }

        return SkillCheckMath.ComputeLanding(NeedleAngle, _zoneStart).HitDelay;
    }

    /// <summary>
    /// Map found: lands the needle in the great block and returns the hit delay (constant 0.3°/ms to 4° into the white
    /// block; 0 if it is already inside, under reduced motion, with animations off or when the landing would not be seen).
    /// <see cref="Hit"/> fires after that delay, <see cref="Finished"/> 520 ms later (the 0.3 s hold and the 220 ms
    /// lift); then the check is <see cref="SkillCheckState.Hidden"/>. With <paramref name="scoreDetail"/> the score event
    /// ("Great skill check" / detail) comes in 150 ms after the hit. When the check is hidden nothing is drawn: Hit and
    /// Finished are still raised (at once) and Zero is returned. A landing that is still playing is completed first (its
    /// owed Hit and Finished are raised at once).
    /// </summary>
    public TimeSpan LandGreat(string? scoreDetail = null)
    {
        if (CancelLanding())
        {
            CompleteCancelledLanding();
        }

        MotionPolicy policy = _watch.Policy;
        if (State == SkillCheckState.Hidden)
        {
            if (scoreDetail is not null)
            {
                Score.Show(scoreDetail);
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                Hit?.Invoke(this, EventArgs.Empty);
                Finished?.Invoke(this, EventArgs.Empty);
            }));
            return TimeSpan.Zero;
        }

        bool animate = policy.CanPresentLanding;
        bool still = !animate || policy.ReducedMotion || !IsSpinMode();
        double angle = NeedleAngle;
        _spin.Stop();
        _stepTimer.Stop();
        SkillCheckLanding landing = SkillCheckMath.ComputeLanding(angle, _zoneStart, still);
        double h = landing.HitDelay.TotalMilliseconds;
        if (landing.Fresh)
        {
            ShowZone(landing.ZoneStart, instant: still, after: true);
        }

        _zoneStart = landing.ZoneStart;
        _landingActive = true;
        _hitPending = true;
        _finishPending = true;
        _landingId++;
        var zoneTurn = new RotateTransform(Normalize(landing.ZoneStart), 100, 100);
        _flashTurn.Angle = zoneTurn.Angle;
        _sparkTurn.Angle = zoneTurn.Angle;

        // rest pose: stopped dead in the white, and (once it has lifted) the dial gone
        _needleTurn.Angle = landing.TargetAngle;
        _dial.Opacity = 0;
        _dim.Opacity = 0;
        _dialScale.ScaleX = _dialScale.ScaleY = 1;

        if (!policy.ShouldAnimate)
        {
            // nothing moves: the white block flashes for a moment (an opacity swap), the dial holds, then it is gone
            _dial.Opacity = 1;
            _dim.Opacity = 1;
            _flash.Opacity = 1;
            _flashTimer.Interval = TimeSpan.FromMilliseconds(100);
            _flashTimer.Start();
            ScheduleLandingTimers(0, scoreDetail);
            return TimeSpan.Zero;
        }

        double hold = h + HoldMs;
        var moment = new MotionMoment(this, "SkillCheck.land") { ExactTiming = true }; // (timed with the sound and the reveal)
        if (h > 0)
        {
            moment.Add(_needleTurn, RotateTransform.AngleProperty, Anim.Keys(0, new[] { new Kf(0, angle), new Kf(h, landing.TargetAngle) }));
        }

        moment.Add(_flash, OpacityProperty, Anim.Keys(0, new[] { new Kf(0, 0), new Kf(h, 0), new Kf(h + 1, 1), new Kf(h + FlashMs, 0, Ease.Out) }));
        if (!policy.ReducedMotion)
        {
            moment.Add(_sparks, OpacityProperty, Anim.Keys(0, new[] { new Kf(0, 0), new Kf(h, 0), new Kf(h + 1, 1), new Kf(h + SparksMs, 0, Ease.Out) }));
            moment.Add(_sparksScale, ScaleTransform.ScaleXProperty, Anim.Keys(0, new[] { new Kf(0, 1), new Kf(h, 1), new Kf(h + SparksMs, 1.17, Ease.Out) }));
            moment.Add(_sparksScale, ScaleTransform.ScaleYProperty, Anim.Keys(0, new[] { new Kf(0, 1), new Kf(h, 1), new Kf(h + SparksMs, 1.17, Ease.Out) }));
            moment.Add(_burst, OpacityProperty, Anim.Keys(0, new[] { new Kf(0, 0), new Kf(h, 0), new Kf(h + 1, 0.95), new Kf(h + BurstMs, 0, Ease.Out) }));
            moment.Add(_burstScale, ScaleTransform.ScaleXProperty, Anim.Keys(0, new[] { new Kf(0, 1), new Kf(h, 1), new Kf(h + BurstMs, 1.32, Ease.Out) }));
            moment.Add(_burstScale, ScaleTransform.ScaleYProperty, Anim.Keys(0, new[] { new Kf(0, 1), new Kf(h, 1), new Kf(h + BurstMs, 1.32, Ease.Out) }));
            moment.Add(_dialScale, ScaleTransform.ScaleXProperty, Anim.Keys(0, new[] { new Kf(0, 1), new Kf(hold, 1), new Kf(hold + LiftMs, 1.06, Ease.Out) }));
            moment.Add(_dialScale, ScaleTransform.ScaleYProperty, Anim.Keys(0, new[] { new Kf(0, 1), new Kf(hold, 1), new Kf(hold + LiftMs, 1.06, Ease.Out) }));
        }

        // the dial holds ~0.3 s like DBD's, then lifts away (and stays gone)
        moment.Add(_dial, OpacityProperty, Anim.Keys(0, new[] { new Kf(0, 1), new Kf(hold, 1), new Kf(hold + LiftMs, 0, Ease.Out) }));
        moment.Add(_dim, OpacityProperty, Anim.Keys(0, new[] { new Kf(0, 1), new Kf(hold, 1), new Kf(hold + LiftMs, 0, Ease.Out) }));
        _landing = moment;
        moment.Begin();
        ScheduleLandingTimers(h, scoreDetail);
        return landing.HitDelay;
    }

    /// <summary>
    /// Puts the scan <paramref name="elapsed"/> into its spin (frame captures and tests): the zone of that revolution, the
    /// needle where it would be. A running spin continues from there; a held one stays there (no clocks).
    /// </summary>
    public void SeekSpin(TimeSpan elapsed)
    {
        double period = RevolutionDuration.TotalMilliseconds;
        double el = Math.Max(0, elapsed.TotalMilliseconds);
        int rev = (int)(el / period);
        _spin.Stop();
        _spin.Offset = TimeSpan.FromMilliseconds(el);
        if (rev != _rev)
        {
            _rev = rev;
            ShowZone(SkillCheckMath.ZoneStart(rev), instant: true);
        }

        _needleTurn.Angle = IsSpinMode() ? el % period / period * 360 : SkillCheckMath.RestAngle(rev);
        UpdateMotion();
    }

    /// <summary>
    /// Ends a landing in progress at once: the dial is gone and the owed <see cref="Hit"/>, then <see cref="Finished"/>,
    /// are raised now (frame captures step past the lift with it). Nothing happens when no landing plays.
    /// </summary>
    public void CompleteLanding()
    {
        if (!_landingActive)
        {
            return;
        }

        _hitTimer.Stop();
        _finishTimer.Stop();
        EndLanding();
    }

    /// <summary>Freezes the running landing (or the enter / exit) at <paramref name="time"/> from its start (frame captures).</summary>
    public void FreezeAt(TimeSpan time)
    {
        _landing?.FreezeAt(time);
        _visibility?.FreezeAt(time);
        _hitTimer.Stop();
        _finishTimer.Stop();
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static double Normalize(double degrees) => ((degrees % 360) + 360) % 360;

    private bool IsSpinMode()
    {
        MotionPolicy policy = _watch.Policy;
        return policy.AnimationsEnabled && !policy.ReducedMotion;
    }

    private void ScheduleLandingTimers(double hitMs, string? scoreDetail)
    {
        if (scoreDetail is not null)
        {
            Score.Show(scoreDetail, TimeSpan.FromMilliseconds(hitMs + ScoreDelayMs));
        }

        if (hitMs <= 0)
        {
            int id = _landingId;
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (id == _landingId)
                {
                    RaiseHit();
                }
            }));
        }
        else
        {
            _hitTimer.Interval = TimeSpan.FromMilliseconds(hitMs);
            _hitTimer.Start();
        }

        _finishTimer.Interval = TimeSpan.FromMilliseconds(hitMs + HoldMs + LiftMs);
        _finishTimer.Start();
    }

    private void EndLanding()
    {
        if (!_landingActive)
        {
            return;
        }

        _landingActive = false;
        _landing?.Stop();
        _landing = null;
        _silent = true;
        try
        {
            // (a current value: a binding on State stays in place)
            SetCurrentValue(StateProperty, SkillCheckState.Hidden);
        }
        finally
        {
            _silent = false;
        }

        _rev = -1;
        ApplyVisibility();
        RaiseHit();
        RaiseFinished();
    }

    /// <summary>Stops a playing landing's clocks and timers; true when it still owes its Hit or Finished.</summary>
    private bool CancelLanding()
    {
        _hitTimer.Stop();
        _finishTimer.Stop();
        _flashTimer.Stop();
        _flash.Opacity = 0;
        if (_landing is not null)
        {
            _landing.Stop();
            _landing = null;
        }

        _landingActive = false;
        return _hitPending || _finishPending;
    }

    /// <summary>A landing cut short still tells its outcome: the owed Hit (the sound), then Finished (the reveal).</summary>
    private void CompleteCancelledLanding()
    {
        RaiseHit();
        RaiseFinished();
    }

    private void RaiseHit()
    {
        if (!_hitPending)
        {
            return;
        }

        _hitPending = false;
        Hit?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseFinished()
    {
        if (!_finishPending)
        {
            return;
        }

        _finishPending = false;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void OnStateChanged(SkillCheckState from, SkillCheckState to)
    {
        if (_silent)
        {
            return;
        }

        // what the dial shows now (it may be mid-landing, mid-enter or mid-exit): the change carries on from there
        double vis = Visible();
        bool completeLanding = false;
        if (_landingActive)
        {
            if (to == SkillCheckState.Hidden)
            {
                // the landing owns the dial until it has lifted away; it ends hidden anyway
                return;
            }

            // a new scan (or a pause) cuts the landing short: the new state shows now, and the landing's owed Hit and
            // Finished are raised once it is applied (so a handler may change the state again safely)
            completeLanding = CancelLanding();
            from = SkillCheckState.Hidden;
        }

        MotionPolicy policy = _watch.Policy;
        switch (to)
        {
            case SkillCheckState.Scanning:
                if (from == SkillCheckState.Paused)
                {
                    Tint(paused: false, animate: true);
                }
                else
                {
                    Fresh(paused: false);
                    Enter(from == SkillCheckState.Hidden, vis);
                }

                break;
            case SkillCheckState.Paused:
                if (from == SkillCheckState.Scanning)
                {
                    // the needle stops where it is
                    _spin.Halt();
                    _stepTimer.Stop();
                    Tint(paused: true, animate: true);
                }
                else
                {
                    Fresh(paused: true);
                    Enter(from == SkillCheckState.Hidden, vis);
                }

                break;
            default:
                _spin.Stop();
                _stepTimer.Stop();
                Exit(policy.ShouldAnimate, vis);
                _rev = -1;
                break;
        }

        UpdateMotion();
        if (completeLanding)
        {
            CompleteCancelledLanding();
        }
    }

    private void Fresh(bool paused)
    {
        _spin.Stop();
        _stepTimer.Stop();
        _rev = 0;
        ShowZone(SkillCheckMath.ZoneStart(0), instant: true);
        if (paused)
        {
            // frozen just past the zone: the last read missed
            _needleTurn.Angle = SkillCheckMath.PausedAngle(_zoneStart);
        }
        else
        {
            // DBD: the needle always starts at 12 o'clock
            _needleTurn.Angle = IsSpinMode() ? 0 : SkillCheckMath.RestAngle(0);
        }

        Tint(paused, animate: false);
    }

    /// <summary>
    /// The dial appears (200 ms after <see cref="EnterDelay"/>: a fade in from .92 scale). From wherever it is: a dial
    /// still fading out (a scan stopped and started again at once) comes back from that opacity, scale .92 + .08 × it
    /// (the prototype's skEnter(delay, vis)).
    /// </summary>
    private void Enter(bool fromHidden, double vis)
    {
        _visibility?.Stop();
        _visibility = null;
        ApplyVisibility();
        MotionPolicy policy = _watch.Policy;
        if (!fromHidden || !policy.ShouldAnimate || !IsLoaded || vis > 0.98)
        {
            return;
        }

        double delay = Math.Max(0, EnterDelay.TotalMilliseconds);
        double scale = 0.92 + 0.08 * vis;
        var moment = new MotionMoment(this, "SkillCheck.enter") { ExactTiming = true };
        moment.Add(_dial, OpacityProperty, Anim.FromTo(vis, 1, 200, Ease.Out, delay));
        moment.Add(_dim, OpacityProperty, Anim.FromTo(vis, 1, 200, Ease.Out, delay));
        if (!policy.ReducedMotion)
        {
            moment.Add(_dialScale, ScaleTransform.ScaleXProperty, Anim.FromTo(scale, 1, 200, Ease.Out, delay));
            moment.Add(_dialScale, ScaleTransform.ScaleYProperty, Anim.FromTo(scale, 1, 200, Ease.Out, delay));
        }

        _visibility = moment;
        moment.Begin();
    }

    /// <summary>
    /// Leaving the scan any other way than a hit: a fast fade (140 ms) from wherever the dial is: one still coming in
    /// fades out from that opacity (the prototype's skExit(vis)), and one that never showed simply goes.
    /// </summary>
    private void Exit(bool animate, double was)
    {
        _visibility?.Stop();
        _visibility = null;
        ApplyVisibility();
        MotionPolicy policy = _watch.Policy;
        if (!animate || was < 0.02 || !IsLoaded)
        {
            return;
        }

        // leaving the scan any other way than a hit: a fast fade, no sound
        _dial.Visibility = Visibility.Visible;
        _dim.Visibility = Visibility.Visible;
        var moment = new MotionMoment(this, "SkillCheck.exit") { ExactTiming = true };
        moment.Add(_dial, OpacityProperty, Anim.FromTo(was, 0, 140));
        moment.Add(_dim, OpacityProperty, Anim.FromTo(was, 0, 140));
        if (!policy.ReducedMotion)
        {
            moment.Add(_dialScale, ScaleTransform.ScaleXProperty, Anim.FromTo(1, 0.98, 140));
            moment.Add(_dialScale, ScaleTransform.ScaleYProperty, Anim.FromTo(1, 0.98, 140));
        }

        moment.Ended += (_, _) => ApplyVisibility();
        _visibility = moment;
        moment.Begin();
    }

    /// <summary>How visible the dial is drawn now (its animated opacity; 0 while it is collapsed).</summary>
    private double Visible() => _dial.Visibility == Visibility.Visible ? Math.Clamp(_dial.Opacity, 0, 1) : 0;

    private void ApplyVisibility()
    {
        bool shown = State != SkillCheckState.Hidden || _landingActive;
        _dial.Opacity = shown ? 1 : 0;
        _dim.Opacity = shown ? 1 : 0;
        _dial.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        _dim.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the check for a new revolution: a quick crossfade between the two zone layers.</summary>
    private void ShowZone(double start, bool instant, bool after = false)
    {
        _zoneStart = start;
        Canvas current = _zones[_zoneShown];
        Canvas next = _zones[1 - _zoneShown];
        _zoneTurns[1 - _zoneShown].Angle = Normalize(start);
        _zoneShown = 1 - _zoneShown;
        _zoneFade?.Stop();
        _zoneFade = null;
        double currentWas = current.Opacity;
        current.Opacity = 0;
        next.Opacity = 1;
        MotionPolicy policy = _watch.Policy;
        if (instant || !policy.ShouldAnimate)
        {
            return;
        }

        // old out 0-110 ms, new in 0-150 ms (or, for a fresh check at "found", new in 100-220 ms)
        var fade = new MotionMoment(this, "SkillCheck.zone") { ExactTiming = true };
        fade.Add(current, OpacityProperty, Anim.FromTo(currentWas, 0, 110));
        fade.Add(next, OpacityProperty, after ? Anim.FromTo(0, 1, 120, Ease.Out, 100) : Anim.FromTo(0, 1, 150));
        _zoneFade = fade;
        fade.Begin();
    }

    private void Tint(bool paused, bool animate)
    {
        _tint?.Stop();
        _tint = null;
        Color ring = paused ? RingPausedColor : RingColor;
        Color zone = paused ? GreyZoneColor : Colors.White;
        Color keyBorder = paused ? KeyBorderPausedColor : KeyBorderColor;
        Color keyText = paused ? KeyTextPausedColor : Colors.White;
        double glow = paused ? 0 : 0.55;
        double grey = paused ? 1 : 0;
        var from = (_ringBrush.Color, _greatBrush.Color, _goodStroke.Color, _keyBorder.Color, _keyText.Color, _needleGlow.Opacity, _needleGrey.Opacity);
        _ringBrush.Color = ring;
        _greatBrush.Color = zone;
        _goodStroke.Color = zone;
        _keyBorder.Color = keyBorder;
        _keyText.Color = keyText;
        _needleGlow.Opacity = glow;
        _needleGrey.Opacity = grey;
        if (!animate || !_watch.Policy.ShouldAnimate || !IsLoaded)
        {
            return;
        }

        var tint = new MotionMoment(this, "SkillCheck.tint") { ExactTiming = true };
        tint.Add(_ringBrush, SolidColorBrush.ColorProperty, Anim.Colors(0, new ColorKf(0, from.Item1), new ColorKf(220, ring, Ease.Css)));
        tint.Add(_greatBrush, SolidColorBrush.ColorProperty, Anim.Colors(0, new ColorKf(0, from.Item2), new ColorKf(220, zone, Ease.Css)));
        tint.Add(_goodStroke, SolidColorBrush.ColorProperty, Anim.Colors(0, new ColorKf(0, from.Item3), new ColorKf(220, zone, Ease.Css)));
        tint.Add(_keyBorder, SolidColorBrush.ColorProperty, Anim.Colors(0, new ColorKf(0, from.Item4), new ColorKf(220, keyBorder, Ease.Css)));
        tint.Add(_keyText, SolidColorBrush.ColorProperty, Anim.Colors(0, new ColorKf(0, from.Item5), new ColorKf(220, keyText, Ease.Css)));
        tint.Add(_needleGlow, OpacityProperty, Anim.FromTo(from.Item6, glow, 220, Ease.Css));
        tint.Add(_needleGrey, OpacityProperty, Anim.FromTo(from.Item7, grey, 220, Ease.Css));
        _tint = tint;
        tint.Begin();
    }

    private void OnPolicyChanged()
    {
        MotionPolicy policy = _watch.Policy;
        if (!policy.ShouldAnimate)
        {
            _visibility?.Stop();
            _visibility = null;
            _zoneFade?.Stop();
            _zoneFade = null;
            ApplyVisibility();
        }
        else if (!policy.ShouldRunAmbient)
        {
            // the zone's crossfade belongs to the spin (a new check every revolution): it stops with it
            _zoneFade?.Stop();
            _zoneFade = null;
        }

        UpdateMotion();
    }

    /// <summary>Runs, holds or steps the needle according to the state and the policy.</summary>
    private void UpdateMotion()
    {
        MotionPolicy policy = _watch.Policy;
        bool scanning = State == SkillCheckState.Scanning && !_landingActive && IsLoaded;
        if (!scanning)
        {
            _spin.Halt();
            _stepTimer.Stop();
            return;
        }

        if (IsSpinMode())
        {
            _stepTimer.Stop();
            if (policy.ShouldRunAmbient)
            {
                _spin.Run();
            }
            else
            {
                _spin.Halt();
            }

            return;
        }

        // still mode (reduced motion / animations off): the needle steps once per read, only while it can be seen
        _spin.Stop();
        _needleTurn.Angle = SkillCheckMath.RestAngle(Math.Max(0, _rev));
        if (policy.WindowVisible && policy.WindowActive)
        {
            _stepTimer.Interval = RevolutionDuration;
            if (!_stepTimer.IsEnabled)
            {
                _stepTimer.Start();
            }
        }
        else
        {
            _stepTimer.Stop();
        }
    }

    private void Step()
    {
        if (State != SkillCheckState.Scanning || _landingActive)
        {
            _stepTimer.Stop();
            return;
        }

        int rev = Math.Max(0, _rev) + 1;
        _rev = rev;
        ShowZone(SkillCheckMath.ZoneStart(rev), instant: true);
        _needleTurn.Angle = SkillCheckMath.RestAngle(rev);
        Revolution?.Invoke(this, rev);
    }

    private IEnumerable<(DependencyObject, DependencyProperty, AnimationTimeline)> BuildSpin()
    {
        double period = RevolutionDuration.TotalMilliseconds;
        var anim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(period))) { RepeatBehavior = RepeatBehavior.Forever };
        anim.CurrentTimeInvalidated += OnSpinTime;
        yield return (_needleTurn, RotateTransform.AngleProperty, anim);
    }

    private void OnSpinTime(object? sender, EventArgs e)
    {
        if (sender is not Clock { CurrentIteration: int iteration } || iteration < 1)
        {
            return;
        }

        int rev = iteration - 1;
        if (rev > _rev)
        {
            int from = _rev;
            _rev = rev;
            ShowZone(SkillCheckMath.ZoneStart(rev), instant: false);
            for (int r = Math.Max(0, from); r < rev; r++)
            {
                Revolution?.Invoke(this, r + 1);
            }
        }
    }

    private void RestartSpinClock()
    {
        if (_spin.IsRunning)
        {
            TimeSpan at = _spin.Offset;
            _spin.Halt();
            _spin.Stop();
            _spin.Offset = at;
            UpdateMotion();
        }
    }

    private void UpdateKey()
    {
        (string? mods, string key) = SkillCheckMath.SplitHotkey(KeyText);
        _keyMods.Text = mods ?? string.Empty;
        _keyMods.Visibility = mods is null ? Visibility.Collapsed : Visibility.Visible;
        _keyName.Text = key;
        UpdateLayoutMetrics();
    }

    private void UpdateLayoutMetrics()
    {
        double h = ActualHeight;
        double w = ActualWidth;
        if (h <= 0 || w <= 0)
        {
            return;
        }

        // a short plate (small windows): the caption takes more of it, so the check is smaller and sits higher
        bool small = h <= 340;
        double skd = (small ? 0.40 : 0.46) * h;
        double cy = small ? 0.40 * h : 0.46 * h;
        double d = skd * 1.5625;
        _dial.Width = d;
        _dial.Height = d;
        _dial.Margin = new Thickness(w / 2 - d / 2, cy - d / 2, 0, 0);

        var dim = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(w / 2 - 1, cy - 1),
            GradientOrigin = new Point(w / 2 - 1, cy - 1),
            RadiusX = skd * 1.1,
            RadiusY = skd * 1.1,
        };
        dim.GradientStops.Add(new GradientStop(CampfirePalette.Argb(0.72, Color.FromRgb(5, 9, 11)), 0));
        dim.GradientStops.Add(new GradientStop(CampfirePalette.Argb(0.55, Color.FromRgb(5, 9, 11)), 0.46 / 1.1));
        dim.GradientStops.Add(new GradientStop(CampfirePalette.Argb(0, Color.FromRgb(5, 9, 11)), 1));
        dim.Freeze();
        _dim.Fill = dim;

        bool stacked = _keyMods.Visibility == Visibility.Visible;
        _key.MinWidth = skd * 0.36;
        _key.MinHeight = skd * 0.2;
        _key.Padding = new Thickness(skd * 0.06, skd * 0.022, skd * 0.06, skd * 0.022);
        _key.CornerRadius = new CornerRadius(skd * 0.03);
        _keyName.FontSize = stacked ? Math.Max(14, skd * 0.12) : skd * 0.13;
        _keyName.LineHeight = _keyName.FontSize;
        _keyMods.FontSize = Math.Max(11, skd * 0.068);
        _keyMods.Margin = new Thickness(0, 0, 0, skd * 0.012);
    }
}
