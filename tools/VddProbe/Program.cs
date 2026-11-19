using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Interop;

namespace VddProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        Win32.SetProcessDpiAwarenessContext(Win32.DpiAwarenessContextPerMonitorAwareV2);
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 某些终端不支持 UTF-8 输出，不影响测量。
        }

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        var options = ParseOptions(args.Skip(1).ToArray());

        return command switch
        {
            "status" => Status(),
            "list" => ListDisplays(),
            "add" => Add(options),
            "remove" => Remove(options),
            "removeall" => RemoveAll(),
            "watchdog" => Watchdog(),
            "watch" => Watch(options),
            "stress" => Stress(options),
            "debug" => DebugDevices(),
            "raw" => RawDisplays(),
            "attach" => Attach(options),
            _ => Usage(),
        };
    }

    private static int Status()
    {
        Console.WriteLine($"elevated      : {IsElevated()}");
        Console.WriteLine($"driver status : {VddDevice.QueryStatus()}");

        using var vdd = new VddDevice();
        if (!vdd.TryOpen(out int error))
        {
            Console.WriteLine($"open          : FAILED err={error} ({DescribeError(error)})");
            PrintDisplays();
            return 2;
        }

        int version = vdd.GetVersion();
        Console.WriteLine($"open          : ok (version {(version >> 16) & 0xFFFF}.{version & 0xFFFF})");
        PrintDisplays();
        return 0;
    }

    private static int ListDisplays()
    {
        PrintDisplays();
        return 0;
    }

    private static int Add(Dictionary<string, string?> options)
    {
        int width = GetInt(options, "width", 1280);
        int height = GetInt(options, "height", 720);
        int frequency = GetInt(options, "hz", 60);
        int holdSeconds = GetInt(options, "hold", 15);

        var primary = DisplayTopology.FindPrimary();
        if (primary is null)
        {
            Console.WriteLine("no primary display");
            return 3;
        }

        int x = primary.X + primary.Width;
        int y = primary.Y;
        Console.WriteLine($"primary       : {primary}");
        Console.WriteLine($"target        : {width}x{height}@{frequency} at ({x},{y})");

        using var vdd = new VddDevice();
        if (!vdd.TryOpen(out int error))
        {
            Console.WriteLine($"open          : FAILED err={error} ({DescribeError(error)})");
            return 2;
        }

        vdd.StartKeepAlive();

        var stopwatch = Stopwatch.StartNew();
        int index = vdd.AddDisplay();
        Console.WriteLine($"add           : index={index} in {stopwatch.Elapsed.TotalMilliseconds:F1}ms");
        if (index < 0)
        {
            return 4;
        }

        if (!DisplayTopology.WaitForParsecAdapter(TimeSpan.FromSeconds(3), out var display) || display is null)
        {
            Console.WriteLine("topology      : virtual adapter did not appear");
            return 5;
        }

        Console.WriteLine($"adapter       : {display} (+{stopwatch.Elapsed.TotalMilliseconds:F1}ms)");

        int setResult = DisplayTopology.SetModeAndPosition(
            display.DeviceName, width, height, frequency, x, y, out int applyResult);
        Console.WriteLine($"set mode      : result={setResult} apply={applyResult} (+{stopwatch.Elapsed.TotalMilliseconds:F1}ms)");

        // 新挂载的 IDD 显示器有时不会被系统自动并入桌面，需要显式扩展拓扑。
        if (!DisplayTopology.WaitUntil(
                () => DisplayTopology.FindParsecDisplay() is { Attached: true },
                TimeSpan.FromSeconds(2)))
        {
            int extendResult = DisplayTopology.ExtendDesktop();
            Console.WriteLine($"extend        : result={extendResult} (+{stopwatch.Elapsed.TotalMilliseconds:F1}ms)");

            setResult = DisplayTopology.SetModeAndPosition(
                display.DeviceName, width, height, frequency, x, y, out applyResult);
            Console.WriteLine($"set mode(2)   : result={setResult} apply={applyResult} (+{stopwatch.Elapsed.TotalMilliseconds:F1}ms)");
        }

        if (setResult != 0)
        {
            // 部分驱动对刷新率字段敏感，退回只设置分辨率与位置。
            setResult = DisplayTopology.SetModeAndPosition(
                display.DeviceName, width, height, 0, x, y, out applyResult);
            Console.WriteLine($"set mode(3)   : result={setResult} apply={applyResult} (+{stopwatch.Elapsed.TotalMilliseconds:F1}ms)");
        }

        bool settled = DisplayTopology.WaitUntil(
            () =>
            {
                var current = DisplayTopology.FindParsecDisplay();
                return current is not null &&
                       current.Width == width &&
                       current.Height == height &&
                       current.X == x &&
                       current.Y == y;
            },
            TimeSpan.FromSeconds(3));

        Console.WriteLine($"settled       : {settled} (+{stopwatch.Elapsed.TotalMilliseconds:F1}ms)");
        PrintDisplays();
        PrintVirtualScreen();

        if (holdSeconds > 0 && settled)
        {
            Console.WriteLine($"holding {holdSeconds}s (Ctrl+C to stop)...");
            var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Set();
            };

            stop.Wait(TimeSpan.FromSeconds(holdSeconds));
        }

        stopwatch.Restart();
        bool removed = vdd.RemoveDisplay(index);
        Console.WriteLine($"remove        : ok={removed} in {stopwatch.Elapsed.TotalMilliseconds:F1}ms");

        bool gone = DisplayTopology.WaitForNoParsecDisplay(TimeSpan.FromSeconds(3));
        Console.WriteLine($"gone          : {gone} (+{stopwatch.Elapsed.TotalMilliseconds:F1}ms)");
        PrintDisplays();
        return gone ? 0 : 6;
    }

    private static int Remove(Dictionary<string, string?> options)
    {
        int index = GetInt(options, "index", 0);

        using var vdd = new VddDevice();
        if (!vdd.TryOpen(out int error))
        {
            Console.WriteLine($"open          : FAILED err={error} ({DescribeError(error)})");
            return 2;
        }

        bool ok = vdd.RemoveDisplay(index);
        Console.WriteLine($"remove({index})   : {ok} err={vdd.LastError}");
        bool gone = DisplayTopology.WaitForNoParsecDisplay(TimeSpan.FromSeconds(3));
        Console.WriteLine($"gone          : {gone}");
        PrintDisplays();
        return gone ? 0 : 6;
    }

    private static int RemoveAll()
    {
        using var vdd = new VddDevice();
        if (!vdd.TryOpen(out int error))
        {
            Console.WriteLine($"open          : FAILED err={error} ({DescribeError(error)})");
            return 2;
        }

        for (int index = 0; index < 8; index++)
        {
            bool ok = vdd.RemoveDisplay(index);
            Console.WriteLine($"remove({index})   : {ok} err={vdd.LastError}");
        }

        bool gone = DisplayTopology.WaitForNoParsecDisplay(TimeSpan.FromSeconds(3));
        Console.WriteLine($"gone          : {gone}");
        PrintDisplays();
        return gone ? 0 : 6;
    }

    private static int AddWithRetry(VddDevice vdd, int attempts = 3, int delayMs = 500)
    {
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            int index = vdd.AddDisplay();
            if (index >= 0)
            {
                return index;
            }

            Console.WriteLine($"  add attempt {attempt} failed err={vdd.LastError}");
            if (attempt < attempts)
            {
                Thread.Sleep(delayMs);
            }
        }

        return -1;
    }

    private static int Watch(Dictionary<string, string?> options)
    {
        int seconds = GetInt(options, "hold", 10);

        using var vdd = new VddDevice();
        if (!vdd.TryOpen(out int error))
        {
            Console.WriteLine($"open          : FAILED err={error} ({DescribeError(error)})");
            return 2;
        }

        vdd.StartKeepAlive();
        int index = vdd.AddDisplay();
        Console.WriteLine($"add           : index={index}");
        if (index < 0)
        {
            return 4;
        }

        for (int tick = 0; tick <= seconds * 2; tick++)
        {
            Console.WriteLine($"--- t={tick * 0.5:F1}s status={VddDevice.QueryStatus()}");
            PrintDisplays();
            Thread.Sleep(500);
        }

        bool removed = vdd.RemoveDisplay(index);
        Console.WriteLine($"remove        : {removed}");
        DisplayTopology.WaitForNoParsecDisplay(TimeSpan.FromSeconds(3));
        PrintDisplays();
        return 0;
    }

    private static int Watchdog()
    {
        using var vdd = new VddDevice();
        if (!vdd.TryOpen(out int error))
        {
            Console.WriteLine($"open          : FAILED err={error} ({DescribeError(error)})");
            return 2;
        }

        vdd.StartKeepAlive();
        int index = vdd.AddDisplay();
        Console.WriteLine($"added index {index}; exiting immediately without remove (crash simulation)");

        // 不 Dispose、不 Remove，直接退出：句柄随进程关闭，驱动应在 ~1s 内自动拔屏。
        Environment.Exit(0);
        return 0;
    }

    private static int Stress(Dictionary<string, string?> options)
    {
        int count = GetInt(options, "count", 100);
        int width = GetInt(options, "width", 1280);
        int height = GetInt(options, "height", 720);
        int frequency = GetInt(options, "hz", 60);
        int holdMs = GetInt(options, "holdms", 1000);
        int postRemoveMs = GetInt(options, "postremove", 300);
        string? csvPath = GetString(options, "csv");

        var primary = DisplayTopology.FindPrimary();
        if (primary is null)
        {
            Console.WriteLine("no primary display");
            return 3;
        }

        int x = primary.X + primary.Width;
        int y = primary.Y;

        Console.WriteLine($"elevated      : {IsElevated()}");
        Console.WriteLine($"driver status : {VddDevice.QueryStatus()}");
        Console.WriteLine($"primary       : {primary}");
        Console.WriteLine($"target        : {width}x{height}@{frequency} at ({x},{y})");
        Console.WriteLine($"cycles        : {count}, hold={holdMs}ms");

        using var vdd = new VddDevice();
        if (!vdd.TryOpen(out int error))
        {
            Console.WriteLine($"open          : FAILED err={error} ({DescribeError(error)})");
            return 2;
        }

        vdd.StartKeepAlive();

        var addIoctl = new List<double>();
        var appear = new List<double>();
        var apply = new List<double>();
        var removeIoctl = new List<double>();
        var disappear = new List<double>();
        var rows = new List<string>
        {
            "cycle,driver_index,add_ioctl_ms,appear_ms,apply_ms,hold_ms,remove_ioctl_ms,disappear_ms,total_ms,width,height,hz,x,y,ok",
        };

        int failures = 0;
        int processHandleBase = Process.GetCurrentProcess().HandleCount;

        for (int cycle = 1; cycle <= count; cycle++)
        {
            var sw = Stopwatch.StartNew();
            bool ok = true;

            int index = AddWithRetry(vdd);
            double tAdd = sw.Elapsed.TotalMilliseconds;
            if (index < 0)
            {
                failures++;
                Console.WriteLine($"#{cycle:D3} ADD FAILED");
                rows.Add($"{cycle},-1,{tAdd:F1},0,0,0,0,0,{sw.Elapsed.TotalMilliseconds:F1},{width},{height},{frequency},{x},{y},0");
                Thread.Sleep(500);
                continue;
            }

            addIoctl.Add(tAdd);

            if (!DisplayTopology.WaitForParsecAdapter(TimeSpan.FromSeconds(3), out var display) || display is null)
            {
                failures++;
                ok = false;
                Console.WriteLine($"#{cycle:D3} adapter did not appear");
                vdd.RemoveDisplay(index);
                continue;
            }

            double tAppear = sw.Elapsed.TotalMilliseconds;
            appear.Add(tAppear);

            sw.Restart();
            int setResult = DisplayTopology.SetModeAndPosition(
                display.DeviceName, width, height, frequency, x, y, out _);

            if (!DisplayTopology.WaitUntil(
                    () => DisplayTopology.FindParsecDisplay() is { Attached: true },
                    TimeSpan.FromSeconds(2)))
            {
                DisplayTopology.ExtendDesktop();
                setResult = DisplayTopology.SetModeAndPosition(
                    display.DeviceName, width, height, frequency, x, y, out _);
            }

            if (setResult != 0)
            {
                setResult = DisplayTopology.SetModeAndPosition(
                    display.DeviceName, width, height, 0, x, y, out _);
            }

            bool settled = DisplayTopology.WaitUntil(
                () =>
                {
                    var current = DisplayTopology.FindParsecDisplay();
                    return current is not null &&
                           current.Width == width &&
                           current.Height == height &&
                           current.X == x &&
                           current.Y == y;
                },
                TimeSpan.FromSeconds(3));

            double tApply = sw.Elapsed.TotalMilliseconds;
            apply.Add(tApply);

            if (!settled || setResult != 0)
            {
                ok = false;
                var current = DisplayTopology.FindParsecDisplay();
                Console.WriteLine($"#{cycle:D3} SET FAILED result={setResult} settled={settled} current={current}");
            }

            Thread.Sleep(holdMs);

            sw.Restart();
            bool removed = vdd.RemoveDisplay(index);
            double tRemove = sw.Elapsed.TotalMilliseconds;
            removeIoctl.Add(tRemove);

            bool gone = DisplayTopology.WaitForNoParsecDisplay(TimeSpan.FromSeconds(3));
            double tGone = sw.Elapsed.TotalMilliseconds;
            disappear.Add(tGone);

            if (!removed || !gone)
            {
                ok = false;
                Console.WriteLine($"#{cycle:D3} REMOVE FAILED removed={removed} gone={gone}");
                for (int extra = 0; extra < 8; extra++)
                {
                    vdd.RemoveDisplay(extra);
                }

                DisplayTopology.WaitForNoParsecDisplay(TimeSpan.FromSeconds(1));
            }

            Thread.Sleep(postRemoveMs);

            rows.Add(string.Join(',',
                cycle.ToString(CultureInfo.InvariantCulture),
                index.ToString(CultureInfo.InvariantCulture),
                tAdd.ToString("F1", CultureInfo.InvariantCulture),
                tAppear.ToString("F1", CultureInfo.InvariantCulture),
                tApply.ToString("F1", CultureInfo.InvariantCulture),
                holdMs.ToString(CultureInfo.InvariantCulture),
                tRemove.ToString("F1", CultureInfo.InvariantCulture),
                tGone.ToString("F1", CultureInfo.InvariantCulture),
                (tAdd + tAppear + tApply + holdMs + tRemove + tGone).ToString("F1", CultureInfo.InvariantCulture),
                width.ToString(CultureInfo.InvariantCulture),
                height.ToString(CultureInfo.InvariantCulture),
                frequency.ToString(CultureInfo.InvariantCulture),
                x.ToString(CultureInfo.InvariantCulture),
                y.ToString(CultureInfo.InvariantCulture),
                ok ? "1" : "0"));

            if (!ok)
            {
                failures++;
            }

            if (cycle % 10 == 0 || cycle == count)
            {
                Console.WriteLine(
                    $"#{cycle:D3} add={tAdd:F0}ms appear={tAppear:F0}ms apply={tApply:F0}ms remove={tRemove:F0}ms gone={tGone:F0}ms");
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int processHandleEnd = Process.GetCurrentProcess().HandleCount;
        long workingSet = Process.GetCurrentProcess().WorkingSet64;
        bool clean = DisplayTopology.WaitForNoParsecDisplay(TimeSpan.FromSeconds(2));

        Console.WriteLine();
        Console.WriteLine($"driver status : {VddDevice.QueryStatus()}  clean={clean}");
        Console.WriteLine($"pings         : {vdd.PingCount}");
        Console.WriteLine($"handles       : {processHandleBase} -> {processHandleEnd}");
        Console.WriteLine($"working set   : {workingSet / 1024 / 1024.0:F1} MB");
        Console.WriteLine($"add ioctl     : {Stats(addIoctl)}");
        Console.WriteLine($"appear        : {Stats(appear)}");
        Console.WriteLine($"set+settle    : {Stats(apply)}");
        Console.WriteLine($"remove ioctl  : {Stats(removeIoctl)}");
        Console.WriteLine($"disappear     : {Stats(disappear)}");
        Console.WriteLine($"failures      : {failures}/{count}");

        if (!string.IsNullOrWhiteSpace(csvPath))
        {
            File.WriteAllLines(csvPath, rows, Encoding.UTF8);
            Console.WriteLine($"csv           : {csvPath}");
        }

        return failures == 0 && clean ? 0 : 7;
    }

    private static int RawDisplays()
    {
        for (uint index = 0; index < 16; index++)
        {
            var adapter = new Win32.DISPLAY_DEVICE
            {
                cb = Marshal.SizeOf<Win32.DISPLAY_DEVICE>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceID = string.Empty,
                DeviceKey = string.Empty,
            };

            if (!Win32.EnumDisplayDevicesW(null, index, ref adapter, 0))
            {
                break;
            }

            var mode = new Win32.DEVMODE
            {
                dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>(),
                dmDeviceName = string.Empty,
                dmFormName = string.Empty,
            };
            bool hasMode = Win32.EnumDisplaySettingsW(adapter.DeviceName, Win32.EnumCurrentSettings, ref mode);

            Console.WriteLine(
                $"[{index}] {adapter.DeviceName} \"{adapter.DeviceString}\" flags=0x{adapter.StateFlags:X8} " +
                $"id={adapter.DeviceID} mode={hasMode} {mode.dmPelsWidth}x{mode.dmPelsHeight}@{mode.dmDisplayFrequency} " +
                $"at ({mode.dmPositionX},{mode.dmPositionY})");
        }

        return 0;
    }

    private static int Attach(Dictionary<string, string?> options)
    {
        string variant = (GetString(options, "variant") ?? "global").ToLowerInvariant();
        int hold = GetInt(options, "hold", 5);
        int width = GetInt(options, "width", 1280);
        int height = GetInt(options, "height", 720);
        int frequency = GetInt(options, "hz", 60);

        var physical = DisplayTopology.List()
            .Where(d => d.Attached && !d.IsParsec)
            .ToList();
        var primary = DisplayTopology.FindPrimary();

        if (primary is null)
        {
            Console.WriteLine("no primary display");
            return 3;
        }

        int x = physical.Count > 0 ? physical.Max(d => d.X + d.Width) : primary.X + primary.Width;
        int y = primary.Y;

        Console.WriteLine($"=== variant={variant} target={width}x{height}@{frequency} at ({x},{y}) ===");
        Console.WriteLine($"primary       : {primary}");

        using var vdd = new VddDevice();
        if (!vdd.TryOpen(out int error))
        {
            Console.WriteLine($"open          : FAILED err={error} ({DescribeError(error)})");
            return 2;
        }

        vdd.StartKeepAlive();
        int index = vdd.AddDisplay();
        Console.WriteLine($"add           : index={index}");
        if (index < 0)
        {
            return 4;
        }

        if (!DisplayTopology.WaitForParsecAdapter(TimeSpan.FromSeconds(3), out var adapter) ||
            adapter is null)
        {
            Console.WriteLine("adapter       : did not appear");
            vdd.RemoveDisplay(index);
            return 5;
        }

        var stopwatch = Stopwatch.StartNew();

        switch (variant)
        {
            case "device":
            {
                int result = DisplayTopology.SetModeAndPositionDeviceOnly(
                    adapter.DeviceName, width, height, frequency, x, y);
                Console.WriteLine($"device-only   : result={result} (+{stopwatch.Elapsed.TotalMilliseconds:F0}ms)");
                break;
            }

            case "extend":
            {
                int extend = DisplayTopology.ExtendDesktop();
                Console.WriteLine($"extend        : result={extend} (+{stopwatch.Elapsed.TotalMilliseconds:F0}ms)");

                int result = DisplayTopology.SetModeAndPositionDeviceOnly(
                    adapter.DeviceName, width, height, frequency, x, y);
                Console.WriteLine($"device-only   : result={result} (+{stopwatch.Elapsed.TotalMilliseconds:F0}ms)");
                break;
            }

            case "extend-noopt":
            {
                int extend = DisplayTopology.ExtendDesktopNoOptimization();
                Console.WriteLine($"extend-noopt  : result={extend} (+{stopwatch.Elapsed.TotalMilliseconds:F0}ms)");

                int result = DisplayTopology.SetModeAndPositionDeviceOnly(
                    adapter.DeviceName, width, height, frequency, x, y);
                Console.WriteLine($"device-only   : result={result} (+{stopwatch.Elapsed.TotalMilliseconds:F0}ms)");
                break;
            }

            case "none":
            {
                Console.WriteLine(
                    $"none          : IOCTL add only, no mode set (+{stopwatch.Elapsed.TotalMilliseconds:F0}ms)");
                break;
            }

            case "global":
            default:
            {
                int result = DisplayTopology.SetModeAndPosition(
                    adapter.DeviceName, width, height, frequency, x, y, out int apply);
                Console.WriteLine($"global        : result={result} apply={apply} (+{stopwatch.Elapsed.TotalMilliseconds:F0}ms)");
                break;
            }
        }

        bool settled = DisplayTopology.WaitUntil(
            () =>
            {
                var current = DisplayTopology.FindParsecDisplay();
                return current is not null &&
                       current.Width == width &&
                       current.Height == height &&
                       current.X == x &&
                       current.Y == y;
            },
            TimeSpan.FromSeconds(3));

        Console.WriteLine($"settled       : {settled} (+{stopwatch.Elapsed.TotalMilliseconds:F0}ms)");
        PrintDisplays();

        if (hold > 0)
        {
            Thread.Sleep(hold * 1000);
        }

        stopwatch.Restart();
        bool removed = vdd.RemoveDisplay(index);
        bool gone = DisplayTopology.WaitForNoParsecDisplay(TimeSpan.FromSeconds(3));
        Console.WriteLine($"remove        : ok={removed} gone={gone} (+{stopwatch.Elapsed.TotalMilliseconds:F0}ms)");
        PrintDisplays();
        return settled && gone ? 0 : 1;
    }

    private static int DebugDevices()
    {
        Guid classGuid = VddDevice.DisplayClassGuid;
        IntPtr set = Win32.SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, Win32.DigcfPresent);
        Console.WriteLine($"class set     : {set} err={Marshal.GetLastWin32Error()}");

        if (set != Win32.InvalidHandleValue)
        {
            var devInfo = new Win32.SP_DEVINFO_DATA
            {
                cbSize = Marshal.SizeOf<Win32.SP_DEVINFO_DATA>(),
            };

            for (uint index = 0; Win32.SetupDiEnumDeviceInfo(set, index, ref devInfo); index++)
            {
                var buffer = new byte[1024];
                bool ok = Win32.SetupDiGetDeviceRegistryPropertyW(
                    set, ref devInfo, Win32.SpdrpHardwareId, out uint type,
                    buffer, (uint)buffer.Length, out uint required);
                string value = ok
                    ? Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, (uint)buffer.Length)).Replace('\0', '|')
                    : $"FAIL err={Marshal.GetLastWin32Error()}";
                Console.WriteLine($"[{index}] devinst={devInfo.DevInst} ok={ok} type={type} req={required} hw={value}");
            }

            Win32.SetupDiDestroyDeviceInfoList(set);
        }

        Guid adapterGuid = VddDevice.AdapterInterfaceGuid;
        IntPtr interfaces = Win32.SetupDiGetClassDevsW(
            ref adapterGuid, null, IntPtr.Zero,
            Win32.DigcfPresent | Win32.DigcfDeviceInterface);
        Console.WriteLine($"iface set     : {interfaces} err={Marshal.GetLastWin32Error()}");

        if (interfaces != Win32.InvalidHandleValue)
        {
            var data = new Win32.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<Win32.SP_DEVICE_INTERFACE_DATA>(),
            };

            for (uint index = 0;
                 Win32.SetupDiEnumDeviceInterfaces(interfaces, IntPtr.Zero, ref adapterGuid, index, ref data);
                 index++)
            {
                Win32.SetupDiGetDeviceInterfaceDetailW(
                    interfaces, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                Console.WriteLine($"[{index}] required={required} err={Marshal.GetLastWin32Error()}");

                if (required == 0)
                {
                    continue;
                }

                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    bool ok = Win32.SetupDiGetDeviceInterfaceDetailW(
                        interfaces, ref data, detail, required, out _, IntPtr.Zero);
                    Console.WriteLine($"     detail ok={ok} path={Marshal.PtrToStringUni(IntPtr.Add(detail, 4))}");
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }

            Win32.SetupDiDestroyDeviceInfoList(interfaces);
        }

        using var vdd = new VddDevice();
        bool opened = vdd.TryOpen(out int error);
        Console.WriteLine($"open          : {opened} err={error} ({DescribeError(error)})");
        return opened ? 0 : 2;
    }

    private static void PrintDisplays()
    {
        Console.WriteLine("displays      :");
        foreach (var display in DisplayTopology.List())
        {
            Console.WriteLine($"  - {display}");
        }

        PrintVirtualScreen();
    }

    private static void PrintVirtualScreen()
    {
        var bounds = DisplayTopology.VirtualScreenBounds();
        Console.WriteLine(
            $"virtual screen: ({bounds.X},{bounds.Y}) {bounds.Width}x{bounds.Height}, monitors={Win32.GetSystemMetrics(Win32.SmCMonitors)}");
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string DescribeError(int error)
    {
        try
        {
            return new Win32Exception(error).Message;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string Stats(List<double> values)
    {
        if (values.Count == 0)
        {
            return "n/a";
        }

        var sorted = values.OrderBy(v => v).ToList();
        int p95Index = Math.Clamp((int)Math.Ceiling(sorted.Count * 0.95) - 1, 0, sorted.Count - 1);
        return $"min={sorted[0]:F1} avg={sorted.Average():F1} p95={sorted[p95Index]:F1} max={sorted[^1]:F1} (n={sorted.Count})";
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string key = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = args[++i];
            }
            else
            {
                result[key] = null;
            }
        }

        return result;
    }

    private static int GetInt(Dictionary<string, string?> options, string key, int fallback)
    {
        return options.TryGetValue(key, out string? raw) &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static string? GetString(Dictionary<string, string?> options, string key)
    {
        return options.TryGetValue(key, out string? value) ? value : null;
    }

    private static int Usage()
    {
        Console.WriteLine(
            """
            VddProbe - M0 harness for Parsec VDD

              status                       device status + non-elevated open test
              list                         list displays / virtual screen
              add [--hold N] [--width W] [--height H] [--hz R]
                                           add, place at primary's right, hold, remove
              remove [--index N]           remove display by driver index
              removeall                    try indices 0..7
              watchdog                     add then exit without cleanup (crash simulation)
              stress [--count N] [--holdms N] [--csv path]
                                           add/remove cycle benchmark
              debug                        dump SetupAPI enumeration details
              attach [--variant global|device|extend] [--hold N]
                                           display attach variant experiment
            """);
        return 1;
    }
}
