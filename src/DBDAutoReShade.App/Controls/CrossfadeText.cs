using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>How a <see cref="CrossfadeText"/> tells a change.</summary>
public enum TextSwap
{
    /// <summary>The state hero: the old text blurs out (160 ms) while the new one rises 8 px out of a 6 px blur (300 ms after 60 ms).</summary>
    BlurRise,

    /// <summary>A quieter swap (the active preset's file name): 4 px blur and 4 px rise, 260 ms after 60 ms.</summary>
    BlurSoft,

    /// <summary>A plain fade-in (status lines): 220 ms after 100 ms.</summary>
    Fade,
}

/// <summary>
/// Text that tells its changes: a blur-bridged crossfade between the old and the new text (the prototype's hero and
/// status lines). Style the text with <see cref="TextStyle"/> (a TextBlock style such as CfHeroXL) or the inherited
/// font properties. A change with motion off (or the first text) just swaps. The leaving text is drawn over the new one
/// and takes no room (the prototype's <c>position: absolute</c> ghost): the control has the new text's size at once,
/// so whatever sits below can glide to its new place while the texts crossfade. A change in the middle of a swap carries
/// on from what is drawn: the leaving text starts from the live text's opacity, blur and rise (a text that never
/// visibly arrived does not flash), and an older leaving text keeps fading out from where it was instead of vanishing.
/// </summary>
public sealed class CrossfadeText : Control
{
    /// <summary>The text.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(CrossfadeText), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => ((CrossfadeText)d).OnTextChanged((string?)e.OldValue, (string?)e.NewValue)));

    /// <summary>A TextBlock style for both the text and its leaving ghost.</summary>
    public static readonly DependencyProperty TextStyleProperty = DependencyProperty.Register(
        nameof(TextStyle), typeof(Style), typeof(CrossfadeText), new PropertyMetadata(null, (d, e) => ((CrossfadeText)d).ApplyStyle((Style?)e.NewValue)));

    /// <summary>How a change is told.</summary>
    public static readonly DependencyProperty SwapProperty = DependencyProperty.Register(
        nameof(Swap), typeof(TextSwap), typeof(CrossfadeText), new PropertyMetadata(TextSwap.BlurRise));

    /// <summary>Wrapping of the text (default Wrap).</summary>
    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(CrossfadeText), new PropertyMetadata(TextWrapping.Wrap, (d, e) => ((CrossfadeText)d).ApplyWrapping((TextWrapping)e.NewValue)));

    private readonly Grid _root = new();
    private readonly TextBlock _text = new();
    private readonly Canvas _ghostLayer = new() { IsHitTestVisible = false };
    private readonly TranslateTransform _rise = new();
    private readonly MotionPolicyWatch _watch;
    private MotionMoment? _moment;

    static CrossfadeText()
    {
        FocusableProperty.OverrideMetadata(typeof(CrossfadeText), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(CrossfadeText), new FrameworkPropertyMetadata(false));
    }

    /// <summary>Creates empty text.</summary>
    public CrossfadeText()
    {
        _text.RenderTransform = _rise;
        _root.Children.Add(_text);
        _root.Children.Add(_ghostLayer);
        AddVisualChild(_root);
        ApplyWrapping(TextWrapping);
        _watch = new MotionPolicyWatch(this, () =>
        {
            if (!_watch!.Policy.ShouldAnimate)
            {
                StopMoment();
            }
        });
    }

    /// <summary>The text.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>A TextBlock style for the text.</summary>
    public Style? TextStyle
    {
        get => (Style?)GetValue(TextStyleProperty);
        set => SetValue(TextStyleProperty, value);
    }

    /// <summary>How a change is told.</summary>
    public TextSwap Swap
    {
        get => (TextSwap)GetValue(SwapProperty);
        set => SetValue(SwapProperty, value);
    }

    /// <summary>Wrapping of the text.</summary>
    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    /// <summary>
    /// The text changes made while this is set are quick swaps (a change the user made, the prototype's
    /// quickStatusMotion): no delay and no rise; 200 ms, the blurred kinds out of a 4 px blur (a plain fade: 180 ms).
    /// The owner sets it around such a change.
    /// </summary>
    public bool SwapQuickly { get; set; }

    /// <summary>The live text element.</summary>
    public TextBlock TextElement => _text;

    /// <summary>The leaving texts drawn over the live one now (a swap in progress).</summary>
    public IReadOnlyList<TextBlock> Ghosts => _ghostLayer.Children.OfType<TextBlock>().ToList();

    /// <inheritdoc />
    protected override int VisualChildrenCount => 1;

    /// <summary>Freezes a running swap at <paramref name="time"/> (frame captures).</summary>
    public void FreezeAt(TimeSpan time) => _moment?.FreezeAt(time);

    /// <inheritdoc />
    protected override Visual GetVisualChild(int index) => index == 0 ? _root : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>
    /// A Text element named by its text (or its <c>AutomationProperties.Name</c>): it carries the control's heading level
    /// and live setting (the state hero is a level-1 heading; the status line a polite live region), and its inner text
    /// and the leaving ghosts of a swap are not exposed.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new TextControlAutomationPeer(this, () => Text);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size constraint)
    {
        _root.Measure(constraint);
        return _root.DesiredSize;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        _root.Arrange(new Rect(arrangeBounds));
        return arrangeBounds;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // (a leaving ghost keeps the style and size it was shown at: a state hero changes its size step with its text)
    private void ApplyStyle(Style? style) => _text.Style = style;

    private void ApplyWrapping(TextWrapping wrapping) => _text.TextWrapping = wrapping;

    private void StopMoment()
    {
        MotionMoment? moment = _moment;
        _moment = null;
        moment?.Stop();
        _ghostLayer.Children.Clear();
        _text.Effect = null;
    }

    private void OnTextChanged(string? old, string? text)
    {
        // what is drawn now (a swap may be mid-way), read before its clocks go: the live text's look, and the leaving
        // texts still fading out
        double textOpacity = Math.Clamp(_text.Opacity, 0, 1);
        double textBlur = (_text.Effect as BlurEffect)?.Radius ?? 0;
        double textRise = _rise.Y;
        var leaving = _ghostLayer.Children.OfType<TextBlock>()
            .Select(g => (Ghost: g, Opacity: Math.Clamp(g.Opacity, 0, 1), Blur: (g.Effect as BlurEffect)?.Radius))
            .ToList();
        MotionMoment? previous = _moment;
        _moment = null; // (its end must not clear the ghosts this change carries on)
        previous?.Stop();
        _text.Effect = null;

        // the leaving text keeps the colour it was shown in (a local colour, e.g. the cold hero, changes after this)
        object ink = _text.ReadLocalValue(TextBlock.ForegroundProperty);
        Brush? oldInk = ink is SolidColorBrush solid ? Frozen(solid.Color) : ink as Brush;
        double oldWidth = _text.ActualWidth; // (the old text's: no layout has run since)
        _text.Text = text ?? string.Empty;
        MotionPolicy policy = _watch?.Policy ?? MotionPolicy.For(this);
        if (string.IsNullOrEmpty(old) || !policy.ShouldAnimate || !IsLoaded)
        {
            _ghostLayer.Children.Clear();
            return;
        }

        bool reduced = policy.ReducedMotion;
        bool quick = SwapQuickly;
        TextSwap swap = Swap;
        var moment = new MotionMoment(this, "CrossfadeText." + swap);

        // a swap cut short: its leaving text fades out from where it is (posed as it was drawn)
        foreach ((TextBlock ghost, double opacity, double? radiusWas) in leaving)
        {
            if (opacity < 0.01)
            {
                _ghostLayer.Children.Remove(ghost);
                continue;
            }

            if (radiusWas is double radius && ghost.Effect is BlurEffect { IsFrozen: false } effect)
            {
                effect.Radius = radius;
            }

            ghost.Opacity = 0;
            moment.Add(ghost, OpacityProperty, Anim.FromTo(opacity, 0, 120));
        }

        if (swap == TextSwap.BlurRise && textOpacity > 0.01)
        {
            // the old label blurs away on top (160 ms) from how it is drawn now, the new one rises out of the blur
            TextBlock ghost = AddGhost(old, oldWidth, oldInk, textRise);
            ghost.Opacity = 0;
            moment.Add(ghost, OpacityProperty, Anim.FromTo(textOpacity, 0, 160));
            if (!reduced)
            {
                var ghostBlur = new BlurEffect { Radius = 15 };
                ghost.Effect = ghostBlur;
                moment.Add(ghostBlur, BlurEffect.RadiusProperty, Anim.FromTo(Math.Min(textBlur, 15), 15, 160));
            }
        }

        (double ms, double delay, double blur, double rise) = (swap, quick) switch
        {
            (TextSwap.Fade, true) => (180.0, 0.0, 0.0, 0.0),
            (_, true) => (200.0, 0.0, 10.0, 0.0),
            (TextSwap.BlurRise, _) => (300.0, 60.0, 15.0, 8.0),
            (TextSwap.BlurSoft, _) => (260.0, 60.0, 10.0, 4.0),
            _ => (220.0, 100.0, 0.0, 0.0),
        };
        moment.Add(_text, OpacityProperty, Anim.FromTo(0, 1, reduced ? Math.Min(ms, 200) : ms, Ease.Out, delay));
        if (!reduced && blur > 0)
        {
            var effect = new BlurEffect { Radius = 0 };
            _text.Effect = effect;
            moment.Add(effect, BlurEffect.RadiusProperty, Anim.FromTo(blur, 0, ms, Ease.Out, delay));
        }

        if (!reduced && rise > 0)
        {
            moment.Add(_rise, TranslateTransform.YProperty, Anim.FromTo(rise, 0, ms, Ease.Out, delay));
        }

        moment.Ended += (_, _) =>
        {
            // (a newer swap may have taken over: then the ghosts and the effect are its own)
            if (!ReferenceEquals(_moment, moment))
            {
                return;
            }

            _moment = null;
            _ghostLayer.Children.Clear();
            _text.Effect = null;
        };
        _moment = moment;
        moment.Begin();
    }

    /// <summary>
    /// The leaving text as it was drawn: the old style and size (the ramp's size copied, so the first frame already has
    /// it), wrapped where the old text wrapped, in its old colour, at its rise.
    /// </summary>
    private TextBlock AddGhost(string old, double oldWidth, Brush? oldInk, double rise)
    {
        var ghost = new TextBlock
        {
            Text = old,
            Style = _text.Style,
            TextWrapping = _text.TextWrapping,
            IsHitTestVisible = false,
            FontSize = _text.FontSize,
            LineHeight = _text.LineHeight,
            MaxWidth = _text.MaxWidth,
        };
        if (oldWidth > 0)
        {
            // it wraps where the old text wrapped (the layer gives it no width of its own); the pixel of slack keeps a
            // rounded layout width from breaking its widest line
            ghost.Width = Math.Ceiling(oldWidth) + 1;
        }

        if (oldInk is not null)
        {
            ghost.Foreground = oldInk;
        }

        if (Math.Abs(rise) > 0.01)
        {
            ghost.RenderTransform = new TranslateTransform(0, rise);
        }

        _ghostLayer.Children.Add(ghost);
        return ghost;
    }
}
