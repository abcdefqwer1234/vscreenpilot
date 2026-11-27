using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using VScreenPilot.Services;

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
    public bool ReadOnly { get; init; }
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

    public ClientContext Resolve(ClientInfo client)
    {
        // 已登记的实例优先：epoch / allow_script / app_allowlist 需要跨调用保持。
        if (_clients.TryGetValue(client.Id, out ClientContext? registered))
        {
            return registered;
        }

        // 握手里带了完整上下文就直接用；否则按 ClientInfo 兜底（ctl / 单进程）。
        // 这里不登记：登记只由 Enter 负责，避免 Release 的重试路径把已断开的客户端加回来。
        return client.Context ?? new ClientContext
        {
            Id = client.Id,
            Name = client.Name,
            Cwd = client.Cwd ?? Environment.CurrentDirectory,
            Roots = client.Roots,
            IncludeAllApps = client.IncludeAllApps,
        };
    }

    public string? FindCwd(string clientId) =>
        _clients.TryGetValue(clientId, out ClientContext? client) ? client.Cwd : null;

    /// <summary>同一 cwd/roots 的客户端得到相同短哈希，用于崩溃恢复的归属提示。</summary>
    public static string ComputeClientKey(string cwd, IReadOnlyList<string> roots)
    {
        // Windows 路径大小写不敏感，且客户端可能带/不带末尾分隔符；
        // 先规范化为绝对路径再统一大小写，否则同一目录会被算成不同 key。
        string material = NormalizePath(cwd) + "|" + string.Join(
            "|",
            roots
                .Select(NormalizePath)
                .Where(item => item.Length > 0)
                .OrderBy(item => item, StringComparer.Ordinal));

        return ShortHash(material);
    }

    /// <summary>0.4.0 开发构建使用过的未规范化算法；只用于兼容旧 state 的自动认领。</summary>
    public static string ComputeLegacyClientKey(string cwd, IReadOnlyList<string> roots)
    {
        string material = (cwd ?? string.Empty) + "|" + string.Join("|", roots ?? Array.Empty<string>());
        return ShortHash(material);
    }

    /// <summary>新算法 + 两个旧算法变体（保留 roots 顺序 / 排序），用于读取旧 state。</summary>
    public static IReadOnlyList<string> ComputeClientKeyCandidates(
        string cwd, IReadOnlyList<string> roots)
    {
        IReadOnlyList<string> safeRoots = roots ?? Array.Empty<string>();
        string[] ordered = safeRoots
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        string[] sorted = ordered.OrderBy(item => item, StringComparer.Ordinal).ToArray();

        var keys = new List<string>
        {
            ComputeClientKey(cwd, safeRoots),
            ShortHash((cwd ?? string.Empty) + "|" + string.Join("|", ordered)),
            ShortHash((cwd ?? string.Empty) + "|" + string.Join("|", sorted)),
        };

        return keys
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ShortHash(string material)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
    }

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
