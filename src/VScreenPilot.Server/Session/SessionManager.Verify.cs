using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Interop;
using VScreenPilot.Json;
using VScreenPilot.Mcp;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using static VScreenPilot.Mcp.JsonArgReader;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    public object FindElement(
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
        string prefer = "auto") =>
        _finder.Find(
            name, controlType, automationId, nameMatch, index, all, maxResults,
            includeOffscreen, pid, role, prefer);

    public WaitForView WaitFor(JsonElement condition, int timeoutMs, int? intervalMs = null)
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

        return new WaitForView(satisfied, stopwatch.ElapsedMilliseconds, last);
    }

    public AssertRegionView AssertRegion(JsonElement region, JsonElement expectation)
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
                        ["element"] = info is null ? null : ElementPresenter.ToJson(info),
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
                        ["element"] = ElementPresenter.ToCompactJson(info),
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
                    MatchVisualView match = MatchVisual(condition);

                    return new JsonObject
                    {
                        ["satisfied"] = true,
                        ["count"] = match.Count,
                        ["first"] = match.Matches is { Count: > 0 }
                            ? VspJson.ToNode(match.Matches[0])
                            : null,
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

    private AssertRegionView AssertColor(
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

        return new AssertRegionView(
            passed,
            Math.Round(ratio, 4),
            Math.Round(ratio, 4),
            coverage,
            "coverage 是请求阈值；actual_coverage/matched_ratio 是实际覆盖率",
            tolerance,
            hex,
            total == 0
                ? "#000000"
                : $"#{sumR / total:X2}{sumG / total:X2}{sumB / total:X2}",
            new SizeView(width, height),
            ChangedRatio: null, Threshold: null, ExpectChanged: null,
            BaselineFrameId: null, Baseline: null);
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

    private AssertRegionView AssertChanged(
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

        return new AssertRegionView(
            passed,
            MatchedRatio: null, ActualCoverage: null, Coverage: null, CoverageSemantics: null,
            Tolerance: null, Expected: null, Average: null, Region: null,
            Math.Round(ratio, 4),
            threshold,
            expectChanged,
            baseline.Id,
            new BoundsView(baseline.X, baseline.Y, baseline.Width, baseline.Height));
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

    private TrackedApp? LastTrackedApp() => LastLiveTrackedApp();

    private HashSet<int> TrackedPids()
    {
        lock (_gate)
        {
            return _apps.Where(IsOwned).Select(app => app.Pid).ToHashSet();
        }
    }

}
