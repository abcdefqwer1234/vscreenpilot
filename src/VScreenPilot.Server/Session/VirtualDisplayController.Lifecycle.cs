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
