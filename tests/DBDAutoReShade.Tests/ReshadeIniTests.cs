using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class ReshadeIniTests
{
    private const string Sample = """
        ; ReShade configuration
        [GENERAL]
        EffectSearchPaths=.\reshade-shaders\Shaders
        PresetPath=C:\Presets\Global.ini

        [INPUT]
        KeyOverlay=36,0,0,0
        """;

    [Fact]
    public void Parse_PreservesCommentsAndReadsValues()
    {
        var doc = IniDocument.Parse(Sample);
        Assert.Equal("C:\\Presets\\Global.ini", doc.GetValue("GENERAL", "PresetPath"));
        Assert.Equal("36,0,0,0", doc.GetValue("input", "keyoverlay")); // case-insensitive
    }

    [Fact]
    public void SetValue_UpdatesInPlace_PreservingSurroundings()
    {
        var doc = IniDocument.Parse(Sample);
        Assert.True(doc.SetValue("GENERAL", "PresetPath", "C:\\Presets\\Coldwind.ini"));
        string text = doc.ToString();
        Assert.Contains("C:\\Presets\\Coldwind.ini", text);
        Assert.Contains("; ReShade configuration", text);
        Assert.Contains("KeyOverlay=36,0,0,0", text);
        Assert.False(doc.SetValue("GENERAL", "PresetPath", "C:\\Presets\\Coldwind.ini")); // no-op
    }

    [Fact]
    public void SetValue_CreatesMissingSectionAndKey()
    {
        var doc = IniDocument.Parse("[GENERAL]\nPresetPath=x\n");
        doc.SetValue("GENERAL", "PresetShortcutKeys", "124,1,1,1");
        doc.SetValue("GENERAL", "PresetShortcutPaths", "C:\\P\\A.ini");
        Assert.Equal("124,1,1,1", doc.GetValue("GENERAL", "PresetShortcutKeys"));
    }

    [Fact]
    public void MissingKey_ReturnsNull()
    {
        var doc = IniDocument.Parse("[GENERAL]\n");
        Assert.Null(doc.GetValue("GENERAL", "PresetPath"));
        Assert.Null(doc.GetValue("NOPE", "PresetPath"));
    }
}
