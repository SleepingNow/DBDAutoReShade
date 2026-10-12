using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>Pure parts of the GDI capture path: black-frame detection, fallback choice, cropping, multi-region capture.</summary>
public sealed class CaptureFallbackTests
{
    /// <summary>Opaque image filled with one BGR color; disposal is observable because it clears alpha.</summary>
    private static CapturedImage Solid(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = 255;
        }

        return new CapturedImage(w, h, px);
    }

    private static bool IsDisposed(CapturedImage image) => image.Pixels[3] == 0;

    [Fact]
    public void IsAllBlack_IgnoresAlpha_AndDetectsAnyColor()
    {
        Assert.True(GdiWindowCaptureService.IsAllBlack(new CapturedImage(4, 4, new byte[64])));
        Assert.True(GdiWindowCaptureService.IsAllBlack(Solid(4, 4, 0, 0, 0))); // alpha 255 only

        foreach (int channel in new[] { 0, 1, 2 })
        {
            using CapturedImage img = Solid(4, 4, 0, 0, 0);
            img.Pixels[img.Pixels.Length - 4 + channel] = 1; // last pixel, one channel
            Assert.False(GdiWindowCaptureService.IsAllBlack(img));
        }
    }

    [Fact]
    public void SelectCapture_UsesScreen_WhenItHasContent()
    {
        CapturedImage screen = Solid(2, 2, 10, 20, 30);
        bool fallbackCalled = false;

        CapturedImage? chosen = GdiWindowCaptureService.SelectCapture(screen, () =>
        {
            fallbackCalled = true;
            return Solid(2, 2, 1, 1, 1);
        });

        Assert.Same(screen, chosen);
        Assert.False(fallbackCalled);
    }

    [Fact]
    public void SelectCapture_BlackScreen_FallsBackToPrintWindow()
    {
        CapturedImage screen = Solid(2, 2, 0, 0, 0);
        CapturedImage printed = Solid(2, 2, 5, 6, 7);

        CapturedImage? chosen = GdiWindowCaptureService.SelectCapture(screen, () => printed);

        Assert.Same(printed, chosen);
        Assert.True(IsDisposed(screen));
    }

    [Fact]
    public void SelectCapture_BothBlack_ReturnsScreenImage()
    {
        CapturedImage screen = Solid(2, 2, 0, 0, 0);
        CapturedImage printed = Solid(2, 2, 0, 0, 0);

        CapturedImage? chosen = GdiWindowCaptureService.SelectCapture(screen, () => printed);

        Assert.Same(screen, chosen); // genuinely black frame
        Assert.False(IsDisposed(screen));
        Assert.True(IsDisposed(printed));
    }

    [Fact]
    public void SelectCapture_FailedScreen_UsesWhateverFallbackReturns()
    {
        CapturedImage black = Solid(2, 2, 0, 0, 0);
        Assert.Same(black, GdiWindowCaptureService.SelectCapture(null, () => black));
        Assert.Null(GdiWindowCaptureService.SelectCapture(null, () => null));
        Assert.Null(GdiWindowCaptureService.SelectCapture(null, () => throw new InvalidOperationException()));
    }

    [Fact]
    public void SelectCapture_FallbackThrows_KeepsScreen()
    {
        CapturedImage screen = Solid(2, 2, 0, 0, 0);
        Assert.Same(screen, GdiWindowCaptureService.SelectCapture(screen, () => throw new InvalidOperationException()));
    }

    [Fact]
    public void PrintWindowFallback_RendersClientWithFullContent()
    {
        Assert.Equal(0x3u, GdiWindowCaptureService.PrintWindowFlags); // PW_CLIENTONLY | PW_RENDERFULLCONTENT
    }

    [Fact]
    public void CropCopy_CopiesRegion_AndKeepsSource()
    {
        var px = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++)
        {
            px[i * 4] = (byte)i;
            px[i * 4 + 3] = 255;
        }

        using var source = new CapturedImage(4, 4, px);
        using CapturedImage crop = GdiWindowCaptureService.CropCopy(source, new IntRegion(1, 1, 2, 2));

        Assert.Equal(2, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal(new byte[] { 5, 6, 9, 10 }, Enumerable.Range(0, 4).Select(i => crop.Pixels[i * 4]).ToArray());
        Assert.Equal(255, source.Pixels[3]); // source untouched

        using CapturedImage clamped = GdiWindowCaptureService.CropCopy(source, new IntRegion(3, 3, 10, 10));
        Assert.Equal(1, clamped.Width);
        Assert.Equal(1, clamped.Height);
        Assert.Equal(15, clamped.Pixels[0]);
    }

    private sealed class RegionOnlyCapture : IWindowCaptureService
    {
        public List<IntRegion> Requested { get; } = new();
        public List<CapturedImage> Returned { get; } = new();
        public int FailOnCall { get; set; } = -1;

        public CapturedImage? CaptureClientArea(IntPtr hwnd) => null;

        public CapturedImage? CaptureRegion(IntPtr hwnd, IntRegion region)
        {
            Requested.Add(region);
            if (Requested.Count == FailOnCall)
            {
                throw new InvalidOperationException("capture failed");
            }

            CapturedImage img = Solid(region.Width, region.Height, 1, 2, 3);
            Returned.Add(img);
            return img;
        }
    }

    [Fact]
    public void DefaultCaptureRegions_CapturesEachRegionInOrder()
    {
        var capture = new RegionOnlyCapture();
        IWindowCaptureService service = capture;
        var regions = new[] { new IntRegion(0, 0, 3, 2), new IntRegion(5, 5, 4, 1) };

        IReadOnlyList<CapturedImage?> crops = service.CaptureRegions(new IntPtr(1), regions);

        Assert.Equal(regions, capture.Requested);
        Assert.Equal(2, crops.Count);
        Assert.Equal(3, crops[0]!.Width);
        Assert.Equal(4, crops[1]!.Width);
    }

    [Fact]
    public void DefaultCaptureRegions_DisposesEarlierCrops_WhenALaterOneThrows()
    {
        var capture = new RegionOnlyCapture { FailOnCall = 2 };
        IWindowCaptureService service = capture;

        Assert.Throws<InvalidOperationException>(() =>
            service.CaptureRegions(new IntPtr(1), new[] { new IntRegion(0, 0, 2, 2), new IntRegion(0, 0, 2, 2) }));
        Assert.True(IsDisposed(Assert.Single(capture.Returned)));
    }

    [Fact]
    public void GdiCapture_InvalidWindow_ReturnsNullPerRegion()
    {
        var gdi = new GdiWindowCaptureService();
        IReadOnlyList<CapturedImage?> crops = gdi.CaptureRegions(IntPtr.Zero, new[] { new IntRegion(0, 0, 10, 10), new IntRegion(0, 0, 5, 5) });

        Assert.Equal(2, crops.Count);
        Assert.All(crops, c => Assert.Null(c));
        Assert.Null(gdi.CaptureRegion(IntPtr.Zero, new IntRegion(0, 0, 10, 10)));
        Assert.Null(gdi.CaptureClientArea(IntPtr.Zero));
    }
}
