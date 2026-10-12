namespace DBDAutoReShade.Desktop.Services;

/// <summary>
/// The app's sound effects (test seam; production: <see cref="SoundEffectsService"/>).
/// Whether and when a sound plays (the sound setting, the skill-check timing)
/// is the caller's decision; this only plays it.
/// </summary>
public interface ISoundEffects
{
    /// <summary>
    /// Plays the Great skill-check sound at <paramref name="volume"/> (0..1, the
    /// amplitude is volume²: 1 = the asset's -1 dBFS peak, 0.5 ≈ -13 dBFS),
    /// asynchronously, replacing a sound that is still playing (sounds never
    /// stack). Returns whether playback started: false when skipped (volume ≤ 0
    /// or NaN) or on any failure. Never throws; callable from any thread.
    /// </summary>
    bool PlayGreat(double volume);

    /// <summary>
    /// Why the Great sound can't play (e.g. its asset is missing or broken), or
    /// null when it can. Loads the sound first if that was not tried yet. For
    /// diagnostics; never throws. The default (an implementation that has
    /// nothing to report) is null.
    /// </summary>
    string? GreatSoundProblem() => null;
}

/// <summary>Plays nothing (tests and tools without audio): <see cref="PlayGreat"/> always returns false.</summary>
public sealed class NullSoundEffects : ISoundEffects
{
    /// <summary>What <see cref="GreatSoundProblem"/> reports.</summary>
    public const string NoSoundOutput = "No sound output is connected.";

    /// <summary>The shared instance.</summary>
    public static NullSoundEffects Instance { get; } = new();

    private NullSoundEffects()
    {
    }

    /// <inheritdoc />
    public bool PlayGreat(double volume) => false;

    /// <inheritdoc />
    public string? GreatSoundProblem() => NoSoundOutput;
}
