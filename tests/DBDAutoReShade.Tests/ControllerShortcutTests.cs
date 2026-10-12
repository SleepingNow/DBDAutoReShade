using DBDAutoReShade.App.Services;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// Per-name "restart DBD" tracking keyed by PID (C13), the transactional,
/// stable shortcut sync (C15), settings (C16) and the controller entry points
/// that keep config changes under its lock (C17).
/// </summary>
public sealed partial class ControllerActivationTests
{
    private const string Autohaven = "Autohaven Wreckers";

    private static string NewPresetFile(ControllerHarness h, string name)
    {
        string path = Path.Combine(h.Dir, Guid.NewGuid().ToString("N")[..8] + "-" + name + ".ini");
        File.WriteAllText(path, "[General]\nTechniques=X\nTechniqueSorting=X\n");
        return path;
    }

    /// <summary>
    /// A test hotkey Ctrl+Alt+<paramref name="key"/>: never one of the app's ReShade shortcut combos
    /// (Ctrl+Alt+Shift + F13–F24/0–9), which the controller refuses as a hotkey.
    /// </summary>
    private static DetectionHotkeyConfig Combo(string key) => new() { Key = key, Ctrl = true, Alt = true };

    private static ReshadeShortcutConfig PoolCombo(int virtualKey) =>
        new() { VirtualKey = virtualKey, Ctrl = true, Alt = true, Shift = true };

    /// <summary>The user's own ReShade shortcut (an entry this app never wrote), appended to ReShade.ini.</summary>
    private static void AddForeignShortcut(ControllerHarness h, int virtualKey, string presetPath)
    {
        var doc = IniDocument.Parse(File.ReadAllText(h.ReshadeIni));
        string keys = doc.GetValue("GENERAL", "PresetShortcutKeys") ?? string.Empty;
        string paths = doc.GetValue("GENERAL", "PresetShortcutPaths") ?? string.Empty;
        doc.SetValue("GENERAL", "PresetShortcutKeys", (keys.Length > 0 ? keys + "," : string.Empty) + $"{virtualKey},1,1,1");
        doc.SetValue("GENERAL", "PresetShortcutPaths", (paths.Length > 0 ? paths + "," : string.Empty) + presetPath);
        File.WriteAllText(h.ReshadeIni, doc.ToString());
    }

    private static AppConfig ConfigOnDisk(ControllerHarness h)
    {
        var svc = new AppConfigService(h.ConfigPath);
        svc.Load();
        return svc.Config;
    }

    private static Dictionary<string, int> CombosByName(ControllerHarness h) =>
        h.Controller.Config.ReshadeShortcuts.ToDictionary(kv => kv.Key, kv => kv.Value.VirtualKey, StringComparer.OrdinalIgnoreCase);

    // ---------- C13: per-name restart tracking keyed by PID ----------

    [Fact]
    public void AssigningWhileDbdRuns_BlocksOnlyThatName()
    {
        ControllerHarness h = Harness();
        Assert.Empty(h.Controller.NamesNeedingDbdRestart);

        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));
        Assert.Equal(new[] { Autohaven }, h.Controller.NamesNeedingDbdRestart);
        Assert.True(h.Controller.NeedsDbdRestartForShortcuts);
        Assert.Contains("Restart Dead by Daylight so ReShade loads the shortcut for Autohaven Wreckers", h.Controller.StatusMessage);

        // The running ReShade never loaded that combo: nothing is sent.
        Assert.Equal(
            "Restart Dead by Daylight so ReShade loads the shortcut for Autohaven Wreckers.",
            h.Controller.TestRealmPreset(Autohaven));
        Assert.Equal(0, h.Keys.CallCount);
        Assert.False(h.Controller.HasPendingActivation);
        Assert.Contains("FAILED", h.Controller.LastActivationText);

        // Everything ReShade did load still works.
        Assert.Null(h.Controller.TestRealmPreset("Coldwind Farm"));
        Assert.Null(h.Controller.TestGlobalPreset());
        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey }, h.SentNames());
    }

    [Fact]
    public void RestartRequired_IsReportedAtOnce_NotAfterWaitingForFocus()
    {
        ControllerHarness h = Harness();
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));
        h.Focused = false; // the Test button focused this app

        Assert.NotNull(h.Controller.TestRealmPreset(Autohaven)); // final, not "click into DBD within 15 s"
        Assert.False(h.Controller.HasPendingActivation);
        Assert.StartsWith("Test failed: Restart Dead by Daylight", h.Controller.StatusMessage);
    }

    [Fact]
    public void ReusedShortcutSlot_IsNeverSentToTheRunningReshade()
    {
        // Root cause of "sends a key ReShade maps to a different preset": a freed
        // combo handed to another preset while DBD still runs with the old mapping.
        ControllerHarness h = Harness();
        int coldwindCombo = CombosByName(h)["Coldwind Farm"];

        Assert.Null(h.Controller.SetRealmPreset("Coldwind Farm", null));
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));
        Assert.Equal(coldwindCombo, CombosByName(h)[Autohaven]); // the freed slot is reused…

        h.Controller.ApplyManualOverride(Autohaven); // …but the running ReShade maps it to Coldwind
        Assert.Equal(0, h.Keys.CallCount);
        Assert.False(h.Controller.HasPendingActivation);
        Assert.Contains("Restart Dead by Daylight so ReShade loads the shortcut for Autohaven Wreckers", h.Controller.StatusMessage);
        Assert.Null(h.Controller.ActivePresetPath);
    }

    [Fact]
    public async Task DetectedRealm_WhosePresetFileChanged_IsNotSentUntilRestart()
    {
        ControllerHarness h = Harness();
        int combo = CombosByName(h)["Coldwind Farm"];
        Assert.Null(h.Controller.SetRealmPreset("Coldwind Farm", NewPresetFile(h, "Coldwind-v2")));
        Assert.Equal(combo, CombosByName(h)["Coldwind Farm"]); // same combo, new file: ReShade still has the old file

        await h.Controller.TriggerDetectionAsync();

        Assert.Equal(0, h.Keys.CallCount);
        Assert.Equal(AppState.Ready, h.States.State); // final failure: not left waiting for gameplay
        Assert.False(h.Controller.HasPendingActivation);
        Assert.Contains("Restart Dead by Daylight so ReShade loads the shortcut for Coldwind Farm", h.Controller.StatusMessage);
    }

    [Fact]
    public void WindowHandleChange_SamePid_KeepsTheRestartBlock()
    {
        ControllerHarness h = Harness();
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));

        h.Dbd.MainWindowHandle = new IntPtr(0x7777); // same process, new window
        h.Dbd.RaiseStatusChanged(running: true);

        Assert.Equal(new[] { Autohaven }, h.Controller.NamesNeedingDbdRestart);
        Assert.NotNull(h.Controller.TestRealmPreset(Autohaven));
        Assert.Equal(0, h.Keys.CallCount);
    }

    [Fact]
    public void NewDbdProcess_LoadsTheSyncedShortcuts_AndClearsTheBlock()
    {
        ControllerHarness h = Harness();
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));

        h.Dbd.ProcessId = 9001; // DBD restarted between two monitor polls
        h.Dbd.RaiseStatusChanged(running: true);

        Assert.Empty(h.Controller.NamesNeedingDbdRestart);
        Assert.Null(h.Controller.TestRealmPreset(Autohaven));
        Assert.Equal(new[] { Autohaven }, h.SentNames());
    }

    [Fact]
    public void DbdExit_ClearsTheSnapshot_TheNextLaunchLoadsEverything()
    {
        ControllerHarness h = Harness();
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));

        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);
        Assert.Empty(h.Controller.NamesNeedingDbdRestart); // nothing runs that could be stale

        h.Dbd.IsRunning = true;
        h.Dbd.ProcessId = 5150;
        h.Dbd.RaiseStatusChanged(running: true);
        Assert.Empty(h.Controller.NamesNeedingDbdRestart);
        Assert.Null(h.Controller.TestRealmPreset(Autohaven));
        Assert.Equal(1, h.Keys.CallCount);
    }

    [Fact]
    public void DbdAlreadyRunningAtStartup_IsSnapshottedBeforeTheStartupSync()
    {
        // ReShade.ini had no shortcuts when DBD started; the app's startup sync
        // writes them, but the running ReShade can't have loaded them.
        ControllerHarness h = Harness(new ControllerHarnessOptions { ShortcutsLoadedByReshade = false });

        Assert.Equal(2, ReshadeConfigurationService.ReadShortcutAssignments(h.ReshadeIni).Count); // written…
        Assert.Equal(new[] { "Global/Menu", "Coldwind Farm" }, h.Controller.NamesNeedingDbdRestart); // …not loaded

        Assert.NotNull(h.Controller.TestGlobalPreset());
        Assert.Equal(0, h.Keys.CallCount);
    }

    [Fact]
    public void DbdSeenByTheMonitorsFirstPoll_CountsAsAlreadyRunning()
    {
        // The real monitor's first poll runs inside Start() and raises its
        // status right there: that DBD was running before the app, so it is
        // snapshotted before the startup sync, not treated as a new launch.
        ControllerHarness h = Harness(new ControllerHarnessOptions
        {
            ShortcutsLoadedByReshade = false,
            DbdSeenByFirstPoll = true
        });

        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Equal(new[] { "Global/Menu", "Coldwind Farm" }, h.Controller.NamesNeedingDbdRestart);
        Assert.NotNull(h.Controller.TestRealmPreset("Coldwind Farm"));
        Assert.Equal(0, h.Keys.CallCount);
    }

    [Fact]
    public void Diagnostics_ListThePresetsTheRunningReshadeHasNotLoaded()
    {
        ControllerHarness h = Harness();
        Assert.Contains("Shortcuts loaded by the running ReShade: all assigned presets", h.Controller.RunReshadeDiagnostics());

        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));

        Assert.Contains("Shortcuts NOT loaded by the running ReShade: Autohaven Wreckers", h.Controller.RunReshadeDiagnostics());
    }

    [Fact]
    public void DbdLaunchedAfterTheApp_IsSnapshottedAfterTheSync()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions
        {
            ShortcutsLoadedByReshade = false,
            DbdRunningAtStart = false
        });
        Assert.Equal(AppState.WaitingForDbd, h.States.State);
        Assert.Empty(h.Controller.NamesNeedingDbdRestart);

        h.Dbd.IsRunning = true; // ReShade reads ReShade.ini (already synced) while the game starts
        h.Dbd.RaiseStatusChanged(running: true);

        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Empty(h.Controller.NamesNeedingDbdRestart);
        Assert.Null(h.Controller.TestRealmPreset("Coldwind Farm"));
        Assert.Equal(new[] { "Coldwind Farm" }, h.SentNames());
    }

    [Fact]
    public void ChangingTheReshadeIniWhileDbdRuns_ConfirmsNothingUntilRestart()
    {
        ControllerHarness h = Harness();
        string otherIni = Path.Combine(h.Dir, Guid.NewGuid() + "-Other-ReShade.ini");
        File.WriteAllText(otherIni, "[GENERAL]\n");

        h.Controller.SetReshadeIni(otherIni); // which file did the running ReShade read? Unknown.

        Assert.Equal(new[] { "Global/Menu", "Coldwind Farm" }, h.Controller.NamesNeedingDbdRestart);
        Assert.NotNull(h.Controller.TestGlobalPreset());
        Assert.Equal(0, h.Keys.CallCount);
    }

    // ---------- C13: what the running ReShade loaded survives an app restart ----------

    private static string StoredSnapshotPath(ControllerHarness h) => Path.Combine(
        Path.GetDirectoryName(h.ConfigPath)!, Path.GetFileNameWithoutExtension(h.ConfigPath) + ".reshade-loaded.json");

    [Fact]
    public void AppRestartedWhileDbdKeepsRunning_StillBlocksTheReusedCombo()
    {
        // Users told to "restart" often restart this app, not DBD. ReShade.ini
        // already maps the freed combo to the new preset, but the running
        // ReShade still maps it to the old one.
        ControllerHarness h = Harness();
        int coldwindCombo = CombosByName(h)["Coldwind Farm"];
        Assert.Null(h.Controller.SetRealmPreset("Coldwind Farm", null));
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));
        Assert.Equal(coldwindCombo, CombosByName(h)[Autohaven]);
        Assert.Equal(new[] { Autohaven }, h.Controller.NamesNeedingDbdRestart);

        ControllerHarness again = Harness(new ControllerHarnessOptions { RestartOf = h }); // same DBD process

        Assert.Equal(new[] { Autohaven }, again.Controller.NamesNeedingDbdRestart);
        again.Controller.ApplyManualOverride(Autohaven);
        Assert.Equal(0, again.Keys.CallCount); // Ctrl+Alt+Shift+<Coldwind's old combo> is never sent
        Assert.False(again.Controller.HasPendingActivation);
        Assert.Contains("Restart Dead by Daylight so ReShade loads the shortcut for Autohaven Wreckers", again.Controller.StatusMessage);
        Assert.Null(again.Controller.ActivePresetPath);

        // What that ReShade did load still works…
        Assert.Null(again.Controller.TestGlobalPreset());
        Assert.Equal(new[] { AppConfigKeys.GlobalShortcutKey }, again.SentNames());

        // …and restarting DBD itself loads the rest.
        again.Dbd.ProcessId = 9001;
        again.Dbd.RaiseStatusChanged(running: true);
        Assert.Empty(again.Controller.NamesNeedingDbdRestart);
        Assert.Null(again.Controller.TestRealmPreset(Autohaven));
    }

    [Fact]
    public void AppRestartedAfterDbdRestartedToo_JudgesTheNewProcessByReshadeIni()
    {
        ControllerHarness h = Harness();
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));
        Assert.Equal(new[] { Autohaven }, h.Controller.NamesNeedingDbdRestart);

        // While the app was closed DBD restarted: the new process loaded the synced ReShade.ini.
        ControllerHarness again = Harness(new ControllerHarnessOptions
        {
            RestartOf = h,
            DbdMeanwhile = dbd => dbd.ProcessId = 9001
        });

        Assert.Empty(again.Controller.NamesNeedingDbdRestart);
        Assert.Null(again.Controller.TestRealmPreset(Autohaven));
        Assert.Equal(new[] { Autohaven }, again.SentNames());
    }

    [Fact]
    public void AReusedPid_IsNotMistakenForTheProcessTheStoredSnapshotDescribes()
    {
        ControllerHarness h = Harness();
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));

        // DBD restarted while the app was closed and got the same PID again (another creation time).
        ControllerHarness again = Harness(new ControllerHarnessOptions
        {
            RestartOf = h,
            ProcessStartTime = _ => new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc)
        });

        Assert.Empty(again.Controller.NamesNeedingDbdRestart);
        Assert.Null(again.Controller.TestRealmPreset(Autohaven));
    }

    [Fact]
    public void StoredSnapshot_LivesAsLongAsTheDbdProcessItDescribes()
    {
        ControllerHarness h = Harness();
        string stored = StoredSnapshotPath(h);
        Assert.True(File.Exists(stored));

        h.Dbd.IsRunning = false;
        h.Dbd.RaiseStatusChanged(running: false);
        Assert.False(File.Exists(stored));

        h.Dbd.IsRunning = true;
        h.Dbd.ProcessId = 5150;
        h.Dbd.RaiseStatusChanged(running: true);
        Assert.True(File.Exists(stored));
    }

    [Fact]
    public void AppStartedWithoutDbd_ForgetsTheStoredSnapshot_OfTheExitedProcess()
    {
        ControllerHarness h = Harness();
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));

        // The app closes, DBD exits while it is closed, and the app starts without DBD…
        ControllerHarness second = Harness(new ControllerHarnessOptions
        {
            RestartOf = h,
            DbdMeanwhile = dbd => dbd.IsRunning = false
        });
        Assert.False(File.Exists(StoredSnapshotPath(second)));

        // …then closes, and DBD starts again (here even with the same PID and
        // creation time as before): that process loaded the synced ReShade.ini.
        ControllerHarness third = Harness(new ControllerHarnessOptions
        {
            RestartOf = second,
            DbdMeanwhile = dbd => dbd.IsRunning = true
        });
        Assert.Empty(third.Controller.NamesNeedingDbdRestart);
        Assert.Null(third.Controller.TestRealmPreset(Autohaven));
    }

    // ---------- C13/C25: without a ReShade.ini a DBD restart alone can't help ----------

    [Fact]
    public void MissingReshadeIni_WhileDbdRuns_SaysToSetIt_NotThatARestartWillHelp()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ReShade.ini");
        var (h, vm, _) = ViewModel(new ControllerHarnessOptions
        {
            Configure = c =>
            {
                c.ReshadeIniPath = missing;
                c.DetectionHotkey = Combo("F20");
            }
        });
        string[] all = { "Global/Menu", "Coldwind Farm" };

        Assert.Empty(h.Controller.NamesNeedingDbdRestart);
        Assert.False(h.Controller.NeedsDbdRestartForShortcuts);
        Assert.Equal(all, h.Controller.NamesWaitingForReshadeIni);
        Assert.True(vm.ShowRestartNotice);
        Assert.StartsWith($"ReShade.ini was not found at '{missing}'", vm.RestartNoticeText);
        Assert.Contains("Set the right ReShade.ini under Settings", vm.RestartNoticeText);
        Assert.DoesNotContain("Restart Dead by Daylight", vm.RestartNoticeText);
        AssertPresetsAskForTheReshadeIni(vm, MainViewModel.ReshadeIniNotFoundFlag); // the Presets tab says the same

        string? error = h.Controller.TestGlobalPreset();
        Assert.StartsWith($"ReShade.ini was not found at '{missing}' — set the right ReShade.ini under Settings", error);
        Assert.Equal(0, h.Keys.CallCount);
        Assert.Contains(
            "Shortcuts NOT loaded by the running ReShade: Global/Menu, Coldwind Farm — ReShade.ini was not found",
            h.Controller.RunReshadeDiagnostics());

        // A DBD restart alone changes nothing (and no text claimed it would).
        h.Dbd.ProcessId = 9001;
        h.Dbd.RaiseStatusChanged(running: true);
        Assert.Equal(all, h.Controller.NamesWaitingForReshadeIni);
        Assert.StartsWith("ReShade.ini was not found", vm.RestartNoticeText);
        AssertPresetsAskForTheReshadeIni(vm, MainViewModel.ReshadeIniNotFoundFlag);

        // Setting the right ReShade.ini writes the shortcuts: now a DBD restart is what's left…
        h.Controller.SetReshadeIni(h.ReshadeIni);
        Assert.Empty(h.Controller.NamesWaitingForReshadeIni);
        Assert.Equal(all, h.Controller.NamesNeedingDbdRestart);
        Assert.StartsWith("Restart Dead by Daylight so ReShade loads the new preset shortcuts", vm.RestartNoticeText);
        Assert.Equal("Restart needed", vm.PresetsTabStatus); // …and the Presets tab says so
        foreach (PresetRowBase row in BlockedRows(vm))
        {
            Assert.True(row.NeedsRestart, row.DisplayName);
            Assert.False(row.NeedsReshadeIni, row.DisplayName);
            Assert.Contains("restart needed", row.AccessibleName);
            vm.SelectRow(row);
            Assert.True(vm.DetailNeedsRestart);
            Assert.Equal(MainViewModel.RestartFlag, vm.DetailFlagText);
            Assert.Equal("assigned, not loaded yet", vm.DetailShortcutValue);
        }

        // …and it works.
        h.Dbd.ProcessId = 9002;
        h.Dbd.RaiseStatusChanged(running: true);
        Assert.False(vm.ShowRestartNotice);
        Assert.Equal(string.Empty, vm.PresetsTabStatus);
        Assert.Equal(string.Empty, vm.DetailFlagText);
        Assert.All(BlockedRows(vm), row => Assert.False(row.IsBlocked, row.DisplayName));
        Assert.Null(h.Controller.TestGlobalPreset());
        Assert.Equal(1, h.Keys.CallCount);
    }

    [Fact]
    public void UnsetReshadeIni_WhileDbdRuns_SaysItIsNotSet()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.ReshadeIniPath = null });

        Assert.Empty(h.Controller.NamesNeedingDbdRestart);
        Assert.StartsWith("ReShade.ini is not set, so ReShade can't load the preset shortcuts", h.Controller.BlockedShortcutsNotice);
        Assert.StartsWith("ReShade.ini is not set — set the right ReShade.ini under Settings", h.Controller.TestRealmPreset("Coldwind Farm"));
        Assert.Equal(0, h.Keys.CallCount);
    }

    [Fact]
    public void UnsetReshadeIni_WhileDbdRuns_ThePresetsTabSaysToSetIt_NotThatARestartWillHelp()
    {
        var (h, vm, _) = ViewModel(new ControllerHarnessOptions
        {
            Configure = c =>
            {
                c.ReshadeIniPath = null;
                c.DetectionHotkey = Combo("F20");
            }
        });

        Assert.Empty(h.Controller.NamesNeedingDbdRestart);
        Assert.Equal(new[] { "Global/Menu", "Coldwind Farm" }, h.Controller.NamesWaitingForReshadeIni);
        AssertPresetsAskForTheReshadeIni(vm, MainViewModel.ReshadeIniNotSetFlag);
    }

    /// <summary>The rows of the presets the harness assigns (Global / Menu and Coldwind Farm).</summary>
    private static PresetRowBase[] BlockedRows(MainViewModel vm) =>
        new PresetRowBase[] { vm.GlobalRow, vm.RealmRows.Single(r => r.RealmName == "Coldwind Farm") };

    /// <summary>
    /// ReShade.ini is not set or was not found while DBD runs: the controller says a DBD restart alone can't help, and
    /// so must the Presets tab. Its status, the blocked rows (Global / Menu and Coldwind Farm: red, "ReShade.ini needed")
    /// and the selected row's flag (<paramref name="flag"/>), also after the banner's "Show in Presets", ask for the
    /// right ReShade.ini; no text there says a restart is needed.
    /// </summary>
    private static void AssertPresetsAskForTheReshadeIni(MainViewModel vm, string flag)
    {
        Assert.Equal("ReShade.ini needed", vm.PresetsTabStatus);
        foreach (PresetRowBase row in BlockedRows(vm))
        {
            Assert.True(row.NeedsReshadeIni, row.DisplayName);
            Assert.False(row.NeedsRestart, row.DisplayName);
            Assert.Equal(RowMark.Red, row.Mark);
            Assert.Contains("ReShade.ini needed", row.AccessibleName);
            AssertNoRestart(row.AccessibleName);
        }

        void AssertDetail()
        {
            Assert.False(vm.DetailNeedsRestart);
            Assert.Equal(flag, vm.DetailFlagText);
            AssertNoRestart(vm.DetailFlagText);
            AssertNoRestart(vm.DetailShortcutValue);
            AssertNoRestart(vm.DetailPresetText);
        }

        vm.ShowRestartInPresets(); // the Status banner's action
        Assert.True(vm.SelectedRow?.IsBlocked);
        AssertDetail();
        foreach (PresetRowBase row in BlockedRows(vm))
        {
            vm.SelectRow(row);
            AssertDetail();
        }

        AssertNoRestart(vm.PresetsTabStatus);
        static void AssertNoRestart(string text) => Assert.DoesNotContain("restart", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- C15: stable, transactional shortcut sync ----------

    [Fact]
    public void AssigningARealm_KeepsEveryOtherCombo()
    {
        ControllerHarness h = Harness();
        Dictionary<string, int> before = CombosByName(h);

        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven"))); // sorts before the others

        Dictionary<string, int> after = CombosByName(h);
        Assert.Equal(before["Coldwind Farm"], after["Coldwind Farm"]);
        Assert.Equal(before[AppConfigKeys.GlobalShortcutKey], after[AppConfigKeys.GlobalShortcutKey]);
        Assert.DoesNotContain(after[Autohaven], before.Values);

        // ReShade.ini still maps the old combos to the old presets (what the running ReShade has).
        var entries = ReshadeConfigurationService.ReadShortcutAssignments(h.ReshadeIni);
        Assert.Contains(entries, e => e.Shortcut.VirtualKey == before["Coldwind Farm"] &&
                                      string.Equals(e.PresetPath, h.ColdwindIni, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Shortcut.VirtualKey == before[AppConfigKeys.GlobalShortcutKey] &&
                                      string.Equals(e.PresetPath, h.GlobalIni, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheUsersOwnShortcutCombos_AreNeverHandedOut()
    {
        ControllerHarness h = Harness();
        string mine = Path.Combine(h.Dir, "my-own-preset.ini");
        int nextFree = CombosByName(h).Values.Max() + 1; // the slot a new preset would get
        AddForeignShortcut(h, nextFree, mine);

        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));

        Assert.DoesNotContain(h.Controller.Config.ReshadeShortcuts.Values, s => s.VirtualKey == nextFree);
        Assert.Equal(nextFree + 1, CombosByName(h)[Autohaven]);
        var entries = ReshadeConfigurationService.ReadShortcutAssignments(h.ReshadeIni);
        Assert.Contains(entries, e => e.Shortcut.VirtualKey == nextFree &&
                                      string.Equals(e.PresetPath, mine, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(entries.Count, entries.Select(e => e.Shortcut.VirtualKey).Distinct().Count()); // no combo twice
    }

    [Fact]
    public void ExhaustedShortcutPool_KeepsTheConfig_AndShowsTheRealError()
    {
        ControllerHarness h = Harness();
        int free = ReshadeConfigurationService.ShortcutPoolSize - 2; // Global and Coldwind hold two
        for (int i = 0; i < free; i++)
        {
            Assert.Null(h.Controller.SetRealmPreset($"Realm {i:00}", NewPresetFile(h, $"Realm{i:00}")));
        }

        Dictionary<string, int> before = CombosByName(h);
        string iniBefore = File.ReadAllText(h.ReshadeIni);

        string? error = h.Controller.SetRealmPreset("One Too Many", NewPresetFile(h, "Overflow"));

        Assert.NotNull(error);
        Assert.Contains("Too many presets", error);
        Assert.False(h.Controller.Config.RealmPresets.ContainsKey("One Too Many"));
        Assert.Equal(before, CombosByName(h));
        Assert.Equal(iniBefore, File.ReadAllText(h.ReshadeIni));
        Assert.Contains("Too many presets", h.Controller.StatusMessage);
        Assert.DoesNotContain("Assigned", h.Controller.StatusMessage);
        Assert.False(ConfigOnDisk(h).RealmPresets.ContainsKey("One Too Many"));
    }

    [Fact]
    public void UnwritableReshadeIni_RollsTheAssignmentBack()
    {
        ControllerHarness h = Harness();
        Dictionary<string, int> before = CombosByName(h);
        string autohaven = NewPresetFile(h, "Autohaven");
        File.SetAttributes(h.ReshadeIni, FileAttributes.ReadOnly);
        try
        {
            string? error = h.Controller.SetRealmPreset(Autohaven, autohaven);

            Assert.NotNull(error);
            Assert.Contains("ReShade.ini", error);
            Assert.False(h.Controller.Config.RealmPresets.ContainsKey(Autohaven));
            Assert.Equal(before, CombosByName(h));
            Assert.Equal(error, h.Controller.StatusMessage);
        }
        finally
        {
            File.SetAttributes(h.ReshadeIni, FileAttributes.Normal);
        }

        Assert.Null(h.Controller.SetRealmPreset(Autohaven, autohaven)); // works once the file is writable
        Assert.True(h.Controller.Config.ReshadeShortcuts.ContainsKey(Autohaven));
    }

    [Fact]
    public void MissingReshadeIni_StillAllocatesStableCombos()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions
        {
            Configure = c =>
            {
                c.ReshadeIniPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ReShade.ini");
                c.DetectionHotkey = Combo("F18"); // a free combo: no hotkey error is added to the startup status
            }
        });
        Assert.Contains("ReShade.ini not found — shortcuts will be written when it is.", h.Controller.StatusMessage);
        Assert.Equal(2, h.Controller.Config.ReshadeShortcuts.Count);
        Dictionary<string, int> before = CombosByName(h);

        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));

        Assert.StartsWith("Assigned", h.Controller.StatusMessage);
        Assert.Contains("ReShade.ini not found — shortcuts will be written when it is.", h.Controller.StatusMessage);
        Dictionary<string, int> after = CombosByName(h);
        Assert.Equal(3, after.Count);
        Assert.Equal(before["Coldwind Farm"], after["Coldwind Farm"]);
        Assert.Equal(before[AppConfigKeys.GlobalShortcutKey], after[AppConfigKeys.GlobalShortcutKey]);
    }

    [Fact]
    public void TheGlobalPresetFile_CannotAlsoBeARealmPreset()
    {
        ControllerHarness h = Harness();

        Assert.Equal("This preset is already assigned to Global/Menu.", h.Controller.SetRealmPreset(Autohaven, h.GlobalIni));
        Assert.Equal("This preset is already assigned to Coldwind Farm.", h.Controller.SetGlobalPreset(h.ColdwindIni));
        Assert.False(h.Controller.Config.RealmPresets.ContainsKey(Autohaven));
        Assert.Equal(h.GlobalIni, h.Controller.Config.GlobalPresetPath);
    }

    [Fact]
    public void PresetPathWithAComma_IsRejected()
    {
        ControllerHarness h = Harness();
        string folder = Path.Combine(h.Dir, "a,b");
        Directory.CreateDirectory(folder);
        string preset = Path.Combine(folder, "Autohaven.ini");
        File.WriteAllText(preset, "[General]\nTechniques=X\nTechniqueSorting=X\n");

        string? error = h.Controller.SetRealmPreset(Autohaven, preset);

        Assert.NotNull(error);
        Assert.Contains("','", error);
        Assert.False(h.Controller.Config.RealmPresets.ContainsKey(Autohaven));
    }

    // ---------- C16: settings ----------

    [Fact]
    public void UpdateHotkey_WhenRegistrationFails_KeepsAndReRegistersThePreviousOne()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.DetectionHotkey = Combo("F20") });
        Assert.Equal("F20", h.Hotkey.RegisteredHotkey?.Key);
        h.Hotkey.HoldElsewhere(Combo("F21")); // another program holds Ctrl+Alt+F21

        string? error = h.Controller.UpdateHotkey(Combo("F21"));

        Assert.NotNull(error);
        Assert.Contains("already in use", error);
        Assert.Equal("F20", h.Controller.Config.DetectionHotkey.Key);
        Assert.Equal("F20", ConfigOnDisk(h).DetectionHotkey.Key); // never saved
        Assert.Equal("F20", h.Hotkey.RegisteredHotkey?.Key); // and still working
        Assert.Null(h.Controller.HotkeyError);
        Assert.Contains("still the detection hotkey", h.Controller.StatusMessage);
    }

    [Fact]
    public void UpdateHotkey_RegistersFirst_ThenSaves()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.DetectionHotkey = Combo("F20") });

        Assert.Null(h.Controller.UpdateHotkey(Combo("F19")));

        Assert.Equal("F19", h.Hotkey.RegisteredHotkey?.Key);
        Assert.Equal("F19", h.Controller.Config.DetectionHotkey.Key);
        Assert.Equal("F19", ConfigOnDisk(h).DetectionHotkey.Key);
        Assert.Contains("Ctrl+Alt+F19", h.Controller.DetectionText);
        Assert.False(h.Hotkey.Holds(Combo("F20"))); // the old one was released
    }

    [Fact]
    public void UpdateHotkey_BareLetter_IsRejected_TheOldOneStays()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.DetectionHotkey = Combo("F20") });

        Assert.Equal(HotkeyParser.ModifierRequiredError, h.Controller.UpdateHotkey(new DetectionHotkeyConfig { Key = "W" }));

        Assert.Equal("F20", h.Controller.Config.DetectionHotkey.Key);
        Assert.Equal("F20", ConfigOnDisk(h).DetectionHotkey.Key);
        Assert.Equal("F20", h.Hotkey.RegisteredHotkey?.Key);
    }

    [Fact]
    public void ManifestUrl_OnlyHttpsIsSaved()
    {
        ControllerHarness h = Harness();

        string? error = h.Controller.SetManifestUrl("http://example.com/manifest.json");
        Assert.NotNull(error);
        Assert.Null(h.Controller.Config.DatabaseUpdateManifestUrl);
        Assert.Null(ConfigOnDisk(h).DatabaseUpdateManifestUrl);
        Assert.Equal(error, h.Controller.StatusMessage);

        Assert.Null(h.Controller.SetManifestUrl(" https://example.com/manifest.json "));
        Assert.Equal("https://example.com/manifest.json", ConfigOnDisk(h).DatabaseUpdateManifestUrl);

        Assert.Null(h.Controller.SetManifestUrl("  ")); // blank disables updates
        Assert.Null(ConfigOnDisk(h).DatabaseUpdateManifestUrl);
    }

    [Fact]
    public void ConfigSaveFailures_AreShownInTheStatus_NotThrown()
    {
        ControllerHarness h = Harness();
        File.Delete(h.ConfigPath);
        Directory.CreateDirectory(h.ConfigPath); // the config can no longer be written

        h.Controller.SetDebug(true);
        Assert.StartsWith("Could not save settings", h.Controller.StatusMessage);

        h.Controller.SetAutoRestore(false);
        Assert.StartsWith("Could not save settings", h.Controller.StatusMessage);

        Assert.Null(h.Controller.SetManifestUrl("https://example.com/manifest.json"));
        Assert.StartsWith("Could not save settings", h.Controller.StatusMessage);

        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));
        Assert.Contains("Could not save settings", h.Controller.StatusMessage);

        h.Controller.ApplyDatabaseVersion(4242);
        Assert.StartsWith("Could not save settings", h.Controller.StatusMessage);
    }

    [Fact]
    public async Task ResetSettings_RemovesTheAppsShortcuts_AndResetsEverything()
    {
        ControllerHarness? created = null;
        ControllerHarness h = created = Harness(new ControllerHarnessOptions
        {
            Configure = c => c.DetectionHotkey = Combo("F20"),
            DiscoverReshadeIni = _ => created?.ReshadeIni // found again after the reset
        });
        string mine = Path.Combine(h.Dir, "my-own-preset.ini");
        AddForeignShortcut(h, 0x41, mine); // the user's own Ctrl+Alt+Shift+A

        await h.Controller.TriggerDetectionAsync(); // a tracked match…
        Assert.Equal(LifecyclePhase.WatchingForTrial, h.Lifecycle.Phase);
        h.Focused = false;
        h.Controller.ApplyManualOverride("Coldwind Farm"); // …and a pending override
        Assert.True(h.Controller.HasPendingActivation);

        h.Controller.ResetSettings();

        // ReShade.ini: only the user's own entry is left.
        var entries = ReshadeConfigurationService.ReadShortcutAssignments(h.ReshadeIni);
        IniShortcutEntry remaining = Assert.Single(entries);
        Assert.Equal(0x41, remaining.Shortcut.VirtualKey);

        // Config: defaults, with ReShade.ini discovered again, saved.
        AppConfig config = h.Controller.Config;
        Assert.Null(config.GlobalPresetPath);
        Assert.Empty(config.RealmPresets);
        Assert.Empty(config.ReshadeShortcuts);
        Assert.Equal("F8", config.DetectionHotkey.Key);
        Assert.Equal(h.ReshadeIni, config.ReshadeIniPath);
        Assert.Equal(h.ReshadeIni, ConfigOnDisk(h).ReshadeIniPath);
        Assert.Null(ConfigOnDisk(h).GlobalPresetPath);

        // Runtime: nothing pending, not overriding, the match no longer tracked.
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.False(h.Controller.IsManualOverride);
        Assert.False(h.Controller.HasPendingActivation);
        Assert.Equal(LifecyclePhase.Unknown, h.Lifecycle.Phase);
        Assert.Null(h.Controller.ActivePresetPath);
        Assert.StartsWith("Settings reset to defaults.", h.Controller.StatusMessage);

        // Hotkey: the old one is released; the default one is registered.
        Assert.False(h.Hotkey.Holds(Combo("F20")));
        Assert.True(h.Hotkey.Holds(new DetectionHotkeyConfig { Key = "F8" }));
        Assert.Null(h.Controller.HotkeyError);

        h.Focused = true;
        Assert.True(h.Controller.RetryPendingActivation());
        Assert.Equal(1, h.Keys.CallCount); // only the detected preset before the reset
    }

    [Fact]
    public void ResetSettings_WithAnUnwritableReshadeIni_StillResets()
    {
        ControllerHarness h = Harness();
        File.SetAttributes(h.ReshadeIni, FileAttributes.ReadOnly);
        try
        {
            h.Controller.ResetSettings();

            Assert.Null(h.Controller.Config.GlobalPresetPath);
            Assert.Empty(h.Controller.Config.RealmPresets);
            Assert.Contains("Could not remove this app's shortcuts from ReShade.ini", h.Controller.StatusMessage);
        }
        finally
        {
            File.SetAttributes(h.ReshadeIni, FileAttributes.Normal);
        }
    }

    [Fact]
    public void ResetSettings_WithTheDefaultHotkeyTaken_StillSaysWhatTheResetDid()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = c => c.DetectionHotkey = Combo("F20") });
        h.Hotkey.HoldElsewhere(new DetectionHotkeyConfig { Key = "F8" }); // another program holds F8
        File.SetAttributes(h.ReshadeIni, FileAttributes.ReadOnly);
        try
        {
            h.Controller.ResetSettings();

            Assert.StartsWith("Settings reset to defaults.", h.Controller.StatusMessage);
            Assert.Contains("Could not remove this app's shortcuts from ReShade.ini", h.Controller.StatusMessage);
            Assert.Contains("Hotkey 'F8' unavailable", h.Controller.StatusMessage); // added, not instead
            Assert.NotNull(h.Controller.HotkeyError); // the banner
        }
        finally
        {
            File.SetAttributes(h.ReshadeIni, FileAttributes.Normal);
        }
    }

    [Fact]
    public void UnavailableHotkeyAtStartup_IsAddedToTheStartupStatus_NotInstead()
    {
        ControllerHarness h = Harness(new ControllerHarnessOptions
        {
            HotkeysHeldElsewhere = new[] { Combo("F21") }, // another program holds Ctrl+Alt+F21
            Configure = c =>
            {
                c.DetectionHotkey = Combo("F21");
                c.ReshadeIniPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ReShade.ini");
            }
        });

        Assert.NotNull(h.Controller.HotkeyError);
        Assert.StartsWith("Dead by Daylight detected.", h.Controller.StatusMessage);
        Assert.Contains("ReShade.ini not found", h.Controller.StatusMessage);
        Assert.Contains("Hotkey 'Ctrl+Alt+F21' unavailable", h.Controller.StatusMessage);

        // A startup warning (e.g. an ignored database override) is added as well.
        h.Controller.ReportWarning("The map database override was ignored.");
        Assert.Contains("ReShade.ini not found", h.Controller.StatusMessage);
        Assert.EndsWith("The map database override was ignored.", h.Controller.StatusMessage);
    }

    // ---------- C17: config changes go through the controller ----------

    [Fact]
    public void ApplyDatabaseVersion_UpdatesAndSavesTheConfig()
    {
        ControllerHarness h = Harness();
        int changes = 0;
        h.Controller.Changed += (_, _) => changes++;

        h.Controller.ApplyDatabaseVersion(4242);

        Assert.Equal(4242, h.Controller.Config.DatabaseVersion);
        Assert.Equal(4242, ConfigOnDisk(h).DatabaseVersion);
        Assert.True(changes > 0); // the UI refreshes (the database may have new realms)
    }
}
