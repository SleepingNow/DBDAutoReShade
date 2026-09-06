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
/// carry the .ini extension, and look like a ReShade preset. Tolerant by
/// design — unusual but legitimate presets are not rejected.
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

        if (!string.Equals(Path.GetExtension(full), ".ini", StringComparison.OrdinalIgnoreCase))
        {
            return PresetValidationResult.Fail("ReShade presets must be .ini files.");
        }

        if (!File.Exists(full))
        {
            return PresetValidationResult.Fail("The preset file does not exist.");
        }

        try
        {
            var info = new FileInfo(full);
            if (info.Attributes.HasFlag(FileAttributes.Directory))
            {
                return PresetValidationResult.Fail("The selected preset is a folder, not a file.");
            }
        }
        catch
        {
            return PresetValidationResult.Fail("The preset file cannot be accessed.");
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

    /// <summary>Normalize a preset path for duplicate comparison.</summary>
    public static string NormalizePresetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    /// <summary>
    /// Find a *different* realm already using this preset file, if any.
    /// The Global preset is exempt: many realms intentionally fall back to it.
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
}
