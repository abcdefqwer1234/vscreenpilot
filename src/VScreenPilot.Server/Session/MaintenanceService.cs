using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Session;

/// <summary>周期维护所需的最小上下文；用委托读取实时状态，避免复制。</summary>
internal sealed record MaintenanceContext(
    string ArtifactRoot,
    Func<bool> Disposed,
    Func<string[]> ArtifactDirectories,
    LogRing Log,
    Action ReapOrphans);

/// <summary>daemon 长期运行时的产物、trace 与日志清理。</summary>
internal sealed class MaintenanceService : IDisposable
{
    private readonly MaintenanceContext _host;
    private long _lastSweepTicks;
    private Timer? _timer;
    private Timer? _orphanTimer;
    private ArtifactStore? _artifacts;

    public MaintenanceService(MaintenanceContext host) => _host = host;

    /// <summary>daemon 长期运行时也要清理；每小时最多扫一次，不依赖客户端断开。</summary>
    public void Start()
    {
        _timer = new Timer(
            _ => Run(),
            state: null,
            dueTime: TimeSpan.FromHours(1),
            period: TimeSpan.FromHours(1));

        // 孤儿窗口按分钟检查：断开后保留 10 分钟仍无人接管才回收。
        _orphanTimer = new Timer(
            _ => ReapOrphansSafely(),
            state: null,
            dueTime: TimeSpan.FromMinutes(1),
            period: TimeSpan.FromMinutes(1));
    }

    private void ReapOrphansSafely()
    {
        if (_host.Disposed())
        {
            return;
        }

        try
        {
            _host.ReapOrphans();
        }
        catch (Exception exception)
        {
            _host.Log.Warn($"回收孤儿窗口失败: {exception.Message}");
        }
    }

    private void Run()
    {
        if (_host.Disposed())
        {
            return;
        }

        try
        {
            SweepNow();
        }
        catch (Exception exception)
        {
            _host.Log.Warn($"周期清理产物失败: {exception.Message}");
        }

        try
        {
            TraceLog.CleanupSessions();
        }
        catch (Exception exception)
        {
            _host.Log.Warn($"周期清理 trace 失败: {exception.Message}");
        }

        try
        {
            _host.Log.CleanupOldLogs();
        }
        catch (Exception exception)
        {
            _host.Log.Warn($"周期清理日志失败: {exception.Message}");
        }
    }

    /// <summary>清理过期截图目录；具体策略在 <see cref="ArtifactStore"/>。</summary>
    public void SweepNow()
    {
        Interlocked.Exchange(ref _lastSweepTicks, Environment.TickCount64);

        _artifacts ??= new ArtifactStore(_host.ArtifactRoot, _host.Log.Info, _host.Log.Warn);
        _artifacts.Sweep(_host.ArtifactDirectories());
    }

    /// <summary>daemon 长期运行时也要清理；最多每小时扫一次，避免每个连接都遍历目录。</summary>
    public void SweepPeriodically()
    {
        long last = Interlocked.Read(ref _lastSweepTicks);

        if (last != 0 && Environment.TickCount64 - last < 60 * 60 * 1000)
        {
            return;
        }

        SweepNow();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _orphanTimer?.Dispose();
        _orphanTimer = null;
    }
}
