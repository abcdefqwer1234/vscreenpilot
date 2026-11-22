using System.Text.Json.Nodes;
using VScreenPilot.Errors;

namespace VScreenPilot.Server.Infrastructure;

/// <summary>
/// 进程内屏幕租约：同一时刻只允许一个客户端执行 GUI/视觉操作。
/// 持有者空闲超过 TTL 后允许其他客户端接管；release_environment 显式释放。
/// </summary>
internal sealed class ScreenLease
{
    private readonly object _gate = new();
    private readonly long _idleTtlMs;
    private readonly long _waitMs;
    private string? _holderId;
    private string? _holderName;
    private long _acquiredTicks;
    private long _lastActivityTicks;
    private long _pinnedUntilTicks;
    private int _activeCalls;
    private long _generation;

    /// <summary>由 daemon 注入，用于记录 TTL 抢占。</summary>
    /// <summary>租约日志出口。实例级：由组合根注入。</summary>
    public Action<string>? LogSink { get; set; }

    /// <summary>由 daemon 注入，用于在 E_SCREEN_LEASED 中给出 holder 的 cwd。</summary>
    /// <summary>按 client id 查 cwd；用于 E_SCREEN_LEASED 的 holder_cwd。</summary>
    public Func<string, string?>? ClientCwdProvider { get; set; }

    public ScreenLease() : this(ResolveTtl(), ResolveWait())
    {
    }

    internal ScreenLease(long idleTtlMs, long waitMs)
    {
        _idleTtlMs = Math.Clamp(idleTtlMs, 1000, 600_000);
        _waitMs = Math.Clamp(waitMs, 0, 120_000);
    }

    public IDisposable Acquire(string clientId, string tool)
    {
        long started = Environment.TickCount64;
        long deadline = started + _waitMs;

        while (true)
        {
            lock (_gate)
            {
                long now = Environment.TickCount64;
                bool idleExpired = _activeCalls == 0 &&
                                   _holderId is not null &&
                                   now - _lastActivityTicks > _idleTtlMs &&
                                   now >= _pinnedUntilTicks;

                if (_holderId is null || _holderId == clientId || idleExpired)
                {
                    if (_holderId != clientId)
                    {
                        string? previous = _holderId;
                        long previousHeld = previous is null ? 0 : Math.Max(0, now - _acquiredTicks);

                        _holderId = clientId;
                        _holderName = tool;
                        _acquiredTicks = now;
                        _pinnedUntilTicks = 0;
                        _generation++;

                        if (idleExpired && previous is not null)
                        {
                            LogSink?.Invoke(
                                $"screen_lease_stolen previous={previous} client={clientId} " +
                                $"held_ms={previousHeld}");
                        }
                    }

                    _activeCalls++;
                    _lastActivityTicks = now;
                    return new Lease(this, clientId, _generation);
                }

                long remaining = deadline - now;

                if (remaining <= 0)
                {
                    throw new ToolException(
                        "E_SCREEN_LEASED",
                        $"另一个客户端正在使用虚拟屏（holder={_holderId}, tool={_holderName}）",
                        new JsonObject
                        {
                            ["holder_client_id"] = _holderId,
                            ["holder_tool"] = _holderName,
                            ["holder_cwd"] = _holderId is null
                                ? null
                                : ClientCwdProvider?.Invoke(_holderId),
                            ["held_ms"] = Math.Max(0, now - _acquiredTicks),
                            ["waited_ms"] = Math.Max(0, now - started),
                            ["retry_after_ms"] = 1000,
                        },
                        retryable: true,
                        hint: $"等待对方 release_environment 或空闲 {_idleTtlMs / 1000}s 后重试",
                        stage: "lease");
                }

                // 100ms 轮询一次 TTL；holder 显式 release 时会被 Pulse 立即唤醒。
                Monitor.Wait(_gate, (int)Math.Min(100, remaining));
            }
        }
    }

    /// <summary>
    /// 显式归还：忽略在途计数，只要 holder 是自己就清空。
    /// release_environment 的工具调用本身持有一个租约，不能用
    /// <see cref="ReleaseHolderIfIdle"/>（它要求 active_calls==0）。
    /// </summary>
    public bool ForceRelease(string clientId)
    {
        lock (_gate)
        {
            if (!string.Equals(_holderId, clientId, StringComparison.Ordinal))
            {
                return false;
            }

            _holderId = null;
            _holderName = null;
            _acquiredTicks = 0;
            _pinnedUntilTicks = 0;
            _activeCalls = 0;
            _generation++;
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    /// <summary>
    /// 释放租约并返回“当前是否已经无人持有”。客户端断开清理用它判断能否安全关闭自己的窗口：
    /// 另一个客户端正在操作（或自己的 handler 还没结束）时返回 false，窗口留到 daemon 退出。
    /// </summary>
    public bool ReleaseHolderIfIdle(string clientId)
    {
        lock (_gate)
        {
            if (string.Equals(_holderId, clientId, StringComparison.Ordinal) && _activeCalls == 0)
            {
                _holderId = null;
                _holderName = null;
                _acquiredTicks = 0;
                _pinnedUntilTicks = 0;
                _generation++;
                Monitor.PulseAll(_gate);
            }

            return _holderId is null;
        }
    }

    /// <summary>keep_foreground 等场景延长租约，避免在用户输入期间被 TTL 抢走。</summary>
    public bool Extend(string clientId, int durationMs)
    {
        lock (_gate)
        {
            if (!string.Equals(_holderId, clientId, StringComparison.Ordinal))
            {
                return false;
            }

            long until = Environment.TickCount64 + Math.Clamp(durationMs, 1000, 600_000);
            _pinnedUntilTicks = Math.Max(_pinnedUntilTicks, until);
            _lastActivityTicks = Environment.TickCount64;
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    public string? HolderId
    {
        get
        {
            lock (_gate)
            {
                return _holderId;
            }
        }
    }

    public JsonObject Describe()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            long expiry = Math.Max(_lastActivityTicks + _idleTtlMs, _pinnedUntilTicks);

            return new JsonObject
            {
                ["held"] = _holderId is not null,
                ["holder_client_id"] = _holderId,
                ["holder_tool"] = _holderName,
                ["held_ms"] = _holderId is null ? 0 : Math.Max(0, now - _acquiredTicks),
                ["expires_in_ms"] = _holderId is null ? 0 : Math.Max(0, expiry - now),
                ["active_calls"] = _activeCalls,
                ["wait_ms"] = _waitMs,
                ["pinned_until_ms"] = _pinnedUntilTicks == 0
                    ? 0
                    : Math.Max(0, _pinnedUntilTicks - now),
            };
        }
    }

    private void Release(string clientId, long generation)
    {
        lock (_gate)
        {
            // ForceRelease/抢占会递增 generation 并清空 active_calls；
            // 旧 Lease 的迟到 Dispose 不能影响新持有者。
            if (!string.Equals(_holderId, clientId, StringComparison.Ordinal) ||
                generation != _generation)
            {
                return;
            }

            if (_activeCalls > 0)
            {
                _activeCalls--;
            }

            _lastActivityTicks = Environment.TickCount64;
            Monitor.PulseAll(_gate);
        }
    }

    private static long ResolveTtl()
    {
        string? raw = Environment.GetEnvironmentVariable("VSP_SCREEN_LEASE_IDLE_MS");

        if (long.TryParse(raw, out long value))
        {
            return Math.Clamp(value, 1000, 600_000);
        }

        return 60_000;
    }

    private static long ResolveWait()
    {
        string? raw = Environment.GetEnvironmentVariable("VSP_SCREEN_LEASE_WAIT_MS");

        if (long.TryParse(raw, out long value))
        {
            return Math.Clamp(value, 0, 120_000);
        }

        return 0;
    }

    private sealed class Lease : IDisposable
    {
        private readonly ScreenLease _owner;
        private readonly string _clientId;
        private readonly long _generation;
        private int _disposed;

        public Lease(ScreenLease owner, string clientId, long generation)
        {
            _owner = owner;
            _clientId = clientId;
            _generation = generation;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(_clientId, _generation);
            }
        }
    }
}
