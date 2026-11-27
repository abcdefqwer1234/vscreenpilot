using System.Text.Json.Nodes;
using VScreenPilot.Ui;
using VScreenPilot.Apps;

using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Session;

/// <summary>ClientOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionClientOps
{
    internal readonly SessionManager _host;

    private const long OrphanGraceMs = 10 * 60 * 1000;
    private readonly Dictionary<string, long> _orphanSince = new(StringComparer.Ordinal);

    public SessionClientOps(SessionManager host) => _host = host;

    /// <summary>连接时按 cwd/roots 的 clientKey 自动认领上次会话遗留的窗口。</summary>
    internal int ClaimRecoveredApps(ClientContext client)
    {
        IReadOnlyList<string> keys = ClientSessionRegistry.ComputeClientKeyCandidates(
            client.Cwd, client.Roots);
        var keySet = new HashSet<string>(keys, StringComparer.Ordinal);

        if (keySet.Count == 0)
        {
            return 0;
        }

        int claimed = 0;

        lock (_host._gate)
        {
            foreach (TrackedApp app in _host._apps.Snapshot())
            {
                if (!app.Recovered ||
                    string.IsNullOrWhiteSpace(app.ClientKey) ||
                    !keySet.Contains(app.ClientKey))
                {
                    continue;
                }

                app.OwnerClientId = client.Id;
                app.Recovered = false;
                claimed++;
            }
        }

        if (claimed > 0)
        {
            _host._log.Info($"按 clientKey 自动认领 {claimed} 个上次会话窗口 (client={client.Id})");
        }

        return claimed;
    }

    internal JsonArray ClientSummariesJson(bool includeCwd = false)
    {
        var array = new JsonArray();
        string? holder = _host.LeaseHolderProvider?.Invoke();

        foreach (ClientContext client in _host.Clients.All
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
        lock (_host._gate)
        {
            _host.Refs.DropClient(clientId);
            _host._input.UnblockClient(clientId);

            _host._frames.RemoveClient(clientId);

            lock (_host._artifactGate)
            {
                _host._artifactDirectories.Remove(clientId);
            }
        }
    }

    /// <summary>
    /// 连接结束时的按客户端清理：refs/帧/artifact 立即清；apps 在拿到屏幕租约（无其他客户端操作）时
    /// 按 abort 保留窗口（0.2.x 起不再丢未保存内容），留到 daemon 退出时全量 force 清理。
    /// 其他客户端正在操作时同样留给 daemon 退出时处理。
    /// </summary>
    internal bool ReleaseClient(ClientContext? client, bool includeApps)
    {
        if (Volatile.Read(ref _host._disposed) != 0)
        {
            return true;
        }

        string clientId = client?.Id ?? "c0";
        DropClientRefs(clientId);

        if (!includeApps || !HasOwnedApps(clientId))
        {
            _host.ArtifactOps.SweepArtifactsPeriodically();
            return true;
        }

        using (_host.Clients.Use(client ?? new ClientContext
        {
            Id = clientId,
            Cwd = Environment.CurrentDirectory,
        }))
        {
            if (!_host.TryEnterExclusive(3000))
            {
                _host._log.Warn($"客户端 {clientId} 已断开，但会话仍被占用；窗口留待下次重试/daemon 退出");
                _host.ArtifactOps.SweepArtifactsPeriodically();
                return false;
            }

            try
            {
                _host.LifecycleOps.ReleaseEnvironment(
                    keepDisplay: true,
                    onDirty: "abort",
                    allowDataLoss: false);
            }
            catch (Exception exception)
            {
                _host._log.Warn($"清理客户端 {clientId} 的窗口失败: {exception.Message}");
                return false;
            }
            finally
            {
                _host.ExitExclusive();
            }
        }

        _host.ArtifactOps.SweepArtifactsPeriodically();
        RecordOrphan(clientId);
        return true;
    }

    /// <summary>abort 保留的窗口在客户端断开后先记账；超过宽限且无人接管才由维护服务回收。</summary>
    private void RecordOrphan(string clientId)
    {
        lock (_host._gate)
        {
            _orphanSince[clientId] = Environment.TickCount64;
        }

        _host._log.Warn(
            $"客户端 {clientId} 已断开，按 abort 保留其窗口；" +
            $"{OrphanGraceMs / 60_000} 分钟内未被接管将由维护服务回收");
    }

    /// <summary>维护服务周期调用：回收已断开客户端在 abort 下保留的窗口。</summary>
    internal void ReapOrphans()
    {
        if (Volatile.Read(ref _host._disposed) != 0)
        {
            return;
        }

        List<string> due;
        long now = Environment.TickCount64;

        lock (_host._gate)
        {
            if (_orphanSince.Count == 0)
            {
                return;
            }

            var live = new HashSet<string>(
                _host.Clients.All.Select(item => item.Id), StringComparer.Ordinal);

            foreach (string id in _orphanSince.Keys.Where(live.Contains).ToList())
            {
                _orphanSince.Remove(id);
            }

            due = _orphanSince
                .Where(pair => now - pair.Value >= OrphanGraceMs)
                .Select(pair => pair.Key)
                .ToList();
        }

        foreach (string clientId in due)
        {
            if (!_host.TryEnterExclusive(3000))
            {
                return;
            }

            try
            {
                using (_host.Clients.Use(new ClientContext { Id = clientId, Name = "orphan" }))
                {
                    _host.LifecycleOps.ReleaseEnvironment(
                        keepDisplay: true,
                        onDirty: "force",
                        allowDataLoss: true);
                }

                _host._log.Warn($"已回收孤儿窗口 client={clientId}");
            }
            catch (Exception exception)
            {
                _host._log.Warn($"回收孤儿窗口失败 client={clientId}: {exception.Message}");
                return;
            }
            finally
            {
                _host.ExitExclusive();
            }

            lock (_host._gate)
            {
                _orphanSince.Remove(clientId);
            }
        }
    }

    internal static bool ShouldReapOrphan(
        bool clientLive, long orphanSinceTicks, long nowTicks, long graceMs) =>
        !clientLive && nowTicks - orphanSinceTicks >= graceMs;

    internal bool HasOwnedApps(string clientId)
    {
        lock (_host._gate)
        {
            return _host._apps.Any(app =>
                string.Equals(app.OwnerClientId, clientId, StringComparison.Ordinal) &&
                TrackedAppTracker.IsLive(app));
        }
    }

    /// <summary>当前客户端是否还有需要关闭/归位的窗口；release 需要据此决定是否拿屏幕租约。</summary>
    internal bool HasLiveOwnedApps()
    {
        lock (_host._gate)
        {
            return _host._apps.Any(app => IsOwned(app) && TrackedAppTracker.IsLive(app));
        }
    }

    internal bool IsOwned(TrackedApp app) =>
        _host.Clients.Current?.IncludeAllApps == true ||
        string.Equals(app.OwnerClientId, _host.Clients.CurrentId, StringComparison.Ordinal);
}
