namespace DBDAutoReShade.Core.Models;

/// <summary>
/// Explicit runtime state model. The application must always be in exactly one
/// of these states; transitions are guarded by <see cref="Services.RuntimeStateService"/>.
/// </summary>
public enum AppState
{
    /// <summary>Dead by Daylight is not running. OCR and capture are fully idle.</summary>
    WaitingForDbd,

    /// <summary>DBD is running, no detection in progress. OCR CPU usage is effectively zero.</summary>
    Ready,

    /// <summary>
    /// Hotkey pressed: sampling the live loading screen with OCR until the map is
    /// read confidently or the hotkey is pressed again (or DBD exits).
    /// </summary>
    DetectingLoadingScreen,

    /// <summary>A realm was identified with sufficient confidence. OCR has stopped. Transient.</summary>
    RealmIdentified,

    /// <summary>
    /// The detected realm's preset (or Global) is the pending activation target:
    /// it is sent as soon as DBD is focused (typically still on the loading screen).
    /// Gameplay starting renews the retry budget.
    /// </summary>
    WaitingForGameplay,

    /// <summary>The detected realm's preset (or Global) was switched to for the current match.</summary>
    PresetActive,

    /// <summary>
    /// User manually overrode automatic switching. Cleared by Return to Automatic,
    /// a new detection, DBD exit, or the match end when the match is tracked and
    /// auto-restore is on.
    /// </summary>
    ManualOverride,

    /// <summary>Trial ended; restoring the Global/Menu preset. Transient (from PresetActive or ManualOverride).</summary>
    ReturningToMenu,

    /// <summary>
    /// An unexpected error occurred; see status message. Never sticky: the
    /// detection hotkey starts a fresh scan, a manual override is allowed, and
    /// DBD (re)appearing returns to <see cref="Ready"/>.
    /// </summary>
    Error
}
