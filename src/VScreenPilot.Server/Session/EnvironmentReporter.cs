using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Json;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Security;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Processes;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Platform.Logging;
using VScreenPilot.Platform.Ipc;

namespace VScreenPilot.Session;

/// <summary>session 视图的输入快照。</summary>
internal sealed record SessionClientSnapshot(
    List<string> Roots, JsonArray List, int OtherClients, int? ProxyPid, string? Version);

internal sealed record SessionUsage(double? WorkingSetMb, int? Handles);

internal sealed record SessionSnapshotInput(
    int SessionId, SessionClientSnapshot Clients, SessionUsage Usage);

/// <summary>环境诊断视图所需的最小宿主接口。</summary>
internal sealed record EnvironmentRuntimeContext(
    MetricsRegistry Metrics,
    object Gate,
    Func<bool> Initialized,
    string ProfileName,
    Func<long> Epoch,
    JobObject Job,
    Func<JsonObject?>? ScreenLease)
{
    public Func<int>? RecoveredPid { get; init; }
    public Func<string?>? RecoveredStartedAt { get; init; }
    public Func<long?>? StateAgeMs { get; init; }
    public Func<InputLeaseView?>? InputLease { get; init; }
}

internal sealed record EnvironmentClientContext(
    ClientSessionRegistry Clients,
    Func<JsonArray> ClientSummaries);

internal sealed record EnvironmentAppContext(
    TrackedAppRegistry Apps,
    Func<TrackedApp, bool> IsOwned,
    Func<TrackedApp, bool> IsLive,
    Action PruneApps,
    UiaHost Uia);

internal sealed record EnvironmentHealContext(
    Func<bool> SelfHealed,
    Func<int> SelfHealCount,
    Func<string?> LastSelfHealReason,
    Func<bool> AdoptedOrphan,
    Func<int> DisplayRecreateCount,
    Func<int> VddReopenCount,
    Func<bool> DisplayPositionFallback);

internal sealed record EnvironmentDisplayContext(
    VddDevice Vdd,
    Func<int> DisplayIndex,
    Func<string> DisplayDevice,
    Func<DisplayInfo> Ensure,
    Func<DisplayInfo?> OwnDisplay);

/// <summary>get_environment 的只读诊断组装；不持有状态。</summary>
internal sealed class EnvironmentReporter
{
    private readonly EnvironmentRuntimeContext _runtime;
    private readonly EnvironmentClientContext _clients;
    private readonly EnvironmentAppContext _apps;
    private readonly EnvironmentHealContext _heal;
    private readonly EnvironmentDisplayContext _display;

    public EnvironmentReporter(EnvironmentRuntimeContext runtime, EnvironmentClientContext clients, EnvironmentAppContext apps, EnvironmentHealContext heal, EnvironmentDisplayContext display)
    {
        _runtime = runtime;
        _clients = clients;
        _apps = apps;
        _heal = heal;
        _display = display;
    }

    private sealed record ProbeInputs(
        string DriverStatus,
        DisplayInfo? ParsecDisplay,
        int VirtualX,
        int VirtualY,
        int VirtualWidth,
        int VirtualHeight,
        DisplayInfo? Primary,
        int MonitorCount,
        IntPtr Foreground);

    public EnvironmentView GetEnvironment(bool ensure)
    {
        if (ensure)
        {
            _display.Ensure();
        }

        _apps.PruneApps();

        ProbeInputs probe = ProbeOutsideGate();
        EnvironmentView view = BuildLockedView(probe, out int? focusedPid);
        JsonNode? focusedElement = ReadFocusedElement(focusedPid);

        int focusRedactions = 0;
        PrivacyRedactor.RedactJsonStrings(focusedElement, ref focusRedactions);

        return view with
        {
            PrivacyRedactions = focusRedactions > 0 ? focusRedactions : null,
            FocusedElement = focusedElement,
        };
    }

    /// <summary>锁外做可能慢的驱动/显示枚举：launch/release 持 _gate 时 get_environment 不应被拖住。</summary>
    private ProbeInputs ProbeOutsideGate()
    {
        string driverStatus = VddDevice.QueryStatus().ToString();
        DisplayInfo? parsecDisplay = _display.OwnDisplay();
        (int vx, int vy, int vw, int vh) = DisplayTopology.VirtualScreenBounds();
        DisplayInfo? primaryDisplay = DisplayTopology.FindPrimary();
        int monitorCount = EnvironmentProbe.GetMonitorCount();
        IntPtr foregroundWindow = WindowManager.GetForegroundWindow();

        return new ProbeInputs(
            driverStatus, parsecDisplay, vx, vy, vw, vh, primaryDisplay, monitorCount, foregroundWindow);
    }

    private EnvironmentView BuildLockedView(ProbeInputs probe, out int? focusedPid)
    {
        lock (_runtime.Gate)
        {
            string? driverVersion = ReadDriverVersion();
            int sessionId = CurrentSessionId();
            (List<string> clientRoots, JsonArray clientList, int otherClients, int? proxyPid, string? clientVersion) =
                ReadClientContext();
            (double? workingSetMb, int? handles) = ReadProcessMetrics();

            SessionView session = BuildSessionView(new SessionSnapshotInput(
                sessionId,
                new SessionClientSnapshot(clientRoots, clientList, otherClients, proxyPid, clientVersion),
                new SessionUsage(workingSetMb, handles)));

            (VirtualDisplayView virtualDisplay, VirtualScreenView virtualScreen, PrimaryView primaryView) =
                BuildDisplayViews(probe);

            (ForegroundView foregroundView, TrackedApp? foregroundApp) = BuildForegroundView(probe.Foreground);
            CursorView cursorView = BuildCursorView();

            bool includeOwner = _clients.Clients.Current?.IncludeAllApps == true;
            List<AppView> apps = BuildAppViews(includeOwner);

            focusedPid = foregroundApp?.Pid;

            return new EnvironmentView
            {
                Driver = new DriverView { Status = probe.DriverStatus, Version = driverVersion },
                Session = session,
                VirtualDisplay = virtualDisplay,
                VirtualScreen = virtualScreen,
                Primary = primaryView,
                Foreground = foregroundView,
                Cursor = cursorView,
                Apps = apps,
                Metrics = _runtime.Metrics.ToView(),
            };
        }
    }

    private string? ReadDriverVersion()
    {
        if (!_display.Vdd.IsOpen)
        {
            return null;
        }

        int version = _display.Vdd.GetVersion();
        return version >= 0 ? $"{((version >> 16) & 0xFFFF)}.{version & 0xFFFF}" : null;
    }

    private static int CurrentSessionId()
    {
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        return currentProcess.SessionId;
    }

    private (List<string> Roots, JsonArray Clients, int OtherClients, int? ProxyPid, string? ClientVersion)
        ReadClientContext()
    {
        var clientRoots = new List<string>();

        foreach (string root in _clients.Clients.CurrentRoots)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                clientRoots.Add(root);
            }
        }

        JsonArray clientList = _clients.ClientSummaries();
        int otherClients = clientList.Count;

        if (_clients.Clients.Current is null || !_clients.Clients.Current.IncludeAllApps)
        {
            otherClients = Math.Max(0, otherClients - 1);
        }

        int? proxyPid = null;
        string? clientVersion = null;

        if (_clients.Clients.Current is { ProxyPid: > 0 } currentClient)
        {
            proxyPid = currentClient.ProxyPid;

            if (!string.IsNullOrWhiteSpace(currentClient.Version))
            {
                clientVersion = currentClient.Version;
            }
        }

        return (clientRoots, clientList, otherClients, proxyPid, clientVersion);
    }

    private static (double? WorkingSetMb, int? Handles) ReadProcessMetrics()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return (Math.Round(process.WorkingSet64 / 1048576.0, 1), process.HandleCount);
        }
        catch (Exception exception)
        {
            // 进程句柄被回收时忽略；字段保持缺省，不影响诊断的其他部分。
            LogSink.Debug($"忽略异常: {exception.Message}");
            return (null, null);
        }
    }

    private SessionView BuildSessionView(SessionSnapshotInput input) =>
        new()
        {
            Pid = Environment.ProcessId,
            Initialized = _runtime.Initialized(),
            MutexHost = Profile.MutexName(_runtime.ProfileName),
            StartedAt = EnvironmentProbe.ProcessStartTime(),
            SessionId = input.SessionId,
            Interactive = Environment.UserInteractive,
            RemoteSession = NativeMethods.GetSystemMetrics(0x1000) != 0,
            InputDesktop = EnvironmentProbe.IsInputDesktopAvailable(),
            Elevated = Elevation.IsProcessElevated(),
            Isolation = "shared_desktop",
            Profile = _runtime.ProfileName,
            Epoch = _runtime.Epoch(),
            JobObject = _runtime.Job.Status,
            Role = "daemon",
            DaemonPid = Environment.ProcessId,
            ClientId = _clients.Clients.CurrentId,
            ClientCwd = _clients.Clients.CurrentCwd,
            ClientRoots = input.Clients.Roots,
            ProxyPid = input.Clients.ProxyPid,
            ClientVersion = input.Clients.Version,
            Clients = input.Clients.List,
            OtherClients = input.Clients.OtherClients,
            RecoveredApps = _apps.Apps.Snapshot().Count(app => app.Recovered),
            PreviousPid = _runtime.RecoveredPid?.Invoke() is int previousPid && previousPid > 0
                ? previousPid
                : null,
            PreviousStartedAt = _runtime.RecoveredStartedAt?.Invoke() is { Length: > 0 } startedAt
                ? startedAt
                : null,
            StateAgeMs = _runtime.StateAgeMs?.Invoke(),
            InputLease = _runtime.InputLease?.Invoke(),
            ScreenLease = _runtime.ScreenLease?.Invoke(),
            WorkingSetMb = input.Usage.WorkingSetMb,
            Handles = input.Usage.Handles,
        };

    private (VirtualDisplayView VirtualDisplay, VirtualScreenView VirtualScreen, PrimaryView Primary)
        BuildDisplayViews(ProbeInputs probe)
    {
        DisplayInfo? display = probe.ParsecDisplay;
        VirtualDisplayView virtualDisplay;

        if (display is null)
        {
            virtualDisplay = new VirtualDisplayView
            {
                Enabled = false,
                Owned = false,
                SelfHealed = _heal.SelfHealed(),
                SelfHealCount = _heal.SelfHealCount(),
                LastSelfHealReason = _heal.LastSelfHealReason(),
                AdoptedOrphan = _heal.AdoptedOrphan(),
                DisplayRecreateCount = _heal.DisplayRecreateCount(),
                VddReopenCount = _heal.VddReopenCount(),
                PositionEnforced = true,
            };
        }
        else
        {
            virtualDisplay = new VirtualDisplayView
            {
                Enabled = true,
                Owned = _display.DisplayIndex() >= 0 &&
                        !string.IsNullOrWhiteSpace(_display.DisplayDevice()) &&
                        string.Equals(display.DeviceName, _display.DisplayDevice(), StringComparison.OrdinalIgnoreCase),
                Device = display.DeviceName,
                X = display.X,
                Y = display.Y,
                Width = display.Width,
                Height = display.Height,
                Dpi = (int)display.Dpi,
                ScalePercent = display.Dpi == 0 ? 0 : (int)Math.Round(display.Dpi / 96.0 * 100),
                SelfHealed = _heal.SelfHealed(),
                SelfHealCount = _heal.SelfHealCount(),
                LastSelfHealReason = _heal.LastSelfHealReason(),
                AdoptedOrphan = _heal.AdoptedOrphan(),
                DisplayRecreateCount = _heal.DisplayRecreateCount(),
                VddReopenCount = _heal.VddReopenCount(),
                PositionEnforced = !_heal.DisplayPositionFallback(),
                Lifetime = "host_session",
            };
        }

        var virtualScreen = new VirtualScreenView
        {
            X = probe.VirtualX,
            Y = probe.VirtualY,
            Width = probe.VirtualWidth,
            Height = probe.VirtualHeight,
            Monitors = probe.MonitorCount,
        };

        DisplayInfo? primary = probe.Primary;
        var primaryView = primary is null
            ? new PrimaryView()
            : new PrimaryView
            {
                Device = primary.DeviceName,
                X = primary.X,
                Y = primary.Y,
                Width = primary.Width,
                Height = primary.Height,
                Dpi = (int)primary.Dpi,
            };

        return (virtualDisplay, virtualScreen, primaryView);
    }

    private (ForegroundView View, TrackedApp? App) BuildForegroundView(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero)
        {
            return (new ForegroundView(), null);
        }

        _ = NativeMethods.GetWindowThreadProcessId(foreground, out uint foregroundPid);
        int pid = (int)foregroundPid;
        TrackedApp? foregroundApp = _apps.Apps.FirstOrDefault(
            app => _apps.IsOwned(app) && app.Pid == pid && _apps.IsLive(app));

        // 标题仍然只对本会话窗口可见（用户主屏的邮件主题/文档名不外泄），
        // 但 hwnd/pid/进程名/是否提权必须给：否则"焦点被谁抢了"完全不可排查。
        string? name = null;

        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        bool? elevated = IntegrityCheck.TryCompareIntegrity(foregroundPid, out int target, out int self)
            ? target > self
            : null;

        var view = new ForegroundView
        {
            Tracked = foregroundApp is not null,
            Hwnd = foreground.ToInt64(),
            Pid = pid,
            Title = foregroundApp is null ? null : WindowManager.GetTitle(foreground),
            Name = name,
            Elevated = elevated,
        };

        return (view, foregroundApp);
    }

    private static CursorView BuildCursorView()
    {
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
        {
            return new CursorView { X = cursor.X, Y = cursor.Y };
        }

        return new CursorView();
    }

    private List<AppView> BuildAppViews(bool includeOwner)
    {
        var apps = new List<AppView>();

        foreach (TrackedApp app in _apps.Apps.Where(app => _apps.IsOwned(app) || app.Recovered))
        {
            WindowInfo? liveWindow = app.Hwnd == IntPtr.Zero
                ? null
                : WindowManager.FromHandle(app.Hwnd);

            apps.Add(new AppView
            {
                Pid = app.Pid,
                Hwnd = app.Hwnd.ToInt64(),
                Title = liveWindow?.Title ?? app.Title,
                Exe = app.ExePath,
                Alive = _apps.IsLive(app),
                LaunchedByUs = app.StartedProcess,
                Owned = app.Owned,
                CloseOnRelease = app.CloseWindowOnRelease,
                Dedicated = app.DedicatedProcess,
                Detached = app.Detached,
                Recovered = app.Recovered,
                ClientId = includeOwner ? app.OwnerClientId : null,
            });
        }

        return apps;
    }

    /// <summary>UIA 读取放到 _gate 外：即使 provider 卡住，release_environment 仍能进 _gate 清理。</summary>
    private JsonNode? ReadFocusedElement(int? focusedPid)
    {
        if (focusedPid is null)
        {
            return null;
        }

        try
        {
            UiaElementInfo? focused = _apps.Uia.GetFocusedElement();
            JsonNode? focusedElement = focused is null ? null : ElementPresenter.ToJson(focused);

            // 二次校验：焦点元素必须属于当前客户端（含 UWP 内容进程）。
            if (focusedElement is JsonObject focus &&
                focus["process_id"] is JsonValue pidValue &&
                pidValue.TryGetValue(out int focusPid))
            {
                bool owned;

                lock (_runtime.Gate)
                {
                    owned = _apps.Apps.Any(app =>
                        _apps.IsOwned(app) &&
                        (app.Pid == focusPid || app.ContentPid == focusPid));
                }

                if (!owned)
                {
                    focusedElement = null;
                }
            }

            return focusedElement;
        }
        catch (Exception exception)
        {
            return new JsonObject { ["error"] = exception.Message };
        }
    }
}
