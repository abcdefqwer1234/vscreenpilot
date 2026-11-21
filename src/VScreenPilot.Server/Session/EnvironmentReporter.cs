using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Json;
using VScreenPilot.Mcp;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Security;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Diagnostics;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Processes;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>环境诊断视图所需的最小宿主接口。</summary>
internal interface IEnvironmentHost
{
    MetricsRegistry Metrics { get; }
    object Gate { get; }
    VddDevice Vdd { get; }
    bool Initialized { get; }
    string ProfileName { get; }
    long Epoch { get; }
    JobObject Job { get; }
    ClientSessionRegistry Clients { get; }
    JsonArray ClientSummaries();
    Func<JsonObject>? ScreenLease { get; }
    TrackedAppRegistry Apps { get; }
    bool IsOwned(TrackedApp app);
    bool IsLive(TrackedApp app);
    UiaHost Uia { get; }
    bool SelfHealed { get; }
    int SelfHealCount { get; }
    string? LastSelfHealReason { get; }
    bool AdoptedOrphan { get; }
    int DisplayRecreateCount { get; }
    int VddReopenCount { get; }
    bool DisplayPositionFallback { get; }
    int DisplayIndex { get; }
    string DisplayDevice { get; }
    DisplayInfo Ensure();
    void PruneApps();
    DisplayInfo? OwnDisplay();
}

/// <summary>get_environment 的只读诊断组装；不持有状态。</summary>
internal sealed class EnvironmentReporter
{
    private readonly IEnvironmentHost _host;

    public EnvironmentReporter(IEnvironmentHost host) => _host = host;

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
            _host.Ensure();
        }

        _host.PruneApps();

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
        DisplayInfo? parsecDisplay = _host.OwnDisplay();
        (int vx, int vy, int vw, int vh) = DisplayTopology.VirtualScreenBounds();
        DisplayInfo? primaryDisplay = DisplayTopology.FindPrimary();
        int monitorCount = EnvironmentProbe.GetMonitorCount();
        IntPtr foregroundWindow = WindowManager.GetForegroundWindow();

        return new ProbeInputs(
            driverStatus, parsecDisplay, vx, vy, vw, vh, primaryDisplay, monitorCount, foregroundWindow);
    }

    private EnvironmentView BuildLockedView(ProbeInputs probe, out int? focusedPid)
    {
        lock (_host.Gate)
        {
            string? driverVersion = ReadDriverVersion();
            int sessionId = CurrentSessionId();
            (List<string> clientRoots, JsonArray clientList, int otherClients, int? proxyPid, string? clientVersion) =
                ReadClientContext();
            (double? workingSetMb, int? handles) = ReadProcessMetrics();

            SessionView session = BuildSessionView(
                sessionId, clientRoots, clientList, otherClients, proxyPid, clientVersion,
                workingSetMb, handles);

            (VirtualDisplayView virtualDisplay, VirtualScreenView virtualScreen, PrimaryView primaryView) =
                BuildDisplayViews(probe);

            (ForegroundView foregroundView, TrackedApp? foregroundApp) = BuildForegroundView(probe.Foreground);
            CursorView cursorView = BuildCursorView();

            bool includeOwner = _host.Clients.Current?.IncludeAllApps == true;
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
                Metrics = _host.Metrics.ToView(),
            };
        }
    }

    private string? ReadDriverVersion()
    {
        if (!_host.Vdd.IsOpen)
        {
            return null;
        }

        int version = _host.Vdd.GetVersion();
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

        foreach (string root in _host.Clients.CurrentRoots)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                clientRoots.Add(root);
            }
        }

        JsonArray clientList = _host.ClientSummaries();
        int otherClients = clientList.Count;

        if (_host.Clients.Current is null || !_host.Clients.Current.IncludeAllApps)
        {
            otherClients = Math.Max(0, otherClients - 1);
        }

        int? proxyPid = null;
        string? clientVersion = null;

        if (_host.Clients.Current is { ProxyPid: > 0 } currentClient)
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

    private SessionView BuildSessionView(
        int sessionId,
        List<string> clientRoots,
        JsonArray clientList,
        int otherClients,
        int? proxyPid,
        string? clientVersion,
        double? workingSetMb,
        int? handles) =>
        new()
        {
            Pid = Environment.ProcessId,
            Initialized = _host.Initialized,
            MutexHost = Profile.MutexName(_host.ProfileName),
            StartedAt = EnvironmentProbe.ProcessStartTime(),
            SessionId = sessionId,
            Interactive = Environment.UserInteractive,
            RemoteSession = NativeMethods.GetSystemMetrics(0x1000) != 0,
            InputDesktop = EnvironmentProbe.IsInputDesktopAvailable(),
            Elevated = Elevation.IsProcessElevated(),
            Isolation = "shared_desktop",
            Profile = _host.ProfileName,
            Epoch = _host.Epoch,
            JobObject = _host.Job.Status,
            Role = "daemon",
            DaemonPid = Environment.ProcessId,
            ClientId = _host.Clients.CurrentId,
            ClientCwd = _host.Clients.CurrentCwd,
            ClientRoots = clientRoots,
            ProxyPid = proxyPid,
            ClientVersion = clientVersion,
            Clients = clientList,
            OtherClients = otherClients,
            ScreenLease = _host.ScreenLease?.Invoke(),
            WorkingSetMb = workingSetMb,
            Handles = handles,
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
                SelfHealed = _host.SelfHealed,
                SelfHealCount = _host.SelfHealCount,
                LastSelfHealReason = _host.LastSelfHealReason,
                AdoptedOrphan = _host.AdoptedOrphan,
                DisplayRecreateCount = _host.DisplayRecreateCount,
                VddReopenCount = _host.VddReopenCount,
                PositionEnforced = true,
            };
        }
        else
        {
            virtualDisplay = new VirtualDisplayView
            {
                Enabled = true,
                Owned = _host.DisplayIndex >= 0 &&
                        !string.IsNullOrWhiteSpace(_host.DisplayDevice) &&
                        string.Equals(display.DeviceName, _host.DisplayDevice, StringComparison.OrdinalIgnoreCase),
                Device = display.DeviceName,
                X = display.X,
                Y = display.Y,
                Width = display.Width,
                Height = display.Height,
                Dpi = (int)display.Dpi,
                ScalePercent = display.Dpi == 0 ? 0 : (int)Math.Round(display.Dpi / 96.0 * 100),
                SelfHealed = _host.SelfHealed,
                SelfHealCount = _host.SelfHealCount,
                LastSelfHealReason = _host.LastSelfHealReason,
                AdoptedOrphan = _host.AdoptedOrphan,
                DisplayRecreateCount = _host.DisplayRecreateCount,
                VddReopenCount = _host.VddReopenCount,
                PositionEnforced = !_host.DisplayPositionFallback,
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
        TrackedApp? foregroundApp = _host.Apps.FirstOrDefault(
            app => _host.IsOwned(app) && app.Pid == (int)foregroundPid && _host.IsLive(app));

        // 只暴露本会话窗口的标题/PID。用户主屏上的窗口标题常含邮件主题、
        // 文档名等隐私信息，不应交给模型。
        var view = new ForegroundView
        {
            Tracked = foregroundApp is not null,
            Hwnd = foregroundApp is null ? null : foreground.ToInt64(),
            Pid = foregroundApp is null ? null : (int)foregroundPid,
            Title = foregroundApp is null ? null : WindowManager.GetTitle(foreground),
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

        foreach (TrackedApp app in _host.Apps.Where(_host.IsOwned))
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
                Alive = _host.IsLive(app),
                LaunchedByUs = app.StartedProcess,
                Owned = app.Owned,
                CloseOnRelease = app.CloseWindowOnRelease,
                Dedicated = app.DedicatedProcess,
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
            UiaElementInfo? focused = _host.Uia.GetFocusedElement();
            JsonNode? focusedElement = focused is null ? null : ElementPresenter.ToJson(focused);

            // 二次校验：焦点元素必须属于当前客户端（含 UWP 内容进程）。
            if (focusedElement is JsonObject focus &&
                focus["process_id"] is JsonValue pidValue &&
                pidValue.TryGetValue(out int focusPid))
            {
                bool owned;

                lock (_host.Gate)
                {
                    owned = _host.Apps.Any(app =>
                        _host.IsOwned(app) &&
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
