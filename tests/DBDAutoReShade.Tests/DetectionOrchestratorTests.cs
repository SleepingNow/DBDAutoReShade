using System.Collections.Concurrent;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

public sealed class DetectionOrchestratorTests : IDisposable
{
    private const string StrongText = "FRACTURED COWSHED\nCOLDWIND FARM";

    private readonly string _debugDir = Path.Combine(Path.GetTempPath(), "dbd-orch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_debugDir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private sealed class FakeOcr : IOcrEngine
    {
        private readonly string _text;
        private readonly bool _available;

        public FakeOcr(string text, bool available = true)
        {
            _text = text;
            _available = available;
        }

        public string Name => "Fake";
        public bool IsAvailable => _available;
        public string? UnavailableReason => _available ? null : "unavailable";

        public Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(CapturedImage image, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OcrTextLine>>(
                _text.Split('\n').Select(l => new OcrTextLine(l, null)).ToList());
    }

    /// <summary>OCR fake driven by the 1-based call number (raw map, raw realm, then preprocessed map, realm per sample).</summary>
    private sealed class ScriptedOcr : IOcrEngine
    {
        private readonly Func<int, string> _script;
        private int _calls;

        public ScriptedOcr(Func<int, string> script) => _script = script;

        public int Calls => Volatile.Read(ref _calls);
        public string Name => "Scripted";
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(CapturedImage image, CancellationToken ct)
        {
            string text = _script(Interlocked.Increment(ref _calls));
            return Task.FromResult<IReadOnlyList<OcrTextLine>>(
                text.Split('\n').Select(l => new OcrTextLine(l, null)).ToList());
        }
    }

    private sealed class FakeCapture : IWindowCaptureService
    {
        public CapturedImage? ImageToReturn { get; set; } = DummyImage();

        public ConcurrentQueue<IntPtr> Windows { get; } = new();

        public ConcurrentQueue<IntRegion> Regions { get; } = new();

        /// <summary>Optional per-call failure: return true to make that region's capture fail (null).</summary>
        public Func<IntPtr, IntRegion, bool>? FailRegion { get; set; }

        /// <summary>Runs inside every region capture (e.g. focus changes mid-capture).</summary>
        public Action? OnCapture { get; set; }

        public static CapturedImage DummyImage() =>
            new(800, 200, Enumerable.Repeat((byte)128, 800 * 200 * 4).ToArray());

        public CapturedImage? CaptureRegion(IntPtr hwnd, IntRegion region)
        {
            Windows.Enqueue(hwnd);
            Regions.Enqueue(region);
            OnCapture?.Invoke();
            if (ImageToReturn is null || (FailRegion?.Invoke(hwnd, region) ?? false))
            {
                return null;
            }

            int w = Math.Min(region.Width, ImageToReturn.Width);
            int h = Math.Min(region.Height, ImageToReturn.Height);
            return new CapturedImage(w, h, new byte[w * h * 4]);
        }
    }

    private static RealmDatabaseService Database()
    {
        var db = new RealmDatabaseService();
        db.LoadAsync(null).GetAwaiter().GetResult();
        return db;
    }

    private static readonly DbdClientBounds FakeBounds = new(0, 0, 1920, 1080, 96);

    private static DetectionOrchestrator Create(
        IOcrEngine ocr, DetectionPolicy policy, FakeCapture? capture = null,
        Func<IntPtr, DbdClientBounds?>? bounds = null) =>
        new(ocr, new MapMatchingService(), Database(), capture ?? new FakeCapture(), policy, bounds ?? (_ => FakeBounds));

    /// <summary>
    /// Deterministic user stop: cancel <paramref name="cts"/> as soon as sample
    /// <paramref name="index"/> has been evaluated (no wall-clock timers, so a
    /// slow first sample under cold JIT or parallel tests cannot race the stop).
    /// </summary>
    private static void StopAfterSample(DetectionOrchestrator orchestrator, CancellationTokenSource cts, int index) =>
        orchestrator.SampleEvaluated += (_, info) =>
        {
            if (info.Index >= index)
            {
                cts.Cancel();
            }
        };

    [Fact]
    public async Task SuccessfulRecognition_StopsImmediately()
    {
        var orchestrator = new DetectionOrchestrator(
            new FakeOcr(StrongText),
            new MapMatchingService(),
            Database(),
            new FakeCapture(),
            new DetectionPolicy(TimeoutSeconds: 10, SampleIntervalMs: 50),
            _ => FakeBounds);

        DetectionResult r = await orchestrator.DetectAsync(new IntPtr(1234));
        Assert.True(r.Success);
        Assert.Equal("Coldwind Farm", r.RealmName);
        Assert.False(orchestrator.IsDetecting); // stopped, resources released
    }

    [Fact]
    public async Task Timeout_StopsDetection_WithFailure()
    {
        var orchestrator = new DetectionOrchestrator(
            new FakeOcr("PRESS START"),
            new MapMatchingService(),
            Database(),
            new FakeCapture(),
            new DetectionPolicy(TimeoutSeconds: 1, SampleIntervalMs: 100),
            _ => FakeBounds);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        DetectionResult r = await orchestrator.DetectAsync(new IntPtr(1234));
        sw.Stop();

        Assert.False(r.Success);
        Assert.False(r.WasStopped); // timeout is a failure, not a user stop
        Assert.True(r.SampleCount >= 2);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False(orchestrator.IsDetecting);
    }

    [Fact]
    public async Task ContinuousMode_IgnoresTimeout_StopsOnCancel()
    {
        var orchestrator = new DetectionOrchestrator(
            new FakeOcr("PRESS START"),
            new MapMatchingService(),
            Database(),
            new FakeCapture(),
            new DetectionPolicy(TimeoutSeconds: 1, SampleIntervalMs: 50),
            _ => FakeBounds);

        int samples = 0;
        orchestrator.SampleTaken += (_, _) => Interlocked.Increment(ref samples);
        using var cts = new CancellationTokenSource();
        Task<DetectionResult> run = orchestrator.DetectAsync(new IntPtr(1234), continuous: true, ct: cts.Token);
        await Task.Delay(1600); // well past the 1 s single-shot timeout
        Assert.True(orchestrator.IsDetecting); // still scanning: no timeout in toggle mode

        // Count real samples rather than assuming a throughput: a loaded test host can
        // starve the loop, but it must keep sampling after the old timeout.
        int atTimeout = Volatile.Read(ref samples);
        for (int i = 0; i < 100 && Volatile.Read(ref samples) < atTimeout + 3; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(Volatile.Read(ref samples) >= atTimeout + 3); // kept sampling past the timeout
        cts.Cancel();
        DetectionResult r = await run;

        Assert.False(r.Success);
        Assert.True(r.WasStopped);
        Assert.True(r.SampleCount >= atTimeout + 3);
        Assert.False(orchestrator.IsDetecting);
    }

    // Replaces the old UserStop_SkipsDebugCrop test: that encoded the bug that made
    // Debug Mode dead in production (the app only runs continuous scans, which end
    // by user stop, so no crop was ever written). A stopped scan now saves the
    // most recent failed sample's crops — the frames that were actually OCR'd.
    [Fact]
    public async Task UserStop_WithDebugMode_SavesFailedSampleCropSet()
    {
        var capture = new FakeCapture();
        DetectionOrchestrator orchestrator = Create(
            new FakeOcr("PRESS START"), new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 50), capture);

        using var cts = new CancellationTokenSource();
        StopAfterSample(orchestrator, cts, 1);
        DetectionResult r = await orchestrator.DetectAsync(
            new IntPtr(1234), debugMode: () => true, debugDirectory: _debugDir,
            continuous: true, ct: cts.Token);

        Assert.True(r.WasStopped);
        Assert.Equal(1, r.SampleCount);
        Assert.NotNull(r.DebugCropPath);
        Assert.True(File.Exists(r.DebugCropPath));
        Assert.EndsWith(".txt", r.DebugCropPath);

        string[] bmps = Directory.GetFiles(_debugDir, "*.bmp");
        Assert.Equal(4, bmps.Length); // raw map + realm, preprocessed map + realm
        Assert.Contains(bmps, f => f.EndsWith("-map.bmp", StringComparison.Ordinal));
        Assert.Contains(bmps, f => f.EndsWith("-realm.bmp", StringComparison.Ordinal));
        Assert.Contains(bmps, f => f.EndsWith("-map-preprocessed.bmp", StringComparison.Ordinal));
        Assert.Contains(bmps, f => f.EndsWith("-realm-preprocessed.bmp", StringComparison.Ordinal));
        Assert.Equal(5, r.DebugCropFiles.Count);
        Assert.All(r.DebugCropFiles, f => Assert.True(File.Exists(f)));

        string summary = File.ReadAllText(r.DebugCropPath!);
        Assert.Contains("PRESS START", summary);
        Assert.Contains("stopped", summary);
        Assert.Contains("no match", summary);

        // The saved raw map crop is the crop that was OCR'd (fake capture: 800 x 173 at 1080p).
        byte[] mapBmp = File.ReadAllBytes(bmps.Single(f => f.EndsWith("-map.bmp", StringComparison.Ordinal)));
        Assert.Equal(800, BitConverter.ToInt32(mapBmp, 18));
        Assert.Equal(173, BitConverter.ToInt32(mapBmp, 22));

        // No fresh frame was captured after the fact: only the two OCR regions, ever.
        IntRegion mapPx = DbdLoadingScreenRegions.ToPixels(DbdLoadingScreenRegions.MapName, 1920, 1080);
        IntRegion realmPx = DbdLoadingScreenRegions.ToPixels(DbdLoadingScreenRegions.RealmName, 1920, 1080);
        Assert.All(capture.Regions, region => Assert.True(region == mapPx || region == realmPx)); // (no whole-frame capture exists)
    }

    [Fact]
    public async Task TimeoutFailure_WithDebugMode_SavesSetWithFailureReason()
    {
        DetectionOrchestrator orchestrator = Create(
            new FakeOcr("PRESS START"), new DetectionPolicy(TimeoutSeconds: 1, SampleIntervalMs: 100));

        DetectionResult r = await orchestrator.DetectAsync(
            new IntPtr(1234), debugMode: () => true, debugDirectory: _debugDir);

        Assert.False(r.Success);
        Assert.False(r.WasStopped);
        Assert.NotNull(r.DebugCropPath);
        Assert.Contains(r.FailureReason!, File.ReadAllText(r.DebugCropPath!));
    }

    [Fact]
    public async Task DebugModeOff_WritesNothing()
    {
        DetectionOrchestrator orchestrator = Create(
            new FakeOcr("PRESS START"), new DetectionPolicy(TimeoutSeconds: 1, SampleIntervalMs: 50));

        using var cts = new CancellationTokenSource();
        StopAfterSample(orchestrator, cts, 1);
        DetectionResult stopped = await orchestrator.DetectAsync(
            new IntPtr(1234), debugMode: () => false, debugDirectory: _debugDir, continuous: true, ct: cts.Token);
        DetectionResult failed = await orchestrator.DetectAsync(
            new IntPtr(1234), debugMode: () => false, debugDirectory: _debugDir);

        Assert.True(stopped.WasStopped);
        Assert.False(failed.Success);
        Assert.Null(stopped.DebugCropPath);
        Assert.Null(failed.DebugCropPath);
        Assert.Empty(failed.DebugCropFiles);
        Assert.False(Directory.Exists(_debugDir));
    }

    [Fact]
    public async Task Success_WithDebugMode_WritesNothing()
    {
        DetectionOrchestrator orchestrator = Create(
            new FakeOcr(StrongText), new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 50));

        DetectionResult r = await orchestrator.DetectAsync(
            new IntPtr(1234), debugMode: () => true, debugDirectory: _debugDir);

        Assert.True(r.Success);
        Assert.Null(r.DebugCropPath);
        Assert.False(Directory.Exists(_debugDir));
    }

    [Fact]
    public async Task DebugRetention_DeletesOldestSets_KeepsNewest20()
    {
        // 22 older single-file sets (the pre-fix naming), oldest first.
        Directory.CreateDirectory(_debugDir);
        var old = new List<string>();
        for (int i = 0; i < 22; i++)
        {
            DateTime stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i);
            string path = Path.Combine(_debugDir, $"ocr-failure-{stamp:yyyyMMdd-HHmmss}.bmp");
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, stamp);
            old.Add(path);
        }

        DetectionOrchestrator orchestrator = Create(
            new FakeOcr("PRESS START"), new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 50));
        using var cts = new CancellationTokenSource();
        StopAfterSample(orchestrator, cts, 1);
        DetectionResult r = await orchestrator.DetectAsync(
            new IntPtr(1234), debugMode: () => true, debugDirectory: _debugDir, continuous: true, ct: cts.Token);

        Assert.NotNull(r.DebugCropPath);
        // New set + the 19 newest old sets survive; the 3 OLDEST were deleted.
        Assert.All(old.Take(3), p => Assert.False(File.Exists(p)));
        Assert.All(old.Skip(3), p => Assert.True(File.Exists(p)));
        Assert.All(r.DebugCropFiles, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public async Task DbdExit_WithDebugMode_SavesLastSampleThatWasOcrd()
    {
        // Ticks 1-2: DBD is up and the OCR reads "PRESS START" (failed samples with crops).
        // Tick 3: the window is closing; only the map region still captures (no OCR runs).
        // Ticks 4-5: the window is gone (no crops at all). Then the app cancels the scan.
        var dying = new IntPtr(5555);
        int reads = 0;
        IntPtr Window() => Interlocked.Increment(ref reads) switch
        {
            <= 2 => new IntPtr(1234),
            3 => dying,
            _ => IntPtr.Zero
        };
        IntRegion realmPx = DbdLoadingScreenRegions.ToPixels(DbdLoadingScreenRegions.RealmName, 1920, 1080);
        var capture = new FakeCapture { FailRegion = (hwnd, region) => hwnd == dying && region == realmPx };
        var infos = new ConcurrentQueue<DetectionSampleInfo>();
        DetectionOrchestrator orchestrator = Create(
            new FakeOcr("PRESS START"), new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20), capture);
        orchestrator.SampleEvaluated += (_, i) => infos.Enqueue(i);
        using var cts = new CancellationTokenSource();
        StopAfterSample(orchestrator, cts, 5);

        DetectionResult r = await orchestrator.DetectAsync(
            Window, debugMode: () => true, debugDirectory: _debugDir, continuous: true, ct: cts.Token);

        Assert.True(r.WasStopped);
        Assert.Equal(5, r.SampleCount);
        Assert.Contains("not available", infos.Last().Diagnostic); // the last ticks really had no window

        // The crops of the last OCR'd sample (#2) survived the crop-less exit ticks.
        Assert.NotNull(r.DebugCropPath);
        Assert.Contains(r.DebugCropFiles, f => f.EndsWith("-map.bmp", StringComparison.Ordinal));
        Assert.Contains(r.DebugCropFiles, f => f.EndsWith("-realm.bmp", StringComparison.Ordinal));
        Assert.Contains(r.DebugCropFiles, f => f.EndsWith("-map-preprocessed.bmp", StringComparison.Ordinal));
        Assert.Contains(r.DebugCropFiles, f => f.EndsWith("-realm-preprocessed.bmp", StringComparison.Ordinal));
        Assert.All(r.DebugCropFiles, f => Assert.True(File.Exists(f)));

        string summary = File.ReadAllText(r.DebugCropPath!);
        Assert.Contains("this set is sample #2, the most recent failed one", summary);
        Assert.Contains("0x4D2", summary); // window 1234, not the dying or zero handle
        Assert.Contains("PRESS START", summary);
        Assert.DoesNotContain("no crops were captured", summary);
        Assert.DoesNotContain("not available", summary);
    }

    [Fact]
    public async Task FailedThenAccepted_WithDebugMode_SavesTheFailedSample()
    {
        // Sample 1 (raw + retry, calls 1-4) fails; sample 2 reads the map exactly
        // (accepted) but cannot confirm on its own under this policy.
        var ocr = new ScriptedOcr(call => call <= 4 ? "PRESS START" : StrongText);
        var infos = new ConcurrentQueue<DetectionSampleInfo>();
        DetectionOrchestrator orchestrator = Create(ocr, new DetectionPolicy(
            TimeoutSeconds: 5, SampleIntervalMs: 20, ConfirmConfidence: 1.01, MinAgreeingSamples: 3));
        orchestrator.SampleEvaluated += (_, i) => infos.Enqueue(i);
        using var cts = new CancellationTokenSource();
        StopAfterSample(orchestrator, cts, 2);

        DetectionResult r = await orchestrator.DetectAsync(
            new IntPtr(1234), debugMode: () => true, debugDirectory: _debugDir, continuous: true, ct: cts.Token);

        Assert.True(r.WasStopped);
        Assert.Equal(2, r.SampleCount);
        DetectionSampleInfo[] all = infos.ToArray();
        Assert.False(all[0].Success);
        Assert.True(all[1].Success); // the later read was accepted...
        Assert.Equal("Fractured Cowshed", all[1].MapName);

        // ...but the saved set is still the most recent FAILED sample.
        Assert.NotNull(r.DebugCropPath);
        string summary = File.ReadAllText(r.DebugCropPath!);
        Assert.Contains("this set is sample #1, the most recent failed one", summary);
        Assert.Contains("PRESS START", summary);
        Assert.DoesNotContain("COWSHED", summary);
        Assert.Equal(5, r.DebugCropFiles.Count); // sample 1 ran the retry: 4 crops + summary
    }

    [Fact]
    public async Task AcceptedOnly_WithDebugMode_SavesSampleLabelledAccepted()
    {
        // A stop after a single accepted read that never confirmed: no sample failed.
        DetectionOrchestrator orchestrator = Create(new FakeOcr(StrongText), new DetectionPolicy(
            TimeoutSeconds: 5, SampleIntervalMs: 20, ConfirmConfidence: 1.01, MinAgreeingSamples: 3));
        using var cts = new CancellationTokenSource();
        StopAfterSample(orchestrator, cts, 1);

        DetectionResult r = await orchestrator.DetectAsync(
            new IntPtr(1234), debugMode: () => true, debugDirectory: _debugDir, continuous: true, ct: cts.Token);

        Assert.True(r.WasStopped);
        Assert.NotNull(r.DebugCropPath);
        string summary = File.ReadAllText(r.DebugCropPath!);
        Assert.Contains("sample #1, the most recent sample (accepted but not confirmed", summary);
        Assert.DoesNotContain("the most recent failed one", summary);
        Assert.Contains("FRACTURED COWSHED", summary);
        Assert.Equal(3, r.DebugCropFiles.Count); // accepted raw read: no retry, so 2 crops + summary
    }

    [Fact]
    public void ShouldKeepForDebug_FailuresWin_UnrecognizedSamplesNeverReplace()
    {
        static DetectionSampleEvidence Sample(int index, bool accepted, bool recognized = true)
        {
            var e = new DetectionSampleEvidence(index, new IntPtr(1)) { Accepted = accepted };
            if (recognized)
            {
                e.MapCrop = FakeCapture.DummyImage();
                e.RealmCrop = FakeCapture.DummyImage();
                e.RawMatch = e.Match;
            }

            return e;
        }

        using var failed = Sample(1, accepted: false);
        using var accepted = Sample(2, accepted: true);
        using var noWindow = Sample(3, accepted: false, recognized: false);
        using var partial = Sample(4, accepted: false, recognized: false);
        partial.MapCrop = FakeCapture.DummyImage(); // one crop captured, never OCR'd

        Assert.True(DetectionOrchestrator.ShouldKeepForDebug(failed, null));
        Assert.True(DetectionOrchestrator.ShouldKeepForDebug(failed, accepted)); // a failure replaces an accepted read
        Assert.True(DetectionOrchestrator.ShouldKeepForDebug(failed, failed)); // a newer failure replaces an older one
        Assert.True(DetectionOrchestrator.ShouldKeepForDebug(accepted, null));
        Assert.True(DetectionOrchestrator.ShouldKeepForDebug(accepted, accepted));
        Assert.False(DetectionOrchestrator.ShouldKeepForDebug(accepted, failed)); // never displaces a failure
        Assert.False(DetectionOrchestrator.ShouldKeepForDebug(noWindow, failed));
        Assert.False(DetectionOrchestrator.ShouldKeepForDebug(noWindow, null));
        Assert.False(DetectionOrchestrator.ShouldKeepForDebug(partial, failed));
    }

    [Fact]
    public async Task WeakRawRead_RunsPreprocessedRetry_AndKeepsStrongerResult()
    {
        // Raw OCR: a one-letter misread (success, but below AcceptConfidence).
        // Preprocessed retry (calls 3-4): the exact text.
        var ocr = new ScriptedOcr(call => (call % 4) switch
        {
            1 => "FRACTURED COWSHFD",
            3 => "FRACTURED COWSHED",
            _ => "COLDWIND FARM"
        });
        var infos = new ConcurrentQueue<DetectionSampleInfo>();
        DetectionOrchestrator orchestrator = Create(
            ocr, new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20, AcceptConfidence: 0.97, ConfirmConfidence: 0.98));
        orchestrator.SampleEvaluated += (_, i) => infos.Enqueue(i);

        DetectionResult r = await orchestrator.DetectAsync(new IntPtr(1234));

        Assert.True(r.Success);
        Assert.Equal(1, r.SampleCount); // the first frame was rescued by the retry, not wasted
        Assert.Equal("Fractured Cowshed", r.MapName);
        Assert.Equal(4, ocr.Calls);
        DetectionSampleInfo first = Assert.Single(infos);
        Assert.Equal(1.0, first.Confidence, 4);
    }

    [Fact]
    public async Task WeakRawRead_WorseRetry_KeepsRawResult()
    {
        var ocr = new ScriptedOcr(call => (call % 4) switch
        {
            1 => "FRACTURED COWSHFD",
            2 => "COLDWIND FARM",
            _ => "PRESS START"
        });
        var infos = new ConcurrentQueue<DetectionSampleInfo>();
        using var cts = new CancellationTokenSource();
        DetectionOrchestrator orchestrator = Create(
            ocr, new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20, AcceptConfidence: 0.97, ConfirmConfidence: 0.98));
        orchestrator.SampleEvaluated += (_, i) =>
        {
            infos.Enqueue(i);
            cts.Cancel();
        };

        DetectionResult r = await orchestrator.DetectAsync(new IntPtr(1234), continuous: true, ct: cts.Token);

        Assert.True(r.WasStopped);
        Assert.Equal(4, ocr.Calls); // the retry ran
        DetectionSampleInfo first = Assert.Single(infos);
        Assert.True(first.Success); // weak raw read kept over the failed retry
        Assert.Equal("Fractured Cowshed", first.MapName);
        Assert.InRange(first.Confidence, 0.9, 0.97);
    }

    [Fact]
    public void Stronger_PrefersSuccessThenConfidence()
    {
        var fail = MapMatchResult.Failure("nope");
        var weak = new MapMatchResult(true, "A", "R", 0.80, MatchMethod.MapName);
        var strong = new MapMatchResult(true, "B", "R", 0.95, MatchMethod.MapName);
        var equal = new MapMatchResult(true, "C", "R", 0.80, MatchMethod.MapName);

        Assert.Same(weak, DetectionOrchestrator.Stronger(weak, fail));
        Assert.Same(strong, DetectionOrchestrator.Stronger(weak, strong));
        Assert.Same(strong, DetectionOrchestrator.Stronger(strong, weak));
        Assert.Same(weak, DetectionOrchestrator.Stronger(fail, weak));
        Assert.Same(weak, DetectionOrchestrator.Stronger(weak, equal)); // ties keep the raw read
    }

    [Fact]
    public async Task WindowProvider_IsReReadEveryTick()
    {
        int reads = 0;
        IntPtr Window() => Interlocked.Increment(ref reads) <= 2 ? IntPtr.Zero : new IntPtr(4321);
        var capture = new FakeCapture();
        DetectionOrchestrator orchestrator = Create(
            new FakeOcr(StrongText), new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20), capture);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        DetectionResult r = await orchestrator.DetectAsync(Window, debugMode: null, debugDirectory: null, continuous: true, ct: cts.Token);

        Assert.True(r.Success);
        Assert.Equal(3, r.SampleCount); // two ticks without a window, then the new handle
        Assert.Equal(3, Volatile.Read(ref reads));
        Assert.NotEmpty(capture.Windows);
        Assert.All(capture.Windows, w => Assert.Equal(new IntPtr(4321), w));
    }

    [Fact]
    public async Task WindowProvider_FollowsHandleChange()
    {
        int reads = 0;
        IntPtr Window() => Interlocked.Increment(ref reads) <= 2 ? new IntPtr(1111) : new IntPtr(2222);
        var capture = new FakeCapture();
        DetectionOrchestrator orchestrator = Create(
            new FakeOcr(StrongText), new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20), capture,
            hwnd => hwnd == new IntPtr(2222) ? FakeBounds : null); // the old window is gone

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        DetectionResult r = await orchestrator.DetectAsync(Window, continuous: true, ct: cts.Token);

        Assert.True(r.Success);
        Assert.All(capture.Windows, w => Assert.Equal(new IntPtr(2222), w));
    }

    [Fact]
    public async Task StopDuringOcr_ReturnsWasStopped_NotSuccess()
    {
        using var cts = new CancellationTokenSource();
        var ocr = new ScriptedOcr(call =>
        {
            if (call == 2)
            {
                cts.Cancel(); // user stop lands while the realm crop is being recognized
            }

            return StrongText;
        });
        DetectionOrchestrator orchestrator = Create(ocr, new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20));

        DetectionResult r = await orchestrator.DetectAsync(new IntPtr(1234), continuous: true, ct: cts.Token);

        Assert.False(r.Success);
        Assert.True(r.WasStopped);
        Assert.Null(r.RealmName);
    }

    [Fact]
    public async Task StopBeforeEvaluation_WinsOverEarlySuccess()
    {
        using var cts = new CancellationTokenSource();
        DetectionOrchestrator orchestrator = Create(new FakeOcr(StrongText), new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20));
        orchestrator.SampleEvaluated += (_, _) => cts.Cancel(); // stop arrives after the read, before acceptance

        DetectionResult r = await orchestrator.DetectAsync(new IntPtr(1234), continuous: true, ct: cts.Token);

        Assert.False(r.Success);
        Assert.True(r.WasStopped);
    }

    [Fact]
    public async Task SampleEvaluated_ReportsEverySample_AndSwallowsSubscriberExceptions()
    {
        // Sample 1 (calls 1-4: raw + retry) reads nothing useful; sample 2 reads the map.
        var ocr = new ScriptedOcr(call => call <= 4 ? "PRESS START" : StrongText);
        DetectionOrchestrator orchestrator = Create(ocr, new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20));
        var infos = new ConcurrentQueue<DetectionSampleInfo>();
        var taken = new ConcurrentQueue<int>();
        orchestrator.SampleEvaluated += (_, _) => throw new InvalidOperationException("subscriber bug");
        orchestrator.SampleEvaluated += (_, i) => infos.Enqueue(i);
        orchestrator.SampleTaken += (_, _) => throw new InvalidOperationException("subscriber bug");
        orchestrator.SampleTaken += (_, n) => taken.Enqueue(n);

        DetectionResult r = await orchestrator.DetectAsync(new IntPtr(1234));

        Assert.True(r.Success);
        Assert.Equal(new[] { 1, 2 }, taken.ToArray()); // compatibility event still raised
        DetectionSampleInfo[] all = infos.ToArray();
        Assert.Equal(2, all.Length);
        Assert.Equal(1, all[0].Index);
        Assert.False(all[0].Success);
        Assert.False(string.IsNullOrWhiteSpace(all[0].Diagnostic));
        Assert.Equal(2, all[1].Index);
        Assert.True(all[1].Success);
        Assert.Equal("Coldwind Farm", all[1].RealmName);
        Assert.Equal("Fractured Cowshed", all[1].MapName);
        Assert.Equal(1.0, all[1].Confidence, 4);
    }

    // ---------- F2: only frames of DBD itself are read ----------

    [Fact]
    public async Task DbdNotInFront_CapturesNothing_ReadsNothing_CountsNothing_KeepsNoDebugSet()
    {
        // Borderless DBD covered by another window: a screen capture would read
        // (and Debug Mode would save) that window's pixels and text.
        var ocr = new ScriptedOcr(_ => "PRESS START");
        var capture = new FakeCapture();
        var asked = new ConcurrentQueue<IntPtr>();
        var infos = new ConcurrentQueue<DetectionSampleInfo>();
        int taken = 0;
        DetectionOrchestrator orchestrator = Create(ocr, new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20), capture);
        orchestrator.SampleTaken += (_, _) => Interlocked.Increment(ref taken);
        using var cts = new CancellationTokenSource();
        orchestrator.SampleEvaluated += (_, i) =>
        {
            infos.Enqueue(i);
            if (infos.Count >= 3)
            {
                cts.Cancel(); // the user stops the scan while still in the other app
            }
        };

        DetectionResult r = await orchestrator.DetectAsync(
            () => new IntPtr(1234),
            isInFront: hwnd =>
            {
                asked.Enqueue(hwnd);
                return false;
            },
            debugMode: () => true, debugDirectory: _debugDir, continuous: true, ct: cts.Token);

        Assert.True(r.WasStopped);
        Assert.Equal(0, r.SampleCount); // nothing reached the aggregator
        Assert.Equal(0, ocr.Calls);
        Assert.Empty(capture.Regions);
        Assert.Equal(0, Volatile.Read(ref taken)); // no sample was taken
        Assert.Null(r.DebugCropPath); // no other window's pixels or text on disk
        Assert.Empty(r.DebugCropFiles);
        Assert.False(Directory.Exists(_debugDir));

        Assert.Equal(3, infos.Count); // every paused tick still says why
        Assert.All(infos, i =>
        {
            Assert.True(i.Paused);
            Assert.False(i.Success);
            Assert.Equal(0, i.Index);
            Assert.Equal(DetectionOrchestrator.NotForegroundDiagnostic, i.Diagnostic);
        });
        Assert.All(asked, hwnd => Assert.Equal(new IntPtr(1234), hwnd)); // asked about DBD's window
    }

    [Fact]
    public async Task DbdBackInFront_ResumesAtTheNormalCadence_AndConfirms()
    {
        var ocr = new ScriptedOcr(_ => StrongText); // confirms on the first frame that is read
        var capture = new FakeCapture();
        var infos = new ConcurrentQueue<DetectionSampleInfo>();
        bool inFront = false; // read and written on the scan's thread only
        DetectionOrchestrator orchestrator = Create(ocr, new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 40), capture);
        orchestrator.SampleEvaluated += (_, i) =>
        {
            infos.Enqueue(i);
            if (infos.Count == 3)
            {
                inFront = true; // the user alt-tabs back into the game
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // safety net only
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DetectionResult r = await orchestrator.DetectAsync(
            () => new IntPtr(1234), _ => inFront, null, null, continuous: true, ct: cts.Token);
        sw.Stop();

        Assert.True(r.Success);
        Assert.Equal("Coldwind Farm", r.RealmName);
        Assert.Equal(1, r.SampleCount); // the paused ticks were no samples
        Assert.Equal(2, ocr.Calls); // one frame (map + realm), read once it was DBD's
        Assert.Equal(2, capture.Regions.Count);

        DetectionSampleInfo[] all = infos.ToArray();
        Assert.Equal(4, all.Length);
        Assert.All(all.Take(3), i => Assert.True(i.Paused));
        Assert.False(all[3].Paused);
        Assert.True(all[3].Success);
        Assert.Equal(1, all[3].Index);

        // Three paused ticks, each followed by the normal sample interval (no busy loop).
        Assert.True(sw.ElapsedMilliseconds >= 100, $"paused ticks took only {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task FocusLostDuringCapture_DropsTheFrameUnread()
    {
        var ocr = new ScriptedOcr(_ => StrongText);
        bool inFront = true; // read and written on the scan's thread only
        int regionCaptures = 0;
        var capture = new FakeCapture
        {
            OnCapture = () =>
            {
                if (++regionCaptures == 1)
                {
                    inFront = false; // alt-tab lands while the first frame is captured
                }
            }
        };
        var infos = new ConcurrentQueue<DetectionSampleInfo>();
        DetectionOrchestrator orchestrator = Create(ocr, new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20), capture);
        orchestrator.SampleEvaluated += (_, i) =>
        {
            infos.Enqueue(i);
            inFront = true; // and straight back
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // safety net only
        DetectionResult r = await orchestrator.DetectAsync(
            () => new IntPtr(1234), _ => inFront, null, null, continuous: true, ct: cts.Token);

        Assert.True(r.Success);
        Assert.Equal(1, r.SampleCount);
        Assert.Equal(2, ocr.Calls); // only the second frame was read
        Assert.Equal(4, capture.Regions.Count); // both frames were captured
        DetectionSampleInfo[] all = infos.ToArray();
        Assert.Equal(2, all.Length);
        Assert.True(all[0].Paused);
        Assert.True(all[1].Success);
    }

    [Fact]
    public async Task ForegroundCheckThatThrows_CountsAsNotInFront()
    {
        var ocr = new ScriptedOcr(_ => StrongText);
        var infos = new ConcurrentQueue<DetectionSampleInfo>();
        DetectionOrchestrator orchestrator = Create(ocr, new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20));
        using var cts = new CancellationTokenSource();
        orchestrator.SampleEvaluated += (_, i) =>
        {
            infos.Enqueue(i);
            if (infos.Count >= 2)
            {
                cts.Cancel();
            }
        };

        DetectionResult r = await orchestrator.DetectAsync(
            () => new IntPtr(1234), _ => throw new InvalidOperationException("focus query failed"),
            null, null, continuous: true, ct: cts.Token);

        Assert.True(r.WasStopped);
        Assert.Equal(0, ocr.Calls);
        Assert.All(infos, i => Assert.True(i.Paused));
    }

    [Fact]
    public async Task StopDuringAPausedTick_RaisesNoPausedEventAfterTheStop()
    {
        var ocr = new ScriptedOcr(_ => StrongText);
        var infos = new ConcurrentQueue<DetectionSampleInfo>();
        DetectionOrchestrator orchestrator = Create(ocr, new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20));
        orchestrator.SampleEvaluated += (_, i) => infos.Enqueue(i);
        using var cts = new CancellationTokenSource();

        DetectionResult r = await orchestrator.DetectAsync(
            () => new IntPtr(1234),
            isInFront: _ =>
            {
                cts.Cancel(); // the user's stop lands while this tick finds DBD covered
                return false;
            },
            debugMode: null, debugDirectory: null, continuous: true, ct: cts.Token);

        Assert.True(r.WasStopped);
        Assert.Equal(0, r.SampleCount);
        Assert.Equal(0, ocr.Calls);
        Assert.Empty(infos); // no "paused" news after the stop (the UI would show it over "Scan stopped")
    }

    [Fact]
    public async Task NoWindow_IsReportedAsNotAvailable_NotAsAPause()
    {
        // DBD exiting: a zero handle is a "window not available" sample, as without the gate.
        var ocr = new ScriptedOcr(_ => StrongText);
        var infos = new ConcurrentQueue<DetectionSampleInfo>();
        DetectionOrchestrator orchestrator = Create(ocr, new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 20));
        using var cts = new CancellationTokenSource();
        StopAfterSample(orchestrator, cts, 2);
        orchestrator.SampleEvaluated += (_, i) => infos.Enqueue(i);

        DetectionResult r = await orchestrator.DetectAsync(
            () => IntPtr.Zero, _ => false, null, null, continuous: true, ct: cts.Token);

        Assert.True(r.WasStopped);
        Assert.Equal(2, r.SampleCount);
        Assert.Equal(0, ocr.Calls);
        Assert.All(infos, i =>
        {
            Assert.False(i.Paused);
            Assert.Contains("not available", i.Diagnostic);
        });
    }

    [Fact]
    public async Task Dispose_CancelsInFlightScan()
    {
        DetectionOrchestrator orchestrator = Create(new FakeOcr("PRESS START"), new DetectionPolicy(TimeoutSeconds: 1, SampleIntervalMs: 20));
        var firstSample = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.SampleTaken += (_, _) => firstSample.TrySetResult();

        Task<DetectionResult> run = orchestrator.DetectAsync(new IntPtr(1234), continuous: true);
        await firstSample.Task.WaitAsync(TimeSpan.FromSeconds(5));
        orchestrator.Dispose();

        Task finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(run, finished); // a continuous scan used to run forever after Dispose
        DetectionResult r = await run;
        Assert.False(r.Success);
        Assert.True(r.WasStopped);
        Assert.False(orchestrator.IsDetecting);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => orchestrator.DetectAsync(new IntPtr(1234)));
    }

    [Fact]
    public async Task ConcurrentAttempts_ArePrevented()
    {
        var orchestrator = new DetectionOrchestrator(
            new FakeOcr("PRESS START"),
            new MapMatchingService(),
            Database(),
            new FakeCapture(),
            new DetectionPolicy(TimeoutSeconds: 3, SampleIntervalMs: 200),
            _ => FakeBounds);

        Task<DetectionResult> first = orchestrator.DetectAsync(new IntPtr(1));
        DetectionResult second = await orchestrator.DetectAsync(new IntPtr(1));
        Assert.False(second.Success);
        Assert.Contains("already running", second.FailureReason);
        await first;
    }

    [Fact]
    public async Task UnavailableOcr_FailsFast()
    {
        var orchestrator = new DetectionOrchestrator(
            new FakeOcr("", available: false),
            new MapMatchingService(),
            Database(),
            new FakeCapture(),
            new DetectionPolicy(),
            _ => FakeBounds);

        DetectionResult r = await orchestrator.DetectAsync(new IntPtr(1));
        Assert.False(r.Success);
        Assert.Contains("OCR", r.FailureReason);
    }

    [Fact]
    public async Task Cancellation_StopsDetection()
    {
        var orchestrator = new DetectionOrchestrator(
            new FakeOcr("PRESS START"),
            new MapMatchingService(),
            Database(),
            new FakeCapture(),
            new DetectionPolicy(TimeoutSeconds: 10, SampleIntervalMs: 100),
            _ => FakeBounds);

        using var cts = new CancellationTokenSource(400);
        DetectionResult r = await orchestrator.DetectAsync(new IntPtr(1), ct: cts.Token);
        Assert.False(r.Success);
        Assert.True(r.WasStopped);
        Assert.False(orchestrator.IsDetecting);
    }
}
