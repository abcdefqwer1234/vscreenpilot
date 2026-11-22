using System.Runtime.InteropServices;
using VScreenPilot.Interop;
using VScreenPilot.Uia;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Uia;

internal sealed record MsaaFindResult(List<UiaElementInfo> Items, bool Truncated);

/// <summary>
/// MSAA（IAccessible）第二语义后端：Office 旧对话框（bosa_sdm_msword）、
/// 旧 Win32 控件不暴露 UIA 时用来兜底。
///
/// ref 形如 <c>msaa:{hwnd:X}:{ordinal}</c>，ordinal 是一次深度优先遍历中的序号。
/// 这样嵌套 IAccessible 子项也有唯一标识，不会全部退化成 childId=0。
/// </summary>
internal static class MsaaService
{
    private const int ObjidWindow = 0;
    private const int MaxDepth = 4;
    private const int MaxChildrenPerNode = 500;
    private const int CallTimeoutMs = 2000;
    private static readonly Guid IidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr hwnd, uint idObject, ref Guid riid, out IntPtr ppvObject);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren(
        IAccessible container, int start, int count, [Out] object[] children, out int obtained);

    public static bool IsMsaaRef(string? runtimeId) =>
        runtimeId is not null && runtimeId.StartsWith("msaa:", StringComparison.Ordinal);

    public static bool TryParse(string? runtimeId, out IntPtr hwnd, out int ordinal)
    {
        hwnd = IntPtr.Zero;
        ordinal = 0;

        if (!IsMsaaRef(runtimeId))
        {
            return false;
        }

        string[] parts = runtimeId!.Split(':');

        if (parts.Length != 3 ||
            !long.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out long handle) ||
            !int.TryParse(parts[2], out ordinal))
        {
            return false;
        }

        hwnd = new IntPtr(handle);
        return true;
    }

    private static string MakeRef(IntPtr hwnd, int ordinal) =>
        $"msaa:{hwnd.ToInt64():X}:{ordinal}";

    public static MsaaFindResult FindElements(
        IntPtr hwnd, string? name, UiaNameMatch nameMatch, string? controlType, int limit)
    {
        return RunWithTimeout(
            () => FindCore(hwnd, name, nameMatch, controlType, limit),
            new MsaaFindResult(new List<UiaElementInfo>(), false));
    }

    public static bool DoDefaultAction(IntPtr hwnd, int ordinal) =>
        RunWithTimeout(() => InvokeAction(hwnd, ordinal), false);

    public static string? GetValue(IntPtr hwnd, int ordinal) =>
        RunWithTimeout(() => ReadValue(hwnd, ordinal), null);

    public static bool SetValue(IntPtr hwnd, int ordinal, string value) =>
        RunWithTimeout(() => WriteValue(hwnd, ordinal, value), false);

    public static (int Left, int Top, int Width, int Height)? GetBounds(IntPtr hwnd, int ordinal) =>
        RunWithTimeout(() => ReadBounds(hwnd, ordinal), null);

    private static MsaaFindResult FindCore(
        IntPtr hwnd, string? name, UiaNameMatch nameMatch, string? controlType, int limit)
    {
        var output = new List<UiaElementInfo>();
        IAccessible? root = Open(hwnd);

        if (root is null)
        {
            return new MsaaFindResult(output, false);
        }

        List<Node> nodes = new();

        try
        {
            nodes = Traverse(root, Math.Clamp(limit, 1, 2000), out bool truncated);
            int processId = WindowPid(hwnd);

            foreach (Node node in nodes)
            {
                if (output.Count >= limit)
                {
                    break;
                }

                UiaElementInfo? info;

                try
                {
                    info = Describe(node, hwnd, processId);
                }
                catch (Exception exception) when (
                    exception is COMException or InvalidCastException or ArgumentException)
                {
                    continue;
                }

                if (info is not null &&
                    Matches(info, name, nameMatch) &&
                    (controlType is null ||
                     info.ControlTypeName.Equals(controlType, StringComparison.OrdinalIgnoreCase)))
                {
                    output.Add(info);
                }
            }

            return new MsaaFindResult(output, truncated);
        }
        finally
        {
            ReleaseChildren(nodes);
            Release(root);
        }
    }

    private static bool InvokeAction(IntPtr hwnd, int ordinal)
    {
        IAccessible? root = Open(hwnd);

        if (root is null)
        {
            return false;
        }

        List<Node> nodes = new();

        try
        {
            nodes = Traverse(root, ordinal + 1, out _);

            if (ordinal < 0 || ordinal >= nodes.Count)
            {
                return false;
            }

            Node node = nodes[ordinal];
            IAccessible source = node.Self ? (IAccessible)node.Child : node.Parent;
            object id = node.Self ? 0 : node.ChildId;
            source.DoDefaultAction(id);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            ReleaseChildren(nodes);
            Release(root);
        }
    }

    private static string? ReadValue(IntPtr hwnd, int ordinal)
    {
        IAccessible? root = Open(hwnd);

        if (root is null)
        {
            return null;
        }

        List<Node> nodes = new();

        try
        {
            nodes = Traverse(root, ordinal + 1, out _);

            if (ordinal < 0 || ordinal >= nodes.Count)
            {
                return null;
            }

            Node node = nodes[ordinal];
            IAccessible source = node.Self ? (IAccessible)node.Child : node.Parent;
            object id = node.Self ? 0 : node.ChildId;
            string value = source.GetValue(id) ?? string.Empty;
            return value.Length > 0 ? value : source.GetName(id);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            ReleaseChildren(nodes);
            Release(root);
        }
    }

    private static bool WriteValue(IntPtr hwnd, int ordinal, string value)
    {
        IAccessible? root = Open(hwnd);

        if (root is null)
        {
            return false;
        }

        List<Node> nodes = new();

        try
        {
            nodes = Traverse(root, ordinal + 1, out _);

            if (ordinal < 0 || ordinal >= nodes.Count)
            {
                return false;
            }

            Node node = nodes[ordinal];
            IAccessible source = node.Self ? (IAccessible)node.Child : node.Parent;
            object id = node.Self ? 0 : node.ChildId;
            source.SetValue(id, value);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            ReleaseChildren(nodes);
            Release(root);
        }
    }

    private static (int Left, int Top, int Width, int Height)? ReadBounds(IntPtr hwnd, int ordinal)
    {
        IAccessible? root = Open(hwnd);

        if (root is null)
        {
            return null;
        }

        List<Node> nodes = new();

        try
        {
            nodes = Traverse(root, ordinal + 1, out _);

            if (ordinal < 0 || ordinal >= nodes.Count)
            {
                return null;
            }

            Node node = nodes[ordinal];
            IAccessible source = node.Self ? (IAccessible)node.Child : node.Parent;
            object id = node.Self ? 0 : node.ChildId;
            source.GetLocation(out int left, out int top, out int width, out int height, id);
            return (left, top, width, height);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            ReleaseChildren(nodes);
            Release(root);
        }
    }

    private sealed record Node(IAccessible Parent, object Child, bool Self, int ChildId, int Ordinal);

    private static List<Node> Traverse(IAccessible root, int limit, out bool truncated)
    {
        var nodes = new List<Node>();
        bool localTruncated = false;

        void Walk(IAccessible parent, int depth)
        {
            if (nodes.Count >= limit || depth > MaxDepth)
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

            if (count > MaxChildrenPerNode)
            {
                localTruncated = true;
            }

            var children = new object[Math.Min(count, MaxChildrenPerNode)];
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

    private static UiaElementInfo? Describe(Node node, IntPtr hwnd, int processId)
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
            MakeRef(hwnd, node.Ordinal),
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

    private static string RoleName(int role) => role switch
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

    private static bool Matches(UiaElementInfo info, string? name, UiaNameMatch mode)
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

    private static bool MatchRegex(string value, string pattern)
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

    private static IAccessible? Open(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        Guid iid = IidIAccessible;
        int hr = AccessibleObjectFromWindow(hwnd, ObjidWindow, ref iid, out IntPtr pointer);

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

    private static void ReleaseChildren(List<Node> nodes)
    {
        foreach (Node node in nodes)
        {
            if (node.Self)
            {
                Release(node.Child);
            }
        }
    }

    private static void Release(object? instance)
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

    private static int WindowPid(IntPtr hwnd)
    {
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    private static string? Safe(Func<string?> read)
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

    private static object? SafeObject(Func<object> read)
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

    private static T RunWithTimeout<T>(Func<T> work, T fallback)
    {
        try
        {
            Task<T> task = Task.Run(work);

            if (!task.Wait(CallTimeoutMs))
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

[ComImport]
[Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IAccessible
{
    [DispId(-5001)]
    int ChildCount { get; }

    [DispId(-5003)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetName([In, MarshalAs(UnmanagedType.Struct)] object child);

    [DispId(-5004)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetValue([In, MarshalAs(UnmanagedType.Struct)] object child);

    [DispId(-5006)]
    [return: MarshalAs(UnmanagedType.Struct)]
    object GetRole([In, MarshalAs(UnmanagedType.Struct)] object child);

    [DispId(-5007)]
    [return: MarshalAs(UnmanagedType.Struct)]
    object GetState([In, MarshalAs(UnmanagedType.Struct)] object child);

    [DispId(-5015)]
    void GetLocation(
        out int left, out int top, out int width, out int height,
        [In, MarshalAs(UnmanagedType.Struct)] object child);

    [DispId(-5018)]
    void DoDefaultAction([In, MarshalAs(UnmanagedType.Struct)] object child);

    [DispId(-5004)]
    void SetValue(
        [In, MarshalAs(UnmanagedType.Struct)] object child,
        [In, MarshalAs(UnmanagedType.BStr)] string value);
}
