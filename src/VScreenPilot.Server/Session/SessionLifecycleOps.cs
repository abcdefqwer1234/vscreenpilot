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

/// <summary>LifecycleOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionLifecycleOps
{
    internal readonly SessionManager _host;

    public SessionLifecycleOps(SessionManager host) => _host = host;
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
            _host._log.Info(
                $"内存裁剪 ({reason}): {before / 1048576.0:F1}MB -> " +
                $"gc={afterGc / 1048576.0:F1}MB -> {after / 1048576.0:F1}MB" +
                (emptied ? " (emptied)" : string.Empty));
        }
        catch (Exception exception)
        {
            _host._log.Warn($"内存裁剪失败 ({reason}): {exception.Message}");
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
        _host._environment.GetEnvironment(ensure);

    public ReleaseReport ReleaseEnvironment(
        bool keepDisplay, string onDirty = "abort", bool allowDataLoss = false,
        bool forceRemoveDisplay = false, bool forceAllClients = false) =>
        _host._releaserService.Release(
            keepDisplay, onDirty, allowDataLoss, forceRemoveDisplay, forceAllClients);


    internal string CloseTrackedApp(TrackedApp app, string policy, out string? warning) =>
        _host._releaser.Close(app, policy, out warning);

    internal void ForgetDisplay() => _host._display.Forget();

    internal DisplayInfo? OwnDisplay() => _host._display.OwnDisplay();

    internal bool TryRemoveDisplay() => _host._display.TryRemove();

    public void SaveState() => _host._stateKeeper.Save();

    internal void RecoverFromPreviousCrash() => _host._stateKeeper.Recover();

    internal void EnsureAppAllowed(string? path) => _host._allowlist.Ensure(path);


    public void Dispose()
    {
        if (Interlocked.Exchange(ref _host._disposed, 1) != 0)
        {
            return;
        }

        _host.MaintenanceService.Dispose();
        _host.FocusOps.StopFocusGuard();

        // 清理不能无限等：如果某个 handler 卡在 UIA COM 里并持有 _host._gate，
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
                _host._log.Error("释放环境失败", exception);
            }
        });

        if (!cleanup.Wait(TimeSpan.FromSeconds(3)))
        {
            _host._log.Warn("清理超过 3s 未完成，执行最小清理并退出");

            try
            {
                // 超时兜底也只能摘“自己的 device”，不能按可能被复用的 index 盲摘。
                if (_host._display.State.Index >= 0 &&
                    !string.IsNullOrWhiteSpace(_host._display.State.DeviceName) &&
                    DisplayTopology.FindParsecDisplayByDevice(_host._display.State.DeviceName) is not null)
                {
                    _host._display.Vdd.RemoveDisplay(_host._display.State.Index);
                }
            }
            catch (Exception exception)
            {
                _host._log.Warn($"强制摘屏失败: {exception.Message}");
            }
        }

        // 会话结束时统一释放 UIA COM 引用（当前 + 退休实例）。
        try
        {
            _host._uiaHost.Dispose();
            _host.Snapshots.Dispose();
        }
        catch (Exception exception)
        {
            // 退出路径上的释放失败不影响进程结束。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        _host._job.Dispose();
        _host._display.Vdd.Dispose();
        _host._stateStore.Clear();
    }

    internal TrackedApp? FindTrackedAppAtWindowCore(uint targetPid, uint rootPid)
    {
        lock (_host._gate)
        {
            return _host._apps.FirstOrDefault(app => _host.ClientOps.IsOwned(app) &&
                (app.Pid == (int)targetPid || app.Pid == (int)rootPid) && TrackedAppTracker.IsLive(app));
        }
    }

    internal List<TrackedApp> OwnedAppsSnapshotCore()
    {
        lock (_host._gate)
        {
            return _host._apps.Where(app => _host.ClientOps.IsOwned(app) && TrackedAppTracker.IsLive(app)).ToList();
        }
    }

    internal bool IsTrackedPidCore(int pid)
    {
        lock (_host._gate)
        {
            return _host._apps.Any(app => _host.ClientOps.IsOwned(app) && app.Pid == pid && TrackedAppTracker.IsLive(app));
        }
    }

    internal TrackedApp? OwnedAppForPidCore(int pid)
    {
        lock (_host._gate)
        {
            return _host._apps.FirstOrDefault(
                item => _host.ClientOps.IsOwned(item) && item.Pid == pid && TrackedAppTracker.IsLive(item));
        }
    }

    internal void ClearElementRefsCore(bool allClients)
    {
        if (allClients)
        {
            _host.Refs.ClearUnsafe();
        }
        else
        {
            _host.Refs.DropClientUnsafe(_host.Clients.CurrentId);
        }
    }

    internal void ClearFramesCore(bool allClients)
    {
        if (allClients)
        {
            _host._frames.Clear();
        }
        else
        {
            _host._frames.RemoveClient(_host.Clients.CurrentId);
        }
    }

    internal void ClearInputBlockCore(bool allClients)
    {
        if (allClients)
        {
            _host._input.ClearBlocked();
        }
        else
        {
            _host._input.UnblockClient(_host.Clients.CurrentId);
        }
    }

    internal void StopAndDisposeVddCore()
    {
        _host._display.Vdd.StopKeepAlive();
        _host._display.Vdd.Dispose();
    }

    internal string[] ArtifactDirectorySnapshot()
    {
        lock (_host._artifactGate)
        {
            return _host._artifactDirectories.Values.ToArray();
        }
    }
}
