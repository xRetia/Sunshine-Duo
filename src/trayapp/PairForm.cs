using System.Linq;
using System.Runtime.InteropServices;

namespace SunshineDuoTray;

public class PairForm : Form
{
    private readonly AppConfig _cfg;
    private readonly Button _session;
    private readonly TextBox[] _pin = new TextBox[4];
    private readonly Button _submit, _advanced, _close;
    private readonly Label _status;
    private readonly bool _gameEnabled;
    private bool _console = true;

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

    public PairForm(AppConfig cfg, bool? presetConsole = null, bool gameEnabled = true)
    {
        _cfg = cfg;
        _gameEnabled = gameEnabled;
        Text = "设备配对";
        if (!gameEnabled && presetConsole == false) presetConsole = true;
        if (presetConsole.HasValue) _console = presetConsole.Value;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 300);
        BackColor = Bg;
        Font = new Font("Segoe UI", 10F);
        DoubleBuffered = true;

        // 圆角区域
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var r = 12;
        path.AddArc(0, 0, r * 2, r * 2, 180, 90);
        path.AddArc(Width - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
        path.AddArc(Width - r * 2 - 1, Height - r * 2 - 1, r * 2, r * 2, 0, 90);
        path.AddArc(0, Height - r * 2 - 1, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        Region = new Region(path);

        // 自定义标题栏
        var title = new Label
        {
            Text = "设备配对",
            ForeColor = TextC,
            BackColor = Color.Transparent,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            Location = new Point(24, 16),
            AutoSize = true,
        };
        title.MouseDown += DragAnywhere;
        _close = MakeFlatButton("✕", new Point(ClientSize.Width - 44, 8), new Size(36, 32), Muted, Surface);
        _close.Click += (_, _) => Close();
        _close.Font = new Font("Segoe UI", 11F);
        _close.FlatAppearance.MouseOverBackColor = Color.FromArgb(196, 43, 28);
        _close.ForeColor = TextC;
        Controls.Add(title);
        Controls.Add(_close);

        var lblTarget = new Label
        {
            Text = "目标会话",
            ForeColor = Muted,
            Location = new Point(24, 62),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        Controls.Add(lblTarget);

        _session = new Button
        {
            Location = new Point(24, 88),
            Size = new Size(ClientSize.Width - 48, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = TextC,
            Font = new Font("Microsoft YaHei UI", 10F),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
        };
        _session.FlatAppearance.BorderSize = 1;
        _session.FlatAppearance.BorderColor = Border;
        _session.FlatAppearance.MouseOverBackColor = SurfaceHover;
        _session.Click += (_, _) => ShowSessionMenu();
        UpdateSessionText();
        Controls.Add(_session);

        var lblPin = new Label
        {
            Text = "配对码",
            ForeColor = Muted,
            Location = new Point(24, 144),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        Controls.Add(lblPin);

        // 4 个 PIN 框：宽 72、间距 16，总宽 336，左右各留 72 边距
        var startY = 170;
        for (var i = 0; i < 4; i++)
        {
            var idx = i;
            _pin[i] = new TextBox
            {
                Location = new Point(72 + i * 88, startY),
                Size = new Size(72, 36),
                MaxLength = 1,
                CharacterCasing = CharacterCasing.Upper,
                TextAlign = HorizontalAlignment.Center,
                Font = new Font("Consolas", 16F, FontStyle.Bold),
                BackColor = Surface,
                ForeColor = TextC,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _pin[i].KeyPress += (_, e) =>
            {
                if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true;
            };
            _pin[i].TextChanged += (_, _) =>
            {
                if (_pin[idx].Text.Length == 1 && idx < 3) _pin[idx + 1].Focus();
            };
            _pin[i].KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Back && _pin[idx].Text.Length == 0 && idx > 0)
                {
                    _pin[idx - 1].Focus();
                    _pin[idx - 1].Clear();
                }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = SubmitPin(); }
            };
            Controls.Add(_pin[i]);
        }

        _status = new Label
        {
            Location = new Point(24, 216),
            Size = new Size(ClientSize.Width - 48, 20),
            ForeColor = Muted,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        Controls.Add(_status);

        // 底部按钮：配对（强调绿）+ 高级（描边），各占一半宽度，不重叠
        _submit = new Button
        {
            Text = "配对",
            Location = new Point(24, 246),
            Size = new Size(210, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        };
        _submit.FlatAppearance.BorderSize = 0;
        _submit.FlatAppearance.MouseOverBackColor = AccentHover;
        _submit.Click += async (_, _) => await SubmitPin();
        Controls.Add(_submit);

        _advanced = new Button
        {
            Text = "高级…",
            Location = new Point(246, 246),
            Size = new Size(210, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = TextC,
            Font = new Font("Microsoft YaHei UI", 10F),
        };
        _advanced.FlatAppearance.BorderSize = 1;
        _advanced.FlatAppearance.BorderColor = Border;
        _advanced.FlatAppearance.MouseOverBackColor = SurfaceHover;
        _advanced.Click += (_, _) => OpenAdvanced();
        Controls.Add(_advanced);

        MouseDown += DragAnywhere;
        CancelButton = _submit;
        _pin[0].Focus();
    }

    private Button MakeFlatButton(string text, Point loc, Size size, Color fore, Color back)
    {
        return new Button
        {
            Text = text,
            Location = loc,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
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

    private void UpdateSessionText()
    {
        _session.Text = $"{(_console ? "主会话" : "游戏会话")}  [https://*:{Port}]";
    }

    private void ShowSessionMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Surface,
            ForeColor = TextC,
            RenderMode = ToolStripRenderMode.Professional,
        };
        var c = new ToolStripMenuItem("主会话") { ForeColor = TextC };
        c.Click += (_, _) => { _console = true; UpdateSessionText(); };
        menu.Items.Add(c);
        if (_gameEnabled)
        {
            var g = new ToolStripMenuItem("游戏会话") { ForeColor = TextC };
            g.Click += (_, _) => { _console = false; UpdateSessionText(); };
            menu.Items.Add(g);
        }
        menu.Show(_session, new Point(0, _session.Height));
    }

    // 会话端口：conf 里能读到 port= 就用 conf 的（与核心一致），否则退回 trayapp.json
    private int Port
    {
        get
        {
            var id = _console ? "console" : "game";
            var s = Paths.GetSessions(_cfg).FirstOrDefault(x => x.Id == id);
            if (s != null) return s.WebPort;
            return (_console ? _cfg.ConsolePort : _cfg.GamePort) + 1;
        }
    }

    private string PinText => string.Concat(_pin.Select(t => t.Text.Trim()));

    private async Task SubmitPin()
    {
        var pin = PinText;
        if (pin.Length != 4 || !pin.All(char.IsDigit))
        {
            _status.Text = "请输入设备上显示的 4 位配对码。";
            _status.ForeColor = Color.FromArgb(244, 96, 86);
            return;
        }
        SetBusy(true);
        _status.Text = "正在提交…";
        _status.ForeColor = Muted;
        try
        {
            using var client = new ApolloClient(Port, _cfg.WebUser, _cfg.WebPass);
            var err = await client.SendPin(pin);
            if (err != null && err.Length > 0)
            {
                _status.Text = "配对失败：" + err;
                _status.ForeColor = Color.FromArgb(244, 96, 86);
            }
            else
            {
                _status.Text = "配对成功！请在 Moonlight 上确认。";
                _status.ForeColor = AccentHover;
                foreach (var t in _pin) t.Clear();
                _pin[0].Focus();
            }
        }
        catch (Exception e)
        {
            _status.Text = "连接失败：" + e.Message;
            _status.ForeColor = Color.FromArgb(244, 96, 86);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenAdvanced()
    {
        var title = _console ? "主会话配对" : "游戏会话配对";
        try
        {
            using var f = new WebViewForm(_cfg, title, Port, "/pin#PIN");
            f.ShowDialog(this);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "无法打开配对页面: " + e.Message, Text);
        }
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _submit.Enabled = _advanced.Enabled = !busy;
        _submit.BackColor = busy ? Color.FromArgb(60, 84, 60) : Accent;
    }
}
