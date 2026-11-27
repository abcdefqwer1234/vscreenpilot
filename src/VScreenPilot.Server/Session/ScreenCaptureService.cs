using System.Diagnostics;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Perception;
using VScreenPilot.Views;
using VScreenPilot.Errors;
using VScreenPilot.Services;

namespace VScreenPilot.Session;

/// <summary>截图工具所需的最小宿主接口。</summary>
internal sealed record ScreenCaptureContext(
    Func<DisplayInfo> EnsureDisplay,
    Func<int, int, int, int, (byte[] Bgra, int Width, int Height)> CaptureRegion,
    Func<byte[], int, int, int, int, string> StoreFrame,
    Func<string, string> ValidateSavePath);

/// <summary>capture_screen：一次采集、主图 + preview 两次编码。</summary>
internal sealed class ScreenCaptureService
{
    private readonly ScreenCaptureContext _host;

    public ScreenCaptureService(ScreenCaptureContext host) => _host = host;

    public (byte[] Png, CaptureView Meta) CaptureScreen(
        int? x, int? y, int? width, int? height, double scale, string? savePath)
    {
        CaptureResult result = CaptureScreenEx(new CaptureRequest(
            x, y, width, height, scale, savePath, StoreFrame: true, PreviewScale: null));
        return (result.Png, result.Meta);
    }

    /// <summary>
    /// 一次采集、两次编码：preview 与主图来自同一帧（R12），
    /// storeFrame=false 时快照/预览不会挤掉 assert_region 的基线（R13）。
    /// </summary>
    internal CaptureResult CaptureScreenEx(CaptureRequest request)
    {
        DisplayInfo display = _host.EnsureDisplay();
        var stopwatch = Stopwatch.StartNew();

        (int requestedLeft, int requestedTop, int requestedWidth, int requestedHeight) =
            ResolveRegion(request, display);
        int left = requestedLeft;
        int top = requestedTop;
        int regionWidth = requestedWidth;
        int regionHeight = requestedHeight;
        bool clipped = false;

        if (request.Clip)
        {
            var clamped = VisualSupport.ClipRegionToDisplay(
                display, left, top, regionWidth, regionHeight, "capture_screen");
            left = clamped.X;
            top = clamped.Y;
            regionWidth = clamped.Width;
            regionHeight = clamped.Height;
            clipped = clamped.Clipped;
        }
        else
        {
            VisualSupport.RequireRegionInDisplay(
                display, left, top, regionWidth, regionHeight, "capture_screen");
        }

        double effectiveScale = ResolveScale(request.Scale);
        double outputPixels = regionWidth * (double)regionHeight * effectiveScale * effectiveScale;

        if (outputPixels > 40_000_000)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                "放大后的输出像素超过 40MP 上限",
                new JsonObject
                {
                    ["width"] = regionWidth,
                    ["height"] = regionHeight,
                    ["scale"] = effectiveScale,
                    ["output_pixels"] = (long)outputPixels,
                },
                retryable: false,
                hint: "缩小区或降低 scale（最大 4）",
                stage: "validate");
        }

        (byte[] bgra, int capturedWidth, int capturedHeight) =
            _host.CaptureRegion(left, top, regionWidth, regionHeight);

        string frameId = request.StoreFrame
            ? _host.StoreFrame(bgra, capturedWidth, capturedHeight, left, top)
            : string.Empty;

        // 与 PngWriter 内部保持一致，meta 里返回真正生效的 scale。
        var encodeWatch = Stopwatch.StartNew();
        byte[] png = PngWriter.Encode(
            bgra, capturedWidth, capturedHeight, effectiveScale,
            out int outputWidth, out int outputHeight);
        double encodeMs = encodeWatch.Elapsed.TotalMilliseconds;

        (byte[]? previewPng, PreviewView? previewMeta) = BuildPreview(
            request.PreviewScale, bgra, (capturedWidth, capturedHeight), frameId);

        SaveIfRequested(request.SavePath, png);

        // 超过 500ms 明确标记：客户端不要误以为工具挂死。
        // assert_region 的 baseline 使用未缩放的源帧尺寸，和这里的输出尺寸可能不同。
        var meta = new CaptureView(
            FrameId: frameId,
            X: left,
            Y: top,
            Width: outputWidth,
            Height: outputHeight,
            SourceWidth: capturedWidth,
            SourceHeight: capturedHeight,
            Scale: effectiveScale,
            PngBytes: png.Length,
            DurationMs: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            EncodeMs: Math.Round(encodeMs, 2),
            Backend: "gdi",
            SlowCapture: stopwatch.Elapsed.TotalMilliseconds > 500,
            Baseline: new BaselineView(left, top, capturedWidth, capturedHeight),
            Path: string.IsNullOrWhiteSpace(request.SavePath)
                ? null
                : _host.ValidateSavePath(request.SavePath),
            Clipped: clipped,
            RequestedRegion: clipped
                ? new BoundsView(requestedLeft, requestedTop, requestedWidth, requestedHeight)
                : null);

        return new CaptureResult(png, meta, previewPng, previewMeta);
    }

    private static (int Left, int Top, int Width, int Height) ResolveRegion(
        CaptureRequest request, DisplayInfo display)
    {
        int left = request.X ?? display.X;
        int top = request.Y ?? display.Y;
        int regionWidth = request.Width ?? display.Width;
        int regionHeight = request.Height ?? display.Height;

        if (regionWidth > 0 && regionHeight > 0)
        {
            return (left, top, regionWidth, regionHeight);
        }

        throw new ToolException("E_INVALID_ARG", "截图区域宽高必须大于 0");
    }

    private static double ResolveScale(double scale) => Math.Clamp(scale <= 0 ? 1.0 : scale, 0.1, 4.0);

    private static (byte[]? Png, PreviewView? Meta) BuildPreview(
        double? previewScale, byte[] bgra, (int Width, int Height) captured, string frameId)
    {
        if (previewScale is not double requestedPreview)
        {
            return (null, null);
        }

        double previewEffective = Math.Clamp(requestedPreview, 0.1, 1.0);
        byte[] previewPng = PngWriter.Encode(
            bgra, captured.Width, captured.Height, previewEffective,
            out int previewWidth, out int previewHeight);

        return (previewPng, new PreviewView(
            previewEffective, previewWidth, previewHeight, previewPng.Length, frameId));
    }

    private void SaveIfRequested(string? savePath, byte[] png)
    {
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return;
        }

        string fullPath = _host.ValidateSavePath(savePath);

        try
        {
            string? directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 不覆盖已有文件：避免 agent 无意中改写用户文档/上一次的证据。
            using var stream = new FileStream(
                fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(png);
        }
        catch (IOException exception)
        {
            throw new ToolException(
                "E_IO",
                $"写入截图失败（目标文件可能已存在）: {exception.Message}",
                new JsonObject { ["path"] = fullPath },
                retryable: true,
                hint: "换一个文件名，或先删除旧文件",
                stage: "capture");
        }
        catch (Exception exception)
        {
            throw new ToolException("E_IO", $"写入截图失败: {exception.Message}");
        }
    }
}
