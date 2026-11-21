using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Apps;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>find_element 所需的最小宿主接口。</summary>
internal interface IElementFinderHost
{
    TrackedApp? FindApp(int? pid);
    DisplayInfo EnsureDisplay();
    void RequireOnDisplay(DisplayInfo display, IntPtr hwnd, string label);
    UiaHost Uia { get; }
    string EnsureSnapshot();
    string RegisterElement(UiaElementInfo info, IntPtr hwnd);
}

/// <summary>find_element：跨主窗口/弹窗查找、MSAA 回退、排序与 ref 登记。</summary>
internal sealed class ElementFinder
{
    private readonly IElementFinderHost _host;

    public ElementFinder(IElementFinderHost host) => _host = host;

    public object Find(
        string? name,
        string? controlType,
        string? automationId,
        string nameMatch,
        int index,
        bool all,
        int maxResults,
        bool includeOffscreen,
        int? pid,
        string? role = null,
        string prefer = "auto")
    {
        IntPtr hwnd = _host.FindApp(pid)?.Hwnd ?? IntPtr.Zero;

        if (hwnd == IntPtr.Zero)
        {
            throw new ToolException(
                "E_NO_TARGET",
                pid is null
                    ? "没有已接管的应用窗口，请先 launch_or_place_app"
                    : $"pid={pid} 不是本会话中存活的应用",
                new JsonObject { ["pid"] = pid });
        }

        DisplayInfo display = _host.EnsureDisplay();

        _host.RequireOnDisplay(display, hwnd, "find_element");

        (controlType, automationId) = ApplyRole(role, controlType, automationId);

        UiaNameMatch mode = nameMatch.ToLowerInvariant() switch
        {
            "exact" => UiaNameMatch.Exact,
            "contains" => UiaNameMatch.Contains,
            "regex" => UiaNameMatch.Regex,
            _ => throw new ToolException(
                "E_INVALID_ARG", $"未知 name_match: {nameMatch}（可选 exact/contains/regex）"),
        };

        string preferMode = (prefer ?? "auto").Trim().ToLowerInvariant();

        if (preferMode is not ("auto" or "first" or "focused" or "largest" or "last"))
        {
            throw new ToolException(
                "E_INVALID_ARG", $"未知 prefer: {prefer}（可选 auto/first/focused/largest/last）");
        }

        if (mode == UiaNameMatch.Regex && !string.IsNullOrEmpty(name))
        {
            try
            {
                _ = new Regex(name, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException exception)
            {
                throw new ToolException("E_INVALID_ARG", $"name 不是合法正则: {exception.Message}");
            }
        }

        int requested = Math.Clamp(all ? maxResults : Math.Max(index + 1, 1), 1, 200);
        // largest/focused 需要尽量完整的候选集；auto/last 只多取少量，避免每次查找都扫全树。
        int limit = preferMode switch
        {
            "first" => requested,
            "largest" or "focused" => 2000,
            _ => Math.Min(200, Math.Max(requested, 20)),
        };

        List<(UiaElementInfo Info, IntPtr Hwnd)> candidates;
        bool findTruncated;

        try
        {
            (candidates, findTruncated) =
                FindAcrossAppWindows(hwnd, name, mode, controlType, automationId, limit);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ToolException("E_INVALID_ARG", "正则匹配超时，请简化 name");
        }
        catch (COMException exception)
        {
            throw new ToolException("E_UIA", $"UI Automation 调用失败: {exception.Message}");
        }

        List<UiaElementInfo> matches = candidates.Select(item => item.Info).ToList();

        if (!includeOffscreen)
        {
            matches = matches.Where(item => !item.Offscreen && item.Width > 0 && item.Height > 0).ToList();
        }

        // UIA 完全看不到时走 MSAA（Office 旧对话框、bosa_sdm_msword 等）。
        if (matches.Count == 0)
        {
            var fallback = new List<(UiaElementInfo Info, IntPtr Hwnd)>();
            int pidForMsaa = WindowManager.GetProcessId(hwnd);

            foreach (WindowInfo window in WindowManager.ListForProcess(pidForMsaa))
            {
                MsaaFindResult msaa = _host.Uia.InvokeMsaa(() => MsaaService.FindElements(
                    window.Hwnd, name, mode, controlType, limit));
                fallback.AddRange(msaa.Items.Select(info => (info, window.Hwnd)));
                findTruncated |= msaa.Truncated;

                if (fallback.Count >= limit)
                {
                    break;
                }
            }

            if (!includeOffscreen)
            {
                fallback = fallback
                    .Where(item => !item.Info.Offscreen && item.Info.Width > 0 && item.Info.Height > 0)
                    .ToList();
            }

            if (fallback.Count > 0)
            {
                candidates = fallback;
                matches = fallback.Select(item => item.Info).ToList();
            }
        }

        if (matches.Count == 0)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                "未找到匹配控件",
                new JsonObject
                {
                    ["name"] = name,
                    ["name_match"] = nameMatch,
                    ["control_type"] = controlType,
                    ["automation_id"] = automationId,
                    ["role"] = role,
                });
        }

        matches = SortMatches(matches, preferMode);

        IntPtr HwndFor(UiaElementInfo info) =>
            candidates.FirstOrDefault(item => ReferenceEquals(item.Info, info)).Hwnd is var found && found != IntPtr.Zero
                ? found
                : hwnd;

        // find_element 属于“当前快照”：不推进代际，避免让刚拿到的 ref 立刻失效。
        string snapshotId = _host.EnsureSnapshot();

        if (all)
        {
            var elements = new List<FindElementItemView>();

            foreach (UiaElementInfo item in matches.Take(maxResults))
            {
                elements.Add(ElementPresenter.ToFindItem(item, _host.RegisterElement(item, HwndFor(item))));
            }

            return new FindElementsView(
                elements.Count,
                matches.Count,
                findTruncated || matches.Count > maxResults,
                nameMatch,
                role,
                preferMode,
                snapshotId,
                elements);
        }

        if (index < 0 || index >= matches.Count)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                $"index={index} 超出候选范围（共 {matches.Count} 个）",
                new JsonObject { ["index"] = index, ["count"] = matches.Count });
        }

        UiaElementInfo info = matches[index];
        ElementView element = ElementPresenter.ToView(info, compact: true);

        return new FindElementView(
            element.Name,
            element.AutomationId,
            element.ControlType,
            element.Bounds,
            element.Enabled,
            element.Offscreen,
            element.Focused,
            element.Value,
            _host.RegisterElement(info, HwndFor(info)),
            nameMatch,
            role,
            preferMode,
            index,
            matches.Count,
            findTruncated,
            snapshotId);
    }

    /// <summary>
    /// 在同一进程的主窗口与所有顶层弹窗（含模态对话框）里查找控件。
    /// 只查主窗口会把 MessageBox / 保存框等真实场景漏掉。
    /// </summary>
    private (List<(UiaElementInfo Info, IntPtr Hwnd)> Items, bool Truncated) FindAcrossAppWindows(
        IntPtr mainHwnd,
        string? name,
        UiaNameMatch mode,
        string? controlType,
        string? automationId,
        int limit)
    {
        var result = new List<(UiaElementInfo Info, IntPtr Hwnd)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var windows = new List<IntPtr> { mainHwnd };
        bool truncated = false;

        foreach (WindowInfo window in WindowManager.ListForProcess(WindowManager.GetProcessId(mainHwnd)))
        {
            if (window.Hwnd != mainHwnd)
            {
                windows.Add(window.Hwnd);
            }
        }

        foreach (IntPtr window in windows)
        {
            List<UiaElementInfo> found;

            try
            {
                UiaFindResult uia = _host.Uia.FindElementsDetailed(
                    window, name, mode, controlType, automationId, limit);
                found = uia.Items;
                truncated |= uia.Truncated;

                // 单个窗口 UIA 完全不可见时，就地回退 MSAA。
                // 不能等全局 0 结果再回退：主窗口有控件会掩盖掉 bosa_sdm_msword 对话框。
                if (found.Count == 0)
                {
                    MsaaFindResult msaa = _host.Uia.InvokeMsaa(() => MsaaService.FindElements(
                        window, name, mode, controlType, limit));
                    found = msaa.Items;
                    truncated |= msaa.Truncated;
                }
            }
            catch (COMException)
            {
                continue;
            }

            foreach (UiaElementInfo info in found)
            {
                string key = !string.IsNullOrEmpty(info.RuntimeId)
                    ? info.RuntimeId
                    : $"{info.ProcessId}|{info.Name}|{info.ControlType}|{info.Left},{info.Top},{info.Width},{info.Height}";

                if (seen.Add(key))
                {
                    result.Add((info, window));
                }
            }
        }

        return (result, truncated);
    }

    private static (string? ControlType, string? AutomationId) ApplyRole(
        string? role, string? controlType, string? automationId)
    {
        string normalized = (role ?? string.Empty).Trim().ToLowerInvariant();

        return normalized switch
        {
            "" => (controlType, automationId),
            "document" => (controlType ?? "Document", automationId),
            "document_body" or "body" =>
                (controlType ?? "Edit", string.IsNullOrWhiteSpace(automationId) ? "Body" : automationId),
            _ => throw new ToolException(
                "E_INVALID_ARG",
                $"未知 role: {role}（可选 document/document_body）"),
        };
    }

    private static List<UiaElementInfo> SortMatches(List<UiaElementInfo> matches, string prefer) =>
        prefer switch
        {
            "first" => matches,
            "last" => matches.AsEnumerable().Reverse().ToList(),
            "focused" => matches
                .OrderByDescending(item => item.Focused)
                .ThenByDescending(item => item.Width * item.Height)
                .ToList(),
            "largest" => matches
                .OrderByDescending(item => item.Width * item.Height)
                .ThenByDescending(item => item.Focused)
                .ToList(),
            _ => matches
                .OrderByDescending(item => item.Focused)
                .ThenByDescending(item => item.Width * item.Height)
                .ToList(),
        };

}
