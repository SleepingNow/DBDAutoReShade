using System.Runtime.InteropServices;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Interop;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>External window capture. Screen DC BitBlt first, PrintWindow fallback.</summary>
public interface IWindowCaptureService
{
    CapturedImage? CaptureClientArea(IntPtr hwnd);
    CapturedImage? CaptureRegion(IntPtr hwnd, IntRegion region);
}

/// <summary>
/// GDI capture of the live DBD client area. All native handles are released
/// per capture; pixel data is plain managed memory (see <see cref="CapturedImage"/>).
/// Only the small OCR regions are captured during detection — never full frames at high rate.
/// </summary>
public sealed class GdiWindowCaptureService : IWindowCaptureService
{
    public CapturedImage? CaptureClientArea(IntPtr hwnd)
    {
        if (!DbdWindowService.TryGetClientBounds(hwnd, out var b) || b.IsEmpty)
        {
            return null;
        }

        return CaptureScreenRect(b.ScreenX, b.ScreenY, b.Width, b.Height)
            ?? CaptureViaPrintWindow(hwnd, new IntRegion(0, 0, b.Width, b.Height));
    }

    public CapturedImage? CaptureRegion(IntPtr hwnd, IntRegion region)
    {
        if (!DbdWindowService.TryGetClientBounds(hwnd, out var b) || b.IsEmpty)
        {
            return null;
        }

        // Clamp the requested region into the client area.
        int x = Math.Clamp(region.X, 0, b.Width - 1);
        int y = Math.Clamp(region.Y, 0, b.Height - 1);
        int w = Math.Clamp(region.Width, 1, b.Width - x);
        int h = Math.Clamp(region.Height, 1, b.Height - y);

        return CaptureScreenRect(b.ScreenX + x, b.ScreenY + y, w, h)
            ?? Crop(CaptureViaPrintWindow(hwnd, new IntRegion(0, 0, b.Width, b.Height)), new IntRegion(x, y, w, h));
    }

    private static CapturedImage? CaptureScreenRect(int screenX, int screenY, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
        {
            return null;
        }

        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBmp = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        IntPtr bits = IntPtr.Zero;
        try
        {
            hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
            {
                return null;
            }

            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
            {
                return null;
            }

            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB
            };

            hBmp = NativeMethods.CreateDIBSection(hdcMem, ref header, NativeMethods.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (hBmp == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            oldObj = NativeMethods.SelectObject(hdcMem, hBmp);
            if (!NativeMethods.BitBlt(hdcMem, 0, 0, width, height, hdcScreen, screenX, screenY, NativeMethods.SRCCOPY))
            {
                return null;
            }

            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            ForceOpaque(pixels);
            return new CapturedImage(width, height, pixels);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hdcMem != IntPtr.Zero)
            {
                if (oldObj != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(hdcMem, oldObj);
                }

                NativeMethods.DeleteDC(hdcMem);
            }

            if (hBmp != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(hBmp);
            }

            if (hdcScreen != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }
    }

    private static CapturedImage? CaptureViaPrintWindow(IntPtr hwnd, IntRegion fullClient)
    {
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBmp = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        IntPtr bits = IntPtr.Zero;
        try
        {
            IntPtr hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
            }

            if (hdcMem == IntPtr.Zero)
            {
                return null;
            }

            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = fullClient.Width,
                biHeight = -fullClient.Height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB
            };

            hBmp = NativeMethods.CreateDIBSection(hdcMem, ref header, NativeMethods.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (hBmp == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            oldObj = NativeMethods.SelectObject(hdcMem, hBmp);
            if (!NativeMethods.PrintWindow(hwnd, hdcMem, NativeMethods.PW_CLIENTONLY))
            {
                return null;
            }

            var pixels = new byte[fullClient.Width * fullClient.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            ForceOpaque(pixels);
            return new CapturedImage(fullClient.Width, fullClient.Height, pixels);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hdcMem != IntPtr.Zero)
            {
                if (oldObj != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(hdcMem, oldObj);
                }

                NativeMethods.DeleteDC(hdcMem);
            }

            if (hBmp != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(hBmp);
            }
        }
    }

    internal static CapturedImage? Crop(CapturedImage? source, IntRegion region)
    {
        if (source is null)
        {
            return null;
        }

        using (source)
        {
            int x = Math.Clamp(region.X, 0, source.Width - 1);
            int y = Math.Clamp(region.Y, 0, source.Height - 1);
            int w = Math.Clamp(region.Width, 1, source.Width - x);
            int h = Math.Clamp(region.Height, 1, source.Height - y);

            var out_ = new byte[w * h * 4];
            for (int row = 0; row < h; row++)
            {
                Buffer.BlockCopy(source.Pixels, ((y + row) * source.Width + x) * 4, out_, row * w * 4, w * 4);
            }

            return new CapturedImage(w, h, out_);
        }
    }

    private static void ForceOpaque(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 255;
        }
    }
}
