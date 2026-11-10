using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

// 确定性 UIA 靶标：文本、计数、复选框、按钮、模态框、画布。
// 提交后把表单值写入 args[0] 指定的 JSON，供 oracle 独立校验。
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string logPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "uitarget.json");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var form = new Form
        {
            Text = "VSP UiTarget",
            Width = 640,
            Height = 520,
            StartPosition = FormStartPosition.CenterScreen,
        };

        var input = new TextBox { Name = "input", Left = 20, Top = 20, Width = 300 };
        var count = new TextBox { Name = "count", Left = 20, Top = 60, Width = 100, Text = "0" };
        var opt = new CheckBox { Name = "opt", Left = 20, Top = 95, Width = 140, Text = "Enable" };
        var submit = new Button { Name = "submit", Left = 20, Top = 130, Width = 100, Text = "Submit" };
        var modal = new Button { Name = "modal", Left = 140, Top = 130, Width = 100, Text = "Modal" };
        var result = new Label { Name = "result", Left = 20, Top = 170, Width = 300, Text = "idle" };
        var canvas = new Panel { Name = "canvas", Left = 20, Top = 210, Width = 200, Height = 160, BackColor = Color.White };

        canvas.Paint += (_, e) =>
        {
            using var brush = new SolidBrush(Color.Red);
            e.Graphics.FillRectangle(brush, 40, 40, 24, 24);
        };

        submit.Click += (_, _) =>
        {
            var payload = new
            {
                input = input.Text,
                count = count.Text,
                opt = opt.Checked,
                submitted = true,
            };
            File.WriteAllText(logPath, JsonSerializer.Serialize(payload));
            result.Text = "OK";
        };

        modal.Click += (_, _) =>
        {
            var answer = MessageBox.Show(form, "Confirm?", "Modal", MessageBoxButtons.YesNo);
            if (answer == DialogResult.Yes)
            {
                File.AppendAllText(logPath + ".modal", "yes");
            }
            result.Text = "MODAL";
        };

        form.Controls.AddRange(new Control[] { input, count, opt, submit, modal, result, canvas });
        Application.Run(form);
    }
}
