using System.Diagnostics;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Json;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Ipc;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Interop;
using VScreenPilot.Services;

namespace VScreenPilot.Session;

/// <summary>
/// 只读的系统级观察：顶层窗口清单、进程快照。
/// 目的：第三方弹窗落到主屏/提权程序没有窗口时，agent 至少能"看见"并精确告知用户，
/// 而不是只能靠 E_TARGET_OFFSCREEN 猜。
/// </summary>
internal sealed class SessionInspectOps
{
    private readonly SessionManager _host;

    public SessionInspectOps(SessionManager host) => _host = host;

    public JsonObject ListWindows(WindowQuery query)
    {
        // 只读工具：不主动挂载虚拟屏。没有虚拟屏时 on_display 一律 false，由调用方决定是否 launch。
        // 只认本会话自己的虚拟屏设备，避免多 profile 时把别人的屏当成自己的。
        DisplayInfo? display = _host.VirtualDisplay;

        if (display is null && !string.IsNullOrEmpty(_host._display.State.DeviceName))
        {
            display = DisplayTopology.FindParsecDisplayByDevice(_host._display.State.DeviceName);
        }

        string scope = (query.Scope ?? "all").Trim().ToLowerInvariant();
        int limit = Math.Clamp(query.Limit, 1, 500);

        if (scope is not ("all" or "tracked" or "unmanaged"))
        {
            throw new Errors.ToolException(
                "E_INVALID_ARG", $"未知 scope: {query.Scope}（可选 all/tracked/unmanaged）");
        }

        IReadOnlyList<TrackedApp> apps = _host.Apps;
        var nameCache = new Dictionary<int, string>();
        var elevatedCache = new Dictionary<int, bool?>();
        var rows = new List<(bool OnDisplay, long Area, JsonObject Row)>();

        foreach (WindowInfo window in WindowManager.ListTopLevel())
        {
            bool tracked = apps.Any(app =>
                _host.ClientOps.IsOwned(app) && app.Pid == window.Pid);

            if (scope == "tracked" && !tracked)
            {
                continue;
            }

            if (scope == "unmanaged" && tracked)
            {
                continue;
            }

            bool onDisplay = display is not null && CenterInside(display, window);

            if (!onDisplay && !query.IncludeOffscreen)
            {
                continue;
            }

            string name = ResolveProcessName(window.Pid, nameCache);
            string? title = tracked ? window.Title : null;
            IntPtr owner = NativeMethods.GetWindow(window.Hwnd, NativeMethods.GwOwner);

            if (!string.IsNullOrWhiteSpace(query.TitleContains) &&
                (title is null ||
                 !title.Contains(query.TitleContains, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var row = new JsonObject
            {
                ["hwnd"] = window.Hwnd.ToInt64(),
                ["pid"] = window.Pid,
                ["name"] = name,
                ["title"] = title,
                ["class"] = window.ClassName,
                ["owned"] = window.Owned,
                ["owner_hwnd"] = owner == IntPtr.Zero ? null : owner.ToInt64(),
                ["tracked"] = tracked,
                ["main"] = apps.Any(app => _host.ClientOps.IsOwned(app) && app.Hwnd == window.Hwnd),
                ["on_display"] = onDisplay,
                ["elevated"] = IsTargetElevated(window.Pid, elevatedCache),
                ["bounds"] = new JsonObject
                {
                    ["x"] = window.Rect.Left,
                    ["y"] = window.Rect.Top,
                    ["width"] = window.Width,
                    ["height"] = window.Height,
                },
            };

            if (!tracked)
            {
                // 非本会话窗口不回传标题，避免把用户主屏的邮件/文档名带进模型上下文。
                row["title_redacted"] = true;
            }

            rows.Add((onDisplay, window.Area, row));
        }

        JsonArray windows = [];

        foreach ((bool _, long _, JsonObject row) in rows
                     .OrderByDescending(item => item.OnDisplay)
                     .ThenByDescending(item => item.Area)
                     .Take(limit))
        {
            windows.Add(row);
        }

        return new JsonObject
        {
            ["windows"] = windows,
            ["count"] = windows.Count,
            ["total"] = rows.Count,
            ["display"] = display is null
                ? null
                : new JsonObject
                {
                    ["x"] = display.X,
                    ["y"] = display.Y,
                    ["width"] = display.Width,
                    ["height"] = display.Height,
                },
        };
    }

    public JsonObject GetProcesses(ProcessQuery query)
    {
        int limit = Math.Clamp(query.Limit, 1, 500);
        string? nameFilter = string.IsNullOrWhiteSpace(query.Name)
            ? null
            : Path.GetFileNameWithoutExtension(query.Name.Trim());
        var rows = new List<(string Name, int Pid, JsonObject Row)>();
        var elevatedCache = new Dictionary<int, bool?>();
        Dictionary<int, List<WindowInfo>>? windowsByPid = query.IncludeWindows
            ? BuildWindowMap()
            : null;

        if (query.Pid is { } single)
        {
            JsonObject? one = DescribeProcess(single, windowsByPid, elevatedCache);

            if (one is not null)
            {
                rows.Add((one["name"]?.GetValue<string>() ?? string.Empty, single, one));
            }
        }
        else
        {
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string name = process.ProcessName;

                    if (nameFilter is not null &&
                        !name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    JsonObject? row = DescribeProcess(process, windowsByPid, elevatedCache);

                    if (row is not null)
                    {
                        rows.Add((name, process.Id, row));
                    }
                }
                catch (Exception)
                {
                    // 进程可能在枚举期间退出。
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        JsonArray processes = [];

        foreach ((string _, int _, JsonObject row) in rows
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Pid)
                     .Take(limit))
        {
            processes.Add(row);
        }

        return new JsonObject
        {
            ["processes"] = processes,
            ["count"] = processes.Count,
            ["total"] = rows.Count,
            ["filtered"] = nameFilter,
        };
    }

    /// <summary>一次 EnumWindows 建 pid→窗口表，避免每个进程各枚举一遍。</summary>
    private static Dictionary<int, List<WindowInfo>> BuildWindowMap()
    {
        var map = new Dictionary<int, List<WindowInfo>>();

        foreach (WindowInfo window in WindowManager.ListTopLevel())
        {
            if (!map.TryGetValue(window.Pid, out List<WindowInfo>? list))
            {
                map[window.Pid] = list = [];
            }

            list.Add(window);
        }

        foreach (List<WindowInfo> list in map.Values)
        {
            list.Sort((left, right) => right.Area.CompareTo(left.Area));
        }

        return map;
    }

    private JsonObject? DescribeProcess(int pid, Dictionary<int, List<WindowInfo>>? windowsByPid,
        Dictionary<int, bool?> elevatedCache)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return DescribeProcess(process, windowsByPid, elevatedCache);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private JsonObject? DescribeProcess(Process process,
        Dictionary<int, List<WindowInfo>>? windowsByPid,
        Dictionary<int, bool?> elevatedCache)
    {
        try
        {
            if (process.HasExited)
            {
                return null;
            }

            var row = new JsonObject
            {
                ["pid"] = process.Id,
                ["name"] = process.ProcessName,
                ["session_id"] = process.SessionId,
                ["elevated"] = IsTargetElevated(process.Id, elevatedCache),
            };

            try
            {
                row["started_at"] = process.StartTime.ToUniversalTime().ToString("O");
                row["uptime_ms"] = (long)Math.Max(0, (DateTime.Now - process.StartTime).TotalMilliseconds);
            }
            catch (Exception)
            {
                // 受保护进程读不到启动时间。
            }

            if (windowsByPid is not null)
            {
                List<WindowInfo> windows = windowsByPid.TryGetValue(process.Id, out List<WindowInfo>? list)
                    ? list
                    : [];

                bool tracked = _host.Apps.Any(app =>
                    _host.ClientOps.IsOwned(app) && app.Pid == process.Id);

                row["window_count"] = windows.Count;
                row["main_window"] = windows.Count == 0
                    ? null
                    : new JsonObject
                    {
                        ["hwnd"] = windows[0].Hwnd.ToInt64(),
                        ["title"] = tracked ? windows[0].Title : null,
                        ["title_redacted"] = !tracked,
                        ["class"] = windows[0].ClassName,
                    };
            }

            return row;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ResolveProcessName(int pid, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(pid, out string? cached))
        {
            return cached;
        }

        string name;

        try
        {
            using Process process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch (Exception)
        {
            name = string.Empty;
        }

        cache[pid] = name;
        return name;
    }

    private static bool? IsTargetElevated(int pid, Dictionary<int, bool?> cache)
    {
        if (pid <= 0)
        {
            return null;
        }

        if (cache.TryGetValue(pid, out bool? cached))
        {
            return cached;
        }

        bool? value = IntegrityCheck.TryCompareIntegrity((uint)pid, out int target, out int self)
            ? target > self
            : null;

        cache[pid] = value;
        return value;
    }

    private static bool CenterInside(DisplayInfo display, WindowInfo window)
    {
        long centerX = window.Rect.Left + window.Width / 2L;
        long centerY = window.Rect.Top + window.Height / 2L;

        return centerX >= display.X &&
               centerX < (long)display.X + display.Width &&
               centerY >= display.Y &&
               centerY < (long)display.Y + display.Height;
    }
}

/// <summary>顶层窗口清单查询。</summary>
internal sealed record WindowQuery
{
    public string Scope { get; init; } = "all";
    public bool IncludeOffscreen { get; init; } = true;
    public string? TitleContains { get; init; }
    public int Limit { get; init; } = 200;
}

/// <summary>进程快照查询。</summary>
internal sealed record ProcessQuery
{
    public string? Name { get; init; }
    public int? Pid { get; init; }
    public bool IncludeWindows { get; init; }
    public int Limit { get; init; } = 200;
}
