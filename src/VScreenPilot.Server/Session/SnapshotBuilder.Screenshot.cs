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

internal sealed partial class SnapshotBuilder : IDisposable
{
    private (byte[]? Png, byte[]? PreviewPng, JsonObject? PreviewMeta) BuildScreenshot(
        JsonObject data, JsonObject tree, DisplayInfo display,
        bool includeScreenshot, bool annotate, double scale)
    {
        byte[]? png = null;
        byte[]? previewPng = null;
        JsonObject? previewMeta = null;

        if (!includeScreenshot)
        {
            return (png, previewPng, previewMeta);
        }

        if (annotate)
        {
            var annotations = new JsonArray();
            CollectAnnotations(tree, display, annotations, maxCount: 80);
            data["annotations"] = annotations;

            // 直接用源帧做叠加，再缩放编码；网格让模型估坐标时有参照。
            (byte[] bgra, int captureWidth, int captureHeight) =
                _host.CaptureRegion(display.X, display.Y, display.Width, display.Height);

            double effectiveScale = Math.Clamp(scale, 0.1, 1.0);
            int thickness = Math.Max(2, (int)Math.Ceiling(2 / effectiveScale));
            int numberScale = Math.Max(2, (int)Math.Ceiling(2 / effectiveScale));

            PngOverlay.DrawGrid(bgra, captureWidth, captureHeight, 100);

            foreach (JsonNode? item in annotations)
            {
                if (item is not JsonObject annotation ||
                    annotation["bounds"] is not JsonObject bounds)
                {
                    continue;
                }

                int index = annotation["index"]?.GetValue<int>() ?? 0;
                int bx = (bounds["x"]?.GetValue<int>() ?? 0) - display.X;
                int by = (bounds["y"]?.GetValue<int>() ?? 0) - display.Y;
                int bw = bounds["width"]?.GetValue<int>() ?? 0;
                int bh = bounds["height"]?.GetValue<int>() ?? 0;
                (byte r, byte g, byte b) = PngOverlay.ColorFor(Math.Max(0, index - 1));

                PngOverlay.DrawBox(
                    bgra, captureWidth, captureHeight, bx, by, bw, bh, r, g, b, thickness);
                PngOverlay.DrawNumber(
                    bgra, captureWidth, captureHeight, bx + 2, by + 2, index, numberScale);
            }

            png = PngWriter.Encode(
                bgra, captureWidth, captureHeight, effectiveScale,
                out int outputWidth, out int outputHeight);
            previewPng = PngWriter.Encode(
                bgra, captureWidth, captureHeight, Math.Max(Limits.MinScale, effectiveScale * Limits.PreviewScale),
                out int previewWidth, out int previewHeight);

            data["screenshot"] = new JsonObject
            {
                ["x"] = display.X,
                ["y"] = display.Y,
                ["width"] = outputWidth,
                ["height"] = outputHeight,
                ["source_width"] = captureWidth,
                ["source_height"] = captureHeight,
                ["scale"] = effectiveScale,
                ["png_bytes"] = png.Length,
                ["backend"] = "gdi+overlay",
                ["baseline"] = new JsonObject
                {
                    ["x"] = display.X,
                    ["y"] = display.Y,
                    ["width"] = captureWidth,
                    ["height"] = captureHeight,
                },
            };
            previewMeta = new JsonObject
            {
                ["width"] = previewWidth,
                ["height"] = previewHeight,
                ["scale"] = Math.Max(Limits.MinScale, effectiveScale * Limits.PreviewScale),
                ["png_bytes"] = previewPng.Length,
            };
        }
        else
        {
            // 快照截图不写帧缓存；preview 与主图同帧，且不阻塞 _refGate（已在锁外）。
            CaptureResult capture = _host.CaptureForSnapshot(
                Math.Clamp(scale, 0.1, 1.0),
                Math.Max(Limits.MinScale, scale * Limits.PreviewScale));

            png = capture.Png;
            previewPng = capture.PreviewPng;
            previewMeta = capture.PreviewMeta is null
                ? null
                : VspJson.ToNode(capture.PreviewMeta).AsObject();
            data["screenshot"] = VspJson.ToNode(capture.Meta);
        }

        return (png, previewPng, previewMeta);
    }


}
