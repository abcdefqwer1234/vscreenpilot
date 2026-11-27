namespace VScreenPilot.Session;

/// <summary>一帧 BGRA 截图，供 assert_region / pixel_diff 引用。</summary>
internal sealed class FrameSnapshot
{
    public required string Id { get; init; }
    public string ClientId { get; init; } = "c0";
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
}

/// <summary>
/// 最近若干帧的环形缓存：最多 8 张 / 32MB，按客户端隔离查询。
/// 所有读写都在内部锁内完成，避免调用方各自持有帧锁。
/// </summary>
internal sealed class FrameStore
{
    private const int MaxFrames = 8;

    /// <summary>单客户端配额：一个客户端刷帧不能把其他客户端的 baseline 顶掉。保留 6 帧给多区域基线。</summary>
    private const int PerClientMaxFrames = 6;

    private const long MaxBytes = 32L * 1024 * 1024;

    /// <summary>单客户端字节配额取总量的一半，保证两个客户端各自刷帧时不会互相淘汰。</summary>
    private const long PerClientMaxBytes = MaxBytes / 2;

    private readonly List<FrameSnapshot> _frames = new();
    private readonly object _gate = new();
    private long _nextId;

    public string Store(byte[] bgra, int width, int height, int x, int y, string clientId)
    {
        string id = $"f{Interlocked.Increment(ref _nextId)}";

        lock (_gate)
        {
            _frames.Add(new FrameSnapshot
            {
                Id = id,
                ClientId = clientId,
                Bgra = bgra,
                Width = width,
                Height = height,
                X = x,
                Y = y,
            });

            TrimClientLocked(clientId);

            // 16 张 1280×720 BGRA ≈ 59MB；改成 32MB/8 张，配合 PNG 池化把稳态内存压下来。
            // 全局上限触发时优先淘汰帧数最多的客户端，避免一个客户端刷帧把别人的基线挤掉。
            while (_frames.Count > MaxFrames ||
                   _frames.Sum(frame => (long)frame.Bgra.Length) > MaxBytes)
            {
                if (_frames.Count == 1)
                {
                    break;
                }

                _frames.RemoveAt(FindEvictionIndexLocked());
            }
        }

        return id;
    }

    private int FindEvictionIndexLocked()
    {
        // 优先淘汰“仍有多帧”的客户端中最旧的一帧；每个客户端至少保留最新一帧作为 baseline。
        // 只有所有客户端都只剩 1 帧（客户端数超过全局槽位）时才淘汰最旧帧。
        List<IGrouping<string, FrameSnapshot>> candidates = _frames
            .GroupBy(frame => frame.ClientId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();

        if (candidates.Count == 0)
        {
            return 0;
        }

        string busiest = candidates
            .OrderByDescending(group => group.Count())
            .ThenBy(group => _frames.FindIndex(frame =>
                string.Equals(frame.ClientId, group.Key, StringComparison.Ordinal)))
            .First()
            .Key;

        return _frames.FindIndex(frame =>
            string.Equals(frame.ClientId, busiest, StringComparison.Ordinal));
    }

    private void TrimClientLocked(string clientId)
    {
        int owned = _frames.Count(frame =>
            string.Equals(frame.ClientId, clientId, StringComparison.Ordinal));
        long ownedBytes = _frames
            .Where(frame => string.Equals(frame.ClientId, clientId, StringComparison.Ordinal))
            .Sum(frame => (long)frame.Bgra.Length);

        // 单帧即使超过单客户端字节配额也保留：调用方刚存进来，至少让它有一次使用机会。
        while (owned > 1 &&
               (owned > PerClientMaxFrames || ownedBytes > PerClientMaxBytes))
        {
            int oldest = _frames.FindIndex(frame =>
                string.Equals(frame.ClientId, clientId, StringComparison.Ordinal));

            if (oldest < 0)
            {
                return;
            }

            ownedBytes -= _frames[oldest].Bgra.Length;
            _frames.RemoveAt(oldest);
            owned--;
        }
    }

    public FrameSnapshot? FindById(string clientId, string frameId)
    {
        lock (_gate)
        {
            return _frames.LastOrDefault(frame =>
                string.Equals(frame.ClientId, clientId, StringComparison.Ordinal) &&
                string.Equals(frame.Id, frameId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public FrameSnapshot? FindLatest(string clientId, int x, int y, int width, int height)
    {
        lock (_gate)
        {
            return _frames.LastOrDefault(frame =>
                string.Equals(frame.ClientId, clientId, StringComparison.Ordinal) &&
                frame.X == x && frame.Y == y &&
                frame.Width == width && frame.Height == height);
        }
    }

    /// <summary>该客户端最近一帧；baseline=last 必须按客户端隔离，不能读到别人的截图。</summary>
    public FrameSnapshot? Latest(string clientId)
    {
        lock (_gate)
        {
            return _frames.LastOrDefault(frame =>
                string.Equals(frame.ClientId, clientId, StringComparison.Ordinal));
        }
    }

    /// <summary>该客户端的帧清单；用于错误详情，避免泄露其他客户端的帧信息。</summary>
    public IReadOnlyList<FrameSnapshot> Snapshot(string clientId)
    {
        lock (_gate)
        {
            return _frames
                .Where(frame => string.Equals(frame.ClientId, clientId, StringComparison.Ordinal))
                .ToArray();
        }
    }

    /// <summary>全局帧快照；仅供诊断/测试，业务查询请用带 clientId 的重载。</summary>
    public IReadOnlyList<FrameSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _frames.ToArray();
        }
    }

    public void RemoveClient(string clientId)
    {
        lock (_gate)
        {
            _frames.RemoveAll(frame =>
                string.Equals(frame.ClientId, clientId, StringComparison.Ordinal));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _nextId = 0;
        }
    }
}
