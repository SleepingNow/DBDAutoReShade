using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class DetectionAggregatorTests
{
    private static MapMatchResult Hit(string realm, double conf, string? map = null) =>
        new(true, map, realm, conf, map is null ? MatchMethod.RealmName : MatchMethod.MapName);

    [Fact]
    public void SingleStrongSample_ConfirmsImmediately()
    {
        var agg = new DetectionAggregator();
        agg.AddSample(Hit("Coldwind Farm", 0.95, "Fractured Cowshed"));
        Assert.True(agg.TryGetEarlyResult(out DetectionResult? r));
        Assert.True(r!.Success);
        Assert.Equal("Fractured Cowshed", r.MapName);
    }

    [Fact]
    public void TwoAgreeingSamples_Confirm()
    {
        var agg = new DetectionAggregator();
        agg.AddSample(Hit("Coldwind Farm", 0.82, "Fractured Cowshed"));
        Assert.False(agg.TryGetEarlyResult(out _)); // one weak sample is not enough
        agg.AddSample(Hit("Coldwind Farm", 0.84, "Fractured Cowshed"));
        Assert.True(agg.TryGetEarlyResult(out DetectionResult? r));
        Assert.True(r!.Success);
        Assert.True(r.Confidence >= 0.84); // agreement bonus
    }

    [Fact]
    public void DisagreeingSamples_DoNotConfirm()
    {
        var agg = new DetectionAggregator();
        agg.AddSample(Hit("Coldwind Farm", 0.82));
        agg.AddSample(Hit("Autohaven Wreckers", 0.83));
        Assert.False(agg.TryGetEarlyResult(out _));
        DetectionResult final = agg.Finalize();
        Assert.False(final.Success); // do not guess
    }

    [Fact]
    public void NoSamples_FinalizesAsFailure()
    {
        var agg = new DetectionAggregator();
        DetectionResult final = agg.Finalize();
        Assert.False(final.Success);
        Assert.Equal(0, final.SampleCount);
    }

    [Fact]
    public void WeakSamplesOnly_FinalizeAsFailure()
    {
        var agg = new DetectionAggregator(new DetectionPolicy(MinAgreeingSamples: 3));
        agg.AddSample(Hit("Coldwind Farm", 0.80));
        agg.AddSample(Hit("Coldwind Farm", 0.81));
        Assert.False(agg.TryGetEarlyResult(out _));
        Assert.False(agg.Finalize().Success);
    }

    [Fact]
    public void NullSamples_AreIgnored()
    {
        var agg = new DetectionAggregator();
        agg.AddSample(null);
        Assert.Equal(0, agg.SampleCount);
    }
}
