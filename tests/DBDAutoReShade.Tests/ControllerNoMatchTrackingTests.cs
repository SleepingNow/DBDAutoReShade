using System.Reflection;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// 1.2.1 regression tests: the app no longer guesses when gameplay starts or a match ends (the owner's real match
/// was "ended" by the old screen-change heuristic while it was still running). Nothing captures the screen outside
/// a scan, a switched preset stays until the user's next switch, a manual override lasts until Return to automatic,
/// a new scan, the Global preset hotkey or DBD exit, and "Start the next game with the Global preset" (the old
/// autoRestoreGlobalOnMenu key) gates only the startup-preset write when DBD closes.
/// Part of <see cref="ControllerActivationTests"/> (sequential).
/// </summary>
public sealed partial class ControllerActivationTests
{
    [Fact]
    public void TheMatchTracking_IsGoneFromEveryAssembly()
    {
        // The exact names 1.2.0 had (generic substrings like "Arm" or "Lifecycle" would trip over unrelated future
        // names); the behavioural test below is the real guard.
        Assembly[] assemblies = { typeof(AppController).Assembly, typeof(DetectionOrchestrator).Assembly, typeof(AppConfig).Assembly };
        string[] types = assemblies.SelectMany(a => a.GetTypes()).Select(t => t.Name).ToArray();
        foreach (string gone in new[] { "MatchLifecycleService", "LifecyclePhase", "LifecycleOptions" })
        {
            Assert.DoesNotContain(gone, types);
        }

        const BindingFlags everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        string[] members = typeof(AppController).GetMembers(everything).Select(m => m.Name).ToArray();
        string[] removedMembers =
        {
            "MatchEndFocusWindow", "_lifecycle", "_lifecycleCommandGate", "_lifecycleTicket", "_lastArmTicket",
            "_appliedLifecycleTicket", "NextLifecycleTicketLocked", "ApplyLifecycleCommand", "OnLifecyclePhase",
            "OnTrialObserved", "ReturnToGlobalAfterMatch", "MatchEndRequestLocked",
        };
        foreach (string gone in removedMembers)
        {
            Assert.DoesNotContain(gone, members);
        }

        Type origin = typeof(AppController).GetNestedType("ActivationOrigin", BindingFlags.NonPublic)!;
        Assert.NotNull(origin);
        Assert.DoesNotContain("MatchEnd", Enum.GetNames(origin));

        Assert.All(typeof(AppController).GetConstructors(), ctor =>
            Assert.DoesNotContain(ctor.GetParameters(), p => p.ParameterType.Name == "MatchLifecycleService" || p.Name == "lifecycle"));
    }

    [Fact]
    public async Task ADetectedPreset_StaysActive_NoGlobalComesByItself_AndNothingIsCapturedOutsideTheScan()
    {
        // A fast activation worker and an immediate read-back: anything that would run by itself gets every chance.
        ControllerHarness h = Harness(new ControllerHarnessOptions { RetryInterval = TimeSpan.FromMilliseconds(10), VerifyDelayMs = 0 });
        Assert.Equal(0, h.Capture.RegionCalls); // starting the app captures nothing

        await h.Controller.TriggerDetectionAsync();
        h.ReshadeSaves(h.ColdwindIni);
        Assert.Equal(AppState.PresetActive, h.States.State);
        int capturedByTheScan = h.Capture.RegionCalls;
        Assert.True(capturedByTheScan > 0);
        string status = h.Controller.StatusMessage;

        // The whole match and long after it (the old heuristic polled every 2.5 s): alt-tabs, a new DBD window, hours
        // on the monotonic clock. The screen is never captured again and the realm preset stays.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; watch.Elapsed < TimeSpan.FromSeconds(3); i++)
        {
            h.Focused = i % 2 == 0; // in and out of the game
            if (i % 10 == 0)
            {
                h.Dbd.MainWindowHandle = new IntPtr(0x1234 + i); // same process, new window
                h.Dbd.RaiseStatusChanged(running: true);
            }

            h.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(50);
        }

        h.Focused = true;
        Assert.Equal(capturedByTheScan, h.Capture.RegionCalls);
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
        Assert.Equal("Coldwind Farm", h.Controller.ActiveRealm);
        Assert.Equal(new[] { "Coldwind Farm" }, h.SentNames());
        Assert.False(h.Controller.HasPendingActivation);
        Assert.True(h.Controller.RetryPendingActivation()); // nothing pending: nothing sent
        Assert.Equal(1, h.Keys.CallCount);
        Assert.Equal(status, h.Controller.StatusMessage);
        Assert.DoesNotContain("Match", h.Controller.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWayBackToGlobal_IsTheGlobalPresetHotkey()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 60_000 });
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);
        int captured = h.Capture.RegionCalls;

        h.GlobalPresetHotkey.Press(); // F9

        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Equal(captured, h.Capture.RegionCalls); // a switch never captures the screen
    }

    [Fact]
    public async Task AManualOverride_LastsUntilTheUserEndsIt()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { RetryInterval = TimeSpan.FromMilliseconds(10), VerifyDelayMs = 0 });
        h.Controller.ApplyManualOverride("Coldwind Farm");
        Assert.Equal(AppState.ManualOverride, h.States.State);
        Assert.Equal(h.ColdwindIni, h.Controller.ActivePresetPath);

        // A match and the menus after it: nothing clears it by itself.
        for (int i = 0; i < 20; i++)
        {
            h.Focused = i % 2 == 0;
            h.Advance(TimeSpan.FromMinutes(10));
            await Task.Delay(25);
        }

        h.Dbd.MainWindowHandle = new IntPtr(0x7777); // a window-handle-only change
        h.Dbd.RaiseStatusChanged(running: true);
        h.Focused = true;

        Assert.True(h.Controller.IsManualOverride);
        Assert.Equal("Coldwind Farm", h.Controller.OverrideRealm);
        Assert.Equal(AppState.ManualOverride, h.States.State);
        Assert.Equal(new[] { "Coldwind Farm" }, h.SentNames());
        Assert.Equal(0, h.Capture.RegionCalls);
    }

    [Theory]
    [InlineData("Return to automatic")]
    [InlineData("F8")]
    [InlineData("F9")]
    [InlineData("DBD exit")]
    public async Task AManualOverride_EndsWith(string how)
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 60_000 });
        h.Controller.ApplyManualOverride("Coldwind Farm");
        Assert.True(h.Controller.IsManualOverride);

        switch (how)
        {
            case "Return to automatic":
                h.Controller.ReturnToAutomatic();
                Assert.Equal(AppState.Ready, h.States.State);
                Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());
                break;

            case "F8":
                await h.Controller.TriggerDetectionAsync(); // a new scan reads Coldwind Farm again: automatic
                Assert.Equal(AppState.PresetActive, h.States.State);
                break;

            case "F9":
                h.GlobalPresetHotkey.Press();
                Assert.Equal(AppState.Ready, h.States.State);
                Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());
                break;

            case "DBD exit":
                h.Dbd.IsRunning = false;
                h.Dbd.RaiseStatusChanged(running: false);
                Assert.Equal(AppState.WaitingForDbd, h.States.State);
                break;
        }

        Assert.False(h.Controller.IsManualOverride);
        Assert.Null(h.Controller.OverrideRealm);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartNextGameWithGlobal_GatesOnlyTheStartupPresetWrite_WhenDbdCloses(bool on)
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions
        {
            VerifyDelayMs = 0,
            Configure = c => c.StartNextGameWithGlobal = on,
        });
        await h.Controller.TriggerDetectionAsync();
        h.ReshadeSaves(h.ColdwindIni);

        // While DBD runs the setting changes nothing: toggling it sends nothing and writes nothing…
        string ini = File.ReadAllText(h.ReshadeIni);
        h.Controller.SetStartNextGameWithGlobal(!on);
        h.Controller.SetStartNextGameWithGlobal(on);
        Assert.Equal(ini, File.ReadAllText(h.ReshadeIni));
        Assert.Equal(new[] { "Coldwind Farm" }, h.SentNames());

        // …a scan without a confident read still switches to Global…
        h.Ocr.Text = "PRESS START";
        Task scan = h.Controller.TriggerDetectionAsync();
        await h.Controller.TriggerDetectionAsync(); // stop
        await scan;
        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());

        // …and so do F9 and Return to automatic.
        h.Ocr.Text = "FRACTURED COWSHED\nCOLDWIND FARM";
        await h.Controller.TriggerDetectionAsync();
        h.GlobalPresetHotkey.Press();
        h.Controller.ApplyManualOverride("Coldwind Farm");
        h.Controller.ReturnToAutomatic();
        Assert.Equal(
            new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey, "Coldwind Farm", AppConfigKeys.GlobalShortcutKey, "Coldwind Farm", AppConfigKeys.GlobalShortcutKey },
            h.SentNames());

        // ReShade ignored that last Global switch and still runs Coldwind when DBD closes: only now does the setting count.
        h.ReshadeSaves(h.ColdwindIni);
        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);

        Assert.Equal(on ? h.GlobalIni : h.ColdwindIni, h.ReshadeStartupPreset());
        Assert.Equal(
            on ? "Launch Dead by Daylight. ReShade will start with the Global preset." : "Launch Dead by Daylight.",
            h.Controller.StatusMessage);
        Assert.Equal(6, h.Keys.CallCount); // closing DBD sends nothing
    }

    [Fact]
    public void SetStartNextGameWithGlobal_IsSaved_UnderTheOldKey()
    {
        ControllerHarness h = Harness();
        Assert.True(h.Controller.Config.StartNextGameWithGlobal); // on by default

        h.Controller.SetStartNextGameWithGlobal(false);
        Assert.Contains("\"autoRestoreGlobalOnMenu\": false", File.ReadAllText(h.ConfigPath));

        var reloaded = new AppConfigService(h.ConfigPath);
        reloaded.Load();
        Assert.False(reloaded.Config.StartNextGameWithGlobal);
    }
}
