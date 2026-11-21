using VScreenPilot.Uia;

namespace VScreenPilot.Ui;

/// <summary>
/// 一个对外 ref 的内部记录。只保存数据与最近一次刷新，不含会话状态。
/// 归属校验按 HWND 的进程（UWP 的内容进程与 frame host 不同），由调用方使用 <see cref="Pid"/>。
/// </summary>
internal sealed class ElementRefEntry
{
    public required string Ref { get; init; }
    public required string Key { get; init; }
    public string OwnerClientId { get; init; } = "c0";

    /// <summary>创建该 ref 时的快照代际；跨代使用返回 E_STALE_SNAPSHOT。</summary>
    public long Generation { get; set; }
    public string RuntimeId { get; set; } = string.Empty;
    public int Pid { get; init; }
    public IntPtr Hwnd { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AutomationId { get; set; } = string.Empty;
    public string ControlTypeName { get; set; } = string.Empty;
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public long LastUsedTicks { get; set; }

    public void Update(UiaElementInfo info, IntPtr hwnd)
    {
        RuntimeId = info.RuntimeId;
        Hwnd = hwnd;
        Name = info.Name;
        AutomationId = info.AutomationId;
        ControlTypeName = info.ControlTypeName;
        Left = info.Left;
        Top = info.Top;
        Right = info.Right;
        Bottom = info.Bottom;
        LastUsedTicks = Environment.TickCount64;
    }

    /// <summary>
    /// ref 的稳定键：优先 runtime id；退化路径带上控件类型与位置。
    /// 键里包含 clientId，保证不同客户端的同名元素互不覆盖。
    /// </summary>
    public static string BuildKey(string clientId, UiaElementInfo info, IntPtr hwnd) =>
        clientId + "\u0000" + (!string.IsNullOrEmpty(info.RuntimeId)
            ? $"{info.ProcessId}:{hwnd.ToInt64():X}:{info.RuntimeId}"
            : $"{info.ProcessId}:{hwnd.ToInt64():X}:{info.ControlType}:{info.AutomationId}:{info.Name}:{info.Left},{info.Top}");
}
