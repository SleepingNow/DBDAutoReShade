using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class TextNormalizationTests
{
    [Theory]
    [InlineData("Fractured Cowshed", "FRACTURED COWSHED")]
    [InlineData("  fractured   cowshed\n", "FRACTURED COWSHED")]
    [InlineData("Father Campbell's Chapel", "FATHER CAMPBELLS CHAPEL")]
    [InlineData("Father Campbell’s Chapel", "FATHER CAMPBELLS CHAPEL")] // curly apostrophe
    [InlineData("Léry's Memorial Institute", "LERYS MEMORIAL INSTITUTE")] // diacritics
    [InlineData("Wreckers' Yard!", "WRECKERS YARD")]
    [InlineData("Raccoon City Police-Station / East Wing", "RACCOON CITY POLICE STATION EAST WING")]
    public void Normalize_FoldsVariants(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizationService.Normalize(input));
    }

    [Fact]
    public void Normalize_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TextNormalizationService.Normalize(null));
        Assert.Equal(string.Empty, TextNormalizationService.Normalize("   "));
    }

    [Fact]
    public void SplitCandidateLines_DropsDigitHeavyLines()
    {
        var lines = TextNormalizationService.SplitCandidateLines("FRACTURED COWSHED\n12:34\n100%\nAB");
        Assert.Equal(new[] { "FRACTURED COWSHED" }, lines);
    }

    [Fact]
    public void ConfusionVariants_CoversRnAndVv()
    {
        var variants = TextNormalizationService.ConfusionVariants("MORNG VV").ToList();
        Assert.Contains("MORNG VV", variants);
        Assert.Contains("MOMG VV", variants);
        Assert.Contains("MORNG W", variants);
    }
}
