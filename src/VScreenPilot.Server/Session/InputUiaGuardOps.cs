using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Views;
using Interop.UIAutomationClient;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Input;
using VScreenPilot.Json;
using VScreenPilot.Interop;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using static VScreenPilot.Json.JsonArgReader;
using ObserveSignature = VScreenPilot.Verification.ObserveSampler.Signature;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>UIA 坐标触发的三态结果：超时必须终止回退，不能再补一次 message/input 点击。</summary>
internal enum UiaInvokeOutcome
{
    Ok,
    Unsupported,
    TimedOut,
}

/// <summary>UiaGuardOps：InputRouter 的输入子通道。</summary>
internal sealed class InputUiaGuardOps
{
    internal readonly InputRouter _host;

    public InputUiaGuardOps(InputRouter host) => _host = host;
    internal void BlockUiaPid(int pid)
    {
        lock (_host._blockGate)
        {
            string clientId = _host._session.Clients.CurrentId;

            if (!_host._uiaBlockedPids.TryGetValue(clientId, out HashSet<int>? blocked))
            {
                blocked = new HashSet<int>();
                _host._uiaBlockedPids[clientId] = blocked;
            }

            blocked.Add(pid);
        }
    }

    internal UiaInvokeOutcome TryUiaInvoke(
        int x, int y, int pid, out UiaElementInfo? hit,
        Func<UiaElementInfo, bool>? confirm = null)
    {
        hit = null;

        lock (_host._blockGate)
        {
            if (_host._uiaBlockedPids.TryGetValue(_host._session.Clients.CurrentId, out HashSet<int>? blocked) &&
                blocked.Contains(pid))
            {
                return UiaInvokeOutcome.Unsupported;
            }
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // ElementFromPoint/Invoke 可能被 provider 卡住；最多等 3s，超时换新 UIA 实例。
            Task<(bool Ok, UiaElementInfo? Info)> invokeTask = Task.Run(() =>
            {
                bool ok = _host._uia.Uia.TryInvokeAtPoint(x, y, pid, out UiaElementInfo? info, confirm);
                return (ok, info);
            });

            if (!invokeTask.Wait(3000))
            {
                _host._uia.ResetUia();
                BlockUiaPid(pid);
                _host._log.Warn($"UIA 坐标触发超时，已熔断 pid={pid}");
                return UiaInvokeOutcome.TimedOut;
            }

            (bool result, UiaElementInfo? info) = invokeTask.Result;
            hit = info;

            // 没有 InvokePattern 会快速返回 false；超过阈值说明 provider 明显卡顿，
            // 本会话内对该 pid 熔断，后续 auto 直接走 message，避免反复挂起。
            if (!result && stopwatch.ElapsedMilliseconds >= 800)
            {
                BlockUiaPid(pid);
            }

            return result ? UiaInvokeOutcome.Ok : UiaInvokeOutcome.Unsupported;
        }
        catch (AggregateException aggregate) when (aggregate.InnerException is ToolException toolException)
        {
            // 护栏回调在 UIA worker 内抛出时，Task 会包装成 AggregateException；
            // 必须把 E_DESTRUCTIVE_TARGET 原样返回，不能降级成“UIA 不可用”。
            throw toolException;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            BlockUiaPid(pid);
            return UiaInvokeOutcome.Unsupported;
        }
    }

    /// <summary>带超时的只读命中测试；失败返回 null，不影响主动作。</summary>
    internal UiaElementInfo? DescribeAtPoint(int x, int y, int pid, int timeoutMs = 500)
    {
        try
        {
            Task<UiaElementInfo?> task = Task.Run(() => _host._uia.Uia.DescribeAtPoint(x, y, pid));
            return task.Wait(timeoutMs) ? task.Result : null;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"命中测试失败: {exception.Message}");
            return null;
        }
    }
}
