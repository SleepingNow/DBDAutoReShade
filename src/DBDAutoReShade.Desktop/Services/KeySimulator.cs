using System.Runtime.InteropServices;
using DBDAutoReShade.Core.Models;
using DBDAutoReShade.Desktop.Interop;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>Simulates the app-managed ReShade preset shortcuts (normal SendInput keystrokes).</summary>
public interface IKeySimulator
{
    bool SendShortcut(ReshadeShortcutConfig shortcut);
}

/// <summary>
/// SendInput-based implementation. Keys go to the foreground window (the game).
/// The combo is HELD briefly (down-phase, short sleep, up-phase in two SendInput
/// batches): games and ReShade sample input per frame, and a down+up inside a
/// single batch can fall between two polls and never be seen.
/// </summary>
public sealed class SendInputKeySimulator : IKeySimulator
{
    /// <summary>How long the combo is held (ms). Long enough for frame-polled input, short enough to be invisible.</summary>
    public const int DefaultHoldMilliseconds = 80;

    // Modifier VKs.
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkMenu = 0x12; // Alt

    public bool SendShortcut(ReshadeShortcutConfig shortcut) =>
        SendShortcut(shortcut, DefaultHoldMilliseconds);

    public bool SendShortcut(ReshadeShortcutConfig shortcut, int holdMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        BuildPress(shortcut, out NativeMethods.INPUT[] downs, out NativeMethods.INPUT[] ups);
        if (!Send(downs))
        {
            return false;
        }

        if (holdMilliseconds > 0)
        {
            Thread.Sleep(holdMilliseconds);
        }

        return Send(ups);
    }

    private static bool Send(NativeMethods.INPUT[] inputs)
    {
        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        return sent == inputs.Length;
    }

    /// <summary>Pure press builder (unit-testable without touching global input).</summary>
    public static void BuildPress(
        ReshadeShortcutConfig shortcut,
        out NativeMethods.INPUT[] downs,
        out NativeMethods.INPUT[] ups)
    {
        var mods = new List<ushort>();
        if (shortcut.Ctrl)
        {
            mods.Add(VkControl);
        }

        if (shortcut.Shift)
        {
            mods.Add(VkShift);
        }

        if (shortcut.Alt)
        {
            mods.Add(VkMenu);
        }

        var downList = new List<NativeMethods.INPUT>(mods.Count + 1);
        foreach (ushort mod in mods)
        {
            downList.Add(KeyInput(mod, keyUp: false));
        }

        downList.Add(KeyInput((ushort)shortcut.VirtualKey, keyUp: false));

        var upList = new List<NativeMethods.INPUT>(mods.Count + 1)
        {
            KeyInput((ushort)shortcut.VirtualKey, keyUp: true)
        };
        for (int i = mods.Count - 1; i >= 0; i--)
        {
            upList.Add(KeyInput(mods[i], keyUp: true));
        }

        downs = downList.ToArray();
        ups = upList.ToArray();
    }

    private static NativeMethods.INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.INPUTUNION
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = UIntPtr.Zero
            }
        }
    };
}
