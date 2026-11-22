using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Json;
using VScreenPilot.Ui;
using VScreenPilot.Perception;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>快照：环境确认、语义树、弹窗、前台、可选截图与脱敏。</summary>
/// <summary>SectionOps：SnapshotBuilder 的拆分子集。</summary>
internal sealed class SnapshotSectionOps
{
    private readonly SnapshotBuilder _core;

    public SnapshotSectionOps(SnapshotBuilder core) => _core = core;
    internal void RequireRoot(UiaTreeResult result)
    {
        if (result.Root is null)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                result.Reason == "root_not_found" ? "树根控件已不存在" : "无法读取语义树",
                new JsonObject { ["reason"] = result.Reason, ["snapshot_id"] = _core._state.SnapshotId() });
        }
    }


    internal UiaTreeResult BuildTree(
        IntPtr hwnd, int depth, int nodes, bool interactiveOnly,
        out bool emptyRetried, out bool effectiveInteractiveOnly)
    {
        UiaTreeResult result = _core._state.Uia.BuildTree(
            hwnd,
            rootRuntimeId: string.Empty,
            maxDepth: depth,
            interactiveOnly: interactiveOnly,
            maxNodes: nodes,
            timeBudgetMs: 3000,
            isCancelled: _core._capture.CancellationProbe());

        emptyRetried = false;
        effectiveInteractiveOnly = interactiveOnly;

        // 空树在 UIA 首帧未就绪时很常见。重试一次并放开 interactive_only；
        // 比直接返回“1 个节点 + 无解释”对调用方有用得多（N4）。
        if (result.Nodes <= 1)
        {
            Thread.Sleep(200);

            UiaTreeResult retry = _core._state.Uia.BuildTree(
                hwnd,
                rootRuntimeId: string.Empty,
                maxDepth: Math.Max(depth, 4),
                interactiveOnly: false,
                maxNodes: nodes,
                timeBudgetMs: 3000,
                isCancelled: _core._capture.CancellationProbe());

            if (retry.Root is not null && retry.Nodes > result.Nodes)
            {
                result = retry;
                emptyRetried = true;
                effectiveInteractiveOnly = false;
            }
        }

        return result;
    }

    internal void BuildDialogs(TrackedApp app, IntPtr hwnd, JsonObject data, ref int redactions)
    {
        var dialogs = new JsonArray();
        bool rootListed = false;

        foreach (WindowInfo dialog in WindowManager.ListOwnedWindows(app.Pid, hwnd))
        {
            if (dialog.Hwnd == hwnd)
            {
                rootListed = true;
            }

            var dialogJson = new JsonObject
            {
                ["hwnd"] = dialog.Hwnd.ToInt64(),
                ["title"] = PrivacyRedactor.RedactEmails(dialog.Title, ref redactions),
                ["class"] = dialog.ClassName,
                ["bounds"] = new JsonObject
                {
                    ["x"] = dialog.Rect.Left,
                    ["y"] = dialog.Rect.Top,
                    ["width"] = dialog.Width,
                    ["height"] = dialog.Height,
                },
            };

            // Office 旧对话框（bosa_sdm_msword）UIA 树为空，MSAA 是唯一语义来源。
            // 把控件直接放进 snapshot.dialogs[].controls，模型不需要再调 find_element。
            if (dialog.ClassName.Contains("bosa", StringComparison.OrdinalIgnoreCase))
            {
                MsaaFindResult msaa = _core._state.Uia.InvokeMsaa(() => MsaaService.FindElements(
                    dialog.Hwnd, null, UiaNameMatch.Exact, null, 100));

                if (msaa.Items.Count > 0)
                {
                    var controls = new JsonArray();

                    lock (_core._state.Refs.SyncRoot)
                    {
                        foreach (UiaElementInfo info in msaa.Items)
                        {
                            var control = new JsonObject
                            {
                                ["ref"] = _core._state.RegisterElement(info, dialog.Hwnd),
                                ["name"] = info.Name,
                                ["control_type"] = info.ControlTypeName,
                                ["bounds"] = new JsonObject
                                {
                                    ["x"] = info.Left,
                                    ["y"] = info.Top,
                                    ["width"] = info.Width,
                                    ["height"] = info.Height,
                                },
                            };

                            if (!string.IsNullOrEmpty(info.Value))
                            {
                                control["value"] = info.Value;
                            }

                            controls.Add(control);
                        }
                    }

                    dialogJson["controls"] = controls;
                    dialogJson["msaa_truncated"] = msaa.Truncated;
                }
            }

            dialogs.Add(dialogJson);
        }

        // snapshot 的 root 可能已经是弹窗（GetActivePopup）。此时 dialogs[] 不含它，
        // 调用方会看到“root 是对话框、dialogs 却是空”的矛盾（N6）。
        if (hwnd != app.Hwnd)
        {
            data["root_hwnd"] = hwnd.ToInt64();
            data["root_is_dialog"] = true;

            if (!rootListed)
            {
                WindowInfo info = WindowManager.FromHandle(hwnd)
                    ?? new WindowInfo(
                        hwnd, app.Pid, string.Empty, string.Empty,
                        default, Visible: false, Owned: false);

                dialogs.Insert(0, (JsonNode)new JsonObject
                {
                    ["hwnd"] = hwnd.ToInt64(),
                    ["title"] = PrivacyRedactor.RedactEmails(info.Title, ref redactions),
                    ["class"] = info.ClassName,
                    ["is_root"] = true,
                    ["bounds"] = new JsonObject
                    {
                        ["x"] = info.Rect.Left,
                        ["y"] = info.Rect.Top,
                        ["width"] = info.Width,
                        ["height"] = info.Height,
                    },
                });
            }
        }

        data["dialogs"] = dialogs;
    }

    internal void BuildWindowsAndForeground(JsonObject data)
    {
        var windows = new JsonArray();

        foreach (TrackedApp tracked in _core._apps.OwnedApps())
        {
            windows.Add((JsonNode)new JsonObject
            {
                ["pid"] = tracked.Pid,
                ["hwnd"] = tracked.Hwnd.ToInt64(),
                ["title"] = _core._apps.IsLive(tracked)
                    ? WindowManager.GetTitle(tracked.Hwnd)
                    : tracked.Title,
                ["alive"] = _core._apps.IsLive(tracked),
                ["owned"] = tracked.Owned,
            });
        }

        data["windows"] = windows;

        IntPtr foreground = WindowManager.GetForegroundWindow();
        WindowInfo? foregroundInfo = WindowManager.FromHandle(foreground);
        bool foregroundTracked = false;

        if (foregroundInfo is not null)
        {
            foregroundTracked = _core._apps.OwnedApps().Any(item => item.Pid == foregroundInfo.Pid);
        }

        data["foreground"] = foregroundInfo is null
            ? null
            : new JsonObject
            {
                ["hwnd"] = foregroundInfo.Hwnd.ToInt64(),
                ["pid"] = foregroundInfo.Pid,
                ["title"] = foregroundTracked ? foregroundInfo.Title : null,
                ["tracked"] = foregroundTracked,
            };
    }
}
