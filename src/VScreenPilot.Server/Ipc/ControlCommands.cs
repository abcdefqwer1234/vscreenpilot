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
using VScreenPilot.Services;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Platform.Ipc;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Ipc;

/// <summary>
/// 只读诊断/控制管道。活跃实例在 <c>\\.\pipe\VScreenPilot-ctl-&lt;profile&gt;-&lt;sid&gt;</c>
/// 上响应 env/snapshot/screen/status/stop；除 stop 外的操作不改变会话状态。
/// </summary>
/// <summary>控制管道的请求处理；从 ControlServer 拆出的命令实现。</summary>
internal sealed class ControlCommands
{
    internal readonly ControlServer _host;

    public ControlCommands(ControlServer host) => _host = host;
    internal JsonObject Snapshot(JsonObject request)
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
            foreach (TrackedApp app in _host._facade.GetApps()
                         .Where(item => pid is null || item.Pid == pid)
                         .Take(32))
            {
                apps.Add((JsonNode)BuildAppItem(app, maxDepth, interactive, maxNodes, ref totalNodes, ref legacy));

                if (totalNodes < 2000)
                {
                    continue;
                }

                truncated = true;
                break;
            }

            result["count"] = apps.Count;
            result["truncated"] = truncated;
            MergeLegacy(result, legacy);
            return result;
        }
        finally
        {
            // ctl 是一次性管理员视图，输出后立即丢弃 refs。
            _host._control.DropClientRefs("ctl");
        }
    }

    private JsonObject BuildAppItem(
        TrackedApp app,
        int maxDepth,
        bool interactive,
        int maxNodes,
        ref int totalNodes,
        ref JsonObject? legacy)
    {
        var item = new JsonObject
        {
            ["pid"] = app.Pid,
            ["hwnd"] = app.Hwnd.ToInt64(),
            ["title"] = ReadTitle(app),
            ["alive"] = TrackedAppTracker.IsLive(app),
            ["owner_client_id"] = app.OwnerClientId,
        };

        try
        {
            JsonObject data = VspJson.ToNode(_host._facade.Snapshot(new SnapshotRequest(
                app.Pid,
                maxDepth,
                interactive,
                maxNodes,
                IncludeValues: true,
                IncludeScreenshot: false,
                Scale: 0.5)).Data).AsObject();
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

        return item;
    }

    private static string ReadTitle(TrackedApp app)
    {
        if (app.Hwnd == IntPtr.Zero)
        {
            return app.Title;
        }

        try
        {
            return WindowManager.GetTitle(app.Hwnd);
        }
        catch (Exception exception)
        {
            // 窗口已失效时保留登记时的标题。
            LogSink.Debug($"忽略异常: {exception.Message}");
            return app.Title;
        }
    }

    private static void MergeLegacy(JsonObject result, JsonObject? legacy)
    {
        if (legacy is null)
        {
            return;
        }

        // 兼容旧的单 app 顶层字段；新客户端应优先读 apps[]。
        foreach ((string key, JsonNode? value) in legacy)
        {
            if (!result.ContainsKey(key))
            {
                result[key] = value?.DeepClone();
            }
        }
    }

    internal JsonObject EnvironmentResult(JsonObject request)
    {
        JsonObject result = VspJson.ToNode(_host._facade.GetEnvironment(ensure: false)).AsObject();

        if (GetBool(request, "ensure", false))
        {
            result["ensure_ignored"] = true;
            result["warning"] = "ctl env 保持只读；需要挂载/修复虚拟屏请通过 MCP get_environment{ensure:true}";
        }

        return result;
    }

    internal JsonObject Capture()
    {
        CaptureResult capture = _host._facade.CaptureScreen(
            new CaptureRequest(null, null, null, null, 0.5, SavePath: null,
                StoreFrame: false, PreviewScale: null));
        string path = Path.Combine(
            _host._facade.ArtifactDirectory,
            $"cli-{DateTime.Now:HHmmss-fff}.png");
        File.WriteAllBytes(path, capture.Png);
        JsonObject meta = VspJson.ToNode(capture.Meta).AsObject();
        meta["path"] = path;
        return meta;
    }

    internal JsonObject Status()
    {
        var result = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["version"] = BuildInfo.Version,
            ["profile"] = _host._facade.ProfileName,
            ["mutex_host"] = Profile.MutexName(_host._facade.ProfileName),
            ["ctl_pipe"] = _host._pipeName,
            ["mcp_pipe"] = PipeNames.Mcp(_host._facade.ProfileName),
            ["initialized"] = _host._facade.Initialized,
            ["apps"] = _host._control.OwnedAppCount,
            ["clients"] = _host._control.ActiveClientCount,
            ["client_list"] = _host._control.ClientSummaries(includeCwd: true),
            ["screen_lease"] = _host._control.LeaseDescribe(),
            ["working_set_mb"] = _host.CurrentWorkingSetMb(),
            ["handles"] = _host.CurrentHandleCount(),
        };

        DisplayInfo? display = _host._control.VirtualDisplay;
        result["display"] = new JsonObject
        {
            ["enabled"] = display is not null,
            ["device"] = display?.DeviceName,
            ["x"] = display?.X,
            ["y"] = display?.Y,
            ["width"] = display?.Width,
            ["height"] = display?.Height,
        };

        var entry = _host._registry.Read();
        if (entry is not null)
        {
            result["registry"] = entry.ToJson();
        }

        return result;
    }

    internal JsonObject Stop(bool force, out bool shutdownRequested)
    {
        if (!force && _host._control.ActiveClientCount > 0)
        {
            throw new ToolException(
                "E_CLIENTS_ACTIVE",
                $"仍有 {_host._control.ActiveClientCount} 个 MCP 客户端连接，拒绝停止",
                new JsonObject { ["clients"] = _host._control.ActiveClientCount },
                retryable: true,
                hint: "关闭这些客户端，或使用 --cli stop --force",
                stage: "control");
        }

        _host._log.Warn(force ? "控制管道请求强制停止" : "控制管道请求停止");
        shutdownRequested = true;
        return new JsonObject { ["stopping"] = true, ["force"] = force };
    }

    /// <summary>按字符读取一行并限制长度，避免超长请求在 5s 内把内存撑大。</summary>

}
