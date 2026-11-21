using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Json;
using VScreenPilot.Perception;
using VScreenPilot.Session;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Mcp;

internal sealed partial class McpTransport
{
    private ValueTask<ListResourcesResult> HandleListResourcesAsync(
        RequestContext<ListResourcesRequestParams> context, CancellationToken cancellationToken)
    {
        using IDisposable? clientScope = _client is null
            ? null
            : _session.Clients.Use(_client);
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
            List<string> files = Directory
                .EnumerateFiles(_session.ArtifactDirectory, "*.png")
                // preview 是资源的附属文件，不单独列出。
                .Where(file => !file.EndsWith(".preview.png", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            foreach (string file in files.Skip(offset).Take(ResourcePageSize))
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

    private ValueTask<ReadResourceResult> HandleReadResourceAsync(
        RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        using IDisposable? clientScope = _client is null
            ? null
            : _session.Clients.Use(_client);
        string uri = context.Params?.Uri ?? string.Empty;

        if (!uri.StartsWith(ArtifactPrefix, StringComparison.Ordinal))
        {
            throw new McpException($"Unknown resource: {uri}");
        }

        string name = uri[ArtifactPrefix.Length..];
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

        string path = Path.Combine(_session.ArtifactDirectory, name);

        // 大图默认返回 preview：避免资源读取又把 300KB+ 原图塞回客户端。
        if (!preview && !forceFull && File.Exists(path))
        {
            preview = new FileInfo(path).Length > Limits.ResourcePreviewBytes;
        }

        if (preview)
        {
            string previewPath = Path.ChangeExtension(path, ".preview.png");

            if (!File.Exists(previewPath))
            {
                TryCreatePreview(path, previewPath);
            }

            if (File.Exists(previewPath))
            {
                path = previewPath;
            }
        }

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

    /// <summary>旧 artifact 没有 preview 时按需生成 0.25× 小图，避免 resources/read 回传大 Blob。</summary>
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
