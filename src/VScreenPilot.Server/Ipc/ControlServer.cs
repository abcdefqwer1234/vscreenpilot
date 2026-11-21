using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Json;
using VScreenPilot.Mcp;
using VScreenPilot.Session;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Platform.Ipc;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Ipc;

/// <summary>
/// 只读诊断/控制管道。活跃实例在 <c>\\.\pipe\VScreenPilot-ctl-&lt;profile&gt;-&lt;sid&gt;</c>
/// 上响应 env/snapshot/screen/status/stop；除 stop 外的操作不改变会话状态。
/// </summary>
internal sealed class ControlServer : IDisposable
{
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private readonly SessionManager _session;
    private readonly LogRing _log;
    private readonly InstanceRegistry _registry;
    private readonly string _pipeName;
    private readonly Action _shutdown;
    private readonly CancellationTokenSource _cts = new();
    private Task[]? _loops;
    private int _disposed;

    // 两个监听循环：一个较慢的 --cli screen/snapshot 不会让 env/status 连不上。
    // Windows 的 maxNumberOfServerInstances 计的是“连接中 + 监听中”，
    // 因此 2 个循环各保留 1 个监听实例、最多 2 个连接，取 4。
    private const int ListerCount = 2;
    private const int MaxInstances = 4;

    public ControlServer(
        SessionManager session,
        LogRing log,
        InstanceRegistry registry,
        string profile,
        Action shutdown)
    {
        _session = session;
        _log = log;
        _registry = registry;
        _pipeName = PipeNames.Ctl(profile);
        _shutdown = shutdown;
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        _loops = Enumerable.Range(0, ListerCount)
            .Select(_ => Task.Run(() => LoopAsync(_cts.Token)))
            .ToArray();
        _log.Info($"控制管道已启动: {_pipeName}");
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
#pragma warning disable CA2007 // await using 无法附加 ConfigureAwait；管道等待随后显式 ConfigureAwait(false)
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    MaxInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#pragma warning restore CA2007

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                if (!IntegrityCheck.TryCheck(pipe.SafePipeHandle, out string integrityReason))
                {
                    _log.Warn($"拒绝控制连接：{integrityReason}");
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                    {
                        AutoFlush = true,
                    };
                    await WriteAsync(writer, Error("E_INTEGRITY_MISMATCH", integrityReason)).ConfigureAwait(false);
                    continue;
                }

                await HandleAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _log.Warn("控制请求超时/取消，继续监听");
                continue;
            }
            catch (Exception exception)
            {
                _log.Warn($"控制管道连接失败: {exception.Message}");
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
        };

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(TimeSpan.FromSeconds(5));

        string? line;

        try
        {
            line = await ReadLimitedLineAsync(reader, MaxRequestBytes, requestCts.Token).ConfigureAwait(false);
        }
        catch (ToolException exception)
        {
            await WriteAsync(writer, Error(exception.Code, exception.Message)).ConfigureAwait(false);
            return;
        }

        if (line is null)
        {
            await WriteAsync(writer, Error("E_INVALID_ARG", "控制请求为空或超过 1MB")).ConfigureAwait(false);
            return;
        }

        JsonObject? request;

        try
        {
            request = JsonNode.Parse(line) as JsonObject;
        }
        catch
        {
            request = null;
        }

        if (request is null)
        {
            await WriteAsync(writer, Error("E_INVALID_ARG", "控制请求不是有效 JSON")).ConfigureAwait(false);
            return;
        }

        string op = request["op"] is JsonValue opValue && opValue.TryGetValue(out string? opText)
            ? opText
            : string.Empty;

        try
        {
            using IDisposable controlScope = _session.Clients.Use(new ClientContext
            {
                Id = "ctl",
                Name = "control",
                Cwd = Environment.CurrentDirectory,
                IncludeAllApps = true,
            });

            bool shutdownRequested = false;
            JsonObject result = op switch
            {
                "env" => EnvironmentResult(request),
                "snapshot" => Snapshot(request),
                "screen" => Capture(),
                "status" => Status(),
                "stop" => Stop(request["force"]?.GetValue<bool>() ?? false, out shutdownRequested),
                _ => throw new ToolException(
                    "E_INVALID_ARG",
                    $"未知控制操作: {op}（可选 env/snapshot/screen/status/stop）"),
            };

            await WriteAsync(writer, new JsonObject
            {
                ["vsp"] = 1,
                ["ok"] = true,
                ["result"] = result,
            }).ConfigureAwait(false);

            if (shutdownRequested)
            {
                // 先让响应 flushed，再退出；否则 --cli stop 可能只看到 EOF。
                _ = Task.Run(() =>
                {
                    try
                    {
                        _shutdown();
                    }
                    catch (Exception exception)
                    {
                        // 退出路径忽略。
                        System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
                    }
                }, CancellationToken.None);
            }
        }
        catch (ToolException exception)
        {
            await WriteAsync(writer, new JsonObject
            {
                ["vsp"] = 1,
                ["ok"] = false,
                ["code"] = exception.Code,
                ["message"] = exception.Message,
                ["hint"] = exception.Hint,
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Error($"控制操作 {op} 失败", exception);
            await WriteAsync(writer, Error("E_INTERNAL", exception.Message)).ConfigureAwait(false);
        }
    }

    private JsonObject Snapshot(JsonObject request)
    {
        int maxNodes = Math.Clamp(GetInt(request, "max_nodes", 300), 1, 1024);
        int maxDepth = Math.Clamp(GetInt(request, "max_depth", 4), 1, 10);
        bool interactive = GetBool(request, "interactive_only", true);
        int? pid = GetNullableInt(request, "pid");
        var result = new JsonObject
        {
            ["admin_view"] = true,
            ["apps"] = new JsonArray(),
            ["count"] = 0,
        };
        var apps = result["apps"]!.AsArray();
        JsonObject? legacy = null;
        int totalNodes = 0;
        bool truncated = false;

        try
        {
            foreach (TrackedApp app in _session.GetAppsSnapshot()
                         .Where(item => pid is null || item.Pid == pid)
                         .Take(32))
            {
                string title = app.Title;

                try
                {
                    if (app.Hwnd != IntPtr.Zero)
                    {
                        title = WindowManager.GetTitle(app.Hwnd);
                    }
                }
                catch (Exception exception)
                {
                    // 窗口已失效时保留登记时的标题。
                    System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
                }

                var item = new JsonObject
                {
                    ["pid"] = app.Pid,
                    ["hwnd"] = app.Hwnd.ToInt64(),
                    ["title"] = title,
                    ["alive"] = TrackedAppTracker.IsLive(app),
                    ["owner_client_id"] = app.OwnerClientId,
                };

                try
                {
                    JsonObject data = VspJson.ToNode(_session.Snapshot(
                        app.Pid,
                        maxDepth,
                        interactive,
                        maxNodes,
                        includeValues: true,
                        includeScreenshot: false,
                        scale: 0.5).Data).AsObject();
                    item["snapshot_id"] = data["snapshot_id"]?.DeepClone();
                    item["tree"] = data["tree"]?.DeepClone();
                    item["nodes"] = data["nodes"]?.DeepClone();
                    item["truncated"] = data["truncated"]?.DeepClone();
                    legacy = data;
                    totalNodes += data["nodes"]?.GetValue<int>() ?? 0;
                }
                catch (ToolException exception)
                {
                    item["error"] = new JsonObject
                    {
                        ["code"] = exception.Code,
                        ["message"] = exception.Message,
                    };
                }

                apps.Add((JsonNode)item);

                if (totalNodes >= 2000)
                {
                    truncated = true;
                    break;
                }
            }

            result["count"] = apps.Count;
            result["truncated"] = truncated;

            // 兼容旧的单 app 顶层字段；新客户端应优先读 apps[]。
            if (legacy is not null)
            {
                foreach ((string key, JsonNode? value) in legacy)
                {
                    if (!result.ContainsKey(key))
                    {
                        result[key] = value?.DeepClone();
                    }
                }
            }

            return result;
        }
        finally
        {
            // ctl 是一次性管理员视图，输出后立即丢弃 refs。
            _session.DropClientRefs("ctl");
        }
    }

    private JsonObject EnvironmentResult(JsonObject request)
    {
        JsonObject result = VspJson.ToNode(_session.GetEnvironment(ensure: false)).AsObject();

        if (GetBool(request, "ensure", false))
        {
            result["ensure_ignored"] = true;
            result["warning"] = "ctl env 保持只读；需要挂载/修复虚拟屏请通过 MCP get_environment{ensure:true}";
        }

        return result;
    }

    private JsonObject Capture()
    {
        CaptureResult capture = _session.CaptureScreenEx(
            null, null, null, null, 0.5, savePath: null, storeFrame: false, previewScale: null);
        string path = Path.Combine(
            _session.ArtifactDirectory,
            $"cli-{DateTime.Now:HHmmss-fff}.png");
        File.WriteAllBytes(path, capture.Png);
        JsonObject meta = VspJson.ToNode(capture.Meta).AsObject();
        meta["path"] = path;
        return meta;
    }

    private JsonObject Status()
    {
        var result = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["version"] = BuildInfo.Version,
            ["profile"] = _session.ProfileName,
            ["mutex_host"] = Profile.MutexName(_session.ProfileName),
            ["ctl_pipe"] = _pipeName,
            ["mcp_pipe"] = PipeNames.Mcp(_session.ProfileName),
            ["initialized"] = _session.Initialized,
            ["apps"] = _session.Apps.Count,
            ["clients"] = _session.Clients.ActiveCount,
            ["client_list"] = _session.ClientSummariesJson(includeCwd: true),
            ["screen_lease"] = _session.Lease.Describe(),
            ["working_set_mb"] = CurrentWorkingSetMb(),
            ["handles"] = CurrentHandleCount(),
        };

        DisplayInfo? display = _session.VirtualDisplay;
        result["display"] = new JsonObject
        {
            ["enabled"] = display is not null,
            ["device"] = display?.DeviceName,
            ["x"] = display?.X,
            ["y"] = display?.Y,
            ["width"] = display?.Width,
            ["height"] = display?.Height,
        };

        var entry = _registry.Read();
        if (entry is not null)
        {
            result["registry"] = entry.ToJson();
        }

        return result;
    }

    private JsonObject Stop(bool force, out bool shutdownRequested)
    {
        if (!force && _session.Clients.ActiveCount > 0)
        {
            throw new ToolException(
                "E_CLIENTS_ACTIVE",
                $"仍有 {_session.Clients.ActiveCount} 个 MCP 客户端连接，拒绝停止",
                new JsonObject { ["clients"] = _session.Clients.ActiveCount },
                retryable: true,
                hint: "关闭这些客户端，或使用 --cli stop --force",
                stage: "control");
        }

        _log.Warn(force ? "控制管道请求强制停止" : "控制管道请求停止");
        shutdownRequested = true;
        return new JsonObject { ["stopping"] = true, ["force"] = force };
    }

    /// <summary>按字符读取一行并限制长度，避免超长请求在 5s 内把内存撑大。</summary>
    private static async Task<string?> ReadLimitedLineAsync(
        StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        char[] buffer = new char[4096];

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
            }

            for (int index = 0; index < read; index++)
            {
                char value = buffer[index];

                if (value == '\n')
                {
                    return line.ToString().TrimEnd('\r');
                }

                if (line.Length >= maxChars)
                {
                    throw new ToolException("E_INVALID_ARG", "控制请求超过 1MB");
                }

                line.Append(value);
            }
        }
    }

    private static async Task WriteAsync(StreamWriter writer, JsonObject payload)
    {
        string json = payload.ToJsonString();

        if (Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
        {
            json = Error(
                "E_RESPONSE_TOO_LARGE",
                $"控制响应超过 {MaxResponseBytes / (1024 * 1024)}MB；请减小 max_nodes/查询范围").ToJsonString();
        }

        await writer.WriteLineAsync(json).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static JsonObject Error(string code, string message) => new()
    {
        ["vsp"] = 1,
        ["ok"] = false,
        ["code"] = code,
        ["message"] = message,
    };

    private static double CurrentWorkingSetMb()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return Math.Round(process.WorkingSet64 / 1048576.0, 1);
        }
        catch
        {
            return 0;
        }
    }

    private static int CurrentHandleCount()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.HandleCount;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException exception){
            // 已经取消。
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
        }

        // 不立即 Dispose：HandleAsync 可能还在创建 linked token；进程退出时由 OS 回收。
    }
}
