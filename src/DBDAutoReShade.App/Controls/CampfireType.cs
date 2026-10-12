using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>A step of the Campfire type ramp whose size follows the window (the prototype's CSS <c>clamp()</c>s).</summary>
public enum TypeRamp
{
    /// <summary>Not scaled (the style's own size).</summary>
    None,

    /// <summary>The state hero, short states: <c>clamp(66px, 7vw, 108px)</c> (≤ 720 px high: <c>clamp(54px, 5.6vw, 82px)</c>),
    /// line height .92, inside the 820 px hero block.</summary>
    HeroXL,

    /// <summary>The state hero, longer states: <c>clamp(54px, 5.6vw, 82px)</c> (≤ 720 px high: <c>clamp(46px, 4.6vw, 68px)</c>),
    /// line height .92, max width 13ch.</summary>
    HeroL,

    /// <summary>The state hero, long states: <c>clamp(40px, 4.1vw, 60px)</c>, line height .92, max width 27ch.</summary>
    HeroM,

    /// <summary>The hero's status line: Barlow 15 / 1.45, max width 70ch.</summary>
    HeroMessage,

    /// <summary>Presets: the selected realm, <c>clamp(38px, 3.7vw, 54px)</c>, line height .95.</summary>
    DetailTitle,

    /// <summary>The map plate caption's realm (or lobby title), <c>clamp(28px, 2.8vw, 38px)</c>, line height .95.</summary>
    PlateRealm,

    /// <summary>Presets: the hero map's name, <c>clamp(24px, 2.3vw, 32px)</c>.</summary>
    HeroMap,
}

/// <summary>The size of a <see cref="TypeRamp"/> step at a viewport: font size, line height (NaN: the style's) and
/// max width (infinity: none), in DIPs.</summary>
public readonly record struct TypeMetrics(double FontSize, double LineHeight, double MaxWidth);

/// <summary>
/// The viewport-scaled type ramp. The prototype sizes its hero, plate caption, detail title and hero map name with CSS
/// <c>clamp(min, n·vw, max)</c> against the window, switches the hero to a smaller ramp when the window is at most
/// 720 px high, and caps the hero labels at 13ch / 27ch (and the hero block at 820 px). <see cref="RampProperty"/>
/// reproduces that on a text element: the <c>CfHeroXL / CfHeroL / CfHeroM / CfHeroMessage / CfDetailTitle /
/// CfPlateRealm / CfHeroMap</c> styles set it, so a TextBlock (or a <see cref="CrossfadeText"/> with one of them as its
/// <c>TextStyle</c>) follows the window by itself: <c>&lt;TextBlock Style="{StaticResource CfHeroXL}"/&gt;</c>, or
/// explicitly <c>cf:CampfireType.Ramp="HeroL"</c>. The viewport is the element's window content (the window's client
/// area); outside a window the 1280 × 800 design viewport applies, which gives the styles' own sizes.
/// The sizes are applied as current values, so an explicit <c>FontSize</c> / <c>LineHeight</c> / <c>MaxWidth</c> (local
/// or bound) always wins. Only loaded elements listen to the window's size, so nothing is kept alive.
/// <see cref="Clamp"/> is the same CSS clamp for layout values (rail <c>clamp(168px, 15.3vw, 220px)</c>, Entity legs
/// <c>clamp(470px, 50vw, 800px)</c>, Presets list <c>clamp(300px, 28vw, 380px)</c>).
/// </summary>
public static class CampfireType
{
    /// <summary>The prototype's design window (the styles' fixed sizes are this viewport's values).</summary>
    public static readonly Size DesignViewport = new(1280, 800);

    /// <summary>Windows at most this high use the short hero ramp (the prototype's <c>@media (max-height: 720px)</c>).</summary>
    public const double ShortViewportHeight = 720;

    /// <summary>The hero block's width cap (<c>.hero { max-width: 820px }</c>).</summary>
    public const double HeroBlockMaxWidth = 820;

    /// <summary>The ramp step of a text element (None: not scaled).</summary>
    public static readonly DependencyProperty RampProperty = DependencyProperty.RegisterAttached(
        "Ramp", typeof(TypeRamp), typeof(CampfireType), new PropertyMetadata(TypeRamp.None, OnRampChanged));

    private static readonly DependencyProperty TrackerProperty = DependencyProperty.RegisterAttached(
        "Tracker", typeof(ViewportTracker), typeof(CampfireType), new PropertyMetadata(null));

    private static readonly Dictionary<(FontFamily Family, FontWeight Weight), double> ChEm = new();

    /// <summary>Gets the ramp step.</summary>
    public static TypeRamp GetRamp(DependencyObject element) => (TypeRamp)element.GetValue(RampProperty);

    /// <summary>Sets the ramp step.</summary>
    public static void SetRamp(DependencyObject element, TypeRamp value) => element.SetValue(RampProperty, value);

    /// <summary>CSS <c>clamp(minPx, vw·1vw, maxPx)</c> for a viewport <paramref name="viewportWidth"/> DIPs wide.</summary>
    public static double Clamp(double minPx, double vw, double maxPx, double viewportWidth) =>
        Math.Clamp(viewportWidth * vw / 100.0, minPx, Math.Max(minPx, maxPx));

    /// <summary>The metrics of <paramref name="ramp"/> at <paramref name="viewport"/> (a zero size means the design viewport).</summary>
    public static TypeMetrics Measure(TypeRamp ramp, Size viewport)
    {
        double w = viewport.Width > 0 ? viewport.Width : DesignViewport.Width;
        double h = viewport.Height > 0 ? viewport.Height : DesignViewport.Height;
        bool shortWindow = h <= ShortViewportHeight;
        double size;
        switch (ramp)
        {
            case TypeRamp.HeroXL:
                size = shortWindow ? Clamp(54, 5.6, 82, w) : Clamp(66, 7, 108, w);
                return new TypeMetrics(size, size * 0.92, HeroBlockMaxWidth);
            case TypeRamp.HeroL:
                size = shortWindow ? Clamp(46, 4.6, 68, w) : Clamp(54, 5.6, 82, w);
                return new TypeMetrics(size, size * 0.92, Math.Min(HeroBlockMaxWidth, 13 * DisplayCh(size)));
            case TypeRamp.HeroM:
                size = Clamp(40, 4.1, 60, w);
                return new TypeMetrics(size, size * 0.92, Math.Min(HeroBlockMaxWidth, 27 * DisplayCh(size)));
            case TypeRamp.HeroMessage:
                return new TypeMetrics(15, 15 * 1.45, Math.Min(HeroBlockMaxWidth, 70 * ChWidth(CampfireFonts.Body, FontWeights.Normal, 15)));
            case TypeRamp.DetailTitle:
                size = Clamp(38, 3.7, 54, w);
                return new TypeMetrics(size, size * 0.95, double.PositiveInfinity);
            case TypeRamp.PlateRealm:
                size = Clamp(28, 2.8, 38, w);
                return new TypeMetrics(size, size * 0.95, double.PositiveInfinity);
            case TypeRamp.HeroMap:
                return new TypeMetrics(Clamp(24, 2.3, 32, w), double.NaN, double.PositiveInfinity);
            default:
                return new TypeMetrics(double.NaN, double.NaN, double.PositiveInfinity);
        }
    }

    /// <summary>The CSS <c>ch</c> unit: the advance width of "0" in <paramref name="family"/> at
    /// <paramref name="weight"/> and <paramref name="fontSize"/> (half an em when the face cannot be read).</summary>
    public static double ChWidth(FontFamily family, FontWeight weight, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(family);
        double em;
        bool known;
        lock (ChEm)
        {
            known = ChEm.TryGetValue((family, weight), out em);
        }

        if (!known)
        {
            // (read outside the lock: font loading must never run under a lock of ours)
            em = 0.5;
            var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs) && glyphs.CharacterToGlyphMap.TryGetValue('0', out ushort glyph))
            {
                em = glyphs.AdvanceWidths[glyph];
            }

            lock (ChEm)
            {
                ChEm[(family, weight)] = em;
            }
        }

        return em * fontSize;
    }

    /// <summary>The viewport <paramref name="element"/> is sized against: its window's content (the client area), else
    /// the window, else <see cref="DesignViewport"/>.</summary>
    public static Size GetViewport(DependencyObject element)
    {
        FrameworkElement? root = ViewportElement(element);
        return root is { ActualWidth: > 0, ActualHeight: > 0 } ? new Size(root.ActualWidth, root.ActualHeight) : DesignViewport;
    }

    private static double DisplayCh(double fontSize) => ChWidth(CampfireFonts.Display, FontWeights.ExtraBold, fontSize);

    private static FrameworkElement? ViewportElement(DependencyObject element)
    {
        Window? window = Window.GetWindow(element);
        if (window is null)
        {
            return null;
        }

        return window.Content is FrameworkElement { ActualWidth: > 0 } content ? content : window;
    }

    private static void OnRampChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        var tracker = (ViewportTracker?)element.GetValue(TrackerProperty);
        if ((TypeRamp)e.NewValue == TypeRamp.None)
        {
            if (tracker is not null)
            {
                tracker.Dispose();
                element.ClearValue(TrackerProperty);
            }

            Reset(element, TextElement.FontSizeProperty);
            Reset(element, TextBlock.LineHeightProperty);
            Reset(element, FrameworkElement.MaxWidthProperty);
            return;
        }

        if (tracker is null)
        {
            tracker = new ViewportTracker(element);
            element.SetValue(TrackerProperty, tracker);
        }

        tracker.Refresh();
    }

    private static void Apply(FrameworkElement element, Size viewport)
    {
        TypeMetrics metrics = Measure(GetRamp(element), viewport);
        if (double.IsNaN(metrics.FontSize))
        {
            return;
        }

        SetSized(element, TextElement.FontSizeProperty, metrics.FontSize);
        if (element is TextBlock)
        {
            if (double.IsNaN(metrics.LineHeight))
            {
                Reset(element, TextBlock.LineHeightProperty);
            }
            else
            {
                SetSized(element, TextBlock.LineHeightProperty, metrics.LineHeight);
            }
        }

        if (double.IsPositiveInfinity(metrics.MaxWidth))
        {
            Reset(element, FrameworkElement.MaxWidthProperty);
        }
        else
        {
            SetSized(element, FrameworkElement.MaxWidthProperty, metrics.MaxWidth);
        }
    }

    /// <summary>Sets a size as the current value unless someone gave it explicitly (local, bound, or by a template).</summary>
    private static void SetSized(FrameworkElement element, DependencyProperty property, double value)
    {
        ValueSource source = DependencyPropertyHelper.GetValueSource(element, property);
        if (source.BaseValueSource is BaseValueSource.Local or BaseValueSource.ParentTemplate or BaseValueSource.ParentTemplateTrigger or BaseValueSource.TemplateTrigger)
        {
            return;
        }

        if (!Equals(element.GetValue(property), value))
        {
            element.SetCurrentValue(property, value);
        }
    }

    /// <summary>Drops a size this behaviour set (the element shows its style's / inherited value again).</summary>
    private static void Reset(DependencyObject element, DependencyProperty property)
    {
        if (DependencyPropertyHelper.GetValueSource(element, property).IsCurrent)
        {
            element.InvalidateProperty(property);
        }
    }

    /// <summary>Follows the window's size while the element is loaded (no reference from the window otherwise).</summary>
    private sealed class ViewportTracker
    {
        private readonly FrameworkElement _element;
        private FrameworkElement? _viewport;
        private bool _pending;

        public ViewportTracker(FrameworkElement element)
        {
            _element = element;
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
        }

        public void Dispose()
        {
            _element.Loaded -= OnLoaded;
            _element.Unloaded -= OnUnloaded;
            Unhook();
        }

        /// <summary>Applies the ramp now and once more after the current change settles (a style being applied may
        /// still reset the font size after the ramp was set).</summary>
        public void Refresh()
        {
            if (_element.IsLoaded)
            {
                Hook();
            }

            Apply(_element, CurrentViewport());
            if (_pending)
            {
                return;
            }

            _pending = true;
            _element.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                _pending = false;
                if (GetRamp(_element) != TypeRamp.None)
                {
                    Apply(_element, CurrentViewport());
                }
            }));
        }

        private Size CurrentViewport() =>
            _viewport is { ActualWidth: > 0, ActualHeight: > 0 } v ? new Size(v.ActualWidth, v.ActualHeight) : DesignViewport;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Hook();
            Apply(_element, CurrentViewport());
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Unhook();

        private void Hook()
        {
            FrameworkElement? root = ViewportElement(_element);
            if (ReferenceEquals(root, _viewport))
            {
                return;
            }

            Unhook();
            _viewport = root;
            if (root is not null)
            {
                root.SizeChanged += OnViewportSizeChanged;
            }
        }

        private void Unhook()
        {
            if (_viewport is not null)
            {
                _viewport.SizeChanged -= OnViewportSizeChanged;
                _viewport = null;
            }
        }

        private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => Apply(_element, e.NewSize);
    }
}
