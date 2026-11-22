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

internal sealed partial class AppLauncher
{
    private void TrackWindow(LaunchState s)
    {
        if (s.Window is null)
        {
            throw new ToolException("E_WINDOW_TIMEOUT", "等待应用主窗口超时");
        }

        // 启动壳进程可能已把窗口交接给新进程；以 hwnd 的真实属主为准。
        s.Window = WindowManager.FromHandle(s.Window!.Hwnd) ?? s.Window;

        s.Window = WaitForStableWindow(s.Window, s.WaitMs, s.Display.Width * s.Display.Height) ?? s.Window;

        // UWP 的 frame host 路径不是用户要启动的应用；内容进程解析失败时才退回 frame。
        if (s.ContentPid == 0)
        {
            (s.ContentPid, s.ContentPath) = ResolveUwpContentProcess(s.Window!.Hwnd);
        }

        // path 启动时已校验；pid/window 接管也必须校验真实 exe，避免绕过 VSP_APP_ALLOWLIST（R24）。
        string allowPath = string.IsNullOrEmpty(s.ContentPath)
            ? WindowManager.GetProcessPath(s.Window!.Pid)
            : s.ContentPath;
        _host.EnsureAppAllowed(allowPath);

        lock (_gate)
        {
            s.Tracked = _host.Apps.FirstOrDefault(app => _host.IsOwned(app) && app.Hwnd == s.Window!.Hwnd)
                ?? _host.Apps.FirstOrDefault(app => _host.IsOwned(app) && app.Pid == s.Window!.Pid);

            TrackedApp? foreign = _host.Apps.FirstOrDefault(app =>
                !_host.IsOwned(app) && (app.Hwnd == s.Window!.Hwnd || app.Pid == s.Window!.Pid));

            if (s.Tracked is null && foreign is not null)
            {
                throw new ToolException(
                    "E_APP_OWNED",
                    $"窗口属于另一个客户端（client={foreign.OwnerClientId}）",
                    new JsonObject
                    {
                        ["hwnd"] = s.Window!.Hwnd.ToInt64(),
                        ["pid"] = s.Window!.Pid,
                        ["owner_client_id"] = foreign.OwnerClientId,
                        ["owner_cwd"] = _host.Clients.FindCwd(foreign.OwnerClientId),
                    },
                    retryable: true,
                    hint: "让该客户端 release_environment，或为当前客户端启动一个独立实例",
                    stage: "app");
            }

            if (s.Tracked is null)
            {
                s.Tracked = new TrackedApp
                {
                    Pid = s.Window!.Pid,
                    OwnerClientId = _host.Clients.CurrentId,
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
                    Placement = WindowManager.GetPlacement(s.Window!.Hwnd),
                    PlacementSaved = true,
                };
                _host.Apps.Add(s.Tracked);
                _log.Info(
                    $"接管应用 pid={s.Window!.Pid} started={s.Started} newInstance={s.NewInstance}");
            }
            else
            {
                s.Tracked.Hwnd = s.Window!.Hwnd;
                s.Tracked.Title = s.Window!.Title;
                s.Tracked.Owned |= s.Started && s.CloseOnRelease;
                s.Tracked.CloseWindowOnRelease |= s.CloseOnRelease;

                if (s.ContentPid > 0)
                {
                    s.Tracked.ContentPid = s.ContentPid;
                    s.Tracked.ContentExePath = s.ContentPath;
                }
            }
        }

        // 现代应用（Edge/Notepad）由 stub 转交给真实进程，早期分配的作业对象不覆盖它。
        if (s.Started && s.Dedicated)
        {
            AssignWindowProcessToJob(s.Window!.Pid);
        }
    }

    private void EnsureDocumentReady(LaunchState s)
    {
        bool word = s.ExeName.Equals("winword", StringComparison.OrdinalIgnoreCase);
        bool powerpoint = s.ExeName.Equals("powerpnt", StringComparison.OrdinalIgnoreCase);
        bool excel = s.ExeName.Equals("excel", StringComparison.OrdinalIgnoreCase);

        if (word)
        {
            s.DocumentReady = _host.Uia.FindElement(s.Window!.Hwnd, null, "Document", null, null) is not null;
            s.DocumentReason = s.DocumentReady == true ? "document_element" : "no_document_element";

            // 仅对本会话新启动的专用 Word 自动新建文档；复用用户 Word 时不动其文档状态。
            if (s.DocumentReady != true && s.EnsureDocument && s.Started && s.CloseOnRelease)
            {
                _host.PauseFocusGuard();

                try
                {
                    if (s.Tracked is not null && _host.EnsureForeground(s.Tracked))
                    {
                        InputInjector.PressKeys("Ctrl+N");
                        s.DocumentReady = WaitForWordDocument(s.Window!.Hwnd, 6000);
                        s.DocumentReason = s.DocumentReady == true ? "created_by_ctrl_n" : "ctrl_n_failed";
                    }
                }
                finally
                {
                    _host.RestoreForeground(s.PreviousForeground);
                    _host.ResumeFocusGuard();
                }
            }

            if (s.DocumentReady != true)
            {
                s.Warnings.Add("Word 当前没有可编辑文档；可传 ensure_document=true 或手动 Ctrl+N");
            }
        }
        else if (powerpoint)
        {
            string currentTitle = WindowManager.GetTitle(s.Window!.Hwnd);
            s.DocumentReady = LooksLikeDocumentWindow(currentTitle, "PowerPoint");
            s.DocumentReason = s.DocumentReady == true ? "document_title" : "start_screen_or_no_document";

            if (s.DocumentReady != true && s.EnsureDocument && s.Started && s.CloseOnRelease && s.Tracked is not null)
            {
                _host.PauseFocusGuard();

                try
                {
                    if (_host.EnsureForeground(s.Tracked))
                    {
                        InputInjector.PressKeys("Ctrl+N");
                        s.DocumentReady = WaitForDocumentTitle(s.Window!.Hwnd, "PowerPoint", 5000);

                        if (s.DocumentReady != true)
                        {
                            // 开始屏幕的“空白演示文稿”磁贴是普通按钮；按名字找，别猜坐标。
                            UiaElementInfo? tile = FindBlankPresentationTile(s.Window!.Hwnd);

                            if (tile is not null && tile.Width > 0 && tile.Height > 0)
                            {
                                InputInjector.Click(
                                    tile.Left + tile.Width / 2, tile.Top + tile.Height / 2,
                                    "left", clicks: 1);
                                s.DocumentReady = WaitForDocumentTitle(s.Window!.Hwnd, "PowerPoint", 8000);
                                s.DocumentReason = s.DocumentReady == true ? "blank_tile" : "blank_tile_failed";
                            }
                            else
                            {
                                // 部分 Office 版本的开始屏幕不把磁贴暴露给 UIA。
                                // 按 1280×720 的实测比例估算“空白演示文稿”磁贴中心。
                                int estimateX = s.Display.X + (int)Math.Round(s.Display.Width * 0.15625);
                                int estimateY = s.Display.Y + (int)Math.Round(s.Display.Height * 0.3125);
                                InputInjector.Click(estimateX, estimateY, "left", clicks: 1);
                                s.DocumentReady = WaitForDocumentTitle(s.Window!.Hwnd, "PowerPoint", 8000);
                                s.DocumentReason = s.DocumentReady == true
                                    ? "blank_tile_estimated"
                                    : "blank_tile_failed";
                            }
                        }
                        else
                        {
                            s.DocumentReason = "created_by_ctrl_n";
                        }
                    }
                }
                finally
                {
                    _host.RestoreForeground(s.PreviousForeground);
                    _host.ResumeFocusGuard();
                }

                if (s.DocumentReady != true)
                {
                    s.Warnings.Add("PowerPoint 停在开始屏幕；请输入文件名/点“空白演示文稿”，或手动 Ctrl+N");
                }
            }
        }
        else if (excel)
        {
            s.DocumentReady = LooksLikeDocumentWindow(WindowManager.GetTitle(s.Window!.Hwnd), "Excel");
            s.DocumentReason = s.DocumentReady == true ? "document_title" : "no_workbook";
        }
        else
        {
            // 没有按应用建模时不要猜：返回 null，让调用方自己决定是否 snapshot。
            s.DocumentReady = null;
            s.DocumentReason = "not_modeled";
        }
    }

}
