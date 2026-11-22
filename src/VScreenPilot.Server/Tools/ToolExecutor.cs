using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Services;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Errors;
using VScreenPilot.Json;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Tools;

/// <summary>
/// 一次工具调用的上下文：会话、客户端与请求级参数。处理器仍绑定在
/// <see cref="McpTransport"/> 实例上，执行器只依赖本记录，不再持有会话字段。
/// </summary>
internal sealed record ToolContext(
    IExecutionControl Control,
    ClientInfo? Client,
    JsonElement Arguments,
    string RequestId,
    string? ArgumentSummary,
    CancellationToken CancellationToken);

/// <summary>
/// 工具执行器：租约、写调度、超时/取消、epoch 校验与 trace 收口。
/// 只读取 <see cref="ToolDescriptor.Policy"/>，不维护任何“哪些工具会写”的清单。
/// </summary>
internal sealed class ToolExecutor
{
    private readonly LogRing _log;
    private readonly WriteScheduler _scheduler;
    private readonly Action<string, string, string?, double, ToolResponse> _trace;
    private readonly Action<int, int, string>? _progress;
    private readonly Action _handlerStarted;
    private readonly Action _handlerFinished;

    public ToolExecutor(
        LogRing log,
        WriteScheduler scheduler,
        Action<string, string, string?, double, ToolResponse> trace,
        Action<int, int, string>? progress,
        Action handlerStarted,
        Action handlerFinished)
    {
        _log = log;
        _scheduler = scheduler;
        _trace = trace;
        _progress = progress;
        _handlerStarted = handlerStarted;
        _handlerFinished = handlerFinished;
    }

    private sealed record ToolCallOutcome(
        ToolDescriptor Tool, ToolContext Context, ToolPolicy Policy, long Epoch);

    private sealed record TimeoutOutcome(
        ToolResponse Response,
        WriteScheduler.Lease? PendingLease,
        IDisposable? PendingScreenLease,
        bool CtsTransferred);

    public async Task<ToolResponse> ExecuteAsync(ToolDescriptor tool, ToolContext context)
    {
        using IDisposable? clientScope = context.Client is null
            ? null
            : context.Control.UseClient(context.Client);
        long epoch = context.Control.Epoch;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var state = new ToolInvocationState();
        ToolPolicy policy = tool.Policy;
        bool releaseSucceeded = false;

        try
        {
            state.HandlerTask = await StartInvocationAsync(tool, context, policy, state).ConfigureAwait(false);

            ToolResponse response = await AwaitHandlerAsync(tool, context, state).ConfigureAwait(false);

            return CompleteResponse(
                new ToolCallOutcome(tool, context, policy, epoch),
                response,
                stopwatch,
                out releaseSucceeded);
        }
        catch (OperationCanceledException)
        {
            ToolCancelPolicy.CancelOutcome outcome = ToolCancelPolicy.BuildCancelOutcome(
                state.HandlerTask, state.Lease, state.ScreenLease, state.CallCts);
            state.Apply(outcome.PendingLease, outcome.PendingScreenLease, outcome.CtsTransferred);
            throw;
        }
        catch (ToolException exception)
        {
            return FailWithToolException(tool, context, stopwatch, exception);
        }
        catch (Exception exception)
        {
            return FailUnexpected(tool, context, stopwatch, exception);
        }
        finally
        {
            CleanupInvocation(context, policy, state, releaseSucceeded);
        }
    }

    private async Task<Task<ToolResponse>> StartInvocationAsync(
        ToolDescriptor tool, ToolContext context, ToolPolicy policy, ToolInvocationState state)
    {
        (state.Lease, state.ScreenLease, state.CallCts) =
            await BeginInvocation(tool, context, policy).ConfigureAwait(false);

        // 长任务至少给一次“已开始/已完成”进度，客户端不会以为调用挂死。
        _progress?.Invoke(0, 1, "started");

        Task<ToolResponse> handlerTask = StartHandlerTask(tool, context, policy, _progress);

        _handlerStarted();
        _ = handlerTask.ContinueWith(
            _ => _handlerFinished(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return handlerTask;
    }

    private async Task<ToolResponse> AwaitHandlerAsync(
        ToolDescriptor tool, ToolContext context, ToolInvocationState state)
    {
        try
        {
            return await state.HandlerTask!
                .WaitAsync(TimeSpan.FromMilliseconds(tool.TimeoutMs), context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TimeoutOutcome outcome = BuildTimeoutOutcome(
                tool, state.HandlerTask!, state.Lease, state.ScreenLease, state.CallCts);
            state.Apply(outcome.PendingLease, outcome.PendingScreenLease, outcome.CtsTransferred);
            return outcome.Response;
        }
    }

    private ToolResponse FailWithToolException(
        ToolDescriptor tool, ToolContext context, System.Diagnostics.Stopwatch stopwatch, ToolException exception)
    {
        var response = ToolResponse.FromException(exception).SetRequestId(context.RequestId);
        _trace(tool.Name, context.RequestId, context.ArgumentSummary, stopwatch.Elapsed.TotalMilliseconds, response);
        return response;
    }

    private ToolResponse FailUnexpected(
        ToolDescriptor tool, ToolContext context, System.Diagnostics.Stopwatch stopwatch, Exception exception)
    {
        _log.Error($"工具 {tool.Name} 失败", exception);
        var response = ToolResponse.FromException(exception, $"tool:{tool.Name}")
            .SetRequestId(context.RequestId);
        _trace(tool.Name, context.RequestId, context.ArgumentSummary, stopwatch.Elapsed.TotalMilliseconds, response);
        return response;
    }

    private void CleanupInvocation(
        ToolContext context, ToolPolicy policy, ToolInvocationState state, bool releaseSucceeded)
    {
        context.Control.SetCancellation(null);

        // 超时的 CTS 所有权已经交给仍在运行的 handler。
        if (!state.CtsTransferred)
        {
            state.CallCts?.Dispose();
        }

        ReleaseClientLease(context, policy, state.HandlerTask, releaseSucceeded);

        // 释放顺序与获取相反：先写锁，后屏幕租约。
        state.Lease?.Dispose();
        state.ScreenLease?.Dispose();
    }

    private static void ReleaseClientLease(
        ToolContext context, ToolPolicy policy, Task<ToolResponse>? handlerTask, bool releaseSucceeded)
    {
        if (!policy.SessionWrite)
        {
            return;
        }

        string releaseClientId = context.Client?.Id ?? "c0";

        if (releaseSucceeded)
        {
            // 只有真正清理成功才把屏幕交给下一个客户端；失败时仍保留租约。
            // 这里不能走 ReleaseHolderIfIdle：本工具自己还持有一个租约（active_calls>=1）。
            context.Control.Lease.ForceRelease(releaseClientId);
            return;
        }

        if (handlerTask is { IsCompleted: false })
        {
            // 超时/取消后 handler 可能仍在关闭窗口：等它真正结束再释放租约。
            _ = handlerTask.ContinueWith(
                _ => context.Control.Lease.ForceRelease(releaseClientId),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task<(WriteScheduler.Lease? Lease, IDisposable? ScreenLease, CancellationTokenSource CallCts)>
        BeginInvocation(ToolDescriptor tool, ToolContext context, ToolPolicy policy)
    {
        WriteScheduler.Lease? lease = null;
        IDisposable? screenLease = null;
        CancellationTokenSource? callCts = null;

        string clientId = context.Client?.Id ?? "c0";
        TimeSpan wait = TimeSpan.FromMilliseconds(policy.WriteTimeoutMs);

        try
        {
            (lease, screenLease) = await InvocationGate.AcquireAsync(
                context.Control.Lease,
                _scheduler,
                clientId,
                tool.Name,
                ToolCancelPolicy.RequiresScreenLease(context, policy),
                policy.Mutates,
                wait,
                policy.BypassWriteLock,
                context.CancellationToken).ConfigureAwait(false);

            if (screenLease is not null && ToolCancelPolicy.KeepsForeground(context.Arguments))
            {
                context.Control.ExtendLease(clientId, 30_000);
                context.Control.PauseFocusGuard(30_000);
            }

            callCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            callCts.CancelAfter(tool.TimeoutMs);
            context.Control.SetCancellation(callCts);

            return (lease, screenLease, callCts);
        }
        catch (Exception)
        {
            lease?.Dispose();
            screenLease?.Dispose();
            callCts?.Dispose();
            throw;
        }
    }

    private Task<ToolResponse> StartHandlerTask(
        ToolDescriptor tool, ToolContext context, ToolPolicy policy, Action<int, int, string>? reporter)
    {
        return Task.Run(() =>
        {
            // release 与只读观察互斥：写锁优先。release_environment 同时声明
            // sessionRead/sessionWrite，先判读会让独占分支永远执行不到。
            // 读锁带超时，避免 release 持写锁期间所有读工具在 EnterReadLock 上无限阻塞。
            SessionLockScope.Acquisition sessionLock = SessionLockScope.Acquire(
                context.Control.Gate, policy.SessionRead, policy.SessionWrite);

            if (sessionLock.Busy is not null)
            {
                return sessionLock.Busy;
            }

            using IDisposable? sessionScope = sessionLock.Scope;
            using var progress = ToolProgress.Begin(reporter);

            try
            {
                return tool.Handler(context.Arguments);
            }
            catch (ToolException exception)
            {
                return ToolResponse.FromException(exception);
            }
            catch (Exception exception)
            {
                _log.Error($"工具 {tool.Name} 异常", exception);
                return ToolResponse.FromException(exception, $"tool:{tool.Name}");
            }
        }, CancellationToken.None);
    }

    private TimeoutOutcome BuildTimeoutOutcome(
        ToolDescriptor tool,
        Task<ToolResponse> handlerTask,
        WriteScheduler.Lease? lease,
        IDisposable? screenLease,
        CancellationTokenSource? callCts)
    {
        // 写锁不释放：卡住的 handler 仍在操作 UI，不能让另一个写操作重叠。
        if (lease is not null)
        {
            WriteScheduler.ReleaseWhen(handlerTask, lease);
            lease = null;
        }

        // 协作式取消：让还在循环里的 handler 尽快停下；CTS 交给 handler 释放。
        // 否则 using 会先对 token 做 dispose，Cancellation 属性就退化成 None（S5）。
        try
        {
            callCts?.Cancel();
        }
        catch (ObjectDisposedException exception)
        {
            // 已经结束了。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        _ = handlerTask.ContinueWith(_ => callCts?.Dispose(), TaskScheduler.Default);

        IDisposable? pendingScreenLease = screenLease;

        if (pendingScreenLease is not null)
        {
            _ = handlerTask.ContinueWith(
                _ => pendingScreenLease.Dispose(), TaskScheduler.Default);
        }

        _log.Warn($"工具 {tool.Name} 超过 {tool.TimeoutMs}ms 未完成，已请求取消");
        var response = ToolResponse.Error(
            "E_TIMEOUT",
            $"工具 {tool.Name} 超过 {tool.TimeoutMs}ms 未完成",
            new JsonObject { ["tool"] = tool.Name, ["timeout_ms"] = tool.TimeoutMs },
            retryable: true,
            hint: "可重试；若持续超时，先保存再 release_environment{on_dirty:\"force\",allow_data_loss:true} 重置会话",
            stage: "tool");

        return new TimeoutOutcome(response, lease, null, CtsTransferred: true);
    }

    private ToolResponse CompleteResponse(
        ToolCallOutcome outcome, ToolResponse response, System.Diagnostics.Stopwatch stopwatch,
        out bool releaseSucceeded)
    {
        ToolDescriptor tool = outcome.Tool;
        ToolContext context = outcome.Context;
        ToolPolicy policy = outcome.Policy;
        long epoch = outcome.Epoch;
        releaseSucceeded = false;

        if (context.Control.Epoch != epoch && !policy.SessionWrite)
        {
            response = ToolResponse.Error(
                "E_CANCELLED",
                "会话已被 release_environment 重置，本次结果已丢弃",
                new JsonObject { ["epoch"] = epoch, ["current_epoch"] = context.Control.Epoch },
                retryable: true,
                hint: "重新调用 snapshot/launch_or_place_app",
                stage: "session");
        }

        stopwatch.Stop();
        response.SetRequestId(context.RequestId);

        if (policy.SessionWrite && !response.IsError)
        {
            releaseSucceeded = true;
        }

        _progress?.Invoke(1, 1, "completed");
        _trace(tool.Name, context.RequestId, context.ArgumentSummary, stopwatch.Elapsed.TotalMilliseconds, response);
        return response;
    }

    /// <summary>
    /// 客户端取消：handler 可能仍在注入。写锁交给它，CTS 也交给它释放。
    /// 避免“已取消但仍在点击”与新写操作并发（S5 的另一半）。
    /// </summary>
}
