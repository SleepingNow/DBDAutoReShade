using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// <see cref="SoundEffectsService"/> through a recording player (no audio
/// device): the asset is read once, scaled by volume², re-encoded and handed
/// over as one voice; silence is skipped; nothing ever throws.
/// </summary>
public sealed class SoundEffectsServiceTests
{
    private static readonly short[] Source = { 0, 29205, -29205, 1000, -3, 2 };

    /// <summary>Records what it is asked to play; plays nothing.</summary>
    private sealed class RecordingPlayer : IWavePlayer
    {
        public List<byte[]> Played { get; } = new();

        public bool Result { get; set; } = true;

        public Exception? Throw { get; set; }

        public bool Disposed { get; private set; }

        public bool Play(byte[] wav)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Played.Add(wav);
            return Result;
        }

        public void Dispose() => Disposed = true;
    }

    private static (SoundEffectsService Sounds, RecordingPlayer Player, Func<int> Opens) Create(Func<Stream?>? open = null)
    {
        int opens = 0;
        byte[] asset = WavPcm16.Encode(44100, 1, Source);
        var player = new RecordingPlayer();
        var sounds = new SoundEffectsService(() =>
        {
            opens++;
            return open is null ? new MemoryStream(asset) : open();
        }, player);
        return (sounds, player, () => opens);
    }

    [Fact]
    public void FullVolume_PlaysTheAssetUnchanged()
    {
        var (sounds, player, _) = Create();

        Assert.True(sounds.PlayGreat(1.0));

        Pcm16Audio played = WavPcm16.Parse(Assert.Single(player.Played));
        Assert.Equal(Source, played.Samples);
        Assert.Equal(44100, played.SampleRate);
    }

    [Fact]
    public void HalfVolume_ScalesEverySampleByAQuarter()
    {
        var (sounds, player, _) = Create();

        Assert.True(sounds.PlayGreat(0.5));

        Pcm16Audio played = WavPcm16.Parse(Assert.Single(player.Played));
        Assert.Equal(new short[] { 0, 7301, -7301, 250, -1, 1 }, played.Samples); // round(s × 0.25), half away from zero
        Assert.Equal(-13.04, WavPcm16.ToDbfs(WavPcm16.Peak(played.Samples)), 2);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    public void Silence_IsSkipped(double volume)
    {
        var (sounds, player, opens) = Create();

        Assert.False(sounds.PlayGreat(volume));

        Assert.Empty(player.Played);
        Assert.Equal(0, opens()); // not even loaded
    }

    [Fact]
    public void AboveFullVolume_PlaysAtFullVolume()
    {
        var (sounds, player, _) = Create();

        Assert.True(sounds.PlayGreat(3.0));

        Assert.Equal(Source, WavPcm16.Parse(Assert.Single(player.Played)).Samples);
    }

    [Fact]
    public void TheAsset_IsReadOnce()
    {
        var (sounds, player, opens) = Create();

        Assert.True(sounds.Preload());
        Assert.True(sounds.PlayGreat(0.8));
        Assert.True(sounds.PlayGreat(0.2));

        Assert.Equal(1, opens());
        Assert.Equal(2, player.Played.Count); // each play replaces the previous voice (the player stops it)
        Assert.Null(sounds.LoadError);
    }

    public static TheoryData<string, Func<Stream?>> BrokenAssets => new()
    {
        { "missing", () => null },
        { "throws", () => throw new IOException("Cannot locate resource 'assets/sounds/great.wav'.") },
        { "garbage", () => new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }) },
    };

    [Theory]
    [MemberData(nameof(BrokenAssets))]
    public void ABrokenAsset_PlaysNothing_NeverThrows_AndIsNotRetried(string what, Func<Stream?> open)
    {
        var (sounds, player, opens) = Create(open);

        Assert.False(sounds.Preload(), what);
        Assert.False(sounds.PlayGreat(1.0));
        Assert.False(sounds.PlayGreat(0.5));

        Assert.Empty(player.Played);
        Assert.Equal(1, opens());
        Assert.NotNull(sounds.LoadError);
    }

    [Fact]
    public void APlayerFailure_ReturnsFalse_NeverThrows()
    {
        var (sounds, player, _) = Create();

        player.Result = false; // e.g. no audio device
        Assert.False(sounds.PlayGreat(1.0));

        player.Throw = new InvalidOperationException("winmm unavailable");
        Assert.False(sounds.PlayGreat(1.0));

        player.Throw = null;
        player.Result = true;
        Assert.True(sounds.PlayGreat(1.0)); // and it recovers
    }

    [Fact]
    public void AfterDispose_NothingPlays()
    {
        var (sounds, player, _) = Create();
        Assert.True(sounds.PlayGreat(1.0));

        sounds.Dispose();
        sounds.Dispose(); // idempotent

        Assert.True(player.Disposed);
        Assert.False(sounds.PlayGreat(1.0));
        Assert.False(sounds.Preload());
        Assert.Single(player.Played);
    }

    [Fact]
    public async Task ConcurrentPlays_NeverThrow_AndAllReachThePlayerOneAtATime()
    {
        var (sounds, player, opens) = Create();

        bool[] results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => sounds.PlayGreat((i % 10 + 1) / 10.0))));

        Assert.All(results, Assert.True);
        Assert.Equal(32, player.Played.Count); // List<T> isn't thread-safe: a lost add would show here
        Assert.Equal(1, opens());
    }

    [Fact]
    public void NullSoundEffects_PlaysNothing_AndSaysSo()
    {
        Assert.False(NullSoundEffects.Instance.PlayGreat(1.0));
        Assert.Equal(NullSoundEffects.NoSoundOutput, NullSoundEffects.Instance.GreatSoundProblem());
    }

    [Fact]
    public void GreatSoundProblem_IsNullWhileTheSoundCanPlay_LoadingItFirst()
    {
        var (sounds, player, opens) = Create();

        Assert.Null(sounds.GreatSoundProblem()); // loads it: the ReShade check runs before any map was found
        Assert.Equal(1, opens());
        Assert.True(sounds.PlayGreat(1.0));
        Assert.Null(sounds.GreatSoundProblem());
        Assert.Equal(1, opens());
        Assert.Single(player.Played);
    }

    [Theory]
    [MemberData(nameof(BrokenAssets))]
    public void GreatSoundProblem_SaysWhyABrokenAssetCantPlay(string what, Func<Stream?> open)
    {
        var (sounds, _, opens) = Create(open);

        string? problem = sounds.GreatSoundProblem();

        Assert.NotNull(problem);
        Assert.Equal(sounds.LoadError, problem);
        Assert.Equal(1, opens());
        if (what == "throws")
        {
            Assert.Contains("Cannot locate resource", problem); // e.g. the csproj lost its Resource item
        }
    }

    [Fact]
    public void GreatSoundProblem_AfterDispose_SaysThePlayerIsShutDown()
    {
        var (sounds, _, _) = Create();
        sounds.Dispose();

        Assert.Equal("The sound player has been shut down.", sounds.GreatSoundProblem());
    }

    [Fact]
    public void GreatSoundProblem_DefaultsToNull_ForSoundEffectsThatDontSay()
    {
        ISoundEffects quiet = new SilentEffects();

        Assert.Null(quiet.GreatSoundProblem());
    }

    private sealed class SilentEffects : ISoundEffects
    {
        public bool PlayGreat(double volume) => false;
    }
}
