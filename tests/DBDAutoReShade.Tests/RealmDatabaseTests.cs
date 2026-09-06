using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class RealmDatabaseTests
{
    private static RealmDatabaseService Loaded()
    {
        var svc = new RealmDatabaseService();
        svc.LoadAsync(null).GetAwaiter().GetResult();
        return svc;
    }

    [Fact]
    public void EmbeddedDatabase_Loads_WithExpectedVersion()
    {
        var db = Loaded();
        Assert.True(db.IsLoaded);
        Assert.Equal(1, db.Version);
        Assert.NotEmpty(db.Realms);
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
}
