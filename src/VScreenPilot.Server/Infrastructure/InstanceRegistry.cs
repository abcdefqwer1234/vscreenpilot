using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Ipc;

namespace VScreenPilot.Infrastructure;

/// <summary>
/// 当前 profile 的活动实例登记。用于“实例已被占用”时给出 pid/cwd/profile，
/// 以及在 --doctor 中列出所有已知实例。主数据是 <c>instances/&lt;profile&gt;.json</c>；
/// 旧 0.1.0 没有 registry 时回退读 state.json。
/// </summary>
internal sealed class InstanceRegistry
{
    public sealed class Entry
    {
        public int Pid { get; set; }
        public string StartedAt { get; set; } = string.Empty;
        public string Cwd { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public bool Elevated { get; set; }
        public string Profile { get; set; } = "default";
        public string Mutex { get; set; } = string.Empty;
        public string McpPipe { get; set; } = string.Empty;
        public string CtlPipe { get; set; } = string.Empty;
        public string DisplayDevice { get; set; } = string.Empty;
        public bool Ready { get; set; }

        public JsonObject ToJson() => new()
        {
            ["pid"] = Pid,
            ["started_at"] = StartedAt,
            ["cwd"] = Cwd,
            ["version"] = Version,
            ["elevated"] = Elevated,
            ["profile"] = Profile,
            ["mutex"] = Mutex,
            ["mcp_pipe"] = McpPipe,
            ["ctl_pipe"] = CtlPipe,
            ["display_device"] = DisplayDevice,
            ["ready"] = Ready,
        };

        public static Entry? FromJson(JsonNode? node)
        {
            if (node is not JsonObject json)
            {
                return null;
            }

            return new Entry
            {
                Pid = json["pid"]?.GetValue<int>() ?? 0,
                StartedAt = json["started_at"]?.GetValue<string>() ?? string.Empty,
                Cwd = json["cwd"]?.GetValue<string>() ?? string.Empty,
                Version = json["version"]?.GetValue<string>() ?? string.Empty,
                Elevated = json["elevated"]?.GetValue<bool>() ?? false,
                Profile = json["profile"]?.GetValue<string>() ?? "default",
                Mutex = json["mutex"]?.GetValue<string>() ?? string.Empty,
                McpPipe = json["mcp_pipe"]?.GetValue<string>() ?? string.Empty,
                CtlPipe = json["ctl_pipe"]?.GetValue<string>() ?? string.Empty,
                DisplayDevice = json["display_device"]?.GetValue<string>() ?? string.Empty,
                Ready = json["ready"]?.GetValue<bool>() ?? false,
            };
        }
    }

    private readonly string? _path;
    private readonly string _profile;

    public InstanceRegistry(string profile)
    {
        _profile = profile;

        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VScreenPilot", "instances");
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, $"{profile}.json");
        }
        catch
        {
            _path = null;
        }
    }

    public string? FilePath => _path;

    public Entry? Read()
    {
        Entry? entry = ReadFile(_path);

        if (entry is null)
        {
            entry = ReadStateFallback();
        }

        return entry is not null && IsLive(entry.Pid, entry.StartedAt) ? entry : null;
    }

    public void Write(Entry entry)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            string temp = $"{_path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, entry.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // 诊断信息写不进不影响主流程。
        }
    }

    public void Delete()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            // 只删自己写的登记：旧 daemon 退出与新 daemon 启动交错时，
            // 不能把新实例刚写好的 registry 删掉。
            Entry? entry = ReadFile(_path);

            if (entry is not null && entry.Pid != Environment.ProcessId)
            {
                return;
            }

            File.Delete(_path);
        }
        catch
        {
            // 忽略。
        }
    }

    public static Entry Create(string profile, bool ready)
    {
        using Process process = Process.GetCurrentProcess();
        string started;

        try
        {
            started = process.StartTime.ToUniversalTime().ToString("O");
        }
        catch
        {
            started = DateTimeOffset.UtcNow.ToString("O");
        }

        return new Entry
        {
            Pid = Environment.ProcessId,
            StartedAt = started,
            Cwd = Environment.CurrentDirectory,
            Version = BuildInfo.Version,
            Elevated = IsElevated(),
            Profile = profile,
            Mutex = $@"Global\{Profile.MutexName(profile)}",
            McpPipe = PipeNames.Mcp(profile),
            CtlPipe = PipeNames.Ctl(profile),
            Ready = ready,
        };
    }

    public static IReadOnlyList<Entry> ListAll()
    {
        var result = new List<Entry>();

        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VScreenPilot", "instances");

            if (!Directory.Exists(directory))
            {
                return result;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
            {
                Entry? entry = ReadFile(file);

                if (entry is not null && IsLive(entry.Pid, entry.StartedAt))
                {
                    result.Add(entry);
                }
            }
        }
        catch
        {
            // 诊断失败忽略。
        }

        return result;
    }

    /// <summary>
    /// 兼容旧 0.1.0：没有 registry，但可能留下 state.json/state.&lt;profile&gt;.json。
    /// 只用于诊断和“这块屏是否属于别的 live 实例”的判断。
    /// </summary>
    public static IReadOnlyList<Entry> ListStateEntries()
    {
        var result = new List<Entry>();

        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VScreenPilot");

            if (!Directory.Exists(directory))
            {
                return result;
            }

            foreach (string path in Directory.EnumerateFiles(directory, "state*.json"))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                    JsonElement root = document.RootElement;
                    int pid = root.TryGetProperty("pid", out JsonElement pidValue)
                        ? pidValue.GetInt32()
                        : 0;
                    string started = root.TryGetProperty("startedAt", out JsonElement startedValue)
                        ? startedValue.GetString() ?? string.Empty
                        : string.Empty;

                    if (!IsLive(pid, started))
                    {
                        continue;
                    }

                    string file = Path.GetFileName(path);
                    string profile = string.Equals(file, "state.json", StringComparison.OrdinalIgnoreCase)
                        ? "default"
                        : file.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                            ? file[6..^5]
                            : "unknown";

                    result.Add(new Entry
                    {
                        Pid = pid,
                        StartedAt = started,
                        Profile = profile,
                        Mutex = $@"Global\{Profile.MutexName(profile)}",
                        DisplayDevice = root.TryGetProperty("display", out JsonElement display) &&
                                        display.TryGetProperty("device", out JsonElement device)
                            ? device.GetString() ?? string.Empty
                            : string.Empty,
                        Version = "0.1.0-or-older",
                        Ready = false,
                    });
                }
                catch
                {
                    // 单个 state 损坏不影响其他。
                }
            }
        }
        catch
        {
            // 忽略。
        }

        return result;
    }

    private Entry? ReadStateFallback()
    {
        try
        {
            string profile = _profile;
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VScreenPilot",
                Profile.StateFileName(profile));

            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;

            return new Entry
            {
                Pid = root.TryGetProperty("pid", out JsonElement pid) ? pid.GetInt32() : 0,
                StartedAt = root.TryGetProperty("startedAt", out JsonElement started)
                    ? started.GetString() ?? string.Empty
                    : string.Empty,
                Profile = profile,
                Mutex = $@"Global\{Profile.MutexName(profile)}",
                DisplayDevice = root.TryGetProperty("display", out JsonElement display) &&
                                display.TryGetProperty("device", out JsonElement device)
                    ? device.GetString() ?? string.Empty
                    : string.Empty,
                Ready = false,
                Version = "0.1.0-or-older",
            };
        }
        catch
        {
            return null;
        }
    }

    private static Entry? ReadFile(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return Entry.FromJson(JsonNode.Parse(File.ReadAllText(path)));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLive(int pid, string startedAt)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            bool nameMatches = string.Equals(
                process.ProcessName, "VScreenPilot", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(startedAt) ||
                !DateTimeOffset.TryParse(startedAt, out DateTimeOffset expected))
            {
                return nameMatches;
            }

            // 进程名精确匹配（发布版）只用来兜底无启动时间的旧 state；
            // 以启动时间为准，同时覆盖 dotnet 运行、改名 exe，避免 PID 复用后误认。
            return Math.Abs(
                (process.StartTime.ToUniversalTime() - expected.UtcDateTime).TotalSeconds) <= 5;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using System.Security.Principal.WindowsIdentity identity =
                System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
