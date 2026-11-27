using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

// 修改 WebUI 账号密码：不走 Apollo API，直接 sunshine.exe --creds 离线写入
// 两个 conf 的 state，然后经 SunshineDuoCore 管道重启两个 Apollo 使其生效。
namespace SunshineDuoTray;

public class PasswordForm : Form
{
    private readonly AppConfig _cfg;
    private readonly TextBox _user, _pass, _confirm;
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

    public PasswordForm(AppConfig cfg)
    {
        _cfg = cfg;
        Text = "修改 WebUI 账号密码";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 260);
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
            Text = "修改 WebUI 账号密码",
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

        _user = new TextBox { Text = cfg.WebUser };
        _pass = new TextBox();
        _confirm = new TextBox();
        Add("新用户名：", _user, 72);
        Add("新密码：", _pass, 116, true);
        Add("确认新密码：", _confirm, 160, true);

        _ok = FlatButton("应用", new Point(126, 200), new Size(208, 34), true);
        _ok.Click += async (_, _) => await Apply();
        _cancel = FlatButton("取消", new Point(342, 200), new Size(88, 34), false);
        _cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(_ok);
        Controls.Add(_cancel);
        MouseDown += DragAnywhere;
        CancelButton = _cancel;
        AcceptButton = _ok;
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

    private async Task Apply()
    {
        var user = _user.Text.Trim();
        var pass = _pass.Text;
        if (user.Length == 0 || pass.Length < 4)
        {
            MessageBox.Show("用户名不能为空，密码至少 4 位。", "修改 WebUI 账号密码");
            return;
        }
        if (pass != _confirm.Text)
        {
            MessageBox.Show("两次输入的密码不一致。", "修改 WebUI 账号密码");
            return;
        }
        SetBusy(true);
        try
        {
            var err = await Task.Run(() => CredentialReset.Apply(user, pass));
            if (err != null)
            {
                MessageBox.Show("写入凭证失败: " + err, "修改 WebUI 账号密码", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _cfg.WebUser = user;
            _cfg.SetPass(pass);
            _cfg.Save();

            var errC = await GuardPipe.RestartApollo("console");
            var errG = await GuardPipe.RestartApollo("game");
            var msg = "凭证已写入，Apollo 正在重启以应用新密码。";
            MessageBox.Show(msg, "修改 WebUI 账号密码", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _ok.Enabled = _cancel.Enabled = !busy;
    }
}
