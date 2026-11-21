using System.Collections.Concurrent;

namespace VScreenPilot.Session;

/// <summary>
/// 一个 MCP 连接（或 CLI 合成客户端）的上下文。
/// refs/apps/epoch 按 Id 隔离；Cwd/Roots 用于路径语义。
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

/// <summary>
/// 会话内客户端的唯一登记处。每个 <c>SessionManager</c> 持有自己的实例；
/// 当前客户端通过 <see cref="AsyncLocal{T}"/> 作用域传递，避免进程级静态状态。
/// </summary>
internal sealed class ClientSessionRegistry
{
    private readonly AsyncLocal<ClientContext?> _slot = new();
    private readonly ConcurrentDictionary<string, ClientContext> _clients = new(StringComparer.Ordinal);
    private int _active;

    public int ActiveCount => Volatile.Read(ref _active);

    public IEnumerable<ClientContext> All => _clients.Values;

    public ClientContext? Current => _slot.Value;

    public string CurrentId => Current?.Id ?? "c0";

    public string CurrentCwd => Current?.Cwd ?? Environment.CurrentDirectory;

    public IReadOnlyList<string> CurrentRoots => Current?.Roots ?? Array.Empty<string>();

    public void Enter(ClientContext client)
    {
        if (_clients.TryAdd(client.Id, client))
        {
            Interlocked.Increment(ref _active);
        }
    }

    public void Leave(string clientId)
    {
        if (_clients.TryRemove(clientId, out _))
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public string? FindCwd(string clientId) =>
        _clients.TryGetValue(clientId, out ClientContext? client) ? client.Cwd : null;

    /// <summary>在当前异步流内切换客户端；Dispose 恢复上一层。</summary>
    public IDisposable Use(ClientContext context)
    {
        ClientContext? previous = _slot.Value;
        _slot.Value = context;
        return new Scope(this, previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly ClientSessionRegistry _owner;
        private readonly ClientContext? _previous;

        public Scope(ClientSessionRegistry owner, ClientContext? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose() => _owner._slot.Value = _previous;
    }
}
