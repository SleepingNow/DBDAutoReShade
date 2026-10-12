using System.Buffers.Binary;
using System.Text;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// The pure 16-bit PCM WAV math behind the Great skill-check sound: parsing,
/// the volume² gain with exact rounding and a clipping guard, re-encoding with
/// a correct header, and the shipped asset itself (-1.0 dBFS peak, no clipping).
/// No audio device is involved.
/// </summary>
public sealed class WavPcm16Tests
{
    /// <summary>round(32768 × 10^(-1/20)): a -1.0 dBFS peak in 16-bit samples.</summary>
    private const int MinusOneDbfsPeak = 29205;

    private static string AssetsDir => Path.Combine(RepoRoot(), "src", "DBDAutoReShade.App", "Assets");

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "DBDAutoReShade.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>A RIFF/WAVE file from raw chunks (id + body; odd bodies get their pad byte).</summary>
    private static byte[] Riff(params (string Id, byte[] Body)[] chunks)
    {
        using var ms = new MemoryStream();
        ms.Write("RIFF"u8);
        ms.Write(new byte[4]); // size, patched below
        ms.Write("WAVE"u8);
        foreach (var (id, body) in chunks)
        {
            ms.Write(Encoding.ASCII.GetBytes(id));
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
            ms.Write(size);
            ms.Write(body);
            if (body.Length % 2 == 1)
            {
                ms.WriteByte(0);
            }
        }

        byte[] wav = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
        return wav;
    }

    private static byte[] Fmt(ushort tag, ushort channels, uint rate, ushort bits, ushort? blockAlign = null, int extra = 0)
    {
        var fmt = new byte[16 + extra];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), rate);
        ushort align = blockAlign ?? (ushort)(channels * bits / 8);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), rate * align);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), align);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), bits);
        return fmt;
    }

    private static byte[] Samples(params short[] samples)
    {
        var data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
        }

        return data;
    }

    // ---------- Encoding ----------

    [Fact]
    public void Encode_WritesACanonicalPcm16Header()
    {
        byte[] wav = WavPcm16.Encode(44100, 2, new short[] { 1, -1, 32767, -32768 });

        Assert.Equal(44 + 8, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36u + 8u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20))); // PCM
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22))); // channels
        Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(44100u * 4, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(28))); // byte rate
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(32))); // block align
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34))); // bits
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(new byte[] { 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0x7F, 0x00, 0x80 }, wav[44..]);
    }

    [Fact]
    public void EncodeThenParse_RoundTrips()
    {
        short[] samples = { 0, 100, -100, 32767, -32768, 12345, -54 };
        Pcm16Audio parsed = WavPcm16.Parse(WavPcm16.Encode(new Pcm16Audio(22050, 1, samples)));

        Assert.Equal(22050, parsed.SampleRate);
        Assert.Equal(1, parsed.Channels);
        Assert.Equal(samples, parsed.Samples);
        Assert.Equal(TimeSpan.FromSeconds(7.0 / 22050), parsed.Duration);
    }

    [Fact]
    public void Encode_RejectsPartialFrames()
    {
        Assert.Throws<ArgumentException>(() => WavPcm16.Encode(44100, 2, new short[] { 1, 2, 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => WavPcm16.Encode(0, 1, new short[] { 1 }));
    }

    // ---------- Parsing ----------

    [Fact]
    public void Parse_SkipsOtherChunks_AndTheirPadBytes()
    {
        byte[] wav = Riff(
            ("LIST", new byte[] { 1, 2, 3 }), // odd-sized: one pad byte follows
            ("fmt ", Fmt(1, 1, 48000, 16)),
            ("fact", new byte[4]),
            ("data", Samples(7, -7, 300)));

        Pcm16Audio audio = WavPcm16.Parse(wav);

        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal(new short[] { 7, -7, 300 }, audio.Samples);
    }

    [Fact]
    public void Parse_AcceptsExtensiblePcm()
    {
        byte[] fmt = Fmt(0xFFFE, 2, 44100, 16, extra: 24);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(16), 22); // cbSize
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(24), 1); // subformat: KSDATAFORMAT_SUBTYPE_PCM

        Pcm16Audio audio = WavPcm16.Parse(Riff(("fmt ", fmt), ("data", Samples(1, 2, 3, 4))));

        Assert.Equal(2, audio.Channels);
        Assert.Equal(new short[] { 1, 2, 3, 4 }, audio.Samples);
    }

    [Fact]
    public void Parse_ReadsATruncatedDataChunk_InWholeFrames()
    {
        byte[] wav = Riff(("fmt ", Fmt(1, 2, 44100, 16)), ("data", Samples(1, 2, 3, 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), 1000); // claims more than there is
        byte[] cut = wav[..^2]; // and the last sample is half there

        Assert.Equal(new short[] { 1, 2 }, WavPcm16.Parse(cut).Samples);
    }

    public static TheoryData<string, byte[]> Unsupported => new()
    {
        { "Not a RIFF/WAVE file.", Encoding.ASCII.GetBytes("ID3 this is an mp3 file") },
        { "only PCM", Riff(("fmt ", Fmt(3, 1, 44100, 32)), ("data", new byte[8])) }, // IEEE float
        { "8-bit", Riff(("fmt ", Fmt(1, 1, 44100, 8)), ("data", new byte[8])) },
        { "24-bit", Riff(("fmt ", Fmt(1, 1, 44100, 24)), ("data", new byte[6])) },
        { "no data chunk", Riff(("fmt ", Fmt(1, 1, 44100, 16))) },
        { "no format chunk", Riff(("data", Samples(1, 2))) },
        { "block alignment", Riff(("fmt ", Fmt(1, 2, 44100, 16, blockAlign: 2)), ("data", Samples(1, 2))) },
        { "truncated", Riff(("fmt ", new byte[10]), ("data", Samples(1, 2))) },
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void Parse_RejectsWhatItCannotPlay_SayingWhy(string reason, byte[] wav)
    {
        Assert.False(WavPcm16.TryParse(wav, out Pcm16Audio? audio, out string? error));
        Assert.Null(audio);
        Assert.Contains(reason, error);
        Assert.Throws<InvalidDataException>(() => WavPcm16.Parse(wav));
    }

    // ---------- Gain ----------

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.25)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.1, 0.010000000000000002)] // exactly 0.1 * 0.1 in doubles
    [InlineData(1.5, 1.0)]
    [InlineData(-0.3, 0.0)]
    [InlineData(double.NaN, 0.0)]
    public void VolumeToGain_IsVolumeSquared_Clamped(double volume, double gain)
    {
        Assert.Equal(gain, WavPcm16.VolumeToGain(volume));
    }

    [Fact]
    public void HalfVolume_OfTheMinusOneDbfsAsset_IsAboutMinus13Dbfs()
    {
        double halfGainDb = 20 * Math.Log10(WavPcm16.VolumeToGain(0.5));
        Assert.Equal(-12.041, halfGainDb, 3);

        int peakAtHalf = WavPcm16.Peak(WavPcm16.Scale(new short[] { MinusOneDbfsPeak, -1000 }, WavPcm16.VolumeToGain(0.5)));
        Assert.Equal(7301, peakAtHalf); // 29205 × 0.25 = 7301.25
        Assert.Equal(-13.04, WavPcm16.ToDbfs(peakAtHalf), 2);
    }

    [Theory]
    [InlineData(1000, 0.25, 250)]
    [InlineData(3, 0.25, 1)] // 0.75 → 1
    [InlineData(2, 0.25, 1)] // 0.5 → 1 (half away from zero)
    [InlineData(-2, 0.25, -1)] // -0.5 → -1
    [InlineData(1, 0.25, 0)] // 0.25 → 0
    [InlineData(-32768, 0.25, -8192)]
    [InlineData(32767, 0.25, 8192)] // 8191.75
    [InlineData(12345, 1.0, 12345)] // unity: unchanged
    [InlineData(12345, 0.0, 0)]
    [InlineData(20000, 2.0, 32767)] // clipping guard: never wraps
    [InlineData(-20000, 2.0, -32768)]
    [InlineData(16384, 2.0, 32767)] // exactly 32768 → clipped to 32767
    public void ScaleSample_RoundsHalfAwayFromZero_AndClips(short sample, double gain, short expected)
    {
        Assert.Equal(expected, WavPcm16.ScaleSample(sample, gain));
        Assert.Equal(new[] { expected }, WavPcm16.Scale(new[] { sample }, gain));
    }

    [Fact]
    public void Scale_RejectsNegativeOrNonFiniteGains()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WavPcm16.Scale(new short[] { 1 }, -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WavPcm16.Scale(new short[] { 1 }, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => WavPcm16.Scale(new short[] { 1 }, double.PositiveInfinity));
    }

    [Fact]
    public void Peak_AndDbfs()
    {
        Assert.Equal(0, WavPcm16.Peak(ReadOnlySpan<short>.Empty));
        Assert.Equal(32768, WavPcm16.Peak(new short[] { 5, -32768, 32767 }));
        Assert.Equal(0.0, WavPcm16.ToDbfs(32768), 10);
        Assert.Equal(double.NegativeInfinity, WavPcm16.ToDbfs(0));
        Assert.Equal(MinusOneDbfsPeak, (int)Math.Round(WavPcm16.FromDbfs(-1.0), MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void NormalizePeak_HitsTheTargetExactly_WithOneLinearGain()
    {
        // A -12 dBFS source like the one the asset was made from.
        short[] source = { 0, 8229, -8229, 4000, -100, 1 };
        var audio = new Pcm16Audio(44100, 1, source);
        double gain = WavPcm16.GainForPeak(8229, -1.0);

        Pcm16Audio normalized = WavPcm16.NormalizePeak(audio, -1.0);

        Assert.Equal(WavPcm16.FromDbfs(-1.0) / 8229, gain);
        Assert.Equal(MinusOneDbfsPeak, WavPcm16.Peak(normalized.Samples));
        Assert.Equal(source.Select(s => WavPcm16.ScaleSample(s, gain)), normalized.Samples);
        Assert.Equal(new short[] { 0, 29205, -29205, 14196, -355, 4 }, normalized.Samples);
        Assert.Equal(44100, normalized.SampleRate);
        Assert.Equal(1, normalized.Channels);
    }

    [Fact]
    public void NormalizePeak_LeavesSilenceAlone()
    {
        var silent = new Pcm16Audio(44100, 1, new short[16]);

        Assert.Same(silent, WavPcm16.NormalizePeak(silent, -1.0));
    }

    // ---------- The shipped asset ----------

    [Fact]
    public void GreatSound_Is16BitPcm44kHz_PeakingAtMinusOneDbfs_WithoutClipping()
    {
        string path = Path.Combine(AssetsDir, "Sounds", "great.wav");
        byte[] wav = File.ReadAllBytes(path);

        Pcm16Audio great = WavPcm16.Parse(wav);

        Assert.Equal(44100, great.SampleRate);
        Assert.Equal(1, great.Channels);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20))); // plain PCM
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));
        Assert.InRange(great.Duration, TimeSpan.FromSeconds(0.3), TimeSpan.FromSeconds(3));

        int peak = WavPcm16.Peak(great.Samples);
        Assert.Equal(MinusOneDbfsPeak, peak);
        Assert.Equal(-1.0, WavPcm16.ToDbfs(peak), 3);
        Assert.DoesNotContain(great.Samples, s => s is short.MaxValue or short.MinValue); // no clipped sample

        // Its canonical encoding is the file itself (nothing but fmt + data).
        Assert.Equal(wav, WavPcm16.Encode(great));
    }

    [Fact]
    public void GreatSound_TheMinus12DbfsSource_IsGone_AndTheCreditsAreThere()
    {
        Assert.False(File.Exists(Path.Combine(AssetsDir, "Sounds", "great-source-12dbfs.wav")));

        string credits = File.ReadAllText(Path.Combine(AssetsDir, "CREDITS.md"));
        Assert.Contains("Behaviour Interactive", credits);
        Assert.Contains("deadbydaylight.wiki.gg", credits);
        Assert.Contains("SIL Open Font License, Version 1.1", credits);
        Assert.Contains("original synthesized recreation", credits);
    }
}
