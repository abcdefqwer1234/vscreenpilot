using VScreenPilot.Session;

namespace VScreenPilot.Apps;

/// <summary>
/// 会话内应用窗口的唯一登记处。使用会话的 <c>_gate</c> 作为锁，
/// 保证与显示状态、初始化标志等复合操作仍处于同一临界区。
/// </summary>
internal sealed class TrackedAppRegistry
{
    private readonly object _gate;
    private readonly List<TrackedApp> _apps = new();

    public TrackedAppRegistry(object gate) => _gate = gate;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _apps.Count;
            }
        }
    }

    /// <summary>返回调用时刻的独立快照；调用方可以安全地在其上做长遍历。</summary>
    public IReadOnlyList<TrackedApp> Snapshot()
    {
        lock (_gate)
        {
            return _apps.ToArray();
        }
    }

    public bool Any(Func<TrackedApp, bool> predicate)
    {
        lock (_gate)
        {
            return _apps.Any(predicate);
        }
    }

    public TrackedApp? FirstOrDefault(Func<TrackedApp, bool> predicate)
    {
        lock (_gate)
        {
            return _apps.FirstOrDefault(predicate);
        }
    }

    public TrackedApp? LastOrDefault(Func<TrackedApp, bool> predicate)
    {
        lock (_gate)
        {
            return _apps.LastOrDefault(predicate);
        }
    }

    public List<TrackedApp> Where(Func<TrackedApp, bool> predicate)
    {
        lock (_gate)
        {
            return _apps.Where(predicate).ToList();
        }
    }

    public TrackedApp this[int index]
    {
        get
        {
            lock (_gate)
            {
                return _apps[index];
            }
        }
    }

    public void Add(TrackedApp app)
    {
        lock (_gate)
        {
            _apps.Add(app);
        }
    }

    public void RemoveAt(int index)
    {
        lock (_gate)
        {
            _apps.RemoveAt(index);
        }
    }

    public int RemoveAll(Func<TrackedApp, bool> predicate)
    {
        lock (_gate)
        {
            return _apps.RemoveAll(item => predicate(item));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _apps.Clear();
        }
    }
}
