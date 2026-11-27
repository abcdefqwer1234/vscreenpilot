using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Services;
using VScreenPilot.Tools;

namespace VScreenPilot.Mcp;

/// <summary>
/// tools/list 与 tools/call 的调度：限速、参数校验、执行与结果映射。
/// 传输层只负责协议和连接生命周期。
/// </summary>
internal sealed class ToolCallPipeline
{
    private readonly ToolRegistry _tools;
    private readonly LogRing _log;
    private readonly WriteScheduler _scheduler;
    private readonly McpTelemetry _telemetry;
    private readonly IExecutionControl _control;
    private readonly ClientInfo? _client;
    private readonly bool _readOnly;
    private int _inflightHandlers;

    public ToolCallPipeline(
        ToolRegistry tools,
        LogRing log,
        WriteScheduler scheduler,
        McpTelemetry telemetry,
        IExecutionControl control,
        ClientInfo? client,
        bool readOnly = false)
    {
        _tools = tools;
        _log = log;
        _scheduler = scheduler;
        _telemetry = telemetry;
        _control = control;
        _client = client;
        _readOnly = readOnly;
    }

    public int InflightHandlers => Volatile.Read(ref _inflightHandlers);

    public ValueTask<ListToolsResult> HandleListToolsAsync(
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

    public async ValueTask<CallToolResult> HandleCallToolAsync(
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

            _telemetry.Trace(name, requestId, null, 0, unknown);
            return ToolResultMapper.ToResult(unknown);
        }

        if (_readOnly && !AllowsReadOnly(tool))
        {
            ToolResponse denied = ToolResponse.Error(
                "E_READ_ONLY",
                $"只读会话拒绝写工具: {name}",
                new JsonObject { ["tool"] = name },
                retryable: false,
                hint: "结束只读会话后以默认可写模式重启；观察与断言工具不受影响",
                stage: "policy").SetRequestId(requestId);

            _telemetry.Trace(name, requestId, null, 0, denied);
            return ToolResultMapper.ToResult(denied);
        }

        if (!_telemetry.TryAcquireRateLimit(out int retryAfterMs))
        {
            ToolResponse limited = ToolResponse.Error(
                "E_RATE_LIMITED",
                $"请求速率超过限制（{300:0} 突发 / {100:0}/s）",
                new JsonObject { ["retry_after_ms"] = retryAfterMs },
                retryable: true,
                hint: "退避后重试；批量截图/查询请合并或降低频率",
                stage: "rate").SetRequestId(requestId);

            _telemetry.Trace(name, requestId, null, 0, limited);
            return ToolResultMapper.ToResult(limited);
        }

        JsonElement arguments = ToArguments(context.Params?.Arguments);
        string? argumentSummary = McpTelemetry.SummarizeArguments(arguments);

        try
        {
            ArgumentValidator.Validate(tool.InputSchema, arguments);
        }
        catch (ToolException exception)
        {
            // 参数错误必须作为工具错误返回（isError=true），不能冒泡成 SDK 的内部错误码。
            ToolResponse invalid = ToolResponse.FromException(exception).SetRequestId(requestId);
            _telemetry.Trace(name, requestId, argumentSummary, 0, invalid);
            return ToolResultMapper.ToResult(invalid);
        }

        return await ExecuteToolAsync(
            tool, arguments, context, requestId, argumentSummary, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>只读会话仍允许观察：标注只读的工具，以及 snapshot/capture_screen/get_environment。</summary>
    private static bool AllowsReadOnly(ToolDescriptor tool) =>
        tool.ReadOnly ||
        tool.Name is "snapshot" or "capture_screen" or "get_environment";

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
            _telemetry.Trace,
            !ProgressPolicy.Enabled || context.Params?.Meta?["progressToken"] is null
                ? null
                : (current, total, message) => _telemetry.ReportProgress(context, current, total, message),
            () => Interlocked.Increment(ref _inflightHandlers),
            () => Interlocked.Decrement(ref _inflightHandlers));

        var call = new ToolContext(
            _control, _client, arguments, requestId, argumentSummary, cancellationToken);
        ToolResponse response = await executor
            .ExecuteAsync(tool, call)
            .ConfigureAwait(false);
        return ToolResultMapper.ToResult(response);
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
}
