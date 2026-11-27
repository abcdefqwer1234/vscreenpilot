using System.Text.Json;
using VScreenPilot.Errors;
using VScreenPilot.Services;
using VScreenPilot.Tools;
using static VScreenPilot.Json.JsonArgReader;

namespace VScreenPilot.Mcp;

/// <summary>Visual 组：wait_for / assert_region / match_visual。</summary>
internal sealed class VisualTools
{
    private readonly IVisualService _visual;
    private readonly IWaitService _wait;

    public VisualTools(IVisualService visual, IWaitService wait)
    {
        _visual = visual;
        _wait = wait;
    }

    public ToolResponse WaitFor(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("condition", out JsonElement condition) ||
            condition.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "wait_for 需要 condition 对象");
        }

        int timeoutMs = OptionalInt(arguments, "timeout_ms", 5000);
        int? intervalMs = OptionalNullableInt(arguments, "interval_ms");
        bool clip = OptionalBool(arguments, "clip", false);
        return ToolResponseWriter.FromView(_wait.WaitFor(condition, timeoutMs, intervalMs, clip));
    }

    public ToolResponse AssertRegion(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("region", out JsonElement region) ||
            region.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "assert_region 需要 region");
        }

        if (!arguments.TryGetProperty("expectation", out JsonElement expectation) ||
            expectation.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "assert_region 需要 expectation");
        }

        bool clip = OptionalBool(arguments, "clip", false);
        return ToolResponseWriter.FromView(_visual.AssertRegion(region, expectation, clip));
    }

    public ToolResponse MatchVisual(JsonElement arguments) =>
        ToolResponseWriter.FromView(_visual.MatchVisual(arguments));
}
