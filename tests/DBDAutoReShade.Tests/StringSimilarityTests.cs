using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class StringSimilarityTests
{
    [Fact]
    public void ExactMatch_IsOne()
    {
        Assert.Equal(1.0, StringSimilarity.Similarity("FRACTURED COWSHED", "FRACTURED COWSHED"));
    }

    [Theory]
    [InlineData("FRACTURED C0WSHED", "FRACTURED COWSHED")] // O/0 confusion
    [InlineData("FATHER CAMPBEILS CHAPEL", "FATHER CAMPBELLS CHAPEL")] // I/L confusion
    [InlineData("FRACTURED COWSHED", "FRACTURED COWSHED")] // sanity
    public void CommonOcrErrors_ScoreHigh(string observed, string candidate)
    {
        Assert.True(StringSimilarity.BestSimilarity(observed, candidate) >= 0.9,
            $"BestSimilarity({observed}, {candidate}) was too low.");
    }

    [Fact]
    public void MinorSpellingError_ScoresAboveAcceptThreshold()
    {
        double sim = StringSimilarity.BestSimilarity("FRACTURED COWSHEDD", "FRACTURED COWSHED");
        Assert.True(sim >= 0.9, $"Similarity was {sim}.");
    }

    [Fact]
    public void UnrelatedStrings_ScoreLow()
    {
        Assert.True(StringSimilarity.Similarity("BLOOD LODGE", "COAL TOWER") < 0.5);
        Assert.True(StringSimilarity.Similarity("THE GAME", "GARDEN OF JOY") < 0.5);
    }

    [Fact]
    public void EmptyStrings_AreIdentical()
    {
        Assert.Equal(1.0, StringSimilarity.Similarity("", ""));
    }

    [Fact]
    public void NearIdenticalMapNames_MaxScoreGapIsBelowTheDefaultMargin()
    {
        // Why the ambiguity margin must adapt: no read can put more than the names' own
        // distance (2/37 ≈ 0.054) between the two wings, which is less than 0.06.
        const string east = "RACCOON CITY POLICE STATION EAST WING";
        const string west = "RACCOON CITY POLICE STATION WEST WING";
        double nameGap = 1.0 - StringSimilarity.Similarity(east, west);
        Assert.Equal(2.0 / 37, nameGap, 10);
        Assert.True(nameGap < new MapMatchingService().AmbiguityMargin);

        double exactReadGap = StringSimilarity.BestSimilarity(east, east) - StringSimilarity.BestSimilarity(east, west);
        Assert.Equal(nameGap, exactReadGap, 10);
    }
}
