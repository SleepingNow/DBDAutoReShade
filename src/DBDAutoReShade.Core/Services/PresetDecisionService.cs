using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>Outcome of a preset decision: which file to activate and why.</summary>
public sealed record PresetDecision(string PresetPath, bool IsGlobal, string Reason);

/// <summary>
/// Pure map → realm → preset decision logic (fully unit-testable, no I/O):
/// unconfigured realms, failed recognition and match end all resolve to Global.
/// Manual override takes precedence until the match ends.
/// </summary>
public sealed class PresetDecisionService
{
    public PresetDecision DecideForDetection(MapMatchResult match, AppConfig config)
    {
        if (match is null || !match.Success || string.IsNullOrWhiteSpace(match.RealmName))
        {
            return Global(config, "No confident map detected — Global preset.");
        }

        return DecideForRealm(match.RealmName, config,
            match.MapName is not null ? $"Detected {match.MapName} → {match.RealmName}." : $"Detected realm {match.RealmName}.");
    }

    public PresetDecision DecideForRealm(string? realmName, AppConfig config, string? context = null)
    {
        if (!string.IsNullOrWhiteSpace(realmName) &&
            config.RealmPresets.TryGetValue(realmName, out string? preset) &&
            !string.IsNullOrWhiteSpace(preset))
        {
            return new PresetDecision(preset, false, context ?? $"Realm {realmName}.");
        }

        return Global(config, string.IsNullOrWhiteSpace(realmName)
            ? "No realm identified — Global preset."
            : $"Realm '{realmName}' has no preset — Global preset.");
    }

    public PresetDecision DecideOnMatchEnd(AppConfig config) =>
        Global(config, "Match ended — Global preset.");

    /// <summary>
    /// Decide the preset for a manual override. The <see cref="PresetDecision.Reason"/> describes
    /// only the target (e.g. "MacMillan Estate."); callers add their own "Manual override:" prefix,
    /// so the reason never starts with it.
    /// </summary>
    public PresetDecision DecideManualOverride(string? realmName, AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(realmName) &&
            config.RealmPresets.TryGetValue(realmName, out string? preset) &&
            !string.IsNullOrWhiteSpace(preset))
        {
            return new PresetDecision(preset, false, $"{realmName}.");
        }

        return Global(config, string.IsNullOrWhiteSpace(realmName)
            ? "No realm selected — Global preset."
            : $"'{realmName}' has no preset — Global preset.");
    }

    private static PresetDecision Global(AppConfig config, string reason) =>
        new(config.GlobalPresetPath ?? string.Empty, true, reason);
}

/// <summary>Thin resolver used by the UI layer for display purposes.</summary>
public static class PresetResolver
{
    public static string ResolvePresetForRealm(string? realmName, AppConfig config, out bool isGlobal)
    {
        if (!string.IsNullOrWhiteSpace(realmName) &&
            config.RealmPresets.TryGetValue(realmName, out string? preset) &&
            !string.IsNullOrWhiteSpace(preset))
        {
            isGlobal = false;
            return preset;
        }

        isGlobal = true;
        return config.GlobalPresetPath ?? string.Empty;
    }
}
