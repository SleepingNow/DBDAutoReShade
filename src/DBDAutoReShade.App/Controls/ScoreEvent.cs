using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>
/// The DBD-style score event, top right of the map plate (where DBD shows "GREAT SKILL CHECK"): "Great skill check"
/// in white Big Shoulders 800 with an ember detail line ("Map found: Coal Tower") and a round emblem whose red arc
/// fills a quarter. <see cref="Show"/> slides it in (250 ms), holds 1.8 s and fades (200 ms). Rests invisible.
/// </summary>
public sealed class ScoreEvent : UserControl
{
    /// <summary>The headline (sentence case; default "Great skill check").</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ScoreEvent), new PropertyMetadata("Great skill check", (d, e) => ((ScoreEvent)d)._title.Text = (string)e.NewValue));

    /// <summary>In, hold and out, milliseconds.</summary>
    public const double InMs = 250, HoldMs = 1800, OutMs = 200;

    private const double ArcUnits = 2 * Math.PI * 17 / 2.6 * 1.0; // the arc's dash unit: 100 % of the circle, in stroke widths
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly Grid _root;
    private readonly Path _arc;
    private readonly TranslateTransform _slide = new();
    private readonly MotionPolicyWatch _watch;
    private readonly DispatcherTimer _hide = new(DispatcherPriority.Normal);
    private MotionMoment? _moment;

    /// <summary>Creates a hidden score event.</summary>
    public ScoreEvent()
    {
        Focusable = false;
        IsTabStop = false;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        _title = new TextBlock
        {
            Text = "Great skill check",
            FontFamily = CampfireFonts.Display,
            FontWeight = FontWeights.ExtraBold,
            FontSize = 26,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 26,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _detail = new TextBlock
        {
            Margin = new Thickness(0, 5, 0, 0),
            FontFamily = CampfireFonts.Body,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = CampfirePalette.Ember,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_detail);

        _arc = new Path
        {
            Data = SkillCheckMath.Frozen("M20,3 A17,17 0 1 1 19.99,3"),
            Stroke = CampfirePalette.Needle,
            StrokeThickness = 2.6,
            StrokeDashArray = new DoubleCollection { ArcUnits, ArcUnits },
            StrokeDashOffset = ArcUnits * 0.76,
        };
        var emblem = new Canvas
        {
            Width = 40,
            Height = 40,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Path
                {
                    Data = new EllipseGeometry(new Point(20, 20), 17, 17),
                    Fill = CampfirePalette.Brush(0.6, Color.FromRgb(6, 10, 12)),
                    Stroke = CampfirePalette.Brush(0.42, CampfirePalette.BoneColor),
                    StrokeThickness = 2.4,
                },
                _arc,
                new Path
                {
                    Data = SkillCheckMath.Frozen("M12,14.5 L17,12.5 L23,14.5 L28,12.5 V25.5 L23,27.5 L17,25.5 L12,27.5 Z M17,12.5 V25.5 M23,14.5 V27.5"),
                    Stroke = CampfirePalette.Brush(Color.FromRgb(0xF2, 0xEE, 0xE6)),
                    StrokeThickness = 1.5,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                },
            },
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(text);
        row.Children.Add(emblem);

        // the dark halo behind it (so it reads over the fog and the map)
        var halo = new Border
        {
            Margin = new Thickness(-56, -24, -22, -24),
            Background = CampfirePalette.Radial(0.5, 0.5, 0.5, 0.5,
                (0, CampfirePalette.Argb(0.8, Color.FromRgb(6, 10, 12))),
                (0.58, CampfirePalette.Argb(0.5, Color.FromRgb(6, 10, 12))),
                (1, CampfirePalette.Argb(0, Color.FromRgb(6, 10, 12)))),
        };
        _root = new Grid { Opacity = 0, RenderTransform = _slide };
        _root.Children.Add(halo);
        _root.Children.Add(row);
        Content = _root;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            _root.Opacity = 0;
        };
        _watch = new MotionPolicyWatch(this, () => { });
    }

    /// <summary>The headline.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The detail line currently shown (e.g. "Map found: Coal Tower").</summary>
    public string Detail => _detail.Text;

    /// <summary>The event is on screen (shown and not yet faded).</summary>
    public bool IsShowing => _moment is { IsActive: true } || _hide.IsEnabled;

    /// <summary>
    /// Shows the event with <paramref name="detail"/> after <paramref name="delay"/>: in over 250 ms (fade + 10 px
    /// slide), holds 1.8 s, fades over 200 ms; the emblem's red arc fills 80 ms after it starts. It is a polite live
    /// region: assistive technology reads "Great skill check. Map found: …".
    /// </summary>
    public void Show(string detail, TimeSpan delay = default)
    {
        _moment?.Stop();
        _moment = null;
        _hide.Stop();
        _detail.Text = detail;
        AutomationProperties.SetName(this, Title + ". " + detail);
        _root.Opacity = 0;
        _slide.X = 0;
        _arc.StrokeDashOffset = ArcUnits * 0.76;
        MotionPolicy policy = _watch.Policy;
        double d = Math.Max(0, delay.TotalMilliseconds);
        if (!policy.ShouldAnimate)
        {
            // no motion: it simply appears and goes
            _root.Opacity = 1;
            _hide.Interval = TimeSpan.FromMilliseconds(d + InMs + HoldMs);
            _hide.Start();
            CampfireAutomation.AnnounceLiveRegion(this);
            return;
        }

        double total = InMs + HoldMs + OutMs;
        var moment = new MotionMoment(this, "ScoreEvent.show") { ExactTiming = true }; // (part of the skill check's landing: it holds, then goes)
        moment.Add(_root, OpacityProperty, Anim.Keys(d, new[]
        {
            new Kf(0, 0), new Kf(InMs, 1, Ease.Out), new Kf(InMs + HoldMs, 1), new Kf(total, 0, Ease.Out),
        }));
        if (!policy.ReducedMotion)
        {
            moment.Add(_slide, TranslateTransform.XProperty, Anim.Keys(d, new[] { new Kf(0, 10), new Kf(InMs, 0, Ease.Out), new Kf(total, 0) }));
            moment.Add(_arc, Shape.StrokeDashOffsetProperty, Anim.Keys(d + 80, new[] { new Kf(0, ArcUnits), new Kf(520, ArcUnits * 0.76, Ease.Out) }));
        }

        _moment = moment;
        moment.Begin();
        CampfireAutomation.AnnounceLiveRegion(this); // (it is showing now: assistive technology finds it on screen)
    }

    /// <summary>Hides it at once.</summary>
    public void Hide()
    {
        _moment?.Stop();
        _moment = null;
        _hide.Stop();
        _root.Opacity = 0;
    }

    /// <summary>Freezes the running show at <paramref name="time"/> from its start (frame captures).</summary>
    public void FreezeAt(TimeSpan time) => _moment?.FreezeAt(time);

    /// <summary>
    /// A Text element named "Great skill check. Map found: …" (a polite live region): its inner texts are not exposed, and
    /// while nothing is shown it is off screen, so a screen reader does not find an empty event.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new ScoreEventAutomationPeer(this);

    private sealed class ScoreEventAutomationPeer : FrameworkElementAutomationPeer
    {
        public ScoreEventAutomationPeer(ScoreEvent owner)
            : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(ScoreEvent);

        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override bool IsOffscreenCore() => !((ScoreEvent)Owner).IsShowing || base.IsOffscreenCore();
    }
}
