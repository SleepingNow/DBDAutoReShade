using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// One-click ReShade setup verification. Reads back the actual ReShade.ini and
/// reports, in plain language, everything the preset switch needs: ini found
/// (and the right one), hook DLL present, every assigned preset stored with
/// exactly the combo the app sends, no combo shared by two presets, preset
/// files on disk. Pure logic over files (no game interaction); safe to run anytime.
/// "[!!]" marks a problem; "[--]" lines are information only and never stand in
/// for a failed check.
/// </summary>
public static class ReshadeDiagnosticsService
{
    private static readonly string[] HookDllNames = { "dxgi.dll", "d3d11.dll", "d3d12.dll" };

    /// <param name="config">Current configuration; every assigned name (Global + every realm with a preset) is checked.</param>
    /// <param name="realmNames">
    /// Realm names from the map database. Assigned realms not in this list are flagged (they can never be
    /// detected). Pass an empty list to skip that check.
    /// </param>
    public static (bool AllOk, string Report) BuildReport(AppConfig config, IReadOnlyList<string> realmNames)
    {
        ArgumentNullException.ThrowIfNull(config);
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
            string? dll = FindHookDll(iniDir);
            Line(dll is not null, $"ReShade hook DLL: {(dll is not null ? Path.GetFileName(dll) : "dxgi.dll/d3d11.dll/d3d12.dll NOT FOUND next to ReShade.ini")}");
        }
        else
        {
            Line(false, "ReShade hook DLL: cannot check (no ReShade.ini folder).");
        }

        // 2. ReShade.ini files near the game (wrong-ini suspicion).
        CheckIniNearGame(config, lines, Line);

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
            doc = IniDocument.Parse(ReshadeConfigurationService.ReadIniText(config.ReshadeIniPath).Text);
        }
        catch (Exception ex)
        {
            Line(false, $"ReShade.ini could not be read: {ex.Message}");
            return (false, string.Join(Environment.NewLine, lines));
        }

        string? resolvedIniDir = ReshadeConfigurationService.IniDirectory(config.ReshadeIniPath);
        string? current = doc.GetValue(ReshadeConfigurationService.GeneralSection, ReshadeConfigurationService.CurrentPresetKey);
        lines.Add($"[--] ReShade's current/startup preset: {(string.IsNullOrWhiteSpace(current) ? "(none recorded)" : current)}");

        ReshadeConfigurationService.ShortcutLayout layout = ReshadeConfigurationService.ParseShortcutLayout(doc, resolvedIniDir);
        bool exactModifiers = ReshadeConfigurationService.ForcesExactModifiers(doc);
        lines.Add($"[--] Shortcut entries stored in ReShade.ini: {layout.Entries.Count}");
        if (!exactModifiers)
        {
            lines.Add("[--] ReShade's 'Force correct modifier keys' is off ([INPUT] ForceShortcutModifiers): a shortcut " +
                      "also fires while extra modifier keys are held.");
        }

        if (layout.MalformedCount > 0 || layout.UnpairedKeyCount > 0 || layout.UnpairedPathCount > 0)
        {
            lines.Add($"[--] Malformed/unpaired shortcut data kept untouched: {layout.MalformedCount} malformed entr" +
                      $"{(layout.MalformedCount == 1 ? "y" : "ies")}, {layout.UnpairedKeyCount} unpaired key value(s), " +
                      $"{layout.UnpairedPathCount} unpaired path(s).");
        }

        // 4. Every assigned name must be stored with EXACTLY the combo the app sends.
        var knownRealms = new HashSet<string>(realmNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var expectedCombos = new List<ReshadeShortcutConfig>();
        foreach (var (name, expectedPath) in AssignedNames(config))
        {
            string label = AppConfigKeys.DisplayName(name);
            string normExpected = PresetValidationService.NormalizePresetPath(expectedPath, resolvedIniDir);

            if (name != AppConfigKeys.GlobalShortcutKey && knownRealms.Count > 0 && !knownRealms.Contains(name))
            {
                Line(false, $"{label}: not a realm in the map database — it can never be detected automatically. " +
                            "Re-assign the preset to the realm's current name.");
            }

            bool fileOk = File.Exists(expectedPath);
            Line(fileOk, $"{label}: file {(fileOk ? "found" : "MISSING")}: {expectedPath}");

            bool iniExt = string.Equals(Path.GetExtension(expectedPath), ".ini", StringComparison.OrdinalIgnoreCase);
            Line(iniExt, iniExt
                ? $"{label}: .ini extension (ReShade accepts it)."
                : $"{label}: extension is '{Path.GetExtension(expectedPath)}' — ReShade only switches to .ini files; rename it to <name>.ini and re-select it.");

            if (expectedPath.Contains(','))
            {
                Line(false, $"{label}: the path contains ',' — the app does not write such paths into ReShade.ini's comma-separated shortcut list. Rename the file or folder and re-select it.");
            }

            config.ReshadeShortcuts.TryGetValue(name, out ReshadeShortcutConfig? combo);
            if (combo is not null)
            {
                expectedCombos.Add(combo);
            }

            // Only the app's own entries (its pool combos) can be the shortcut it sends; the
            // user's own shortcuts for the same file are theirs and are kept as they are.
            var expectedPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normExpected };
            var byPath = layout.Entries
                .Where(e => ReshadeConfigurationService.IsAppOwned(e, expectedPathSet))
                .ToList();
            var usersOwn = layout.Entries
                .Where(e => e.IsWellFormed && expectedPathSet.Contains(e.NormalizedPath) &&
                            !ReshadeConfigurationService.IsAppOwned(e, expectedPathSet))
                .ToList();
            if (byPath.Count == 0)
            {
                Line(false, $"{label}: NO shortcut entry in ReShade.ini (assign the preset again, then restart DBD once).");
            }
            else if (combo is null)
            {
                Line(false, $"{label}: ReShade.ini maps it to {FormatAll(byPath)}, but the app has no shortcut stored for it " +
                            "(assign the preset again, then restart DBD once).");
            }
            else if (!byPath.Any(e => ReshadeConfigurationService.SameCombo(e.Shortcut, combo)))
            {
                Line(false, $"{label}: shortcut MISMATCH — the app sends {ReshadeConfigurationService.FormatShortcut(combo)} but " +
                            $"ReShade.ini maps this preset to {FormatAll(byPath)} (assign the preset again, then restart DBD once).");
            }
            else
            {
                Line(true, $"{label}: shortcut stored → {ReshadeConfigurationService.FormatShortcut(combo)}");
            }

            if (usersOwn.Count > 0)
            {
                lines.Add($"[--] {label}: your own shortcut {FormatAll(usersOwn)} for this preset file is kept (not used by this app).");
            }

            if (combo is not null && !exactModifiers)
            {
                // With exact modifiers off, sending the app's combo also fires every entry on the
                // same key, and ReShade switches to the first match in file order.
                var alsoTriggered = layout.Entries
                    .Where(e => e.Shortcut.VirtualKey == combo.VirtualKey &&
                                !ReshadeConfigurationService.SameCombo(e.Shortcut, combo) &&
                                !string.Equals(e.NormalizedPath, normExpected, StringComparison.OrdinalIgnoreCase))
                    .Select(e => $"{ReshadeConfigurationService.FormatShortcut(e.Shortcut)} → " +
                                 (e.NormalizedPath.Length > 0 ? e.NormalizedPath : "(no preset)"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (alsoTriggered.Count > 0)
                {
                    Line(false, $"{label}: ReShade's 'Force correct modifier keys' is off, so " +
                                $"{ReshadeConfigurationService.FormatShortcut(combo)} also triggers your shortcut " +
                                $"{string.Join(", ", alsoTriggered)} — ReShade may switch to that preset instead. Turn the " +
                                "option on in ReShade's settings, or assign the preset again so the app picks a free " +
                                "shortcut, then restart DBD once.");
                }
            }

            if (combo is not null)
            {
                var others = layout.Entries
                    .Where(e => ReshadeConfigurationService.SameCombo(e.Shortcut, combo) &&
                                !string.Equals(e.NormalizedPath, normExpected, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.NormalizedPath.Length > 0 ? e.NormalizedPath : "(no preset)")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (others.Count > 0)
                {
                    Line(false, $"{label}: {ReshadeConfigurationService.FormatShortcut(combo)} is ALSO mapped to " +
                                $"{string.Join(", ", others)} in ReShade.ini — the same combo is used by more than one " +
                                "preset, so ReShade may switch to the wrong one.");
                }
            }
        }

        // 5. The app's own settings must not hand one combo to two different presets.
        var assigned = AssignedNames(config)
            .Select(a => (a.Name, Path: PresetValidationService.NormalizePresetPath(a.Path, resolvedIniDir),
                Combo: config.ReshadeShortcuts.TryGetValue(a.Name, out ReshadeShortcutConfig? c) ? c : null))
            .Where(a => a.Combo is not null)
            .ToList();
        foreach (var group in assigned
                     .GroupBy(a => (a.Combo!.VirtualKey, a.Combo.Ctrl, a.Combo.Shift, a.Combo.Alt))
                     .Where(g => g.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            Line(false, $"{ReshadeConfigurationService.FormatShortcut(group.First().Combo!)} is stored for more than one preset " +
                        $"in the app's settings ({string.Join(", ", group.Select(a => AppConfigKeys.DisplayName(a.Name)))}); " +
                        "assign the presets again so each gets its own shortcut, then restart DBD once.");
        }

        // 6. Combos shared by several presets that the checks above do not already cover (user-owned entries).
        var sharedForeign = layout.Entries
            .Where(e => !expectedCombos.Any(c => ReshadeConfigurationService.SameCombo(c, e.Shortcut)))
            .GroupBy(e => (e.Shortcut.VirtualKey, e.Shortcut.Ctrl, e.Shortcut.Shift, e.Shortcut.Alt))
            .Where(g => g.Select(e => e.NormalizedPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToList();
        foreach (var group in sharedForeign)
        {
            lines.Add($"[--] Your own shortcut {ReshadeConfigurationService.FormatShortcut(group.First().Shortcut)} is mapped to " +
                      $"{group.Select(e => e.NormalizedPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} presets " +
                      "(not used by this app).");
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

    /// <summary>Global first (if set), then every realm with a preset in ordinal order — whether or not it is in the database.</summary>
    private static IEnumerable<(string Name, string Path)> AssignedNames(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.GlobalPresetPath))
        {
            yield return (AppConfigKeys.GlobalShortcutKey, config.GlobalPresetPath);
        }

        foreach (var kv in config.RealmPresets
                     .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            yield return (kv.Key, kv.Value);
        }
    }

    private static string FormatAll(IEnumerable<ReshadeConfigurationService.ShortcutEntry> entries) =>
        string.Join(", ", entries.Select(e => ReshadeConfigurationService.FormatShortcut(e.Shortcut)).Distinct());

    private static string? FindHookDll(string directory) =>
        HookDllNames.Select(n => Path.Combine(directory, n)).FirstOrDefault(File.Exists);

    /// <summary>
    /// Scan the same folders as discovery (<see cref="ReshadeSearchLocations"/>: the game
    /// folder and 3 parents, plus their DeadByDaylight\Binaries\Win64/WinGDK and
    /// Binaries\Win64/WinGDK subfolders, so a configured launcher exe in the install root
    /// is covered) for ReShade.ini and flag every one that is not the configured file (or
    /// the absence of any).
    /// </summary>
    private static void CheckIniNearGame(AppConfig config, List<string> lines, Action<bool, string> line)
    {
        if (string.IsNullOrWhiteSpace(config.DbdExePath))
        {
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(config.DbdExePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return;
            }

            IReadOnlyList<string> found = ReshadeSearchLocations.FindIniFiles(config.DbdExePath);

            string configured = PresetValidationService.NormalizePresetPath(config.ReshadeIniPath);
            bool IsConfigured(string path) =>
                configured.Length > 0 &&
                string.Equals(PresetValidationService.NormalizePresetPath(path), configured, StringComparison.OrdinalIgnoreCase);

            if (found.Count == 0)
            {
                line(false, "No ReShade.ini found next to the game (game folder + 3 parent folders, and their " +
                            @"DeadByDaylight\Binaries\Win64/WinGDK and Binaries\Win64/WinGDK subfolders). ReShade " +
                            "may not be installed for this game, or the configured ReShade.ini is not the one it uses.");
                return;
            }

            if (found.All(IsConfigured))
            {
                line(true, "ReShade.ini near the game is the configured file.");
                return;
            }

            line(false, found.Count > 1
                ? $"Multiple ReShade.ini files near the game ({found.Count}); ReShade uses the one next to its DLL:"
                : "A different ReShade.ini than the configured one was found near the game; ReShade uses the one next to its DLL:");
            foreach (string f in found)
            {
                string? folder = Path.GetDirectoryName(f);
                string notes = (IsConfigured(f) ? " (configured)" : string.Empty) +
                               (folder is not null && FindHookDll(folder) is not null ? " (hook DLL next to it)" : string.Empty);
                lines.Add($"       - {f}{notes}");
            }

            if (!found.Any(IsConfigured))
            {
                lines.Add($"       Configured: {config.ReshadeIniPath ?? "(not set)"} (not among them)");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[--] Could not scan for other ReShade.ini files: {ex.Message}");
        }
    }
}

/// <summary>Shared well-known config keys (avoids Core↔App magic strings).</summary>
public static class AppConfigKeys
{
    public const string GlobalShortcutKey = "__GLOBAL__";

    /// <summary>User-facing label for an assignment name: "Global/Menu" for <see cref="GlobalShortcutKey"/>, else the realm name.</summary>
    public static string DisplayName(string? name) =>
        string.Equals(name, GlobalShortcutKey, StringComparison.OrdinalIgnoreCase) ? "Global/Menu" : name ?? string.Empty;
}
