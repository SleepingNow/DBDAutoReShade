using DBDAutoReShade.App.Services;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// End-to-end activation flows through the real <see cref="AppController"/>:
/// detection → pending → retry → switch, F8 recovery, exhaustion guidance.
/// Regression tests for "map detected but preset never switches".
/// One partial class (sequential) across several files, sharing <see cref="ControllerHarness"/>.
/// </summary>
public sealed partial class ControllerActivationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-ctrl-" + Guid.NewGuid().ToString("N"));
    private readonly List<ControllerHarness> _harnesses = new();

    public ControllerActivationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (ControllerHarness h in _harnesses)
        {
            h.Dispose();
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

    private ControllerHarness Harness(ControllerHarnessOptions? options = null)
    {
        ControllerHarness h = ControllerHarness.Create(_dir, options);
        _harnesses.Add(h);
        return h;
    }

    private ControllerHarness Create(TimeSpan retryInterval, int maxAttempts, bool withPresets = true, int verifyDelayMs = 2000) =>
        Harness(new ControllerHarnessOptions
        {
            RetryInterval = retryInterval,
            MaxAttempts = maxAttempts,
            WithPresets = withPresets,
            VerifyDelayMs = verifyDelayMs
        });

    [Fact]
    public async Task FailedActivation_CanBeRetried_ThenSucceeds()
    {
        ControllerHarness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20);
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
        ControllerHarness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20);

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
        ControllerHarness h = Create(TimeSpan.FromMilliseconds(50), maxAttempts: 3);
        h.Keys.NextResult = false; // rejected sends (SendBlocked) consume attempts

        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.WaitingForGameplay, h.States.State);

        // Background loop burns through the 3 attempts; the message must guide recovery.
        string? message = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            message = h.Controller.StatusMessage;
            if (message.Contains("Press F8 on a loading screen to try again", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.Contains("Press F8 on a loading screen to try again, or use the manual override.", message);
        Assert.Equal(AppState.Ready, h.States.State); // final: nothing left waiting for focus
        Assert.False(h.Controller.HasPendingActivation);

        h.Keys.NextResult = true; // recoverable via F8 on the loading screen
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task SecondPress_StopsRunningScan()
    {
        ControllerHarness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20);
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
        ControllerHarness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20);
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
        ControllerHarness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20);
        h.Ocr.Text = "THE GAME"; // Gideon Meat Plant — no preset assigned
        h.Keys.NextResult = true;

        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task NothingAssigned_DoesNotPendPointlessRetries()
    {
        ControllerHarness h = Create(TimeSpan.FromMilliseconds(50), maxAttempts: 3, withPresets: false);
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
        ControllerHarness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20);
        h.Keys.NextResult = true;
        h.Controller.TestGlobalPreset();
        Assert.Contains("Global.ini", h.Controller.LastActivationText);
        Assert.Contains("OK", h.Controller.LastActivationText);
    }

    [Fact]
    public async Task SuccessfulSwitch_IsConfirmedByReshadeIni()
    {
        // The read-back watches ReShade.ini for the whole window (polling), so a
        // long window only makes the test robust; it still confirms promptly.
        ControllerHarness h = Create(TimeSpan.FromSeconds(30), maxAttempts: 20, verifyDelayMs: 10_000);
        h.Keys.NextResult = true;

        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.PresetActive, h.States.State);

        // Simulate ReShade's save_config: it records the new PresetPath.
        string ini = h.Controller.Config.ReshadeIniPath!;
        var doc = IniDocument.Parse(await File.ReadAllTextAsync(ini));
        doc.SetValue("GENERAL", "PresetPath", h.ColdwindIni);
        await File.WriteAllTextAsync(ini, doc.ToString());

        string? message = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
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
