using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Input;
using VScreenPilot.Interop;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    private readonly ElementRefStore _refs = new();

    // 容量属于对外可观察字段（snapshot 的 ref_capacity），保留原常量名。
    private const int ElementRefCapacity = ElementRefStore.Capacity;

    /// <summary>最近一次 snapshot 的 id；ref 只在该快照内有效。</summary>
    public string SnapshotId => _refs.CurrentSnapshotId(Clients.CurrentId);

    /// <summary>当前快照 id；没有则创建第一个。</summary>
    public string EnsureSnapshot() => _refs.EnsureSnapshot(Clients.CurrentId);

    private void ClearSnapshotState(string? clientId) => _refs.ClearSnapshots(clientId);

    /// <summary>开始新快照：推进代际并清掉更早一代的 ref。</summary>
    public string BeginSnapshot() => _refs.BeginSnapshot(Clients.CurrentId);

    /// <summary>登记元素并返回稳定的会话内引用（只对外暴露 ref）。</summary>
    public string RegisterElement(UiaElementInfo info, IntPtr hwnd) =>
        _refs.Register(Clients.CurrentId, info, hwnd);

    /* element_ref 解析与元素操作已拆到 ElementResolver / ElementOperations */

    public UiTreeView GetUiTree(
        int? maxDepth, bool? interactiveOnly, int? maxNodes, string? root, int? pid,
        string? format = null, bool newSnapshot = false) =>
        _elements.GetUiTree(maxDepth, interactiveOnly, maxNodes, root, pid, format, newSnapshot);

    public InvokeElementView InvokeElement(string reference, bool allowClick, string? pattern = null) =>
        _elements.InvokeElement(reference, allowClick, pattern);

    public SetElementValueView SetElementValue(
        string reference, string value, bool allowKeyboard, string? mode = null,
        bool allowDestructive = false) =>
        _elements.SetElementValue(reference, value, allowKeyboard, mode, allowDestructive);

    public ElementTextView GetElementText(string reference, int maxChars) =>
        _elements.GetElementText(reference, maxChars);
}
