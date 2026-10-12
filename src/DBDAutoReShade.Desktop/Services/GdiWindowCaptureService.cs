using System.Runtime.InteropServices;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Interop;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>External window capture. Screen DC BitBlt first, PrintWindow fallback.</summary>
public interface IWindowCaptureService
{
    CapturedImage? CaptureClientArea(IntPtr hwnd);

    CapturedImage? CaptureRegion(IntPtr hwnd, IntRegion region);

    /// <summary>
    /// Capture several client-area regions of the same frame tick. The result
    /// has one entry per requested region (null where that capture failed);
    /// the caller owns and disposes every returned image. The default
    /// implementation captures each region with <see cref="CaptureRegion"/>;
    /// implementations may share expensive work (e.g. one full-client
    /// fallback capture) across the regions.
    /// </summary>
    IReadOnlyList<CapturedImage?> CaptureRegions(IntPtr hwnd, IReadOnlyList<IntRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var results = new CapturedImage?[regions.Count];
        try
        {
            for (int i = 0; i < regions.Count; i++)
            {
                results[i] = CaptureRegion(hwnd, regions[i]);
            }
        }
        catch
        {
            foreach (CapturedImage? image in results)
            {
                image?.Dispose();
            }

            throw;
        }

        return results;
    }
}

/// <summary>
/// GDI capture of the live DBD client area. All native handles are released
/// per capture; pixel data is plain managed memory (see <see cref="CapturedImage"/>).
/// Only the small OCR regions are captured during detection — never full frames at high rate.
/// A screen BitBlt that fails, or that returns a frame whose pixels are all
/// exactly black (typical of exclusive fullscreen / flip-model presentation),
/// falls back to PrintWindow with full-content rendering; when that fallback is
/// needed for several regions of one tick, the full client is captured once.
/// </summary>
public sealed class GdiWindowCaptureService : IWindowCaptureService
{
    /// <summary>PrintWindow: render DirectComposition / DirectX content too (Windows 8.1+).</summary>
    internal const uint PW_RENDERFULLCONTENT = 0x00000002;

    /// <summary>Flags for the PrintWindow fallback: client area only, full-content rendering.</summary>
    internal const uint PrintWindowFlags = NativeMethods.PW_CLIENTONLY | PW_RENDERFULLCONTENT;

    public CapturedImage? CaptureClientArea(IntPtr hwnd)
    {
        if (!DbdWindowService.TryGetClientBounds(hwnd, out var b) || b.IsEmpty)
        {
            return null;
        }

        return SelectCapture(
            CaptureScreenRect(b.ScreenX, b.ScreenY, b.Width, b.Height),
            () => CaptureViaPrintWindow(hwnd, new IntRegion(0, 0, b.Width, b.Height)));
    }

    public CapturedImage? CaptureRegion(IntPtr hwnd, IntRegion region)
    {
        return CaptureRegions(hwnd, new[] { region })[0];
    }

    public IReadOnlyList<CapturedImage?> CaptureRegions(IntPtr hwnd, IReadOnlyList<IntRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var results = new CapturedImage?[regions.Count];
        if (regions.Count == 0 || !DbdWindowService.TryGetClientBounds(hwnd, out var b) || b.IsEmpty)
        {
            return results;
        }

        // The PrintWindow fallback renders the whole client; do it at most once
        // per call and crop every region that needs it from that one frame.
        CapturedImage? fullClient = null;
        bool fullClientAttempted = false;
        try
        {
            for (int i = 0; i < regions.Count; i++)
            {
                IntRegion r = ClampToClient(regions[i], b.Width, b.Height);
                results[i] = SelectCapture(
                    CaptureScreenRect(b.ScreenX + r.X, b.ScreenY + r.Y, r.Width, r.Height),
                    () =>
                    {
                        if (!fullClientAttempted)
                        {
                            fullClientAttempted = true;
                            fullClient = CaptureViaPrintWindow(hwnd, new IntRegion(0, 0, b.Width, b.Height));
                        }

                        return fullClient is null ? null : CropCopy(fullClient, r);
                    });
            }
        }
        catch
        {
            foreach (CapturedImage? image in results)
            {
                image?.Dispose();
            }

            throw;
        }
        finally
        {
            fullClient?.Dispose();
        }

        return results;
    }

    /// <summary>
    /// Pick between the screen capture and the PrintWindow fallback. The
    /// fallback runs only when the screen capture failed (null) or is entirely
    /// black (B=G=R=0 everywhere). If the fallback is unavailable or also all
    /// black, the screen image is returned as is (a genuinely black frame).
    /// Whichever image is not returned is disposed.
    /// </summary>
    internal static CapturedImage? SelectCapture(CapturedImage? screen, Func<CapturedImage?> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (screen is not null && !IsAllBlack(screen))
        {
            return screen;
        }

        CapturedImage? alternative;
        try
        {
            alternative = fallback();
        }
        catch
        {
            alternative = null;
        }

        if (alternative is null)
        {
            return screen;
        }

        if (screen is null || !IsAllBlack(alternative))
        {
            screen?.Dispose();
            return alternative;
        }

        alternative.Dispose();
        return screen;
    }

    /// <summary>True when every pixel's B, G and R are exactly zero (alpha is ignored).</summary>
    public static bool IsAllBlack(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return IsAllBlack(image.Pixels);
    }

    /// <summary>True when every 4-byte BGRA pixel has B=G=R=0 (alpha is ignored).</summary>
    internal static bool IsAllBlack(ReadOnlySpan<byte> bgra)
    {
        ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(bgra);
        // BGRA in memory is 0xAARRGGBB as a little-endian uint.
        uint colorMask = BitConverter.IsLittleEndian ? 0x00FFFFFFu : 0xFFFFFF00u;
        foreach (uint pixel in pixels)
        {
            if ((pixel & colorMask) != 0)
            {
                return false;
            }
        }

        // Trailing bytes that do not form a whole pixel (never produced by CapturedImage).
        for (int i = pixels.Length * 4; i < bgra.Length; i++)
        {
            if ((i & 3) != 3 && bgra[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static IntRegion ClampToClient(IntRegion region, int clientWidth, int clientHeight)
    {
        int x = Math.Clamp(region.X, 0, clientWidth - 1);
        int y = Math.Clamp(region.Y, 0, clientHeight - 1);
        int w = Math.Clamp(region.Width, 1, clientWidth - x);
        int h = Math.Clamp(region.Height, 1, clientHeight - y);
        return new IntRegion(x, y, w, h);
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

            // GDI batches drawing calls: flush before reading the DIB section bits directly.
            CaptureNative.GdiFlush();

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
        if (fullClient.Width <= 0 || fullClient.Height <= 0 || fullClient.Width > 16384 || fullClient.Height > 16384)
        {
            return null;
        }

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
            if (!NativeMethods.PrintWindow(hwnd, hdcMem, PrintWindowFlags))
            {
                return null;
            }

            CaptureNative.GdiFlush();

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

    /// <summary>Crop <paramref name="region"/> (clamped) out of <paramref name="source"/> and dispose the source.</summary>
    internal static CapturedImage? Crop(CapturedImage? source, IntRegion region)
    {
        if (source is null)
        {
            return null;
        }

        using (source)
        {
            return CropCopy(source, region);
        }
    }

    /// <summary>Copy <paramref name="region"/> (clamped) out of <paramref name="source"/>, leaving the source intact.</summary>
    internal static CapturedImage CropCopy(CapturedImage source, IntRegion region)
    {
        ArgumentNullException.ThrowIfNull(source);
        IntRegion r = ClampToClient(region, source.Width, source.Height);

        var out_ = new byte[r.Width * r.Height * 4];
        for (int row = 0; row < r.Height; row++)
        {
            Buffer.BlockCopy(source.Pixels, ((r.Y + row) * source.Width + r.X) * 4, out_, row * r.Width * 4, r.Width * 4);
        }

        return new CapturedImage(r.Width, r.Height, out_);
    }

    private static void ForceOpaque(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 255;
        }
    }

    /// <summary>
    /// GDI entry points used only by this capture path. Declared here because
    /// the shared <see cref="NativeMethods"/> table is maintained separately;
    /// read-only GDI calls on our own memory DC, never game memory.
    /// </summary>
    private static class CaptureNative
    {
        [DllImport("gdi32.dll")]
        public static extern bool GdiFlush();
    }
}
