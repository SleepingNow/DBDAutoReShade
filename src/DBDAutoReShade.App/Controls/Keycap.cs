using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>The size of a <see cref="Keycap"/>.</summary>
public enum KeycapSize
{
    /// <summary>The rail footer key (min 30 x 26, Big Shoulders 800 / 16); a long hotkey wraps after a "+".</summary>
    Small,

    /// <summary>The Settings hotkey key (min 58 x 44, Big Shoulders 800 / 26).</summary>
    Large,
}

/// <summary>
/// A keyboard key (<c>kbd</c>): the configured hotkey in Big Shoulders 800 on a bone-tinted key with a heavier bottom
/// edge. <see cref="IsGlowing"/> lights it in ember (the rail key while the app reads); <see cref="IsCapturing"/> is the
/// Settings capture look (ember border and text). Long hotkeys (Ctrl+Alt+Shift+NumPad5) wrap after a "+". For assistive
/// technology it is a Text element: its <c>AutomationProperties.Name</c> when set, else the key.
/// </summary>
public sealed class Keycap : UserControl
{
    /// <summary>The hotkey text, e.g. "F8" or "Ctrl+Shift+F9".</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Keycap), new PropertyMetadata(string.Empty, (d, _) => ((Keycap)d).UpdateText()));

    /// <summary>The key size.</summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(KeycapSize), typeof(Keycap), new PropertyMetadata(KeycapSize.Small, (d, _) => ((Keycap)d).UpdateSize()));

    /// <summary>The ember glow (rail key while scanning), faded in / out over 200 ms.</summary>
    public static readonly DependencyProperty IsGlowingProperty = DependencyProperty.Register(
        nameof(IsGlowing), typeof(bool), typeof(Keycap), new PropertyMetadata(false, (d, _) => ((Keycap)d).UpdateGlow(animate: ((Keycap)d).IsLoaded)));

    /// <summary>Capturing a new hotkey: ember border, ember text and glow.</summary>
    public static readonly DependencyProperty IsCapturingProperty = DependencyProperty.Register(
        nameof(IsCapturing), typeof(bool), typeof(Keycap), new PropertyMetadata(false, (d, _) => ((Keycap)d).UpdateCapture()));

    private static readonly Brush DefaultFill = CampfirePalette.Brush(0.06, CampfirePalette.BoneColor);
    private readonly Border _key;
    private readonly Border _glow;
    private readonly TextBlock _text;
    private readonly MotionPolicyWatch _watch;
    private MotionMoment? _moment;

    /// <summary>Creates an empty small key.</summary>
    public Keycap()
    {
        Focusable = false;
        IsTabStop = false;
        Background = DefaultFill;
        _text = new TextBlock
        {
            FontFamily = CampfireFonts.Display,
            FontWeight = FontWeights.ExtraBold,
            Foreground = CampfirePalette.Bone,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        _key = new Border
        {
            BorderBrush = CampfirePalette.LineStrong,
            BorderThickness = new Thickness(1, 1, 1, 2),
            CornerRadius = new CornerRadius(3),
            SnapsToDevicePixels = true,
            Child = _text,
        };
        _key.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background)) { Source = this });
        _glow = new Border
        {
            Margin = new Thickness(-2),
            CornerRadius = new CornerRadius(4),
            BorderBrush = CampfirePalette.Brush(0.6, CampfirePalette.EmberColor),
            BorderThickness = new Thickness(1),
            Opacity = 0,
            IsHitTestVisible = false,
            Effect = Frozen(new DropShadowEffect { Color = CampfirePalette.EmberColor, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.7 }),
        };
        var root = new Grid();
        root.Children.Add(_glow);
        root.Children.Add(_key);
        Content = root;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        _watch = new MotionPolicyWatch(this, () => UpdateGlow(animate: false));
        UpdateSize();
        UpdateText();
    }

    /// <summary>The hotkey text.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The key size.</summary>
    public KeycapSize Size
    {
        get => (KeycapSize)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>The ember glow.</summary>
    public bool IsGlowing
    {
        get => (bool)GetValue(IsGlowingProperty);
        set => SetValue(IsGlowingProperty, value);
    }

    /// <summary>Capturing a new hotkey.</summary>
    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        set => SetValue(IsCapturingProperty, value);
    }

    /// <summary>The text with a break opportunity after each "+" (so "Ctrl+Alt+Shift+NumPad5" wraps, never clips).</summary>
    public static string Breakable(string? hotkey) => (hotkey ?? string.Empty).Replace("+", "+​", StringComparison.Ordinal);

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>
    /// A Text element named by its <c>AutomationProperties.Name</c> when the page gives one ("Detection hotkey F8"),
    /// else by the key itself; the name is never overwritten by the key text (that would drop the page's label).
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new TextControlAutomationPeer(this, () => Text);

    private void UpdateText() => _text.Text = Breakable(Text);

    private void UpdateSize()
    {
        if (Size == KeycapSize.Large)
        {
            _key.MinWidth = 58;
            _key.MinHeight = 44;
            _key.Padding = new Thickness(12, 0, 12, 0);
            _text.FontSize = 26;
            _text.LineHeight = 28.6;
        }
        else
        {
            _key.MinWidth = 30;
            _key.MinHeight = 26;
            _key.Padding = new Thickness(7, 4, 7, 4);
            _text.FontSize = 16;
            _text.LineHeight = 17.6;
        }
    }

    private void UpdateCapture()
    {
        bool on = IsCapturing;
        _key.BorderBrush = on ? CampfirePalette.Ember : CampfirePalette.LineStrong;
        _text.Foreground = on ? CampfirePalette.Ember : CampfirePalette.Bone;
        UpdateGlow(animate: false);
    }

    private void UpdateGlow(bool animate)
    {
        double target = IsGlowing || IsCapturing ? 1 : 0;
        _moment?.Stop();
        _moment = null;
        double was = _glow.Opacity;
        _glow.Opacity = target;
        MotionPolicy policy = _watch?.Policy ?? MotionPolicy.For(this);
        if (!animate || !policy.ShouldAnimate || Math.Abs(was - target) < 0.001)
        {
            return;
        }

        var moment = new MotionMoment(this, "Keycap.glow");
        moment.Add(_glow, OpacityProperty, Anim.FromTo(was, target, 200));
        _moment = moment;
        moment.Begin();
    }
}
