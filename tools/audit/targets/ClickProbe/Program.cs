using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

// 点击坐标探针：记录 Win32 鼠标消息的屏幕坐标/客户区坐标，用于验证 mode=input 是否把
// 虚拟屏坐标正确映射（P0：缺少 MOUSEEVENTF_VIRTUALDESK 时会落到主屏）。
internal sealed class ProbeForm : Form
{
    private readonly string _logPath;
    private readonly object _gate = new();

    public ProbeForm(string logPath)
    {
        _logPath = logPath;
        Text = "VSP ClickProbe";
        Width = 900;
        Height = 600;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        DoubleBuffered = true;
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_LBUTTONUP = 0x0202;
        const int WM_RBUTTONDOWN = 0x0204;
        const int WM_MOUSEWHEEL = 0x020A;
        const int WM_MOUSEMOVE = 0x0200;

        if (m.Msg is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_RBUTTONDOWN or WM_MOUSEWHEEL or WM_MOUSEMOVE)
        {
            // GetCursorPos 会在 SendInput 恢复光标后才读取，可能拿到旧坐标；
            // GetMessagePos 返回“消息产生时”的光标位置，才是可靠 oracle。
            int packed = Native.GetMessagePos();
            int mx = (short)(packed & 0xFFFF);
            int my = (short)((packed >> 16) & 0xFFFF);
            int wheel = m.Msg == WM_MOUSEWHEEL ? (short)((long)m.WParam >> 16) : 0;
            var record = new
            {
                t = DateTimeOffset.Now.ToString("O"),
                msg = m.Msg,
                screen = new { x = mx, y = my },
                client = new { x = (short)((long)m.LParam & 0xFFFF), y = (short)(((long)m.LParam >> 16) & 0xFFFF) },
                form = new { x = Left, y = Top, width = Width, height = Height },
                wheel,
            };
            lock (_gate)
            {
                File.AppendAllText(_logPath, JsonSerializer.Serialize(record) + Environment.NewLine);
            }
        }

        base.WndProc(ref m);
    }

    private static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        public static extern int GetMessagePos();

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string logPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "clicks.jsonl");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ProbeForm(logPath));
    }
}
