using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>焦点护栏所需的最小上下文；用委托读取实时状态。</summary>
internal sealed record FocusContext(
    Func<IntPtr, bool> IsTrackedWindow,
    Func<IntPtr, bool> IsAnyClientWindow,
    Action<IntPtr> RestoreForeground,
    LogRing Log);

/// <summary>常驻焦点护栏：本会话窗口占据前台时把焦点归还用户窗口。</summary>
internal sealed class FocusGuard
{
    private volatile bool _paused;
    private volatile bool _running;

    internal bool IsPaused => _paused;
    internal long ResumeAtTicks => Interlocked.Read(ref _resumeAtTicks);
    private readonly object _actionGate = new();
    private Thread? _thread;
    private IntPtr _lastUserForeground;
    private long _nextRestoreTicks;
    private long _resumeAtTicks;
    private readonly FocusContext _host;

    public FocusGuard(FocusContext host) => _host = host;

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
        long now = Environment.TickCount64;
        long existing = Interlocked.Read(ref _resumeAtTicks);

        // leaseMs=0 是“本次注入短暂暂停”，不能清掉仍然有效的租约到期时间；
        // 新租约也不能缩短另一个客户端更长的租约暂停，取两者较大值。
        long requested = leaseMs > 0 ? now + leaseMs : 0;
        long resumeAt = existing > now ? existing : 0;

        if (requested > resumeAt)
        {
            resumeAt = requested;
        }

        Interlocked.Exchange(ref _resumeAtTicks, resumeAt);

        // 等待可能已经在进行的归还动作结束，否则它可能在键盘注入开始后才抢前台。
        lock (_actionGate)
        {
        }
    }

    /// <summary>
    /// 恢复焦点护栏。默认不清除仍在有效期内的租约暂停：
    /// 普通输入结束时的 Resume 不能把另一个客户端的 keep_foreground/lease_input 清掉。
    /// 只有确实没有剩余租约（或 release 显式清理）时才传 force=true。
    /// </summary>
    public void Resume(bool force = false)
    {
        if (!force)
        {
            long resumeAt = Interlocked.Read(ref _resumeAtTicks);

            if (resumeAt > Environment.TickCount64)
            {
                return;
            }
        }

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
                GuardOnce();
            }
            catch (Exception exception)
            {
                // 焦点护栏失败不能影响主流程。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }
        }
    }

    private void GuardOnce()
    {
        IntPtr foreground = WindowManager.GetForegroundWindow();

        if (foreground == IntPtr.Zero)
        {
            return;
        }

        if (!_host.IsTrackedWindow(foreground))
        {
            TrackUserWindow(foreground);
            return;
        }

        if (!TryResumeFromPause())
        {
            return;
        }

        IntPtr target = _lastUserForeground;

        if (target == IntPtr.Zero || target == foreground || !NativeMethods.IsWindow(target))
        {
            return;
        }

        long now = Environment.TickCount64;

        if (now < Interlocked.Read(ref _nextRestoreTicks))
        {
            return;
        }

        // 与 PauseFocusGuard 串行：注入期间绝不能把前台夺回。
        lock (_actionGate)
        {
            if (_paused)
            {
                return;
            }

            Interlocked.Exchange(ref _nextRestoreTicks, now + 500);
            _host.RestoreForeground(target);
        }
    }

    private void TrackUserWindow(IntPtr foreground)
    {
        // 用户主动切换的非本会话窗口才作为归还目标；
        // 其他客户端的 app 不算用户窗口，否则会在租约切换时抢前台。
        if (!_host.IsAnyClientWindow(foreground))
        {
            _lastUserForeground = foreground;
        }
    }

    private bool TryResumeFromPause()
    {
        if (!_paused)
        {
            return true;
        }

        long resumeAt = Interlocked.Read(ref _resumeAtTicks);

        if (resumeAt <= 0 || Environment.TickCount64 < resumeAt)
        {
            return false;
        }

        _paused = false;
        Interlocked.Exchange(ref _resumeAtTicks, 0);
        _host.Log.Info("前台租约到期，焦点护栏自动恢复");
        return true;
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
    }
}
