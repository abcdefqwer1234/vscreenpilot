using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
using VScreenPilot.Services;

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
    Action<IntPtr> SetSnapshotRoot,
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
    string text, int? pid, bool verify, int chunkChars, string channel, long? hwnd, bool strictVerify,
    int paceMs, string? target);

internal delegate TextWriteResult WriteTextVerifiedHandler(
    TrackedApp app, UiaElementHandle? handle, TextWriteRequest request);

internal delegate PressKeysView PressKeysHandler(
    string keys, bool keepForeground, int? pid, bool allowSystemKeys, bool observe, long? hwnd,
    string? target);

internal sealed record ElementInputContext(
    ClickHandler Click,
    TypeTextHandler TypeText,
    WriteTextVerifiedHandler WriteTextVerified,
    PressKeysHandler PressKeys,
    Action ResetUia);

internal sealed record ElementFocusContext(
    Func<TrackedApp, bool> EnsureForeground,
    Action PauseFocusGuard,
    Action ResumeFocusGuard,
    Action<IntPtr> RestoreForeground);

internal sealed partial class ElementOperations
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
            _snapshot.SetSnapshotRoot(hwnd);
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

    /// <summary>按名称选中标签页；优先 SelectionItemPattern，失败回退 Invoke。</summary>
    public TabActionView SelectTab(string name, string nameMatch, int index, int? pid)
    {
        (UiaElementInfo info, IntPtr hwnd) = ResolveTab(
            name, nameMatch, index, pid, "select_tab");

        using UiaElementHandle handle = _state.Uia.FindByRuntimeId(hwnd, info.RuntimeId)
            ?? throw StaleTab(info);

        handle.Focus();
        (bool ok, string? strategy) = handle.Invoke(UiaInvokePattern.Auto);

        if (!ok)
        {
            // 自绘标签页可能只有矩形没有 Selection/Invoke；按计划回退为中心点击。
            int centerX = info.Left + Math.Max(1, info.Width) / 2;
            int centerY = info.Top + Math.Max(1, info.Height) / 2;
            ClickView click = _input.Click(new SessionClickRequest(
                centerX, centerY, "left", 1, "auto",
                KeepForeground: false, Observe: true, FallbackInput: false));

            if (click.Verified || click.Method == "uia" || click.SuspectNoop == false)
            {
                return new TabActionView(
                    "select",
                    info.Name,
                    _state.RegisterElement(info, hwnd),
                    false,
                    "click:" + click.Method,
                    new BoundsView(info.Left, info.Top, info.Width, info.Height));
            }

            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "该标签页没有可用的 SelectionItem/Invoke 模式，中心点击也未生效",
                new JsonObject { ["name"] = info.Name, ["hwnd"] = hwnd.ToInt64() },
                retryable: false,
                hint: "改用 click_ref 或坐标点击标签页正文（不要点右缘关闭按钮）",
                stage: "uia");
        }

        return new TabActionView(
            "select",
            info.Name,
            _state.RegisterElement(info, hwnd),
            false,
            strategy ?? "invoke",
            new BoundsView(info.Left, info.Top, info.Width, info.Height));
    }

    /// <summary>按名称关闭标签页；必须显式允许破坏性操作。</summary>
    public TabActionView CloseTab(
        string name, string nameMatch, int index, int? pid, bool allowDestructive)
    {
        (UiaElementInfo info, IntPtr hwnd) = ResolveTab(
            name, nameMatch, index, pid, "close_tab");

        if (!allowDestructive)
        {
            throw new ToolException(
                "E_DESTRUCTIVE_TARGET",
                $"close_tab 会关闭标签页「{info.Name}」，需要显式允许",
                new JsonObject
                {
                    ["name"] = info.Name,
                    ["hwnd"] = hwnd.ToInt64(),
                    ["control_type"] = info.ControlTypeName,
                },
                retryable: false,
                hint: "确认要关闭时传 allow_destructive=true",
                stage: "security");
        }

        using UiaElementHandle handle = _state.Uia.FindByRuntimeId(hwnd, info.RuntimeId)
            ?? throw StaleTab(info);

        handle.Focus();
        (bool selected, string? selectStrategy) = handle.Invoke(UiaInvokePattern.Select);
        if (!selected)
        {
            (selected, selectStrategy) = handle.Invoke(UiaInvokePattern.Auto);
        }

        string method;
        UiaFindResult buttons = handle.FindDescendants(
            null, UiaNameMatch.Exact, "Button", null, 50);
        UiaElementInfo? closeButton = buttons.Items.FirstOrDefault(item =>
            Regex.IsMatch(item.Name.Trim(), @"^(close|关闭|✕|×)$", RegexOptions.IgnoreCase));

        if (closeButton is not null)
        {
            using UiaElementHandle? button = _state.Uia.FindByRuntimeId(hwnd, closeButton.RuntimeId);
            (bool ok, string? strategy) = button?.Invoke(UiaInvokePattern.Auto) ?? (false, null);

            if (!ok)
            {
                throw new ToolException(
                    "E_PATTERN_UNSUPPORTED",
                    "标签页关闭按钮没有可用的 UIA 模式",
                    new JsonObject { ["name"] = closeButton.Name, ["hwnd"] = hwnd.ToInt64() },
                    retryable: false,
                    hint: "改用 press_keys Ctrl+W",
                    stage: "uia");
            }

            method = "button:" + (strategy ?? "invoke");
        }
        else
        {
            if (!selected)
            {
                throw new ToolException(
                    "E_PATTERN_UNSUPPORTED",
                    "无法先选中目标标签页，已拒绝发送 Ctrl+W（可能关闭当前活动标签页）",
                    new JsonObject
                    {
                        ["name"] = info.Name,
                        ["hwnd"] = hwnd.ToInt64(),
                        ["select_strategy"] = selectStrategy,
                    },
                    retryable: false,
                    hint: "改用 click_ref/坐标点击目标标签页正文后再关闭，或手动关闭",
                    stage: "uia");
            }

            // 自绘标签页没有独立的关闭按钮：先选中目标标签页，再发送 Ctrl+W。
            // 不传 pid（只传 hwnd），避免 UWP 内容进程 pid 与 frame host hwnd 不一致。
            _input.PressKeys("Ctrl+W", false, null, false, false, hwnd, null);
            method = "ctrl+w";
        }

        return new TabActionView(
            "close",
            info.Name,
            _state.RegisterElement(info, hwnd),
            true,
            method,
            new BoundsView(info.Left, info.Top, info.Width, info.Height));
    }

    private ToolException StaleTab(UiaElementInfo info) => new(
        "E_ELEMENT_STALE",
        "标签页已失效，请重新 find_element(control_type=TabItem)",
        new JsonObject { ["name"] = info.Name },
        retryable: true,
        hint: "重新 snapshot 或 find_element 后再试",
        stage: "uia");

    private (UiaElementInfo Info, IntPtr Hwnd) ResolveTab(
        string name, string nameMatch, int index, int? pid, string operation)
    {
        DisplayInfo display = _apps.EnsureDisplay();
        TrackedApp app = _apps.FindApp(pid)
            ?? throw new ToolException(
                "E_NO_TARGET",
                pid is null
                    ? "没有已接管的应用窗口，请先 launch_or_place_app"
                    : $"pid={pid} 不是本会话中存活的应用",
                new JsonObject { ["pid"] = pid });

        UiaNameMatch mode = nameMatch.ToLowerInvariant() switch
        {
            "contains" => UiaNameMatch.Contains,
            "regex" => UiaNameMatch.Regex,
            _ => UiaNameMatch.Exact,
        };

        var matches = new List<(UiaElementInfo Info, IntPtr Hwnd)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (WindowInfo window in WindowManager.ListForProcess(app.Pid))
        {
            UiaFindResult found;

            try
            {
                found = _state.Uia.FindElementsDetailed(
                    window.Hwnd, name, mode, "TabItem", null, 50);
            }
            catch (COMException)
            {
                continue;
            }

            foreach (UiaElementInfo info in found.Items)
            {
                string key = string.IsNullOrEmpty(info.RuntimeId)
                    ? $"{info.ProcessId}|{info.Name}|{info.Left},{info.Top}"
                    : info.RuntimeId;

                if (seen.Add(key))
                {
                    matches.Add((info, window.Hwnd));
                }
            }
        }

        if (matches.Count == 0)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                $"未找到标签页「{name}」",
                new JsonObject { ["name"] = name, ["name_match"] = nameMatch },
                retryable: true,
                hint: "先用 find_element(control_type=TabItem) 确认标签页是否存在",
                stage: "uia");
        }

        if (index < 0 || index >= matches.Count)
        {
            if (matches.Count > 1)
            {
                var candidates = new JsonArray();

                foreach ((UiaElementInfo info, IntPtr hwnd) in matches.Take(20))
                {
                    candidates.Add((JsonNode)new JsonObject
                    {
                        ["name"] = info.Name,
                        ["hwnd"] = hwnd.ToInt64(),
                        ["bounds"] = new JsonObject
                        {
                            ["x"] = info.Left,
                            ["y"] = info.Top,
                            ["width"] = info.Width,
                            ["height"] = info.Height,
                        },
                    });
                }

                throw new ToolException(
                    "E_ELEMENT_AMBIGUOUS",
                    $"匹配到 {matches.Count} 个标签页，index={index} 超出范围",
                    new JsonObject { ["name"] = name, ["count"] = matches.Count, ["candidates"] = candidates },
                    retryable: true,
                    hint: "传 index 指定目标（从 0 开始）",
                    stage: "uia");
            }

            throw new ToolException(
                "E_INVALID_ARG",
                $"index={index} 超出候选范围（共 {matches.Count} 个）",
                new JsonObject { ["index"] = index, ["count"] = matches.Count });
        }

        (UiaElementInfo picked, IntPtr pickedHwnd) = matches[index];
        _apps.RequireOnDisplay(display, pickedHwnd, operation);
        return (picked, pickedHwnd);
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
            _snapshot.SetSnapshotRoot(hwnd);

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

        // 多读 1 个字符才能区分“刚好 max_chars”和“确实被截断”。
        int readLimit = maxChars < int.MaxValue ? maxChars + 1 : maxChars;
        string? text = handle.GetText(readLimit);

        if (text is null)
        {
            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "该控件不支持 TextPattern/ValuePattern，无法读取文本",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(handle.Info) });
        }

        bool truncated = text.Length > maxChars;

        if (truncated)
        {
            text = text[..maxChars];
        }

        return new ElementTextView(
            entry.Ref,
            handle.Info.ControlTypeName,
            handle.Info.AutomationId,
            text,
            text.Length,
            truncated,
            refreshed);
    }

    public InvokeElementView InvokeElement(
        string reference, bool allowClick, string? pattern = null, string? selectText = null,
        bool allowDestructive = false) =>
        InvokeOps.InvokeElement(reference, allowClick, pattern, selectText, allowDestructive);

    /// <summary>把容器 ref 解析为 UIA 句柄，供 find_element{within} 做子树查询。</summary>
    public (UiaElementHandle Handle, IntPtr Hwnd) ResolveContainer(string reference, DisplayInfo display)
    {
        UiaElementHandle handle = _state.Resolver.Resolve(reference, display, out ElementRefEntry entry, out _);
        return (handle, entry.Hwnd);
    }

    public SetElementValueView SetElementValue(
        string reference, string value, bool allowKeyboard, string? mode = null,
        bool allowDestructive = false, bool strictVerify = false)
    {
        SetElementValueView view = ValueOps.SetElementValue(
            reference, value, allowKeyboard, mode, allowDestructive);

        if (strictVerify && !view.Verified)
        {
            throw new ToolException(
                "E_VERIFY_FAILED",
                "set_element_value 写入后回读与期望不一致",
                new JsonObject
                {
                    ["ref"] = view.Ref,
                    ["method"] = view.Method,
                    ["mode"] = view.Mode,
                    ["expected"] = view.Written,
                    ["observed"] = view.Value?.DeepClone(),
                    ["observed_length"] = view.ObservedLength,
                },
                retryable: false,
                hint: "确认控件可写、焦点正确且未被弹窗拦截；必要时改用 allow_keyboard + 键盘路径",
                stage: "verify");
        }

        return view;
    }
}
