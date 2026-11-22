using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Uia;

/// <summary>MSAA 树的 BFS/DFS 遍历状态，避免长参数列表。</summary>
internal sealed class MsaaWalker
{
    private readonly List<MsaaComOps.Node> _nodes = new();
    private readonly int _limit;
    private bool _truncated;

    public MsaaWalker(int limit) => _limit = limit;

    public IReadOnlyList<MsaaComOps.Node> Nodes => _nodes;

    public bool Truncated => _truncated;

    public void Walk(IAccessible parent, int depth)
    {
        if (_nodes.Count >= _limit || depth > MsaaService.MaxDepth)
        {
            _truncated = true;
            return;
        }

        int count = ReadChildCount(parent);

        if (count <= 0)
        {
            return;
        }

        if (count > MsaaService.MaxChildrenPerNode)
        {
            _truncated = true;
        }

        object[] children = ReadChildren(parent, count);

        for (int index = 0; index < children.Length && _nodes.Count < _limit; index++)
        {
            AddChild(parent, children[index], depth);
        }

        if (_nodes.Count >= _limit)
        {
            _truncated = true;
        }
    }

    private static int ReadChildCount(IAccessible parent)
    {
        try
        {
            return parent.ChildCount;
        }
        catch (COMException exception)
        {
            LogSink.Debug($"读取 MSAA 子节点数失败: {exception.Message}");
            return 0;
        }
    }

    private static object[] ReadChildren(IAccessible parent, int count)
    {
        var children = new object[Math.Min(count, MsaaService.MaxChildrenPerNode)];

        try
        {
            _ = MsaaComOps.AccessibleChildren(parent, 0, children.Length, children, out int obtained);
            return obtained < children.Length ? children[..obtained] : children;
        }
        catch (COMException exception)
        {
            LogSink.Debug($"读取 MSAA 子节点失败: {exception.Message}");
            return Array.Empty<object>();
        }
    }

    private void AddChild(IAccessible parent, object child, int depth)
    {
        try
        {
            bool self = child is IAccessible;
            int childId = self
                ? 0
                : Convert.ToInt32(child, System.Globalization.CultureInfo.InvariantCulture);
            int ordinal = _nodes.Count;
            _nodes.Add(new MsaaComOps.Node(parent, child, self, childId, ordinal));

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
}
