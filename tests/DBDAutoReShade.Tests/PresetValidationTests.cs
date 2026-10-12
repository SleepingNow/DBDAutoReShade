using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class PresetValidationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbd-preset-" + Guid.NewGuid().ToString("N"));

    public PresetValidationTests() => Directory.CreateDirectory(_dir);

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

    private string Write(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ValidReshadePreset_PassesStrong()
    {
        string path = Write("Coldwind.ini", "[General]\nTechniques=Clarity,Colourfulness\nTechniqueSorting=Clarity,Colourfulness\n");
        var r = PresetValidationService.ValidatePresetFile(path);
        Assert.True(r.IsValid);
        Assert.Equal(PresetValidationStrength.Strong, r.Strength);
    }

    [Fact]
    public void IniLikeFile_PassesWeak()
    {
        string path = Write("Odd.ini", "[Something]\nfoo=bar\n");
        var r = PresetValidationService.ValidatePresetFile(path);
        Assert.True(r.IsValid);
        Assert.Equal(PresetValidationStrength.Weak, r.Strength);
    }

    [Fact]
    public void MissingFile_Fails()
    {
        Assert.False(PresetValidationService.ValidatePresetFile(Path.Combine(_dir, "Nope.ini")).IsValid);
    }

    [Fact]
    public void WrongExtension_Fails()
    {
        string path = Write("preset.txt", "Techniques=Foo");
        Assert.False(PresetValidationService.ValidatePresetFile(path).IsValid);
    }

    [Fact]
    public void Directory_Fails()
    {
        var r = PresetValidationService.ValidatePresetFile(_dir + "\\");
        Assert.False(r.IsValid);
        Assert.Contains("is a folder", r.Message); // the folder branch is reached, not the extension check
    }

    [Fact]
    public void DirectoryNamedLikeAnIniFile_FailsWithTheFolderMessage()
    {
        string folder = Path.Combine(_dir, "Looks Like A Preset.ini");
        Directory.CreateDirectory(folder);
        var r = PresetValidationService.ValidatePresetFile(folder);
        Assert.False(r.IsValid);
        Assert.Contains("is a folder", r.Message);
    }

    [Fact]
    public void NonIniContent_Fails()
    {
        string path = Write("junk.ini", "\0\0\0binary garbage without markers");
        // Contains no '=' or '[' → rejected.
        Assert.False(PresetValidationService.ValidatePresetFile(path).IsValid);
    }

    [Fact]
    public void PathWithComma_Fails_WithAClearMessage()
    {
        string inFile = Write("Cold,wind.ini", "[General]\nTechniques=A\n");
        string inFolder = Write(Path.Combine("My, Presets", "Coldwind.ini"), "[General]\nTechniques=A\n");

        foreach (string path in new[] { inFile, inFolder })
        {
            var r = PresetValidationService.ValidatePresetFile(path);
            Assert.False(r.IsValid, path);
            Assert.Contains("comma", r.Message);
        }
    }

    [Fact]
    public void NormalizePresetPath_ResolvesRelativePathsAgainstTheGivenFolder()
    {
        string baseDir = Path.Combine(_dir, "ReShadeFolder");
        Assert.Equal(Path.Combine(baseDir, "presets", "A.ini"),
            PresetValidationService.NormalizePresetPath(@" ""presets\A.ini"" ", baseDir));
        Assert.Equal(Path.Combine(_dir, "B.ini"), PresetValidationService.NormalizePresetPath(@"..\B.ini", baseDir));
        Assert.Equal(@"C:\Abs\C.ini", PresetValidationService.NormalizePresetPath(@"C:\Abs\C.ini", baseDir)); // absolute unchanged
        Assert.Equal(Path.GetFullPath("rel.ini"), PresetValidationService.NormalizePresetPath("rel.ini", null)); // CWD, as before
        Assert.Equal(Path.GetFullPath("rel.ini"), PresetValidationService.NormalizePresetPath("rel.ini"));
        Assert.Equal(string.Empty, PresetValidationService.NormalizePresetPath("  ", baseDir));
    }

    [Fact]
    public void DuplicateRealmAssignment_IsDetected_CaseInsensitive()
    {
        var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Coldwind Farm"] = @"C:\Presets\Coldwind.ini"
        };
        Assert.Equal("Coldwind Farm",
            PresetValidationService.FindDuplicateRealm(@"c:\presets\coldwind.ini", "Autohaven Wreckers", assigned));
        Assert.Null(PresetValidationService.FindDuplicateRealm(@"C:\Presets\Other.ini", "Autohaven Wreckers", assigned));
        Assert.Null(PresetValidationService.FindDuplicateRealm(@"C:\Presets\Coldwind.ini", "Coldwind Farm", assigned)); // self
    }

    private static AppConfig Config() => new()
    {
        GlobalPresetPath = @"C:\Presets\Global.ini",
        RealmPresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Coldwind Farm"] = @"C:\Presets\Coldwind.ini"
        }
    };

    [Fact]
    public void FindDuplicateAssignment_ConsidersTheGlobalPreset()
    {
        AppConfig config = Config();

        // A realm reusing the Global file is reported (FindDuplicateRealm cannot see it).
        Assert.Null(PresetValidationService.FindDuplicateRealm(@"C:\Presets\Global.ini", "MacMillan Estate", config.RealmPresets));
        Assert.Equal(AppConfigKeys.GlobalShortcutKey,
            PresetValidationService.FindDuplicateAssignment(@"c:\presets\GLOBAL.ini", "MacMillan Estate", config));
        Assert.Equal("Global/Menu", AppConfigKeys.DisplayName(AppConfigKeys.GlobalShortcutKey));

        // Global reusing a realm's file is reported too.
        Assert.Equal("Coldwind Farm",
            PresetValidationService.FindDuplicateAssignment(@"C:\Presets\Coldwind.ini", AppConfigKeys.GlobalShortcutKey, config));

        // Re-assigning the same name, or a new file, is fine.
        Assert.Null(PresetValidationService.FindDuplicateAssignment(@"C:\Presets\Global.ini", AppConfigKeys.GlobalShortcutKey, config));
        Assert.Null(PresetValidationService.FindDuplicateAssignment(@"C:\Presets\Coldwind.ini", "coldwind farm", config));
        Assert.Null(PresetValidationService.FindDuplicateAssignment(@"C:\Presets\New.ini", "MacMillan Estate", config));
        Assert.Null(PresetValidationService.FindDuplicateAssignment(" ", "MacMillan Estate", config));
    }
}
