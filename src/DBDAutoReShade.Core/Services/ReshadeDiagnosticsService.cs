using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// One-click ReShade setup verification. Reads back the actual ReShade.ini and
/// reports, in plain language, everything the preset switch needs: ini found,
/// hook DLL present, managed shortcuts really stored, preset files on disk.
/// Pure logic over files (no game interaction); safe to run anytime.
/// </summary>
public static class ReshadeDiagnosticsService
{
    private static readonly string[] HookDllNames = { "dxgi.dll", "d3d11.dll", "d3d12.dll" };

    public static (bool AllOk, string Report) BuildReport(AppConfig config, IReadOnlyList<string> realmNames)
    {
        var lines = new List<string>();
        bool ok = true;

        void Line(bool good, string text)
        {
            lines.Add($"{(good ? "[OK] " : "[!!] ")}{text}");
            if (!good)
            {
                ok = false;
            }
        }

        lines.Add("DBD Auto ReShade — ReShade setup check");
        lines.Add($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        lines.Add(string.Empty);

        // 1. Paths.
        Line(!string.IsNullOrWhiteSpace(config.DbdExePath) && File.Exists(config.DbdExePath ?? string.Empty),
            $"DBD executable: {config.DbdExePath ?? "(not set)"}");
        Line(!string.IsNullOrWhiteSpace(config.ReshadeIniPath) && File.Exists(config.ReshadeIniPath ?? string.Empty),
            $"ReShade.ini: {config.ReshadeIniPath ?? "(not set)"}");

        string? iniDir = null;
        try
        {
            iniDir = Path.GetDirectoryName(config.ReshadeIniPath ?? string.Empty);
        }
        catch
        {
            // Ignore; reported below.
        }

        if (!string.IsNullOrEmpty(iniDir) && Directory.Exists(iniDir))
        {
            string? dll = HookDllNames.Select(n => Path.Combine(iniDir, n)).FirstOrDefault(File.Exists);
            Line(dll is not null, $"ReShade hook DLL: {(dll is not null ? Path.GetFileName(dll) : "dxgi.dll/d3d11.dll/d3d12.dll NOT FOUND next to ReShade.ini")}");
        }
        else
        {
            Line(false, "ReShade hook DLL: cannot check (no ReShade.ini folder).");
        }

        // 2. Other ReShade.ini candidates (wrong-ini suspicion).
        if (!string.IsNullOrWhiteSpace(config.DbdExePath))
        {
            try
            {
                string? dir = Path.GetDirectoryName(config.DbdExePath);
                var found = new List<string>();
                for (int level = 0; level < 3 && dir is not null && found.Count < 10; level++)
                {
                    string candidate = Path.Combine(dir, "ReShade.ini");
                    if (File.Exists(candidate))
                    {
                        found.Add(candidate);
                    }

                    dir = Path.GetDirectoryName(dir);
                }

                if (found.Count > 1)
                {
                    Line(false, $"Multiple ReShade.ini files near the game ({found.Count}); ReShade uses the one next to its DLL:");
                    foreach (string f in found)
                    {
                        lines.Add($"       - {f}");
                    }
                }
                else
                {
                    lines.Add($"[--] ReShade.ini files near the game: {found.Count}");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"[--] Could not scan for other ReShade.ini files: {ex.Message}");
            }
        }

        lines.Add(string.Empty);

        if (string.IsNullOrWhiteSpace(config.ReshadeIniPath) || !File.Exists(config.ReshadeIniPath))
        {
            lines.Add("Fix: set the correct ReShade.ini under Settings (Browse), or reinstall ReShade");
            lines.Add("for Dead by Daylight, then restart this app.");
            return (false, string.Join(Environment.NewLine, lines));
        }

        // 3. Parse the ini back.
        IniDocument doc;
        try
        {
            doc = IniDocument.Parse(File.ReadAllText(config.ReshadeIniPath));
        }
        catch (Exception ex)
        {
            Line(false, $"ReShade.ini could not be read: {ex.Message}");
            return (false, string.Join(Environment.NewLine, lines));
        }

        string? current = doc.GetValue(ReshadeConfigurationService.GeneralSection, ReshadeConfigurationService.CurrentPresetKey);
        lines.Add($"[--] ReShade reports current preset: {current ?? "(none recorded)"}");

        var pairs = ReshadeConfigurationService.ReadShortcutPairs(doc);
        lines.Add($"[--] Shortcut entries stored in ReShade.ini: {pairs.Count}");

        // 4. Expected managed entries (rebuilt exactly like the sync does).
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.GlobalPresetPath))
        {
            names.Add(AppConfigKeys.GlobalShortcutKey);
        }

        foreach (string realm in realmNames)
        {
            if (config.RealmPresets.TryGetValue(realm, out string? p) && !string.IsNullOrWhiteSpace(p))
            {
                names.Add(realm);
            }
        }

        Dictionary<string, ReshadeShortcutConfig> allocated;
        try
        {
            allocated = ReshadeConfigurationService.AllocateShortcuts(names);
        }
        catch (Exception ex)
        {
            Line(false, $"Could not allocate shortcuts: {ex.Message}");
            return (false, string.Join(Environment.NewLine, lines));
        }

        foreach (var kv in allocated.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string label = kv.Key == AppConfigKeys.GlobalShortcutKey ? "Global/Menu" : kv.Key;
            string expectedPath = kv.Key == AppConfigKeys.GlobalShortcutKey
                ? config.GlobalPresetPath ?? string.Empty
                : config.RealmPresets[kv.Key];
            string normExpected = PresetValidationService.NormalizePresetPath(expectedPath);

            bool fileOk = File.Exists(expectedPath);
            Line(fileOk, $"{label}: file {(fileOk ? "found" : "MISSING")}: {expectedPath}");

            bool iniExt = string.Equals(Path.GetExtension(expectedPath), ".ini", StringComparison.OrdinalIgnoreCase);
            Line(iniExt, iniExt
                ? $"{label}: .ini extension (ReShade accepts it)."
                : $"{label}: extension is '{Path.GetExtension(expectedPath)}' — ReShade only switches to .ini files; rename it to <name>.ini and re-select it.");

            var stored = pairs.FirstOrDefault(p =>
                string.Equals(PresetValidationService.NormalizePresetPath(p.PresetPath), normExpected, StringComparison.OrdinalIgnoreCase));
            if (stored is null)
            {
                Line(false, $"{label}: NO shortcut entry in ReShade.ini (assign the preset again, then restart DBD once).");
            }
            else
            {
                string combo = stored.KeyData.Length == 4
                    ? ReshadeConfigurationService.FormatShortcut(new ReshadeShortcutConfig
                    {
                        VirtualKey = stored.KeyData[0],
                        Ctrl = stored.KeyData[1] != 0,
                        Shift = stored.KeyData[2] != 0,
                        Alt = stored.KeyData[3] != 0
                    })
                    : string.Join(",", stored.KeyData);
                Line(true, $"{label}: shortcut stored → {combo}");
            }
        }

        lines.Add(string.Empty);
        lines.Add(ok
            ? "Result: setup looks correct. If switching still fails, the keystrokes are not " +
              "reaching ReShade: keep DBD focused, run game and app at the same privilege " +
              "level, and confirm ReShade's overlay (Home key) shows the expected preset."
            : "Result: problems found above — fix them, then restart Dead by Daylight once " +
              "so ReShade loads the corrected shortcuts.");
        return (ok, string.Join(Environment.NewLine, lines));
    }
}

/// <summary>Shared well-known config keys (avoids Core↔App magic strings).</summary>
public static class AppConfigKeys
{
    public const string GlobalShortcutKey = "__GLOBAL__";
}
