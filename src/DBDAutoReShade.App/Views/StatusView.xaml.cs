using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DBDAutoReShade.App.Controls;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.ViewModels;

namespace DBDAutoReShade.App.Views;

/// <summary>
/// The Status screen. The window presents a <see cref="StatusScreen"/> with <see cref="Present(StatusScreen, bool)"/>:
/// the texts follow through bindings (blur-bridged crossfades), the hero's ink cools or warms (300 ms), the map plate
/// tells the scene change (reveal, sink, swap or the fire's own moment), the ember runs round the frame when ReShade
/// confirms (and the hero catches the light a beat later), the skill check spins, freezes or hides, and the detection
/// line fades in. When a change moves the hero or the status grid (the hero's size step, a banner opening or closing),
/// they glide to their new places instead of jumping (the prototype's FLIP, 300 ms). A plate that grows with the grid
/// keeps its bottom edge (the border and the caption) where it was while its top rises with the grid: its growth is
/// clipped off at the top and opened with the glide. A change the user made (the override) is told quickly instead:
/// a blur-bridged swap on the plate, quick text swaps, no detection story. The window plays all of it inside its state
/// moment.
/// Layout follows the prototype's breakpoints: the plate width is <c>min(max(cw·.64, cw−420), (vh−430)·2, 960)</c>
/// (cw = the content width), the status grid takes the spare height between plate-w/2 and plate-w/1.5, windows
/// under 1180 px wide use tighter paddings and stack the override controls, and windows at most 720 px high use the
/// short paddings. The state hero's glyphs sit where the prototype's CSS puts them (<see cref="HeroBaselineShift"/>).
/// </summary>
public partial class StatusView : UserControl
{
    /// <summary>The screen shown (set by <see cref="Present"/>).</summary>
    public static readonly DependencyProperty PresentedProperty = DependencyProperty.Register(
        nameof(Presented), typeof(StatusScreen), typeof(StatusView), new PropertyMetadata(null));

    private static readonly Color ColdHeroColor = Color.FromRgb(0xC3, 0xC8, 0xC6);
    private readonly DropShadowEffect _liveGlow = new() { Color = CampfirePalette.EmberColor, BlurRadius = 44, ShadowDepth = 0, Opacity = 0.34 };
    private readonly SolidColorBrush _heroInk = new(CampfirePalette.BoneColor);
    private readonly TranslateTransform _tailRise = new();
    private readonly TranslateTransform _heroShift = new();
    private readonly TranslateTransform _heroGlide = new();
    private readonly TranslateTransform _gridGlide = new();
    private readonly TranslateTransform _plateGlide = new();
    private MotionMoment? _tailMoment;
    private MotionMoment? _glowMoment;
    private MotionMoment? _inkMoment;
    private MotionMoment? _detectionMoment;
    private MotionMoment? _glideMoment;
    private GlideStart? _glideFrom;
    private long _confirmedAt;

    /// <summary>Creates the view (the window sets its data context to the main view model).</summary>
    public StatusView()
    {
        CampfireTheme.EnsureFor(this);
        InitializeComponent();
        ConfirmedLine.RenderTransform = _tailRise;
        HeroText.RenderTransform = _heroShift;
        HeroText.TextElement.Foreground = _heroInk;
        Hero.RenderTransform = _heroGlide;
        StatusGrid.RenderTransform = _gridGlide;
        Plate.RenderTransform = _plateGlide;
        HeroText.SizeChanged += (_, _) => AlignHeroGlyphs();
        SizeChanged += (_, _) => UpdateLayoutMetrics();
        Loaded += (_, _) => UpdateLayoutMetrics();
        Scroller.SizeChanged += (_, _) => UpdateLayoutMetrics();
        Banners.SizeChanged += (_, _) => FitStatusGrid();
        Hero.SizeChanged += (_, _) => FitStatusGrid();
        OverridePanel.SizeChanged += (_, _) => FitStatusGrid();
    }

    /// <summary>Raised by the hotkey banner's "Open Settings".</summary>
    public event EventHandler? OpenSettingsRequested;

    /// <summary>The screen shown.</summary>
    public StatusScreen? Presented
    {
        get => (StatusScreen?)GetValue(PresentedProperty);
        private set => SetValue(PresentedProperty, value);
    }

    /// <summary>The map plate (its skill check lands the Great).</summary>
    public MapPlate MapPlate => Plate;

    /// <summary>Puts the keyboard focus on the page's own control, the manual override's realm list.</summary>
    public void FocusEntry() => CampfireAssist.MoveFocusTo(OverrideRealm);

    /// <summary>
    /// Shows <paramref name="next"/>; a state change (<see cref="StatusScreen.IsStateChange"/>) is told as one (see
    /// <see cref="Present(StatusScreen, bool)"/>).
    /// </summary>
    public bool Present(StatusScreen next) => Present(next, beat: StatusScreen.IsStateChange(Presented, next));

    /// <summary>
    /// Shows <paramref name="next"/>. A change on screen tells itself (subject to the motion policy): the texts
    /// crossfade, the plate plays the scene's moment, waiting for focus → active plays the ember confirm. A
    /// <paramref name="beat"/> (a state change) also fades the detection line in and glides the hero and the status grid
    /// to their new places. A <paramref name="quick"/> change is one the user made (the override on or off, the
    /// prototype's quickStatusMotion): no drama, a blur-bridged swap on the plate (whatever the scenes), quick text swaps
    /// and tails (no delays, no rise), the glide, and no detection fade. Returns true when it was the confirm (the window
    /// adds its own part: the legs flinch, the light flares).
    /// </summary>
    public bool Present(StatusScreen next, bool beat, bool quick = false)
    {
        ArgumentNullException.ThrowIfNull(next);
        StatusScreen? previous = Presented;
        if (Equals(previous, next))
        {
            return false;
        }

        bool glide = (beat || quick) && previous is not null && BeginGlide();

        string heroStyle = next.HeroSize switch
        {
            HeroSize.XL => "CfHeroXL",
            HeroSize.L => "CfHeroL",
            _ => "CfHeroM",
        };
        SwapTextsQuickly(quick);
        try
        {
            Presented = next; // the old hero leaves at its own size, then the new size step applies
        }
        finally
        {
            SwapTextsQuickly(false);
        }

        HeroText.TextStyle = (Style)FindResource(heroStyle);
        AlignHeroGlyphs();

        // the hero's temperature: cold while DBD is not running (the ink changes over 300 ms), an ember glow while a
        // preset is live (on the control: the text's own effect belongs to its crossfade)
        SetHeroInk(next.State == ScreenState.Waiting ? ColdHeroColor : CampfirePalette.BoneColor, animate: previous is not null);
        HeroText.Effect = next.State == ScreenState.Active ? _liveGlow : null;

        // the plate
        SkillCheck skill = Plate.SkillCheck;
        skill.KeyText = next.HotkeyText;
        skill.EnterDelay = next.SkillCheck == SkillCheckState.Scanning && previous?.Plate.IsMap == true
            ? TimeSpan.FromMilliseconds(480) // a new scan while a map is shown: the map sinks first, then the check appears
            : TimeSpan.Zero;
        bool confirming = !quick && previous is { State: ScreenState.WaitingFocus } && next.State == ScreenState.Active && Equals(previous.Plate, next.Plate);
        if (!Equals(Plate.Scene, next.Plate))
        {
            // (only a different scene: a new but equal one would play a swap)
            if (quick && Plate.Scene is { } shown && (shown.IsMap || next.Plate.IsMap) &&
                !(shown.IsMap && next.Plate.IsMap && string.Equals(shown.MapName, next.Plate.MapName, StringComparison.OrdinalIgnoreCase)))
            {
                Plate.SetScene(next.Plate, PlateTransition.Swap); // the user's own change: no reveal, no sink
            }
            else
            {
                Plate.Scene = next.Plate;
            }
        }

        if (previous is null || previous.Tag != next.Tag)
        {
            Plate.CaptionTag = BuildTag(next.Tag);
        }

        if (confirming)
        {
            Plate.Confirm();
            CatchTheLight();
            _confirmedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        else
        {
            Plate.Tone = next.Tone;
        }

        Plate.IsWaitingForFocus = next.State == ScreenState.WaitingFocus;
        skill.State = next.SkillCheck;

        // "Confirmed by ReShade" arrives after the ember has gone round the frame (a quick change: whatever follows the
        // file fades in at once)
        if (previous is not null && previous.Tail != next.Tail && quick && TailLine(next.Tail) is { } line)
        {
            QuickTail(line);
        }
        else if (previous is not null && previous.Tail != next.Tail && next.Tail == PresetTail.Confirmed)
        {
            ArriveTail(confirming ? 900 : Math.Max(240, 900 - SinceConfirm()));
        }

        if (beat && previous is not null && previous.Detection != next.Detection)
        {
            FadeInDetection();
        }

        if (glide)
        {
            EndGlide();
        }

        return confirming;
    }

    /// <summary>
    /// The banners above the hero changed (the restart notice or a hotkey problem opened or closed): the banners'
    /// bindings are brought up to date now, a banner that <paramref name="restartOpened"/> / <paramref name="hotkeyOpened"/>
    /// drops in, and the hero and the status grid glide to their new places. The window calls it inside its state moment.
    /// </summary>
    public void TellBannersChanged(bool restartOpened, bool hotkeyOpened)
    {
        bool glide = BeginGlide();
        foreach (RestartBanner banner in new[] { RestartBanner, HotkeyBanner })
        {
            BindingOperations.GetBindingExpression(banner, RestartBanner.TextProperty)?.UpdateTarget();
            BindingOperations.GetBindingExpression(banner, RestartBanner.IsOpenProperty)?.UpdateTarget();
        }

        if (restartOpened)
        {
            RestartBanner.DropIn();
        }

        if (hotkeyOpened)
        {
            HotkeyBanner.DropIn();
        }

        if (glide)
        {
            EndGlide();
        }
    }

    /// <summary>The next text changes of the hero, the status line and the file name are quick swaps (or not).</summary>
    private void SwapTextsQuickly(bool quick)
    {
        HeroText.SwapQuickly = quick;
        MessageText.SwapQuickly = quick;
        ActiveFileText.SwapQuickly = quick;
    }

    /// <summary>The line that follows the active preset's file name for <paramref name="tail"/>, or null.</summary>
    private FrameworkElement? TailLine(PresetTail tail) => tail switch
    {
        PresetTail.Confirmed => ConfirmedLine,
        PresetTail.Pending => PendingLine,
        PresetTail.Override => OverrideLine,
        _ => null,
    };

    /// <summary>A quick change brought another tail: it fades in (200 ms, no rise, no delay).</summary>
    private void QuickTail(FrameworkElement line)
    {
        _tailMoment?.Stop();
        _tailMoment = null;
        if (!MotionPolicy.For(this).ShouldAnimate || !IsLoaded)
        {
            return;
        }

        var moment = new MotionMoment(this, "StatusView.tail");
        moment.Add(line, OpacityProperty, Anim.FromTo(0, 1, 200));
        _tailMoment = moment;
        moment.Begin();
    }

    private static object? BuildTag(PlateTag tag)
    {
        switch (tag)
        {
            case PlateTag.WaitingForFocus:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                panel.Children.Add(new StatusDot { Kind = DotKind.Ring, IsHeartbeat = true, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                panel.Children.Add(new TextBlock
                {
                    Text = "Waiting for game focus",
                    FontFamily = CampfireFonts.Body,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13,
                    Foreground = CampfirePalette.Ember,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                return panel;
            }

            case PlateTag.Override:
            {
                var pill = new ContentControl { Content = "Override" };
                pill.SetResourceReference(StyleProperty, "CfPillRed");
                return pill;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Milliseconds since the ember confirm began (large when there was none): ReShade's read-back usually confirms a
    /// moment after the switch, and "Confirmed by ReShade" still arrives only once the ember has gone round the frame.
    /// </summary>
    private double SinceConfirm() =>
        _confirmedAt == 0 ? double.MaxValue : System.Diagnostics.Stopwatch.GetElapsedTime(_confirmedAt).TotalMilliseconds;

    /// <summary>The hero catches the light a beat after the ember confirm (its glow swells to .62 and settles at .34).</summary>
    private void CatchTheLight()
    {
        _glowMoment?.Stop();
        _glowMoment = null;
        MotionPolicy policy = MotionPolicy.For(this);
        if (!policy.ShouldAnimate || !IsLoaded)
        {
            return;
        }

        // text-shadow 0 0 44px .0 -> 54px .62 at 35 % (ease-out) -> 44px .34 (ease-in-out), 1 s after 700 ms
        var moment = new MotionMoment(this, "StatusView.heroGlow");
        if (policy.ReducedMotion)
        {
            moment.Add(_liveGlow, DropShadowEffect.OpacityProperty, Anim.Keys(160, new[] { new Kf(0, 0), new Kf(200, 0.34, Ease.Out) }));
        }
        else
        {
            moment.Add(_liveGlow, DropShadowEffect.OpacityProperty, Anim.Keys(700, new[] { new Kf(0, 0), new Kf(350, 0.62, Ease.Out), new Kf(1000, 0.34, Ease.InOut) }));
            moment.Add(_liveGlow, DropShadowEffect.BlurRadiusProperty, Anim.Keys(700, new[] { new Kf(0, 44), new Kf(350, 54, Ease.Out), new Kf(1000, 44, Ease.InOut) }));
        }

        _glowMoment = moment;
        moment.Begin();
    }

    /// <summary>The hero's ink: cold while DBD is not running, bone otherwise (CSS <c>transition: color 300ms ease</c>).</summary>
    private void SetHeroInk(Color ink, bool animate)
    {
        Color from = _heroInk.Color;
        _inkMoment?.Stop();
        _inkMoment = null;
        _heroInk.Color = ink;
        MotionPolicy policy = MotionPolicy.For(this);
        if (!animate || from == ink || !policy.ShouldAnimate || !IsLoaded)
        {
            return;
        }

        var moment = new MotionMoment(this, "StatusView.heroInk");
        moment.Add(_heroInk, SolidColorBrush.ColorProperty, Anim.Colors(0, new ColorKf(0, from), new ColorKf(policy.ReducedMotion ? 200 : 300, ink, Ease.Css)));
        _inkMoment = moment;
        moment.Begin();
    }

    /// <summary>A state change rewrites the detection line: it fades in (160 ms).</summary>
    private void FadeInDetection()
    {
        _detectionMoment?.Stop();
        _detectionMoment = null;
        MotionPolicy policy = MotionPolicy.For(this);
        if (!policy.ShouldAnimate || !IsLoaded)
        {
            return;
        }

        var moment = new MotionMoment(this, "StatusView.detection");
        moment.Add(DetectionLine, OpacityProperty, Anim.FromTo(0, 1, 160));
        _detectionMoment = moment;
        moment.Begin();
    }

    /// <summary>
    /// Notes where the hero and the status grid are drawn now (their layout place plus a glide in progress), before a
    /// change moves them. False when nothing may glide (not shown, motion off or reduced).
    /// </summary>
    private bool BeginGlide()
    {
        MotionPolicy policy = MotionPolicy.For(this);
        if (!IsLoaded || !IsVisible || !policy.ShouldAnimate || policy.ReducedMotion)
        {
            _glideFrom = null;
            return false;
        }

        (double frameTop, double frameBottom) = FrameSpan();
        _glideFrom = new GlideStart(TopOf(Hero), TopOf(StatusGrid), frameTop, frameBottom);
        return true;
    }

    /// <summary>
    /// After the change: lays the page out now and glides the hero and the grid from where they were to their new
    /// places (translateY back to 0, 300 ms ease-out). A plate frame that grew (the grid takes the height a shorter hero
    /// leaves it) keeps its bottom edge, its border and its caption, where they were: it glides from its old bottom to
    /// its new one while its growth, clipped off at the top, opens with the same curve, so its top rises with the grid.
    /// (The prototype clips the grid's growth at the bottom, which cuts the plate's own bottom edge for the glide.)
    /// </summary>
    private void EndGlide()
    {
        if (_glideFrom is not { } from)
        {
            return;
        }

        _glideFrom = null;
        _glideMoment?.Stop();
        _glideMoment = null;
        Plate.Clip = null;
        UpdateLayout();
        double heroDy = from.HeroTop - TopOf(Hero);
        double gridDy = from.GridTop - TopOf(StatusGrid);
        var moment = new MotionMoment(this, "StatusView.glide");
        if (Math.Abs(heroDy) > 1)
        {
            moment.Add(_heroGlide, TranslateTransform.YProperty, Anim.FromTo(heroDy, 0, 300));
        }

        if (Math.Abs(gridDy) > 1)
        {
            moment.Add(_gridGlide, TranslateTransform.YProperty, Anim.FromTo(gridDy, 0, 300));
        }
        else
        {
            gridDy = 0;
        }

        RectangleGeometry? clip = null;
        (double top, double bottom) = FrameSpan();
        double grow = (bottom - top) - (from.FrameBottom - from.FrameTop);
        if (grow > 1 && (Math.Abs(gridDy) > 1 || Math.Abs(from.FrameBottom - bottom) > 1))
        {
            // the frame's bottom edge glides from where it was (on top of the grid's glide) ...
            double plateDy = from.FrameBottom - bottom - gridDy;
            if (Math.Abs(plateDy) > 0.5)
            {
                moment.Add(_plateGlide, TranslateTransform.YProperty, Anim.FromTo(plateDy, 0, 300));
            }

            // ... and its growth is clipped off at the top and opened with the glide (the generous side and bottom
            // insets keep the glow and the ember sweep unclipped)
            double frameTop = Plate.Frame.TranslatePoint(new Point(0, 0), Plate).Y;
            var open = new TranslateTransform();
            clip = new RectangleGeometry(new Rect(-80, frameTop, Plate.ActualWidth + 160, Math.Max(0, Plate.ActualHeight - frameTop) + 80), 0, 0, open);
            Plate.Clip = clip;
            moment.Add(open, TranslateTransform.YProperty, Anim.FromTo(grow, 0, 300));
        }

        if (moment.Count == 0)
        {
            return;
        }

        moment.Ended += (_, _) =>
        {
            if (clip is not null && ReferenceEquals(Plate.Clip, clip))
            {
                Plate.Clip = null;
            }
        };
        _glideMoment = moment;
        moment.Begin();
    }

    /// <summary>Where <paramref name="element"/> is drawn on the page now (its layout place and its render transform).</summary>
    private double TopOf(FrameworkElement element) => element.TranslatePoint(new Point(0, 0), Page).Y;

    /// <summary>Where the plate's frame is drawn on the page now: its top and bottom edges.</summary>
    private (double Top, double Bottom) FrameSpan()
    {
        double top = Plate.Frame.TranslatePoint(new Point(0, 0), Page).Y;
        return (top, top + Plate.Frame.ActualHeight);
    }

    private void ArriveTail(double delayMs)
    {
        _tailMoment?.Stop();
        _tailMoment = null;
        MotionPolicy policy = MotionPolicy.For(this);
        if (!policy.ShouldAnimate || !IsLoaded)
        {
            return;
        }

        var moment = new MotionMoment(this, "StatusView.tail");
        moment.Add(ConfirmedLine, OpacityProperty, Anim.FromTo(0, 1, policy.ReducedMotion ? 200 : 280, Ease.Out, delayMs));
        if (!policy.ReducedMotion)
        {
            moment.Add(_tailRise, TranslateTransform.YProperty, Anim.FromTo(4, 0, 280, Ease.Out, delayMs));
        }

        _tailMoment = moment;
        moment.Begin();
        ConfirmedDot.Pop(delayMs);
    }

    private void OnOpenSettings(object? sender, EventArgs e) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Where the hero, the status grid and the plate's frame were drawn before a change.</summary>
    private readonly record struct GlideStart(double HeroTop, double GridTop, double FrameTop, double FrameBottom);

    /// <summary>
    /// How far down the prototype's CSS draws a line's glyphs compared with WPF, for a line of
    /// <paramref name="lineHeight"/> in a font of <paramref name="fontSize"/> whose ascent is <paramref name="ascent"/>
    /// and whose ascent + descent is <paramref name="lineSpacing"/> (both in em). CSS splits the leading evenly
    /// (baseline = (line height − content) / 2 + ascent); WPF's block line height puts the baseline at
    /// line height × ascent / content. Under the hero's tight line height (0.92) WPF's glyphs sit about 0.08 em higher.
    /// </summary>
    public static double HeroBaselineShift(double fontSize, double lineHeight, double ascent, double lineSpacing)
    {
        if (!(fontSize > 0) || !(lineHeight > 0) || !(ascent > 0) || !(lineSpacing > 0))
        {
            return 0;
        }

        double css = (lineHeight - lineSpacing * fontSize) / 2 + ascent * fontSize;
        double wpf = lineHeight * ascent / lineSpacing;
        return css - wpf;
    }

    /// <summary>Moves the hero's glyphs down to the prototype's place (a render offset: the layout is unchanged).</summary>
    private void AlignHeroGlyphs()
    {
        TextBlock text = HeroText.TextElement;
        double shift = HeroBaselineShift(text.FontSize, text.LineHeight, text.FontFamily.Baseline, text.FontFamily.LineSpacing);
        shift = Math.Floor(shift); // whole pixels, rounded down: crisp glyphs whose baseline lands on the prototype's
        if (Math.Abs(_heroShift.Y - shift) > 0.01)
        {
            _heroShift.Y = shift;
        }
    }

    /// <summary>The prototype's media queries and plate formula, for the window's current size.</summary>
    private void UpdateLayoutMetrics()
    {
        Size viewport = CampfireType.GetViewport(this);
        double vw = viewport.Width, vh = viewport.Height;
        if (vw <= 0 || vh <= 0)
        {
            return;
        }

        bool small = vw < 1180;
        bool shortWindow = vh <= CampfireType.ShortViewportHeight;
        double rail = Math.Round(CampfireType.Clamp(168, 15.3, 220, vw));
        double top = shortWindow ? 22 : small ? 24 : 34;
        double bottom = shortWindow ? 22 : small ? 26 : 30;
        Page.Margin = small ? new Thickness(32, top, 32, bottom) : new Thickness(44, top, 48, bottom);
        double cw = vw - rail - (small ? 64 : 92);
        double plate = shortWindow
            ? Math.Min(Math.Min(Math.Max(cw * 0.62, cw - 420), (vh - 440) * 2), 960)
            : Math.Min(Math.Min(Math.Max(cw * 0.64, cw - 420), (vh - 430) * 2), 960);
        plate = Math.Max(240, Math.Floor(plate));
        PlateColumn.Width = new GridLength(plate);
        GapColumn.Width = new GridLength(small ? 28 : 36);
        StatusGrid.MinHeight = plate / 2;
        StatusGrid.MaxHeight = plate / 1.5;
        StatusGrid.Margin = shortWindow ? new Thickness(0, 18, 0, 18) : new Thickness(0, 28, 0, 28);
        MessageArea.Margin = new Thickness(0, shortWindow ? 8 : 12, 0, 0);
        RestartBanner.Margin = HotkeyBanner.Margin = new Thickness(0, 0, 0, shortWindow ? 16 : 22);
        FactsLines.Margin = new Thickness(0, shortWindow ? 14 : 20, 0, 0);
        foreach (Border row in new[] { DetectionRow, LastSwitchRow })
        {
            row.Padding = shortWindow ? new Thickness(0, 8, 0, 9) : new Thickness(0, 10, 0, 11);
        }

        // the override bar stacks under 1180 px
        if (small)
        {
            OverridePanel.Padding = new Thickness(18, 14, 18, 14);
            OverrideControlsColumn.Width = new GridLength(0);
            OverrideSecondRow.Height = GridLength.Auto;
            Grid.SetColumn(OverrideControls, 0);
            Grid.SetRow(OverrideControls, 1);
            OverrideControls.HorizontalAlignment = HorizontalAlignment.Left;
            OverrideControls.Margin = new Thickness(0, 10, 0, 0);
            OverrideRealm.Width = 210;
        }
        else
        {
            OverridePanel.Padding = new Thickness(20, 16, 20, 16);
            OverrideControlsColumn.Width = GridLength.Auto;
            OverrideSecondRow.Height = new GridLength(0);
            Grid.SetColumn(OverrideControls, 1);
            Grid.SetRow(OverrideControls, 0);
            OverrideControls.HorizontalAlignment = HorizontalAlignment.Right;
            OverrideControls.Margin = new Thickness(0);
            OverrideRealm.Width = 232;
        }

        FitStatusGrid();
    }

    /// <summary>
    /// The status grid takes the height the viewport leaves between the hero and the override bar (the prototype's
    /// flex: 1), clamped to plate-w/2 .. plate-w/1.5; below the minimum the page scrolls. Computed from the other
    /// parts' heights, never from the grid's own, so shrinking the window shrinks it again.
    /// </summary>
    private void FitStatusGrid()
    {
        double viewportHeight = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : ActualHeight;
        if (viewportHeight <= 0 || double.IsNaN(StatusGrid.MinHeight))
        {
            return;
        }

        double available = viewportHeight - Page.Margin.Top - Page.Margin.Bottom
            - Banners.ActualHeight - Hero.ActualHeight - OverridePanel.ActualHeight
            - StatusGrid.Margin.Top - StatusGrid.Margin.Bottom;
        double height = Math.Clamp(available, StatusGrid.MinHeight, Math.Max(StatusGrid.MinHeight, StatusGrid.MaxHeight));
        if (double.IsNaN(StatusGrid.Height) || Math.Abs(StatusGrid.Height - height) > 0.5)
        {
            StatusGrid.Height = height;
        }
    }
}
