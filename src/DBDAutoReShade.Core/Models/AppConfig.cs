using System.Text.Json.Serialization;

namespace DBDAutoReShade.Core.Models;

/// <summary>Persisted user configuration (per-user AppData location).</summary>
public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("setupCompleted")]
    public bool SetupCompleted { get; set; }

    [JsonPropertyName("dbdExePath")]
    public string? DbdExePath { get; set; }

    [JsonPropertyName("reshadeIniPath")]
    public string? ReshadeIniPath { get; set; }

    [JsonPropertyName("globalPresetPath")]
    public string? GlobalPresetPath { get; set; }

    /// <summary>Realm name → ReShade preset .ini path. Case-insensitive.</summary>
    [JsonPropertyName("realmPresets")]
    public Dictionary<string, string> RealmPresets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("detectionHotkey")]
    public DetectionHotkeyConfig DetectionHotkey { get; set; } = new();

    /// <summary>
    /// Realm name → internal ReShade shortcut managed by this app.
    /// Deterministic, collision-free, persisted.
    /// </summary>
    [JsonPropertyName("reshadeShortcuts")]
    public Dictionary<string, ReshadeShortcutConfig> ReshadeShortcuts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Preset paths this app wrote into ReShade.ini at the last sync.
    /// Lets later syncs remove stale entries without ever touching the
    /// user's own (foreign) shortcuts.
    /// </summary>
    [JsonPropertyName("lastSyncedPresetPaths")]
    public List<string> LastSyncedPresetPaths { get; set; } = new();

    [JsonPropertyName("debugMode")]
    public bool DebugMode { get; set; }

    [JsonPropertyName("databaseVersion")]
    public int DatabaseVersion { get; set; }

    /// <summary>
    /// HTTPS URL of the database update manifest. Null/empty disables update checks.
    /// No production endpoint is hard-coded; the user (or a future release) configures it.
    /// </summary>
    [JsonPropertyName("databaseUpdateManifestUrl")]
    public string? DatabaseUpdateManifestUrl { get; set; }

    [JsonPropertyName("autoRestoreGlobalOnMenu")]
    public bool AutoRestoreGlobalOnMenu { get; set; } = true;
}

/// <summary>Global detection hotkey, e.g. F8 with optional modifiers.</summary>
public sealed class DetectionHotkeyConfig
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "F8";

    [JsonPropertyName("ctrl")]
    public bool Ctrl { get; set; }

    [JsonPropertyName("shift")]
    public bool Shift { get; set; }

    [JsonPropertyName("alt")]
    public bool Alt { get; set; }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        parts.Add(Key);
        return string.Join("+", parts);
    }
}

/// <summary>Internal ReShade preset shortcut (sent via SendInput, never pressed by the user).</summary>
public sealed class ReshadeShortcutConfig
{
    [JsonPropertyName("virtualKey")]
    public int VirtualKey { get; set; }

    [JsonPropertyName("ctrl")]
    public bool Ctrl { get; set; } = true;

    [JsonPropertyName("shift")]
    public bool Shift { get; set; } = true;

    [JsonPropertyName("alt")]
    public bool Alt { get; set; } = true;
}
