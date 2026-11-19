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

    // 已知的“无法用文本方式还原”的二进制格式。
    private const uint CfBitmap = 2;
    private const uint CfMetafilePict = 3;
    private const uint CfDib = 8;
    private const uint CfEnhMetaFile = 14;
    private const uint CfHDrop = 15;
    private const uint CfDibV5 = 17;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClipboardFormatNameW(
        uint format, [Out] System.Text.StringBuilder name, int capacity);

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

    public static void Restore(string? previous)
    {
        if (previous is null)
        {
            // 原来没有文本：清空，而不是留下一个空字符串条目。
            Clear();
            return;
        }

        SetText(previous);
    }

    public static void Clear() =>
        Shared.Value.Invoke(() =>
        {
            if (TryOpen())
            {
                try
                {
                    EmptyClipboard();
                }
                finally
                {
                    CloseClipboard();
                }
            }

            return true;
        });

    /// <summary>
    /// 剪贴板里是否有文本以外的内容（图片/文件/图元）。
    /// 这些格式无法在 WriteCore 之后还原，长文本输入必须绕开剪贴板通道（S7）。
    /// </summary>
    public static bool HasNonTextFormats() => Shared.Value.Invoke(HasNonTextCore);

    private static bool HasNonTextCore()
    {
        if (!TryOpen())
        {
            // 打不开就无法保证还原，按“不安全”处理，让调用方走 SendInput。
            return true;
        }

        try
        {
            uint format = 0;

            while ((format = EnumClipboardFormats(format)) != 0)
            {
                if (format is 1 or 7 or CfUnicodeText or 16)
                {
                    continue; // CF_TEXT / CF_OEMTEXT / CF_UNICODETEXT / CF_LOCALE
                }

                if (format is CfBitmap or CfMetafilePict or CfDib or CfEnhMetaFile or CfHDrop or CfDibV5)
                {
                    return true;
                }

                var name = new System.Text.StringBuilder(128);
                int length = GetClipboardFormatNameW(format, name, name.Capacity);

                if (length > 0)
                {
                    string value = name.ToString(0, length);

                    if (value.Contains("PNG", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("JPEG", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("JFIF", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("GIF", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("Image", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("File", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("Shell", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("HTML", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("Rich Text", StringComparison.OrdinalIgnoreCase) ||
                        value.Contains("RTF", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

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
#pragma warning disable CA2201 // 分配失败用 OutOfMemoryException 表达，调用方按“内存不足”统一处理
                throw new OutOfMemoryException("GlobalAlloc 失败");
#pragma warning restore CA2201
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

#pragma warning disable CA1001 // 队列随进程结束释放；Worker 与 AppDomain 同生命周期，无需独立 Dispose
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
