using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

internal sealed partial class VirtualDisplayController
{
    /// <summary>ADD 与“解析新 device”必须整体串行，避免两个 profile 把同一块新屏都认成自己的。</summary>
    private void CreateNewDisplay(ref string? targetDevice, ref bool displayAdded)
    {
        Mutex? allocation = TryAcquireDisplayAllocation(out bool allocationHeld);

        try
        {
            // Parsec 驱动在 REMOVE 后约 3-5 秒内会拒绝 ADD；重试并等待本次新变可用的屏。
            // 不能用“设备名是否出现过”判断：驱动会复用已 detached 适配器的名字（本次 DISPLAY5~20）。
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

            if (!string.IsNullOrWhiteSpace(targetDevice))
            {
                return;
            }

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
                // 驱动创建的 monitor 未被 Windows 自动挂载：直接对新建的
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
        finally
        {
            ReleaseDisplayAllocation(allocation, allocationHeld);
        }
    }


    private bool GeometryStillValid(DisplayInfo current)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref State.LastGeometryCheckTicks) < 1000)
        {
            return true;
        }

        DisplayInfo? actual = OwnDisplay();
        if (actual is null)
        {
            _log.Warn("虚拟屏已从拓扑中消失，准备重建");
            return false;
        }

        (int x, int y) = ExpectedPosition();
        // Windows/VDD 可能拒绝移动位置，此时已置 PositionFallback；再拿“期望位置”
        // 比较会让每次快速调用都走完整自愈。回退状态下只校验尺寸与设备存在。
        bool positionOk = State.PositionFallback || (actual.X == x && actual.Y == y);
        bool matches = actual.Width == _width &&
                       actual.Height == _height &&
                       positionOk;

        if (matches)
        {
            // 只缓存成功结果；失败时下一次调用立即重试，避免把“已摘屏”缓存成 1 秒有效。
            Interlocked.Exchange(ref State.LastGeometryCheckTicks, now);
            State.VirtualDisplay = actual;
        }
        else
        {
            _log.Warn($"虚拟屏几何被改变: {actual}，期望 {_width}x{_height} at ({x},{y})");
        }

        return matches;
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

}
