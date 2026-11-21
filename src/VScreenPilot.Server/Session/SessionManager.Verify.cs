using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using static VScreenPilot.Mcp.JsonArgReader;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    public JsonObject FindElement(
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
        IntPtr hwnd = FindLiveTrackedApp(pid)?.Hwnd ?? IntPtr.Zero;

        if (hwnd == IntPtr.Zero)
        {
            throw new ToolException(
                "E_NO_TARGET",
                pid is null
                    ? "没有已接管的应用窗口，请先 launch_or_place_app"
                    : $"pid={pid} 不是本会话中存活的应用",
                new JsonObject { ["pid"] = pid });
        }

        DisplayInfo display = EnsureEnvironment();

        RequireWindowOnDisplay(display, hwnd, "find_element");

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
                MsaaFindResult msaa = Uia.InvokeMsaa(() => MsaaService.FindElements(
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
        string snapshotId = EnsureSnapshot();

        if (all)
        {
            var elements = new JsonArray();

            foreach (UiaElementInfo item in matches.Take(maxResults))
            {
                JsonObject element = item.ToCompactJson();
                element["ref"] = RegisterElement(item, HwndFor(item));
                elements.Add(element);
            }

            return new JsonObject
            {
                ["count"] = elements.Count,
                ["total"] = matches.Count,
                ["truncated"] = findTruncated || matches.Count > maxResults,
                ["name_match"] = nameMatch,
                ["role"] = role,
                ["prefer"] = preferMode,
                ["snapshot_id"] = snapshotId,
                ["elements"] = elements,
            };
        }

        if (index < 0 || index >= matches.Count)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                $"index={index} 超出候选范围（共 {matches.Count} 个）",
                new JsonObject { ["index"] = index, ["count"] = matches.Count });
        }

        UiaElementInfo info = matches[index];
        JsonObject result = info.ToCompactJson();
        result["ref"] = RegisterElement(info, HwndFor(info));
        result["name_match"] = nameMatch;
        result["role"] = role;
        result["prefer"] = preferMode;
        result["index"] = index;
        result["count"] = matches.Count;
        result["truncated"] = findTruncated;
        result["snapshot_id"] = snapshotId;
        return result;
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
                UiaFindResult uia = Uia.FindElementsDetailed(
                    window, name, mode, controlType, automationId, limit);
                found = uia.Items;
                truncated |= uia.Truncated;

                // 单个窗口 UIA 完全不可见时，就地回退 MSAA。
                // 不能等全局 0 结果再回退：主窗口有控件会掩盖掉 bosa_sdm_msword 对话框。
                if (found.Count == 0)
                {
                    MsaaFindResult msaa = Uia.InvokeMsaa(() => MsaaService.FindElements(
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

    public JsonObject WaitFor(JsonElement condition, int timeoutMs, int? intervalMs = null)
    {
        DisplayInfo display = EnsureEnvironmentFast();
        timeoutMs = Math.Clamp(timeoutMs, 0, 120_000);
        int? requestedInterval = intervalMs is null ? null : Math.Clamp(intervalMs.Value, 50, 2000);

        var stopwatch = Stopwatch.StartNew();
        // wait_for 可能持续 120s，不能整段持读锁把 release 挡死；
        // 每次轮询取一次短读锁，release 最多等一次轮询。
        JsonObject Evaluate()
        {
            EnterRead();

            try
            {
                return EvaluateCondition(condition, display);
            }
            finally
            {
                ExitRead();
            }
        }

        JsonObject last = Evaluate();

        while (!(last["satisfied"]?.GetValue<bool>() ?? false))
        {
            // 基线条件不会因为继续等待而变得可用，直接返回，避免白等完整超时。
            if (string.Equals(
                    last["reason"]?.GetValue<string>(),
                    "no_matching_baseline",
                    StringComparison.Ordinal))
            {
                break;
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                break;
            }

            // 只有短等待需要高频轮询；长时间等待逐步退避，避免持续占用 CPU/UIA。
            int interval = requestedInterval ?? stopwatch.ElapsedMilliseconds switch
            {
                < 2_000 => 100,
                < 5_000 => 250,
                _ => 500,
            };

            int sleep = (int)Math.Min(interval, timeoutMs - stopwatch.ElapsedMilliseconds);
            if (sleep > 0)
            {
                ToolProgress.Report(
                    (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
                    Math.Max(1, timeoutMs),
                    "wait_for 轮询中");
                Thread.Sleep(sleep);
            }

            if (Cancellation.IsCancellationRequested)
            {
                throw new ToolException("E_CANCELLED", "请求已取消");
            }

            last = Evaluate();
        }

        bool satisfied = last["satisfied"]?.GetValue<bool>() ?? false;

        return new JsonObject
        {
            ["satisfied"] = satisfied,
            ["elapsed_ms"] = stopwatch.ElapsedMilliseconds,
            ["last"] = last,
        };
    }

    public JsonObject AssertRegion(JsonElement region, JsonElement expectation)
    {
        DisplayInfo display = EnsureEnvironmentFast();
        (int x, int y, int width, int height) = ParseRegion(region);

        if (width <= 0 || height <= 0 || x < int.MinValue + 1 || y < int.MinValue + 1)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                "assert_region 的 region 必须包含正整数 width/height",
                new JsonObject
                {
                    ["field"] = "region",
                    ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height,
                },
                retryable: false,
                hint: "传 {x,y,width,height}，width/height ≥ 1",
                stage: "validate");
        }

        // expectation 至少要有一种断言，否则不要浪费一次截图。
        bool hasExpectation =
            (expectation.ValueKind == JsonValueKind.Object) &&
            (expectation.TryGetProperty("color", out _) ||
             expectation.TryGetProperty("changed", out _) ||
             expectation.TryGetProperty("baseline", out _) ||
             expectation.TryGetProperty("baseline_frame_id", out _));

        if (!hasExpectation)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                "assert_region 需要 expectation.color / expectation.changed / baseline",
                new JsonObject { ["field"] = "expectation" },
                retryable: false,
                hint: "示例：{\"changed\":true,\"threshold\":0.01} 或 {\"color\":\"#RRGGBB\",\"coverage\":0.9}",
                stage: "validate");
        }

        RequireRegionInDisplay(display, x, y, width, height, "assert_region");

        (byte[] bgra, int capturedWidth, int capturedHeight) = CaptureRegion(x, y, width, height);

        if (expectation.TryGetProperty("color", out JsonElement colorElement) &&
            colorElement.ValueKind == JsonValueKind.String)
        {
            return AssertColor(bgra, capturedWidth, capturedHeight, colorElement.GetString()!, expectation);
        }

        if (expectation.TryGetProperty("changed", out JsonElement changedElement) &&
            (changedElement.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return AssertChanged(
                bgra, capturedWidth, capturedHeight, changedElement.GetBoolean(),
                expectation, x, y);
        }

        if (expectation.TryGetProperty("baseline", out JsonElement baselineElement) &&
            baselineElement.ValueKind == JsonValueKind.String)
        {
            string baseline = (baselineElement.GetString() ?? string.Empty)
                .Trim()
                .ToLowerInvariant();

            if (baseline is not ("last" or "latest" or "last_same_region" or "last-same-region"))
            {
                throw new ToolException(
                    "E_INVALID_ARG",
                    $"未知 baseline: {baseline}（可选 last_same_region/last）");
            }

            return AssertChanged(
                bgra, capturedWidth, capturedHeight, expectChanged: false, expectation, x, y);
        }

        throw new ToolException(
            "E_INVALID_ARG",
            "expectation 需要 color、changed 或 baseline 之一");
    }

    private JsonObject EvaluateCondition(JsonElement condition, DisplayInfo display)
    {
        string type = GetString(condition, "type") ?? "element";

        switch (type.ToLowerInvariant())
        {
            case "element":
            {
                string? name = GetString(condition, "name");
                string? controlType = GetString(condition, "control_type");
                string? automationId = GetString(condition, "automation_id");
                int? pid = GetInt(condition, "pid", -1) is var requested && requested >= 0
                    ? requested
                    : null;

                if (string.IsNullOrWhiteSpace(name) &&
                    string.IsNullOrWhiteSpace(controlType) &&
                    string.IsNullOrWhiteSpace(automationId))
                {
                    throw new ToolException(
                        "E_INVALID_ARG",
                        "element 条件至少需要 name、control_type 或 automation_id 之一");
                }

                IntPtr hwnd = FindLiveTrackedApp(pid)?.Hwnd ?? IntPtr.Zero;

                if (hwnd == IntPtr.Zero)
                {
                    return new JsonObject { ["satisfied"] = false, ["reason"] = "no_target_window" };
                }

                try
                {
                    UiaElementInfo? info = Uia.FindElement(hwnd, name, controlType, automationId, null);
                    bool satisfied = info is not null && !info.Offscreen;

                    return new JsonObject
                    {
                        ["satisfied"] = satisfied,
                        ["element"] = info?.ToJson(),
                    };
                }
                catch (COMException exception)
                {
                    return new JsonObject { ["satisfied"] = false, ["reason"] = exception.Message };
                }
            }

            case "text":
            {
                string? needle = GetString(condition, "text");

                if (string.IsNullOrWhiteSpace(needle))
                {
                    throw new ToolException("E_INVALID_ARG", "text 条件需要非空 text");
                }

                (int rx, int ry, int rw, int rh)? region = null;

                if (condition.TryGetProperty("region", out JsonElement regionElement) &&
                    regionElement.ValueKind == JsonValueKind.Object)
                {
                    (int x, int y, int width, int height) = ParseRegion(regionElement);
                    RequireRegionInDisplay(display, x, y, width, height, "text");
                    region = (x, y, width, height);
                }

                List<TrackedApp> candidates;

                lock (_gate)
                {
                    candidates = _apps
                        .Where(app => IsOwned(app) && IsLiveTrackedApp(app))
                        .ToList();
                }

                int? requestedPid = GetInt(condition, "pid", -1) is var pidValue && pidValue >= 0
                    ? pidValue
                    : null;

                foreach (TrackedApp app in candidates)
                {
                    if (requestedPid is not null && app.Pid != requestedPid)
                    {
                        continue;
                    }

                    UiaElementInfo? info = Uia.FindTextElement(app.Hwnd, needle);

                    if (info is null || info.Offscreen)
                    {
                        continue;
                    }

                    if (region is { } bounds &&
                        (info.Left + info.Width <= bounds.rx ||
                         info.Top + info.Height <= bounds.ry ||
                         info.Left >= bounds.rx + bounds.rw ||
                         info.Top >= bounds.ry + bounds.rh))
                    {
                        continue;
                    }

                    return new JsonObject
                    {
                        ["satisfied"] = true,
                        ["element"] = info.ToCompactJson(),
                        ["window"] = new JsonObject
                        {
                            ["pid"] = app.Pid,
                            ["title"] = WindowManager.GetTitle(app.Hwnd),
                        },
                    };
                }

                return new JsonObject
                {
                    ["satisfied"] = false,
                    ["reason"] = candidates.Count == 0 ? "no_target_window" : "uia_text_not_found",
                };
            }

            case "color":
            {
                int x = GetInt(condition, "x", int.MinValue);
                int y = GetInt(condition, "y", int.MinValue);
                string? value = GetString(condition, "value");

                if (x == int.MinValue || y == int.MinValue || value is null ||
                    !TryParseHex(value, out byte r, out byte g, out byte b))
                {
                    throw new ToolException("E_INVALID_ARG", "color 条件需要 x、y 与 #RRGGBB");
                }

                RequireRegionInDisplay(display, x, y, 1, 1, "color");

                byte[] pixel = Perception.ScreenCapture.CapturePixel(x, y);
                int tolerance = GetInt(condition, "tolerance", 10);
                bool satisfied = ColorMatches(pixel, 0, r, g, b, tolerance);

                return new JsonObject
                {
                    ["satisfied"] = satisfied,
                    ["actual"] = ToHex(pixel, 0),
                };
            }

            case "window":
            {
                string? title = GetString(condition, "title");
                string state = GetString(condition, "state") ?? "exists";
                string scope = (GetString(condition, "scope") ?? "tracked").ToLowerInvariant();
                int minWidth = GetInt(condition, "min_width", 0);
                int minHeight = GetInt(condition, "min_height", 0);

                if (scope is not ("tracked" or "any"))
                {
                    throw new ToolException("E_INVALID_ARG", $"未知 window scope: {scope}");
                }

                HashSet<int> pids = scope == "tracked" ? TrackedPids() : new HashSet<int>();

                if (scope == "tracked" && pids.Count == 0)
                {
                    return new JsonObject
                    {
                        ["satisfied"] = false,
                        ["reason"] = "no_target_window",
                    };
                }

                // title 可省略：用于等待“任意窗口出现”或“标题从 splash 变成主窗口”。
                // min_width/min_height 可过滤启动 splash。
                WindowInfo? window = WindowManager.ListTopLevel()
                    .Where(item =>
                        item.Visible &&
                        (string.IsNullOrWhiteSpace(title) ||
                         item.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) &&
                        (minWidth <= 0 || item.Width >= minWidth) &&
                        (minHeight <= 0 || item.Height >= minHeight) &&
                        (scope == "any" || pids.Contains(item.Pid)) &&
                        item.Area >= 100 * 80)
                    .OrderByDescending(item => item.Area)
                    .FirstOrDefault();

                bool satisfied = state.ToLowerInvariant() switch
                {
                    "exists" => window is not null,
                    "visible" => window is { Visible: true },
                    "foreground" => window is not null &&
                                    WindowManager.GetForegroundWindow() == window.Hwnd,
                    _ => throw new ToolException("E_INVALID_ARG", $"未知 window state: {state}"),
                };

                bool trackedWindow = window is not null && pids.Contains(window.Pid);

                return new JsonObject
                {
                    ["satisfied"] = satisfied,
                    ["scope"] = scope,
                    ["window"] = window is null
                        ? null
                        : new JsonObject
                        {
                            ["hwnd"] = window.Hwnd.ToInt64(),
                            ["tracked"] = trackedWindow,
                            // scope=any 只用于判断存在性；非本会话窗口不回传标题/PID，
                            // 避免把用户主屏的邮件主题、文档名泄露给模型。
                            ["title"] = trackedWindow ? window.Title : "<redacted>",
                            ["pid"] = trackedWindow ? window.Pid : null,
                        },
                };
            }

            case "pixel_diff":
            {
                if (!condition.TryGetProperty("region", out JsonElement regionElement) ||
                    regionElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ToolException("E_INVALID_ARG", "pixel_diff 条件需要 region");
                }

                (int x, int y, int width, int height) = ParseRegion(regionElement);
                RequireRegionInDisplay(display, x, y, width, height, "pixel_diff");
                (byte[] bgra, int capturedWidth, int capturedHeight) = CaptureRegion(x, y, width, height);

                FrameSnapshot? baseline = ResolveBaselineFrame(
                    condition, x, y, capturedWidth, capturedHeight);

                if (baseline is null)
                {
                    return new JsonObject
                    {
                        ["satisfied"] = false,
                        ["reason"] = "no_matching_baseline",
                        ["baseline"] = null,
                        ["available_frames"] = FramesJson(),
                    };
                }

                double threshold = GetDouble(condition, "threshold", 0.01);
                double ratio = ChangedRatio(bgra, baseline.Bgra, GetInt(condition, "tolerance", 8));

                return new JsonObject
                {
                    ["satisfied"] = ratio >= threshold,
                    ["changed_ratio"] = ratio,
                    ["threshold"] = threshold,
                    ["baseline_frame_id"] = baseline.Id,
                };
            }

            case "visual":
            {
                // 直接复用 match_visual：condition 与 match_visual 参数同构（type 字段会被忽略）。
                try
                {
                    JsonObject match = MatchVisual(condition);
                    JsonArray? matches = match["matches"] as JsonArray;

                    return new JsonObject
                    {
                        ["satisfied"] = true,
                        ["count"] = match["count"]?.DeepClone(),
                        ["first"] = matches is { Count: > 0 } ? matches[0]?.DeepClone() : null,
                    };
                }
                catch (ToolException exception) when (exception.Code == "E_NO_MATCH")
                {
                    return new JsonObject
                    {
                        ["satisfied"] = false,
                        ["reason"] = "no_match",
                    };
                }
            }

            default:
                throw new ToolException("E_INVALID_ARG", $"未知 wait_for 条件类型: {type}");
        }
    }

    private JsonObject AssertColor(
        byte[] bgra, int width, int height, string hex, JsonElement expectation)
    {
        if (!TryParseHex(hex, out byte r, out byte g, out byte b))
        {
            throw new ToolException("E_INVALID_ARG", $"颜色格式无效: {hex}");
        }

        int tolerance = GetInt(expectation, "tolerance", 10);
        double coverage = GetDouble(expectation, "coverage", 0.9);

        if (tolerance is < 0 or > 255)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"tolerance 必须在 0..255（收到 {tolerance}）",
                new JsonObject { ["field"] = "tolerance", ["value"] = tolerance },
                retryable: false,
                hint: "颜色容差按 0..255 的每通道绝对差计算",
                stage: "validate");
        }

        if (coverage is < 0 or > 1)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"coverage 必须在 0..1（收到 {coverage}）",
                new JsonObject { ["field"] = "coverage", ["value"] = coverage },
                retryable: false,
                hint: "coverage 是比例阈值；1.0 表示全部像素匹配",
                stage: "validate");
        }

        int total = width * height;
        int matched = 0;
        long sumR = 0, sumG = 0, sumB = 0;

        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte pb = bgra[i];
            byte pg = bgra[i + 1];
            byte pr = bgra[i + 2];
            sumB += pb;
            sumG += pg;
            sumR += pr;

            if (Math.Abs(pr - r) <= tolerance &&
                Math.Abs(pg - g) <= tolerance &&
                Math.Abs(pb - b) <= tolerance)
            {
                matched++;
            }
        }

        double ratio = total == 0 ? 0 : (double)matched / total;
        bool passed = ratio >= coverage;

        return new JsonObject
        {
            ["passed"] = passed,
            ["matched_ratio"] = Math.Round(ratio, 4),
            ["actual_coverage"] = Math.Round(ratio, 4),
            ["coverage"] = coverage,
            ["coverage_semantics"] = "coverage 是请求阈值；actual_coverage/matched_ratio 是实际覆盖率",
            ["tolerance"] = tolerance,
            ["expected"] = hex,
            ["average"] = total == 0
                ? "#000000"
                : $"#{sumR / total:X2}{sumG / total:X2}{sumB / total:X2}",
            ["region"] = new JsonObject { ["width"] = width, ["height"] = height },
        };
    }

    private static void ValidateChangedExpectation(JsonElement expectation)
    {
        double threshold = GetDouble(expectation, "threshold", 0.01);
        int tolerance = GetInt(expectation, "tolerance", 8);

        if (threshold is < 0 or > 1)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"threshold 必须在 0..1（收到 {threshold}）",
                new JsonObject { ["field"] = "threshold", ["value"] = threshold },
                retryable: false,
                hint: "threshold 是变化像素比例；2.0 会恒假、-1 会恒真",
                stage: "validate");
        }

        if (tolerance is < 0 or > 255)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"tolerance 必须在 0..255（收到 {tolerance}）",
                new JsonObject { ["field"] = "tolerance", ["value"] = tolerance },
                retryable: false,
                hint: "像素差容差按 0..255 计算",
                stage: "validate");
        }
    }

    private JsonObject AssertChanged(
        byte[] bgra, int width, int height, bool expectChanged, JsonElement expectation, int x, int y)
    {
        // 先校验阈值：非法阈值应在“找基线之前”就返回 E_INVALID_ARG，
        // 否则会被 E_NO_BASELINE 掩盖（REG-08）。
        ValidateChangedExpectation(expectation);

        FrameSnapshot? baseline = ResolveBaselineFrame(expectation, x, y, width, height);

        if (baseline is null)
        {
            throw new ToolException(
                "E_NO_BASELINE",
                "没有同区域的基线帧；请先 capture_screen 同一 region，或传 baseline_frame_id",
                new JsonObject
                {
                    ["current"] = new JsonObject
                    {
                        ["x"] = x,
                        ["y"] = y,
                        ["width"] = width,
                        ["height"] = height,
                    },
                    ["available_frames"] = FramesJson(),
                });
        }

        double threshold = GetDouble(expectation, "threshold", 0.01);
        int tolerance = GetInt(expectation, "tolerance", 8);
        double ratio = ChangedRatio(bgra, baseline.Bgra, tolerance);
        bool passed = expectChanged ? ratio >= threshold : ratio <= threshold;

        return new JsonObject
        {
            ["passed"] = passed,
            ["changed_ratio"] = Math.Round(ratio, 4),
            ["threshold"] = threshold,
            ["expect_changed"] = expectChanged,
            ["baseline_frame_id"] = baseline.Id,
            ["baseline"] = new JsonObject
            {
                ["x"] = baseline.X,
                ["y"] = baseline.Y,
                ["width"] = baseline.Width,
                ["height"] = baseline.Height,
            },
        };
    }

    private FrameSnapshot? ResolveBaselineFrame(
        JsonElement source, int x, int y, int width, int height)
    {
        if (source.ValueKind == JsonValueKind.Object &&
            source.TryGetProperty("baseline_frame_id", out JsonElement frameIdElement) &&
            frameIdElement.ValueKind == JsonValueKind.String)
        {
            FrameSnapshot? frame = FindFrame(frameIdElement.GetString() ?? string.Empty);

            // 显式 baseline 也必须校验区域，否则“同尺寸不同区域”会被静默拿来比较（SEC-09）。
            return frame is not null &&
                   frame.X == x &&
                   frame.Y == y &&
                   frame.Width == width &&
                   frame.Height == height
                ? frame
                : null;
        }

        string mode = "last_same_region";

        if (source.ValueKind == JsonValueKind.Object &&
            source.TryGetProperty("baseline", out JsonElement baselineElement) &&
            baselineElement.ValueKind == JsonValueKind.String)
        {
            mode = (baselineElement.GetString() ?? mode).Trim().ToLowerInvariant();
        }

        if (mode is "last" or "latest")
        {
            FrameSnapshot? latest = _frames.Latest();
            return latest is not null &&
                   latest.Width == width &&
                   latest.Height == height
                ? latest
                : null;
        }

        return FindLatestFrame(x, y, width, height);
    }

    private JsonArray FramesJson()
    {
        var array = new JsonArray();

        foreach (FrameSnapshot frame in _frames.Snapshot())
        {
            array.Add(new JsonObject
            {
                ["frame_id"] = frame.Id,
                ["x"] = frame.X,
                ["y"] = frame.Y,
                ["width"] = frame.Width,
                ["height"] = frame.Height,
            });
        }

        return array;
    }

    private static double ChangedRatio(byte[] current, byte[] baseline, int tolerance)
    {
        int count = Math.Min(current.Length, baseline.Length) / 4;
        if (count == 0)
        {
            return 0;
        }

        int changed = 0;

        for (int pixel = 0; pixel < count; pixel++)
        {
            int offset = pixel * 4;
            if (Math.Abs(current[offset] - baseline[offset]) > tolerance ||
                Math.Abs(current[offset + 1] - baseline[offset + 1]) > tolerance ||
                Math.Abs(current[offset + 2] - baseline[offset + 2]) > tolerance)
            {
                changed++;
            }
        }

        return (double)changed / count;
    }

    private static (int X, int Y, int Width, int Height) ParseRegion(JsonElement region)
    {
        int x = GetInt(region, "x", int.MinValue);
        int y = GetInt(region, "y", int.MinValue);
        int width = GetInt(region, "width", int.MinValue);
        int height = GetInt(region, "height", int.MinValue);

        if (x == int.MinValue || y == int.MinValue || width <= 0 || height <= 0)
        {
            throw new ToolException("E_INVALID_ARG", "region 需要有效的 x、y、width、height");
        }

        return (x, y, width, height);
    }

    private static void RequireRegionInDisplay(
        DisplayInfo display, int x, int y, int width, int height, string label)
    {
        long right = (long)x + width;
        long bottom = (long)y + height;

        if (width <= 0 || height <= 0 ||
            x < display.X || y < display.Y ||
            right > (long)display.X + display.Width ||
            bottom > (long)display.Y + display.Height)
        {
            throw new ToolException(
                "E_OUT_OF_BOUNDS",
                $"{label} 不在虚拟屏范围内，拒绝读取主屏",
                new JsonObject
                {
                    ["region"] = new JsonObject
                    {
                        ["x"] = x,
                        ["y"] = y,
                        ["width"] = width,
                        ["height"] = height,
                    },
                    ["display"] = new JsonObject
                    {
                        ["x"] = display.X,
                        ["y"] = display.Y,
                        ["width"] = display.Width,
                        ["height"] = display.Height,
                    },
                });
        }
    }

    private TrackedApp? LastTrackedApp() => LastLiveTrackedApp();

    private HashSet<int> TrackedPids()
    {
        lock (_gate)
        {
            return _apps.Where(IsOwned).Select(app => app.Pid).ToHashSet();
        }
    }

    private static bool ColorMatches(byte[] bgra, int offset, byte r, byte g, byte b, int tolerance) =>
        Math.Abs(bgra[offset + 2] - r) <= tolerance &&
        Math.Abs(bgra[offset + 1] - g) <= tolerance &&
        Math.Abs(bgra[offset] - b) <= tolerance;

    private static string ToHex(byte[] bgra, int offset) =>
        $"#{bgra[offset + 2]:X2}{bgra[offset + 1]:X2}{bgra[offset]:X2}";

    private static bool TryParseHex(string value, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        string hex = value.Trim().TrimStart('#');

        if (hex.Length == 8)
        {
            hex = hex[2..];
        }

        if (hex.Length != 6 ||
            !byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out r) ||
            !byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out g) ||
            !byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out b))
        {
            return false;
        }

        return true;
    }
}
