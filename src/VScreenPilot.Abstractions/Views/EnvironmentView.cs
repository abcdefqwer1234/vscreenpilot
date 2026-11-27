using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

/// <summary>
/// `get_environment` 的领域视图。Server 会在编码后补顶层 `client_id` 与 `screen_lease`。
/// 字段顺序按旧实现的拼装顺序声明，保证 text 逐字节稳定。
/// </summary>
public sealed record EnvironmentView
{
    [JsonPropertyName("driver")] public DriverView Driver { get; init; } = new();
    [JsonPropertyName("session")] public SessionView Session { get; init; } = new();
    [JsonPropertyName("virtual_display")] public VirtualDisplayView VirtualDisplay { get; init; } = new();
    [JsonPropertyName("virtual_screen")] public VirtualScreenView VirtualScreen { get; init; } = new();
    [JsonPropertyName("primary")] public PrimaryView Primary { get; init; } = new();
    [JsonPropertyName("foreground")] public ForegroundView Foreground { get; init; } = new();
    [JsonPropertyName("cursor")] public CursorView Cursor { get; init; } = new();
    [JsonPropertyName("apps")] public IReadOnlyList<AppView> Apps { get; init; } = [];
    [JsonPropertyName("metrics")] public MetricsView Metrics { get; init; } = new();

    /// <summary>仅当焦点元素被脱敏时出现。</summary>
    [JsonPropertyName("privacy_redactions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PrivacyRedactions { get; init; }

    [JsonPropertyName("focused_element")] public JsonNode? FocusedElement { get; init; }
}

public sealed record DriverView
{
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }
}

public sealed record SessionView
{
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("initialized")] public bool Initialized { get; init; }
    [JsonPropertyName("mutexHost")] public string MutexHost { get; init; } = string.Empty;
    [JsonPropertyName("startedAt")] public string StartedAt { get; init; } = string.Empty;
    [JsonPropertyName("session_id")] public int SessionId { get; init; }
    [JsonPropertyName("interactive")] public bool Interactive { get; init; }
    [JsonPropertyName("remote_session")] public bool RemoteSession { get; init; }
    [JsonPropertyName("input_desktop")] public bool InputDesktop { get; init; }
    [JsonPropertyName("elevated")] public bool Elevated { get; init; }
    [JsonPropertyName("isolation")] public string Isolation { get; init; } = string.Empty;
    [JsonPropertyName("profile")] public string Profile { get; init; } = string.Empty;
    [JsonPropertyName("epoch")] public long Epoch { get; init; }
    [JsonPropertyName("job_object")] public string JobObject { get; init; } = string.Empty;
    [JsonPropertyName("recovered_apps")] public int RecoveredApps { get; init; }

    [JsonPropertyName("previous_pid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PreviousPid { get; init; }

    [JsonPropertyName("previous_started_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousStartedAt { get; init; }

    [JsonPropertyName("state_age_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StateAgeMs { get; init; }

    [JsonPropertyName("input_lease")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InputLeaseView? InputLease { get; init; }
    [JsonPropertyName("role")] public string Role { get; init; } = string.Empty;
    [JsonPropertyName("daemon_pid")] public int DaemonPid { get; init; }
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
    [JsonPropertyName("client_cwd")] public string ClientCwd { get; init; } = string.Empty;
    [JsonPropertyName("client_roots")] public IReadOnlyList<string> ClientRoots { get; init; } = [];

    [JsonPropertyName("proxy_pid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProxyPid { get; init; }

    [JsonPropertyName("client_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientVersion { get; init; }

    [JsonPropertyName("clients")] public JsonNode? Clients { get; init; }
    [JsonPropertyName("other_clients")] public int OtherClients { get; init; }

    [JsonPropertyName("screen_lease")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? ScreenLease { get; init; }

    [JsonPropertyName("working_set_mb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? WorkingSetMb { get; init; }

    [JsonPropertyName("handles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Handles { get; init; }
}

public sealed record VirtualDisplayView
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("owned")] public bool Owned { get; init; }

    [JsonPropertyName("device")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Device { get; init; }

    [JsonPropertyName("x")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? X { get; init; }

    [JsonPropertyName("y")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Y { get; init; }

    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; init; }

    [JsonPropertyName("dpi")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Dpi { get; init; }

    [JsonPropertyName("scale_percent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScalePercent { get; init; }

    [JsonPropertyName("self_healed")] public bool SelfHealed { get; init; }
    [JsonPropertyName("self_heal_count")] public int SelfHealCount { get; init; }
    [JsonPropertyName("last_self_heal_reason")] public string? LastSelfHealReason { get; init; }
    [JsonPropertyName("adopted_orphan")] public bool AdoptedOrphan { get; init; }
    [JsonPropertyName("display_recreate_count")] public int DisplayRecreateCount { get; init; }
    [JsonPropertyName("vdd_reopen_count")] public int VddReopenCount { get; init; }
    [JsonPropertyName("position_enforced")] public bool PositionEnforced { get; init; }

    [JsonPropertyName("lifetime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lifetime { get; init; }
}

public sealed record VirtualScreenView
{
    [JsonPropertyName("x")] public int X { get; init; }
    [JsonPropertyName("y")] public int Y { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
    [JsonPropertyName("monitors")] public int Monitors { get; init; }
}

/// <summary>主屏。找不到主屏时所有字段为 null，序列化为 `{}`（与旧实现一致）。</summary>
public sealed record PrimaryView
{
    [JsonPropertyName("device")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Device { get; init; }

    [JsonPropertyName("x")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? X { get; init; }

    [JsonPropertyName("y")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Y { get; init; }

    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; init; }

    [JsonPropertyName("dpi")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Dpi { get; init; }
}

/// <summary>前台窗口。没有前台或不属于本会话时序列化为 `{}`。</summary>
public sealed record ForegroundView
{
    [JsonPropertyName("tracked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Tracked { get; init; }

    [JsonPropertyName("hwnd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Hwnd { get; init; }

    [JsonPropertyName("pid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Pid { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("elevated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Elevated { get; init; }
}

/// <summary>光标位置。取不到时序列化为 `{}`。</summary>
public sealed record CursorView
{
    [JsonPropertyName("x")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? X { get; init; }

    [JsonPropertyName("y")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Y { get; init; }
}

public sealed record AppView
{
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("hwnd")] public long Hwnd { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("exe")] public string Exe { get; init; } = string.Empty;
    [JsonPropertyName("alive")] public bool Alive { get; init; }
    [JsonPropertyName("launched_by_us")] public bool LaunchedByUs { get; init; }
    [JsonPropertyName("owned")] public bool Owned { get; init; }
    [JsonPropertyName("close_on_release")] public bool CloseOnRelease { get; init; }
    [JsonPropertyName("dedicated")] public bool Dedicated { get; init; }
    [JsonPropertyName("detached")] public bool Detached { get; init; }
    [JsonPropertyName("recovered")] public bool Recovered { get; init; }

    [JsonPropertyName("client_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientId { get; init; }
}

public sealed record MetricsView
{
    [JsonPropertyName("calls")] public long Calls { get; init; }
    [JsonPropertyName("errors")] public long Errors { get; init; }
    [JsonPropertyName("error_rate")] public double ErrorRate { get; init; }
    [JsonPropertyName("avg_ms")] public double AvgMs { get; init; }
    [JsonPropertyName("max_ms")] public long MaxMs { get; init; }
    [JsonPropertyName("text_bytes")] public long TextBytes { get; init; }
    [JsonPropertyName("image_bytes")] public long ImageBytes { get; init; }
    [JsonPropertyName("snapshot_nodes")] public long SnapshotNodes { get; init; }
    [JsonPropertyName("ocr_calls")] public long OcrCalls { get; init; }
    [JsonPropertyName("by_tool")]
    public IReadOnlyDictionary<string, long> ByTool { get; init; } =
        new Dictionary<string, long>();
    [JsonPropertyName("by_code")]
    public IReadOnlyDictionary<string, long> ByCode { get; init; } =
        new Dictionary<string, long>();
}
