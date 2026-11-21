using VScreenPilot.Interop;
using VScreenPilot.Errors;

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

    public static bool TryResolve(
        string token, out ushort virtualKey, out bool needsShift, out bool extended) =>
        TryResolve(token, out virtualKey, out needsShift, out extended, out _);

    public static bool TryResolve(
        string token, out ushort virtualKey, out bool needsShift, out bool extended,
        out bool needsAltGr)
    {
        virtualKey = 0;
        needsShift = false;
        extended = false;
        needsAltGr = false;

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
                // VkKeyScanW 的 state 位：1=Shift、2=Ctrl、4=Alt。Ctrl+Alt 即 AltGr；
                // 当前按键模型只表达 Shift，遇到 AltGr 字符必须显式拒绝，否则会打错。
                needsAltGr = (state & 6) != 0;
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

/// <summary>
/// 系统级快捷键黑名单。这些组合会作用到用户桌面/工作站，而不是目标窗口，
/// 在虚拟屏自动化里属于“逃逸”路径，默认拒绝。
/// </summary>
internal static class SystemKeyGuard
{
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "alt+tab",
        "alt+esc",
        "alt+space",
        "ctrl+esc",
        "ctrl+shift+esc",
        "ctrl+alt+del",
        "ctrl+alt+delete",
        "ctrl+shift+delete",
        "win",
        "win+l",
        "win+r",
        "win+e",
        "win+d",
        "win+tab",
        "win+space",
        "win+x",
        "win+i",
        "win+s",
        "win+a",
        "win+p",
        "win+arrowleft",
        "win+arrowright",
        "win+arrowup",
        "win+arrowdown",
    };

    public static void ThrowIfBlocked(string keys, bool allowSystemKeys)
    {
        if (allowSystemKeys)
        {
            return;
        }

        string normalized = Normalize(keys);
        string[] tokens = normalized.Split('+');
        bool win = tokens.Contains("win", StringComparer.OrdinalIgnoreCase);
        bool alt = tokens.Contains("alt", StringComparer.OrdinalIgnoreCase);
        bool tab = tokens.Contains("tab", StringComparer.OrdinalIgnoreCase);
        bool escape = tokens.Contains("esc", StringComparer.OrdinalIgnoreCase);

        // 变体必须按语义拦截：Alt+Shift+Tab / Ctrl+Alt+Tab 都是窗口切换，
        // 只匹配精确字符串会漏掉它们（SEC-03）。
        if (Blocked.Contains(normalized) ||
            win ||
            (alt && tab) ||
            (alt && escape))
        {
            throw new VScreenPilot.Errors.ToolException(
                "E_SYSTEM_KEY_BLOCKED",
                $"按键组合会作用于用户桌面/系统，已拒绝: {keys}",
                new System.Text.Json.Nodes.JsonObject { ["keys"] = keys },
                retryable: false,
                hint: "如确需发送系统级组合，请显式传 allow_system_keys=true",
                stage: "input");
        }
    }

    private static string Normalize(string keys)
    {
        var tokens = keys
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Canonical)
            .ToList();

        return string.Join('+', tokens);
    }

    private static string Canonical(string token)
    {
        string value = token.Trim().ToLowerInvariant();

        return value switch
        {
            "control" => "ctrl",
            "windows" or "cmd" or "meta" => "win",
            "escape" => "esc",
            "delete" => "del",
            "left" => "arrowleft",
            "right" => "arrowright",
            "up" => "arrowup",
            "down" => "arrowdown",
            "return" => "enter",
            _ => value,
        };
    }
}
