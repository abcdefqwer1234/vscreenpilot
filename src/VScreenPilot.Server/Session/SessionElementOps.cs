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
using VScreenPilot.Services;

namespace VScreenPilot.Session;

/// <summary>ElementOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionElementOps
{
    internal readonly SessionManager _host;

    public SessionElementOps(SessionManager host) => _host = host;

    // 容量属于对外可观察字段（snapshot 的 ref_capacity），保留原常量名。

    /// <summary>最近一次 snapshot 的 id；ref 只在该快照内有效。</summary>
    public string SnapshotId => _host.Refs.CurrentSnapshotId(_host.Clients.CurrentId);

    /// <summary>当前快照 id；没有则创建第一个。</summary>
    public string EnsureSnapshot(IntPtr rootHwnd = default) =>
        _host.Refs.EnsureSnapshot(_host.Clients.CurrentId, rootHwnd);

    /// <summary>当前快照元数据（代际/创建时间/根 hwnd）；没有快照时为 null。</summary>
    public SnapshotMeta? SnapshotMeta => _host.Refs.GetSnapshotMeta(_host.Clients.CurrentId);

    /// <summary>ref 是否仍属于当前客户端且未过期；供 run_actions dry_run 预检。</summary>
    public bool OwnsRef(string reference) =>
        _host.Refs.TryResolve(_host.Clients.CurrentId, reference, out _) is not null;

    internal void ClearSnapshotState(string? clientId) => _host.Refs.ClearSnapshots(clientId);

    /// <summary>开始新快照：推进代际并清掉更早一代的 ref。</summary>
    public string BeginSnapshot(IntPtr rootHwnd = default) =>
        _host.Refs.BeginSnapshot(_host.Clients.CurrentId, rootHwnd);

    /// <summary>回填快照根窗口；不推进代际。</summary>
    public void SetSnapshotRoot(IntPtr rootHwnd) =>
        _host.Refs.SetSnapshotRoot(_host.Clients.CurrentId, rootHwnd);

    /// <summary>登记元素并返回稳定的会话内引用（只对外暴露 ref）。</summary>
    public string RegisterElement(UiaElementInfo info, IntPtr hwnd) =>
        _host.Refs.Register(_host.Clients.CurrentId, info, hwnd);

    /* element_ref 解析与元素操作已拆到 ElementResolver / ElementOperations */

    public UiTreeView GetUiTree(
        UiTreeOptions options, string? format = null, bool newSnapshot = false) =>
        _host._elements.GetUiTree(options, format, newSnapshot);

    public TableView ReadTable(TableQuery query) => _host._elements.ReadTable(query);

    public InvokeElementView InvokeElement(
        string reference, bool allowClick, string? pattern = null, string? selectText = null,
        bool allowDestructive = false) =>
        _host._elements.InvokeElement(reference, allowClick, pattern, selectText, allowDestructive);

    public (UiaElementHandle Handle, IntPtr Hwnd) ResolveContainer(string reference, DisplayInfo display) =>
        _host._elements.ResolveContainer(reference, display);

    public SetElementValueView SetElementValue(
        string reference, string value, bool allowKeyboard, string? mode = null,
        bool allowDestructive = false, bool strictVerify = false) =>
        _host._elements.SetElementValue(
            reference, value, allowKeyboard, mode, allowDestructive, strictVerify);

    public ElementTextView GetElementText(string reference, int maxChars) =>
        _host._elements.GetElementText(reference, maxChars);

    public TabActionView SelectTab(string name, string nameMatch, int index, int? pid) =>
        _host._elements.SelectTab(name, nameMatch, index, pid);

    public TabActionView CloseTab(
        string name, string nameMatch, int index, int? pid, bool allowDestructive) =>
        _host._elements.CloseTab(name, nameMatch, index, pid, allowDestructive);
}
