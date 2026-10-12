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

    // ---- A4: near-identical names of one realm resolve once the read passes the midpoint ----

    [Theory]
    [InlineData("RACCOON CITY POLICE STATION EAST WING", "Raccoon City Police Station East Wing")]
    [InlineData("RACCOON CITY POLICE STATION WEST WING", "Raccoon City Police Station West Wing")]
    [InlineData("Raccoon City Police Station East Wing\nRACCOON CITY", "Raccoon City Police Station East Wing")]
    [InlineData("RACCOON CITY POLICE STATION FAST WING", "Raccoon City Police Station East Wing")] // 1 edit from East, 2 from West
    public void NearIdenticalMapsOfOneRealm_ResolveTheMap(string ocr, string expectedMap)
    {
        var (matcher, realms) = Setup();
        MapMatchResult r = matcher.Match(ocr, realms);
        Assert.True(r.Success, r.Diagnostic);
        Assert.Equal(expectedMap, r.MapName);
        Assert.Equal("Raccoon City", r.RealmName);
        Assert.Equal(MatchMethod.MapName, r.Method);
    }

    [Theory]
    [InlineData("RACCOON CITY POLICE STATION EEST WING")] // 1 edit from both wings
    [InlineData("RACCOON CITY POLICE STATION WING")] // wing word missing
    public void ReadEquidistantBetweenWings_GivesRealmOnly(string ocr)
    {
        var (matcher, realms) = Setup();
        MapMatchResult r = matcher.Match(ocr, realms);
        Assert.True(r.Success, r.Diagnostic);
        Assert.Null(r.MapName);
        Assert.Equal("Raccoon City", r.RealmName);
        Assert.Equal(MatchMethod.RealmName, r.Method);
    }

    [Fact]
    public void LessSimilarMapsOfOneRealm_KeepTheFullMargin()
    {
        // Names 4/20 apart: half their gap (0.10) exceeds 0.06, so the 0.06 margin applies.
        var realms = new List<RealmEntry>
        {
            new() { Name = "Realm Alpha", Maps = new List<string> { "ABCDEFGHIJKLMNOPQRST", "ABCDEFGHIJKLMNOPWXYZ" } }
        };
        // 2 edits from the first (0.90), 3 from the second (0.85): gap 0.05 < 0.06 → realm only.
        MapMatchResult r = new MapMatchingService().Match("ABCDEFGHIJKLMNOPQRYQ", realms);
        Assert.True(r.Success, r.Diagnostic);
        Assert.Null(r.MapName);
        Assert.Equal("Realm Alpha", r.RealmName);
    }

    [Fact]
    public void CloseMapsOfOneRealm_ResolvePastTheMidpoint()
    {
        // Names 3/40 apart: the required margin shrinks to half their gap (0.0375).
        const string first = "ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMN";
        const string second = "ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKXYZ";
        var realms = new List<RealmEntry>
        {
            new() { Name = "Realm Alpha", Maps = new List<string> { first, second } }
        };
        // 1 edit from the first (0.975), 3 from the second (0.925): gap 0.05 is below the old
        // fixed 0.06 but well past the midpoint between the two names.
        MapMatchResult r = new MapMatchingService().Match("ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKQMN", realms);
        Assert.True(r.Success, r.Diagnostic);
        Assert.Equal(first, r.MapName);
        Assert.Equal(MatchMethod.MapName, r.Method);
    }

    // ---- A4: every close competitor is checked, not only the top two maps of each line ----

    [Theory]
    [InlineData("Ha1lowed Ground")] // ties the same-realm runner-up; database order puts it third on the line
    [InlineData("Ha1lowed Gr0und")] // strictly below the same-realm runner-up, still inside the 0.06 margin
    public void CrossRealmCompetitor_BehindASameRealmRunnerUp_StillMakesItAmbiguous(string betaMap)
    {
        // The same-realm runner-up 'Hallowed Gr0und' passes its own adaptive margin, but it must
        // not hide the close map of another realm from the ambiguity check.
        MapMatchResult r = new MapMatchingService().Match("HALLOWED GROUND", ShadowedCompetitorRealms(betaMap));
        Assert.False(r.Success);
        Assert.Null(r.RealmName);
        Assert.Contains("Ambiguous", r.Diagnostic);
        Assert.Contains(betaMap, r.Diagnostic);
    }

    [Theory]
    [InlineData("Ha1lowed Ground")]
    [InlineData("Ha1lowed Gr0und")]
    public void CrossRealmCompetitor_BehindASameRealmRunnerUp_IsSettledByTheRealmLine(string betaMap)
    {
        MapMatchResult r = new MapMatchingService().Match("HALLOWED GROUND\nREALM BETA", ShadowedCompetitorRealms(betaMap));
        Assert.True(r.Success, r.Diagnostic);
        Assert.Null(r.MapName);
        Assert.Equal("Realm Beta", r.RealmName);
        Assert.Equal(MatchMethod.RealmName, r.Method);
        Assert.Equal(Math.Round(1.0 * 0.95, 4), r.Confidence);
    }

    // ---- A5: the map line and the realm line are cross-checked ----

    [Fact]
    public void RealmLineContradictingMapLine_Fails()
    {
        var (matcher, realms) = Setup();
        // Map line: Fractured Cowshed (Coldwind Farm) at 94%; realm line: Backwater Swamp at 100%.
        MapMatchResult r = matcher.Match("FRACTURED COWSHEX\nBACKWATER SWAMP", realms);
        Assert.False(r.Success);
        Assert.Null(r.RealmName);
        Assert.Contains("disagree", r.Diagnostic);
    }

    [Fact]
    public void RealmLineAgreeingWithMapLine_Succeeds()
    {
        var (matcher, realms) = Setup();
        MapMatchResult r = matcher.Match("FRACTURED COWSHEX\nCOLDWIND FARM", realms);
        Assert.True(r.Success, r.Diagnostic);
        Assert.Equal("Fractured Cowshed", r.MapName);
        Assert.Equal("Coldwind Farm", r.RealmName);
    }

    [Fact]
    public void WeakerContradictingRealmLine_DoesNotVetoAStrongerMapRead()
    {
        var (matcher, realms) = Setup();
        // Exact map read (100%) beats a 93% read of another realm's name.
        MapMatchResult r = matcher.Match("FRACTURED COWSHED\nBACKWATER SWAMX", realms);
        Assert.True(r.Success, r.Diagnostic);
        Assert.Equal("Coldwind Farm", r.RealmName);
    }

    [Theory]
    [InlineData("HALLOWED GROUND\nREALM ALPHA", "Realm Alpha")]
    [InlineData("HALLOWED GROUND\nREALM BETA", "Realm Beta")]
    public void CrossRealmAmbiguity_IsSettledByTheRealmLine(string ocr, string expectedRealm)
    {
        var (matcher, _) = Setup();
        MapMatchResult r = matcher.Match(ocr, AmbiguousRealms());
        Assert.True(r.Success, r.Diagnostic);
        Assert.Null(r.MapName);
        Assert.Equal(expectedRealm, r.RealmName);
        Assert.Equal(MatchMethod.RealmName, r.Method);
        Assert.Equal(Math.Round(1.0 * 0.95, 4), r.Confidence); // realm score × 0.95, as for realm-only
    }

    [Theory]
    [InlineData("HALLOWED GROUND\nREALM GAMMA")] // realm line names neither candidate
    [InlineData("HALLOWED GROUND\nREALM ALPHA\nREALM BETA")] // realm lines name both candidates
    [InlineData("HALLOWED GROUND\nREALM ALPXX")] // realm line below the realm threshold
    public void CrossRealmAmbiguity_WithoutAClearRealmLine_StillFails(string ocr)
    {
        var (matcher, _) = Setup();
        MapMatchResult r = matcher.Match(ocr, AmbiguousRealms());
        Assert.False(r.Success);
        Assert.Contains("Ambiguous", r.Diagnostic);
    }

    // ---- A6: same-realm ambiguity keeps succeeding but says when it is weak ----

    [Fact]
    public void SameRealmAmbiguity_ConfidenceFormula_AndWeakDiagnostic()
    {
        var realms = new List<RealmEntry>
        {
            new() { Name = "Realm Alpha", Maps = new List<string> { "Northern Quarry Yard", "Northern Quarry Ward" } }
        };
        const string ocr = "NXRTHXRN QXARRY XARD"; // 4 edits from both names
        var matcher = new MapMatchingService();
        MapMatchResult r = matcher.Match(ocr, realms);

        string line = TextNormalizationService.Normalize(ocr);
        double top = StringSimilarity.BestSimilarity(line, "NORTHERN QUARRY YARD");
        double runnerUp = StringSimilarity.BestSimilarity(line, "NORTHERN QUARRY WARD");
        Assert.True(r.Success, r.Diagnostic);
        Assert.Null(r.MapName);
        Assert.Equal("Realm Alpha", r.RealmName);
        Assert.Equal(MatchMethod.RealmName, r.Method);
        Assert.Equal(Math.Round(Math.Min(top, runnerUp) * 0.95, 4), r.Confidence);
        Assert.True(r.Confidence < matcher.MapAcceptThreshold);
        Assert.Contains("weak", r.Diagnostic);
    }

    [Fact]
    public void SameRealmAmbiguity_StrongRead_IsNotFlaggedWeak()
    {
        var (matcher, realms) = Setup();
        MapMatchResult r = matcher.Match("RACCOON CITY POLICE STATION EEST WING", realms);
        Assert.True(r.Confidence >= matcher.MapAcceptThreshold);
        Assert.DoesNotContain("weak", r.Diagnostic);
    }

    private static List<RealmEntry> AmbiguousRealms() => new()
    {
        new() { Name = "Realm Alpha", Maps = new List<string> { "Hallowed Ground" } },
        new() { Name = "Realm Beta", Maps = new List<string> { "Ha1lowed Ground" } },
        new() { Name = "Realm Gamma", Maps = new List<string> { "Blood Lodge" } }
    };

    /// <summary>
    /// Realm Alpha has the exact map plus a near-identical sibling (O/0, 1 - 0.4/15) that outranks
    /// or ties Realm Beta's close map, so a top-two-per-line collection would never see Beta.
    /// </summary>
    private static List<RealmEntry> ShadowedCompetitorRealms(string betaMap) => new()
    {
        new() { Name = "Realm Alpha", Maps = new List<string> { "Hallowed Ground", "Hallowed Gr0und" } },
        new() { Name = "Realm Beta", Maps = new List<string> { betaMap } }
    };
}
