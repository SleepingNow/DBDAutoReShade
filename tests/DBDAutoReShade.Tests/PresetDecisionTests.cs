using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class PresetDecisionTests
{
    private static AppConfig Config() => new()
    {
        GlobalPresetPath = @"C:\Presets\Global.ini",
        RealmPresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Coldwind Farm"] = @"C:\Presets\Coldwind.ini",
            ["MacMillan Estate"] = @"C:\Presets\MacMillan.ini"
        }
    };

    [Fact]
    public void MapToRealmToPreset_Resolves()
    {
        var svc = new PresetDecisionService();
        var match = new MapMatchResult(true, "Fractured Cowshed", "Coldwind Farm", 0.96, MatchMethod.MapName);
        PresetDecision d = svc.DecideForDetection(match, Config());
        Assert.Equal(@"C:\Presets\Coldwind.ini", d.PresetPath);
        Assert.False(d.IsGlobal);
    }

    [Fact]
    public void UnconfiguredRealm_FallsBackToGlobal()
    {
        var svc = new PresetDecisionService();
        PresetDecision d = svc.DecideForRealm("Autohaven Wreckers", Config());
        Assert.True(d.IsGlobal);
        Assert.Equal(@"C:\Presets\Global.ini", d.PresetPath);
    }

    [Fact]
    public void FailedRecognition_FallsBackToGlobal()
    {
        var svc = new PresetDecisionService();
        PresetDecision d = svc.DecideForDetection(MapMatchResult.Failure("nope"), Config());
        Assert.True(d.IsGlobal);
    }

    [Fact]
    public void MatchEnd_RestoresGlobal()
    {
        var svc = new PresetDecisionService();
        PresetDecision d = svc.DecideOnMatchEnd(Config());
        Assert.True(d.IsGlobal);
        Assert.Equal(@"C:\Presets\Global.ini", d.PresetPath);
    }

    [Fact]
    public void ManualOverride_ConfiguredRealm_ActivatesIt()
    {
        var svc = new PresetDecisionService();
        PresetDecision d = svc.DecideManualOverride("MacMillan Estate", Config());
        Assert.False(d.IsGlobal);
        Assert.Equal(@"C:\Presets\MacMillan.ini", d.PresetPath);
    }

    [Fact]
    public void ManualOverride_UnconfiguredRealm_UsesGlobal()
    {
        var svc = new PresetDecisionService();
        PresetDecision d = svc.DecideManualOverride("Autohaven Wreckers", Config());
        Assert.True(d.IsGlobal);
    }

    [Fact]
    public void ManualOverride_ClearsOnMatchEnd()
    {
        // Override takes precedence during the match; match end returns to Global.
        var svc = new PresetDecisionService();
        var config = Config();
        PresetDecision during = svc.DecideManualOverride("MacMillan Estate", config);
        PresetDecision after = svc.DecideOnMatchEnd(config);
        Assert.False(during.IsGlobal);
        Assert.True(after.IsGlobal);
    }
}
