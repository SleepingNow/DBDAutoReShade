using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.Services;

namespace DBDAutoReShade.App.Controls;

/// <summary>Which layer of the atmosphere a <see cref="FogBackdrop"/> draws (the window stacks them in this order:
/// backdrop → Entity legs → fog bank → content → rail → front fog).</summary>
public enum FogLayer
{
    /// <summary>Behind everything: the darkened, blurred map, the tint, the halo, two drifting fog layers and the campfire light.</summary>
    Backdrop,

    /// <summary>The low fog bank that rolls in over the Entity's feet (the lower 56 % of the window).</summary>
    Bank,

    /// <summary>The front wisp along the bottom of the Status content (96 px high).</summary>
    Front,
}

/// <summary>
/// The fog. As <see cref="FogLayer.Backdrop"/>: the selected map's picture darkened (60 %) and blurred, the tint,
/// a cold halo, two fog layers drifting slowly (80 s / 64 s, ambient), and the campfire's warm light low on the left
/// whose strength is <see cref="EmberLevel"/> (one per state: waiting 0, ready .5, scanning .55, waiting for focus .72,
/// active 1, paused .22, match ended .5) and which <see cref="Flare"/> swells once. As <see cref="FogLayer.Bank"/> and
/// <see cref="FogLayer.Front"/>: the fog that sits over the legs and along the bottom. Drift is ambient: it holds in
/// place whenever the <see cref="MotionPolicy"/> forbids ambient motion.
/// </summary>
public sealed class FogBackdrop : UserControl
{
    /// <summary>Decorative: the fog layers is hidden from assistive technology (the prototype's aria-hidden).</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new DecorativeAutomationPeer(this);

    /// <summary>The layer drawn.</summary>
    public static readonly DependencyProperty LayerProperty = DependencyProperty.Register(
        nameof(Layer), typeof(FogLayer), typeof(FogBackdrop), new PropertyMetadata(FogLayer.Backdrop, (d, _) => ((FogBackdrop)d).Build()));

    /// <summary>The map shown behind the fog (null: none). Changing it crossfades (220 ms; see <see cref="ShowMap"/>).</summary>
    public static readonly DependencyProperty MapNameProperty = DependencyProperty.Register(
        nameof(MapName), typeof(string), typeof(FogBackdrop), new PropertyMetadata(null, (d, e) => ((FogBackdrop)d).OnMapChanged((string?)e.NewValue)));

    /// <summary>The campfire light, 0..1 (animates 600 ms; 1.2 s when it dies to 0).</summary>
    public static readonly DependencyProperty EmberLevelProperty = DependencyProperty.Register(
        nameof(EmberLevel), typeof(double), typeof(FogBackdrop), new PropertyMetadata(0.0, (d, e) => ((FogBackdrop)d).OnEmberLevelChanged((double)e.OldValue, (double)e.NewValue)));

    private static readonly TimeSpan DefaultCrossfade = TimeSpan.FromMilliseconds(220);
    private static readonly Color Mist = CampfirePalette.MistColor;
    private static readonly Color Night = CampfirePalette.NightColor;
    private static readonly Color Ember = CampfirePalette.EmberColor;

    private readonly Grid _root = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly MotionPolicyWatch _watch;
    private AmbientLoop _drift;
    private readonly List<(TranslateTransform Move, double Period, double Amplitude, bool Reverse)> _drifts = new();
    private readonly Image[] _images = new Image[2];
    private readonly Grid[] _imageHosts = new Grid[2];
    private Rectangle? _emberGlow;
    private Grid? _emberFlare;
    private ScaleTransform? _flareScale;
    private MotionMoment? _imageMoment;
    private MotionMoment? _emberMoment;
    private MotionMoment? _flareMoment;
    private int _front;
    private TimeSpan _nextCrossfade = DefaultCrossfade;
    private TimeSpan? _nextEmberFade;

    /// <summary>Creates the backdrop layer.</summary>
    public FogBackdrop()
    {
        Focusable = false;
        IsTabStop = false;
        IsHitTestVisible = false;
        Content = _root;
        _drift = new AmbientLoop(this, "FogBackdrop.drift", BuildDrift, AmbientLoop.SlowFrameRate);
        _watch = new MotionPolicyWatch(this, OnPolicyChanged);
        Build();
    }

    /// <summary>The layer drawn.</summary>
    public FogLayer Layer
    {
        get => (FogLayer)GetValue(LayerProperty);
        set => SetValue(LayerProperty, value);
    }

    /// <summary>The map behind the fog.</summary>
    public string? MapName
    {
        get => (string?)GetValue(MapNameProperty);
        set => SetValue(MapNameProperty, value);
    }

    /// <summary>The campfire light, 0..1.</summary>
    public double EmberLevel
    {
        get => (double)GetValue(EmberLevelProperty);
        set => SetValue(EmberLevelProperty, value);
    }

    /// <summary>The drift's clocks are running.</summary>
    public bool IsDrifting => _drift.IsRunning;

    /// <summary>
    /// Shows <paramref name="mapName"/> (null: none) with a crossfade of <paramref name="crossfade"/> (220 ms while
    /// browsing; 600-700 ms inside a state-change moment). The duration applies to this change only: when the map is
    /// already shown nothing happens, and later <see cref="MapName"/> changes use the default 220 ms. A binding on
    /// <see cref="MapName"/> is kept.
    /// </summary>
    public void ShowMap(string? mapName, TimeSpan crossfade)
    {
        if (MapName == mapName)
        {
            return;
        }

        _nextCrossfade = crossfade;
        try
        {
            SetCurrentValue(MapNameProperty, mapName);
        }
        finally
        {
            _nextCrossfade = DefaultCrossfade;
        }
    }

    /// <summary>
    /// Sets <see cref="EmberLevel"/> with a fade of <paramref name="duration"/> for this change only, instead of the
    /// state change's swell (600 ms; 1.2 s to 0): a change the user made (the override) is quick. Zero applies it at once.
    /// </summary>
    public void SetEmberLevel(double level, TimeSpan duration)
    {
        _nextEmberFade = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        try
        {
            EmberLevel = level;
        }
        finally
        {
            _nextEmberFade = null;
        }
    }

    /// <summary>The campfire's light swells once: fast in (ease-out), slowly back (ease-in-out). Backdrop layer only.</summary>
    public void Flare(TimeSpan delay, TimeSpan duration)
    {
        _flareMoment?.Stop();
        _flareMoment = null;
        if (_emberFlare is null || _flareScale is null || !_watch.Policy.ShouldAnimate)
        {
            return;
        }

        bool reduced = _watch.Policy.ReducedMotion;
        double ms = duration.TotalMilliseconds;
        double d = delay.TotalMilliseconds;
        var moment = new MotionMoment(this, "FogBackdrop.flare");
        moment.Add(_emberFlare, OpacityProperty, Anim.Keys(d, new[] { new Kf(0, 0), new Kf(ms * 0.32, 1, Ease.Out), new Kf(ms, 0, Ease.InOut) }));
        if (!reduced)
        {
            moment.Add(_flareScale, ScaleTransform.ScaleXProperty, Anim.Keys(d, new[] { new Kf(0, 0.92), new Kf(ms * 0.32, 1.08, Ease.Out), new Kf(ms, 1.12, Ease.InOut) }));
            moment.Add(_flareScale, ScaleTransform.ScaleYProperty, Anim.Keys(d, new[] { new Kf(0, 0.92), new Kf(ms * 0.32, 1.08, Ease.Out), new Kf(ms, 1.12, Ease.InOut) }));
        }

        _flareMoment = moment;
        moment.Begin();
    }

    /// <summary>Freezes every running moment and the drift at <paramref name="time"/> (frame captures).</summary>
    public void FreezeAt(TimeSpan time)
    {
        _imageMoment?.FreezeAt(time);
        _emberMoment?.FreezeAt(time);
        _flareMoment?.FreezeAt(time);
    }

    /// <summary>A rectangle filled with a radial gradient: CSS radial-gradient(rx ry at cx cy, color, transparent stop) on a box of
    /// relative x / width (the layer's own geometry, so the drift can move the brush).</summary>
    private static Rectangle Radial(double cx, double cy, double rx, double ry, params (double Offset, Color Color)[] stops) =>
        new() { Fill = CampfirePalette.Radial(cx, cy, rx, ry, stops) };

    private void Build()
    {
        _drift.Stop();
        _drift = new AmbientLoop(this, "FogBackdrop.drift", BuildDrift, AmbientLoop.SlowFrameRate);
        _drifts.Clear();
        _root.Children.Clear();
        _emberGlow = null;
        _emberFlare = null;
        switch (Layer)
        {
            case FogLayer.Backdrop:
                BuildBackdrop();
                break;
            case FogLayer.Bank:
                BuildBank();
                break;
            default:
                BuildFront();
                break;
        }

        UpdateAmbient();
    }

    private void BuildBackdrop()
    {
        // the map picture, darkened and blurred, crossfading between two layers (inset -24 px hides the blurred edge)
        for (int i = 0; i < 2; i++)
        {
            var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            MapImageCatalog.ApplyQuality(image);
            var blur = new BlurEffect { Radius = 6, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
            blur.Freeze();
            var host = new Grid { Margin = new Thickness(-24), Opacity = 0, ClipToBounds = true, Effect = blur };
            host.Children.Add(image);
            _images[i] = image;
            _imageHosts[i] = host;
            _root.Children.Add(host);
        }

        // the tint: dark at the rail, lighter in the middle; darker toward the bottom
        _root.Children.Add(new Rectangle
        {
            Fill = CampfirePalette.Linear(new Point(0, 0), new Point(0, 1),
                (0, CampfirePalette.Argb(0.30, Night)), (0.48, CampfirePalette.Argb(0.58, Night)), (1, CampfirePalette.Argb(0.9, Night))),
        });
        _root.Children.Add(new Rectangle
        {
            Fill = CampfirePalette.Linear(new Point(0, 0), new Point(1, 0),
                (0, CampfirePalette.Argb(0.94, Night)), (0.34, CampfirePalette.Argb(0.62, Night)), (0.68, CampfirePalette.Argb(0.42, Night)), (1, CampfirePalette.Argb(0.62, Night))),
        });

        // the cold halo, top right (top -30 %, right -12 %, 62 % x 96 %)
        var halo = new Grid();
        halo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50, GridUnitType.Star) });
        halo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62, GridUnitType.Star) });
        halo.RowDefinitions.Add(new RowDefinition { Height = new GridLength(96, GridUnitType.Star) });
        halo.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34, GridUnitType.Star) });
        var haloDisc = new Rectangle
        {
            Fill = CampfirePalette.Radial(0.5, 0.5, 0.5, 0.5, (0, CampfirePalette.Argb(0.5, Mist)), (0.58, CampfirePalette.Argb(0.17, Mist)), (1, CampfirePalette.Argb(0, Mist))),
        };
        Grid.SetColumn(haloDisc, 1);
        var haloHost = new Grid();
        haloHost.Children.Add(halo);
        halo.Children.Add(haloDisc);
        haloHost.SizeChanged += (_, e) => halo.Margin = new Thickness(0, -0.30 * e.NewSize.Height, -0.12 * e.NewSize.Width, 0);
        _root.Children.Add(haloHost);

        // two fog layers, drifting ±9 % of their 160 % width (80 s and 64 s, alternate)
        _root.Children.Add(DriftLayer(80000, reverse: false,
            Radial(0.852, 0.20, 0.34 * 1.6, 0.26, (0, CampfirePalette.Argb(0.30, Mist)), (0.7, CampfirePalette.Argb(0, Mist))),
            Radial(0.244, 0.40, 0.26 * 1.6, 0.20, (0, CampfirePalette.Argb(0.16, Mist)), (0.7, CampfirePalette.Argb(0, Mist)))));
        _root.Children.Add(DriftLayer(64000, reverse: true,
            Radial(0.052, 0.66, 0.22 * 1.6, 0.16, (0, CampfirePalette.Argb(0.20, Mist)), (0.7, CampfirePalette.Argb(0, Mist))),
            Radial(0.692, 0.52, 0.26 * 1.6, 0.14, (0, CampfirePalette.Argb(0.14, Mist)), (0.7, CampfirePalette.Argb(0, Mist)))));

        // the campfire light, low left (left -14 %, bottom -38 %, 72 % x 86 %), and its one-shot flare
        var emberBox = new Grid();
        emberBox.SizeChanged += (_, e) =>
        {
            double w = e.NewSize.Width, h = e.NewSize.Height;
            var rect = new Thickness(-0.14 * w, h - 0.86 * h + 0.38 * h, w - (-0.14 * w + 0.72 * w), -0.38 * h);
            if (_emberGlow is not null)
            {
                _emberGlow.Margin = rect;
            }

            if (_emberFlare is not null)
            {
                _emberFlare.Margin = rect;
            }
        };
        _emberGlow = new Rectangle
        {
            Opacity = EmberLevel,
            Fill = CampfirePalette.Radial(0.5, 0.5, 0.5, 0.5, (0, CampfirePalette.Argb(0.26, Ember)), (0.52, CampfirePalette.Argb(0.09, Ember)), (1, CampfirePalette.Argb(0, Ember))),
        };
        _flareScale = new ScaleTransform(1, 1);
        _emberFlare = new Grid { Opacity = 0, RenderTransform = _flareScale, RenderTransformOrigin = new Point(0.3, 0.8) };
        _emberFlare.Children.Add(new Rectangle
        {
            Fill = CampfirePalette.Radial(0.5, 0.5, 0.5, 0.5,
                (0, CampfirePalette.Argb(0.42, Color.FromRgb(255, 170, 90))), (0.5, CampfirePalette.Argb(0.16, Ember)), (1, CampfirePalette.Argb(0, Ember))),
        });
        emberBox.Children.Add(_emberGlow);
        emberBox.Children.Add(_emberFlare);
        _root.Children.Add(emberBox);
        OnMapChanged(MapName);
    }

    private void BuildBank()
    {
        // the low fog bank: the lower 56 % of the window
        var bank = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
        _root.SizeChanged += (_, e) => bank.Height = e.NewSize.Height * 0.56;
        bank.Children.Add(DriftLayer(72000, reverse: false,
            Radial(0.436, 1.06, 0.78 * 1.6, 0.58, (0, CampfirePalette.Argb(0.62, Mist)), (0.5, CampfirePalette.Argb(0.26, Mist)), (0.76, CampfirePalette.Argb(0, Mist)))));
        bank.Children.Add(DriftLayer(110000, reverse: true,
            Radial(-0.012, 0.78, 0.26 * 1.6, 0.36, (0, CampfirePalette.Argb(0.36, Mist)), (0.7, CampfirePalette.Argb(0, Mist))),
            Radial(0.404, 0.70, 0.30 * 1.6, 0.40, (0, CampfirePalette.Argb(0.24, Mist)), (0.7, CampfirePalette.Argb(0, Mist))),
            Radial(0.884, 0.76, 0.32 * 1.6, 0.44, (0, CampfirePalette.Argb(0.42, Mist)), (0.7, CampfirePalette.Argb(0, Mist))),
            Radial(1.236, 0.66, 0.24 * 1.6, 0.34, (0, CampfirePalette.Argb(0.30, Mist)), (0.7, CampfirePalette.Argb(0, Mist)))));
        _root.Children.Add(bank);
    }

    private void BuildFront()
    {
        // the front wisp: 96 px along the bottom
        var front = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Height = 96 };
        front.Children.Add(DriftLayer(58000, reverse: false,
            Radial(0.18, 1.12, 0.46 * 1.6, 0.9, (0, CampfirePalette.Argb(0.34, Mist)), (0.74, CampfirePalette.Argb(0, Mist))),
            Radial(1.012, 1.16, 0.40 * 1.6, 0.8, (0, CampfirePalette.Argb(0.28, Mist)), (0.74, CampfirePalette.Argb(0, Mist)))));
        _root.Children.Add(front);
    }

    /// <summary>
    /// A fog layer: CSS spans it from -30 % to 130 % of the window and drifts it ±9 % of that width. Here the gradients
    /// are drawn on the window's own box (their x and x-radius converted: x' = -0.3 + 1.6 x, rx' = 1.6 rx) and the
    /// drift moves the brushes (±0.144 of the window width), which needs no layout.
    /// </summary>
    private Grid DriftLayer(double period, bool reverse, params Rectangle[] fills)
    {
        var move = new TranslateTransform();
        var layer = new Grid();
        foreach (Rectangle fill in fills)
        {
            var brush = ((Brush)fill.Fill).Clone();
            brush.RelativeTransform = move;
            fill.Fill = brush;
            layer.Children.Add(fill);
        }

        _drifts.Add((move, period, 0.144, reverse));
        move.X = reverse ? 0.144 : -0.144;
        return layer;
    }

    private IEnumerable<(DependencyObject, DependencyProperty, AnimationTimeline)> BuildDrift()
    {
        foreach ((TranslateTransform move, double period, double amplitude, bool reverse) in _drifts)
        {
            double from = reverse ? amplitude : -amplitude;
            yield return (move, TranslateTransform.XProperty, Anim.Loop(period, new[] { new Kf(0, from), new Kf(period, -from, Ease.CssInOut) }, alternate: true));
        }
    }

    private void OnMapChanged(string? mapName)
    {
        if (Layer != FogLayer.Backdrop || _images[0] is null)
        {
            return;
        }

        TimeSpan crossfade = _nextCrossfade;
        ImageSource? source = string.IsNullOrWhiteSpace(mapName) ? null : MapImageCatalog.Default.TryGetImage(mapName);
        int back = 1 - _front;
        Grid front = _imageHosts[_front], next = _imageHosts[back];
        double frontWas = front.Opacity, nextWas = next.Opacity;
        _imageMoment?.Stop();
        _imageMoment = null;
        _images[back].Source = source;
        next.Opacity = source is null ? 0 : 0.6;
        front.Opacity = 0;
        _front = back;
        MotionPolicy policy = _watch.Policy;
        if (!policy.ShouldAnimate || !IsLoaded || crossfade <= TimeSpan.Zero)
        {
            return;
        }

        double ms = policy.ReducedMotion ? Math.Min(200, crossfade.TotalMilliseconds) : crossfade.TotalMilliseconds;
        var moment = new MotionMoment(this, "FogBackdrop.map");
        if (frontWas > 0.001)
        {
            moment.Add(front, OpacityProperty, Anim.FromTo(frontWas, 0, ms));
        }

        if (next.Opacity > 0.001)
        {
            moment.Add(next, OpacityProperty, Anim.FromTo(nextWas, next.Opacity, ms));
        }

        _imageMoment = moment;
        moment.Begin();
    }

    private void OnEmberLevelChanged(double from, double to)
    {
        if (_emberGlow is null)
        {
            return;
        }

        double was = _emberGlow.Opacity;
        _emberMoment?.Stop();
        _emberMoment = null;
        _emberGlow.Opacity = to;
        MotionPolicy policy = _watch.Policy;
        double ms = _nextEmberFade?.TotalMilliseconds ?? (to <= 0.001 ? 1200 : 600);
        if (!policy.ShouldAnimate || !IsLoaded || ms <= 0)
        {
            return;
        }

        if (policy.ReducedMotion)
        {
            ms = Math.Min(ms, 200);
        }

        var moment = new MotionMoment(this, "FogBackdrop.ember");
        moment.Add(_emberGlow, OpacityProperty, Anim.FromTo(was, to, ms));
        _emberMoment = moment;
        moment.Begin();
    }

    private void OnPolicyChanged()
    {
        if (!_watch.Policy.ShouldAnimate)
        {
            _imageMoment?.Stop();
            _emberMoment?.Stop();
            _flareMoment?.Stop();
        }

        UpdateAmbient();
    }

    private void UpdateAmbient()
    {
        if (_watch is null)
        {
            return;
        }

        MotionPolicy policy = _watch.Policy;
        if (!policy.AnimationsEnabled || policy.ReducedMotion)
        {
            _drift.Stop();
            return;
        }

        _drift.Set(IsLoaded && policy.ShouldRunAmbient);
    }
}
