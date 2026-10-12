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

    /// <summary>
    /// MOD_NOREPEAT: holding the key down fires one WM_HOTKEY, not a stream of
    /// auto-repeats. Not part of <see cref="ToModifiers"/>; the hotkey service
    /// ORs it in at registration.
    /// </summary>
    public const int ModNoRepeat = 0x4000;

    private const int VkF1 = 0x70;
    private const int VkF24 = 0x87;
    private const int VkNumPad0 = 0x60;

    /// <summary>Rejection reason for a letter/number hotkey without Ctrl, Alt or Shift.</summary>
    public const string ModifierRequiredError =
        "Letters and number keys need Ctrl, Alt or Shift (they would block typing and game controls).";

    /// <summary>
    /// Try to resolve a key name (F1..F24, A..Z, 0..9, NumPad0..NumPad9) to a VK code.
    /// "0".."9" are the top-row digits (VK 0x30..0x39); "NumPad0".."NumPad9"
    /// are the number-pad keys (VK_NUMPAD0..9, 0x60..0x69).
    /// </summary>
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

        if (key.Length == 7 && key.StartsWith("NUMPAD", StringComparison.Ordinal))
        {
            char d = key[6];
            if (d >= '0' && d <= '9')
            {
                virtualKey = VkNumPad0 + (d - '0');
                return true;
            }

            return false;
        }

        if (key.StartsWith("F", StringComparison.Ordinal))
        {
            if (int.TryParse(key.Substring(1), out int f) && f >= 1 && f <= 24)
            {
                virtualKey = VkF1 - 1 + f; // VK_F1 = 0x70 ... VK_F24 = 0x87
                return true;
            }
        }

        return false;
    }

    /// <summary>True for VK_F1..VK_F24, the only keys allowed without a modifier.</summary>
    public static bool IsFunctionKey(int virtualKey) => virtualKey >= VkF1 && virtualKey <= VkF24;

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

    public static bool TryParse(DetectionHotkeyConfig? hotkey, out int virtualKey, out int modifiers) =>
        TryParse(hotkey, out virtualKey, out modifiers, out _);

    /// <summary>
    /// Validates and resolves a hotkey. F1–F24 may be used bare (F8 is the
    /// default); letters, digits and NumPad digits need at least one of
    /// Ctrl/Alt/Shift, because RegisterHotKey swallows the key system-wide and
    /// a bare "W" would block typing and DBD movement. On failure
    /// <paramref name="error"/> is a user-facing reason and the outputs are 0.
    /// </summary>
    public static bool TryParse(DetectionHotkeyConfig? hotkey, out int virtualKey, out int modifiers, out string? error)
    {
        virtualKey = 0;
        modifiers = 0;
        error = null;
        if (hotkey is null)
        {
            error = "No hotkey configured.";
            return false;
        }

        if (!TryParseKey(hotkey.Key, out int vk))
        {
            error = $"Unsupported hotkey '{hotkey}'. Use F1–F24, or A–Z, 0–9, NumPad0–NumPad9 with Ctrl, Alt or Shift.";
            return false;
        }

        int mods = ToModifiers(hotkey);
        if (mods == 0 && !IsFunctionKey(vk))
        {
            error = ModifierRequiredError;
            return false;
        }

        virtualKey = vk;
        modifiers = mods;
        return true;
    }
}
