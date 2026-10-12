using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class DetectionAggregatorTests
{
    private const string Coldwind = "Coldwind Farm";
    private const string Autohaven = "Autohaven Wreckers";

    private static MapMatchResult Hit(string realm, double conf, string? map = null) =>
        new(true, map, realm, conf, map is null ? MatchMethod.RealmName : MatchMethod.MapName);

    private static MapMatchResult Miss() => MapMatchResult.Failure("No readable text.");

    /// <summary>Feed samples, checking for an early result after each one like the orchestrator does.</summary>
    private static DetectionResult? Feed(DetectionAggregator agg, params MapMatchResult[] samples)
    {
        foreach (MapMatchResult s in samples)
        {
            agg.AddSample(s);
            if (agg.TryGetEarlyResult(out DetectionResult? r))
            {
                return r;
            }
        }

        return null;
    }

    [Fact]
    public void SingleStrongSample_ConfirmsImmediately()
    {
        var agg = new DetectionAggregator();
        agg.AddSample(Hit(Coldwind, 0.95, "Fractured Cowshed"));
        Assert.True(agg.TryGetEarlyResult(out DetectionResult? r));
        Assert.True(r!.Success);
        Assert.Equal("Fractured Cowshed", r.MapName);
    }

    [Fact]
    public void TwoAgreeingSamples_Confirm()
    {
        var agg = new DetectionAggregator();
        agg.AddSample(Hit(Coldwind, 0.82, "Fractured Cowshed"));
        Assert.False(agg.TryGetEarlyResult(out _)); // one weak sample is not enough
        agg.AddSample(Hit(Coldwind, 0.84, "Fractured Cowshed"));
        Assert.True(agg.TryGetEarlyResult(out DetectionResult? r));
        Assert.True(r!.Success);
        Assert.True(r.Confidence >= 0.84); // agreement bonus
    }

    [Fact]
    public void DisagreeingSamples_DoNotConfirm()
    {
        var agg = new DetectionAggregator();
        agg.AddSample(Hit(Coldwind, 0.82));
        agg.AddSample(Hit(Autohaven, 0.83));
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
        agg.AddSample(Hit(Coldwind, 0.80));
        agg.AddSample(Hit(Coldwind, 0.81));
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

    [Fact]
    public void DefaultPolicy_UsesEightSampleWindow()
    {
        Assert.Equal(8, new DetectionPolicy().WindowSamples);
    }

    [Fact]
    public void ABA_ConfirmsTheRealmWithStrictlyMoreReads()
    {
        var agg = new DetectionAggregator();
        DetectionResult? r = Feed(agg, Hit(Coldwind, 0.82), Hit(Autohaven, 0.83), Hit(Coldwind, 0.84));

        Assert.NotNull(r);
        Assert.Equal(Coldwind, r.RealmName);
        Assert.Equal(3, r.SampleCount);
        Assert.Equal(0.86, r.Confidence, 4); // best (0.84) + 0.02 for the second agreeing read
    }

    [Fact]
    public void TiedContradictingGroups_AreVetoed()
    {
        var agg = new DetectionAggregator();
        // A, B, A, B evaluated only at the end: 2 vs 2 must not confirm either realm.
        agg.AddSample(Hit(Coldwind, 0.85));
        agg.AddSample(Hit(Autohaven, 0.84));
        agg.AddSample(Hit(Coldwind, 0.86));
        agg.AddSample(Hit(Autohaven, 0.85));

        Assert.False(agg.TryGetEarlyResult(out _));
        Assert.False(agg.Finalize().Success);
    }

    [Fact]
    public void StrongSingleSample_IsVetoed_ByAnotherRealmInTheWindow()
    {
        var agg = new DetectionAggregator();
        // Previously the 0.95 read confirmed Autohaven despite the contradicting Coldwind read.
        Assert.Null(Feed(agg, Hit(Coldwind, 0.82), Hit(Autohaven, 0.95)));

        // A second Autohaven read gives it strictly more agreement: confirmed.
        DetectionResult? r = Feed(agg, Hit(Autohaven, 0.90));
        Assert.NotNull(r);
        Assert.Equal(Autohaven, r.RealmName);
    }

    [Fact]
    public void StrongSingleSample_Confirms_OnceTheContradictionLeftTheWindow()
    {
        var agg = new DetectionAggregator();
        agg.AddSample(Hit(Coldwind, 0.82));
        for (int i = 0; i < 7; i++)
        {
            agg.AddSample(Miss());
        }

        // Window (8) is now: 7 misses + the strong read; the Coldwind read expired.
        DetectionResult? r = Feed(agg, Hit(Autohaven, 0.95));
        Assert.NotNull(r);
        Assert.Equal(Autohaven, r.RealmName);
        Assert.Equal(0.95, r.Confidence, 4);
        Assert.Equal(9, r.SampleCount); // count covers every sample, not just the window
    }

    [Fact]
    public void SampleOutsideWindow_DoesNotCountTowardAgreement()
    {
        var agg = new DetectionAggregator();
        MapMatchResult[] samples = new[] { Hit(Coldwind, 0.82) }
            .Concat(Enumerable.Repeat(Miss(), 7))
            .Append(Hit(Coldwind, 0.82))
            .ToArray();

        Assert.Null(Feed(agg, samples)); // first read is 9 samples back: expired
        Assert.False(agg.Finalize().Success);
    }

    [Fact]
    public void SamplesAtWindowEdge_StillAgree()
    {
        var agg = new DetectionAggregator();
        MapMatchResult[] samples = new[] { Hit(Coldwind, 0.82) }
            .Concat(Enumerable.Repeat(Miss(), 6))
            .Append(Hit(Coldwind, 0.82))
            .ToArray();

        DetectionResult? r = Feed(agg, samples); // exactly 8 samples: both reads inside
        Assert.NotNull(r);
        Assert.Equal(Coldwind, r.RealmName);
    }

    [Fact]
    public void LongScan_WeakHitsMinutesApart_DoNotConfirm()
    {
        // Continuous scan: a weak Coldwind read, then a long stretch of other reads
        // (misses and a contradicting realm), then another weak Coldwind read.
        var agg = new DetectionAggregator();
        var samples = new List<MapMatchResult> { Hit(Coldwind, 0.80) };
        for (int i = 0; i < 60; i++)
        {
            samples.Add(i % 10 == 5 ? Hit(Autohaven, 0.80) : Miss());
        }

        samples.Add(Hit(Coldwind, 0.80));
        Assert.Null(Feed(agg, samples.ToArray()));
    }

    [Fact]
    public void AgreementBonus_IsCappedAt099()
    {
        var agg = new DetectionAggregator();
        agg.AddSample(Hit(Coldwind, 0.985, "Fractured Cowshed"));
        agg.AddSample(Hit(Coldwind, 0.98, "Fractured Cowshed"));
        agg.AddSample(Hit(Coldwind, 0.97));

        Assert.True(agg.TryGetEarlyResult(out DetectionResult? r));
        Assert.Equal(0.99, r!.Confidence, 4);
        Assert.Equal("Fractured Cowshed", r.MapName);
    }

    [Fact]
    public void IsAccepted_RequiresSuccessRealmAndAcceptConfidence()
    {
        var policy = new DetectionPolicy();
        Assert.True(DetectionAggregator.IsAccepted(Hit(Coldwind, 0.78), policy));
        Assert.False(DetectionAggregator.IsAccepted(Hit(Coldwind, 0.77), policy));
        Assert.False(DetectionAggregator.IsAccepted(Hit(" ", 0.95), policy));
        Assert.False(DetectionAggregator.IsAccepted(Miss(), policy));
        Assert.False(DetectionAggregator.IsAccepted(null, policy));
    }
}
