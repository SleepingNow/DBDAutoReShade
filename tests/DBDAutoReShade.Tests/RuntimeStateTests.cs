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
        Assert.True(states.TryTransitionTo(AppState.Ready)); // the Global preset hotkey (F9)
        Assert.True(states.TryTransitionTo(AppState.DetectingLoadingScreen)); // F8 on the next loading screen
    }

    [Fact]
    public void ThereIsNoMatchEndState()
    {
        // 1.2.1 removed match-end detection: no state stands for "back to menus".
        Assert.DoesNotContain("ReturningToMenu", Enum.GetNames<AppState>());
        Assert.Equal(
            new[] { "WaitingForDbd", "Ready", "DetectingLoadingScreen", "RealmIdentified", "WaitingForGameplay", "PresetActive", "ManualOverride", "Error" },
            Enum.GetNames<AppState>());
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
        Assert.False(states.TryTransitionTo(AppState.WaitingForGameplay));
        Assert.Equal(AppState.Ready, states.State);
    }

    [Fact]
    public void ManualOverride_Flow()
    {
        var states = new RuntimeStateService();
        states.ResetTo(AppState.PresetActive);
        Assert.True(states.TryTransitionTo(AppState.ManualOverride));
        Assert.True(states.TryTransitionTo(AppState.Ready)); // Return to automatic, or the Global preset hotkey
        Assert.True(states.TryTransitionTo(AppState.ManualOverride));
        Assert.True(states.TryTransitionTo(AppState.DetectingLoadingScreen)); // a new scan ends it too
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
