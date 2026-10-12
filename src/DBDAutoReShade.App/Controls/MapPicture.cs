using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DBDAutoReShade.App.Media;
using DBDAutoReShade.App.Services;

namespace DBDAutoReShade.App.Controls;

/// <summary>What a <see cref="MapPicture"/> without a picture says.</summary>
public enum MapPlaceholderLabel
{
    /// <summary>The map's name, bottom left (thumbnails, row strips).</summary>
    MapName,

    /// <summary>"Picture unavailable", top left (the hero and the Status plate, which caption the name themselves).</summary>
    Unavailable,

    /// <summary>Nothing.</summary>
    None,
}

/// <summary>
/// One map picture: the original image drawn UniformToFill (a centred crop, never stretched out of its aspect) with
/// HighQuality (Fant) scaling, from <see cref="MapImageCatalog"/>. A map without a picture (a database update can add
/// maps) shows the fog placeholder: a mist-lit slate gradient with the map's name or "Picture unavailable".
/// <see cref="Picture"/> is the image element itself, for moments that blur or scale only the picture.
/// </summary>
public sealed class MapPicture : UserControl
{
    /// <summary>Decorative: the map picture (its caption, button or row names the map) is hidden from assistive technology (the prototype's aria-hidden).</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new DecorativeAutomationPeer(this);

    /// <summary>The map's display name (as in mapDatabase.json).</summary>
    public static readonly DependencyProperty MapNameProperty = DependencyProperty.Register(
        nameof(MapName), typeof(string), typeof(MapPicture), new PropertyMetadata(null, (d, _) => ((MapPicture)d).Update()));

    /// <summary>An explicit picture (overrides the catalog lookup).</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(MapPicture), new PropertyMetadata(null, (d, _) => ((MapPicture)d).Update()));

    /// <summary>What the placeholder says.</summary>
    public static readonly DependencyProperty PlaceholderLabelProperty = DependencyProperty.Register(
        nameof(PlaceholderLabel), typeof(MapPlaceholderLabel), typeof(MapPicture), new PropertyMetadata(MapPlaceholderLabel.MapName, (d, _) => ((MapPicture)d).Update()));

    /// <summary>The catalog to read pictures from (default <see cref="MapImageCatalog.Default"/>).</summary>
    public static readonly DependencyProperty CatalogProperty = DependencyProperty.Register(
        nameof(Catalog), typeof(MapImageCatalog), typeof(MapPicture), new PropertyMetadata(null, (d, _) => ((MapPicture)d).Update()));

    private static readonly Brush PlaceholderBase = CampfirePalette.Linear(new Point(0.18, 0), new Point(0.82, 1),
        (0, Color.FromRgb(0x1D, 0x29, 0x2E)), (1, Color.FromRgb(0x0E, 0x15, 0x18)));

    private static readonly Brush PlaceholderMist = CampfirePalette.Radial(0.28, 0.24, 0.7, 0.7,
        (0, CampfirePalette.Argb(0.5, CampfirePalette.MistColor)), (0.7, CampfirePalette.Argb(0, CampfirePalette.MistColor)));

    private readonly Grid _placeholder;
    private readonly TextBlock _name;
    private readonly TextBlock _unavailable;

    /// <summary>Creates an empty picture (the placeholder).</summary>
    public MapPicture()
    {
        Focusable = false;
        IsTabStop = false;
        ClipToBounds = true;
        _name = new TextBlock
        {
            Margin = new Thickness(12, 10, 12, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            FontFamily = CampfireFonts.Display,
            FontWeight = FontWeights.SemiBold,
            FontSize = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 19,
            Foreground = CampfirePalette.Brush(0.82, CampfirePalette.BoneColor),
            TextWrapping = TextWrapping.Wrap,
        };
        _unavailable = new TextBlock
        {
            Text = "Picture unavailable",
            Margin = new Thickness(18, 14, 18, 14),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            FontFamily = CampfireFonts.Body,
            FontSize = 13,
            Foreground = CampfirePalette.Ash,
        };
        _placeholder = new Grid();
        _placeholder.Children.Add(new Border { Background = PlaceholderBase });
        _placeholder.Children.Add(new Border { Background = PlaceholderMist });
        _placeholder.Children.Add(_name);
        _placeholder.Children.Add(_unavailable);
        Picture = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        MapImageCatalog.ApplyQuality(Picture);
        var root = new Grid { Background = CampfirePalette.NightDeep };
        root.Children.Add(_placeholder);
        root.Children.Add(Picture);
        Content = root;
        Update();
    }

    /// <summary>The map's display name.</summary>
    public string? MapName
    {
        get => (string?)GetValue(MapNameProperty);
        set => SetValue(MapNameProperty, value);
    }

    /// <summary>An explicit picture.</summary>
    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>What the placeholder says.</summary>
    public MapPlaceholderLabel PlaceholderLabel
    {
        get => (MapPlaceholderLabel)GetValue(PlaceholderLabelProperty);
        set => SetValue(PlaceholderLabelProperty, value);
    }

    /// <summary>The catalog to read pictures from.</summary>
    public MapImageCatalog? Catalog
    {
        get => (MapImageCatalog?)GetValue(CatalogProperty);
        set => SetValue(CatalogProperty, value);
    }

    /// <summary>The image element (UniformToFill, HighQuality): animate it to blur or scale only the picture.</summary>
    public Image Picture { get; }

    /// <summary>A picture is shown (false: the placeholder).</summary>
    public bool HasPicture => Picture.Source is not null;

    /// <summary>The shown picture's width in source pixels (0 without one).</summary>
    public int PixelWidth => Picture.Source is BitmapSource b ? b.PixelWidth : 0;

    private void Update()
    {
        ImageSource? source = Source;
        if (source is null && !string.IsNullOrWhiteSpace(MapName))
        {
            source = (Catalog ?? MapImageCatalog.Default).TryGetImage(MapName);
        }

        Picture.Source = source;
        Picture.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        _placeholder.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
        _name.Text = MapName ?? string.Empty;
        _name.Visibility = PlaceholderLabel == MapPlaceholderLabel.MapName ? Visibility.Visible : Visibility.Collapsed;
        _unavailable.Visibility = PlaceholderLabel == MapPlaceholderLabel.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(this, MapName ?? string.Empty);
    }
}

/// <summary>
/// Sizes its child to an aspect range and never larger than the picture allows: width ≤ <see cref="MaxWidthPixels"/>
/// physical pixels (1.8 × the source width) and height ≤ <see cref="MaxHeightPixels"/> (1.8 × the source height) at
/// the current DPI; height between width / <see cref="MaxAspect"/> and width / <see cref="MinAspect"/>. A picture drawn
/// UniformToFill is scaled by whichever side it is short of, so a frame taller than the picture's aspect enlarges it by
/// its height: both caps keep every picture within the limit. The rest of the space is left to the fog around it.
/// Aligned by its own HorizontalContentAlignment / VerticalContentAlignment equivalents (<see cref="FrameAlignment"/>).
/// </summary>
public sealed class AspectFrame : Decorator
{
    /// <summary>The tallest allowed shape, width / height (default 2, a 2:1 frame).</summary>
    public static readonly DependencyProperty MinAspectProperty = DependencyProperty.Register(
        nameof(MinAspect), typeof(double), typeof(AspectFrame), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The widest allowed shape, width / height (default 2).</summary>
    public static readonly DependencyProperty MaxAspectProperty = DependencyProperty.Register(
        nameof(MaxAspect), typeof(double), typeof(AspectFrame), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The widest the frame may be, in physical pixels (NaN = no cap). MapPlate sets 1.8 × the picture width.</summary>
    public static readonly DependencyProperty MaxWidthPixelsProperty = DependencyProperty.Register(
        nameof(MaxWidthPixels), typeof(double), typeof(AspectFrame), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>
    /// The tallest the frame may be, in physical pixels (NaN = no cap). MapPlate sets 1.8 × the picture height: its
    /// frame can be taller than the pictures' aspect (3:2 against about 1.51:1), and UniformToFill then scales by height.
    /// </summary>
    public static readonly DependencyProperty MaxHeightPixelsProperty = DependencyProperty.Register(
        nameof(MaxHeightPixels), typeof(double), typeof(AspectFrame), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Where the frame sits when it is narrower than the space (default centre).</summary>
    public static readonly DependencyProperty FrameAlignmentProperty = DependencyProperty.Register(
        nameof(FrameAlignment), typeof(HorizontalAlignment), typeof(AspectFrame), new FrameworkPropertyMetadata(HorizontalAlignment.Center, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>The tallest allowed shape.</summary>
    public double MinAspect
    {
        get => (double)GetValue(MinAspectProperty);
        set => SetValue(MinAspectProperty, value);
    }

    /// <summary>The widest allowed shape.</summary>
    public double MaxAspect
    {
        get => (double)GetValue(MaxAspectProperty);
        set => SetValue(MaxAspectProperty, value);
    }

    /// <summary>The width cap in physical pixels.</summary>
    public double MaxWidthPixels
    {
        get => (double)GetValue(MaxWidthPixelsProperty);
        set => SetValue(MaxWidthPixelsProperty, value);
    }

    /// <summary>The height cap in physical pixels.</summary>
    public double MaxHeightPixels
    {
        get => (double)GetValue(MaxHeightPixelsProperty);
        set => SetValue(MaxHeightPixelsProperty, value);
    }

    /// <summary>Horizontal placement of a capped frame.</summary>
    public HorizontalAlignment FrameAlignment
    {
        get => (HorizontalAlignment)GetValue(FrameAlignmentProperty);
        set => SetValue(FrameAlignmentProperty, value);
    }

    /// <summary>The frame size for <paramref name="available"/> (DIPs) at a display scale of <paramref name="dpiScaleX"/> on both axes.</summary>
    public Size FrameSize(Size available, double dpiScaleX) => FrameSize(available, dpiScaleX, dpiScaleX);

    /// <summary>The frame size for <paramref name="available"/> (DIPs) at <paramref name="dpiScaleX"/> × <paramref name="dpiScaleY"/>.</summary>
    public Size FrameSize(Size available, double dpiScaleX, double dpiScaleY)
    {
        double minAspect = Math.Max(0.1, Math.Min(MinAspect, MaxAspect));
        double maxAspect = Math.Max(minAspect, MaxAspect);
        double w = available.Width;
        if (double.IsInfinity(w))
        {
            w = double.IsInfinity(available.Height) ? 640 : available.Height * maxAspect;
        }

        if (!double.IsNaN(MaxWidthPixels) && MaxWidthPixels > 0)
        {
            w = Math.Min(w, MaxWidthPixels / (dpiScaleX > 0 ? dpiScaleX : 1));
        }

        double hCap = !double.IsNaN(MaxHeightPixels) && MaxHeightPixels > 0
            ? MaxHeightPixels / (dpiScaleY > 0 ? dpiScaleY : 1)
            : double.PositiveInfinity;
        if (w / maxAspect > hCap)
        {
            w = hCap * maxAspect; // even the widest shape would be too tall: narrow it
        }

        double h = available.Height;
        double hMin = w / maxAspect, hMax = Math.Max(hMin, Math.Min(w / minAspect, hCap));
        h = double.IsInfinity(h) ? hMin : Math.Clamp(h, hMin, hMax);
        if (!double.IsInfinity(available.Height) && h > available.Height)
        {
            h = available.Height;
            w = Math.Min(w, h * maxAspect);
        }

        return new Size(Math.Max(0, w), Math.Max(0, h));
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size constraint)
    {
        DpiScale dpi = Dpi();
        Size size = FrameSize(constraint, dpi.DpiScaleX, dpi.DpiScaleY);
        Child?.Measure(size);
        return size;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        DpiScale dpi = Dpi();
        Size size = FrameSize(arrangeSize, dpi.DpiScaleX, dpi.DpiScaleY);
        double x = FrameAlignment switch
        {
            HorizontalAlignment.Left => 0,
            HorizontalAlignment.Right => arrangeSize.Width - size.Width,
            _ => (arrangeSize.Width - size.Width) / 2,
        };
        double y = (arrangeSize.Height - size.Height) / 2;
        Child?.Arrange(new Rect(new Point(Math.Max(0, x), Math.Max(0, y)), size));
        return arrangeSize;
    }

    private DpiScale Dpi()
    {
        try
        {
            return VisualTreeHelper.GetDpi(this);
        }
        catch (Exception)
        {
            return new DpiScale(1.0, 1.0);
        }
    }
}
