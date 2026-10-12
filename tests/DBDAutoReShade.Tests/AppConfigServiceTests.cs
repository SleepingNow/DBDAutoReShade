using System.Text.Json;
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
        Assert.Null(reloaded.LastLoadWarning);
    }

    [Fact]
    public void MissingFile_LoadsDefaults()
    {
        var svc = new AppConfigService(PathFor());
        svc.Load();
        Assert.False(svc.Config.SetupCompleted);
        Assert.Equal("F8", svc.Config.DetectionHotkey.Key);
        Assert.Null(svc.LastLoadWarning);
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
        string backup = Assert.Single(Directory.GetFiles(_dir, "config.corrupt-*.json"));
        Assert.Equal("{ this is not json !!!", File.ReadAllText(backup));
        Assert.Contains(Path.GetFileName(backup), svc.LastLoadWarning);
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

    // ---- B7: save safety ----

    [Fact]
    public void ConcurrentSaves_AreSerialized_AndLeaveNoTempFiles()
    {
        var svc = new AppConfigService(PathFor());
        svc.Config.GlobalPresetPath = @"C:\Presets\Global.ini";

        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            try
            {
                svc.Save();
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        Assert.Empty(errors);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        var reloaded = new AppConfigService(PathFor());
        reloaded.Load();
        Assert.Equal(@"C:\Presets\Global.ini", reloaded.Config.GlobalPresetPath);
    }

    [Fact]
    public void FailedSave_RemovesItsTempFile()
    {
        Directory.CreateDirectory(PathFor()); // a folder where the config file should go: the final rename fails
        var svc = new AppConfigService(PathFor());

        Assert.ThrowsAny<Exception>(() => svc.Save());

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Save_WritesValidJson_WithoutByteOrderMark()
    {
        var svc = new AppConfigService(PathFor());
        svc.Config.DebugMode = true;
        svc.Save();

        byte[] bytes = File.ReadAllBytes(PathFor());
        Assert.Equal((byte)'{', bytes[0]);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        Assert.True(doc.RootElement.GetProperty("debugMode").GetBoolean());
    }

    // ---- B7: quarantine ----

    [Fact]
    public void CorruptFiles_GetUniqueQuarantineNames()
    {
        Directory.CreateDirectory(_dir);
        var svc = new AppConfigService(PathFor());

        for (int i = 0; i < 3; i++)
        {
            File.WriteAllText(PathFor(), "{ broken " + i);
            svc.Load();
            Assert.False(File.Exists(PathFor()));
        }

        Assert.Equal(3, Directory.GetFiles(_dir, "config.corrupt-*.json").Length);
    }

    [Fact]
    public void CorruptFile_ThatCannotBeMovedAside_IsKept_AndNeverOverwritten()
    {
        Directory.CreateDirectory(_dir);
        const string corrupt = "{ \"setupCompleted\": tru";
        File.WriteAllText(PathFor(), corrupt);
        var svc = new AppConfigService(PathFor());

        // Readable, but the quarantine move is blocked (no FILE_SHARE_DELETE).
        using (new FileStream(PathFor(), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            svc.Load();

            Assert.False(svc.Config.SetupCompleted); // defaults
            Assert.Contains("could not be moved aside", svc.LastLoadWarning);
            Assert.Empty(Directory.GetFiles(_dir, "config.corrupt-*.json"));

            svc.Config.DebugMode = true;
            Assert.Throws<IOException>(() => svc.Save());
        }

        Assert.Equal(corrupt, File.ReadAllText(PathFor())); // original untouched
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // Once the file can be moved, the next save preserves it first, then writes.
        svc.Save();
        string backup = Assert.Single(Directory.GetFiles(_dir, "config.corrupt-*.json"));
        Assert.Equal(corrupt, File.ReadAllText(backup));
        var reloaded = new AppConfigService(PathFor());
        reloaded.Load();
        Assert.True(reloaded.Config.DebugMode);
    }

    // ---- B7: IO errors are not corruption ----

    [Fact]
    public void LockedFile_IsNotQuarantined_LoadThrowsClearError()
    {
        var original = new AppConfigService(PathFor());
        original.Config.SetupCompleted = true;
        original.Save();
        string before = File.ReadAllText(PathFor());

        var svc = new AppConfigService(PathFor());
        IOException ex;
        using (new FileStream(PathFor(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ex = Assert.Throws<IOException>(() => svc.Load());
        }

        Assert.Contains("Could not read the configuration file", ex.Message);
        Assert.Equal(before, File.ReadAllText(PathFor()));
        Assert.Empty(Directory.GetFiles(_dir, "config.corrupt-*.json"));
    }

    [Fact]
    public void TransientLock_IsRetried_AndRealConfigLoads()
    {
        var original = new AppConfigService(PathFor());
        original.Config.SetupCompleted = true;
        original.Save();

        var handle = new FileStream(PathFor(), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var releaser = new Thread(() =>
        {
            Thread.Sleep(60);
            handle.Dispose();
        });
        releaser.Start();

        var svc = new AppConfigService(PathFor());
        svc.Load();
        releaser.Join();

        Assert.True(svc.Config.SetupCompleted);
        Assert.Empty(Directory.GetFiles(_dir, "config.corrupt-*.json"));
    }

    // ---- B7: hand-edited data ----

    [Fact]
    public void KeysDifferingOnlyByCase_Merge_LastWins_NotQuarantined()
    {
        Directory.CreateDirectory(_dir);
        const string json = """
            {
              "setupCompleted": true,
              "realmPresets": { "Coldwind Farm": "C:\\a.ini", "COLDWIND FARM": "C:\\b.ini", "Haddonfield": "C:\\h.ini" },
              "reshadeShortcuts": { "Global": { "virtualKey": 124 }, "global": { "virtualKey": 125 } }
            }
            """;
        File.WriteAllText(PathFor(), json);

        var svc = new AppConfigService(PathFor());
        svc.Load();

        Assert.True(svc.Config.SetupCompleted);
        Assert.Null(svc.LastLoadWarning);
        Assert.True(File.Exists(PathFor()));
        Assert.Empty(Directory.GetFiles(_dir, "config.corrupt-*.json"));
        Assert.Equal(2, svc.Config.RealmPresets.Count);
        Assert.Equal(@"C:\b.ini", svc.Config.RealmPresets["coldwind farm"]);
        Assert.Equal(125, Assert.Single(svc.Config.ReshadeShortcuts).Value.VirtualKey);
        Assert.Equal(125, svc.Config.ReshadeShortcuts["GLOBAL"].VirtualKey);
    }

    [Fact]
    public void JsonNulls_AreReplacedWithSafeDefaults()
    {
        Directory.CreateDirectory(_dir);
        const string json = """
            {
              "setupCompleted": true,
              "realmPresets": { "Coldwind Farm": null, "Haddonfield": "C:\\h.ini" },
              "reshadeShortcuts": null,
              "lastSyncedPresetPaths": null,
              "detectionHotkey": null
            }
            """;
        File.WriteAllText(PathFor(), json);

        var svc = new AppConfigService(PathFor());
        svc.Load();

        Assert.True(svc.Config.SetupCompleted);
        Assert.Equal("C:\\h.ini", Assert.Single(svc.Config.RealmPresets).Value);
        Assert.NotNull(svc.Config.ReshadeShortcuts);
        Assert.NotNull(svc.Config.LastSyncedPresetPaths);
        Assert.Equal("F8", svc.Config.DetectionHotkey.Key);
    }
}
