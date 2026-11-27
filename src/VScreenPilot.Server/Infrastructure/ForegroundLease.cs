using System.Text.Json.Nodes;
using VScreenPilot.Errors;
using VScreenPilot.Views;

namespace VScreenPilot.Server.Infrastructure;

/// <summary>
/// 会话级输入/前台租约：一次申请后，FocusGuard 在 TTL 内不归还前台；
/// 输入工具也不在每次调用结束时恢复焦点。租约按客户端隔离，过期自动失效。
/// </summary>
internal sealed class ForegroundLease
{
    internal const int DefaultTtlMs = 30_000;
    internal const int MaxTtlMs = 600_000;

    /// <summary>被抢占/延迟的待归还前台最多保留 2 分钟，避免句柄复用后恢复错窗口。</summary>
    internal const int PendingRestoreTtlMs = 120_000;

    private sealed record Entry(
        string LeaseId,
        int Pid,
        IntPtr Hwnd,
        IntPtr PreviousForeground,
        long ExpiresAtTicks,
        bool Exclusive,
        string Source,
        long Sequence);

    private sealed record PendingRestore(IntPtr Hwnd, long ExpiresAtTicks, long Sequence);

    private readonly Func<long> _now;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _leases = new(StringComparer.Ordinal);

    // force 抢占会移除对方 entry；保留其 PreviousForeground，等对方下次 release/drop 时归还。
    private readonly Dictionary<string, PendingRestore> _preemptedPrevious = new(StringComparer.Ordinal);

    // 仍有其他租约时不能抢前台，先把归还目标延迟到这里；最后一个租约结束时再取。
    private readonly List<PendingRestore> _deferredPrevious = new();
    private long _sequence;

    public ForegroundLease(Func<long>? now = null)
    {
        _now = now ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// 申请或续期租约。同一客户端重复申请会更新 pid/hwnd/TTL；
    /// 其他客户端持有不同目标的活跃租约时返回 E_INPUT_LEASE_CONFLICT。
    /// </summary>
    public InputLeaseView Acquire(
        string clientId, int pid, long hwnd, int ttlMs, bool exclusive, string source,
        IntPtr previousForeground, bool force = false)
    {
        int ttl = Math.Clamp(ttlMs <= 0 ? DefaultTtlMs : ttlMs, 1000, MaxTtlMs);
        long now = _now();
        long expires = now + ttl;

        lock (_gate)
        {
            PruneExpiredLocked(now);

            List<string>? preempted = null;

            foreach ((string otherClient, Entry other) in _leases)
            {
                if (!string.Equals(otherClient, clientId, StringComparison.Ordinal) &&
                    other.Pid != pid)
                {
                    if (!force)
                    {
                        throw new ToolException(
                            "E_INPUT_LEASE_CONFLICT",
                            "另一个客户端持有不同目标的输入租约",
                            new JsonObject
                            {
                                ["holder_client_id"] = otherClient,
                                ["holder_pid"] = other.Pid,
                                ["holder_hwnd"] = other.Hwnd.ToInt64(),
                            },
                            retryable: true,
                            hint: "稍后重试；或传 force=true 抢占（会中断对方的前台租约）",
                            stage: "lease");
                    }

                    if (other.PreviousForeground != IntPtr.Zero)
                    {
                        _preemptedPrevious[otherClient] = new PendingRestore(
                            other.PreviousForeground,
                            now + PendingRestoreTtlMs,
                            ++_sequence);
                    }

                    (preempted ??= new List<string>()).Add(otherClient);
                }
            }

            if (preempted is not null)
            {
                foreach (string otherClient in preempted)
                {
                    _leases.Remove(otherClient);
                }
            }

            // 重新申请租约说明该客户端已回到受控状态，丢弃旧的待归还目标。
            _preemptedPrevious.Remove(clientId);

            if (_leases.TryGetValue(clientId, out Entry? existing) &&
                existing.Pid == pid &&
                existing.Hwnd == new IntPtr(hwnd))
            {
                Entry renewed = existing with
                {
                    ExpiresAtTicks = expires,
                    Exclusive = exclusive,
                    Source = source,
                };
                _leases[clientId] = renewed;
                return ToView(renewed, active: true);
            }

            long sequence = Interlocked.Increment(ref _sequence);
            var entry = new Entry(
                "L" + sequence,
                pid,
                new IntPtr(hwnd),
                previousForeground,
                expires,
                exclusive,
                source,
                sequence);
            _leases[clientId] = entry;
            return ToView(entry, active: true);
        }
    }

    public InputLeaseView? Renew(string clientId, int ttlMs, bool? exclusive = null)
    {
        int ttl = Math.Clamp(ttlMs <= 0 ? DefaultTtlMs : ttlMs, 1000, MaxTtlMs);

        lock (_gate)
        {
            long now = _now();
            PruneExpiredLocked(now);

            if (!_leases.TryGetValue(clientId, out Entry? entry))
            {
                return null;
            }

            entry = entry with
            {
                ExpiresAtTicks = now + ttl,
                Exclusive = exclusive ?? entry.Exclusive,
            };
            _leases[clientId] = entry;
            return ToView(entry, active: true);
        }
    }

    public (InputLeaseView? View, IntPtr PreviousForeground) Release(string clientId)
    {
        lock (_gate)
        {
            PruneExpiredLocked(_now());

            if (!_leases.Remove(clientId, out Entry? entry))
            {
                IntPtr pending = TakePreemptedLocked(clientId);
                return (null, pending);
            }

            return (ToView(entry, active: false), entry.PreviousForeground);
        }
    }

    /// <summary>仍有其他租约时暂存归还目标；等最后一个租约结束时由 InputRouter 统一恢复。</summary>
    public void DeferRestore(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        lock (_gate)
        {
            long now = _now();
            PruneExpiredLocked(now);
            _deferredPrevious.Add(new PendingRestore(
                hwnd,
                now + PendingRestoreTtlMs,
                Interlocked.Increment(ref _sequence)));
        }
    }

    /// <summary>
    /// 取最后一个租约结束时应归还的前台。优先使用当前租约的 previous；
    /// 没有时再取最早暂存的待归还目标。取走后清空暂存，避免重复恢复。
    /// </summary>
    public IntPtr TakeRestoreTarget(IntPtr fallback)
    {
        lock (_gate)
        {
            long now = _now();
            PruneExpiredLocked(now);

            PendingRestore? best = _preemptedPrevious.Values
                .Concat(_deferredPrevious)
                .Where(item => item.Hwnd != IntPtr.Zero)
                .OrderBy(item => item.Sequence)
                .FirstOrDefault();

            IntPtr target = fallback != IntPtr.Zero
                ? fallback
                : best?.Hwnd ?? IntPtr.Zero;

            _preemptedPrevious.Clear();
            _deferredPrevious.Clear();
            return target;
        }
    }

    public InputLeaseView? Status(string clientId)
    {
        lock (_gate)
        {
            PruneExpiredLocked(_now());
            return _leases.TryGetValue(clientId, out Entry? entry)
                ? ToView(entry, active: true)
                : null;
        }
    }

    public bool IsActive(string clientId)
    {
        lock (_gate)
        {
            PruneExpiredLocked(_now());
            return _leases.ContainsKey(clientId);
        }
    }

    public bool IsActiveForPid(int pid)
    {
        lock (_gate)
        {
            PruneExpiredLocked(_now());
            return _leases.Values.Any(entry => entry.Pid == pid);
        }
    }

    /// <summary>是否有任意客户端持有租约；后台线程（FocusGuard/CursorGuard）用。</summary>
    public bool IsAnyActive()
    {
        lock (_gate)
        {
            PruneExpiredLocked(_now());
            return _leases.Count > 0;
        }
    }

    /// <summary>全量清理（release_environment forceAllClients/daemon 退出）；返回应归还的前台窗口。</summary>
    public IntPtr ClearAll()
    {
        lock (_gate)
        {
            long now = _now();
            PruneExpiredLocked(now);

            PendingRestore? best = _leases.Values
                .Select(entry => new PendingRestore(
                    entry.PreviousForeground, entry.ExpiresAtTicks, entry.Sequence))
                .Concat(_preemptedPrevious.Values)
                .Concat(_deferredPrevious)
                .Where(item => item.Hwnd != IntPtr.Zero)
                .OrderBy(item => item.Sequence)
                .FirstOrDefault();

            _leases.Clear();
            _preemptedPrevious.Clear();
            _deferredPrevious.Clear();
            return best?.Hwnd ?? IntPtr.Zero;
        }
    }

    /// <summary>客户端断开时清理；返回被清理租约的归还目标。</summary>
    public IntPtr OnClientDrop(string clientId)
    {
        lock (_gate)
        {
            PruneExpiredLocked(_now());

            if (!_leases.Remove(clientId, out Entry? entry))
            {
                return TakePreemptedLocked(clientId);
            }

            return entry.PreviousForeground;
        }
    }

    private IntPtr TakePreemptedLocked(string clientId)
    {
        if (_preemptedPrevious.Remove(clientId, out PendingRestore? pending) &&
            pending.ExpiresAtTicks > _now())
        {
            return pending.Hwnd;
        }

        return IntPtr.Zero;
    }

    private void PruneExpiredLocked(long now)
    {
        List<string>? expired = null;

        foreach ((string clientId, Entry entry) in _leases)
        {
            if (entry.ExpiresAtTicks <= now)
            {
                (expired ??= new List<string>()).Add(clientId);
            }
        }

        if (expired is not null)
        {
            foreach (string clientId in expired)
            {
                _leases.Remove(clientId);
            }
        }

        List<string>? expiredPending = null;

        foreach ((string clientId, PendingRestore pending) in _preemptedPrevious)
        {
            if (pending.ExpiresAtTicks <= now)
            {
                (expiredPending ??= new List<string>()).Add(clientId);
            }
        }

        if (expiredPending is not null)
        {
            foreach (string clientId in expiredPending)
            {
                _preemptedPrevious.Remove(clientId);
            }
        }

        _deferredPrevious.RemoveAll(item => item.ExpiresAtTicks <= now);
    }

    private InputLeaseView ToView(Entry entry, bool active) => new(
        entry.LeaseId,
        entry.Pid,
        entry.Hwnd.ToInt64(),
        Math.Max(0, entry.ExpiresAtTicks - _now()),
        active,
        entry.Exclusive,
        entry.Source);
}
