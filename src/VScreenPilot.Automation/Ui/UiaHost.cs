using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using VScreenPilot.Errors;

namespace VScreenPilot.Uia;

/// <summary>
/// UIA 的单线程 MTA 宿主：所有 <see cref="UiaService"/> 调用在同一个专用线程上执行，
/// 消除跨 apartment 的隐式封送；调用方仍是同步等待结果。
///
/// 超时退休语义：<see cref="Retire"/> 把当前代际移出服务列表并标记 Abandoned，
/// 新调用走新代际；队列里尚未执行的 WorkItem 直接失败（不会迟到执行），
/// 已经在 COM 调用中的无法取消。卡死的代际不阻塞主流程，也不在调用线程上 Dispose。
/// </summary>
internal sealed class UiaHost : IDisposable
{
    private static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<UiaService> _factory;
    private readonly TimeSpan _callTimeout;
    private readonly object _gate = new();
    private readonly List<UiaGeneration> _retired = new();
    private UiaGeneration? _current;
    private int _disposed;

    public UiaHost()
        : this(static () => new UiaService(), DefaultCallTimeout)
    {
    }

    internal UiaHost(Func<UiaService> factory)
        : this(factory, DefaultCallTimeout)
    {
    }

    internal UiaHost(Func<UiaService> factory, TimeSpan callTimeout)
    {
        _factory = factory;
        _callTimeout = callTimeout;
    }

    /// <summary>放弃当前代际；后续调用自动创建新代际。旧代际由自己的工作线程收尾。</summary>
    public bool Retire()
    {
        UiaGeneration? old;

        lock (_gate)
        {
            old = _current;
            _current = null;

            if (old is not null)
            {
                _retired.Add(old);
            }
        }

        old?.Abandon();
        old?.Queue.CompleteAdding();
        return old is not null;
    }

    public UiaTreeResult BuildTree(UiTreeRequest request, Func<bool>? isCancelled) =>
        Invoke(service => service.TreeOps.BuildTree(request, isCancelled));

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

    public UiaElementHandle? FindByFingerprint(FingerprintQuery query) =>
        Invoke(service => service.LocateOps.FindByFingerprint(query));

    public UiaElementInfo? GetFocusedElement() =>
        Invoke(static service => service.GetFocusedElement());

    public UiaElementHandle? GetFocusedElementHandle(int? expectedPid = null) =>
        Invoke(service => service.GetFocusedElementHandle(expectedPid));

    public bool TryInvokeAtPoint(
        int x, int y, int? expectedPid, out UiaElementInfo? info,
        Func<UiaElementInfo, bool>? confirm = null)
    {
        UiaElementInfo? observed = null;
        bool ok = Invoke(service => service.TryInvokeAtPoint(x, y, expectedPid, out observed, confirm));
        info = observed;
        return ok;
    }

    /// <summary>只读命中测试；不会触发任何 UIA pattern。</summary>
    public UiaElementInfo? DescribeAtPoint(int x, int y, int? expectedPid) =>
        Invoke(service => service.DescribeAtPoint(x, y, expectedPid));

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

    /// <summary>MSAA 与服务级 UIA 共用同一 MTA worker，保证 COM 串行。</summary>
    public T InvokeMsaa<T>(Func<T> action) => Invoke(_ => action());

    /// <summary>MSAA 与服务级 UIA 共用同一 MTA worker，保证 COM 串行。</summary>
    public void InvokeMsaa(Action action) =>
        Invoke(service =>
        {
            action();
            return true;
        });

    public UiaElementInfo? FindTextElement(IntPtr hwnd, string needle, int maxChars = 65536) =>
        Invoke(service => service.FindTextElement(hwnd, needle, maxChars));

    public static bool IsKnownControlType(string controlType) =>
        UiaService.IsKnownControlType(controlType);

    public static IReadOnlyList<string> SupportedControlTypes => UiaService.SupportedControlTypes;

    /// <summary>
    /// 把句柄级操作派发到它所属代际的 worker；代际已退休/队列关闭时退回直接调用，
    /// 保证旧句柄在超时路径上仍可用。
    /// </summary>
    public T InvokeOn<T>(UiaService owner, Func<UiaService, T> action)
    {
        UiaGeneration? generation;

        lock (_gate)
        {
            generation = _current?.Service == owner
                ? _current
                : _retired.FirstOrDefault(item => item.Service == owner);
        }

        if (generation is null || generation.Queue.IsAddingCompleted ||
            generation.Thread == Thread.CurrentThread)
        {
            return action(owner);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            generation.Queue.Add(new WorkItem<T>(action, completion));
        }
        catch (InvalidOperationException)
        {
            return action(owner);
        }

        WaitReadyOrRetire(generation);
        return WaitCompletionOrRetire(generation, completion);
    }

    private T Invoke<T>(Func<UiaService, T> action)
    {
        while (true)
        {
            UiaGeneration generation = Current();

            if (generation.Thread == Thread.CurrentThread)
            {
                return action(generation.Service!);
            }

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

            WaitReadyOrRetire(generation);
            return WaitCompletionOrRetire(generation, completion);
        }
    }

    /// <summary>代际启动超时：退休当前代际，避免卡在 Ready 上。</summary>
    private void WaitReadyOrRetire(UiaGeneration generation)
    {
        try
        {
            generation.Ready.Task.WaitAsync(_callTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            RetireIfCurrent(generation);
            throw CallTimeout();
        }
    }

    /// <summary>调用超时：退休当前代际并抛 E_TIMEOUT；旧线程把手上工作做完后自行释放。</summary>
    private T WaitCompletionOrRetire<T>(UiaGeneration generation, TaskCompletionSource<T> completion)
    {
        try
        {
            return completion.Task.WaitAsync(_callTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            RetireIfCurrent(generation);
            throw CallTimeout();
        }
    }

    private void RetireIfCurrent(UiaGeneration generation)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, generation))
            {
                return;
            }

            _current = null;
            _retired.Add(generation);
        }

        generation.Abandon();
        generation.Queue.CompleteAdding();
    }

    internal ToolException CallTimeout() =>
        new(
            "E_TIMEOUT",
            $"UI Automation 调用超过 {_callTimeout.TotalSeconds:0} 秒未返回，已退休当前 UIA 代际",
            retryable: true,
            hint: "重试；若持续超时，应用可能卡死，可先 release_environment",
            stage: "uia");

    private UiaGeneration Current()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            // 工厂抛异常（Ready faulted）后旧代际永远不会恢复；下次调用必须换新代际，
            // 否则所有后续 UIA 调用都会同步抛同一个异常。
            if (_current is { } failed && (failed.Ready.Task.IsFaulted || failed.Ready.Task.IsCanceled))
            {
                _current = null;
                _retired.Add(failed);
                failed.Abandon();
                failed.Queue.CompleteAdding();
            }

            return _current ??= Start();
        }
    }

    private UiaGeneration Start()
    {
        var generation = new UiaGeneration(_factory, this);
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
            foreach (UiaGeneration generation in _retired)
            {
                generation.Queue.CompleteAdding();
            }

            _retired.Clear();
        }
    }

    internal abstract class WorkItem
    {
        public abstract void Run(UiaService service);

        public abstract void Fail(Exception exception);
    }

    internal sealed class WorkItem<T> : WorkItem
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
}
