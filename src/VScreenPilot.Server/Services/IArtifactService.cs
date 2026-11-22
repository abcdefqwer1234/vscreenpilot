using VScreenPilot.Session;

namespace VScreenPilot.Services;

/// <summary>产物落盘：截图写入 artifact 目录并返回 URI。</summary>
internal interface IArtifactService
{
    string Directory { get; }
    ArtifactOutput Write(byte[] png, string prefix, byte[]? preview = null);
}

internal sealed record ArtifactOutput(string Path, string Uri);

internal sealed class ArtifactToolService : IArtifactService
{
    private readonly SessionManager _session;

    public ArtifactToolService(SessionManager session) => _session = session;

    public string Directory => _session.ArtifactDirectory;

    public ArtifactOutput Write(byte[] png, string prefix, byte[]? preview = null)
    {
        string fileName =
            $"{prefix}-{DateTime.Now:HHmmss-fff}-{Guid.NewGuid().ToString("N")[..8]}.png";
        string path = Path.Combine(_session.ArtifactDirectory, fileName);
        File.WriteAllBytes(path, png);

        if (preview is not null)
        {
            // 同目录写出 0.25× 预览图，res 读取时按 ?preview=1 命中。
            File.WriteAllBytes(Path.ChangeExtension(path, ".preview.png"), preview);
        }

        return new ArtifactOutput(path, $"vsp://artifacts/{fileName}");
    }
}
