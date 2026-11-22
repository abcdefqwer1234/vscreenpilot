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
    internal const int ObjidWindow = 0;
    internal const int MaxDepth = 4;
    internal const int MaxChildrenPerNode = 500;
    internal const int CallTimeoutMs = 2000;
    internal static readonly Guid IidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

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

    internal static string MakeRef(IntPtr hwnd, int ordinal) =>
        $"msaa:{hwnd.ToInt64():X}:{ordinal}";

    public static MsaaFindResult FindElements(
        IntPtr hwnd, string? name, UiaNameMatch nameMatch, string? controlType, int limit)
    {
        return MsaaComOps.RunWithTimeout(
            () => FindCore(hwnd, name, nameMatch, controlType, limit),
            new MsaaFindResult(new List<UiaElementInfo>(), false));
    }

    public static bool DoDefaultAction(IntPtr hwnd, int ordinal) =>
        MsaaComOps.RunWithTimeout(() => InvokeAction(hwnd, ordinal), false);

    public static string? GetValue(IntPtr hwnd, int ordinal) =>
        MsaaComOps.RunWithTimeout(() => ReadValue(hwnd, ordinal), null);

    public static bool SetValue(IntPtr hwnd, int ordinal, string value) =>
        MsaaComOps.RunWithTimeout(() => WriteValue(hwnd, ordinal, value), false);

    public static (int Left, int Top, int Width, int Height)? GetBounds(IntPtr hwnd, int ordinal) =>
        MsaaComOps.RunWithTimeout(() => ReadBounds(hwnd, ordinal), null);

    private static MsaaFindResult FindCore(
        IntPtr hwnd, string? name, UiaNameMatch nameMatch, string? controlType, int limit)
    {
        var output = new List<UiaElementInfo>();
        IAccessible? root = MsaaComOps.Open(hwnd);

        if (root is null)
        {
            return new MsaaFindResult(output, false);
        }

        List<MsaaComOps.Node> nodes = new();

        try
        {
            nodes = MsaaComOps.Traverse(root, Math.Clamp(limit, 1, 2000), out bool truncated);
            int processId = MsaaComOps.WindowPid(hwnd);

            foreach (MsaaComOps.Node node in nodes)
            {
                if (output.Count >= limit)
                {
                    break;
                }

                UiaElementInfo? info;

                try
                {
                    info = MsaaComOps.Describe(node, hwnd, processId);
                }
                catch (Exception exception) when (
                    exception is COMException or InvalidCastException or ArgumentException)
                {
                    continue;
                }

                if (info is not null &&
                    MsaaComOps.Matches(info, name, nameMatch) &&
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
            MsaaComOps.ReleaseChildren(nodes);
            MsaaComOps.Release(root);
        }
    }

    private static bool InvokeAction(IntPtr hwnd, int ordinal)
    {
        IAccessible? root = MsaaComOps.Open(hwnd);

        if (root is null)
        {
            return false;
        }

        List<MsaaComOps.Node> nodes = new();

        try
        {
            nodes = MsaaComOps.Traverse(root, ordinal + 1, out _);

            if (ordinal < 0 || ordinal >= nodes.Count)
            {
                return false;
            }

            MsaaComOps.Node node = nodes[ordinal];
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
            MsaaComOps.ReleaseChildren(nodes);
            MsaaComOps.Release(root);
        }
    }

    private static string? ReadValue(IntPtr hwnd, int ordinal)
    {
        IAccessible? root = MsaaComOps.Open(hwnd);

        if (root is null)
        {
            return null;
        }

        List<MsaaComOps.Node> nodes = new();

        try
        {
            nodes = MsaaComOps.Traverse(root, ordinal + 1, out _);

            if (ordinal < 0 || ordinal >= nodes.Count)
            {
                return null;
            }

            MsaaComOps.Node node = nodes[ordinal];
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
            MsaaComOps.ReleaseChildren(nodes);
            MsaaComOps.Release(root);
        }
    }

    private static bool WriteValue(IntPtr hwnd, int ordinal, string value)
    {
        IAccessible? root = MsaaComOps.Open(hwnd);

        if (root is null)
        {
            return false;
        }

        List<MsaaComOps.Node> nodes = new();

        try
        {
            nodes = MsaaComOps.Traverse(root, ordinal + 1, out _);

            if (ordinal < 0 || ordinal >= nodes.Count)
            {
                return false;
            }

            MsaaComOps.Node node = nodes[ordinal];
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
            MsaaComOps.ReleaseChildren(nodes);
            MsaaComOps.Release(root);
        }
    }

    private static (int Left, int Top, int Width, int Height)? ReadBounds(IntPtr hwnd, int ordinal)
    {
        IAccessible? root = MsaaComOps.Open(hwnd);

        if (root is null)
        {
            return null;
        }

        List<MsaaComOps.Node> nodes = new();

        try
        {
            nodes = MsaaComOps.Traverse(root, ordinal + 1, out _);

            if (ordinal < 0 || ordinal >= nodes.Count)
            {
                return null;
            }

            MsaaComOps.Node node = nodes[ordinal];
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
            MsaaComOps.ReleaseChildren(nodes);
            MsaaComOps.Release(root);
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
