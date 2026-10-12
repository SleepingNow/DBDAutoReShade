using System.Collections.Concurrent;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// The one focus-aware desired activation (C3), read-back verification (C4),
/// scans without a confident read (C6), DBD exit (C7), Return to Automatic (C9)
/// and the renewed budget once gameplay starts (C14).
/// </summary>
public sealed partial class ControllerActivationTests
{
    // ---------- C3: truthful, focus-aware activation ----------

    [Fact]
    public void TestButton_WaitsForFocus_ThenSends()
    {
        ControllerHarness h = Harness();
        h.Focused = false; // clicking the WPF button focused this app, not DBD

        Assert.Null(h.Controller.TestRealmPreset("Coldwind Farm")); // no error popup: it waits
        Assert.Contains("Click into Dead by Daylight within 15 s to test", h.Controller.StatusMessage);
        Assert.Equal(0, h.Keys.CallCount);
        Assert.Null(h.Controller.ActivePresetPath);
        Assert.True(h.Controller.HasPendingActivation);

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(new[] { "Coldwind Farm" }, h.SentNames());
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
        Assert.Equal("Coldwind Farm", h.Controller.ActiveRealm);
        Assert.False(h.Controller.HasPendingActivation);
    }

    [Fact]
    public void TestButton_GivesUpAfterItsFocusWindow()
    {
        ControllerHarness h = Harness();
        h.Focused = false;
        Assert.Null(h.Controller.TestGlobalPreset());

        h.Advance(AppController.TestFocusWindow);
        Assert.False(h.Controller.RetryPendingActivation());
        Assert.Contains("cancelled", h.Controller.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(h.Controller.HasPendingActivation);

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation()); // nothing pending any more
        Assert.Equal(0, h.Keys.CallCount);
    }

    [Fact]
    public void ManualOverride_WithoutFocus_SendsOnceFocused()
    {
        ControllerHarness h = Harness();
        h.Focused = false;

        h.Controller.ApplyManualOverride("Coldwind Farm");
        Assert.Equal(AppState.ManualOverride, h.States.State);
        Assert.Contains("Waiting for DBD focus to switch to", h.Controller.StatusMessage);
        Assert.DoesNotContain("deferred", h.Controller.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(h.Controller.ActivePresetPath);
        Assert.Equal(0, h.Keys.CallCount);

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
        Assert.StartsWith("Manual override: Coldwind Farm.", h.Controller.StatusMessage);
        Assert.DoesNotContain("Manual override: Manual override", h.Controller.StatusMessage);
    }

    [Fact]
    public void ManualOverride_GivesUpAfter60Seconds()
    {
        ControllerHarness h = Harness();
        h.Focused = false;
        h.Controller.ApplyManualOverride("Coldwind Farm");

        h.Advance(AppController.OverrideFocusWindow - TimeSpan.FromSeconds(1));
        Assert.False(h.Controller.RetryPendingActivation());
        Assert.True(h.Controller.HasPendingActivation); // still within its window

        h.Advance(TimeSpan.FromSeconds(1));
        Assert.False(h.Controller.RetryPendingActivation());
        Assert.False(h.Controller.HasPendingActivation);
        Assert.Contains("wasn't focused within 60 s", h.Controller.StatusMessage);
        Assert.Equal(AppState.ManualOverride, h.States.State); // the override itself stays selected
    }

    [Fact]
    public async Task BackgroundWorker_SendsAsSoonAsDbdIsFocused()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { RetryInterval = TimeSpan.FromMilliseconds(10) });
        h.Focused = false;

        h.Controller.ApplyManualOverride("Coldwind Farm");
        Assert.Equal(0, h.Keys.CallCount);

        h.Focused = true; // the user clicks into the game; nobody calls Retry
        await ControllerHarness.WaitUntilAsync(() => h.Controller.ActivePresetPath == h.ColdwindIni, "worker sends the override");
        Assert.Equal(1, h.Keys.CallCount);
        Assert.False(h.Controller.HasPendingActivation);
    }

    [Fact]
    public void FailedSend_LeavesActivePresetUnchanged()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { MaxAttempts = 1 });
        h.Keys.NextResult = false;

        h.Controller.ApplyManualOverride("Coldwind Farm");
        Assert.Null(h.Controller.ActivePresetPath);
        Assert.Null(h.Controller.ActiveRealm);

        string? error = h.Controller.TestGlobalPreset();
        Assert.NotNull(error);
        Assert.Null(h.Controller.ActivePresetPath);
        Assert.Contains("FAILED", h.Controller.LastActivationText);
    }

    [Fact]
    public void WaitingForFocus_DoesNotConsumeAttempts()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { MaxAttempts = 1 });
        h.Focused = false;
        h.Controller.TestGlobalPreset();
        for (int i = 0; i < 5; i++)
        {
            Assert.False(h.Controller.RetryPendingActivation());
        }

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(1, h.Keys.CallCount);
    }

    [Fact]
    public void RejectedSends_ConsumeAttempts_ThenReportTheHint()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { MaxAttempts = 2 });
        h.Keys.NextResult = false;

        h.Controller.ApplyManualOverride("Coldwind Farm");
        Assert.True(h.Controller.HasPendingActivation); // first rejection: retrying
        Assert.Contains("retrying", h.Controller.StatusMessage);

        Assert.False(h.Controller.RetryPendingActivation());
        Assert.False(h.Controller.HasPendingActivation); // budget spent: final
        Assert.Contains("Could not switch to", h.Controller.StatusMessage);
        Assert.Contains("blocked", h.Controller.StatusMessage);
        Assert.Equal(2, h.Keys.CallCount);
    }

    [Fact]
    public void NewerRequest_SupersedesOlder()
    {
        ControllerHarness h = Harness();
        h.Focused = false;
        h.Controller.ApplyManualOverride("Coldwind Farm");
        h.Controller.TestGlobalPreset();

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.True(h.Controller.RetryPendingActivation()); // nothing else pending
        Assert.Equal(new[] { AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task DetectedPreset_WaitsForFocusWithoutDeadline()
    {
        ControllerHarness h = Harness();
        h.UnfocusRightAfterTheRead();

        await h.Controller.TriggerDetectionAsync();
        Assert.False(h.Focused);
        Assert.Equal(AppState.WaitingForGameplay, h.States.State);
        Assert.Contains("Waiting for DBD focus to switch to", h.Controller.StatusMessage);
        Assert.Contains("click into the game", h.Controller.StatusMessage);

        h.Advance(TimeSpan.FromHours(1)); // no deadline while waiting for gameplay
        Assert.False(h.Controller.RetryPendingActivation());

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task PendingSwitch_DoesNotOverwriteAnUnrelatedStatus()
    {
        ControllerHarness h = Harness();
        h.UnfocusRightAfterTheRead();
        await h.Controller.TriggerDetectionAsync(); // the detected preset waits for focus (no deadline)
        Assert.Contains("Waiting for DBD focus", h.Controller.StatusMessage);

        h.Controller.ReturnToAutomatic(); // something unrelated the user just did
        Assert.Equal("Automatic mode is already active.", h.Controller.StatusMessage);
        Assert.False(h.Controller.RetryPendingActivation()); // the worker's next focus check
        Assert.Equal("Automatic mode is already active.", h.Controller.StatusMessage);
        Assert.True(h.Controller.HasPendingActivation);

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.StartsWith("Active preset: ", h.Controller.StatusMessage); // the switch itself is news again
    }

    [Fact]
    public async Task Verification_DoesNotOverwriteALaterUnrelatedStatus()
    {
        var gates = new ConcurrentQueue<TaskCompletionSource>();
        Task Delay(TimeSpan delay, CancellationToken ct)
        {
            if (delay != TimeSpan.FromMilliseconds(250))
            {
                return Task.Delay(delay, ct); // the (30 s) activation worker
            }

            var gate = new TaskCompletionSource();
            gates.Enqueue(gate);
            return gate.Task;
        }

        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 250, Delay = Delay });
        h.Controller.TestRealmPreset("Coldwind Farm"); // sent → read-back verification pending
        Assert.StartsWith("Test: switched to", h.Controller.StatusMessage);

        h.Controller.ReturnToAutomatic(); // a later, unrelated message
        h.ReshadeSaves(h.ColdwindIni);
        Assert.True(gates.TryDequeue(out TaskCompletionSource? verification));

        // Completed from a pool thread (no test synchronization context), the
        // verification runs to its end inline: it would confirm now.
        await Task.Run(verification.SetResult);

        Assert.Equal("Automatic mode is already active.", h.Controller.StatusMessage);
    }

    // ---------- C14: gameplay starting renews the budget ----------

    [Fact]
    public async Task ExhaustedDetectedPreset_IsRetriedWhenGameplayStarts()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { MaxAttempts = 2 });
        h.Keys.NextResult = false;

        await h.Controller.TriggerDetectionAsync();
        Assert.False(h.Controller.RetryPendingActivation()); // second rejection: exhausted
        Assert.Contains("Press F8 to retry", h.Controller.StatusMessage);
        Assert.Equal(AppState.WaitingForGameplay, h.States.State);

        h.Keys.NextResult = true;
        h.DriveToTrial(); // gameplay starts: fresh budget, immediate try
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
    }

    // ---------- C9: Return to Automatic ----------

    [Fact]
    public async Task ReturnToAutomatic_OutsideOverride_ChangesNothing()
    {
        ControllerHarness h = Harness();
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.PresetActive, h.States.State);

        h.Controller.ReturnToAutomatic();
        Assert.Equal("Automatic mode is already active.", h.Controller.StatusMessage);
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(1, h.Keys.CallCount); // no Global mid-match
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public void ReturnToAutomatic_FromOverride_RequestsGlobal()
    {
        ControllerHarness h = Harness();
        h.Controller.ApplyManualOverride("Coldwind Farm");
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);

        h.Focused = false;
        h.Controller.ReturnToAutomatic();
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.False(h.Controller.IsManualOverride);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath); // nothing sent yet

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
        Assert.Null(h.Controller.ActiveRealm);
    }

    // ---------- C6: scan ended without a confident read ----------

    [Fact]
    public async Task StoppedScan_WithRealmPresetActive_SwitchesToGlobal()
    {
        ControllerHarness h = Harness();
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);

        h.Ocr.Text = "PRESS START";
        Task scan = h.Controller.TriggerDetectionAsync();
        await h.Controller.TriggerDetectionAsync(); // stop
        await scan;

        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
        Assert.Contains("stopped", h.Controller.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Global", h.Controller.StatusMessage);
    }

    [Fact]
    public async Task StoppedScan_WithGlobalAlreadyActive_SendsNothing()
    {
        ControllerHarness h = Harness();
        h.Controller.TestGlobalPreset();
        Assert.Equal(1, h.Keys.CallCount);

        h.Ocr.Text = "PRESS START";
        Task scan = h.Controller.TriggerDetectionAsync();
        await h.Controller.TriggerDetectionAsync();
        await scan;

        Assert.Equal(1, h.Keys.CallCount);
        Assert.Contains("stays active", h.Controller.StatusMessage);
    }

    [Fact]
    public async Task StoppedScan_AfterAGlobalRestoreReshadeIgnored_SendsGlobalAgain()
    {
        // The read-back runs right after each send (no wait).
        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 0 });
        await h.Controller.TriggerDetectionAsync();
        h.ReshadeSaves(@".\" + Path.GetFileName(h.ColdwindIni)); // ReShade switched (it saves paths relative to its folder)
        h.DriveToTrial();
        h.DriveToMenus(); // match end: the Global keys go out, but ReShade ignores them
        Assert.Contains("hasn't confirmed the switch", h.Controller.StatusMessage);
        Assert.Equal(h.ColdwindIni, h.ReshadeStartupPreset()); // ReShade still runs the realm preset

        h.Ocr.Text = "PRESS START"; // the next loading screen isn't read
        Task scan = h.Controller.TriggerDetectionAsync();
        await h.Controller.TriggerDetectionAsync(); // stop
        await scan;

        // Unsure reads use Global, and nothing showed ReShade runs it: sent again.
        Assert.Equal(
            new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey, AppConfigKeys.GlobalShortcutKey },
            h.SentNames());
        Assert.DoesNotContain("stays active", h.Controller.StatusMessage);
    }

    [Fact]
    public async Task StoppedScan_WaitsForFocusToSwitchToGlobal()
    {
        ControllerHarness h = Harness();
        h.Ocr.Text = "PRESS START";
        h.Focused = false;
        Task scan = h.Controller.TriggerDetectionAsync();
        await h.Controller.TriggerDetectionAsync();
        await scan;

        // The active preset is unknown at startup: Global is requested, not claimed.
        Assert.Null(h.Controller.ActivePresetPath);
        Assert.Contains("Waiting for DBD focus to switch to", h.Controller.StatusMessage);
        Assert.DoesNotContain("retained", h.Controller.StatusMessage);

        h.Advance(AppController.ScanWithoutResultFocusWindow - TimeSpan.FromSeconds(1));
        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task StoppedScan_WithoutGlobal_SaysSo()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.GlobalPresetPath = null });
        h.Ocr.Text = "PRESS START";
        Task scan = h.Controller.TriggerDetectionAsync();
        await h.Controller.TriggerDetectionAsync();
        await scan;

        Assert.Equal(0, h.Keys.CallCount);
        Assert.Contains("No Global preset is configured", h.Controller.StatusMessage);
        Assert.False(h.Controller.HasPendingActivation);
    }

    [Fact]
    public async Task ScanStoppedByDbdExit_RequestsNothing()
    {
        ControllerHarness h = Harness();
        h.Ocr.Text = "PRESS START";
        Task scan = h.Controller.TriggerDetectionAsync();

        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);
        await scan;

        Assert.Equal(AppState.WaitingForDbd, h.States.State);
        Assert.False(h.Controller.HasPendingActivation);
        Assert.Equal(0, h.Keys.CallCount);
        Assert.StartsWith("Launch Dead by Daylight.", h.Controller.StatusMessage);
    }

    // ---------- C7: DBD exit makes Global the startup preset ----------

    [Fact]
    public async Task DbdExit_WritesGlobalAsReshadeStartupPreset()
    {
        ControllerHarness h = Harness();
        await h.Controller.TriggerDetectionAsync();
        h.ReshadeSaves(h.ColdwindIni); // what ReShade does on the switch
        Assert.Equal(h.ColdwindIni, h.ReshadeStartupPreset());

        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);

        Assert.Equal(h.GlobalIni, h.ReshadeStartupPreset());
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
        Assert.Null(h.Controller.ActiveRealm);
        Assert.Contains("Global", h.Controller.StatusMessage);
    }

    [Fact]
    public async Task DbdExit_WithAutoRestoreOff_LeavesReshadeIniAlone()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.AutoRestoreGlobalOnMenu = false });
        await h.Controller.TriggerDetectionAsync();
        h.ReshadeSaves(h.ColdwindIni);

        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);

        Assert.Equal(h.ColdwindIni, h.ReshadeStartupPreset());
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public void DbdExit_WithGlobalActive_LeavesReshadeIniAlone()
    {
        ControllerHarness h = Harness();
        h.Controller.TestGlobalPreset();
        h.ReshadeSaves(h.ColdwindIni); // e.g. the user picked a preset in the ReShade overlay

        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);

        Assert.Equal(h.ColdwindIni, h.ReshadeStartupPreset());
    }

    [Fact]
    public async Task DbdExit_AfterAGlobalRestoreReshadeIgnored_StillWritesGlobal()
    {
        // The read-back runs right after each send (no wait).
        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 0 });
        await h.Controller.TriggerDetectionAsync();
        h.ReshadeSaves(@".\" + Path.GetFileName(h.ColdwindIni)); // ReShade switched (it saves paths relative to its folder)
        h.DriveToTrial();
        h.DriveToMenus(); // match end: the Global keys go out, but ReShade ignores them

        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath); // what the app sent…
        Assert.Contains("hasn't confirmed the switch", h.Controller.StatusMessage);
        Assert.Equal(h.ColdwindIni, h.ReshadeStartupPreset()); // …is not what ReShade has

        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);

        Assert.Equal(h.GlobalIni, h.ReshadeStartupPreset());
        Assert.Equal("Launch Dead by Daylight. ReShade will start with the Global preset.", h.Controller.StatusMessage);
    }

    [Fact]
    public async Task DbdExit_AfterAConfirmedGlobalRestore_KeepsThePresetTheUserPickedSince()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 10_000 });
        await h.Controller.TriggerDetectionAsync();
        h.ReshadeSaves(h.ColdwindIni);
        h.DriveToTrial();
        h.DriveToMenus(); // match end: Global restored…
        h.ReshadeSaves(h.GlobalIni);
        await ControllerHarness.WaitUntilAsync(
            () => h.Controller.StatusMessage.Contains("confirmed by ReShade"), "Global restore confirmed");

        h.ReshadeSaves(h.ColdwindIni); // …then the user picked a preset in the ReShade overlay
        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);

        Assert.Equal(h.ColdwindIni, h.ReshadeStartupPreset());
        Assert.Equal("Launch Dead by Daylight.", h.Controller.StatusMessage);
    }

    [Fact]
    public async Task DbdExit_KeepsTheUserPick_EvenWhenAnUnrelatedStatusCameBeforeTheGlobalConfirmation()
    {
        var gates = new ConcurrentQueue<TaskCompletionSource>();
        Task Delay(TimeSpan delay, CancellationToken ct)
        {
            if (delay != TimeSpan.FromMilliseconds(250))
            {
                return Task.Delay(delay, ct); // the (30 s) activation worker
            }

            var gate = new TaskCompletionSource();
            gates.Enqueue(gate);
            return gate.Task;
        }

        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 250, Delay = Delay });
        await h.Controller.TriggerDetectionAsync(); // Coldwind sent → read-back pending
        h.ReshadeSaves(@".\" + Path.GetFileName(h.ColdwindIni));
        Assert.True(gates.TryDequeue(out TaskCompletionSource? coldwind));
        await Task.Run(coldwind.SetResult); // opened from a pool thread: the read-back runs to its end inline
        Assert.Contains("confirmed by ReShade", h.Controller.StatusMessage);

        h.DriveToTrial();
        h.DriveToMenus(); // match end: the Global keys go out → read-back pending
        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        h.Controller.ReturnToAutomatic(); // something unrelated the user did meanwhile
        h.ReshadeSaves(@".\" + Path.GetFileName(h.GlobalIni)); // ReShade switched
        Assert.True(gates.TryDequeue(out TaskCompletionSource? global));
        await Task.Run(global.SetResult);
        Assert.Equal("Automatic mode is already active.", h.Controller.StatusMessage); // the later message stays…
        Assert.Empty(gates); // …and the read-back is done

        h.ReshadeSaves(h.ColdwindIni); // then the user picked a preset in the ReShade overlay
        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);

        Assert.Equal(h.ColdwindIni, h.ReshadeStartupPreset()); // ReShade did switch to Global: the pick is the user's
    }

    [Fact]
    public async Task DbdExit_KeepingTheUserPick_TheNextLaunchKnowsGlobalIsNotActive()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 0 });
        h.Controller.TestGlobalPreset(); // ReShade.ini already holds Global: confirmed
        Assert.Contains("already active in ReShade", h.Controller.StatusMessage);
        h.ReshadeSaves(@".\" + Path.GetFileName(h.ColdwindIni)); // the user picks a preset in the ReShade overlay

        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);
        Assert.Equal(h.ColdwindIni, h.ReshadeStartupPreset()); // kept: the next launch starts with it
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);

        h.Dbd.ProcessId = 5151;
        h.Dbd.IsRunning = true;
        h.Dbd.RaiseStatusChanged(running: true);
        h.Ocr.Text = "PRESS START"; // the loading screen isn't read
        Task scan = h.Controller.TriggerDetectionAsync();
        await h.Controller.TriggerDetectionAsync(); // stop
        await scan;

        // Unsure reads use Global: ReShade started with the user's pick, so Global is sent.
        Assert.Equal(new[] { AppConfigKeys.GlobalShortcutKey, AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
        Assert.DoesNotContain("stays active", h.Controller.StatusMessage);
    }

    [Fact]
    public void DbdExit_WithMissingIni_DoesNotThrow()
    {
        ControllerHarness h = Harness();
        h.Controller.TestRealmPreset("Coldwind Farm");
        File.Delete(h.ReshadeIni);

        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);

        Assert.Equal(AppState.WaitingForDbd, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath); // nothing was written
    }

    [Fact]
    public void DbdExit_ClearsTheDesiredActivation()
    {
        ControllerHarness h = Harness();
        h.Focused = false;
        h.Controller.TestRealmPreset("Coldwind Farm");
        Assert.True(h.Controller.HasPendingActivation);

        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);
        Assert.False(h.Controller.HasPendingActivation);

        h.Dbd.IsRunning = true;
        h.Dbd.ProcessId = 5151;
        h.Dbd.RaiseStatusChanged(running: true);
        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(0, h.Keys.CallCount);
    }

    // ---------- C4: read-back verification ----------

    [Fact]
    public void AlreadyActiveInReshade_IsReportedInsteadOfConfirmed()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 0 });
        Assert.Equal(h.GlobalIni, h.ReshadeStartupPreset()); // ReShade already saved Global

        h.Controller.TestGlobalPreset();

        Assert.Contains("already active in ReShade", h.Controller.StatusMessage);
        Assert.DoesNotContain("confirmed", h.Controller.StatusMessage);
    }

    [Fact]
    public async Task TestAndOverride_AreVerified()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 10_000 });
        h.Controller.TestRealmPreset("Coldwind Farm");
        h.ReshadeSaves(h.ColdwindIni);
        await ControllerHarness.WaitUntilAsync(
            () => h.Controller.StatusMessage.Contains("confirmed by ReShade"), "test switch confirmed");
        Assert.StartsWith("Test: switched to", h.Controller.StatusMessage);

        h.Controller.ApplyManualOverride(null); // Global
        h.ReshadeSaves(h.GlobalIni);
        await ControllerHarness.WaitUntilAsync(
            () => h.Controller.StatusMessage.StartsWith("Manual override:") &&
                  h.Controller.StatusMessage.Contains("confirmed by ReShade"), "override confirmed");
    }

    [Fact]
    public async Task OlderVerification_CannotOverwriteNewerActivationStatus()
    {
        // Verification polls wait on gates the test opens. Each gate is opened
        // from a pool thread (no test synchronization context), where the
        // verification's continuation runs inline to its end before the await
        // returns, so every assertion sees that verification's final word.
        var gates = new ConcurrentQueue<TaskCompletionSource>();
        Task Delay(TimeSpan delay, CancellationToken ct)
        {
            if (delay != TimeSpan.FromMilliseconds(250))
            {
                return Task.Delay(delay, ct); // the (30 s) activation worker
            }

            var gate = new TaskCompletionSource();
            gates.Enqueue(gate);
            return gate.Task;
        }

        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 250, Delay = Delay });

        await h.Controller.TriggerDetectionAsync(); // detected Coldwind → verification A
        Assert.Equal(AppState.PresetActive, h.States.State);
        h.Controller.TestRealmPreset("Coldwind Farm"); // same preset again → verification B
        string testStatus = h.Controller.StatusMessage;
        Assert.StartsWith("Test: switched to", testStatus);
        Assert.Equal(2, gates.Count);

        h.ReshadeSaves(h.ColdwindIni);
        Assert.True(gates.TryDequeue(out TaskCompletionSource? verificationA));
        await Task.Run(verificationA.SetResult); // A would confirm, but it is stale
        Assert.Equal(testStatus, h.Controller.StatusMessage);

        Assert.True(gates.TryDequeue(out TaskCompletionSource? verificationB));
        await Task.Run(verificationB.SetResult);
        Assert.StartsWith("Test: switched to", h.Controller.StatusMessage);
        Assert.Contains("confirmed by ReShade", h.Controller.StatusMessage);
        Assert.Empty(gates); // each verification polled once and finished
    }
}
