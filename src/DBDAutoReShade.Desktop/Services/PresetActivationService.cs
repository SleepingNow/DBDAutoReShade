using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Desktop.Interop;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>Result of a preset activation attempt.</summary>
public sealed record ActivationResult(bool Success, string Message);

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

    public ActivationResult Activate(
        string presetPath,
        ReshadeShortcutConfig? shortcut,
        IntPtr dbdWindow,
        bool dbdRunning)
    {
        if (string.IsNullOrWhiteSpace(presetPath))
        {
            return new ActivationResult(false, "No preset is configured.");
        }

        if (!File.Exists(presetPath))
        {
            return new ActivationResult(false, $"The preset file was not found: {presetPath}");
        }

        if (!dbdRunning || dbdWindow == IntPtr.Zero)
        {
            return new ActivationResult(false, "Dead by Daylight is not running.");
        }

        if (!_isForeground(dbdWindow))
        {
            return new ActivationResult(false, "DBD must be focused to switch presets (click into the game).");
        }

        if (shortcut is null)
        {
            return new ActivationResult(false,
                "No ReShade shortcut is assigned yet. Assign presets, then restart DBD so ReShade loads the new shortcuts.");
        }

        bool sent;
        try
        {
            sent = _keys.SendShortcut(shortcut);
        }
        catch (Exception ex)
        {
            return new ActivationResult(false, $"Could not send the preset shortcut: {ex.Message}");
        }

        if (sent)
        {
            return new ActivationResult(true, $"Activated {Path.GetFileName(presetPath)}.");
        }

        return new ActivationResult(false, $"Could not send the preset shortcut.{BuildBlockedHint(dbdWindow)}");
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
