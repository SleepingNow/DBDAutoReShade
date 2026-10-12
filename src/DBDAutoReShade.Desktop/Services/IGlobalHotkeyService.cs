using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>
/// One system-wide hotkey (test seam; production: <see cref="GlobalHotkeyService"/>,
/// which registers it with Windows). The app uses two: the detection hotkey and
/// the Global preset hotkey.
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    /// <summary>True only while a hotkey is actually registered.</summary>
    bool IsRegistered { get; }

    /// <summary>The registered hotkey; null while none is.</summary>
    DetectionHotkeyConfig? RegisteredHotkey { get; }

    /// <summary>
    /// Error from the most recent registration attempt (also returned
    /// synchronously by <see cref="Start"/>); null while registered or stopped.
    /// </summary>
    string? LastError { get; }

    /// <summary>Raised when the hotkey is pressed, on the service's own thread.</summary>
    event EventHandler? HotkeyPressed;

    /// <summary>
    /// Register (or re-register) the hotkey; returns only once the outcome is
    /// known. Null on success, otherwise a user-facing error. A hotkey that
    /// fails to PARSE leaves the current one registered; one that fails to
    /// REGISTER leaves nothing registered.
    /// </summary>
    string? Start(DetectionHotkeyConfig hotkey);

    /// <summary>Releases the hotkey; it is free for any registration when this returns.</summary>
    void Stop();
}
