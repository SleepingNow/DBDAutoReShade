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

    /// <summary>
    /// The second global hotkey, "Switch to Global preset" (null = disabled).
    /// New configs, and configs saved before it existed (no "globalHotkey"
    /// property), get <see cref="DefaultGlobalHotkeyKey"/>. Clearing it saves an
    /// explicit JSON null, which loads as null again, so "cleared" and "missing"
    /// stay distinct. The app never registers it while it is the same combo as
    /// <see cref="DetectionHotkey"/> or one of its ReShade shortcut combos (it
    /// says why instead); such a value is still kept as saved.
    /// </summary>
    [JsonPropertyName("globalHotkey")]
    public DetectionHotkeyConfig? GlobalHotkey { get; set; } = new() { Key = DefaultGlobalHotkeyKey };

    /// <summary>Whether the UI plays its animations (storyboards, ambient loops, particles). Default on.</summary>
    [JsonPropertyName("animationsEnabled")]
    public bool AnimationsEnabled { get; set; } = true;

    /// <summary>Whether the Great skill-check sound plays when a map is found. Default on.</summary>
    [JsonPropertyName("soundEnabled")]
    public bool SoundEnabled { get; set; } = true;

    /// <summary>
    /// Volume of the Great skill-check sound, 0..1 (the amplitude is volume²).
    /// <see cref="DefaultSoundVolume"/> on first run and for configs saved
    /// before it existed; out-of-range values load clamped.
    /// </summary>
    [JsonPropertyName("soundVolume")]
    public double SoundVolume { get; set; } = DefaultSoundVolume;

    /// <summary>Key of the default Global preset hotkey (no modifiers).</summary>
    public const string DefaultGlobalHotkeyKey = "F9";

    /// <summary>The skill-check sound volume of a new (or pre-1.2) config: 50 %.</summary>
    public const double DefaultSoundVolume = 0.5;

    /// <summary><paramref name="volume"/> clamped to 0..1; a non-finite value gives <see cref="DefaultSoundVolume"/>.</summary>
    public static double ClampSoundVolume(double volume) =>
        double.IsFinite(volume) ? Math.Clamp(volume, 0.0, 1.0) : DefaultSoundVolume;
}

/// <summary>
/// A global hotkey, e.g. F8 with optional modifiers: the detection hotkey, and
/// also the Global preset hotkey (<see cref="AppConfig.GlobalHotkey"/>).
/// </summary>
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
