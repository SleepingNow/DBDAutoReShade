using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class RuntimeStateTests
{
    [Fact]
    public void FullDetectionCycle_TransitionsLegally()
    {
        var states = new RuntimeStateService();
        Assert.Equal(AppState.WaitingForDbd, states.State);

        Assert.True(states.TryTransitionTo(AppState.Ready)); // DBD launched
        Assert.True(states.TryTransitionTo(AppState.DetectingLoadingScreen)); // F8
        Assert.True(states.TryTransitionTo(AppState.RealmIdentified)); // OCR success stops detection
        Assert.True(states.TryTransitionTo(AppState.WaitingForGameplay));
        Assert.True(states.TryTransitionTo(AppState.PresetActive));
        Assert.True(states.TryTransitionTo(AppState.ReturningToMenu)); // match end
        Assert.True(states.TryTransitionTo(AppState.Ready));
    }

    [Fact]
    public void DetectionTimeout_ReturnsToReady()
    {
        var states = new RuntimeStateService();
        states.ResetTo(AppState.Ready);
        Assert.True(states.TryTransitionTo(AppState.DetectingLoadingScreen));
        Assert.True(states.TryTransitionTo(AppState.Ready)); // timeout → Global retained
    }

    [Fact]
    public void IllegalTransitions_AreRejected()
    {
        var states = new RuntimeStateService();
        states.ResetTo(AppState.Ready);
        Assert.False(states.TryTransitionTo(AppState.PresetActive)); // must detect first
        Assert.False(states.TryTransitionTo(AppState.ReturningToMenu));
        Assert.Equal(AppState.Ready, states.State);
    }

    [Fact]
    public void ManualOverride_Flow()
    {
        var states = new RuntimeStateService();
        states.ResetTo(AppState.PresetActive);
        Assert.True(states.TryTransitionTo(AppState.ManualOverride));
        Assert.True(states.TryTransitionTo(AppState.ReturningToMenu));
        Assert.True(states.TryTransitionTo(AppState.Ready));
    }

    [Fact]
    public void DbdExit_IsLegalFromAnywhere()
    {
        var states = new RuntimeStateService();
        foreach (AppState from in Enum.GetValues<AppState>())
        {
            states.ResetTo(from);
            Assert.True(states.TryTransitionTo(AppState.WaitingForDbd), $"from {from}");
        }
    }

    [Fact]
    public void Error_IsNotSticky()
    {
        var states = new RuntimeStateService();
        states.ResetTo(AppState.Error);
        Assert.True(states.TryTransitionTo(AppState.DetectingLoadingScreen)); // the hotkey starts a fresh scan

        states.ResetTo(AppState.Error);
        Assert.True(states.TryTransitionTo(AppState.ManualOverride));

        states.ResetTo(AppState.Error);
        Assert.True(states.TryTransitionTo(AppState.Ready)); // DBD (re)detected

        states.ResetTo(AppState.Error);
        Assert.False(states.TryTransitionTo(AppState.PresetActive)); // still guarded
    }

    [Fact]
    public void StateChanged_EventFires()
    {
        var states = new RuntimeStateService();
        AppState? seen = null;
        states.StateChanged += (_, s) => seen = s;
        states.TryTransitionTo(AppState.Ready);
        Assert.Equal(AppState.Ready, seen);
    }
}
