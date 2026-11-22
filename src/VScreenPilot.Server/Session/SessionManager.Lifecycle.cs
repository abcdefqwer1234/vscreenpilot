using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Security;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Processes;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager : IDisposable, IElementOperationsHost
{
    /// <summary>
    /// 在释放/预热等低负载节点裁剪工作集。不是定时看门狗，只按事件触发。
    /// </summary>
    public MemoryView TrimIdleMemory(string reason)
    {
        long before = 0;
        long afterGc = 0;
        long after = 0;
        bool emptied = false;

        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            before = process.WorkingSet64;

            if (before < 36L * 1024 * 1024)
            {
                return new MemoryView(
                    Math.Round(before / 1048576.0, 1),
                    Math.Round(before / 1048576.0, 1),
                    Math.Round(before / 1048576.0, 1),
                    Trimmed: false,
                    Emptied: false,
                    PrivateMb: Math.Round(process.PrivateMemorySize64 / 1048576.0, 1));
            }

            // 不阻塞等待终结器：UIA COM 释放期强制 GC + WaitForPendingFinalizers
            // 在高频进程启停压测中可能拖住终结器线程，导致进程退出变慢。
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);

            Thread.Sleep(20);
            process.Refresh();
            afterGc = process.WorkingSet64;

            // GC 后仍高于目标时再清空工作集；否则保留热页，避免下次调用立刻缺页。
            if (afterGc > 40L * 1024 * 1024)
            {
                NativeMethods.SetProcessWorkingSetSize(
                    process.Handle, new IntPtr(-1), new IntPtr(-1));
                Thread.Sleep(50);
                process.Refresh();
                emptied = true;
            }

            after = process.WorkingSet64;
            _log.Info(
                $"内存裁剪 ({reason}): {before / 1048576.0:F1}MB -> " +
                $"gc={afterGc / 1048576.0:F1}MB -> {after / 1048576.0:F1}MB" +
                (emptied ? " (emptied)" : string.Empty));
        }
        catch (Exception exception)
        {
            _log.Warn($"内存裁剪失败 ({reason}): {exception.Message}");
            after = before;
            afterGc = before;
        }

        return new MemoryView(
            Math.Round(before / 1048576.0, 1),
            Math.Round(afterGc / 1048576.0, 1),
            Math.Round(after / 1048576.0, 1),
            Trimmed: after > 0 && after < before,
            Emptied: emptied);
    }

    public EnvironmentView GetEnvironment(bool ensure = false) =>
        _environment.GetEnvironment(ensure);

    public ReleaseReport ReleaseEnvironment(
        bool keepDisplay, string onDirty = "abort", bool allowDataLoss = false,
        bool forceRemoveDisplay = false, bool forceAllClients = false) =>
        _releaserService.Release(
            keepDisplay, onDirty, allowDataLoss, forceRemoveDisplay, forceAllClients);


    private string CloseTrackedApp(TrackedApp app, string policy, out string? warning) =>
        _releaser.Close(app, policy, out warning);

    private void ForgetDisplay() => _display.Forget();

    private DisplayInfo? OwnDisplay() => _display.OwnDisplay();

    private bool TryRemoveDisplay() => _display.TryRemove();

    public void SaveState() => _stateKeeper.Save();

    private void RecoverFromPreviousCrash() => _stateKeeper.Recover();

    private void EnsureAppAllowed(string? path) => _allowlist.Ensure(path);


    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _maintenance.Dispose();
        StopFocusGuard();

        // 清理不能无限等：如果某个 handler 卡在 UIA COM 里并持有 _gate，
        // 直接 Dispose 会让宿主退出永久挂起。给 3s 预算，超时就做最小清理。
        Task cleanup = Task.Run(() =>
        {
            try
            {
                // 进程退出必须摘屏并回收本会话进程；不能沿用 abort 的“保留窗口/保留屏”语义，
                // 否则会留下孤儿虚拟屏（下次启动可能 E_ORPHAN_DISPLAY）。
                ReleaseEnvironment(
                    keepDisplay: false,
                    onDirty: "force",
                    allowDataLoss: true,
                    forceRemoveDisplay: true,
                    forceAllClients: true);
            }
            catch (Exception exception)
            {
                _log.Error("释放环境失败", exception);
            }
        });

        if (!cleanup.Wait(TimeSpan.FromSeconds(3)))
        {
            _log.Warn("清理超过 3s 未完成，执行最小清理并退出");

            try
            {
                // 超时兜底也只能摘“自己的 device”，不能按可能被复用的 index 盲摘。
                if (_display.State.Index >= 0 &&
                    !string.IsNullOrWhiteSpace(_display.State.DeviceName) &&
                    DisplayTopology.FindParsecDisplayByDevice(_display.State.DeviceName) is not null)
                {
                    _display.Vdd.RemoveDisplay(_display.State.Index);
                }
            }
            catch (Exception exception)
            {
                _log.Warn($"强制摘屏失败: {exception.Message}");
            }
        }

        // 会话结束时统一释放 UIA COM 引用（当前 + 退休实例）。
        try
        {
            _uiaHost.Dispose();
            _snapshots.Dispose();
        }
        catch (Exception exception)
        {
            // 退出路径上的释放失败不影响进程结束。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        _job.Dispose();
        _display.Vdd.Dispose();
        _stateStore.Clear();
    }

    private List<TrackedApp> OwnedAppsSnapshotCore()
    {
        lock (_gate)
        {
            return _apps.Where(app => IsOwned(app) && TrackedAppTracker.IsLive(app)).ToList();
        }
    }

    private bool IsTrackedPidCore(int pid)
    {
        lock (_gate)
        {
            return _apps.Any(app => IsOwned(app) && app.Pid == pid && TrackedAppTracker.IsLive(app));
        }
    }

    DisplayInfo IElementOperationsHost.EnsureDisplay() => EnsureEnvironment();

    UiaHost IElementOperationsHost.Uia => _uiaHost;

    ElementResolver IElementOperationsHost.Resolver => _resolver;

    ElementRefStore IElementOperationsHost.Refs => _refs;

    int IElementOperationsHost.RefCapacity => ElementRefCapacity;

    bool IElementOperationsHost.IsCancelled => Cancellation.IsCancellationRequested;

    LogRing IElementOperationsHost.Log => _log;

    TrackedApp? IElementOperationsHost.FindApp(int? pid) => FindLiveTrackedApp(pid);

    TrackedApp? IElementOperationsHost.OwnedAppForPid(int pid)
    {
        lock (_gate)
        {
            return _apps.FirstOrDefault(
                item => IsOwned(item) && item.Pid == pid && TrackedAppTracker.IsLive(item));
        }
    }

    void IElementOperationsHost.RequireOnDisplay(DisplayInfo display, IntPtr hwnd, string label) =>
        RequireWindowOnDisplay(display, hwnd, label);

    void IElementOperationsHost.RequirePointInTrackedApp(
        DisplayInfo display, int x, int y, string label) =>
        RequirePointInTrackedApp(display, x, y, label);

    ClickView IElementOperationsHost.Click(
        int x, int y, string button, int clicks, string mode,
        bool keepForeground, bool observe, bool fallbackInput) =>
        Click(x, y, button, clicks, mode, keepForeground, observe, fallbackInput);

    TypeTextView IElementOperationsHost.TypeText(
        string text, int? pid, bool verify, int chunkChars, string channel) =>
        TypeText(text, pid, verify, chunkChars, channel);

    TextWriteResult IElementOperationsHost.WriteTextVerified(
        TrackedApp app, UiaElementHandle handle, string value, bool replace, int timeoutMs) =>
        _input.WriteTextVerified(app, handle, value, replace, timeoutMs);

    bool IElementOperationsHost.EnsureForeground(TrackedApp app) => EnsureForeground(app);

    void IElementOperationsHost.PauseFocusGuard() => PauseFocusGuard();

    void IElementOperationsHost.ResumeFocusGuard() => ResumeFocusGuard();

    void IElementOperationsHost.RestoreForeground(IntPtr previous) => RestoreForeground(previous);

    void IElementOperationsHost.ResetUia() => ResetUia();

    JsonObject IElementOperationsHost.ConvertTreeNode(
        UiaTreeNode node, IntPtr hwnd, ref string rootRef) =>
        _snapshots.ConvertTreeNode(node, hwnd, ref rootRef);

    Func<bool> IElementOperationsHost.CancellationProbe() => CancellationProbe();

    MetricsRegistry IEnvironmentHost.Metrics => Metrics;

    private void ClearElementRefsCore(bool allClients)
    {
        if (allClients)
        {
            _refs.ClearUnsafe();
        }
        else
        {
            _refs.DropClientUnsafe(Clients.CurrentId);
        }
    }

    private void ClearFramesCore(bool allClients)
    {
        if (allClients)
        {
            _frames.Clear();
        }
        else
        {
            _frames.RemoveClient(Clients.CurrentId);
        }
    }

    private void ClearInputBlockCore(bool allClients)
    {
        if (allClients)
        {
            _input.ClearBlocked();
        }
        else
        {
            _input.UnblockClient(Clients.CurrentId);
        }
    }

    private void StopAndDisposeVddCore()
    {
        _display.Vdd.StopKeepAlive();
        _display.Vdd.Dispose();
    }

    private string[] ArtifactDirectorySnapshot()
    {
        lock (_artifactGate)
        {
            return _artifactDirectories.Values.ToArray();
        }
    }

}
