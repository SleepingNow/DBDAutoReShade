using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// Which ReShade.ini shortcut entries belong to the app (F1: only a pool combo
/// for a managed path; the user's own shortcut for an assigned file stays
/// theirs), and ReShade's [INPUT] ForceShortcutModifiers (F5: with it off, a
/// shortcut also fires with extra modifiers held).
/// </summary>
public sealed class ReshadeShortcutOwnershipTests : IDisposable
{
    private const int F9 = 0x78;
    private const int F13 = 0x7C;
    private const string Pack = "STEAXS FILTERS PACK - CHARLIE";
    private const string RelativeUniversal = @".\" + Pack + @"\00.STX.Universal.ini";
    private const string UsersQuad = "120,1,1,0"; // the owner's own Ctrl+Shift+F9

    /// <summary>Stands in for &lt;root&gt;\DeadByDaylight\Binaries\Win64 (ReShade.ini's folder).</summary>
    private readonly string _win64 = Path.Combine(
        Path.GetTempPath(), "dbd-own-" + Guid.NewGuid().ToString("N"), "Dead by Daylight", "DeadByDaylight", "Binaries", "Win64");

    private readonly string _root;

    public ReshadeShortcutOwnershipTests()
    {
        Directory.CreateDirectory(Path.Combine(_win64, Pack));
        _root = Path.GetFullPath(Path.Combine(_win64, "..", "..", "..", ".."));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private string Ini => Path.Combine(_win64, "ReShade.ini");

    private string Universal => Path.Combine(_win64, Pack, "00.STX.Universal.ini");

    private static ReshadeShortcutConfig Combo(int vk, bool ctrl = true, bool shift = true, bool alt = true) =>
        new() { VirtualKey = vk, Ctrl = ctrl, Shift = shift, Alt = alt };

    private static ReshadeShortcutConfig PoolCombo(int vk) => Combo(vk);

    private string Value(string key) =>
        IniDocument.Parse(File.ReadAllText(Ini)).GetValue("GENERAL", key) ?? string.Empty;

    /// <summary>The file without the two shortcut list lines (everything the sync must leave alone).</summary>
    private string WithoutShortcutLists() => string.Concat(
        File.ReadAllText(Ini)
            .Split("\r\n")
            .Where(l => !l.StartsWith("PresetShortcutKeys=", StringComparison.Ordinal) &&
                        !l.StartsWith("PresetShortcutPaths=", StringComparison.Ordinal))
            .Select(l => l + "\r\n"));

    /// <summary>19 realms the app already manages (pool combos, absolute paths), as on the owner's machine.</summary>
    private List<PresetShortcutAssignment> OwnersManagedRealms()
    {
        var presets = Enumerable.Range(1, 19).ToDictionary(
            i => $"Realm {i:00}",
            i => Path.Combine(_win64, Pack, $"{i:00}.STX.Realm {i:00}.ini"));
        foreach (string file in presets.Values.Append(Universal))
        {
            File.WriteAllText(file, "[General]\r\nTechniques=X\r\n");
        }

        var allocated = ReshadeConfigurationService.AllocateShortcuts(presets.Keys);
        return presets
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new PresetShortcutAssignment(kv.Key, kv.Value, allocated[kv.Key]))
            .ToList();
    }

    /// <summary>
    /// The owner's real ReShade.ini layout (CRLF): relative PresetPath, the user's own
    /// Ctrl+Shift+F9 → Universal entry written FIRST, then the app's 19 pool entries.
    /// </summary>
    private void WriteOwnersIni(string usersPath, IReadOnlyList<PresetShortcutAssignment> managed, string force = "1")
    {
        string keys = string.Join(",", new[] { UsersQuad }.Concat(
            managed.Select(a => $"{a.Shortcut.VirtualKey},1,1,1")));
        string paths = string.Join(",", new[] { usersPath }.Concat(managed.Select(a => a.PresetPath)));
        File.WriteAllText(Ini,
            "[GENERAL]\r\n" +
            @"EffectSearchPaths=.\reshade-shaders\Shaders\**" + "\r\n" +
            "PresetPath=" + RelativeUniversal + "\r\n" +
            "PresetShortcutKeys=" + keys + "\r\n" +
            "PresetShortcutPaths=" + paths + "\r\n" +
            "PresetTransitionDuration=1000\r\n" +
            "\r\n" +
            "[INPUT]\r\n" +
            "ForceShortcutModifiers=" + force + "\r\n" +
            "KeyOverlay=36,0,0,0\r\n");
    }

    /// <summary>Allocates like the app (stable, avoiding reserved combos) and syncs.</summary>
    private ShortcutSyncResult SyncLikeTheApp(
        List<PresetShortcutAssignment> current,
        IEnumerable<(string Name, string Path)> wanted,
        IEnumerable<string> lastSynced)
    {
        var wantedList = wanted.ToList();
        var existing = current.ToDictionary(a => a.Realm, a => a.Shortcut, StringComparer.OrdinalIgnoreCase);
        var reserved = ReshadeConfigurationService.ReadReservedShortcuts(
            Ini, wantedList.Select(w => w.Path).Concat(lastSynced));
        var allocated = ReshadeConfigurationService.AllocateShortcuts(wantedList.Select(w => w.Name), existing, reserved);
        var assignments = wantedList.Select(w => new PresetShortcutAssignment(w.Name, w.Path, allocated[w.Name])).ToList();
        ShortcutSyncResult result = new ReshadeConfigurationService().SyncPresetShortcuts(Ini, assignments, lastSynced);
        current.Clear();
        current.AddRange(assignments);
        return result;
    }

    private void AssertUsersEntryIsFirstAndUnchanged(string usersPath)
    {
        Assert.StartsWith(UsersQuad + ",", Value("PresetShortcutKeys") + ",", StringComparison.Ordinal);
        Assert.StartsWith(usersPath + ",", Value("PresetShortcutPaths") + ",", StringComparison.Ordinal);
        IniShortcutEntry first = ReshadeConfigurationService.ReadShortcutAssignments(Ini)[0];
        Assert.True(ReshadeConfigurationService.SameCombo(Combo(F9, ctrl: true, shift: true, alt: false), first.Shortcut));
        Assert.Equal(Universal, first.PresetPath, ignoreCase: true);
    }

    // ---------- F1: the user's own shortcut for an assigned preset file ----------

    [Theory]
    [InlineData(true, AppConfigKeys.GlobalShortcutKey)]
    [InlineData(false, AppConfigKeys.GlobalShortcutKey)]
    [InlineData(true, "Haddonfield")]
    [InlineData(false, "Haddonfield")]
    public void OwnersLayout_AssigningTheUsersPresetFile_KeepsTheUsersShortcut_ThroughAssignClearAndRemove(
        bool relativeUsersEntry, string name)
    {
        List<PresetShortcutAssignment> managed = OwnersManagedRealms();
        string usersPath = relativeUsersEntry ? RelativeUniversal : Universal;
        WriteOwnersIni(usersPath, managed);
        string untouched = WithoutShortcutLists();
        var realms = managed.Select(a => (Name: a.Realm, Path: a.PresetPath)).ToList();
        var lastSynced = realms.Select(r => r.Path).ToList();

        // Assign Universal (the absolute path the file picker returns) to Global / a realm.
        ShortcutSyncResult assigned = SyncLikeTheApp(managed, realms.Append((name, Universal)), lastSynced);

        Assert.True(assigned.Changed);
        Assert.Empty(assigned.Conflicts);
        Assert.Equal(1, assigned.PreservedForeign); // the user's entry is foreign, not dropped
        Assert.Equal(1, assigned.Added);
        Assert.Equal(0, assigned.Removed);
        AssertUsersEntryIsFirstAndUnchanged(usersPath);
        Assert.Equal(untouched, WithoutShortcutLists());
        ReshadeShortcutConfig appsCombo = managed.Single(a => a.Realm == name).Shortcut;
        Assert.True(ReshadeConfigurationService.IsInShortcutPool(appsCombo));
        Assert.Equal((int)'7', appsCombo.VirtualKey); // the first free pool slot: a separate entry
        var loaded = ReshadeConfigurationService.ReadShortcutAssignments(Ini);
        Assert.Equal(21, loaded.Count);
        Assert.Equal(2, loaded.Count(e => string.Equals(e.PresetPath, Universal, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(loaded, e => ReshadeConfigurationService.SameCombo(e.Shortcut, appsCombo) &&
                                     string.Equals(e.PresetPath, Universal, StringComparison.OrdinalIgnoreCase));
        lastSynced = realms.Select(r => r.Path).Append(Universal).ToList();

        // A second sync changes nothing (the user's entry is not re-added or moved).
        Assert.False(SyncLikeTheApp(managed, realms.Append((name, Universal)), lastSynced).Changed);

        // Un-assign: the app's entry goes, the user's stays.
        ShortcutSyncResult cleared = SyncLikeTheApp(managed, realms, lastSynced);
        Assert.Equal(1, cleared.Removed);
        Assert.Equal(1, cleared.PreservedForeign);
        AssertUsersEntryIsFirstAndUnchanged(usersPath);
        Assert.Single(ReshadeConfigurationService.ReadShortcutAssignments(Ini),
            e => string.Equals(e.PresetPath, Universal, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(untouched, WithoutShortcutLists());

        // Assigned again, then every app entry removed (Reset Settings): only the user's is left.
        SyncLikeTheApp(managed, realms.Append((name, Universal)), lastSynced);
        ShortcutSyncResult removedAll = new ReshadeConfigurationService().SyncPresetShortcuts(
            Ini, Array.Empty<PresetShortcutAssignment>(), realms.Select(r => r.Path).Append(Universal));
        Assert.Equal(20, removedAll.Removed);
        Assert.Equal(1, removedAll.PreservedForeign);
        Assert.Equal(UsersQuad, Value("PresetShortcutKeys"));
        Assert.Equal(usersPath, Value("PresetShortcutPaths"));
        Assert.Equal(untouched, WithoutShortcutLists());
    }

    [Fact]
    public void ReadForeignShortcuts_TheUsersShortcutForAManagedFile_StaysReserved()
    {
        List<PresetShortcutAssignment> managed = OwnersManagedRealms();
        WriteOwnersIni(RelativeUniversal, managed);

        var foreign = ReshadeConfigurationService.ReadForeignShortcuts(
            Ini, managed.Select(a => a.PresetPath).Append(Universal));

        ReshadeShortcutConfig only = Assert.Single(foreign);
        Assert.True(ReshadeConfigurationService.SameCombo(Combo(F9, ctrl: true, shift: true, alt: false), only));
    }

    [Fact]
    public void PoolComboForAManagedFile_IsStillTheAppsOwnEntry()
    {
        // The ownership rule needs both: a pool combo AND a managed path. A pool entry for a
        // file the app manages (e.g. written by an older version, relative path) is replaced.
        File.WriteAllText(Universal, "[General]\r\nTechniques=X\r\n");
        File.WriteAllText(Ini, "[GENERAL]\r\nPresetShortcutKeys=" + UsersQuad + ",125,1,1,1\r\n" +
                               "PresetShortcutPaths=" + RelativeUniversal + "," + RelativeUniversal + "\r\n");

        ShortcutSyncResult result = new ReshadeConfigurationService().SyncPresetShortcuts(
            Ini, new[] { new PresetShortcutAssignment(AppConfigKeys.GlobalShortcutKey, Universal, PoolCombo(F13)) }, new[] { Universal });

        Assert.Equal(1, result.PreservedForeign);
        Assert.Equal(0, result.Added); // the app's entry for that path was there (same path, new combo)
        Assert.Equal(UsersQuad + ",124,1,1,1", Value("PresetShortcutKeys"));
        Assert.Equal(RelativeUniversal + "," + Universal, Value("PresetShortcutPaths"));
    }

    // ---------- F5: [INPUT] ForceShortcutModifiers ----------

    public static TheoryData<string, bool> ModifierSettings() => new()
    {
        { string.Empty, false }, // absent: ReShade's default is on (exact match)
        { "[INPUT]\r\nForceShortcutModifiers=1\r\n", false },
        { "[INPUT]\r\nForceShortcutModifiers=0\r\n", true },
        { "[INPUT]\r\nForceShortcutModifiers = 0 \r\n", true },
        { "[INPUT]\r\nForceShortcutModifiers=\r\n", true }, // blank reads as 0
        { "[INPUT]\r\nForceShortcutModifiers=true\r\n", true }, // not what ReShade writes: assume off
        { "[INPUT]\r\nForceShortcutModifiers=1\r\n[INPUT]\r\nForceShortcutModifiers=0\r\n", true }, // any off wins
        { "[input]\r\nforceshortcutmodifiers=0\r\n", true },
        { "[GENERAL]\r\nForceShortcutModifiers=0\r\n", false }, // another section: not ReShade's setting
    };

    [Theory]
    [MemberData(nameof(ModifierSettings))]
    public void ReadReservedShortcuts_WithModifiersNotForced_ReservesThePoolComboOnEachUserKey(string input, bool loose)
    {
        File.WriteAllText(Ini,
            "[GENERAL]\r\nPresetShortcutKeys=124,0,0,0,49,1,0,0,120,1,1,0,125,1,1,1\r\n" +
            @"PresetShortcutPaths=C:\User\BareF13.ini,C:\User\Ctrl1.ini,C:\User\CtrlShiftF9.ini,C:\Presets\Managed.ini" + "\r\n" +
            input);

        var reserved = ReshadeConfigurationService.ReadReservedShortcuts(Ini, new[] { @"C:\Presets\Managed.ini" });

        // The user's own combos are always reserved; the app's own entry (F14) never is.
        Assert.Contains(reserved, c => ReshadeConfigurationService.SameCombo(c, Combo(F13, false, false, false)));
        Assert.Contains(reserved, c => ReshadeConfigurationService.SameCombo(c, Combo('1', ctrl: true, shift: false, alt: false)));
        Assert.DoesNotContain(reserved, c => ReshadeConfigurationService.SameCombo(c, PoolCombo(F13 + 1)));
        Assert.DoesNotContain(reserved, c => ReshadeConfigurationService.SameCombo(c, PoolCombo(F9))); // F9 is not a pool key

        // With modifiers not forced, sending Ctrl+Alt+Shift+F13 / +1 would fire the user's F13 / Ctrl+1.
        Assert.Equal(loose, reserved.Any(c => ReshadeConfigurationService.SameCombo(c, PoolCombo(F13))));
        Assert.Equal(loose, reserved.Any(c => ReshadeConfigurationService.SameCombo(c, PoolCombo('1'))));
        Assert.Empty(ReshadeConfigurationService.ReadReservedShortcuts(Path.Combine(_win64, "missing.ini"), Array.Empty<string>()));
    }

    [Theory]
    [InlineData("0", new[] { '0', '2' })]
    [InlineData("1", new[] { '0', '1' })]
    public void Allocation_WithModifiersNotForced_NeverHandsOutAComboThatFiresTheUsersShortcut(string force, char[] lastTwo)
    {
        // The finding's scenario: a foreign Ctrl+1 and more than 12 presets.
        File.WriteAllText(Ini,
            "[GENERAL]\r\nPresetShortcutKeys=49,1,0,0\r\nPresetShortcutPaths=C:\\User\\MyPreset.ini\r\n" +
            "[INPUT]\r\nForceShortcutModifiers=" + force + "\r\n");
        var names = Enumerable.Range(1, 14).Select(i => $"Realm {i:00}").ToList();

        var allocated = ReshadeConfigurationService.AllocateShortcuts(
            names, null, ReshadeConfigurationService.ReadReservedShortcuts(Ini, Array.Empty<string>()));

        Assert.Equal(lastTwo.Select(c => (int)c), new[] { allocated["Realm 13"].VirtualKey, allocated["Realm 14"].VirtualKey });
    }

    [Fact]
    public void Allocation_WithModifiersNotForced_MovesAnExistingComboOnTheUsersKey()
    {
        File.WriteAllText(Ini,
            "[GENERAL]\r\nPresetShortcutKeys=124,0,0,0\r\nPresetShortcutPaths=C:\\User\\BareF13.ini\r\n" +
            "[INPUT]\r\nForceShortcutModifiers=0\r\n");
        var existing = new Dictionary<string, ReshadeShortcutConfig> { ["Coldwind Farm"] = PoolCombo(F13) };

        var allocated = ReshadeConfigurationService.AllocateShortcuts(
            new[] { "Coldwind Farm" }, existing, ReshadeConfigurationService.ReadReservedShortcuts(Ini, Array.Empty<string>()));

        Assert.Equal(F13 + 1, allocated["Coldwind Farm"].VirtualKey);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", false)]
    public void Sync_WithModifiersNotForced_SkipsAnAssignmentThatWouldFireAKeptEntry(string force, bool skipped)
    {
        File.WriteAllText(Ini,
            "[GENERAL]\r\nPresetShortcutKeys=124,1,0,0\r\nPresetShortcutPaths=C:\\User\\CtrlF13.ini\r\n" +
            "[INPUT]\r\nForceShortcutModifiers=" + force + "\r\n");

        ShortcutSyncResult result = new ReshadeConfigurationService().SyncPresetShortcuts(
            Ini, new[] { new PresetShortcutAssignment("Coldwind Farm", @"C:\Presets\Coldwind.ini", PoolCombo(F13)) }, Array.Empty<string>());

        Assert.Equal(skipped, result.HasConflicts);
        Assert.Equal(skipped ? "124,1,0,0" : "124,1,0,0,124,1,1,1", Value("PresetShortcutKeys"));
        if (skipped)
        {
            ShortcutConflict conflict = Assert.Single(result.Conflicts);
            Assert.Equal(@"C:\User\CtrlF13.ini", conflict.ConflictingPresetPath);
            Assert.Contains("would also trigger your ReShade shortcut Ctrl+F13", conflict.Message);
            Assert.Contains("'Force correct modifier keys' is off", conflict.Message);
        }
    }

    // ---------- F5: which entry a sent combo fires (the send-time check) ----------

    [Theory]
    // The app sends Ctrl+Alt+Shift+1; does ReShade fire this entry with / without forced modifiers?
    [InlineData(0x31, true, true, true, true, true)] // the same combo
    [InlineData(0x31, true, false, false, false, true)] // the user's Ctrl+1
    [InlineData(0x31, false, false, false, false, true)] // a bare 1
    [InlineData(0x31, false, true, true, false, true)] // Shift+Alt+1
    [InlineData(0x32, true, true, true, false, false)] // another key
    [InlineData(0, true, true, true, false, false)] // key 0 never fires
    public void Fires_MirrorsReshadesModifierMatching(int vk, bool ctrl, bool shift, bool alt, bool exact, bool loose)
    {
        ReshadeShortcutConfig entry = Combo(vk, ctrl, shift, alt);
        ReshadeShortcutConfig sent = vk == 0 ? Combo(0) : PoolCombo('1');

        Assert.Equal(exact, ReshadeConfigurationService.Fires(sent, entry, exactModifiers: true));
        Assert.Equal(loose, ReshadeConfigurationService.Fires(sent, entry, exactModifiers: false));
    }

    [Fact]
    public void Fires_NeverWithoutTheEntrysOwnModifiers()
    {
        // Ctrl+1 held does not fire a Ctrl+Alt+Shift+1 entry, whatever the setting.
        Assert.False(ReshadeConfigurationService.Fires(Combo('1', shift: false, alt: false), PoolCombo('1'), exactModifiers: false));
        Assert.False(ReshadeConfigurationService.Fires(Combo('1', shift: false, alt: false), PoolCombo('1'), exactModifiers: true));
    }

    [Fact]
    public void EntryFiredBy_IsTheFirstEntryThatFires_InReshadeOrder()
    {
        var loaded = new[]
        {
            new IniShortcutEntry(Combo(F9, alt: false), @"C:\User\CtrlShiftF9.ini"),
            new IniShortcutEntry(Combo('1', shift: false, alt: false), @"C:\User\Ctrl1.ini"),
            new IniShortcutEntry(PoolCombo('1'), @"C:\Presets\Realm 13.ini"),
        };

        Assert.Equal(@"C:\User\Ctrl1.ini", ReshadeConfigurationService.EntryFiredBy(loaded, PoolCombo('1'), exactModifiers: false)?.PresetPath);
        Assert.Equal(@"C:\Presets\Realm 13.ini", ReshadeConfigurationService.EntryFiredBy(loaded, PoolCombo('1'), exactModifiers: true)?.PresetPath);
        Assert.Null(ReshadeConfigurationService.EntryFiredBy(loaded, PoolCombo(F13), exactModifiers: false));
    }

    [Theory]
    [MemberData(nameof(ModifierSettings))]
    public void ReadForcesExactModifiers_IsTrueOnlyWhenReshadeIsKnownToForceThem(string input, bool loose)
    {
        File.WriteAllText(Ini, "[GENERAL]\r\nPresetPath=" + RelativeUniversal + "\r\n" + input);

        Assert.Equal(!loose, ReshadeConfigurationService.ReadForcesExactModifiers(Ini));
        Assert.False(ReshadeConfigurationService.ReadForcesExactModifiers(Path.Combine(_win64, "missing.ini")));
    }
}
