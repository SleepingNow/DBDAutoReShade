using System.Reflection;
using System.Text.Json;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Loads and validates the built-in (or updated) map → realm database.
/// Ship default is an embedded resource; an updater-provided copy in AppData
/// takes precedence once validated. Never hard-codes giant switch statements.
/// </summary>
public sealed class RealmDatabaseService
{
    public const int SupportedSchemaVersion = 1;
    private const string EmbeddedResourceName = "DBDAutoReShade.Core.RealmData.mapDatabase.json";

    private MapDatabaseFile? _database;

    public IReadOnlyList<RealmEntry> Realms => (IReadOnlyList<RealmEntry>?)_database?.Realms ?? Array.Empty<RealmEntry>();

    public int Version => _database?.Version ?? 0;

    public bool IsLoaded => _database is not null;

    /// <summary>Load embedded default, optionally overridden by a validated file on disk.</summary>
    public async Task LoadAsync(string? overridePath = null, CancellationToken ct = default)
    {
        MapDatabaseFile? fromDisk = null;
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            string json = await File.ReadAllTextAsync(overridePath, ct).ConfigureAwait(false);
            if (TryParse(json, out var parsed, out _))
            {
                fromDisk = parsed;
            }
            // Corrupt override files are ignored here (updater keeps last-known-good);
            // the embedded default still loads below.
        }

        if (fromDisk is not null)
        {
            _database = fromDisk;
            return;
        }

        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded database resource '{EmbeddedResourceName}' not found.");
        }

        using var reader = new StreamReader(stream);
        string embedded = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (!TryParse(embedded, out var db, out string? error))
        {
            throw new InvalidOperationException($"Embedded map database is invalid: {error}");
        }

        _database = db;
    }

    /// <summary>Validate + parse database JSON without throwing.</summary>
    public static bool TryParse(string? json, out MapDatabaseFile? database, out string? error)
    {
        database = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Database JSON is empty.";
            return false;
        }

        MapDatabaseFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MapDatabaseFile>(json, DatabaseJson.Options);
        }
        catch (Exception ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "Database JSON deserialized to null.";
            return false;
        }

        return TryValidate(parsed, out database, out error);
    }

    public static bool TryValidate(MapDatabaseFile? parsed, out MapDatabaseFile? database, out string? error)
    {
        database = null;
        error = null;
        if (parsed is null)
        {
            error = "Database is null.";
            return false;
        }

        if (parsed.SchemaVersion != SupportedSchemaVersion)
        {
            error = $"Unsupported schemaVersion {parsed.SchemaVersion} (supported: {SupportedSchemaVersion}).";
            return false;
        }

        if (parsed.Version < 1)
        {
            error = "Database version must be >= 1.";
            return false;
        }

        if (parsed.Realms is null || parsed.Realms.Count == 0)
        {
            error = "Database contains no realms.";
            return false;
        }

        var seenRealms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenMaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (RealmEntry realm in parsed.Realms)
        {
            if (string.IsNullOrWhiteSpace(realm.Name))
            {
                error = "A realm has an empty name.";
                return false;
            }

            string normRealm = TextNormalizationService.Normalize(realm.Name);
            if (normRealm.Length == 0 || !seenRealms.Add(normRealm))
            {
                error = $"Duplicate or empty realm name: '{realm.Name}'.";
                return false;
            }

            if (realm.Maps is null || realm.Maps.Count == 0)
            {
                error = $"Realm '{realm.Name}' has no maps.";
                return false;
            }

            foreach (string map in realm.Maps)
            {
                if (string.IsNullOrWhiteSpace(map))
                {
                    error = $"Realm '{realm.Name}' contains an empty map name.";
                    return false;
                }

                string normMap = TextNormalizationService.Normalize(map);
                if (seenMaps.TryGetValue(normMap, out string? otherRealm))
                {
                    error = $"Map '{map}' appears in both '{otherRealm}' and '{realm.Name}'.";
                    return false;
                }

                seenMaps[normMap] = realm.Name;
            }
        }

        database = parsed;
        return true;
    }

    /// <summary>Exact (normalized) lookup of a map name → realm. Fuzzy matching lives in <see cref="MapMatchingService"/>.</summary>
    public string? FindRealmForMap(string? mapName)
    {
        if (_database is null || string.IsNullOrWhiteSpace(mapName))
        {
            return null;
        }

        string norm = TextNormalizationService.Normalize(mapName);
        foreach (RealmEntry realm in _database.Realms)
        {
            foreach (string map in realm.Maps)
            {
                if (TextNormalizationService.Normalize(map) == norm)
                {
                    return realm.Name;
                }
            }
        }

        return null;
    }

    public bool TryGetRealm(string? realmName, out RealmEntry? realm)
    {
        realm = null;
        if (_database is null || string.IsNullOrWhiteSpace(realmName))
        {
            return false;
        }

        string norm = TextNormalizationService.Normalize(realmName);
        foreach (RealmEntry r in _database.Realms)
        {
            if (TextNormalizationService.Normalize(r.Name) == norm)
            {
                realm = r;
                return true;
            }
        }

        return false;
    }
}

internal static class DatabaseJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}
