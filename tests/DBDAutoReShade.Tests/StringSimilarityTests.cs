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
}
