using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Json;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>wait_for 所需的最小宿主接口。</summary>
internal sealed record WaitDisplayContext(
    Func<DisplayInfo> EnsureDisplay,
    Func<int, int, int, int, (byte[] Bgra, int Width, int Height)> CaptureRegion);

internal sealed record WaitSessionContext(
    Func<int, bool> TryEnterRead,
    Action ExitRead,
    Func<bool> IsReadLockHeld,
    Func<bool> IsCancelled,
    Action<int, int, string> ReportProgress);

internal sealed record WaitAppContext(
    Func<int?, TrackedApp?> FindApp,
    Func<List<TrackedApp>> OwnedAppsSnapshot,
    Func<HashSet<int>> TrackedPids,
    UiaHost Uia);

internal sealed record WaitFrameContext(
    Func<JsonElement, int, int, int, int, FrameSnapshot?> ResolveBaselineFrame,
    Func<JsonArray> Frames,
    Func<JsonElement, MatchVisualView> MatchVisual);

/// <summary>wait_for：轮询 element/text/color/window/pixel_diff/visual 条件。</summary>
internal sealed class WaitService
{
    private readonly WaitDisplayContext _display;
    private readonly WaitSessionContext _session;
    private readonly WaitAppContext _apps;
    private readonly WaitFrameContext _frames;

    public WaitService(WaitDisplayContext display, WaitSessionContext session, WaitAppContext apps, WaitFrameContext frames)
    {
        _display = display;
        _session = session;
        _apps = apps;
        _frames = frames;
    }

    public WaitForView Wait(
        JsonElement condition, int timeoutMs, int? intervalMs = null,
        bool clipOutOfBounds = false)
    {
        // 进程条件不需要虚拟屏；不要为了等一个安装器把屏挂起来。
        string conditionType = (GetString(condition, "type") ?? "element").Trim().ToLowerInvariant();
        DisplayInfo? display = conditionType == "process" ? null : _display.EnsureDisplay();
        timeoutMs = Math.Clamp(timeoutMs, 0, 120_000);
        int? requestedInterval = intervalMs is null ? null : Math.Clamp(intervalMs.Value, 50, 2000);

        var stopwatch = Stopwatch.StartNew();
        // 拿不到短读锁说明 release 正持写锁：本轮按未满足处理，继续走超时/取消。
        JsonObject Evaluate()
        {
            // run_actions 等外层已经持有会话读锁时会再次进入这里；NoRecursion
            // 的 ReaderWriterLockSlim 会抛 LockRecursionException。外层读锁已经
            // 提供了同样的保护，此时不能再嵌套获取/释放。
            bool alreadyHeld = _session.IsReadLockHeld();

            if (!alreadyHeld && !_session.TryEnterRead(250))
            {
                return new JsonObject { ["satisfied"] = false, ["reason"] = "session_busy" };
            }

            try
            {
                return EvaluateCondition(condition, display, clipOutOfBounds);
            }
            finally
            {
                if (!alreadyHeld)
                {
                    _session.ExitRead();
                }
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
                _session.ReportProgress(
                    (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
                    Math.Max(1, timeoutMs),
                    "wait_for 轮询中");
                Thread.Sleep(sleep);
            }

            if (_session.IsCancelled())
            {
                throw new ToolException("E_CANCELLED", "请求已取消");
            }

            last = Evaluate();
        }

        bool satisfied = last["satisfied"]?.GetValue<bool>() ?? false;

        return new WaitForView(satisfied, stopwatch.ElapsedMilliseconds, last);
    }

    private JsonObject EvaluateCondition(
        JsonElement condition, DisplayInfo? display, bool clipOutOfBounds)
    {
        string type = GetString(condition, "type") ?? "element";

        return type.ToLowerInvariant() switch
        {
            "element" => EvaluateElement(condition),
            "text" => EvaluateText(condition, display!, clipOutOfBounds),
            "color" => EvaluateColor(condition, display!, clipOutOfBounds),
            "window" => EvaluateWindow(condition),
            "process" => EvaluateProcess(condition),
            "pixel_diff" => EvaluatePixelDiff(condition, display!, clipOutOfBounds),
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

        IntPtr hwnd = _apps.FindApp(pid)?.Hwnd ?? IntPtr.Zero;

        if (hwnd == IntPtr.Zero)
        {
            return new JsonObject { ["satisfied"] = false, ["reason"] = "no_target_window" };
        }

        try
        {
            UiaElementInfo? info = _apps.Uia.FindElement(hwnd, name, controlType, automationId, null);
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

    private JsonObject EvaluateText(JsonElement condition, DisplayInfo display, bool clipOutOfBounds)
    {
        string? needle = GetString(condition, "text");

        if (string.IsNullOrWhiteSpace(needle))
        {
            throw new ToolException("E_INVALID_ARG", "text 条件需要非空 text");
        }

        TextBounds? region = ReadTextRegion(condition, display, clipOutOfBounds);
        List<TrackedApp> candidates = _apps.OwnedAppsSnapshot();
        int? requestedPid = GetInt(condition, "pid", -1) is var pidValue && pidValue >= 0
            ? pidValue
            : null;

        foreach (TrackedApp app in candidates)
        {
            if (requestedPid is not null && app.Pid != requestedPid)
            {
                continue;
            }

            JsonObject? found = MatchTextInApp(app, needle!, region);
            if (found is not null)
            {
                return found;
            }
        }

        return new JsonObject
        {
            ["satisfied"] = false,
            ["reason"] = candidates.Count == 0 ? "no_target_window" : "uia_text_not_found",
        };
    }

    private static TextBounds? ReadTextRegion(
        JsonElement condition, DisplayInfo display, bool clipOutOfBounds)
    {
        if (!condition.TryGetProperty("region", out JsonElement regionElement) ||
            regionElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        (int x, int y, int width, int height) = VisualSupport.ParseRegion(regionElement);

        if (clipOutOfBounds)
        {
            var clipped = VisualSupport.ClipRegionToDisplay(display, x, y, width, height, "text");
            return new TextBounds(clipped.X, clipped.Y, clipped.Width, clipped.Height);
        }

        VisualSupport.RequireRegionInDisplay(display, x, y, width, height, "text");
        return new TextBounds(x, y, width, height);
    }

    private JsonObject? MatchTextInApp(TrackedApp app, string needle, TextBounds? region)
    {
        UiaElementInfo? info = _apps.Uia.FindTextElement(app.Hwnd, needle);

        if (info is null || info.Offscreen)
        {
            return null;
        }

        if (region is { } bounds && IsOutside(info, bounds))
        {
            return null;
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

    private static bool IsOutside(UiaElementInfo info, TextBounds bounds) =>
        info.Left + info.Width <= bounds.X ||
        info.Top + info.Height <= bounds.Y ||
        info.Left >= bounds.X + bounds.Width ||
        info.Top >= bounds.Y + bounds.Height;

    private readonly record struct TextBounds(int X, int Y, int Width, int Height);

    private JsonObject EvaluateProcess(JsonElement condition)
    {
        string? name = GetString(condition, "name");
        int? pid = GetInt(condition, "pid", -1) is var requested && requested >= 0
            ? requested
            : null;
        string state = (GetString(condition, "state") ?? "exited").Trim().ToLowerInvariant();

        if (pid is null && string.IsNullOrWhiteSpace(name))
        {
            throw new ToolException("E_INVALID_ARG", "process 条件需要 pid 或 name");
        }

        if (state is not ("running" or "exited"))
        {
            throw new ToolException("E_INVALID_ARG", $"process state 只能是 running/exited（收到 {state}）");
        }

        Process? match = null;
        int count = 0;

        try
        {
            if (pid is not null)
            {
                try
                {
                    match = Process.GetProcessById(pid.Value);
                    count = 1;
                }
                catch (ArgumentException)
                {
                    count = 0;
                }
            }
            else
            {
                string bare = Path.GetFileNameWithoutExtension(name!.Trim());
                Process[] candidates = Process.GetProcessesByName(bare);
                count = candidates.Length;
                match = candidates.FirstOrDefault();

                foreach (Process extra in candidates.Skip(1))
                {
                    extra.Dispose();
                }
            }

            bool running = match is not null && IsAlive(match);
            int? exitCode = null;

            if (match is not null && !running)
            {
                try
                {
                    exitCode = match.ExitCode;
                }
                catch (Exception)
                {
                    // 已退出但拿不到退出码（权限/竞态）。
                }
            }

            bool satisfied = state == "running" ? running : !running;

            return new JsonObject
            {
                ["satisfied"] = satisfied,
                ["process"] = match is null
                    ? null
                    : new JsonObject
                    {
                        ["pid"] = match.Id,
                        ["name"] = SafeName(match),
                        ["running"] = running,
                        ["exit_code"] = exitCode,
                    },
                ["matches"] = count,
            };
        }
        finally
        {
            match?.Dispose();
        }
    }

    private static bool IsAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string SafeName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private JsonObject EvaluateColor(
        JsonElement condition, DisplayInfo display, bool clipOutOfBounds)
    {
        int x = GetInt(condition, "x", int.MinValue);
        int y = GetInt(condition, "y", int.MinValue);
        string? value = GetString(condition, "value");

        if (x == int.MinValue || y == int.MinValue || value is null ||
            !VisualSupport.TryParseHex(value, out byte r, out byte g, out byte b))
        {
            throw new ToolException("E_INVALID_ARG", "color 条件需要 x、y 与 #RRGGBB");
        }

        if (clipOutOfBounds)
        {
            var clipped = VisualSupport.ClipRegionToDisplay(display, x, y, 1, 1, "color");
            x = clipped.X;
            y = clipped.Y;
        }
        else
        {
            VisualSupport.RequireRegionInDisplay(display, x, y, 1, 1, "color");
        }

        byte[] pixel = _display.CaptureRegion(x, y, 1, 1).Bgra;
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
        var query = new WindowWaitQuery(
            Title: GetString(condition, "title"),
            State: GetString(condition, "state") ?? "exists",
            Scope: (GetString(condition, "scope") ?? "tracked").ToLowerInvariant(),
            MinWidth: GetInt(condition, "min_width", 0),
            MinHeight: GetInt(condition, "min_height", 0));

        ValidateWindowScope(query.Scope);

        HashSet<int> pids = query.Scope == "tracked" ? _apps.TrackedPids() : new HashSet<int>();

        if (query.Scope == "tracked" && pids.Count == 0)
        {
            return new JsonObject
            {
                ["satisfied"] = false,
                ["reason"] = "no_target_window",
            };
        }

        WindowInfo? window = FindWindow(query, pids);
        bool satisfied = IsWindowStateSatisfied(query.State, window);
        bool trackedWindow = window is not null && pids.Contains(window.Pid);

        return new JsonObject
        {
            ["satisfied"] = satisfied,
            ["scope"] = query.Scope,
            ["window"] = window is null ? null : BuildWindowNode(window, trackedWindow),
        };
    }

    private static void ValidateWindowScope(string scope)
    {
        if (scope is not ("tracked" or "any"))
        {
            throw new ToolException("E_INVALID_ARG", $"未知 window scope: {scope}");
        }
    }

    // title 可省略：用于等待“任意窗口出现”或“标题从 splash 变成主窗口”。
    // min_width/min_height 可过滤启动 splash。
    private static WindowInfo? FindWindow(WindowWaitQuery query, HashSet<int> pids) =>
        WindowManager.ListTopLevel()
            .Where(item =>
                item.Visible &&
                (string.IsNullOrWhiteSpace(query.Title) ||
                 item.Title.Contains(query.Title, StringComparison.OrdinalIgnoreCase)) &&
                (query.MinWidth <= 0 || item.Width >= query.MinWidth) &&
                (query.MinHeight <= 0 || item.Height >= query.MinHeight) &&
                (query.Scope == "any" || pids.Contains(item.Pid)) &&
                item.Area >= 100 * 80)
            .OrderByDescending(item => item.Area)
            .FirstOrDefault();

    private static bool IsWindowStateSatisfied(string state, WindowInfo? window) =>
        state.ToLowerInvariant() switch
        {
            "exists" => window is not null,
            "visible" => window is { Visible: true },
            "foreground" => window is not null &&
                            WindowManager.GetForegroundWindow() == window.Hwnd,
            _ => throw new ToolException("E_INVALID_ARG", $"未知 window state: {state}"),
        };

    private static JsonObject BuildWindowNode(WindowInfo window, bool trackedWindow) =>
        new()
        {
            ["hwnd"] = window.Hwnd.ToInt64(),
            ["tracked"] = trackedWindow,
            // scope=any 只用于判断存在性；非本会话窗口不回传标题/PID，
            // 避免把用户主屏的邮件主题、文档名泄露给模型。
            ["title"] = trackedWindow ? window.Title : "<redacted>",
            ["pid"] = trackedWindow ? window.Pid : null,
        };

    private sealed record WindowWaitQuery(
        string? Title, string State, string Scope, int MinWidth, int MinHeight);

    private JsonObject EvaluatePixelDiff(
        JsonElement condition, DisplayInfo display, bool clipOutOfBounds)
    {
        if (!condition.TryGetProperty("region", out JsonElement regionElement) ||
            regionElement.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "pixel_diff 条件需要 region");
        }

        (int x, int y, int width, int height) = VisualSupport.ParseRegion(regionElement);

        if (clipOutOfBounds)
        {
            var clipped = VisualSupport.ClipRegionToDisplay(
                display, x, y, width, height, "pixel_diff");
            x = clipped.X;
            y = clipped.Y;
            width = clipped.Width;
            height = clipped.Height;
        }
        else
        {
            VisualSupport.RequireRegionInDisplay(display, x, y, width, height, "pixel_diff");
        }
        (byte[] bgra, int capturedWidth, int capturedHeight) = _display.CaptureRegion(x, y, width, height);

        FrameSnapshot? baseline = _frames.ResolveBaselineFrame(
            condition, x, y, capturedWidth, capturedHeight);

        if (baseline is null)
        {
            return new JsonObject
            {
                ["satisfied"] = false,
                ["reason"] = "no_matching_baseline",
                ["baseline"] = null,
                ["available_frames"] = _frames.Frames(),
            };
        }

        double threshold = ResolveChangedThreshold(condition);
        double ratio = VisualSupport.ChangedRatio(bgra, baseline.Bgra, GetInt(condition, "tolerance", 8));

        return new JsonObject
        {
            ["satisfied"] = ratio >= threshold,
            ["changed_ratio"] = ratio,
            ["threshold"] = threshold,
            ["min_changed_ratio"] = threshold,
            ["baseline_frame_id"] = baseline.Id,
        };
    }

    /// <summary>
    /// pixel_diff 的阈值：schema/文档使用 min_changed_ratio；旧调用方可能传 threshold。
    /// 两者都接受，但必须落在 0..1，避免“恒真/恒假”等待。
    /// </summary>
    internal static double ResolveChangedThreshold(JsonElement condition)
    {
        double threshold = GetDouble(
            condition, "min_changed_ratio", GetDouble(condition, "threshold", 0.01));

        if (threshold is < 0 or > 1)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"min_changed_ratio 必须在 0..1（收到 {threshold}）",
                new JsonObject { ["field"] = "min_changed_ratio", ["value"] = threshold },
                retryable: false,
                hint: "阈值是变化像素比例；0 表示任何变化都满足，1 表示全部变化",
                stage: "validate");
        }

        return threshold;
    }

    private JsonObject EvaluateVisual(JsonElement condition)
    {
        // 直接复用 match_visual：condition 与 match_visual 参数同构（type 字段会被忽略）。
        try
        {
            MatchVisualView match = _frames.MatchVisual(condition);

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
