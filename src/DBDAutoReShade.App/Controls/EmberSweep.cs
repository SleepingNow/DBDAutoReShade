using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.App.Controls;

/// <summary>
/// The ember confirm's strokes (the prototype's emberSweep): an ember ignites at the bottom-left corner of a frame and
/// runs both ways round it (up the left edge and along the top; along the bottom and up the right edge); where the two
/// flames meet, the frame is lit. Each stroke is a blurred heat line under a bright core (WPF: Paths whose
/// StrokeDashOffset runs from the path's length to 0). Used by the Status map plate and the Presets hero picture.
/// </summary>
internal static class EmberSweep
{
    private static readonly Color Ember = CampfirePalette.EmberColor;

    /// <summary>
    /// Draws the two strokes into <paramref name="host"/> (an empty-looking Canvas laid over a frame of
    /// <paramref name="width"/> x <paramref name="height"/>, resting at opacity 0) and adds their run
    /// (<paramref name="ms"/>, ease-in-out) and the host's fade (420 ms from <paramref name="ms"/> − 40) to
    /// <paramref name="moment"/>; the strokes are removed when the moment ends. False when the frame is too small to draw.
    /// </summary>
    public static bool Add(MotionMoment moment, Canvas host, double width, double height, double ms)
    {
        ArgumentNullException.ThrowIfNull(moment);
        ArgumentNullException.ThrowIfNull(host);
        if (width <= 2 || height <= 2)
        {
            return false;
        }

        double x = 0.75, y = 0.75, sw = width - 1.5, sh = height - 1.5;
        string d1 = FormattableString.Invariant($"M{x},{y + sh} V{y} H{x + sw}");
        string d2 = FormattableString.Invariant($"M{x},{y + sh} H{x + sw} V{y}");
        double length = sw + sh;
        var heatBlur = new BlurEffect { Radius = 7.5 };
        heatBlur.Freeze();
        var heat = new Canvas { Effect = heatBlur };
        var core = new Canvas();
        foreach (string d in new[] { d1, d2 })
        {
            heat.Children.Add(Stroke(d, CampfirePalette.Brush(0.55, Ember), 8, length, moment, ms));
            core.Children.Add(Stroke(d, CampfirePalette.Brush(Color.FromRgb(0xFF, 0xC8, 0x90)), 2, length, moment, ms));
        }

        host.Children.Add(heat);
        host.Children.Add(core);
        moment.Add(host, UIElement.OpacityProperty, Anim.Keys(0, new[] { new Kf(0, 1), new Kf(ms - 40, 1), new Kf(ms + 380, 0, Ease.Out) }));
        moment.Ended += (_, _) =>
        {
            // (only this sweep's strokes: a newer confirm may already be drawing its own)
            host.Children.Remove(heat);
            host.Children.Remove(core);
        };
        return true;
    }

    private static Path Stroke(string data, Brush stroke, double thickness, double length, MotionMoment moment, double ms)
    {
        double units = length / thickness;
        var path = new Path
        {
            Data = SkillCheckMath.Frozen(data),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeDashArray = new DoubleCollection { units, units },
            StrokeDashOffset = 0,
        };
        moment.Add(path, Shape.StrokeDashOffsetProperty, Anim.FromTo(units, 0, ms, Ease.InOut));
        return path;
    }
}
