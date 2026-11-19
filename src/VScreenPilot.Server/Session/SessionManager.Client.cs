using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace VScreenPilot.Session;

/// <summary>
/// 一次 MCP 连接（或 CLI 合成客户端）的上下文。
/// refs/apps/epoch 等会在后续 partial 中按 Id 隔离；Cwd/Roots 用于 P-33 路径语义。
/// </summary>
internal sealed class ClientContext
{
    public string Id { get; init; } = "c0";
    public string Name { get; init; } = "local";
    public string Cwd { get; init; } = Environment.CurrentDirectory;
    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();
    public int ProxyPid { get; init; }
    public string Version { get; init; } = string.Empty;
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool AllowScript { get; init; }
    public bool AllowUnsafeScript { get; init; }
    public bool IncludeAllApps { get; init; }
    public string[] AppAllowlist { get; init; } = Array.Empty<string>();
    public long Epoch;
}

internal sealed partial class SessionManager
{
    private static readonly AsyncLocal<ClientContext?> ClientContextSlot = new();
    private static readonly ConcurrentDictionary<string, ClientContext> ClientsById =
        new(StringComparer.Ordinal);
    private static int _activeClients;

    public static int ActiveClientCount => Volatile.Read(ref _activeClients);

    /// <summary>租约/状态由 McpServer 提供，避免 SessionManager 反向依赖 MCP 层。</summary>
    internal Func<string?>? LeaseHolderProvider { get; set; }

    internal Func<JsonObject>? ScreenLeaseProvider { get; set; }

    internal static void ClientEntered(ClientContext client)
    {
        if (ClientsById.TryAdd(client.Id, client))
        {
            Interlocked.Increment(ref _activeClients);
        }
    }

    internal static void ClientLeft(string clientId)
    {
        if (ClientsById.TryRemove(clientId, out _))
        {
            Interlocked.Decrement(ref _activeClients);
        }
    }

    internal static string? FindClientCwd(string clientId) =>
        ClientsById.TryGetValue(clientId, out ClientContext? client) ? client.Cwd : null;

    internal JsonArray ClientSummariesJson(bool includeCwd = false)
    {
        var array = new JsonArray();
        string? holder = LeaseHolderProvider?.Invoke();

        foreach (ClientContext client in ClientsById.Values
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

    public static ClientContext? CurrentClient => ClientContextSlot.Value;

    public static string CurrentClientId => CurrentClient?.Id ?? "c0";

    public static string CurrentCwd => CurrentClient?.Cwd ?? Environment.CurrentDirectory;

    public static IReadOnlyList<string> CurrentRoots =>
        CurrentClient?.Roots ?? Array.Empty<string>();

    public static IDisposable UseClient(ClientContext context)
    {
        ClientContext? previous = ClientContextSlot.Value;
        ClientContextSlot.Value = context;
        return new ClientScope(previous);
    }

    private sealed class ClientScope : IDisposable
    {
        private readonly ClientContext? _previous;

        public ClientScope(ClientContext? previous) => _previous = previous;

        public void Dispose() => ClientContextSlot.Value = _previous;
    }

    internal static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, Path.GetFullPath(CurrentCwd));
    }

    /// <summary>只清指定客户端的 refs/快照/帧/artifact 映射；ctl 管理员视图也用它保持只读。</summary>
    internal void DropClientRefs(string clientId)
    {
        lock (_gate)
        {
            foreach ((string refKey, ElementRefEntry entry) in _elements
                         .Where(pair => string.Equals(
                             pair.Value.OwnerClientId, clientId, StringComparison.Ordinal))
                         .ToList())
            {
                _elements.Remove(refKey);
                _elementKeys.Remove(entry.Key);
            }

            ClearSnapshotState(clientId);
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

        using (UseClient(client ?? new ClientContext
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
        CurrentClient?.IncludeAllApps == true ||
        string.Equals(app.OwnerClientId, CurrentClientId, StringComparison.Ordinal);
}
