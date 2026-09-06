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
        Assert.False(PresetValidationService.ValidatePresetFile(_dir + "\\").IsValid);
    }

    [Fact]
    public void NonIniContent_Fails()
    {
        string path = Write("junk.ini", "\0\0\0binary garbage without markers");
        // Contains no '=' or '[' → rejected.
        Assert.False(PresetValidationService.ValidatePresetFile(path).IsValid);
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
}
