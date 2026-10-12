using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>
/// Runs detection attempts: samples the live DBD loading screen at a modest
/// cadence (default ~0.9s), OCRs the small normalized map/realm regions,
/// matches with confidence, and stops immediately on a confirmed read.
/// Two modes: single-shot (internal <see cref="DetectionPolicy.TimeoutSeconds"/>
/// timeout) and continuous toggle mode (scans until the caller's token is
/// cancelled). In both modes: no overlapping attempts, full disposal of
/// captures, OCR fully idle outside the scan, and the DBD window handle is
/// re-read every tick. A stop always wins over a read that completes at the
/// same moment. Disposing the orchestrator stops an in-flight scan.
/// When a scan ends without success (failure, user stop, DBD exit or
/// disposal) and Debug Mode is enabled, the crops, OCR text and match
/// diagnostic of the most recent failed sample — kept in memory during the
/// scan — are saved locally with bounded retention (see <see cref="DebugCropStore"/>).
/// If no OCR'd sample failed, the most recent accepted-but-unconfirmed one is
/// saved instead, labelled as such. No fresh frame is captured after the fact.
/// <para>
/// Only frames of DBD itself are read. The capture copies whatever is on
/// screen at DBD's position, so when an "in front" predicate is given, a tick
/// where it says the screen there does not show DBD (another window covers a
/// DBD that is not the foreground window, e.g. alt-tabbed in borderless
/// windowed mode; DBD minimized) is a pause: nothing is captured or OCR'd,
/// nothing counts as a sample, nothing is kept as Debug Mode evidence, and the
/// scan keeps waiting at the normal cadence. Each paused tick still raises
/// <see cref="SampleEvaluated"/> (<see cref="DetectionSampleInfo.Paused"/>,
/// <see cref="NotForegroundDiagnostic"/>) so the UI can say why nothing is
/// read, unless the scan was stopped meanwhile.
/// </para>
/// </summary>
public sealed class DetectionOrchestrator : IDisposable
{
    /// <summary>Diagnostic of a paused tick: DBD was not in front (not the foreground window, and covered), so nothing was captured.</summary>
    public const string NotForegroundDiagnostic = "Scan paused — Dead by Daylight is not the active window.";

    private readonly IOcrEngine _ocr;
    private readonly MapMatchingService _matcher;
    private readonly RealmDatabaseService _database;
    private readonly IWindowCaptureService _capture;
    private readonly Func<IntPtr, DbdClientBounds?> _boundsProvider;
    private readonly DetectionPolicy _policy;

    // Cancelled by Dispose and linked into every scan. Deliberately never
    // disposed: a scan starting concurrently with Dispose may still read its
    // token, and a plain CTS holds no unmanaged resources.
    private readonly CancellationTokenSource _disposeCts = new();
    private int _running;
    private volatile bool _disposed;

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

    /// <summary>
    /// Sample index (1-based) after each evaluated sample (not raised for a
    /// paused tick, which takes no sample). Kept for compatibility; prefer <see cref="SampleEvaluated"/>.
    /// </summary>
    public event EventHandler<int>? SampleTaken;

    /// <summary>
    /// Raised after every evaluated sample with its match outcome, and for every
    /// paused tick (DBD not in front; <see cref="DetectionSampleInfo.Paused"/>),
    /// on the scan's thread and outside any lock. Exceptions thrown by
    /// subscribers are swallowed and never affect detection or other subscribers.
    /// </summary>
    public event EventHandler<DetectionSampleInfo>? SampleEvaluated;

    /// <summary>
    /// Run a detection attempt against a fixed DBD window handle.
    /// Prefer the <see cref="DetectAsync(Func{IntPtr}, Func{bool}?, string?, bool, CancellationToken)"/>
    /// overload so the scan follows window-handle changes.
    /// </summary>
    public Task<DetectionResult> DetectAsync(
        IntPtr dbdWindow,
        Func<bool>? debugMode = null,
        string? debugDirectory = null,
        bool continuous = false,
        CancellationToken ct = default)
    {
        return DetectAsync(() => dbdWindow, debugMode, debugDirectory, continuous, ct);
    }

    /// <summary>
    /// Run a detection attempt against the live DBD window, re-reading the
    /// window handle from <paramref name="windowProvider"/> every tick (a zero
    /// handle counts as "window not available" for that tick only).
    /// When <paramref name="continuous"/> is true there is no internal timeout:
    /// sampling continues until confidence succeeds or <paramref name="ct"/> is
    /// cancelled (toggle mode — the caller stops the scan). A cancelled
    /// <paramref name="ct"/> (or disposal) yields <see cref="DetectionResult.WasStopped"/>.
    /// Every frame is read whether or not DBD is in front; prefer the overload
    /// with an "in front" predicate.
    /// </summary>
    public Task<DetectionResult> DetectAsync(
        Func<IntPtr> windowProvider,
        Func<bool>? debugMode = null,
        string? debugDirectory = null,
        bool continuous = false,
        CancellationToken ct = default)
    {
        return DetectAsync(windowProvider, null, debugMode, debugDirectory, continuous, ct);
    }

    /// <summary>
    /// Like <see cref="DetectAsync(Func{IntPtr}, Func{bool}?, string?, bool, CancellationToken)"/>,
    /// but only frames of DBD itself are read: <paramref name="isInFront"/>
    /// says whether the screen at the tick's DBD window shows DBD itself (it is
    /// the foreground window, or nothing covers the areas read; null: no
    /// check). A tick where it does not (or the predicate throws) is a pause:
    /// nothing is captured or OCR'd, nothing is added to the aggregator or kept
    /// as Debug Mode evidence, and <see cref="SampleEvaluated"/> reports
    /// <see cref="NotForegroundDiagnostic"/> (not raised once the scan is
    /// stopped). The predicate is asked again right after the capture, so a
    /// frame taken while DBD was being covered is dropped too.
    /// </summary>
    public async Task<DetectionResult> DetectAsync(
        Func<IntPtr> windowProvider,
        Func<IntPtr, bool>? isInFront,
        Func<bool>? debugMode,
        string? debugDirectory,
        bool continuous = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(windowProvider);
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, _disposeCts.Token);

            var aggregator = new DetectionAggregator(_policy);
            int sample = 0;

            // Debug Mode: evidence of the most recent failed sample (owned here).
            DetectionSampleEvidence? debugEvidence = null;

            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    DetectionSampleEvidence? evidence;
                    try
                    {
                        evidence = await TakeSampleAsync(windowProvider, isInFront, sample + 1, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break; // stopped mid-sample (timeout, user stop or disposal), not an error
                    }

                    if (evidence is null)
                    {
                        // DBD is not in front: no sample and no evidence. Say why, keep waiting.
                        if (linked.Token.IsCancellationRequested)
                        {
                            break; // stopped meanwhile: no "paused" news after the stop
                        }

                        RaisePausedEvent(sample);
                    }
                    else
                    {
                        sample++;
                        MapMatchResult match = evidence.Match;
                        aggregator.AddSample(match);
                        evidence.Accepted = DetectionAggregator.IsAccepted(match, _policy);

                        if (IsDebugEnabled(debugMode) && ShouldKeepForDebug(evidence, debugEvidence))
                        {
                            debugEvidence?.Dispose();
                            debugEvidence = evidence;
                        }
                        else
                        {
                            evidence.Dispose();
                        }

                        RaiseSampleEvents(sample, match);

                        if (aggregator.TryGetEarlyResult(out DetectionResult? early) && early is not null)
                        {
                            if (linked.Token.IsCancellationRequested)
                            {
                                break; // a stop that raced this read wins
                            }

                            return early;
                        }
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

                DetectionResult final = ct.IsCancellationRequested || _disposeCts.IsCancellationRequested
                    // Stopped by the caller (toggle off, DBD exit) or by disposal: not a detection failure.
                    ? new DetectionResult(false, null, null, 0.0, aggregator.SampleCount, "Scan stopped.", WasStopped: true)
                    : aggregator.Finalize();

                if (!final.Success && debugEvidence is not null && !string.IsNullOrWhiteSpace(debugDirectory)
                    && IsDebugEnabled(debugMode))
                {
                    DebugCropSet? saved = DebugCropStore.TrySave(debugDirectory, debugEvidence, final);
                    if (saved is not null)
                    {
                        final = final with { DebugCropPath = saved.SummaryPath, DebugCropFiles = saved.Files };
                    }
                }

                return final;
            }
            finally
            {
                debugEvidence?.Dispose();
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// One tick: the sample, or null when DBD is not in front (checked before
    /// the capture and again right after it, so a frame taken while DBD was
    /// being covered is dropped unread). A zero handle is not a pause but a
    /// "window not available" sample, as without an "in front" predicate.
    /// </summary>
    private async Task<DetectionSampleEvidence?> TakeSampleAsync(
        Func<IntPtr> windowProvider, Func<IntPtr, bool>? isInFront, int index, CancellationToken ct)
    {
        IntPtr hwnd = ReadWindow(windowProvider);
        if (hwnd != IntPtr.Zero && !IsInFront(isInFront, hwnd))
        {
            return null; // nothing captured: the screen at DBD's position shows another window
        }

        var evidence = new DetectionSampleEvidence(index, hwnd);
        try
        {
            DbdClientBounds? bounds = null;
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    bounds = _boundsProvider(hwnd);
                }
                catch
                {
                    // Provider failures mean "no window".
                }
            }

            if (bounds is null || bounds.IsEmpty)
            {
                evidence.Match = MapMatchResult.Failure("DBD window is not available.");
                return evidence;
            }

            IntRegion mapPx = DbdLoadingScreenRegions.ToPixels(DbdLoadingScreenRegions.MapName, bounds.Width, bounds.Height);
            IntRegion realmPx = DbdLoadingScreenRegions.ToPixels(DbdLoadingScreenRegions.RealmName, bounds.Width, bounds.Height);

            IReadOnlyList<CapturedImage?> crops = _capture.CaptureRegions(hwnd, new[] { mapPx, realmPx });
            for (int i = 0; i < crops.Count; i++)
            {
                switch (i)
                {
                    case 0:
                        evidence.MapCrop = crops[i];
                        break;
                    case 1:
                        evidence.RealmCrop = crops[i];
                        break;
                    default:
                        crops[i]?.Dispose();
                        break;
                }
            }

            if (!IsInFront(isInFront, hwnd))
            {
                // DBD was covered while capturing: the crops may show the other window.
                evidence.Dispose();
                return null;
            }

            if (evidence.MapCrop is null || evidence.RealmCrop is null)
            {
                evidence.Match = MapMatchResult.Failure("Could not capture the loading screen.");
                return evidence;
            }

            evidence.RawMapText = await OcrToTextAsync(evidence.MapCrop, ct).ConfigureAwait(false);
            evidence.RawRealmText = await OcrToTextAsync(evidence.RealmCrop, ct).ConfigureAwait(false);

            // A stop that arrived during OCR must not turn into a detection.
            ct.ThrowIfCancellationRequested();

            MapMatchResult raw = _matcher.Match(evidence.RawMapText + "\n" + evidence.RawRealmText, _database.Realms);
            evidence.RawMatch = raw;
            evidence.Match = raw;
            if (DetectionAggregator.IsAccepted(raw, _policy))
            {
                return evidence;
            }

            // One preprocessed retry per sample: for a failed read, and for a
            // weak success the aggregator would discard anyway. Keep the stronger.
            try
            {
                evidence.PreprocessedMapCrop = ImagePreprocessingService.PreprocessForOcr(evidence.MapCrop);
                evidence.PreprocessedRealmCrop = ImagePreprocessingService.PreprocessForOcr(evidence.RealmCrop);
                evidence.PreprocessedMapText = await OcrToTextAsync(evidence.PreprocessedMapCrop, ct).ConfigureAwait(false);
                evidence.PreprocessedRealmText = await OcrToTextAsync(evidence.PreprocessedRealmCrop, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                MapMatchResult retry = _matcher.Match(
                    evidence.PreprocessedMapText + "\n" + evidence.PreprocessedRealmText, _database.Realms);
                evidence.RetryMatch = retry;
                evidence.Match = Stronger(raw, retry);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Keep the raw result.
            }

            return evidence;
        }
        catch
        {
            evidence.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Debug Mode: whether <paramref name="candidate"/> replaces the evidence
    /// kept so far. Only samples that were actually OCR'd (both crops captured)
    /// qualify, so the window-gone / capture-failed ticks at the end of a scan
    /// (e.g. after DBD exits) never displace the last real read. A failed sample
    /// always replaces; an accepted-but-unconfirmed sample is kept only while no
    /// failed sample has been seen, so it can never displace a failure.
    /// </summary>
    internal static bool ShouldKeepForDebug(DetectionSampleEvidence candidate, DetectionSampleEvidence? kept)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.WasRecognized)
        {
            return false;
        }

        return !candidate.Accepted || kept is null || kept.Accepted;
    }

    /// <summary>The stronger of two reads of one frame: a success beats a failure, then higher confidence wins; ties keep <paramref name="raw"/>.</summary>
    internal static MapMatchResult Stronger(MapMatchResult raw, MapMatchResult retry)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(retry);
        return retry.Success && (!raw.Success || retry.Confidence > raw.Confidence) ? retry : raw;
    }

    /// <summary>Whether <paramref name="hwnd"/> is in front (always, without a predicate). A predicate that throws counts as "not in front".</summary>
    private static bool IsInFront(Func<IntPtr, bool>? isInFront, IntPtr hwnd)
    {
        if (isInFront is null)
        {
            return true;
        }

        try
        {
            return isInFront(hwnd);
        }
        catch
        {
            return false; // never read a frame that can't be vouched for
        }
    }

    private static IntPtr ReadWindow(Func<IntPtr> windowProvider)
    {
        try
        {
            return windowProvider();
        }
        catch
        {
            // Provider failures mean "no window" for this tick.
            return IntPtr.Zero;
        }
    }

    private static bool IsDebugEnabled(Func<bool>? debugMode)
    {
        try
        {
            return debugMode?.Invoke() ?? false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> OcrToTextAsync(CapturedImage image, CancellationToken ct)
    {
        try
        {
            var lines = await _ocr.RecognizeAsync(image, ct).ConfigureAwait(false);
            return string.Join("\n", lines.Select(l => l.Text));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void RaiseSampleEvents(int index, MapMatchResult match)
    {
        RaiseSafely(SampleTaken, index);
        RaiseSafely(SampleEvaluated, new DetectionSampleInfo(
            index, match.Success, match.MapName, match.RealmName, match.Confidence, match.Diagnostic));
    }

    /// <summary>A paused tick: no sample was taken, so only <see cref="SampleEvaluated"/> is raised.</summary>
    private void RaisePausedEvent(int samplesSoFar)
    {
        RaiseSafely(SampleEvaluated, new DetectionSampleInfo(
            samplesSoFar, false, null, null, 0.0, NotForegroundDiagnostic) { Paused = true });
    }

    private void RaiseSafely<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
        {
            return;
        }

        foreach (Delegate subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)subscriber)(this, args);
            }
            catch
            {
                // UI subscribers must never break detection (or each other).
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _disposeCts.Cancel();
        }
        catch
        {
            // Best effort: the scan also observes _disposed via its linked token.
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Everything one detection sample produced: the crops exactly as OCR'd, the
/// preprocessed retry crops (when the retry ran), the OCR text and the match
/// results. Owns its images; kept beyond the sample only for Debug Mode.
/// </summary>
internal sealed class DetectionSampleEvidence : IDisposable
{
    public DetectionSampleEvidence(int index, IntPtr window)
    {
        Index = index;
        Window = window;
    }

    public int Index { get; }

    public IntPtr Window { get; }

    public CapturedImage? MapCrop { get; set; }

    public CapturedImage? RealmCrop { get; set; }

    public CapturedImage? PreprocessedMapCrop { get; set; }

    public CapturedImage? PreprocessedRealmCrop { get; set; }

    public string? RawMapText { get; set; }

    public string? RawRealmText { get; set; }

    public string? PreprocessedMapText { get; set; }

    public string? PreprocessedRealmText { get; set; }

    public MapMatchResult? RawMatch { get; set; }

    public MapMatchResult? RetryMatch { get; set; }

    /// <summary>The sample's verdict (the stronger of the raw and retry reads).</summary>
    public MapMatchResult Match { get; set; } = MapMatchResult.Failure("No sample was taken.");

    /// <summary>
    /// Whether the aggregator accepted <see cref="Match"/> (a usable read that
    /// still did not confirm on its own). False for a failed sample.
    /// </summary>
    public bool Accepted { get; set; }

    /// <summary>Both crops were captured and OCR'd (false for window-gone or capture-failed ticks).</summary>
    public bool WasRecognized => MapCrop is not null && RealmCrop is not null && RawMatch is not null;

    public void Dispose()
    {
        MapCrop?.Dispose();
        RealmCrop?.Dispose();
        PreprocessedMapCrop?.Dispose();
        PreprocessedRealmCrop?.Dispose();
        MapCrop = null;
        RealmCrop = null;
        PreprocessedMapCrop = null;
        PreprocessedRealmCrop = null;
    }
}
