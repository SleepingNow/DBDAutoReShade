using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

public sealed class DetectionOrchestratorTests
{
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

    private sealed class FakeCapture : IWindowCaptureService
    {
        public CapturedImage? ImageToReturn { get; set; } = DummyImage();

        public static CapturedImage DummyImage() =>
            new(800, 200, Enumerable.Repeat((byte)128, 800 * 200 * 4).ToArray());

        public CapturedImage? CaptureClientArea(IntPtr hwnd) => ImageToReturn?.Clone();

        public CapturedImage? CaptureRegion(IntPtr hwnd, IntRegion region)
        {
            if (ImageToReturn is null)
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

    [Fact]
    public async Task SuccessfulRecognition_StopsImmediately()
    {
        var orchestrator = new DetectionOrchestrator(
            new FakeOcr("FRACTURED COWSHED\nCOLDWIND FARM"),
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

        using var cts = new CancellationTokenSource();
        Task<DetectionResult> run = orchestrator.DetectAsync(new IntPtr(1234), continuous: true, ct: cts.Token);
        await Task.Delay(1600); // well past the 1 s single-shot timeout
        Assert.True(orchestrator.IsDetecting); // still scanning: no timeout in toggle mode
        cts.Cancel();
        DetectionResult r = await run;

        Assert.False(r.Success);
        Assert.True(r.WasStopped);
        Assert.True(r.SampleCount >= 10); // kept sampling the whole time
        Assert.False(orchestrator.IsDetecting);
    }

    [Fact]
    public async Task UserStop_SkipsDebugCrop()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dbd-nocrop-" + Guid.NewGuid().ToString("N"));
        var orchestrator = new DetectionOrchestrator(
            new FakeOcr("PRESS START"),
            new MapMatchingService(),
            Database(),
            new FakeCapture(),
            new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 50),
            _ => FakeBounds);

        using var cts = new CancellationTokenSource(300);
        DetectionResult r = await orchestrator.DetectAsync(
            new IntPtr(1234), debugMode: () => true, debugDirectory: dir,
            continuous: true, ct: cts.Token);

        Assert.True(r.WasStopped);
        Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir, "*.bmp").Length > 0);
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
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
