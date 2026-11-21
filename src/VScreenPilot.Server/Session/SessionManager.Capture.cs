using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
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
        int? x, int? y, int? width, int? height, double scale, string? savePath) =>
        _capture.CaptureScreen(x, y, width, height, scale, savePath);

    internal CaptureResult CaptureScreenEx(
        int? x, int? y, int? width, int? height, double scale, string? savePath,
        bool storeFrame, double? previewScale) =>
        _capture.CaptureScreenEx(x, y, width, height, scale, savePath, storeFrame, previewScale);

    private readonly ScreenCapture _screenCapture = new();

    public (byte[] Bgra, int Width, int Height) CaptureRegion(int x, int y, int width, int height)
    {
        try
        {
            return _screenCapture.Capture(x, y, width, height);
        }
        catch (InvalidOperationException exception)
        {
            // 显示被摘除/安全桌面/DC 失效：给出可操作错误，而不是 E_INTERNAL。
            bool interactive = EnvironmentProbe.IsInputDesktopAvailable();

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
}
