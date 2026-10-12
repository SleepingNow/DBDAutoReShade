namespace DBDAutoReShade.Core.Services;

/// <summary>Knobs for the OCR preprocessing pipeline (kept modular for later tuning).</summary>
/// <param name="UpscaleSmallImages">Upscale images narrower than <paramref name="UpscaleTargetWidth"/> (at most 3x).</param>
/// <param name="UpscaleTargetWidth">Target width in pixels for the upscale step.</param>
/// <param name="Grayscale">
/// Convert to a single luma channel. When false and <paramref name="Binarize"/> is false, colour is
/// preserved: the contrast stretch (levels taken from luma) and the polarity inversion are applied
/// identically to B, G and R. Binarization always yields a grayscale image, so it implies this step.
/// </param>
/// <param name="ContrastStretch">2nd / 98th percentile contrast stretch.</param>
/// <param name="Binarize">Otsu binarization to pure black and white.</param>
/// <param name="DarkTextOnLightBackgroundThreshold">
/// Polarity check: the image is inverted when its mean brightness, as a fraction of white (0..1),
/// is below this value. The default 0.5 inverts a binarized image that is more than half black.
/// </param>
public sealed record OcrPreprocessingOptions(
    bool UpscaleSmallImages = true,
    int UpscaleTargetWidth = 1400,
    bool Grayscale = true,
    bool ContrastStretch = true,
    bool Binarize = true,
    double DarkTextOnLightBackgroundThreshold = 0.5);

/// <summary>
/// Lightweight, fully managed preprocessing: upscale → grayscale → contrast
/// stretch → Otsu binarization → polarity check. Pure functions over
/// <see cref="CapturedImage"/>; no native handles, no disk I/O.
/// </summary>
public static class ImagePreprocessingService
{
    public static CapturedImage PreprocessForOcr(CapturedImage source, OcrPreprocessingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new OcrPreprocessingOptions();
        double polarityThreshold = options.DarkTextOnLightBackgroundThreshold;
        if (double.IsNaN(polarityThreshold) || polarityThreshold < 0 || polarityThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "DarkTextOnLightBackgroundThreshold must be between 0 and 1.");
        }

        CapturedImage current = source.Clone();
        try
        {
            if (options.UpscaleSmallImages && current.Width < options.UpscaleTargetWidth)
            {
                double factor = (double)options.UpscaleTargetWidth / current.Width;
                factor = Math.Min(factor, 3.0);
                var upscaled = BilinearUpscale(current, factor);
                current.Dispose();
                current = upscaled;
            }

            // Binarization needs a single intensity channel, so only the
            // non-binarized pipeline can keep colour.
            if (!options.Grayscale && !options.Binarize)
            {
                return PreprocessColour(current, options.ContrastStretch, polarityThreshold);
            }

            byte[] gray = ToGrayscale(current);

            if (options.ContrastStretch)
            {
                gray = ContrastStretch(gray);
            }

            byte[] mono = options.Binarize ? OtsuBinarize(gray) : gray;

            // OCR engines expect dark text on a light background.
            if (NeedsInversion(Mean(mono), polarityThreshold))
            {
                InvertInPlace(mono);
            }

            return FromGrayscale(current.Width, current.Height, mono);
        }
        finally
        {
            current.Dispose();
        }
    }

    public static byte[] ToGrayscale(CapturedImage image)
    {
        var gray = new byte[image.Width * image.Height];
        byte[] px = image.Pixels;
        for (int i = 0, g = 0; i < px.Length; i += 4, g++)
        {
            gray[g] = Luma(px, i);
        }

        return gray;
    }

    public static byte[] ContrastStretch(byte[] gray)
    {
        byte[]? lut = ContrastStretchLut(gray);
        if (lut is null)
        {
            return (byte[])gray.Clone();
        }

        var out_ = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
        {
            out_[i] = lut[gray[i]];
        }

        return out_;
    }

    public static byte[] OtsuBinarize(byte[] gray)
    {
        ArgumentNullException.ThrowIfNull(gray);
        int threshold = OtsuThreshold(gray);

        var out_ = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
        {
            out_[i] = gray[i] > threshold ? (byte)255 : (byte)0;
        }

        return out_;
    }

    /// <summary>
    /// Otsu's threshold: the level that maximizes the between-class variance of the
    /// histogram. Pixels <c>&gt;</c> the returned value are foreground (white) in
    /// <see cref="OtsuBinarize"/>. Returns 127 for an empty or single-level input.
    /// </summary>
    public static int OtsuThreshold(ReadOnlySpan<byte> gray)
    {
        long[] hist = new long[256];
        foreach (byte v in gray)
        {
            hist[v]++;
        }

        // All weights and sums are kept in 64-bit / double: real crops have far
        // more pixels than an int product wB * wF can hold (it wraps above ~92k px).
        long total = gray.Length;
        double sum = 0;
        for (int i = 0; i < 256; i++)
        {
            sum += (double)i * hist[i];
        }

        double sumB = 0;
        long wB = 0;
        double bestVar = -1;
        int threshold = 127;
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

            sumB += (double)t * hist[t];
            double mB = sumB / wB;
            double mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > bestVar)
            {
                bestVar = between;
                threshold = t;
            }
        }

        return threshold;
    }

    public static CapturedImage BilinearUpscale(CapturedImage image, double factor)
    {
        if (factor <= 1.0)
        {
            return image.Clone();
        }

        int w = Math.Max(1, (int)(image.Width * factor));
        int h = Math.Max(1, (int)(image.Height * factor));
        var out_ = new byte[w * h * 4];
        byte[] src = image.Pixels;

        for (int y = 0; y < h; y++)
        {
            double sy = (y + 0.5) / factor - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, image.Height - 1);
            int y1 = Math.Clamp(y0 + 1, 0, image.Height - 1);
            double fy = Math.Clamp(sy - y0, 0, 1);

            for (int x = 0; x < w; x++)
            {
                double sx = (x + 0.5) / factor - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, image.Width - 1);
                int x1 = Math.Clamp(x0 + 1, 0, image.Width - 1);
                double fx = Math.Clamp(sx - x0, 0, 1);

                for (int c = 0; c < 4; c++)
                {
                    double p00 = src[(y0 * image.Width + x0) * 4 + c];
                    double p10 = src[(y0 * image.Width + x1) * 4 + c];
                    double p01 = src[(y1 * image.Width + x0) * 4 + c];
                    double p11 = src[(y1 * image.Width + x1) * 4 + c];
                    double v = p00 * (1 - fx) * (1 - fy) + p10 * fx * (1 - fy) + p01 * (1 - fx) * fy + p11 * fx * fy;
                    out_[(y * w + x) * 4 + c] = (byte)Math.Clamp((int)(v + 0.5), 0, 255);
                }
            }
        }

        // Force opaque alpha.
        for (int i = 3; i < out_.Length; i += 4)
        {
            out_[i] = 255;
        }

        return new CapturedImage(w, h, out_);
    }

    /// <summary>Mean brightness of a grayscale buffer (0..255).</summary>
    public static double Mean(byte[] gray)
    {
        if (gray.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (byte v in gray)
        {
            sum += v;
        }

        return sum / gray.Length;
    }

    public static void InvertInPlace(byte[] gray)
    {
        for (int i = 0; i < gray.Length; i++)
        {
            gray[i] = (byte)(255 - gray[i]);
        }
    }

    public static CapturedImage FromGrayscale(int width, int height, byte[] gray)
    {
        var bgra = new byte[width * height * 4];
        for (int i = 0, o = 0; i < gray.Length; i++, o += 4)
        {
            bgra[o] = bgra[o + 1] = bgra[o + 2] = gray[i];
            bgra[o + 3] = 255;
        }

        return new CapturedImage(width, height, bgra);
    }

    /// <summary>ITU-R BT.601 luma of the BGRA pixel starting at <paramref name="offset"/>.</summary>
    private static byte Luma(byte[] bgra, int offset) =>
        (byte)((bgra[offset] * 114 + bgra[offset + 1] * 587 + bgra[offset + 2] * 299 + 500) / 1000);

    /// <summary>True when an image of this mean brightness (0..255) is light-on-dark and must be inverted.</summary>
    private static bool NeedsInversion(double meanBrightness, double darkTextOnLightThreshold) =>
        meanBrightness < darkTextOnLightThreshold * 255.0;

    /// <summary>
    /// Lookup table for the 2nd / 98th percentile stretch (ignores outlier specks),
    /// or null when the input is too flat to stretch.
    /// </summary>
    private static byte[]? ContrastStretchLut(byte[] gray)
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
            return null;
        }

        var lut = new byte[256];
        double scale = 255.0 / (hi - lo);
        for (int v = 0; v < 256; v++)
        {
            lut[v] = (byte)Math.Clamp((int)((v - lo) * scale), 0, 255);
        }

        return lut;
    }

    /// <summary>
    /// Colour-preserving variant of the pipeline (Grayscale and Binarize both off):
    /// the luma-derived contrast stretch and the polarity inversion are applied
    /// identically to B, G and R, so hue survives for the OCR engine.
    /// </summary>
    private static CapturedImage PreprocessColour(CapturedImage image, bool contrastStretch, double darkTextOnLightThreshold)
    {
        byte[] src = image.Pixels;
        byte[]? lut = contrastStretch ? ContrastStretchLut(ToGrayscale(image)) : null;
        var out_ = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                out_[i + c] = lut is null ? src[i + c] : lut[src[i + c]];
            }

            out_[i + 3] = 255;
        }

        var result = new CapturedImage(image.Width, image.Height, out_);
        if (NeedsInversion(Mean(ToGrayscale(result)), darkTextOnLightThreshold))
        {
            for (int i = 0; i < out_.Length; i += 4)
            {
                out_[i] = (byte)(255 - out_[i]);
                out_[i + 1] = (byte)(255 - out_[i + 1]);
                out_[i + 2] = (byte)(255 - out_[i + 2]);
            }
        }

        return result;
    }
}
