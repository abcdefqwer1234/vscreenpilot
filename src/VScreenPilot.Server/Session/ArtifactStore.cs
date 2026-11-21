using System.Text.RegularExpressions;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>
/// 截图产物的保留策略与清理。不含日志/会话依赖：日志通过委托传入，
/// 这样它可以独立于 Server 存在并单测。
/// 目录布局：<c>&lt;profile&gt;/&lt;clientId&gt;/&lt;yyyyMMdd-HHmmss&gt;-&lt;pid&gt;</c>，
/// 同时兼容旧的 <c>&lt;profile&gt;/&lt;yyyyMMdd-HHmmss&gt;-&lt;pid&gt;</c>。
/// </summary>
internal sealed partial class ArtifactStore
{
    internal const string RetentionEnv = "VSP_ARTIFACT_RETENTION_DAYS";

    /// <summary>artifact 根目录：%LOCALAPPDATA%\VScreenPilot\artifacts\&lt;profile&gt;。</summary>
    internal static string RootFor(string profile) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VScreenPilot",
        "artifacts",
        profile);

    internal const int DefaultRetentionDays = 7;

    private readonly string _root;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;

    public ArtifactStore(string root, Action<string> info, Action<string> warn)
    {
        _root = root;
        _info = info;
        _warn = warn;
    }

    /// <summary>解析保留天数：未设置/非法 → 默认 7；0 → 关闭清理。</summary>
    public static int ResolveRetentionDays(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultRetentionDays;
        }

        if (!int.TryParse(raw.Trim(), out int days) || days < 0)
        {
            return DefaultRetentionDays;
        }

        return days;
    }

    /// <summary>只清理本程序命名的会话目录；当前会话目录永不删除。</summary>
    public static bool ShouldDeleteDirectory(
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

        if (!DirectoryName().IsMatch(directoryName))
        {
            return false;
        }

        return lastWriteUtc <= nowUtc.AddDays(-retentionDays);
    }

    /// <summary>清理过期截图目录。画面可能含用户数据，默认保留 7 天。</summary>
    public void Sweep(IReadOnlyCollection<string> currentDirectories)
    {
        int days = ResolveRetentionDays(Environment.GetEnvironmentVariable(RetentionEnv));

        if (days <= 0)
        {
            _info("截图产物保留策略已关闭（VSP_ARTIFACT_RETENTION_DAYS=0）");
            return;
        }

        string root = _root;

        if (!Directory.Exists(root))
        {
            return;
        }

        int deleted = 0;
        int failed = 0;
        DateTime nowUtc = DateTime.UtcNow;
        string[] current = currentDirectories.ToArray();

        void TryDelete(string directory)
        {
            string name = Path.GetFileName(directory);
            bool isCurrent = current.Any(path => string.Equals(
                Path.GetFullPath(directory),
                Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase));

            try
            {
                if (!ShouldDeleteDirectory(
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
                _warn($"清理旧截图目录失败 {name}: {exception.Message}");
            }
        }

        try
        {
            foreach (string child in Directory.EnumerateDirectories(root))
            {
                if (DirectoryName().IsMatch(Path.GetFileName(child)))
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
                        catch (Exception exception)
                        {
                            // 刚有新连接写入时删不掉，下一轮再试。
                            LogSink.Debug($"忽略异常: {exception.Message}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    failed++;
                    _warn($"枚举客户端产物目录失败 {Path.GetFileName(child)}: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            _warn($"枚举截图产物目录失败: {exception.Message}");
            return;
        }

        if (deleted > 0 || failed > 0)
        {
            _info($"截图产物清理：删除 {deleted} 个，失败 {failed} 个，保留 {days} 天");
        }
    }

    [GeneratedRegex(@"^\d{8}-\d{6}-\d+$")]
    private static partial Regex DirectoryName();
}
