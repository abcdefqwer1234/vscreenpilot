using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Input;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;
using VScreenPilot.Json;

namespace VScreenPilot.Session;

/// <summary>get_ui_tree 的入参。</summary>
internal sealed record UiTreeOptions(
    int? MaxDepth, bool? InteractiveOnly, int? MaxNodes, string? Root, int? Pid);

internal delegate JsonObject ConvertTreeNodeHandler(UiaTreeNode node, IntPtr hwnd, ref string rootRef);

internal sealed record ElementStateContext(
    UiaHost Uia,
    ElementResolver Resolver,
    ElementRefStore Refs,
    Func<UiaElementInfo, IntPtr, string> RegisterElement,
    int RefCapacity,
    LogRing Log,
    ConvertTreeNodeHandler ConvertTreeNode);

internal sealed record ElementSnapshotContext(
    Func<string> EnsureSnapshot,
    Func<string> BeginSnapshot,
    Func<bool> IsCancelled,
    Func<Func<bool>> CancellationProbe);

internal sealed record ElementAppContext(
    Func<DisplayInfo> EnsureDisplay,
    Func<int?, TrackedApp?> FindApp,
    Func<int, TrackedApp?> OwnedAppForPid,
    Action<DisplayInfo, IntPtr, string> RequireOnDisplay,
    Action<DisplayInfo, int, int, string> RequirePointInTrackedApp);

internal delegate ClickView ClickHandler(SessionClickRequest request);

internal delegate TypeTextView TypeTextHandler(
    string text, int? pid, bool verify, int chunkChars, string channel);

internal delegate TextWriteResult WriteTextVerifiedHandler(
    TrackedApp app, UiaElementHandle? handle, TextWriteRequest request);

internal sealed record ElementInputContext(
    ClickHandler Click,
    TypeTextHandler TypeText,
    WriteTextVerifiedHandler WriteTextVerified,
    Action ResetUia);

internal sealed record ElementFocusContext(
    Func<TrackedApp, bool> EnsureForeground,
    Action PauseFocusGuard,
    Action ResumeFocusGuard,
    Action<IntPtr> RestoreForeground);

internal sealed class ElementOperations
{
    internal readonly ElementStateContext _state;
    internal readonly ElementSnapshotContext _snapshot;
    internal readonly ElementAppContext _apps;
    internal readonly ElementInputContext _input;
    internal readonly ElementFocusContext _focus;

    internal readonly ElementInvokeOps InvokeOps;
    internal readonly ElementValueOps ValueOps;
    internal readonly ElementKeyboardValueOps KeyboardValueOps;

    public ElementOperations(
        ElementStateContext state,
        ElementSnapshotContext snapshot,
        ElementAppContext apps,
        ElementInputContext input,
        ElementFocusContext focus)
    {
        InvokeOps = new ElementInvokeOps(this);
        ValueOps = new ElementValueOps(this);
        KeyboardValueOps = new ElementKeyboardValueOps(this);
        _state = state;
        _snapshot = snapshot;
        _apps = apps;
        _input = input;
        _focus = focus;
    }

    public UiTreeView GetUiTree(
        UiTreeOptions options, string? format = null, bool newSnapshot = false)
    {
        int depth = Math.Clamp(options.MaxDepth ?? 3, 1, 10);
        int nodes = Math.Clamp(options.MaxNodes ?? 300, 1, Math.Min(1024, _state.RefCapacity));
        bool interactive = options.InteractiveOnly ?? true;
        string? root = options.Root;
        int? pid = options.Pid;
        int effectiveNodes = nodes;

        (IntPtr hwnd, string rootRuntimeId, UiTreeView? early) = ResolveRoot(root, pid, nodes, format);

        if (early is not null)
        {
            return early;
        }

        UiaTreeResult result = _state.Uia.BuildTree(
            new UiTreeRequest(hwnd, rootRuntimeId, depth, interactive, nodes, TimeBudgetMs: 3000),
            _snapshot.CancellationProbe());

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

        lock (_state.Refs.SyncRoot)
        {
            snapshotId = newSnapshot ? _snapshot.BeginSnapshot() : _snapshot.EnsureSnapshot();
            tree = _state.ConvertTreeNode(result.Root, hwnd, ref rootRef);
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
            _state.RefCapacity,
            effectiveNodes,
            snapshotId,
            newSnapshot,
            Backend: null,
            nodes != (options.MaxNodes ?? 300) ? true : null,
            result.Reason,
            string.Equals(format, "text", StringComparison.OrdinalIgnoreCase)
                ? SnapshotTextFormatter.Render(tree, includeValues: true)
                : null,
            window is null
                ? null
                : new TreeWindowView(window.Hwnd.ToInt64(), window.Pid, window.Title));
    }

    /// <summary>解析 get_ui_tree 的树根：MSAA ref、UIA ref 或当前应用窗口。</summary>
    internal (IntPtr Hwnd, string RootRuntimeId, UiTreeView? Early) ResolveRoot(
        string? root, int? pid, int nodes, string? format)
    {
        IntPtr hwnd;
        string rootRuntimeId;

        if (!string.IsNullOrWhiteSpace(root))
        {
            // MSAA ref 不是 UIA runtime_id，不能走 ResolveElementHandle；单独展开成扁平树。
            ElementRefEntry msaaEntry = _state.Resolver.Require(root);

            if (MsaaService.TryParse(msaaEntry.RuntimeId, out IntPtr msaaHwnd, out int msaaOrdinal))
            {
                return (IntPtr.Zero, string.Empty, BuildMsaaTree(msaaEntry, msaaHwnd, msaaOrdinal, nodes, format));
            }

            // 走完整的 ref 校验：pid 仍在会话、窗口在虚拟屏、runtime_id 失效时按指纹重定位。
            DisplayInfo display = _apps.EnsureDisplay();
            using (UiaElementHandle handle = _state.Resolver.Resolve(
                       root, display, out ElementRefEntry entry, out bool refreshed))
            {
                hwnd = entry.Hwnd;
                rootRuntimeId = handle.Info.RuntimeId;

                if (refreshed)
                {
                    _state.Log.Info($"get_ui_tree 树根 {entry.Ref} 已按指纹重定位");
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
            TrackedApp? app = _apps.FindApp(pid);

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
            DisplayInfo display = _apps.EnsureDisplay();
            _apps.RequireOnDisplay(display, hwnd, "get_ui_tree");
            rootRuntimeId = string.Empty;
        }


        return (hwnd, rootRuntimeId, null);
    }


    /// <summary>
    /// MSAA 对话框没有 UIA 树；把控件展开成一层扁平树，ref 可直接 invoke/click/get_text。
    /// </summary>
    internal UiTreeView BuildMsaaTree(
        ElementRefEntry rootEntry, IntPtr hwnd, int ordinal, int maxNodes, string? format)
    {
        MsaaFindResult found = _state.Uia.InvokeMsaa(() => MsaaService.FindElements(
            hwnd, null, UiaNameMatch.Exact, null, maxNodes));

        var children = new JsonArray();
        string snapshotId;
        int count = 0;

        lock (_state.Refs.SyncRoot)
        {
            snapshotId = _snapshot.EnsureSnapshot();

            foreach (UiaElementInfo info in found.Items)
            {
                if (info.RuntimeId == rootEntry.RuntimeId)
                {
                    continue;
                }

                children.Add((JsonNode)new JsonObject
                {
                    ["ref"] = _state.RegisterElement(info, hwnd),
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
            _state.RefCapacity,
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
        DisplayInfo display = _apps.EnsureDisplay();
        ElementRefEntry probe = _state.Resolver.Require(reference);
        maxChars = Math.Clamp(maxChars, 1, 65536);

        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaChildId))
        {
            string msaaText = _state.Uia.InvokeMsaa(() => MsaaService.GetValue(msaaHwnd, msaaChildId)) ?? string.Empty;
            return new ElementTextView(
                probe.Ref,
                probe.ControlTypeName,
                probe.AutomationId,
                msaaText.Length > maxChars ? msaaText[..maxChars] : msaaText,
                msaaText.Length,
                msaaText.Length > maxChars,
                Refreshed: false);
        }

        using UiaElementHandle handle = _state.Resolver.Resolve(
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

    public InvokeElementView InvokeElement(string reference, bool allowClick, string? pattern = null) =>
        InvokeOps.InvokeElement(reference, allowClick, pattern);

    public SetElementValueView SetElementValue(
        string reference, string value, bool allowKeyboard, string? mode = null, bool allowDestructive = false) =>
        ValueOps.SetElementValue(reference, value, allowKeyboard, mode, allowDestructive);
}
