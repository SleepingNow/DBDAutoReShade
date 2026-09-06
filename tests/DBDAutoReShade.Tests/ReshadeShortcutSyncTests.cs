using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class ReshadeShortcutSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-reshade-" + Guid.NewGuid().ToString("N"));
    private string Ini => Path.Combine(_dir, "ReShade.ini");

    public ReshadeShortcutSyncTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Ini, "[GENERAL]\nPresetPath=C:\\Presets\\Global.ini\n\n[INPUT]\nKeyOverlay=36,0,0,0\n");
    }

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

    private static IReadOnlyCollection<PresetShortcutAssignment> Assignments(params (string Realm, string Preset)[] items)
    {
        var allocated = ReshadeConfigurationService.AllocateShortcuts(items.Select(i => i.Realm));
        return items.Select(i => new PresetShortcutAssignment(i.Realm, i.Preset, allocated[i.Realm])).ToList();
    }

    [Fact]
    public void AllocateShortcuts_IsDeterministic_AndCollisionFree()
    {
        var a = ReshadeConfigurationService.AllocateShortcuts(new[] { "Coldwind Farm", "Autohaven Wreckers" });
        var b = ReshadeConfigurationService.AllocateShortcuts(new[] { "Autohaven Wreckers", "Coldwind Farm" });
        Assert.Equal(a["Coldwind Farm"].VirtualKey, b["Coldwind Farm"].VirtualKey);
        Assert.NotEqual(a["Coldwind Farm"].VirtualKey, a["Autohaven Wreckers"].VirtualKey);
        Assert.Equal(0x7C, a["Autohaven Wreckers"].VirtualKey); // sorted first → F13
    }

    [Fact]
    public void Sync_WritesKeysAndPaths_PreservingOtherContent()
    {
        var svc = new ReshadeConfigurationService();
        var result = svc.SyncPresetShortcuts(Ini, Assignments(
            ("Coldwind Farm", @"C:\Presets\Coldwind.ini"),
            ("MacMillan Estate", @"C:\Presets\MacMillan.ini")));

        Assert.True(result.Changed);
        var doc = IniDocument.Parse(File.ReadAllText(Ini));
        string keys = doc.GetValue("GENERAL", "PresetShortcutKeys")!;
        string paths = doc.GetValue("GENERAL", "PresetShortcutPaths")!;
        // Sorted: Autohaven? No — Coldwind < MacMillan ordinally.
        Assert.Contains(@"C:\Presets\Coldwind.ini", paths);
        Assert.Contains(@"C:\Presets\MacMillan.ini", paths);
        Assert.Equal(8, keys.Split(',').Length); // 2 shortcuts × 4 ints
        Assert.Contains("KeyOverlay=36,0,0,0", doc.ToString()); // untouched
        Assert.Contains("C:\\Presets\\Global.ini", doc.ToString()); // untouched
    }

    [Fact]
    public void Sync_PreservesForeignShortcuts_AndRemovesStaleManaged()
    {
        File.WriteAllText(Ini, "[GENERAL]\nPresetShortcutKeys=124,1,1,1\nPresetShortcutPaths=C:\\User\\Mine.ini\n");
        var svc = new ReshadeConfigurationService();

        // First sync establishes management of Old.ini alongside Coldwind.
        var first = Assignments(
            ("Coldwind Farm", @"C:\Presets\Coldwind.ini"),
            ("Old Realm", @"C:\Presets\Old.ini"));
        ShortcutSyncResult r1 = svc.SyncPresetShortcuts(Ini, first, previouslyManagedPaths: Array.Empty<string>());
        Assert.Equal(1, r1.PreservedForeign); // user's own entry kept
        var previouslyManaged = first.Select(a => a.PresetPath).ToList();

        // Second sync drops Old Realm: its entry is removed, the user's stays.
        var second = Assignments(("Coldwind Farm", @"C:\Presets\Coldwind.ini"));
        ShortcutSyncResult r2 = svc.SyncPresetShortcuts(Ini, second, previouslyManaged);
        Assert.Equal(1, r2.Removed);

        var doc = IniDocument.Parse(File.ReadAllText(Ini));
        string paths = doc.GetValue("GENERAL", "PresetShortcutPaths")!;
        Assert.Contains(@"C:\User\Mine.ini", paths); // foreign preserved
        Assert.DoesNotContain(@"C:\Presets\Old.ini", paths); // stale managed removed
        Assert.Contains(@"C:\Presets\Coldwind.ini", paths);
    }

    [Fact]
    public void Sync_IsIdempotent()
    {
        var svc = new ReshadeConfigurationService();
        var a = Assignments(("Coldwind Farm", @"C:\Presets\Coldwind.ini"));
        Assert.True(svc.SyncPresetShortcuts(Ini, a).Changed);
        Assert.False(svc.SyncPresetShortcuts(Ini, a).Changed);
    }

    [Fact]
    public void ReadCurrentPreset_ReturnsPresetPath()
    {
        Assert.Equal("C:\\Presets\\Global.ini", ReshadeConfigurationService.ReadCurrentPreset(Ini));
        Assert.Null(ReshadeConfigurationService.ReadCurrentPreset(Path.Combine(_dir, "missing.ini")));
    }

    [Fact]
    public void ReadCurrentPresetAbsolute_ResolvesRelativePaths()
    {
        File.WriteAllText(Ini, "[GENERAL]\nPresetPath=..\\P\\Coldwind.ini\n");
        string? absolute = ReshadeConfigurationService.ReadCurrentPresetAbsolute(Ini);
        string expected = Path.GetFullPath(Path.Combine(_dir, "..", "P", "Coldwind.ini"));
        Assert.Equal(expected, absolute);
    }
}
