using System.Diagnostics.CodeAnalysis;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>
/// Local, offline OCR via the in-box Windows OCR engine (Windows.Media.Ocr).
/// No model files to bundle, no network, no telemetry. WinRT OCR exposes no
/// per-word confidence, so confidence is derived from string similarity plus
/// multi-sample agreement in <see cref="DetectionAggregator"/>.
/// Requires an English OCR language pack; absence is reported, never fatal,
/// and re-checked at most every <see cref="ReprobeInterval"/> so installing the
/// pack takes effect without restarting the app. Images larger than
/// <see cref="OcrEngine.MaxImageDimension"/> are downscaled (aspect kept)
/// before recognition.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    /// <summary>Minimum time between two availability probes while OCR is unavailable.</summary>
    internal static readonly TimeSpan ReprobeInterval = TimeSpan.FromSeconds(10);

    /// <summary>Used only if the engine's own limit cannot be read (the documented Windows value).</summary>
    private const int FallbackMaxImageDimension = 2600;

    private readonly ReprobingInitializer<OcrEngine> _engine = new(ProbeEngine, ReprobeInterval);

    public string Name => "Windows OCR (local)";

    public bool IsAvailable => _engine.TryGet(out _, out _);

    public string? UnavailableReason => _engine.TryGet(out _, out string? reason) ? null : reason;

    private static (OcrEngine? Value, string? Error) ProbeEngine()
    {
        try
        {
            bool hasEnglish = OcrEngine.AvailableRecognizerLanguages
                .Any(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (!hasEnglish)
            {
                return (null, "No English OCR language is installed (Settings → Time & language → Language → Add English OCR).");
            }

            OcrEngine? engine = OcrEngine.TryCreateFromLanguage(new Language("en"));
            return engine is null
                ? (null, "The Windows OCR engine could not be created for English.")
                : (engine, null);
        }
        catch (Exception ex)
        {
            return (null, $"Windows OCR is unavailable: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(CapturedImage image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!_engine.TryGet(out OcrEngine? engine, out string? reason))
        {
            throw new InvalidOperationException(reason ?? "OCR engine unavailable.");
        }

        ct.ThrowIfCancellationRequested();

        // The engine rejects images beyond its limit (raw 5K crops are ~2663 px
        // wide; preprocessing can upscale further): shrink, keeping aspect.
        CapturedImage? scaled = DownscaleToFit(image, GetMaxImageDimension());
        try
        {
            CapturedImage input = scaled ?? image;

            // Note: Windows.Storage.Streams.CryptographicBuffer is not projected
            // to .NET by CsWinRT, so bytes are wrapped via DataWriter.DetachBuffer.
            IBuffer buffer;
            using (var writer = new DataWriter())
            {
                writer.WriteBytes(input.Pixels);
                buffer = writer.DetachBuffer();
            }

            using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                buffer, BitmapPixelFormat.Bgra8, input.Width, input.Height);

            var op = engine.RecognizeAsync(bitmap);
            using (ct.Register(() =>
            {
                try
                {
                    op.Cancel();
                }
                catch
                {
                    // Best effort.
                }
            }))
            {
                OcrResult result = await op;
                var lines = new List<OcrTextLine>(result.Lines.Count);
                foreach (OcrLine? line in result.Lines)
                {
                    if (!string.IsNullOrWhiteSpace(line?.Text))
                    {
                        lines.Add(new OcrTextLine(line.Text, EngineConfidence: null));
                    }
                }

                return lines;
            }
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    private static int GetMaxImageDimension()
    {
        try
        {
            uint max = OcrEngine.MaxImageDimension;
            return max is > 0 and <= int.MaxValue ? (int)max : FallbackMaxImageDimension;
        }
        catch
        {
            return FallbackMaxImageDimension;
        }
    }

    /// <summary>
    /// Size that fits within <paramref name="maxDimension"/> on both axes,
    /// keeping the aspect ratio (rounded, at least 1 px). Sizes already within
    /// the limit, and a non-positive limit, are returned unchanged.
    /// </summary>
    internal static (int Width, int Height) FitWithin(int width, int height, int maxDimension)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (maxDimension <= 0 || (width <= maxDimension && height <= maxDimension))
        {
            return (width, height);
        }

        if (width >= height)
        {
            long scaledHeight = ((long)height * maxDimension + width / 2) / width;
            return (maxDimension, (int)Math.Clamp(scaledHeight, 1, maxDimension));
        }

        long scaledWidth = ((long)width * maxDimension + height / 2) / height;
        return ((int)Math.Clamp(scaledWidth, 1, maxDimension), maxDimension);
    }

    /// <summary>
    /// A downscaled copy of <paramref name="image"/> that fits within
    /// <paramref name="maxDimension"/>, or null when it already fits (use the
    /// original). Area-averaging (box filter) keeps thin glyph strokes legible.
    /// </summary>
    internal static CapturedImage? DownscaleToFit(CapturedImage image, int maxDimension)
    {
        ArgumentNullException.ThrowIfNull(image);
        (int width, int height) = FitWithin(image.Width, image.Height, maxDimension);
        if (width == image.Width && height == image.Height)
        {
            return null;
        }

        return AreaDownscale(image, width, height);
    }

    private static CapturedImage AreaDownscale(CapturedImage source, int width, int height)
    {
        int sw = source.Width;
        int sh = source.Height;
        byte[] src = source.Pixels;
        var dst = new byte[width * height * 4];

        for (int dy = 0; dy < height; dy++)
        {
            int y0 = (int)((long)dy * sh / height);
            int y1 = Math.Max(y0 + 1, (int)((long)(dy + 1) * sh / height));
            for (int dx = 0; dx < width; dx++)
            {
                int x0 = (int)((long)dx * sw / width);
                int x1 = Math.Max(x0 + 1, (int)((long)(dx + 1) * sw / width));

                long b = 0, g = 0, r = 0, a = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = (y * sw + x0) * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        b += src[i];
                        g += src[i + 1];
                        r += src[i + 2];
                        a += src[i + 3];
                    }
                }

                long count = (long)(x1 - x0) * (y1 - y0);
                long half = count / 2;
                int o = (dy * width + dx) * 4;
                dst[o] = (byte)((b + half) / count);
                dst[o + 1] = (byte)((g + half) / count);
                dst[o + 2] = (byte)((r + half) / count);
                dst[o + 3] = (byte)((a + half) / count);
            }
        }

        return new CapturedImage(width, height, dst);
    }
}

/// <summary>
/// Thread-safe lazy initializer that, unlike <see cref="Lazy{T}"/>, does not
/// cache failure forever: while the value is unavailable, a later query probes
/// again once <c>retryAfter</c> has elapsed since the previous probe finished.
/// A successful value is cached for good. State is published only after a
/// probe completes, under the lock, so concurrent callers never observe a
/// half-finished probe as "unavailable".
/// </summary>
internal sealed class ReprobingInitializer<T> where T : class
{
    private readonly object _gate = new();
    private readonly Func<(T? Value, string? Error)> _probe;
    private readonly long _retryAfterMs;
    private readonly Func<long> _clockMs;
    private T? _value;
    private string? _error;
    private bool _probed;
    private long _lastProbeEndMs;
    private int _probeCount;

    public ReprobingInitializer(Func<(T? Value, string? Error)> probe, TimeSpan retryAfter, Func<long>? clockMs = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _retryAfterMs = Math.Max(0, (long)retryAfter.TotalMilliseconds);
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    /// <summary>Number of probes run so far (diagnostics/tests).</summary>
    public int ProbeCount => Volatile.Read(ref _probeCount);

    public bool TryGet([NotNullWhen(true)] out T? value, out string? error)
    {
        T? cached = Volatile.Read(ref _value);
        if (cached is not null)
        {
            value = cached;
            error = null;
            return true;
        }

        lock (_gate)
        {
            if (_value is null && (!_probed || _clockMs() - _lastProbeEndMs >= _retryAfterMs))
            {
                (T? Value, string? Error) result;
                try
                {
                    result = _probe();
                }
                catch (Exception ex)
                {
                    result = (null, ex.Message);
                }

                Interlocked.Increment(ref _probeCount);
                _error = result.Value is null ? result.Error ?? "Unavailable." : null;
                _lastProbeEndMs = _clockMs();
                _probed = true;
                Volatile.Write(ref _value, result.Value);
            }

            value = _value;
            error = _error;
            return value is not null;
        }
    }
}
