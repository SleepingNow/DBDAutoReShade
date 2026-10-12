using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// K1: discovery and the setup check look for ReShade.ini in the same places,
/// also when the configured DBD executable is the launcher in the install root
/// (&lt;root&gt;\DeadByDaylight.exe) and ReShade sits in DeadByDaylight\Binaries\Win64.
/// Plus the setup check's view of the user's own shortcuts (F1) and of
/// ReShade's ForceShortcutModifiers (F5).
/// </summary>
public sealed class ReshadeSearchLocationsTests : IDisposable
{
    private const string Pack = "STEAXS FILTERS PACK - CHARLIE";

    private readonly string _top = Path.Combine(Path.GetTempPath(), "dbd-search-" + Guid.NewGuid().ToString("N"));

    /// <summary>The install root, deep enough that every scanned parent is inside this test's folder.</summary>
    private readonly string _root;

    public ReshadeSearchLocationsTests()
    {
        _root = Path.Combine(_top, "a", "b", "c", "Dead by Daylight");
        Directory.CreateDirectory(_root);
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

    private string Launcher => Path.Combine(_root, "DeadByDaylight.exe");

    private string Win64 => Path.Combine(_root, "DeadByDaylight", "Binaries", "Win64");

    private string ShippingExe => Path.Combine(Win64, "DeadByDaylight-Win64-Shipping.exe");

    private static void Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string[] ProblemLines(string report) =>
        report.Split(Environment.NewLine).Where(l => l.StartsWith("[!!]")).ToArray();

    // ---------- CandidateDirectories ----------

    [Fact]
    public void CandidateDirectories_LauncherInTheInstallRoot_IncludeTheGamesBinaryFolders_NearestFirst()
    {
        var dirs = ReshadeSearchLocations.CandidateDirectories(Launcher);

        string c = Path.GetDirectoryName(_root)!;
        Assert.Equal(new[]
        {
            _root,
            Path.Combine(_root, "DeadByDaylight", "Binaries", "Win64"),
            Path.Combine(_root, "DeadByDaylight", "Binaries", "WinGDK"),
            Path.Combine(_root, "Binaries", "Win64"),
            Path.Combine(_root, "Binaries", "WinGDK"),
            c,
            Path.Combine(c, "DeadByDaylight", "Binaries", "Win64"),
        }, dirs.Take(7));
        Assert.Equal(4 * 5, dirs.Count); // 4 folders × (itself + 4 subfolders), no duplicates here
        Assert.Equal(dirs, ReshadeSearchLocations.CandidateDirectories(Launcher)); // deterministic
    }

    [Fact]
    public void CandidateDirectories_ShippingExe_StartWithTheExeFolder_WithoutDuplicates()
    {
        var dirs = ReshadeSearchLocations.CandidateDirectories(ShippingExe);

        Assert.Equal(Win64, dirs[0]);
        Assert.Equal(dirs.Count, dirs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Single(dirs, d => string.Equals(d, Win64, StringComparison.OrdinalIgnoreCase)); // reached again from the root
        Assert.Contains(Path.Combine(_root, "DeadByDaylight", "Binaries", "WinGDK"), dirs);
        Assert.Contains(_root, dirs); // exe folder + 3 parents, as before
        Assert.DoesNotContain(Path.GetDirectoryName(_root)!, dirs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\bad\0path\\DeadByDaylight.exe")]
    public void CandidateDirectories_BlankOrInvalidPath_IsEmpty(string? exe) =>
        Assert.Empty(ReshadeSearchLocations.CandidateDirectories(exe));

    // ---------- Discovery ----------

    [Theory]
    [InlineData("Win64")]
    [InlineData("WinGDK")]
    public void Discovery_LauncherExe_FindsTheIniInTheGamesBinaryFolder(string platform)
    {
        string gameDir = Path.Combine(_root, "DeadByDaylight", "Binaries", platform);
        Touch(Launcher);
        Touch(Path.Combine(gameDir, "ReShade.ini"), "[GENERAL]\r\n");
        Touch(Path.Combine(gameDir, "dxgi.dll"));

        ReshadeLocation? loc = ReshadeDiscoveryService.Discover(Launcher);

        Assert.NotNull(loc);
        Assert.Equal(Path.Combine(gameDir, "ReShade.ini"), loc.IniPath);
        Assert.Equal(Path.Combine(gameDir, "dxgi.dll"), loc.HookDllPath);
        Assert.Equal(gameDir, loc.GameDirectory);
    }

    [Fact]
    public void Discovery_ShippingExe_StillPrefersTheIniNextToTheExe()
    {
        Touch(ShippingExe);
        Touch(Path.Combine(Win64, "ReShade.ini"), "[GENERAL]\r\n");
        Touch(Path.Combine(_root, "ReShade.ini"), "[GENERAL]\r\n"); // a stray one in the install root

        Assert.Equal(Path.Combine(Win64, "ReShade.ini"), ReshadeDiscoveryService.Discover(ShippingExe)?.IniPath);

        // Nested so the exe folder and all 3 scanned parents stay inside this test's folder: the
        // result must not depend on a ReShade.ini somewhere under %TEMP% or %LOCALAPPDATA%.
        Assert.Null(ReshadeDiscoveryService.Discover(Path.Combine(_top, "elsewhere", "x", "y", "z", "DeadByDaylight.exe")));
    }

    [Fact]
    public void Discovery_LauncherExe_PrefersTheIniBesideTheHookDll_OverAStrayOneBesideTheLauncher()
    {
        Touch(Launcher);
        Touch(Path.Combine(_root, "ReShade.ini"), "[GENERAL]\r\n"); // stray copy, no ReShade DLL here
        Touch(Path.Combine(Win64, "ReShade.ini"), "[GENERAL]\r\n");
        Touch(Path.Combine(Win64, "dxgi.dll"));

        ReshadeLocation? loc = ReshadeDiscoveryService.Discover(Launcher);

        Assert.Equal(Path.Combine(Win64, "ReShade.ini"), loc?.IniPath);
        Assert.Equal(Path.Combine(Win64, "dxgi.dll"), loc?.HookDllPath);
    }

    [Fact]
    public void Discovery_NoHookDllAnywhere_FallsBackToTheNearestIni()
    {
        Touch(Launcher);
        Touch(Path.Combine(_root, "ReShade.ini"), "[GENERAL]\r\n");
        Touch(Path.Combine(Win64, "ReShade.ini"), "[GENERAL]\r\n");

        ReshadeLocation? loc = ReshadeDiscoveryService.Discover(Launcher);

        Assert.Equal(Path.Combine(_root, "ReShade.ini"), loc?.IniPath);
        Assert.Null(loc?.HookDllPath);
    }

    // ---------- Setup check (diagnostics) ----------

    /// <summary>
    /// A healthy setup synced like the app does: ReShade (ini + dxgi.dll) in Binaries\Win64,
    /// Global and Coldwind presets in the pack folder next to it, the given DBD executable.
    /// </summary>
    private AppConfig HealthySetup(string exePath, out string ini, string extraIni = "")
    {
        Touch(exePath);
        Touch(Path.Combine(Win64, "dxgi.dll"));
        ini = Path.Combine(Win64, "ReShade.ini");
        Touch(ini, "[GENERAL]\r\nPresetPath=.\\" + Pack + "\\Global.ini\r\n" + extraIni);
        string global = Path.Combine(Win64, Pack, "Global.ini");
        string coldwind = Path.Combine(Win64, Pack, "Coldwind.ini");
        Touch(global, "[General]\r\nTechniques=A\r\n");
        Touch(coldwind, "[General]\r\nTechniques=A\r\n");

        var config = new AppConfig
        {
            DbdExePath = exePath,
            ReshadeIniPath = ini,
            GlobalPresetPath = global,
            RealmPresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Coldwind Farm"] = coldwind }
        };
        var names = new[] { AppConfigKeys.GlobalShortcutKey, "Coldwind Farm" };
        var allocated = ReshadeConfigurationService.AllocateShortcuts(names, null, null);
        foreach (var kv in allocated)
        {
            config.ReshadeShortcuts[kv.Key] = kv.Value;
        }

        new ReshadeConfigurationService().SyncPresetShortcuts(ini, names.Select(n => new PresetShortcutAssignment(
            n, n == AppConfigKeys.GlobalShortcutKey ? global : coldwind, allocated[n])).ToList(), Array.Empty<string>());
        return config;
    }

    private static void PrependShortcut(string ini, string quad, string path)
    {
        var doc = IniDocument.Parse(File.ReadAllText(ini));
        string keys = doc.GetValue("GENERAL", "PresetShortcutKeys") ?? string.Empty;
        string paths = doc.GetValue("GENERAL", "PresetShortcutPaths") ?? string.Empty;
        doc.SetValue("GENERAL", "PresetShortcutKeys", quad + (keys.Length > 0 ? "," + keys : string.Empty));
        doc.SetValue("GENERAL", "PresetShortcutPaths", path + (paths.Length > 0 ? "," + paths : string.Empty));
        File.WriteAllText(ini, doc.ToString());
    }

    [Fact]
    public void Diagnostics_LauncherExe_FindsTheConfiguredIni_NoFalseProblem()
    {
        AppConfig config = HealthySetup(Launcher, out string ini);

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.True(allOk, report);
        Assert.Empty(ProblemLines(report));
        Assert.Contains("[OK] ReShade.ini near the game is the configured file.", report);
        Assert.DoesNotContain("No ReShade.ini found", report);
        Assert.True(File.Exists(ini));
    }

    [Fact]
    public void Diagnostics_LauncherExe_StillFlagsASecondDifferentIni()
    {
        AppConfig config = HealthySetup(Launcher, out string ini);
        string stray = Path.Combine(_root, "ReShade.ini"); // e.g. ReShade once installed on the launcher
        Touch(stray, "[GENERAL]\r\n");

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.False(allOk);
        Assert.Contains(ProblemLines(report), l => l.Contains("Multiple ReShade.ini files near the game (2)"));
        Assert.Contains($"       - {stray}", report);
        Assert.Contains($"       - {ini} (configured) (hook DLL next to it)", report);
    }

    [Fact]
    public void Diagnostics_ShippingExe_Unchanged()
    {
        AppConfig config = HealthySetup(ShippingExe, out _);

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.True(allOk, report);
        Assert.Contains("[OK] ReShade.ini near the game is the configured file.", report);
    }

    [Fact]
    public void Diagnostics_UsersOwnShortcutForAnAssignedFile_IsNotTakenForTheAppsEntry()
    {
        AppConfig config = HealthySetup(Launcher, out string ini);
        PrependShortcut(ini, "120,1,1,0", ".\\" + Pack + "\\Coldwind.ini"); // the user's own Ctrl+Shift+F9

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });
        Assert.True(allOk, report);
        Assert.Contains("[OK] Coldwind Farm: shortcut stored", report);
        Assert.Contains("[--] Coldwind Farm: your own shortcut Ctrl+Shift+F9 for this preset file is kept (not used by this app).", report);

        // The app's own entry is gone; only the user's is left for that file. That is a
        // missing entry, not "the app sends X but ReShade.ini maps this preset to Ctrl+Shift+F9".
        new ReshadeConfigurationService().SyncPresetShortcuts(
            ini,
            new[] { new PresetShortcutAssignment(AppConfigKeys.GlobalShortcutKey, config.GlobalPresetPath!, config.ReshadeShortcuts[AppConfigKeys.GlobalShortcutKey]) },
            config.RealmPresets.Values);
        Assert.StartsWith("120,1,1,0,", IniDocument.Parse(File.ReadAllText(ini)).GetValue("GENERAL", "PresetShortcutKeys"));

        (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });
        Assert.False(allOk);
        string problem = Assert.Single(ProblemLines(report));
        Assert.Contains("Coldwind Farm: NO shortcut entry in ReShade.ini", problem);
        Assert.DoesNotContain("MISMATCH", report);
    }

    [Theory]
    [InlineData("1", false)]
    [InlineData("0", true)]
    public void Diagnostics_ModifiersNotForced_FlagsAUserShortcutOnTheSameKey(string force, bool flagged)
    {
        AppConfig config = HealthySetup(ShippingExe, out string ini, "[INPUT]\r\nForceShortcutModifiers=" + force + "\r\n");
        ReshadeShortcutConfig coldwind = config.ReshadeShortcuts["Coldwind Farm"];
        PrependShortcut(ini, $"{coldwind.VirtualKey},1,0,0", @"C:\User\Mine.ini"); // Ctrl+<Coldwind's key>

        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });

        Assert.Equal(!flagged, allOk);
        Assert.Equal(flagged, report.Contains("[--] ReShade's 'Force correct modifier keys' is off", StringComparison.Ordinal));
        string key = ReshadeConfigurationService.FormatShortcut(coldwind);
        string userCombo = "Ctrl+" + key.Split('+').Last();
        if (flagged)
        {
            string problem = Assert.Single(ProblemLines(report));
            Assert.Contains($"Coldwind Farm: ReShade's 'Force correct modifier keys' is off, so {key} also triggers " +
                            $"your shortcut {userCombo} → C:\\User\\Mine.ini", problem);
        }
        else
        {
            Assert.Empty(ProblemLines(report));
        }
    }
}
