using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.App.Services;

/// <summary>
/// The shortcut entries the DBD process <paramref name="ProcessId"/> (created at
/// <paramref name="ProcessStartUtc"/>, null = unknown) loaded from
/// <paramref name="IniPath"/> (null = none configured) when it started.
/// </summary>
internal sealed record ReshadeLoadedSnapshot(
    int? ProcessId,
    DateTime? ProcessStartUtc,
    string? IniPath,
    IReadOnlyList<IniShortcutEntry> Entries)
{
    /// <summary>
    /// True when this snapshot describes the process <paramref name="processId"/>
    /// created at <paramref name="startUtc"/>. PIDs are reused, so when both
    /// creation times are known they must match too.
    /// </summary>
    public bool Describes(int? processId, DateTime? startUtc) =>
        processId is not null && ProcessId == processId &&
        (ProcessStartUtc is not DateTime recorded || startUtc is not DateTime live || recorded == live);
}

/// <summary>
/// Keeps the <see cref="ReshadeLoadedSnapshot"/> of the running DBD on disk,
/// next to the config. ReShade loads its shortcut lists only while the game
/// starts, and this app may rewrite ReShade.ini while the same DBD keeps
/// running: after an app restart the file no longer says what that process
/// loaded, but this record still does. Best effort: never throws.
/// </summary>
internal sealed class ReshadeLoadedSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ReshadeLoadedSnapshotStore(string path) => FilePath = path;

    public string FilePath { get; }

    /// <summary>The store that belongs to the config at <paramref name="configPath"/> (same folder, named after it).</summary>
    public static ReshadeLoadedSnapshotStore ForConfig(string configPath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? AppConfigService.DefaultAppDirectory();
        return new ReshadeLoadedSnapshotStore(
            Path.Combine(folder, Path.GetFileNameWithoutExtension(configPath) + ".reshade-loaded.json"));
    }

    /// <summary>The stored snapshot; null when there is none or it can't be read.</summary>
    public ReshadeLoadedSnapshot? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            SnapshotFile? file = JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(FilePath), JsonOptions);
            if (file is null)
            {
                return null;
            }

            var entries = (file.Entries ?? new List<EntryFile>())
                .Where(e => !string.IsNullOrWhiteSpace(e.PresetPath))
                .Select(e => new IniShortcutEntry(
                    new ReshadeShortcutConfig { VirtualKey = e.VirtualKey, Ctrl = e.Ctrl, Shift = e.Shift, Alt = e.Alt },
                    e.PresetPath!))
                .ToList();
            return new ReshadeLoadedSnapshot(file.ProcessId, file.ProcessStartUtc, file.IniPath, entries);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Replaces the stored snapshot (atomic write). Best effort.</summary>
    public void Save(ReshadeLoadedSnapshot snapshot)
    {
        string tmp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var file = new SnapshotFile
            {
                ProcessId = snapshot.ProcessId,
                ProcessStartUtc = snapshot.ProcessStartUtc,
                IniPath = snapshot.IniPath,
                Entries = snapshot.Entries.Select(e => new EntryFile
                {
                    VirtualKey = e.Shortcut.VirtualKey,
                    Ctrl = e.Shortcut.Ctrl,
                    Shift = e.Shortcut.Shift,
                    Alt = e.Shortcut.Alt,
                    PresetPath = e.PresetPath
                }).ToList()
            };

            string? folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
        }
    }

    /// <summary>Forgets the stored snapshot (DBD is not running). Best effort.</summary>
    public void Delete() => TryDelete(FilePath);

    private static void TryDelete(string path)
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

    private sealed class SnapshotFile
    {
        [JsonPropertyName("processId")]
        public int? ProcessId { get; set; }

        [JsonPropertyName("processStartUtc")]
        public DateTime? ProcessStartUtc { get; set; }

        [JsonPropertyName("iniPath")]
        public string? IniPath { get; set; }

        [JsonPropertyName("entries")]
        public List<EntryFile>? Entries { get; set; }
    }

    private sealed class EntryFile
    {
        [JsonPropertyName("virtualKey")]
        public int VirtualKey { get; set; }

        [JsonPropertyName("ctrl")]
        public bool Ctrl { get; set; }

        [JsonPropertyName("shift")]
        public bool Shift { get; set; }

        [JsonPropertyName("alt")]
        public bool Alt { get; set; }

        [JsonPropertyName("presetPath")]
        public string? PresetPath { get; set; }
    }
}
