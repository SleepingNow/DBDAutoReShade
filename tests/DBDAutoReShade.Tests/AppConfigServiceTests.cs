using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class AppConfigServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-config-" + Guid.NewGuid().ToString("N"));
    private string PathFor(string name = "config.json") => Path.Combine(_dir, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    [Fact]
    public void Save_Reload_Roundtrips()
    {
        var svc = new AppConfigService(PathFor());
        svc.Config.SetupCompleted = true;
        svc.Config.DbdExePath = @"C:\Games\DBD\DeadByDaylight.exe";
        svc.Config.GlobalPresetPath = @"C:\Presets\Global.ini";
        svc.Config.RealmPresets["Coldwind Farm"] = @"C:\Presets\Coldwind.ini";
        svc.Config.DetectionHotkey = new DetectionHotkeyConfig { Key = "F9", Ctrl = true };
        svc.Config.DebugMode = true;
        svc.Save();

        var reloaded = new AppConfigService(PathFor());
        reloaded.Load();
        Assert.True(reloaded.Config.SetupCompleted);
        Assert.Equal(@"C:\Games\DBD\DeadByDaylight.exe", reloaded.Config.DbdExePath);
        Assert.Equal(@"C:\Presets\Coldwind.ini", reloaded.Config.RealmPresets["coldwind farm"]); // case-insensitive
        Assert.Equal("F9", reloaded.Config.DetectionHotkey.Key);
        Assert.True(reloaded.Config.DetectionHotkey.Ctrl);
        Assert.True(reloaded.Config.DebugMode);
    }

    [Fact]
    public void MissingFile_LoadsDefaults()
    {
        var svc = new AppConfigService(PathFor());
        svc.Load();
        Assert.False(svc.Config.SetupCompleted);
        Assert.Equal("F8", svc.Config.DetectionHotkey.Key);
    }

    [Fact]
    public void CorruptFile_IsQuarantined_AndDefaultsReturned()
    {
        string path = PathFor();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ this is not json !!!");

        var svc = new AppConfigService(path);
        svc.Load();

        Assert.False(svc.Config.SetupCompleted); // defaults
        Assert.False(File.Exists(path)); // moved away
        Assert.Single(Directory.GetFiles(_dir, "config.corrupt-*.json"));
    }

    [Fact]
    public void Reset_RestoresDefaults_OnlyTouchesOwnConfig()
    {
        string preset = Path.Combine(_dir, "MyPreset.ini");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(preset, "Techniques=Foo");

        var svc = new AppConfigService(PathFor());
        svc.Config.RealmPresets["Coldwind Farm"] = preset;
        svc.Save();
        svc.Reset();

        Assert.Empty(svc.Config.RealmPresets);
        Assert.True(File.Exists(preset)); // user preset file untouched
    }
}
