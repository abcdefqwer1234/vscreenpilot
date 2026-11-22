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

    internal UiaInvokeOutcome TryUiaInvoke(int x, int y, int pid)
    {
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
            Task<bool> invokeTask = Task.Run(() =>
            {
                bool ok = _host._uia.Uia.TryInvokeAtPoint(x, y, pid, out _);
                return ok;
            });

            if (!invokeTask.Wait(3000))
            {
                _host._uia.ResetUia();
                BlockUiaPid(pid);
                _host._log.Warn($"UIA 坐标触发超时，已熔断 pid={pid}");
                return UiaInvokeOutcome.TimedOut;
            }

            bool result = invokeTask.Result;

            // 没有 InvokePattern 会快速返回 false；超过阈值说明 provider 明显卡顿，
            // 本会话内对该 pid 熔断，后续 auto 直接走 message，避免反复挂起。
            if (!result && stopwatch.ElapsedMilliseconds >= 800)
            {
                BlockUiaPid(pid);
            }

            return result ? UiaInvokeOutcome.Ok : UiaInvokeOutcome.Unsupported;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            BlockUiaPid(pid);
            return UiaInvokeOutcome.Unsupported;
        }
    }
}
