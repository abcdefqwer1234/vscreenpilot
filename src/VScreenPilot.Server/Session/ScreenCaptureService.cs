using System.Diagnostics;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Mcp;
using VScreenPilot.Perception;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>截图工具所需的最小宿主接口。</summary>
internal interface IScreenCaptureHost
{
    DisplayInfo EnsureDisplay();
    (byte[] Bgra, int Width, int Height) CaptureRegion(int x, int y, int width, int height);
    string StoreFrame(byte[] bgra, int width, int height, int x, int y);
    string ValidateSavePath(string path);
}

/// <summary>capture_screen：一次采集、主图 + preview 两次编码。</summary>
internal sealed class ScreenCaptureService
{
    private readonly IScreenCaptureHost _host;

    public ScreenCaptureService(IScreenCaptureHost host) => _host = host;

    public (byte[] Png, CaptureView Meta) CaptureScreen(
        int? x, int? y, int? width, int? height, double scale, string? savePath)
    {
        SessionManager.CaptureResult result = CaptureScreenEx(
            x, y, width, height, scale, savePath, storeFrame: true, previewScale: null);
        return (result.Png, result.Meta);
    }

    /// <summary>
    /// 一次采集、两次编码：preview 与主图来自同一帧（R12），
    /// storeFrame=false 时快照/预览不会挤掉 assert_region 的基线（R13）。
    /// </summary>
    internal SessionManager.CaptureResult CaptureScreenEx(
        int? x, int? y, int? width, int? height, double scale, string? savePath,
        bool storeFrame, double? previewScale)
    {
        DisplayInfo display = _host.EnsureDisplay();
        var stopwatch = Stopwatch.StartNew();

        int left = x ?? display.X;
        int top = y ?? display.Y;
        int regionWidth = width ?? display.Width;
        int regionHeight = height ?? display.Height;

        if (regionWidth <= 0 || regionHeight <= 0)
        {
            throw new ToolException("E_INVALID_ARG", "截图区域宽高必须大于 0");
        }

        VisualSupport.RequireRegionInDisplay(display, left, top, regionWidth, regionHeight, "capture_screen");

        (byte[] bgra, int capturedWidth, int capturedHeight) =
            _host.CaptureRegion(left, top, regionWidth, regionHeight);

        string frameId = storeFrame
            ? _host.StoreFrame(bgra, capturedWidth, capturedHeight, left, top)
            : string.Empty;

        // 与 PngWriter 内部保持一致，meta 里返回真正生效的 scale。
        double effectiveScale = Math.Clamp(scale <= 0 ? 1.0 : scale, 0.1, 1.0);
        var encodeWatch = Stopwatch.StartNew();
        byte[] png = PngWriter.Encode(
            bgra, capturedWidth, capturedHeight, effectiveScale,
            out int outputWidth, out int outputHeight);
        double encodeMs = encodeWatch.Elapsed.TotalMilliseconds;

        byte[]? previewPng = null;
        PreviewView? previewMeta = null;

        if (previewScale is double requestedPreview)
        {
            double previewEffective = Math.Clamp(requestedPreview, 0.1, 1.0);
            previewPng = PngWriter.Encode(
                bgra, capturedWidth, capturedHeight, previewEffective,
                out int previewWidth, out int previewHeight);
            previewMeta = new PreviewView(
                previewEffective, previewWidth, previewHeight, previewPng.Length, frameId);
        }

        if (!string.IsNullOrWhiteSpace(savePath))
        {
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
            Path: string.IsNullOrWhiteSpace(savePath) ? null : _host.ValidateSavePath(savePath));

        return new SessionManager.CaptureResult(png, meta, previewPng, previewMeta);
    }
}
