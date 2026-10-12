using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class ReshadeDiagnosticsTests : IDisposable
{
    private readonly string _top = Path.Combine(Path.GetTempPath(), "dbd-diag-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Nested so that the game folders of these tests and the 3 parents the setup check scans
    /// above them stay inside this test's folder: the reports must not depend on a ReShade.ini
    /// somewhere under %TEMP% or %LOCALAPPDATA%.
    /// </summary>
    private readonly string _dir;

    public ReshadeDiagnosticsTests()
    {
        _dir = Path.Combine(_top, "a", "b", "c");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_top, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    [Fact]
    public void FormatShortcut_FullCombo()
    {
        var s = new ReshadeShortcutConfig { VirtualKey = 0x7C, Ctrl = true, Shift = true, Alt = true };
        Assert.Equal("Ctrl+Alt+Shift+F13", ReshadeConfigurationService.FormatShortcut(s));
    }

    [Fact]
    public void FormatShortcut_BareKey()
    {
        var s = new ReshadeShortcutConfig { VirtualKey = 0x77, Ctrl = false, Shift = false, Alt = false };
        Assert.Equal("F8", ReshadeConfigurationService.FormatShortcut(s));
    }

    private string WritePreset(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "[General]\nTechniques=A\n");
        return path;
    }

    private AppConfig HealthyConfig(out string ini, params string[] extraRealms)
    {
        string gameDir = Path.Combine(_dir, "game");
        Directory.CreateDirectory(gameDir);
        string exe = Path.Combine(gameDir, "DeadByDaylight-Win64-Shipping.exe");
        File.WriteAllText(exe, "x");
        File.WriteAllText(Path.Combine(gameDir, "dxgi.dll"), "x");
        ini = Path.Combine(gameDir, "ReShade.ini");
        File.WriteAllText(ini, "[GENERAL]\nPresetPath=C:\\P\\Global.ini\n");

        var config = new AppConfig
        {
            DbdExePath = exe,
            ReshadeIniPath = ini,
            GlobalPresetPath = WritePreset("Global.ini"),
            RealmPresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Coldwind Farm"] = WritePreset("Coldwind.ini")
            }
        };
        foreach (string realm in extraRealms)
        {
            config.RealmPresets[realm] = WritePreset(realm.Replace(' ', '_') + ".ini");
        }

        // Real sync, exactly like the app does: the allocated combos are stored in the config
        // (that is what the app sends) and written into ReShade.ini.
        var svc = new ReshadeConfigurationService();
        var names = new List<string> { "__GLOBAL__" };
        names.AddRange(config.RealmPresets.Keys);
        var allocated = ReshadeConfigurationService.AllocateShortcuts(names, config.ReshadeShortcuts, null);
        foreach (var kv in allocated)
        {
            config.ReshadeShortcuts[kv.Key] = kv.Value;
        }

        var assignments = names.Select(n => new PresetShortcutAssignment(
            n, n == "__GLOBAL__" ? config.GlobalPresetPath! : config.RealmPresets[n], allocated[n])).ToList();
        svc.SyncPresetShortcuts(ini, assignments, Array.Empty<string>());
        return config;
    }

    private static string[] ProblemLines(string report) =>
        report.Split(Environment.NewLine).Where(l => l.StartsWith("[!!]")).ToArray();

    [Fact]
    public void HealthySetup_ReportsAllOk()
    {
        AppConfig config = HealthyConfig(out _);
        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });
        Assert.True(allOk, report);
        Assert.Empty(ProblemLines(report));
        Assert.Contains("Ctrl+Alt+Shift+F", report);
        Assert.Contains("Global/Menu: shortcut stored", report);
    }

    [Fact]
    public void MissingIni_IsFlagged()
    {
        var config = new AppConfig { ReshadeIniPath = Path.Combine(_dir, "missing.ini") };
        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, Array.Empty<string>());
        Assert.False(allOk);
        Assert.Contains("ReShade.ini", report);
    }

    [Fact]
    public void MissingPresetFile_IsFlagged()
    {
        AppConfig config = HealthyConfig(out _);
        config.RealmPresets["Coldwind Farm"] = Path.Combine(_dir, "gone.ini");
        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });
        Assert.False(allOk);
        Assert.Contains("MISSING", report);
    }

    [Fact]
    public void MissingShortcutEntry_IsFlagged()
    {
        AppConfig config = HealthyConfig(out string ini);
        // Wipe the managed entries (simulates an ini ReShade never received).
        var doc = IniDocument.Parse(File.ReadAllText(ini));
        doc.SetValue("GENERAL", "PresetShortcutKeys", string.Empty);
        doc.SetValue("GENERAL", "PresetShortcutPaths", string.Empty);
        File.WriteAllText(ini, doc.ToString());

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });
        Assert.False(allOk);
        Assert.Contains("NO shortcut entry", report);
        Assert.DoesNotContain("Malformed/unpaired", report); // blank lists hold no stray data
    }

    private static void AppendShortcuts(string ini, string keys, string paths)
    {
        var doc = IniDocument.Parse(File.ReadAllText(ini));
        doc.SetValue("GENERAL", "PresetShortcutKeys", doc.GetValue("GENERAL", "PresetShortcutKeys") + "," + keys);
        doc.SetValue("GENERAL", "PresetShortcutPaths", doc.GetValue("GENERAL", "PresetShortcutPaths") + "," + paths);
        File.WriteAllText(ini, doc.ToString());
    }

    [Fact]
    public void AssignedPresetPathWithComma_IsAProblem()
    {
        AppConfig config = HealthyConfig(out _);
        string folder = Path.Combine(_dir, "My,Presets");
        Directory.CreateDirectory(folder);
        string preset = Path.Combine(folder, "Coldwind.ini");
        File.WriteAllText(preset, "[General]\nTechniques=A\n");
        config.RealmPresets["Coldwind Farm"] = preset;

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.False(allOk);
        Assert.Contains(ProblemLines(report), l => l.Contains("Coldwind Farm: the path contains ','"));
    }

    [Fact]
    public void MalformedAndUnpairedShortcutData_IsInformationOnly()
    {
        AppConfig config = HealthyConfig(out string ini);
        // A quad with a non-numeric key, then a partial quad; two paths, the second one unpaired.
        AppendShortcuts(ini, "abc,1,0,0,51,1", @"C:\User\Weird.ini,C:\User\Orphan.ini");

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.True(allOk, report);
        Assert.Empty(ProblemLines(report));
        Assert.Contains(
            "[--] Malformed/unpaired shortcut data kept untouched: 1 malformed entry, 2 unpaired key value(s), 1 unpaired path(s).",
            report);
    }

    [Fact]
    public void UserOwnComboSharedByTwoPresets_IsInformationOnly()
    {
        AppConfig config = HealthyConfig(out string ini);
        AppendShortcuts(ini, "49,1,0,0,49,1,0,0", @"C:\User\A.ini,C:\User\B.ini");

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.True(allOk, report);
        Assert.Empty(ProblemLines(report));
        Assert.Contains("[--] Your own shortcut Ctrl+1 is mapped to 2 presets (not used by this app).", report);
    }

    [Fact]
    public void ComboMismatch_BetweenConfigAndIni_IsFlagged()
    {
        // The app would send F20 for Coldwind, but ReShade.ini maps Coldwind to another combo.
        // The old check re-allocated combos and only compared paths, so it reported OK.
        AppConfig config = HealthyConfig(out _);
        config.ReshadeShortcuts["Coldwind Farm"] = new ReshadeShortcutConfig { VirtualKey = 0x83 };

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.False(allOk);
        string problem = Assert.Single(ProblemLines(report));
        Assert.Contains("Coldwind Farm: shortcut MISMATCH", problem);
        Assert.Contains("Ctrl+Alt+Shift+F20", problem);
    }

    [Fact]
    public void NoStoredShortcutInConfig_IsFlagged()
    {
        AppConfig config = HealthyConfig(out _);
        config.ReshadeShortcuts.Remove("Coldwind Farm");

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.False(allOk);
        Assert.Contains("the app has no shortcut stored", Assert.Single(ProblemLines(report)));
    }

    [Fact]
    public void AssignedRealmOutsideTheDatabase_IsStillChecked()
    {
        AppConfig config = HealthyConfig(out string ini, "Old Realm");
        var healthy = ReshadeDiagnosticsService.BuildReport(config, Array.Empty<string>());
        Assert.True(healthy.AllOk, healthy.Report);
        Assert.Contains("Old Realm: shortcut stored", healthy.Report); // checked although not a DB realm

        // Its entry vanishes from ReShade.ini: the old report only looked at DB realms and missed it.
        new ReshadeConfigurationService().SyncPresetShortcuts(
            ini,
            new[] { "__GLOBAL__", "Coldwind Farm" }.Select(n => new PresetShortcutAssignment(
                n, n == "__GLOBAL__" ? config.GlobalPresetPath! : config.RealmPresets[n], config.ReshadeShortcuts[n])).ToList(),
            config.RealmPresets.Values.Append(config.GlobalPresetPath!));

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });
        Assert.False(allOk);
        Assert.Contains(ProblemLines(report), l => l.Contains("Old Realm: NO shortcut entry"));
        Assert.Contains(ProblemLines(report), l => l.Contains("Old Realm: not a realm in the map database"));
    }

    [Fact]
    public void SameComboUsedByAnotherPreset_IsFlagged()
    {
        AppConfig config = HealthyConfig(out string ini);
        ReshadeShortcutConfig coldwind = config.ReshadeShortcuts["Coldwind Farm"];

        // Append a user entry that reuses Coldwind's combo for another preset.
        var doc = IniDocument.Parse(File.ReadAllText(ini));
        string keys = doc.GetValue("GENERAL", "PresetShortcutKeys")!;
        string paths = doc.GetValue("GENERAL", "PresetShortcutPaths")!;
        doc.SetValue("GENERAL", "PresetShortcutKeys", $"{keys},{coldwind.VirtualKey},1,1,1");
        doc.SetValue("GENERAL", "PresetShortcutPaths", $"{paths},C:\\User\\Mine.ini");
        File.WriteAllText(ini, doc.ToString());

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.False(allOk);
        string problem = Assert.Single(ProblemLines(report));
        Assert.Contains("Coldwind Farm", problem);
        Assert.Contains("ALSO mapped to C:\\User\\Mine.ini", problem);
    }

    [Fact]
    public void SameComboStoredForTwoPresetsInConfig_IsFlagged()
    {
        AppConfig config = HealthyConfig(out _, "MacMillan Estate");
        ReshadeShortcutConfig coldwind = config.ReshadeShortcuts["Coldwind Farm"];
        config.ReshadeShortcuts["MacMillan Estate"] = new ReshadeShortcutConfig { VirtualKey = coldwind.VirtualKey };

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm", "MacMillan Estate" });

        Assert.False(allOk);
        Assert.Contains(ProblemLines(report), l => l.Contains("is stored for more than one preset") &&
                                                   l.Contains("Coldwind Farm") && l.Contains("MacMillan Estate"));
    }

    [Fact]
    public void MultipleIniFiles_WarnsAboutWrongIni()
    {
        AppConfig config = HealthyConfig(out _);
        string deep = Path.Combine(_dir, "game", "Sub", "Deep");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "ReShade.ini"), "[GENERAL]\n");
        config.DbdExePath = Path.Combine(deep, "DeadByDaylight-Win64-Shipping.exe");
        File.WriteAllText(config.DbdExePath, "x");

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });
        Assert.False(allOk);
        Assert.Contains("Multiple ReShade.ini files", report);
    }

    [Fact]
    public void IniThreeFoldersAboveTheGame_IsFound_LikeDiscovery()
    {
        // The configured ini sits three parents above the exe: discovery (exe dir + 3 parents)
        // finds it, but the old diagnostics scan (exe dir + 2 parents) did not see the second ini.
        AppConfig config = HealthyConfig(out _);
        string deep = Path.Combine(_dir, "game", "A", "B", "C");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "ReShade.ini"), "[GENERAL]\n");
        config.DbdExePath = Path.Combine(deep, "DeadByDaylight-Win64-Shipping.exe");
        File.WriteAllText(config.DbdExePath, "x");

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.False(allOk);
        Assert.Contains("Multiple ReShade.ini files near the game (2)", report);
        Assert.Contains(Path.Combine(_dir, "game", "ReShade.ini") + " (configured) (hook DLL next to it)", report);
    }

    [Fact]
    public void SingleIniNearTheGame_ThatIsNotTheConfiguredOne_IsAProblem()
    {
        // Used to be an informational "[--] ReShade.ini files near the game: 1" line.
        AppConfig config = HealthyConfig(out string ini);
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        string otherIni = Path.Combine(elsewhere, "ReShade.ini");
        File.Copy(ini, otherIni);
        File.WriteAllText(Path.Combine(elsewhere, "dxgi.dll"), "x");
        config.ReshadeIniPath = otherIni;

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.False(allOk);
        Assert.Contains(ProblemLines(report), l => l.Contains("different ReShade.ini than the configured one"));
        Assert.Contains($"Configured: {otherIni} (not among them)", report);
    }

    [Fact]
    public void NoIniNearTheGame_IsAProblem()
    {
        AppConfig config = HealthyConfig(out _);
        string otherGame = Path.Combine(_dir, "other-game");
        Directory.CreateDirectory(otherGame);
        config.DbdExePath = Path.Combine(otherGame, "DeadByDaylight-Win64-Shipping.exe");
        File.WriteAllText(config.DbdExePath, "x");

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.False(allOk);
        Assert.Contains(ProblemLines(report), l => l.Contains("No ReShade.ini found next to the game"));
    }
}
