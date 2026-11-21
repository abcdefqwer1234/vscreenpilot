using System.Runtime.InteropServices;
using VScreenPilot.Interop;
using VScreenPilot.Errors;

namespace VScreenPilot.Input;

internal static class InputInjector
{
    public static void Click(int x, int y, string button, int clicks)
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT original);

        try
        {
            if (clicks <= 1)
            {
                // 单次点击合并为一个 SendInput，避免额外的睡眠与调度开销。
                Send(new List<NativeMethods.INPUT>
                {
                    AbsoluteMoveInput(x, y),
                    ButtonInput(button, down: true),
                    ButtonInput(button, down: false),
                });
            }
            else
            {
                for (int i = 0; i < clicks; i++)
                {
                    Send(new List<NativeMethods.INPUT>
                    {
                        AbsoluteMoveInput(x, y),
                        ButtonInput(button, down: true),
                        ButtonInput(button, down: false),
                    });

                    if (i < clicks - 1)
                    {
                        // 多击需要间隔，让系统识别为双击/三击。
                        Thread.Sleep(50);
                    }
                }
            }
        }
        finally
        {
            NativeMethods.SetCursorPos(original.X, original.Y);
        }
    }

    public static void Move(int x, int y)
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT original);
        try
        {
            MoveAbsolute(x, y);
        }
        finally
        {
            NativeMethods.SetCursorPos(original.X, original.Y);
        }
    }

    public static void Drag(int fromX, int fromY, int toX, int toY, string button, int durationMs)
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT original);

        try
        {
            MoveAbsolute(fromX, fromY);
            SendButton(button, down: true);

            int steps = Math.Clamp(durationMs / 10, 1, 200);
            for (int i = 1; i <= steps; i++)
            {
                int x = fromX + (toX - fromX) * i / steps;
                int y = fromY + (toY - fromY) * i / steps;
                MoveAbsolute(x, y);
                Thread.Sleep(Math.Max(1, durationMs / steps));
            }

            SendButton(button, down: false);
        }
        finally
        {
            NativeMethods.SetCursorPos(original.X, original.Y);
        }
    }

    public static void Scroll(int? x, int? y, int dx, int dy)
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT original);

        try
        {
            if (x.HasValue && y.HasValue)
            {
                MoveAbsolute(x.Value, y.Value);
            }

            var inputs = new List<NativeMethods.INPUT>(2);

            if (dy != 0)
            {
                inputs.Add(MouseInput(0, 0, (uint)(-dy * 120), NativeMethods.MouseWheel));
            }

            if (dx != 0)
            {
                inputs.Add(MouseInput(0, 0, (uint)(dx * 120), NativeMethods.MouseHWheel));
            }

            Send(inputs);
        }
        finally
        {
            NativeMethods.SetCursorPos(original.X, original.Y);
        }
    }

    public static int TypeText(string text)
    {
        var inputs = new List<NativeMethods.INPUT>(text.Length * 2);
        int typed = 0;

        foreach (char ch in text)
        {
            if (ch == '\r')
            {
                continue;
            }

            if (ch == '\n')
            {
                AddKey(inputs, 0x0D, extended: false, up: false);
                AddKey(inputs, 0x0D, extended: false, up: true);
                typed++;
                continue;
            }

            if (ch == '\t')
            {
                AddKey(inputs, 0x09, extended: false, up: false);
                AddKey(inputs, 0x09, extended: false, up: true);
                typed++;
                continue;
            }

            inputs.Add(UnicodeInput(ch, up: false));
            inputs.Add(UnicodeInput(ch, up: true));
            typed++;

            if (inputs.Count >= 256)
            {
                Send(inputs);
                inputs.Clear();
            }
        }

        if (inputs.Count > 0)
        {
            Send(inputs);
        }

        return typed;
    }

    public static void PressKeys(string keys, bool allowSystemKeys = false)
    {
        SystemKeyGuard.ThrowIfBlocked(keys, allowSystemKeys);

        string[] rawTokens = SplitKeys(keys);
        if (rawTokens.Length == 0)
        {
            throw new ToolException("E_INVALID_ARG", "keys 不能为空");
        }

        var modifiers = new List<ushort>();
        var inputs = new List<NativeMethods.INPUT>(rawTokens.Length * 2 + 4);

        for (int i = 0; i < rawTokens.Length - 1; i++)
        {
            if (!KeyMap.TryResolve(rawTokens[i], out ushort modifier, out _, out _, out bool modifierAltGr))
            {
                throw new ToolException("E_INVALID_ARG", $"未知按键: {rawTokens[i]}");
            }

            if (modifierAltGr)
            {
                throw new ToolException(
                    "E_INVALID_ARG",
                    $"按键 {rawTokens[i]} 需要 AltGr/Ctrl+Alt，当前组合键模型无法安全表达",
                    retryable: false,
                    hint: "改用 type_text（Unicode 注入）输入该字符",
                    stage: "input");
            }

            modifiers.Add(modifier);
            AddKey(inputs, modifier, KeyMap.IsExtended(modifier), up: false);
        }

        string mainToken = rawTokens[^1];
        if (!KeyMap.TryResolve(
                mainToken, out ushort mainKey, out bool needsShift, out bool extended, out bool altGr))
        {
            throw new ToolException("E_INVALID_ARG", $"未知按键: {mainToken}");
        }

        if (altGr)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"按键 {mainToken} 需要 AltGr/Ctrl+Alt，当前组合键模型无法安全表达",
                retryable: false,
                hint: "改用 type_text（Unicode 注入）输入该字符",
                stage: "input");
        }

        if (needsShift)
        {
            modifiers.Add(0x10);
            AddKey(inputs, 0x10, extended: false, up: false);
        }

        AddKey(inputs, mainKey, extended, up: false);
        AddKey(inputs, mainKey, extended, up: true);

        for (int i = modifiers.Count - 1; i >= 0; i--)
        {
            AddKey(inputs, modifiers[i], KeyMap.IsExtended(modifiers[i]), up: true);
        }

        Send(inputs);
    }

    private static string[] SplitKeys(string keys)
    {
        string trimmed = keys.Trim();

        // 允许用 "Ctrl++" / "Shift++" 表达加号本身。
        if (trimmed.Length > 1 && trimmed.EndsWith('+'))
        {
            string prefix = trimmed[..^1];
            var tokens = prefix.Split(
                '+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            tokens.Add("+");
            return tokens.ToArray();
        }

        return trimmed.Split(
            '+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void SendButton(string button, bool down)
    {
        Send(new List<NativeMethods.INPUT> { ButtonInput(button, down) });
    }

    private static NativeMethods.INPUT ButtonInput(string button, bool down)
    {
        uint flag = button.ToLowerInvariant() switch
        {
            "left" => down ? NativeMethods.MouseLeftDown : NativeMethods.MouseLeftUp,
            "right" => down ? NativeMethods.MouseRightDown : NativeMethods.MouseRightUp,
            "middle" => down ? NativeMethods.MouseMiddleDown : NativeMethods.MouseMiddleUp,
            _ => throw new ToolException("E_INVALID_ARG", $"未知鼠标按键: {button}"),
        };

        return MouseInput(0, 0, 0, flag);
    }

    private static NativeMethods.INPUT AbsoluteMoveInput(int x, int y)
    {
        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        int vy = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
        int vw = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen));
        int vh = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));

        int nx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));

        return MouseInput(
            Math.Clamp(nx, 0, 65535),
            Math.Clamp(ny, 0, 65535),
            0,
            NativeMethods.MouseMove | NativeMethods.MouseAbsolute | NativeMethods.MouseVirtualDesk);
    }

    private static void MoveAbsolute(int x, int y)
    {
        Send(new List<NativeMethods.INPUT> { AbsoluteMoveInput(x, y) });
    }

    private static NativeMethods.INPUT MouseInput(int dx, int dy, uint data, uint flags) =>
        new()
        {
            type = NativeMethods.InputMouse,
            U = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = data,
                    dwFlags = flags,
                },
            },
        };

    private static NativeMethods.INPUT UnicodeInput(char ch, bool up) =>
        new()
        {
            type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = NativeMethods.KeyEventUnicode | (up ? NativeMethods.KeyEventKeyUp : 0),
                },
            },
        };

    private static void AddKey(List<NativeMethods.INPUT> inputs, ushort virtualKey, bool extended, bool up)
    {
        uint flags = up ? NativeMethods.KeyEventKeyUp : 0;
        if (extended)
        {
            flags |= NativeMethods.KeyEventExtendedKey;
        }

        inputs.Add(new NativeMethods.INPUT
        {
            type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = (ushort)NativeMethods.MapVirtualKeyW(virtualKey, NativeMethods.MapvkVkToVsc),
                    dwFlags = flags,
                },
            },
        });
    }

    private static void Send(List<NativeMethods.INPUT> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        NativeMethods.INPUT[] array = inputs.ToArray();
        uint sent = NativeMethods.SendInput(
            (uint)array.Length, array, Marshal.SizeOf<NativeMethods.INPUT>());

        if (sent != array.Length)
        {
            int error = Marshal.GetLastWin32Error();

            if (error == 5)
            {
                throw new ToolException(
                    "E_UIPI",
                    "SendInput 被 UIPI 拦截：目标进程可能以更高完整性级别运行",
                    new System.Text.Json.Nodes.JsonObject { ["win32_error"] = error },
                    retryable: false,
                    hint: "以相同权限运行服务端和目标应用，或改用 UIA 语义操作",
                    stage: "input");
            }

            throw new ToolException(
                "E_INPUT",
                $"SendInput 只注入了 {sent}/{array.Length} 个事件",
                new System.Text.Json.Nodes.JsonObject
                {
                    ["win32_error"] = error,
                });
        }
    }
}
