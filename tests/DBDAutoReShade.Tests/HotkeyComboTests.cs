using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

/// <summary>
/// <see cref="HotkeyParser.SameCombo"/>: the app's two global hotkeys must never be the same combo.
/// <see cref="HotkeyParser.IsReshadeShortcutCombo"/>: neither may be a combo the app sends to ReShade.
/// </summary>
public sealed class HotkeyComboTests
{
    private static DetectionHotkeyConfig Key(string key, bool ctrl = false, bool shift = false, bool alt = false) =>
        new() { Key = key, Ctrl = ctrl, Shift = shift, Alt = alt };

    [Fact]
    public void SameKeyAndModifiers_AreTheSameCombo_WhateverTheCase()
    {
        Assert.True(HotkeyParser.SameCombo(Key("F9"), Key("F9")));
        Assert.True(HotkeyParser.SameCombo(Key("f9"), Key("F9")));
        Assert.True(HotkeyParser.SameCombo(Key(" F9 "), Key("F9")));
        Assert.True(HotkeyParser.SameCombo(Key("g", ctrl: true, alt: true), Key("G", ctrl: true, alt: true)));
        Assert.True(HotkeyParser.SameCombo(Key("numpad3", shift: true), Key("NumPad3", shift: true)));
    }

    [Fact]
    public void DifferentKeysOrModifiers_AreDifferentCombos()
    {
        Assert.False(HotkeyParser.SameCombo(Key("F8"), Key("F9")));
        Assert.False(HotkeyParser.SameCombo(Key("F9", ctrl: true), Key("F9")));
        Assert.False(HotkeyParser.SameCombo(Key("F9", shift: true), Key("F9", alt: true)));
        Assert.False(HotkeyParser.SameCombo(Key("3", ctrl: true), Key("NumPad3", ctrl: true))); // top row vs number pad
    }

    [Fact]
    public void Null_IsNeverTheSameCombo()
    {
        Assert.False(HotkeyParser.SameCombo(null, Key("F9")));
        Assert.False(HotkeyParser.SameCombo(Key("F9"), null));
        Assert.False(HotkeyParser.SameCombo(null, null));
    }

    [Fact]
    public void UnparseableKeys_CompareByName()
    {
        Assert.True(HotkeyParser.SameCombo(Key("Space"), Key("space")));
        Assert.False(HotkeyParser.SameCombo(Key("Space"), Key("F9")));
    }

    [Theory]
    [InlineData("F13")]
    [InlineData("f19")]
    [InlineData("F24")]
    [InlineData("0")]
    [InlineData("9")]
    public void TheAppsShortcutPool_IsReservedForReShade(string key)
    {
        DetectionHotkeyConfig combo = Key(key, ctrl: true, shift: true, alt: true);

        Assert.True(HotkeyParser.IsReshadeShortcutCombo(combo));
        Assert.True(HotkeyParser.IsReshadeShortcutCombo(combo, Array.Empty<ReshadeShortcutConfig>()));
    }

    [Theory]
    [InlineData("F12", true, true, true)] // F1–F12 are not in the pool
    [InlineData("F13", true, false, true)] // without Shift
    [InlineData("F13", true, true, false)] // without Alt
    [InlineData("F13", false, false, false)] // bare F13
    [InlineData("NumPad0", true, true, true)] // the number pad is not the top row
    [InlineData("A", true, true, true)]
    [InlineData("F9", false, false, false)]
    public void OtherCombos_AreNotReserved(string key, bool ctrl, bool shift, bool alt)
    {
        Assert.False(HotkeyParser.IsReshadeShortcutCombo(Key(key, ctrl, shift, alt)));
    }

    [Fact]
    public void AnAssignedShortcutOutsideThePool_IsReservedToo()
    {
        // A hand-edited config may hold one; the app would send it until the next sync.
        var assigned = new ReshadeShortcutConfig?[] { null, new() { VirtualKey = 0x82, Ctrl = true, Alt = true, Shift = false } };

        Assert.True(HotkeyParser.IsReshadeShortcutCombo(Key("F19", ctrl: true, alt: true), assigned));
        Assert.False(HotkeyParser.IsReshadeShortcutCombo(Key("F19", ctrl: true), assigned));
        Assert.False(HotkeyParser.IsReshadeShortcutCombo(Key("F19", ctrl: true, alt: true), null));
    }

    [Fact]
    public void NoHotkey_OrAnUnparseableOne_IsNotReserved()
    {
        Assert.False(HotkeyParser.IsReshadeShortcutCombo(null));
        Assert.False(HotkeyParser.IsReshadeShortcutCombo(Key("Space", ctrl: true, shift: true, alt: true)));
        Assert.False(HotkeyParser.IsReshadeShortcutCombo(Key("", ctrl: true, shift: true, alt: true)));
    }

    [Fact]
    public void TheReservedError_NamesTheCombo()
    {
        Assert.Equal(
            "Ctrl+Shift+Alt+F19 is reserved for ReShade preset shortcuts.",
            HotkeyParser.ReservedForReshadeError(Key("F19", ctrl: true, shift: true, alt: true)));
    }
}
