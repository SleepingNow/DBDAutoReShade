using System.Windows;
using System.Windows.Media;

namespace DBDAutoReShade.App.Media;

/// <summary>
/// The Campfire colour tokens, the single source for both the theme dictionary (<c>Themes/Campfire.xaml</c>
/// references these through <c>x:Static</c>) and the self-contained controls. Every brush is frozen.
/// Cold fog everywhere; one warm ember where things are alive. Ember = live / confirmed, Entity red = attention.
/// </summary>
public static class CampfirePalette
{
    /// <summary>Fog Night, the base <c>#0F1619</c>.</summary>
    public static readonly Color NightColor = Rgb(0x0F, 0x16, 0x19);

    /// <summary>Title bar / wells <c>#0B1114</c>.</summary>
    public static readonly Color NightDeepColor = Rgb(0x0B, 0x11, 0x14);

    /// <summary>Damp Slate, panels <c>#1A2428</c>.</summary>
    public static readonly Color SlateColor = Rgb(0x1A, 0x24, 0x28);

    /// <summary>Mist, the fog layers <c>#4A5C63</c>.</summary>
    public static readonly Color MistColor = Rgb(0x4A, 0x5C, 0x63);

    /// <summary>Bone, primary text <c>#E3DCCD</c>.</summary>
    public static readonly Color BoneColor = Rgb(0xE3, 0xDC, 0xCD);

    /// <summary>Ash, secondary text <c>#97A0A2</c>.</summary>
    public static readonly Color AshColor = Rgb(0x97, 0xA0, 0xA2);

    /// <summary>Ember, live / confirmed <c>#F08A3C</c>.</summary>
    public static readonly Color EmberColor = Rgb(0xF0, 0x8A, 0x3C);

    /// <summary>Entity red, danger / restart <c>#B0182D</c>.</summary>
    public static readonly Color EntityColor = Rgb(0xB0, 0x18, 0x2D);

    /// <summary>Red text that still reads on the night <c>#F08C97</c>.</summary>
    public static readonly Color EntityInkColor = Rgb(0xF0, 0x8C, 0x97);

    /// <summary>The skill-check needle red <c>#E8283F</c>.</summary>
    public static readonly Color NeedleColor = Rgb(0xE8, 0x28, 0x3F);

    /// <summary>Hairlines: ash at 18 %.</summary>
    public static readonly Color LineColor = Argb(0.18, AshColor);

    /// <summary>Strong hairlines (rules, picture frames, button outlines): bone at 28 %.</summary>
    public static readonly Color LineStrongColor = Argb(0.28, BoneColor);

    /// <summary>
    /// The boundary of a control whose outline is what identifies it and its state (a check box's box, a text field, a
    /// combo box): bone at 45 %, at least 3:1 against the night, the fields and the panels (WCAG 1.4.11). The 28 %
    /// <see cref="LineStrongColor"/> stays for rules and decorative frames.
    /// </summary>
    public static readonly Color ControlLineColor = Argb(0.45, BoneColor);

    /// <summary>Panels over the fog: rgba(20,29,33,.9).</summary>
    public static readonly Color PanelColor = Argb(0.9, Rgb(0x14, 0x1D, 0x21));

    /// <summary>Input wells: rgba(11,17,20,.78).</summary>
    public static readonly Color FieldColor = Argb(0.78, NightDeepColor);

    /// <summary>Placeholder text <c>#7E888A</c>.</summary>
    public static readonly Color PlaceholderColor = Rgb(0x7E, 0x88, 0x8A);

    /// <summary>Hover wash: bone at 8 %.</summary>
    public static readonly Color HoverColor = Argb(0.08, BoneColor);

    /// <summary>Selected wash: bone at 9 %.</summary>
    public static readonly Color SelectedColor = Argb(0.09, BoneColor);

    /// <summary>Danger hover <c>#C51E35</c>.</summary>
    public static readonly Color EntityHoverColor = Rgb(0xC5, 0x1E, 0x35);

    /// <summary>Primary hover <c>#F4EEE2</c>.</summary>
    public static readonly Color BoneHoverColor = Rgb(0xF4, 0xEE, 0xE2);

    /// <summary>Brush for <see cref="NightColor"/>.</summary>
    public static readonly SolidColorBrush Night = Brush(NightColor);

    /// <summary>Brush for <see cref="NightDeepColor"/>.</summary>
    public static readonly SolidColorBrush NightDeep = Brush(NightDeepColor);

    /// <summary>Brush for <see cref="SlateColor"/>.</summary>
    public static readonly SolidColorBrush Slate = Brush(SlateColor);

    /// <summary>Brush for <see cref="MistColor"/>.</summary>
    public static readonly SolidColorBrush Mist = Brush(MistColor);

    /// <summary>Brush for <see cref="BoneColor"/>.</summary>
    public static readonly SolidColorBrush Bone = Brush(BoneColor);

    /// <summary>Brush for <see cref="AshColor"/>.</summary>
    public static readonly SolidColorBrush Ash = Brush(AshColor);

    /// <summary>Brush for <see cref="EmberColor"/>.</summary>
    public static readonly SolidColorBrush Ember = Brush(EmberColor);

    /// <summary>Brush for <see cref="EntityColor"/>.</summary>
    public static readonly SolidColorBrush Entity = Brush(EntityColor);

    /// <summary>Brush for <see cref="EntityInkColor"/>.</summary>
    public static readonly SolidColorBrush EntityInk = Brush(EntityInkColor);

    /// <summary>Brush for <see cref="NeedleColor"/>.</summary>
    public static readonly SolidColorBrush Needle = Brush(NeedleColor);

    /// <summary>Brush for <see cref="LineColor"/>.</summary>
    public static readonly SolidColorBrush Line = Brush(LineColor);

    /// <summary>Brush for <see cref="LineStrongColor"/>.</summary>
    public static readonly SolidColorBrush LineStrong = Brush(LineStrongColor);

    /// <summary>Brush for <see cref="ControlLineColor"/>.</summary>
    public static readonly SolidColorBrush ControlLine = Brush(ControlLineColor);

    /// <summary>Brush for <see cref="PanelColor"/>.</summary>
    public static readonly SolidColorBrush Panel = Brush(PanelColor);

    /// <summary>Brush for <see cref="FieldColor"/>.</summary>
    public static readonly SolidColorBrush Field = Brush(FieldColor);

    /// <summary>Pure white (skill-check zones, flash).</summary>
    public static readonly SolidColorBrush White = Brush(Colors.White);

    /// <summary>A colour from 0..255 channels.</summary>
    public static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary><paramref name="color"/> with <paramref name="alpha"/> (0..1) opacity, like CSS rgba().</summary>
    public static Color Argb(double alpha, Color color) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    /// <summary>A frozen solid brush.</summary>
    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>A frozen solid brush of <paramref name="color"/> at <paramref name="alpha"/>.</summary>
    public static SolidColorBrush Brush(double alpha, Color color) => Brush(Argb(alpha, color));

    /// <summary>A frozen radial gradient (CSS <c>radial-gradient(rx ry at cx cy, ...)</c>, all relative to the box).</summary>
    public static RadialGradientBrush Radial(double cx, double cy, double rx, double ry, params (double Offset, Color Color)[] stops)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(cx, cy),
            GradientOrigin = new Point(cx, cy),
            RadiusX = rx,
            RadiusY = ry,
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        foreach ((double offset, Color color) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }

        brush.Freeze();
        return brush;
    }

    /// <summary>A frozen linear gradient from <paramref name="start"/> to <paramref name="end"/> (relative to the box).</summary>
    public static LinearGradientBrush Linear(Point start, Point end, params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach ((double offset, Color color) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }

        brush.Freeze();
        return brush;
    }
}
