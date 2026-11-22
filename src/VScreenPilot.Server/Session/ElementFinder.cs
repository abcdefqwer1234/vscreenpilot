using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Apps;
using VScreenPilot.Interop;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>find_element 所需的最小宿主接口。</summary>
internal sealed record ElementFinderContext(
    Func<int?, TrackedApp?> FindApp,
    Func<DisplayInfo> EnsureDisplay,
    Action<DisplayInfo, IntPtr, string> RequireOnDisplay,
    UiaHost Uia,
    Func<string> EnsureSnapshot,
    Func<UiaElementInfo, IntPtr, string> RegisterElement);

/// <summary>find_element 的查询参数。</summary>
internal sealed record FindQuery(
    string? Name,
    string? ControlType,
    string? AutomationId,
    string NameMatch,
    int Index,
    bool All,
    int MaxResults,
    bool IncludeOffscreen,
    int? Pid,
    string? Role = null,
    string Prefer = "auto");

/// <summary>查找范围（ApplyRole 之后的过滤条件）。</summary>
internal sealed record FindScope(string? ControlType, string? AutomationId, int Limit);

/// <summary>一次查找的候选与结果。</summary>
internal sealed record FindResultSet(
    List<(UiaElementInfo Info, IntPtr Hwnd)> Candidates,
    List<UiaElementInfo> Matches,
    IntPtr Hwnd,
    bool Truncated);

/// <summary>find_element：跨主窗口/弹窗查找、MSAA 回退、排序与 ref 登记。</summary>
internal sealed class ElementFinder
{
    private readonly ElementFinderContext _host;

    public ElementFinder(ElementFinderContext host) => _host = host;

    public object Find(FindQuery query)
    {
        IntPtr hwnd = ResolveWindow(query.Pid);
        DisplayInfo display = _host.EnsureDisplay();
        _host.RequireOnDisplay(display, hwnd, "find_element");

        (string? controlType, string? automationId) =
            ApplyRole(query.Role, query.ControlType, query.AutomationId);
        (UiaNameMatch mode, string preferMode) = ParseQuery(query.NameMatch, query.Prefer, query.Name);

        int limit = ResolveLimit(query, preferMode);
        var scope = new FindScope(controlType, automationId, limit);

        FindResultSet found = FindMatches(hwnd, query, mode, scope);

        if (found.Matches.Count == 0)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                "未找到匹配控件",
                new JsonObject
                {
                    ["name"] = query.Name,
                    ["name_match"] = query.NameMatch,
                    ["control_type"] = controlType,
                    ["automation_id"] = automationId,
                    ["role"] = query.Role,
                });
        }

        found = found with { Matches = SortMatches(found.Matches, preferMode) };

        // find_element 属于“当前快照”：不推进代际，避免让刚拿到的 ref 立刻失效。
        string snapshotId = _host.EnsureSnapshot();

        return query.All
            ? BuildAllView(query, found, preferMode, snapshotId)
            : BuildSingleView(query, found, preferMode, snapshotId);
    }

    private static int ResolveLimit(FindQuery query, string preferMode)
    {
        int requested = Math.Clamp(
            query.All ? query.MaxResults : Math.Max(query.Index + 1, 1), 1, 200);

        // largest/focused 需要尽量完整的候选集；auto/last 只多取少量，避免每次查找都扫全树。
        return preferMode switch
        {
            "first" => requested,
            "largest" or "focused" => 2000,
            _ => Math.Min(200, Math.Max(requested, 20)),
        };
    }

    private object BuildSingleView(
        FindQuery query, FindResultSet found, string preferMode, string snapshotId)
    {
        if (query.Index < 0 || query.Index >= found.Matches.Count)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                $"index={query.Index} 超出候选范围（共 {found.Matches.Count} 个）",
                new JsonObject { ["index"] = query.Index, ["count"] = found.Matches.Count });
        }

        UiaElementInfo info = found.Matches[query.Index];
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
            _host.RegisterElement(info, HwndFor(found.Candidates, info, found.Hwnd)),
            query.NameMatch,
            query.Role,
            preferMode,
            query.Index,
            found.Matches.Count,
            found.Truncated,
            snapshotId);
    }

    private static IntPtr HwndFor(
        List<(UiaElementInfo Info, IntPtr Hwnd)> candidates, UiaElementInfo info, IntPtr fallback)
    {
        IntPtr found = candidates.FirstOrDefault(item => ReferenceEquals(item.Info, info)).Hwnd;
        return found == IntPtr.Zero ? fallback : found;
    }

    private IntPtr ResolveWindow(int? pid)
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

        return hwnd;
    }

    private static (UiaNameMatch Mode, string PreferMode) ParseQuery(
        string nameMatch, string? prefer, string? name)
    {
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

        return (mode, preferMode);
    }

    private FindResultSet FindMatches(IntPtr hwnd, FindQuery query, UiaNameMatch mode, FindScope scope)
    {
        List<(UiaElementInfo Info, IntPtr Hwnd)> candidates;
        bool findTruncated;

        try
        {
            (candidates, findTruncated) =
                FindAcrossAppWindows(hwnd, query, mode, scope);
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

        if (!query.IncludeOffscreen)
        {
            matches = matches.Where(item => !item.Offscreen && item.Width > 0 && item.Height > 0).ToList();
        }

        // UIA 完全看不到时回退 MSAA（Office 旧对话框、bosa_sdm_msword 等）。
        if (matches.Count == 0)
        {
            return FindWithMsaa(hwnd, query, mode, scope, findTruncated);
        }

        return new FindResultSet(candidates, matches, hwnd, findTruncated);
    }

    private FindResultSet FindWithMsaa(
        IntPtr hwnd, FindQuery query, UiaNameMatch mode, FindScope scope, bool findTruncated)
    {
        var fallback = new List<(UiaElementInfo Info, IntPtr Hwnd)>();
        int pidForMsaa = WindowManager.GetProcessId(hwnd);

        foreach (WindowInfo window in WindowManager.ListForProcess(pidForMsaa))
        {
            MsaaFindResult msaa = _host.Uia.InvokeMsaa(() => MsaaService.FindElements(
                window.Hwnd, query.Name, mode, scope.ControlType, scope.Limit));
            fallback.AddRange(msaa.Items.Select(info => (info, window.Hwnd)));
            findTruncated |= msaa.Truncated;

            if (fallback.Count >= scope.Limit)
            {
                break;
            }
        }

        if (!query.IncludeOffscreen)
        {
            fallback = fallback
                .Where(item => !item.Info.Offscreen && item.Info.Width > 0 && item.Info.Height > 0)
                .ToList();
        }

        List<UiaElementInfo> matches = fallback.Select(item => item.Info).ToList();
        return new FindResultSet(fallback, matches, hwnd, findTruncated);
    }

    private object BuildAllView(
        FindQuery query, FindResultSet found, string preferMode, string snapshotId)
    {
        var elements = new List<FindElementItemView>();

        foreach (UiaElementInfo item in found.Matches.Take(query.MaxResults))
        {
            elements.Add(ElementPresenter.ToFindItem(
                item, _host.RegisterElement(item, HwndFor(found.Candidates, item, found.Hwnd))));
        }

        return new FindElementsView(
            elements.Count,
            found.Matches.Count,
            found.Truncated || found.Matches.Count > query.MaxResults,
            query.NameMatch,
            query.Role,
            preferMode,
            snapshotId,
            elements);
    }

    /// <summary>
    /// 在同一进程的主窗口与所有顶层弹窗（含模态对话框）里查找控件。
    /// 只查主窗口会把 MessageBox / 保存框等真实场景漏掉。
    /// </summary>
    private (List<(UiaElementInfo Info, IntPtr Hwnd)> Items, bool Truncated) FindAcrossAppWindows(
        IntPtr mainHwnd, FindQuery query, UiaNameMatch mode, FindScope scope)
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
                    window, query.Name, mode, scope.ControlType, scope.AutomationId, scope.Limit);
                found = uia.Items;
                truncated |= uia.Truncated;

                // 单个窗口 UIA 完全不可见时，就地回退 MSAA。
                // 不能等全局 0 结果再回退：主窗口有控件会掩盖掉 bosa_sdm_msword 对话框。
                if (found.Count == 0)
                {
                    MsaaFindResult msaa = _host.Uia.InvokeMsaa(() => MsaaService.FindElements(
                        window, query.Name, mode, scope.ControlType, scope.Limit));
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
