using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Mcp;

namespace VScreenPilot.Session;

/// <summary>显示状态：只存字段，不做逻辑；由 <see cref="VirtualDisplayController"/> 在会话锁内读写。</summary>
internal sealed class DisplayStateStore
{
    public DisplayInfo? VirtualDisplay { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public int Index { get; set; } = -1;
    public bool Initialized { get; set; }
    public bool PositionFallback { get; set; }
    public int RecreateCount { get; set; }
    public int VddReopenCount { get; set; }
    public int RecoveredIndex { get; set; } = -1;
    public string RecoveredDevice { get; set; } = string.Empty;
    public int RecoveredPid { get; set; }
    public long LastGeometryCheckTicks;
    public bool SelfHealed { get; set; }
    public int SelfHealCount { get; set; }
    public string? LastSelfHealReason { get; set; }
    public bool AdoptedOrphan { get; set; }
}

/// <summary>
/// 虚拟屏控制：VDD 打开/心跳、ADD/REMOVE、孤儿屏接管、几何自愈、位置回退。
/// 所有 public 入口都假定调用方已持有会话锁（与旧实现一致）。
/// </summary>
internal sealed class VirtualDisplayController
{
    private readonly object _gate;
    private readonly LogRing _log;
    private readonly string _profile;
    private readonly Func<bool> _isDisposed;
    private readonly Action<DisplayInfo> _onReady;
    private readonly Action _onRemoved;
    private readonly Action _saveState;
    private readonly int _width;
    private readonly int _height;
    private readonly int _refresh;

    public VirtualDisplayController(
        object gate,
        LogRing log,
        string profile,
        Func<bool> isDisposed,
        Action<DisplayInfo> onReady,
        Action onRemoved,
        Action saveState,
        int width,
        int height,
        int refresh)
    {
        _gate = gate;
        _log = log;
        _profile = profile;
        _isDisposed = isDisposed;
        _onReady = onReady;
        _onRemoved = onRemoved;
        _saveState = saveState;
        _width = width;
        _height = height;
        _refresh = refresh;
    }

    public VddDevice Vdd { get; } = new();

    public DisplayStateStore State { get; } = new();

    public DisplayInfo EnsureFast()
    {
        DisplayInfo? display = State.VirtualDisplay;

        return display is { Attached: true } && Vdd.IsOpen
            ? display
            : Ensure();
    }

    /// <summary>按需构造虚拟屏；幂等，必要时自愈。</summary>
    public DisplayInfo Ensure()
    {
        lock (_gate)
        {
            if (_isDisposed())
            {
                throw new ToolException("E_RELEASED", "会话已结束，无法重新建立虚拟屏");
            }

            if (State.VirtualDisplay is { Attached: true } current &&
                Vdd.IsOpen &&
                GeometryStillValid(current))
            {
                return current;
            }

            string? selfHealReason = null;

            VddDeviceStatus status = VddDevice.QueryStatus();
            if (status != VddDeviceStatus.Ok)
            {
                throw new ToolException("E_DRIVER", $"Parsec VDD 驱动状态异常: {status}");
            }

            if (Vdd.NeedsReopen)
            {
                _log.Warn(
                    $"VDD 心跳连续失败 ({Vdd.ConsecutiveUpdateFailures})，重新打开设备");
                Vdd.Dispose();
                State.VddReopenCount++;
                selfHealReason = "vdd_reopen";
            }

            if (!Vdd.TryOpen(out int openError))
            {
                throw new ToolException("E_DRIVER_OPEN", $"打开 VDD 设备失败 (Win32 {openError})");
            }

            Vdd.StartKeepAlive();

            bool configured = false;
            bool displayAdded = false;
            string? targetDevice = null;
            DisplayInfo? existing = OwnDisplay();

            if (existing is null)
            {
                DisplayInfo? attached = DisplayTopology.FindParsecDisplay();

                // 已经有其他 live profile 认领的屏不能接管；否则会抢别人的屏。
                if (attached is not null && !IsDisplayOwnedByOther(attached.DeviceName))
                {
                    bool matchesRecovered = !string.IsNullOrWhiteSpace(State.RecoveredDevice) &&
                                            string.Equals(
                                                attached.DeviceName,
                                                State.RecoveredDevice,
                                                StringComparison.OrdinalIgnoreCase);

                    // 不认领“来源不明”的屏；只有自己的崩溃 state 匹配才允许。
                    // 另一个 profile 正在启动（registry 已存在但 display_device 还没写）时，
                    // 不要抢它的屏，也不要直接失败，直接申请一块新的。
                    if (matchesRecovered)
                    {
                        existing = attached;
                    }
                    else if (HasOtherLiveProfile())
                    {
                        existing = null;
                    }
                    else if (!string.Equals(_profile, "default", StringComparison.Ordinal))
                    {
                        throw new ToolException(
                            "E_ORPHAN_DISPLAY",
                            "检测到未归属的虚拟显示器；非默认 profile 不自动接管，避免抢其他 profile 的屏",
                            new JsonObject
                            {
                                ["display"] = attached.ToString(),
                                ["profile"] = _profile,
                            },
                            retryable: true,
                            hint: "确认该屏无人使用后手动摘除，或使用默认 profile/独立 VM",
                            stage: "display");
                    }
                    else
                    {
                        existing = attached;
                    }
                }
            }

            if (existing is not null && State.Index < 0)
            {
                if (State.RecoveredIndex >= 0)
                {
                    _log.Warn(
                        $"检测到孤儿虚拟屏，按上次记录接管 " +
                        $"(index={State.RecoveredIndex}, device={State.RecoveredDevice}, " +
                        $"pid={State.RecoveredPid})");
                    State.Index = State.RecoveredIndex;
                    configured = TryActivateDisplay(existing.DeviceName, out string? adoptFailure);

                    if (configured)
                    {
                        State.DeviceName = existing.DeviceName;
                        State.AdoptedOrphan = true;
                        selfHealReason = "orphan_adopted";
                        _log.Info($"孤儿虚拟屏已接管: {existing}");
                    }
                    else
                    {
                        _log.Warn($"孤儿虚拟屏接管失败 ({adoptFailure})，尝试重置");
                        RemoveDisplayAfterEnsureFailure(displayAdded);
                        selfHealReason = "orphan_reclaimed";
                    }
                }
                else
                {
                    // 进程异常退出后驱动通常约 1s 自动回收；给一个短窗口再决定。
                    if (!DisplayTopology.WaitForNoParsecDisplay(TimeSpan.FromSeconds(2)))
                    {
                        throw new ToolException(
                            "E_ORPHAN_DISPLAY",
                            "检测到未归属的虚拟显示器，且无上次会话记录可供安全接管",
                            new JsonObject { ["display"] = existing.ToString() });
                    }

                    _log.Info("孤儿虚拟屏已被驱动自动回收");
                    existing = DisplayTopology.FindParsecDisplay();
                }

                // 一次会话只按上次记录接管一次，避免旧索引在后续重建时被误用。
                State.RecoveredIndex = -1;
                State.RecoveredDevice = string.Empty;
            }

            if (existing is not null)
            {
                targetDevice = existing.DeviceName;
            }

            // 显示索引仍归本会话，但拓扑丢失/几何被改（睡眠、驱动回收、用户改设置）。
            if (existing is not null && State.Index >= 0)
            {
                _log.Warn($"虚拟屏状态异常 ({existing})，尝试恢复");

                if (!configured)
                {
                    configured = TryActivateDisplay(existing.DeviceName, out string? failure);

                    if (!configured)
                    {
                        _log.Warn($"恢复失败 ({failure})，重建虚拟屏");
                        RemoveDisplayAfterEnsureFailure(displayAdded);
                        State.RecreateCount++;
                        selfHealReason = "display_recreated";
                    }
                    else if (selfHealReason is null)
                    {
                        selfHealReason = "geometry_changed";
                    }
                }
            }
            else if (existing is null && State.Index >= 0)
            {
                // 驱动已回收设备：索引失效，直接丢弃后重新创建，避免对旧索引反复重试。
                _log.Warn("虚拟屏已被驱动回收，重新创建");
                Forget();
                State.RecreateCount++;
                selfHealReason = "display_reclaimed";
            }

            if (!configured && State.Index < 0)
            {
                // ADD 与“解析新屏 device”必须整体串行：只锁 ADD 时，两个 profile 可能在
                // 解析阶段把同一块刚挂上的屏都认成自己的（后写 registry 的会覆盖前者）。
                Mutex? allocation = TryAcquireDisplayAllocation(out bool allocationHeld);

                try
                {
                    // Parsec 驱动在 REMOVE 后约 3-5 秒内会拒绝 ADD；重试并等待本次新变可用的屏。
                    // 不能用“设备名是否出现过”判断：驱动会复用已有 detached 适配器的名字（本机 DISPLAY5~20）。
                    HashSet<string> beforeLive = DisplayTopology.LiveParsecDevices();
                    // 记录 ADD 前已存在的全部 Parsec 设备名（含 detached）：
                    // 部分驱动/系统组合只会创建 detached monitor，必须由我们显式设模式才会挂载。
                    HashSet<string> beforeAll = DisplayTopology.List()
                        .Where(item => item.IsParsec)
                        .Select(item => item.DeviceName)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    int[] delays = { 0, 500, 1000, 1500, 2000 };

                    foreach (int delay in delays)
                    {
                        if (delay > 0)
                        {
                            Thread.Sleep(delay);
                        }

                        State.Index = Vdd.AddDisplay();

                        if (State.Index >= 0)
                        {
                            displayAdded = true;
                            break;
                        }

                        if (DisplayTopology.PickNewLiveParsec(
                                DisplayTopology.List(), beforeLive, IsDisplayOwnedByOther) is not null)
                        {
                            _log.Warn("驱动拒绝 ADD，但检测到本次新变可用的 Parsec 屏，按已创建处理");
                            break;
                        }

                        _log.Warn("驱动拒绝创建虚拟显示器，准备重试");
                    }

                    if (State.Index < 0 &&
                        DisplayTopology.PickNewLiveParsec(
                            DisplayTopology.List(), beforeLive, IsDisplayOwnedByOther) is null)
                    {
                        throw new ToolException(
                            "E_DISPLAY_ADD",
                            "驱动拒绝创建虚拟显示器",
                            new JsonObject
                            {
                                ["before_live"] = DescribeDevices(beforeLive),
                                ["after_live"] = DescribeDevices(DisplayTopology.LiveParsecDevices()),
                            },
                            retryable: true,
                            hint: "驱动 REMOVE 后需要 3-5 秒冷却；稍后重试或重启驱动",
                            stage: "display");
                    }

                    if (string.IsNullOrWhiteSpace(targetDevice))
                    {
                        bool liveArrived = DisplayTopology.WaitForNewLiveParsecDisplay(
                            beforeLive,
                            TimeSpan.FromSeconds(3),
                            out DisplayInfo? added,
                            IsDisplayOwnedByOther);

                        if ((!liveArrived || added is null) &&
                            DisplayTopology.List()
                                .FirstOrDefault(item =>
                                    item.IsParsec &&
                                    !beforeAll.Contains(item.DeviceName) &&
                                    !IsDisplayOwnedByOther(item.DeviceName)) is { } detachedNew)
                        {
                            // 驱动创建了 monitor 但 Windows 未自动挂载：直接对新建的
                            // detached 设备设模式，TryActivateDisplay 会完成挂载。
                            targetDevice = detachedNew.DeviceName;
                            _log.Info($"新建虚拟屏处于 detached，直接激活: {detachedNew.DeviceName}");
                        }

                        if (string.IsNullOrWhiteSpace(targetDevice) &&
                            (!liveArrived || added is null))
                        {
                            JsonObject details = new()
                            {
                                ["display_index"] = State.Index,
                                ["before_live"] = DescribeDevices(beforeLive),
                                ["after_live"] = DescribeDevices(DisplayTopology.LiveParsecDevices()),
                            };

                            _log.Warn($"新建虚拟屏未转可用: {details.ToJsonString()}");
                            RemoveDisplayAfterEnsureFailure(displayAdded);
                            throw new ToolException(
                                "E_DISPLAY_ADD",
                                "新建的虚拟显示器未出现在拓扑中",
                                details,
                                retryable: true,
                                hint: "确认 Parsec VDD 正常；detached 适配器不算已存在，若仍失败请提供 details",
                                stage: "display");
                        }

                        if (liveArrived && added is not null)
                        {
                            targetDevice = added.DeviceName;
                            _log.Info($"新建虚拟屏已可用: {added}");
                        }
                    }
                }
                finally
                {
                    ReleaseDisplayAllocation(allocation, allocationHeld);
                }
            }

            if (!configured)
            {
                if (string.IsNullOrWhiteSpace(targetDevice))
                {
                    RemoveDisplayAfterEnsureFailure(displayAdded);
                    throw new ToolException("E_DISPLAY_ADD", "无法确定本次虚拟屏的 device");
                }

                configured = TryActivateDisplay(targetDevice, out string? failure);

                if (!configured)
                {
                    RemoveDisplayAfterEnsureFailure(displayAdded);
                    throw new ToolException(
                        "E_DISPLAY_MODE",
                        "虚拟显示器模式/坐标未稳定",
                        new JsonObject { ["detail"] = failure });
                }
            }

            DisplayInfo? activated = DisplayTopology.FindParsecDisplayByDevice(targetDevice);
            if (activated is null)
            {
                RemoveDisplayAfterEnsureFailure(displayAdded);
                throw new ToolException("E_DISPLAY_ADD", "虚拟显示器激活后消失");
            }

            State.VirtualDisplay = activated;
            State.DeviceName = activated.DeviceName;
            State.Initialized = true;

            if (selfHealReason is not null)
            {
                NoteSelfHeal(selfHealReason);
            }

            _onReady(activated);
            _log.Info($"环境就绪: {activated}");
            _saveState();
            return activated;
        }
    }

    /// <summary>
    /// 不同 profile 同时启动时串行化“ADD + 解析 device”，避免抢同一个 slot 或同一块新屏。
    /// 拿不到锁时返回 null（降级为不串行）；设备身份校验仍会防止认领错屏。
    /// </summary>
    private static Mutex? TryAcquireDisplayAllocation(out bool held)
    {
        held = false;

        try
        {
            var allocation = new Mutex(false, @"Local\VScreenPilot_DisplayAlloc");

            try
            {
                held = allocation.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }

            return allocation;
        }
        catch
        {
            return null;
        }
    }

    private static void ReleaseDisplayAllocation(Mutex? allocation, bool held)
    {
        if (allocation is null)
        {
            return;
        }

        try
        {
            if (held)
            {
                allocation.ReleaseMutex();
            }
        }
        catch (Exception exception)
        {
            // 释放失败不应覆盖原始错误。
            _ = exception;
        }
        finally
        {
            allocation.Dispose();
        }
    }

    private void NoteSelfHeal(string reason)
    {
        State.SelfHealed = true;
        State.SelfHealCount++;
        State.LastSelfHealReason = reason;
        _log.Info($"自愈 [{reason}] 累计={State.SelfHealCount}");
    }

    private bool GeometryStillValid(DisplayInfo current)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref State.LastGeometryCheckTicks) < 1000)
        {
            return true;
        }

        Interlocked.Exchange(ref State.LastGeometryCheckTicks, now);

        DisplayInfo? actual = OwnDisplay();
        if (actual is null)
        {
            _log.Warn("虚拟屏已从拓扑中消失，准备重建");
            return false;
        }

        (int x, int y) = ExpectedPosition();
        bool matches = actual.Width == _width &&
                       actual.Height == _height &&
                       actual.X == x &&
                       actual.Y == y;

        if (matches)
        {
            State.VirtualDisplay = actual;
        }
        else
        {
            _log.Warn($"虚拟屏几何被改变: {actual}，期望 {_width}x{_height} at ({x},{y})");
        }

        return matches;
    }

    private (int X, int Y) ExpectedPosition()
    {
        DisplayInfo? primary = DisplayTopology.FindPrimary();

        if (primary is null)
        {
            return (State.VirtualDisplay?.X ?? 0, State.VirtualDisplay?.Y ?? 0);
        }

        // 多 profile 不能都放在主屏右侧同一点：默认 profile 偏移 0，
        // 其他 profile 用稳定哈希取 1..7 个屏宽，避免互相覆盖。
        int offset = DisplayOffset();

        return (primary.X + primary.Width + offset, primary.Y);
    }

    private int DisplayOffset()
    {
        string? raw = Environment.GetEnvironmentVariable("VSP_DISPLAY_OFFSET");

        if (int.TryParse(raw, out int configured))
        {
            return Math.Clamp(configured, 0, 20000);
        }

        // 选择“第一个真正空闲的槽位”，避免第 8 个 profile 与前面的重叠。
        // 计算时排除本会话自己的 Parsec 屏，否则每次启动都会把上次的位置当成占用而漂移。
        DisplayInfo? own = OwnDisplay();
        var occupied = new HashSet<int>();

        foreach (DisplayInfo display in DisplayTopology.List())
        {
            if (own is not null &&
                string.Equals(display.DeviceName, own.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            occupied.Add(display.X);
        }

        DisplayInfo? primary = DisplayTopology.FindPrimary();
        int originX = primary is null ? 0 : primary.X + primary.Width;
        int preferredSlot = 0;

        if (!string.Equals(_profile, "default", StringComparison.Ordinal))
        {
            int hash = 17;

            foreach (char ch in _profile)
            {
                hash = unchecked(hash * 31 + ch) & 0x7FFFFFFF;
            }

            preferredSlot = hash % 8;
        }

        for (int step = 0; step < 8; step++)
        {
            int slot = (preferredSlot + step) % 8;

            if (!occupied.Contains(originX + slot * _width))
            {
                return slot * _width;
            }
        }

        return preferredSlot * _width;
    }

    private bool TryActivateDisplay(string deviceName, out string? failure)
    {
        failure = null;
        State.PositionFallback = false;
        (int x, int y) = ExpectedPosition();

        int setResult = DisplayTopology.SetModeAndPosition(
            deviceName, _width, _height, _refresh, x, y, out int applyResult);

        if (!DisplayTopology.WaitUntil(
                () => DisplayTopology.FindParsecDisplayByDevice(deviceName) is { Attached: true },
                TimeSpan.FromSeconds(2)))
        {
            int extendResult = DisplayTopology.ExtendDesktop();
            _log.Warn($"SetDisplayConfig(EXTEND)={extendResult}，重试设置虚拟屏模式");
            setResult = DisplayTopology.SetModeAndPosition(
                deviceName, _width, _height, _refresh, x, y, out applyResult);
        }

        bool settled = DisplayTopology.WaitUntil(
            () =>
            {
                DisplayInfo? display = DisplayTopology.FindParsecDisplayByDevice(deviceName);
                return display is not null &&
                       display.Width == _width &&
                       display.Height == _height &&
                       display.X == x &&
                       display.Y == y;
            },
            TimeSpan.FromSeconds(3));

        if (!settled)
        {
            DisplayInfo? current = DisplayTopology.FindParsecDisplayByDevice(deviceName);

            // Windows/VDD 可能忽略 dmPosition，把新屏固定在主屏右侧。
            // 只要分辨率正确且已挂载，就接受实际坐标（否则整个 profile 直接不可用）。
            if (current is { Attached: true } &&
                current.Width == _width &&
                current.Height == _height)
            {
                State.PositionFallback = true;
                failure =
                    $"position_not_applied expected=({x},{y}) actual=({current.X},{current.Y})";
                _log.Warn($"虚拟屏位置未生效，接受实际坐标 ({current.X},{current.Y})");
                return true;
            }

            failure = $"set={setResult} apply={applyResult} current={current?.ToString() ?? "not found"}";
        }

        return settled;
    }

    public void Forget()
    {
        State.Index = -1;
        State.DeviceName = string.Empty;
        State.VirtualDisplay = null;
    }

    public DisplayInfo? OwnDisplay()
    {
        DisplayInfo? byDevice = DisplayTopology.FindParsecDisplayByDevice(State.DeviceName);

        if (byDevice is not null)
        {
            return byDevice;
        }

        return State.VirtualDisplay is { Attached: true }
            ? DisplayTopology.FindParsecDisplayByDevice(State.VirtualDisplay.DeviceName)
            : null;
    }

    private static bool HasOtherLiveProfile() =>
        InstanceRegistry.ListAll().Any(entry => entry.Pid != Environment.ProcessId);

    private static bool IsDisplayOwnedByOther(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return false;
        }

        return InstanceRegistry.ListAll()
            .Concat(InstanceRegistry.ListStateEntries())
            .Any(entry =>
                entry.Pid != Environment.ProcessId &&
                string.Equals(entry.DisplayDevice, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把设备名转成稳定排序的 JSON 数组，便于日志和错误 details。</summary>
    private static JsonArray DescribeDevices(IEnumerable<string> devices)
    {
        var array = new JsonArray();
        foreach (string device in devices.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            array.Add(device);
        }

        return array;
    }

    /// <summary>
    /// EnsureEnvironment 失败时：若是本次刚 ADD 的屏，index 一定属于本会话，可直接回收；
    /// 否则走带 device 校验的 TryRemoveDisplay，避免误摘其他 profile。
    /// </summary>
    private void RemoveDisplayAfterEnsureFailure(bool displayAdded)
    {
        if (displayAdded && State.Index >= 0)
        {
            try
            {
                Vdd.RemoveDisplay(State.Index);
            }
            catch (Exception exception)
            {
                _log.Warn($"回收本次新建虚拟屏失败: {exception.Message}");
            }

            State.Index = -1;
            State.DeviceName = string.Empty;
            State.VirtualDisplay = null;
            return;
        }

        TryRemove();
    }

    public bool TryRemove()
    {
        _onRemoved();

        bool removed = false;
        string device = State.DeviceName;

        if (State.Index >= 0 && Vdd.IsOpen)
        {
            // index 可能被驱动复用；摘除前必须确认它仍对应本会话的 device。
            DisplayInfo? own = string.IsNullOrWhiteSpace(device)
                ? null
                : DisplayTopology.FindParsecDisplayByDevice(device);

            if (string.IsNullOrWhiteSpace(device))
            {
                // 没有 device 身份时不能按 index 猜；否则可能摘掉其他 profile 的屏。
                _log.Warn($"没有本会话 device 记录，跳过按 index={State.Index} 摘除");
            }
            else if (own is null)
            {
                _log.Warn($"虚拟屏 {device} 已不在拓扑中，跳过按 index 摘除");
            }
            else
            {
                removed = Vdd.RemoveDisplay(State.Index);
                if (!removed)
                {
                    _log.Warn($"摘除虚拟屏失败 (index={State.Index})，等待驱动回收");
                }

                DisplayTopology.WaitForDisplayGone(device, TimeSpan.FromSeconds(3));
            }
        }

        State.Index = -1;
        State.DeviceName = string.Empty;
        State.VirtualDisplay = null;
        return removed;
    }
}
