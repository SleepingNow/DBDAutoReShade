using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class ReshadeDiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-diag-" + Guid.NewGuid().ToString("N"));

    public ReshadeDiagnosticsTests() => Directory.CreateDirectory(_dir);

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

    private AppConfig HealthyConfig(out string ini)
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

        // Real sync, exactly like the app does.
        var svc = new ReshadeConfigurationService();
        var names = new List<string> { "__GLOBAL__", "Coldwind Farm" };
        var allocated = ReshadeConfigurationService.AllocateShortcuts(names);
        var assignments = names.Select(n => new PresetShortcutAssignment(
            n, n == "__GLOBAL__" ? config.GlobalPresetPath! : config.RealmPresets[n], allocated[n])).ToList();
        svc.SyncPresetShortcuts(ini, assignments, Array.Empty<string>());
        return config;
    }

    [Fact]
    public void HealthySetup_ReportsAllOk()
    {
        AppConfig config = HealthyConfig(out _);
        var (allOk, report) = ReshadeDiagnosticsService.BuildReport(config, new[] { "Coldwind Farm" });
        Assert.True(allOk, report);
        Assert.Contains("Ctrl+Alt+Shift+F", report);
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
}
