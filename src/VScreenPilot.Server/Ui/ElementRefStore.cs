using System.Collections.Concurrent;
using VScreenPilot.Apps;
using VScreenPilot.Uia;
using VScreenPilot.Platform.Windows;

namespace VScreenPilot.Ui;

/// <summary>
/// 元素引用与快照代际的唯一持有者。
/// 锁对象通过 <see cref="SyncRoot"/> 暴露：快照构建需要在“推进代际 + 建树”期间持有同一把锁，
/// 用同一对象保证与原实现完全相同的锁语义（Monitor 可重入）。
/// </summary>
internal sealed class ElementRefStore
{
    public const int Capacity = 2048;
    private const long TtlMs = 5 * 60 * 1000;

    /// <summary>TTL 全量扫描降频：Register 是快照构建的热点，没必要每次 O(n) 扫 2048 项。</summary>
    private const long PruneIntervalMs = 30_000;

    private readonly Dictionary<string, ElementRefEntry> _elements = new();
    private readonly Dictionary<string, string> _keys = new();
    private long _lastPruneTicks;

    // 这两个字典会在不持锁的路径被读取（SnapshotId），写入发生在锁内。
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _snapshotIds = new(StringComparer.Ordinal);

    private long _sequence;
    private long _snapshotSequence;

    /// <summary>快照构建与 ref 注册共用的锁；调用方可以 lock 它来保证多步操作原子。</summary>
    public object SyncRoot { get; } = new();

    public string CurrentSnapshotId(string clientId) =>
        _snapshotIds.TryGetValue(clientId, out string? id) ? id : string.Empty;

    /// <summary>若该客户端还没有快照则创建第一个；否则复用当前代。</summary>
    public string EnsureSnapshot(string clientId)
    {
        lock (SyncRoot)
        {
            if (_generations.TryGetValue(clientId, out long generation) &&
                generation > 0 &&
                _snapshotIds.TryGetValue(clientId, out string? id))
            {
                return id;
            }

            return BeginSnapshot(clientId);
        }
    }

    /// <summary>推进代际并清掉更早一代的 ref（保留上一代用于 E_STALE_SNAPSHOT 报错）。</summary>
    public string BeginSnapshot(string clientId)
    {
        lock (SyncRoot)
        {
            long generation = _generations.TryGetValue(clientId, out long current)
                ? current + 1
                : 1;
            _generations[clientId] = generation;

            List<string>? expired = null;

            foreach ((string key, ElementRefEntry value) in _elements)
            {
                if (string.Equals(value.OwnerClientId, clientId, StringComparison.Ordinal) &&
                    value.Generation < generation - 1)
                {
                    (expired ??= new List<string>()).Add(key);
                }
            }

            if (expired is not null)
            {
                foreach (string key in expired)
                {
                    RemoveLocked(key);
                }
            }

            string snapshotId = "s" + Interlocked.Increment(ref _snapshotSequence);
            _snapshotIds[clientId] = snapshotId;
            return snapshotId;
        }
    }

    /// <summary>清空指定客户端的快照代际；clientId 为空表示全量（daemon 退出）。</summary>
    public void ClearSnapshots(string? clientId)
    {
        if (clientId is not null)
        {
            _generations.TryRemove(clientId, out _);
            _snapshotIds.TryRemove(clientId, out _);
            return;
        }

        foreach (string key in _generations.Keys)
        {
            _generations.TryRemove(key, out _);
        }

        foreach (string key in _snapshotIds.Keys)
        {
            _snapshotIds.TryRemove(key, out _);
        }
    }

    /// <summary>登记元素并返回稳定的会话内引用；同键同代复用。</summary>
    public string Register(string clientId, UiaElementInfo info, IntPtr hwnd)
    {
        lock (SyncRoot)
        {
            PruneExpiredLocked();

            string key = ElementRefEntry.BuildKey(clientId, info, hwnd);
            long generation = _generations.TryGetValue(clientId, out long current) ? current : 0;

            if (_keys.TryGetValue(key, out string? existingRef) &&
                _elements.TryGetValue(existingRef, out ElementRefEntry? existing) &&
                existing.Generation == generation)
            {
                existing.Update(info, hwnd);
                return existing.Ref;
            }

            if (_elements.Count >= Capacity)
            {
                EvictOldestLocked();
            }

            string reference = "e" + (++_sequence);
            // UWP 等跨进程宿主：元素属于内容进程，HWND 属于 ApplicationFrameHost。
            // ref 的归属校验必须按 HWND 的进程，否则每次解析都会被判成“句柄复用”。
            int ownerPid = hwnd != IntPtr.Zero ? WindowManager.GetProcessId(hwnd) : 0;
            var entry = new ElementRefEntry
            {
                Ref = reference,
                Key = key,
                OwnerClientId = clientId,
                Pid = ownerPid > 0 ? ownerPid : info.ProcessId,
                Hwnd = hwnd,
                Generation = generation,
                LastUsedTicks = Environment.TickCount64,
            };
            entry.Update(info, hwnd);

            _elements[reference] = entry;
            _keys[key] = reference;
            return reference;
        }
    }

    /// <summary>
    /// 解析 ref：校验归属、TTL、代际；跨代用 <paramref name="stale"/> 区分
    /// “旧快照”（E_STALE_SNAPSHOT）与“未知/淘汰”（E_ELEMENT_STALE）。
    /// </summary>
    public ElementRefEntry? TryResolve(string clientId, string reference, out bool stale)
    {
        stale = false;

        lock (SyncRoot)
        {
            long generation = _generations.TryGetValue(clientId, out long current) ? current : 0;

            if (!_elements.TryGetValue(reference, out ElementRefEntry? entry))
            {
                return null;
            }

            if (!string.Equals(entry.OwnerClientId, clientId, StringComparison.Ordinal))
            {
                // 其他客户端的 ref 对当前客户端等同未知，避免跨会话读取元素。
                return null;
            }

            if (Environment.TickCount64 - entry.LastUsedTicks > TtlMs)
            {
                RemoveLocked(reference);
                return null;
            }

            if (generation > 0 && entry.Generation != generation)
            {
                stale = true;
                return null;
            }

            entry.LastUsedTicks = Environment.TickCount64;
            return entry;
        }
    }

    public void DropClient(string clientId)
    {
        lock (SyncRoot)
        {
            List<string>? keys = null;

            foreach ((string refKey, ElementRefEntry entry) in _elements)
            {
                if (string.Equals(entry.OwnerClientId, clientId, StringComparison.Ordinal))
                {
                    (keys ??= new List<string>()).Add(refKey);
                }
            }

            if (keys is not null)
            {
                foreach (string key in keys)
                {
                    RemoveLocked(key);
                }
            }

            ClearSnapshots(clientId);
        }
    }

    public void Clear()
    {
        lock (SyncRoot)
        {
            _elements.Clear();
            _keys.Clear();
            ClearSnapshots(clientId: null);
        }
    }


    // ---- release/Dispose 专用：调用方已持有会话级互斥（_gate + 读写锁），不得再取本锁 ----

    /// <summary>不加锁清理指定客户端的 ref 与代际；仅 release 路径使用。</summary>
    public void DropClientUnsafe(string clientId)
    {
        List<string>? keys = null;

        foreach ((string refKey, ElementRefEntry entry) in _elements)
        {
            if (string.Equals(entry.OwnerClientId, clientId, StringComparison.Ordinal))
            {
                (keys ??= new List<string>()).Add(refKey);
            }
        }

        if (keys is not null)
        {
            foreach (string key in keys)
            {
                RemoveLocked(key);
            }
        }

        ClearSnapshots(clientId);
    }

    /// <summary>不加锁清空全部 ref 与代际；仅 Dispose/release(forceAllClients) 使用。</summary>
    public void ClearUnsafe()
    {
        _elements.Clear();
        _keys.Clear();
        ClearSnapshots(clientId: null);
    }

    private void PruneExpiredLocked()
    {
        long now = Environment.TickCount64;

        if (now - _lastPruneTicks < PruneIntervalMs)
        {
            return;
        }

        _lastPruneTicks = now;
        List<string>? expired = null;

        foreach ((string key, ElementRefEntry entry) in _elements)
        {
            if (now - entry.LastUsedTicks > TtlMs)
            {
                (expired ??= new List<string>()).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (string reference in expired)
        {
            RemoveLocked(reference);
        }
    }

    private void EvictOldestLocked()
    {
        string? oldestRef = null;
        long oldestTicks = long.MaxValue;

        foreach ((string key, ElementRefEntry entry) in _elements)
        {
            if (entry.LastUsedTicks < oldestTicks)
            {
                oldestTicks = entry.LastUsedTicks;
                oldestRef = key;
            }
        }

        if (oldestRef is not null)
        {
            RemoveLocked(oldestRef);
        }
    }

    private void RemoveLocked(string reference)
    {
        if (!_elements.Remove(reference, out ElementRefEntry? entry))
        {
            return;
        }

        if (_keys.TryGetValue(entry.Key, out string? mapped) && mapped == reference)
        {
            _keys.Remove(entry.Key);
        }
        else
        {
            foreach ((string key, string value) in _keys)
            {
                if (value == reference)
                {
                    _keys.Remove(key);
                    break;
                }
            }
        }
    }
}
