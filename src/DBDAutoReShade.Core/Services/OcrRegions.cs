namespace DBDAutoReShade.Core.Services;

/// <summary>Pixel rectangle with integer coordinates.</summary>
public readonly record struct IntRegion(int X, int Y, int Width, int Height);

/// <summary>Normalized (0..1) rectangle relative to the DBD client area.</summary>
public readonly record struct NormalizedRegion(double X, double Y, double Width, double Height);

/// <summary>
/// Loading-screen OCR regions as normalized rectangles so the same relative
/// area scales across 1080p / 1440p / 4K / ultrawide / windowed modes.
/// Never hard-code absolute pixels; never assume the primary monitor.
///
/// Assumption (tunable): DBD shows the map name large with the realm name
/// beneath it in the lower-left area of the loading screen. Detection OCRs
/// exactly two small regions per sample — <see cref="MapName"/> and
/// <see cref="RealmName"/> — never the full frame.
/// </summary>
public static class DbdLoadingScreenRegions
{
    /// <summary>Expected map-name text area (lower-left, large type).</summary>
    public static readonly NormalizedRegion MapName = new(0.03, 0.70, 0.52, 0.16);

    /// <summary>Expected realm-name text area (below the map name, smaller type).</summary>
    public static readonly NormalizedRegion RealmName = new(0.03, 0.84, 0.52, 0.12);

    /// <summary>
    /// Wider rectangle covering both lines. Not OCR'd and not used by detection
    /// or Debug Mode (which saves the map and realm crops actually OCR'd);
    /// kept as a reference area for tuning tools.
    /// </summary>
    public static readonly NormalizedRegion Combined = new(0.02, 0.66, 0.62, 0.30);

    /// <summary>Scale a normalized region to client pixels, clamped inside the client area.</summary>
    public static IntRegion ToPixels(NormalizedRegion region, int clientWidth, int clientHeight)
    {
        if (clientWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientWidth));
        }

        if (clientHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientHeight));
        }

        int x = (int)Math.Floor(region.X * clientWidth);
        int y = (int)Math.Floor(region.Y * clientHeight);
        int w = (int)Math.Ceiling(region.Width * clientWidth);
        int h = (int)Math.Ceiling(region.Height * clientHeight);

        x = Math.Clamp(x, 0, clientWidth - 1);
        y = Math.Clamp(y, 0, clientHeight - 1);
        w = Math.Clamp(w, 1, clientWidth - x);
        h = Math.Clamp(h, 1, clientHeight - y);
        return new IntRegion(x, y, w, h);
    }
}
