using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class ReshadeShortcutSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-reshade-" + Guid.NewGuid().ToString("N"));
    private string Ini => Path.Combine(_dir, "ReShade.ini");
    private string Bak => Ini + ".dbdautoreshade.bak";

    public ReshadeShortcutSyncTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Ini, "[GENERAL]\nPresetPath=C:\\Presets\\Global.ini\n\n[INPUT]\nKeyOverlay=36,0,0,0\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private const int F13 = 0x7C;

    private static ReshadeShortcutConfig Combo(int vk, bool ctrl = true, bool shift = true, bool alt = true) =>
        new() { VirtualKey = vk, Ctrl = ctrl, Shift = shift, Alt = alt };

    private static IReadOnlyCollection<PresetShortcutAssignment> Assignments(params (string Realm, string Preset)[] items)
    {
        var allocated = ReshadeConfigurationService.AllocateShortcuts(items.Select(i => i.Realm));
        return items.Select(i => new PresetShortcutAssignment(i.Realm, i.Preset, allocated[i.Realm])).ToList();
    }

    private IReadOnlyCollection<PresetShortcutAssignment> AssignmentsAvoidingForeign(
        IEnumerable<string> managedOrPreviouslyManaged, params (string Realm, string Preset)[] items)
    {
        var reserved = ReshadeConfigurationService.ReadForeignShortcuts(
            Ini, items.Select(i => i.Preset).Concat(managedOrPreviouslyManaged));
        var allocated = ReshadeConfigurationService.AllocateShortcuts(items.Select(i => i.Realm), null, reserved);
        return items.Select(i => new PresetShortcutAssignment(i.Realm, i.Preset, allocated[i.Realm])).ToList();
    }

    private string Value(string key) => IniDocument.Parse(File.ReadAllText(Ini)).GetValue("GENERAL", key) ?? string.Empty;

    /// <summary>
    /// Independent oracle: a literal port of the list splitting in ReShade's ini_file::load
    /// (source/ini_file.cpp) — ",," is an escaped comma, only a single ',' splits.
    /// </summary>
    private static List<string> ReshadeLoadList(string value)
    {
        var elements = new List<string>();
        for (int offset = 0, @base = 0, len = value.Length; offset <= len;)
        {
            int next = value.IndexOf(',', offset);
            int found = next < 0 ? len : next;
            if (found + 1 < len && value[found + 1] == ',')
            {
                offset = found + 2;
            }
            else
            {
                var element = new System.Text.StringBuilder();
                while (@base < found)
                {
                    char c = value[@base++];
                    element.Append(c);
                    if (c == ',' && @base < found && value[@base] == ',')
                    {
                        @base++; // Skip second comma in a ",," escape sequence
                    }
                }

                elements.Add(element.ToString());
                @base = offset = found + 1;
            }
        }

        return elements;
    }

    /// <summary>strtoul(s, nullptr, 10) as ReShade uses it for key values: leading digits, else 0.</summary>
    private static int Strtoul(string s)
    {
        string t = s.TrimStart();
        int len = 0;
        while (len < t.Length && char.IsAsciiDigit(t[len]))
        {
            len++;
        }

        return len == 0 ? 0 : int.Parse(t.AsSpan(0, len), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The shortcuts ReShade loads from the ini right now: (combo, raw path) per pair.</summary>
    private List<(int Vk, bool Ctrl, bool Shift, bool Alt, string Path)> ReshadeLoads()
    {
        var doc = IniDocument.Parse(File.ReadAllText(Ini));
        string? k = doc.GetValue("GENERAL", "PresetShortcutKeys");
        string? p = doc.GetValue("GENERAL", "PresetShortcutPaths");
        List<string> keys = k is null ? new List<string>() : ReshadeLoadList(k);
        List<string> paths = p is null ? new List<string>() : ReshadeLoadList(p);
        var result = new List<(int, bool, bool, bool, string)>();
        for (int i = 0; i < keys.Count / 4 && i < paths.Count; i++)
        {
            result.Add((Strtoul(keys[i * 4]), Strtoul(keys[i * 4 + 1]) != 0, Strtoul(keys[i * 4 + 2]) != 0,
                Strtoul(keys[i * 4 + 3]) != 0, paths[i]));
        }

        return result;
    }

    /// <summary>
    /// ReShade loads exactly <paramref name="expected"/> (any order), and the app's own
    /// read API agrees with ReShade for every entry that has a preset.
    /// </summary>
    private void AssertReshadeLoads(params (ReshadeShortcutConfig Combo, string Path)[] expected)
    {
        var loaded = ReshadeLoads();
        Assert.Equal(
            expected.Select(e => (e.Combo.VirtualKey, e.Combo.Ctrl, e.Combo.Shift, e.Combo.Alt, e.Path)).OrderBy(e => e.Path, StringComparer.Ordinal),
            loaded.OrderBy(e => e.Path, StringComparer.Ordinal));

        var app = ReshadeConfigurationService.ReadShortcutAssignments(Ini);
        Assert.Equal(
            loaded.Where(e => e.Path.Length > 0).Select(e => (e.Vk, e.Ctrl, e.Shift, e.Alt, e.Path)),
            app.Select(e => (e.Shortcut.VirtualKey, e.Shortcut.Ctrl, e.Shortcut.Shift, e.Shortcut.Alt, e.PresetPath)));
    }

    // ---------- R1: stable allocation ----------

    [Fact]
    public void AllocateShortcuts_IsDeterministic_AndCollisionFree()
    {
        var a = ReshadeConfigurationService.AllocateShortcuts(new[] { "Coldwind Farm", "Autohaven Wreckers" });
        var b = ReshadeConfigurationService.AllocateShortcuts(new[] { "Autohaven Wreckers", "Coldwind Farm" });
        Assert.Equal(a["Coldwind Farm"].VirtualKey, b["Coldwind Farm"].VirtualKey);
        Assert.NotEqual(a["Coldwind Farm"].VirtualKey, a["Autohaven Wreckers"].VirtualKey);
        Assert.Equal(0x7C, a["Autohaven Wreckers"].VirtualKey); // sorted first → F13
    }

    [Fact]
    public void Allocate_AddingARealm_DoesNotChangeExistingKeys()
    {
        var before = ReshadeConfigurationService.AllocateShortcuts(new[] { "Coldwind Farm", "MacMillan Estate" });
        Assert.Equal(F13, before["Coldwind Farm"].VirtualKey);
        Assert.Equal(F13 + 1, before["MacMillan Estate"].VirtualKey);

        // "Autohaven Wreckers" sorts first; the old ordinal allocation gave it F13 and shifted the others.
        var after = ReshadeConfigurationService.AllocateShortcuts(
            new[] { "Coldwind Farm", "MacMillan Estate", "Autohaven Wreckers" }, before, null);

        Assert.True(ReshadeConfigurationService.SameCombo(before["Coldwind Farm"], after["Coldwind Farm"]));
        Assert.True(ReshadeConfigurationService.SameCombo(before["MacMillan Estate"], after["MacMillan Estate"]));
        Assert.Equal(F13 + 2, after["Autohaven Wreckers"].VirtualKey); // lowest free slot
    }

    [Fact]
    public void Allocate_RemovingARealm_DoesNotShiftOthers_AndItsSlotIsReusedNext()
    {
        var all = ReshadeConfigurationService.AllocateShortcuts(new[] { "A Realm", "B Realm", "C Realm" });
        Assert.Equal(F13, all["A Realm"].VirtualKey);

        var withoutA = ReshadeConfigurationService.AllocateShortcuts(new[] { "B Realm", "C Realm" }, all, null);
        Assert.Equal(F13 + 1, withoutA["B Realm"].VirtualKey);
        Assert.Equal(F13 + 2, withoutA["C Realm"].VirtualKey);
        Assert.False(withoutA.ContainsKey("A Realm"));

        var withD = ReshadeConfigurationService.AllocateShortcuts(new[] { "B Realm", "C Realm", "D Realm" }, withoutA, null);
        Assert.Equal(F13, withD["D Realm"].VirtualKey); // freed slot reused
        Assert.Equal(F13 + 1, withD["B Realm"].VirtualKey);
        Assert.Equal(F13 + 2, withD["C Realm"].VirtualKey);
    }

    [Fact]
    public void Allocate_SkipsReservedCombos_AndMovesAnExistingComboThatBecameReserved()
    {
        var existing = new Dictionary<string, ReshadeShortcutConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["B Realm"] = Combo(F13 + 2)
        };
        var reserved = new[]
        {
            Combo(F13),
            Combo(F13 + 2),
            Combo(F13 + 3, shift: false) // not a pool combo: irrelevant
        };

        var result = ReshadeConfigurationService.AllocateShortcuts(new[] { "A Realm", "B Realm" }, existing, reserved);

        Assert.Equal(F13 + 1, result["A Realm"].VirtualKey);
        Assert.Equal(F13 + 3, result["B Realm"].VirtualKey);
        Assert.All(result.Values, c => Assert.True(c.Ctrl && c.Alt && c.Shift));
    }

    [Fact]
    public void Allocate_InvalidOrDuplicatedExistingCombos_AreReplaced()
    {
        var existing = new Dictionary<string, ReshadeShortcutConfig>(StringComparer.Ordinal)
        {
            ["Zeta"] = Combo(F13 + 5),
            ["Alpha"] = Combo(F13 + 5),          // duplicate: ordinal-first name keeps it
            ["Beta"] = Combo(0x77, false, false, false), // bare F8: not in the managed pool
            ["Gamma"] = null!
        };

        var result = ReshadeConfigurationService.AllocateShortcuts(new[] { "Zeta", "Alpha", "Beta", "Gamma" }, existing, null);

        Assert.Equal(F13 + 5, result["Alpha"].VirtualKey);
        Assert.Equal(F13, result["Beta"].VirtualKey);
        Assert.Equal(F13 + 1, result["Gamma"].VirtualKey);
        Assert.Equal(F13 + 2, result["Zeta"].VirtualKey);
        Assert.Equal(4, result.Values.Select(v => v.VirtualKey).Distinct().Count());
    }

    [Fact]
    public void Allocate_IsDeterministic_RegardlessOfInputOrder()
    {
        var existing = new Dictionary<string, ReshadeShortcutConfig> { ["M"] = Combo(F13 + 4) };
        var reserved = new[] { Combo(F13 + 1) };
        var a = ReshadeConfigurationService.AllocateShortcuts(new[] { "Z", "M", "A", "Q" }, existing, reserved);
        var b = ReshadeConfigurationService.AllocateShortcuts(new[] { "Q", "A", "M", "Z", "a" }, existing, Enumerable.Reverse(reserved));

        Assert.Equal(a.Count, b.Count);
        foreach (var kv in a)
        {
            Assert.True(ReshadeConfigurationService.SameCombo(kv.Value, b[kv.Key]), kv.Key);
        }

        Assert.Equal(F13, a["A"].VirtualKey);
        Assert.Equal(F13 + 2, a["Q"].VirtualKey);
        Assert.Equal(F13 + 3, a["Z"].VirtualKey);
    }

    [Fact]
    public void Allocate_UsesDigitsAfterF24_ThenReportsExhaustion()
    {
        var names = Enumerable.Range(0, ReshadeConfigurationService.ShortcutPoolSize).Select(i => $"Realm {i:D2}").ToList();
        var full = ReshadeConfigurationService.AllocateShortcuts(names, null, null);
        Assert.Equal(0x87, full["Realm 11"].VirtualKey); // F24
        Assert.Equal('0', full["Realm 12"].VirtualKey);
        Assert.Equal('9', full["Realm 21"].VirtualKey);
        Assert.Equal(22, ReshadeConfigurationService.ShortcutPoolSize);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ReshadeConfigurationService.AllocateShortcuts(names.Append("One Too Many"), full, null));
        Assert.Contains("Too many presets assigned", ex.Message);
        Assert.Contains("pool (22) exhausted", ex.Message);

        // Reserved combos shrink the pool too.
        var reservedEx = Assert.Throws<InvalidOperationException>(() =>
            ReshadeConfigurationService.AllocateShortcuts(names, null, new[] { Combo('5') }));
        Assert.Contains("used by your own ReShade shortcuts", reservedEx.Message);
    }

    [Fact]
    public void SingleArgumentOverload_EqualsStableOverloadWithNulls()
    {
        var names = new[] { "Coldwind Farm", "__GLOBAL__", "Autohaven Wreckers" };
        var a = ReshadeConfigurationService.AllocateShortcuts(names);
        var b = ReshadeConfigurationService.AllocateShortcuts(names, null, null);
        Assert.Equal(a.Count, b.Count);
        Assert.All(a, kv => Assert.True(ReshadeConfigurationService.SameCombo(kv.Value, b[kv.Key])));
    }

    [Fact]
    public void SameCombo_ComparesKeyAndModifiers()
    {
        Assert.True(ReshadeConfigurationService.SameCombo(Combo(F13), Combo(F13)));
        Assert.False(ReshadeConfigurationService.SameCombo(Combo(F13), Combo(F13 + 1)));
        Assert.False(ReshadeConfigurationService.SameCombo(Combo(F13), Combo(F13, shift: false)));
        Assert.False(ReshadeConfigurationService.SameCombo(Combo(F13), null));
        Assert.False(ReshadeConfigurationService.SameCombo(null, null));
    }

    // ---------- Sync basics ----------

    [Fact]
    public void Sync_WritesKeysAndPaths_PreservingOtherContent()
    {
        var svc = new ReshadeConfigurationService();
        var result = svc.SyncPresetShortcuts(Ini, Assignments(
            ("Coldwind Farm", @"C:\Presets\Coldwind.ini"),
            ("MacMillan Estate", @"C:\Presets\MacMillan.ini")));

        Assert.True(result.Changed);
        var doc = IniDocument.Parse(File.ReadAllText(Ini));
        string keys = doc.GetValue("GENERAL", "PresetShortcutKeys")!;
        string paths = doc.GetValue("GENERAL", "PresetShortcutPaths")!;
        // Sorted: Autohaven? No — Coldwind < MacMillan ordinally.
        Assert.Contains(@"C:\Presets\Coldwind.ini", paths);
        Assert.Contains(@"C:\Presets\MacMillan.ini", paths);
        Assert.Equal(8, keys.Split(',').Length); // 2 shortcuts × 4 ints
        Assert.Contains("KeyOverlay=36,0,0,0", doc.ToString()); // untouched
        Assert.Contains("C:\\Presets\\Global.ini", doc.ToString()); // untouched
    }

    [Fact]
    public void Sync_PreservesForeignShortcuts_AndRemovesStaleManaged()
    {
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=124,1,1,1\nPresetShortcutPaths=C:\\User\\Mine.ini\n");
        var svc = new ReshadeConfigurationService();

        // First sync establishes management of Old.ini alongside Coldwind. Allocation avoids the
        // user's own combo (F13), exactly like the app does.
        var first = AssignmentsAvoidingForeign(
            Array.Empty<string>(),
            ("Coldwind Farm", @"C:\Presets\Coldwind.ini"),
            ("Old Realm", @"C:\Presets\Old.ini"));
        ShortcutSyncResult r1 = svc.SyncPresetShortcuts(Ini, first, previouslyManagedPaths: Array.Empty<string>());
        Assert.Equal(1, r1.PreservedForeign); // user's own entry kept
        Assert.Empty(r1.Conflicts);
        var previouslyManaged = first.Select(a => a.PresetPath).ToList();

        // Second sync drops Old Realm: its entry is removed, the user's stays.
        var second = first.Where(a => a.Realm == "Coldwind Farm").ToList();
        ShortcutSyncResult r2 = svc.SyncPresetShortcuts(Ini, second, previouslyManaged);
        Assert.Equal(1, r2.Removed);

        var doc = IniDocument.Parse(File.ReadAllText(Ini));
        string paths = doc.GetValue("GENERAL", "PresetShortcutPaths")!;
        Assert.Contains(@"C:\User\Mine.ini", paths); // foreign preserved
        Assert.DoesNotContain(@"C:\Presets\Old.ini", paths); // stale managed removed
        Assert.Contains(@"C:\Presets\Coldwind.ini", paths);
    }

    [Fact]
    public void Sync_IsIdempotent()
    {
        var svc = new ReshadeConfigurationService();
        var a = Assignments(("Coldwind Farm", @"C:\Presets\Coldwind.ini"));
        Assert.True(svc.SyncPresetShortcuts(Ini, a).Changed);
        Assert.False(svc.SyncPresetShortcuts(Ini, a).Changed);
    }

    // ---------- R2: foreign collisions ----------

    [Fact]
    public void ReadForeignShortcuts_ReturnsOnlyCombosOfNonManagedEntries()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "presets"));
        File.WriteAllText(Ini,
            "[GENERAL]\nPresetShortcutKeys=124,1,1,1,125,1,1,1,49,1,0,0\n" +
            "PresetShortcutPaths=C:\\User\\Mine.ini,presets\\Managed.ini,C:\\User\\Other.ini\n");

        var foreign = ReshadeConfigurationService.ReadForeignShortcuts(
            Ini, new[] { Path.Combine(_dir, "presets", "Managed.ini") });

        Assert.Equal(2, foreign.Count);
        Assert.Contains(foreign, c => ReshadeConfigurationService.SameCombo(c, Combo(F13)));
        Assert.Contains(foreign, c => ReshadeConfigurationService.SameCombo(c, Combo('1', ctrl: true, shift: false, alt: false)));
        Assert.Empty(ReshadeConfigurationService.ReadForeignShortcuts(Path.Combine(_dir, "missing.ini"), Array.Empty<string>()));
    }

    [Fact]
    public void Sync_ForeignEntryWithSameCombo_IsKept_AndConflictReported()
    {
        // Legacy state: the user's own entry already uses Ctrl+Alt+Shift+F13.
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=124,1,1,1\nPresetShortcutPaths=C:\\User\\Mine.ini\n");
        var svc = new ReshadeConfigurationService();
        var assignments = new[]
        {
            new PresetShortcutAssignment("Coldwind Farm", @"C:\Presets\Coldwind.ini", Combo(F13)),
            new PresetShortcutAssignment("MacMillan Estate", @"C:\Presets\MacMillan.ini", Combo(F13 + 1))
        };

        ShortcutSyncResult result = svc.SyncPresetShortcuts(Ini, assignments, Array.Empty<string>());

        ShortcutConflict conflict = Assert.Single(result.Conflicts);
        Assert.True(result.HasConflicts);
        Assert.Equal("Coldwind Farm", conflict.Name);
        Assert.Equal(@"C:\User\Mine.ini", conflict.ConflictingPresetPath);
        Assert.Contains("Ctrl+Alt+Shift+F13", conflict.Message);

        var entries = ReshadeConfigurationService.ReadShortcutAssignments(Ini);
        Assert.Equal(2, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => (e.Shortcut.VirtualKey, e.Shortcut.Ctrl, e.Shortcut.Shift, e.Shortcut.Alt)).Distinct().Count());
        Assert.Contains(entries, e => e.PresetPath == @"C:\User\Mine.ini" && e.Shortcut.VirtualKey == F13);
        Assert.Contains(entries, e => e.PresetPath == @"C:\Presets\MacMillan.ini" && e.Shortcut.VirtualKey == F13 + 1);
        Assert.DoesNotContain(entries, e => e.PresetPath == @"C:\Presets\Coldwind.ini");
    }

    [Fact]
    public void Sync_TwoAssignmentsWithTheSameCombo_WritesOnlyOne()
    {
        var svc = new ReshadeConfigurationService();
        var result = svc.SyncPresetShortcuts(Ini, new[]
        {
            new PresetShortcutAssignment("B Realm", @"C:\Presets\B.ini", Combo(F13)),
            new PresetShortcutAssignment("A Realm", @"C:\Presets\A.ini", Combo(F13))
        });

        Assert.Equal("B Realm", Assert.Single(result.Conflicts).Name);
        Assert.Equal(@"C:\Presets\A.ini", Value("PresetShortcutPaths"));
    }

    [Fact]
    public void Sync_PathWithComma_IsNotWritten_AndReported()
    {
        var svc = new ReshadeConfigurationService();
        var result = svc.SyncPresetShortcuts(Ini, new[]
        {
            new PresetShortcutAssignment("A Realm", @"C:\Presets\A,B.ini", Combo(F13)),
            new PresetShortcutAssignment("C Realm", @"C:\Presets\C.ini", Combo(F13 + 1))
        });

        Assert.Equal("A Realm", Assert.Single(result.Conflicts).Name);
        Assert.Equal(@"C:\Presets\C.ini", Value("PresetShortcutPaths"));
        Assert.Equal("125,1,1,1", Value("PresetShortcutKeys"));
    }

    // ---------- R3: read API ----------

    [Fact]
    public void ReadShortcutAssignments_ReturnsCombosWithAbsolutePaths_ResolvedAgainstIniFolder()
    {
        File.WriteAllText(Ini,
            "[GENERAL]\nPresetShortcutKeys=124,1,1,1,0x31,1,0,0,55,0,0,0\n" +
            "PresetShortcutPaths=presets\\Coldwind.ini,C:\\Abs\\B.ini,\n");

        var entries = ReshadeConfigurationService.ReadShortcutAssignments(Ini);

        Assert.Equal(2, entries.Count); // the entry with an empty path is not a preset
        Assert.Equal(Path.Combine(_dir, "presets", "Coldwind.ini"), entries[0].PresetPath);
        Assert.True(ReshadeConfigurationService.SameCombo(Combo(F13), entries[0].Shortcut));
        Assert.Equal(@"C:\Abs\B.ini", entries[1].PresetPath);
        Assert.Equal(0, entries[1].Shortcut.VirtualKey); // "0x31" reads as 0, exactly like ReShade's strtoul
        Assert.True(entries[1].Shortcut.Ctrl);

        Assert.Empty(ReshadeConfigurationService.ReadShortcutAssignments(Path.Combine(_dir, "missing.ini")));
    }

    // ---------- R4: write PresetPath ----------

    [Fact]
    public void WriteCurrentPreset_SetsPresetPath_PreservingEverythingElseByteForByte()
    {
        const string original = "; mine\r\n[GENERAL]\r\nEffectSearchPaths = .\\Shaders\r\nPresetPath = C:\\Presets\\Coldwind.ini\r\n\r\n[INPUT]\r\nKeyOverlay=36,0,0,0\r\n";
        File.WriteAllText(Ini, original);
        var svc = new ReshadeConfigurationService();

        Assert.True(svc.WriteCurrentPreset(Ini, @"C:\Presets\Global.ini"));

        Assert.Equal(original.Replace(@"C:\Presets\Coldwind.ini", @"C:\Presets\Global.ini"), File.ReadAllText(Ini));
        Assert.Equal(original, File.ReadAllText(Bak)); // backup of the original
        Assert.False(File.Exists(Ini + ".tmp"));
        Assert.Equal(@"C:\Presets\Global.ini", ReshadeConfigurationService.ReadCurrentPresetAbsolute(Ini));

        // Already the startup preset: no write.
        DateTime stamp = File.GetLastWriteTimeUtc(Ini);
        Assert.False(svc.WriteCurrentPreset(Ini, @"c:\presets\GLOBAL.ini"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Ini));
    }

    [Fact]
    public void WriteCurrentPreset_EquivalentRelativeValue_IsLeftAlone()
    {
        const string original = "[GENERAL]\nPresetPath=.\\Global.ini\n";
        File.WriteAllText(Ini, original);
        Assert.False(new ReshadeConfigurationService().WriteCurrentPreset(Ini, Path.Combine(_dir, "Global.ini")));
        Assert.Equal(original, File.ReadAllText(Ini));
        Assert.False(File.Exists(Bak));
    }

    [Fact]
    public void WriteCurrentPreset_AddsMissingKey_AndRequiresTheIni()
    {
        File.WriteAllText(Ini, "[GENERAL]\nEffectSearchPaths=.\\Shaders\n");
        var svc = new ReshadeConfigurationService();
        Assert.True(svc.WriteCurrentPreset(Ini, @"C:\Presets\Global.ini"));
        Assert.Equal("[GENERAL]\nEffectSearchPaths=.\\Shaders\nPresetPath=C:\\Presets\\Global.ini\n", File.ReadAllText(Ini));

        Assert.Throws<FileNotFoundException>(() => svc.WriteCurrentPreset(Path.Combine(_dir, "missing.ini"), @"C:\P\G.ini"));
    }

    // ---------- R5: pair alignment ----------

    [Fact]
    public void Sync_EmptyPathToken_DoesNotShiftPairs()
    {
        // Entry 0 has an empty path; entry 1 (F14) belongs to B.ini. Dropping the empty token
        // used to pair B.ini with entry 0's keys (F13).
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=124,1,1,1,125,1,1,1\nPresetShortcutPaths=,C:\\User\\B.ini\n");

        var before = ReshadeConfigurationService.ReadShortcutAssignments(Ini);
        Assert.Equal(F13 + 1, Assert.Single(before).Shortcut.VirtualKey);

        var svc = new ReshadeConfigurationService();
        svc.SyncPresetShortcuts(Ini, new[] { new PresetShortcutAssignment("Coldwind Farm", @"C:\Presets\Coldwind.ini", Combo(F13 + 2)) });

        Assert.Equal("124,1,1,1,125,1,1,1,126,1,1,1", Value("PresetShortcutKeys"));
        Assert.Equal(",C:\\User\\B.ini,C:\\Presets\\Coldwind.ini", Value("PresetShortcutPaths"));
        var after = ReshadeConfigurationService.ReadShortcutAssignments(Ini);
        Assert.Contains(after, e => e.PresetPath == @"C:\User\B.ini" && e.Shortcut.VirtualKey == F13 + 1);
        Assert.Contains(after, e => e.PresetPath == @"C:\Presets\Coldwind.ini" && e.Shortcut.VirtualKey == F13 + 2);
    }

    [Fact]
    public void Sync_UnparseableKeyToken_KeepsTheRawEntryUntouched()
    {
        // Skipping "abc" used to misalign every later quad and corrupt B.ini's entry.
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=abc,1,1,1,125,0,0,0\nPresetShortcutPaths=C:\\User\\A.ini,C:\\User\\B.ini\n");
        var svc = new ReshadeConfigurationService();

        var result = svc.SyncPresetShortcuts(Ini,
            new[] { new PresetShortcutAssignment("Coldwind Farm", @"C:\Presets\Coldwind.ini", Combo(F13)) },
            previouslyManagedPaths: new[] { @"C:\User\A.ini" }); // even a managed path: malformed = foreign

        Assert.Equal(2, result.PreservedForeign);
        Assert.Equal("abc,1,1,1,125,0,0,0,124,1,1,1", Value("PresetShortcutKeys"));
        Assert.Equal(@"C:\User\A.ini,C:\User\B.ini,C:\Presets\Coldwind.ini", Value("PresetShortcutPaths"));
        var entries = ReshadeConfigurationService.ReadShortcutAssignments(Ini);
        Assert.Contains(entries, e => e.PresetPath == @"C:\User\B.ini" && e.Shortcut.VirtualKey == F13 + 1 && !e.Shortcut.Ctrl);
    }

    [Fact]
    public void Sync_PreservesTrailingKeysWithoutPaths_AtTheEnd()
    {
        // One full pair, then a quad without a path and a partial quad.
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=49,1,0,0,50,1,0,0,51,1\nPresetShortcutPaths=C:\\User\\A.ini\n");
        var svc = new ReshadeConfigurationService();

        svc.SyncPresetShortcuts(Ini, new[] { new PresetShortcutAssignment("Coldwind Farm", @"C:\Presets\Coldwind.ini", Combo(F13)) });

        Assert.Equal("49,1,0,0,124,1,1,1,50,1,0,0,51,1", Value("PresetShortcutKeys"));
        Assert.Equal(@"C:\User\A.ini,C:\Presets\Coldwind.ini", Value("PresetShortcutPaths"));
        var entries = ReshadeConfigurationService.ReadShortcutAssignments(Ini);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.PresetPath == @"C:\Presets\Coldwind.ini" && e.Shortcut.VirtualKey == F13);
    }

    [Fact]
    public void Sync_PreservesTrailingPathsWithoutKeys_AtTheEnd()
    {
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=49,1,0,0,50,1\nPresetShortcutPaths=C:\\User\\A.ini,C:\\User\\Orphan.ini,C:\\User\\Orphan2.ini\n");
        var svc = new ReshadeConfigurationService();

        svc.SyncPresetShortcuts(Ini, new[] { new PresetShortcutAssignment("Coldwind Farm", @"C:\Presets\Coldwind.ini", Combo(F13)) });

        Assert.Equal("49,1,0,0,124,1,1,1,50,1", Value("PresetShortcutKeys"));
        Assert.Equal(@"C:\User\A.ini,C:\Presets\Coldwind.ini,C:\User\Orphan.ini,C:\User\Orphan2.ini", Value("PresetShortcutPaths"));
        var entries = ReshadeConfigurationService.ReadShortcutAssignments(Ini);
        Assert.Equal(2, entries.Count); // the orphans stay unpaired, Coldwind pairs with its own keys
        Assert.Contains(entries, e => e.PresetPath == @"C:\Presets\Coldwind.ini" && e.Shortcut.VirtualKey == F13);

        // And it is stable.
        Assert.False(svc.SyncPresetShortcuts(Ini, new[] { new PresetShortcutAssignment("Coldwind Farm", @"C:\Presets\Coldwind.ini", Combo(F13)) }).Changed);
    }

    // ---------- R5: ReShade's list syntax (",," is an escaped comma) ----------

    [Fact]
    public void ReadApis_SplitTheListsExactlyLikeReshade()
    {
        // ",," is a comma inside a path; ",,," is an escaped comma followed by a separator.
        // Pool combos, so only the (escaped) path decides which entry is the app's own.
        File.WriteAllText(Ini,
            "[GENERAL]\nPresetShortcutKeys=49,1,1,1,50,1,1,1,51,1,1,1\n" +
            "PresetShortcutPaths=C:\\A,,B\\x.ini,C:\\C,,,C:\\D\\z.ini\n");

        AssertReshadeLoads(
            (Combo('1'), @"C:\A,B\x.ini"),
            (Combo('2'), @"C:\C,"),
            (Combo('3'), @"C:\D\z.ini"));

        var foreign = ReshadeConfigurationService.ReadForeignShortcuts(Ini, new[] { @"C:\A,B\x.ini" });
        Assert.Equal(new[] { '2', '3' }, foreign.Select(c => (char)c.VirtualKey));
    }

    [Fact]
    public void Sync_ForeignPathWithEscapedComma_StaysPaired_AndIsWrittenBackEscaped()
    {
        // ReShade itself saves a ',' inside a path as ",,". Splitting on every ',' read this as
        // three paths: Coldwind's combo (F13) then paired with an empty path (a spurious conflict),
        // and the rewrite merged paths so ReShade switched F13 to " Presets\X.ini".
        File.WriteAllText(Ini,
            "[GENERAL]\nPresetShortcutKeys=49,1,0,0,124,1,1,1\n" +
            "PresetShortcutPaths=C:\\My,, Presets\\X.ini,C:\\P\\Coldwind.ini\n");
        AssertReshadeLoads((Combo('1', true, false, false), @"C:\My, Presets\X.ini"), (Combo(F13), @"C:\P\Coldwind.ini"));
        var reserved = ReshadeConfigurationService.ReadForeignShortcuts(Ini, new[] { @"C:\P\Coldwind.ini" });
        Assert.True(ReshadeConfigurationService.SameCombo(Combo('1', true, false, false), Assert.Single(reserved)));

        var svc = new ReshadeConfigurationService();
        var assignments = new[]
        {
            new PresetShortcutAssignment("Coldwind Farm", @"C:\P\Coldwind.ini", Combo(F13)),
            new PresetShortcutAssignment("MacMillan Estate", @"C:\P\MacMillan.ini", Combo(F13 + 1))
        };
        ShortcutSyncResult result = svc.SyncPresetShortcuts(Ini, assignments, new[] { @"C:\P\Coldwind.ini" });

        Assert.Empty(result.Conflicts);
        Assert.Equal(1, result.PreservedForeign);
        Assert.Equal("49,1,0,0,124,1,1,1,125,1,1,1", Value("PresetShortcutKeys"));
        Assert.Equal(@"C:\My,, Presets\X.ini,C:\P\Coldwind.ini,C:\P\MacMillan.ini", Value("PresetShortcutPaths"));
        AssertReshadeLoads(
            (Combo('1', true, false, false), @"C:\My, Presets\X.ini"),
            (Combo(F13), @"C:\P\Coldwind.ini"),
            (Combo(F13 + 1), @"C:\P\MacMillan.ini"));
        Assert.False(svc.SyncPresetShortcuts(Ini, assignments, new[] { @"C:\P\Coldwind.ini" }).Changed);
    }

    [Fact]
    public void Sync_TrailingEmptyPath_PlusNewEntry_NeverWritesDoubleComma()
    {
        // Re-joining "A.ini" + "" + "Coldwind.ini" with ',' gave "A.ini,,Coldwind.ini", which ReShade
        // reads as ONE path: the user's shortcut broke and F13 got no preset.
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=49,1,0,0,50,1,0,0\nPresetShortcutPaths=C:\\User\\A.ini,\n");
        AssertReshadeLoads((Combo('1', true, false, false), @"C:\User\A.ini"), (Combo('2', true, false, false), string.Empty));
        var svc = new ReshadeConfigurationService();
        var assignments = new[] { new PresetShortcutAssignment("Coldwind Farm", @"C:\Presets\Coldwind.ini", Combo(F13)) };

        ShortcutSyncResult result = svc.SyncPresetShortcuts(Ini, assignments);

        Assert.Empty(result.Conflicts);
        Assert.DoesNotContain(",,", Value("PresetShortcutPaths"));
        Assert.Equal("49,1,0,0,124,1,1,1,50,1,0,0", Value("PresetShortcutKeys"));
        Assert.Equal(@"C:\User\A.ini,C:\Presets\Coldwind.ini,", Value("PresetShortcutPaths"));
        AssertReshadeLoads(
            (Combo('1', true, false, false), @"C:\User\A.ini"),
            (Combo(F13), @"C:\Presets\Coldwind.ini"),
            (Combo('2', true, false, false), string.Empty));
        Assert.False(svc.SyncPresetShortcuts(Ini, assignments).Changed);
    }

    [Fact]
    public void Sync_TrailingEmptyKeyElement_PlusNewEntry_IsWrittenAsTheValueReshadeReads()
    {
        // "49,1,0," + new quad used to give "49,1,0,,124,..." — ReShade reads "0,124" as one
        // element, so the user's combo changed and the new quad lost its alignment.
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=49,1,0,\nPresetShortcutPaths=C:\\User\\A.ini\n");
        AssertReshadeLoads((Combo('1', true, false, false), @"C:\User\A.ini"));
        var svc = new ReshadeConfigurationService();

        svc.SyncPresetShortcuts(Ini, new[] { new PresetShortcutAssignment("Coldwind Farm", @"C:\Presets\Coldwind.ini", Combo(F13)) });

        Assert.Equal("49,1,0,0,124,1,1,1", Value("PresetShortcutKeys"));
        AssertReshadeLoads((Combo('1', true, false, false), @"C:\User\A.ini"), (Combo(F13), @"C:\Presets\Coldwind.ini"));
    }

    [Fact]
    public void Sync_LeadingEmptyUnpairedPath_IsDropped_InsteadOfMergingTwoPaths()
    {
        // No keys, paths ",Orphan.ini": the unpaired empty element would land between the new
        // entry and the orphan and merge them ("Coldwind.ini,,Orphan.ini").
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutPaths=,C:\\User\\Orphan.ini\n");
        var svc = new ReshadeConfigurationService();

        svc.SyncPresetShortcuts(Ini, new[] { new PresetShortcutAssignment("Coldwind Farm", @"C:\Presets\Coldwind.ini", Combo(F13)) });

        Assert.Equal("124,1,1,1", Value("PresetShortcutKeys"));
        Assert.Equal(@"C:\Presets\Coldwind.ini,C:\User\Orphan.ini", Value("PresetShortcutPaths"));
        AssertReshadeLoads((Combo(F13), @"C:\Presets\Coldwind.ini"));
    }

    [Fact]
    public void Sync_RemovingTheLastPath_DoesNotPairLeftoverKeysWithAnEmptyPath()
    {
        // ReShade reads a blank PresetShortcutPaths as one empty path; leaving the unpaired
        // quad "49,1,0,0" behind would turn it into a Ctrl+1 shortcut to no preset.
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=124,1,1,1,49,1,0,0\nPresetShortcutPaths=C:\\P\\Coldwind.ini\n");
        var svc = new ReshadeConfigurationService();

        ShortcutSyncResult result = svc.SyncPresetShortcuts(Ini, Array.Empty<PresetShortcutAssignment>(), new[] { @"C:\P\Coldwind.ini" });

        Assert.True(result.Changed);
        Assert.Equal(1, result.Removed);
        AssertReshadeLoads();
        Assert.Empty(ReshadeConfigurationService.ReadForeignShortcuts(Ini, Array.Empty<string>()));
    }

    [Fact]
    public void BlankPathList_WithAKeyQuad_IsReadAsReshadeDoes_AndKeptOnSync()
    {
        // ReShade pairs the quad with the single empty element of a blank list: it is a
        // (preset-less) entry whose combo the allocator must not hand out.
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=124,1,1,1\nPresetShortcutPaths=\n");
        AssertReshadeLoads((Combo(F13), string.Empty));
        Assert.True(ReshadeConfigurationService.SameCombo(Combo(F13),
            Assert.Single(ReshadeConfigurationService.ReadForeignShortcuts(Ini, Array.Empty<string>()))));

        new ReshadeConfigurationService().SyncPresetShortcuts(Ini,
            new[] { new PresetShortcutAssignment("Coldwind Farm", @"C:\Presets\Coldwind.ini", Combo(F13 + 1)) });

        Assert.Equal(@",C:\Presets\Coldwind.ini", Value("PresetShortcutPaths"));
        AssertReshadeLoads((Combo(F13), string.Empty), (Combo(F13 + 1), @"C:\Presets\Coldwind.ini"));
    }

    // ---------- R7: relative paths ----------

    [Fact]
    public void Sync_RelativeIniEntry_IsMatchedAgainstTheIniFolder()
    {
        string preset = Path.Combine(_dir, "presets", "Coldwind.ini");
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=125,1,1,1\nPresetShortcutPaths=presets\\Coldwind.ini\n");
        var svc = new ReshadeConfigurationService();

        var result = svc.SyncPresetShortcuts(Ini,
            new[] { new PresetShortcutAssignment("Coldwind Farm", preset, Combo(F13)) },
            previouslyManagedPaths: new[] { preset });

        // The relative entry is the managed one (resolved against the ini folder, not the CWD),
        // so it is replaced instead of being kept as a foreign duplicate.
        Assert.Equal(0, result.PreservedForeign);
        Assert.Equal("124,1,1,1", Value("PresetShortcutKeys"));
        Assert.Equal(preset, Value("PresetShortcutPaths"));
    }

    [Fact]
    public void ReadCurrentPreset_ReturnsPresetPath()
    {
        Assert.Equal("C:\\Presets\\Global.ini", ReshadeConfigurationService.ReadCurrentPreset(Ini));
        Assert.Null(ReshadeConfigurationService.ReadCurrentPreset(Path.Combine(_dir, "missing.ini")));
    }

    [Fact]
    public void ReadCurrentPresetAbsolute_ResolvesRelativePaths()
    {
        File.WriteAllText(Ini, "[GENERAL]\nPresetPath=..\\P\\Coldwind.ini\n");
        string? absolute = ReshadeConfigurationService.ReadCurrentPresetAbsolute(Ini);
        string expected = Path.GetFullPath(Path.Combine(_dir, "..", "P", "Coldwind.ini"));
        Assert.Equal(expected, absolute);
    }

    // ---------- R9: no-op first sync ----------

    [Fact]
    public void Sync_NothingAssigned_NoShortcutKeys_IsANoOp()
    {
        byte[] before = File.ReadAllBytes(Ini);
        var result = new ReshadeConfigurationService().SyncPresetShortcuts(Ini, Array.Empty<PresetShortcutAssignment>(), Array.Empty<string>());

        Assert.False(result.Changed);
        Assert.Equal(before, File.ReadAllBytes(Ini));
        Assert.False(File.Exists(Bak));
        Assert.Null(IniDocument.Parse(File.ReadAllText(Ini)).GetValue("GENERAL", "PresetShortcutKeys"));
    }

    [Fact]
    public void Sync_NothingAssigned_EmptyShortcutKeys_IsANoOp()
    {
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=\nPresetShortcutPaths=\n");
        byte[] before = File.ReadAllBytes(Ini);
        var result = new ReshadeConfigurationService().SyncPresetShortcuts(Ini, Array.Empty<PresetShortcutAssignment>());

        Assert.False(result.Changed);
        Assert.Equal(before, File.ReadAllBytes(Ini));
        Assert.False(File.Exists(Bak));
    }

    // ---------- R8: fidelity of real writes ----------

    [Fact]
    public void Sync_PreservesNewlinesSpacingAndBom_AndRepeatedWritesAreStable()
    {
        byte[] bom = { 0xEF, 0xBB, 0xBF };
        string text = "[GENERAL]\nPresetPath = C:\\Presets\\Global.ini\nPresetShortcutKeys=\nPresetShortcutPaths=\n\n[INPUT]\nKeyOverlay = 36,0,0,0\n";
        File.WriteAllBytes(Ini, bom.Concat(System.Text.Encoding.UTF8.GetBytes(text)).ToArray());
        var svc = new ReshadeConfigurationService();
        var a = Assignments(("Coldwind Farm", @"C:\Presets\Coldwind.ini"));
        var b = Assignments(("Coldwind Farm", @"C:\Presets\Coldwind.ini"), ("MacMillan Estate", @"C:\Presets\MacMillan.ini"));

        svc.SyncPresetShortcuts(Ini, a);
        byte[] first = File.ReadAllBytes(Ini);
        Assert.Equal(bom, first.Take(3));
        string written = System.Text.Encoding.UTF8.GetString(first, 3, first.Length - 3);
        Assert.DoesNotContain("\r", written);
        Assert.Equal(
            text.Replace("PresetShortcutKeys=\n", "PresetShortcutKeys=124,1,1,1\n")
                .Replace("PresetShortcutPaths=\n", "PresetShortcutPaths=C:\\Presets\\Coldwind.ini\n"),
            written);

        svc.SyncPresetShortcuts(Ini, b, a.Select(x => x.PresetPath));
        svc.SyncPresetShortcuts(Ini, a, b.Select(x => x.PresetPath));
        Assert.Equal(first, File.ReadAllBytes(Ini)); // back to exactly the same bytes
        Assert.False(svc.SyncPresetShortcuts(Ini, a, a.Select(x => x.PresetPath)).Changed);
    }
}
