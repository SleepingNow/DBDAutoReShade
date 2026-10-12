using System.Windows.Input;
using DBDAutoReShade.App.ViewModels;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Tests;

/// <summary>Hotkey-capture key names (view model) round-trip through <see cref="HotkeyParser"/>.</summary>
public sealed class HotkeyKeyNameTests
{
    [Theory]
    [InlineData(Key.NumPad0, "NumPad0", 0x60)]
    [InlineData(Key.NumPad5, "NumPad5", 0x65)]
    [InlineData(Key.NumPad9, "NumPad9", 0x69)]
    [InlineData(Key.D0, "0", 0x30)]
    [InlineData(Key.D9, "9", 0x39)]
    [InlineData(Key.A, "A", 0x41)]
    [InlineData(Key.F8, "F8", 0x77)]
    [InlineData(Key.F24, "F24", 0x87)]
    public void CapturedKey_MapsToNameWithMatchingVirtualKey(Key key, string expectedName, int expectedVk)
    {
        string? name = MainViewModel.KeyToName(key);
        Assert.Equal(expectedName, name);

        var hotkey = new DetectionHotkeyConfig { Key = name!, Ctrl = true };
        Assert.True(HotkeyParser.TryParse(hotkey, out int vk, out _, out _));
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    [InlineData(Key.LeftCtrl)]
    public void UnsupportedKeys_MapToNull(Key key)
    {
        Assert.Null(MainViewModel.KeyToName(key));
    }
}
