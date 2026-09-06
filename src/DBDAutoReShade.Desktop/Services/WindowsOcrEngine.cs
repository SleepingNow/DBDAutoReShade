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
/// Requires an English OCR language pack; absence is reported, never fatal.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly object _gate = new();
    private bool _initAttempted;
    private OcrEngine? _engine;
    private string? _unavailableReason;

    public string Name => "Windows OCR (local)";

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _engine is not null;
        }
    }

    public string? UnavailableReason
    {
        get
        {
            EnsureInitialized();
            return _unavailableReason;
        }
    }

    private void EnsureInitialized()
    {
        if (_initAttempted)
        {
            return;
        }

        lock (_gate)
        {
            if (_initAttempted)
            {
                return;
            }

            _initAttempted = true;
            try
            {
                bool hasEnglish = OcrEngine.AvailableRecognizerLanguages
                    .Any(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                if (!hasEnglish)
                {
                    _unavailableReason = "No English OCR language is installed (Settings → Time & language → Language → Add English OCR).";
                    return;
                }

                _engine = OcrEngine.TryCreateFromLanguage(new Language("en"));
                if (_engine is null)
                {
                    _unavailableReason = "The Windows OCR engine could not be created for English.";
                }
            }
            catch (Exception ex)
            {
                _unavailableReason = $"Windows OCR is unavailable: {ex.Message}";
            }
        }
    }

    public async Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(CapturedImage image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        EnsureInitialized();
        if (_engine is null)
        {
            throw new InvalidOperationException(_unavailableReason ?? "OCR engine unavailable.");
        }

        ct.ThrowIfCancellationRequested();

        // Note: Windows.Storage.Streams.CryptographicBuffer is not projected
        // to .NET by CsWinRT, so bytes are wrapped via DataWriter.DetachBuffer.
        IBuffer buffer;
        using (var writer = new DataWriter())
        {
            writer.WriteBytes(image.Pixels);
            buffer = writer.DetachBuffer();
        }

        using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            buffer, BitmapPixelFormat.Bgra8, image.Width, image.Height);

        var op = _engine.RecognizeAsync(bitmap);
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
}
