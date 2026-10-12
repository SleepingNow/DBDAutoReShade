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
/// <para>
/// Never leaves a key stuck: whatever part of the down batch went out is
/// always released (even when the down batch was cut short), and a short
/// up batch is retried once for the keys that did not go out.
/// </para>
/// </summary>
public sealed class SendInputKeySimulator : IKeySimulator
{
    /// <summary>How long the combo is held (ms). Long enough for frame-polled input, short enough to be invisible.</summary>
    public const int DefaultHoldMilliseconds = 80;

    // Modifier VKs.
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkMenu = 0x12; // Alt

    private readonly Func<NativeMethods.INPUT[], uint> _inject;
    private readonly Action<int> _sleep;

    public SendInputKeySimulator()
        : this(SendInputNative, Thread.Sleep)
    {
    }

    /// <summary>Test seam: <paramref name="inject"/> stands in for SendInput and returns how many events were inserted.</summary>
    internal SendInputKeySimulator(Func<NativeMethods.INPUT[], uint> inject, Action<int>? sleep = null)
    {
        _inject = inject ?? throw new ArgumentNullException(nameof(inject));
        _sleep = sleep ?? Thread.Sleep;
    }

    public bool SendShortcut(ReshadeShortcutConfig shortcut) =>
        SendShortcut(shortcut, DefaultHoldMilliseconds);

    /// <summary>
    /// Presses and releases the combo. Returns false if any part failed (the
    /// down batch was not fully inserted, or the release could not be
    /// completed even after one retry).
    /// </summary>
    public bool SendShortcut(ReshadeShortcutConfig shortcut, int holdMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        BuildPress(shortcut, out NativeMethods.INPUT[] downs, out _);

        int pressed = Inject(downs);
        bool downOk = pressed == downs.Length;
        if (downOk && holdMilliseconds > 0)
        {
            _sleep(holdMilliseconds);
        }

        // SendInput inserts events in order and stops at the first failure, so
        // exactly the first `pressed` keys may be down: release those (all of
        // them on success), even when the down batch failed part-way.
        bool upOk = Release(BuildRelease(downs, pressed));
        return downOk && upOk;
    }

    /// <summary>Sends the release batch; on a short count retries the keys that did not go out, once.</summary>
    private bool Release(NativeMethods.INPUT[] ups)
    {
        if (ups.Length == 0)
        {
            return true;
        }

        int sent = Inject(ups);
        if (sent == ups.Length)
        {
            return true;
        }

        NativeMethods.INPUT[] rest = ups[sent..];
        return Inject(rest) == rest.Length;
    }

    private int Inject(NativeMethods.INPUT[] inputs)
    {
        uint sent;
        try
        {
            sent = _inject(inputs);
        }
        catch
        {
            sent = 0;
        }

        return (int)Math.Min(sent, (uint)inputs.Length);
    }

    private static uint SendInputNative(NativeMethods.INPUT[] inputs) =>
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());

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

        downs = downList.ToArray();
        ups = BuildRelease(downs, downs.Length);
    }

    /// <summary>
    /// Pure release builder: key-ups for the first <paramref name="pressedCount"/>
    /// entries of <paramref name="downs"/>, in reverse order (main key first,
    /// then modifiers). Empty when nothing was pressed.
    /// </summary>
    internal static NativeMethods.INPUT[] BuildRelease(IReadOnlyList<NativeMethods.INPUT> downs, int pressedCount)
    {
        int count = Math.Clamp(pressedCount, 0, downs.Count);
        var ups = new NativeMethods.INPUT[count];
        for (int i = 0; i < count; i++)
        {
            ups[i] = KeyInput(downs[count - 1 - i].u.ki.wVk, keyUp: true);
        }

        return ups;
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
