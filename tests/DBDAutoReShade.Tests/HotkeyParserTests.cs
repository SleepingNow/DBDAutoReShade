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

    // ----- S5: number-pad keys are distinct from the top-row digits -----

    [Theory]
    [InlineData("NumPad0", 0x60)]
    [InlineData("NumPad5", 0x65)]
    [InlineData("NumPad9", 0x69)]
    [InlineData("numpad3", 0x63)]
    [InlineData("NUMPAD7", 0x67)]
    public void NumPadKeys_ParseToNumPadVirtualKeys(string name, int expectedVk)
    {
        Assert.True(HotkeyParser.TryParseKey(name, out int vk));
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData("NumPad")]
    [InlineData("NumPad10")]
    [InlineData("NumPadA")]
    [InlineData("Num1")]
    public void MalformedNumPadKeys_Fail(string name)
    {
        Assert.False(HotkeyParser.TryParseKey(name, out _));
    }

    [Fact]
    public void NumPadDigit_DoesNotRegisterTopRowDigit()
    {
        var hotkey = new DetectionHotkeyConfig { Key = "NumPad1", Ctrl = true };
        Assert.True(HotkeyParser.TryParse(hotkey, out int vk, out int mods, out string? error));
        Assert.Null(error);
        Assert.Equal(0x61, vk);
        Assert.NotEqual(0x31, vk);
        Assert.Equal(HotkeyParser.ModControl, mods);
    }

    // ----- S6: bare letters / digits would be swallowed system-wide -----

    [Theory]
    [InlineData("W")]
    [InlineData("a")]
    [InlineData("5")]
    [InlineData("NumPad2")]
    public void BareLetterOrDigit_IsRejectedWithClearError(string key)
    {
        var hotkey = new DetectionHotkeyConfig { Key = key };
        Assert.False(HotkeyParser.TryParse(hotkey, out int vk, out int mods, out string? error));
        Assert.Equal(0, vk);
        Assert.Equal(0, mods);
        Assert.Equal(
            "Letters and number keys need Ctrl, Alt or Shift (they would block typing and game controls).",
            error);
        Assert.Equal(HotkeyParser.ModifierRequiredError, error);

        // The legacy overload rejects it too.
        Assert.False(HotkeyParser.TryParse(hotkey, out _, out _));
    }

    [Theory]
    [InlineData("W", true, false, false)]
    [InlineData("W", false, true, false)]
    [InlineData("5", false, false, true)]
    [InlineData("NumPad4", false, true, false)]
    public void LetterOrDigit_WithAnyModifier_IsAccepted(string key, bool ctrl, bool alt, bool shift)
    {
        var hotkey = new DetectionHotkeyConfig { Key = key, Ctrl = ctrl, Alt = alt, Shift = shift };
        Assert.True(HotkeyParser.TryParse(hotkey, out _, out int mods, out string? error));
        Assert.Null(error);
        Assert.NotEqual(0, mods);
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("F8")]
    [InlineData("F13")]
    [InlineData("F24")]
    public void BareFunctionKeys_StayAllowed(string key)
    {
        var hotkey = new DetectionHotkeyConfig { Key = key };
        Assert.True(HotkeyParser.TryParse(hotkey, out int vk, out int mods, out string? error));
        Assert.Null(error);
        Assert.Equal(0, mods);
        Assert.True(HotkeyParser.IsFunctionKey(vk));
    }

    [Fact]
    public void UnknownKey_ReportsUnsupported()
    {
        var hotkey = new DetectionHotkeyConfig { Key = "Enter", Ctrl = true };
        Assert.False(HotkeyParser.TryParse(hotkey, out _, out _, out string? error));
        Assert.NotNull(error);
        Assert.Contains("Unsupported hotkey", error);
        Assert.Contains("NumPad", error);
    }

    [Fact]
    public void NullHotkey_FailsWithError()
    {
        Assert.False(HotkeyParser.TryParse(null, out _, out _, out string? error));
        Assert.NotNull(error);
    }

    // ----- S4: auto-repeat suppression is not part of the user modifier bitmask -----

    [Fact]
    public void ToModifiers_DoesNotIncludeNoRepeat()
    {
        var hotkey = new DetectionHotkeyConfig { Key = "F9", Ctrl = true, Shift = true, Alt = true };
        Assert.Equal(0, HotkeyParser.ToModifiers(hotkey) & HotkeyParser.ModNoRepeat);
        Assert.Equal(0x4000, HotkeyParser.ModNoRepeat);
    }
}
