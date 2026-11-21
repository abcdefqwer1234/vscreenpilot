using System.Text.Json.Nodes;
using VScreenPilot.Ui;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    /// <summary>租约/状态由 McpTransport 提供，避免 SessionManager 反向依赖 MCP 层。</summary>
    internal Func<string?>? LeaseHolderProvider { get; set; }

    internal Func<JsonObject>? ScreenLeaseProvider { get; set; }

    internal JsonArray ClientSummariesJson(bool includeCwd = false)
    {
        var array = new JsonArray();
        string? holder = LeaseHolderProvider?.Invoke();

        foreach (ClientContext client in Clients.All
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var json = new JsonObject
            {
                ["id"] = client.Id,
                ["name"] = client.Name,
                ["connected_at"] = client.ConnectedAt.ToString("O"),
                ["holds_lease"] = string.Equals(holder, client.Id, StringComparison.Ordinal),
            };

            if (includeCwd)
            {
                json["cwd"] = client.Cwd;
                var roots = new JsonArray();

                foreach (string root in client.Roots)
                {
                    roots.Add(root);
                }

                json["roots"] = roots;
            }

            array.Add((JsonNode)json);
        }

        return array;
    }

    /// <summary>只清指定客户端的 refs/快照/帧/artifact 映射；ctl 管理员视图也用它保持只读。</summary>
    internal void DropClientRefs(string clientId)
    {
        lock (_gate)
        {
            _refs.DropClient(clientId);
            _uiaBlockedPids.Remove(clientId);

            _frames.RemoveClient(clientId);

            lock (_artifactGate)
            {
                _artifactDirectories.Remove(clientId);
            }
        }
    }

    /// <summary>
    /// 连接结束时的按客户端清理：refs/帧/artifact 立即清；apps 在拿到屏幕租约（无其他客户端操作）时
    /// 按 force 关闭/归位（与 0.1.0 进程退出一致）。其他客户端正在操作时留给 daemon 退出时全量清理。
    /// </summary>
    internal bool ReleaseClient(ClientContext? client, bool includeApps)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return true;
        }

        string clientId = client?.Id ?? "c0";
        DropClientRefs(clientId);

        if (!includeApps || !HasOwnedApps(clientId))
        {
            SweepArtifactsPeriodically();
            return true;
        }

        using (Clients.Use(client ?? new ClientContext
               {
                   Id = clientId,
                   Cwd = Environment.CurrentDirectory,
               }))
        {
            if (!TryEnterExclusive(3000))
            {
                _log.Warn($"客户端 {clientId} 已断开，但会话仍被占用；窗口留待下次重试/daemon 退出");
                SweepArtifactsPeriodically();
                return false;
            }

            try
            {
                ReleaseEnvironment(
                    keepDisplay: true,
                    onDirty: "force",
                    allowDataLoss: true);
            }
            catch (Exception exception)
            {
                _log.Warn($"清理客户端 {clientId} 的窗口失败: {exception.Message}");
                return false;
            }
            finally
            {
                ExitExclusive();
            }
        }

        SweepArtifactsPeriodically();
        return true;
    }

    private bool HasOwnedApps(string clientId)
    {
        lock (_gate)
        {
            return _apps.Any(app =>
                string.Equals(app.OwnerClientId, clientId, StringComparison.Ordinal) &&
                IsLiveTrackedApp(app));
        }
    }

    /// <summary>当前客户端是否还有需要关闭/归位的窗口；release 需要据此决定是否拿屏幕租约。</summary>
    internal bool HasLiveOwnedApps()
    {
        lock (_gate)
        {
            return _apps.Any(app => IsOwned(app) && IsLiveTrackedApp(app));
        }
    }

    private bool IsOwned(TrackedApp app) =>
        Clients.Current?.IncludeAllApps == true ||
        string.Equals(app.OwnerClientId, Clients.CurrentId, StringComparison.Ordinal);
}
