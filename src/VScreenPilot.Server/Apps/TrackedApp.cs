using VScreenPilot.Interop;

namespace VScreenPilot.Apps;

/// <summary>
/// 会话跟踪的一个应用窗口。UWP 会同时记录 frame host（Pid/Hwnd）与内容进程（ContentPid）；
/// 关闭/强杀必须用内容进程，不能杀 frame host。
/// </summary>
internal sealed class TrackedApp
{
    public int Pid { get; set; }

    /// <summary>哪个 MCP 客户端拥有该窗口；单客户端旧行为默认为 c0。</summary>
    public string OwnerClientId { get; set; } = "c0";

    public string ExePath { get; init; } = string.Empty;

    /// <summary>UWP 内容进程；普通应用为 0。</summary>
    public int ContentPid { get; set; }

    public string ContentExePath { get; set; } = string.Empty;
    public IntPtr Hwnd { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool StartedProcess { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>本会话真正拥有该窗口/进程时才允许在释放时关闭；接管用户窗口必须为 false。</summary>
    public bool Owned { get; set; }

    public bool CloseWindowOnRelease { get; set; }
    public bool DedicatedProcess { get; set; }
    public string? TempProfile { get; init; }
    public NativeMethods.WINDOWPLACEMENT Placement { get; set; }
    public bool PlacementSaved { get; set; }

    /// <summary>首次观察到窗口失效的时间；超过 3s 才从 apps[] 移除，避免瞬时刷新被误删。</summary>
    public DateTime? DeadSince { get; set; }
}
