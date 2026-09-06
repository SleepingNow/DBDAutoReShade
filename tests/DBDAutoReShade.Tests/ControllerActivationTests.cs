using DBDAutoReShade.App.Services;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// End-to-end activation flows through the real <see cref="AppController"/>:
/// detection → pending → retry → switch, F8 recovery, exhaustion guidance.
/// Regression tests for "map detected but preset never switches".
/// </summary>
public sealed class ControllerActivationTests : IDisposable
{
    private sealed class FakeDbdMonitor : IDbdMonitor
    {
        public bool IsRunning { get; set; } = true;
        public IntPtr MainWindowHandle { get; set; } = new(0x1234);
        public string? ExePath { get; set; }
        public int? ProcessId { get; set; }
#pragma warning disable CS0067 // required by IDbdMonitor; raised by production monitors
        public event EventHandler<DbdStatusEventArgs>? StatusChanged;
#pragma warning restore CS0067

        public void RaiseStatusChanged(bool running) =>
            StatusChanged?.Invoke(this, new DbdStatusEventArgs(running, MainWindowHandle, ExePath, ProcessId));
        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeKeys : IKeySimulator
    {
        public bool NextResult { get; set; } = true;
        public int CallCount { get; private set; }
        public bool SendShortcut(ReshadeShortcutConfig shortcut)
        {
            CallCount++;
            return NextResult;
        }
    }

    private sealed class FakeOcr : IOcrEngine
    {
        public string Text { get; set; } = "FRACTURED COWSHED\nCOLDWIND FARM";
        public string Name => "Fake";
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(CapturedImage image, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OcrTextLine>>(
                Text.Split('\n').Select(l => new OcrTextLine(l, null)).ToList());
    }

    private sealed class FakeCapture : IWindowCaptureService
    {
        public CapturedImage? CaptureClientArea(IntPtr hwnd) =>
            new(800, 200, new byte[800 * 200 * 4]);

        public CapturedImage? CaptureRegion(IntPtr hwnd, IntRegion region)
        {
            int w = Math.Max(1, Math.Min(region.Width, 800));
            int h = Math.Max(1, Math.Min(region.Height, 200));
            return new CapturedImage(w, h, new byte[w * h * 4]);
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-ctrl-" + Guid.NewGuid().ToString("N"));
    private readonly List<AppController> _controllers = new();

    public ControllerActivationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (AppController c in _controllers)
        {
            try
            {
                c.Shutdown();
                c.Dispose();
            }
            catch
            {
                // Best effort.
            }
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private sealed record Harness(
        AppController Controller,
        RuntimeStateService States,
        FakeDbdMonitor Dbd,
        FakeKeys Keys,
        FakeOcr Ocr,
        string ColdwindIni,
        string GlobalIni);

    private Harness Create(TimeSpan retryInterval, int maxAttempts, bool withPresets = true, int verifyDelayMs = 2000)
    {
        string coldwind = Path.Combine(_dir, Guid.NewGuid() + "-Coldwind.ini");
        string global = Path.Combine(_dir, Guid.NewGuid() + "-Global.ini");
        File.WriteAllText(coldwind, "[General]\nTechniques=A\nTechniqueSorting=A\n");
        File.WriteAllText(global, "[General]\nTechniques=B\nTechniqueSorting=B\n");
        string ini = Path.Combine(_dir, Guid.NewGuid() + "-ReShade.ini");
        File.WriteAllText(ini, "[GENERAL]\nPresetPath=" + global + "\n");
        string configPath = Path.Combine(_dir, Guid.NewGuid() + "-config.json");

        var configSvc = new AppConfigService(configPath);
        if (withPresets)
        {
            configSvc.Config.GlobalPresetPath = global;
            configSvc.Config.RealmPresets["Coldwind Farm"] = coldwind;
        }

        configSvc.Config.ReshadeIniPath = ini;

        var db = new RealmDatabaseService();
        db.LoadAsync(null).GetAwaiter().GetResult();

        var states = new RuntimeStateService();
        var dbd = new FakeDbdMonitor();
        var hotkey = new GlobalHotkeyService();
        var ocr = new FakeOcr();
        var capture = new FakeCapture();
        var detector = new DetectionOrchestrator(
            ocr, new MapMatchingService(), db, capture,
            new DetectionPolicy(TimeoutSeconds: 5, SampleIntervalMs: 25),
            _ => new DbdClientBounds(0, 0, 1920, 1080, 96));
        var lifecycle = new MatchLifecycleService(capture);
        var keys = new FakeKeys();
        var activator = new PresetActivationService(keys, _ => true); // focus bypass for tests
        var controller = new AppController(
            configSvc, db, states, dbd, hotkey, detector, lifecycle, activator,
            new PresetDecisionService(), new ReshadeConfigurationService(),
            activationRetryInterval: retryInterval, maxActivationAttempts: maxAttempts,
            presetVerifyDelayMs: verifyDelayMs);
        _controllers.Add(controller);
        controller.Initialize();
        return new Harness(controller, states, dbd, keys, ocr, coldwind, global);
    }

    [Fact]
    public async Task FailedActivation_CanBeRetried_ThenSucceeds()
    {
        Harness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20);
        Assert.Equal(AppState.Ready, h.States.State);

        h.Keys.NextResult = false; // e.g. game momentarily not ready
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.WaitingForGameplay, h.States.State);
        Assert.False(h.Controller.RetryPendingActivation()); // still failing

        h.Keys.NextResult = true; // user clicked into DBD
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task PressingF8Again_AfterFailedActivation_StartsFreshAttempt()
    {
        Harness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20);

        h.Keys.NextResult = false;
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.WaitingForGameplay, h.States.State);

        // Previously this was rejected with "Busy (WaitingForGameplay)" — stuck forever.
        h.Keys.NextResult = true;
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task ExhaustedAttempts_ExplainRecovery()
    {
        Harness h = Create(TimeSpan.FromMilliseconds(50), maxAttempts: 3);
        h.Keys.NextResult = false;

        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.WaitingForGameplay, h.States.State);

        // Background loop burns through the 3 attempts; the message must guide recovery.
        string? message = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            message = h.Controller.StatusMessage;
            if (message.Contains("press F8 to retry", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.Contains("press F8 to retry", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AppState.WaitingForGameplay, h.States.State); // still recoverable via F8
    }

    [Fact]
    public async Task SecondPress_StopsRunningScan()
    {
        Harness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20);
        h.Ocr.Text = "PRESS START"; // never matches: scan runs until toggled off

        Task first = h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.DetectingLoadingScreen, h.States.State);

        await h.Controller.TriggerDetectionAsync(); // second press = stop
        await first;

        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Contains("stopped", h.Controller.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scan_StopsWhenDbdExits()
    {
        Harness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20);
        h.Ocr.Text = "PRESS START"; // never matches

        Task first = h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.DetectingLoadingScreen, h.States.State);

        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);
        await first;

        Assert.Equal(AppState.WaitingForDbd, h.States.State);
    }

    [Fact]
    public async Task UnconfiguredRealm_ActivatesGlobal()
    {
        Harness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20);
        h.Ocr.Text = "THE GAME"; // Gideon Meat Plant — no preset assigned
        h.Keys.NextResult = true;

        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task NothingAssigned_DoesNotPendPointlessRetries()
    {
        Harness h = Create(TimeSpan.FromMilliseconds(50), maxAttempts: 3, withPresets: false);
        h.Keys.NextResult = true;

        await h.Controller.TriggerDetectionAsync();

        // Detected fine, but there is nothing to switch to: back to Ready, no pending loop.
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Contains("no preset", h.Controller.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, h.Keys.CallCount);
        Assert.True(h.Controller.RetryPendingActivation()); // nothing pending
    }

    [Fact]
    public void LastActivation_IsRecorded()
    {
        Harness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20);
        h.Keys.NextResult = true;
        h.Controller.TestGlobalPreset();
        Assert.Contains("Global.ini", h.Controller.LastActivationText);
        Assert.Contains("OK", h.Controller.LastActivationText);
    }

    [Fact]
    public async Task SuccessfulSwitch_IsConfirmedByReshadeIni()
    {
        Harness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20, verifyDelayMs: 50);
        h.Keys.NextResult = true;

        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.PresetActive, h.States.State);

        // Simulate ReShade's save_config: it records the new PresetPath.
        string ini = h.Controller.Config.ReshadeIniPath!;
        var doc = IniDocument.Parse(await File.ReadAllTextAsync(ini));
        doc.SetValue("GENERAL", "PresetPath", h.ColdwindIni);
        await File.WriteAllTextAsync(ini, doc.ToString());

        string? message = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            message = h.Controller.StatusMessage;
            if (message.Contains("confirmed by ReShade", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.Contains("confirmed by ReShade", message, StringComparison.OrdinalIgnoreCase);
    }
}
