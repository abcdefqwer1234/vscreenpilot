using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Diagnostics;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Input;
using VScreenPilot.Mcp;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;
using VScreenPilot.Json;

namespace VScreenPilot.Session;

internal interface IElementOperationsHost
{
    DisplayInfo EnsureDisplay();
    UiaHost Uia { get; }
    ElementResolver Resolver { get; }
    ElementRefStore Refs { get; }
    string RegisterElement(UiaElementInfo info, IntPtr hwnd);
    string EnsureSnapshot();
    string BeginSnapshot();
    int RefCapacity { get; }
    bool IsCancelled { get; }
    LogRing Log { get; }
    TrackedApp? FindApp(int? pid);
    TrackedApp? OwnedAppForPid(int pid);
    void RequireOnDisplay(DisplayInfo display, IntPtr hwnd, string label);
    void RequirePointInTrackedApp(DisplayInfo display, int x, int y, string label);
    ClickView Click(
        int x, int y, string button, int clicks, string mode,
        bool keepForeground = false, bool observe = true, bool fallbackInput = false);
    TypeTextView TypeText(string text, int? pid, bool verify, int chunkChars, string channel);
    TextWriteResult WriteTextVerified(
        TrackedApp app, UiaElementHandle handle, string value, bool replace, int timeoutMs);
    bool EnsureForeground(TrackedApp app);
    void PauseFocusGuard();
    void ResumeFocusGuard();
    void RestoreForeground(IntPtr previous);
    void ResetUia();
    JsonObject ConvertTreeNode(UiaTreeNode node, IntPtr hwnd, ref string rootRef);
    Func<bool> CancellationProbe();
}

internal sealed partial class ElementOperations
{
    private readonly IElementOperationsHost _host;

    public ElementOperations(IElementOperationsHost host) => _host = host;

    public UiTreeView GetUiTree(
        int? maxDepth, bool? interactiveOnly, int? maxNodes, string? root, int? pid,
        string? format = null, bool newSnapshot = false)
    {
        int depth = Math.Clamp(maxDepth ?? 3, 1, 10);
        // 树节点上限不能超过 ref 容量，否则返回的早期 ref 已被淘汰（E5）。
        int nodes = Math.Clamp(maxNodes ?? 300, 1, Math.Min(1024, _host.RefCapacity));
        bool interactive = interactiveOnly ?? true;
        int effectiveNodes = nodes;

        IntPtr hwnd;
        string rootRuntimeId;

        if (!string.IsNullOrWhiteSpace(root))
        {
            // MSAA ref 不是 UIA runtime_id，不能走 ResolveElementHandle；单独展开成扁平树。
            ElementRefEntry msaaEntry = _host.Resolver.Require(root);

            if (MsaaService.TryParse(msaaEntry.RuntimeId, out IntPtr msaaHwnd, out int msaaOrdinal))
            {
                return BuildMsaaTree(msaaEntry, msaaHwnd, msaaOrdinal, nodes, format);
            }

            // 走完整的 ref 校验：pid 仍在会话、窗口在虚拟屏、runtime_id 失效时按指纹重定位。
            DisplayInfo display = _host.EnsureDisplay();
            using (UiaElementHandle handle = _host.Resolver.Resolve(
                       root, display, out ElementRefEntry entry, out bool refreshed))
            {
                hwnd = entry.Hwnd;
                rootRuntimeId = handle.Info.RuntimeId;

                if (refreshed)
                {
                    _host.Log.Info($"get_ui_tree 树根 {entry.Ref} 已按指纹重定位");
                }
            }

            if (string.IsNullOrEmpty(rootRuntimeId))
            {
                throw new ToolException(
                    "E_ELEMENT_STALE",
                    "该 element_ref 缺少 runtime_id，无法作为树根，请重新 find_element",
                    new JsonObject { ["ref"] = root });
            }
        }
        else
        {
            TrackedApp? app = _host.FindApp(pid);

            if (app is null)
            {
                throw new ToolException(
                    "E_NO_TARGET",
                    pid is null
                        ? "没有已接管的应用窗口，请先 launch_or_place_app"
                        : $"pid={pid} 不是本会话中存活的应用",
                    new JsonObject { ["pid"] = pid });
            }

            hwnd = WindowManager.GetActivePopup(app.Hwnd);
            DisplayInfo display = _host.EnsureDisplay();
            _host.RequireOnDisplay(display, hwnd, "get_ui_tree");
            rootRuntimeId = string.Empty;
        }

        // 建树不持 _refGate：UIA 最坏 3s，不能把 ref 解析/其它观察一起堵住。
        // 只有“推进代际 + 登记 ref + 生成 JSON”这段纯内存操作才进锁。
        UiaTreeResult result = _host.Uia.BuildTree(
            hwnd,
            rootRuntimeId,
            depth,
            interactive,
            nodes,
            timeBudgetMs: 3000,
            isCancelled: _host.CancellationProbe());

        if (result.Root is null)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                result.Reason == "root_not_found"
                    ? "树根控件已不存在"
                    : "无法读取语义树",
                new JsonObject { ["reason"] = result.Reason });
        }

        string rootRef = string.Empty;
        JsonObject tree;
        string snapshotId;

        lock (_host.Refs.SyncRoot)
        {
            // 只有显式 new_snapshot 才推进代际；默认复用当前快照，
            // 这样 find -> get_ui_tree -> invoke 不会莫名 E_STALE_SNAPSHOT。
            snapshotId = newSnapshot ? _host.BeginSnapshot() : _host.EnsureSnapshot();
            tree = _host.ConvertTreeNode(result.Root, hwnd, ref rootRef);
        }

        WindowInfo? window = WindowManager.FromHandle(hwnd);

        return new UiTreeView(
            rootRef,
            tree,
            result.Nodes,
            result.Visited,
            result.Pruned,
            result.Truncated,
            interactive,
            depth,
            result.MaxDepthReached,
            result.ElapsedMs,
            _host.RefCapacity,
            effectiveNodes,
            snapshotId,
            newSnapshot,
            Backend: null,
            nodes != (maxNodes ?? 300) ? true : null,
            result.Reason,
            string.Equals(format, "text", StringComparison.OrdinalIgnoreCase)
                ? SnapshotTextFormatter.Render(tree, includeValues: true)
                : null,
            window is null
                ? null
                : new TreeWindowView(window.Hwnd.ToInt64(), window.Pid, window.Title));
    }

    /// <summary>
    /// MSAA 对话框没有 UIA 树；把控件展开成一层扁平树，ref 可直接 invoke/click/get_text。
    /// </summary>
    private UiTreeView BuildMsaaTree(
        ElementRefEntry rootEntry, IntPtr hwnd, int ordinal, int maxNodes, string? format)
    {
        MsaaFindResult found = _host.Uia.InvokeMsaa(() => MsaaService.FindElements(
            hwnd, null, UiaNameMatch.Exact, null, maxNodes));

        var children = new JsonArray();
        string snapshotId;
        int count = 0;

        lock (_host.Refs.SyncRoot)
        {
            snapshotId = _host.EnsureSnapshot();

            foreach (UiaElementInfo info in found.Items)
            {
                if (info.RuntimeId == rootEntry.RuntimeId)
                {
                    continue;
                }

                children.Add((JsonNode)new JsonObject
                {
                    ["ref"] = _host.RegisterElement(info, hwnd),
                    ["control_type"] = info.ControlTypeName,
                    ["name"] = info.Name,
                    ["bounds"] = new JsonObject
                    {
                        ["x"] = info.Left,
                        ["y"] = info.Top,
                        ["width"] = info.Width,
                        ["height"] = info.Height,
                    },
                });
                count++;
            }
        }

        var tree = new JsonObject
        {
            ["ref"] = rootEntry.Ref,
            ["control_type"] = rootEntry.ControlTypeName,
            ["name"] = rootEntry.Name,
            ["children"] = children,
        };

        return new UiTreeView(
            rootEntry.Ref,
            tree,
            count + 1,
            Visited: null,
            Pruned: null,
            found.Truncated,
            InteractiveOnly: true,
            MaxDepth: 1,
            MaxDepthReached: null,
            ElapsedMs: null,
            _host.RefCapacity,
            maxNodes,
            snapshotId,
            NewSnapshot: false,
            Backend: "msaa",
            MaxNodesClamped: null,
            found.Truncated ? "msaa_limit" : null,
            string.Equals(format, "text", StringComparison.OrdinalIgnoreCase)
                ? SnapshotTextFormatter.Render(tree, includeValues: true)
                : null,
            Window: null);
    }

    /* ------------------------------------------------------------------ */
    /* 工具：invoke_element / set_element_value                            */
    /* ------------------------------------------------------------------ */

    public ElementTextView GetElementText(string reference, int maxChars)
    {
        DisplayInfo display = _host.EnsureDisplay();
        ElementRefEntry probe = _host.Resolver.Require(reference);
        maxChars = Math.Clamp(maxChars, 1, 65536);

        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaChildId))
        {
            string msaaText = _host.Uia.InvokeMsaa(() => MsaaService.GetValue(msaaHwnd, msaaChildId)) ?? string.Empty;
            return new ElementTextView(
                probe.Ref,
                probe.ControlTypeName,
                probe.AutomationId,
                msaaText.Length > maxChars ? msaaText[..maxChars] : msaaText,
                msaaText.Length,
                msaaText.Length > maxChars,
                Refreshed: false);
        }

        using UiaElementHandle handle = _host.Resolver.Resolve(
            reference, display, out ElementRefEntry entry, out bool refreshed);

        string? text = handle.GetText(maxChars);

        if (text is null)
        {
            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "该控件不支持 TextPattern/ValuePattern，无法读取文本",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(handle.Info) });
        }

        return new ElementTextView(
            entry.Ref,
            handle.Info.ControlTypeName,
            handle.Info.AutomationId,
            text,
            text.Length,
            text.Length >= maxChars,
            refreshed);
    }

}
