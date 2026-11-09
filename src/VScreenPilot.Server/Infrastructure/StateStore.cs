using System.Text.Json;
using System.Text.Json.Nodes;

namespace VScreenPilot.Infrastructure;

public sealed class AppState
{
    public int Pid { get; set; }
    public string ExePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long Hwnd { get; set; }
    public bool LaunchedByUs { get; set; }
    public int ShowCmd { get; set; }
    public int MinX { get; set; }
    public int MinY { get; set; }
    public int MaxX { get; set; }
    public int MaxY { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}

public sealed class SessionState
{
    public int Pid { get; set; }
    public string StartedAt { get; set; } = string.Empty;
    public int DisplayIndex { get; set; } = -1;
    public string DisplayDevice { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public List<AppState> Apps { get; } = new();
}

/// <summary>
/// 会话状态持久化（%LOCALAPPDATA%\VScreenPilot\state.json）。
/// 用于崩溃后恢复窗口位置与识别遗留的虚拟屏归属。
/// </summary>
public sealed class StateStore
{
    private readonly LogRing _log;
    private readonly string? _path;

    public StateStore(LogRing log)
    {
        _log = log;

        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VScreenPilot");
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "state.json");
        }
        catch (Exception exception)
        {
            _log.Warn($"状态目录不可用: {exception.Message}");
            _path = null;
        }
    }

    public string? FilePath => _path;

    /// <summary>供 --doctor 使用的摘要，不输出窗口标题等用户内容。</summary>
    public JsonObject Describe()
    {
        SessionState? state = Load();
        if (state is null)
        {
            return new JsonObject { ["present"] = false };
        }

        return new JsonObject
        {
            ["present"] = true,
            ["pid"] = state.Pid,
            ["started_at"] = state.StartedAt,
            ["display_index"] = state.DisplayIndex,
            ["display_device"] = state.DisplayDevice,
            ["apps"] = state.Apps.Count,
        };
    }

    public void Save(SessionState state)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var apps = new JsonArray();
            foreach (var app in state.Apps)
            {
                apps.Add((JsonNode)new JsonObject
                {
                    ["pid"] = app.Pid,
                    ["exe"] = app.ExePath,
                    ["title"] = app.Title,
                    ["hwnd"] = app.Hwnd,
                    ["launchedByUs"] = app.LaunchedByUs,
                    ["showCmd"] = app.ShowCmd,
                    ["min"] = new JsonArray(app.MinX, app.MinY),
                    ["max"] = new JsonArray(app.MaxX, app.MaxY),
                    ["normal"] = new JsonArray(app.Left, app.Top, app.Right, app.Bottom),
                });
            }

            var root = new JsonObject
            {
                ["version"] = 1,
                ["pid"] = state.Pid,
                ["startedAt"] = state.StartedAt,
                ["display"] = new JsonObject
                {
                    ["index"] = state.DisplayIndex,
                    ["device"] = state.DisplayDevice,
                    ["x"] = state.X,
                    ["y"] = state.Y,
                    ["width"] = state.Width,
                    ["height"] = state.Height,
                },
                ["apps"] = apps,
            };

            string temp = _path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception exception)
        {
            _log.Warn($"写入状态失败: {exception.Message}");
        }
    }

    public SessionState? Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;

            var state = new SessionState
            {
                Pid = root.TryGetProperty("pid", out var pid) ? pid.GetInt32() : 0,
                StartedAt = root.TryGetProperty("startedAt", out var started) ? started.GetString() ?? string.Empty : string.Empty,
            };

            if (root.TryGetProperty("display", out var display))
            {
                state.DisplayIndex = display.TryGetProperty("index", out var index) ? index.GetInt32() : -1;
                state.DisplayDevice = display.TryGetProperty("device", out var device) ? device.GetString() ?? string.Empty : string.Empty;
                state.X = display.TryGetProperty("x", out var x) ? x.GetInt32() : 0;
                state.Y = display.TryGetProperty("y", out var y) ? y.GetInt32() : 0;
                state.Width = display.TryGetProperty("width", out var width) ? width.GetInt32() : 0;
                state.Height = display.TryGetProperty("height", out var height) ? height.GetInt32() : 0;
            }

            if (root.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
            {
                foreach (var app in apps.EnumerateArray())
                {
                    var entry = new AppState
                    {
                        Pid = app.TryGetProperty("pid", out var p) ? p.GetInt32() : 0,
                        ExePath = app.TryGetProperty("exe", out var exe) ? exe.GetString() ?? string.Empty : string.Empty,
                        Title = app.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                        Hwnd = app.TryGetProperty("hwnd", out var hwnd) ? hwnd.GetInt64() : 0,
                        LaunchedByUs = app.TryGetProperty("launchedByUs", out var launched) && launched.GetBoolean(),
                        ShowCmd = app.TryGetProperty("showCmd", out var showCmd) ? showCmd.GetInt32() : 1,
                    };

                    if (app.TryGetProperty("min", out var min) &&
                        min.ValueKind == JsonValueKind.Array &&
                        min.GetArrayLength() >= 2)
                    {
                        entry.MinX = min[0].GetInt32();
                        entry.MinY = min[1].GetInt32();
                    }

                    if (app.TryGetProperty("max", out var max) &&
                        max.ValueKind == JsonValueKind.Array &&
                        max.GetArrayLength() >= 2)
                    {
                        entry.MaxX = max[0].GetInt32();
                        entry.MaxY = max[1].GetInt32();
                    }

                    if (app.TryGetProperty("normal", out var normal) &&
                        normal.ValueKind == JsonValueKind.Array &&
                        normal.GetArrayLength() >= 4)
                    {
                        entry.Left = normal[0].GetInt32();
                        entry.Top = normal[1].GetInt32();
                        entry.Right = normal[2].GetInt32();
                        entry.Bottom = normal[3].GetInt32();
                    }

                    state.Apps.Add(entry);
                }
            }

            return state;
        }
        catch (Exception exception)
        {
            _log.Warn($"读取状态失败: {exception.Message}");
            return null;
        }
    }

    public void Clear()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // 忽略。
        }
    }
}
