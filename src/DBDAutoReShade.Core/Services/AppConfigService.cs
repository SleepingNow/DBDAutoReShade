using System.Text.Json;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Persists <see cref="AppConfig"/> under %AppData%\DBDAutoReShade\config.json.
/// Atomic saves, corrupt-config recovery (quarantines the bad file, never
/// deletes user presets), and config-only reset.
/// </summary>
public sealed class AppConfigService
{
    public const string AppFolderName = "DBDAutoReShade";
    public const string ConfigFileName = "config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly string _configPath;

    public AppConfigService(string? configPath = null)
    {
        _configPath = configPath ?? DefaultConfigPath();
        Config = new AppConfig();
    }

    public AppConfig Config { get; private set; }

    public string ConfigPath => _configPath;

    public static string DefaultAppDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string DefaultConfigPath() => Path.Combine(DefaultAppDirectory(), ConfigFileName);

    public void Load()
    {
        if (!File.Exists(_configPath))
        {
            Config = new AppConfig();
            return;
        }

        try
        {
            string json = File.ReadAllText(_configPath);
            var parsed = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            Config = parsed is null ? new AppConfig() : WithSafeDictionaries(parsed);
        }
        catch
        {
            QuarantineCorruptFile();
            Config = new AppConfig();
        }
    }

    public void Save()
    {
        string? dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(Config, JsonOptions);
        string tmp = _configPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _configPath, overwrite: true);
    }

    /// <summary>Reset application configuration only. Never touches preset files, DBD or ReShade installs.</summary>
    public void Reset()
    {
        Config = new AppConfig();
        Save();
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_configPath);
            string name = Path.GetFileNameWithoutExtension(_configPath);
            string backup = Path.Combine(dir ?? ".", $"{name}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(_configPath, backup, overwrite: false);
        }
        catch
        {
            // Best effort; a fresh default config is still returned.
        }
    }

    private static AppConfig WithSafeDictionaries(AppConfig parsed)
    {
        parsed.RealmPresets = new Dictionary<string, string>(parsed.RealmPresets ?? new(), StringComparer.OrdinalIgnoreCase);
        parsed.ReshadeShortcuts = new Dictionary<string, ReshadeShortcutConfig>(parsed.ReshadeShortcuts ?? new(), StringComparer.OrdinalIgnoreCase);
        parsed.DetectionHotkey ??= new DetectionHotkeyConfig();
        if (string.IsNullOrWhiteSpace(parsed.DetectionHotkey.Key))
        {
            parsed.DetectionHotkey.Key = "F8";
        }

        return parsed;
    }
}
