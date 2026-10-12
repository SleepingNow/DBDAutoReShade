using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace DBDAutoReShade.Core.Services;

/// <summary>16-bit PCM audio: interleaved little-endian samples and the format they play at.</summary>
public sealed class Pcm16Audio
{
    /// <param name="sampleRate">Frames per second (e.g. 44100).</param>
    /// <param name="channels">Interleaved channels (1 = mono).</param>
    /// <param name="samples">Interleaved samples; a whole number of frames.</param>
    public Pcm16Audio(int sampleRate, int channels, short[] samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length % channels != 0)
        {
            throw new ArgumentException("The samples must be a whole number of frames.", nameof(samples));
        }

        SampleRate = sampleRate;
        Channels = channels;
        Samples = samples;
    }

    /// <summary>Frames per second.</summary>
    public int SampleRate { get; }

    /// <summary>Interleaved channels.</summary>
    public int Channels { get; }

    /// <summary>Interleaved samples (not copied: treat as read-only).</summary>
    public short[] Samples { get; }

    /// <summary>Length of the sound.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / Channels / SampleRate);
}

/// <summary>
/// Pure WAV (RIFF) handling for 16-bit PCM: parsing, gain and re-encoding.
/// Levels are in dBFS relative to 16-bit full scale, 32768 (as ffmpeg and most
/// meters measure it), so a -1 dBFS peak is round(32768 × 10^(-1/20)) = 29205.
/// Gains round half away from zero and clip to the 16-bit range, never wrap.
/// </summary>
public static class WavPcm16
{
    /// <summary>16-bit full scale (0 dBFS).</summary>
    public const double FullScale = 32768.0;

    /// <summary>Size of the canonical header <see cref="Encode(int, int, ReadOnlySpan{short})"/> writes.</summary>
    public const int HeaderSize = 44;

    private const ushort FormatPcm = 1;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>Parses a RIFF/WAVE file holding 16-bit PCM; throws <see cref="InvalidDataException"/> saying why it can't.</summary>
    public static Pcm16Audio Parse(ReadOnlySpan<byte> wav) =>
        TryParse(wav, out Pcm16Audio? audio, out string? error)
            ? audio
            : throw new InvalidDataException(error);

    /// <summary>
    /// Parses a RIFF/WAVE file holding 16-bit PCM (WAVE_FORMAT_PCM, or
    /// WAVE_FORMAT_EXTENSIBLE with the PCM subformat). Chunks other than
    /// "fmt " and "data" are skipped (with their pad byte when odd-sized); a
    /// data chunk that claims more bytes than the file holds is read as far as
    /// it goes, in whole frames. False with a reason for anything else.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> wav, [NotNullWhen(true)] out Pcm16Audio? audio, out string? error)
    {
        audio = null;
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            error = "Not a RIFF/WAVE file.";
            return false;
        }

        int channels = 0;
        int sampleRate = 0;
        bool haveFormat = false;
        ReadOnlySpan<byte> data = default;
        bool haveData = false;
        int offset = 12;
        while (offset <= wav.Length - 8)
        {
            ReadOnlySpan<byte> id = wav.Slice(offset, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 4, 4));
            int body = offset + 8;
            long available = wav.Length - body;
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || size > available)
                {
                    error = "The format chunk is truncated.";
                    return false;
                }

                if (!TryReadFormat(wav.Slice(body, (int)size), out channels, out sampleRate, out error))
                {
                    return false;
                }

                haveFormat = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = wav.Slice(body, (int)Math.Min(size, available));
                haveData = true;
                break; // the samples are all that is needed after the format
            }

            long next = body + size + (size & 1); // chunks are word-aligned
            if (next > wav.Length)
            {
                break;
            }

            offset = (int)next;
        }

        if (!haveFormat)
        {
            error = "The file has no format chunk before its data.";
            return false;
        }

        if (!haveData)
        {
            error = "The file has no data chunk.";
            return false;
        }

        int frameBytes = channels * 2;
        int frames = data.Length / frameBytes;
        var samples = new short[frames * channels];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
        }

        audio = new Pcm16Audio(sampleRate, channels, samples);
        error = null;
        return true;
    }

    /// <summary>Encodes <paramref name="audio"/> as a canonical 16-bit PCM WAV file (44-byte header).</summary>
    public static byte[] Encode(Pcm16Audio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return Encode(audio.SampleRate, audio.Channels, audio.Samples);
    }

    /// <summary>
    /// Encodes interleaved samples as a canonical 16-bit PCM WAV file:
    /// "RIFF" (size = 36 + data), "WAVE", a 16-byte "fmt " chunk
    /// (WAVE_FORMAT_PCM, channels, rate, byte rate, block align, 16 bits) and
    /// one "data" chunk (plus a pad byte, never needed for 16-bit samples).
    /// </summary>
    public static byte[] Encode(int sampleRate, int channels, ReadOnlySpan<short> samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        if (samples.Length % channels != 0)
        {
            throw new ArgumentException("The samples must be a whole number of frames.", nameof(samples));
        }

        int dataBytes = checked(samples.Length * 2);
        var wav = new byte[checked(HeaderSize + dataBytes)];
        Span<byte> span = wav;
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(HeaderSize + i * 2, 2), samples[i]);
        }

        return wav;
    }

    /// <summary>
    /// The playback gain for a 0..1 volume setting: volume² (so 50 % is
    /// -12.04 dB, 100 % is unity). The volume is clamped to 0..1 first; NaN gives 0 (silent).
    /// </summary>
    public static double VolumeToGain(double volume)
    {
        if (double.IsNaN(volume))
        {
            return 0.0;
        }

        double v = Math.Clamp(volume, 0.0, 1.0);
        return v * v;
    }

    /// <summary>One sample times <paramref name="gain"/>, rounded half away from zero and clipped to −32768..32767.</summary>
    public static short ScaleSample(short sample, double gain)
    {
        double scaled = Math.Round(sample * gain, MidpointRounding.AwayFromZero);
        return scaled >= short.MaxValue ? short.MaxValue
            : scaled <= short.MinValue ? short.MinValue
            : (short)scaled;
    }

    /// <summary>Every sample times <paramref name="gain"/> (see <see cref="ScaleSample"/>); a new array.</summary>
    public static short[] Scale(ReadOnlySpan<short> samples, double gain)
    {
        if (!double.IsFinite(gain) || gain < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gain), gain, "The gain must be a finite, non-negative number.");
        }

        var scaled = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            scaled[i] = ScaleSample(samples[i], gain);
        }

        return scaled;
    }

    /// <summary>The largest absolute sample value (32768 for −32768); 0 for silence.</summary>
    public static int Peak(ReadOnlySpan<short> samples)
    {
        int peak = 0;
        foreach (short s in samples)
        {
            peak = Math.Max(peak, Math.Abs((int)s));
        }

        return peak;
    }

    /// <summary>A sample magnitude in dBFS (20·log10(peak / 32768)); −∞ for 0.</summary>
    public static double ToDbfs(int peak) =>
        peak <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(peak / FullScale);

    /// <summary>The sample magnitude of <paramref name="dbfs"/>, unrounded (32768 · 10^(dBFS/20)).</summary>
    public static double FromDbfs(double dbfs) => FullScale * Math.Pow(10.0, dbfs / 20.0);

    /// <summary>The exact linear gain that moves <paramref name="peak"/> to <paramref name="targetDbfs"/>.</summary>
    public static double GainForPeak(int peak, double targetDbfs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(peak);
        return FromDbfs(targetDbfs) / peak;
    }

    /// <summary>
    /// <paramref name="audio"/> with one exact linear gain applied so its peak
    /// lands on <paramref name="targetDbfs"/> (rounded to the nearest sample
    /// value, clipped to the 16-bit range). Silence is returned unchanged.
    /// </summary>
    public static Pcm16Audio NormalizePeak(Pcm16Audio audio, double targetDbfs)
    {
        ArgumentNullException.ThrowIfNull(audio);
        int peak = Peak(audio.Samples);
        if (peak == 0)
        {
            return audio;
        }

        return new Pcm16Audio(audio.SampleRate, audio.Channels, Scale(audio.Samples, GainForPeak(peak, targetDbfs)));
    }

    private static bool TryReadFormat(ReadOnlySpan<byte> fmt, out int channels, out int sampleRate, out string? error)
    {
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
        ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        sampleRate = rate is > 0 and <= int.MaxValue ? (int)rate : 0;

        if (tag == FormatExtensible)
        {
            // WAVE_FORMAT_EXTENSIBLE: cbSize, valid bits, channel mask, then the
            // subformat GUID, whose first two bytes are the format tag.
            if (fmt.Length < 40)
            {
                error = "The extensible format chunk is truncated.";
                return false;
            }

            tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
        }

        if (tag != FormatPcm)
        {
            error = $"Unsupported WAV encoding (format {tag}); only PCM is supported.";
            return false;
        }

        if (bits != 16)
        {
            error = $"Unsupported sample size ({bits}-bit); only 16-bit PCM is supported.";
            return false;
        }

        if (channels == 0 || sampleRate == 0)
        {
            error = "The format chunk has no channels or no sample rate.";
            return false;
        }

        if (blockAlign != channels * 2)
        {
            error = $"Inconsistent block alignment ({blockAlign} for {channels} channel(s) of 16-bit samples).";
            return false;
        }

        error = null;
        return true;
    }
}
