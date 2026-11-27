using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Server.Infrastructure;

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
    public string OwnerClientId { get; set; } = string.Empty;
    public bool Dedicated { get; set; }
    public bool Detached { get; set; }
    public string TempProfile { get; set; } = string.Empty;
    public string ClientKey { get; set; } = string.Empty;
}

public sealed class SessionState
{
    public int Pid { get; set; }
    public string StartedAt { get; set; } = string.Empty;
    public string SavedAt { get; set; } = string.Empty;
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
public sealed class StateStore : ISessionStateStore
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
            _path = Path.Combine(directory, Profile.StateFileName(Profile.Resolve()));
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
                    // 不持久化窗口标题：可能包含文档名/客户名，属于隐私面。
                    ["hwnd"] = app.Hwnd,
                    ["launchedByUs"] = app.LaunchedByUs,
                    ["ownerClientId"] = app.OwnerClientId,
                    ["dedicated"] = app.Dedicated,
                    ["detached"] = app.Detached,
                    ["tempProfile"] = app.TempProfile,
                    ["clientKey"] = app.ClientKey,
                    ["showCmd"] = app.ShowCmd,
                    ["min"] = new JsonArray(app.MinX, app.MinY),
                    ["max"] = new JsonArray(app.MaxX, app.MaxY),
                    ["normal"] = new JsonArray(app.Left, app.Top, app.Right, app.Bottom),
                });
            }

            var root = new JsonObject
            {
                ["version"] = 2,
                ["pid"] = state.Pid,
                ["startedAt"] = state.StartedAt,
                ["savedAt"] = DateTimeOffset.UtcNow.ToString("O"),
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
                StartedAt = root.TryGetProperty("startedAt", out var started)
                    ? started.GetString() ?? string.Empty
                    : string.Empty,
                SavedAt = root.TryGetProperty("savedAt", out var savedAt)
                    ? savedAt.GetString() ?? string.Empty
                    : string.Empty,
            };

            if (root.TryGetProperty("display", out var display))
            {
                ParseDisplay(display, state);
            }

            if (root.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
            {
                ParseApps(apps, state);
            }

            return state;
        }
        catch (Exception exception)
        {
            _log.Warn($"读取状态失败: {exception.Message}");
            return null;
        }
    }

    private static void ParseDisplay(JsonElement display, SessionState state)
    {
        state.DisplayIndex = display.TryGetProperty("index", out var index) ? index.GetInt32() : -1;
        state.DisplayDevice = display.TryGetProperty("device", out var device)
            ? device.GetString() ?? string.Empty
            : string.Empty;
        state.X = display.TryGetProperty("x", out var x) ? x.GetInt32() : 0;
        state.Y = display.TryGetProperty("y", out var y) ? y.GetInt32() : 0;
        state.Width = display.TryGetProperty("width", out var width) ? width.GetInt32() : 0;
        state.Height = display.TryGetProperty("height", out var height) ? height.GetInt32() : 0;
    }

    private static void ParseApps(JsonElement apps, SessionState state)
    {
        foreach (var app in apps.EnumerateArray())
        {
            state.Apps.Add(ParseApp(app));
        }
    }

    private static AppState ParseApp(JsonElement app)
    {
        var entry = new AppState
        {
            Pid = app.TryGetProperty("pid", out var p) ? p.GetInt32() : 0,
            ExePath = app.TryGetProperty("exe", out var exe) ? exe.GetString() ?? string.Empty : string.Empty,
            Title = app.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
            Hwnd = app.TryGetProperty("hwnd", out var hwnd) ? hwnd.GetInt64() : 0,
            LaunchedByUs = app.TryGetProperty("launchedByUs", out var launched) && launched.GetBoolean(),
            OwnerClientId = app.TryGetProperty("ownerClientId", out var owner)
                ? owner.GetString() ?? string.Empty
                : string.Empty,
            Dedicated = app.TryGetProperty("dedicated", out var dedicated) && dedicated.GetBoolean(),
            Detached = app.TryGetProperty("detached", out var detached) && detached.GetBoolean(),
            TempProfile = app.TryGetProperty("tempProfile", out var tempProfile)
                ? tempProfile.GetString() ?? string.Empty
                : string.Empty,
            ClientKey = app.TryGetProperty("clientKey", out var clientKey)
                ? clientKey.GetString() ?? string.Empty
                : string.Empty,
            ShowCmd = app.TryGetProperty("showCmd", out var showCmd) ? showCmd.GetInt32() : 1,
        };

        if (TryReadArray(app, "min", 2, out var min))
        {
            entry.MinX = min[0].GetInt32();
            entry.MinY = min[1].GetInt32();
        }

        if (TryReadArray(app, "max", 2, out var max))
        {
            entry.MaxX = max[0].GetInt32();
            entry.MaxY = max[1].GetInt32();
        }

        if (TryReadArray(app, "normal", 4, out var normal))
        {
            entry.Left = normal[0].GetInt32();
            entry.Top = normal[1].GetInt32();
            entry.Right = normal[2].GetInt32();
            entry.Bottom = normal[3].GetInt32();
        }

        return entry;
    }

    private static bool TryReadArray(
        JsonElement element, string property, int minimumLength, out JsonElement array)
    {
        array = default;

        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() < minimumLength)
        {
            return false;
        }

        array = value;
        return true;
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
        catch (Exception exception)
        {
            // 忽略。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
    }
}
