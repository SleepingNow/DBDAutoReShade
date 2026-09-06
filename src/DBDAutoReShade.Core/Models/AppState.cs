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

    /// <summary>Hotkey pressed: sampling the live loading screen with OCR (max ~10s).</summary>
    DetectingLoadingScreen,

    /// <summary>A realm was identified with sufficient confidence. OCR has stopped.</summary>
    RealmIdentified,

    /// <summary>Waiting (lightweight) for gameplay to begin before activating the preset.</summary>
    WaitingForGameplay,

    /// <summary>A realm preset is active for the current trial.</summary>
    PresetActive,

    /// <summary>User manually overrode automatic switching for the current match.</summary>
    ManualOverride,

    /// <summary>Trial ended; restoring the Global/Menu preset.</summary>
    ReturningToMenu,

    /// <summary>An unexpected error occurred; see status message. Recovery possible.</summary>
    Error
}
