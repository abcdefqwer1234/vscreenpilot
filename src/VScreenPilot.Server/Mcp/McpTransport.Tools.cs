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
using VScreenPilot.Services;
using VScreenPilot.Session;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

using VScreenPilot.Tools;

namespace VScreenPilot.Mcp;

internal sealed partial class McpTransport
{
    internal ToolRegistry BuildTools()
    {
        var handlers = new Dictionary<string, Func<JsonElement, ToolResponse>>(StringComparer.Ordinal)
        {
            ["get_environment"] = GetEnvironment,
            ["launch_or_place_app"] = LaunchOrPlace,
            ["snapshot"] = Snapshot,
            ["capture_screen"] = CaptureScreen,
            ["find_element"] = FindElement,
            ["get_ui_tree"] = GetUiTree,
            ["invoke_element"] = InvokeElement,
            ["set_element_value"] = SetElementValue,
            ["get_element_text"] = GetElementText,
            ["read_screen_text"] = ReadScreenText,
            ["click"] = Click,
            ["click_ref"] = ClickRef,
            ["gesture"] = Gesture,
            ["click_text"] = ClickText,
            ["type_text"] = TypeText,
            ["press_keys"] = PressKeys,
            ["wait_for"] = WaitFor,
            ["assert_region"] = AssertRegion,
            ["match_visual"] = MatchVisual,
            ["release_environment"] = ReleaseEnvironment,
            ["get_log"] = GetLog,
            ["run_script"] = RunScript,
        };

        return ToolCatalog.Build(handlers);
    }

    private ValueTask<ListToolsResult> HandleListToolsAsync(
        RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
    {
        var list = new List<Tool>(_tools.Count);

        foreach (ToolDescriptor definition in _tools.All.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            list.Add(new Tool
            {
                Name = definition.Name,
                Title = definition.Title,
                Description = definition.Description,
                InputSchema = JsonSerializer.SerializeToElement(definition.InputSchema),
                OutputSchema = JsonSerializer.SerializeToElement(definition.OutputSchema),
                Annotations = new ToolAnnotations
                {
                    ReadOnlyHint = definition.ReadOnly,
                    DestructiveHint = definition.Destructive,
                    OpenWorldHint = !definition.ReadOnly,
                    IdempotentHint = definition.ReadOnly,
                },
            });
        }

        return ValueTask.FromResult(new ListToolsResult { Tools = list });
    }

    private const string ArtifactPrefix = "vsp://artifacts/";

    private const int ResourcePageSize = 50;

    private async ValueTask<CallToolResult> HandleCallToolAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        string name = context.Params?.Name ?? string.Empty;
        string requestId = Guid.NewGuid().ToString("N")[..12];

        if (!_tools.TryGet(name, out ToolDescriptor? tool))
        {
            // SDK 会把 handler 异常转成无结构的 isError 文本；这里主动返回统一错误目录，
            // 客户端和模型都能拿到 code/hint/docs，而不是一句英文。
            ToolResponse unknown = ToolResponse.Error(
                "E_UNKNOWN_TOOL",
                $"Unknown tool: {name}",
                new JsonObject { ["tool"] = name },
                retryable: false,
                hint: "调用 tools/list 查看可用工具名；工具名区分大小写",
                stage: "validate").SetRequestId(requestId);

            Trace(name, requestId, null, 0, unknown);
            return ToResult(unknown);
        }

        if (!TryAcquireRateLimit(out int retryAfterMs))
        {
            ToolResponse limited = ToolResponse.Error(
                "E_RATE_LIMITED",
                $"请求速率超过限制（{RateCapacity:0} 突发 / {RateRefillPerSecond:0}/s）",
                new JsonObject { ["retry_after_ms"] = retryAfterMs },
                retryable: true,
                hint: "退避后重试；批量截图/查询请合并或降低频率",
                stage: "rate").SetRequestId(requestId);

            Trace(name, requestId, null, 0, limited);
            return ToResult(limited);
        }

        JsonElement arguments = ToArguments(context.Params?.Arguments);
        string? argumentSummary = SummarizeArguments(arguments);

        try
        {
            ArgumentValidator.Validate(tool.InputSchema, arguments);
        }
        catch (ToolException exception)
        {
            // 参数错误必须作为工具错误返回（isError=true），不能冒泡成 SDK 的内部错误码。
            ToolResponse invalid = ToolResponse.FromException(exception).SetRequestId(requestId);
            Trace(name, requestId, argumentSummary, 0, invalid);
            return ToResult(invalid);
        }

        return await ExecuteToolAsync(
            tool, arguments, context, requestId, argumentSummary, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CallToolResult> ExecuteToolAsync(
        ToolDescriptor tool,
        JsonElement arguments,
        RequestContext<CallToolRequestParams> context,
        string requestId,
        string? argumentSummary,
        CancellationToken cancellationToken)
    {
        var executor = new ToolExecutor(
            _log,
            _scheduler,
            Trace,
            context.Params?.Meta?["progressToken"] is null
                ? null
                : (current, total, message) => ReportProgress(context, current, total, message),
            () => Interlocked.Increment(ref _inflightHandlers),
            () => Interlocked.Decrement(ref _inflightHandlers));

        ClientInfo? client = _client is null
            ? null
            : new ClientInfo(_client.Id, _client.Cwd, _client.Roots,
                _client.Name, _client.IncludeAllApps);
        var call = new ToolContext(
            new SessionExecutionControl(_session), client, arguments, requestId,
            argumentSummary, cancellationToken);
        ToolResponse response = await executor
            .ExecuteAsync(tool, call)
            .ConfigureAwait(false);
        return ToResult(response);
    }

    private static JsonElement ToArguments(IDictionary<string, JsonElement>? arguments)
    {
        var obj = new JsonObject();

        if (arguments is not null)
        {
            foreach ((string key, JsonElement value) in arguments)
            {
                obj[key] = JsonNode.Parse(value.GetRawText());
            }
        }

        using JsonDocument document = JsonDocument.Parse(obj.ToJsonString());
        return document.RootElement.Clone();
    }

    internal static CallToolResult ToResult(ToolResponse response)
    {
        var content = new List<ContentBlock>();

        foreach (JsonNode? item in response.Content)
        {
            if (item is not JsonObject block)
            {
                continue;
            }

            string? type = block["type"]?.GetValue<string>();

            if (type == "text")
            {
                content.Add(new TextContentBlock
                {
                    Text = block["text"]?.GetValue<string>() ?? string.Empty,
                });
            }
            else if (type == "image")
            {
                string base64 = block["data"]?.GetValue<string>() ?? string.Empty;

                // ImageContentBlock.Data 的契约是“base64 编码后的 UTF-8 字节”，
                // 不是原始 PNG 字节。用 FromBytes 让 SDK 负责编码；直接把
                // Convert.FromBase64String 的结果塞进 Data 会让客户端解码失败（N1）。
                content.Add(ImageContentBlock.FromBytes(
                    Convert.FromBase64String(base64),
                    block["mimeType"]?.GetValue<string>() ?? "image/png"));
            }
            else if (type == "resource_link")
            {
                content.Add(new ResourceLinkBlock
                {
                    Uri = block["uri"]?.GetValue<string>() ?? string.Empty,
                    Name = block["name"]?.GetValue<string>() ?? "artifact",
                    MimeType = block["mimeType"]?.GetValue<string>(),
                    Size = block["size"]?.GetValue<long>(),
                });
            }
        }

        return new CallToolResult
        {
            Content = content,
            StructuredContent = response.StructuredContent is null
                ? null
                : JsonDocument.Parse(response.StructuredContent.ToJsonString()).RootElement.Clone(),
            IsError = response.IsError,
        };
    }


}
