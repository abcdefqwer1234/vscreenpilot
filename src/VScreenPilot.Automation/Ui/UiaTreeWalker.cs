using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using VScreenPilot.Interop;

namespace VScreenPilot.Uia;

internal sealed partial class UiaService
{
    public UiaTreeResult BuildTree(
        IntPtr hwnd,
        string? rootRuntimeId,
        int maxDepth,
        bool interactiveOnly,
        int maxNodes,
        int timeBudgetMs,
        Func<bool>? isCancelled)
    {
        // 树构建同样访问 IUIAutomation；必须与 find/invoke 串行，否则会跨线程使用 COM 对象。
        lock (_sync)
        {
            return BuildTreeCore(
                hwnd, rootRuntimeId, maxDepth, interactiveOnly, maxNodes, timeBudgetMs, isCancelled);
        }
    }

    private UiaTreeResult BuildTreeCore(
        IntPtr hwnd,
        string? rootRuntimeId,
        int maxDepth,
        bool interactiveOnly,
        int maxNodes,
        int timeBudgetMs,
        Func<bool>? isCancelled)
    {
        IUIAutomationElement? rootElement = null;
        UiaElementHandle? rootHandle = null;
        var stopwatch = Stopwatch.StartNew();

        maxDepth = Math.Clamp(maxDepth, 1, 10);
        maxNodes = Math.Clamp(maxNodes, 1, 2000);

        try
        {
            if (!string.IsNullOrWhiteSpace(rootRuntimeId))
            {
                rootHandle = FindByRuntimeId(hwnd, rootRuntimeId);
                rootElement = rootHandle?.Element;

                if (rootElement is null)
                {
                    return new UiaTreeResult(null, 0, 0, 0, false, "root_not_found", 0, 0);
                }

                // 句柄负责释放；这里用同一对象继续查询。
            }
            else
            {
                // 必须使用 BuildCache 版本，否则后续读取 Cached* 属性会抛 ArgumentException。
                rootElement = hwnd != IntPtr.Zero
                    ? _automation.ElementFromHandleBuildCache(
                        ResolveUwpContentHwnd(hwnd), _elementCache)
                    : _automation.GetRootElementBuildCache(_elementCache);
            }

            if (rootElement is null)
            {
                return new UiaTreeResult(null, 0, 0, 0, false, "root_not_found", 0, 0);
            }

            UiaElementInfo? rootInfo = Describe(rootElement, cached: true);
            if (rootInfo is null)
            {
                return new UiaTreeResult(null, 0, 0, 0, false, "root_unavailable", 0, 0);
            }

            var state = new TreeBuildState
            {
                MaxDepth = maxDepth,
                MaxNodes = maxNodes,
                InteractiveOnly = interactiveOnly,
                Deadline = timeBudgetMs,
                Stopwatch = stopwatch,
                VisitedLimit = Math.Max(64, maxNodes * 6),
                IsCancelled = isCancelled,
                Nodes = 1,
            };

            var root = new UiaTreeNode { Info = rootInfo };
            BuildChildren(rootElement, root, 1, state);

            return new UiaTreeResult(
                root,
                state.Nodes,
                state.Visited,
                state.Pruned,
                state.Truncated,
                state.Reason,
                state.MaxDepthReached,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2));
        }
        catch (COMException)
        {
            return new UiaTreeResult(
                null, 0, 0, 0, false, "uia_error", 0,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2));
        }
        finally
        {
            rootHandle?.Dispose();
            if (rootHandle is null)
            {
                ReleaseElement(rootElement);
            }
        }
    }

    private void BuildChildren(
        IUIAutomationElement element, UiaTreeNode node, int depth, TreeBuildState state)
    {
        if (depth > state.MaxDepth)
        {
            return;
        }

        if (state.IsCancelled?.Invoke() == true)
        {
            state.Truncate("cancelled");
            return;
        }

        if (state.Stopwatch.ElapsedMilliseconds > state.Deadline)
        {
            state.Truncate("time_budget");
            return;
        }

        IUIAutomationElementArray? children = null;

        try
        {
            children = element.FindAllBuildCache(
                TreeScope.TreeScope_Children, TrueCondition, _elementCache);
        }
        catch (COMException)
        {
            return;
        }

        if (children is null)
        {
            return;
        }

        try
        {
            int count = children.Length;

            for (int i = 0; i < count; i++)
            {
                if (state.Nodes >= state.MaxNodes)
                {
                    state.Truncate("max_nodes");
                    return;
                }

                if (state.Visited >= state.VisitedLimit)
                {
                    state.Truncate("visited_limit");
                    return;
                }

                if ((i & 7) == 0)
                {
                    if (state.Stopwatch.ElapsedMilliseconds > state.Deadline)
                    {
                        state.Truncate("time_budget");
                        return;
                    }

                    if (state.IsCancelled?.Invoke() == true)
                    {
                        state.Truncate("cancelled");
                        return;
                    }
                }

                VisitChild(children, i, node, depth, state);
            }
        }
        finally
        {
            ReleaseElement(children);
        }
    }

    private void VisitChild(
        IUIAutomationElementArray children, int i, UiaTreeNode node, int depth, TreeBuildState state)
    {
                IUIAutomationElement? child = null;

                try
                {
                    child = children.GetElement(i);
                    if (child is null)
                    {
                        return;
                    }

                    state.Visited++;
                    state.MaxDepthReached = Math.Max(state.MaxDepthReached, depth);

                    UiaElementInfo? info = Describe(child, cached: true);
                    if (info is null)
                    {
                        return;
                    }

                    if (IsNoiseControl(info.ControlType))
                    {
                        // 滚动条/进度条/分隔条内部全是机械按钮，进树只会污染上下文。
                        return;
                    }

                    var childNode = new UiaTreeNode { Info = info };
                    BuildChildren(child, childNode, depth + 1, state);

                    if (state.InteractiveOnly && !IsInteresting(childNode))
                    {
                        state.Pruned++;

                        // 单子节点的无意义容器（Chromium 常见）直接上提，别让它吃掉深度预算。
                        if (childNode.Children.Count == 1)
                        {
                            node.Children.Add(childNode.Children[0]);
                        }

                        return;
                    }

                    // 递归可能已经用满预算；此时不能再把当前节点计入，否则 max_nodes 会被超过。
                    if (state.Nodes >= state.MaxNodes)
                    {
                        state.Truncate("max_nodes");
                        return;
                    }

                    state.Nodes++;
                    node.Children.Add(childNode);
                }
                finally
                {
                    ReleaseElement(child);
                }
    }

    private static bool IsInteresting(UiaTreeNode node) =>
        node.Children.Count > 0 ||
        node.Info.Focusable ||
        node.Info.Focused ||
        InteractiveControlTypes.Contains(node.Info.ControlType) ||
        // 画布/自绘应用常把内容暴露成无子节点、不可聚焦的 Text/Image/Custom。
        // 名字为空时再看 Value/Text：很多文本控件只有 TextPattern 内容。
        (node.Info.ControlType is 50020 or 50006 or 50025 &&
         (!string.IsNullOrWhiteSpace(node.Info.Name) ||
          !string.IsNullOrWhiteSpace(node.Info.Value) ||
          !string.IsNullOrWhiteSpace(node.Info.Text)));

    private static bool IsNoiseControl(int controlType) =>
        controlType is 50012 or 50014 or 50027 or 50038; // ProgressBar/ScrollBar/Thumb/Separator

    private sealed class TreeBuildState
    {
        public int MaxDepth;
        public int MaxNodes;
        public bool InteractiveOnly;
        public int Deadline;
        public Stopwatch Stopwatch = null!;
        public int VisitedLimit;
        public Func<bool>? IsCancelled;
        public int Nodes;
        public int Visited;
        public int Pruned;
        public int MaxDepthReached;
        public bool Truncated;
        public string? Reason;

        public void Truncate(string reason)
        {
            if (Truncated)
            {
                return;
            }

            Truncated = true;
            Reason = reason;
        }
    }

    /* ------------------------------------------------------------------ */
    /* 条件与元数据                                                        */
    /* ------------------------------------------------------------------ */
}
