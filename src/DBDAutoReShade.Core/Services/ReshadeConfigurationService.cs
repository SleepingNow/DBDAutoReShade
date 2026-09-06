using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>One managed preset shortcut: realm → (preset file, key combo).</summary>
public sealed record PresetShortcutAssignment(string Realm, string PresetPath, ReshadeShortcutConfig Shortcut);

public sealed record ShortcutSyncResult(bool Changed, int Added, int Removed, int PreservedForeign);

/// <summary>
/// Manages this app's preset shortcuts inside ReShade.ini.
///
/// Verified against the ReShade source (runtime.cpp) and official docs:
/// <c>[GENERAL] PresetPath</c> holds the current preset, while
/// <c>PresetShortcutKeys</c> (flat int list, 4 per shortcut: key,ctrl,shift,alt)
/// plus <c>PresetShortcutPaths</c> define per-preset shortcut switches.
/// Pressing a shortcut switches presets in-memory; ReShade does NOT poll
/// ReShade.ini for external edits, so activation is done by simulating the
/// managed shortcut (see Desktop KeySimulator), not by rewriting PresetPath.
///
/// Foreign shortcut entries (paths this app does not manage) are preserved.
/// </summary>
public sealed class ReshadeConfigurationService
{
    public const string GeneralSection = "GENERAL";
    public const string KeysKey = "PresetShortcutKeys";
    public const string PathsKey = "PresetShortcutPaths";
    public const string CurrentPresetKey = "PresetPath";

    /// <summary>First managed virtual key (VK_F13). F13–F24 need Ctrl+Alt+Shift and never collide with DBD bindings.</summary>
    public const int FirstFunctionKey = 0x7C; // VK_F13
    public const int LastFunctionKey = 0x87; // VK_F24

    /// <summary>Human-readable key combo, e.g. "Ctrl+Alt+Shift+F13".</summary>
    public static string FormatShortcut(ReshadeShortcutConfig shortcut)
    {
        var parts = new List<string>();
        if (shortcut.Ctrl)
        {
            parts.Add("Ctrl");
        }

        if (shortcut.Alt)
        {
            parts.Add("Alt");
        }

        if (shortcut.Shift)
        {
            parts.Add("Shift");
        }

        parts.Add(KeyName(shortcut.VirtualKey));
        return string.Join("+", parts);
    }

    internal static string KeyName(int virtualKey)
    {
        if (virtualKey >= 0x70 && virtualKey <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        if (virtualKey >= 'A' && virtualKey <= 'Z')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey >= '0' && virtualKey <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        return $"VK 0x{virtualKey:X2}";
    }

    /// <summary>
    /// Deterministic, collision-free shortcut allocation for realms with presets.
    /// Realms sorted ordinally → F13..F24, then Ctrl+Alt+Shift+0..9.
    /// </summary>
    public static Dictionary<string, ReshadeShortcutConfig> AllocateShortcuts(IEnumerable<string> realmsWithPresets)
    {
        var sorted = realmsWithPresets
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        var result = new Dictionary<string, ReshadeShortcutConfig>(StringComparer.OrdinalIgnoreCase);
        var usedVk = new HashSet<int>();
        int fn = FirstFunctionKey;
        int digit = '0';

        foreach (string realm in sorted)
        {
            int vk;
            if (fn <= LastFunctionKey)
            {
                vk = fn++;
            }
            else if (digit <= '9')
            {
                vk = digit++;
            }
            else
            {
                throw new InvalidOperationException("More realms with presets than available shortcut keys.");
            }

            if (!usedVk.Add(vk))
            {
                throw new InvalidOperationException("Shortcut collision during allocation.");
            }

            result[realm] = new ReshadeShortcutConfig { VirtualKey = vk, Ctrl = true, Shift = true, Alt = true };
        }

        return result;
    }

    public static string? ReadCurrentPreset(string iniPath)
    {
        if (!File.Exists(iniPath))
        {
            return null;
        }

        string content = File.ReadAllText(iniPath);
        return IniDocument.Parse(content).GetValue(GeneralSection, CurrentPresetKey);
    }

    /// <summary>
    /// Reads the current preset and resolves it to an absolute path. ReShade
    /// stores PresetPath relative to its own folder, so relative values are
    /// resolved against the ini directory before comparing.
    /// </summary>
    public static string? ReadCurrentPresetAbsolute(string iniPath)
    {
        string? raw;
        try
        {
            raw = ReadCurrentPreset(iniPath);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            string full = raw.Trim().Trim('"');
            if (!Path.IsPathRooted(full))
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(iniPath));
                full = dir is null ? full : Path.Combine(dir, full);
            }

            return Path.GetFullPath(full);
        }
        catch
        {
            return raw;
        }
    }

    /// <summary>
    /// Rewrite the managed entries of PresetShortcutKeys/Paths to match
    /// <paramref name="assignments"/>. Creates a .bak backup on first change.
    /// Entries whose paths were previously managed by this app but are no
    /// longer assigned are removed; entries this app never managed (foreign,
    /// i.e. the user's own shortcuts) are always preserved.
    /// </summary>
    public ShortcutSyncResult SyncPresetShortcuts(
        string iniPath,
        IReadOnlyCollection<PresetShortcutAssignment> assignments,
        IEnumerable<string>? previouslyManagedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        if (!File.Exists(iniPath))
        {
            throw new FileNotFoundException("ReShade.ini not found.", iniPath);
        }

        var managedPaths = new HashSet<string>(
            assignments.Select(a => PresetValidationService.NormalizePresetPath(a.PresetPath)),
            StringComparer.OrdinalIgnoreCase);

        var previouslyManaged = new HashSet<string>(
            (previouslyManagedPaths ?? Enumerable.Empty<string>()).Select(PresetValidationService.NormalizePresetPath),
            StringComparer.OrdinalIgnoreCase);

        var doc = IniDocument.Parse(File.ReadAllText(iniPath));
        var existing = ReadShortcutPairs(doc);

        // Keep foreign entries (shortcuts this app never managed).
        var foreign = existing
            .Where(p =>
            {
                string norm = PresetValidationService.NormalizePresetPath(p.PresetPath);
                return !managedPaths.Contains(norm) && !previouslyManaged.Contains(norm);
            })
            .ToList();

        int removed = existing.Count(p =>
        {
            string norm = PresetValidationService.NormalizePresetPath(p.PresetPath);
            return !managedPaths.Contains(norm) && previouslyManaged.Contains(norm);
        });

        var ordered = assignments
            .OrderBy(a => a.Realm, StringComparer.Ordinal)
            .Select(a => (KeyData: new[] { a.Shortcut.VirtualKey, Bool(a.Shortcut.Ctrl), Bool(a.Shortcut.Shift), Bool(a.Shortcut.Alt) }, PresetPath: a.PresetPath))
            .ToList();

        var mergedKeys = new List<int>();
        var mergedPaths = new List<string>();
        foreach (var f in foreign)
        {
            mergedKeys.AddRange(f.KeyData);
            mergedPaths.Add(f.PresetPath);
        }

        foreach (var o in ordered)
        {
            mergedKeys.AddRange(o.KeyData);
            mergedPaths.Add(o.PresetPath);
        }

        string newKeys = string.Join(",", mergedKeys);
        string newPaths = string.Join(",", mergedPaths);

        string? oldKeys = doc.GetValue(GeneralSection, KeysKey) ?? string.Empty;
        string? oldPaths = doc.GetValue(GeneralSection, PathsKey) ?? string.Empty;

        bool changed = false;
        changed |= doc.SetValue(GeneralSection, KeysKey, newKeys);
        changed |= doc.SetValue(GeneralSection, PathsKey, newPaths);

        int added = ordered.Count(o => !existing.Any(e =>
            string.Equals(PresetValidationService.NormalizePresetPath(e.PresetPath),
                PresetValidationService.NormalizePresetPath(o.PresetPath), StringComparison.OrdinalIgnoreCase)));

        _ = oldKeys;
        _ = oldPaths;

        if (changed)
        {
            BackupOnce(iniPath);
            string tmp = iniPath + ".tmp";
            File.WriteAllText(tmp, doc.ToString());
            File.Move(tmp, iniPath, overwrite: true);
        }

        return new ShortcutSyncResult(changed, added, removed, foreign.Count);
    }

    private static void BackupOnce(string iniPath)
    {
        try
        {
            string bak = iniPath + ".dbdautoreshade.bak";
            if (!File.Exists(bak))
            {
                File.Copy(iniPath, bak);
            }
        }
        catch
        {
            // Backup is best-effort.
        }
    }

    private static int Bool(bool b) => b ? 1 : 0;

    internal sealed record ShortcutPair(int[] KeyData, string PresetPath);

    internal static List<ShortcutPair> ReadShortcutPairs(IniDocument doc)
    {
        var result = new List<ShortcutPair>();
        int[] keys = ParseIntList(doc.GetValue(GeneralSection, KeysKey));
        string[] paths = (doc.GetValue(GeneralSection, PathsKey) ?? string.Empty)
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        int count = Math.Min(paths.Length, keys.Length / 4);
        for (int i = 0; i < count; i++)
        {
            result.Add(new ShortcutPair(keys.Skip(i * 4).Take(4).ToArray(), paths[i]));
        }

        return result;
    }

    private static int[] ParseIntList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<int>();
        }

        var list = new List<int>();
        foreach (string part in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int n))
            {
                list.Add(n);
            }
        }

        return list.ToArray();
    }
}
