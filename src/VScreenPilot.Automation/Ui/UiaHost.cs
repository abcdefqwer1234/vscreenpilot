using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace VScreenPilot.Uia;

/// <summary>
/// UIA 的单线程 MTA 宿主：所有 <see cref="UiaService"/> 调用在同一个专用线程上执行，
/// 消除跨 apartment 的隐式封送；调用方仍是同步等待结果。
///
/// 超时退休语义与旧实现一致：<see cref="Retire"/> 只把当前代际移出服务列表并让新调用走新代际，
/// 旧线程继续把队列里的工作做完再释放 COM；卡死的代际不阻塞主流程，也不在调用线程上 Dispose。
/// </summary>
internal sealed class UiaHost : IDisposable
{
    private readonly Func<UiaService> _factory;
    private readonly object _gate = new();
    private readonly List<Generation> _retired = new();
    private Generation? _current;
    private int _disposed;

    public UiaHost()
        : this(static () => new UiaService())
    {
    }

    internal UiaHost(Func<UiaService> factory) => _factory = factory;

    /// <summary>放弃当前代际；后续调用自动创建新代际。旧代际由自己的工作线程收尾。</summary>
    public bool Retire()
    {
        Generation? old;

        lock (_gate)
        {
            old = _current;
            _current = null;

            if (old is not null)
            {
                _retired.Add(old);
            }
        }

        old?.Queue.CompleteAdding();
        return old is not null;
    }

    public UiaTreeResult BuildTree(
        IntPtr hwnd,
        string? rootRuntimeId,
        int maxDepth,
        bool interactiveOnly,
        int maxNodes,
        int timeBudgetMs,
        Func<bool>? isCancelled) =>
        Invoke(service => service.BuildTree(
            hwnd, rootRuntimeId, maxDepth, interactiveOnly, maxNodes, timeBudgetMs, isCancelled));

    public UiaElementInfo? FindElement(
        IntPtr hwnd,
        string? name,
        string? controlType,
        string? automationId,
        string? runtimeId) =>
        Invoke(service => service.FindElement(hwnd, name, controlType, automationId, runtimeId));

    public List<UiaElementInfo> FindElements(
        IntPtr hwnd,
        string? name,
        UiaNameMatch nameMatch,
        string? controlType,
        string? automationId,
        int maxResults) =>
        Invoke(service => service.FindElements(
            hwnd, name, nameMatch, controlType, automationId, maxResults));

    public UiaFindResult FindElementsDetailed(
        IntPtr hwnd,
        string? name,
        UiaNameMatch nameMatch,
        string? controlType,
        string? automationId,
        int maxResults) =>
        Invoke(service => service.FindElementsDetailed(
            hwnd, name, nameMatch, controlType, automationId, maxResults));

    public UiaElementHandle? FindByRuntimeId(IntPtr hwnd, string runtimeId) =>
        Invoke(service => service.FindByRuntimeId(hwnd, runtimeId));

    public UiaElementHandle? FindByFingerprint(
        IntPtr hwnd,
        string name,
        string automationId,
        string controlTypeName,
        int left,
        int top,
        int right,
        int bottom) =>
        Invoke(service => service.FindByFingerprint(
            hwnd, name, automationId, controlTypeName, left, top, right, bottom));

    public UiaElementInfo? GetFocusedElement() =>
        Invoke(static service => service.GetFocusedElement());

    public UiaElementHandle? GetFocusedElementHandle(int? expectedPid = null) =>
        Invoke(service => service.GetFocusedElementHandle(expectedPid));

    public bool TryInvokeAtPoint(int x, int y, int? expectedPid, out UiaElementInfo? info)
    {
        UiaElementInfo? observed = null;
        bool ok = Invoke(service => service.TryInvokeAtPoint(x, y, expectedPid, out observed));
        info = observed;
        return ok;
    }

    public (bool Ok, string? Strategy) TryInvoke(
        IUIAutomationElement element, UiaInvokePattern pattern = UiaInvokePattern.Auto) =>
        Invoke(service => service.TryInvoke(element, pattern));

    public string? GetText(IUIAutomationElement element, int maxChars = 65536) =>
        Invoke(service => service.GetText(element, maxChars));

    public string? GetValue(IUIAutomationElement element) =>
        Invoke(service => service.GetValue(element));

    public UiaSetValueResult TrySetValue(IUIAutomationElement element, string value) =>
        Invoke(service => service.TrySetValue(element, value));

    public void FocusElement(IUIAutomationElement element) =>
        Invoke(service =>
        {
            service.FocusElement(element);
            return true;
        });

    public UiaElementInfo? FindTextElement(IntPtr hwnd, string needle, int maxChars = 65536) =>
        Invoke(service => service.FindTextElement(hwnd, needle, maxChars));

    public static bool IsKnownControlType(string controlType) =>
        UiaService.IsKnownControlType(controlType);

    public static IReadOnlyList<string> SupportedControlTypes => UiaService.SupportedControlTypes;

    private T Invoke<T>(Func<UiaService, T> action)
    {
        while (true)
        {
            Generation generation = Current();
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                generation.Queue.Add(new WorkItem<T>(action, completion));
            }
            catch (InvalidOperationException)
            {
                // 代际在取得后刚好被退休；换新代际重试。
                continue;
            }

            generation.Ready.Task.GetAwaiter().GetResult();
            return completion.Task.GetAwaiter().GetResult();
        }
    }

    private Generation Current()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _current ??= Start();
        }
    }

    private Generation Start()
    {
        var generation = new Generation(_factory);
        generation.Thread.Start();
        return generation;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Retire();

        lock (_gate)
        {
            foreach (Generation generation in _retired)
            {
                generation.Queue.CompleteAdding();
            }

            _retired.Clear();
        }
    }

    private abstract class WorkItem
    {
        public abstract void Run(UiaService service);

        public abstract void Fail(Exception exception);
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly Func<UiaService, T> _action;
        private readonly TaskCompletionSource<T> _completion;

        public WorkItem(Func<UiaService, T> action, TaskCompletionSource<T> completion)
        {
            _action = action;
            _completion = completion;
        }

        public override void Run(UiaService service) => _completion.TrySetResult(_action(service));

        public override void Fail(Exception exception) => _completion.TrySetException(exception);
    }

    private sealed class Generation
    {
        private readonly Func<UiaService> _factory;

        public Generation(Func<UiaService> factory)
        {
            _factory = factory;
            Thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "vsp-uia-host",
            };
            Thread.SetApartmentState(ApartmentState.MTA);
        }

        public BlockingCollection<WorkItem> Queue { get; } = new();

        public Thread Thread { get; }

        public TaskCompletionSource Ready { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Run()
        {
            UiaService? service = null;

            try
            {
                service = _factory();
                Ready.TrySetResult();
            }
            catch (Exception exception)
            {
                Ready.TrySetException(exception);

                foreach (WorkItem item in Queue.GetConsumingEnumerable())
                {
                    item.Fail(exception);
                }

                return;
            }

            try
            {
                foreach (WorkItem item in Queue.GetConsumingEnumerable())
                {
                    try
                    {
                        item.Run(service);
                    }
                    catch (Exception exception)
                    {
                        item.Fail(exception);
                    }
                }
            }
            finally
            {
                service?.Dispose();
            }
        }
    }
}
