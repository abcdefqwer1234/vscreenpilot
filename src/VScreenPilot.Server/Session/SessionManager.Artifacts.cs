using System.Text.RegularExpressions;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    internal const string ArtifactRetentionEnv = "VSP_ARTIFACT_RETENTION_DAYS";

    internal const int DefaultArtifactRetentionDays = 7;

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
    /// </summary>
    private void SweepOldArtifacts()
    {
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
        string? current = _artifactDirectory;

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(directory);
                bool isCurrent = current is not null &&
                                 string.Equals(
                                     Path.GetFullPath(directory),
                                     Path.GetFullPath(current),
                                     StringComparison.OrdinalIgnoreCase);

                try
                {
                    if (!ShouldDeleteArtifactDirectory(
                            name,
                            Directory.GetLastWriteTimeUtc(directory),
                            nowUtc,
                            days,
                            isCurrent))
                    {
                        continue;
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

    [GeneratedRegex(@"^\d{8}-\d{6}-\d+$")]
    private static partial Regex ArtifactDirectoryName();
}
