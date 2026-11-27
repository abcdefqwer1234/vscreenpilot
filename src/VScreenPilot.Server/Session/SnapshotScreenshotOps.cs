using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Json;
using VScreenPilot.Ui;
using VScreenPilot.Perception;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>ScreenshotOps：SnapshotBuilder 的拆分子集。</summary>
internal sealed class SnapshotScreenshotOps
{
    private readonly SnapshotBuilder _core;

    public SnapshotScreenshotOps(SnapshotBuilder core) => _core = core;
    private const long MaxOutputPixels = 40_000_000;

    internal (byte[]? Png, byte[]? PreviewPng, JsonObject? PreviewMeta) BuildScreenshot(
        JsonObject data, JsonObject tree, DisplayInfo display,
        bool includeScreenshot, bool annotate, double scale)
    {
        if (!includeScreenshot)
        {
            return (null, null, null);
        }

        return annotate
            ? BuildAnnotatedScreenshot(data, tree, display, scale)
            : CapturedScreenshot(data, display, scale);
    }

    private (byte[]? Png, byte[]? PreviewPng, JsonObject? PreviewMeta) BuildAnnotatedScreenshot(
        JsonObject data, JsonObject tree, DisplayInfo display, double scale)
    {
        EnsureOutputWithinLimit(display, scale);
        var annotations = new JsonArray();
        _core.ConvertOps.CollectAnnotations(tree, display, annotations, maxCount: 80);
        data["annotations"] = annotations;

        // 直接用源帧做叠加，再缩放编码；网格让模型估坐标时有参照。
        (byte[] bgra, int captureWidth, int captureHeight) =
            _core._capture.CaptureRegion(display.X, display.Y, display.Width, display.Height);

        double effectiveScale = Math.Clamp(scale, 0.1, 4.0);
        int thickness = Math.Max(2, (int)Math.Ceiling(2 / effectiveScale));
        int numberScale = Math.Max(2, (int)Math.Ceiling(2 / effectiveScale));

        var canvas = new PngCanvas(bgra, captureWidth, captureHeight);
        PngOverlay.DrawGrid(canvas, 100);

        foreach (JsonNode? item in annotations)
        {
            DrawAnnotation(canvas, item, display, thickness, numberScale);
        }

        byte[] png = PngWriter.Encode(
            bgra, captureWidth, captureHeight, effectiveScale,
            out int outputWidth, out int outputHeight);
        byte[] previewPng = PngWriter.Encode(
            bgra, captureWidth, captureHeight, Math.Max(Limits.MinScale, effectiveScale * Limits.PreviewScale),
            out int previewWidth, out int previewHeight);

        data["screenshot"] = BuildAnnotatedMeta(
            display, (captureWidth, captureHeight), (outputWidth, outputHeight), effectiveScale, png.Length);
        JsonObject previewMeta = BuildPreviewMeta(
            previewWidth, previewHeight, effectiveScale, previewPng.Length);

        return (png, previewPng, previewMeta);
    }

    private static void DrawAnnotation(
        PngCanvas canvas,
        JsonNode? item,
        DisplayInfo display,
        int thickness,
        int numberScale)
    {
        if (item is not JsonObject annotation ||
            annotation["bounds"] is not JsonObject bounds)
        {
            return;
        }

        int index = annotation["index"]?.GetValue<int>() ?? 0;
        int bx = (bounds["x"]?.GetValue<int>() ?? 0) - display.X;
        int by = (bounds["y"]?.GetValue<int>() ?? 0) - display.Y;
        int bw = bounds["width"]?.GetValue<int>() ?? 0;
        int bh = bounds["height"]?.GetValue<int>() ?? 0;
        (byte r, byte g, byte b) = PngOverlay.ColorFor(Math.Max(0, index - 1));

        PngOverlay.DrawBox(canvas, new PngBox(bx, by, bw, bh), (r, g, b), thickness);
        PngOverlay.DrawNumber(canvas, bx + 2, by + 2, index, numberScale);
    }

    private static JsonObject BuildAnnotatedMeta(
        DisplayInfo display,
        (int Width, int Height) capture,
        (int Width, int Height) output,
        double effectiveScale,
        int pngBytes) =>
        new()
        {
            ["x"] = display.X,
            ["y"] = display.Y,
            ["width"] = output.Width,
            ["height"] = output.Height,
            ["source_width"] = capture.Width,
            ["source_height"] = capture.Height,
            ["scale"] = effectiveScale,
            ["png_bytes"] = pngBytes,
            ["backend"] = "gdi+overlay",
            ["baseline"] = new JsonObject
            {
                ["x"] = display.X,
                ["y"] = display.Y,
                ["width"] = capture.Width,
                ["height"] = capture.Height,
            },
        };

    private static void EnsureOutputWithinLimit(DisplayInfo display, double scale)
    {
        double effectiveScale = Math.Clamp(scale, 0.1, 4.0);
        double outputPixels = display.Width * (double)display.Height * effectiveScale * effectiveScale;

        if (outputPixels <= MaxOutputPixels)
        {
            return;
        }

        throw new ToolException(
            "E_INVALID_ARG",
            "放大后的快照输出像素超过 40MP 上限",
            new JsonObject
            {
                ["width"] = display.Width,
                ["height"] = display.Height,
                ["scale"] = effectiveScale,
                ["output_pixels"] = (long)outputPixels,
            },
            retryable: false,
            hint: "降低 scale（快照默认 0.5，最大 4）",
            stage: "validate");
    }

    private static JsonObject BuildPreviewMeta(
        int previewWidth, int previewHeight, double effectiveScale, int pngBytes) =>
        new()
        {
            ["width"] = previewWidth,
            ["height"] = previewHeight,
            ["scale"] = Math.Max(Limits.MinScale, effectiveScale * Limits.PreviewScale),
            ["png_bytes"] = pngBytes,
        };

    private (byte[]? Png, byte[]? PreviewPng, JsonObject? PreviewMeta) CapturedScreenshot(
        JsonObject data, DisplayInfo display, double scale)
    {
        EnsureOutputWithinLimit(display, scale);
        // 快照截图不写帧缓存；preview 与主图同帧，且不阻塞 _refGate（已在锁外）。
        CaptureResult capture = _core._capture.CaptureForSnapshot(
            Math.Clamp(scale, 0.1, 4.0),
            Math.Max(Limits.MinScale, scale * Limits.PreviewScale));

        JsonObject? previewMeta = capture.PreviewMeta is null
            ? null
            : VspJson.ToNode(capture.PreviewMeta).AsObject();
        data["screenshot"] = VspJson.ToNode(capture.Meta);

        return (capture.Png, capture.PreviewPng, previewMeta);
    }
}
