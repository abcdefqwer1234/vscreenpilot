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
using VScreenPilot.Platform.Logging;

using VScreenPilot.Tools;

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

}
