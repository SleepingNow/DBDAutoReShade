using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

public sealed class HotkeyParserTests
{
    [Theory]
    [InlineData("F8", 0x77)]
    [InlineData("f8", 0x77)]
    [InlineData("F1", 0x70)]
    [InlineData("F24", 0x87)]
    [InlineData("A", 0x41)]
    [InlineData("Z", 0x5A)]
    [InlineData("0", 0x30)]
    [InlineData("9", 0x39)]
    public void ValidKeys_Parse(string name, int expectedVk)
    {
        Assert.True(HotkeyParser.TryParseKey(name, out int vk));
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData("Foo")]
    [InlineData("F0")]
    [InlineData("F25")]
    [InlineData("")]
    [InlineData("Enter")]
    public void InvalidKeys_Fail(string name)
    {
        Assert.False(HotkeyParser.TryParseKey(name, out _));
    }

    [Fact]
    public void Modifiers_EncodeCorrectly()
    {
        var hotkey = new DetectionHotkeyConfig { Key = "F9", Ctrl = true, Shift = true, Alt = true };
        Assert.True(HotkeyParser.TryParse(hotkey, out int vk, out int mods));
        Assert.Equal(0x78, vk);
        Assert.Equal(HotkeyParser.ModAlt | HotkeyParser.ModControl | HotkeyParser.ModShift, mods);
    }

    [Fact]
    public void BareF8_HasNoModifiers()
    {
        var hotkey = new DetectionHotkeyConfig { Key = "F8" };
        Assert.True(HotkeyParser.TryParse(hotkey, out _, out int mods));
        Assert.Equal(0, mods);
        Assert.Equal("F8", hotkey.ToString());
    }

    [Fact]
    public void ToString_ListsModifiers()
    {
        var hotkey = new DetectionHotkeyConfig { Key = "F8", Ctrl = true, Alt = true };
        Assert.Equal("Ctrl+Alt+F8", hotkey.ToString());
    }
}
