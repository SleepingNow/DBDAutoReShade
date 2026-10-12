using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// Controller robustness: no deadlock and no interleaved sends under concurrent
/// switches (C1), sticky Error (C5), configured hotkey in texts (C10),
/// per-sample feedback (C11) and window-handle-only changes (C12).
/// </summary>
public sealed partial class ControllerActivationTests
{
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

    // ---------- C1: no deadlock, no interleaved sends ----------

    [Fact]
    public async Task ConcurrentSwitchesScansAndDbdChanges_DoNotDeadlock()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions
        {
            MaxAttempts = 3,
            VerifyDelayMs = 0,
        });
        h.Keys.HoldSpins = 50_000; // each send "holds" its keys: an interleaved send is caught

        // The Global preset hotkey, pressed again and again (queued off its pump thread, as in the app).
        Task globalPresses = Task.Run(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                if (h.GlobalPresetHotkey.IsRegistered)
                {
                    h.GlobalPresetHotkey.PressWithoutWaiting();
                }

                h.Controller.ActivateGlobalNow();
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
                        // DBD exits (Global as startup preset), then a new process starts.
                        h.Dbd.IsRunning = false;
                        h.Dbd.RaiseStatusChanged(running: false);
                        h.Dbd.IsRunning = true;
                        h.Dbd.ProcessId = 10_000 + i;
                        h.Dbd.RaiseStatusChanged(running: true);
                        break;
                    case 4:
                        // DBD restarted between two monitor polls: PID change (resets).
                        h.Dbd.ProcessId = 20_000 + i;
                        h.Dbd.RaiseStatusChanged(running: true);
                        break;
                    default:
                        h.Dbd.RaiseStatusChanged(running: true); // window-handle-only change
                        break;
                }
            }
        });

        // Scans (detections, detected sends) keep running for the whole duration.
        using var stopScans = new CancellationTokenSource();
        Task scans = Task.Run(async () =>
        {
            while (!stopScans.IsCancellationRequested)
            {
                await h.Controller.TriggerDetectionAsync();
                await Task.Yield();
            }
        });

        Task others = Task.WhenAll(globalPresses, buttons);
        Task finished = await Task.WhenAny(others, Task.Delay(TimeSpan.FromSeconds(60)));
        stopScans.Cancel();
        Assert.True(ReferenceEquals(others, finished), "deadlock: concurrent controller work did not finish");
        await others;
        Task scansFinished = await Task.WhenAny(scans, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(scans, scansFinished), "deadlock: the scan loop did not finish");
        await scans;

        await h.Controller.GlobalHotkeyPressesHandled.WaitAsync(TimeSpan.FromSeconds(30)); // the queued presses ran
        Assert.True(h.Keys.CallCount > 0);
        Assert.Equal(0, h.Keys.Overlaps); // no two sends ever interleaved

        // Still fully functional afterwards.
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
