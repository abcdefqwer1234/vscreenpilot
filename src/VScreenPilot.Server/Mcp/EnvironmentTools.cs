using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Json;
using VScreenPilot.Services;
using VScreenPilot.Session;
using VScreenPilot.Tools;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;

namespace VScreenPilot.Mcp;

/// <summary>Environment 组：get_environment / release_environment。</summary>
internal sealed class EnvironmentTools
{
    private readonly IEnvironmentService _environment;
    private readonly ClientInfo? _client;

    public EnvironmentTools(IEnvironmentService environment, ClientInfo? client)
    {
        _environment = environment;
        _client = client;
    }

    public ToolResponse GetEnvironment(JsonElement arguments)
    {
        EnvironmentView environment = _environment.Get(OptionalBool(arguments, "ensure", false));
        JsonObject data = VspJson.ToNode(environment).AsObject();
        data["client_id"] = _client?.Id ?? "c0";
        data["screen_lease"] = _environment.DescribeLease();
        return ToolResponse.Text(data);
    }

    public ToolResponse ReleaseEnvironment(JsonElement arguments)
    {
        bool keepDisplay = OptionalBool(arguments, "keep_display", true);
        string onDirty = OptionalString(arguments, "on_dirty") ?? "abort";
        bool allowDataLoss = OptionalBool(arguments, "allow_data_loss", false);
        string clientId = _client?.Id ?? "c0";
        ReleaseReport report = _environment.Release(new ReleaseRequest
        {
            KeepDisplay = keepDisplay,
            OnDirty = onDirty,
            AllowDataLoss = allowDataLoss,
        });
        JsonObject data = VspJson.ToNode(report).AsObject();
        data["client_id"] = clientId;

        if (data["display_kept"]?.GetValue<bool>() == true)
        {
            data["display_kept_reason"] = _environment.ActiveClientCount > 1
                ? "other_clients"
                : onDirty == "abort" ? "on_dirty_abort" : "requested";
        }

        // 成功释放后把屏幕租约还给下一个客户端；finally 不再重复调用（幂等）。
        _environment.ReleaseLease(clientId);
        JsonObject lease = _environment.DescribeLease();
        bool leaseHeld = lease["held"]?.GetValue<bool>() == true;
        string? holder = lease["holder_client_id"]?.GetValue<string>();
        int activeCalls = lease["active_calls"]?.GetValue<int>() ?? 0;
        data["screen_lease_released"] =
            !leaseHeld ||
            (string.Equals(holder, clientId, StringComparison.Ordinal) && activeCalls <= 1);
        return ToolResponse.Text(data);
    }

    /// <summary>只读：列出顶层窗口，非本会话窗口只给进程名/类名，不回传标题。</summary>
    public ToolResponse ListWindows(JsonElement arguments) =>
        ToolResponse.Text(_environment.ListWindows(new WindowQuery
        {
            Scope = OptionalString(arguments, "scope") ?? "all",
            IncludeOffscreen = OptionalBool(arguments, "include_offscreen", true),
            TitleContains = OptionalString(arguments, "title_contains"),
            Limit = OptionalInt(arguments, "limit", 200),
        }));

    /// <summary>只读：进程快照，解决长任务"看不到跑没跑、退出码是多少"。</summary>
    public ToolResponse GetProcesses(JsonElement arguments) =>
        ToolResponse.Text(_environment.GetProcesses(new ProcessQuery
        {
            Name = OptionalString(arguments, "name"),
            Pid = OptionalNullableInt(arguments, "pid"),
            IncludeWindows = OptionalBool(arguments, "include_windows", false),
            Limit = OptionalInt(arguments, "limit", 200),
        }));

    /// <summary>运行期切换虚拟屏几何；只改本会话自己的 VDD 屏。</summary>
    public ToolResponse SetDisplay(JsonElement arguments)
    {
        int width = OptionalInt(arguments, "width", 0);
        int height = OptionalInt(arguments, "height", 0);
        int refresh = OptionalInt(arguments, "refresh", 60);

        if (width < 800 || width > 3840 || height < 600 || height > 2160)
        {
            throw new VScreenPilot.Errors.ToolException(
                "E_INVALID_ARG",
                $"分辨率超出范围：{width}×{height}（宽 800–3840，高 600–2160）",
                retryable: false,
                stage: "validate");
        }

        bool fitWindows = OptionalBool(arguments, "fit_windows", false) ||
                          string.Equals(
                              Environment.GetEnvironmentVariable("VSP_FIT_MANAGED_WINDOWS"),
                              "1",
                              StringComparison.Ordinal);

        return ToolResponse.Text(
            _environment.SetDisplay(
                width, height, Math.Clamp(refresh, 30, 240),
                OptionalBool(arguments, "reposition", true), fitWindows));
    }
}
