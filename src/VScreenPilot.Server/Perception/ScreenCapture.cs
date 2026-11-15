using System.Runtime.InteropServices;

namespace VScreenPilot.Perception;

/// <summary>
/// GDI 屏幕位图拷贝（内存级，不落盘）。
/// 复用 screen DC / memory DC / DIB：旧实现每次调用都新建 3.7MB DIB，
/// 120 次全屏会产生 ~1.4GB 分配，并显著加剧 IDD 上的性能悬崖。
/// </summary>
internal static class ScreenCapture
{
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint DibRgbColors = 0;
    private const int MaxSurfaces = 4;
    private static readonly bool IncludeLayered =
        Environment.GetEnvironmentVariable("VSP_CAPTURE_LAYERED") == "1";

    private static readonly object Sync = new();
    private static readonly Dictionary<(int Width, int Height), Surface> Surfaces = new();
    private static IntPtr _screenDc;
    private static IntPtr _pixelDc;
    private static long _captureCount;
    private static long _slowCount;
    private static double _lastMs;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    private sealed class Surface
    {
        public IntPtr Dc;
        public IntPtr Dib;
        public IntPtr Bits;
        public IntPtr Previous;
        public int Width;
        public int Height;
        public long LastUsedTicks;

        public void Dispose()
        {
            if (Dc != IntPtr.Zero && Previous != IntPtr.Zero)
            {
                SelectObject(Dc, Previous);
            }

            if (Dib != IntPtr.Zero)
            {
                DeleteObject(Dib);
            }

            if (Dc != IntPtr.Zero)
            {
                DeleteDC(Dc);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr hdcDest, int x, int y, int width, int height, IntPtr hdcSrc, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    public static (byte[] Bgra, int Width, int Height) Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        long started = Environment.TickCount64;

        lock (Sync)
        {
            Surface surface = RentSurface(width, height);
            uint rop = SrcCopy | (IncludeLayered ? CaptureBlt : 0);

            if (!BitBlt(surface.Dc, 0, 0, width, height, EnsureScreenDc(), x, y, rop))
            {
                // 显示拓扑变化后缓存的 screen DC 可能失效；重置后重试一次。
                ResetCachedResources();
                surface = RentSurface(width, height);

                if (!BitBlt(surface.Dc, 0, 0, width, height, EnsureScreenDc(), x, y, rop) &&
                    !BitBlt(surface.Dc, 0, 0, width, height, EnsureScreenDc(), x, y, SrcCopy | CaptureBlt))
                {
                    throw new InvalidOperationException(
                        $"BitBlt 失败 (Win32 {Marshal.GetLastWin32Error()})");
                }
            }

            int size = width * height * 4;
            var buffer = new byte[size];
            Marshal.Copy(surface.Bits, buffer, 0, size);

            _captureCount++;
            _lastMs = Environment.TickCount64 - started;

            if (_lastMs > 500)
            {
                _slowCount++;
            }

            return (buffer, width, height);
        }
    }

    /// <summary>单像素读取：复用同一个 screen DC，避免为 1×1 创建 DIB。</summary>
    public static byte[] CapturePixel(int x, int y)
    {
        lock (Sync)
        {
            IntPtr dc = EnsurePixelDc();
            uint color = GetPixel(dc, x, y);

            if (color == 0xFFFFFFFF)
            {
                // CLR_INVALID：桌面切换/DC 失效，重建一次。
                if (_pixelDc != IntPtr.Zero)
                {
                    _ = ReleaseDC(IntPtr.Zero, _pixelDc);
                    _pixelDc = IntPtr.Zero;
                }

                color = GetPixel(EnsurePixelDc(), x, y);
            }

            // GetPixel 返回 COLORREF = 0x00BBGGRR（低字节是 R），需要转成 BGRA。
            // 旧实现把低字节当 B，导致红色/蓝色通道互换，wait_for color 会误判。
            return new[]
            {
                (byte)((color >> 16) & 0xFF),
                (byte)((color >> 8) & 0xFF),
                (byte)(color & 0xFF),
                (byte)255,
            };
        }
    }

    public static string Diagnostics()
    {
        lock (Sync)
        {
            return $"captures={_captureCount},slow={_slowCount},last_ms={_lastMs:F0}," +
                   $"surfaces={Surfaces.Count},layered={IncludeLayered}";
        }
    }

    /// <summary>显示器切换/驱动重挂后，旧 DC 与 DIB 可能全部失效，必须整体重建。</summary>
    private static void ResetCachedResources()
    {
        foreach (Surface surface in Surfaces.Values)
        {
            surface.Dispose();
        }

        Surfaces.Clear();

        if (_screenDc != IntPtr.Zero)
        {
            _ = ReleaseDC(IntPtr.Zero, _screenDc);
            _screenDc = IntPtr.Zero;
        }

        if (_pixelDc != IntPtr.Zero)
        {
            _ = ReleaseDC(IntPtr.Zero, _pixelDc);
            _pixelDc = IntPtr.Zero;
        }
    }

    private static IntPtr EnsureScreenDc()
    {
        if (_screenDc == IntPtr.Zero)
        {
            _screenDc = GetDC(IntPtr.Zero);
        }

        return _screenDc;
    }

    private static IntPtr EnsurePixelDc()
    {
        if (_pixelDc == IntPtr.Zero)
        {
            _pixelDc = GetDC(IntPtr.Zero);
        }

        return _pixelDc;
    }

    private static Surface RentSurface(int width, int height)
    {
        var key = (width, height);

        if (Surfaces.TryGetValue(key, out Surface? existing))
        {
            existing.LastUsedTicks = Environment.TickCount64;
            return existing;
        }

        if (Surfaces.Count >= MaxSurfaces)
        {
            (int Width, int Height) oldestKey = Surfaces
                .OrderBy(pair => pair.Value.LastUsedTicks)
                .First().Key;
            Surfaces[oldestKey].Dispose();
            Surfaces.Remove(oldestKey);
        }

        IntPtr screen = EnsureScreenDc();
        IntPtr dc = CreateCompatibleDC(screen);

        if (dc == IntPtr.Zero)
        {
            // 显示器被摘除/重建后旧 screen DC 会失效；重置缓存的 DC 与 surface 再试一次。
            ResetCachedResources();
            screen = EnsureScreenDc();
            dc = CreateCompatibleDC(screen);

            if (dc == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleDC 失败");
            }
        }

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
                biSizeImage = (uint)(width * height * 4),
            },
        };

        IntPtr dib = CreateDIBSection(screen, ref info, DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);

        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            DeleteDC(dc);
            throw new InvalidOperationException("CreateDIBSection 失败");
        }

        IntPtr previous = SelectObject(dc, dib);
        var surface = new Surface
        {
            Dc = dc,
            Dib = dib,
            Bits = bits,
            Previous = previous,
            Width = width,
            Height = height,
            LastUsedTicks = Environment.TickCount64,
        };

        Surfaces[key] = surface;
        return surface;
    }
}
