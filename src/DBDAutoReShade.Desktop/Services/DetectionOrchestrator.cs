using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>
/// Runs detection attempts: samples the live DBD loading screen at a modest
/// cadence (default ~0.9s), OCRs small normalized regions, matches with
/// confidence, and stops immediately on success.
/// Two modes: single-shot (internal ~10 s timeout, legacy) and continuous
/// toggle mode (scans until the caller's token is cancelled). In both modes:
/// no overlapping attempts, full disposal of captures, OCR fully idle outside
/// the window. Debug crops are saved ONLY on failure when Debug Mode is
/// enabled (bounded retention, local only) — never on user stop.
/// </summary>
public sealed class DetectionOrchestrator : IDisposable
{
    private readonly IOcrEngine _ocr;
    private readonly MapMatchingService _matcher;
    private readonly RealmDatabaseService _database;
    private readonly IWindowCaptureService _capture;
    private readonly Func<IntPtr, DbdClientBounds?> _boundsProvider;
    private readonly DetectionPolicy _policy;
    private int _running;
    private bool _disposed;

    public DetectionOrchestrator(
        IOcrEngine ocr,
        MapMatchingService matcher,
        RealmDatabaseService database,
        IWindowCaptureService capture,
        DetectionPolicy? policy = null,
        Func<IntPtr, DbdClientBounds?>? boundsProvider = null)
    {
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _policy = policy ?? new DetectionPolicy();
        _boundsProvider = boundsProvider ?? (hwnd =>
            DbdWindowService.TryGetClientBounds(hwnd, out var b) ? b : null);
    }

    public bool IsDetecting => Volatile.Read(ref _running) == 1;

    public event EventHandler<int>? SampleTaken; // sample index (1-based)

    /// <summary>
    /// Run a detection attempt against the live DBD window.
    /// When <paramref name="continuous"/> is true there is no internal timeout:
    /// sampling continues until confidence succeeds or <paramref name="ct"/> is
    /// cancelled (toggle mode — the caller stops the scan).
    /// </summary>
    public async Task<DetectionResult> DetectAsync(
        IntPtr dbdWindow,
        Func<bool>? debugMode = null,
        string? debugDirectory = null,
        bool continuous = false,
        CancellationToken ct = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DetectionOrchestrator));
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new DetectionResult(false, null, null, 0.0, 0, "A detection attempt is already running.");
        }

        try
        {
            if (!_ocr.IsAvailable)
            {
                return new DetectionResult(false, null, null, 0.0, 0,
                    $"Local OCR unavailable: {_ocr.UnavailableReason ?? "unknown reason"}");
            }

            if (!_database.IsLoaded)
            {
                return new DetectionResult(false, null, null, 0.0, 0, "Map database is not loaded.");
            }

            using var timeout = continuous
                ? new CancellationTokenSource()
                : new CancellationTokenSource(TimeSpan.FromSeconds(_policy.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var aggregator = new DetectionAggregator(_policy);
            int sample = 0;
            CapturedImage? lastDebugCrop = null;

            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    sample++;
                    MapMatchResult match;
                    try
                    {
                        match = await TakeSampleAsync(dbdWindow, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break; // stopped mid-sample (timeout or user stop), not an error
                    }

                    aggregator.AddSample(match);
                    try
                    {
                        SampleTaken?.Invoke(this, sample);
                    }
                    catch
                    {
                        // UI subscribers must never break detection.
                    }

                    if (aggregator.TryGetEarlyResult(out DetectionResult? early) && early is not null)
                    {
                        return early;
                    }

                    try
                    {
                        await Task.Delay(_policy.SampleIntervalMs, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                lastDebugCrop?.Dispose();
            }

            DetectionResult final = aggregator.Finalize();

            if (ct.IsCancellationRequested)
            {
                // Stopped by the caller (toggle off), not a detection failure:
                // no debug crop, distinct outcome for the controller.
                return new DetectionResult(false, null, null, 0.0, final.SampleCount,
                    "Scan stopped — press F8 to scan again.", WasStopped: true);
            }

            if (!final.Success && (debugMode?.Invoke() ?? false) && !string.IsNullOrWhiteSpace(debugDirectory))
            {
                SaveDebugCrop(dbdWindow, debugDirectory);
            }

            return final;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<MapMatchResult> TakeSampleAsync(IntPtr hwnd, CancellationToken ct)
    {
        DbdClientBounds? bounds = null;
        try
        {
            bounds = _boundsProvider(hwnd);
        }
        catch
        {
            // Provider failures mean "no window".
        }

        if (bounds is null || bounds.IsEmpty)
        {
            return MapMatchResult.Failure("DBD window is not available.");
        }

        IntRegion mapPx = DbdLoadingScreenRegions.ToPixels(DbdLoadingScreenRegions.MapName, bounds.Width, bounds.Height);
        IntRegion realmPx = DbdLoadingScreenRegions.ToPixels(DbdLoadingScreenRegions.RealmName, bounds.Width, bounds.Height);

        using CapturedImage? mapCrop = _capture.CaptureRegion(hwnd, mapPx);
        using CapturedImage? realmCrop = _capture.CaptureRegion(hwnd, realmPx);
        if (mapCrop is null || realmCrop is null)
        {
            return MapMatchResult.Failure("Could not capture the loading screen.");
        }

        string text = await OcrToTextAsync(mapCrop, ct).ConfigureAwait(false)
            + "\n" + await OcrToTextAsync(realmCrop, ct).ConfigureAwait(false);

        MapMatchResult match = _matcher.Match(text, _database.Realms);
        if (match.Success)
        {
            return match;
        }

        // One preprocessed retry per sample before giving up on this frame.
        try
        {
            using var preMap = ImagePreprocessingService.PreprocessForOcr(mapCrop);
            using var preRealm = ImagePreprocessingService.PreprocessForOcr(realmCrop);
            string retryText = await OcrToTextAsync(preMap, ct).ConfigureAwait(false)
                + "\n" + await OcrToTextAsync(preRealm, ct).ConfigureAwait(false);
            MapMatchResult retry = _matcher.Match(retryText, _database.Realms);
            return retry.Success ? retry : match;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return match;
        }
    }

    private async Task<string> OcrToTextAsync(CapturedImage image, CancellationToken ct)
    {
        try
        {
            var lines = await _ocr.RecognizeAsync(image, ct).ConfigureAwait(false);
            return string.Join("\n", lines.Select(l => l.Text));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void SaveDebugCrop(IntPtr hwnd, string debugDirectory)
    {
        try
        {
            Directory.CreateDirectory(debugDirectory);

            // Bounded retention: keep at most 20 crops.
            string[] existing = Directory.GetFiles(debugDirectory, "ocr-failure-*.bmp")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
            foreach (string old in existing.Skip(Math.Max(0, existing.Length - 19)))
            {
                try
                {
                    File.Delete(old);
                }
                catch
                {
                    // Best effort.
                }
            }

            DbdClientBounds? bounds = null;
            try
            {
                bounds = _boundsProvider(hwnd);
            }
            catch
            {
                return;
            }

            if (bounds is null || bounds.IsEmpty)
            {
                return;
            }

            IntRegion combined = DbdLoadingScreenRegions.ToPixels(DbdLoadingScreenRegions.Combined, bounds.Width, bounds.Height);
            using CapturedImage? crop = _capture.CaptureRegion(hwnd, combined);
            if (crop is null)
            {
                return;
            }

            string name = $"ocr-failure-{DateTime.Now:yyyyMMdd-HHmmss}.bmp";
            BmpWriter.Save24(Path.Combine(debugDirectory, name), crop);
        }
        catch
        {
            // Debug capture must never break the app.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
