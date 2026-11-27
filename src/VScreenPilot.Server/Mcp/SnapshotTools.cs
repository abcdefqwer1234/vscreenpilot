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

        if (arguments.TryGetProperty("scale", out _) && (scale < 0.1 || scale > 4.0))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"scale 必须在 0.1..4.0（收到 {scale}）",
                new JsonObject { ["field"] = "scale", ["value"] = scale },
                retryable: false,
                hint: "0.1–1.0 为缩小/原尺寸，1.0–4.0 为超采样放大",
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
        (int? x, int? y, int? width, int? height) = ParseRegion(arguments);
        double scale = ParseScale(arguments, x is null);
        string? savePath = OptionalString(arguments, "save_path");
        bool inline = OptionalBool(arguments, "inline", true);

        // preview 与主图同帧；small 图会多编码一次缩略图，但省掉了第二次截屏。
        CaptureOutput capture = _snapshot.Capture(new CaptureRequest(
            x, y, width, height, scale, savePath,
            StoreFrame: true,
            PreviewScale: inline ? Math.Max(Limits.MinScale, scale * Limits.PreviewScale) : null,
            Clip: !string.Equals(
                OptionalString(arguments, "on_out_of_bounds") ?? "clip",
                "error",
                StringComparison.OrdinalIgnoreCase)));

        byte[] png = capture.Png;
        JsonObject meta = VspJson.ToNode(capture.Meta).AsObject();

        if (inline && png.Length <= Limits.InlineLimitBytes)
        {
            meta["inline"] = true;
            return ToolResponse.Text(meta).AddImage(png);
        }

        (string? artifactUri, string? artifactName) =
            AttachArtifact(meta, png, capture.PreviewPng, savePath);
        meta["inline"] = false;
        bool hasPreview = inline && capture.PreviewPng is not null;

        if (hasPreview)
        {
            meta["preview"] = VspJson.ToNode(capture.PreviewMeta);
        }

        return BuildLargeResponse(meta, png, capture.PreviewPng, hasPreview, artifactUri, artifactName);
    }

    private static (int? X, int? Y, int? Width, int? Height) ParseRegion(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("region", out JsonElement region))
        {
            return (null, null, null, null);
        }

        if (region.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "region 必须是对象");
        }

        return (RequiredInt(region, "x"), RequiredInt(region, "y"),
            RequiredInt(region, "width"), RequiredInt(region, "height"));
    }

    private static double ParseScale(JsonElement arguments, bool fullScreen)
    {
        // 全屏默认降到 0.5：UI 截图缩小一半仍然可读，体积/上下文成本降 4 倍。
        double scale = OptionalDouble(arguments, "scale", fullScreen ? 0.5 : 1.0);

        if (!arguments.TryGetProperty("scale", out _) || scale is >= 0.1 and <= 4.0)
        {
            return scale;
        }

        throw new ToolException(
            "E_INVALID_ARG",
            $"scale 必须在 0.1..4.0（收到 {scale}）",
            new JsonObject { ["field"] = "scale", ["value"] = scale },
            retryable: false,
            hint: "0.1–1.0 为缩小/原尺寸，1.0–4.0 为超采样放大",
            stage: "validate");
    }

    private (string? Uri, string? Name) AttachArtifact(
        JsonObject meta, byte[] png, byte[]? previewPng, string? savePath)
    {
        // 显式 save_path 就是唯一正式产物：不再额外写 artifact（R4）。
        if (!string.IsNullOrWhiteSpace(savePath))
        {
            return (null, null);
        }

        try
        {
            ArtifactOutput artifact = _artifacts.Write(png, "shot", previewPng);
            meta["artifact_path"] = artifact.Path;
            meta["artifact_uri"] = artifact.Uri;
            meta["resource_uri"] = artifact.Uri;
            meta["path"] = artifact.Path;
            return (artifact.Uri, Path.GetFileName(artifact.Path));
        }
        catch (Exception exception)
        {
            meta["artifact_error"] = exception.Message;
            return (null, null);
        }
    }

    private static ToolResponse BuildLargeResponse(
        JsonObject meta,
        byte[] png,
        byte[]? previewPng,
        bool hasPreview,
        string? artifactUri,
        string? artifactName)
    {
        ToolResponse response = ToolResponse.Text(meta);

        if (artifactUri is not null)
        {
            // 标准 resource_link：客户端可直接按 URI 读取大图，不必依赖自定义字段。
            response.AddResourceLink(artifactUri, artifactName ?? "shot.png", "image/png", png.Length);
        }

        if (hasPreview)
        {
            response.AddImage(previewPng!);
        }

        return response;
    }

}
