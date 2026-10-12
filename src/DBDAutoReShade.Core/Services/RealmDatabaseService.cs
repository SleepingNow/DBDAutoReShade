using System.Reflection;
using System.Text.Json;
using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Loads and validates the built-in (or updated) map → realm database.
/// Ship default is an embedded resource; an updater-provided copy in AppData
/// takes precedence once validated, but only while it is at least as new as
/// the built-in one (an app upgrade can ship a newer built-in database).
/// A bad or unreadable override never breaks loading: the built-in database
/// is used and the reason is exposed in <see cref="LastLoadWarning"/>.
/// Never hard-codes giant switch statements.
/// </summary>
public sealed class RealmDatabaseService
{
    public const int SupportedSchemaVersion = 1;
    private const string EmbeddedResourceName = "DBDAutoReShade.Core.RealmData.mapDatabase.json";

    private readonly string? _builtInJson;

    // Swapped atomically by LoadAsync (an update can reload while detection reads Realms).
    private volatile MapDatabaseFile? _database;
    private volatile string? _lastLoadWarning;
    private volatile bool _loadedFromOverride;

    /// <summary>Uses the database embedded in this assembly as the built-in baseline.</summary>
    public RealmDatabaseService()
        : this(builtInJson: null)
    {
    }

    /// <summary>
    /// Uses <paramref name="builtInJson"/> as the built-in baseline instead of the embedded
    /// resource (tests and tooling). Null means "use the embedded resource".
    /// </summary>
    public RealmDatabaseService(string? builtInJson)
    {
        _builtInJson = builtInJson;
    }

    public IReadOnlyList<RealmEntry> Realms => (IReadOnlyList<RealmEntry>?)_database?.Realms ?? Array.Empty<RealmEntry>();

    public int Version => _database?.Version ?? 0;

    public bool IsLoaded => _database is not null;

    /// <summary>
    /// Why the override file passed to the last <see cref="LoadAsync"/> was not used
    /// (unreadable or invalid), or null when there was nothing to report.
    /// An override that is merely older than the built-in database is not a warning.
    /// </summary>
    public string? LastLoadWarning => _lastLoadWarning;

    /// <summary>True when the last <see cref="LoadAsync"/> used the override file rather than the built-in database.</summary>
    public bool LoadedFromOverride => _loadedFromOverride;

    /// <summary>
    /// Load the built-in database, optionally overridden by a validated file on disk.
    /// The override is used only when its version is ≥ the built-in version.
    /// Never throws because of a bad or unreadable override file; only a broken
    /// built-in database (a build defect) or cancellation throws.
    /// </summary>
    public async Task LoadAsync(string? overridePath = null, CancellationToken ct = default)
    {
        MapDatabaseFile builtIn = await LoadBuiltInAsync(ct).ConfigureAwait(false);

        MapDatabaseFile? fromDisk = null;
        string? warning = null;
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            string? json = null;
            try
            {
                json = await File.ReadAllTextAsync(overridePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warning = $"Could not read the downloaded map database '{overridePath}' ({ex.Message}); using the built-in database.";
            }

            if (json is not null)
            {
                if (!TryParse(json, out MapDatabaseFile? parsed, out string? error))
                {
                    // Corrupt override files are ignored here (updater keeps last-known-good).
                    warning = $"The downloaded map database '{overridePath}' is invalid ({error}); using the built-in database.";
                }
                else if (parsed!.Version >= builtIn.Version)
                {
                    fromDisk = parsed;
                }
                // An older override (e.g. after an app upgrade) is silently shadowed by the newer built-in one.
            }
        }

        _lastLoadWarning = warning;
        _loadedFromOverride = fromDisk is not null;
        _database = fromDisk ?? builtIn;
    }

    private async Task<MapDatabaseFile> LoadBuiltInAsync(CancellationToken ct)
    {
        string json;
        if (_builtInJson is not null)
        {
            json = _builtInJson;
        }
        else
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
            if (stream is null)
            {
                throw new InvalidOperationException($"Embedded database resource '{EmbeddedResourceName}' not found.");
            }

            using var reader = new StreamReader(stream);
            json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        if (!TryParse(json, out MapDatabaseFile? db, out string? error))
        {
            throw new InvalidOperationException($"Built-in map database is invalid: {error}");
        }

        return db!;
    }

    /// <summary>Validate + parse database JSON without throwing. A leading UTF-8 BOM (U+FEFF) is ignored.</summary>
    public static bool TryParse(string? json, out MapDatabaseFile? database, out string? error)
    {
        database = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Database JSON is empty.";
            return false;
        }

        // Encoding.UTF8.GetString keeps the byte-order mark as U+FEFF, which the JSON parser rejects.
        if (json[0] == '﻿')
        {
            json = json[1..];
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

    /// <summary>
    /// Validate a deserialized database without throwing. Rejects null realm entries,
    /// null/blank realm or map names, null map lists and duplicates.
    /// </summary>
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
        for (int i = 0; i < parsed.Realms.Count; i++)
        {
            // JSON can carry nulls where the model says non-null ("realms": [null], "name": null, "maps": null).
            RealmEntry? realm = parsed.Realms[i];
            if (realm is null)
            {
                error = $"Realm entry #{i + 1} is null.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(realm.Name))
            {
                error = $"Realm entry #{i + 1} has an empty name.";
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

            foreach (string? map in realm.Maps)
            {
                if (string.IsNullOrWhiteSpace(map))
                {
                    error = $"Realm '{realm.Name}' contains an empty map name.";
                    return false;
                }

                string normMap = TextNormalizationService.Normalize(map);
                if (normMap.Length == 0)
                {
                    error = $"Realm '{realm.Name}' contains a map name without letters or digits: '{map}'.";
                    return false;
                }

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
        MapDatabaseFile? database = _database;
        if (database is null || string.IsNullOrWhiteSpace(mapName))
        {
            return null;
        }

        string norm = TextNormalizationService.Normalize(mapName);
        foreach (RealmEntry realm in database.Realms)
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
        MapDatabaseFile? database = _database;
        if (database is null || string.IsNullOrWhiteSpace(realmName))
        {
            return false;
        }

        string norm = TextNormalizationService.Normalize(realmName);
        foreach (RealmEntry r in database.Realms)
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
