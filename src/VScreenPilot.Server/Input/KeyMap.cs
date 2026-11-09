using VScreenPilot.Interop;

namespace VScreenPilot.Input;

internal static class KeyMap
{
    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Tab"] = 0x09,
        ["Esc"] = 0x1B,
        ["Escape"] = 0x1B,
        ["Space"] = 0x20,
        ["Backspace"] = 0x08,
        ["Back"] = 0x08,
        ["Delete"] = 0x2E,
        ["Del"] = 0x2E,
        ["Insert"] = 0x2D,
        ["Ins"] = 0x2D,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27,
        ["PrintScreen"] = 0x2C,
        ["Pause"] = 0x13,
        ["CapsLock"] = 0x14,
        ["NumLock"] = 0x90,
        ["ScrollLock"] = 0x91,
        ["Ctrl"] = 0x11,
        ["Control"] = 0x11,
        ["Alt"] = 0x12,
        ["Shift"] = 0x10,
        ["Win"] = 0x5B,
        ["Windows"] = 0x5B,
        ["Cmd"] = 0x5B,
        ["Menu"] = 0x5D,
        ["Application"] = 0x5D,
        ["Apps"] = 0x5D,
    };

    static KeyMap()
    {
        for (int i = 1; i <= 24; i++)
        {
            Named[$"F{i}"] = (ushort)(0x6F + i);
        }
    }

    public static bool TryResolve(string token, out ushort virtualKey, out bool needsShift, out bool extended)
    {
        virtualKey = 0;
        needsShift = false;
        extended = false;

        if (Named.TryGetValue(token, out virtualKey))
        {
            extended = IsExtended(virtualKey);
            return true;
        }

        if (token.Length == 1)
        {
            char ch = token[0];
            if (ch >= 'a' && ch <= 'z')
            {
                virtualKey = (ushort)char.ToUpperInvariant(ch);
                return true;
            }

            if (ch >= 'A' && ch <= 'Z')
            {
                virtualKey = ch;
                return true;
            }

            if (ch >= '0' && ch <= '9')
            {
                virtualKey = ch;
                return true;
            }

            short scan = NativeMethods.VkKeyScanW(ch);
            if (scan != -1)
            {
                byte vk = (byte)(scan & 0xFF);
                byte state = (byte)((scan >> 8) & 0xFF);
                virtualKey = vk;
                needsShift = (state & 1) != 0;
                extended = IsExtended(vk);
                return true;
            }
        }

        return false;
    }

    public static bool IsExtended(ushort virtualKey) =>
        virtualKey is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
            or 0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D;
}
