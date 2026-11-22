using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>element_ref 解析所需的最小宿主接口。</summary>
internal interface IElementResolverHost
{
    ElementRefStore Refs { get; }
    string CurrentClientId { get; }
    string SnapshotId { get; }
    UiaHost Uia { get; }
    bool IsTrackedPid(int pid);
    void RequireOnDisplay(DisplayInfo display, IntPtr hwnd, string label);
    LogRing Log { get; }
}

/// <summary>element_ref 校验与 UIA 句柄重定位（runtime_id → 指纹）。</summary>
internal sealed class ElementResolver
{
    private readonly IElementResolverHost _host;

    public ElementResolver(IElementResolverHost host) => _host = host;

    public ElementRefEntry Require(string reference)
    {
        string key = (reference ?? string.Empty).Trim();

        if (key.Length == 0)
        {
            throw new ToolException("E_INVALID_ARG", "element_ref 不能为空");
        }

        ElementRefEntry? entry = _host.Refs.TryResolve(_host.CurrentClientId, key, out bool stale);

        if (stale)
        {
            throw new ToolException(
                "E_STALE_SNAPSHOT",
                $"element_ref {key} 属于旧快照，已失效",
                new JsonObject { ["ref"] = key, ["current_snapshot"] = _host.SnapshotId },
                retryable: true,
                hint: "重新调用 snapshot，并只使用新返回的 ref",
                stage: "snapshot");
        }

        if (entry is null)
        {
            throw new ToolException(
                "E_ELEMENT_STALE",
                $"未知或已失效的 element_ref: {key}，请重新 get_ui_tree / find_element",
                new JsonObject { ["ref"] = key });
        }

        return entry;
    }

    /// <summary>
    /// 把 element_ref 解析为可执行模式的 UIA 句柄：先按 runtime_id 定位，
    /// 失败再按指纹唯一重定位；仍失败则 E_ELEMENT_STALE（不猜）。
    /// </summary>
    public UiaElementHandle Resolve(
        string reference, DisplayInfo display, out ElementRefEntry entry, out bool refreshed)
    {
        entry = Require(reference);
        refreshed = false;

        if (entry.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(entry.Hwnd))
        {
            throw new ToolException(
                "E_ELEMENT_STALE",
                "element_ref 所属窗口已关闭",
                new JsonObject { ["ref"] = entry.Ref, ["hwnd"] = entry.Hwnd.ToInt64() });
        }

        bool tracked;
        int entryPid = entry.Pid;

        // 句柄可能被系统复用：必须确认该 HWND 仍属于登记时的进程。
        _ = NativeMethods.GetWindowThreadProcessId(entry.Hwnd, out uint entryHwndPid);

        if (entryHwndPid != (uint)entryPid)
        {
            throw new ToolException(
                "E_ELEMENT_STALE",
                "element_ref 所属窗口已不属于登记时的进程（可能发生了 PID/句柄复用）",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["pid"] = entry.Pid,
                    ["actual_pid"] = (int)entryHwndPid,
                });
        }

        tracked = _host.IsTrackedPid(entryPid);

        if (!tracked)
        {
            throw new ToolException(
                "E_NO_TARGET",
                "element_ref 所属应用已不在本会话中，请重新 launch_or_place_app",
                new JsonObject { ["ref"] = entry.Ref, ["pid"] = entry.Pid });
        }

        _host.RequireOnDisplay(display, entry.Hwnd, "element_ref");

        UiaElementHandle? handle = null;

        if (!string.IsNullOrEmpty(entry.RuntimeId))
        {
            handle = _host.Uia.FindByRuntimeId(entry.Hwnd, entry.RuntimeId);
        }

        if (handle is not null)
        {
            entry.Update(handle.Info, entry.Hwnd);
            return handle;
        }

        handle = _host.Uia.FindByFingerprint(
            entry.Hwnd, entry.Name, entry.AutomationId, entry.ControlTypeName,
            entry.Left, entry.Top, entry.Right, entry.Bottom);

        // 控件可能被移动到同进程的另一个顶层窗口（保存框、浮动面板等）。
        if (handle is null)
        {
            foreach (WindowInfo candidate in WindowManager.ListForProcess(entry.Pid))
            {
                if (candidate.Hwnd == entry.Hwnd)
                {
                    continue;
                }

                handle = _host.Uia.FindByFingerprint(
                    candidate.Hwnd, entry.Name, entry.AutomationId, entry.ControlTypeName,
                    entry.Left, entry.Top, entry.Right, entry.Bottom);

                if (handle is not null)
                {
                    entry.Hwnd = candidate.Hwnd;
                    break;
                }
            }
        }

        if (handle is null)
        {
            throw new ToolException(
                "E_ELEMENT_STALE",
                "控件已销毁或无法唯一重定位，请重新 get_ui_tree / find_element",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["name"] = entry.Name,
                    ["automation_id"] = entry.AutomationId,
                    ["control_type"] = entry.ControlTypeName,
                });
        }

        refreshed = true;
        entry.Update(handle.Info, entry.Hwnd);
        _host.Log.Info($"element_ref {entry.Ref} 已按指纹重定位");
        return handle;
    }

    /* ------------------------------------------------------------------ */
    /* 工具：get_ui_tree                                                   */
    /* ------------------------------------------------------------------ */
}
