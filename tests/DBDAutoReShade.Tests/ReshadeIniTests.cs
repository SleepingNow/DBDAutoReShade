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

    [Theory]
    [InlineData("[GENERAL]\nPresetPath=C:\\P\\G.ini\n\n[INPUT]\nKeyOverlay=36,0,0,0\n")] // LF
    [InlineData("[GENERAL]\r\nPresetPath=C:\\P\\G.ini\r\n\r\n[INPUT]\r\nKeyOverlay=36,0,0,0\r\n")] // CRLF
    [InlineData("[GENERAL]\r\nPresetPath=C:\\P\\G.ini\n[INPUT]\rKeyOverlay=36,0,0,0")] // mixed, no final newline
    [InlineData("[GENERAL]\nPresetPath = C:\\P\\G.ini  \n  Key2\t=\tv  \n; comment\nnot a key line\n\n\n")] // spacing, blank tail
    [InlineData("\uFEFF[GENERAL]\r\nPresetPath=x\r\n")] // BOM character
    [InlineData("")]
    public void ParseToString_RoundTripsByteForByte(string text)
    {
        Assert.Equal(text, IniDocument.Parse(text).ToString());
    }

    [Fact]
    public void Parse_IgnoresLeadingBomForSectionDetection()
    {
        var doc = IniDocument.Parse("\uFEFF[GENERAL]\r\nPresetPath=x\r\n");
        Assert.Equal("x", doc.GetValue("GENERAL", "PresetPath"));
    }

    [Fact]
    public void SetValue_ChangesOnlyTheValue_PreservingSpacingAndOtherLines()
    {
        const string text = "[GENERAL]\nEffectSearchPaths = .\\Shaders  \nPresetPath = C:\\P\\Old.ini  \n\n[INPUT]\nKeyOverlay= 36,0,0,0\n";
        var doc = IniDocument.Parse(text);
        Assert.True(doc.SetValue("GENERAL", "PresetPath", "C:\\P\\New.ini"));

        string expected = text.Replace("PresetPath = C:\\P\\Old.ini  ", "PresetPath = C:\\P\\New.ini  ");
        Assert.Equal(expected, doc.ToString());
        Assert.Equal("C:\\P\\New.ini", IniDocument.Parse(doc.ToString()).GetValue("GENERAL", "PresetPath"));
    }

    [Fact]
    public void SetValue_KeepsLfNewlines_AndAddsNoTrailingBlankLine()
    {
        const string text = "[GENERAL]\nPresetPath=x\n";
        var doc = IniDocument.Parse(text);
        doc.SetValue("GENERAL", "PresetPath", "y");
        Assert.Equal("[GENERAL]\nPresetPath=y\n", doc.ToString());
    }

    [Fact]
    public void SetValue_KeepsCrlfNewlines()
    {
        var doc = IniDocument.Parse("[GENERAL]\r\nPresetPath=x\r\n");
        doc.SetValue("GENERAL", "PresetPath", "y");
        Assert.Equal("[GENERAL]\r\nPresetPath=y\r\n", doc.ToString());
    }

    [Fact]
    public void NewKey_UsesTheDocumentsNewline()
    {
        var doc = IniDocument.Parse("[GENERAL]\nPresetPath=x\n\n[INPUT]\nKeyOverlay=36,0,0,0\n");
        doc.SetValue("GENERAL", "PresetShortcutKeys", "124,1,1,1");
        Assert.Equal("[GENERAL]\nPresetPath=x\nPresetShortcutKeys=124,1,1,1\n\n[INPUT]\nKeyOverlay=36,0,0,0\n", doc.ToString());
    }

    [Fact]
    public void NewKey_AtEndOfFileWithoutFinalNewline_KeepsLinesSeparate()
    {
        var doc = IniDocument.Parse("[GENERAL]\r\nPresetPath=x");
        doc.SetValue("GENERAL", "PresetShortcutKeys", "124,1,1,1");
        doc.SetValue("APP", "Foo", "1");
        Assert.Equal("[GENERAL]\r\nPresetPath=x\r\nPresetShortcutKeys=124,1,1,1\r\n\r\n[APP]\r\nFoo=1", doc.ToString());
    }

    [Fact]
    public void RepeatedWrites_AreStable()
    {
        string text = "[GENERAL]\nPresetPath = a\nPresetShortcutKeys=\n";
        for (int i = 0; i < 3; i++)
        {
            var doc = IniDocument.Parse(text);
            doc.SetValue("GENERAL", "PresetPath", "b");
            doc.SetValue("GENERAL", "PresetShortcutKeys", "124,1,1,1");
            string next = doc.ToString();
            if (i > 0)
            {
                Assert.Equal(text, next); // idempotent byte-for-byte
            }

            text = next;
        }

        Assert.Equal("[GENERAL]\nPresetPath = b\nPresetShortcutKeys=124,1,1,1\n", text);
    }
}
