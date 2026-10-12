using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DBDAutoReShade.App.Services;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// The Global preset hotkey (default F9): its registration next to the
/// detection hotkey, changing and clearing it, capture mode, and
/// <see cref="AppController.ActivateGlobalNow"/> in every state.
/// Part of <see cref="ControllerActivationTests"/> (sequential).
/// </summary>
public sealed partial class ControllerActivationTests
{
    private static readonly DetectionHotkeyConfig F8 = new() { Key = "F8" };
    private static readonly DetectionHotkeyConfig F9 = new() { Key = "F9" };

    private const string GlobalSwitched = "Switched to the Global preset (F9).";

    /// <summary>
    /// A harness whose ReShade currently runs Coldwind, so a switch to Global is
    /// a real change. The read-back watches ReShade.ini for a minute, so its
    /// "not confirmed" text never replaces a status a test is still reading.
    /// </summary>
    private ControllerHarness HarnessOnColdwind(ControllerHarnessOptions? options = null)
    {
        ControllerHarness h = Harness((options ?? new ControllerHarnessOptions()) with { VerifyDelayMs = 60_000 });
        h.ReshadeSaves(h.ColdwindIni);
        return h;
    }

    // ---------- Registration ----------

    [Fact]
    public void BothHotkeys_AreRegistered_SideBySide()
    {
        ControllerHarness h = Harness();

        Assert.True(h.Hotkey.Holds(F8));
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
        Assert.Null(h.Controller.HotkeyError);
        Assert.Null(h.Controller.GlobalHotkeyError);
        Assert.Equal("F9", h.Controller.GlobalHotkey?.ToString());
        Assert.StartsWith("Dead by Daylight detected.", h.Controller.StatusMessage);
    }

    [Fact]
    public void GlobalHotkeyHeldElsewhereAtStartup_IsSurfaced_AndAddedToTheStartupStatus()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { HotkeysHeldElsewhere = new[] { F9 } });

        Assert.Contains("already in use", h.Controller.GlobalHotkeyError);
        Assert.Null(h.Controller.HotkeyError); // F8 still works
        Assert.True(h.Hotkey.Holds(F8));
        Assert.False(h.GlobalPresetHotkey.IsRegistered);
        Assert.StartsWith("Dead by Daylight detected.", h.Controller.StatusMessage);
        Assert.Contains("Global preset hotkey 'F9' unavailable", h.Controller.StatusMessage);
        Assert.Contains("Global preset hotkey: F9 — unavailable:", h.Controller.RunReshadeDiagnostics());
    }

    [Fact]
    public void ClearedGlobalHotkey_RegistersNothing()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.GlobalHotkey = null });

        Assert.Null(h.Controller.GlobalHotkey);
        Assert.False(h.GlobalPresetHotkey.IsRegistered);
        Assert.Null(h.Controller.GlobalHotkeyError);
        Assert.Contains("Global preset hotkey: not set", h.Controller.RunReshadeDiagnostics());
    }

    [Fact]
    public void UpdateGlobalHotkey_RegistersFirst_ThenSaves()
    {
        ControllerHarness h = Harness();

        Assert.Null(h.Controller.UpdateGlobalHotkey(Combo("F19"))); // Ctrl+Alt+F19: not a ReShade shortcut combo

        Assert.True(h.GlobalPresetHotkey.Holds(Combo("F19")));
        Assert.Equal("Ctrl+Alt+F19", h.Controller.GlobalHotkey?.ToString());
        Assert.Equal("F19", ConfigOnDisk(h).GlobalHotkey?.Key);
        Assert.Equal("Global preset hotkey: Ctrl+Alt+F19.", h.Controller.StatusMessage);
        Assert.True(h.Hotkey.Holds(F8)); // the detection hotkey is untouched
        Assert.Contains("Global preset hotkey: Ctrl+Alt+F19", h.Controller.RunReshadeDiagnostics());

        // A press now uses the new key's text.
        h.ReshadeSaves(h.ColdwindIni);
        h.GlobalPresetHotkey.Press();
        Assert.Equal("Switched to the Global preset (Ctrl+Alt+F19).", h.Controller.StatusMessage);
    }

    /// <summary>
    /// The combos the app sends to ReShade: the Global preset's own (a press would
    /// trigger the hotkey again and again), a realm's (a detected switch would land
    /// on Global), and the rest of the managed pool (a later assignment may use them).
    /// </summary>
    public static TheoryData<string> ReshadeShortcutCombos => new() { "global", "realm", "F24", "0" };

    private static DetectionHotkeyConfig ReshadeCombo(ControllerHarness h, string which)
    {
        int vk = which switch
        {
            "global" => h.Controller.Config.ReshadeShortcuts[AppConfigKeys.GlobalShortcutKey].VirtualKey,
            "realm" => h.Controller.Config.ReshadeShortcuts["Coldwind Farm"].VirtualKey,
            "F24" => 0x87,
            _ => '0'
        };
        return AsHotkey(vk);
    }

    private static DetectionHotkeyConfig AsHotkey(int vk) => new()
    {
        Key = vk >= 0x70 && vk <= 0x87 ? $"F{vk - 0x6F}" : ((char)vk).ToString(),
        Ctrl = true,
        Shift = true,
        Alt = true
    };

    /// <summary>The combo the harness's earlier run gave <paramref name="name"/> (the Global key or a realm).</summary>
    private static DetectionHotkeyConfig AllocatedCombo(string name) =>
        AsHotkey(ReshadeConfigurationService.AllocateShortcuts(new[] { AppConfigKeys.GlobalShortcutKey, "Coldwind Farm" })[name].VirtualKey);

    [Theory]
    [MemberData(nameof(ReshadeShortcutCombos))]
    public void UpdateGlobalHotkey_AReshadeShortcutCombo_IsRefused(string which)
    {
        ControllerHarness h = Harness();
        DetectionHotkeyConfig combo = ReshadeCombo(h, which);
        int starts = h.GlobalPresetHotkey.StartCount;

        string? error = h.Controller.UpdateGlobalHotkey(combo);

        Assert.Equal($"{combo} is reserved for ReShade preset shortcuts.", error);
        Assert.Equal(starts, h.GlobalPresetHotkey.StartCount); // never registered, not even for a moment
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
        Assert.Equal("F9", h.Controller.GlobalHotkey?.ToString());
        Assert.Equal("F9", ConfigOnDisk(h).GlobalHotkey?.ToString());
        Assert.Null(h.Controller.GlobalHotkeyError);
        Assert.Contains("F9 is still the Global preset hotkey.", h.Controller.StatusMessage);
    }

    [Theory]
    [MemberData(nameof(ReshadeShortcutCombos))]
    public void UpdateHotkey_AReshadeShortcutCombo_IsRefused(string which)
    {
        ControllerHarness h = Harness();
        DetectionHotkeyConfig combo = ReshadeCombo(h, which);
        int starts = h.Hotkey.StartCount;

        string? error = h.Controller.UpdateHotkey(combo);

        Assert.Equal($"{combo} is reserved for ReShade preset shortcuts.", error);
        Assert.Equal(starts, h.Hotkey.StartCount);
        Assert.True(h.Hotkey.Holds(F8));
        Assert.Equal("F8", ConfigOnDisk(h).DetectionHotkey.ToString());
        Assert.Null(h.Controller.HotkeyError);
        Assert.Contains("F8 is still the detection hotkey.", h.Controller.StatusMessage);
    }

    [Fact]
    public void GlobalHotkeyOnTheGlobalPresetsOwnCombo_IsNeverRegistered_AndSaysWhy()
    {
        // E.g. a hand-edited config: the combo the app sends for the Global preset.
        DetectionHotkeyConfig own = AllocatedCombo(AppConfigKeys.GlobalShortcutKey);
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.GlobalHotkey = own });
        Assert.True(HotkeyParser.SameCombo(own, ReshadeCombo(h, "global")));

        Assert.False(h.GlobalPresetHotkey.IsRegistered); // so the app's own send can't trigger it
        Assert.Equal($"{own} is reserved for ReShade preset shortcuts.", h.Controller.GlobalHotkeyError);
        Assert.StartsWith("Dead by Daylight detected.", h.Controller.StatusMessage);
        Assert.Contains($"Global preset hotkey '{own}' unavailable: {own} is reserved", h.Controller.StatusMessage);
        Assert.Contains($"Global preset hotkey: {own} — unavailable:", h.Controller.RunReshadeDiagnostics());
        Assert.True(h.Hotkey.Holds(F8));
        Assert.Equal(own.ToString(), ConfigOnDisk(h).GlobalHotkey?.ToString()); // kept as saved, not "cleared"

        // A capture that ends without a change doesn't register it either.
        h.Controller.SuspendHotkey();
        h.Controller.ResumeHotkey();
        Assert.False(h.GlobalPresetHotkey.IsRegistered);

        // Choosing another key fixes it.
        Assert.Null(h.Controller.UpdateGlobalHotkey(F9));
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
        Assert.Null(h.Controller.GlobalHotkeyError);
    }

    [Fact]
    public void DetectionHotkeyOnAReshadeShortcutCombo_IsNeverRegistered_AndSaysWhy()
    {
        DetectionHotkeyConfig realmCombo = AllocatedCombo("Coldwind Farm");
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.DetectionHotkey = realmCombo });
        Assert.True(HotkeyParser.SameCombo(realmCombo, ReshadeCombo(h, "realm")));

        Assert.False(h.Hotkey.IsRegistered); // a detected Coldwind switch would toggle the scan instead
        Assert.Equal($"{realmCombo} is reserved for ReShade preset shortcuts.", h.Controller.HotkeyError);
        Assert.Contains($"Hotkey '{realmCombo}' unavailable: {realmCombo} is reserved", h.Controller.StatusMessage);
        Assert.Contains($"Detection hotkey: {realmCombo} — unavailable:", h.Controller.RunReshadeDiagnostics());
        Assert.True(h.GlobalPresetHotkey.Holds(F9)); // the Global preset hotkey still works

        // A refused change keeps it released (it is not registered again as "the previous one").
        Assert.NotNull(h.Controller.UpdateHotkey(new DetectionHotkeyConfig { Key = "W" }));
        Assert.False(h.Hotkey.IsRegistered);
        Assert.Contains("could not be registered again either", h.Controller.StatusMessage);

        Assert.Null(h.Controller.UpdateHotkey(F8));
        Assert.True(h.Hotkey.Holds(F8));
        Assert.Null(h.Controller.HotkeyError);
    }

    [Fact]
    public void ConfigSavedBy11_WithF9AsTheDetectionHotkey_KeepsF9ForDetection_AndSaysWhyGlobalIsOff()
    {
        // A config as 1.1 saved it (no 1.2 settings), with F9 as the detection hotkey.
        ControllerHarness first = Harness();
        first.Dispose(); // the old version exits
        JsonObject json = JsonNode.Parse(File.ReadAllText(first.ConfigPath))!.AsObject();
        foreach (string added in new[] { "globalHotkey", "animationsEnabled", "soundEnabled", "soundVolume" })
        {
            Assert.True(json.Remove(added));
        }

        json["detectionHotkey"] = new JsonObject { ["key"] = "F9", ["ctrl"] = false, ["shift"] = false, ["alt"] = false };
        File.WriteAllText(first.ConfigPath, json.ToJsonString());

        ControllerHarness h = Harness(new ControllerHarnessOptions { RestartOf = first }); // the first 1.2 start

        Assert.True(h.Hotkey.Holds(F9)); // F9 stays the detection hotkey
        Assert.False(h.GlobalPresetHotkey.IsRegistered);
        Assert.Equal("F9", h.Controller.GlobalHotkey?.ToString()); // the default, not "cleared"
        const string Why = "F9 is also the detection hotkey; choose another key for the Global preset.";
        Assert.Equal(Why, h.Controller.GlobalHotkeyError); // the banner
        Assert.StartsWith("Dead by Daylight detected.", h.Controller.StatusMessage);
        Assert.Contains($"Global preset hotkey 'F9' unavailable: {Why}", h.Controller.StatusMessage);

        // Saved (startup and any later save): still F9, never an explicit null (a user's Clear).
        h.Controller.SetSoundVolume(0.4);
        string saved = File.ReadAllText(h.ConfigPath);
        Assert.DoesNotContain("\"globalHotkey\": null", saved);
        Assert.Equal("F9", ConfigOnDisk(h).GlobalHotkey?.ToString());

        // Confirming F9 for detection again is allowed (it already is).
        Assert.Null(h.Controller.UpdateHotkey(F9));
        Assert.True(h.Hotkey.Holds(F9));
        Assert.Equal(Why, h.Controller.GlobalHotkeyError);

        // Moving detection elsewhere frees F9: the Global preset hotkey works from then on.
        Assert.Null(h.Controller.UpdateHotkey(new DetectionHotkeyConfig { Key = "F7" }));
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
        Assert.Null(h.Controller.GlobalHotkeyError);
        Assert.Equal("Detection hotkey: F7.", h.Controller.StatusMessage);
        h.ReshadeSaves(h.ColdwindIni);
        h.GlobalPresetHotkey.Press();
        Assert.Equal(GlobalSwitched, h.Controller.StatusMessage);
    }

    [Fact]
    public void UpdateGlobalHotkey_WhenRegistrationFails_KeepsAndReRegistersThePreviousOne()
    {
        ControllerHarness h = Harness();
        h.Hotkey.HoldElsewhere(Combo("F21")); // another program holds Ctrl+Alt+F21

        string? error = h.Controller.UpdateGlobalHotkey(Combo("F21"));

        Assert.Equal(GlobalHotkeyService.AlreadyInUseError, error);
        Assert.Equal("F9", h.Controller.GlobalHotkey?.Key);
        Assert.Equal("F9", ConfigOnDisk(h).GlobalHotkey?.Key); // never saved
        Assert.True(h.GlobalPresetHotkey.Holds(F9)); // and still working
        Assert.Null(h.Controller.GlobalHotkeyError);
        Assert.Contains("unavailable for the Global preset", h.Controller.StatusMessage);
        Assert.Contains("F9 is still the Global preset hotkey.", h.Controller.StatusMessage);
    }

    [Fact]
    public void UpdateGlobalHotkey_FromCleared_WhenRegistrationFails_StaysCleared()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.GlobalHotkey = null });
        h.Hotkey.HoldElsewhere(Combo("F21"));

        Assert.NotNull(h.Controller.UpdateGlobalHotkey(Combo("F21")));

        Assert.Null(h.Controller.GlobalHotkey);
        Assert.False(h.GlobalPresetHotkey.IsRegistered);
        Assert.Null(h.Controller.GlobalHotkeyError);
        Assert.Contains("The Global preset still has no hotkey.", h.Controller.StatusMessage);
    }

    [Fact]
    public void UpdateGlobalHotkey_TheDetectionHotkeysCombo_IsRefused()
    {
        ControllerHarness h = Harness();

        string? error = h.Controller.UpdateGlobalHotkey(new DetectionHotkeyConfig { Key = "f8" });

        Assert.Equal("f8 is already the detection hotkey.", error);
        Assert.True(h.Hotkey.Holds(F8));
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
        Assert.Equal("F9", h.Controller.GlobalHotkey?.Key);
        Assert.Equal("F9", ConfigOnDisk(h).GlobalHotkey?.Key);
    }

    [Fact]
    public void UpdateHotkey_TheGlobalHotkeysCombo_IsRefused()
    {
        ControllerHarness h = Harness();

        string? error = h.Controller.UpdateHotkey(F9);

        Assert.Equal("F9 is already the Global preset hotkey.", error);
        Assert.True(h.Hotkey.Holds(F8));
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
        Assert.Equal("F8", h.Controller.Config.DetectionHotkey.Key);
        Assert.Contains("F8 is still the detection hotkey.", h.Controller.StatusMessage);

        // With modifiers it is another combo: allowed.
        Assert.Null(h.Controller.UpdateHotkey(new DetectionHotkeyConfig { Key = "F9", Ctrl = true }));
        Assert.True(h.Hotkey.Holds(new DetectionHotkeyConfig { Key = "F9", Ctrl = true }));
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
    }

    [Fact]
    public void UpdateGlobalHotkey_BareLetter_IsRejected_TheOldOneStays()
    {
        ControllerHarness h = Harness();

        Assert.Equal(HotkeyParser.ModifierRequiredError, h.Controller.UpdateGlobalHotkey(new DetectionHotkeyConfig { Key = "W" }));

        Assert.Equal("F9", h.Controller.GlobalHotkey?.Key);
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
        Assert.Null(h.Controller.GlobalHotkeyError);
    }

    [Fact]
    public void ClearGlobalHotkey_ReleasesIt_SavesItAsCleared_AndItCanBeSetAgain()
    {
        ControllerHarness h = Harness();

        h.Controller.ClearGlobalHotkey();

        Assert.Null(h.Controller.GlobalHotkey);
        Assert.False(h.GlobalPresetHotkey.IsRegistered);
        Assert.Equal("Global preset hotkey cleared.", h.Controller.StatusMessage);
        Assert.Contains("\"globalHotkey\": null", File.ReadAllText(h.ConfigPath)); // cleared, not missing
        Assert.Null(ConfigOnDisk(h).GlobalHotkey); // and it stays cleared when loaded again
        Assert.True(h.Hotkey.Holds(F8));

        Assert.Null(h.Controller.UpdateGlobalHotkey(new DetectionHotkeyConfig { Key = "F10" }));
        Assert.True(h.GlobalPresetHotkey.Holds(new DetectionHotkeyConfig { Key = "F10" }));
        Assert.Equal("F10", ConfigOnDisk(h).GlobalHotkey?.Key);
    }

    [Fact]
    public void ClearedGlobalHotkey_SurvivesAnAppRestart()
    {
        ControllerHarness first = Harness();
        first.Controller.ClearGlobalHotkey();

        ControllerHarness h = Harness(new ControllerHarnessOptions { RestartOf = first });

        Assert.Null(h.Controller.GlobalHotkey);
        Assert.False(h.GlobalPresetHotkey.IsRegistered);
    }

    public static TheoryData<string> CaptureEndings => new() { "resume", "detection", "global", "clear", "refused" };

    [Theory]
    [MemberData(nameof(CaptureEndings))]
    public void Capture_ReleasesBothHotkeys_AndEveryWayOutRegistersBothAgain(string ending)
    {
        ControllerHarness h = Harness();

        h.Controller.SuspendHotkey();
        Assert.False(h.Hotkey.IsRegistered);
        Assert.False(h.GlobalPresetHotkey.IsRegistered); // pressing F9 now reaches the window, not ActivateGlobalNow

        DetectionHotkeyConfig expectedDetection = F8;
        DetectionHotkeyConfig? expectedGlobal = F9;
        switch (ending)
        {
            case "resume":
                h.Controller.ResumeHotkey();
                break;
            case "detection":
                expectedDetection = new DetectionHotkeyConfig { Key = "F7" };
                Assert.Null(h.Controller.UpdateHotkey(expectedDetection));
                break;
            case "global":
                expectedGlobal = new DetectionHotkeyConfig { Key = "F10" };
                Assert.Null(h.Controller.UpdateGlobalHotkey(expectedGlobal));
                break;
            case "clear":
                expectedGlobal = null;
                h.Controller.ClearGlobalHotkey();
                break;
            case "refused":
                Assert.NotNull(h.Controller.UpdateGlobalHotkey(F8)); // the detection hotkey's combo
                break;
        }

        Assert.True(h.Hotkey.Holds(expectedDetection));
        if (expectedGlobal is null)
        {
            Assert.False(h.GlobalPresetHotkey.IsRegistered);
        }
        else
        {
            Assert.True(h.GlobalPresetHotkey.Holds(expectedGlobal));
        }

        Assert.Null(h.Controller.HotkeyError);
        Assert.Null(h.Controller.GlobalHotkeyError);
    }

    [Fact]
    public void Capture_OfTheDetectionHotkey_RefusedCombo_RegistersBothAgain()
    {
        ControllerHarness h = Harness();
        h.Controller.SuspendHotkey();

        Assert.NotNull(h.Controller.UpdateHotkey(F9));

        Assert.True(h.Hotkey.Holds(F8));
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
    }

    [Fact]
    public void ResetSettings_RegistersBothDefaultHotkeys_EvenWhenTheyWereSwapped()
    {
        // The detection hotkey on F20 and the Global preset hotkey on F8: the
        // reset gives F8 back to detection and F9 to Global (no "in use" clash).
        ControllerHarness h = Harness(new ControllerHarnessOptions
        {
            Configure = c =>
            {
                c.DetectionHotkey = Combo("F20");
                c.GlobalHotkey = new DetectionHotkeyConfig { Key = "F8" };
                c.AnimationsEnabled = false;
                c.SoundEnabled = false;
                c.SoundVolume = 0.9;
            }
        });
        Assert.True(h.GlobalPresetHotkey.Holds(F8));

        h.Controller.ResetSettings();

        Assert.True(h.Hotkey.Holds(F8));
        Assert.True(h.GlobalPresetHotkey.Holds(F9));
        Assert.Null(h.Controller.HotkeyError);
        Assert.Null(h.Controller.GlobalHotkeyError);
        Assert.True(h.Controller.AnimationsEnabled);
        Assert.True(h.Controller.SoundEnabled);
        Assert.Equal(AppConfig.DefaultSoundVolume, h.Controller.SoundVolume);
        AppConfig onDisk = ConfigOnDisk(h);
        Assert.Equal("F9", onDisk.GlobalHotkey?.Key);
        Assert.Equal(0.5, onDisk.SoundVolume);
        Assert.StartsWith("Settings reset to defaults.", h.Controller.StatusMessage);
    }

    // ---------- ActivateGlobalNow ----------

    [Fact]
    public void GlobalHotkey_WhenDbdIsNotRunning_SaysSo()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { DbdRunningAtStart = false });
        Assert.Equal(AppState.WaitingForDbd, h.States.State);

        h.GlobalPresetHotkey.Press();

        Assert.Equal("Dead by Daylight is not running.", h.Controller.StatusMessage);
        Assert.Equal(0, h.Keys.CallCount);
        Assert.False(h.Controller.HasPendingActivation);
        Assert.Equal(AppState.WaitingForDbd, h.States.State);
    }

    [Fact]
    public void GlobalHotkey_WithoutAGlobalPreset_SaysWhereToSetIt()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.GlobalPresetPath = null });
        h.Controller.ApplyManualOverride("Coldwind Farm");
        int sent = h.Keys.CallCount;

        h.GlobalPresetHotkey.Press();

        Assert.Equal("No Global / Menu preset is set — choose one under Presets.", h.Controller.StatusMessage);
        Assert.Equal(sent, h.Keys.CallCount);
        Assert.False(h.Controller.HasPendingActivation);
        Assert.Equal(AppState.ManualOverride, h.States.State); // nothing else changes
    }

    [Fact]
    public async Task GlobalHotkey_InGame_SwitchesAtOnce_AndReShadeConfirmsIt()
    {
        ControllerHarness h = HarnessOnColdwind();
        int changes = 0;
        h.Controller.Changed += (_, _) => Interlocked.Increment(ref changes);

        h.GlobalPresetHotkey.Press();

        Assert.Equal(new[] { AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal(GlobalSwitched, h.Controller.StatusMessage);
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
        Assert.Null(h.Controller.ActiveRealm);
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.False(h.Controller.HasPendingActivation);
        Assert.Contains("Global preset hotkey (F9) →", h.Controller.LastActivationText);
        Assert.Contains("OK", h.Controller.LastActivationText);
        Assert.True(changes > 0);

        h.ReshadeSaves(h.GlobalIni); // ReShade switched and saved
        await ControllerHarness.WaitUntilAsync(
            () => h.Controller.StatusMessage == "Switched to the Global preset (F9) — confirmed by ReShade.",
            "the read-back confirmation");
    }

    [Fact]
    public void GlobalHotkey_WhenGlobalIsAlreadyActive_SendsAnyway_AndSaysSo()
    {
        ControllerHarness h = Harness(); // ReShade.ini says Global

        h.GlobalPresetHotkey.Press();

        Assert.Equal(new[] { AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal("Switched to the Global preset (F9) — already active in ReShade.", h.Controller.StatusMessage);
    }

    [Fact]
    public void GlobalHotkey_Unfocused_WaitsForFocus_ThenSends()
    {
        ControllerHarness h = HarnessOnColdwind();
        h.Focused = false; // e.g. pressed while the app's own window is in front

        h.GlobalPresetHotkey.Press();

        Assert.Equal(0, h.Keys.CallCount);
        Assert.True(h.Controller.HasPendingActivation);
        Assert.Equal(
            $"F9 pressed. Waiting for DBD focus to switch to {Path.GetFileName(h.GlobalIni)} — click into the game.",
            h.Controller.StatusMessage);
        Assert.Equal(AppState.Ready, h.States.State);

        Assert.False(h.Controller.RetryPendingActivation()); // still waiting: no attempt used
        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(new[] { AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal(GlobalSwitched, h.Controller.StatusMessage);
        Assert.False(h.Controller.HasPendingActivation);
    }

    [Fact]
    public void GlobalHotkey_Unfocused_GivesUpAfter60Seconds()
    {
        ControllerHarness h = Harness();
        h.Focused = false;
        h.GlobalPresetHotkey.Press();

        h.Advance(AppController.GlobalHotkeyFocusWindow - TimeSpan.FromSeconds(1));
        Assert.False(h.Controller.RetryPendingActivation());
        Assert.True(h.Controller.HasPendingActivation);

        h.Advance(TimeSpan.FromSeconds(1));
        Assert.False(h.Controller.RetryPendingActivation());
        Assert.False(h.Controller.HasPendingActivation);
        Assert.Equal(
            $"F9 pressed. Gave up switching to {Path.GetFileName(h.GlobalIni)}: Dead by Daylight wasn't focused within 60 s.",
            h.Controller.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(60), AppController.GlobalHotkeyFocusWindow);

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation()); // nothing left to send
        Assert.Equal(0, h.Keys.CallCount);
    }

    [Fact]
    public void GlobalHotkey_RejectedSends_UseTheAttemptBudget_ThenFail()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { MaxAttempts = 2 });
        h.Keys.NextResult = false; // Windows blocks the keystrokes

        h.GlobalPresetHotkey.Press();
        Assert.Contains("retrying", h.Controller.StatusMessage);
        Assert.False(h.Controller.RetryPendingActivation());

        Assert.False(h.Controller.HasPendingActivation);
        Assert.StartsWith($"F9 pressed. Could not switch to {Path.GetFileName(h.GlobalIni)}:", h.Controller.StatusMessage);
        Assert.Equal(2, h.Keys.CallCount);
        Assert.Contains("FAILED", h.Controller.LastActivationText);
        Assert.Equal(AppState.Ready, h.States.State);
    }

    [Fact]
    public async Task GlobalHotkey_WhileScanning_StopsTheScan_WithoutItsOwnGlobalRequest()
    {
        ControllerHarness h = HarnessOnColdwind();
        h.Ocr.Text = "PRESS START"; // reads, but never confirms: the scan keeps going
        var detected = new ConcurrentQueue<MapDetectedEventArgs>();
        h.Controller.MapDetected += (_, e) => detected.Enqueue(e);

        Task scan = h.Controller.TriggerDetectionAsync();
        await ControllerHarness.WaitUntilAsync(() => h.Ocr.Calls >= 2, "the scan reads");
        Assert.Equal(AppState.DetectingLoadingScreen, h.States.State);

        h.GlobalPresetHotkey.Press();
        Assert.Equal(AppState.Ready, h.States.State); // at once, while the scan winds down
        await scan.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { AppConfigKeys.GlobalShortcutKey }, h.SentNames()); // once: no "scan stopped → Global"
        Assert.Equal(GlobalSwitched, h.Controller.StatusMessage); // not "Scan stopped."
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Equal("Ready — Press F8", h.Controller.DetectionText);
        Assert.False(h.Controller.HasPendingActivation);
        Assert.True(h.Controller.LastDetection?.WasStopped);
        Assert.Empty(detected);
        Assert.Equal(LifecyclePhase.Unknown, h.Lifecycle.Phase);

        // F8 afterwards: a fresh scan, as always.
        h.Ocr.Text = "FRACTURED COWSHED\nCOLDWIND FARM";
        await h.Controller.TriggerDetectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AppState.PresetActive, h.States.State);
        Assert.Equal(new[] { AppConfigKeys.GlobalShortcutKey, "Coldwind Farm" }, h.SentNames());
        Assert.Single(detected);
    }

    [Fact]
    public async Task F8_WhileTheScanTheGlobalHotkeyStoppedIsStillEnding_SaysSo_ThenWorksAgain()
    {
        ControllerHarness h = HarnessOnColdwind();
        h.Ocr.Text = "PRESS START";
        using var sampleSeen = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        h.Detector.SampleEvaluated += (_, _) =>
        {
            sampleSeen.Release();
            release.Wait(TimeSpan.FromSeconds(10)); // holds the scan inside a sample
        };

        // On a pool thread: the first sample may run synchronously inside the call.
        Task scan = Task.Run(() => h.Controller.TriggerDetectionAsync());
        Assert.True(await sampleSeen.WaitAsync(TimeSpan.FromSeconds(10)));
        h.GlobalPresetHotkey.Press(); // the scan is told to stop, but is still inside its sample

        Task early = h.Controller.TriggerDetectionAsync(); // F8 right away
        Assert.True(early.IsCompleted);
        Assert.Equal("The previous scan is still stopping — press F8 again in a moment.", h.Controller.StatusMessage);
        Assert.Equal(AppState.Ready, h.States.State);

        release.Set();
        await scan.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { AppConfigKeys.GlobalShortcutKey }, h.SentNames()); // the stopped scan requested nothing

        Task next = h.Controller.TriggerDetectionAsync(); // F8 again: a fresh scan
        Assert.Equal(AppState.DetectingLoadingScreen, h.States.State);
        await h.Controller.TriggerDetectionAsync(); // and F8 stops it
        await next.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AppState.Ready, h.States.State);
    }

    [Fact]
    public async Task GlobalHotkey_WhileTheDetectedPresetWaitsForFocus_ReplacesIt_AndStopsTrackingTheMatch()
    {
        ControllerHarness h = HarnessOnColdwind();
        h.UnfocusRightAfterTheRead();
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.WaitingForGameplay, h.States.State);
        Assert.Equal(LifecyclePhase.WatchingForTrial, h.Lifecycle.Phase);
        Assert.True(h.Controller.HasPendingActivation);

        h.GlobalPresetHotkey.Press(); // still unfocused

        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Equal(LifecyclePhase.Unknown, h.Lifecycle.Phase); // disarmed: no match-end restore follows
        Assert.Contains("F9 pressed. Waiting for DBD focus", h.Controller.StatusMessage);

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.True(h.Controller.RetryPendingActivation()); // nothing else pending
        Assert.Equal(new[] { AppConfigKeys.GlobalShortcutKey }, h.SentNames()); // the detected preset never went out
        Assert.Equal(GlobalSwitched, h.Controller.StatusMessage);
    }

    [Fact]
    public void GlobalHotkey_DuringAManualOverride_EndsIt()
    {
        ControllerHarness h = Harness();
        h.Controller.ApplyManualOverride("Coldwind Farm");
        Assert.Equal(AppState.ManualOverride, h.States.State);
        Assert.Equal("Coldwind Farm", h.Controller.ActiveRealm);

        h.GlobalPresetHotkey.Press();

        Assert.False(h.Controller.IsManualOverride);
        Assert.Null(h.Controller.OverrideRealm);
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
        Assert.Null(h.Controller.ActiveRealm);

        h.Controller.ReturnToAutomatic(); // already automatic
        Assert.Equal("Automatic mode is already active.", h.Controller.StatusMessage);
        Assert.Equal(2, h.Keys.CallCount);
    }

    [Fact]
    public async Task GlobalHotkey_DuringAMatch_Switches_AndTheMatchEndAddsNothing()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { VerifyDelayMs = 60_000 });
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(AppState.PresetActive, h.States.State);
        h.ReshadeSaves(h.ColdwindIni);
        h.DriveToTrial();

        h.GlobalPresetHotkey.Press();

        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Equal(LifecyclePhase.Unknown, h.Lifecycle.Phase);
        Assert.Equal(GlobalSwitched, h.Controller.StatusMessage);

        h.DriveToMenus(); // the match ends: no longer tracked, so no second Global restore
        Assert.Equal(2, h.Keys.CallCount);
        Assert.Equal(GlobalSwitched, h.Controller.StatusMessage);
        Assert.Equal(AppState.Ready, h.States.State);
    }

    [Fact]
    public void GlobalHotkey_RapidPressesWhileUnfocused_LeaveOneRequest_SentOnce()
    {
        ControllerHarness h = Harness();
        h.Focused = false;

        for (int i = 0; i < 5; i++)
        {
            h.GlobalPresetHotkey.Press();
        }

        Assert.Equal(0, h.Keys.CallCount);
        Assert.True(h.Controller.HasPendingActivation);
        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(1, h.Keys.CallCount);
        Assert.False(h.Controller.HasPendingActivation);
    }

    [Fact]
    public async Task GlobalHotkey_RapidConcurrentPresses_NeverInterleaveKeystrokes()
    {
        ControllerHarness h = Harness();
        h.Keys.HoldSpins = 20_000; // widen each send's down/hold/up window

        // Hotkey presses (queued, one at a time) mixed with direct requests (e.g. a
        // UI button, on their own threads): every send still runs alone.
        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            if (i % 2 == 0)
            {
                h.GlobalPresetHotkey.Press();
            }
            else
            {
                h.Controller.ActivateGlobalNow();
            }
        });
        await h.Controller.GlobalHotkeyPressesHandled.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, h.Keys.Overlaps);
        Assert.InRange(h.Keys.CallCount, 1, 32);
        Assert.All(h.SentNames(), name => Assert.Equal(AppConfigKeys.GlobalShortcutKey, name));
        Assert.False(h.Controller.HasPendingActivation);
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task F8_AfterAPendingGlobalHotkeySwitch_StartsAFreshScan_WhichSupersedesIt()
    {
        ControllerHarness h = Harness();
        h.Focused = false;
        h.GlobalPresetHotkey.Press();
        Assert.True(h.Controller.HasPendingActivation);

        Task scan = h.Controller.TriggerDetectionAsync(); // F8: a fresh detection supersedes any pending switch
        Assert.Equal(AppState.DetectingLoadingScreen, h.States.State);
        Assert.False(h.Controller.HasPendingActivation);

        h.Focused = true; // back in the game: the scan reads Coldwind
        await scan.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "Coldwind Farm" }, h.SentNames()); // Global never went out
        Assert.Equal(AppState.PresetActive, h.States.State);
    }

    [Fact]
    public void ActivateGlobalNow_WithTheHotkeyCleared_StillSwitches_WithoutAKeyInTheText()
    {
        ControllerHarness h = HarnessOnColdwind(new ControllerHarnessOptions { Configure = c => c.GlobalHotkey = null });

        h.Controller.ActivateGlobalNow(); // e.g. from a UI button

        Assert.Equal(new[] { AppConfigKeys.GlobalShortcutKey }, h.SentNames());
        Assert.Equal("Switched to the Global preset.", h.Controller.StatusMessage);
    }

    [Fact]
    public void ActivateGlobalNow_FromAButton_SaysTheGlobalPresetWasRequested_NotThatAKeyWasPressed()
    {
        ControllerHarness h = HarnessOnColdwind(); // F9 is set, but nobody pressed it
        h.Focused = false; // the app's own window is in front: the usual case for a button

        h.Controller.ActivateGlobalNow();

        string file = Path.GetFileName(h.GlobalIni);
        Assert.Equal($"Global preset requested. Waiting for DBD focus to switch to {file} — click into the game.",
            h.Controller.StatusMessage);
        Assert.Equal(AppState.Ready, h.States.State);

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal("Switched to the Global preset.", h.Controller.StatusMessage);
        Assert.Contains("Switch to Global →", h.Controller.LastActivationText);
        Assert.DoesNotContain("F9", h.Controller.LastActivationText);
        Assert.DoesNotContain("hotkey", h.Controller.LastActivationText, StringComparison.OrdinalIgnoreCase);

        // Not focused within a minute: the give-up text doesn't claim a key press either.
        h.Focused = false;
        h.Controller.ActivateGlobalNow();
        h.Advance(AppController.GlobalHotkeyFocusWindow);
        Assert.False(h.Controller.RetryPendingActivation());
        Assert.Equal($"Global preset requested. Gave up switching to {file}: Dead by Daylight wasn't focused within 60 s.",
            h.Controller.StatusMessage);

        // The hotkey itself still names its key.
        h.Focused = true;
        h.ReshadeSaves(h.ColdwindIni);
        h.GlobalPresetHotkey.Press();
        Assert.Equal(GlobalSwitched, h.Controller.StatusMessage);
        Assert.Contains("Global preset hotkey (F9) →", h.Controller.LastActivationText);
    }

    [Fact]
    public async Task GlobalHotkeyPress_ReturnsAtOnce_EvenWhileAnotherSendIsInProgress_ThenRunsAfterIt()
    {
        // The pump thread hands the press off: registering or releasing a hotkey
        // (which joins that thread) never waits for a switch in progress.
        ControllerHarness h = HarnessOnColdwind();
        using var sending = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        h.Keys.OnSend = s =>
        {
            if (h.Keys.CallCount == 1)
            {
                sending.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10))); // the first send hangs
            }
        };
        Task test = Task.Run(() => h.Controller.TestRealmPreset("Coldwind Farm"));
        Assert.True(sending.Wait(TimeSpan.FromSeconds(10)));

        Task press = Task.Run(() => h.GlobalPresetHotkey.PressWithoutWaiting()); // on the "pump thread"
        await press.WaitAsync(TimeSpan.FromSeconds(5)); // a TimeoutException: the pump thread waited for the switch
        Assert.False(h.Controller.GlobalHotkeyPressesHandled.IsCompleted); // queued behind the send in progress

        // Meanwhile the hotkeys can be released and registered again (a capture).
        h.Controller.SuspendHotkey();
        h.Controller.ResumeHotkey();
        Assert.True(h.GlobalPresetHotkey.Holds(F9));

        release.Set();
        await test.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Controller.GlobalHotkeyPressesHandled.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames()); // in order
        Assert.Equal(0, h.Keys.Overlaps);
        Assert.Equal(GlobalSwitched, h.Controller.StatusMessage);
        Assert.Equal(h.GlobalIni, h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task GlobalHotkeyPresses_RunOneAtATime_AndOnesWaitingToStartAreFoldedTogether()
    {
        ControllerHarness h = HarnessOnColdwind();
        using var sending = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        h.Keys.OnSend = _ =>
        {
            if (h.Keys.CallCount == 1)
            {
                sending.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };

        h.GlobalPresetHotkey.PressWithoutWaiting(); // runs, and hangs in its send
        Assert.True(sending.Wait(TimeSpan.FromSeconds(10)));
        for (int i = 0; i < 4; i++)
        {
            h.GlobalPresetHotkey.PressWithoutWaiting(); // one waits; the rest join it
        }

        release.Set();
        await h.Controller.GlobalHotkeyPressesHandled.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, h.Keys.CallCount);
        Assert.Equal(0, h.Keys.Overlaps);
        Assert.Equal(GlobalSwitched, h.Controller.StatusMessage);
        Assert.False(h.Controller.HasPendingActivation);
    }

    [Fact]
    public void GlobalHotkey_AfterShutdown_DoesNothing()
    {
        ControllerHarness h = Harness();
        h.Controller.Shutdown();
        h.Controller.ActivateGlobalNow();

        Assert.Equal(0, h.Keys.CallCount);
        Assert.False(h.Controller.HasPendingActivation);
    }

    // ---------- MapDetected ----------

    [Fact]
    public async Task MapDetected_IsRaisedOncePerConfidentDetection_OutsideTheLocks_BeforeTheSwitch()
    {
        ControllerHarness h = Harness();
        var detected = new ConcurrentQueue<MapDetectedEventArgs>();
        var sentBefore = new ConcurrentQueue<int>();
        var lockFree = new ConcurrentQueue<bool>();
        h.Controller.MapDetected += (_, e) =>
        {
            detected.Enqueue(e);
            sentBefore.Enqueue(h.Keys.CallCount);

            // Another thread can take the controller's lock: the event is not raised under it.
            lockFree.Enqueue(Task.Run(() => h.Controller.HasPendingActivation).Wait(TimeSpan.FromSeconds(5)));
        };

        await h.Controller.TriggerDetectionAsync();

        MapDetectedEventArgs e = Assert.Single(detected);
        Assert.Equal("Coldwind Farm", e.Realm);
        Assert.Equal("Fractured Cowshed", e.Map, ignoreCase: true);
        Assert.InRange(e.Confidence, 0.5, 1.0);
        Assert.Equal(new[] { 0 }, sentBefore); // raised before the preset was sent
        Assert.Equal(new[] { true }, lockFree);
        Assert.Equal(new[] { "Coldwind Farm" }, h.SentNames());

        await h.Controller.TriggerDetectionAsync(); // the next loading screen: once more
        Assert.Equal(2, detected.Count);
    }

    [Fact]
    public async Task MapDetected_IsRaised_EvenWithoutAnAssignedPreset()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { WithPresets = false });
        var detected = new ConcurrentQueue<MapDetectedEventArgs>();
        h.Controller.MapDetected += (_, e) => detected.Enqueue(e);

        await h.Controller.TriggerDetectionAsync();

        Assert.Equal("Coldwind Farm", Assert.Single(detected).Realm);
        Assert.Contains("no preset is assigned", h.Controller.StatusMessage);
        Assert.Equal(0, h.Keys.CallCount);
    }

    [Fact]
    public async Task MapDetected_IsNotRaised_OnStartup_StoppedScans_OrOtherTransitions()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { DbdRunningAtStart = false });
        var detected = new ConcurrentQueue<MapDetectedEventArgs>();
        h.Controller.MapDetected += (_, e) => detected.Enqueue(e);

        h.Dbd.IsRunning = true; // DBD starts
        h.Dbd.RaiseStatusChanged(running: true);
        h.Controller.ApplyManualOverride("Coldwind Farm");
        h.Controller.ReturnToAutomatic();
        h.Controller.TestRealmPreset("Coldwind Farm");
        h.GlobalPresetHotkey.Press();

        h.Ocr.Text = "PRESS START";
        Task scan = h.Controller.TriggerDetectionAsync();
        await ControllerHarness.WaitUntilAsync(() => h.Ocr.Calls >= 2, "the scan reads");
        await h.Controller.TriggerDetectionAsync(); // F8 again: stopped
        await scan.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Scan stopped.", h.Controller.StatusMessage);

        h.Dbd.IsRunning = false; // DBD exits
        h.Dbd.RaiseStatusChanged(running: false);

        Assert.Empty(detected);
    }

    [Fact]
    public async Task MapDetected_SubscriberThatThrows_DoesNotStopTheSwitch()
    {
        ControllerHarness h = Harness();
        h.Controller.MapDetected += (_, _) => throw new InvalidOperationException("UI bug");

        await h.Controller.TriggerDetectionAsync();

        Assert.Equal(new[] { "Coldwind Farm" }, h.SentNames());
        Assert.Equal(AppState.PresetActive, h.States.State);
    }

    // ---------- Animations, sound and volume ----------

    [Fact]
    public void NewSettings_StartWithTheirDefaults()
    {
        ControllerHarness h = Harness();

        Assert.True(h.Controller.AnimationsEnabled);
        Assert.True(h.Controller.SoundEnabled);
        Assert.Equal(0.5, h.Controller.SoundVolume);
        Assert.Same(h.Sounds, h.Controller.SoundEffects);
    }

    [Fact]
    public void NewSettings_AreSaved_AndSurviveAnAppRestart()
    {
        ControllerHarness first = Harness();
        int changes = 0;
        first.Controller.Changed += (_, _) => changes++;

        first.Controller.SetAnimationsEnabled(false);
        first.Controller.SetSoundEnabled(false);
        first.Controller.SetSoundVolume(0.3);

        Assert.Equal(3, changes);
        AppConfig onDisk = ConfigOnDisk(first);
        Assert.False(onDisk.AnimationsEnabled);
        Assert.False(onDisk.SoundEnabled);
        Assert.Equal(0.3, onDisk.SoundVolume);

        ControllerHarness h = Harness(new ControllerHarnessOptions { RestartOf = first });
        Assert.False(h.Controller.AnimationsEnabled);
        Assert.False(h.Controller.SoundEnabled);
        Assert.Equal(0.3, h.Controller.SoundVolume);

        h.Controller.SetAnimationsEnabled(true);
        h.Controller.SetSoundEnabled(true);
        Assert.True(ConfigOnDisk(h).AnimationsEnabled);
        Assert.True(ConfigOnDisk(h).SoundEnabled);
    }

    [Fact]
    public void SettingsSetters_WithoutAChange_NeitherSaveNorNotify()
    {
        ControllerHarness h = Harness();
        int changes = 0;
        h.Controller.Changed += (_, _) => changes++;
        DateTime saved = File.GetLastWriteTimeUtc(h.ConfigPath);
        File.SetLastWriteTimeUtc(h.ConfigPath, saved.AddMinutes(-5));
        DateTime marker = File.GetLastWriteTimeUtc(h.ConfigPath);

        h.Controller.SetAnimationsEnabled(true);
        h.Controller.SetSoundEnabled(true);
        h.Controller.SetSoundVolume(0.5);

        Assert.Equal(0, changes);
        Assert.Equal(marker, File.GetLastWriteTimeUtc(h.ConfigPath));
    }

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.7, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 0.0)]
    public void SetSoundVolume_ClampsTo0Through1(double requested, double expected)
    {
        ControllerHarness h = Harness();

        h.Controller.SetSoundVolume(requested);

        Assert.Equal(expected, h.Controller.SoundVolume);
        Assert.Equal(expected, ConfigOnDisk(h).SoundVolume);
    }

    [Fact]
    public void SetSoundVolume_NaN_IsIgnored()
    {
        ControllerHarness h = Harness();
        h.Controller.SetSoundVolume(0.7);

        h.Controller.SetSoundVolume(double.NaN);

        Assert.Equal(0.7, h.Controller.SoundVolume);
        Assert.Equal(0.7, ConfigOnDisk(h).SoundVolume);
    }

    [Fact]
    public void NewSettings_SaveFailures_AreShownInTheStatus_NotThrown()
    {
        ControllerHarness h = Harness();
        File.Delete(h.ConfigPath);
        Directory.CreateDirectory(h.ConfigPath); // the config can no longer be written

        h.Controller.SetAnimationsEnabled(false);
        Assert.StartsWith("Could not save settings", h.Controller.StatusMessage);

        h.Controller.SetSoundEnabled(false);
        Assert.StartsWith("Could not save settings", h.Controller.StatusMessage);

        h.Controller.SetSoundVolume(0.2);
        Assert.StartsWith("Could not save settings", h.Controller.StatusMessage);

        Assert.Null(h.Controller.UpdateGlobalHotkey(new DetectionHotkeyConfig { Key = "F10" })); // registered…
        Assert.Contains("Could not save settings", h.Controller.StatusMessage); // …but not saved: said so

        h.Controller.ClearGlobalHotkey();
        Assert.Contains("Could not save settings", h.Controller.StatusMessage);

        // In memory the values changed (they apply for this run).
        Assert.False(h.Controller.AnimationsEnabled);
        Assert.False(h.Controller.SoundEnabled);
        Assert.Equal(0.2, h.Controller.SoundVolume);
    }

    [Fact]
    public void PlayGreatSound_PlaysAtTheConfiguredVolume_OnlyWhileTheSoundIsOn()
    {
        ControllerHarness h = Harness();

        Assert.True(h.Controller.PlayGreatSound());
        h.Controller.SetSoundVolume(0.8);
        Assert.True(h.Controller.PlayGreatSound());
        Assert.Equal(new[] { 0.5, 0.8 }, h.Sounds.Played);

        h.Controller.SetSoundVolume(0);
        Assert.False(h.Controller.PlayGreatSound()); // silent: skipped
        h.Controller.SetSoundVolume(0.4);
        h.Controller.SetSoundEnabled(false);
        Assert.False(h.Controller.PlayGreatSound());
        Assert.Equal(2, h.Sounds.Played.Count);

        h.Controller.SetSoundEnabled(true);
        h.Sounds.Throw = new InvalidOperationException("no audio device");
        Assert.False(h.Controller.PlayGreatSound()); // never throws
        h.Sounds.Throw = null;
        h.Sounds.Result = false;
        Assert.False(h.Controller.PlayGreatSound()); // playback did not start
    }

    [Fact]
    public void TheReShadeCheck_SaysWhetherTheGreatSoundCanPlay()
    {
        ControllerHarness h = Harness();

        Assert.Null(h.Controller.GreatSoundProblem());
        Assert.Contains("Great sound: OK (volume 50 %)", h.Controller.RunReshadeDiagnostics());

        h.Controller.SetSoundVolume(0.125);
        Assert.Contains("Great sound: OK (volume 13 %)", h.Controller.RunReshadeDiagnostics());

        h.Controller.SetSoundEnabled(false);
        Assert.Contains("Great sound: OK (turned off under Settings)", h.Controller.RunReshadeDiagnostics());

        // E.g. the build lost the Resource item: silent, but the check says why.
        h.Sounds.Problem = "Could not load the Great sound: Cannot locate resource 'assets/sounds/great.wav'.";
        Assert.Equal(h.Sounds.Problem, h.Controller.GreatSoundProblem());
        Assert.Contains($"Great sound: unavailable — {h.Sounds.Problem}", h.Controller.RunReshadeDiagnostics());
    }

    [Fact]
    public async Task AControllerWithoutTheOptionalServices_RegistersNoGlobalHotkeyWithWindows_AndStaysSilent()
    {
        string configPath = Path.Combine(_dir, Guid.NewGuid() + "-config.json");
        var config = new AppConfigService(configPath);
        var db = new RealmDatabaseService();
        await db.LoadAsync(null);
        var dbd = new ControllerHarness.FakeDbdMonitor { IsRunning = false };
        using var hotkey = new ControllerHarness.FakeHotkeyService();
        var capture = new ControllerHarness.FakeCapture();
        using var detector = new DetectionOrchestrator(new ControllerHarness.FakeOcr(), new MapMatchingService(), db, capture);
        using var lifecycle = new MatchLifecycleService(capture);
        using var controller = new AppController(
            config, db, new RuntimeStateService(), dbd, hotkey, detector, lifecycle,
            new PresetActivationService(new ControllerHarness.FakeKeys(), _ => true),
            new PresetDecisionService(), new ReshadeConfigurationService(),
            discoverDbdExePath: static () => null,
            discoverReshadeIniPath: static _ => null);

        controller.Initialize();
        try
        {
            Assert.Equal("F9", controller.GlobalHotkey?.Key);
            Assert.Null(controller.GlobalHotkeyError);
            Assert.Same(NullSoundEffects.Instance, controller.SoundEffects);
            Assert.False(controller.PlayGreatSound());
            Assert.Equal(NullSoundEffects.NoSoundOutput, controller.GreatSoundProblem());
            Assert.Contains($"Great sound: unavailable — {NullSoundEffects.NoSoundOutput}", controller.RunReshadeDiagnostics());
            Assert.Null(controller.UpdateGlobalHotkey(new DetectionHotkeyConfig { Key = "F10" }));
            Assert.Equal(HotkeyParser.ModifierRequiredError, controller.UpdateGlobalHotkey(new DetectionHotkeyConfig { Key = "W" }));
            Assert.Equal("F10", controller.GlobalHotkey?.Key);
        }
        finally
        {
            controller.Shutdown();
        }
    }
}
