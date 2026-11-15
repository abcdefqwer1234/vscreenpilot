using System.Diagnostics;
using System.Text.Json.Nodes;
using VScreenPilot.Core.Displays;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Perception;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    internal sealed record CaptureResult(
        byte[] Png, JsonObject Meta, byte[]? PreviewPng, JsonObject? PreviewMeta);

    public (byte[] Png, JsonObject Meta) CaptureScreen(
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
        JsonObject? previewMeta = null;

        if (previewScale is double requestedPreview)
        {
            double previewEffective = Math.Clamp(requestedPreview, 0.1, 1.0);
            previewPng = PngWriter.Encode(
                bgra, capturedWidth, capturedHeight, previewEffective,
                out int previewWidth, out int previewHeight);
            previewMeta = new JsonObject
            {
                ["scale"] = previewEffective,
                ["width"] = previewWidth,
                ["height"] = previewHeight,
                ["png_bytes"] = previewPng.Length,
                ["source_frame_id"] = frameId,
            };
        }

        if (!string.IsNullOrWhiteSpace(savePath))
        {
            string fullPath = ValidateSavePath(savePath);

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

        var meta = new JsonObject
        {
            ["frame_id"] = frameId,
            ["x"] = left,
            ["y"] = top,
            ["width"] = outputWidth,
            ["height"] = outputHeight,
            ["source_width"] = capturedWidth,
            ["source_height"] = capturedHeight,
            ["scale"] = effectiveScale,
            ["png_bytes"] = png.Length,
            ["duration_ms"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            ["encode_ms"] = Math.Round(encodeMs, 2),
            ["backend"] = "gdi",
            // 超过 500ms 明确标记：客户端不要误以为工具挂死。
            ["slow_capture"] = stopwatch.Elapsed.TotalMilliseconds > 500,
            // assert_region 的 baseline 使用未缩放的源帧尺寸，和这里的输出尺寸可能不同。
            ["baseline"] = new JsonObject
            {
                ["x"] = left,
                ["y"] = top,
                ["width"] = capturedWidth,
                ["height"] = capturedHeight,
            },
        };

        if (!string.IsNullOrWhiteSpace(savePath))
        {
            meta["path"] = ValidateSavePath(savePath);
        }

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
    internal string StoreFrame(byte[] bgra, int width, int height, int x, int y)
    {
        string id = $"f{Interlocked.Increment(ref _nextFrameId)}";

        lock (_frameLock)
        {
            _frames.Add(new FrameSnapshot
            {
                Id = id,
                Bgra = bgra,
                Width = width,
                Height = height,
                X = x,
                Y = y,
            });

            // 16 张 1280×720 BGRA ≈ 59MB；改成 32MB/8 张，配合 PNG 池化把稳态内存压下来。
            const long maxBytes = 32L * 1024 * 1024;

            while (_frames.Count > MaxFrames ||
                   _frames.Sum(frame => (long)frame.Bgra.Length) > maxBytes)
            {
                if (_frames.Count == 1)
                {
                    break;
                }

                _frames.RemoveAt(0);
            }
        }

        return id;
    }

    internal FrameSnapshot? FindFrame(string frameId)
    {
        lock (_frameLock)
        {
            return _frames.LastOrDefault(
                frame => string.Equals(frame.Id, frameId, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal FrameSnapshot? FindLatestFrame(int x, int y, int width, int height)
    {
        lock (_frameLock)
        {
            return _frames.LastOrDefault(frame =>
                frame.X == x && frame.Y == y &&
                frame.Width == width && frame.Height == height);
        }
    }

    /// <summary>
    /// 截图/模板落盘只允许当前工作目录或系统临时目录下的 .png，
    /// 防止工具覆盖用户文档。
    /// </summary>
    internal static string ValidateSavePath(string savePath) => ValidatePngPath(savePath, "save_path");

    internal static string ValidatePngPath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ToolException("E_INVALID_ARG", $"{label} 不能为空");
        }

        // 相对路径过去按“服务端 cwd”落盘（artifacts/server），调用方找不到文件。
        // 直接拒绝并要求绝对路径，比猜客户端 cwd 更诚实。
        if (!Path.IsPathRooted(path))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"{label} 必须是绝对路径（收到相对路径 {path}）",
                new JsonObject { ["field"] = label, ["value"] = path },
                retryable: false,
                hint: "传绝对路径；相对路径会落到服务端工作目录，客户端通常找不到",
                stage: "validate");
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolException("E_INVALID_ARG", $"{label} 无效: {exception.Message}");
        }

        // 解析符号链接/junction，避免用“工作目录内的链接”绕过根目录限制。
        try
        {
            FileSystemInfo? target = File.ResolveLinkTarget(fullPath, returnFinalTarget: true);

            if (target is not null)
            {
                fullPath = target.FullName;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 文件尚不存在/无权限读取链接：保留原路径，后续 CreateNew 会给出明确 IO 错误。
        }

        // 文件本身不存在时，父目录仍可能是 junction；逐级解析，防止写到允许根之外。
        fullPath = ResolveParentLinks(fullPath);

        if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("E_INVALID_ARG", $"{label} 只允许 .png 文件");
        }

        string tempRoot = Path.GetTempPath();

        if (!IsUnder(fullPath, Environment.CurrentDirectory) && !IsUnder(fullPath, tempRoot))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"{label} 只能落在当前工作目录或系统临时目录内",
                new JsonObject
                {
                    ["path"] = fullPath,
                    ["allowed_roots"] = new JsonArray(
                        Environment.CurrentDirectory,
                        Path.GetFullPath(tempRoot)),
                });
        }

        return fullPath;
    }

    private static string ResolveParentLinks(string fullPath)
    {
        string? directory = Path.GetDirectoryName(fullPath);

        while (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            try
            {
                if (Directory.ResolveLinkTarget(directory, returnFinalTarget: true) is DirectoryInfo target)
                {
                    return Path.Combine(
                        target.FullName, Path.GetRelativePath(directory, fullPath));
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // 无权限读取链接时继续向上；最终仍会受 IsUnder 限制。
            }

            directory = Path.GetDirectoryName(directory);
        }

        return fullPath;
    }

    private static bool IsUnder(string path, string root)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
