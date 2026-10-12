using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Persists <see cref="AppConfig"/> under %AppData%\DBDAutoReShade\config.json.
/// Thread-safe, durable, atomic saves (unique temp file, flushed to disk, then
/// renamed over the config). Corrupt-config recovery quarantines the bad file
/// under a unique name and never deletes user presets; a config that cannot be
/// read because of an IO error (e.g. a locked file) is never replaced.
/// Config-only reset.
/// </summary>
public sealed class AppConfigService
{
    public const string AppFolderName = "DBDAutoReShade";
    public const string ConfigFileName = "config.json";

    /// <summary>How many times a failed read is retried before <see cref="Load"/> gives up.</summary>
    public const int ReadRetryCount = 3;

    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly string _configPath;
    private readonly object _sync = new();

    // Set when a corrupt config could not be moved aside: it must not be overwritten until it is.
    private bool _corruptOriginalPending;

    public AppConfigService(string? configPath = null)
    {
        _configPath = configPath ?? DefaultConfigPath();
        Config = new AppConfig();
    }

    public AppConfig Config { get; private set; }

    public string ConfigPath => _configPath;

    /// <summary>
    /// What the last <see cref="Load"/> had to do about a corrupt config file (where it was
    /// quarantined, or that it could not be moved aside), or null when it loaded cleanly.
    /// </summary>
    public string? LastLoadWarning { get; private set; }

    public static string DefaultAppDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string DefaultConfigPath() => Path.Combine(DefaultAppDirectory(), ConfigFileName);

    /// <summary>
    /// Load the config. A missing file gives defaults. Invalid JSON/data gives defaults and the
    /// file is quarantined (see <see cref="LastLoadWarning"/>). An IO error (locked or unreadable
    /// file) is retried briefly and then thrown as <see cref="IOException"/>; the file is left
    /// untouched and <see cref="Config"/> keeps its previous value.
    /// </summary>
    public void Load()
    {
        lock (_sync)
        {
            // Throws (leaving Config and the file untouched) when the file exists but cannot be read.
            bool exists = TryReadConfigText(out string? json);
            _corruptOriginalPending = false;
            LastLoadWarning = null;
            if (!exists)
            {
                Config = new AppConfig();
                return;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<AppConfig>(json!, JsonOptions);
                Config = parsed is null ? new AppConfig() : WithSafeDictionaries(parsed);
            }
            catch (Exception ex)
            {
                // The text is already in memory, so anything thrown here is a data problem
                // (bad JSON, wrong types), never an IO problem.
                Config = new AppConfig();
                if (TryQuarantineCorruptFile(out string? backup, out string? moveError))
                {
                    LastLoadWarning =
                        $"The configuration file was corrupt ({ex.Message}). It was moved to '{backup}' and default settings were loaded.";
                }
                else
                {
                    _corruptOriginalPending = true;
                    LastLoadWarning =
                        $"The configuration file '{_configPath}' is corrupt ({ex.Message}) and could not be moved aside ({moveError}). " +
                        "Default settings were loaded; the original file was left in place and will not be overwritten.";
                }
            }
        }
    }

    /// <summary>
    /// Durably and atomically write <see cref="Config"/>. Serialized across threads; each save
    /// uses its own temp file, which is removed on failure. Throws on IO errors (including a
    /// corrupt original that still cannot be moved aside).
    /// </summary>
    public void Save()
    {
        lock (_sync)
        {
            EnsureCorruptOriginalPreserved();

            string? dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(Config, JsonOptions);
            string tmp = $"{_configPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(json);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tmp, _configPath, overwrite: true);
            }
            catch
            {
                TryDeleteFile(tmp);
                throw;
            }
        }
    }

    /// <summary>Reset application configuration only. Never touches preset files, DBD or ReShade installs.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            Config = new AppConfig();
            Save();
        }
    }

    /// <summary>Read the config text, retrying transient IO errors. False when the file does not exist.</summary>
    private bool TryReadConfigText([NotNullWhen(true)] out string? json)
    {
        json = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    return false;
                }

                json = File.ReadAllText(_configPath);
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadRetryCount)
                {
                    throw new IOException(
                        $"Could not read the configuration file '{_configPath}' ({ex.Message}). " +
                        "It was left untouched; close any program that has it open and start DBD Auto ReShade again.", ex);
                }

                Thread.Sleep(ReadRetryDelay);
            }
        }
    }

    /// <summary>Before overwriting a corrupt original that could not be quarantined at load, try again; refuse if it still fails.</summary>
    private void EnsureCorruptOriginalPreserved()
    {
        if (!_corruptOriginalPending)
        {
            return;
        }

        if (File.Exists(_configPath) && !TryQuarantineCorruptFile(out _, out string? moveError))
        {
            throw new IOException(
                $"The corrupt configuration file '{_configPath}' could not be moved aside ({moveError}), so it was not overwritten. " +
                "Close any program that has it open (or move it away yourself) and try again.");
        }

        _corruptOriginalPending = false;
    }

    private bool TryQuarantineCorruptFile(out string? backupPath, out string? error)
    {
        backupPath = null;
        error = null;
        try
        {
            string? dir = Path.GetDirectoryName(_configPath);
            string name = Path.GetFileNameWithoutExtension(_configPath);
            string suffix = Guid.NewGuid().ToString("N")[..6];
            string backup = Path.Combine(dir ?? ".", $"{name}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{suffix}.json");
            File.Move(_configPath, backup, overwrite: false);
            backupPath = backup;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Re-key dictionaries case-insensitively (a hand-edited file may hold keys that differ only
    /// by case: they merge, last one wins) and replace JSON nulls with safe defaults.
    /// </summary>
    private static AppConfig WithSafeDictionaries(AppConfig parsed)
    {
        parsed.RealmPresets = MergeCaseInsensitive(parsed.RealmPresets);
        parsed.ReshadeShortcuts = MergeCaseInsensitive(parsed.ReshadeShortcuts);
        parsed.LastSyncedPresetPaths = parsed.LastSyncedPresetPaths?.Where(p => p is not null).ToList() ?? new List<string>();
        parsed.DetectionHotkey ??= new DetectionHotkeyConfig();
        if (string.IsNullOrWhiteSpace(parsed.DetectionHotkey.Key))
        {
            parsed.DetectionHotkey.Key = "F8";
        }

        return parsed;
    }

    private static Dictionary<string, TValue> MergeCaseInsensitive<TValue>(Dictionary<string, TValue>? source)
        where TValue : class
    {
        var merged = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return merged;
        }

        // Dictionary enumeration follows insertion (= file) order here, so the later key wins.
        foreach (KeyValuePair<string, TValue> pair in source)
        {
            if (pair.Value is null)
            {
                continue;
            }

            merged[pair.Key] = pair.Value;
        }

        return merged;
    }
}
