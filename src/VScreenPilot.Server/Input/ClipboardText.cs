using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace VScreenPilot.Input;

/// <summary>
/// 剪贴板读写。必须运行在 STA 线程上；用单独的工作线程串行化，
/// 避免与 UI 线程/其他工具的剪贴板操作互相踩。
/// </summary>
internal static class ClipboardText
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    private static readonly Lazy<Worker> Shared = new(() => new Worker(), isThreadSafe: true);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    public static string? GetText() => Shared.Value.Invoke(ReadCore);

    public static void SetText(string text) =>
        Shared.Value.Invoke(() => { WriteCore(text); return true; });

    /// <summary>保存当前文本；非文本内容无法还原，调用方应只在必要时走剪贴板通道。</summary>
    public static string? Capture() => GetText();

    public static void Restore(string? previous) => SetText(previous ?? string.Empty);

    private static string? ReadCore()
    {
        if (!TryOpen())
        {
            return null;
        }

        try
        {
            IntPtr handle = GetClipboardData(CfUnicodeText);

            if (handle == IntPtr.Zero)
            {
                return null;
            }

            IntPtr pointer = GlobalLock(handle);

            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void WriteCore(string text)
    {
        if (!TryOpen())
        {
            throw new InvalidOperationException("剪贴板被其他程序占用，无法写入");
        }

        try
        {
            EmptyClipboard();
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text + '\0');
            IntPtr memory = GlobalAlloc(GmemMoveable, (UIntPtr)(uint)bytes.Length);

            if (memory == IntPtr.Zero)
            {
                throw new OutOfMemoryException("GlobalAlloc 失败");
            }

            IntPtr pointer = GlobalLock(memory);

            if (pointer == IntPtr.Zero)
            {
                GlobalFree(memory);
                throw new InvalidOperationException("GlobalLock 失败");
            }

            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            GlobalUnlock(memory);

            // 成功 SetClipboardData 后内存归系统所有，不能 GlobalFree。
            if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
            {
                GlobalFree(memory);
                throw new InvalidOperationException("SetClipboardData 失败");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TryOpen()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    private sealed class Worker
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public Worker()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "vsp-clipboard",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void Invoke(Action action) => Run(action);

        public T Invoke<T>(Func<T> func)
        {
            T result = default!;
            // 必须显式转成 Action：否则重载解析会再次选中 Invoke<T>(Func<T>)，无限递归。
            Run(() => result = func());
            return result;
        }

        private void Run(Action action)
        {
            using var done = new ManualResetEventSlim();
            Exception? error = null;

            _queue.Add(() =>
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    error = exception;
                }
                finally
                {
                    done.Set();
                }
            });

            if (!done.Wait(TimeSpan.FromSeconds(3)))
            {
                throw new TimeoutException("剪贴板操作超时");
            }

            if (error is not null)
            {
                throw error;
            }
        }

        private void Run()
        {
            foreach (Action action in _queue.GetConsumingEnumerable())
            {
                action();
            }
        }
    }
}
