using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.Services;

namespace DBDAutoReShade.App.Controls;

/// <summary>
/// A realm row's map strip (104 x 52 by default): a slice of each of the realm's maps side by side (the centre of
/// each picture, UniformToFill, HighQuality), or the fog placeholder for maps without a picture.
/// <see cref="IsFogged"/> (a realm with no preset sits in the fog): a mist bank covers the strip and the pictures dim;
/// setting it plays the Presets "Clear" moment (fog rolls in, 280 ms) or the fog lifting (220 ms).
/// </summary>
public sealed class MapStrip : UserControl
{
    /// <summary>Decorative: the row's map strip is hidden from assistive technology (the prototype's aria-hidden).</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new DecorativeAutomationPeer(this);

    /// <summary>The realm's map names (one slice each).</summary>
    public static readonly DependencyProperty MapNamesProperty = DependencyProperty.Register(
        nameof(MapNames), typeof(IEnumerable), typeof(MapStrip), new PropertyMetadata(null, (d, _) => ((MapStrip)d).Rebuild()));

    /// <summary>The realm sits in the fog (no preset). Changes animate.</summary>
    public static readonly DependencyProperty IsFoggedProperty = DependencyProperty.Register(
        nameof(IsFogged), typeof(bool), typeof(MapStrip), new PropertyMetadata(false, (d, _) => ((MapStrip)d).ApplyFog(animate: ((MapStrip)d).IsLoaded)));

    /// <summary>The strip's border brush (default the hairline; bone 50 % on the selected row). Styleable.</summary>
    public static readonly DependencyProperty FrameBrushProperty = DependencyProperty.Register(
        nameof(FrameBrush), typeof(Brush), typeof(MapStrip),
        new PropertyMetadata(CampfirePalette.Line, (d, e) => ((MapStrip)d)._frame.BorderBrush = (Brush)e.NewValue));

    private readonly Grid _slices = new() { ClipToBounds = true };
    private readonly Border _frame;
    private readonly Grid _fog;
    private readonly TranslateTransform _fogMove = new();
    private readonly MotionPolicyWatch _watch;
    private MotionMoment? _moment;

    /// <summary>Creates an empty strip.</summary>
    public MapStrip()
    {
        Focusable = false;
        IsTabStop = false;
        Width = 104;
        Height = 52;
        _fog = new Grid { Opacity = 0, IsHitTestVisible = false, RenderTransform = _fogMove };
        _fog.Children.Add(new Rectangle
        {
            Fill = CampfirePalette.Linear(new Point(0, 0), new Point(1, 0),
                (0, CampfirePalette.Argb(0.25, Color.FromRgb(84, 104, 112))), (0.38, CampfirePalette.Argb(0.62, Color.FromRgb(84, 104, 112))), (1, CampfirePalette.Argb(0.8, Color.FromRgb(70, 86, 92)))),
        });
        _fog.Children.Add(new Rectangle
        {
            Fill = CampfirePalette.Linear(new Point(0, 0), new Point(0, 1),
                (0, CampfirePalette.Argb(0.2, CampfirePalette.NightColor)), (1, CampfirePalette.Argb(0.5, CampfirePalette.NightColor))),
        });
        var inner = new Grid { ClipToBounds = true };
        inner.Children.Add(_slices);
        inner.Children.Add(_fog);
        _frame = new Border
        {
            BorderBrush = CampfirePalette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(1),
            Background = CampfirePalette.NightDeep,
            Child = inner,
            SnapsToDevicePixels = true,
        };
        Content = _frame;
        _watch = new MotionPolicyWatch(this, () =>
        {
            if (!_watch!.Policy.ShouldAnimate)
            {
                _moment?.Stop();
            }
        });
        ApplyFog(animate: false);
    }

    /// <summary>The realm's map names.</summary>
    public IEnumerable? MapNames
    {
        get => (IEnumerable?)GetValue(MapNamesProperty);
        set => SetValue(MapNamesProperty, value);
    }

    /// <summary>The realm sits in the fog.</summary>
    public bool IsFogged
    {
        get => (bool)GetValue(IsFoggedProperty);
        set => SetValue(IsFoggedProperty, value);
    }

    /// <summary>The strip's border brush (bone 50 % on the selected row).</summary>
    public Brush FrameBrush
    {
        get => (Brush)GetValue(FrameBrushProperty);
        set => SetValue(FrameBrushProperty, value);
    }

    /// <summary>Number of slices shown.</summary>
    public int SliceCount => _slices.Children.Count;

    /// <summary>Freezes a running fog moment at <paramref name="time"/> (frame captures).</summary>
    public void FreezeAt(TimeSpan time) => _moment?.FreezeAt(time);

    private void Rebuild()
    {
        _slices.Children.Clear();
        _slices.ColumnDefinitions.Clear();
        List<string> maps = MapNames?.Cast<object?>().Select(o => o?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList() ?? new List<string>();
        for (int i = 0; i < maps.Count; i++)
        {
            _slices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var picture = new MapPicture { MapName = maps[i], PlaceholderLabel = maps.Count == 1 ? MapPlaceholderLabel.MapName : MapPlaceholderLabel.None };
            if (i > 0)
            {
                picture.Margin = new Thickness(1, 0, 0, 0);
            }

            Grid.SetColumn(picture, i);
            _slices.Children.Add(picture);
        }
    }

    private void ApplyFog(bool animate)
    {
        bool fogged = IsFogged;
        double fogWas = _fog.Opacity, picturesWas = _slices.Opacity, moveWas = _fogMove.X;
        _moment?.Stop();
        _moment = null;
        double width = Math.Max(1, double.IsNaN(Width) ? ActualWidth : Width);
        _fog.Opacity = fogged ? 1 : 0;
        _fogMove.X = fogged ? 0 : -0.26 * width;
        _slices.Opacity = fogged ? 0.6 : 1;
        MotionPolicy policy = _watch?.Policy ?? MotionPolicy.For(this);
        if (!animate || _watch is null || !policy.ShouldAnimate)
        {
            return;
        }

        var moment = new MotionMoment(this, fogged ? "MapStrip.fogIn" : "MapStrip.fogOut");
        double ms = policy.ReducedMotion ? 200 : fogged ? 280 : 220;
        moment.Add(_fog, OpacityProperty, Anim.FromTo(fogWas, _fog.Opacity, ms));
        moment.Add(_slices, OpacityProperty, Anim.FromTo(picturesWas, _slices.Opacity, ms));
        if (!policy.ReducedMotion && fogged)
        {
            // the fog swallows the strip: it rolls in from the left
            moment.Add(_fogMove, TranslateTransform.XProperty, Anim.FromTo(moveWas, 0, ms));
        }

        _moment = moment;
        moment.Begin();
    }
}
