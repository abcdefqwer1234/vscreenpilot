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
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

internal sealed partial class AppLauncher
{
    private bool WaitForWordDocument(IntPtr hwnd, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));

        while (DateTime.UtcNow < deadline)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                return false;
            }

            try
            {
                if (_apps.Uia.FindElement(hwnd, null, "Document", null, null) is not null)
                {
                    return true;
                }
            }
            catch (System.Runtime.InteropServices.COMException exception)
            {
                // Word 正在切换文档，稍后重试。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }

            Thread.Sleep(150);
        }

        return false;
    }

    /// <summary>Office 窗口标题在打开文档后是“文件名 - 产品名”；开始屏幕只有产品名。</summary>
    internal static bool LooksLikeDocumentWindow(string title, string product)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        string trimmed = title.Trim();
        return trimmed.EndsWith(" - " + product, StringComparison.OrdinalIgnoreCase) &&
               trimmed.Length > product.Length + 3;
    }

    private static bool WaitForDocumentTitle(IntPtr hwnd, string product, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));

        while (DateTime.UtcNow < deadline)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                return false;
            }

            if (LooksLikeDocumentWindow(WindowManager.GetTitle(hwnd), product))
            {
                return true;
            }

            Thread.Sleep(150);
        }

        return false;
    }

    private UiaElementInfo? FindBlankPresentationTile(IntPtr hwnd)
    {
        try
        {
            foreach (UiaElementInfo info in _apps.Uia.FindElements(
                         hwnd, null, UiaNameMatch.Exact, "Button", null, 200))
            {
                if (info.Name.Contains("空白", StringComparison.OrdinalIgnoreCase) ||
                    info.Name.Contains("Blank", StringComparison.OrdinalIgnoreCase))
                {
                    return info;
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            // 开始屏幕的 UIA 提供程序不稳定时按“找不到磁贴”处理。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        return null;
    }

}
