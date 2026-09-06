using System.Text.Json.Serialization;

namespace DBDAutoReShade.Core.Models;

/// <summary>
/// On-disk / on-the-wire shape of the map → realm database.
/// Kept separate from application logic so it can be updated independently.
/// </summary>
public sealed class MapDatabaseFile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("realms")]
    public List<RealmEntry> Realms { get; set; } = new();
}

/// <summary>A realm and the map names that belong to it.</summary>
public sealed class RealmEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("maps")]
    public List<string> Maps { get; set; } = new();
}
