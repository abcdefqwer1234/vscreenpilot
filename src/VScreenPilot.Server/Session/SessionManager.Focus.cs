using VScreenPilot.Apps;
using VScreenPilot.Interop;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    /// <summary>
    /// 常驻焦点护栏：虚拟屏不可见，本会话应用不应长期占据前台，
    /// 否则用户在物理屏上的键盘输入会打进去。每 200ms 检查一次。
    /// </summary>
    public void StartFocusGuard(IntPtr preferredUserForeground = default)
    {
        if (_focusGuardRunning)
        {
            if (preferredUserForeground != IntPtr.Zero &&
                !IsTrackedWindow(preferredUserForeground))
            {
                _lastUserForeground = preferredUserForeground;
            }

            return;
        }

        _focusGuardRunning = true;

        IntPtr initial = preferredUserForeground != IntPtr.Zero &&
                         !IsTrackedWindow(preferredUserForeground)
            ? preferredUserForeground
            : WindowManager.GetForegroundWindow();

        _lastUserForeground = IsTrackedWindow(initial) ? IntPtr.Zero : initial;

        _focusGuardThread = new Thread(FocusGuardLoop)
        {
            IsBackground = true,
            Name = "FocusGuard",
            Priority = ThreadPriority.BelowNormal,
        };
        _focusGuardThread.Start();
        _log.Info("焦点护栏启动");
    }

    public void PauseFocusGuard()
    {
        _focusGuardPaused = true;

        // 等待可能已经在进行的归还动作结束，否则它可能在键盘注入开始后才抢前台。
        lock (_focusActionGate)
        {
        }
    }

    public void ResumeFocusGuard() => _focusGuardPaused = false;

    private bool IsTrackedWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);

        lock (_gate)
        {
            return _apps.Any(app => app.Pid == (int)pid && IsLiveTrackedApp(app));
        }
    }

    private void FocusGuardLoop()
    {
        while (_focusGuardRunning)
        {
            Thread.Sleep(200);

            try
            {
                IntPtr foreground = WindowManager.GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                {
                    continue;
                }

                if (!IsTrackedWindow(foreground))
                {
                    // 用户主动切换的非本会话窗口：记住它，作为归还目标。
                    _lastUserForeground = foreground;
                    continue;
                }

                if (_focusGuardPaused)
                {
                    continue;
                }

                IntPtr target = _lastUserForeground;
                if (target == IntPtr.Zero ||
                    target == foreground ||
                    !NativeMethods.IsWindow(target))
                {
                    continue;
                }

                long now = Environment.TickCount64;
                if (now < Interlocked.Read(ref _nextFocusRestoreTicks))
                {
                    continue;
                }

                // 与 PauseFocusGuard 串行：注入期间绝不能把前台夺回。
                lock (_focusActionGate)
                {
                    if (_focusGuardPaused)
                    {
                        continue;
                    }

                    Interlocked.Exchange(ref _nextFocusRestoreTicks, now + 500);
                    RestoreForeground(target, logFailure: false);
                }
            }
            catch
            {
                // 焦点护栏失败不能影响主流程。
            }
        }
    }

    private void StopFocusGuard()
    {
        _focusGuardRunning = false;
        _focusGuardThread?.Join(500);
        _focusGuardThread = null;
    }
}
