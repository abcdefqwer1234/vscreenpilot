using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VScreenPilot.Infrastructure;
using VScreenPilot.Perception;
using VScreenPilot.Platform.Logging;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Services;


namespace VScreenPilot.Mcp;

/// <summary>vsp://artifacts 资源列表与读取；大图按需生成 0.25× preview。</summary>
internal sealed class ResourceHandler
{
    public const string ArtifactPrefix = "vsp://artifacts/";
    private const int PageSize = 50;

    private readonly IArtifactService _artifacts;
    private readonly Func<string> _clientId;

    public ResourceHandler(IArtifactService artifacts, Func<string>? clientId = null)
    {
        _artifacts = artifacts;
        _clientId = clientId ?? (() => "c0");
    }

    /// <summary>资源回调没有工具作用域，目录必须按连接客户端显式取。</summary>
    internal string ArtifactDirectory() => _artifacts.DirectoryFor(_clientId());

    public ValueTask<ListResourcesResult> ListAsync(
        RequestContext<ListResourcesRequestParams> context, CancellationToken cancellationToken)
    {
        var resources = new List<Resource>();
        int offset = 0;
        string? cursor = context.Params?.Cursor;

        if (!string.IsNullOrEmpty(cursor) && !int.TryParse(cursor, out offset))
        {
            offset = 0;
        }

        offset = Math.Max(0, offset);
        string? nextCursor = null;

        try
        {
            List<string> files = System.IO.Directory
                .EnumerateFiles(ArtifactDirectory(), "*.png")
                .Where(file => !file.EndsWith(".preview.png", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            foreach (string file in files.Skip(offset).Take(PageSize))
            {
                string name = Path.GetFileName(file);
                long size = new FileInfo(file).Length;
                resources.Add(new Resource
                {
                    Uri = ArtifactPrefix + name,
                    Name = name,
                    Description = $"Screenshot artifact ({size} bytes); append ?preview=1 for a smaller image",
                    MimeType = "image/png",
                    Size = size,
                });
            }

            if (offset + resources.Count < files.Count)
            {
                nextCursor = (offset + resources.Count).ToString();
            }
        }
        catch (Exception exception)
        {
            // artifact 目录不可用时返回空列表，不影响工具调用。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        return ValueTask.FromResult(new ListResourcesResult
        {
            Resources = resources,
            NextCursor = nextCursor,
        });
    }

    public ValueTask<ReadResourceResult> ReadAsync(
        RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        string uri = context.Params?.Uri ?? string.Empty;

        if (!uri.StartsWith(ArtifactPrefix, StringComparison.Ordinal))
        {
            throw new McpException($"Unknown resource: {uri}");
        }

        ArtifactRequest request = ParseArtifactRequest(uri[ArtifactPrefix.Length..]);
        string path = ResolvePath(request);

        if (!File.Exists(path))
        {
            throw new McpException($"Resource not found: {uri}");
        }

        return ValueTask.FromResult(new ReadResourceResult
        {
            Contents =
            [
                new BlobResourceContents
                {
                    Uri = uri,
                    MimeType = "image/png",
                    Blob = File.ReadAllBytes(path),
                },
            ],
        });
    }

    private static ArtifactRequest ParseArtifactRequest(string raw)
    {
        string name = raw;
        bool preview = false;
        bool forceFull = false;
        int queryIndex = name.IndexOf('?');

        if (queryIndex >= 0)
        {
            string[] query = name[(queryIndex + 1)..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries);
            preview = query.Any(part => part.Equals("preview=1", StringComparison.OrdinalIgnoreCase));
            forceFull = query.Any(part => part.Equals("full=1", StringComparison.OrdinalIgnoreCase));
            name = name[..queryIndex];
        }

        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
        {
            throw new McpException($"Invalid artifact name: {name}");
        }

        return new ArtifactRequest(name, preview, forceFull);
    }

    private string ResolvePath(ArtifactRequest request)
    {
        string path = Path.Combine(ArtifactDirectory(), request.Name);
        bool wantsPreview = request.Preview ||
                            (!request.ForceFull &&
                             File.Exists(path) &&
                             new FileInfo(path).Length > Limits.ResourcePreviewBytes);

        if (!wantsPreview)
        {
            return path;
        }

        string previewPath = Path.ChangeExtension(path, ".preview.png");

        if (!File.Exists(previewPath))
        {
            TryCreatePreview(path, previewPath);
        }

        return File.Exists(previewPath) ? previewPath : path;
    }

    private sealed record ArtifactRequest(string Name, bool Preview, bool ForceFull);

    private static void TryCreatePreview(string sourcePath, string previewPath)
    {
        try
        {
            byte[] data = File.ReadAllBytes(sourcePath);
            PngImage image = PngReader.Decode(data);
            var bgra = new byte[image.Rgba.Length];

            for (int index = 0; index < image.Rgba.Length; index += 4)
            {
                bgra[index] = image.Rgba[index + 2];
                bgra[index + 1] = image.Rgba[index + 1];
                bgra[index + 2] = image.Rgba[index];
                bgra[index + 3] = image.Rgba[index + 3];
            }

            byte[] preview = PngWriter.Encode(
                bgra, image.Width, image.Height, Limits.PreviewScale, out _, out _);
            File.WriteAllBytes(previewPath, preview);
        }
        catch (Exception exception)
        {
            // 生成失败就退回原图；资源读取仍然可用。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
    }
}
