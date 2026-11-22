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

/// <summary>取消与屏幕租约策略；从 ToolExecutor 拆出。</summary>
internal static class ToolCancelPolicy
{
    internal sealed record CancelOutcome(
        WriteScheduler.Lease? PendingLease,
        IDisposable? PendingScreenLease,
        bool CtsTransferred);


    internal static CancelOutcome BuildCancelOutcome(
        Task<ToolResponse>? handlerTask,
        WriteScheduler.Lease? lease,
        IDisposable? screenLease,
        CancellationTokenSource? callCts)
    {
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
        catch (ObjectDisposedException exception)
        {
            // 已经结束了。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        bool transferred = false;

        if (callCts is not null && handlerTask is not null && !handlerTask.IsCompleted)
        {
            _ = handlerTask.ContinueWith(_ => callCts?.Dispose(), TaskScheduler.Default);
            transferred = true;
        }

        return new CancelOutcome(lease, screenLease, transferred);
    }


    internal static bool RequiresScreenLease(ToolContext context, ToolPolicy policy) => policy.Lease switch
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
        ScreenLeaseMode.IfLiveApps => context.Control.HasLiveOwnedApps(),
        _ => false,
    };


    internal static bool ArgTrue(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.True;


    internal static bool KeepsForeground(JsonElement arguments) =>
        ArgTrue(arguments, "keep_foreground");

}
