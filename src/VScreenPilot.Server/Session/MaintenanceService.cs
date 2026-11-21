using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Session;

/// <summary>周期维护所需的最小宿主接口。</summary>
internal interface IMaintenanceHost
{
    string ArtifactRoot { get; }
    bool Disposed { get; }
    string[] ArtifactDirectories { get; }
    LogRing Log { get; }
}

/// <summary>daemon 长期运行时的产物、trace 与日志清理。</summary>
internal sealed class MaintenanceService : IDisposable
{
    private readonly IMaintenanceHost _host;
    private long _lastSweepTicks;
    private Timer? _timer;
    private ArtifactStore? _artifacts;

    public MaintenanceService(IMaintenanceHost host) => _host = host;

    /// <summary>daemon 长期运行时也要清理；每小时最多扫一次，不依赖客户端断开。</summary>
    public void Start()
    {
        _timer = new Timer(
            _ => Run(),
            state: null,
            dueTime: TimeSpan.FromHours(1),
            period: TimeSpan.FromHours(1));
    }

    private void Run()
    {
        if (_host.Disposed)
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
        _artifacts.Sweep(_host.ArtifactDirectories);
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
    }
}
