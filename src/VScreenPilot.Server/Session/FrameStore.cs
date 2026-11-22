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
            while (_frames.Count > MaxFrames ||
                   _frames.Sum(frame => (long)frame.Bgra.Length) > MaxBytes)
            {
                if (_frames.Count == 1)
                {
                    break;
                }

                _frames.RemoveAt(0);
            }
        }

        return id;
    }

    private void TrimClientLocked(string clientId)
    {
        int owned = _frames.Count(frame =>
            string.Equals(frame.ClientId, clientId, StringComparison.Ordinal));

        while (owned > PerClientMaxFrames)
        {
            int oldest = _frames.FindIndex(frame =>
                string.Equals(frame.ClientId, clientId, StringComparison.Ordinal));

            if (oldest < 0)
            {
                return;
            }

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

    public FrameSnapshot? Latest()
    {
        lock (_gate)
        {
            return _frames.LastOrDefault();
        }
    }

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
