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

namespace VScreenPilot.Mcp;

/// <summary>
/// MCP 服务端：协议/分帧/生命周期交给官方 ModelContextProtocol SDK，
/// 本类只负责工具目录、参数校验、写操作调度、进度与追踪。
/// </summary>
internal sealed partial class McpTransport
{
    private readonly SessionManager _session;
    private readonly LogRing _log;
    private readonly ClientContext? _client;
    private readonly TraceLog? _trace;
    private readonly bool _ownsTrace;
    private readonly ToolRegistry _tools;
    private int _inflightHandlers;
    /// <summary>
    /// 组合根接线：把会话需要的租约/日志提供者指向本进程的唯一实例。
    /// 必须在 SessionManager 构造完成后调用（提供者是懒加载，但语义上属于宿主装配）。
    /// </summary>
    internal static void WireProviders(SessionManager session, LogRing log)
    {
        session.LeaseHolderProvider = () => session.Lease.HolderId;
        session.ScreenLeaseProvider = () => session.Lease.Describe();
        session.Lease.LogSink = log.Info;
        session.Lease.ClientCwdProvider = id => session.Clients.FindCwd(id);
    }

    private readonly WriteScheduler _scheduler;

    // 令牌桶限流：daemon 内所有连接共享，避免 N 个客户端把限制放大 N 倍。
    private readonly object _rateGate = new();
    private double _rateTokens = 300;
    private long _rateLastTicks = Environment.TickCount64;
    private const double RateCapacity = 300;
    private const double RateRefillPerSecond = 100;

    public McpTransport(SessionManager session, LogRing log)
        : this(session, log, trace: true, client: null, sharedTrace: null)
    {
    }

    internal McpTransport(SessionManager session, LogRing log, TraceLog sharedTrace)
        : this(session, log, trace: true, client: null, sharedTrace: sharedTrace)
    {
    }

    internal McpTransport(
        SessionManager session, LogRing log, ClientContext client, TraceLog sharedTrace)
        : this(session, log, trace: true, client: client, sharedTrace: sharedTrace)
    {
    }

    /// <summary>trace=false 供单测检查工具目录；此时不读取会话，也不写 trace 文件。</summary>
    internal McpTransport(SessionManager session, LogRing log, bool trace)
        : this(session, log, trace, client: null, sharedTrace: null)
    {
    }

    private McpTransport(
        SessionManager session, LogRing log, bool trace, ClientContext? client, TraceLog? sharedTrace)
    {
        _session = session;
        _log = log;
        // 单测只构造目录（session/log 传 null!），此时给一个独立调度器即可。
        _scheduler = session is null ? new WriteScheduler() : session.Scheduler;
        _client = client;
        _tools = BuildTools();
        _trace = sharedTrace ?? (trace ? new TraceLog() : null);
        _ownsTrace = sharedTrace is null && _trace is not null;

        if (_trace is null)
        {
            return;
        }

        _trace.Write("session_start", new JsonObject
        {
            ["version"] = BuildInfo.Version,
            ["pid"] = Environment.ProcessId,
            ["tools"] = _tools.Count,
            ["profile"] = _session.ProfileName,
            ["client"] = client?.Id,
        });
    }

    public string TracePath => _trace?.FilePath ?? string.Empty;

    public Task<int> RunAsync(CancellationToken cancellationToken = default) =>
        RunHostAsync(null, null, cancellationToken);

    public Task<int> RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default) =>
        RunHostAsync(input, output, cancellationToken);

    private async Task<int> RunHostAsync(
        Stream? input, Stream? output, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder();

        // 宿主默认日志会写 stderr；stdio 传输下保持安静，日志仍走 LogRing/文件。
        builder.Logging.ClearProviders();

        IMcpServerBuilder mcp = builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "vscreenpilot",
                    Version = BuildInfo.Version,
                };
                options.ServerInstructions = Instructions;
                options.Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = false },
                    Resources = new ResourcesCapability { ListChanged = false, Subscribe = false },
                };
            });

        if (input is null || output is null)
        {
            mcp.WithStdioServerTransport();
        }
        else
        {
            mcp.WithStreamServerTransport(input, output);
        }

        mcp.WithListToolsHandler(HandleListToolsAsync)
            .WithListResourcesHandler(HandleListResourcesAsync)
            .WithReadResourceHandler(HandleReadResourceAsync)
            .WithCallToolHandler(HandleCallToolAsync);

        ClientContext effectiveClient = _client ?? new ClientContext
        {
            Id = "c0",
            Name = "stdio",
            Cwd = Environment.CurrentDirectory,
        };

        try
        {
            _session.Clients.Enter(effectiveClient);
            await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            string clientId = effectiveClient.Id;

            // 连接结束：先释放租约（若自己的 handler 仍在跑或别人持有，返回 false）。
            // refs/artifacts 可以立即清；apps/窗口必须等租约和 handler 都空闲后再按 force 清理。
            bool leaseFree = _session.Lease.ReleaseHolderIfIdle(clientId);
            bool handlersIdle = Volatile.Read(ref _inflightHandlers) == 0;

            try
            {
                _session.DropClientRefs(clientId);

                if (leaseFree && handlersIdle &&
                    _session.ReleaseClient(_client, includeApps: true))
                {
                    // 已立即完成清理。
                }
                else
                {
                    ScheduleClientCleanup(_client, clientId);
                }
            }
            catch (Exception exception)
            {
                // 清理失败不能影响连接计数/trace 释放；稍后由 daemon 维护或重试兜底。
                _log.Warn($"客户端 {clientId} 清理失败: {exception.Message}");
                ScheduleClientCleanup(_client, clientId);
            }

            _session.Clients.Leave(clientId);
            _trace?.Write("session_end", new JsonObject { ["client_id"] = clientId });

            if (_ownsTrace)
            {
                _trace?.Dispose();
            }
        }
    }

    /// <summary>
    /// 客户端已断开但仍有在途 handler/租约时，等真正空闲后再按 force 清理窗口。
    /// daemon 退出时会做全量清理，这里是长期运行 daemon 的补充。
    /// </summary>
    private void ScheduleClientCleanup(ClientContext? client, string clientId)
    {
        _ = Task.Run(async () =>
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(30);

            while (DateTime.UtcNow < deadline)
            {
                if (Volatile.Read(ref _inflightHandlers) == 0 &&
                    _session.Lease.ReleaseHolderIfIdle(clientId))
                {
                    try
                    {
                        if (_session.ReleaseClient(client, includeApps: true))
                        {
                            return;
                        }
                    }
                    catch (Exception exception)
                    {
                        _log.Warn($"客户端 {clientId} 延后清理失败: {exception.Message}");
                    }
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            _log.Warn($"客户端 {clientId} 的延后清理超过 30 分钟，留待 daemon 退出处理");
        });
    }

    private const string Instructions =
        "Virtual-display automation for Windows apps. The server mounts a 1280x720 virtual display " +
        "(override with VSP_DISPLAY_WIDTH/HEIGHT/REFRESH; read when the daemon starts) and only operates " +
        "windows it has tracked. Start with `snapshot` (semantic tree + dialogs + optional screenshot); it " +
        "returns a snapshot_id and refs that are valid only for that snapshot. Prefer semantic actions " +
        "(click_ref/invoke_element/set_element_value) over pixel coordinates. Coordinates must stay inside " +
        "the virtual display. Chromium/WebView2 apps ignore window messages; for click/click_ref/click_text " +
        "pass `channel=input` or `fallback_input=true`, and for gesture pass `mode=input`. `auto` never " +
        "moves the real cursor unless fallback_input or VSP_INPUT_AUTOFALLBACK=1 enables it. Run " +
        "`release_environment` when finished; the display is kept by default. This is a shared desktop " +
        "session, not a security sandbox; use a VM for untrusted work. run_script is disabled unless the " +
        "server was started with --allow-script.";

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

    private ValueTask<ListResourcesResult> HandleListResourcesAsync(
        RequestContext<ListResourcesRequestParams> context, CancellationToken cancellationToken)
    {
        using IDisposable? clientScope = _client is null
            ? null
            : _session.Clients.Use(_client);
        var resources = new List<Resource>();
        int offset = 0;
        string? cursor = context.Params?.Cursor;

        if (!string.IsNullOrEmpty(cursor) && !int.TryParse(cursor, out offset))
        {
            offset = 0;
        }

        offset = Math.Max(0, offset);
        string? nextCursor = null;

        try
        {
            List<string> files = Directory
                .EnumerateFiles(_session.ArtifactDirectory, "*.png")
                // preview 是资源的附属文件，不单独列出。
                .Where(file => !file.EndsWith(".preview.png", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            foreach (string file in files.Skip(offset).Take(ResourcePageSize))
            {
                string name = Path.GetFileName(file);
                long size = new FileInfo(file).Length;
                resources.Add(new Resource
                {
                    Uri = ArtifactPrefix + name,
                    Name = name,
                    Description = $"Screenshot artifact ({size} bytes); append ?preview=1 for a smaller image",
                    MimeType = "image/png",
                    Size = size,
                });
            }

            if (offset + resources.Count < files.Count)
            {
                nextCursor = (offset + resources.Count).ToString();
            }
        }
        catch (Exception exception)
        {
            // artifact 目录不可用时返回空列表，不影响工具调用。
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
        }

        return ValueTask.FromResult(new ListResourcesResult
        {
            Resources = resources,
            NextCursor = nextCursor,
        });
    }

    private ValueTask<ReadResourceResult> HandleReadResourceAsync(
        RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        using IDisposable? clientScope = _client is null
            ? null
            : _session.Clients.Use(_client);
        string uri = context.Params?.Uri ?? string.Empty;

        if (!uri.StartsWith(ArtifactPrefix, StringComparison.Ordinal))
        {
            throw new McpException($"Unknown resource: {uri}");
        }

        string name = uri[ArtifactPrefix.Length..];
        bool preview = false;
        bool forceFull = false;
        int queryIndex = name.IndexOf('?');

        if (queryIndex >= 0)
        {
            string[] query = name[(queryIndex + 1)..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries);
            preview = query.Any(part => part.Equals("preview=1", StringComparison.OrdinalIgnoreCase));
            forceFull = query.Any(part => part.Equals("full=1", StringComparison.OrdinalIgnoreCase));
            name = name[..queryIndex];
        }

        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
        {
            throw new McpException($"Invalid artifact name: {name}");
        }

        string path = Path.Combine(_session.ArtifactDirectory, name);

        // 大图默认返回 preview：避免资源读取又把 300KB+ 原图塞回客户端。
        if (!preview && !forceFull && File.Exists(path))
        {
            preview = new FileInfo(path).Length > Limits.ResourcePreviewBytes;
        }

        if (preview)
        {
            string previewPath = Path.ChangeExtension(path, ".preview.png");

            if (!File.Exists(previewPath))
            {
                TryCreatePreview(path, previewPath);
            }

            if (File.Exists(previewPath))
            {
                path = previewPath;
            }
        }

        if (!File.Exists(path))
        {
            throw new McpException($"Resource not found: {uri}");
        }

        return ValueTask.FromResult(new ReadResourceResult
        {
            Contents =
            [
                new BlobResourceContents
                {
                    Uri = uri,
                    MimeType = "image/png",
                    Blob = File.ReadAllBytes(path),
                },
            ],
        });
    }

    /// <summary>旧 artifact 没有 preview 时按需生成 0.25× 小图，避免 resources/read 回传大 Blob。</summary>
    private static void TryCreatePreview(string sourcePath, string previewPath)
    {
        try
        {
            byte[] data = File.ReadAllBytes(sourcePath);
            PngImage image = PngReader.Decode(data);
            var bgra = new byte[image.Rgba.Length];

            for (int index = 0; index < image.Rgba.Length; index += 4)
            {
                bgra[index] = image.Rgba[index + 2];
                bgra[index + 1] = image.Rgba[index + 1];
                bgra[index + 2] = image.Rgba[index];
                bgra[index + 3] = image.Rgba[index + 3];
            }

            byte[] preview = PngWriter.Encode(
                bgra, image.Width, image.Height, Limits.PreviewScale, out _, out _);
            File.WriteAllBytes(previewPath, preview);
        }
        catch (Exception exception)
        {
            // 生成失败就退回原图；资源读取仍然可用。
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
        }
    }

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

        var call = new ToolContext(
            _session, _client, arguments, requestId, argumentSummary, cancellationToken);
        ToolResponse response = await executor
            .ExecuteAsync(tool, call)
            .ConfigureAwait(false);
        return ToResult(response);
    }

    private bool TryAcquireRateLimit(out int retryAfterMs)
    {
        retryAfterMs = 0;

        lock (_rateGate)
        {
            long now = Environment.TickCount64;
            double elapsed = Math.Max(0, now - _rateLastTicks) / 1000.0;
            _rateLastTicks = now;
            _rateTokens = Math.Min(RateCapacity, _rateTokens + elapsed * RateRefillPerSecond);

            if (_rateTokens >= 1)
            {
                _rateTokens -= 1;
                return true;
            }

            retryAfterMs = (int)Math.Ceiling((1 - _rateTokens) / RateRefillPerSecond * 1000);
            return false;
        }
    }

    /// <summary>参数摘要用于审计：文本/脚本等敏感字段只记录长度。</summary>
    private static string? SummarizeArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var copy = new JsonObject();

        foreach (JsonProperty property in arguments.EnumerateObject())
        {
            if (property.Name is "text" or "value" or "code" or "args" or "password"
                or "window" or "path" or "save_path" or "target_path")
            {
                string raw = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
                copy[property.Name] = $"<redacted:{raw.Length}>";
                continue;
            }

            copy[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        string json = copy.ToJsonString();
        return json.Length <= 300 ? json : json[..300] + "…";
    }

    private void ReportProgress(
        RequestContext<CallToolRequestParams> context, int current, int total, string message)
    {
        JsonNode? token = context.Params?.Meta?["progressToken"];

        if (token is null)
        {
            return;
        }

        try
        {
            _ = context.Server.SendNotificationAsync(
                "notifications/progress",
                new JsonObject
                {
                    ["progressToken"] = token.DeepClone(),
                    ["progress"] = current,
                    ["total"] = total,
                    ["message"] = message,
                });
        }
        catch (Exception exception)
        {
            // 进度是尽力而为。
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
        }
    }

    private void Trace(
        string tool, string requestId, string? arguments, double milliseconds, ToolResponse response)
    {
        string? code = null;
        string? stage = null;
        int textBytes = 0;
        int imageBytes = 0;

        foreach (JsonNode? item in response.Content)
        {
            if (item is not JsonObject block)
            {
                continue;
            }

            string? type = block["type"]?.GetValue<string>();

            if (type == "text")
            {
                string text = block["text"]?.GetValue<string>() ?? string.Empty;
                textBytes += System.Text.Encoding.UTF8.GetByteCount(text);

                if (code is null)
                {
                    try
                    {
                        JsonNode? parsed = JsonNode.Parse(text);
                        code = parsed?["code"]?.GetValue<string>();
                        stage = parsed?["stage"]?.GetValue<string>();
                    }
                    catch (Exception exception)
                    {
                        // 非 JSON 文本。
                        System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
                    }
                }
            }
            else if (type == "image")
            {
                string data = block["data"]?.GetValue<string>() ?? string.Empty;
                imageBytes += data.Length * 3 / 4;
            }
        }

        _session.Metrics.RecordTool(
            tool, milliseconds, code, response.IsError, textBytes, imageBytes);

        _trace?.ToolCall(tool, requestId, arguments, milliseconds, code, response.IsError,
            textBytes, imageBytes, channel: null, stage: stage, clientId: _client?.Id ?? "c0");
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
