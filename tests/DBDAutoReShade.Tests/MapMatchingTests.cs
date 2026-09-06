using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class MapMatchingTests
{
    private static (MapMatchingService Matcher, IReadOnlyList<RealmEntry> Realms) Setup()
    {
        var db = new RealmDatabaseService();
        db.LoadAsync(null).GetAwaiter().GetResult();
        return (new MapMatchingService(), db.Realms);
    }

    [Fact]
    public void ExactMap_MatchesWithFullConfidence()
    {
        var (matcher, realms) = Setup();
        MapMatchResult r = matcher.Match("FRACTURED COWSHED\nCOLDWIND FARM", realms);
        Assert.True(r.Success);
        Assert.Equal("Fractured Cowshed", r.MapName);
        Assert.Equal("Coldwind Farm", r.RealmName);
        Assert.Equal(MatchMethod.MapName, r.Method);
    }

    [Fact]
    public void MinorOcrError_StillMatches()
    {
        var (matcher, realms) = Setup();
        MapMatchResult r = matcher.Match("FRACTURED C0WSHED\nCOLDWIND FARM", realms);
        Assert.True(r.Success);
        Assert.Equal("Coldwind Farm", r.RealmName);
    }

    [Fact]
    public void ApostropheOcrError_StillMatches()
    {
        var (matcher, realms) = Setup();
        MapMatchResult r = matcher.Match("FATHER CAMPBEILS CHAPEL", realms);
        Assert.True(r.Success);
        Assert.Equal("Crotus Prenn Asylum", r.RealmName);
    }

    [Fact]
    public void RealmOnlyText_ResolvesRealm()
    {
        var (matcher, realms) = Setup();
        MapMatchResult r = matcher.Match("COLDWIND FARM", realms);
        Assert.True(r.Success);
        Assert.Equal("Coldwind Farm", r.RealmName);
        Assert.Equal(MatchMethod.RealmName, r.Method);
    }

    [Fact]
    public void UnknownText_FailsInsteadOfGuessing()
    {
        var (matcher, realms) = Setup();
        MapMatchResult r = matcher.Match("PRESS START TO PLAY", realms);
        Assert.False(r.Success);
        Assert.Null(r.RealmName);
    }

    [Fact]
    public void EmptyText_Fails()
    {
        var (matcher, realms) = Setup();
        Assert.False(matcher.Match("", realms).Success);
        Assert.False(matcher.Match(null, realms).Success);
    }

    [Fact]
    public void CaseAndPunctuation_AreIgnored()
    {
        var (matcher, realms) = Setup();
        MapMatchResult r = matcher.Match("  fractured... cowshed!! ", realms);
        Assert.True(r.Success);
        Assert.Equal("Coldwind Farm", r.RealmName);
    }

    [Fact]
    public void AmbiguousCloseMaps_DoNotGuess()
    {
        var (matcher, _) = Setup();
        var realms = new List<RealmEntry>
        {
            new() { Name = "Realm Alpha", Maps = new List<string> { "Hallowed Ground" } },
            // L/1 is a classic OCR confusion (reduced-cost substitution): near-tie, different realms.
            new() { Name = "Realm Beta", Maps = new List<string> { "Ha1lowed Ground" } }
        };
        MapMatchResult r = matcher.Match("HALLOWED GROUND", realms);
        // Both maps score ~identically for different realms: must not guess.
        Assert.False(r.Success);
    }
}
