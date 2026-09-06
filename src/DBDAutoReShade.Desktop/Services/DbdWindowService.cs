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
}
