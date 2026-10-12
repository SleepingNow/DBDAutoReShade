using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>WindowsOcrEngine: availability re-probing and the max-dimension downscale.</summary>
public sealed class OcrEngineTests
{
    [Theory]
    [InlineData(2663, 461, 2600, 2600, 450)] // raw 5K map crop
    [InlineData(5326, 922, 2600, 2600, 450)] // preprocessed (upscaled) 5K crop
    [InlineData(200, 5000, 2600, 104, 2600)] // tall: height is the limiting side
    [InlineData(3000, 1, 2600, 2600, 1)] // never below 1 px
    [InlineData(1000, 200, 2600, 1000, 200)] // already fits: unchanged
    [InlineData(2600, 2600, 2600, 2600, 2600)] // exactly at the limit: unchanged
    [InlineData(5000, 5000, 0, 5000, 5000)] // no known limit: unchanged
    public void FitWithin_KeepsAspect_AndRespectsLimit(int w, int h, int max, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), WindowsOcrEngine.FitWithin(w, h, max));
    }

    [Fact]
    public void DownscaleToFit_ReturnsNull_WhenImageFits()
    {
        using var img = new CapturedImage(100, 50, new byte[100 * 50 * 4]);
        Assert.Null(WindowsOcrEngine.DownscaleToFit(img, 2600));
    }

    [Fact]
    public void DownscaleToFit_AveragesBlocks()
    {
        // 4x2 → 2x1: each output pixel is the mean of a 2x2 block.
        byte[] blue = { 0, 40, 80, 120, 20, 60, 100, 140 };
        var px = new byte[4 * 2 * 4];
        for (int i = 0; i < blue.Length; i++)
        {
            px[i * 4] = blue[i];
            px[i * 4 + 3] = 255;
        }

        using var img = new CapturedImage(4, 2, px);
        using CapturedImage? small = WindowsOcrEngine.DownscaleToFit(img, 2);

        Assert.NotNull(small);
        Assert.Equal(2, small.Width);
        Assert.Equal(1, small.Height);
        Assert.Equal(30, small.Pixels[0]);
        Assert.Equal(110, small.Pixels[4]);
        Assert.Equal(255, small.Pixels[3]);
    }

    [Fact]
    public void DownscaleToFit_LargeCrop_FitsEngineLimit()
    {
        using var img = new CapturedImage(5326, 922, new byte[5326 * 922 * 4]);
        using CapturedImage? small = WindowsOcrEngine.DownscaleToFit(img, 2600);

        Assert.NotNull(small);
        Assert.Equal(2600, small.Width);
        Assert.Equal(450, small.Height);
    }

    [Fact]
    public void Reprobe_RetriesAfterInterval_ThenCachesSuccess()
    {
        long now = 0;
        string? installed = null;
        var init = new ReprobingInitializer<string>(
            () => installed is null ? (null, "No English OCR language is installed.") : (installed, null),
            TimeSpan.FromSeconds(10),
            () => now);

        Assert.False(init.TryGet(out _, out string? error));
        Assert.Equal("No English OCR language is installed.", error);
        Assert.Equal(1, init.ProbeCount);

        installed = "engine"; // the user installs the language pack

        now = 9_999;
        Assert.False(init.TryGet(out _, out _)); // too soon: the cached failure stands
        Assert.Equal(1, init.ProbeCount);

        now = 10_000;
        Assert.True(init.TryGet(out string? value, out error)); // no app restart needed
        Assert.Equal("engine", value);
        Assert.Null(error);
        Assert.Equal(2, init.ProbeCount);

        now = 1_000_000;
        Assert.True(init.TryGet(out _, out _));
        Assert.Equal(2, init.ProbeCount); // success is cached for good
    }

    [Fact]
    public void Reprobe_ProbeException_IsReportedAsUnavailable()
    {
        var init = new ReprobingInitializer<string>(
            () => throw new InvalidOperationException("WinRT exploded"), TimeSpan.FromSeconds(10), () => 0);

        Assert.False(init.TryGet(out string? value, out string? error));
        Assert.Null(value);
        Assert.Equal("WinRT exploded", error);
    }

    [Fact]
    public async Task Reprobe_ConcurrentCallers_NeverSeeAHalfFinishedProbe()
    {
        int probes = 0;
        var init = new ReprobingInitializer<string>(
            () =>
            {
                Interlocked.Increment(ref probes);
                Thread.Sleep(150); // slow probe: callers arrive while it runs
                return ("engine", null);
            },
            TimeSpan.FromSeconds(10));

        using var start = new ManualResetEventSlim(false);
        Task<bool>[] callers = Enumerable.Range(0, 8)
            .Select(caller => Task.Run(() =>
            {
                start.Wait();
                return init.TryGet(out _, out _);
            }))
            .ToArray();
        start.Set();

        bool[] results = await Task.WhenAll(callers);
        Assert.All(results, r => Assert.True(r));
        Assert.Equal(1, Volatile.Read(ref probes));
    }

    [Fact]
    public async Task WindowsOcr_AcceptsImagesLargerThanEngineLimit()
    {
        var engine = new WindowsOcrEngine();
        if (!engine.IsAvailable)
        {
            // No English OCR pack on this machine: availability must be explained.
            Assert.False(string.IsNullOrWhiteSpace(engine.UnavailableReason));
            return;
        }

        Assert.Null(engine.UnavailableReason);
        int max = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;
        int width = max * 2 + 3;
        var white = new byte[width * 300 * 4];
        Array.Fill(white, (byte)255);
        using var big = new CapturedImage(width, 300, white);

        IReadOnlyList<OcrTextLine> lines = await engine.RecognizeAsync(big, CancellationToken.None);
        Assert.NotNull(lines);
    }
}
