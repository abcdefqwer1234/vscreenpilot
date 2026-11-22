using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Json;
using VScreenPilot.Perception;
using VScreenPilot.Session;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Errors;

using VScreenPilot.Tools;

namespace VScreenPilot.Mcp;

internal sealed partial class McpTransport
{
    private ToolResponse Click(JsonElement arguments)
    {
        int x = RequiredCoordinate(arguments, "x");
        int y = RequiredCoordinate(arguments, "y");

        string button = OptionalString(arguments, "button") ?? "left";
        int clicks = OptionalInt(arguments, "clicks", 1);
        string mode = OptionalString(arguments, "channel")
            ?? OptionalString(arguments, "mode")
            ?? "auto";
        bool keepForeground = OptionalBool(arguments, "keep_foreground", false);
        bool observe = OptionalBool(arguments, "observe", true);
        bool fallbackInput = OptionalBool(arguments, "fallback_input", false);

        return ToolResponseWriter.FromView(
            _session.Click(x, y, button, clicks, mode, keepForeground, observe, fallbackInput));
    }

    private ToolResponse ClickRef(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "click_ref 需要 ref");

        return ToolResponseWriter.FromView(_session.ClickElementRef(
            reference,
            OptionalString(arguments, "channel"),
            OptionalBool(arguments, "keep_foreground", false),
            OptionalBool(arguments, "fallback_input", false)));
    }

    private ToolResponse Gesture(JsonElement arguments) =>
        ToolResponseWriter.FromView(_session.Gesture(arguments));

    private ToolResponse ClickText(JsonElement arguments)
    {
        string text = OptionalString(arguments, "text")
            ?? throw new ToolException("E_INVALID_ARG", "click_text 需要 text");
        bool keepForeground = OptionalBool(arguments, "keep_foreground", false);
        bool observe = OptionalBool(arguments, "observe", true);
        bool requireUnique = OptionalBool(arguments, "require_unique", false);
        string channel = OptionalString(arguments, "channel") ?? "auto";
        bool fallbackInput = OptionalBool(arguments, "fallback_input", false);

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

        return ToolResponseWriter.FromView(_session.ClickText(
            text, keepForeground, observe, channel, requireUnique,
            x, y, width, height, fallbackInput));
    }

    private ToolResponse TypeText(JsonElement arguments)
    {
        string text = OptionalString(arguments, "text")
            ?? throw new ToolException("E_INVALID_ARG", "type_text 需要 text");
        int? pid = OptionalNullableInt(arguments, "pid");
        bool verify = OptionalBool(arguments, "verify", true);
        int chunkChars = OptionalInt(arguments, "chunk_chars", 32);
        string channel = OptionalString(arguments, "channel") ?? "auto";

        return ToolResponseWriter.FromView(_session.TypeText(text, pid, verify, chunkChars, channel));
    }

    private ToolResponse PressKeys(JsonElement arguments)
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
                _session.PressKeySequence(keys, keepForeground, pid, allowSystemKeys, observe));
        }

        string keysText = OptionalString(arguments, "keys")
            ?? throw new ToolException("E_INVALID_ARG", "press_keys 需要 keys 或 sequence");

        return ToolResponseWriter.FromView(
            _session.PressKeys(keysText, keepForeground, pid, allowSystemKeys, observe));
    }

    private ToolResponse ReleaseEnvironment(JsonElement arguments)
    {
        bool keepDisplay = OptionalBool(arguments, "keep_display", true);
        string onDirty = OptionalString(arguments, "on_dirty") ?? "abort";
        bool allowDataLoss = OptionalBool(arguments, "allow_data_loss", false);
        string clientId = _client?.Id ?? "c0";
        ReleaseReport report = _session.ReleaseEnvironment(keepDisplay, onDirty, allowDataLoss);
        JsonObject data = VspJson.ToNode(report).AsObject();
        data["client_id"] = clientId;

        if (data["display_kept"]?.GetValue<bool>() == true)
        {
            data["display_kept_reason"] = _session.Clients.ActiveCount > 1
                ? "other_clients"
                : onDirty == "abort" ? "on_dirty_abort" : "requested";
        }

        // 成功释放后立即把屏幕让给下一个客户端；finally 会再次调用（幂等）。
        // 用 ForceRelease：当前工具自己持有一个租约，IfIdle 会因 active_calls>0 拒绝。
        _session.Lease.ForceRelease(clientId);
        JsonObject lease = _session.Lease.Describe();
        bool leaseHeld = lease["held"]?.GetValue<bool>() == true;
        string? holder = lease["holder_client_id"]?.GetValue<string>();
        int activeCalls = lease["active_calls"]?.GetValue<int>() ?? 0;
        data["screen_lease_released"] =
            !leaseHeld ||
            (string.Equals(holder, clientId, StringComparison.Ordinal) && activeCalls <= 1);
        return ToolResponse.Text(data);
    }

    private ToolResponse RunScript(JsonElement arguments)
    {
        string code = OptionalString(arguments, "code")
            ?? throw new ToolException("E_INVALID_ARG", "run_script 需要 code");

        int timeoutMs = OptionalInt(arguments, "timeout_ms", 30000);
        return ToolResponseWriter.FromView(_session.RunScript(code, timeoutMs));
    }


}
