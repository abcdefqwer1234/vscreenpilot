using System.Text.Json;
using VScreenPilot.Errors;
using VScreenPilot.Services;
using VScreenPilot.Tools;
using static VScreenPilot.Json.JsonArgReader;

namespace VScreenPilot.Mcp;

/// <summary>Input 组：click / click_ref / gesture / click_text / type_text / press_keys。</summary>
internal sealed class InputTools
{
    private readonly IInputService _input;

    public InputTools(IInputService input) => _input = input;

    public ToolResponse Click(JsonElement arguments)
    {
        int x = RequiredCoordinate(arguments, "x");
        int y = RequiredCoordinate(arguments, "y");

        return ToolResponseWriter.FromView(_input.Click(new ClickRequest
        {
            X = x,
            Y = y,
            Button = OptionalString(arguments, "button") ?? "left",
            Clicks = OptionalInt(arguments, "clicks", 1),
            Mode = OptionalString(arguments, "channel")
                ?? OptionalString(arguments, "mode")
                ?? "auto",
            KeepForeground = OptionalBool(arguments, "keep_foreground", false),
            Observe = OptionalBool(arguments, "observe", true),
            FallbackInput = OptionalBool(arguments, "fallback_input", false),
        }));
    }

    public ToolResponse ClickRef(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "click_ref 需要 ref");

        return ToolResponseWriter.FromView(_input.ClickRef(
            reference,
            OptionalString(arguments, "channel"),
            OptionalBool(arguments, "keep_foreground", false),
            OptionalBool(arguments, "fallback_input", false)));
    }

    public ToolResponse Gesture(JsonElement arguments) =>
        ToolResponseWriter.FromView(_input.Gesture(arguments));

    public ToolResponse ClickText(JsonElement arguments)
    {
        string text = OptionalString(arguments, "text")
            ?? throw new ToolException("E_INVALID_ARG", "click_text 需要 text");

        int? x = null, y = null, width = null, height = null;

        if (arguments.TryGetProperty("region", out JsonElement region))
        {
            if (region.ValueKind != JsonValueKind.Object)
            {
                throw new ToolException("E_INVALID_ARG", "region 必须是对象");
            }

            x = OptionalNullableInt(region, "x");
            y = OptionalNullableInt(region, "y");
            width = OptionalNullableInt(region, "width");
            height = OptionalNullableInt(region, "height");

            if (x is null || y is null || width is null || height is null)
            {
                throw new ToolException(
                    "E_INVALID_ARG", "region 需要 x/y/width/height",
                    hint: "省略 region 表示整个虚拟屏", stage: "validate");
            }
        }

        return ToolResponseWriter.FromView(_input.ClickText(new ClickTextRequest
        {
            Text = text,
            KeepForeground = OptionalBool(arguments, "keep_foreground", false),
            Observe = OptionalBool(arguments, "observe", true),
            Channel = OptionalString(arguments, "channel") ?? "auto",
            RequireUnique = OptionalBool(arguments, "require_unique", false),
            RegionX = x,
            RegionY = y,
            RegionWidth = width,
            RegionHeight = height,
            FallbackInput = OptionalBool(arguments, "fallback_input", false),
        }));
    }

    public ToolResponse TypeText(JsonElement arguments)
    {
        string text = OptionalString(arguments, "text")
            ?? throw new ToolException("E_INVALID_ARG", "type_text 需要 text");

        return ToolResponseWriter.FromView(_input.TypeText(
            text,
            OptionalNullableInt(arguments, "pid"),
            OptionalBool(arguments, "verify", true),
            OptionalInt(arguments, "chunk_chars", 32),
            OptionalString(arguments, "channel") ?? "auto"));
    }

    public ToolResponse PressKeys(JsonElement arguments)
    {
        bool keepForeground = OptionalBool(arguments, "keep_foreground", false);
        bool allowSystemKeys = OptionalBool(arguments, "allow_system_keys", false);
        bool observe = OptionalBool(arguments, "observe", true);
        int? pid = OptionalNullableInt(arguments, "pid");

        if (arguments.TryGetProperty("sequence", out JsonElement sequence) &&
            sequence.ValueKind == JsonValueKind.Array)
        {
            var keys = new List<string>();

            foreach (JsonElement item in sequence.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new ToolException("E_INVALID_ARG", "sequence 必须是字符串数组");
                }

                keys.Add(item.GetString() ?? string.Empty);
            }

            return ToolResponseWriter.FromView(
                _input.PressKeySequence(keys, keepForeground, pid, allowSystemKeys, observe));
        }

        string keysText = OptionalString(arguments, "keys")
            ?? throw new ToolException("E_INVALID_ARG", "press_keys 需要 keys 或 sequence");

        return ToolResponseWriter.FromView(
            _input.PressKeys(keysText, keepForeground, pid, allowSystemKeys, observe));
    }
}
