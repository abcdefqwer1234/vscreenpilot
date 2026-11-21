using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Mcp;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

internal sealed partial class VirtualDisplayController
{
    /// <summary>已存在的孤儿屏：优先按上次 state 接管；非 default profile 不抢别人的屏。</summary>
    private DisplayInfo? AdoptOrphanIfNeeded(
        DisplayInfo? existing, ref bool configured, ref bool displayAdded, ref string? selfHealReason)
    {
        if (existing is not null)
        {
            return existing;
        }

        DisplayInfo? attached = DisplayTopology.FindParsecDisplay();

        // 已经有其他 live profile 认领的屏不能接管；否则会抢别人的屏。
        if (attached is null || IsDisplayOwnedByOther(attached.DeviceName))
        {
            return existing;
        }

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
            return attached;
        }

        if (HasOtherLiveProfile())
        {
            return null;
        }

        if (!string.Equals(_profile, "default", StringComparison.Ordinal))
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

        return attached;
    }

    /// <summary>已有屏的索引/几何修复；索引失效时重新创建。</summary>
    private DisplayInfo? ReconcileExisting(
        ref DisplayInfo? existing, ref bool configured, ref bool displayAdded, ref string? selfHealReason)
    {
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
                    _log.Warn($"孤儿虚拟屏接管失败 ({adoptFailure})，尝试重建");
                    RemoveDisplayAfterEnsureFailure(displayAdded);
                    selfHealReason = "orphan_reclaimed";
                }
            }
            else
            {
                // 进程异常退出后驱动通常在 ~1s 自动回收；给一个短窗口再决定。
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

        // 显示索引仍归本会话，但拓扑丢失或几何被改（睡眠、驱动回收、用户改设置）。
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

        return existing;
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

}
