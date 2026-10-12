using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>How strongly a preset file passed validation.</summary>
public enum PresetValidationStrength
{
    Invalid,
    /// <summary>INI-like file; accepted but without ReShade-specific markers.</summary>
    Weak,
    /// <summary>Contains ReShade preset markers (Techniques/TechniqueSorting).</summary>
    Strong
}

public sealed record PresetValidationResult(bool IsValid, string Message, PresetValidationStrength Strength)
{
    public static PresetValidationResult Ok(string message, PresetValidationStrength strength) =>
        new(true, message, strength);

    public static PresetValidationResult Fail(string message) =>
        new(false, message, PresetValidationStrength.Invalid);
}

/// <summary>
/// Validates user-selected ReShade preset files: must exist, be a file,
/// carry the .ini extension, be storable in ReShade.ini's comma-separated
/// shortcut list, and look like a ReShade preset. Tolerant by design —
/// unusual but legitimate presets are not rejected.
/// </summary>
public static class PresetValidationService
{
    private const long MaxProbeBytes = 256 * 1024;

    public static PresetValidationResult ValidatePresetFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PresetValidationResult.Fail("No preset file selected.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return PresetValidationResult.Fail("The preset path is not valid.");
        }

        // Before the extension check: a folder (even one named "x.ini") must get the folder message.
        if (Directory.Exists(full))
        {
            return PresetValidationResult.Fail("The selected preset is a folder, not a file.");
        }

        if (!string.Equals(Path.GetExtension(full), ".ini", StringComparison.OrdinalIgnoreCase))
        {
            return PresetValidationResult.Fail("ReShade presets must be .ini files.");
        }

        // ReShade stores PresetShortcutPaths as a comma-separated list. A literal ',' has to be
        // escaped as ",,", which any unescaped writer (or hand edit) turns into two entries that
        // shift every shortcut after it, so the app does not accept such paths at all.
        if (full.Contains(','))
        {
            return PresetValidationResult.Fail(
                "The preset path contains a comma (','). ReShade stores preset shortcut paths as a " +
                "comma-separated list, so this file cannot be used. Rename the file or folder to remove the comma.");
        }

        if (!File.Exists(full))
        {
            return PresetValidationResult.Fail("The preset file does not exist.");
        }

        string head;
        try
        {
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int len = (int)Math.Min(MaxProbeBytes, fs.Length);
            var buf = new byte[len];
            _ = fs.Read(buf, 0, len);
            head = System.Text.Encoding.UTF8.GetString(buf);
        }
        catch (Exception ex)
        {
            return PresetValidationResult.Fail($"The preset file cannot be read: {ex.Message}");
        }

        if (head.Contains("TechniqueSorting", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("Techniques=", StringComparison.OrdinalIgnoreCase))
        {
            return PresetValidationResult.Ok("Valid ReShade preset.", PresetValidationStrength.Strong);
        }

        if (head.Contains('=') || head.Contains('['))
        {
            return PresetValidationResult.Ok(
                "File looks like a preset (accepted; no ReShade technique markers found).",
                PresetValidationStrength.Weak);
        }

        return PresetValidationResult.Fail("The file does not look like a ReShade preset.");
    }

    /// <summary>
    /// Normalize a preset path for duplicate comparison. Relative paths are
    /// resolved against the process working directory; use the
    /// <see cref="NormalizePresetPath(string?, string?)"/> overload to resolve
    /// them against a specific folder (e.g. the ReShade.ini folder).
    /// </summary>
    public static string NormalizePresetPath(string? path) => NormalizePresetPath(path, null);

    /// <summary>
    /// Normalize a preset path for comparison: trims whitespace/quotes, makes it
    /// absolute and drops trailing separators. A relative path is resolved
    /// against <paramref name="baseDirectory"/> when one is given (ReShade
    /// resolves relative paths in ReShade.ini against the ini's own folder),
    /// otherwise against the process working directory.
    /// </summary>
    public static string NormalizePresetPath(string? path, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            string trimmed = path.Trim().Trim('"');
            string full = string.IsNullOrWhiteSpace(baseDirectory) || Path.IsPathFullyQualified(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(trimmed, Path.GetFullPath(baseDirectory.Trim().Trim('"')));
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    /// <summary>
    /// Find a *different* realm already using this preset file, if any.
    /// The Global preset is not considered here; use
    /// <see cref="FindDuplicateAssignment"/> to include it.
    /// </summary>
    public static string? FindDuplicateRealm(
        string presetPath,
        string forRealm,
        IReadOnlyDictionary<string, string> realmPresets)
    {
        string norm = NormalizePresetPath(presetPath);
        if (norm.Length == 0)
        {
            return null;
        }

        foreach (var kv in realmPresets)
        {
            if (string.Equals(kv.Key, forRealm, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(NormalizePresetPath(kv.Value), norm, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// Find another assignment (Global or a realm) that already uses this preset
    /// file. Each assignment gets its own ReShade shortcut entry, so the same
    /// file assigned twice yields two entries with the same path.
    /// </summary>
    /// <param name="presetPath">The file about to be assigned.</param>
    /// <param name="excludingName">
    /// The name being assigned: a realm name, or <see cref="AppConfigKeys.GlobalShortcutKey"/>
    /// when assigning the Global preset. That name itself is never reported.
    /// </param>
    /// <param name="config">Current configuration (Global + realm presets).</param>
    /// <returns>
    /// The conflicting name — <see cref="AppConfigKeys.GlobalShortcutKey"/> for the Global
    /// preset (see <see cref="AppConfigKeys.DisplayName"/>), otherwise the realm name — or null.
    /// Realms are checked in ordinal order after Global, so the result is deterministic.
    /// </returns>
    public static string? FindDuplicateAssignment(string presetPath, string? excludingName, AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string norm = NormalizePresetPath(presetPath);
        if (norm.Length == 0)
        {
            return null;
        }

        if (!string.Equals(excludingName, AppConfigKeys.GlobalShortcutKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(config.GlobalPresetPath) &&
            string.Equals(NormalizePresetPath(config.GlobalPresetPath), norm, StringComparison.OrdinalIgnoreCase))
        {
            return AppConfigKeys.GlobalShortcutKey;
        }

        foreach (var kv in config.RealmPresets.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (string.Equals(kv.Key, excludingName, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(kv.Value))
            {
                continue;
            }

            if (string.Equals(NormalizePresetPath(kv.Value), norm, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Key;
            }
        }

        return null;
    }
}
