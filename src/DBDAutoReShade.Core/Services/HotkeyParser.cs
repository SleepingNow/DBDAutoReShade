using DBDAutoReShade.Core.Models;

namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Parses a configurable global hotkey (e.g. "F8", "Ctrl+Alt+F9") into a
/// virtual-key code plus RegisterHotKey modifiers. Pure and unit-testable.
/// </summary>
public static class HotkeyParser
{
    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    /// <summary>Try to resolve a key name (F1..F24, A..Z, 0..9) to a VK code.</summary>
    public static bool TryParseKey(string? keyName, out int virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return false;
        }

        string key = keyName.Trim().ToUpperInvariant();

        if (key.Length == 1)
        {
            char c = key[0];
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                virtualKey = c;
                return true;
            }

            return false;
        }

        if (key.StartsWith("F", StringComparison.Ordinal))
        {
            if (int.TryParse(key.Substring(1), out int f) && f >= 1 && f <= 24)
            {
                virtualKey = 0x6F + f; // VK_F1 = 0x70 ... VK_F24 = 0x87
                return true;
            }
        }

        return false;
    }

    public static int ToModifiers(DetectionHotkeyConfig hotkey, bool useWin = false)
    {
        int mods = 0;
        if (hotkey.Alt)
        {
            mods |= ModAlt;
        }

        if (hotkey.Ctrl)
        {
            mods |= ModControl;
        }

        if (hotkey.Shift)
        {
            mods |= ModShift;
        }

        if (useWin)
        {
            mods |= ModWin;
        }

        return mods;
    }

    public static bool TryParse(DetectionHotkeyConfig? hotkey, out int virtualKey, out int modifiers)
    {
        virtualKey = 0;
        modifiers = 0;
        if (hotkey is null)
        {
            return false;
        }

        if (!TryParseKey(hotkey.Key, out virtualKey))
        {
            return false;
        }

        modifiers = ToModifiers(hotkey);
        if (modifiers == 0)
        {
            // Bare keys (like F8) are the intended default; still valid.
        }

        return true;
    }
}
