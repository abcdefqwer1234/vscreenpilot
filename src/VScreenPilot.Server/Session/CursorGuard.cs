using VScreenPilot.Core.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Interop;

namespace VScreenPilot.Session;

/// <summary>
/// 光标护栏：虚拟屏是扩展桌面，用户可能把光标移进不可见区域。
/// 空闲时若光标进入虚拟屏，弹回相邻物理屏边缘。注入期间暂停。
/// </summary>
internal sealed class CursorGuard : IDisposable
{
    private readonly DisplayInfo _virtualDisplay;
    private readonly IReadOnlyList<DisplayInfo> _physicalDisplays;
    private readonly int _fallbackX;
    private readonly LogRing _log;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _paused;
    private long _lastLogTimestamp;

    public CursorGuard(DisplayInfo virtualDisplay, LogRing log)
    {
        _virtualDisplay = virtualDisplay;
        _fallbackX = Math.Max(0, virtualDisplay.X - 1);
        _physicalDisplays = DisplayTopology.List()
            .Where(display => display.Attached && !display.IsParsec &&
                              display.Width > 0 && display.Height > 0)
            .ToList();
        _log = log;
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "CursorGuard",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
        _log.Info($"光标护栏启动: 虚拟屏 x={_virtualDisplay.X}, 回弹目标=最近物理屏");
    }

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    private void Loop()
    {
        while (_running)
        {
            Thread.Sleep(150);

            if (_paused)
            {
                continue;
            }

            try
            {
                if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
                {
                    continue;
                }

                bool inside =
                    cursor.X >= _virtualDisplay.X &&
                    cursor.X < _virtualDisplay.X + _virtualDisplay.Width &&
                    cursor.Y >= _virtualDisplay.Y &&
                    cursor.Y < _virtualDisplay.Y + _virtualDisplay.Height;

                if (!inside)
                {
                    continue;
                }

                (int targetX, int targetY) = NearestPhysicalPoint(cursor);
                NativeMethods.SetCursorPos(targetX, targetY);

                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastLogTimestamp) > 5000)
                {
                    Interlocked.Exchange(ref _lastLogTimestamp, now);
                    _log.Warn($"光标进入虚拟屏 ({cursor.X},{cursor.Y})，已弹回 ({targetX},{targetY})");
                }
            }
            catch
            {
                // 护栏失败不能影响主流程。
            }
        }
    }

    private (int X, int Y) NearestPhysicalPoint(NativeMethods.POINT cursor)
    {
        if (_physicalDisplays.Count == 0)
        {
            int y = Math.Clamp(
                cursor.Y,
                _virtualDisplay.Y,
                _virtualDisplay.Y + _virtualDisplay.Height - 1);
            return (_fallbackX, y);
        }

        DisplayInfo best = _physicalDisplays[0];
        long bestDistance = long.MaxValue;

        foreach (DisplayInfo display in _physicalDisplays)
        {
            (int x, int y) = ClampTo(display, cursor);
            long distance = Math.Abs((long)x - cursor.X) + Math.Abs((long)y - cursor.Y);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = display;
            }
        }

        return ClampTo(best, cursor);
    }

    private static (int X, int Y) ClampTo(DisplayInfo display, NativeMethods.POINT cursor)
    {
        int x = Math.Clamp(cursor.X, display.X, display.X + display.Width - 1);
        int y = Math.Clamp(cursor.Y, display.Y, display.Y + display.Height - 1);
        return (x, y);
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
    }
}
