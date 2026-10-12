using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// Controller ↔ lifecycle: lock order (C1), no false mid-match restore (C2),
/// sticky Error (C5), match end (C8), configured hotkey in texts (C10),
/// per-sample feedback (C11) and window-handle-only changes (C12).
/// </summary>
public sealed partial class ControllerActivationTests
{
    // ---------- C2: activation never forces InTrial ----------

    [Fact]
    public async Task ActivationOnLoadingScreen_KeepsWatching_NoMidMatchGlobalRestore()
    {
        ControllerHarness h = Harness();
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(LifecyclePhase.WatchingForTrial, h.Lifecycle.Phase); // not InTrial

        h.DriveToTrial(); // loading screen → gameplay: the gameplay frame is the reference
        for (int i = 0; i < 8; i++)
        {
            h.Lifecycle.PollOnce(); // more gameplay
        }

        Assert.Equal(LifecyclePhase.InTrial, h.Lifecycle.Phase);
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
        Assert.Equal(1, h.Keys.CallCount);
    }

    // ---------- C8: match end ----------

    [Fact]
    public async Task MatchEnd_RestoresGlobal_AndDisarms()
    {
        ControllerHarness h = Harness();
        await h.Controller.TriggerDetectionAsync();
        h.DriveToTrial();
        h.DriveToMenus();

        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Equal(LifecyclePhase.Unknown, h.Lifecycle.Phase);
        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
        Assert.StartsWith("Match ended — Global preset restored", h.Controller.StatusMessage);
    }

    [Fact]
    public async Task MatchEnd_WhileUnfocused_RestoresGlobalOnceFocused()
    {
        ControllerHarness h = Harness();
        await h.Controller.TriggerDetectionAsync();
        h.DriveToTrial();

        // The lifecycle only evaluates frames of DBD itself, so the menus are
        // seen while DBD is in front; the user alt-tabs right as the poll that
        // confirms them captures (e.g. on the results screen).
        int polls = 0;
        h.Capture.OnClientAreaCapture = () =>
        {
            if (++polls == 4)
            {
                h.Focused = false;
            }
        };
        h.DriveToMenus();
        h.Capture.OnClientAreaCapture = null;
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath); // not claimed before it is sent
        Assert.Contains("Waiting for DBD focus to switch to", h.Controller.StatusMessage);

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task MatchEnd_WithAutoRestoreOff_StillDisarms()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.AutoRestoreGlobalOnMenu = false });
        await h.Controller.TriggerDetectionAsync();
        h.DriveToTrial();
        h.DriveToMenus();

        Assert.Equal(LifecyclePhase.Unknown, h.Lifecycle.Phase);
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(1, h.Keys.CallCount);
    }

    [Fact]
    public async Task MatchEnd_DuringScan_LeavesTheScanRunning()
    {
        ControllerHarness h = Harness();
        await h.Controller.TriggerDetectionAsync();
        h.DriveToTrial();

        h.Ocr.Text = "PRESS START";
        Task scan = h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.DetectingLoadingScreen, h.States.State);

        h.DriveToMenus();
        Assert.Equal(AppState.DetectingLoadingScreen, h.States.State);
        Assert.False(scan.IsCompleted);
        Assert.Equal(LifecyclePhase.Unknown, h.Lifecycle.Phase);
        Assert.Equal(1, h.Keys.CallCount);

        await h.Controller.TriggerDetectionAsync(); // stop
        await scan;
    }

    [Fact]
    public async Task MatchEnd_WhilePresetStillPending_GoesStraightToReady()
    {
        // The detected preset stays pending for the whole match because every
        // send is rejected (the lifecycle needs DBD in front to see the match).
        ControllerHarness h = Harness();
        h.Keys.NextResult = false;
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.WaitingForGameplay, h.States.State);
        h.DriveToTrial();
        Assert.Equal(AppState.WaitingForGameplay, h.States.State);
        h.DriveToMenus();

        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Null(h.Controller.ActivePresetPath); // never claimed without a send

        h.Keys.NextResult = true;
        Assert.True(h.Controller.RetryPendingActivation()); // Global, not the stale realm preset
        Assert.Equal(AppConfigKeys.GlobalShortcutKey, h.SentNames()[^1]);
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
        Assert.Null(h.Controller.ActiveRealm);
        Assert.True(h.Controller.RetryPendingActivation()); // nothing else pending
    }

    [Fact]
    public async Task MatchEnd_WhilePresetStillPending_SendsAGlobalReshadeIgnoredLastTime()
    {
        // The read-back runs right after each send (no wait).
        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 0 });
        await h.Controller.TriggerDetectionAsync();
        h.ReshadeSaves(@".\" + Path.GetFileName(h.ColdwindIni)); // ReShade switched (it saves paths relative to its folder)
        h.DriveToTrial();
        h.DriveToMenus(); // match end: the Global keys go out, but ReShade ignores them
        Assert.Contains("hasn't confirmed the switch", h.Controller.StatusMessage);

        // The next match: its detected preset stays pending (every send is rejected).
        h.Keys.NextResult = false;
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.WaitingForGameplay, h.States.State);
        h.DriveToTrial();
        h.DriveToMenus();

        // ReShade still runs the first match's preset: Global is requested.
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.True(h.Controller.HasPendingActivation);
        Assert.Contains("Global", h.Controller.StatusMessage);

        h.Keys.NextResult = true;
        int sends = h.Keys.CallCount;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(sends + 1, h.Keys.CallCount);
        Assert.Equal(AppConfigKeys.GlobalShortcutKey, h.SentNames()[^1]);
        Assert.True(h.Controller.RetryPendingActivation()); // nothing else pending
    }

    // ---------- C2: only frames of DBD itself count ----------

    [Fact]
    public async Task UnfocusedFrames_AreIgnoredByTheLifecycle()
    {
        ControllerHarness h = Harness();
        await h.Controller.TriggerDetectionAsync(); // loading screen (10) is the reference
        Assert.Equal(LifecyclePhase.WatchingForTrial, h.Lifecycle.Phase);

        // Alt-tabbed to a browser covering DBD's area (borderless windowed).
        h.Focused = false;
        h.Capture.Level = 200;
        int captures = h.Capture.ClientAreaCalls;
        for (int i = 0; i < 6; i++)
        {
            h.Lifecycle.PollOnce();
        }

        Assert.Equal(LifecyclePhase.WatchingForTrial, h.Lifecycle.Phase); // no false trial start
        Assert.Equal(captures, h.Capture.ClientAreaCalls); // nothing captured while hidden

        // Back in the game: gameplay is observed normally, then the match end.
        h.Focused = true;
        h.DriveToTrial(gameplayLevel: 120);
        h.Focused = false;
        h.Capture.Level = 250;
        for (int i = 0; i < 8; i++)
        {
            h.Lifecycle.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, h.Lifecycle.Phase); // no false mid-match restore
        Assert.Equal(new[] { "Coldwind Farm" }, h.SentNames());

        h.Focused = true;
        h.DriveToMenus();
        Assert.Equal(LifecyclePhase.Unknown, h.Lifecycle.Phase);
        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());
    }

    // ---------- C8: a stale match end never drops a fresh detection ----------

    [Fact]
    public async Task StaleMatchEnd_AfterANewDetection_KeepsTheNewPreset()
    {
        ControllerHarness h = Harness();
        await h.Controller.TriggerDetectionAsync(); // previous match
        h.DriveToTrial(); // gameplay (200) is the reference

        // The previous match's return to menus was never observed. The next
        // loading screen (10) is far from the gameplay reference: three polls
        // build the menu streak, the fourth would confirm it.
        h.Capture.Level = 10;
        for (int i = 0; i < 3; i++)
        {
            h.Lifecycle.PollOnce();
        }

        Assert.Equal(LifecyclePhase.InTrial, h.Lifecycle.Phase);

        // The user pressed the hotkey on that loading screen. While the accepted
        // detection arms the monitor (its capture), the confirming poll runs and
        // raises "back to menus" for the old match.
        Task? stalePoll = null;
        h.Capture.OnClientAreaCapture = () =>
        {
            h.Capture.OnClientAreaCapture = null;
            stalePoll = Task.Run(h.Lifecycle.PollOnce);
            SpinWait.SpinUntil(
                () => stalePoll.IsCompleted || h.States.State != AppState.WaitingForGameplay,
                TimeSpan.FromSeconds(5));
        };
        await h.Controller.TriggerDetectionAsync();
        Assert.NotNull(stalePoll);
        await stalePoll.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
        Assert.Equal(LifecyclePhase.WatchingForTrial, h.Lifecycle.Phase); // the new match is tracked
        Assert.DoesNotContain(AppConfigKeys.GlobalShortcutKey, h.SentNames());
    }

    // ---------- C5: Error is not sticky ----------

    [Fact]
    public async Task DetectionError_IsNotSticky_HotkeyStartsFreshScan()
    {
        ControllerHarness h = Harness();
        h.Capture.ThrowOnRegions = true;
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.Error, h.States.State);

        h.Capture.ThrowOnRegions = false;
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.PresetActive, h.States.State);
    }

    [Fact]
    public async Task ErrorState_AllowsOverride()
    {
        ControllerHarness h = Harness();
        h.Capture.ThrowOnRegions = true;
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.Error, h.States.State);

        h.Controller.ApplyManualOverride("Coldwind Farm");
        Assert.Equal(AppState.ManualOverride, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task ErrorState_DbdRunningStatus_ReturnsToReady()
    {
        ControllerHarness h = Harness();
        h.Capture.ThrowOnRegions = true;
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.Error, h.States.State);

        h.Dbd.RaiseStatusChanged(running: true);
        Assert.Equal(AppState.Ready, h.States.State);
    }

    // ---------- C10 / C11: texts ----------

    [Fact]
    public async Task StatusTexts_UseTheConfiguredHotkey()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions
        {
            Configure = c => c.DetectionHotkey = new DetectionHotkeyConfig { Key = "F9", Ctrl = true }
        });
        h.Ocr.Text = "PRESS START";

        Task scan = h.Controller.TriggerDetectionAsync();
        Assert.Contains("Ctrl+F9", h.Controller.StatusMessage);
        Assert.DoesNotContain("F8", h.Controller.StatusMessage);

        await h.Controller.TriggerDetectionAsync(); // "already scanning" can't happen: second press stops
        await scan;
        Assert.DoesNotContain("F8", h.Controller.DetectionText);
        Assert.Contains("Ctrl+F9", h.Controller.DetectionText);
    }

    [Fact]
    public async Task ScanFeedback_ShowsTheLastRead()
    {
        ControllerHarness h = Harness();
        h.Ocr.Text = "PRESS START";

        Task scan = h.Controller.TriggerDetectionAsync();
        await ControllerHarness.WaitUntilAsync(
            () => h.Controller.DetectionText.Contains("— last read:"), "per-sample feedback");
        Assert.StartsWith("Scanning… (", h.Controller.DetectionText);

        await h.Controller.TriggerDetectionAsync();
        await scan;
        Assert.DoesNotContain("last read", h.Controller.DetectionText); // no late sample text after the scan
    }

    // ---------- C12: window-handle-only changes ----------

    [Fact]
    public async Task WindowHandleOnlyChange_KeepsStatusStateAndRestartNotice()
    {
        ControllerHarness h = Harness();
        await h.Controller.TriggerDetectionAsync();

        string autohaven = Path.Combine(h.Dir, Guid.NewGuid() + "-Autohaven.ini");
        File.WriteAllText(autohaven, "[General]\nTechniques=C\nTechniqueSorting=C\n");
        Assert.Null(h.Controller.SetRealmPreset("Autohaven Wreckers", autohaven));
        Assert.True(h.Controller.NeedsDbdRestartForShortcuts);
        string status = h.Controller.StatusMessage;
        string detection = h.Controller.DetectionText;

        h.Dbd.MainWindowHandle = new IntPtr(0x9999); // same PID, new window
        h.Dbd.RaiseStatusChanged(running: true);

        Assert.Equal(status, h.Controller.StatusMessage);
        Assert.Equal(detection, h.Controller.DetectionText);
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.True(h.Controller.NeedsDbdRestartForShortcuts);

        h.Dbd.ProcessId = 9001; // DBD restarted between polls
        h.Dbd.RaiseStatusChanged(running: true);
        Assert.False(h.Controller.NeedsDbdRestartForShortcuts);
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Equal("Dead by Daylight detected.", h.Controller.StatusMessage);
    }

    [Fact]
    public async Task Scan_FollowsWindowHandleChanges()
    {
        ControllerHarness h = Harness();
        h.Dbd.MainWindowHandle = IntPtr.Zero; // window not created yet when the scan starts

        Task scan = h.Controller.TriggerDetectionAsync();
        h.Dbd.MainWindowHandle = new IntPtr(0x5678);
        h.Dbd.RaiseStatusChanged(running: true);

        Task finished = await Task.WhenAny(scan, Task.Delay(10_000));
        Assert.Same(scan, finished);
        await scan;
        Assert.Equal(AppState.PresetActive, h.States.State);
    }

    // ---------- C1: lock order ----------

    [Fact]
    public async Task LifecycleCapture_RunsOutsideTheControllerLock()
    {
        ControllerHarness h = Harness();
        bool probed = false;
        h.Capture.OnClientAreaCapture = () =>
        {
            // Another thread needs the controller lock while the lifecycle captures.
            var other = Task.Run(() => h.Controller.HasPendingActivation);
            Assert.True(other.Wait(TimeSpan.FromSeconds(5)), "controller lock held during lifecycle capture");
            probed = true;
        };

        await h.Controller.TriggerDetectionAsync(); // arms the lifecycle (captures the loading screen)
        h.Capture.OnClientAreaCapture = null;
        Assert.True(probed);
        Assert.Equal(LifecyclePhase.WatchingForTrial, h.Lifecycle.Phase);
    }

    [Fact]
    public async Task ConcurrentPollsCommandsAndActivations_DoNotDeadlock()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions
        {
            MaxAttempts = 3,
            VerifyDelayMs = 0,
            Lifecycle = new LifecycleOptions(
                PollIntervalMs: 3_600_000,
                TrialTransitionDistance: 30,
                TrialConfirmPolls: 1,
                MenuTransitionDistance: 35,
                MenuConfirmPolls: 1,
                MinimumTrialSeconds: 0)
        });
        h.Capture.Alternate = true; // every poll sees a scene change: phases flip constantly
        h.Keys.HoldSpins = 50_000; // each send "holds" its keys: an interleaved send is caught

        Task poller = Task.Run(() =>
        {
            for (int i = 0; i < 3000; i++)
            {
                h.Lifecycle.PollOnce();
            }
        });
        Task commands = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                h.Lifecycle.ArmForTrial();
                h.Lifecycle.EnsureArmed();
                h.Lifecycle.PollOnce();
                h.Lifecycle.Disarm();
                h.Lifecycle.EnsureArmed();
            }
        });
        Task buttons = Task.Run(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                h.Controller.ApplyManualOverride("Coldwind Farm");
                h.Controller.TestGlobalPreset();
                h.Controller.ReturnToAutomatic();
                h.Controller.RetryPendingActivation();
                switch (i % 10)
                {
                    case 9:
                        // DBD exits (disarm, Global as startup preset), then a new process starts.
                        h.Dbd.IsRunning = false;
                        h.Dbd.RaiseStatusChanged(running: false);
                        h.Dbd.IsRunning = true;
                        h.Dbd.ProcessId = 10_000 + i;
                        h.Dbd.RaiseStatusChanged(running: true);
                        break;
                    case 4:
                        // DBD restarted between two monitor polls: PID change (resets and disarms).
                        h.Dbd.ProcessId = 20_000 + i;
                        h.Dbd.RaiseStatusChanged(running: true);
                        break;
                    default:
                        h.Dbd.RaiseStatusChanged(running: true); // window-handle-only change
                        break;
                }
            }
        });

        // Scans (arm decisions, detected sends) keep running for the whole duration.
        using var stopScans = new CancellationTokenSource();
        Task scans = Task.Run(async () =>
        {
            while (!stopScans.IsCancellationRequested)
            {
                await h.Controller.TriggerDetectionAsync();
                await Task.Yield();
            }
        });

        Task others = Task.WhenAll(poller, commands, buttons);
        Task finished = await Task.WhenAny(others, Task.Delay(TimeSpan.FromSeconds(60)));
        stopScans.Cancel();
        Assert.True(ReferenceEquals(others, finished), "deadlock: concurrent lifecycle/controller work did not finish");
        await others;
        Task scansFinished = await Task.WhenAny(scans, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(scans, scansFinished), "deadlock: the scan loop did not finish");
        await scans;

        Assert.True(h.Keys.CallCount > 0);
        Assert.Equal(0, h.Keys.Overlaps); // no two sends ever interleaved

        // Still fully functional afterwards.
        h.Capture.Alternate = false;
        h.Capture.Level = 10;
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task Sends_FromDifferentSources_NeverInterleave()
    {
        ControllerHarness h = Harness();
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int sends = 0;
        h.Keys.OnSend = _ =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                holding.Set(); // the override's keys are down…
                release.Wait(TimeSpan.FromSeconds(10)); // …and held until the test lets go
            }
        };

        Task overrideSend = Task.Run(() => h.Controller.ApplyManualOverride("Coldwind Farm"));
        Assert.True(holding.Wait(TimeSpan.FromSeconds(5)));

        // Meanwhile a Test button (UI thread) wants to send too: it must wait
        // for the first down/hold/up sequence instead of interleaving with it.
        Task<string?> testSend = Task.Run(h.Controller.TestGlobalPreset);
        Assert.NotSame(testSend, await Task.WhenAny(testSend, Task.Delay(TimeSpan.FromMilliseconds(300))));
        Assert.Equal(1, h.Keys.CallCount);

        release.Set();
        await overrideSend.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await testSend.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal(0, h.Keys.Overlaps);
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath); // the newer request won
    }

    [Fact]
    public async Task Shutdown_StoppingAScan_SendsNothing()
    {
        ControllerHarness h = Harness();
        await h.Controller.TriggerDetectionAsync(); // a realm preset is active
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);

        h.Ocr.Text = "PRESS START";
        Task scan = h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.DetectingLoadingScreen, h.States.State);

        // Quitting stops the scan; a stopped scan normally switches to Global,
        // but quitting must not send keys to the game.
        h.Controller.Shutdown();
        await scan.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "Coldwind Farm" }, h.SentNames());
        Assert.False(h.Controller.HasPendingActivation);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
    }
}
