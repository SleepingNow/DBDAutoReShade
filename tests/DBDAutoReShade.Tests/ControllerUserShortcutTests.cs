using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// The user's own ReShade shortcut for a preset file that is also assigned in
/// the app (F1) survives assigning, clearing and Reset Settings through the
/// controller, on the owner's real ReShade.ini layout.
/// </summary>
public sealed partial class ControllerActivationTests
{
    private const string OwnersPack = "STEAXS FILTERS PACK - CHARLIE";
    private const string OwnersRelativeUniversal = @".\" + OwnersPack + @"\00.STX.Universal.ini";

    private static string IniValue(ControllerHarness h, string key) =>
        IniDocument.Parse(File.ReadAllText(h.ReshadeIni)).GetValue("GENERAL", key) ?? string.Empty;

    private static void AssertUsersCtrlShiftF9IsFirst(ControllerHarness h, string universal)
    {
        Assert.StartsWith("120,1,1,0,", IniValue(h, "PresetShortcutKeys") + ",", StringComparison.Ordinal);
        Assert.StartsWith(OwnersRelativeUniversal + ",", IniValue(h, "PresetShortcutPaths") + ",", StringComparison.Ordinal);
        IniShortcutEntry first = ReshadeConfigurationService.ReadShortcutAssignments(h.ReshadeIni)[0];
        Assert.Equal(0x78, first.Shortcut.VirtualKey);
        Assert.True(first.Shortcut.Ctrl && first.Shortcut.Shift && !first.Shortcut.Alt);
        Assert.Equal(universal, first.PresetPath, ignoreCase: true);
    }

    [Fact]
    public void GlobalPreset_SetToTheFileOfTheUsersOwnShortcut_KeepsThatShortcut_ThroughClearAndReset()
    {
        string universal = Path.Combine(_dir, OwnersPack, "00.STX.Universal.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(universal)!);
        File.WriteAllText(universal, "[General]\r\nTechniques=X\r\nTechniqueSorting=X\r\n");

        // The owner's ReShade.ini (CRLF, relative PresetPath, no Global preset): the user's own
        // Ctrl+Shift+F9 → Universal comes first, the app's entries (here Coldwind) follow.
        ControllerHarness h = Harness(new ControllerHarnessOptions
        {
            Configure = c =>
            {
                c.GlobalPresetPath = null;
                File.WriteAllText(c.ReshadeIniPath!,
                    "[GENERAL]\r\nPresetPath=" + OwnersRelativeUniversal + "\r\n" +
                    "PresetShortcutKeys=120,1,1,0\r\nPresetShortcutPaths=" + OwnersRelativeUniversal + "\r\n" +
                    "\r\n[INPUT]\r\nForceShortcutModifiers=1\r\nKeyOverlay=36,0,0,0\r\n");
            }
        });
        AssertUsersCtrlShiftF9IsFirst(h, universal);
        Assert.Equal(2, ReshadeConfigurationService.ReadShortcutAssignments(h.ReshadeIni).Count);

        // Presets → Global → 00.STX.Universal.ini (the absolute path the file picker returns).
        Assert.Null(h.Controller.SetGlobalPreset(universal));

        AssertUsersCtrlShiftF9IsFirst(h, universal);
        ReshadeShortcutConfig global = h.Controller.Config.ReshadeShortcuts[AppConfigKeys.GlobalShortcutKey];
        Assert.True(ReshadeConfigurationService.IsInShortcutPool(global));
        var entries = ReshadeConfigurationService.ReadShortcutAssignments(h.ReshadeIni);
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => ReshadeConfigurationService.SameCombo(e.Shortcut, global) &&
                                      string.Equals(e.PresetPath, universal, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("not written", h.Controller.StatusMessage);

        // Cleared again: the app's entry goes, the user's stays.
        Assert.Null(h.Controller.SetGlobalPreset(null));
        AssertUsersCtrlShiftF9IsFirst(h, universal);
        Assert.Single(ReshadeConfigurationService.ReadShortcutAssignments(h.ReshadeIni),
            e => string.Equals(e.PresetPath, universal, StringComparison.OrdinalIgnoreCase));

        // Assigned to a realm, then Reset Settings: only the user's own entry is left, unchanged.
        Assert.Null(h.Controller.SetRealmPreset(Autohaven, universal));
        Assert.Equal(2, ReshadeConfigurationService.ReadShortcutAssignments(h.ReshadeIni)
            .Count(e => string.Equals(e.PresetPath, universal, StringComparison.OrdinalIgnoreCase)));
        h.Controller.ResetSettings();

        Assert.Equal("120,1,1,0", IniValue(h, "PresetShortcutKeys"));
        Assert.Equal(OwnersRelativeUniversal, IniValue(h, "PresetShortcutPaths"));
        Assert.StartsWith("Settings reset to defaults.", h.Controller.StatusMessage);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void WithModifiersNotForced_AComboThatWouldFireTheUsersShortcut_IsNeverHandedOut(string force, bool handedOut)
    {
        ControllerHarness h = Harness();
        int nextFree = CombosByName(h).Values.Max() + 1; // the slot a new preset would get
        string mine = Path.Combine(h.Dir, "my-own-preset.ini");

        // The user's own Ctrl+<that key> (not a pool combo), with ReShade's
        // "Force correct modifier keys" set as given.
        var doc = IniDocument.Parse(File.ReadAllText(h.ReshadeIni));
        doc.SetValue("GENERAL", "PresetShortcutKeys", $"{nextFree},1,0,0," + doc.GetValue("GENERAL", "PresetShortcutKeys"));
        doc.SetValue("GENERAL", "PresetShortcutPaths", mine + "," + doc.GetValue("GENERAL", "PresetShortcutPaths"));
        doc.SetValue("INPUT", "ForceShortcutModifiers", force);
        File.WriteAllText(h.ReshadeIni, doc.ToString());

        Assert.Null(h.Controller.SetRealmPreset(Autohaven, NewPresetFile(h, "Autohaven")));

        Assert.Equal(handedOut ? nextFree : nextFree + 1, CombosByName(h)[Autohaven]);
        Assert.Equal(handedOut, h.Controller.Config.ReshadeShortcuts.Values.Any(s => s.VirtualKey == nextFree));
        Assert.StartsWith($"{nextFree},1,0,0,", IniValue(h, "PresetShortcutKeys"), StringComparison.Ordinal); // kept
    }

    // ---------- F5 at send time: a loaded combo that would fire the user's shortcut first ----------

    /// <summary>
    /// The user's own Ctrl+F13 → <paramref name="mine"/> comes first in ReShade.ini (a sync keeps
    /// the user's entries before the app's), with "Force correct modifier keys" on, so the earlier
    /// run gave the first preset Ctrl+Alt+Shift+F13 and the running ReShade loaded it.
    /// </summary>
    private static Action<AppConfig> UsersCtrlF13First(string mine, Action<AppConfig>? more = null) => c =>
    {
        more?.Invoke(c);
        var doc = IniDocument.Parse(File.ReadAllText(c.ReshadeIniPath!));
        doc.SetValue("GENERAL", "PresetShortcutKeys", "124,1,0,0");
        doc.SetValue("GENERAL", "PresetShortcutPaths", mine);
        doc.SetValue("INPUT", "ForceShortcutModifiers", "1");
        File.WriteAllText(c.ReshadeIniPath!, doc.ToString());
    };

    /// <summary>What ReShade does when "Force correct modifier keys" is toggled in its settings: save it to ReShade.ini.</summary>
    private static void ReshadeSavesForceModifiers(ControllerHarness h, string value)
    {
        var doc = IniDocument.Parse(File.ReadAllText(h.ReshadeIni));
        doc.SetValue("INPUT", "ForceShortcutModifiers", value);
        File.WriteAllText(h.ReshadeIni, doc.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // DBD restarted and loaded the same file, before the monitor reported it
    public async Task ModifiersTurnedOffWhileDbdRuns_AComboThatWouldFireTheUsersShortcutFirst_IsNeverSent(bool newProcessNotYetSeen)
    {
        string mine = Path.Combine(_dir, "my-own-preset.ini");
        ControllerHarness h = Harness(new ControllerHarnessOptions { Configure = UsersCtrlF13First(mine) });
        Assert.Equal(0x7C, CombosByName(h)["Coldwind Farm"]);
        Assert.Equal("124,1,0,0,124,1,1,1,125,1,1,1", IniValue(h, "PresetShortcutKeys"));
        Assert.Null(h.Controller.TestRealmPreset("Coldwind Farm")); // modifiers forced: only the exact combo fires

        // The user turns the option off in ReShade while the game runs; nothing re-syncs. Sending
        // Ctrl+Alt+Shift+F13 now fires the user's Ctrl+F13, which ReShade checks first.
        ReshadeSavesForceModifiers(h, "0");
        if (newProcessNotYetSeen)
        {
            h.Dbd.ProcessId = 9001;
        }

        string? error = h.Controller.TestRealmPreset("Coldwind Farm");
        Assert.NotNull(error);
        Assert.Contains($"Ctrl+Alt+Shift+F13 would also trigger your ReShade shortcut Ctrl+F13 → {mine}", error);
        Assert.Contains("'Force correct modifier keys' option is off", error);
        Assert.Equal(1, h.Keys.CallCount); // nothing sent
        Assert.False(h.Controller.HasPendingActivation);
        Assert.StartsWith("Test failed:", h.Controller.StatusMessage);
        Assert.Contains("FAILED", h.Controller.LastActivationText);

        // A detected realm fails for good as well (not left waiting for gameplay).
        await h.Controller.TriggerDetectionAsync();
        Assert.Equal(1, h.Keys.CallCount);
        Assert.Equal(AppState.Ready, h.States.State);
        Assert.Contains("would also trigger your ReShade shortcut Ctrl+F13", h.Controller.StatusMessage);

        // Presets on other keys still switch, and turning the option on again unblocks Coldwind at once.
        Assert.Null(h.Controller.TestGlobalPreset());
        ReshadeSavesForceModifiers(h, "1");
        Assert.Null(h.Controller.TestRealmPreset("Coldwind Farm"));
        Assert.Equal(new[] { "Coldwind Farm", AppConfigKeys.GlobalShortcutKey, "Coldwind Farm" }, h.SentNames());
    }

    [Fact]
    public void ModifiersForced_ALooserEntryForThePresetFirst_DoesNotHideAnotherPresetsExactEntry()
    {
        // ReShade.ini edited by hand while DBD runs (the sync never writes duplicate combos):
        // the user's Ctrl+F13 → Coldwind's own preset, then the user's Ctrl+Alt+Shift+F13 → another
        // preset, then the app's Ctrl+Alt+Shift+F13 → Coldwind. With "Force correct modifier keys"
        // on, Ctrl+F13 doesn't fire for Ctrl+Alt+Shift+F13; the other preset's exact entry does.
        ControllerHarness h = Harness();
        Assert.Equal(0x7C, CombosByName(h)["Coldwind Farm"]);
        string coldwind = h.Controller.Config.RealmPresets["Coldwind Farm"];
        string mine = Path.Combine(_dir, "my-own-preset.ini");
        var doc = IniDocument.Parse(File.ReadAllText(h.ReshadeIni));
        doc.SetValue("GENERAL", "PresetShortcutKeys", "124,1,0,0,124,1,1,1," + doc.GetValue("GENERAL", "PresetShortcutKeys"));
        doc.SetValue("GENERAL", "PresetShortcutPaths", $"{coldwind},{mine}," + doc.GetValue("GENERAL", "PresetShortcutPaths"));
        doc.SetValue("INPUT", "ForceShortcutModifiers", "1");
        File.WriteAllText(h.ReshadeIni, doc.ToString());
        h.Dbd.ProcessId = 9001; // DBD restarted and loaded this file, before the monitor reported it

        string? error = h.Controller.TestRealmPreset("Coldwind Farm");

        Assert.NotNull(error);
        Assert.Contains($"Ctrl+Alt+Shift+F13 is also the ReShade shortcut for {mine}, which ReShade checks first", error);
        Assert.Equal(0, h.Keys.CallCount); // nothing sent
    }

    [Fact]
    public void ModifiersOff_PoolExhausted_ThePresetLeftOnTheUsersKey_IsNeverSent()
    {
        // Coldwind, 20 more realms and Global fill the 22-combo pool; the user's own Ctrl+F13 comes first.
        string mine = Path.Combine(_dir, "my-own-preset.ini");
        ControllerHarness h = Harness(new ControllerHarnessOptions
        {
            Configure = UsersCtrlF13First(mine, c =>
            {
                for (int i = 1; i <= 20; i++)
                {
                    string preset = Path.Combine(_dir, $"realm-{i:00}.ini");
                    File.WriteAllText(preset, "[General]\nTechniques=X\nTechniqueSorting=X\n");
                    c.RealmPresets[$"Realm {i:00}"] = preset;
                }
            })
        });
        Assert.Equal(ReshadeConfigurationService.ShortcutPoolSize, h.Controller.Config.ReshadeShortcuts.Count);
        Assert.Equal(0x7C, CombosByName(h)["Coldwind Farm"]);

        // The option is turned off, then DBD restarts. The sync can't move Coldwind off F13 (every
        // other combo is taken), so Coldwind keeps Ctrl+Alt+Shift+F13, which the new ReShade loads
        // right after the user's Ctrl+F13.
        ReshadeSavesForceModifiers(h, "0");
        h.Dbd.ProcessId = 9001;
        h.Dbd.RaiseStatusChanged(running: true);
        Assert.Contains("Too many presets assigned", h.Controller.StatusMessage);
        Assert.Equal(0x7C, CombosByName(h)["Coldwind Farm"]);
        Assert.Empty(h.Controller.NamesNeedingDbdRestart); // the new process loaded every pair

        string? error = h.Controller.TestRealmPreset("Coldwind Farm");
        Assert.NotNull(error);
        Assert.Contains($"Ctrl+Alt+Shift+F13 would also trigger your ReShade shortcut Ctrl+F13 → {mine}", error);
        Assert.Equal(0, h.Keys.CallCount);

        Assert.Null(h.Controller.TestRealmPreset("Realm 01")); // Ctrl+Alt+Shift+F14: no shortcut of the user's on F14
        Assert.Equal(new[] { "Realm 01" }, h.SentNames());
    }
}
