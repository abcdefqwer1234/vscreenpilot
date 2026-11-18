using System.Text.RegularExpressions;
using VScreenPilot.Infrastructure;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    internal const string ArtifactRetentionEnv = "VSP_ARTIFACT_RETENTION_DAYS";

    internal const int DefaultArtifactRetentionDays = 7;

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

    /// <summary>解析保留天数：未设置/非法 → 默认 7；0 → 关闭清理。</summary>
    internal static int ResolveArtifactRetentionDays(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultArtifactRetentionDays;
        }

        if (!int.TryParse(raw.Trim(), out int days) || days < 0)
        {
            return DefaultArtifactRetentionDays;
        }

        return days;
    }

    /// <summary>只清理本程序命名的会话目录；当前会话目录永不删除。</summary>
    internal static bool ShouldDeleteArtifactDirectory(
        string directoryName,
        DateTime lastWriteUtc,
        DateTime nowUtc,
        int retentionDays,
        bool isCurrent)
    {
        if (retentionDays <= 0 || isCurrent)
        {
            return false;
        }

        if (!ArtifactDirectoryName().IsMatch(directoryName))
        {
            return false;
        }

        return lastWriteUtc <= nowUtc.AddDays(-retentionDays);
    }

    /// <summary>
    /// 清理旧的截图目录。画面可能含用户数据，默认保留 7 天；
    /// 可通过 VSP_ARTIFACT_RETENTION_DAYS 调整，0 表示不清理。
    /// 目录布局：<c>&lt;profile&gt;/&lt;clientId&gt;/&lt;yyyyMMdd-HHmmss&gt;-&lt;pid&gt;</c>，
    /// 同时兼容旧的 <c>&lt;profile&gt;/&lt;yyyyMMdd-HHmmss&gt;-&lt;pid&gt;</c>。
    /// </summary>
    private void SweepOldArtifacts()
    {
        Interlocked.Exchange(ref _lastArtifactSweepTicks, Environment.TickCount64);

        int days = ResolveArtifactRetentionDays(
            Environment.GetEnvironmentVariable(ArtifactRetentionEnv));

        if (days <= 0)
        {
            _log.Info("截图产物保留策略已关闭（VSP_ARTIFACT_RETENTION_DAYS=0）");
            return;
        }

        string root = ArtifactRoot;

        if (!Directory.Exists(root))
        {
            return;
        }

        int deleted = 0;
        int failed = 0;
        DateTime nowUtc = DateTime.UtcNow;
        string[] current;

        lock (_artifactGate)
        {
            current = _artifactDirectories.Values.ToArray();
        }

        void TryDelete(string directory)
        {
            string name = Path.GetFileName(directory);
            bool isCurrent = current.Any(path => string.Equals(
                Path.GetFullPath(directory),
                Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase));

            try
            {
                if (!ShouldDeleteArtifactDirectory(
                        name,
                        Directory.GetLastWriteTimeUtc(directory),
                        nowUtc,
                        days,
                        isCurrent))
                {
                    return;
                }

                Directory.Delete(directory, recursive: true);
                deleted++;
            }
            catch (Exception exception)
            {
                failed++;
                _log.Warn($"清理旧截图目录失败 {name}: {exception.Message}");
            }
        }

        try
        {
            foreach (string child in Directory.EnumerateDirectories(root))
            {
                if (ArtifactDirectoryName().IsMatch(Path.GetFileName(child)))
                {
                    // 旧布局：时间戳目录直接在 profile 根下。
                    TryDelete(child);
                    continue;
                }

                try
                {
                    foreach (string session in Directory.EnumerateDirectories(child))
                    {
                        TryDelete(session);
                    }

                    // 只删空目录：清理完旧会话后不要留下每个连接一个空壳。
                    if (!Directory.EnumerateFileSystemEntries(child).Any())
                    {
                        try
                        {
                            Directory.Delete(child, recursive: false);
                        }
                        catch
                        {
                            // 刚有新连接写入时删不掉，下一轮再试。
                        }
                    }
                }
                catch (Exception exception)
                {
                    failed++;
                    _log.Warn($"枚举客户端产物目录失败 {Path.GetFileName(child)}: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            _log.Warn($"枚举截图产物目录失败: {exception.Message}");
            return;
        }

        if (deleted > 0 || failed > 0)
        {
            _log.Info($"截图产物清理：删除 {deleted} 个，失败 {failed} 个，保留 {days} 天");
        }
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

    [GeneratedRegex(@"^\d{8}-\d{6}-\d+$")]
    private static partial Regex ArtifactDirectoryName();
}
