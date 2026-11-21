using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VScreenPilot.Infrastructure;
using VScreenPilot.Json;
using VScreenPilot.Perception;
using VScreenPilot.Session;
using VScreenPilot.Views;
using static VScreenPilot.Mcp.JsonArgReader;

namespace VScreenPilot.Mcp;

/// <summary>
/// MCP 服务端：协议/分帧/生命周期交给官方 ModelContextProtocol SDK，
/// 本类只负责工具目录、参数校验、写操作调度、进度与追踪。
/// </summary>
internal sealed class McpServer
{
    internal static readonly HashSet<string> MutatingTools = new(StringComparer.Ordinal)
    {
        "click", "click_ref", "click_text", "gesture", "type_text", "press_keys",
        "invoke_element", "set_element_value", "launch_or_place_app", "release_environment",
        "run_script",
    };

    private readonly SessionManager _session;
    private readonly LogRing _log;
    private readonly ClientContext? _client;
    private readonly TraceLog? _trace;
    private readonly bool _ownsTrace;
    private readonly Dictionary<string, ToolDefinition> _tools;
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
    private static readonly object RateGate = new();
    private static double _rateTokens = 300;
    private static long _rateLastTicks = Environment.TickCount64;
    private const double RateCapacity = 300;
    private const double RateRefillPerSecond = 100;

    internal sealed record ToolDefinition(
        string Name,
        string Title,
        string Description,
        JsonNode InputSchema,
        JsonNode OutputSchema,
        bool ReadOnly,
        bool Destructive,
        int TimeoutMs,
        Func<JsonElement, ToolResponse> Handler);

    public McpServer(SessionManager session, LogRing log)
        : this(session, log, trace: true, client: null, sharedTrace: null)
    {
    }

    internal McpServer(SessionManager session, LogRing log, TraceLog sharedTrace)
        : this(session, log, trace: true, client: null, sharedTrace: sharedTrace)
    {
    }

    internal McpServer(
        SessionManager session, LogRing log, ClientContext client, TraceLog sharedTrace)
        : this(session, log, trace: true, client: client, sharedTrace: sharedTrace)
    {
    }

    /// <summary>trace=false 供单测检查工具目录；此时不读取会话，也不写 trace 文件。</summary>
    internal McpServer(SessionManager session, LogRing log, bool trace)
        : this(session, log, trace, client: null, sharedTrace: null)
    {
    }

    private McpServer(
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

        foreach (ToolDefinition definition in _tools.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
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
        catch
        {
            // artifact 目录不可用时返回空列表，不影响工具调用。
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
        catch
        {
            // 生成失败就退回原图；资源读取仍然可用。
        }
    }

    private async ValueTask<CallToolResult> HandleCallToolAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        string name = context.Params?.Name ?? string.Empty;
        string requestId = Guid.NewGuid().ToString("N")[..12];

        if (!_tools.TryGetValue(name, out ToolDefinition? tool))
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
        ToolDefinition tool,
        JsonElement arguments,
        RequestContext<CallToolRequestParams> context,
        string requestId,
        string? argumentSummary,
        CancellationToken cancellationToken)
    {
        using IDisposable? clientScope = _client is null
            ? null
            : _session.Clients.Use(_client);
        long epoch = _session.Epoch;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        WriteScheduler.Lease? lease = null;
        CancellationTokenSource? callCts = null;
        bool callCtsTransferred = false;
        Task<ToolResponse>? handlerTask = null;
        bool mutating = MutatingTools.Contains(tool.Name);
        IDisposable? screenLease = null;
        bool releaseSucceeded = false;

        try
        {
            string clientId = _client?.Id ?? "c0";

            if (RequiresScreenLease(tool.Name, arguments))
            {
                screenLease = _session.Lease.Acquire(clientId, tool.Name);

                if (KeepsForeground(arguments))
                {
                    _session.Lease.Extend(clientId, 30_000);
                    _session.PauseFocusGuard(30_000);
                }
            }

            if (mutating)
            {
                // release 等 6s：正常写操作应在这之前结束；只有锁定超过 5s 的卡死 handler 才旁路。
                TimeSpan wait = tool.Name == "release_environment"
                    ? TimeSpan.FromSeconds(6)
                    : TimeSpan.FromSeconds(30);

                lease = await _scheduler.AcquireAsync(
                    tool.Name, wait, cancellationToken,
                    bypass: tool.Name == "release_environment").ConfigureAwait(false);
            }

            callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            callCts.CancelAfter(tool.TimeoutMs);
            _session.SetCancellation(callCts);

            // 长任务至少给一次“已开始/已完成”进度，客户端不会以为调用挂死。
            ReportProgress(context, 0, 1, "started");

            handlerTask = Task.Run(() =>
            {
                Action<int, int, string>? reporter = context.Params?.Meta?["progressToken"] is null
                    ? null
                    : (current, total, message) => ReportProgress(context, current, total, message);

                // release 与只读观察互斥：release 摘屏前先等 snapshot/capture 结束。
                // wait_for 可能持续 120s，不能持读锁阻塞 release；get_log 不碰显示，也跳过。
                // 注意：这里用“是否读会话”而不是 readOnlyHint 判断。snapshot/capture_screen
                // 会换代/写盘，标注上不是只读，但仍然必须受 release 的读锁保护。
                bool sessionRead = tool.Name is not ("wait_for" or "get_log" or "release_environment");
                bool sessionWrite = tool.Name == "release_environment";
                bool writeHeld = false;

                if (sessionRead)
                {
                    _session.EnterRead();
                }
                else if (sessionWrite)
                {
                    writeHeld = _session.TryEnterExclusive(3000);

                    if (!writeHeld)
                    {
                        // 不能“等不到锁也继续清理”：只读观察可能正在读 UIA/屏幕，
                        // 同时摘屏会让它们拿到半清理状态。宁可让调用方重试。
                        return ToolResponse.Error(
                            "E_BUSY",
                            "release_environment 等待只读观察结束超时（3s）",
                            new JsonObject
                            {
                                ["held_by"] = "snapshot/capture/find_element",
                                ["retry_after_ms"] = 500,
                            },
                            retryable: true,
                            hint: "稍后重试；也可以等当前 snapshot/capture 返回后再 release",
                            stage: "session");
                    }
                }

                using var progress = ToolProgress.Begin(reporter);

                try
                {
                    return tool.Handler(arguments);
                }
                catch (ToolException exception)
                {
                    return ToolResponse.FromException(exception);
                }
                catch (Exception exception)
                {
                    _log.Error($"工具 {tool.Name} 异常", exception);
                    return ToolResponse.FromException(exception, $"tool:{tool.Name}");
                }
                finally
                {
                    if (sessionRead)
                    {
                        _session.ExitRead();
                    }
                    else if (writeHeld)
                    {
                        _session.ExitExclusive();
                    }
                }
            }, CancellationToken.None);

            Interlocked.Increment(ref _inflightHandlers);
            _ = handlerTask.ContinueWith(
                _ => Interlocked.Decrement(ref _inflightHandlers),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            ToolResponse response;

            try
            {
                response = await handlerTask!
                    .WaitAsync(TimeSpan.FromMilliseconds(tool.TimeoutMs), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 写锁不释放：卡住的 handler 仍在操作 UI，不能让另一个写操作重叠。
                if (lease is not null)
                {
                    WriteScheduler.ReleaseWhen(handlerTask!, lease);
                    lease = null;
                }

                // 协作式取消：让还在循环里的 handler 尽快停下；CTS 交给 handler 释放，
                // 否则 using 会先把 token 源 dispose，Cancellation 属性就退化成 None（S5）。
                try
                {
                    callCts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 已经结束。
                }
                _ = handlerTask!.ContinueWith(
                    _ => callCts?.Dispose(), TaskScheduler.Default);
                callCtsTransferred = true;

                if (screenLease is not null)
                {
                    IDisposable pendingLease = screenLease;
                    screenLease = null;
                    _ = handlerTask!.ContinueWith(
                        _ => pendingLease.Dispose(), TaskScheduler.Default);
                }

                _log.Warn($"工具 {tool.Name} 超过 {tool.TimeoutMs}ms 未完成，已请求取消");
                response = ToolResponse.Error(
                    "E_TIMEOUT",
                    $"工具 {tool.Name} 超过 {tool.TimeoutMs}ms 未完成",
                    new JsonObject { ["tool"] = tool.Name, ["timeout_ms"] = tool.TimeoutMs },
                    retryable: true,
                    hint: "可重试；若持续超时，先保存再 release_environment{on_dirty:\"force\",allow_data_loss:true} 重置会话",
                    stage: "tool");
            }

            if (_session.Epoch != epoch && tool.Name != "release_environment")
            {
                response = ToolResponse.Error(
                    "E_CANCELLED",
                    "会话已被 release_environment 重置，本次结果已丢弃",
                    new JsonObject { ["epoch"] = epoch, ["current_epoch"] = _session.Epoch },
                    retryable: true,
                    hint: "重新调用 snapshot/launch_or_place_app",
                    stage: "session");
            }

            stopwatch.Stop();
            response.SetRequestId(requestId);

            if (tool.Name == "release_environment" && !response.IsError)
            {
                releaseSucceeded = true;
            }

            ReportProgress(context, 1, 1, "completed");
            Trace(tool.Name, requestId, argumentSummary, stopwatch.Elapsed.TotalMilliseconds, response);
            return ToResult(response);
        }
        catch (OperationCanceledException)
        {
            // 客户端取消：handler 可能仍在注入。写锁交给它，CTS 也交给它释放，
            // 避免 “已取消但仍在点击” 与新写操作并发（S5 的另一半）。
            if (lease is not null)
            {
                if (handlerTask is not null)
                {
                    WriteScheduler.ReleaseWhen(handlerTask, lease);
                }
                else
                {
                    lease.Dispose();
                }

                lease = null;
            }

            if (screenLease is not null)
            {
                IDisposable pendingLease = screenLease;
                screenLease = null;

                if (handlerTask is not null)
                {
                    _ = handlerTask.ContinueWith(
                        _ => pendingLease.Dispose(), TaskScheduler.Default);
                }
                else
                {
                    pendingLease.Dispose();
                }
            }

            try
            {
                callCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已经结束。
            }

            if (callCts is not null && handlerTask is not null && !handlerTask.IsCompleted)
            {
                _ = handlerTask.ContinueWith(_ => callCts?.Dispose(), TaskScheduler.Default);
                callCtsTransferred = true;
            }

            throw;
        }
        catch (ToolException exception)
        {
            var response = ToolResponse.FromException(exception).SetRequestId(requestId);
            Trace(tool.Name, requestId, argumentSummary, stopwatch.Elapsed.TotalMilliseconds, response);
            return ToResult(response);
        }
        catch (Exception exception)
        {
            _log.Error($"工具 {tool.Name} 失败", exception);
            var response = ToolResponse.FromException(exception, $"tool:{tool.Name}")
                .SetRequestId(requestId);
            Trace(tool.Name, requestId, argumentSummary, stopwatch.Elapsed.TotalMilliseconds, response);
            return ToResult(response);
        }
        finally
        {
            _session.SetCancellation(null);

            // 超时后 CTS 的所有权已经交给仍在运行的 handler。
            if (!callCtsTransferred)
            {
                callCts?.Dispose();
            }

            if (tool.Name == "release_environment")
            {
                string releaseClientId = _client?.Id ?? "c0";

                if (releaseSucceeded)
                {
                    // 只有真正清理成功才把屏幕交给下一个客户端；失败时仍保留租约。
                    // 这里不能用 ReleaseHolderIfIdle：本工具自己还持有一个租约（active_calls>=1）。
                    _session.Lease.ForceRelease(releaseClientId);
                }
                else if (handlerTask is { IsCompleted: false })
                {
                    // 超时/取消时 handler 可能仍在关闭窗口：等它真正结束再释放租约。
                    _ = handlerTask.ContinueWith(
                        _ => _session.Lease.ForceRelease(releaseClientId),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }

            // 释放顺序与获取相反：先写锁，后屏幕租约。
            lease?.Dispose();
            screenLease?.Dispose();
        }
    }

    private static bool KeepsForeground(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty("keep_foreground", out JsonElement value) &&
        value.ValueKind == JsonValueKind.True;

    private bool RequiresScreenLease(string tool, JsonElement arguments)
    {
        // 只清 refs/租约的 release 不需要租约；有 live apps 时要和 GUI 操作互斥。
        if (tool == "release_environment")
        {
            return _session.HasLiveOwnedApps();
        }

        if (tool == "get_environment")
        {
            // 只读元数据不需要租约；ensure=true 会挂载/修复显示，必须和 GUI 串行。
            return OptionalBool(arguments, "ensure", false);
        }

        if (tool is "get_log" or "find_element" or "get_ui_tree" or "get_element_text")
        {
            return false;
        }

        if (tool == "snapshot")
        {
            bool screenshot = arguments.TryGetProperty("include_screenshot", out JsonElement value) &&
                              value.ValueKind == JsonValueKind.True;
            bool annotate = arguments.TryGetProperty("annotate", out JsonElement annotateValue) &&
                            annotateValue.ValueKind == JsonValueKind.True;
            return screenshot || annotate;
        }

        if (tool == "wait_for")
        {
            return arguments.TryGetProperty("condition", out JsonElement condition) &&
                   condition.TryGetProperty("type", out JsonElement type) &&
                   type.ValueKind == JsonValueKind.String &&
                   type.GetString() is "color" or "pixel_diff" or "visual";
        }

        return true;
    }

    private static bool TryAcquireRateLimit(out int retryAfterMs)
    {
        retryAfterMs = 0;

        lock (RateGate)
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
        catch
        {
            // 进度是尽力而为。
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
                    catch
                    {
                        // 非 JSON 文本。
                    }
                }
            }
            else if (type == "image")
            {
                string data = block["data"]?.GetValue<string>() ?? string.Empty;
                imageBytes += data.Length * 3 / 4;
            }
        }

        MetricsRegistry.RecordTool(
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

    internal Dictionary<string, ToolDefinition> BuildTools()
    {
        var tools = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);

        Add(tools, "get_environment", "Environment",
            "Read virtual display, display topology, foreground window and session state. " +
            "foreground is only reported for tracked windows. ensure=true mounts/heals the virtual display first.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "ensure":{"type":"boolean","description":"Mount or heal the virtual display before reading state."}}}
            """,
            Out("virtual_display:object", "session:object", "foreground:object", "primary:object", "virtual_screen:object",
                "client_id:string", "screen_lease:object"),
            readOnly: false, destructive: false, timeoutMs: 20000, GetEnvironment);

        Add(tools, "launch_or_place_app", "Launch or attach app",
            "Start an application or attach to an existing window, move it onto the virtual display and maximize it. " +
            "Returns pid/hwnd plus any owned dialogs discovered at attach time. Prefer path for a new instance; " +
            "window only when you intentionally take over a running app. Single-instance apps may fall back to " +
            "adopting an existing window: check `reused` and `warnings`; an adopted window is only moved back on " +
            "release, never closed. Windows 11 Notepad can restore the previous session's tabs when launched " +
            "without args; pass `args` or confirm the title with snapshot before typing.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "path":{"type":"string","description":"Executable path."},
              "window":{"type":"string","description":"Attach by window title. Ignores new_instance."},
              "args":{"type":"string"},
              "wait_ms":{"type":"integer","minimum":1000,"maximum":60000},
              "new_instance":{"type":"boolean","description":"Default true: dedicated/new window."},
              "ensure_document":{"type":"boolean","description":"Default true. Word: Ctrl+N. PowerPoint: Ctrl+N then the blank-presentation tile. PowerPoint/Excel report document_ready_reason; apps without modeling return document_ready=null."},
              "pid":{"type":"integer","minimum":1,"description":"Attach by process id (most precise)."}}}
            """,
            Out("launched:boolean", "new_instance:boolean", "reused:boolean", "pid:integer", "hwnd:integer",
                "title:string", "maximized:boolean", "document_ready:boolean",
                "document_ready_reason:string", "dialogs:array", "warnings:array"),
            readOnly: false, destructive: false, timeoutMs: 90000, LaunchOrPlace);

        Add(tools, "snapshot", "Snapshot",
            "One-call observation: compact semantic tree with refs, tracked windows, owned dialogs, focus and " +
            "optional screenshot. Each call advances the ref generation: refs from an older snapshot return " +
            "E_STALE_SNAPSHOT. (find_element/get_ui_tree reuse the current generation.)",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "pid":{"type":"integer","minimum":1},
              "max_depth":{"type":"integer","minimum":1,"maximum":10,"description":"Default 4."},
              "interactive_only":{"type":"boolean","description":"Default true."},
              "max_nodes":{"type":"integer","minimum":1,"maximum":1024,"description":"Default 300; max 1024 (ref capacity)."},
              "include_screenshot":{"type":"boolean","description":"Default false; adds a scaled PNG."},
              "annotate":{"type":"boolean","description":"Default false; draw element boxes and numbered labels on the screenshot. Implies include_screenshot."},
              "scale":{"type":"number","minimum":0.1,"maximum":1,"description":"Screenshot scale, default 0.5."},
              "include_values":{"type":"boolean","description":"Include text/value of text controls. Default true."}}}
            """,
            Out("snapshot_id:string", "tree:object", "text:string", "nodes:integer",
                "truncated:boolean", "max_nodes_effective:integer", "ref_capacity:integer",
                "windows:array", "dialogs:array", "foreground:object", "screenshot:object",
                "screenshot_path:string", "screenshot_uri:string", "screenshot_preview:object", "annotations:array"),
            readOnly: false, destructive: false, timeoutMs: 45000, Snapshot);

        Add(tools, "capture_screen", "Capture screen",
            "Capture the virtual display or a region. Returns a PNG plus meta. If save_path is given it is the only " +
            "output file; otherwise large frames are written to the session artifact directory and returned as a " +
            "resource link. inline=true (default) embeds small images and a 0.25x preview for large ones. " +
            "save_path must be an absolute .png under the working directory or temp.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "region":{"type":"object","additionalProperties":false,"properties":{
                "x":{"type":"integer"},"y":{"type":"integer"},
                "width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1}},
                "required":["x","y","width","height"]},
              "scale":{"type":"number","minimum":0.1,"maximum":1,"description":"Default 0.5 for full screen, 1 for regions."},
              "inline":{"type":"boolean","description":"Embed the PNG in the MCP result. Default true for small images."},
              "save_path":{"type":"string","description":"Absolute .png path under cwd or temp."}}}
            """,
            Out("frame_id:string", "x:integer", "y:integer", "width:integer", "height:integer",
                "source_width:integer", "source_height:integer", "scale:number", "png_bytes:integer",
                "duration_ms:number", "encode_ms:number", "path:string",
                "artifact_path:string", "resource_uri:string", "artifact_uri:string",
                "inline:boolean", "preview:object", "preview_error:string",
                "backend:string", "slow_capture:boolean"),
            readOnly: false, destructive: false, timeoutMs: 30000, CaptureScreen);

        Add(tools, "find_element", "Find element",
            "Find UI Automation elements and return refs, bounds, state and snapshot_id. Falls back to MSAA when a " +
            "window exposes no UIA content (Office legacy dialogs). control_type must be a valid UIA control type. " +
            "role=document_body maps the document text surface.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "name":{"type":"string"},
              "name_match":{"type":"string","enum":["exact","contains","regex"]},
              "control_type":{"type":"string","description":"UIA control type, e.g. Button, Edit, Document."},
              "automation_id":{"type":"string"},
              "role":{"type":"string","enum":["document","document_body"]},
              "prefer":{"type":"string","enum":["auto","first","focused","largest","last"]},
              "index":{"type":"integer","minimum":0},
              "all":{"type":"boolean"},
              "max_results":{"type":"integer","minimum":1,"maximum":200},
              "include_offscreen":{"type":"boolean"},
              "pid":{"type":"integer","minimum":1}}}
            """,
            Out("ref:string", "count:integer", "total:integer", "truncated:boolean",
                "snapshot_id:string", "name:string", "control_type:string", "bounds:object",
                "enabled:boolean", "offscreen:boolean", "matches:array", "elements:array"),
            readOnly: true, destructive: false, timeoutMs: 30000, FindElement);

        Add(tools, "get_ui_tree", "UI tree",
            "Read the pruned semantic tree of the tracked window. Nodes carry refs. By default it reuses the current " +
            "snapshot generation, so existing refs stay valid; pass new_snapshot=true to invalidate old refs.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "max_depth":{"type":"integer","minimum":1,"maximum":10},
              "interactive_only":{"type":"boolean"},
              "max_nodes":{"type":"integer","minimum":1,"maximum":1024},
              "root":{"type":"string"},
              "pid":{"type":"integer","minimum":1},
              "format":{"type":"string","enum":["json","text"],"description":"text is the compact line format."},
              "new_snapshot":{"type":"boolean","description":"Advance the ref generation. Default false: keep existing refs valid."}}}
            """,
            Out("tree:object", "text:string", "nodes:integer", "truncated:boolean",
                "truncated_reason:string", "max_nodes_effective:integer",
                "max_nodes_clamped:boolean", "ref_capacity:integer", "snapshot_id:string",
                "new_snapshot:boolean"),
            readOnly: true, destructive: false, timeoutMs: 30000, GetUiTree);

        Add(tools, "invoke_element", "Invoke element",
            "Invoke a control by ref. auto tries Invoke/Toggle/SelectionItem/ExpandCollapse/Legacy; pattern=default " +
            "focuses and presses Space/Enter (useful for tree nodes).",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "ref":{"type":"string"},
              "pattern":{"type":"string","enum":["auto","invoke","toggle","select","selectionitem","expand","expandcollapse","legacy","default","defaultaction","open"]},
              "allow_click":{"type":"boolean","description":"Allow message click fallback."}},
              "required":["ref"]}
            """,
            Out("invoked:boolean", "pattern:string", "channel:string", "verified:boolean"),
            readOnly: false, destructive: true, timeoutMs: 30000, InvokeElement);

        Add(tools, "set_element_value", "Set value",
            "Write a value by ref. ValuePattern first; allow_keyboard=true falls back to typing. Overwriting non-empty " +
            "content requires allow_destructive=true.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "ref":{"type":"string"},
              "value":{"type":"string"},
              "allow_keyboard":{"type":"boolean"},
              "mode":{"type":"string","enum":["auto","append","insert","replace"]},
              "allow_destructive":{"type":"boolean"}},
              "required":["ref","value"]}
            """,
            Out("written:string", "value:string", "mode:string", "verified:boolean", "observed_length:integer"),
            readOnly: false, destructive: true, timeoutMs: 30000, SetElementValue);

        Add(tools, "get_element_text", "Get text",
            "Read text through TextPattern then ValuePattern. Use it to verify what you typed.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "ref":{"type":"string"},
              "max_chars":{"type":"integer","minimum":1,"maximum":65536}},
              "required":["ref"]}
            """,
            Out("text:string", "length:integer", "source:string", "truncated:boolean"),
            readOnly: true, destructive: false, timeoutMs: 20000, GetElementText);

        Add(tools, "click", "Click",
            "Click at virtual-desktop coordinates. channel=auto tries a UIA element, then a window message; " +
            "it only falls back to SendInput when fallback_input=true (or VSP_INPUT_AUTOFALLBACK=1). " +
            "Non-client areas (title bar, caption buttons) and popup menus are handled explicitly. " +
            "keep_foreground=true pauses the focus guard for 30s.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "x":{"type":"integer"},"y":{"type":"integer"},
              "button":{"type":"string","enum":["left","right","middle"]},
              "clicks":{"type":"integer","minimum":1,"maximum":10},
              "mode":{"type":"string","enum":["auto","uia","message","input"],"description":"Legacy alias of channel."},
              "channel":{"type":"string","enum":["auto","uia","message","input"]},
              "keep_foreground":{"type":"boolean"},
              "fallback_input":{"type":"boolean","description":"Default false. When UIA/message paths fail, move the real cursor and use SendInput."},
              "observe":{"type":"boolean","description":"Default true: capture a small before/after diff and return observed.changed."}},
              "required":["x","y"]}
            """,
            Out("x:integer", "y:integer", "button:string", "clicks:integer", "mode:string",
                "method:string", "inject_ms:number", "duration_ms:number", "verified:boolean",
                "keep_foreground:boolean", "detail:string", "hint:string", "observed:object"),
            readOnly: false, destructive: true, timeoutMs: 25000, Click);

        Add(tools, "click_ref", "Click element by ref",
            "Click a control by element_ref. auto first tries UIA Invoke/Toggle/Select, then message click " +
            "at the control center; channel=input moves the real cursor. fallback_input=true (or " +
            "VSP_INPUT_AUTOFALLBACK=1) enables SendInput after UIA/message fail. Prefer this over coordinate clicks.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "ref":{"type":"string"},
              "channel":{"type":"string","enum":["auto","uia","message","input"]},
              "keep_foreground":{"type":"boolean"},
              "fallback_input":{"type":"boolean","description":"Default false. When UIA/message paths fail, move the real cursor and use SendInput."}},
              "required":["ref"]}
            """,
            Out("clicked:boolean", "ref:string", "channel:string", "pattern:string",
                "refreshed:boolean", "bounds:object", "observed:object"),
            readOnly: false, destructive: true, timeoutMs: 20000, ClickRef);

        Add(tools, "read_screen_text", "Read screen text (OCR)",
            "OCR the virtual display or a region with the built-in Windows OCR engine. Use it for canvas apps " +
            "(PowerPoint, charts, games) where UIA exposes no text. Optional query filters lines by substring.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "region":{"type":"object","additionalProperties":false,"properties":{
                "x":{"type":"integer"},"y":{"type":"integer"},
                "width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1}},
                "required":["x","y","width","height"]},
              "query":{"type":"string"},
              "max_lines":{"type":"integer","minimum":1,"maximum":200}}}
            """,
            Out("engine:string", "language:string", "lines:array", "matches:array", "count:integer",
                "returned:integer", "truncated:boolean", "region:object", "duration_ms:number"),
            readOnly: true, destructive: false, timeoutMs: 60000, ReadScreenText);

        Add(tools, "click_text", "Click text on screen",
            "OCR the virtual display (or a region), find the first line containing text, click its center and " +
            "return the before/after diff. Fallback for canvas apps where click_ref cannot find the element. " +
            "fallback_input=true (or VSP_INPUT_AUTOFALLBACK=1) enables SendInput after UIA/message fail.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "text":{"type":"string"},
              "region":{"type":"object","additionalProperties":false,"properties":{
                "x":{"type":"integer"},"y":{"type":"integer"},
                "width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1}},
                "required":["x","y","width","height"]},
              "channel":{"type":"string","enum":["auto","uia","message","input"]},
              "keep_foreground":{"type":"boolean"},
              "fallback_input":{"type":"boolean","description":"Default false. When UIA/message paths fail, move the real cursor and use SendInput."},
              "require_unique":{"type":"boolean","description":"Fail with E_MULTIPLE_MATCHES when more than one line matches."},
              "observe":{"type":"boolean"}},
              "required":["text"]}
            """,
            Out("found:boolean", "text:string", "x:integer", "y:integer", "bounds:object",
                "ambiguous:boolean", "matches:array", "method:string", "observed:object"),
            readOnly: false, destructive: true, timeoutMs: 60000, ClickText);

        Add(tools, "gesture", "Gesture",
            "Composite mouse action: drag, scroll or move. Message mode cannot drag across windows; use " +
            "mode=input for drags (this tool uses `mode`, not `channel`).",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "type":{"type":"string","enum":["drag","scroll","move"]},
              "from":{"type":"object","additionalProperties":false,"properties":{"x":{"type":"integer"},"y":{"type":"integer"}},"required":["x","y"]},
              "to":{"type":"object","additionalProperties":false,"properties":{"x":{"type":"integer"},"y":{"type":"integer"}},"required":["x","y"]},
              "dx":{"type":"integer"},"dy":{"type":"integer"},
              "button":{"type":"string","enum":["left","right","middle"]},
              "duration_ms":{"type":"integer","minimum":20,"maximum":5000},
              "observe":{"type":"boolean","description":"Default true: capture a before/after diff and return observed.changed."},
              "mode":{"type":"string","enum":["auto","message","input"]}},
              "required":["type"]}
            """,
            Out("type:string", "from:object", "to:object", "dx:integer", "dy:integer",
                "button:string", "method:string", "detail:string", "verified:boolean",
                "inject_ms:number", "duration_ms:number", "observed:object", "hint:string"),
            readOnly: false, destructive: true, timeoutMs: 35000, Gesture);

        Add(tools, "type_text", "Type text",
            "Type text into the focused control. auto uses a pattern write, clipboard paste for long text, or " +
            "SendInput as fallback; verify=true reads back through TextPattern/ValuePattern.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "text":{"type":"string"},
              "pid":{"type":"integer","minimum":1},
              "verify":{"type":"boolean"},
              "chunk_chars":{"type":"integer","minimum":1,"maximum":256,"description":"Chunk size in characters; default 32."},
              "channel":{"type":"string","enum":["auto","pattern","clipboard","keys"]}},
              "required":["text"]}
            """,
            Out("typed:integer", "verified:boolean", "channel:string", "inject_ms:number", "duration_ms:number",
                "observed_length:integer", "chunks:integer"),
            readOnly: false, destructive: true, timeoutMs: 120000, TypeText);

        Add(tools, "press_keys", "Press keys",
            "Send a key combination or a sequence inside one foreground lease. System shortcuts (Win+*, Alt+Tab, " +
            "Ctrl+Esc and variants) are rejected unless allow_system_keys=true.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "keys":{"type":"string"},
              "sequence":{"type":"array","items":{"type":"string"}},
              "keep_foreground":{"type":"boolean"},
              "allow_system_keys":{"type":"boolean"},
              "observe":{"type":"boolean","description":"Default true: capture a before/after diff and return observed.changed."},
              "pid":{"type":"integer","minimum":1}}}
            """,
            Out("keys:string", "count:integer", "target_pid:integer", "inject_ms:number",
                "duration_ms:number", "keep_foreground:boolean", "verified:boolean", "observed:object"),
            readOnly: false, destructive: true, timeoutMs: 35000, PressKeys);

        Add(tools, "wait_for", "Wait for condition",
            "Poll a condition on the server inside one call. type=element|color|window|pixel_diff|visual|text. " +
            "Read-only: it never holds the write lock, so other observations keep working.",
            """
            {"type":"object","required":["condition"],"additionalProperties":false,"properties":{
              "timeout_ms":{"type":"integer","minimum":0,"maximum":120000},
              "interval_ms":{"type":"integer","minimum":50,"maximum":2000},
              "condition":{"oneOf":[
                {"type":"object","required":["type"],"properties":{"type":{"const":"element"},"name":{"type":"string"},"control_type":{"type":"string"},"automation_id":{"type":"string"},"pid":{"type":"integer"}}},
                {"type":"object","required":["type","x","y","value"],"properties":{"type":{"const":"color"},"x":{"type":"integer"},"y":{"type":"integer"},"value":{"type":"string"},"tolerance":{"type":"integer","minimum":0,"maximum":255}}},
                {"type":"object","required":["type"],"properties":{"type":{"const":"window"},"title":{"type":"string"},"scope":{"type":"string","enum":["tracked","any"]}}},
                {"type":"object","required":["type","region"],"properties":{"type":{"const":"pixel_diff"},"region":{"type":"object"},"baseline_frame_id":{"type":"string"},"min_changed_ratio":{"type":"number","minimum":0,"maximum":1}}},
                {"type":"object","required":["type"],"properties":{"type":{"const":"visual"},"mode":{"type":"string"},"target":{},"region":{"type":"object"},"threshold":{"type":"number"}}},
                {"type":"object","required":["type","text"],"properties":{"type":{"const":"text"},"text":{"type":"string"},"region":{"type":"object"}}}
              ]}}}
            """,
            Out("satisfied:boolean", "elapsed_ms:integer", "last:object"),
            readOnly: true, destructive: false, timeoutMs: 130000, WaitFor);

        Add(tools, "assert_region", "Assert region",
            "Assert color coverage or change against a baseline. An explicit baseline_frame_id must match x/y/width/height " +
            "exactly; mismatch returns E_NO_BASELINE instead of comparing different areas.",
            """
            {"type":"object","required":["region","expectation"],"additionalProperties":false,"properties":{
              "region":{"type":"object","additionalProperties":false,"properties":{"x":{"type":"integer"},"y":{"type":"integer"},"width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1}},"required":["x","y","width","height"]},
              "expectation":{"oneOf":[
                {"type":"object","required":["color"],"properties":{"color":{"type":"string"},"coverage":{"type":"number","minimum":0,"maximum":1},"tolerance":{"type":"integer","minimum":0,"maximum":255}}},
                {"type":"object","required":["changed"],"properties":{"changed":{"type":"boolean"},"threshold":{"type":"number","minimum":0,"maximum":1},"baseline_frame_id":{"type":"string"},"baseline":{"type":"string","enum":["last_same_region","last"]}}}
              ]}}}
            """,
            Out("passed:boolean", "changed_ratio:number", "coverage:number", "baseline_frame_id:string"),
            readOnly: true, destructive: false, timeoutMs: 30000, AssertRegion);

        Add(tools, "match_visual", "Match visual",
            "Locate pixels on the virtual display: color connected components, single pixel, or PNG template match. " +
            "Template matching is done at capture pixel scale; if the template comes from a different render " +
            "scale/DPI, pass scales (0.5-2.0, default [1], max 5) to search.",
            """
            {"type":"object","required":["mode"],"additionalProperties":false,"properties":{
              "mode":{"type":"string","enum":["template","color","pixel"]},
              "target":{"description":"color: #RRGGBB; pixel: {x,y}; template: base64 PNG"},
              "target_path":{"type":"string"},
              "region":{"type":"object","additionalProperties":false,"properties":{"x":{"type":"integer"},"y":{"type":"integer"},"width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1}},"required":["x","y","width","height"]},
              "tolerance":{"type":"integer","minimum":0,"maximum":255},
              "threshold":{"type":"number","minimum":0.3,"maximum":1},
              "scales":{"type":"array","items":{"type":"number","minimum":0.5,"maximum":2},"maxItems":5,
                "description":"Template scales to try, e.g. [0.75,1,1.25]; default [1]."},
              "max_results":{"type":"integer","minimum":1,"maximum":100},
              "min_pixels":{"type":"integer","minimum":1},
              "max_area_ratio":{"type":"number","minimum":0.01,"maximum":1},
              "exclude_border":{"type":"boolean"},
              "require_unique":{"type":"boolean"}}}
            """,
            Out("found:boolean", "count:integer", "x:integer", "y:integer", "width:integer", "height:integer",
                "color:string", "matches:array", "threshold:number", "template:object", "scale:number"),
            readOnly: true, destructive: false, timeoutMs: 60000, MatchVisual);

        Add(tools, "release_environment", "Release environment",
            "End the debug session: restore window placement, close only processes this session started, optionally " +
            "remove the virtual display. on_dirty controls unsaved content. Default is abort: unsaved windows are kept " +
            "and the display stays mounted. force/discard require allow_data_loss=true.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "keep_display":{"type":"boolean","description":"Default true."},
              "on_dirty":{"type":"string","enum":["force","discard","save","abort"],"description":"Default abort."},
              "allow_data_loss":{"type":"boolean","description":"Required for on_dirty=force/discard when a tracked app may have unsaved content."}}}
            """,
            Out("released:boolean", "display_removed:boolean", "display_kept:boolean",
                "on_dirty:string", "restored_pids:array", "closed_pids:array",
                "forced_pids:array", "kept_pids:array", "saved_pids:array",
                "discarded_pids:array", "memory:object", "warnings:array"),
            readOnly: false, destructive: true, timeoutMs: 90000, ReleaseEnvironment);

        Add(tools, "get_log", "Get log",
            "Read recent in-memory or file log entries, filtered by level/contains. Use it after E_INTERNAL or E_TIMEOUT.",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "tail":{"type":"integer","minimum":1,"maximum":500},
              "source":{"type":"string","enum":["memory","file","both"]},
              "level":{"type":"string","enum":["DEBUG","INFO","WARN","ERROR"]},
              "contains":{"type":"string"},
              "include_other_clients":{"type":"boolean","description":"Include entries owned by other MCP clients. Default false."}}}
            """,
            Out("source:string", "entries:array", "file_entries:array", "last_error:object"),
            readOnly: true, destructive: false, timeoutMs: 15000, GetLog);

        Add(tools, "run_script", "Run script",
            "Run a temporary PowerShell script with the current user token. Disabled unless the server was started " +
            "with --allow-script. Output is UTF-8 and truncated to 64KB.",
            """
            {"type":"object","required":["code"],"additionalProperties":false,"properties":{
              "code":{"type":"string"},
              "timeout_ms":{"type":"integer","minimum":1000,"maximum":120000}}}
            """,
            Out("stdout:string", "stderr:string", "exit_code:integer", "duration_ms:number",
                "timed_out:boolean", "truncated:boolean"),
            readOnly: false, destructive: true, timeoutMs: 130000, RunScript);

        return tools;
    }

    private static void Add(
        Dictionary<string, ToolDefinition> tools,
        string name,
        string title,
        string description,
        string schemaJson,
        JsonNode outputSchema,
        bool readOnly,
        bool destructive,
        int timeoutMs,
        Func<JsonElement, ToolResponse> handler)
    {
        tools[name] = new ToolDefinition(
            name, title, description,
            JsonNode.Parse(schemaJson)!, outputSchema,
            readOnly, destructive, timeoutMs, handler);
    }

    /// <summary>生成“成功或错误都能通过”的输出 schema：声明字段但不强制 required。</summary>
    private static JsonNode Out(params string[] fields)
    {
        var properties = new JsonObject
        {
            ["code"] = Type("string"),
            ["message"] = Type("string"),
        };

        foreach (string field in fields)
        {
            int separator = field.IndexOf(':');

            if (separator <= 0)
            {
                continue;
            }

            properties[field[..separator]] = Type(field[(separator + 1)..]);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = true,
        };
    }

    private static JsonObject Type(string type) => type switch
    {
        // 输出字段允许 null（例如 last_error、path），schema 仍声明真实类型。
        "object" => new JsonObject
        {
            ["type"] = new JsonArray("object", "null"),
            ["additionalProperties"] = true,
        },
        "array" => new JsonObject
        {
            ["type"] = new JsonArray("array", "null"),
            ["items"] = new JsonObject(),
        },
        _ => new JsonObject { ["type"] = new JsonArray(type, "null") },
    };

    private ToolResponse GetEnvironment(JsonElement arguments)
    {
        EnvironmentView environment = _session.GetEnvironment(OptionalBool(arguments, "ensure", false));
        JsonObject data = VspJson.ToNode(environment).AsObject();
        data["client_id"] = _client?.Id ?? "c0";
        data["screen_lease"] = _session.Lease.Describe();
        return ToolResponse.Text(data);
    }

    private ToolResponse LaunchOrPlace(JsonElement arguments)
    {
        string? path = OptionalString(arguments, "path");
        string? window = OptionalString(arguments, "window");
        string? args = OptionalString(arguments, "args");
        int waitMs = OptionalInt(arguments, "wait_ms", 15000);
        bool newInstance = OptionalBool(arguments, "new_instance", true);
        int? pid = OptionalNullableInt(arguments, "pid");

        return ToolResponse.Text(
            _session.LaunchOrPlaceApp(
                path, window, args, waitMs, newInstance,
                OptionalBool(arguments, "ensure_document", true), pid));
    }

    private ToolResponse Snapshot(JsonElement arguments)
    {
        double scale = OptionalDouble(arguments, "scale", 0.5);

        if (arguments.TryGetProperty("scale", out _) && (scale < 0.1 || scale > 1.0))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"scale 必须在 0.1..1.0（收到 {scale}）",
                new JsonObject { ["field"] = "scale", ["value"] = scale },
                retryable: false,
                hint: "快照截图只接受 0.1–1.0",
                stage: "validate");
        }

        bool annotate = OptionalBool(arguments, "annotate", false);

        SnapshotResult result = _session.Snapshot(
            OptionalNullableInt(arguments, "pid"),
            OptionalInt(arguments, "max_depth", 4),
            OptionalBool(arguments, "interactive_only", true),
            OptionalInt(arguments, "max_nodes", 300),
            OptionalBool(arguments, "include_values", true),
            OptionalBool(arguments, "include_screenshot", false) || annotate,
            scale,
            annotate);

        if (result.Png is null)
        {
            return ToolResponse.Text(result.Data);
        }

        if (result.Png.Length <= Limits.InlineLimitBytes)
        {
            return ToolResponse.Text(result.Data).AddImage(result.Png);
        }

        // 大图先落盘、把路径写进 data，再序列化；否则 Text() 之后追加的字段不会进入响应。
        string artifactUri;
        string artifactName;

        try
        {
            (string path, string uri) = WriteArtifact(result.Png, "snapshot", result.PreviewPng);
            artifactUri = uri;
            artifactName = Path.GetFileName(path);
            result.Data["screenshot_path"] = path;
            result.Data["screenshot_uri"] = uri;
            result.Data["screenshot_resource_uri"] = uri;
        }
        catch (Exception exception)
        {
            result.Data["screenshot_error"] = exception.Message;
            return ToolResponse.Text(result.Data);
        }

        bool hasPreview = result.PreviewPng is not null;

        if (hasPreview)
        {
            // preview 与主图来自同一帧，不再二次截屏。
            result.Data["screenshot_preview"] = result.PreviewMeta;
        }

        ToolResponse response = ToolResponse.Text(result.Data)
            .AddResourceLink(artifactUri, artifactName, "image/png", result.Png.Length);

        if (!hasPreview)
        {
            return response;
        }

        return response.AddImage(result.PreviewPng!);
    }

    private (string Path, string Uri) WriteArtifact(byte[] png, string prefix, byte[]? preview = null)
    {
        string fileName =
            $"{prefix}-{DateTime.Now:HHmmss-fff}-{Guid.NewGuid().ToString("N")[..8]}.png";
        string path = Path.Combine(_session.ArtifactDirectory, fileName);
        File.WriteAllBytes(path, png);

        if (preview is not null)
        {
            // 资源读取时用 ?preview=1 指向这张小图，避免把 300KB+ 原图塞回 Blob。
            File.WriteAllBytes(Path.ChangeExtension(path, ".preview.png"), preview);
        }

        return (path, $"vsp://artifacts/{fileName}");
    }

    private ToolResponse CaptureScreen(JsonElement arguments)
    {
        int? x = null, y = null, width = null, height = null;

        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("region", out JsonElement region))
        {
            if (region.ValueKind != JsonValueKind.Object)
            {
                throw new ToolException("E_INVALID_ARG", "region 必须是对象");
            }

            x = RequiredInt(region, "x");
            y = RequiredInt(region, "y");
            width = RequiredInt(region, "width");
            height = RequiredInt(region, "height");
        }

        // 全屏默认降到 0.5：UI 截图缩小一半仍然可读，体积/上下文成本降 4 倍。
        double scale = OptionalDouble(arguments, "scale", x is null ? 0.5 : 1.0);

        if (arguments.TryGetProperty("scale", out _) && (scale < 0.1 || scale > 1.0))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"scale 必须在 0.1..1.0（收到 {scale}）",
                new JsonObject { ["field"] = "scale", ["value"] = scale },
                retryable: false,
                hint: "0 或 10 这类值不会再被静默钳制；需要小图请用 0.1–1.0",
                stage: "validate");
        }
        string? savePath = OptionalString(arguments, "save_path");
        bool inline = OptionalBool(arguments, "inline", true);

        // 关键发现：全屏“悬崖”主要不是 GDI 采集，而是大 PNG 的 Base64/JSON/stdio 回传。
        // 366KB PNG 内联 → 客户端约 1.2s；落盘 + 200B 元数据 → 约 35ms。
        // 因此超过 128KB 的图默认落 artifact，并额外内联一张 0.25× 预览。
        const int InlineLimit = Limits.InlineLimitBytes;
        bool hasSavePath = !string.IsNullOrWhiteSpace(savePath);

        // preview 与主图同帧；small 图会多编码一次缩略图，但省掉了第二次截屏。
        SessionManager.CaptureResult capture = _session.CaptureScreenEx(
            x, y, width, height, scale, savePath,
            storeFrame: true,
            previewScale: inline ? Math.Max(Limits.MinScale, scale * Limits.PreviewScale) : null);

        byte[] png = capture.Png;
        JsonObject meta = VspJson.ToNode(capture.Meta).AsObject();

        if (inline && png.Length <= InlineLimit)
        {
            meta["inline"] = true;
            return ToolResponse.Text(meta).AddImage(png);
        }

        string? artifactUri = null;
        string? artifactName = null;

        // 显式 save_path 就是唯一正式产物：不再额外写 artifact（R4）。
        if (!hasSavePath)
        {
            try
            {
                (string path, string uri) = WriteArtifact(png, "shot", capture.PreviewPng);
                meta["artifact_path"] = path;
                meta["artifact_uri"] = uri;
                meta["resource_uri"] = uri;
                meta["path"] = path;
                artifactUri = uri;
                artifactName = Path.GetFileName(path);
            }
            catch (Exception exception)
            {
                meta["artifact_error"] = exception.Message;
            }
        }

        meta["inline"] = false;

        bool hasPreview = inline && capture.PreviewPng is not null;

        if (hasPreview)
        {
            meta["preview"] = VspJson.ToNode(capture.PreviewMeta);
        }

        ToolResponse response = ToolResponse.Text(meta);

        if (artifactUri is not null)
        {
            // 标准 resource_link：客户端可直接按 URI 读取大图，不必依赖自定义字段。
            response.AddResourceLink(artifactUri, artifactName ?? "shot.png", "image/png", png.Length);
        }

        if (hasPreview)
        {
            response.AddImage(capture.PreviewPng!);
        }

        return response;
    }

    private ToolResponse FindElement(JsonElement arguments)
    {
        string? name = OptionalString(arguments, "name");
        string? controlType = OptionalString(arguments, "control_type");
        string? automationId = OptionalString(arguments, "automation_id");
        if (controlType is not null && !Uia.UiaService.IsKnownControlType(controlType))
        {
            var supported = new JsonArray();

            foreach (string item in Uia.UiaService.SupportedControlTypes)
            {
                supported.Add(item);
            }

            throw new ToolException(
                "E_INVALID_ARG",
                $"control_type 无效: {controlType}",
                new JsonObject { ["field"] = "control_type", ["supported"] = supported },
                retryable: false,
                hint: "使用 UIA 控件类型英文名，例如 Button、Edit、Document",
                stage: "validate");
        }

        string nameMatch = OptionalString(arguments, "name_match") ?? "exact";
        int index = OptionalInt(arguments, "index", 0);
        bool all = OptionalBool(arguments, "all", false);
        int maxResults = OptionalInt(arguments, "max_results", 20);
        bool includeOffscreen = OptionalBool(arguments, "include_offscreen", false);
        int? pid = OptionalNullableInt(arguments, "pid");
        string? role = OptionalString(arguments, "role");
        string prefer = OptionalString(arguments, "prefer") ?? "auto";

        return ToolResponse.Text(_session.FindElement(
            name, controlType, automationId, nameMatch, index, all, maxResults,
            includeOffscreen, pid, role, prefer));
    }

    private ToolResponse GetUiTree(JsonElement arguments)
    {
        int? maxDepth = OptionalNullableInt(arguments, "max_depth");
        bool? interactiveOnly = OptionalNullableBool(arguments, "interactive_only");
        int? maxNodes = OptionalNullableInt(arguments, "max_nodes");
        string? root = OptionalString(arguments, "root");
        int? pid = OptionalNullableInt(arguments, "pid");
        string? format = OptionalString(arguments, "format");
        bool newSnapshot = OptionalBool(arguments, "new_snapshot", false);

        return ToolResponse.Text(
            _session.GetUiTree(maxDepth, interactiveOnly, maxNodes, root, pid, format, newSnapshot));
    }

    private ToolResponse InvokeElement(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "invoke_element 需要 ref");
        bool allowClick = OptionalBool(arguments, "allow_click", false);
        string? pattern = OptionalString(arguments, "pattern");

        return ToolResponse.Text(_session.InvokeElement(reference, allowClick, pattern));
    }

    private ToolResponse SetElementValue(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "set_element_value 需要 ref");
        string value = OptionalString(arguments, "value")
            ?? throw new ToolException("E_INVALID_ARG", "set_element_value 需要 value");
        bool allowKeyboard = OptionalBool(arguments, "allow_keyboard", false);
        string? mode = OptionalString(arguments, "mode");
        bool allowDestructive = OptionalBool(arguments, "allow_destructive", false);

        return ToolResponse.Text(_session.SetElementValue(
            reference, value, allowKeyboard, mode, allowDestructive));
    }

    private ToolResponse GetElementText(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "get_element_text 需要 ref");
        int maxChars = OptionalInt(arguments, "max_chars", 4096);

        return ToolResponse.Text(_session.GetElementText(reference, maxChars));
    }

    private ToolResponse ReadScreenText(JsonElement arguments)
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

        return ToolResponse.Text(_session.ReadScreenText(
            x, y, width, height,
            OptionalString(arguments, "query"),
            OptionalInt(arguments, "max_lines", 50)));
    }

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

        return ToolResponse.Text(
            _session.Click(x, y, button, clicks, mode, keepForeground, observe, fallbackInput));
    }

    private ToolResponse ClickRef(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "click_ref 需要 ref");

        return ToolResponse.Text(_session.ClickElementRef(
            reference,
            OptionalString(arguments, "channel"),
            OptionalBool(arguments, "keep_foreground", false),
            OptionalBool(arguments, "fallback_input", false)));
    }

    private ToolResponse Gesture(JsonElement arguments) =>
        ToolResponse.Text(_session.Gesture(arguments));

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

        return ToolResponse.Text(_session.ClickText(
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

        return ToolResponse.Text(_session.TypeText(text, pid, verify, chunkChars, channel));
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

            return ToolResponse.Text(
                _session.PressKeySequence(keys, keepForeground, pid, allowSystemKeys, observe));
        }

        string keysText = OptionalString(arguments, "keys")
            ?? throw new ToolException("E_INVALID_ARG", "press_keys 需要 keys 或 sequence");

        return ToolResponse.Text(
            _session.PressKeys(keysText, keepForeground, pid, allowSystemKeys, observe));
    }

    private ToolResponse WaitFor(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("condition", out JsonElement condition) ||
            condition.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "wait_for 需要 condition 对象");
        }

        int timeoutMs = OptionalInt(arguments, "timeout_ms", 5000);
        int? intervalMs = OptionalNullableInt(arguments, "interval_ms");
        return ToolResponse.Text(_session.WaitFor(condition, timeoutMs, intervalMs));
    }

    private ToolResponse AssertRegion(JsonElement arguments)
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

        return ToolResponse.Text(_session.AssertRegion(region, expectation));
    }

    private ToolResponse MatchVisual(JsonElement arguments) =>
        ToolResponse.Text(_session.MatchVisual(arguments));

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

    private ToolResponse GetLog(JsonElement arguments)
    {
        int tail = Math.Clamp(OptionalInt(arguments, "tail", 100), 1, 500);
        string source = (OptionalString(arguments, "source") ?? "memory").Trim().ToLowerInvariant();

        if (source is not ("memory" or "file" or "both"))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"未知 source: {source}（可选 memory/file/both）");
        }

        string? level = OptionalString(arguments, "level");

        if (level is not null &&
            level.Trim().ToUpperInvariant() is not ("DEBUG" or "INFO" or "WARN" or "ERROR"))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"未知 level: {level}（可选 DEBUG/INFO/WARN/ERROR）",
                new JsonObject { ["field"] = "level" },
                retryable: false,
                hint: "level 只接受 DEBUG/INFO/WARN/ERROR",
                stage: "validate");
        }

        string? contains = OptionalString(arguments, "contains");
        bool includeOther = OptionalBool(arguments, "include_other_clients", false);
        string currentClient = _client?.Id ?? "c0";

        bool Visible(LogEntry entry) =>
            includeOther ||
            entry.ClientId is null ||
            string.Equals(entry.ClientId, currentClient, StringComparison.Ordinal);

        bool Matches(LogEntry entry) =>
            Visible(entry) &&
            (string.IsNullOrWhiteSpace(level) ||
             entry.Level.Equals(level.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(contains) ||
             entry.Message.Contains(contains, StringComparison.OrdinalIgnoreCase));

        var memoryEntries = new List<LogEntryView>();

        foreach (LogEntry entry in _log.Tail(Math.Min(500, Math.Max(tail, tail * 4))))
        {
            if (Matches(entry))
            {
                memoryEntries.Add(ToLogView(entry));
            }
        }

        while (memoryEntries.Count > tail)
        {
            memoryEntries.RemoveAt(0);
        }

        IReadOnlyList<string> fileLines = source is "file" or "both"
            ? _log.ReadFileTail(tail)
            : Array.Empty<string>();

        var fileEntries = new List<LogEntryView>();
        LogEntry? lastFileError = null;

        foreach (string line in fileLines)
        {
            LogEntry? parsed = LogRing.Parse(line);

            if (parsed is null)
            {
                if (string.IsNullOrWhiteSpace(level) && string.IsNullOrWhiteSpace(contains))
                {
                    fileEntries.Add(new LogEntryView(null, "RAW", line, null));
                }
                continue;
            }

            if (!Matches(parsed))
            {
                continue;
            }

            fileEntries.Add(ToLogView(parsed));

            if (string.Equals(parsed.Level, "ERROR", StringComparison.Ordinal))
            {
                lastFileError = parsed;
            }
        }

        LogEntry? lastError = _log.Tail(500)
            .Where(Visible)
            .LastOrDefault(entry => string.Equals(entry.Level, "ERROR", StringComparison.Ordinal))
            ?? lastFileError;

        var view = new LogView(
            source,
            includeOther,
            source == "file" ? fileEntries : memoryEntries,
            _log.FilePath,
            lastError is null ? null : ToLogView(lastError),
            source is "file" or "both" ? fileEntries : null);

        return ToolResponse.Text(VspJson.ToNode(view));
    }


    /// <summary>日志条目 → 视图。time 统一用往返格式（O），与旧输出一致。</summary>
    private static LogEntryView ToLogView(LogEntry entry) =>
        new(
            entry.Time.ToString("O"),
            entry.Level,
            entry.Message,
            string.IsNullOrWhiteSpace(entry.ClientId) ? null : entry.ClientId);

    private ToolResponse RunScript(JsonElement arguments)
    {
        string code = OptionalString(arguments, "code")
            ?? throw new ToolException("E_INVALID_ARG", "run_script 需要 code");

        int timeoutMs = OptionalInt(arguments, "timeout_ms", 30000);
        return ToolResponse.Text(VspJson.ToNode(_session.RunScript(code, timeoutMs)));
    }

}
