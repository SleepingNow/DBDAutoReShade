using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DBDAutoReShade.App.Infrastructure;

/// <summary>A window's place and size, and the minimum size that goes with it (see <see cref="WindowPlacement.Fit"/>).</summary>
/// <param name="Bounds">Left, top, width and height in DIPs.</param>
/// <param name="Minimum">The minimum width and height (never more than the work area).</param>
public readonly record struct WindowFit(Rect Bounds, Size Minimum);

/// <summary>
/// Keeps the custom-chrome windows on their monitor. WPF's CenterScreen does not clamp: a window taller than the
/// monitor's work area (1280 x 800 on a 1366 x 768 laptop, or on a 1080p screen at 150 %) would open with its top above
/// the screen, and with it the 36 px title bar, the only drag area and the home of the window buttons. A window that
/// opens centred is therefore fitted to its monitor's work area. Maximized, a chromeless window with a resize frame
/// overhangs the monitor by that frame plus the padded border; <see cref="MaximizedInset(Window)"/> is the padding that
/// brings its edges back on screen.
/// </summary>
public static class WindowPlacement
{
    private const int SmCxFrame = 32;
    private const int SmCyFrame = 33;
    private const int SmCxPaddedBorder = 92;
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>
    /// Fits <paramref name="bounds"/> into <paramref name="workArea"/>: a size that fits is kept (the design size when the
    /// screen has room), a larger one shrinks to the work area and is centred on that axis again; the position is then
    /// clamped so the whole window, title bar first, is on the work area. The minimum size never exceeds the work area.
    /// </summary>
    public static WindowFit Fit(Rect workArea, Rect bounds, Size minimum)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0)
        {
            return new WindowFit(bounds, minimum);
        }

        double minWidth = Math.Min(minimum.Width, workArea.Width);
        double minHeight = Math.Min(minimum.Height, workArea.Height);
        double width = Math.Max(minWidth, Math.Min(bounds.Width, workArea.Width));
        double height = Math.Max(minHeight, Math.Min(bounds.Height, workArea.Height));
        double left = width < bounds.Width ? workArea.Left + (workArea.Width - width) / 2 : bounds.Left;
        double top = height < bounds.Height ? workArea.Top + (workArea.Height - height) / 2 : bounds.Top;
        if (double.IsNaN(left))
        {
            left = workArea.Left + (workArea.Width - width) / 2;
        }

        if (double.IsNaN(top))
        {
            top = workArea.Top + (workArea.Height - height) / 2;
        }

        left = Math.Clamp(left, workArea.Left, workArea.Right - width);
        top = Math.Clamp(top, workArea.Top, workArea.Bottom - height);
        return new WindowFit(new Rect(left, top, width, height), new Size(minWidth, minHeight));
    }

    /// <summary>
    /// Fits <paramref name="window"/> into <paramref name="workArea"/> (DIPs): see <see cref="Fit"/>. A size that was
    /// never set (NaN) counts as the window's minimum.
    /// </summary>
    public static void FitTo(Window window, Rect workArea)
    {
        ArgumentNullException.ThrowIfNull(window);
        double width = double.IsNaN(window.Width) ? Math.Max(window.MinWidth, window.ActualWidth) : window.Width;
        double height = double.IsNaN(window.Height) ? Math.Max(window.MinHeight, window.ActualHeight) : window.Height;
        WindowFit fit = Fit(workArea, new Rect(window.Left, window.Top, width, height), new Size(window.MinWidth, window.MinHeight));
        window.MinWidth = fit.Minimum.Width;
        window.MinHeight = fit.Minimum.Height;
        window.Width = fit.Bounds.Width;
        window.Height = fit.Bounds.Height;
        window.Left = fit.Bounds.Left;
        window.Top = fit.Bounds.Top;
    }

    /// <summary>
    /// Fits a window that opens centred (<see cref="WindowStartupLocation.CenterScreen"/>) into the work area of the
    /// monitor it is on. Call it from SourceInitialized (the window has its handle and its place, and is not shown yet).
    /// A window placed by hand (a position given, e.g. far off screen for a snapshot) is left alone.
    /// </summary>
    public static void FitCenteredWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.WindowStartupLocation != WindowStartupLocation.CenterScreen || WorkAreaOf(window) is not { } workArea)
        {
            return;
        }

        FitTo(window, workArea);
    }

    /// <summary>The work area (DIPs, the window's DPI) of the monitor <paramref name="window"/> is on; null without a handle.</summary>
    public static Rect? WorkAreaOf(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                return null;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1, sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
            NativeRect work = info.Work;
            return new Rect(work.Left / sx, work.Top / sy, (work.Right - work.Left) / sx, (work.Bottom - work.Top) / sy);
        }
        catch (Exception)
        {
            return null; // best effort: the window keeps WPF's place
        }
    }

    /// <summary>
    /// The padding (DIPs) that brings a maximized chromeless window's edges back on screen: the resize frame plus the
    /// padded border, in physical pixels at the window's DPI (<c>SM_CXFRAME + SM_CXPADDEDBORDER</c>, 4 + 4 at 100 %).
    /// </summary>
    public static Thickness MaximizedInset(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        DpiScale dpi;
        try
        {
            dpi = VisualTreeHelper.GetDpi(window);
        }
        catch (Exception)
        {
            dpi = new DpiScale(1, 1);
        }

        uint dpiValue = (uint)Math.Round(96 * (dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1));
        int frameX = Metric(SmCxFrame, dpiValue), frameY = Metric(SmCyFrame, dpiValue), padded = Metric(SmCxPaddedBorder, dpiValue);
        if (frameX <= 0 || frameY <= 0)
        {
            // no metrics: WPF's own (the resize frame only, at the system DPI)
            Thickness frame = SystemParameters.WindowResizeBorderThickness;
            return new Thickness(frame.Left + padded / Math.Max(1, dpi.DpiScaleX), frame.Top + padded / Math.Max(1, dpi.DpiScaleY),
                frame.Right + padded / Math.Max(1, dpi.DpiScaleX), frame.Bottom + padded / Math.Max(1, dpi.DpiScaleY));
        }

        return MaximizedInset(frameX, frameY, padded, dpi.DpiScaleX, dpi.DpiScaleY);
    }

    /// <summary>The maximized padding for a resize frame of <paramref name="frameX"/> x <paramref name="frameY"/> and a padded border of <paramref name="paddedBorder"/> physical pixels.</summary>
    public static Thickness MaximizedInset(int frameX, int frameY, int paddedBorder, double dpiScaleX, double dpiScaleY)
    {
        double sx = dpiScaleX > 0 ? dpiScaleX : 1, sy = dpiScaleY > 0 ? dpiScaleY : 1;
        double x = Math.Max(0, frameX + Math.Max(0, paddedBorder)) / sx;
        double y = Math.Max(0, frameY + Math.Max(0, paddedBorder)) / sy;
        return new Thickness(x, y, x, y);
    }

    /// <summary>A system metric for <paramref name="dpi"/>; 0 when it can't be read.</summary>
    public static int Metric(int index, uint dpi)
    {
        try
        {
            int value = GetSystemMetricsForDpi(index, dpi);
            return value > 0 ? value : GetSystemMetrics(index);
        }
        catch (Exception)
        {
            try
            {
                return GetSystemMetrics(index);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
