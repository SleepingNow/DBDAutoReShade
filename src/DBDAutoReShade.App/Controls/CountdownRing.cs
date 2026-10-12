using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>How a <see cref="CountdownRing"/> run ended.</summary>
public sealed class CountdownEndedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public CountdownEndedEventArgs(bool cancelled) => Cancelled = cancelled;

    /// <summary>True when <see cref="CountdownRing.Cancel"/> ended it; false when the time ran out.</summary>
    public bool Cancelled { get; }
}

/// <summary>How a <see cref="CountdownRing"/> draws its run.</summary>
public enum CountdownMode
{
    /// <summary>No needle; the ring steps once a second (reduced motion, animations off, window hidden).</summary>
    Still,

    /// <summary>The needle and ring stand still and step with the 250 ms ticker (no animation clock): the window is
    /// inactive (the user is in the game during a Test) or the ring is not in the visual tree.</summary>
    Held,

    /// <summary>The ring drains and the needle sweeps smoothly (an animation clock; ambient motion).</summary>
    Drain,
}

/// <summary>
/// The Presets "Test" countdown: the 15 s focus wait drawn as a skill-check ring that drains while the red spindle
/// needle sweeps once (linear), with the seconds left in the centre (52 x 52, Big Shoulders 800 / 20).
/// <see cref="Start()"/> runs it; <see cref="Cancel"/> stops the needle where it is and fades the ring (150 ms) before
/// <see cref="Ended"/>; running out fades it the same way. The run is timed by a stopwatch, so it keeps counting (and
/// ends on time) while the ring is out of the visual tree (another tab) and resumes its drawing when it comes back.
/// The smooth drain is ambient motion: while the <see cref="MotionPolicy"/> forbids ambient motion (the window is not
/// the active window) the needle steps with the ticker instead, with no animation clock. With reduced motion or
/// animations off there is no needle and the ring steps once a second.
/// </summary>
public sealed class CountdownRing : UserControl
{
    /// <summary>The countdown length (default 15 s).</summary>
    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(TimeSpan), typeof(CountdownRing), new PropertyMetadata(TimeSpan.FromSeconds(15)));

    private static readonly DependencyPropertyKey SecondsLeftKey = DependencyProperty.RegisterReadOnly(
        nameof(SecondsLeft), typeof(int), typeof(CountdownRing), new PropertyMetadata(15));

    /// <summary>Whole seconds left (rounded up), updated once a second.</summary>
    public static readonly DependencyProperty SecondsLeftProperty = SecondsLeftKey.DependencyProperty;

    private const double Radius = 22;
    private static readonly double DashUnits = 2 * Math.PI * Radius / 3; // circumference in stroke-thickness units

    private readonly Grid _ring = new() { Width = 52, Height = 52 };
    private readonly Path _left;
    private readonly Canvas _needle = new();
    private readonly RotateTransform _needleTurn = new(0, 26, 26);
    private readonly TextBlock _number;
    private readonly MotionPolicyWatch _watch;
    private readonly DispatcherTimer _ticker = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _clock = new();
    private TimeSpan _startOffset;
    private MotionMoment? _drain;
    private MotionMoment? _fade;
    private bool _running;
    private CountdownMode _mode;

    /// <summary>Creates an idle ring.</summary>
    public CountdownRing()
    {
        Focusable = false;
        IsTabStop = false;
        var canvas = new Canvas { Width = 52, Height = 52, ClipToBounds = false };
        canvas.Children.Add(new Path
        {
            Data = new EllipseGeometry(new Point(26, 26), Radius, Radius),
            Stroke = CampfirePalette.Brush(0.16, CampfirePalette.BoneColor),
            StrokeThickness = 3,
        });
        _left = new Path
        {
            Data = SkillCheckMath.Frozen("M26,4 A22,22 0 1 1 26,48 A22,22 0 1 1 26,4"),
            Stroke = CampfirePalette.Bone,
            StrokeThickness = 3,
            StrokeDashArray = new DoubleCollection { DashUnits, DashUnits },
            StrokeDashOffset = 0,
        };
        canvas.Children.Add(_left);

        // the Status dial's spindle needle and gradient, scaled 22 / 64 about the centre
        var core = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(25.11, 0), EndPoint = new Point(26.89, 0) };
        core.GradientStops.Add(new GradientStop(Color.FromRgb(0xB4, 0x0F, 0x1E), 0));
        core.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x6A, 0x3E), 0.5));
        core.GradientStops.Add(new GradientStop(Color.FromRgb(0xB4, 0x0F, 0x1E), 1));
        core.Freeze();
        var glowEffect = new BlurEffect { Radius = 3.6 };
        glowEffect.Freeze();
        _needle.Children.Add(new Path
        {
            Data = SkillCheckMath.Frozen("M26,12.77 Q24.01,4.34 26,-5.97 Q27.99,4.34 26,12.77 Z"),
            Fill = CampfirePalette.Brush(Color.FromRgb(0xFF, 0x2B, 0x3D)),
            Opacity = 0.55,
            Effect = glowEffect,
        });
        _needle.Children.Add(new Path { Data = SkillCheckMath.Frozen("M26,12.25 Q24.42,4.34 26,-5.28 Q27.58,4.34 26,12.25 Z"), Fill = core });
        _needle.RenderTransform = _needleTurn;
        canvas.Children.Add(_needle);
        _number = new TextBlock
        {
            FontFamily = CampfireFonts.Display,
            FontWeight = FontWeights.ExtraBold,
            FontSize = 20,
            Foreground = CampfirePalette.Bone,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "15",
        };
        _ring.Children.Add(canvas);
        _ring.Children.Add(_number);
        Content = _ring;
        _ticker.Tick += (_, _) => OnTick();
        _watch = new MotionPolicyWatch(this, OnPolicyChanged);

        // (the ticker keeps running while the ring is out of the tree: the run is bounded by Duration)
        Loaded += (_, _) => OnLoaded();
    }

    /// <summary>Raised when the run ends (time ran out, or cancelled), after the ring has faded.</summary>
    public event EventHandler<CountdownEndedEventArgs>? Ended;

    /// <summary>Raised when <see cref="SecondsLeft"/> changes.</summary>
    public event EventHandler? SecondElapsed;

    /// <summary>The countdown length.</summary>
    public TimeSpan Duration
    {
        get => (TimeSpan)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <summary>Whole seconds left.</summary>
    public int SecondsLeft => (int)GetValue(SecondsLeftProperty);

    /// <summary>The countdown is running.</summary>
    public bool IsRunning => _running;

    /// <summary>Time elapsed in the current run.</summary>
    public TimeSpan Elapsed => _running ? _startOffset + _clock.Elapsed : TimeSpan.Zero;

    /// <summary>How the current run is drawn.</summary>
    public CountdownMode Mode => _mode;

    /// <summary>The needle's angle now, degrees clockwise from 12 o'clock.</summary>
    public double NeedleAngle => _needleTurn.Angle;

    /// <summary>Starts a full countdown.</summary>
    public void Start() => Start(TimeSpan.Zero);

    /// <summary>Starts (or resumes, after a re-render) a countdown that is already <paramref name="elapsed"/> in.</summary>
    public void Start(TimeSpan elapsed)
    {
        StopAll();
        _startOffset = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        _clock.Restart();
        _running = true;
        _ring.Opacity = 1;
        _mode = ChooseMode();
        _needle.Visibility = _mode == CountdownMode.Still ? Visibility.Collapsed : Visibility.Visible;
        double total = Duration.TotalMilliseconds;
        double done = Math.Min(total, _startOffset.TotalMilliseconds);
        if (_mode == CountdownMode.Drain)
        {
            // the rest pose is the drained ring; the moment runs from "now" to it, linear
            _left.StrokeDashOffset = -DashUnits;
            _needleTurn.Angle = 360;
            var drain = new MotionMoment(this, "CountdownRing.drain") { ExactTiming = true }; // (the real 15 s wait)
            double remaining = Math.Max(1, total - done);
            double from = total > 0 ? done / total : 1;
            drain.Add(_left, Shape.StrokeDashOffsetProperty, Anim.Keys(0, new[] { new Kf(0, -from * DashUnits), new Kf(remaining, -DashUnits) }));
            drain.Add(_needleTurn, RotateTransform.AngleProperty, Anim.Keys(0, new[] { new Kf(0, from * 360), new Kf(remaining, 360) }));
            _drain = drain;
            drain.Begin();
        }

        UpdateStep();
        _ticker.Start();
    }

    /// <summary>Cancels: the needle stops where it is and the ring fades (150 ms), then <see cref="Ended"/> (cancelled).</summary>
    public void Cancel()
    {
        if (!_running)
        {
            return;
        }

        End(cancelled: true);
    }

    /// <summary>Freezes the run at <paramref name="time"/> from its start (frame captures): the drain's clock is
    /// paused there, or (held / still) the ring and needle are drawn there.</summary>
    public void FreezeAt(TimeSpan time)
    {
        _ticker.Stop();
        if (_drain is not null)
        {
            _drain.FreezeAt(time);
        }
        else
        {
            DrawPose(Math.Min(Duration.TotalMilliseconds, Math.Max(0, (_startOffset + time).TotalMilliseconds)));
        }

        int left = (int)Math.Ceiling((Duration - time).TotalSeconds);
        SetValue(SecondsLeftKey, Math.Max(0, left));
        _number.Text = Math.Max(0, left).ToString(CultureInfo.InvariantCulture);
    }

    private CountdownMode ChooseMode()
    {
        MotionPolicy policy = _watch.Policy;
        if (!policy.AnimationsEnabled || !policy.WindowVisible || policy.ReducedMotion)
        {
            return CountdownMode.Still;
        }

        // shown but behind the game (inactive): the needle steps, without a clock
        return policy.ShouldRunAmbient && IsLoaded ? CountdownMode.Drain : CountdownMode.Held;
    }

    private void OnLoaded()
    {
        if (!_running)
        {
            return;
        }

        // back in the tree: catch up at once (a run that expired meanwhile ends now), and make sure it ticks
        UpdateStep();
        if (Elapsed >= Duration)
        {
            End(cancelled: false);
            return;
        }

        if (!_ticker.IsEnabled)
        {
            _ticker.Start();
        }
    }

    private void OnTick()
    {
        if (!_running)
        {
            _ticker.Stop();
            return;
        }

        UpdateStep();
        if (Elapsed >= Duration)
        {
            End(cancelled: false);
        }
    }

    private void UpdateStep()
    {
        double total = Duration.TotalMilliseconds;
        double elapsed = Math.Min(total, Elapsed.TotalMilliseconds);
        int left = (int)Math.Ceiling((total - elapsed) / 1000);
        if (left != SecondsLeft || _number.Text != left.ToString(CultureInfo.InvariantCulture))
        {
            SetValue(SecondsLeftKey, left);
            _number.Text = left.ToString(CultureInfo.InvariantCulture);
            SecondElapsed?.Invoke(this, EventArgs.Empty);
        }

        DrawPose(elapsed);
    }

    /// <summary>Draws the ring (and, held, the needle) <paramref name="elapsedMs"/> into the run; a drain draws itself.</summary>
    private void DrawPose(double elapsedMs)
    {
        double total = Duration.TotalMilliseconds;
        if (_mode == CountdownMode.Still)
        {
            // no needle: the ring steps once a second
            int left = (int)Math.Ceiling((total - elapsedMs) / 1000);
            double whole = Duration.TotalSeconds - left;
            _left.StrokeDashOffset = -Math.Min(1, whole / Duration.TotalSeconds) * DashUnits;
        }
        else if (_mode == CountdownMode.Held)
        {
            // no clock: the needle and the ring step with the ticker
            double done = total > 0 ? elapsedMs / total : 1;
            _left.StrokeDashOffset = -done * DashUnits;
            _needleTurn.Angle = done * 360;
        }
    }

    private void End(bool cancelled)
    {
        _running = false;
        _ticker.Stop();
        _clock.Stop();
        if (_drain is { IsActive: true })
        {
            // the needle stops where it is
            double offset = _left.StrokeDashOffset;
            double angle = _needleTurn.Angle;
            _drain.Stop();
            _left.StrokeDashOffset = offset;
            _needleTurn.Angle = angle;
        }

        _drain = null;
        MotionPolicy policy = _watch.Policy;
        _ring.Opacity = 0;
        if (!policy.ShouldAnimate || !IsLoaded)
        {
            // nothing to see (or nothing may move): it is over at once
            Ended?.Invoke(this, new CountdownEndedEventArgs(cancelled));
            return;
        }

        var fade = new MotionMoment(this, "CountdownRing.out");
        fade.Add(_ring, OpacityProperty, Anim.FromTo(1, 0, 150));
        fade.Ended += (_, _) =>
        {
            // (also when something stops the fade early, e.g. animations turned off; not when a new run replaced it)
            if (ReferenceEquals(_fade, fade))
            {
                _fade = null;
                Ended?.Invoke(this, new CountdownEndedEventArgs(cancelled));
            }
        };
        _fade = fade;
        fade.Begin();
    }

    private void StopAll()
    {
        _ticker.Stop();
        _drain?.Stop();
        _drain = null;
        MotionMoment? fade = _fade;
        _fade = null;
        fade?.Stop();
    }

    private void OnPolicyChanged()
    {
        if (!_running)
        {
            return;
        }

        // e.g. the window deactivates (the user is in the game during a Test): the drain's clock stops and the needle
        // steps in place; back in front it sweeps again from where the run is now
        if (ChooseMode() != _mode)
        {
            Start(Elapsed);
        }
    }
}
