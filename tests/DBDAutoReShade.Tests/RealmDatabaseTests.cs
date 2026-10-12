using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class RealmDatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-realmdb-" + Guid.NewGuid().ToString("N"));

    public RealmDatabaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private static RealmDatabaseService Loaded()
    {
        var svc = new RealmDatabaseService();
        svc.LoadAsync(null).GetAwaiter().GetResult();
        return svc;
    }

    private static string DbJson(int version, string realm = "Coldwind Farm", string map = "Fractured Cowshed") =>
        $$"""{"schemaVersion": 1, "version": {{version}}, "realms": [{"name": "{{realm}}", "maps": ["{{map}}"]}]}""";

    private string WriteOverride(string json)
    {
        string path = Path.Combine(_dir, "mapDatabase.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void EmbeddedDatabase_Loads_WithExpectedVersion()
    {
        var db = Loaded();
        Assert.True(db.IsLoaded);
        Assert.Equal(1, db.Version);
        Assert.NotEmpty(db.Realms);
        Assert.False(db.LoadedFromOverride);
        Assert.Null(db.LastLoadWarning);
    }

    [Theory]
    [InlineData("Fractured Cowshed", "Coldwind Farm")]
    [InlineData("Rancid Abattoir", "Coldwind Farm")]
    [InlineData("Father Campbell's Chapel", "Crotus Prenn Asylum")]
    [InlineData("Lampkin Lane", "Haddonfield")]
    [InlineData("The Pale Rose", "Backwater Swamp")]
    [InlineData("Treatment Theatre", "Léry's Memorial Institute")]
    [InlineData("Badham Preschool", "Springwood")]
    [InlineData("The Game", "Gideon Meat Plant")]
    [InlineData("Sanctum of Wrath", "Yamaoka Estate")]
    [InlineData("Mount Ormond Resort", "Ormond")]
    [InlineData("The Underground Complex", "Hawkins National Laboratory")]
    [InlineData("Dead Dawg Saloon", "Grave of Glenvale")]
    [InlineData("Midwich Elementary School", "Silent Hill")]
    [InlineData("Coal Tower", "MacMillan Estate")]
    [InlineData("Eyrie of Crows", "Forsaken Boneyard")]
    [InlineData("The Shattered Square", "The Decimated Borgo")]
    [InlineData("Toba Landing", "Dvarka Deepwood")]
    public void KnownMap_ResolvesToCorrectRealm(string map, string realm)
    {
        var db = Loaded();
        Assert.Equal(realm, db.FindRealmForMap(map));
    }

    [Theory]
    [InlineData("Not A Real Map")]
    [InlineData("")]
    [InlineData("Fractured Cow")]
    public void UnknownMap_ReturnsNull(string map)
    {
        var db = Loaded();
        Assert.Null(db.FindRealmForMap(map));
    }

    [Fact]
    public void CorruptJson_IsRejected()
    {
        Assert.False(RealmDatabaseService.TryParse("{not json", out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void WrongSchemaVersion_IsRejected()
    {
        const string json = """{"schemaVersion": 999, "version": 1, "realms": [{"name": "X", "maps": ["Y"]}]}""";
        Assert.False(RealmDatabaseService.TryParse(json, out _, out _));
    }

    [Fact]
    public void EmptyRealms_AreRejected()
    {
        const string json = """{"schemaVersion": 1, "version": 1, "realms": []}""";
        Assert.False(RealmDatabaseService.TryParse(json, out _, out _));
    }

    [Fact]
    public void DuplicateMapAcrossRealms_IsRejected()
    {
        const string json = "{\"schemaVersion\": 1, \"version\": 2, \"realms\": [" +
            "{\"name\": \"Realm A\", \"maps\": [\"Same Map\"]}," +
            "{\"name\": \"Realm B\", \"maps\": [\"Same Map\"]}]}";
        Assert.False(RealmDatabaseService.TryParse(json, out _, out string? error));
        Assert.Contains("Same Map", error);
    }

    [Fact]
    public void OlderDatabaseVersion_StillParses()
    {
        const string json = """{"schemaVersion": 1, "version": 1, "realms": [{"name": "R", "maps": ["M"]}]}""";
        Assert.True(RealmDatabaseService.TryParse(json, out var db, out _));
        Assert.Equal(1, db!.Version);
    }

    [Theory]
    [InlineData("""{"schemaVersion": 1, "version": 2, "realms": [null]}""")]
    [InlineData("""{"schemaVersion": 1, "version": 2, "realms": [{"name": "R", "maps": ["M"]}, null]}""")]
    [InlineData("""{"schemaVersion": 1, "version": 2, "realms": [{"name": null, "maps": ["M"]}]}""")]
    [InlineData("""{"schemaVersion": 1, "version": 2, "realms": [{"name": "   ", "maps": ["M"]}]}""")]
    [InlineData("""{"schemaVersion": 1, "version": 2, "realms": [{"maps": ["M"]}]}""")]
    [InlineData("""{"schemaVersion": 1, "version": 2, "realms": [{"name": "R", "maps": null}]}""")]
    [InlineData("""{"schemaVersion": 1, "version": 2, "realms": [{"name": "R", "maps": [null]}]}""")]
    [InlineData("""{"schemaVersion": 1, "version": 2, "realms": [{"name": "R", "maps": ["  "]}]}""")]
    [InlineData("""{"schemaVersion": 1, "version": 2, "realms": [{"name": "R", "maps": ["!!!"]}]}""")]
    [InlineData("""{"schemaVersion": 1, "version": 2, "realms": null}""")]
    public void NullOrBlankEntries_AreRejected_WithoutThrowing(string json)
    {
        bool ok = RealmDatabaseService.TryParse(json, out var db, out string? error);
        Assert.False(ok);
        Assert.Null(db);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void LeadingByteOrderMark_IsIgnored()
    {
        Assert.True(RealmDatabaseService.TryParse("﻿" + DbJson(2), out var db, out string? error), error);
        Assert.Equal(2, db!.Version);
    }

    [Fact]
    public async Task LoadAsync_InvalidOverride_FallsBackToBuiltIn_WithWarning()
    {
        string path = WriteOverride("""{"schemaVersion": 1, "version": 9, "realms": [null]}""");
        var db = new RealmDatabaseService();

        await db.LoadAsync(path); // must not throw

        Assert.True(db.IsLoaded);
        Assert.Equal(1, db.Version);
        Assert.False(db.LoadedFromOverride);
        Assert.Contains("invalid", db.LastLoadWarning);
        Assert.Equal("Coldwind Farm", db.FindRealmForMap("Fractured Cowshed"));
    }

    [Fact]
    public async Task LoadAsync_UnreadableOverride_FallsBackToBuiltIn_WithWarning()
    {
        string path = WriteOverride(DbJson(5));
        var db = new RealmDatabaseService();

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await db.LoadAsync(path); // IO error must not escape
        }

        Assert.True(db.IsLoaded);
        Assert.Equal(1, db.Version);
        Assert.False(db.LoadedFromOverride);
        Assert.Contains("Could not read", db.LastLoadWarning);
    }

    [Fact]
    public async Task LoadAsync_OlderOverride_IsShadowedByNewerBuiltIn()
    {
        string path = WriteOverride(DbJson(2, "Old Realm", "Old Map"));
        var db = new RealmDatabaseService(DbJson(3));

        await db.LoadAsync(path);

        Assert.Equal(3, db.Version);
        Assert.False(db.LoadedFromOverride);
        Assert.Null(db.LastLoadWarning); // expected after an app upgrade, not a problem
        Assert.Null(db.FindRealmForMap("Old Map"));
        Assert.Equal("Coldwind Farm", db.FindRealmForMap("Fractured Cowshed"));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LoadAsync_SameOrNewerOverride_Wins(int overrideVersion)
    {
        string path = WriteOverride(DbJson(overrideVersion, "New Realm", "New Map"));
        var db = new RealmDatabaseService(DbJson(3));

        await db.LoadAsync(path);

        Assert.Equal(overrideVersion, db.Version);
        Assert.True(db.LoadedFromOverride);
        Assert.Equal("New Realm", db.FindRealmForMap("New Map"));
    }

    [Fact]
    public async Task LoadAsync_EmbeddedVersionOverride_IsUsed()
    {
        // The embedded database is v1; an override of the same version still wins (≥).
        string path = WriteOverride(DbJson(1, "Override Realm", "Override Map"));
        var db = new RealmDatabaseService();

        await db.LoadAsync(path);

        Assert.True(db.LoadedFromOverride);
        Assert.Equal("Override Realm", db.FindRealmForMap("Override Map"));
    }

    [Fact]
    public async Task LoadAsync_GoodOverride_ClearsPreviousWarning()
    {
        string path = WriteOverride("{broken");
        var db = new RealmDatabaseService();
        await db.LoadAsync(path);
        Assert.NotNull(db.LastLoadWarning);

        File.WriteAllText(path, DbJson(2));
        await db.LoadAsync(path);

        Assert.Null(db.LastLoadWarning);
        Assert.True(db.LoadedFromOverride);
        Assert.Equal(2, db.Version);
    }

    [Fact]
    public async Task LoadAsync_OverrideWithByteOrderMark_IsUsed()
    {
        string path = Path.Combine(_dir, "bom.json");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(DbJson(2))]);
        var db = new RealmDatabaseService();

        await db.LoadAsync(path);

        Assert.True(db.LoadedFromOverride);
        Assert.Equal(2, db.Version);
    }

    [Fact]
    public async Task LoadAsync_InvalidBuiltIn_Throws()
    {
        var db = new RealmDatabaseService("""{"schemaVersion": 1, "version": 1, "realms": []}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.LoadAsync(null));
    }
}
