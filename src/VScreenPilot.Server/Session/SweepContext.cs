namespace VScreenPilot.Session;

/// <summary>artifact 清理的共享状态：保留天数、当前会话目录集合与计数。</summary>
internal sealed class SweepContext
{
    private readonly HashSet<string> _current;

    public SweepContext(IReadOnlyCollection<string> currentDirectories, int days, DateTime nowUtc)
    {
        _current = new HashSet<string>(
            currentDirectories.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        Days = days;
        NowUtc = nowUtc;
    }

    public int Days { get; }

    public DateTime NowUtc { get; }

    public int Deleted { get; set; }

    public int Failed { get; set; }

    public bool IsCurrent(string directory) => _current.Contains(Path.GetFullPath(directory));
}
