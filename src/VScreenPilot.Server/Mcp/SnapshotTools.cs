using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Errors;
using VScreenPilot.Json;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Services;
using VScreenPilot.Tools;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;

namespace VScreenPilot.Mcp;

/// <summary>Snapshot 组：snapshot / capture_screen。</summary>
internal sealed class SnapshotTools
{
    private readonly ISnapshotService _snapshot;
    private readonly IArtifactService _artifacts;

    public SnapshotTools(ISnapshotService snapshot, IArtifactService artifacts)
    {
        _snapshot = snapshot;
        _artifacts = artifacts;
    }

    public ToolResponse Snapshot(JsonElement arguments)
    {
        double scale = OptionalDouble(arguments, "scale", 0.5);

        if (arguments.TryGetProperty("scale", out _) && (scale < 0.1 || scale > 1.0))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"scale 必须在 0.1..1.0（收到 {scale}）",
                new JsonObject { ["field"] = "scale", ["value"] = scale },
                retryable: false,
                hint: "快照截图只接受 0.1–1.0",
                stage: "validate");
        }

        bool annotate = OptionalBool(arguments, "annotate", false);

        SnapshotOutput result = _snapshot.Snapshot(new SnapshotRequest(
            OptionalNullableInt(arguments, "pid"),
            OptionalInt(arguments, "max_depth", 4),
            OptionalBool(arguments, "interactive_only", true),
            OptionalInt(arguments, "max_nodes", 300),
            OptionalBool(arguments, "include_values", true),
            OptionalBool(arguments, "include_screenshot", false) || annotate,
            scale,
            annotate));

        if (result.Png is null)
        {
            return ToolResponseWriter.FromView(result.Data);
        }

        if (result.Png.Length <= Limits.InlineLimitBytes)
        {
            return ToolResponseWriter.FromView(result.Data).AddImage(result.Png);
        }

        // 大图先落盘、把路径写进 data，再序列化；否则 Text() 之后追加的字段不会进入响应。
        string artifactUri;
        string artifactName;
        SnapshotView data = result.Data;

        try
        {
            ArtifactOutput artifact = _artifacts.Write(result.Png, "snapshot", result.PreviewPng);
            artifactUri = artifact.Uri;
            artifactName = Path.GetFileName(artifact.Path);
            data = result.Data with
            {
                ScreenshotPath = artifact.Path,
                ScreenshotUri = artifact.Uri,
                ScreenshotResourceUri = artifact.Uri,
            };
        }
        catch (Exception exception)
        {
            data = result.Data with { ScreenshotError = exception.Message };
            return ToolResponseWriter.FromView(data);
        }

        bool hasPreview = result.PreviewPng is not null;

        if (hasPreview)
        {
            // preview 与主图来自同一帧，不再二次截屏。
            data = data with { ScreenshotPreview = result.PreviewMeta };
        }

        ToolResponse response = ToolResponseWriter.FromView(data)
            .AddResourceLink(artifactUri, artifactName, "image/png", result.Png.Length);

        if (!hasPreview)
        {
            return response;
        }

        return response.AddImage(result.PreviewPng!);
    }

    public ToolResponse CaptureScreen(JsonElement arguments)
    {
        int? x = null, y = null, width = null, height = null;

        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("region", out JsonElement region))
        {
            if (region.ValueKind != JsonValueKind.Object)
            {
                throw new ToolException("E_INVALID_ARG", "region 必须是对象");
            }

            x = RequiredInt(region, "x");
            y = RequiredInt(region, "y");
            width = RequiredInt(region, "width");
            height = RequiredInt(region, "height");
        }

        // 全屏默认降到 0.5：UI 截图缩小一半仍然可读，体积/上下文成本降 4 倍。
        double scale = OptionalDouble(arguments, "scale", x is null ? 0.5 : 1.0);

        if (arguments.TryGetProperty("scale", out _) && (scale < 0.1 || scale > 1.0))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"scale 必须在 0.1..1.0（收到 {scale}）",
                new JsonObject { ["field"] = "scale", ["value"] = scale },
                retryable: false,
                hint: "0 或 10 这类值不会再被静默钳制；需要小图请用 0.1–1.0",
                stage: "validate");
        }

        string? savePath = OptionalString(arguments, "save_path");
        bool inline = OptionalBool(arguments, "inline", true);

        // 关键发现：全屏“悬崖”主要不是 GDI 采集，而是大 PNG 的 Base64/JSON/stdio 回传。
        // 366KB PNG 内联 → 客户端约 1.2s；落盘 + 200B 元数据 → 约 35ms。
        // 因此超过 128KB 的图默认落 artifact，并额外内联一张 0.25× 预览。
        const int InlineLimit = Limits.InlineLimitBytes;
        bool hasSavePath = !string.IsNullOrWhiteSpace(savePath);

        // preview 与主图同帧；small 图会多编码一次缩略图，但省掉了第二次截屏。
        CaptureOutput capture = _snapshot.Capture(new CaptureRequest(
            x, y, width, height, scale, savePath,
            StoreFrame: true,
            PreviewScale: inline ? Math.Max(Limits.MinScale, scale * Limits.PreviewScale) : null));

        byte[] png = capture.Png;
        JsonObject meta = VspJson.ToNode(capture.Meta).AsObject();

        if (inline && png.Length <= InlineLimit)
        {
            meta["inline"] = true;
            return ToolResponse.Text(meta).AddImage(png);
        }

        string? artifactUri = null;
        string? artifactName = null;

        // 显式 save_path 就是唯一正式产物：不再额外写 artifact（R4）。
        if (!hasSavePath)
        {
            try
            {
                ArtifactOutput artifact = _artifacts.Write(png, "shot", capture.PreviewPng);
                meta["artifact_path"] = artifact.Path;
                meta["artifact_uri"] = artifact.Uri;
                meta["resource_uri"] = artifact.Uri;
                meta["path"] = artifact.Path;
                artifactUri = artifact.Uri;
                artifactName = Path.GetFileName(artifact.Path);
            }
            catch (Exception exception)
            {
                meta["artifact_error"] = exception.Message;
            }
        }

        meta["inline"] = false;

        bool hasPreview = inline && capture.PreviewPng is not null;

        if (hasPreview)
        {
            meta["preview"] = VspJson.ToNode(capture.PreviewMeta);
        }

        ToolResponse response = ToolResponse.Text(meta);

        if (artifactUri is not null)
        {
            // 标准 resource_link：客户端可直接按 URI 读取大图，不必依赖自定义字段。
            response.AddResourceLink(artifactUri, artifactName ?? "shot.png", "image/png", png.Length);
        }

        if (hasPreview)
        {
            response.AddImage(capture.PreviewPng!);
        }

        return response;
    }
}
