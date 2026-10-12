using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Desktop.Interop;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>Why a preset activation did not happen (<see cref="None"/> on success).</summary>
public enum ActivationFailure
{
    None,

    /// <summary>No preset path was given.</summary>
    NoPreset,

    /// <summary>The preset file does not exist.</summary>
    FileMissing,

    /// <summary>Dead by Daylight is not running.</summary>
    NotRunning,

    /// <summary>DBD runs but its window is not the foreground window (or has no window yet). Transient: wait for focus.</summary>
    NotFocused,

    /// <summary>No ReShade shortcut is assigned to the preset yet.</summary>
    NoShortcut,

    /// <summary>The running ReShade has not loaded the shortcut; DBD must be restarted first.</summary>
    RestartRequired,

    /// <summary>The keystroke could not be sent (SendInput rejected or threw).</summary>
    SendBlocked,

    /// <summary>
    /// The shortcut would fire an earlier ReShade shortcut for a different preset first (typically
    /// ReShade's "Force correct modifier keys" is off and that entry uses the same key).
    /// </summary>
    ShortcutClash
}

/// <summary>
/// Result of a preset activation attempt. <see cref="Failure"/> says why it
/// failed; a failed result built with the two-argument constructor reports
/// <see cref="ActivationFailure.None"/>, which callers treat as a final failure.
/// </summary>
public sealed record ActivationResult(bool Success, string Message, ActivationFailure Failure = ActivationFailure.None)
{
    public static ActivationResult Ok(string message) => new(true, message);

    public static ActivationResult Fail(ActivationFailure failure, string message) => new(false, message, failure);
}

/// <summary>
/// Activates ReShade presets by simulating the app-managed preset shortcut
/// (SendInput to the foreground game window). ReShade applies shortcut
/// switches in-memory, which is the only supported external mechanism —
/// ReShade does not poll ReShade.ini for external edits while running.
/// </summary>
public sealed class PresetActivationService
{
    private readonly IKeySimulator _keys;
    private readonly Func<IntPtr, bool> _isForeground;
    private readonly Func<int, IntegrityLevel> _integrityProbe;

    public PresetActivationService(
        IKeySimulator keys,
        Func<IntPtr, bool>? isForeground = null,
        Func<int, IntegrityLevel>? integrityProbe = null)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _isForeground = isForeground ?? DbdWindowService.IsForeground;
        _integrityProbe = integrityProbe ?? ProcessIntegrity.GetForProcess;
    }

    /// <summary>
    /// Every check <see cref="Activate"/> makes before sending, without sending
    /// anything. Final problems (no preset, missing file, no shortcut, DBD not
    /// running) are reported before the transient one (DBD not focused), so a
    /// caller never waits for focus only to fail afterwards.
    /// </summary>
    public ActivationResult CheckReady(
        string presetPath,
        ReshadeShortcutConfig? shortcut,
        IntPtr dbdWindow,
        bool dbdRunning)
    {
        if (string.IsNullOrWhiteSpace(presetPath))
        {
            return ActivationResult.Fail(ActivationFailure.NoPreset, "No preset is configured.");
        }

        if (!File.Exists(presetPath))
        {
            return ActivationResult.Fail(ActivationFailure.FileMissing, $"The preset file was not found: {presetPath}");
        }

        if (shortcut is null)
        {
            return ActivationResult.Fail(ActivationFailure.NoShortcut,
                "No ReShade shortcut is assigned yet. Assign presets, then restart DBD so ReShade loads the new shortcuts.");
        }

        if (!dbdRunning)
        {
            return ActivationResult.Fail(ActivationFailure.NotRunning, "Dead by Daylight is not running.");
        }

        if (dbdWindow == IntPtr.Zero)
        {
            return ActivationResult.Fail(ActivationFailure.NotFocused,
                "DBD's game window isn't available yet (click into the game once it is).");
        }

        if (!IsForeground(dbdWindow))
        {
            return ActivationResult.Fail(ActivationFailure.NotFocused,
                "DBD must be focused to switch presets (click into the game).");
        }

        return ActivationResult.Ok("Ready to switch.");
    }

    /// <summary>
    /// Whether <paramref name="dbdWindow"/> is the foreground window (false for
    /// no window). The same focus check activation uses; never throws.
    /// </summary>
    public bool IsForeground(IntPtr dbdWindow)
    {
        if (dbdWindow == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return _isForeground(dbdWindow);
        }
        catch
        {
            return false;
        }
    }

    public ActivationResult Activate(
        string presetPath,
        ReshadeShortcutConfig? shortcut,
        IntPtr dbdWindow,
        bool dbdRunning)
    {
        ActivationResult ready = CheckReady(presetPath, shortcut, dbdWindow, dbdRunning);
        if (!ready.Success)
        {
            return ready;
        }

        bool sent;
        try
        {
            sent = _keys.SendShortcut(shortcut!);
        }
        catch (Exception ex)
        {
            return ActivationResult.Fail(ActivationFailure.SendBlocked, $"Could not send the preset shortcut: {ex.Message}");
        }

        if (sent)
        {
            return ActivationResult.Ok($"Activated {Path.GetFileName(presetPath)}.");
        }

        return ActivationResult.Fail(ActivationFailure.SendBlocked,
            $"Could not send the preset shortcut.{BuildBlockedHint(dbdWindow)}");
    }

    /// <summary>
    /// Explains a rejected SendInput. Pure logic over two integrity levels
    /// (testable); probing itself is best-effort and never throws.
    /// </summary>
    internal static string BuildBlockedHint(
        IntegrityLevel ownLevel, IntegrityLevel gameLevel)
    {
        if (ownLevel != IntegrityLevel.Unknown && gameLevel != IntegrityLevel.Unknown &&
            gameLevel > ownLevel)
        {
            return $" Windows blocked the keystroke: DBD runs {ProcessIntegrity.Describe(gameLevel)} " +
                $"but this app runs {ProcessIntegrity.Describe(ownLevel)}. " +
                "Run both the same way (preferably both normally, not as admin).";
        }

        return " Windows blocked the keystroke. If DBD/Steam runs as admin, run this app " +
            "as admin too (or better: run both normally). Also close any UAC prompt or " +
            "secure-desktop screen.";
    }

    private string BuildBlockedHint(IntPtr dbdWindow)
    {
        try
        {
            IntegrityLevel own = ProcessIntegrity.GetForCurrentProcess();
            IntegrityLevel game = IntegrityLevel.Unknown;
            try
            {
                NativeMethods.GetWindowThreadProcessId(dbdWindow, out uint pid);
                if (pid != 0)
                {
                    game = _integrityProbe((int)pid);
                }
            }
            catch
            {
                // Probe is best-effort; fall through to the generic hint.
            }

            return BuildBlockedHint(own, game);
        }
        catch
        {
            return " Windows blocked the keystroke.";
        }
    }
}
