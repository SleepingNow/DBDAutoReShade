using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class ImagePreprocessingTests
{
    private static CapturedImage WhiteTextOnBlack(int w = 200, int h = 60)
    {
        var px = new byte[w * h * 4];
        // Black background, white horizontal band as pseudo-text.
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool ink = y > h / 3 && y < 2 * h / 3 && x > 10 && x < w - 10;
                byte v = ink ? (byte)255 : (byte)0;
                int i = (y * w + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = 255;
            }
        }

        return new CapturedImage(w, h, px);
    }

    [Fact]
    public void Preprocess_ProducesDarkTextOnLightBackground()
    {
        using var src = WhiteTextOnBlack();
        using CapturedImage processed = ImagePreprocessingService.PreprocessForOcr(src);
        byte[] gray = ImagePreprocessingService.ToGrayscale(processed);
        Assert.True(ImagePreprocessingService.Mean(gray) > 127.5,
            "Preprocessed image must be light-background for OCR.");
    }

    [Fact]
    public void Preprocess_UpscalesSmallImages()
    {
        using var src = WhiteTextOnBlack(500, 150);
        using CapturedImage processed = ImagePreprocessingService.PreprocessForOcr(src);
        Assert.Equal(1400, processed.Width); // 500 * 2.8 → 1400 target
    }

    [Fact]
    public void Preprocess_UpscaleFactor_IsCapped()
    {
        using var src = WhiteTextOnBlack(200, 60);
        using CapturedImage processed = ImagePreprocessingService.PreprocessForOcr(src);
        Assert.Equal(600, processed.Width); // 200 * 7 → capped at 3x
    }

    [Fact]
    public void Grayscale_UsesLumaWeights()
    {
        var px = new byte[] { 0, 0, 255, 255 }; // pure red
        using var img = new CapturedImage(1, 1, px);
        byte[] gray = ImagePreprocessingService.ToGrayscale(img);
        Assert.Equal(76, gray[0]); // 0.299*255 ≈ 76
    }

    [Fact]
    public void ThumbnailDistance_IdenticalIsZero_DifferentIsPositive()
    {
        using var a = WhiteTextOnBlack();
        using var b = WhiteTextOnBlack();
        byte[] ta = ImagePreprocessingService.Thumbnail32(a);
        byte[] tb = ImagePreprocessingService.Thumbnail32(b);
        Assert.Equal(0, ImagePreprocessingService.ThumbnailDistance(ta, tb));

        // Invert b: distance must be large.
        for (int i = 0; i < tb.Length; i++)
        {
            tb[i] = (byte)(255 - tb[i]);
        }

        Assert.True(ImagePreprocessingService.ThumbnailDistance(ta, tb) > 100);
        Assert.Equal(32 * 32, ta.Length);
    }

    [Fact]
    public void CapturedImage_RejectsBadBuffer()
    {
        Assert.Throws<ArgumentException>(() => new CapturedImage(4, 4, new byte[10]));
    }
}
