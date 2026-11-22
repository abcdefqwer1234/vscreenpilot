using System.Runtime.InteropServices;
using VScreenPilot.Interop;
using VScreenPilot.Uia;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Uia;

/// <summary>MSAA COM 访问与树遍历；从 MsaaService 拆出。</summary>
internal static class MsaaComOps
{
    [DllImport("oleacc.dll")]
    internal static extern int AccessibleObjectFromWindow(
        IntPtr hwnd, uint idObject, ref Guid riid, out IntPtr ppvObject);


    [DllImport("oleacc.dll")]
    internal static extern int AccessibleChildren(
        IAccessible container, int start, int count, [Out] object[] children, out int obtained);


    internal sealed record Node(IAccessible Parent, object Child, bool Self, int ChildId, int Ordinal);


    internal static List<Node> Traverse(IAccessible root, int limit, out bool truncated)
    {
        var nodes = new List<Node>();
        bool localTruncated = false;

        void Walk(IAccessible parent, int depth)
        {
            if (nodes.Count >= limit || depth > MsaaService.MaxDepth)
            {
                localTruncated = true;
                return;
            }

            int count;

            try
            {
                count = parent.ChildCount;
            }
            catch (COMException)
            {
                return;
            }

            if (count <= 0)
            {
                return;
            }

            if (count > MsaaService.MaxChildrenPerNode)
            {
                localTruncated = true;
            }

            var children = new object[Math.Min(count, MsaaService.MaxChildrenPerNode)];
            int obtained = 0;

            try
            {
                _ = AccessibleChildren(parent, 0, children.Length, children, out obtained);
            }
            catch (COMException)
            {
                return;
            }

            if (obtained < count)
            {
                localTruncated = true;
            }

            for (int index = 0; index < obtained && nodes.Count < limit; index++)
            {
                object child = children[index];

                try
                {
                    bool self = child is IAccessible;
                    int childId = self ? 0 : Convert.ToInt32(child, System.Globalization.CultureInfo.InvariantCulture);
                    int ordinal = nodes.Count;
                    nodes.Add(new Node(parent, child, self, childId, ordinal));

                    if (self)
                    {
                        Walk((IAccessible)child, depth + 1);
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidCastException or FormatException or OverflowException)
                {
                    // 非 child-id / IAccessible 的 VARIANT：跳过，不影响其它控件。
                    LogSink.Debug($"忽略异常: {exception.Message}");
                }
            }

            if (nodes.Count >= limit)
            {
                localTruncated = true;
            }
        }

        Walk(root, 0);
        truncated = localTruncated;
        return nodes;
    }

    internal static UiaElementInfo? Describe(Node node, IntPtr hwnd, int processId)
    {
        IAccessible source = node.Self ? (IAccessible)node.Child : node.Parent;
        object id = node.Self ? 0 : node.ChildId;

        string name = Safe(() => source.GetName(id)) ?? string.Empty;
        string value = Safe(() => source.GetValue(id)) ?? string.Empty;
        object? role = SafeObject(() => source.GetRole(id));
        object? state = SafeObject(() => source.GetState(id));
        int roleId = role is int idValue ? idValue : -1;
        string roleName = role is string text ? text : RoleName(roleId);
        int stateBits = state is int bits ? bits : 0;
        bool enabled = (stateBits & 0x1) == 0;
        bool offscreen = (stateBits & 0x10000) != 0 || (stateBits & 0x8000) != 0;

        source.GetLocation(out int left, out int top, out int width, out int height, id);

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        // ordinal 就是 nodes 中的序号；MakeRef 的第三段必须与遍历顺序一致。
        return new UiaElementInfo(
            MsaaService.MakeRef(hwnd, node.Ordinal),
            name,
            string.Empty,
            roleId,
            roleName,
            left,
            top,
            left + width,
            top + height,
            enabled,
            offscreen,
            processId,
            "MSAA",
            Focusable: false,
            Focused: false,
            Password: false,
            Value: value);
    }

    internal static string RoleName(int role) => role switch
    {
        43 => "Button",
        42 => "Edit",
        41 => "Text",
        44 => "CheckBox",
        45 => "RadioButton",
        46 => "ComboBox",
        33 => "List",
        34 => "ListItem",
        12 => "MenuItem",
        18 => "Dialog",
        _ => $"Msaa{role}",
    };

    internal static bool Matches(UiaElementInfo info, string? name, UiaNameMatch mode)
    {
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        return mode switch
        {
            UiaNameMatch.Contains => info.Name.Contains(name, StringComparison.OrdinalIgnoreCase),
            UiaNameMatch.Regex => MatchRegex(info.Name, name),
            _ => string.Equals(info.Name, name, StringComparison.Ordinal),
        };
    }

    internal static bool MatchRegex(string value, string pattern)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                value, pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static IAccessible? Open(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        Guid iid = MsaaService.IidIAccessible;
        int hr = AccessibleObjectFromWindow(hwnd, MsaaService.ObjidWindow, ref iid, out IntPtr pointer);

        if (hr != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.GetObjectForIUnknown(pointer) as IAccessible;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    internal static void ReleaseChildren(List<Node> nodes)
    {
        foreach (Node node in nodes)
        {
            if (node.Self)
            {
                Release(node.Child);
            }
        }
    }

    internal static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            try
            {
                Marshal.ReleaseComObject(instance);
            }
            catch (Exception exception)
            {
                // 已显式忽略：尽力而为的清理/探测失败不影响主流程。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }
        }
    }

    internal static int WindowPid(IntPtr hwnd)
    {
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    internal static string? Safe(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            return null;
        }
    }

    internal static object? SafeObject(Func<object> read)
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            return null;
        }
    }

    internal static T RunWithTimeout<T>(Func<T> work, T fallback)
    {
        try
        {
            Task<T> task = Task.Run(work);

            if (!task.Wait(MsaaService.CallTimeoutMs))
            {
                // COM 调用无法取消；放弃等待，避免整个 MCP 循环被一个旧 provider 拖死。
                return fallback;
            }

            return task.Result;
        }
        catch (AggregateException)
        {
            return fallback;
        }
    }
}
