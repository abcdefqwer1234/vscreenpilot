using System.Text.Json;
using VScreenPilot.Errors;
using VScreenPilot.Services;
using VScreenPilot.Tools;
using static VScreenPilot.Json.JsonArgReader;

namespace VScreenPilot.Mcp;

/// <summary>System 组：read_screen_text / run_script。</summary>
internal sealed class SystemTools
{
    private readonly IScreenTextService _screenText;
    private readonly IScriptService _script;

    public SystemTools(IScreenTextService screenText, IScriptService script)
    {
        _screenText = screenText;
        _script = script;
    }

    public ToolResponse ReadScreenText(JsonElement arguments)
    {
        int? x = null, y = null, width = null, height = null;

        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("region", out JsonElement region))
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

        return ToolResponseWriter.FromView(_screenText.Read(new ScreenTextRequest
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Query = OptionalString(arguments, "query"),
            MaxLines = OptionalInt(arguments, "max_lines", 50),
        }));
    }

    public ToolResponse RunScript(JsonElement arguments)
    {
        string code = OptionalString(arguments, "code")
            ?? throw new ToolException("E_INVALID_ARG", "run_script 需要 code");

        int timeoutMs = OptionalInt(arguments, "timeout_ms", 30000);
        return ToolResponseWriter.FromView(_script.Run(code, timeoutMs));
    }
}
