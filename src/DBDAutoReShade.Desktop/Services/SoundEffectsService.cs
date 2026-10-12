using System.Runtime.InteropServices;
using DBDAutoReShade.Core.Services;
using DBDAutoReShade.Desktop.Interop;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>
/// Plays the Great skill-check sound: the 16-bit PCM asset (read once from an
/// injected stream factory, e.g. a WPF pack Resource), scaled in memory by
/// volume² (<see cref="WavPcm16.VolumeToGain"/>) and played asynchronously
/// with winmm PlaySound from memory. One voice: a new sound stops the previous
/// one. Never throws; a sound that can't be loaded or played returns false.
/// </summary>
public sealed class SoundEffectsService : ISoundEffects, IDisposable
{
    private readonly Func<Stream?> _openGreat;
    private readonly IWavePlayer _player;
    private readonly object _lock = new();
    private Pcm16Audio? _great;
    private bool _loadAttempted;
    private bool _disposed;

    /// <param name="openGreatWav">Opens the Great sound WAV (16-bit PCM); null or an exception means it is unavailable. Called at most once.</param>
    public SoundEffectsService(Func<Stream?> openGreatWav)
        : this(openGreatWav, new WinmmWavePlayer())
    {
    }

    /// <summary>Test seam: plays through <paramref name="player"/> instead of the sound card.</summary>
    internal SoundEffectsService(Func<Stream?> openGreatWav, IWavePlayer player)
    {
        ArgumentNullException.ThrowIfNull(openGreatWav);
        ArgumentNullException.ThrowIfNull(player);
        _openGreat = openGreatWav;
        _player = player;
    }

    /// <summary>Why the Great sound could not be loaded; null while it loaded (or was not needed yet).</summary>
    public string? LoadError { get; private set; }

    /// <summary>
    /// Loads and parses the Great sound now (e.g. on the UI thread at startup),
    /// so the first <see cref="PlayGreat"/> doesn't. Returns whether it is
    /// available. Never throws.
    /// </summary>
    public bool Preload()
    {
        lock (_lock)
        {
            return !_disposed && LoadLocked() is not null;
        }
    }

    /// <inheritdoc />
    public bool PlayGreat(double volume)
    {
        try
        {
            double gain = WavPcm16.VolumeToGain(volume);
            if (gain <= 0)
            {
                return false; // silent: skipped, nothing is stopped or played
            }

            lock (_lock)
            {
                if (_disposed || LoadLocked() is not { } great)
                {
                    return false;
                }

                byte[] wav = WavPcm16.Encode(great.SampleRate, great.Channels, WavPcm16.Scale(great.Samples, gain));
                return _player.Play(wav); // stops the previous voice first
            }
        }
        catch
        {
            return false; // a sound effect must never break the caller
        }
    }

    /// <inheritdoc />
    public string? GreatSoundProblem()
    {
        try
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return "The sound player has been shut down.";
                }

                return LoadLocked() is null ? LoadError ?? "The Great sound is not available." : null;
            }
        }
        catch (Exception ex)
        {
            return ex.Message; // diagnostics must never break the report
        }
    }

    /// <summary>Stops the sound and releases the player; later calls play nothing.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _player.Dispose();
            }
            catch
            {
                // Best effort on exit.
            }
        }
    }

    /// <summary>The parsed Great sound; loaded on first use, a failure is remembered (no retry). Call with _lock held.</summary>
    private Pcm16Audio? LoadLocked()
    {
        if (_loadAttempted)
        {
            return _great;
        }

        _loadAttempted = true;
        try
        {
            using Stream? stream = _openGreat();
            if (stream is null)
            {
                LoadError = "The Great sound is not available.";
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            _great = WavPcm16.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            LoadError = null;
        }
        catch (Exception ex)
        {
            _great = null;
            LoadError = $"Could not load the Great sound: {ex.Message}";
        }

        return _great;
    }
}

/// <summary>Plays one WAV image at a time (test seam; production: <see cref="WinmmWavePlayer"/>).</summary>
internal interface IWavePlayer : IDisposable
{
    /// <summary>Stops the previous sound, then starts <paramref name="wav"/> asynchronously; returns whether it started.</summary>
    bool Play(byte[] wav);
}

/// <summary>
/// winmm PlaySound (SND_MEMORY | SND_ASYNC) from unmanaged memory. winmm reads
/// the WAV image while it plays, so the image is copied to unmanaged memory
/// (never moved by the GC) and kept alive until a later sound has replaced it;
/// PlaySound(NULL) stops the playing sound before a new one starts.
/// </summary>
internal sealed class WinmmWavePlayer : IWavePlayer
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private readonly object _lock = new();

    // The image of the current (or last) sound, and the one before it (kept one
    // extra round in case winmm still touches it while stopping).
    private IntPtr _current;
    private IntPtr _retired;
    private bool _disposed;

    public bool Play(byte[] wav)
    {
        ArgumentNullException.ThrowIfNull(wav);
        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }

            StopLocked();
            IntPtr image = Marshal.AllocHGlobal(wav.Length);
            Marshal.Copy(wav, 0, image, wav.Length);
            bool started;
            try
            {
                started = NativeMethods.PlaySound(image, IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
            }
            catch
            {
                started = false; // e.g. winmm unavailable
            }

            if (!started)
            {
                Marshal.FreeHGlobal(image);
                return false;
            }

            _current = image;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopLocked();
            Free(ref _retired);
        }
    }

    /// <summary>Stops the playing sound; the image two sounds back is freed, the last one retired. Call with _lock held.</summary>
    private void StopLocked()
    {
        try
        {
            NativeMethods.PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        }
        catch
        {
            // Best effort.
        }

        Free(ref _retired);
        _retired = _current;
        _current = IntPtr.Zero;
    }

    private static void Free(ref IntPtr image)
    {
        if (image != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(image);
            image = IntPtr.Zero;
        }
    }
}
