using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Interop;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>Client bounds of the live DBD window in physical screen pixels.</summary>
public sealed record DbdClientBounds(int ScreenX, int ScreenY, int Width, int Height, uint Dpi)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Resolves the actual DBD game window geometry: client area in screen
/// coordinates (DPI/physical pixels, correct monitor automatically because
/// ClientToScreen returns the window's real location).
/// </summary>
public static class DbdWindowService
{
    public static bool TryGetClientBounds(IntPtr hwnd, out DbdClientBounds bounds)
    {
        bounds = new DbdClientBounds(0, 0, 0, 0, 96);
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        try
        {
            if (!NativeMethods.GetClientRect(hwnd, out var rc) || rc.Width <= 0 || rc.Height <= 0)
            {
                return false;
            }

            var origin = new NativeMethods.POINT { X = rc.Left, Y = rc.Top };
            if (!NativeMethods.ClientToScreen(hwnd, ref origin))
            {
                return false;
            }

            uint dpi = 96;
            try
            {
                uint d = NativeMethods.GetDpiForWindow(hwnd);
                if (d >= 96)
                {
                    dpi = d;
                }
            }
            catch
            {
                // Pre-Win10-1607: assume 96.
            }

            bounds = new DbdClientBounds(origin.X, origin.Y, rc.Width, rc.Height, dpi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsWindowUsable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        try
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            {
                return false;
            }

            return TryGetClientBounds(hwnd, out var b) && !b.IsEmpty;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return NativeMethods.GetForegroundWindow() == hwnd;
        }
        catch
        {
            return false;
        }
    }

    // Probe grid inside each OCR region (fractions of its width and height).
    // The outer columns and rows sit inside the region, so a taskbar of usual
    // size along the bottom or left screen edge, which Windows shows over a
    // DBD that is not in front, does not count as covering the text.
    private static readonly double[] ProbeColumns = { 0.05, 0.15, 0.25, 0.35, 0.45, 0.55, 0.65, 0.75, 0.85, 0.95 };
    private static readonly double[] ProbeRows = { 0.2, 0.5, 0.8 };

    /// <summary>
    /// Whether the screen at DBD's loading-screen text (the map and realm OCR
    /// regions) shows DBD's own window, so a screen capture there reads DBD even
    /// while it is not the foreground window (e.g. DBD in plain view on one
    /// monitor while the user works on another). False when DBD is hidden,
    /// minimized or cloaked (e.g. on another virtual desktop), and when at any
    /// probe point inside either region the top-level window under the point is
    /// not DBD's (another window covers it, or the point is off screen). Never
    /// throws: false whenever it can't be vouched for. Click-through overlay
    /// windows are not seen by hit-testing; they are captured just the same
    /// when DBD is the foreground window.
    /// </summary>
    public static bool IsLoadingScreenTextUncovered(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd) ||
                NativeMethods.IsIconic(hwnd) || IsCloaked(hwnd) ||
                !TryGetClientBounds(hwnd, out DbdClientBounds bounds))
            {
                return false;
            }

            return AreLoadingScreenRegionsShowing(hwnd, bounds, TopLevelWindowAt);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The occlusion test behind <see cref="IsLoadingScreenTextUncovered"/>:
    /// every point of <see cref="LoadingScreenProbePoints"/> must hit
    /// <paramref name="hwnd"/> itself. <paramref name="topLevelWindowAt"/> maps a
    /// screen point to the top-level window under it (zero: none).
    /// </summary>
    internal static bool AreLoadingScreenRegionsShowing(
        IntPtr hwnd, DbdClientBounds bounds, Func<int, int, IntPtr> topLevelWindowAt)
    {
        ArgumentNullException.ThrowIfNull(topLevelWindowAt);
        if (hwnd == IntPtr.Zero || bounds.IsEmpty)
        {
            return false;
        }

        foreach ((int x, int y) in LoadingScreenProbePoints(bounds))
        {
            if (topLevelWindowAt(x, y) != hwnd)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Screen points spread over the map and realm OCR regions of a DBD client
    /// area: a 10 × 3 grid inside each region (empty for empty bounds).
    /// </summary>
    internal static IReadOnlyList<(int X, int Y)> LoadingScreenProbePoints(DbdClientBounds bounds)
    {
        var points = new List<(int X, int Y)>(2 * ProbeColumns.Length * ProbeRows.Length);
        if (bounds.IsEmpty)
        {
            return points;
        }

        foreach (NormalizedRegion region in new[] { DbdLoadingScreenRegions.MapName, DbdLoadingScreenRegions.RealmName })
        {
            IntRegion px = DbdLoadingScreenRegions.ToPixels(region, bounds.Width, bounds.Height);
            foreach (double fy in ProbeRows)
            {
                foreach (double fx in ProbeColumns)
                {
                    points.Add((bounds.ScreenX + px.X + (int)(px.Width * fx),
                                bounds.ScreenY + px.Y + (int)(px.Height * fy)));
                }
            }
        }

        return points;
    }

    private static IntPtr TopLevelWindowAt(int x, int y)
    {
        IntPtr hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        return hit == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
    }

    /// <summary>Whether DWM hides the window (a failed query counts as hidden: it can't be vouched for).</summary>
    private static bool IsCloaked(IntPtr hwnd) =>
        NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0
        || cloaked != 0;
}
