using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Session;

/// <summary>ArtifactOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionArtifactOps
{
    internal readonly SessionManager _host;

    public SessionArtifactOps(SessionManager host) => _host = host;

    /// <summary>大截图/快照的落盘目录；按客户端隔离，通过 vsp://artifacts/ 资源 URI 暴露。</summary>
    internal string Directory
    {
        get
        {
            string clientId = _host.Clients.CurrentId;

            lock (_host._artifactGate)
            {
                if (_host._artifactDirectories.TryGetValue(clientId, out string? cached))
                {
                    return cached;
                }

                string created = CreateDirectory(clientId);
                _host._artifactDirectories[clientId] = created;
                return created;
            }
        }
    }

    /// <summary>截图/快照的 profile 根目录；按会话再建时间戳子目录。</summary>
    internal string Root => ArtifactStore.RootFor(_host.ProfileName);

    private string CreateDirectory(string clientId)
    {
        string root = Path.Combine(
            Root,
            clientId,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
        System.IO.Directory.CreateDirectory(root);
        return root;
    }

    internal void StartMaintenance() => _host.MaintenanceService.Start();

    internal void SweepOldArtifacts() => _host.MaintenanceService.SweepNow();

    /// <summary>daemon 长期运行时也要清理；最多每小时扫一次，避免每个连接都遍历目录。</summary>
    internal void SweepArtifactsPeriodically() => _host.MaintenanceService.SweepPeriodically();
}
