using System.Runtime.InteropServices;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>会话状态持久化的最小存储抽象，便于单测替换。</summary>
internal interface ISessionStateStore
{
    SessionState? Load();
    void Save(SessionState state);
    void Clear();
}

/// <summary>会话状态持久化所需的最小宿主接口。</summary>
internal interface ISessionStateHost
{
    object Gate { get; }
    ISessionStateStore Store { get; }
    DisplayStateStore DisplayState { get; }
    TrackedAppRegistry Apps { get; }
    LogRing Log { get; }
}

/// <summary>崩溃恢复与窗口位置状态的持久化。</summary>
internal sealed class SessionStateKeeper
{
    private readonly ISessionStateHost _host;

    public SessionStateKeeper(ISessionStateHost host) => _host = host;

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
        if (state is null || state.Pid == Environment.ProcessId)
        {
            return;
        }

        _host.DisplayState.RecoveredPid = state.Pid;
        _host.DisplayState.RecoveredIndex = state.DisplayIndex;
        _host.DisplayState.RecoveredDevice = state.DisplayDevice;

        if (state.DisplayIndex >= 0)
        {
            _host.Log.Info(
                $"发现上次会话状态: pid={state.Pid}, displayIndex={state.DisplayIndex}, " +
                $"device={state.DisplayDevice}, apps={state.Apps.Count}");
        }

        // 互斥体保证不会有两个 VScreenPilot；若旧 pid 仍对应同名且启动时间匹配的进程，
        // 说明它还没退干净，此时不冒险接管它的屏，也不恢复/清空 state。
        if (state.Pid > 0 && IsVScreenPilotProcess(state.Pid, state.StartedAt))
        {
            _host.Log.Warn($"上次会话进程仍在运行 (pid={state.Pid})，跳过全部恢复");
            _host.DisplayState.RecoveredIndex = -1;
            _host.DisplayState.RecoveredDevice = string.Empty;
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
        }

        _host.Store.Clear();
    }

    /// <summary>
    /// 可选的启动白名单：VSP_APP_ALLOWLIST 用分号分隔目录。
    /// 未配置时保持原有“允许任意 exe”的行为；配置后只允许白名单目录下的完整路径。
    /// </summary>
    public static bool IsVScreenPilotProcess(int pid, string startedAt)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            bool nameMatches = string.Equals(
                process.ProcessName, "VScreenPilot", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(startedAt) ||
                !DateTimeOffset.TryParse(startedAt, out DateTimeOffset expected))
            {
                return nameMatches;
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
