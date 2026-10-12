using DBDAutoReShade.App.Infrastructure;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// The 1.2 settings in config.json (Global preset hotkey, animations, sound,
/// volume): defaults, configs saved before they existed, "cleared" versus
/// "missing", clamping and round trips; plus the development data-folder
/// override and its single-instance mutex name.
/// </summary>
public sealed class AppConfigSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-settings-" + Guid.NewGuid().ToString("N"));

    public AppConfigSettingsTests() => Directory.CreateDirectory(_dir);

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

    private string ConfigPath => Path.Combine(_dir, "config.json");

    private AppConfigService LoadFrom(string json)
    {
        File.WriteAllText(ConfigPath, json);
        var svc = new AppConfigService(ConfigPath);
        svc.Load();
        Assert.Null(svc.LastLoadWarning);
        return svc;
    }

    private AppConfigService Reload()
    {
        var svc = new AppConfigService(ConfigPath);
        svc.Load();
        return svc;
    }

    /// <summary>A config.json as version 1.1 saved it: none of the 1.2 settings.</summary>
    private const string SavedBy11 = """
        {
          "schemaVersion": 1,
          "setupCompleted": true,
          "dbdExePath": "G:\\SteamLibrary\\steamapps\\common\\Dead by Daylight\\DeadByDaylight.exe",
          "reshadeIniPath": "G:\\SteamLibrary\\steamapps\\common\\Dead by Daylight\\DeadByDaylight\\Binaries\\Win64\\ReShade.ini",
          "globalPresetPath": "G:\\Presets\\Global.ini",
          "realmPresets": {
            "Coldwind Farm": "G:\\Presets\\Coldwind.ini"
          },
          "detectionHotkey": {
            "key": "F8",
            "ctrl": false,
            "shift": false,
            "alt": false
          },
          "reshadeShortcuts": {
            "__GLOBAL__": { "virtualKey": 124, "ctrl": true, "shift": true, "alt": true },
            "Coldwind Farm": { "virtualKey": 125, "ctrl": true, "shift": true, "alt": true }
          },
          "lastSyncedPresetPaths": [ "G:\\Presets\\Global.ini", "G:\\Presets\\Coldwind.ini" ],
          "debugMode": false,
          "databaseVersion": 3,
          "databaseUpdateManifestUrl": null,
          "autoRestoreGlobalOnMenu": false
        }
        """;

    [Fact]
    public void NewConfig_HasTheNewDefaults()
    {
        var config = new AppConfig();

        Assert.True(config.AnimationsEnabled);
        Assert.True(config.SoundEnabled);
        Assert.Equal(0.5, config.SoundVolume);
        Assert.Equal(AppConfig.DefaultSoundVolume, config.SoundVolume);
        Assert.NotNull(config.GlobalHotkey);
        Assert.Equal("F9", config.GlobalHotkey.ToString()); // no modifiers
        Assert.Equal("F8", config.DetectionHotkey.ToString());
    }

    [Fact]
    public void ConfigSavedBy11_LoadsTheNewSettingsWithTheirDefaults_AndKeepsEverythingElse()
    {
        AppConfigService svc = LoadFrom(SavedBy11);
        AppConfig config = svc.Config;

        Assert.Equal(0.5, config.SoundVolume); // first 1.2 start: the slider sits at 50 %
        Assert.True(config.SoundEnabled);
        Assert.True(config.AnimationsEnabled);
        Assert.Equal("F9", config.GlobalHotkey?.ToString()); // the property was missing: F9

        Assert.True(config.SetupCompleted);
        Assert.Equal(@"G:\Presets\Global.ini", config.GlobalPresetPath);
        Assert.Equal(@"G:\Presets\Coldwind.ini", config.RealmPresets["coldwind farm"]);
        Assert.Equal(124, config.ReshadeShortcuts[AppConfigKeys.GlobalShortcutKey].VirtualKey);
        Assert.False(config.AutoRestoreGlobalOnMenu);
        Assert.Equal(3, config.DatabaseVersion);

        // Saved again: written out explicitly, and it loads the same.
        svc.Save();
        string json = File.ReadAllText(ConfigPath);
        Assert.Contains("\"globalHotkey\": {", json);
        Assert.Contains("\"soundVolume\": 0.5", json);
        Assert.Contains("\"soundEnabled\": true", json);
        Assert.Contains("\"animationsEnabled\": true", json);
        AppConfig again = Reload().Config;
        Assert.Equal("F9", again.GlobalHotkey?.ToString());
        Assert.Equal(0.5, again.SoundVolume);
    }

    [Fact]
    public void ClearedGlobalHotkey_IsSavedAsNull_AndLoadsCleared_UnlikeAMissingOne()
    {
        var svc = new AppConfigService(ConfigPath);
        svc.Config.GlobalHotkey = null;
        svc.Save();

        Assert.Contains("\"globalHotkey\": null", File.ReadAllText(ConfigPath));
        Assert.Null(Reload().Config.GlobalHotkey); // cleared stays cleared

        // The same file without the property (as saved before 1.2): F9.
        string missing = string.Join('\n', File.ReadAllLines(ConfigPath).Where(l => !l.Contains("\"globalHotkey\"")));
        Assert.Equal("F9", LoadFrom(missing).Config.GlobalHotkey?.ToString());
    }

    [Fact]
    public void NewSettings_RoundTrip()
    {
        var svc = new AppConfigService(ConfigPath);
        svc.Config.AnimationsEnabled = false;
        svc.Config.SoundEnabled = false;
        svc.Config.SoundVolume = 0.37;
        svc.Config.GlobalHotkey = new DetectionHotkeyConfig { Key = "G", Ctrl = true, Alt = true };
        svc.Save();

        AppConfig loaded = Reload().Config;

        Assert.False(loaded.AnimationsEnabled);
        Assert.False(loaded.SoundEnabled);
        Assert.Equal(0.37, loaded.SoundVolume);
        Assert.Equal("Ctrl+Alt+G", loaded.GlobalHotkey?.ToString());
    }

    [Theory]
    [InlineData("1.5", 1.0)]
    [InlineData("-0.2", 0.0)]
    [InlineData("0", 0.0)]
    [InlineData("0.25", 0.25)]
    [InlineData("1", 1.0)]
    public void SoundVolumeOutOfRange_LoadsClamped(string stored, double expected)
    {
        AppConfig config = LoadFrom($"{{ \"soundVolume\": {stored} }}").Config;

        Assert.Equal(expected, config.SoundVolume);
    }

    [Theory]
    [InlineData("{ \"globalHotkey\": { \"key\": \"\" } }")]
    [InlineData("{ \"globalHotkey\": { \"key\": \"   \", \"ctrl\": true } }")]
    public void GlobalHotkeyWithoutAKey_LoadsDisabled(string json)
    {
        Assert.Null(LoadFrom(json).Config.GlobalHotkey);
    }

    [Theory]
    [InlineData("{ \"globalHotkey\": { \"key\": \"F9\" }, \"detectionHotkey\": { \"key\": \"F9\" } }", "F9")]
    [InlineData("{ \"globalHotkey\": { } }", "F8")] // an empty object means F8: the detection hotkey's combo
    [InlineData("{ \"globalHotkey\": { \"key\": \"F13\", \"ctrl\": true, \"shift\": true, \"alt\": true } }", "Ctrl+Shift+Alt+F13")]
    public void GlobalHotkeyTheAppCantRegister_LoadsAsSaved_TheAppSaysWhyInstead(string json, string expected)
    {
        // The detection hotkey's combo, or one of the app's ReShade shortcut combos: the
        // controller refuses to register it (AppController.GlobalHotkeyError). Loading it as
        // null would save it as null, which is what a user's Clear writes.
        Assert.Equal(expected, LoadFrom(json).Config.GlobalHotkey?.ToString());
    }

    [Fact]
    public void ConfigSavedBy11_WithF9AsTheDetectionHotkey_KeepsBoth_AndSavingItAgainRecordsNoClear()
    {
        // The upgrade gives F9 twice: the detection hotkey keeps it (the controller leaves the
        // Global preset hotkey unregistered and says why), and the file still has no "cleared".
        AppConfigService svc = LoadFrom("{ \"detectionHotkey\": { \"key\": \"f9\" } }");

        Assert.Equal("f9", svc.Config.DetectionHotkey.Key);
        Assert.Equal("F9", svc.Config.GlobalHotkey?.ToString());

        svc.Save();
        Assert.DoesNotContain("\"globalHotkey\": null", File.ReadAllText(ConfigPath));
        Assert.Equal("F9", Reload().Config.GlobalHotkey?.ToString());

        // Ctrl+F9 is another combo: no clash at all.
        Assert.Equal("F9", LoadFrom("{ \"detectionHotkey\": { \"key\": \"F9\", \"ctrl\": true } }").Config.GlobalHotkey?.ToString());
    }

    [Fact]
    public void GlobalHotkeyWithModifiers_Loads()
    {
        AppConfig config = LoadFrom("{ \"globalHotkey\": { \"key\": \"NumPad5\", \"shift\": true } }").Config;

        Assert.Equal("Shift+NumPad5", config.GlobalHotkey?.ToString());
    }

    [Fact]
    public void Reset_RestoresTheNewDefaults()
    {
        var svc = new AppConfigService(ConfigPath);
        svc.Config.AnimationsEnabled = false;
        svc.Config.SoundEnabled = false;
        svc.Config.SoundVolume = 0.9;
        svc.Config.GlobalHotkey = null;
        svc.Save();

        svc.Reset();

        AppConfig loaded = Reload().Config;
        Assert.True(loaded.AnimationsEnabled);
        Assert.True(loaded.SoundEnabled);
        Assert.Equal(0.5, loaded.SoundVolume);
        Assert.Equal("F9", loaded.GlobalHotkey?.ToString());
    }

    [Theory]
    [InlineData(double.NaN, 0.5)]
    [InlineData(double.PositiveInfinity, 0.5)]
    [InlineData(-1.0, 0.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(0.42, 0.42)]
    public void ClampSoundVolume_KeepsTheVolumeIn0Through1(double volume, double expected)
    {
        Assert.Equal(expected, AppConfig.ClampSoundVolume(volume));
    }

    // ---------- Development data-folder override ----------

    [Fact]
    public void WithoutTheOverride_TheDataFolderIsAppData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfigService.AppFolderName);

        Assert.Equal(expected, AppConfigService.ResolveAppDirectory(null));
        Assert.Null(AppConfigService.NormalizeDataDirOverride(null));
        Assert.Null(AppConfigService.NormalizeDataDirOverride("   "));
        Assert.Equal("DBDAUTORESHADE_DATA_DIR", AppConfigService.DataDirEnvironmentVariable);
    }

    [Fact]
    public void TheOverride_IsTheDataFolder_AsAFullPath()
    {
        Assert.Equal(_dir, AppConfigService.ResolveAppDirectory(AppConfigService.NormalizeDataDirOverride($"  {_dir}  ")));

        // A relative override is resolved, never ignored (it must not fall back to the real folder).
        string relative = AppConfigService.NormalizeDataDirOverride(@"smoke\data")!;
        Assert.True(Path.IsPathFullyQualified(relative));
        Assert.EndsWith(@"smoke\data", relative);
    }

    [Fact]
    public void MutexName_IsTheDefault_WithoutTheOverride_AndPerFolderWithIt()
    {
        Assert.Equal(SingleInstanceGuard.DefaultName, SingleInstanceGuard.NameFor(null));
        Assert.Equal(SingleInstanceGuard.DefaultName, SingleInstanceGuard.NameFor(" "));

        string name = SingleInstanceGuard.NameFor(@"C:\Temp\DbdSmoke");
        Assert.StartsWith(SingleInstanceGuard.DefaultName + ".", name);
        Assert.NotEqual(SingleInstanceGuard.DefaultName, name);
        Assert.Equal(name, SingleInstanceGuard.NameFor(@"c:\temp\dbdsmoke\")); // same folder, same name
        Assert.NotEqual(name, SingleInstanceGuard.NameFor(@"C:\Temp\DbdSmoke2"));
        Assert.DoesNotContain('\\', name[@"Local\".Length..]); // a valid kernel object name
    }

    [Fact]
    public void InstancesOnTheSameOverrideFolder_ExcludeEachOther_ButNotOnOthers()
    {
        string a = Path.Combine(_dir, "a");
        string b = Path.Combine(_dir, "b");

        using SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(a));
        Assert.NotNull(first);
        Assert.Null(SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(a)));

        using SingleInstanceGuard? other = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(b));
        Assert.NotNull(other);
    }
}

/// <summary>Tests that change this process's environment run alone (nothing else reads it meanwhile).</summary>
[CollectionDefinition(nameof(ProcessEnvironmentCollection), DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
}

/// <summary>DBDAUTORESHADE_DATA_DIR really moves the data folder, and only while it is set.</summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class DataDirOverrideEnvironmentTests
{
    [Fact]
    public void TheEnvironmentVariable_MovesTheDataFolder_OnlyWhileSet()
    {
        string? original = Environment.GetEnvironmentVariable(AppConfigService.DataDirEnvironmentVariable);
        string dir = Path.Combine(Path.GetTempPath(), "dbd-datadir-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(AppConfigService.DataDirEnvironmentVariable, dir);
            Assert.Equal(dir, AppConfigService.DataDirOverride());
            Assert.Equal(dir, AppConfigService.DefaultAppDirectory());
            Assert.Equal(Path.Combine(dir, "config.json"), AppConfigService.DefaultConfigPath());
            Assert.Equal(Path.Combine(dir, "config.json"), new AppConfigService().ConfigPath);
            Assert.NotEqual(SingleInstanceGuard.DefaultName, SingleInstanceGuard.NameFor(AppConfigService.DataDirOverride()));

            Environment.SetEnvironmentVariable(AppConfigService.DataDirEnvironmentVariable, null);
            Assert.Null(AppConfigService.DataDirOverride());
            Assert.Equal(AppConfigService.ResolveAppDirectory(null), AppConfigService.DefaultAppDirectory());
            Assert.Equal(SingleInstanceGuard.DefaultName, SingleInstanceGuard.NameFor(AppConfigService.DataDirOverride()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppConfigService.DataDirEnvironmentVariable, original);
        }
    }
}
