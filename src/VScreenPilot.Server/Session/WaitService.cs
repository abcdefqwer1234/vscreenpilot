using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Json;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Mcp;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>wait_for 所需的最小宿主接口。</summary>
internal interface IWaitHost
{
    DisplayInfo EnsureDisplay();
    void EnterRead();
    void ExitRead();
    bool IsCancelled();
    TrackedApp? FindApp(int? pid);
    UiaHost Uia { get; }
    List<TrackedApp> OwnedAppsSnapshot();
    HashSet<int> TrackedPids();
    (byte[] Bgra, int Width, int Height) CaptureRegion(int x, int y, int width, int height);
    FrameSnapshot? ResolveBaselineFrame(JsonElement source, int x, int y, int width, int height);
    JsonArray Frames();
    MatchVisualView MatchVisual(JsonElement arguments);
    void ReportProgress(int done, int total, string message);
}

/// <summary>wait_for：轮询 element/text/color/window/pixel_diff/visual 条件。</summary>
internal sealed class WaitService
{
    private readonly IWaitHost _host;

    public WaitService(IWaitHost host) => _host = host;

    public WaitForView Wait(JsonElement condition, int timeoutMs, int? intervalMs = null)
    {
        DisplayInfo display = _host.EnsureDisplay();
        timeoutMs = Math.Clamp(timeoutMs, 0, 120_000);
        int? requestedInterval = intervalMs is null ? null : Math.Clamp(intervalMs.Value, 50, 2000);

        var stopwatch = Stopwatch.StartNew();
        // wait_for 可能持续 120s，不能整段持读锁把 release 挡死；
        // 每次轮询取一次短读锁，release 最多等一次轮询。
        JsonObject Evaluate()
        {
            _host.EnterRead();

            try
            {
                return EvaluateCondition(condition, display);
            }
            finally
            {
                _host.ExitRead();
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
                _host.ReportProgress(
                    (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
                    Math.Max(1, timeoutMs),
                    "wait_for 轮询中");
                Thread.Sleep(sleep);
            }

            if (_host.IsCancelled())
            {
                throw new ToolException("E_CANCELLED", "请求已取消");
            }

            last = Evaluate();
        }

        bool satisfied = last["satisfied"]?.GetValue<bool>() ?? false;

        return new WaitForView(satisfied, stopwatch.ElapsedMilliseconds, last);
    }

    private JsonObject EvaluateCondition(JsonElement condition, DisplayInfo display)
    {
        string type = GetString(condition, "type") ?? "element";

        return type.ToLowerInvariant() switch
        {
            "element" => EvaluateElement(condition),
            "text" => EvaluateText(condition, display),
            "color" => EvaluateColor(condition, display),
            "window" => EvaluateWindow(condition),
            "pixel_diff" => EvaluatePixelDiff(condition, display),
            "visual" => EvaluateVisual(condition),
            _ => throw new ToolException("E_INVALID_ARG", $"未知 wait_for 条件类型: {type}"),
        };
    }

    private JsonObject EvaluateElement(JsonElement condition)
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

                IntPtr hwnd = _host.FindApp(pid)?.Hwnd ?? IntPtr.Zero;

                if (hwnd == IntPtr.Zero)
                {
                    return new JsonObject { ["satisfied"] = false, ["reason"] = "no_target_window" };
                }

                try
                {
                    UiaElementInfo? info = _host.Uia.FindElement(hwnd, name, controlType, automationId, null);
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

    private JsonObject EvaluateText(JsonElement condition, DisplayInfo display)
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
                    (int x, int y, int width, int height) = VisualSupport.ParseRegion(regionElement);
                    VisualSupport.RequireRegionInDisplay(display, x, y, width, height, "text");
                    region = (x, y, width, height);
                }

                List<TrackedApp> candidates;

                candidates = _host.OwnedAppsSnapshot();

                int? requestedPid = GetInt(condition, "pid", -1) is var pidValue && pidValue >= 0
                    ? pidValue
                    : null;

                foreach (TrackedApp app in candidates)
                {
                    if (requestedPid is not null && app.Pid != requestedPid)
                    {
                        continue;
                    }

                    UiaElementInfo? info = _host.Uia.FindTextElement(app.Hwnd, needle);

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

    private JsonObject EvaluateColor(JsonElement condition, DisplayInfo display)
    {
                int x = GetInt(condition, "x", int.MinValue);
                int y = GetInt(condition, "y", int.MinValue);
                string? value = GetString(condition, "value");

                if (x == int.MinValue || y == int.MinValue || value is null ||
                    !VisualSupport.TryParseHex(value, out byte r, out byte g, out byte b))
                {
                    throw new ToolException("E_INVALID_ARG", "color 条件需要 x、y 与 #RRGGBB");
                }

                VisualSupport.RequireRegionInDisplay(display, x, y, 1, 1, "color");

                byte[] pixel = _host.CaptureRegion(x, y, 1, 1).Bgra;
                int tolerance = GetInt(condition, "tolerance", 10);
                bool satisfied = VisualSupport.ColorMatches(pixel, 0, r, g, b, tolerance);

                return new JsonObject
                {
                    ["satisfied"] = satisfied,
                    ["actual"] = VisualSupport.ToHex(pixel, 0),
                };
    }

    private JsonObject EvaluateWindow(JsonElement condition)
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

                HashSet<int> pids = scope == "tracked" ? _host.TrackedPids() : new HashSet<int>();

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

    private JsonObject EvaluatePixelDiff(JsonElement condition, DisplayInfo display)
    {
                if (!condition.TryGetProperty("region", out JsonElement regionElement) ||
                    regionElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ToolException("E_INVALID_ARG", "pixel_diff 条件需要 region");
                }

                (int x, int y, int width, int height) = VisualSupport.ParseRegion(regionElement);
                VisualSupport.RequireRegionInDisplay(display, x, y, width, height, "pixel_diff");
                (byte[] bgra, int capturedWidth, int capturedHeight) = _host.CaptureRegion(x, y, width, height);

                FrameSnapshot? baseline = _host.ResolveBaselineFrame(
                    condition, x, y, capturedWidth, capturedHeight);

                if (baseline is null)
                {
                    return new JsonObject
                    {
                        ["satisfied"] = false,
                        ["reason"] = "no_matching_baseline",
                        ["baseline"] = null,
                        ["available_frames"] = _host.Frames(),
                    };
                }

                double threshold = GetDouble(condition, "threshold", 0.01);
                double ratio = VisualSupport.ChangedRatio(bgra, baseline.Bgra, GetInt(condition, "tolerance", 8));

                return new JsonObject
                {
                    ["satisfied"] = ratio >= threshold,
                    ["changed_ratio"] = ratio,
                    ["threshold"] = threshold,
                    ["baseline_frame_id"] = baseline.Id,
                };
    }

    private JsonObject EvaluateVisual(JsonElement condition)
    {
                // 直接复用 match_visual：condition 与 match_visual 参数同构（type 字段会被忽略）。
                try
                {
                    MatchVisualView match = _host.MatchVisual(condition);

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
}
