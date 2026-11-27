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
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>InvokeOps：ElementOperations 的拆分子集。</summary>
internal sealed class ElementInvokeOps
{
    internal readonly ElementOperations _host;

    public ElementInvokeOps(ElementOperations host) => _host = host;
    public InvokeElementView InvokeElement(
        string reference, bool allowClick, string? pattern = null, string? selectText = null,
        bool allowDestructive = false)
    {
        if (!string.IsNullOrWhiteSpace(selectText))
        {
            return SelectByText(reference, selectText);
        }

        DisplayInfo display = _host._apps.EnsureDisplay();
        ElementRefEntry probe = _host._state.Resolver.Require(reference);

        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaChildId))
        {
            return InvokeMsaaElement(probe, msaaHwnd, msaaChildId, allowClick, allowDestructive);
        }

        UiaElementHandle handle = _host._state.Resolver.Resolve(
            reference, display, out ElementRefEntry entry, out bool refreshed);
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

            // 语义触发同样是“点击”关闭/删除按钮：与 click/click_ref 保持同一护栏。
            int guardX = info.Left + Math.Max(1, info.Width) / 2;
            int guardY = info.Top + Math.Max(1, info.Height) / 2;
            DestructiveTargetGuard.Check(info, guardX, guardY, allowDestructive, "invoke_element");

            if (mode == UiaInvokePattern.Default)
            {
                return InvokeDefaultAction(display, handle, entry, refreshed);
            }

            InvokeElementView? invoked = TryInvokeWithTimeout(
                handle, info, mode, entry, refreshed, ref handleTransferred);

            if (invoked is not null)
            {
                return invoked;
            }

            return InvokeByMessageClick(display, info, entry, refreshed, allowClick);
        }
        finally
        {
            if (!handleTransferred)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// Invoke 是同步 COM 调用：按钮弹出模态对话框时会一直等到对话框关闭。
    /// 在独立任务里执行，超时后放弃旧 UIA 实例，避免整个会话永久卡死。
    /// </summary>
    private InvokeElementView? TryInvokeWithTimeout(
        UiaElementHandle handle,
        UiaElementInfo info,
        UiaInvokePattern mode,
        ElementRefEntry entry,
        bool refreshed,
        ref bool handleTransferred)
    {
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
            _host._input.ResetUia();
            throw new ToolException(
                "E_TIMEOUT",
                "UIA 触发 5s 未返回，常见于会打开模态对话框的按钮",
                new JsonObject { ["ref"] = entry.Ref },
                retryable: true,
                hint: "先用 get_ui_tree 或截图确认是否已弹出对话框；也可改用 click 的消息点击",
                stage: "invoke");
        }

        (bool ok, string? strategy) = invokeTask.Result;

        if (!ok)
        {
            return null;
        }

        Thread.Sleep(50);
        return new InvokeElementView(
            entry.Ref, strategy ?? "uia", null, null, null, refreshed, null,
            ElementPresenter.ToView(info, compact: true));
    }

    /// <summary>语义触发不可用时的兜底：按控件中心做消息点击（需 allow_click）。</summary>
    private InvokeElementView InvokeByMessageClick(
        DisplayInfo display,
        UiaElementInfo info,
        ElementRefEntry entry,
        bool refreshed,
        bool allowClick)
    {
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

        _host._apps.RequirePointInTrackedApp(display, centerX, centerY, "invoke_element");

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

    internal static UiaInvokePattern ParseInvokePattern(string? pattern) =>
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
    internal InvokeElementView InvokeDefaultAction(
        DisplayInfo display,
        UiaElementHandle handle,
        ElementRefEntry entry,
        bool refreshed)
    {
        TrackedApp? app;

        app = _host._apps.OwnedAppForPid(entry.Pid);

        if (app is null)
        {
            throw new ToolException(
                "E_NO_TARGET",
                "element_ref 所属应用不在本会话中，无法执行默认动作",
                new JsonObject { ["ref"] = entry.Ref });
        }

        _host._apps.RequireOnDisplay(display, app.Hwnd, "invoke_element");

        IntPtr previous = WindowManager.GetForegroundWindow();
        _host._focus.PauseFocusGuard();

        try
        {
            if (!_host._focus.EnsureForeground(app))
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
                exception is COMException or InvalidOperationException)
            {
                // 部分 WinForms/WPF 控件不支持 SetFocus；继续用 Enter 尝试。
                LogSink.Debug($"忽略异常: {exception.Message}");
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
            _host._focus.RestoreForeground(previous);
            _host._focus.ResumeFocusGuard();
        }
    }


    /// <summary>
    /// 展开控件、在子树内按精确文本找列表项并选中。多命中时返回候选而不是猜第一项。
    /// 只覆盖标准 UIA 组合框/列表；下拉列表若在独立弹窗中，会返回 E_ELEMENT_NOT_FOUND 并提示。
    /// 选择动作本身不触发目标项，因此不套用破坏性目标护栏；需要执行时再显式 click/invoke。
    /// </summary>
    private InvokeElementView SelectByText(
        string reference, string selectText)
    {
        DisplayInfo display = _host._apps.EnsureDisplay();
        using UiaElementHandle handle = _host._state.Resolver.Resolve(
            reference, display, out ElementRefEntry entry, out bool refreshed);

        if (!handle.Info.Enabled)
        {
            throw new ToolException(
                "E_ELEMENT_DISABLED",
                "控件已禁用，拒绝展开/选择",
                new JsonObject { ["ref"] = entry.Ref });
        }

        (bool expanded, string? expandStrategy) = handle.Invoke(UiaInvokePattern.Expand);

        if (!expanded)
        {
            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "控件不支持 ExpandCollapse，无法按文本选择",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["control_type"] = handle.Info.ControlTypeName,
                },
                retryable: false,
                hint: "先确认该控件是组合框/列表；自绘下拉需要 Profile 或 click_ref",
                stage: "invoke");
        }

        Thread.Sleep(150);

        UiaFindResult found = handle.FindDescendants(
            selectText, UiaNameMatch.Exact, null, null, 50);
        string[] itemTypes = ["ListItem", "DataItem", "TreeItem", "MenuItem"];
        List<UiaElementInfo> typed = found.Items
            .Where(item => itemTypes.Contains(item.ControlTypeName, StringComparer.OrdinalIgnoreCase))
            .ToList();
        List<UiaElementInfo> candidates = typed.Count > 0 ? typed : found.Items;

        if (candidates.Count == 0)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                $"已展开但未找到文本为“{selectText}”的候选项",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["select_text"] = selectText,
                    ["expanded"] = true,
                    ["expand_method"] = expandStrategy,
                },
                retryable: true,
                hint: "下拉列表可能在独立弹窗中；改用 find_element{within} 或 click_text 定位后 click_ref",
                stage: "invoke");
        }

        if (candidates.Count > 1)
        {
            var details = new JsonArray();

            foreach (UiaElementInfo candidate in candidates.Take(20))
            {
                details.Add((JsonNode)new JsonObject
                {
                    ["name"] = candidate.Name,
                    ["control_type"] = candidate.ControlTypeName,
                    ["left"] = candidate.Left,
                    ["top"] = candidate.Top,
                    ["width"] = candidate.Width,
                    ["height"] = candidate.Height,
                });
            }

            throw new ToolException(
                "E_MULTIPLE_MATCHES",
                $"“{selectText}”命中 {candidates.Count} 个候选项，拒绝猜测",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["select_text"] = selectText,
                    ["candidates"] = details,
                },
                retryable: false,
                hint: "改用更精确的文本，或先 find_element{within} 拿到唯一 ref 再 invoke_element",
                stage: "invoke");
        }

        UiaElementInfo item = candidates[0];

        if (string.IsNullOrEmpty(item.RuntimeId))
        {
            throw new ToolException(
                "E_ELEMENT_STALE",
                "候选项没有 runtime_id，无法唯一定位",
                new JsonObject { ["ref"] = entry.Ref, ["name"] = item.Name });
        }

        using UiaElementHandle? itemHandle = _host._state.Uia.FindByRuntimeId(entry.Hwnd, item.RuntimeId);

        if (itemHandle is null)
        {
            throw new ToolException(
                "E_ELEMENT_STALE",
                "候选项在选中前已失效",
                new JsonObject { ["ref"] = entry.Ref, ["name"] = item.Name });
        }

        (bool selected, string? selectStrategy) = itemHandle.Invoke(UiaInvokePattern.Select);

        if (!selected)
        {
            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "候选项不支持 SelectionItem.Select",
                new JsonObject { ["ref"] = entry.Ref, ["name"] = item.Name },
                retryable: false,
                hint: "改用 click_ref 点击该候选项",
                stage: "invoke");
        }

        Thread.Sleep(80);
        string? observed = handle.GetValue();
        bool? verified = observed is null
            ? null
            : string.Equals(observed, selectText, StringComparison.Ordinal);

        return new InvokeElementView(
            entry.Ref,
            selectStrategy ?? "SelectionItem",
            Invoked: true,
            Verified: verified,
            ControlType: item.ControlTypeName,
            Refreshed: refreshed,
            Detail: $"selected={selectText}",
            Element: ElementPresenter.ToView(item, compact: true));
    }

    internal InvokeElementView InvokeMsaaElement(
        ElementRefEntry probe, IntPtr msaaHwnd, int msaaChildId, bool allowClick,
        bool allowDestructive = false)
    {
        var probeInfo = new UiaElementInfo(
            probe.RuntimeId, probe.Name, probe.AutomationId, 0, probe.ControlTypeName,
            probe.Left, probe.Top, probe.Right, probe.Bottom, true, false, probe.Pid,
            string.Empty);
        DestructiveTargetGuard.Check(
            probeInfo,
            probe.Left + Math.Max(1, probe.Right - probe.Left) / 2,
            probe.Top + Math.Max(1, probe.Bottom - probe.Top) / 2,
            allowDestructive,
            "invoke_element");

        bool invoked = _host._state.Uia.InvokeMsaa(() => MsaaService.DoDefaultAction(msaaHwnd, msaaChildId));

        if (!invoked && allowClick)
        {
            var bounds = _host._state.Uia.InvokeMsaa(() => MsaaService.GetBounds(msaaHwnd, msaaChildId));

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
}
