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
    public void CapturedImage_RejectsBadBuffer()
    {
        Assert.Throws<ArgumentException>(() => new CapturedImage(4, 4, new byte[10]));
    }

    // ---- A1: Otsu on real-size crops (the int product wB * wF wrapped above ~92k px) ----

    [Fact]
    public void OtsuThreshold_LargeBimodalImage_SplitsBetweenTheModes()
    {
        // 480k px: 70% around 40, 30% around 200, ±10 noise.
        var rng = new Random(1234);
        var gray = new byte[800 * 600];
        int lowMax = 0, highMin = 255;
        for (int i = 0; i < gray.Length; i++)
        {
            bool high = rng.NextDouble() < 0.3;
            int v = (high ? 200 : 40) + rng.Next(-10, 11);
            gray[i] = (byte)v;
            if (high)
            {
                highMin = Math.Min(highMin, v);
            }
            else
            {
                lowMax = Math.Max(lowMax, v);
            }
        }

        int threshold = ImagePreprocessingService.OtsuThreshold(gray);

        Assert.InRange(threshold, lowMax, highMin - 1); // strictly between the modes
        Assert.Equal(ExactOtsuThreshold(gray), threshold);

        byte[] mono = ImagePreprocessingService.OtsuBinarize(gray);
        for (int i = 0; i < gray.Length; i++)
        {
            Assert.Equal(gray[i] >= highMin ? (byte)255 : (byte)0, mono[i]);
        }
    }

    [Fact]
    public void OtsuThreshold_SmallImage_MatchesExactReference()
    {
        var rng = new Random(7);
        var gray = new byte[97 * 61];
        rng.NextBytes(gray);
        Assert.Equal(ExactOtsuThreshold(gray), ImagePreprocessingService.OtsuThreshold(gray));
    }

    [Fact]
    public void Preprocess_LargeLightTextOnDarkCrop_KeepsTextDistinctFromBackground()
    {
        // 2000x300 = 600k px (no upscale): noisy dark background, bright glyph-like bars.
        const int w = 2000, h = 300;
        var rng = new Random(99);
        var px = new byte[w * h * 4];
        var isText = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool ink = y >= 100 && y < 200 && (x / 12) % 3 == 0;
                int v = (ink ? 230 : 25) + rng.Next(-8, 9);
                int i = (y * w + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = (byte)v;
                px[i + 3] = 255;
                isText[y * w + x] = ink;
            }
        }

        using var src = new CapturedImage(w, h, px);
        using CapturedImage processed = ImagePreprocessingService.PreprocessForOcr(src);
        byte[] gray = ImagePreprocessingService.ToGrayscale(processed);

        int textWrong = 0, backgroundWrong = 0;
        for (int i = 0; i < gray.Length; i++)
        {
            if (isText[i] && gray[i] != 0)
            {
                textWrong++; // text must come out black
            }
            else if (!isText[i] && gray[i] != 255)
            {
                backgroundWrong++; // background must come out white
            }
        }

        Assert.Equal(0, textWrong);
        Assert.Equal(0, backgroundWrong);
    }

    // ---- A3: preprocessing knobs are honoured; defaults reproduce the classic pipeline ----

    [Fact]
    public void ContrastStretch_MatchesOriginalImplementation()
    {
        var rng = new Random(5);
        for (int run = 0; run < 20; run++)
        {
            var gray = new byte[rng.Next(1, 5000)];
            int lo = rng.Next(0, 200);
            int span = rng.Next(1, 256 - lo);
            for (int i = 0; i < gray.Length; i++)
            {
                gray[i] = (byte)(lo + rng.Next(0, span));
            }

            Assert.Equal(ReferenceContrastStretch(gray), ImagePreprocessingService.ContrastStretch(gray));
        }
    }

    [Fact]
    public void Preprocess_DefaultOptions_MatchClassicPipeline()
    {
        var rng = new Random(11);
        const int w = 300, h = 80;
        var px = new byte[w * h * 4];
        rng.NextBytes(px);
        using var src = new CapturedImage(w, h, px);

        // Classic pipeline: upscale (≤3x) → luma → stretch → Otsu → invert if mean < 127.5.
        using CapturedImage upscaled = ImagePreprocessingService.BilinearUpscale(src, Math.Min(1400.0 / w, 3.0));
        byte[] mono = ImagePreprocessingService.OtsuBinarize(
            ImagePreprocessingService.ContrastStretch(ImagePreprocessingService.ToGrayscale(upscaled)));
        if (ImagePreprocessingService.Mean(mono) < 127.5)
        {
            ImagePreprocessingService.InvertInPlace(mono);
        }

        using CapturedImage expected = ImagePreprocessingService.FromGrayscale(upscaled.Width, upscaled.Height, mono);
        using CapturedImage actual = ImagePreprocessingService.PreprocessForOcr(src);
        using CapturedImage explicitDefaults = ImagePreprocessingService.PreprocessForOcr(src, new OcrPreprocessingOptions());

        Assert.Equal(expected.Pixels, actual.Pixels);
        Assert.Equal(expected.Pixels, explicitDefaults.Pixels);
    }

    [Fact]
    public void Preprocess_HonoursDarkTextOnLightBackgroundThreshold()
    {
        // 40% bright columns on the left, 60% dark: binarized mean is 40% white.
        using var src = TwoToneColumns(1400, 50, brightFraction: 0.4);

        using CapturedImage byDefault = ImagePreprocessingService.PreprocessForOcr(src);
        using CapturedImage lowered = ImagePreprocessingService.PreprocessForOcr(src,
            new OcrPreprocessingOptions(DarkTextOnLightBackgroundThreshold: 0.3));

        // Default 0.5: more than half black → inverted, the bright columns turn black.
        Assert.Equal(0, byDefault.Pixels[0]);
        // 0.3: 40% white is already "light enough" → left as is.
        Assert.Equal(255, lowered.Pixels[0]);
    }

    [Fact]
    public void Preprocess_RejectsOutOfRangePolarityThreshold()
    {
        using var src = WhiteTextOnBlack();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImagePreprocessingService.PreprocessForOcr(src, new OcrPreprocessingOptions(DarkTextOnLightBackgroundThreshold: 1.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImagePreprocessingService.PreprocessForOcr(src, new OcrPreprocessingOptions(DarkTextOnLightBackgroundThreshold: double.NaN)));
    }

    [Fact]
    public void Preprocess_GrayscaleOff_WithoutBinarize_KeepsColour()
    {
        // Pure red (BGR 0,0,200) on dark blue (BGR 60,0,0): dark overall → inverted per channel.
        const int w = 64, h = 16;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            bool red = (i / 4) % w < w / 2;
            px[i] = red ? (byte)0 : (byte)60;
            px[i + 2] = red ? (byte)200 : (byte)0;
            px[i + 3] = 255;
        }

        using var src = new CapturedImage(w, h, px);
        var options = new OcrPreprocessingOptions(UpscaleSmallImages: false, Grayscale: false, ContrastStretch: false, Binarize: false);
        using CapturedImage processed = ImagePreprocessingService.PreprocessForOcr(src, options);

        Assert.Equal(new byte[] { 255, 255, 55, 255 }, processed.Pixels[..4]);
        int blue = (w - 1) * 4;
        Assert.Equal(new byte[] { 195, 255, 255, 255 }, processed.Pixels[blue..(blue + 4)]);
    }

    [Fact]
    public void Preprocess_GrayscaleOff_WithStretch_KeepsHue()
    {
        using var src = TwoToneColumns(64, 16, brightFraction: 0.5, bright: (B: 20, G: 40, R: 180), dark: (B: 10, G: 10, R: 30));
        var options = new OcrPreprocessingOptions(UpscaleSmallImages: false, Grayscale: false, Binarize: false);
        using CapturedImage processed = ImagePreprocessingService.PreprocessForOcr(src, options);

        byte[] p = processed.Pixels;
        Assert.NotEqual(p[0], p[2]); // still coloured, not gray
    }

    [Fact]
    public void Preprocess_GrayscaleOff_WithBinarize_IsStillMono()
    {
        using var src = TwoToneColumns(1400, 20, brightFraction: 0.3);
        var options = new OcrPreprocessingOptions(Grayscale: false);
        using CapturedImage processed = ImagePreprocessingService.PreprocessForOcr(src, options);

        byte[] p = processed.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            Assert.True(p[i] is 0 or 255);
            Assert.Equal(p[i], p[i + 1]);
            Assert.Equal(p[i], p[i + 2]);
        }
    }

    private static CapturedImage TwoToneColumns(
        int w,
        int h,
        double brightFraction,
        (byte B, byte G, byte R)? bright = null,
        (byte B, byte G, byte R)? dark = null)
    {
        var hi = bright ?? (200, 200, 200);
        var lo = dark ?? (50, 50, 50);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = x < w * brightFraction ? hi : lo;
                int i = (y * w + x) * 4;
                px[i] = c.B;
                px[i + 1] = c.G;
                px[i + 2] = c.R;
                px[i + 3] = 255;
            }
        }

        return new CapturedImage(w, h, px);
    }

    /// <summary>Otsu with exact integer arithmetic; ties keep the lowest level (as the production code does).</summary>
    private static int ExactOtsuThreshold(byte[] gray)
    {
        var hist = new long[256];
        foreach (byte v in gray)
        {
            hist[v]++;
        }

        long total = gray.Length;
        long sum = 0;
        for (int i = 0; i < 256; i++)
        {
            sum += i * hist[i];
        }

        // wB * wF * (mB - mF)^2 == (sumB * total - sum * wB)^2 / (wB * wF), compared as exact fractions.
        System.Numerics.BigInteger bestNum = -1, bestDen = 1;
        int threshold = 127;
        long wB = 0, sumB = 0;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0)
            {
                continue;
            }

            long wF = total - wB;
            if (wF == 0)
            {
                break;
            }

            sumB += t * hist[t];
            System.Numerics.BigInteger diff = (System.Numerics.BigInteger)sumB * total - (System.Numerics.BigInteger)sum * wB;
            System.Numerics.BigInteger num = diff * diff;
            System.Numerics.BigInteger den = (System.Numerics.BigInteger)wB * wF;
            if (num * bestDen > bestNum * den)
            {
                bestNum = num;
                bestDen = den;
                threshold = t;
            }
        }

        return threshold;
    }

    /// <summary>The original per-pixel ContrastStretch, kept to prove the lookup-table version is identical.</summary>
    private static byte[] ReferenceContrastStretch(byte[] gray)
    {
        int[] hist = new int[256];
        foreach (byte v in gray)
        {
            hist[v]++;
        }

        int total = gray.Length;
        int lo = 0, hi = 255;
        int acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= total * 0.02)
            {
                lo = i;
                break;
            }
        }

        acc = 0;
        for (int i = 255; i >= 0; i--)
        {
            acc += hist[i];
            if (acc >= total * 0.02)
            {
                hi = i;
                break;
            }
        }

        if (hi - lo < 8)
        {
            return (byte[])gray.Clone();
        }

        var out_ = new byte[gray.Length];
        double scale = 255.0 / (hi - lo);
        for (int i = 0; i < gray.Length; i++)
        {
            out_[i] = (byte)Math.Clamp((int)((gray[i] - lo) * scale), 0, 255);
        }

        return out_;
    }
}
