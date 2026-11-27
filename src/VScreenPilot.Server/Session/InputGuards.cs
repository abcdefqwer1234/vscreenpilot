using System.Text.Json.Nodes;
using VScreenPilot.Errors;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Ipc;
using VScreenPilot.Platform.Windows;

namespace VScreenPilot.Session;

/// <summary>
/// 目标进程权限护栏：目标完整性级别高于服务端时，UIPI 会让点击/输入“没反应”。
/// 查询失败（受保护进程等）不据此拦截，避免把“查不到”当成“更高权限”。
/// </summary>
internal static class ElevationGuard
{
    public static void RequireNotElevated(int pid, string operation)
    {
        if (pid <= 0 || !IntegrityCheck.TryCompareIntegrity((uint)pid, out int target, out int self))
        {
            return;
        }

        if (target <= self)
        {
            return;
        }

        throw new ToolException(
            "E_ELEVATION_REQUIRED",
            $"{operation} 的目标进程完整性级别高于服务端；UIPI 会丢弃输入",
            new JsonObject
            {
                ["pid"] = pid,
                ["target_integrity"] = target,
                ["server_integrity"] = self,
            },
            retryable: false,
            hint: "以与目标相同的权限运行服务端；本工具不会自动提权",
            stage: "security");
    }
}

/// <summary>
/// 模态对话框护栏：窗口被禁用说明有模态框在阻塞它。
/// 只把它作为“显式失败”返回，不自动点掉未知弹窗。
/// </summary>
internal static class BlockingDialogGuard
{
    public static void RequireNotBlocked(IntPtr hwnd, string operation)
    {
        if (hwnd == IntPtr.Zero ||
            !NativeMethods.IsWindow(hwnd) ||
            WindowManager.IsEnabled(hwnd))
        {
            return;
        }

        int pid = WindowManager.GetProcessId(hwnd);
        var dialogs = new JsonArray();

        foreach (WindowInfo dialog in WindowManager.ListOwnedWindows(pid, hwnd))
        {
            if (string.Equals(dialog.ClassName, "#32768", StringComparison.Ordinal))
            {
                continue;
            }

            dialogs.Add((JsonNode)new JsonObject
            {
                ["hwnd"] = dialog.Hwnd.ToInt64(),
                ["title"] = dialog.Title,
                ["class"] = dialog.ClassName,
                ["enabled"] = WindowManager.IsEnabled(dialog.Hwnd),
                ["suggested_target"] = "current_dialog",
            });
        }

        throw new ToolException(
            "E_BLOCKED",
            $"{operation} 的目标窗口被模态对话框禁用",
            new JsonObject
            {
                ["hwnd"] = hwnd.ToInt64(),
                ["title"] = WindowManager.GetTitle(hwnd),
                ["dialogs"] = dialogs,
            },
            retryable: false,
            hint: "先处理 details.dialogs；需要操作对话框时传 target=current_dialog（press_keys/type_text），或改用 details.dialogs[].hwnd",
            stage: "dialog");
    }
}
