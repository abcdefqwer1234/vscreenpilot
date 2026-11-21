using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Diagnostics;
using VScreenPilot.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Input;
using VScreenPilot.Mcp;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using static VScreenPilot.Mcp.JsonArgReader;

namespace VScreenPilot.Session;

/// <summary>元素操作所需的最小宿主接口。</summary>
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

/// <summary>invoke_element / set_element_value / get_element_text / get_ui_tree。</summary>
internal sealed class ElementOperations
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

    public InvokeElementView InvokeElement(string reference, bool allowClick, string? pattern = null)
    {
        DisplayInfo display = _host.EnsureDisplay();
        ElementRefEntry probe = _host.Resolver.Require(reference);

        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaChildId))
        {
            bool invoked = _host.Uia.InvokeMsaa(() => MsaaService.DoDefaultAction(msaaHwnd, msaaChildId));

            if (!invoked && allowClick)
            {
                var bounds = _host.Uia.InvokeMsaa(() => MsaaService.GetBounds(msaaHwnd, msaaChildId));

                if (bounds is not null)
                {
                    invoked = MessageInput.Click(
                        bounds.Value.Left + bounds.Value.Width / 2,
                        bounds.Value.Top + bounds.Value.Height / 2,
                        "left", 1, out _);
                }
            }

            if (!invoked)
            {
                throw new ToolException(
                    "E_PATTERN_UNSUPPORTED",
                    "MSAA 控件不支持默认动作；可改用 click 或 allow_click=true",
                    new JsonObject { ["ref"] = probe.Ref });
            }

            return new InvokeElementView(
                probe.Ref, "msaa", Invoked: true, Verified: false, ControlType: null,
                Refreshed: false, Detail: null, Element: null);
        }

        UiaElementHandle handle = _host.Resolver.Resolve(reference, display, out ElementRefEntry entry, out bool refreshed);
        bool handleTransferred = false;

        try
        {
        UiaElementInfo info = handle.Info;

        UiaInvokePattern mode = ParseInvokePattern(pattern);

        if (!info.Enabled)
        {
            throw new ToolException(
                "E_ELEMENT_DISABLED",
                "控件已禁用，拒绝触发",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(info) });
        }

        if (mode == UiaInvokePattern.Default)
        {
            return InvokeDefaultAction(display, handle, entry, refreshed);
        }

        // Invoke 是同步 COM 调用：按钮弹出模态对话框时会一直等到对话框关闭。
        // 在独立任务里执行，超时后放弃旧 UIA 实例，避免整个会话永久卡死。
        Task<(bool Ok, string? Strategy)> invokeTask = Task.Run(() =>
        {
            try
            {
                return handle.Invoke(mode);
            }
            finally
            {
                handle.Dispose();
            }
        });

        handleTransferred = true;

        if (!invokeTask.Wait(5000))
        {
            _host.ResetUia();
            throw new ToolException(
                "E_TIMEOUT",
                "UIA 触发 5s 未返回，常见于会打开模态对话框的按钮",
                new JsonObject { ["ref"] = entry.Ref },
                retryable: true,
                hint: "先用 get_ui_tree 或截图确认是否已弹出对话框；也可改用 click 的消息点击",
                stage: "invoke");
        }

        (bool ok, string? strategy) = invokeTask.Result;

        if (ok)
        {
            Thread.Sleep(50);
            return new InvokeElementView(
                entry.Ref, strategy ?? "uia", null, null, null, refreshed, null,
                ElementPresenter.ToView(info, compact: true));
        }

        if (!allowClick)
        {
            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "该控件没有可用的 UIA 触发模式（Invoke/Toggle/SelectionItem/ExpandCollapse/Legacy）；" +
                "可先用 pattern 指定期望模式；如需按坐标触发，请改用 click 或显式传 allow_click=true",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(info) });
        }

        int centerX = info.Left + info.Width / 2;
        int centerY = info.Top + info.Height / 2;

        if (info.Width <= 0 || info.Height <= 0)
        {
            throw new ToolException(
                "E_ELEMENT_OFFSCREEN",
                "控件没有可见尺寸，无法按坐标触发",
                new JsonObject { ["ref"] = entry.Ref });
        }

        _host.RequirePointInTrackedApp(display, centerX, centerY, "invoke_element");

        if (!MessageInput.Click(centerX, centerY, "left", 1, out string? detail))
        {
            throw new ToolException(
                "E_INPUT_UNSUPPORTED",
                "控件不支持语义触发，按坐标点击也失败",
                new JsonObject { ["ref"] = entry.Ref, ["detail"] = detail });
        }

        Thread.Sleep(50);
        return new InvokeElementView(
            entry.Ref, "message_click", null, null, null, refreshed, detail,
            ElementPresenter.ToView(info, compact: true));
        }
        finally
        {
            if (!handleTransferred)
            {
                handle.Dispose();
            }
        }
    }

    private static UiaInvokePattern ParseInvokePattern(string? pattern) =>
        (pattern ?? "auto").Trim().ToLowerInvariant() switch
        {
            "" or "auto" => UiaInvokePattern.Auto,
            "invoke" => UiaInvokePattern.Invoke,
            "toggle" => UiaInvokePattern.Toggle,
            "select" or "selectionitem" => UiaInvokePattern.Select,
            "expand" or "expandcollapse" => UiaInvokePattern.Expand,
            "legacy" or "legacyiaccessible" => UiaInvokePattern.Legacy,
            "default" or "open" or "defaultaction" => UiaInvokePattern.Default,
            _ => throw new ToolException(
                "E_INVALID_ARG",
                $"未知 pattern: {pattern}（可选 auto/invoke/toggle/select/expand/legacy/default）"),
        };

    /// <summary>
    /// 显式“打开/默认动作”：聚焦控件后发送 Space+Enter（树节点）或 Enter。
    /// 用于 WinForms 树节点等没有 InvokePattern、只能靠键盘默认动作打开的场景。
    /// </summary>
    private InvokeElementView InvokeDefaultAction(
        DisplayInfo display,
        UiaElementHandle handle,
        ElementRefEntry entry,
        bool refreshed)
    {
        TrackedApp? app;

        app = _host.OwnedAppForPid(entry.Pid);

        if (app is null)
        {
            throw new ToolException(
                "E_NO_TARGET",
                "element_ref 所属应用不在本会话中，无法执行默认动作",
                new JsonObject { ["ref"] = entry.Ref });
        }

        _host.RequireOnDisplay(display, app.Hwnd, "invoke_element");

        IntPtr previous = WindowManager.GetForegroundWindow();
        _host.PauseFocusGuard();

        try
        {
            if (!_host.EnsureForeground(app))
            {
                throw new ToolException(
                    "E_FOCUS",
                    "无法把目标窗口切换到前台，已取消默认动作",
                    new JsonObject { ["ref"] = entry.Ref },
                    retryable: true,
                    hint: "如果当前有模态框，先处理弹窗再重试",
                    stage: "focus");
            }

            try
            {
                handle.Focus();
            }
            catch (Exception exception) when (
                exception is COMException or InvalidOperationException){
                // 部分 WinForms/WPF 控件不支持 SetFocus；继续用 Enter 尝试。
                _ = exception;
            }

            Thread.Sleep(80);

            bool treeItem = string.Equals(
                handle.Info.ControlTypeName, "TreeItem", StringComparison.OrdinalIgnoreCase);

            if (treeItem)
            {
                InputInjector.PressKeys("Space");
                Thread.Sleep(80);
            }

            InputInjector.PressKeys("Enter");
            Thread.Sleep(250);

            return new InvokeElementView(
                entry.Ref, "default_action", null, null, handle.Info.ControlTypeName, refreshed, null,
                ElementPresenter.ToView(handle.Info, compact: false));
        }
        finally
        {
            _host.RestoreForeground(previous);
            _host.ResumeFocusGuard();
        }
    }

    public SetElementValueView SetElementValue(
        string reference, string value, bool allowKeyboard, string? mode = null, bool allowDestructive = false)
    {
        DisplayInfo display = _host.EnsureDisplay();
        ElementRefEntry probe = _host.Resolver.Require(reference);

        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaChildId))
        {
            string? existing = _host.Uia.InvokeMsaa(() => MsaaService.GetValue(msaaHwnd, msaaChildId));

            if (!string.IsNullOrEmpty(existing) &&
                !string.Equals(existing, value, StringComparison.Ordinal) &&
                !allowDestructive)
            {
                throw new ToolException(
                    "E_DESTRUCTIVE_REQUIRED",
                    "MSAA 控件已有内容；覆盖请显式传 allow_destructive=true",
                    new JsonObject { ["ref"] = probe.Ref, ["existing_length"] = existing.Length });
            }

            if (!_host.Uia.InvokeMsaa(() => MsaaService.SetValue(msaaHwnd, msaaChildId, value)))
            {
                if (!allowKeyboard)
                {
                    throw new ToolException(
                        "E_PATTERN_UNSUPPORTED",
                        "MSAA 控件不支持 accValue 写入；可改用 allow_keyboard=true 的键盘路径",
                        new JsonObject { ["ref"] = probe.Ref });
                }

                var bounds = _host.Uia.InvokeMsaa(() => MsaaService.GetBounds(msaaHwnd, msaaChildId));

                if (bounds is null)
                {
                    throw new ToolException(
                        "E_ELEMENT_OFFSCREEN",
                        "MSAA 控件没有可用位置，无法切换到键盘写入",
                        new JsonObject { ["ref"] = probe.Ref });
                }

                _host.Click(
                    bounds.Value.Left + bounds.Value.Width / 2,
                    bounds.Value.Top + bounds.Value.Height / 2,
                    "left", 1, "message");

                TypeTextView typed = _host.TypeText(value, null, verify: true, chunkChars: 32, channel: "auto");
                return new SetElementValueView(
                    probe.Ref,
                    "msaa+keyboard",
                    mode ?? "auto",
                    Destructive: null,
                    typed.Verified,
                    value,
                    typed.ObservedLength is null ? null : JsonValue.Create(typed.ObservedLength.Value),
                    Refreshed: false,
                    Chunks: null,
                    DurationMs: null,
                    ObservedLength: null,
                    Warning: null);
            }

            string? observed = _host.Uia.InvokeMsaa(() => MsaaService.GetValue(msaaHwnd, msaaChildId));
            return new SetElementValueView(
                probe.Ref,
                "msaa",
                mode ?? "auto",
                Destructive: null,
                string.Equals(observed, value, StringComparison.Ordinal),
                value,
                observed is null ? null : JsonValue.Create(observed),
                Refreshed: false,
                Chunks: null,
                DurationMs: null,
                ObservedLength: null,
                Warning: null);
        }

        using UiaElementHandle handle = _host.Resolver.Resolve(reference, display, out ElementRefEntry entry, out bool refreshed);
        UiaElementInfo info = handle.Info;

        if (!info.Enabled)
        {
            throw new ToolException(
                "E_ELEMENT_DISABLED",
                "控件已禁用，拒绝写值",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(info) });
        }

        string effectiveMode = ResolveWriteMode(mode, info);

        if (effectiveMode == "replace" && !allowDestructive)
        {
            throw new ToolException(
                "E_DESTRUCTIVE_REQUIRED",
                "replace 会先全选目标内容再覆盖；请显式传 allow_destructive=true，或改用 mode=append/insert",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["mode"] = effectiveMode,
                    ["control_type"] = info.ControlTypeName,
                    ["automation_id"] = info.AutomationId,
                });
        }

        // ValuePattern 只能整值替换，无法追加；append/replace 必须走键盘路径，
        // 否则会在支持 ValuePattern 的控件上静默覆盖已有内容。
        if (effectiveMode is "append" or "replace")
        {
            if (!allowKeyboard)
            {
                throw new ToolException(
                    "E_PATTERN_UNSUPPORTED",
                    effectiveMode == "append"
                        ? "append 无法通过 ValuePattern 实现（只能整值替换）；请显式传 allow_keyboard=true"
                        : "replace 需要键盘写入（Ctrl+A 覆盖）；请显式传 allow_keyboard=true，或改用 mode=insert",
                    new JsonObject
                    {
                        ["ref"] = entry.Ref,
                        ["mode"] = effectiveMode,
                        ["element"] = ElementPresenter.ToJson(info),
                    });
            }

            return SetElementValueByKeyboard(
                display, handle, entry, value, refreshed, effectiveMode, allowDestructive);
        }

        // ValuePattern 的 SetValue 是整值覆盖。mode=insert 在普通控件上走这条路径，
        // 因此必须先看现有内容：非空且不同就要求显式 allow_destructive，
        // 避免把“插入”误当成安全操作而静默清空用户已有输入。
        string? existingValue = handle.GetValue();
        bool destructiveInsert = effectiveMode == "insert" &&
                                 !string.IsNullOrEmpty(existingValue) &&
                                 !string.Equals(existingValue, value, StringComparison.Ordinal);

        if (destructiveInsert && !allowDestructive)
        {
            throw new ToolException(
                "E_DESTRUCTIVE_REQUIRED",
                "insert 在 ValuePattern 控件上是整值写入，会覆盖现有内容；" +
                "请显式传 allow_destructive=true，或改用 mode=append / allow_keyboard=true",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["mode"] = effectiveMode,
                    ["existing_length"] = existingValue?.Length ?? 0,
                    ["control_type"] = info.ControlTypeName,
                    ["automation_id"] = info.AutomationId,
                });
        }

        UiaSetValueResult result = handle.SetValue(value);

        if (result.Failure is not null)
        {
            throw new ToolException(
                "E_UIA",
                $"ValuePattern 写值失败: {result.Failure}",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(info) });
        }

        if (result.Supported && result.ReadOnly)
        {
            throw new ToolException(
                "E_ELEMENT_READONLY",
                "控件为只读，无法通过 ValuePattern 写值",
                new JsonObject { ["ref"] = entry.Ref, ["value"] = result.Value });
        }

        if (result.Supported)
        {
            bool verified = string.Equals(result.Value ?? string.Empty, value, StringComparison.Ordinal);
            return new SetElementValueView(
                entry.Ref,
                "value_pattern",
                effectiveMode,
                destructiveInsert,
                verified,
                value,
                result.Value is null ? null : JsonValue.Create(result.Value),
                refreshed,
                Chunks: null,
                DurationMs: null,
                ObservedLength: null,
                Warning: null);
        }

        if (!allowKeyboard)
        {
            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "该控件不支持 ValuePattern；如接受切换前台后键入，请显式传 allow_keyboard=true",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(info) });
        }

        return SetElementValueByKeyboard(
            display, handle, entry, value, refreshed, mode, allowDestructive);
    }

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

    private SetElementValueView SetElementValueByKeyboard(
        DisplayInfo display,
        UiaElementHandle handle,
        ElementRefEntry entry,
        string value,
        bool refreshed,
        string? mode,
        bool allowDestructive)
    {
        TrackedApp? app;

        app = _host.OwnedAppForPid(entry.Pid);

        if (app is null)
        {
            throw new ToolException(
                "E_NO_TARGET",
                "element_ref 所属应用不在本会话中，无法键入",
                new JsonObject { ["ref"] = entry.Ref });
        }

        _host.RequireOnDisplay(display, entry.Hwnd, "set_element_value");

        string effectiveMode = ResolveWriteMode(mode, handle.Info);
        bool replace = effectiveMode == "replace";

        if (replace && !allowDestructive)
        {
            throw new ToolException(
                "E_DESTRUCTIVE_REQUIRED",
                "replace 会先全选目标内容再覆盖；请显式传 allow_destructive=true，或改用 mode=append/insert",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["mode"] = effectiveMode,
                    ["control_type"] = handle.Info.ControlTypeName,
                    ["automation_id"] = handle.Info.AutomationId,
                });
        }

        IntPtr previous = WindowManager.GetForegroundWindow();
        _host.PauseFocusGuard();

        try
        {
            if (!_host.EnsureForeground(app))
            {
                throw new ToolException(
                    "E_FOCUS",
                    "无法把目标窗口切到前台，已取消键入以避免输入到用户窗口",
                    new JsonObject { ["ref"] = entry.Ref },
                    retryable: true,
                    hint: "确认没有模态框禁用主窗口；可改用消息点击或先关闭弹窗",
                    stage: "focus");
            }

            bool focused = TryFocusElement(handle, display);

            if (!focused)
            {
                throw new ToolException(
                    "E_FOCUS",
                    "无法把焦点设置到控件，已取消键入",
                    new JsonObject { ["ref"] = entry.Ref },
                    retryable: true,
                    stage: "focus");
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (replace)
            {
                InputInjector.PressKeys("Ctrl+A");
                Thread.Sleep(60);

                if (value.Length == 0)
                {
                    // 新版记事本的 Document 控件对 Ctrl+A→Delete 的处理是异步的；
                    // 连续发送可能丢掉 Delete，导致“清空”实际未生效。
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        InputInjector.PressKeys(attempt == 0 ? "Delete" : "Backspace");
                        Thread.Sleep(80);

                        string? probe = handle.GetText(32);
                        if (string.IsNullOrEmpty(probe))
                        {
                            break;
                        }

                        if (attempt + 1 < 3)
                        {
                            InputInjector.PressKeys("Ctrl+A");
                            Thread.Sleep(50);
                        }
                    }
                }
            }
            else if (effectiveMode == "append")
            {
                InputInjector.PressKeys("Ctrl+End");
            }

            Thread.Sleep(30);
            TextWriteResult write = _host.WriteTextVerified(
                app, handle, value, replace, timeoutMs: 12000);

            string? after = handle.GetText(65536);
            bool verified = write.Verified;

            return new SetElementValueView(
                entry.Ref,
                "keyboard",
                effectiveMode,
                replace,
                verified,
                value,
                after is null ? null : JsonValue.Create(after),
                refreshed,
                write.Chunks,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                after?.Length,
                !verified
                    ? "键盘写入未能通过 TextPattern/ValuePattern 回读确认；请用 get_element_text 或截图复核"
                    : null);
        }
        finally
        {
            _host.RestoreForeground(previous);
            _host.ResumeFocusGuard();
        }
    }

    /// <summary>
    /// auto：文档类控件默认追加（避免覆盖），普通控件默认插入；
    /// replace 必须显式 allow_destructive=true。
    /// </summary>
    private static string ResolveWriteMode(string? mode, UiaElementInfo info)
    {
        string requested = (mode ?? "auto").Trim().ToLowerInvariant();

        if (requested is "replace" or "append" or "insert")
        {
            return requested;
        }

        if (requested is not ("auto" or ""))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"未知 set_element_value mode: {mode}（可选 auto/append/insert/replace）");
        }

        bool documentLike = info.ControlTypeName == "Document" ||
                            string.Equals(info.AutomationId, "Body", StringComparison.OrdinalIgnoreCase);

        return documentLike ? "append" : "insert";
    }

    private bool TryFocusElement(UiaElementHandle handle, DisplayInfo display)
    {
        try
        {
            handle.Focus();
            Thread.Sleep(40);
            return VerifyFocus(handle);
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or InvalidOperationException){
            // Word 正文的 Edit/Body 不支持 UIA SetFocus；退化到消息点击元素中心。
            _ = exception;
        }

        UiaElementInfo info = handle.Info;
        int centerX = info.Left + info.Width / 2;
        int centerY = info.Top + info.Height / 2;

        if (centerX < display.X || centerX >= display.X + display.Width ||
            centerY < display.Y || centerY >= display.Y + display.Height)
        {
            return false;
        }

        if (!MessageInput.Click(centerX, centerY, "left", 1, out _))
        {
            return false;
        }

        Thread.Sleep(80);
        return VerifyFocus(handle);
    }

    /// <summary>SetFocus/消息点击后确认焦点确实落在目标控件上，避免键入到相邻控件。</summary>
    private bool VerifyFocus(UiaElementHandle expected)
    {
        using UiaElementHandle? focused = _host.Uia.GetFocusedElementHandle(expected.Info.ProcessId);
        if (focused is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(expected.Info.RuntimeId) &&
            string.Equals(focused.Info.RuntimeId, expected.Info.RuntimeId, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(focused.Info.AutomationId, expected.Info.AutomationId, StringComparison.Ordinal) &&
               string.Equals(focused.Info.ControlTypeName, expected.Info.ControlTypeName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(focused.Info.Name, expected.Info.Name, StringComparison.Ordinal);
    }}
