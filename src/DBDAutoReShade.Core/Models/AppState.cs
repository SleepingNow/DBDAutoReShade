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
    /// it is sent as soon as DBD is focused (typically still on the loading
    /// screen), with no deadline. (The name predates 1.2.1: the app no longer
    /// watches for gameplay; this state only waits for DBD focus.)
    /// </summary>
    WaitingForGameplay,

    /// <summary>
    /// The detected realm's preset (or Global) was switched to. It stays until the
    /// user's next switch (a new scan, the Global preset hotkey, an override, a
    /// Test) or DBD exit: the app does not watch for the match to end.
    /// </summary>
    PresetActive,

    /// <summary>
    /// User manually overrode automatic switching. Cleared only by Return to
    /// Automatic, a new detection, the Global preset hotkey ("Switch to Global")
    /// or DBD exit.
    /// </summary>
    ManualOverride,

    /// <summary>
    /// An unexpected error occurred; see status message. Never sticky: the
    /// detection hotkey starts a fresh scan, a manual override is allowed, and
    /// DBD (re)appearing returns to <see cref="Ready"/>.
    /// </summary>
    Error
}
