using System.Drawing.Drawing2D;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace SunshineDuoTray;

public class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly bool _showConsole = true, _showGame = true;
    private readonly TextBox _consolePort, _gamePort;
    private readonly CheckBox _autoStart;
    private readonly Button _ok, _cancel;

    private static readonly Color Bg = Color.FromArgb(24, 26, 30);
    private static readonly Color Surface = Color.FromArgb(38, 41, 47);
    private static readonly Color SurfaceHover = Color.FromArgb(50, 54, 61);
    private static readonly Color Accent = Color.FromArgb(16, 124, 16);
    private static readonly Color AccentHover = Color.FromArgb(28, 148, 28);
    private static readonly Color TextC = Color.FromArgb(240, 242, 245);
    private static readonly Color Muted = Color.FromArgb(150, 156, 166);
    private static readonly Color Border = Color.FromArgb(70, 75, 84);

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    public SettingsForm(AppConfig cfg)
    {
        _cfg = cfg;
        Text = "设置";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 344);
        BackColor = Bg;
        Font = new Font("Segoe UI", 10F);
        DoubleBuffered = true;

        var path = new GraphicsPath();
        var r = 12;
        path.AddArc(0, 0, r * 2, r * 2, 180, 90);
        path.AddArc(Width - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
        path.AddArc(Width - r * 2 - 1, Height - r * 2 - 1, r * 2, r * 2, 0, 90);
        path.AddArc(0, Height - r * 2 - 1, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        Region = new Region(path);

        var title = new Label
        {
            Text = "设置",
            ForeColor = TextC,
            BackColor = Color.Transparent,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            Location = new Point(24, 16),
            AutoSize = true,
        };
        title.MouseDown += DragAnywhere;
        var close = new Button
        {
            Text = "✕", Location = new Point(ClientSize.Width - 44, 8), Size = new Size(36, 32),
            FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent, ForeColor = TextC,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(196, 43, 28) },
            Font = new Font("Segoe UI", 11F),
        };
        close.Click += (_, _) => Close();
        Controls.Add(title);
        Controls.Add(close);

        void Add(string label, TextBox box, int y, bool isPass = false)
        {
            Controls.Add(new Label
            {
                Text = label,
                ForeColor = Muted,
                Location = new Point(20, y + 4),
                Size = new Size(126, 22),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Microsoft YaHei UI", 9F),
            });
            box.Location = new Point(152, y);
            box.Width = 284;
            if (isPass) box.UseSystemPasswordChar = true;
            box.BackColor = Surface;
            box.ForeColor = TextC;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = new Font("Segoe UI", 10F);
            Controls.Add(box);
        }

        _consolePort = new TextBox { Text = cfg.ConsolePort.ToString() };
        _gamePort = new TextBox { Text = cfg.GamePort.ToString() };
        var y = 72;
        _showConsole = Paths.ConsoleEnabled;
        _showGame = Paths.GameEnabled;
        if (_showConsole) Add("当前会话串流端口：", _consolePort, y);
        if (_showConsole) y += 44;
        if (_showGame) Add("游戏会话串流端口：", _gamePort, y);
        if (_showGame) y += 44;

        _autoStart = new CheckBox
        {
            Text = "开机自启（当前用户登录时）",
            Location = new Point(24, y + 8),
            AutoSize = true,
            Checked = IsAutoStartEnabled(),
            ForeColor = TextC,
            Font = new Font("Microsoft YaHei UI", 10F),
            BackColor = Color.Transparent,
        };
        _autoStart.MouseDown += DragAnywhere;
        Controls.Add(_autoStart);

        _ok = FlatButton("保存", new Point(246, y + 52), new Size(88, 34), true);
        _ok.Click += (_, _) => Save();
        _cancel = FlatButton("取消", new Point(348, y + 52), new Size(88, 34), false);
        _cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(_ok);
        Controls.Add(_cancel);
        CancelButton = _cancel;
        AcceptButton = _ok;

        MouseDown += DragAnywhere;
    }

    private Button FlatButton(string text, Point loc, Size size, bool accent)
    {
        return new Button
        {
            Text = text,
            Location = loc,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = accent ? Accent : Surface,
            ForeColor = accent ? Color.White : TextC,
            Font = new Font("Microsoft YaHei UI", 10F),
            FlatAppearance = { BorderSize = accent ? 0 : 1, BorderColor = Border, MouseOverBackColor = accent ? AccentHover : SurfaceHover },
        };
    }

    private void DragAnywhere(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 0x2, 0);
        }
    }

    private void Save()
    {
        int? cp = null, gp = null;
        // 端口框可能被隐藏（单会话模式），先解析再决定是否校验
        int cpv = 0, gpv = 0;
        bool cpOk = int.TryParse(_consolePort.Text.Trim(), out cpv) && cpv is >= 1024 and <= 65535;
        bool gpOk = int.TryParse(_gamePort.Text.Trim(), out gpv) && gpv is >= 1024 and <= 65535;
        if (_showConsole && !cpOk)
        {
            MessageBox.Show("端口必须是 1024-65535 之间的数字，且两个端口差需 ≥ 50。", "设置");
            return;
        }
        if (_showConsole) { cp = cpv; }
        if (_showGame && !gpOk)
        {
            MessageBox.Show("端口必须是 1024-65535 之间的数字，且两个端口差需 ≥ 50。", "设置");
            return;
        }
        if (_showGame) { gp = gpv; }
        // 双会话时两个端口族不能重叠；单会话无需该约束
        if (_showConsole && _showGame && cp.HasValue && gp.HasValue && Math.Abs(cp.Value - gp.Value) < 50)
        {
            MessageBox.Show("两个端口差需 ≥ 50（串流/RTSP 端口族与外设端口避免重叠）。", "设置");
            return;
        }
        if (cp.HasValue) { _cfg.ConsolePort = cp.Value; }
        if (gp.HasValue) { _cfg.GamePort = gp.Value; }
        _cfg.AutoStart = _autoStart.Checked;
        _cfg.Save();
        SetAutoStart(_autoStart.Checked);
        DialogResult = DialogResult.OK;
    }

    public static void SetAutoStart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (on)
                key.SetValue("SunshineDuoTray", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("SunshineDuoTray", false);
        }
        catch { }
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return false;
            var v = key.GetValue("SunshineDuoTray") as string;
            return v != null && v.Contains("SunshineDuoTray.exe");
        }
        catch { return false; }
    }
}
