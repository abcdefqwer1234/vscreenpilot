using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

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
    internal readonly object _gate;
    internal readonly LogRing _log;
    internal readonly string _profile;
    internal readonly Func<bool> _isDisposed;
    internal readonly Action<DisplayInfo> _onReady;
    internal readonly Action _onRemoved;
    internal readonly Action _saveState;
    internal readonly int _width;
    internal readonly int _height;
    internal readonly int _refresh;

    internal readonly VirtualCreateOps CreateOps;
    internal readonly VirtualAdoptOps AdoptOps;
    internal readonly VirtualLifecycleOps LifecycleOps;

    public DisplayInfo? OwnDisplay() => AdoptOps.OwnDisplay();

    public bool TryRemove() => LifecycleOps.TryRemove();

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
        CreateOps = new VirtualCreateOps(this);
        AdoptOps = new VirtualAdoptOps(this);
        LifecycleOps = new VirtualLifecycleOps(this);
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

        // 快速路径也必须验证拓扑：外接摘屏后 Attached 可能仍是旧值，
        // 直接返回会让 capture_screen 在已消失的坐标上截到黑图。
        return display is { Attached: true } && Vdd.IsOpen && CreateOps.GeometryStillValid(display)
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

            DisplayInfo? current = TryReuseCurrent();

            if (current is not null)
            {
                return current;
            }

            string? selfHealReason = PrepareDriver();

            bool configured = false;
            bool displayAdded = false;
            DisplayInfo? existing = OwnDisplay();

            existing = AdoptOps.AdoptOrphanIfNeeded(
                existing, ref configured, ref displayAdded, ref selfHealReason);
            existing = AdoptOps.ReconcileExisting(
                ref existing, ref configured, ref displayAdded, ref selfHealReason);

            string? targetDevice = PrepareDisplay(existing, ref configured, ref displayAdded);
            EnsureMode(targetDevice, ref configured, displayAdded);
            DisplayInfo activated = RequireActivated(targetDevice, displayAdded);

            return Publish(activated, selfHealReason);
        }
    }

    private DisplayInfo? TryReuseCurrent()
    {
        if (State.VirtualDisplay is { Attached: true } current &&
            Vdd.IsOpen &&
            CreateOps.GeometryStillValid(current))
        {
            return current;
        }

        return null;
    }

    private string? PrepareDriver()
    {
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
        return selfHealReason;
    }

    private string? PrepareDisplay(DisplayInfo? existing, ref bool configured, ref bool displayAdded)
    {
        string? targetDevice = existing?.DeviceName;

        if (!configured && State.Index < 0)
        {
            CreateOps.CreateNewDisplay(ref targetDevice, ref displayAdded);
        }

        return targetDevice;
    }

    private void EnsureMode(string? targetDevice, ref bool configured, bool displayAdded)
    {
        if (configured)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(targetDevice))
        {
            LifecycleOps.RemoveDisplayAfterEnsureFailure(displayAdded);
            throw new ToolException("E_DISPLAY_ADD", "无法确定本次虚拟屏的 device");
        }

        configured = CreateOps.TryActivateDisplay(targetDevice, out string? failure);

        if (configured)
        {
            return;
        }

        LifecycleOps.RemoveDisplayAfterEnsureFailure(displayAdded);
        throw new ToolException(
            "E_DISPLAY_MODE",
            "虚拟显示器模式/坐标未稳定",
            new JsonObject { ["detail"] = failure });
    }

    private DisplayInfo RequireActivated(string? targetDevice, bool displayAdded)
    {
        DisplayInfo? activated = DisplayTopology.FindParsecDisplayByDevice(targetDevice);

        if (activated is not null)
        {
            return activated;
        }

        LifecycleOps.RemoveDisplayAfterEnsureFailure(displayAdded);
        throw new ToolException("E_DISPLAY_ADD", "虚拟显示器激活后消失");
    }

    private DisplayInfo Publish(DisplayInfo activated, string? selfHealReason)
    {
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

    /// <summary>
    /// 不同 profile 同时启动时串行化“ADD + 解析 device”，避免抢同一个 slot 或同一块新屏。
    /// 拿不到锁时返回 null（降级为不串行）；设备身份校验仍会防止认领错屏。
    /// </summary>
    internal Mutex? TryAcquireDisplayAllocation(out bool held)
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
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return null;
        }
    }

    internal void ReleaseDisplayAllocation(Mutex? allocation, bool held)
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
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
        finally
        {
            allocation.Dispose();
        }
    }

    internal void NoteSelfHeal(string reason)
    {
        State.SelfHealed = true;
        State.SelfHealCount++;
        State.LastSelfHealReason = reason;
        _log.Info($"自愈 [{reason}] 累计={State.SelfHealCount}");
    }

    internal (int X, int Y) ExpectedPosition()
    {
        DisplayInfo? primary = DisplayTopology.FindPrimary();

        if (primary is null)
        {
            return (State.VirtualDisplay?.X ?? 0, State.VirtualDisplay?.Y ?? 0);
        }

        // 多 profile 不能都放在主屏右侧同一点：默认 profile 偏移 0，
        // 其他 profile 用稳定哈希取 1..7 个屏宽，避免互相覆盖。
        int offset = CreateOps.DisplayOffset();

        return (primary.X + primary.Width + offset, primary.Y);
    }

    public void Forget()
    {
        State.Index = -1;
        State.DeviceName = string.Empty;
        State.VirtualDisplay = null;
    }

}
