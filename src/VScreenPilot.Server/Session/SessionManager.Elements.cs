using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Core.Displays;
using VScreenPilot.Input;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Uia;

namespace VScreenPilot.Session;

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
}

internal sealed partial class SessionManager
{
    // 保留当前代 + 上一代（max_nodes 1024），避免新快照把自己的 ref 挤掉。
    private const int ElementRefCapacity = 2048;
    private const long ElementRefTtlMs = 5 * 60 * 1000;

    private readonly Dictionary<string, ElementRefEntry> _elements = new();
    private readonly Dictionary<string, string> _elementKeys = new();
    // 快照构建与 ref 解析共用一把锁：保证“清空 + 建树 + 登记”对其他线程是原子的。
    private readonly object _refGate = new();
    // snapshot 的建树与提交串行化：避免两个 snapshot 乱序提交、互相覆盖代际。
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private long _elementSeq;
    private long _snapshotSeq;
    // 这两个字典会在不持 _refGate 的路径被读取（SnapshotId/RequireElementEntry），
    // 而写入发生在 _refGate + _gate 或 release 的 _gate 下；用并发字典避免跨客户端读写撕裂。
    private readonly ConcurrentDictionary<string, long> _snapshotGenerationByClient =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _snapshotIdByClient =
        new(StringComparer.Ordinal);

    /// <summary>最近一次 snapshot 的 id；ref 只在该快照内有效。</summary>
    public string SnapshotId =>
        _snapshotIdByClient.TryGetValue(CurrentClientId, out string? id) ? id : string.Empty;

    /// <summary>
    /// 当前快照 id；若还没有任何快照则创建第一个。
    /// get_ui_tree/find_element 默认复用当前代，不再每次调用都让既有 ref 失效。
    /// </summary>
    public string EnsureSnapshot()
    {
        lock (_refGate)
        {
            string clientId = CurrentClientId;

            if (_snapshotGenerationByClient.TryGetValue(clientId, out long generation) &&
                generation > 0 &&
                _snapshotIdByClient.TryGetValue(clientId, out string? id))
            {
                return id;
            }

            return BeginSnapshot();
        }
    }

    /// <summary>清空指定客户端的快照代际；clientId 为空表示全量（daemon 退出）。</summary>
    private void ClearSnapshotState(string? clientId)
    {
        if (clientId is not null)
        {
            _snapshotGenerationByClient.TryRemove(clientId, out _);
            _snapshotIdByClient.TryRemove(clientId, out _);
            return;
        }

        foreach (string key in _snapshotGenerationByClient.Keys)
        {
            _snapshotGenerationByClient.TryRemove(key, out _);
        }

        foreach (string key in _snapshotIdByClient.Keys)
        {
            _snapshotIdByClient.TryRemove(key, out _);
        }
    }

    /// <summary>开始新快照：清空当前客户端的旧 ref，让跨快照使用返回 E_ELEMENT_STALE。</summary>
    public string BeginSnapshot()
    {
        lock (_refGate)
        {
            lock (_gate)
            {
                string clientId = CurrentClientId;
                long generation = _snapshotGenerationByClient.TryGetValue(clientId, out long current)
                    ? current + 1
                    : 1;
                _snapshotGenerationByClient[clientId] = generation;

                // 保留上一代 ref，才能对“旧快照 ref”返回 E_STALE_SNAPSHOT 而不是“未知 ref”。
                // 只淘汰当前客户端的更老代际，不能影响其他客户端。
                List<string>? expired = null;

                foreach ((string key, ElementRefEntry value) in _elements)
                {
                    if (string.Equals(value.OwnerClientId, clientId, StringComparison.Ordinal) &&
                        value.Generation < generation - 1)
                    {
                        (expired ??= new List<string>()).Add(key);
                    }
                }

                if (expired is not null)
                {
                    foreach (string key in expired)
                    {
                        RemoveElement(key);
                    }
                }

                string snapshotId = "s" + Interlocked.Increment(ref _snapshotSeq);
                _snapshotIdByClient[clientId] = snapshotId;
                return snapshotId;
            }
        }
    }

    /// <summary>登记元素并返回稳定的会话内引用（只对外暴露 ref）。</summary>
    public string RegisterElement(UiaElementInfo info, IntPtr hwnd)
    {
        lock (_refGate)
        lock (_gate)
        {
            PruneExpiredElements();

            string clientId = CurrentClientId;
            string key = clientId + "\u0000" + ElementKey(info, hwnd);
            long generation = _snapshotGenerationByClient.TryGetValue(clientId, out long current)
                ? current
                : 0;

            if (_elementKeys.TryGetValue(key, out string? existingRef) &&
                _elements.TryGetValue(existingRef, out ElementRefEntry? existing) &&
                existing.Generation == generation)
            {
                existing.Update(info, hwnd);
                return existing.Ref;
            }

            if (_elements.Count >= ElementRefCapacity)
            {
                EvictOldestElement();
            }

            string reference = "e" + (++_elementSeq);
            // UWP 等跨进程宿主：元素属于内容进程，HWND 属于 ApplicationFrameHost。
            // ref 的归属校验必须按 HWND 的进程，否则每次解析都会被判成“句柄复用”。
            int ownerPid = hwnd != IntPtr.Zero ? WindowManager.GetProcessId(hwnd) : 0;
            var entry = new ElementRefEntry
            {
                Ref = reference,
                Key = key,
                OwnerClientId = clientId,
                Pid = ownerPid > 0 ? ownerPid : info.ProcessId,
                Hwnd = hwnd,
                Generation = generation,
                LastUsedTicks = Environment.TickCount64,
            };
            entry.Update(info, hwnd);

            _elements[reference] = entry;
            _elementKeys[key] = reference;
            return reference;
        }
    }

    private static string ElementKey(UiaElementInfo info, IntPtr hwnd) =>
        !string.IsNullOrEmpty(info.RuntimeId)
            ? $"{info.ProcessId}:{hwnd.ToInt64():X}:{info.RuntimeId}"
            : $"{info.ProcessId}:{hwnd.ToInt64():X}:{info.ControlType}:{info.AutomationId}:{info.Name}:{info.Left},{info.Top}";

    private void PruneExpiredElements()
    {
        long now = Environment.TickCount64;
        List<string>? expired = null;

        foreach (var pair in _elements)
        {
            if (now - pair.Value.LastUsedTicks > ElementRefTtlMs)
            {
                (expired ??= new List<string>()).Add(pair.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (string reference in expired)
        {
            RemoveElement(reference);
        }
    }

    private void EvictOldestElement()
    {
        string? oldestRef = null;
        long oldestTicks = long.MaxValue;

        foreach (var pair in _elements)
        {
            if (pair.Value.LastUsedTicks < oldestTicks)
            {
                oldestTicks = pair.Value.LastUsedTicks;
                oldestRef = pair.Key;
            }
        }

        if (oldestRef is not null)
        {
            RemoveElement(oldestRef);
        }
    }

    private void RemoveElement(string reference)
    {
        if (!_elements.Remove(reference, out ElementRefEntry? entry))
        {
            return;
        }

        if (_elementKeys.TryGetValue(entry.Key, out string? mapped) && mapped == reference)
        {
            _elementKeys.Remove(entry.Key);
        }
        else
        {
            foreach (var pair in _elementKeys)
            {
                if (pair.Value == reference)
                {
                    _elementKeys.Remove(pair.Key);
                    break;
                }
            }
        }
    }

    private ElementRefEntry RequireElementEntry(string reference)
    {
        string key = (reference ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            throw new ToolException("E_INVALID_ARG", "element_ref 不能为空");
        }

        ElementRefEntry? entry;
        bool staleSnapshot = false;
        string clientId = CurrentClientId;
        long generation = _snapshotGenerationByClient.TryGetValue(clientId, out long current)
            ? current
            : 0;

        lock (_refGate)
        lock (_gate)
        {
            if (!_elements.TryGetValue(key, out entry))
            {
                entry = null;
            }
            else if (!string.Equals(entry.OwnerClientId, clientId, StringComparison.Ordinal))
            {
                // 其他客户端的 ref 对当前客户端等同未知，避免跨会话读取元素。
                entry = null;
            }
            else if (Environment.TickCount64 - entry.LastUsedTicks > ElementRefTtlMs)
            {
                // 过期条目直接移除，避免后续按旧 runtime_id 误命中。
                RemoveElement(key);
                entry = null;
            }
            else if (generation > 0 && entry.Generation != generation)
            {
                // ref 只对创建它的快照有效：跨快照使用是明确错误，而不是“碰巧还能用”。
                entry = null;
                staleSnapshot = true;
            }
            else
            {
                entry.LastUsedTicks = Environment.TickCount64;
            }
        }

        if (staleSnapshot)
        {
            throw new ToolException(
                "E_STALE_SNAPSHOT",
                $"element_ref {key} 属于旧快照，已失效",
                new JsonObject { ["ref"] = key, ["current_snapshot"] = SnapshotId },
                retryable: true,
                hint: "重新调用 snapshot，并只使用新返回的 ref",
                stage: "snapshot");
        }

        if (entry is null)
        {
            throw new ToolException(
                "E_ELEMENT_STALE",
                $"未知或已失效的 element_ref: {key}，请重新 get_ui_tree / find_element",
                new JsonObject { ["ref"] = key });
        }

        return entry;
    }

    /// <summary>
    /// 把 element_ref 解析为可执行模式的 UIA 句柄：先按 runtime_id 定位，
    /// 失败再按指纹唯一重定位；仍失败则 E_ELEMENT_STALE（不猜）。
    /// </summary>
    private UiaElementHandle ResolveElementHandle(
        string reference, DisplayInfo display, out ElementRefEntry entry, out bool refreshed)
    {
        entry = RequireElementEntry(reference);
        refreshed = false;

        if (entry.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(entry.Hwnd))
        {
            throw new ToolException(
                "E_ELEMENT_STALE",
                "element_ref 所属窗口已关闭",
                new JsonObject { ["ref"] = entry.Ref, ["hwnd"] = entry.Hwnd.ToInt64() });
        }

        bool tracked;
        int entryPid = entry.Pid;

        // 句柄可能被系统复用：必须确认该 HWND 仍属于登记时的进程。
        _ = NativeMethods.GetWindowThreadProcessId(entry.Hwnd, out uint entryHwndPid);

        if (entryHwndPid != (uint)entryPid)
        {
            throw new ToolException(
                "E_ELEMENT_STALE",
                "element_ref 所属窗口已不属于登记时的进程（可能发生了 PID/句柄复用）",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["pid"] = entry.Pid,
                    ["actual_pid"] = (int)entryHwndPid,
                });
        }

        lock (_gate)
        {
            tracked = _apps.Any(app => IsOwned(app) && app.Pid == entryPid && IsLiveTrackedApp(app));
        }

        if (!tracked)
        {
            throw new ToolException(
                "E_NO_TARGET",
                "element_ref 所属应用已不在本会话中，请重新 launch_or_place_app",
                new JsonObject { ["ref"] = entry.Ref, ["pid"] = entry.Pid });
        }

        RequireWindowOnDisplay(display, entry.Hwnd, "element_ref");

        UiaElementHandle? handle = null;

        if (!string.IsNullOrEmpty(entry.RuntimeId))
        {
            handle = Uia.FindByRuntimeId(entry.Hwnd, entry.RuntimeId);
        }

        if (handle is not null)
        {
            entry.Update(handle.Info, entry.Hwnd);
            return handle;
        }

        handle = Uia.FindByFingerprint(
            entry.Hwnd, entry.Name, entry.AutomationId, entry.ControlTypeName,
            entry.Left, entry.Top, entry.Right, entry.Bottom);

        // 控件可能被移动到同进程的另一个顶层窗口（保存框、浮动面板等）。
        if (handle is null)
        {
            foreach (WindowInfo candidate in WindowManager.ListForProcess(entry.Pid))
            {
                if (candidate.Hwnd == entry.Hwnd)
                {
                    continue;
                }

                handle = Uia.FindByFingerprint(
                    candidate.Hwnd, entry.Name, entry.AutomationId, entry.ControlTypeName,
                    entry.Left, entry.Top, entry.Right, entry.Bottom);

                if (handle is not null)
                {
                    entry.Hwnd = candidate.Hwnd;
                    break;
                }
            }
        }

        if (handle is null)
        {
            throw new ToolException(
                "E_ELEMENT_STALE",
                "控件已销毁或无法唯一重定位，请重新 get_ui_tree / find_element",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["name"] = entry.Name,
                    ["automation_id"] = entry.AutomationId,
                    ["control_type"] = entry.ControlTypeName,
                });
        }

        refreshed = true;
        entry.Update(handle.Info, entry.Hwnd);
        _log.Info($"element_ref {entry.Ref} 已按指纹重定位");
        return handle;
    }

    /* ------------------------------------------------------------------ */
    /* 工具：get_ui_tree                                                   */
    /* ------------------------------------------------------------------ */

    public JsonObject GetUiTree(
        int? maxDepth, bool? interactiveOnly, int? maxNodes, string? root, int? pid,
        string? format = null, bool newSnapshot = false)
    {
        int depth = Math.Clamp(maxDepth ?? 3, 1, 10);
        // 树节点上限不能超过 ref 容量，否则返回的早期 ref 已被淘汰（E5）。
        int nodes = Math.Clamp(maxNodes ?? 300, 1, Math.Min(1024, ElementRefCapacity));
        bool interactive = interactiveOnly ?? true;
        int effectiveNodes = nodes;

        IntPtr hwnd;
        string rootRuntimeId;

        if (!string.IsNullOrWhiteSpace(root))
        {
            // MSAA ref 不是 UIA runtime_id，不能走 ResolveElementHandle；单独展开成扁平树。
            ElementRefEntry msaaEntry = RequireElementEntry(root);

            if (MsaaService.TryParse(msaaEntry.RuntimeId, out IntPtr msaaHwnd, out int msaaOrdinal))
            {
                return BuildMsaaTree(msaaEntry, msaaHwnd, msaaOrdinal, nodes, format);
            }

            // 走完整的 ref 校验：pid 仍在会话、窗口在虚拟屏、runtime_id 失效时按指纹重定位。
            DisplayInfo display = EnsureEnvironmentFast();
            using (UiaElementHandle handle = ResolveElementHandle(
                       root, display, out ElementRefEntry entry, out bool refreshed))
            {
                hwnd = entry.Hwnd;
                rootRuntimeId = handle.Info.RuntimeId;

                if (refreshed)
                {
                    _log.Info($"get_ui_tree 树根 {entry.Ref} 已按指纹重定位");
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
            TrackedApp? app = FindLiveTrackedApp(pid);

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
            DisplayInfo display = EnsureEnvironmentFast();
            RequireWindowOnDisplay(display, hwnd, "get_ui_tree");
            rootRuntimeId = string.Empty;
        }

        // 建树不持 _refGate：UIA 最坏 3s，不能把 ref 解析/其它观察一起堵住。
        // 只有“推进代际 + 登记 ref + 生成 JSON”这段纯内存操作才进锁。
        UiaTreeResult result = Uia.BuildTree(
            hwnd,
            rootRuntimeId,
            depth,
            interactive,
            nodes,
            timeBudgetMs: 3000,
            isCancelled: () => Cancellation.IsCancellationRequested);

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

        lock (_refGate)
        {
            // 只有显式 new_snapshot 才推进代际；默认复用当前快照，
            // 这样 find -> get_ui_tree -> invoke 不会莫名 E_STALE_SNAPSHOT。
            snapshotId = newSnapshot ? BeginSnapshot() : EnsureSnapshot();
            tree = ConvertTreeNode(result.Root, hwnd, ref rootRef);
        }

        WindowInfo? window = WindowManager.FromHandle(hwnd);
        var json = new JsonObject
        {
            ["root_ref"] = rootRef,
            ["tree"] = tree,
            ["nodes"] = result.Nodes,
            ["visited"] = result.Visited,
            ["pruned"] = result.Pruned,
            ["truncated"] = result.Truncated,
            ["interactive_only"] = interactive,
            ["max_depth"] = depth,
            ["max_depth_reached"] = result.MaxDepthReached,
            ["elapsed_ms"] = result.ElapsedMs,
            ["ref_capacity"] = ElementRefCapacity,
            ["max_nodes_effective"] = effectiveNodes,
            ["snapshot_id"] = snapshotId,
            ["new_snapshot"] = newSnapshot,
        };

        if (nodes != (maxNodes ?? 300))
        {
            json["max_nodes_clamped"] = true;
        }

        if (string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
        {
            json["text"] = SnapshotFormat.Render(tree, includeValues: true);
        }

        if (result.Reason is not null)
        {
            json["truncated_reason"] = result.Reason;
        }

        if (window is not null)
        {
            json["window"] = new JsonObject
            {
                ["hwnd"] = window.Hwnd.ToInt64(),
                ["pid"] = window.Pid,
                ["title"] = window.Title,
            };
        }

        return json;
    }

    /// <summary>
    /// MSAA 对话框没有 UIA 树；把控件展开成一层扁平树，ref 可直接 invoke/click/get_text。
    /// </summary>
    private JsonObject BuildMsaaTree(
        ElementRefEntry rootEntry, IntPtr hwnd, int ordinal, int maxNodes, string? format)
    {
        MsaaFindResult found = MsaaService.FindElements(
            hwnd, null, UiaNameMatch.Exact, null, maxNodes);

        var children = new JsonArray();
        string snapshotId;
        int count = 0;

        lock (_refGate)
        {
            snapshotId = EnsureSnapshot();

            foreach (UiaElementInfo info in found.Items)
            {
                if (info.RuntimeId == rootEntry.RuntimeId)
                {
                    continue;
                }

                children.Add((JsonNode)new JsonObject
                {
                    ["ref"] = RegisterElement(info, hwnd),
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

        var json = new JsonObject
        {
            ["root_ref"] = rootEntry.Ref,
            ["tree"] = tree,
            ["nodes"] = count + 1,
            ["truncated"] = found.Truncated,
            ["interactive_only"] = true,
            ["max_depth"] = 1,
            ["max_nodes_effective"] = maxNodes,
            ["ref_capacity"] = ElementRefCapacity,
            ["snapshot_id"] = snapshotId,
            ["new_snapshot"] = false,
            ["backend"] = "msaa",
        };

        if (found.Truncated)
        {
            json["truncated_reason"] = "msaa_limit";
        }

        if (string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
        {
            json["text"] = SnapshotFormat.Render(tree, includeValues: true);
        }

        return json;
    }

    private JsonObject ConvertTreeNode(UiaTreeNode node, IntPtr hwnd, ref string rootRef)
    {
        UiaElementInfo info = node.Info;
        string reference = RegisterElement(info, hwnd);

        if (string.IsNullOrEmpty(rootRef))
        {
            rootRef = reference;
        }

        var json = new JsonObject
        {
            ["ref"] = reference,
            ["control_type"] = info.ControlTypeName,
        };

        if (!string.IsNullOrEmpty(info.Name))
        {
            json["name"] = info.Name.Length > 200 ? info.Name[..200] : info.Name;
        }

        if (!string.IsNullOrEmpty(info.AutomationId))
        {
            json["automation_id"] = info.AutomationId;
        }

        if (!info.Password && !string.IsNullOrEmpty(info.Value))
        {
            json["value"] = info.Value.Length > 200 ? info.Value[..200] : info.Value;
        }

        json["bounds"] = new JsonObject
        {
            ["x"] = info.Left,
            ["y"] = info.Top,
            ["width"] = info.Width,
            ["height"] = info.Height,
        };

        if (!info.Enabled)
        {
            json["enabled"] = false;
        }

        if (info.Offscreen)
        {
            json["offscreen"] = true;
        }

        if (info.Focused)
        {
            json["focused"] = true;
        }

        if (node.Children.Count > 0)
        {
            var children = new JsonArray();
            foreach (UiaTreeNode child in node.Children)
            {
                string unused = string.Empty;
                children.Add(ConvertTreeNode(child, hwnd, ref unused));
            }

            json["children"] = children;
        }

        return json;
    }

    /* ------------------------------------------------------------------ */
    /* 工具：invoke_element / set_element_value                            */
    /* ------------------------------------------------------------------ */

    public JsonObject InvokeElement(string reference, bool allowClick, string? pattern = null)
    {
        DisplayInfo display = EnsureEnvironment();
        ElementRefEntry probe = RequireElementEntry(reference);

        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaChildId))
        {
            bool invoked = MsaaService.DoDefaultAction(msaaHwnd, msaaChildId);

            if (!invoked && allowClick)
            {
                var bounds = MsaaService.GetBounds(msaaHwnd, msaaChildId);

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

            return new JsonObject
            {
                ["ref"] = probe.Ref,
                ["method"] = "msaa",
                ["invoked"] = true,
                ["verified"] = false,
                ["refreshed"] = false,
            };
        }

        UiaElementHandle handle = ResolveElementHandle(reference, display, out ElementRefEntry entry, out bool refreshed);
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
                new JsonObject { ["ref"] = entry.Ref, ["element"] = info.ToJson() });
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
            ResetUia();
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
            return new JsonObject
            {
                ["ref"] = entry.Ref,
                ["method"] = strategy,
                ["refreshed"] = refreshed,
                ["element"] = info.ToCompactJson(),
            };
        }

        if (!allowClick)
        {
            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "该控件没有可用的 UIA 触发模式（Invoke/Toggle/SelectionItem/ExpandCollapse/Legacy）；" +
                "可先用 pattern 指定期望模式；如需按坐标触发，请改用 click 或显式传 allow_click=true",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = info.ToJson() });
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

        RequirePointInTrackedApp(display, centerX, centerY, "invoke_element");

        if (!MessageInput.Click(centerX, centerY, "left", 1, out string? detail))
        {
            throw new ToolException(
                "E_INPUT_UNSUPPORTED",
                "控件不支持语义触发，按坐标点击也失败",
                new JsonObject { ["ref"] = entry.Ref, ["detail"] = detail });
        }

        Thread.Sleep(50);
        return new JsonObject
        {
            ["ref"] = entry.Ref,
            ["method"] = "message_click",
            ["refreshed"] = refreshed,
            ["detail"] = detail,
            ["element"] = info.ToCompactJson(),
        };
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
    private JsonObject InvokeDefaultAction(
        DisplayInfo display,
        UiaElementHandle handle,
        ElementRefEntry entry,
        bool refreshed)
    {
        TrackedApp? app;

        lock (_gate)
        {
            app = _apps.FirstOrDefault(
                item => IsOwned(item) && item.Pid == entry.Pid && IsLiveTrackedApp(item));
        }

        if (app is null)
        {
            throw new ToolException(
                "E_NO_TARGET",
                "element_ref 所属应用不在本会话中，无法执行默认动作",
                new JsonObject { ["ref"] = entry.Ref });
        }

        RequireWindowOnDisplay(display, app.Hwnd, "invoke_element");

        IntPtr previous = WindowManager.GetForegroundWindow();
        PauseFocusGuard();

        try
        {
            if (!EnsureForeground(app))
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

            return new JsonObject
            {
                ["ref"] = entry.Ref,
                ["method"] = "default_action",
                ["control_type"] = handle.Info.ControlTypeName,
                ["refreshed"] = refreshed,
                ["element"] = handle.Info.ToJson(),
            };
        }
        finally
        {
            RestoreForeground(previous);
            ResumeFocusGuard();
        }
    }

    public JsonObject SetElementValue(
        string reference, string value, bool allowKeyboard, string? mode = null, bool allowDestructive = false)
    {
        DisplayInfo display = EnsureEnvironment();
        ElementRefEntry probe = RequireElementEntry(reference);

        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaChildId))
        {
            string? existing = MsaaService.GetValue(msaaHwnd, msaaChildId);

            if (!string.IsNullOrEmpty(existing) &&
                !string.Equals(existing, value, StringComparison.Ordinal) &&
                !allowDestructive)
            {
                throw new ToolException(
                    "E_DESTRUCTIVE_REQUIRED",
                    "MSAA 控件已有内容；覆盖请显式传 allow_destructive=true",
                    new JsonObject { ["ref"] = probe.Ref, ["existing_length"] = existing.Length });
            }

            if (!MsaaService.SetValue(msaaHwnd, msaaChildId, value))
            {
                if (!allowKeyboard)
                {
                    throw new ToolException(
                        "E_PATTERN_UNSUPPORTED",
                        "MSAA 控件不支持 accValue 写入；可改用 allow_keyboard=true 的键盘路径",
                        new JsonObject { ["ref"] = probe.Ref });
                }

                var bounds = MsaaService.GetBounds(msaaHwnd, msaaChildId);

                if (bounds is null)
                {
                    throw new ToolException(
                        "E_ELEMENT_OFFSCREEN",
                        "MSAA 控件没有可用位置，无法切换到键盘写入",
                        new JsonObject { ["ref"] = probe.Ref });
                }

                Click(
                    bounds.Value.Left + bounds.Value.Width / 2,
                    bounds.Value.Top + bounds.Value.Height / 2,
                    "left", 1, "message");

                JsonObject typed = TypeText(value, null, verify: true, chunkChars: 32, channel: "auto");
                return new JsonObject
                {
                    ["ref"] = probe.Ref,
                    ["method"] = "msaa+keyboard",
                    ["mode"] = mode ?? "auto",
                    ["verified"] = typed["verified"]?.GetValue<bool>() ?? false,
                    ["written"] = value,
                    ["value"] = typed["observed_length"],
                    ["refreshed"] = false,
                };
            }

            string? observed = MsaaService.GetValue(msaaHwnd, msaaChildId);
            return new JsonObject
            {
                ["ref"] = probe.Ref,
                ["method"] = "msaa",
                ["mode"] = mode ?? "auto",
                ["verified"] = string.Equals(observed, value, StringComparison.Ordinal),
                ["written"] = value,
                ["value"] = observed,
                ["refreshed"] = false,
            };
        }

        using UiaElementHandle handle = ResolveElementHandle(reference, display, out ElementRefEntry entry, out bool refreshed);
        UiaElementInfo info = handle.Info;

        if (!info.Enabled)
        {
            throw new ToolException(
                "E_ELEMENT_DISABLED",
                "控件已禁用，拒绝写值",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = info.ToJson() });
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
                        ["element"] = info.ToJson(),
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
                new JsonObject { ["ref"] = entry.Ref, ["element"] = info.ToJson() });
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
            return new JsonObject
            {
                ["ref"] = entry.Ref,
                ["method"] = "value_pattern",
                ["mode"] = effectiveMode,
                ["destructive"] = destructiveInsert,
                ["verified"] = verified,
                ["written"] = value,
                ["value"] = result.Value,
                ["refreshed"] = refreshed,
            };
        }

        if (!allowKeyboard)
        {
            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "该控件不支持 ValuePattern；如接受切换前台后键入，请显式传 allow_keyboard=true",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = info.ToJson() });
        }

        return SetElementValueByKeyboard(
            display, handle, entry, value, refreshed, mode, allowDestructive);
    }

    public JsonObject GetElementText(string reference, int maxChars)
    {
        DisplayInfo display = EnsureEnvironment();
        ElementRefEntry probe = RequireElementEntry(reference);
        maxChars = Math.Clamp(maxChars, 1, 65536);

        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaChildId))
        {
            string msaaText = MsaaService.GetValue(msaaHwnd, msaaChildId) ?? string.Empty;
            return new JsonObject
            {
                ["ref"] = probe.Ref,
                ["control_type"] = probe.ControlTypeName,
                ["automation_id"] = probe.AutomationId,
                ["text"] = msaaText.Length > maxChars ? msaaText[..maxChars] : msaaText,
                ["length"] = msaaText.Length,
                ["truncated"] = msaaText.Length > maxChars,
                ["refreshed"] = false,
            };
        }

        using UiaElementHandle handle = ResolveElementHandle(
            reference, display, out ElementRefEntry entry, out bool refreshed);

        string? text = handle.GetText(maxChars);

        if (text is null)
        {
            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "该控件不支持 TextPattern/ValuePattern，无法读取文本",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = handle.Info.ToJson() });
        }

        return new JsonObject
        {
            ["ref"] = entry.Ref,
            ["control_type"] = handle.Info.ControlTypeName,
            ["automation_id"] = handle.Info.AutomationId,
            ["text"] = text,
            ["length"] = text.Length,
            ["truncated"] = text.Length >= maxChars,
            ["refreshed"] = refreshed,
        };
    }

    private JsonObject SetElementValueByKeyboard(
        DisplayInfo display,
        UiaElementHandle handle,
        ElementRefEntry entry,
        string value,
        bool refreshed,
        string? mode,
        bool allowDestructive)
    {
        TrackedApp? app;

        lock (_gate)
        {
            app = _apps.FirstOrDefault(
                item => IsOwned(item) && item.Pid == entry.Pid && IsLiveTrackedApp(item));
        }

        if (app is null)
        {
            throw new ToolException(
                "E_NO_TARGET",
                "element_ref 所属应用不在本会话中，无法键入",
                new JsonObject { ["ref"] = entry.Ref });
        }

        RequireWindowOnDisplay(display, entry.Hwnd, "set_element_value");

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
        PauseFocusGuard();

        try
        {
            if (!EnsureForeground(app))
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
            TextWriteResult write = WriteTextVerified(
                app, handle, value, replace, timeoutMs: 12000);

            string? after = handle.GetText(65536);
            bool verified = write.Verified;

            var result = new JsonObject
            {
                ["ref"] = entry.Ref,
                ["method"] = "keyboard",
                ["mode"] = effectiveMode,
                ["destructive"] = replace,
                ["verified"] = verified,
                ["written"] = value,
                ["refreshed"] = refreshed,
                ["chunks"] = write.Chunks,
                ["duration_ms"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            };

            if (after is not null)
            {
                result["value"] = after;
                result["observed_length"] = after.Length;
            }

            if (!verified)
            {
                result["warning"] =
                    "键盘写入未能通过 TextPattern/ValuePattern 回读确认；请用 get_element_text 或截图复核";
            }

            return result;
        }
        finally
        {
            RestoreForeground(previous);
            ResumeFocusGuard();
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
            exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Word 正文的 Edit/Body 不支持 UIA SetFocus；退化到消息点击元素中心。
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
        using UiaElementHandle? focused = Uia.GetFocusedElementHandle(expected.Info.ProcessId);
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
    }

    /// <summary>写值后回读：runtime_id 可能已变化，失败时按指纹重定位再读。</summary>
    private string? ReadElementValue(ElementRefEntry entry)
    {
        UiaElementHandle? handle = null;

        try
        {
            if (!string.IsNullOrEmpty(entry.RuntimeId))
            {
                handle = Uia.FindByRuntimeId(entry.Hwnd, entry.RuntimeId);
            }

            handle ??= Uia.FindByFingerprint(
                entry.Hwnd, entry.Name, entry.AutomationId, entry.ControlTypeName,
                entry.Left, entry.Top, entry.Right, entry.Bottom);

            return handle is null ? null : handle.GetValue();
        }
        finally
        {
            handle?.Dispose();
        }
    }
}
