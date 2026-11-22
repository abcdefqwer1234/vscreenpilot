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

internal sealed partial class InputRouter
{
    private void BlockUiaPid(int pid)
    {
        lock (_blockGate)
        {
            string clientId = _host.Clients.CurrentId;

            if (!_uiaBlockedPids.TryGetValue(clientId, out HashSet<int>? blocked))
            {
                blocked = new HashSet<int>();
                _uiaBlockedPids[clientId] = blocked;
            }

            blocked.Add(pid);
        }
    }

    private bool TryUiaInvoke(int x, int y, int pid)
    {
        lock (_blockGate)
        {
            if (_uiaBlockedPids.TryGetValue(_host.Clients.CurrentId, out HashSet<int>? blocked) &&
                blocked.Contains(pid))
            {
                return false;
            }
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // ElementFromPoint/Invoke 可能被 provider 卡住；最多等 3s，超时换新 UIA 实例。
            Task<bool> invokeTask = Task.Run(() =>
            {
                bool ok = _host.Uia.TryInvokeAtPoint(x, y, pid, out _);
                return ok;
            });

            if (!invokeTask.Wait(3000))
            {
                _host.ResetUia();
                BlockUiaPid(pid);
                _log.Warn($"UIA 坐标触发超时，已熔断 pid={pid}");
                return false;
            }

            bool result = invokeTask.Result;

            // 没有 InvokePattern 会快速返回 false；超过阈值说明 provider 明显卡顿，
            // 本会话内对该 pid 熔断，后续 auto 直接走 message，避免反复挂起。
            if (!result && stopwatch.ElapsedMilliseconds >= 800)
            {
                BlockUiaPid(pid);
            }

            return result;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            BlockUiaPid(pid);
            return false;
        }
    }

}
