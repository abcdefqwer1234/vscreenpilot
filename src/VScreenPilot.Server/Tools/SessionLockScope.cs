using System.Text.Json.Nodes;
using VScreenPilot.Services;

namespace VScreenPilot.Tools;

/// <summary>
/// 会话读/写锁的获取策略：<b>写锁优先</b>。
/// <c>release_environment</c> 同时声明 <c>sessionRead</c> 与 <c>sessionWrite</c>，
/// 旧实现先判读锁，导致独占分支永远执行不到、release 可以和 snapshot/capture 并发。
/// 读锁也带超时：release 持写锁期间不能让所有读工具无限阻塞在 <c>EnterReadLock</c> 上。
/// </summary>
internal static class SessionLockScope
{
    /// <summary>与既有 E_BUSY 提示一致：release 最多等只读观察 3s。</summary>
    internal const int ExclusiveWaitMs = 3000;

    /// <summary>读工具最多等 release 结束 10s，避免和外层 30s 工具超时赛跑。</summary>
    internal const int ReadWaitMs = 10_000;

    internal sealed record Acquisition(IDisposable? Scope, ToolResponse? Busy);

    internal static Acquisition Acquire(ISessionGate gate, bool sessionRead, bool sessionWrite)
    {
        if (sessionWrite)
        {
            return gate.TryEnterExclusive(ExclusiveWaitMs)
                ? new Acquisition(new ExclusiveScope(gate), null)
                : new Acquisition(
                    null,
                    Busy(
                        "release_environment 等待只读观察结束超时（3s）",
                        "snapshot/capture/find_element",
                        ExclusiveWaitMs,
                        "稍后重试；等当前 snapshot/capture 返回后再 release"));
        }

        if (!sessionRead)
        {
            return new Acquisition(null, null);
        }

        return gate.TryEnterRead(ReadWaitMs)
            ? new Acquisition(new ReadScope(gate), null)
            : new Acquisition(
                null,
                Busy(
                    "release_environment 正在清理环境，等待会话锁超时（10s）",
                    "release_environment",
                    ReadWaitMs,
                    "稍后重试；等 release_environment 返回后再调用"));
    }

    private static ToolResponse Busy(string message, string holder, int waitedMs, string hint) =>
        ToolResponse.Error(
            "E_BUSY",
            message,
            new JsonObject
            {
                ["held_by"] = holder,
                ["retry_after_ms"] = 500,
                ["waited_ms"] = waitedMs,
            },
            retryable: true,
            hint: hint,
            stage: "session");

    private sealed class ReadScope(ISessionGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.ExitRead();
            }
        }
    }

    private sealed class ExclusiveScope(ISessionGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.ExitExclusive();
            }
        }
    }
}
