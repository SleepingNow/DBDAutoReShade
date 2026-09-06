using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Desktop.Interop;
using DBDAutoReShade.Desktop.Services;

namespace DBDAutoReShade.Tests;

public sealed class KeySimulatorTests
{
    [Fact]
    public void FullCombo_ProducesDownUpSequence()
    {
        var shortcut = new ReshadeShortcutConfig { VirtualKey = 0x7C, Ctrl = true, Shift = true, Alt = true };
        SendInputKeySimulator.BuildPress(shortcut, out NativeMethods.INPUT[] downs, out NativeMethods.INPUT[] ups);

        // Down-phase: 3 modifiers + key. Up-phase: key + 3 modifiers (reverse order).
        Assert.Equal(4, downs.Length);
        Assert.Equal(4, ups.Length);
        Assert.All(downs.Concat(ups), i => Assert.Equal(NativeMethods.INPUT_KEYBOARD, i.type));
        Assert.All(downs, i => Assert.Equal(0u, i.u.ki.dwFlags));
        Assert.All(ups, i => Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, i.u.ki.dwFlags));

        Assert.Equal(0x7Cu, downs[3].u.ki.wVk); // VK_F13 down last
        Assert.Equal(0x7Cu, ups[0].u.ki.wVk); // VK_F13 up first
        Assert.Equal(0x12u, ups[1].u.ki.wVk); // Alt up next
        Assert.Equal(0x11u, ups[3].u.ki.wVk); // Ctrl up last
    }

    [Fact]
    public void BareKey_ProducesSinglePress()
    {
        var shortcut = new ReshadeShortcutConfig { VirtualKey = 0x77, Ctrl = false, Shift = false, Alt = false };
        SendInputKeySimulator.BuildPress(shortcut, out NativeMethods.INPUT[] downs, out NativeMethods.INPUT[] ups);
        Assert.Single(downs);
        Assert.Single(ups);
    }
}
