using System.Runtime.InteropServices;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>会话状态持久化所需的最小上下文；持有实时共享对象，不复制状态。</summary>
internal sealed record SessionStateContext(
    object Gate,
    ISessionStateStore Store,
    DisplayStateStore DisplayState,
    TrackedAppRegistry Apps,
    LogRing Log);

/// <summary>崩溃恢复与窗口位置状态的持久化。</summary>
internal sealed class SessionStateKeeper
{
    private readonly SessionStateContext _host;

    public SessionStateKeeper(SessionStateContext host) => _host = host;

    public void Save()
    {
        lock (_host.Gate)
        {
            var state = new SessionState
            {
                Pid = Environment.ProcessId,
                StartedAt = EnvironmentProbe.ProcessStartTime(),
                DisplayIndex = _host.DisplayState.Index,
                DisplayDevice = _host.DisplayState.VirtualDisplay?.DeviceName ?? _host.DisplayState.DeviceName,
                X = _host.DisplayState.VirtualDisplay?.X ?? 0,
                Y = _host.DisplayState.VirtualDisplay?.Y ?? 0,
                Width = _host.DisplayState.VirtualDisplay?.Width ?? 0,
                Height = _host.DisplayState.VirtualDisplay?.Height ?? 0,
            };

            foreach (TrackedApp app in _host.Apps.Snapshot())
            {
                state.Apps.Add(new AppState
                {
                    Pid = app.Pid,
                    ExePath = app.ExePath,
                    Title = app.Title,
                    Hwnd = app.Hwnd.ToInt64(),
                    LaunchedByUs = app.StartedProcess,
                    OwnerClientId = app.OwnerClientId,
                    Dedicated = app.DedicatedProcess,
                    Detached = app.Detached,
                    TempProfile = app.TempProfile ?? string.Empty,
                    ClientKey = app.ClientKey,
                    ShowCmd = app.Placement.showCmd,
                    MinX = app.Placement.minPosition.X,
                    MinY = app.Placement.minPosition.Y,
                    MaxX = app.Placement.maxPosition.X,
                    MaxY = app.Placement.maxPosition.Y,
                    Left = app.Placement.normalPosition.Left,
                    Top = app.Placement.normalPosition.Top,
                    Right = app.Placement.normalPosition.Right,
                    Bottom = app.Placement.normalPosition.Bottom,
                });
            }

            _host.Store.Save(state);
        }
    }

    public void Recover()
    {
        SessionState? state = _host.Store.Load();
        if (state is null)
        {
            return;
        }

        // 不能只比较 PID：Windows 会复用 PID。当前进程仍在运行时由下面的
        // IsVScreenPilotProcess(pid, startedAt) 按启动时间精确判断。

        _host.DisplayState.RecoveredPid = state.Pid;
        _host.DisplayState.RecoveredIndex = state.DisplayIndex;
        _host.DisplayState.RecoveredDevice = state.DisplayDevice;
        _host.DisplayState.RecoveredStartedAt = state.StartedAt;
        _host.DisplayState.RecoveredSavedAt = state.SavedAt;

        if (state.DisplayIndex >= 0)
        {
            _host.Log.Info(
                $"发现上次会话状态: pid={state.Pid}, displayIndex={state.DisplayIndex}, " +
                $"device={state.DisplayDevice}, apps={state.Apps.Count}, savedAt={state.SavedAt}");
        }

        // 互斥体保证不会有两个 VScreenPilot；若旧 pid 仍对应启动时间匹配的 VScreenPilot 进程，
        // 说明它还没退干净，此时不冒险接管它的屏，也不恢复/清空 state。
        if (state.Pid > 0 && IsVScreenPilotProcess(state.Pid, state.StartedAt))
        {
            _host.Log.Warn($"上次会话进程仍在运行 (pid={state.Pid})，跳过全部恢复");
            _host.DisplayState.RecoveredIndex = -1;
            _host.DisplayState.RecoveredDevice = string.Empty;
            _host.DisplayState.RecoveredPid = 0;
            _host.DisplayState.RecoveredStartedAt = string.Empty;
            _host.DisplayState.RecoveredSavedAt = string.Empty;
            return;
        }

        foreach (AppState app in state.Apps)
        {
            var hwnd = new IntPtr(app.Hwnd);
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                continue;
            }

            // 句柄可能已被系统复用：必须确认仍属于记录中的进程。
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint ownerPid);
            if (ownerPid != (uint)app.Pid)
            {
                _host.Log.Warn($"崩溃恢复跳过已复用的窗口句柄 0x{app.Hwnd:X} (pid={app.Pid})");
                continue;
            }

            var placement = new NativeMethods.WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
                flags = 0,
                showCmd = app.ShowCmd,
                minPosition = new NativeMethods.POINT { X = app.MinX, Y = app.MinY },
                maxPosition = new NativeMethods.POINT { X = app.MaxX, Y = app.MaxY },
                normalPosition = new NativeMethods.RECT
                {
                    Left = app.Left,
                    Top = app.Top,
                    Right = app.Right,
                    Bottom = app.Bottom,
                },
            };

            if (WindowManager.SetPlacement(hwnd, placement))
            {
                _host.Log.Info($"崩溃恢复: 已还原窗口 (pid={app.Pid})");
            }

            // 重新登记为“待认领”窗口：客户端可用 launch_or_place_app(hwnd=...) 接管。
            if (!_host.Apps.Any(existing => existing.Hwnd == hwnd && existing.Pid == app.Pid))
            {
                _host.Apps.Add(new TrackedApp
                {
                    Pid = app.Pid,
                    OwnerClientId = string.Empty,
                    ExePath = app.ExePath,
                    Hwnd = hwnd,
                    Title = WindowManager.GetTitle(hwnd),
                    StartedProcess = app.LaunchedByUs,
                    Owned = false,
                    CloseWindowOnRelease = false,
                    DedicatedProcess = app.Dedicated,
                    Detached = app.Detached,
                    TempProfile = string.IsNullOrWhiteSpace(app.TempProfile) ? null : app.TempProfile,
                    ClientKey = app.ClientKey,
                    Recovered = true,
                    Placement = placement,
                    PlacementSaved = true,
                });
            }
        }

        int recovered = _host.Apps.Snapshot().Count(app => app.Recovered);

        if (recovered > 0)
        {
            _host.Log.Info($"崩溃恢复: 已登记 {recovered} 个待认领窗口");
        }

        if (recovered > 0)
        {
            // 保留恢复清单：若 daemon 在客户端认领前再次崩溃，下一次仍能恢复。
            // 这里重写 savedAt，但不改 pid/startedAt，下一次启动仍会走恢复流程。
            _host.Store.Save(state);
        }
        else
        {
            _host.Store.Clear();
        }
    }

    /// <summary>上次会话 state.json 的保存时间距现在的毫秒数；无法解析时返回 null。</summary>
    public static long? StateAgeMs(string savedAt)
    {
        if (string.IsNullOrWhiteSpace(savedAt) ||
            !DateTimeOffset.TryParse(savedAt, out DateTimeOffset saved))
        {
            return null;
        }

        return Math.Max(0, (long)(DateTimeOffset.UtcNow - saved).TotalMilliseconds);
    }

    public static bool IsVScreenPilotProcess(int pid, string startedAt)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);

            if (string.IsNullOrWhiteSpace(startedAt) ||
                !DateTimeOffset.TryParse(startedAt, out DateTimeOffset expected))
            {
                // 老 state（0.1/0.2）没有 startedAt。此时仅凭同名进程无法排除 PID 复用，
                // 继续“跳过恢复”会把可恢复窗口永久留在遗留状态里，因此按“旧进程已退出”处理。
                LogSink.Debug("state 缺少有效的 startedAt，按旧进程已退出处理");
                return false;
            }

            // 以启动时间为准：dotnet 运行、改名 exe 也能识别；避免 PID 复用误判。
            return Math.Abs(
                (process.StartTime.ToUniversalTime() - expected.UtcDateTime).TotalSeconds) <= 5;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return false;
        }
    }

    /// <summary>路径是否位于根目录下（大小写不敏感，供应用白名单使用）。</summary>
}
