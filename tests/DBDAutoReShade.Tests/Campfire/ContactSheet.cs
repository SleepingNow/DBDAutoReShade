using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DBDAutoReShade.App.Media;

namespace DBDAutoReShade.Tests.Campfire;

/// <summary>
/// Frames rendered side by side with their labels under a title (a contact sheet laid out like the prototype's
/// <c>sheet-*.png</c>), saved as <c>sheet-&lt;name&gt;.png</c> in the snapshot folder (<c>DBD_SNAPSHOTS</c>).
/// Every frame is rendered off screen with RenderTargetBitmap.
/// </summary>
internal sealed class ContactSheet
{
    private readonly List<(BitmapSource Image, string Label)> _frames = new();

    /// <summary>The snapshot folder (<c>DBD_SNAPSHOTS</c>, else the temp folder).</summary>
    public static string OutDir => Environment.GetEnvironmentVariable("DBD_SNAPSHOTS") ?? Path.GetTempPath();

    /// <summary>Number of frames added.</summary>
    public int Count => _frames.Count;

    /// <summary>Renders <paramref name="element"/> (laid out at its size, over the night colour) as the next frame.</summary>
    public void Add(FrameworkElement element, string label, double scale = 1)
    {
        _frames.Add((Render(element, scale), label));
    }

    /// <summary>
    /// Renders <paramref name="root"/> (e.g. the whole window, so whatever lies behind <paramref name="part"/> shows too)
    /// and keeps the region of <paramref name="part"/> grown by <paramref name="pad"/> as the next frame.
    /// </summary>
    public void AddRegion(FrameworkElement root, FrameworkElement part, string label, double scale = 1, double pad = 0)
    {
        BitmapSource whole = Render(root, scale);
        Rect bounds = part.TransformToAncestor(root).TransformBounds(new Rect(0, 0, part.ActualWidth, part.ActualHeight));
        bounds.Inflate(pad, pad);
        int x = (int)Math.Clamp(Math.Floor(bounds.X * scale), 0, whole.PixelWidth - 1);
        int y = (int)Math.Clamp(Math.Floor(bounds.Y * scale), 0, whole.PixelHeight - 1);
        int w = (int)Math.Clamp(Math.Ceiling(bounds.Width * scale), 1, whole.PixelWidth - x);
        int h = (int)Math.Clamp(Math.Ceiling(bounds.Height * scale), 1, whole.PixelHeight - y);
        var crop = new CroppedBitmap(whole, new Int32Rect(x, y, w, h));
        crop.Freeze();
        _frames.Add((crop, label));
    }

    /// <summary>Lays the frames out in <paramref name="columns"/> columns under <paramref name="title"/> and saves the sheet.</summary>
    public void Save(string name, int columns, string title)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        // (laid out in pixels: a frame rendered at a scale has its DPI scaled too, so its DIP size is the unscaled one)
        double cellW = _frames.Max(f => f.Image.PixelWidth), cellH = _frames.Max(f => f.Image.PixelHeight);
        const double gap = 16, labelH = 28, titleH = 44;
        int rows = (int)Math.Ceiling(_frames.Count / (double)columns);
        double width = gap + columns * (cellW + gap), height = titleH + rows * (cellH + labelH + gap) + gap;
        var visual = new DrawingVisual();
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)), null, new Rect(0, 0, width, height));
            dc.DrawText(Text(title, typeface, 18, width - 2 * gap), new Point(gap, 12));
            for (int i = 0; i < _frames.Count; i++)
            {
                double x = gap + i % columns * (cellW + gap);
                double y = titleH + i / columns * (cellH + labelH + gap);
                dc.DrawImage(_frames[i].Image, new Rect(x, y, _frames[i].Image.PixelWidth, _frames[i].Image.PixelHeight));
                dc.DrawText(Text(_frames[i].Label, typeface, 14, cellW), new Point(x, y + cellH + 4));
            }
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(OutDir);
        using FileStream stream = File.Create(Path.Combine(OutDir, "sheet-" + name + ".png"));
        encoder.Save(stream);
    }

    private static BitmapSource Render(FrameworkElement element, double scale)
    {
        element.UpdateLayout();
        double w = Math.Max(1, element.ActualWidth), h = Math.Max(1, element.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(CampfirePalette.Night, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
        }

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static FormattedText Text(string text, Typeface typeface, double size, double maxWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.White, 1.0)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return formatted;
    }
}
