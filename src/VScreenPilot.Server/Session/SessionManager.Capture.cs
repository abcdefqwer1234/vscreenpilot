using System.Diagnostics;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Perception;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    internal sealed record CaptureResult(
        byte[] Png, CaptureView Meta, byte[]? PreviewPng, PreviewView? PreviewMeta);

    public (byte[] Png, CaptureView Meta) CaptureScreen(
        int? x, int? y, int? width, int? height, double scale, string? savePath)
    {
        CaptureResult result = CaptureScreenEx(
            x, y, width, height, scale, savePath, storeFrame: true, previewScale: null);
        return (result.Png, result.Meta);
    }

    /// <summary>
    /// 一次采集、两次编码：preview 与主图来自同一帧（R12），
    /// storeFrame=false 时快照/预览不会挤掉 assert_region 的基线（R13）。
    /// </summary>
    internal CaptureResult CaptureScreenEx(
        int? x, int? y, int? width, int? height, double scale, string? savePath,
        bool storeFrame, double? previewScale)
    {
        DisplayInfo display = EnsureEnvironmentFast();
        var stopwatch = Stopwatch.StartNew();

        int left = x ?? display.X;
        int top = y ?? display.Y;
        int regionWidth = width ?? display.Width;
        int regionHeight = height ?? display.Height;

        if (regionWidth <= 0 || regionHeight <= 0)
        {
            throw new ToolException("E_INVALID_ARG", "截图区域宽高必须大于 0");
        }

        RequireRegionInDisplay(display, left, top, regionWidth, regionHeight, "capture_screen");

        (byte[] bgra, int capturedWidth, int capturedHeight) =
            CaptureRegion(left, top, regionWidth, regionHeight);

        string frameId = storeFrame
            ? StoreFrame(bgra, capturedWidth, capturedHeight, left, top)
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
            string fullPath = _paths.ValidateSavePath(savePath);

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
            Path: string.IsNullOrWhiteSpace(savePath) ? null : _paths.ValidateSavePath(savePath));

        return new CaptureResult(png, meta, previewPng, previewMeta);
    }

    public (byte[] Bgra, int Width, int Height) CaptureRegion(int x, int y, int width, int height)
    {
        try
        {
            return ScreenCapture.Capture(x, y, width, height);
        }
        catch (InvalidOperationException exception)
        {
            // 显示被摘除/安全桌面/DC 失效：给出可操作错误，而不是 E_INTERNAL。
            bool interactive = IsInputDesktopAvailable();

            throw new ToolException(
                interactive ? "E_DISPLAY" : "E_NO_INTERACTIVE_DESKTOP",
                interactive
                    ? $"屏幕采集失败（虚拟屏可能已被摘除）: {exception.Message}"
                    : $"当前没有可交互桌面，无法采集: {exception.Message}",
                new JsonObject { ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height },
                retryable: true,
                hint: interactive
                    ? "调用 get_environment(ensure=true) 重新挂载虚拟屏后重试"
                    : "等待用户解锁/关闭 UAC 安全桌面后重试",
                stage: "capture");
        }
    }

    /// <summary>保存最近几帧，供 assert_region/pixel_diff 按区域或 frame_id 引用。</summary>
    internal string StoreFrame(byte[] bgra, int width, int height, int x, int y) =>
        _frames.Store(bgra, width, height, x, y, Clients.CurrentId);

    internal FrameSnapshot? FindFrame(string frameId) =>
        _frames.FindById(Clients.CurrentId, frameId);

    internal FrameSnapshot? FindLatestFrame(int x, int y, int width, int height) =>
        _frames.FindLatest(Clients.CurrentId, x, y, width, height);

    /// <summary>
    /// 截图/模板落盘只允许当前工作目录或系统临时目录下的 .png，
    /// 防止工具覆盖用户文档。
    /// </summary>
}
