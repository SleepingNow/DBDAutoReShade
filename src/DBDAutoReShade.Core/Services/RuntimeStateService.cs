using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Explicit application state machine. Illegal transitions are rejected
/// instead of being tracked with scattered booleans.
/// </summary>
public sealed class RuntimeStateService
{
    private static readonly Dictionary<AppState, HashSet<AppState>> Allowed = new()
    {
        [AppState.WaitingForDbd] = new() { AppState.Ready, AppState.Error },
        [AppState.Ready] = new() { AppState.DetectingLoadingScreen, AppState.ManualOverride, AppState.WaitingForDbd, AppState.Error },
        [AppState.DetectingLoadingScreen] = new() { AppState.RealmIdentified, AppState.Ready, AppState.WaitingForDbd, AppState.Error },
        [AppState.RealmIdentified] = new() { AppState.WaitingForGameplay, AppState.PresetActive, AppState.Ready, AppState.WaitingForDbd, AppState.Error },
        [AppState.WaitingForGameplay] = new() { AppState.PresetActive, AppState.Ready, AppState.DetectingLoadingScreen, AppState.ManualOverride, AppState.WaitingForDbd, AppState.Error },
        [AppState.PresetActive] = new() { AppState.Ready, AppState.DetectingLoadingScreen, AppState.ManualOverride, AppState.WaitingForDbd, AppState.Error },
        [AppState.ManualOverride] = new() { AppState.Ready, AppState.DetectingLoadingScreen, AppState.WaitingForDbd, AppState.Error },
        // Error is never sticky: the detection hotkey starts a fresh scan, an
        // override still works, and DBD (re)appearing returns to Ready.
        [AppState.Error] = new() { AppState.Ready, AppState.DetectingLoadingScreen, AppState.ManualOverride, AppState.WaitingForDbd },
    };

    private AppState _state = AppState.WaitingForDbd;

    public AppState State => _state;

    public event EventHandler<AppState>? StateChanged;

    public bool CanTransitionTo(AppState next)
    {
        if (next == _state)
        {
            return true;
        }

        // Losing DBD is legal from anywhere (except already waiting).
        if (next == AppState.WaitingForDbd && _state != AppState.WaitingForDbd)
        {
            return true;
        }

        return Allowed.TryGetValue(_state, out var set) && set.Contains(next);
    }

    /// <summary>Attempt a transition; returns false (no throw) when illegal.</summary>
    public bool TryTransitionTo(AppState next)
    {
        if (!CanTransitionTo(next))
        {
            return false;
        }

        if (next != _state)
        {
            _state = next;
            StateChanged?.Invoke(this, next);
        }

        return true;
    }

    public void ResetTo(AppState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
    }
}
