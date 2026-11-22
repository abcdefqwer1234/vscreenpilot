using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Json;
using VScreenPilot.Services;
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
}
