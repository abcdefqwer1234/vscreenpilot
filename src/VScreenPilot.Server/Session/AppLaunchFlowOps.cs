using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Input;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Processes;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>LaunchFlowOps：AppLauncher 的拆分子集。</summary>
internal sealed class AppLaunchFlowOps
{
    private readonly AppLauncher _core;

    public AppLaunchFlowOps(AppLauncher core) => _core = core;
    internal void TrackWindow(LaunchState s)
    {
        PrepareWindow(s);
        EnsureAllowed(s);

        lock (_core._gate)
        {
            TrackApp(s);
        }

        // 现代应用（Edge/Notepad）由 stub 转交给真实进程，早期分配的作业对象不覆盖它。
        // detached 明确不加入 Job：daemon 崩溃/退出时应用继续运行。
        if (s.Started && s.Dedicated && !s.Detached)
        {
            _core.WindowOps.AssignWindowProcessToJob(s.Window!.Pid);
        }
    }

    private void PrepareWindow(LaunchState s)
    {
        if (s.Window is null)
        {
            throw new ToolException("E_WINDOW_TIMEOUT", "等待应用主窗口超时");
        }

        // 启动壳进程可能已把窗口交接给新进程；以 hwnd 的真实属主为准。
        s.Window = WindowManager.FromHandle(s.Window!.Hwnd) ?? s.Window;

        s.Window = _core.WindowOps.WaitForStableWindow(
            s.Window, s.WaitMs, s.Display.Width * s.Display.Height) ?? s.Window;

        // UWP 的 frame host 路径不是用户要启动的应用；内容进程解析失败时才退回 frame。
        if (s.ContentPid == 0)
        {
            (s.ContentPid, s.ContentPath) = _core.ResolveUwpContentProcess(s.Window!.Hwnd);
        }
    }

    private void EnsureAllowed(LaunchState s)
    {
        // path 启动时已校验；pid/window 接管也必须校验真实 exe，避免绕过 VSP_APP_ALLOWLIST（R24）。
        string allowPath = string.IsNullOrEmpty(s.ContentPath)
            ? WindowManager.GetProcessPath(s.Window!.Pid)
            : s.ContentPath;
        _core._apps.EnsureAppAllowed(allowPath);
    }

    private void TrackApp(LaunchState s)
    {
        s.Tracked = _core._apps.Apps.FirstOrDefault(app => _core._apps.IsOwned(app) && app.Hwnd == s.Window!.Hwnd)
            ?? _core._apps.Apps.FirstOrDefault(app => _core._apps.IsOwned(app) && app.Pid == s.Window!.Pid);

        TrackedApp? foreign = _core._apps.Apps.FirstOrDefault(app =>
            !_core._apps.IsOwned(app) && (app.Hwnd == s.Window!.Hwnd || app.Pid == s.Window!.Pid));

        if (s.Tracked is null && foreign is not null)
        {
            // 崩溃恢复的窗口没有归属；谁先显式接管就归谁。
            if (foreign.Recovered)
            {
                foreign.OwnerClientId = _core._session.Clients.CurrentId;
                foreign.Recovered = false;
                s.Tracked = foreign;
                UpdateTrackedApp(s);
                return;
            }

            throw new ToolException(
                "E_APP_OWNED",
                $"窗口属于另一个客户端（client={foreign.OwnerClientId}）",
                new JsonObject
                {
                    ["hwnd"] = s.Window!.Hwnd.ToInt64(),
                    ["pid"] = s.Window!.Pid,
                    ["owner_client_id"] = foreign.OwnerClientId,
                    ["owner_cwd"] = _core._session.Clients.FindCwd(foreign.OwnerClientId),
                },
                retryable: true,
                hint: "让该客户端 release_environment，或为当前客户端启动一个独立实例",
                stage: "app");
        }

        if (s.Tracked is null)
        {
            RegisterTrackedApp(s);
            return;
        }

        UpdateTrackedApp(s);
    }

    private void RegisterTrackedApp(LaunchState s)
    {
        s.Tracked = new TrackedApp
        {
            Pid = s.Window!.Pid,
            OwnerClientId = _core._session.Clients.CurrentId,
            ClientKey = ClientSessionRegistry.ComputeClientKey(
                _core._session.Clients.CurrentCwd, _core._session.Clients.CurrentRoots),
            ExePath = WindowManager.GetProcessPath(s.Window!.Pid),
            ContentPid = s.ContentPid,
            ContentExePath = s.ContentPath,
            Hwnd = s.Window!.Hwnd,
            Title = s.Window!.Title,
            StartedProcess = s.Started,
            Owned = s.Started && s.CloseOnRelease,
            CloseWindowOnRelease = s.CloseOnRelease,
            DedicatedProcess = s.Dedicated,
            TempProfile = s.TempProfile,
            Detached = s.Detached,
            Placement = WindowManager.GetPlacement(s.Window!.Hwnd),
            PlacementSaved = true,
        };
        _core._apps.Apps.Add(s.Tracked);
        _core._log.Info(
            $"接管应用 pid={s.Window!.Pid} started={s.Started} newInstance={s.NewInstance}");
    }

    private static void UpdateTrackedApp(LaunchState s)
    {
        bool hwndChanged = s.Tracked!.Hwnd != s.Window!.Hwnd;
        s.Tracked.Hwnd = s.Window!.Hwnd;
        s.Tracked.Title = s.Window!.Title;
        s.Tracked.Owned |= s.Started && s.CloseOnRelease;
        s.Tracked.CloseWindowOnRelease |= s.CloseOnRelease;

        // 按 hwnd 接管对话框时，release 要还原的是这个对话框的位置，而不是主窗口的位置。
        if (hwndChanged)
        {
            s.Tracked.Placement = WindowManager.GetPlacement(s.Window.Hwnd);
            s.Tracked.PlacementSaved = true;
        }

        if (s.ContentPid <= 0)
        {
            return;
        }

        s.Tracked.ContentPid = s.ContentPid;
        s.Tracked.ContentExePath = s.ContentPath;
    }

    internal void EnsureDocumentReady(LaunchState s)
    {
        if (s.ExeName.Equals("winword", StringComparison.OrdinalIgnoreCase))
        {
            EnsureWordDocument(s);
            return;
        }

        if (s.ExeName.Equals("powerpnt", StringComparison.OrdinalIgnoreCase))
        {
            EnsurePowerPointDocument(s);
            return;
        }

        if (s.ExeName.Equals("excel", StringComparison.OrdinalIgnoreCase))
        {
            EnsureExcelDocument(s);
            return;
        }

        // 没有按应用建模时不要猜：返回 null，让调用方自己决定是否 snapshot。
        s.DocumentReady = null;
        s.DocumentReason = "not_modeled";
    }

    private static bool ShouldAutoCreateDocument(LaunchState s) =>
        s.EnsureDocument && s.Started && s.CloseOnRelease;

    private void EnsureWordDocument(LaunchState s)
    {
        s.DocumentReady = _core._apps.Uia.FindElement(s.Window!.Hwnd, null, "Document", null, null) is not null;
        s.DocumentReason = s.DocumentReady == true ? "document_element" : "no_document_element";

        // 仅对本会话新启动的专用 Word 自动新建文档；复用用户 Word 时不动其文档状态。
        if (s.DocumentReady != true && ShouldAutoCreateDocument(s))
        {
            TryCreateWordDocument(s);
        }

        if (s.DocumentReady != true)
        {
            s.Warnings.Add("Word 当前没有可编辑文档；可传 ensure_document=true 或手动 Ctrl+N");
        }
    }

    private void TryCreateWordDocument(LaunchState s)
    {
        _core._focus.PauseFocusGuard();

        try
        {
            if (s.Tracked is not null && _core._focus.EnsureForeground(s.Tracked))
            {
                InputInjector.PressKeys("Ctrl+N");
                s.DocumentReady = _core.DocumentOps.WaitForWordDocument(s.Window!.Hwnd, 6000);
                s.DocumentReason = s.DocumentReady == true ? "created_by_ctrl_n" : "ctrl_n_failed";
            }
        }
        finally
        {
            _core._focus.RestoreForeground(s.PreviousForeground);
            _core._focus.ResumeFocusGuard();
        }
    }

    private void EnsurePowerPointDocument(LaunchState s)
    {
        string currentTitle = WindowManager.GetTitle(s.Window!.Hwnd);
        s.DocumentReady = AppDocumentOps.LooksLikeDocumentWindow(currentTitle, "PowerPoint");
        s.DocumentReason = s.DocumentReady == true ? "document_title" : "start_screen_or_no_document";

        if (s.DocumentReady != true && ShouldAutoCreateDocument(s) && s.Tracked is not null)
        {
            TryCreatePowerPointDocument(s);

            if (s.DocumentReady != true)
            {
                s.Warnings.Add("PowerPoint 停在开始屏幕；请输入文件名/点“空白演示文稿”，或手动 Ctrl+N");
            }
        }
    }

    private void TryCreatePowerPointDocument(LaunchState s)
    {
        _core._focus.PauseFocusGuard();

        try
        {
            if (!_core._focus.EnsureForeground(s.Tracked!))
            {
                return;
            }

            InputInjector.PressKeys("Ctrl+N");
            s.DocumentReady = _core.DocumentOps.WaitForDocumentTitle(s.Window!.Hwnd, "PowerPoint", 5000);

            if (s.DocumentReady == true)
            {
                s.DocumentReason = "created_by_ctrl_n";
                return;
            }

            ClickBlankPresentationTile(s);
        }
        finally
        {
            _core._focus.RestoreForeground(s.PreviousForeground);
            _core._focus.ResumeFocusGuard();
        }
    }

    private void ClickBlankPresentationTile(LaunchState s)
    {
        // 开始屏幕的“空白演示文稿”磁贴是普通按钮；按名字找，别猜坐标。
        UiaElementInfo? tile = _core.DocumentOps.FindBlankPresentationTile(s.Window!.Hwnd);

        if (tile is not null && tile.Width > 0 && tile.Height > 0)
        {
            InputInjector.Click(tile.Left + tile.Width / 2, tile.Top + tile.Height / 2, "left", clicks: 1);
            s.DocumentReady = _core.DocumentOps.WaitForDocumentTitle(s.Window!.Hwnd, "PowerPoint", 8000);
            s.DocumentReason = s.DocumentReady == true ? "blank_tile" : "blank_tile_failed";
            return;
        }

        // 部分 Office 版本的开始屏幕不把磁贴暴露给 UIA。
        // 按 1280×720 的实测比例估算“空白演示文稿”磁贴中心。
        int estimateX = s.Display.X + (int)Math.Round(s.Display.Width * 0.15625);
        int estimateY = s.Display.Y + (int)Math.Round(s.Display.Height * 0.3125);
        InputInjector.Click(estimateX, estimateY, "left", clicks: 1);
        s.DocumentReady = _core.DocumentOps.WaitForDocumentTitle(s.Window!.Hwnd, "PowerPoint", 8000);
        s.DocumentReason = s.DocumentReady == true ? "blank_tile_estimated" : "blank_tile_failed";
    }

    private void EnsureExcelDocument(LaunchState s)
    {
        s.DocumentReady = AppDocumentOps.LooksLikeDocumentWindow(WindowManager.GetTitle(s.Window!.Hwnd), "Excel");
        s.DocumentReason = s.DocumentReady == true ? "document_title" : "no_workbook";
    }
}
