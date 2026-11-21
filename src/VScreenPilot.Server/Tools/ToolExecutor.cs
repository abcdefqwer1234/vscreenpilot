using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Session;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Errors;
using VScreenPilot.Json;

namespace VScreenPilot.Mcp;

/// <summary>
/// 一次工具调用的上下文：会话、客户端与请求级参数。处理器仍绑定在
/// <see cref="McpTransport"/> 实例上，执行器只依赖本记录，不再持有会话字段。
/// </summary>
internal sealed record ToolContext(
    SessionManager Session,
    ClientContext? Client,
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

    public async Task<ToolResponse> ExecuteAsync(ToolDescriptor tool, ToolContext context)
    {

        using IDisposable? clientScope = context.Client is null
            ? null
            : context.Session.Clients.Use(context.Client);
        long epoch = context.Session.Epoch;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        WriteScheduler.Lease? lease = null;
        CancellationTokenSource? callCts = null;
        bool callCtsTransferred = false;
        Task<ToolResponse>? handlerTask = null;
        ToolPolicy policy = tool.Policy;
        bool mutating = policy.Mutates;
        IDisposable? screenLease = null;
        bool releaseSucceeded = false;

        try
        {
            string clientId = context.Client?.Id ?? "c0";

            if (RequiresScreenLease(context, policy))
            {
                screenLease = context.Session.Lease.Acquire(clientId, tool.Name);

                if (KeepsForeground(context.Arguments))
                {
                    context.Session.Lease.Extend(clientId, 30_000);
                    context.Session.PauseFocusGuard(30_000);
                }
            }

            if (mutating)
            {
                // release 等 6s：正常写操作应在这之前结束；只有锁定超过 5s 的卡死 handler 才旁路。
                TimeSpan wait = TimeSpan.FromMilliseconds(policy.WriteTimeoutMs);

                lease = await _scheduler.AcquireAsync(
                    tool.Name, wait, context.CancellationToken,
                    bypass: policy.BypassWriteLock).ConfigureAwait(false);
            }

            callCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            callCts.CancelAfter(tool.TimeoutMs);
            context.Session.SetCancellation(callCts);

            // 长任务至少给一次“已开始/已完成”进度，客户端不会以为调用挂死。
            _progress?.Invoke(0, 1, "started");

            handlerTask = Task.Run(() =>
            {
                Action<int, int, string>? reporter = _progress;

                // release 与只读观察互斥：release 摘屏前先等 snapshot/capture 结束。
                // wait_for 可能持续 120s，不能持读锁阻塞 release；get_log 不碰显示，也跳过。
                // 注意：这里用“是否读会话”而不是 readOnlyHint 判断。snapshot/capture_screen
                // 会换代/写盘，标注上不是只读，但仍然必须受 release 的读锁保护。
                bool sessionRead = policy.SessionRead;
                bool sessionWrite = policy.SessionWrite;
                bool writeHeld = false;

                if (sessionRead)
                {
                    context.Session.EnterRead();
                }
                else if (sessionWrite)
                {
                    writeHeld = context.Session.TryEnterExclusive(3000);

                    if (!writeHeld)
                    {
                        // 不能“等不到锁也继续清理”：只读观察可能正在读 UIA/屏幕，
                        // 同时摘屏会让它们拿到半清理状态。宁可让调用方重试。
                        return ToolResponse.Error(
                            "E_BUSY",
                            "release_environment 等待只读观察结束超时（3s）",
                            new JsonObject
                            {
                                ["held_by"] = "snapshot/capture/find_element",
                                ["retry_after_ms"] = 500,
                            },
                            retryable: true,
                            hint: "稍后重试；也可以等当前 snapshot/capture 返回后再 release",
                            stage: "session");
                    }
                }

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
                finally
                {
                    if (sessionRead)
                    {
                        context.Session.ExitRead();
                    }
                    else if (writeHeld)
                    {
                        context.Session.ExitExclusive();
                    }
                }
            }, CancellationToken.None);

            _handlerStarted();
            _ = handlerTask.ContinueWith(
                _ => _handlerFinished(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            ToolResponse response;

            try
            {
                response = await handlerTask!
                    .WaitAsync(TimeSpan.FromMilliseconds(tool.TimeoutMs), context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 写锁不释放：卡住的 handler 仍在操作 UI，不能让另一个写操作重叠。
                if (lease is not null)
                {
                    WriteScheduler.ReleaseWhen(handlerTask!, lease);
                    lease = null;
                }

                // 协作式取消：让还在循环里的 handler 尽快停下；CTS 交给 handler 释放，
                // 否则 using 会先把 token 源 dispose，Cancellation 属性就退化成 None（S5）。
                try
                {
                    callCts?.Cancel();
                }
                catch (ObjectDisposedException exception){
                    // 已经结束。
                    System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
                }
                _ = handlerTask!.ContinueWith(
                    _ => callCts?.Dispose(), TaskScheduler.Default);
                callCtsTransferred = true;

                if (screenLease is not null)
                {
                    IDisposable pendingLease = screenLease;
                    screenLease = null;
                    _ = handlerTask!.ContinueWith(
                        _ => pendingLease.Dispose(), TaskScheduler.Default);
                }

                _log.Warn($"工具 {tool.Name} 超过 {tool.TimeoutMs}ms 未完成，已请求取消");
                response = ToolResponse.Error(
                    "E_TIMEOUT",
                    $"工具 {tool.Name} 超过 {tool.TimeoutMs}ms 未完成",
                    new JsonObject { ["tool"] = tool.Name, ["timeout_ms"] = tool.TimeoutMs },
                    retryable: true,
                    hint: "可重试；若持续超时，先保存再 release_environment{on_dirty:\"force\",allow_data_loss:true} 重置会话",
                    stage: "tool");
            }

            if (context.Session.Epoch != epoch && !policy.SessionWrite)
            {
                response = ToolResponse.Error(
                    "E_CANCELLED",
                    "会话已被 release_environment 重置，本次结果已丢弃",
                    new JsonObject { ["epoch"] = epoch, ["current_epoch"] = context.Session.Epoch },
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
            return (response);
        }
        catch (OperationCanceledException)
        {
            // 客户端取消：handler 可能仍在注入。写锁交给它，CTS 也交给它释放，
            // 避免 “已取消但仍在点击” 与新写操作并发（S5 的另一半）。
            if (lease is not null)
            {
                if (handlerTask is not null)
                {
                    WriteScheduler.ReleaseWhen(handlerTask, lease);
                }
                else
                {
                    lease.Dispose();
                }

                lease = null;
            }

            if (screenLease is not null)
            {
                IDisposable pendingLease = screenLease;
                screenLease = null;

                if (handlerTask is not null)
                {
                    _ = handlerTask.ContinueWith(
                        _ => pendingLease.Dispose(), TaskScheduler.Default);
                }
                else
                {
                    pendingLease.Dispose();
                }
            }

            try
            {
                callCts?.Cancel();
            }
            catch (ObjectDisposedException exception){
                // 已经结束。
                System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
            }

            if (callCts is not null && handlerTask is not null && !handlerTask.IsCompleted)
            {
                _ = handlerTask.ContinueWith(_ => callCts?.Dispose(), TaskScheduler.Default);
                callCtsTransferred = true;
            }

            throw;
        }
        catch (ToolException exception)
        {
            var response = ToolResponse.FromException(exception).SetRequestId(context.RequestId);
            _trace(tool.Name, context.RequestId, context.ArgumentSummary, stopwatch.Elapsed.TotalMilliseconds, response);
            return (response);
        }
        catch (Exception exception)
        {
            _log.Error($"工具 {tool.Name} 失败", exception);
            var response = ToolResponse.FromException(exception, $"tool:{tool.Name}")
                .SetRequestId(context.RequestId);
            _trace(tool.Name, context.RequestId, context.ArgumentSummary, stopwatch.Elapsed.TotalMilliseconds, response);
            return (response);
        }
        finally
        {
            context.Session.SetCancellation(null);

            // 超时后 CTS 的所有权已经交给仍在运行的 handler。
            if (!callCtsTransferred)
            {
                callCts?.Dispose();
            }

            if (policy.SessionWrite)
            {
                string releaseClientId = context.Client?.Id ?? "c0";

                if (releaseSucceeded)
                {
                    // 只有真正清理成功才把屏幕交给下一个客户端；失败时仍保留租约。
                    // 这里不能用 ReleaseHolderIfIdle：本工具自己还持有一个租约（active_calls>=1）。
                    context.Session.Lease.ForceRelease(releaseClientId);
                }
                else if (handlerTask is { IsCompleted: false })
                {
                    // 超时/取消时 handler 可能仍在关闭窗口：等它真正结束再释放租约。
                    _ = handlerTask.ContinueWith(
                        _ => context.Session.Lease.ForceRelease(releaseClientId),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }

            // 释放顺序与获取相反：先写锁，后屏幕租约。
            lease?.Dispose();
            screenLease?.Dispose();
        }
        }
    private bool RequiresScreenLease(ToolContext context, ToolPolicy policy) => policy.Lease switch
    {
        ScreenLeaseMode.None => false,
        ScreenLeaseMode.Always => true,
        ScreenLeaseMode.EnsureOption => OptionalBool(context.Arguments, "ensure", false),
        ScreenLeaseMode.SnapshotOptions => ArgTrue(context.Arguments, "include_screenshot") ||
                                           ArgTrue(context.Arguments, "annotate"),
        ScreenLeaseMode.WaitCondition =>
            context.Arguments.ValueKind == JsonValueKind.Object &&
            context.Arguments.TryGetProperty("condition", out JsonElement condition) &&
            condition.TryGetProperty("type", out JsonElement type) &&
            type.ValueKind == JsonValueKind.String &&
            type.GetString() is "color" or "pixel_diff" or "visual",
        ScreenLeaseMode.IfLiveApps => context.Session.HasLiveOwnedApps(),
        _ => false,
    };

    private static bool ArgTrue(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.True;

    private static bool KeepsForeground(JsonElement arguments) =>
        ArgTrue(arguments, "keep_foreground");

}
