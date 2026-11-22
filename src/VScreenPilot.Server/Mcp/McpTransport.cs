using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VScreenPilot.Infrastructure;
using VScreenPilot.Platform.Logging;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Services;
using VScreenPilot.Tools;

namespace VScreenPilot.Mcp;

/// <summary>
/// MCP 传输层：协议、连接生命周期与工具目录装配。
/// 工具调度交给 <see cref="ToolCallPipeline"/>，handler 只做参数解析。
/// </summary>
internal sealed class McpTransport
{
    private readonly ToolServices _services;
    private readonly IMcpRuntime _runtime;
    private readonly LogRing _log;
    private readonly ClientInfo? _client;
    private readonly TraceLog? _trace;
    private readonly bool _ownsTrace;
    private readonly ToolRegistry _tools;
    private readonly ToolCallPipeline _pipeline;
    private readonly McpClientLifecycle _lifecycle;
    private readonly ResourceHandler _resources;

    internal McpTransport(McpTransportOptions options)
    {
        _services = options.Services;
        _runtime = options.Runtime;
        _log = options.Log;
        _client = options.Client;
        _trace = options.Trace ?? (options.TraceEnabled ? new TraceLog() : null);
        _ownsTrace = options.Trace is null && _trace is not null;
        _resources = new ResourceHandler(_services.Artifacts);
        McpTelemetry telemetry = new(
            _runtime.Metrics, _trace, options.Client?.Id ?? "c0");
        _tools = ToolHandlerMap.Build(_services, _log, _client);
        _pipeline = new ToolCallPipeline(
            _tools,
            _log,
            options.Scheduler,
            telemetry,
            options.Control,
            _client);
        _lifecycle = new McpClientLifecycle(_runtime, _log, () => _pipeline.InflightHandlers, _client);

        if (_trace is null)
        {
            return;
        }

        _trace.Write("session_start", new JsonObject
        {
            ["version"] = BuildInfo.Version,
            ["pid"] = Environment.ProcessId,
            ["tools"] = _tools.Count,
            ["profile"] = _runtime.ProfileName,
            ["client"] = options.Client?.Id,
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

        mcp.WithListToolsHandler(_pipeline.HandleListToolsAsync)
            .WithListResourcesHandler(_resources.ListAsync)
            .WithReadResourceHandler(_resources.ReadAsync)
            .WithCallToolHandler(_pipeline.HandleCallToolAsync);

        ClientInfo effective = _client
            ?? new ClientInfo("c0", Environment.CurrentDirectory, Array.Empty<string>(), "stdio");

        try
        {
            _lifecycle.Begin(effective);
            await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            string clientId = effective.Id;
            _lifecycle.End(clientId);
            _trace?.Write("session_end", new JsonObject { ["client_id"] = clientId });

            if (_ownsTrace)
            {
                _trace?.Dispose();
            }
        }
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
