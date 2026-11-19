using VScreenPilot.Infrastructure;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    internal const string ArtifactRetentionEnv = ArtifactStore.RetentionEnv;

    internal const int DefaultArtifactRetentionDays = ArtifactStore.DefaultRetentionDays;

    private long _lastArtifactSweepTicks;
    private Timer? _maintenanceTimer;

    /// <summary>daemon 长期运行时也要清理；每小时最多扫一次，不依赖客户端断开。</summary>
    private void StartMaintenance()
    {
        _maintenanceTimer = new Timer(
            _ => RunMaintenance(),
            state: null,
            dueTime: TimeSpan.FromHours(1),
            period: TimeSpan.FromHours(1));
    }

    private void RunMaintenance()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            SweepOldArtifacts();
        }
        catch (Exception exception)
        {
            _log.Warn($"周期清理产物失败: {exception.Message}");
        }

        try
        {
            TraceLog.CleanupSessions();
        }
        catch (Exception exception)
        {
            _log.Warn($"周期清理 trace 失败: {exception.Message}");
        }

        try
        {
            _log.CleanupOldLogs();
        }
        catch (Exception exception)
        {
            _log.Warn($"周期清理日志失败: {exception.Message}");
        }
    }

    /// <summary>清理过期截图目录；具体策略在 <see cref="ArtifactStore"/>。</summary>
    private void SweepOldArtifacts()
    {
        Interlocked.Exchange(ref _lastArtifactSweepTicks, Environment.TickCount64);

        string[] current;

        lock (_artifactGate)
        {
            current = _artifactDirectories.Values.ToArray();
        }

        _artifacts ??= new ArtifactStore(ArtifactRoot, _log.Info, _log.Warn);
        _artifacts.Sweep(current);
    }

    /// <summary>daemon 长期运行时也要清理；最多每小时扫一次，避免每个连接都遍历目录。</summary>
    private void SweepArtifactsPeriodically()
    {
        long last = Interlocked.Read(ref _lastArtifactSweepTicks);

        if (last != 0 && Environment.TickCount64 - last < 60 * 60 * 1000)
        {
            return;
        }

        SweepOldArtifacts();
    }
}
