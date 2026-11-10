using System.Diagnostics;
using System.Text.Json.Nodes;
using VScreenPilot.Core.Displays;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Perception;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    public (byte[] Png, JsonObject Meta) CaptureScreen(
        int? x, int? y, int? width, int? height, double scale, string? savePath)
    {
        DisplayInfo display = EnsureEnvironment();
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
            ScreenCapture.Capture(left, top, regionWidth, regionHeight);

        string frameId = StoreFrame(bgra, capturedWidth, capturedHeight, left, top);

        // 与 PngWriter 内部保持一致，meta 里返回真正生效的 scale。
        double effectiveScale = Math.Clamp(scale <= 0 ? 1.0 : scale, 0.1, 1.0);
        byte[] png = PngWriter.Encode(
            bgra, capturedWidth, capturedHeight, effectiveScale,
            out int outputWidth, out int outputHeight);
        double encodeMs = stopwatch.Elapsed.TotalMilliseconds;

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

        return (png, meta);
    }

    public (byte[] Bgra, int Width, int Height) CaptureRegion(int x, int y, int width, int height)
    {
        return ScreenCapture.Capture(x, y, width, height);
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

            const long maxBytes = 64L * 1024 * 1024;

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

    private static bool IsUnder(string path, string root)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
