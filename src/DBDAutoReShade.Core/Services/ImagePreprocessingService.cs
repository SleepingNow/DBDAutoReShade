namespace DBDAutoReShade.Core.Services;

/// <summary>Knobs for the OCR preprocessing pipeline (kept modular for later tuning).</summary>
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

            byte[] gray = ToGrayscale(current);

            if (options.ContrastStretch)
            {
                gray = ContrastStretch(gray);
            }

            byte[] mono = options.Binarize ? OtsuBinarize(gray) : gray;

            // OCR engines expect dark text on a light background.
            if (Mean(mono) < 127.5)
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
            // ITU-R BT.601 luma.
            gray[g] = (byte)((px[i] * 114 + px[i + 1] * 587 + px[i + 2] * 299 + 500) / 1000);
        }

        return gray;
    }

    public static byte[] ContrastStretch(byte[] gray)
    {
        // 2nd / 98th percentile stretch ignores outlier specks.
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
            int v = gray[i];
            out_[i] = (byte)Math.Clamp((int)((v - lo) * scale), 0, 255);
        }

        return out_;
    }

    public static byte[] OtsuBinarize(byte[] gray)
    {
        int[] hist = new int[256];
        foreach (byte v in gray)
        {
            hist[v]++;
        }

        int total = gray.Length;
        double sum = 0;
        for (int i = 0; i < 256; i++)
        {
            sum += i * hist[i];
        }

        double sumB = 0;
        int wB = 0;
        double bestVar = -1;
        int threshold = 127;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0)
            {
                continue;
            }

            int wF = total - wB;
            if (wF == 0)
            {
                break;
            }

            sumB += t * hist[t];
            double mB = sumB / wB;
            double mF = (sum - sumB) / wF;
            double between = wB * wF * (mB - mF) * (mB - mF);
            if (between > bestVar)
            {
                bestVar = between;
                threshold = t;
            }
        }

        var out_ = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
        {
            out_[i] = gray[i] > threshold ? (byte)255 : (byte)0;
        }

        return out_;
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

    /// <summary>
    /// Cheap perceptual hash input: 32x32 grayscale thumbnail used by the
    /// lifecycle service to notice scene changes WITHOUT running OCR.
    /// </summary>
    public static byte[] Thumbnail32(CapturedImage image)
    {
        byte[] gray = ToGrayscale(image);
        var thumb = new byte[32 * 32];
        for (int y = 0; y < 32; y++)
        {
            int sy = y * image.Height / 32;
            for (int x = 0; x < 32; x++)
            {
                int sx = x * image.Width / 32;
                thumb[y * 32 + x] = gray[sy * image.Width + sx];
            }
        }

        return thumb;
    }

    /// <summary>Mean absolute per-pixel difference between two thumbnails, 0..255.</summary>
    public static double ThumbnailDistance(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return double.MaxValue;
        }

        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return sum / a.Length;
    }
}
