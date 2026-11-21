using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>焦点护栏所需的最小宿主接口。</summary>
internal interface IFocusHost
{
    bool IsTrackedWindow(IntPtr hwnd);
    bool IsAnyClientWindow(IntPtr hwnd);
    void RestoreForeground(IntPtr target);
    LogRing Log { get; }
}

/// <summary>常驻焦点护栏：本会话窗口占据前台时把焦点归还用户窗口。</summary>
internal sealed class FocusGuard
{
    private volatile bool _paused;
    private volatile bool _running;
    private readonly object _actionGate = new();
    private Thread? _thread;
    private IntPtr _lastUserForeground;
    private long _nextRestoreTicks;
    private long _resumeAtTicks;
    private readonly IFocusHost _host;

    public FocusGuard(IFocusHost host) => _host = host;

    public void Start(IntPtr preferredUserForeground = default)
    {
        if (_running)
        {
            if (preferredUserForeground != IntPtr.Zero &&
                !_host.IsTrackedWindow(preferredUserForeground))
            {
                _lastUserForeground = preferredUserForeground;
            }

            return;
        }

        _running = true;

        IntPtr initial = preferredUserForeground != IntPtr.Zero &&
                         !_host.IsTrackedWindow(preferredUserForeground)
            ? preferredUserForeground
            : WindowManager.GetForegroundWindow();

        _lastUserForeground = _host.IsTrackedWindow(initial) ? IntPtr.Zero : initial;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "FocusGuard",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
        _host.Log.Info("焦点护栏启动");
    }

    public void Pause(int leaseMs = 0)
    {
        _paused = true;
        Interlocked.Exchange(
            ref _resumeAtTicks,
            leaseMs > 0 ? Environment.TickCount64 + leaseMs : 0);

        // 等待可能已经在进行的归还动作结束，否则它可能在键盘注入开始后才抢前台。
        lock (_actionGate)
        {
        }
    }

    public void Resume()
    {
        _paused = false;
        Interlocked.Exchange(ref _resumeAtTicks, 0);
    }

    private void Loop()
    {
        while (_running)
        {
            Thread.Sleep(200);

            try
            {
                IntPtr foreground = WindowManager.GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                {
                    continue;
                }

                if (!_host.IsTrackedWindow(foreground))
                {
                    // 用户主动切换的非本会话窗口才作为归还目标；
                    // 其他客户端的 app 不算用户窗口，否则会在租约切换时抢前台。
                    if (!_host.IsAnyClientWindow(foreground))
                    {
                        _lastUserForeground = foreground;
                    }

                    continue;
                }

                if (_paused)
                {
                    long resumeAt = Interlocked.Read(ref _resumeAtTicks);
                    if (resumeAt > 0 && Environment.TickCount64 >= resumeAt)
                    {
                        _paused = false;
                        Interlocked.Exchange(ref _resumeAtTicks, 0);
                        _host.Log.Info("前台租约到期，焦点护栏自动恢复");
                    }
                    else
                    {
                        continue;
                    }
                }

                IntPtr target = _lastUserForeground;
                if (target == IntPtr.Zero ||
                    target == foreground ||
                    !NativeMethods.IsWindow(target))
                {
                    continue;
                }

                long now = Environment.TickCount64;
                if (now < Interlocked.Read(ref _nextRestoreTicks))
                {
                    continue;
                }

                // 与 PauseFocusGuard 串行：注入期间绝不能把前台夺回。
                lock (_actionGate)
                {
                    if (_paused)
                    {
                        continue;
                    }

                    Interlocked.Exchange(ref _nextRestoreTicks, now + 500);
                    _host.RestoreForeground(target);
                }
            }
            catch (Exception exception)
            {
                // 焦点护栏失败不能影响主流程。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }
        }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
    }}
