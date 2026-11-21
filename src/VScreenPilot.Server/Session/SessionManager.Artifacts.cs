using VScreenPilot.Infrastructure;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    internal const string ArtifactRetentionEnv = ArtifactStore.RetentionEnv;

    internal const int DefaultArtifactRetentionDays = ArtifactStore.DefaultRetentionDays;

    private readonly MaintenanceService _maintenance;

    private void StartMaintenance() => _maintenance.Start();

    private void SweepOldArtifacts() => _maintenance.SweepNow();

    /// <summary>daemon 长期运行时也要清理；最多每小时扫一次，避免每个连接都遍历目录。</summary>
    private void SweepArtifactsPeriodically() => _maintenance.SweepPeriodically();
}
