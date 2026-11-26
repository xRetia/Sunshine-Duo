using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SunshineDuoSetup;

// 主窗体：左侧导航 + 右侧分页（无边框，亮色 WinUI 风格；导航沿用托盘布局）
internal sealed class MainForm : Form
{
    private const int SidebarWidth = 220;
    private const int ContentWidth = 780;
    private const int ContentHeight = 700;

    private readonly bool _updateMode;
    private Panel? _sidebar;
    private Panel? _contentHost;
    private readonly Dictionary<string, Control> _pages = [];
    private readonly Dictionary<string, NavItem> _nav = [];

    private WelcomePage? _welcome;
    private WizardPage? _wizard;
    private RunPage? _run;
    private ResultPage? _result;
    private UninstallPage? _uninstall;
    private UpdatePage? _updatePage;

    private readonly RunPump _pump = new();

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    public MainForm(bool updateMode)
    {
        _updateMode = updateMode;
        Text = "萌远控 Duo";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(SidebarWidth + ContentWidth, ContentHeight);
        BackColor = Ui.Bg;
        DoubleBuffered = true;
        Font = Ui.F(9F);
        ShowInTaskbar = true;
        MaximizeBox = false;
        Icon = ProgramIcon.Get();
        KeyPreview = true;
        MouseDown += DragAnywhere;

        BuildUi(updateMode ? "v" : "w");
    }

    private void BuildUi(string initial)
    {
        SuspendLayout();

        _sidebar = new Panel { Location = new Point(0, 0), Size = new Size(SidebarWidth, ContentHeight), BackColor = Ui.Sidebar };
        Controls.Add(_sidebar);
        _sidebar.MouseDown += DragAnywhere;

        var brand = new PictureBox
        {
            Location = new Point(24, 24), Size = new Size(40, 40),
            BackColor = Color.Transparent, SizeMode = PictureBoxSizeMode.Zoom,
            Image = BrandImage(),
        };
        if (brand.Image is null)
        {
            brand.BackColor = Ui.Accent;
            brand.Region = new Region(Ui.RoundRect(new RectangleF(0, 0, 40, 40), 9));
            var brandText = new Label
            {
                Text = "萌", ForeColor = Color.White, BackColor = Color.Transparent,
                Font = Ui.F(15F, FontStyle.Bold), Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            brand.Controls.Add(brandText);
        }
        _sidebar.Controls.Add(brand);
        brand.MouseDown += DragAnywhere;

        var name = new Label
        {
            Text = "萌远控 Duo",
            ForeColor = Ui.Text, BackColor = Color.Transparent,
            Font = Ui.F(13F, FontStyle.Bold),
            Location = new Point(74, 26), AutoSize = true,
        };
        _sidebar.Controls.Add(name);
        name.MouseDown += DragAnywhere;
        _sidebar.Controls.Add(new Label
        {
            Text = "部署工具",
            ForeColor = Ui.Text3, BackColor = Color.Transparent,
            Font = Ui.F(8.5F), Location = new Point(75, 52), AutoSize = true,
        });

        var y = 104;
        AddNav("w", "总览", Glyphs.Home, ref y);
        AddNav("s", "安装", Glyphs.Monitor, ref y);
        AddNav("u", "卸载", Glyphs.Trash, ref y);
        AddNav("v", "更新", Glyphs.Refresh, ref y);
        AddNav("about", "关于", Glyphs.Info, ref y);

        _sidebar.Controls.Add(new Panel
        {
            Location = new Point(24, ContentHeight - 92),
            Size = new Size(SidebarWidth - 48, 1),
            BackColor = Ui.Line,
        });
        _sidebar.Controls.Add(new Label
        {
            Text = "版权所有 (c) 苏羽茜工作室",
            ForeColor = Color.FromArgb(160, 160, 160), BackColor = Color.Transparent,
            Font = Ui.F(8F), Location = new Point(24, ContentHeight - 72), AutoSize = true,
        });

        _contentHost = new Panel
        {
            Location = new Point(SidebarWidth, 0),
            Size = new Size(ContentWidth, ContentHeight),
            BackColor = Ui.Bg,
        };
        Controls.Add(_contentHost);
        _contentHost.MouseDown += DragAnywhere;
        _contentHost.Paint += (_, e) =>
        {
            using var pen = new Pen(Ui.Line);
            e.Graphics.DrawLine(pen, 0, 0, 0, _contentHost.Height);
        };

        var minimize = TitleBtn("─", SidebarWidth + ContentWidth - 92, CodeWinUiText, Ui.CardHover);
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        Controls.Add(minimize);

        var close = TitleBtn("✕", SidebarWidth + ContentWidth - 52, Ui.Text2, Color.FromArgb(196, 43, 28));
        close.MouseEnter += (_, _) => close.ForeColor = Color.White;
        close.MouseLeave += (_, _) => close.ForeColor = Ui.Text2;
        close.Click += (_, _) => Close();
        Controls.Add(close);
        close.BringToFront();
        minimize.BringToFront();

        _pages["w"] = _welcome = new WelcomePage(Navigate, _pump);
        _pages["s"] = _wizard = new WizardPage(this, _pump);
        _pages["r"] = _run = new RunPage(_pump);
        _pages["f"] = _result = new ResultPage(_pump);
        _pages["u"] = _uninstall = new UninstallPage(_pump);
        _pages["v"] = _updatePage = new UpdatePage(_pump);
        _pages["about"] = new AboutPage();

        foreach (var kv in _pages)
        {
            kv.Value.Location = new Point(24, 0);
            kv.Value.Size = new Size(ContentWidth - 48, ContentHeight);
            kv.Value.BackColor = Ui.Bg;
            kv.Value.Visible = false;
            _contentHost.Controls.Add(kv.Value);
        }
        _result.BindOwner(this);
        _uninstall.BindOwner(this);
        _updatePage.BindOwner(this);

        _pump.RunFinished += hasFail => BeginInvoke(() =>
        {
            var rows = _run?.Rows() ?? [];
            _result.ShowResult(_pump.IsInstall, hasFail, aborted: false, _pump.LastOpts, rows);
            Navigate("f");
        });

        Navigate(initial);
        ResumeLayout(true);
    }

    private static Color CodeWinUiText => Ui.Text2;

    private Button TitleBtn(string glyph, int x, Color fore, Color hoverBack)
    {
        var b = new Button
        {
            Text = glyph,
            Location = new Point(x, 12),
            Size = new Size(34, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Ui.Bg,
            ForeColor = fore,
            Font = new Font("Segoe UI", 9F),
            TabStop = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = hoverBack;
        return b;
    }

    private void AddNav(string id, string text, Action<Graphics, RectangleF, Color> icon, ref int y)
    {
        var item = new NavItem(text, icon) { Location = new Point(12, y), Size = new Size(SidebarWidth - 24, 44), PageId = id };
        item.Activated += () => Navigate(id);
        _sidebar!.Controls.Add(item);
        _nav[id] = item;
        y += 50;
    }

    public void Navigate(string page)
    {
        foreach (var kv in _nav) kv.Value.Selected = kv.Key == page;
        ShowPage(page);
    }

    private void ShowPage(string page)
    {
        if (!_pages.TryGetValue(page, out var view)) return;
        foreach (var kv in _pages) kv.Value.Visible = kv.Key == page;
        view.Visible = true;
        view.BringToFront();
        if (page == "u") _uninstall?.RefreshChecks();
        if (page == "w") _welcome?.Reload();
        if (page == "v") _updatePage?.RefreshLocal();
    }

    internal void GoRun() => Navigate("r");
    internal void GoResult() => Navigate("f");
    internal Control? Page(string id) => _pages.TryGetValue(id, out var c) ? c : null;
    internal WizardPage? Wizard => _wizard;
    internal RunPage? RunPage => _run;
    internal UninstallPage? Uninstall => _uninstall;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_updateMode) _updatePage?.EnterUpdateMode();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_pump.Running)
        {
            e.Cancel = true;
            MessageBox.Show(this, "任务正在后台运行中，完成后才能关闭窗口。", "请稍候",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var path = Ui.RoundRect(new RectangleF(0, 0, Width, Height), 8);
        Region = new Region(path);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    private void DragAnywhere(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 0x2, 0);
    }

    private static Image? BrandImage()
    {
        // 优先内嵌资源（单文件发布后也可用），再退回磁盘路径
        try
        {
            using var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("sun.png");
            if (s is not null)
            {
                using var tmp = new Bitmap(s);
                return new Bitmap(tmp);
            }
        }
        catch { }
        try
        {
            var root = Paths.Root;
            var sunPng = Path.Combine(root, "sun.png");
            if (File.Exists(sunPng)) return Image.FromFile(sunPng);
            var traySun = Path.Combine(root, "src", "trayapp", "sun.png");
            if (File.Exists(traySun)) return Image.FromFile(traySun);
        }
        catch { }
        return null;
    }
}

// 侧边导航项（亮色）
internal sealed class NavItem : Control
{
    private readonly Action<Graphics, RectangleF, Color> _icon;
    private bool _hover;

    public NavItem(string text, Action<Graphics, RectangleF, Color> icon)
    {
        _icon = icon;
        Text = text;
        Cursor = Cursors.Hand;
        Font = Ui.F(10.5F);
        ForeColor = Ui.Text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PageId { get; init; } = "";
    public event Action? Activated;
    
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Invalidate(); }
    }
    private bool _selected;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Activated?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Selected || _hover)
        {
            using var p = Ui.RoundRect(new RectangleF(0, 0, Width, Height), 6);
            using var brush = new SolidBrush(Selected ? Ui.AccentTint : Ui.CardHover);
            g.FillPath(brush, p);
        }
        if (Selected)
        {
            using var bar = Ui.RoundRect(new RectangleF(2, Height / 2f - 10, 3, 20), 1.5f);
            using var brush = new SolidBrush(Ui.Accent);
            g.FillPath(brush, bar);
        }
        var iconColor = Selected ? Ui.Accent : Ui.Text3;
        _icon(g, new RectangleF(16, Height / 2f - 10, 20, 20), iconColor);
        TextRenderer.DrawText(g, Text, Font, new Point(48, Height / 2 - Font.Height / 2 - 1),
            Selected ? Ui.Accent : Ui.Text);
    }
}

// 程序图标（apollo.ico；失败时内置色块）
internal static class ProgramIcon
{
    private static Icon? _icon;

    public static Icon Get()
    {
        if (_icon is not null) return _icon;
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null) _icon = Icon.ExtractAssociatedIcon(exe);
        }
        catch { }
        if (_icon is null)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
                g.Clear(Ui.Accent);
            _icon = Icon.FromHandle(bmp.GetHicon());
        }
        return _icon;
    }
}

// 后台运行泵
internal sealed class RunPump
{
    public bool Running { get; private set; }
    public bool IsInstall { get; private set; }
    public Opts? LastOpts { get; set; }
    private readonly SynchronizationContext? _sync = SynchronizationContext.Current;
    private readonly List<(string Key, string Label, bool Ok)> _results = [];
    private Dictionary<string, string> _labels = [];

    public event Action<string>? Logged;
    public event Action<string>? StepStarted;
    public event Action<string, bool>? StepFinished;
    public event Action<bool>? RunFinished;

    private Reporter? _reporter;

    public IRunReporter Start(bool isInstall)
    {
        Running = true;
        IsInstall = isInstall;
        _results.Clear();
        _reporter = new Reporter(this);
        return _reporter;
    }

    public IReadOnlyList<(string Key, string Label, bool Ok)> Results => _results;

    public void SetLabels(Dictionary<string, string> labels) => _labels = labels;

    public void Finish(bool hasFail)
    {
        Running = false;
        RunFinished?.Invoke(hasFail);
    }

    private sealed class Reporter(RunPump owner) : IRunReporter
    {
        public void Log(string message) => owner._sync?.Post(_ => owner.Logged?.Invoke(message), null);
        public void StepStart(string key) => owner._sync?.Post(_ => owner.StepStarted?.Invoke(key), null);
        public void StepDone(string key, bool ok)
        {
            owner._sync?.Post(_ =>
            {
                var label = owner._labels.TryGetValue(key, out var l) ? l : key;
                owner._results.Add((key, label, ok));
                owner.StepFinished?.Invoke(key, ok);
            }, null);
        }
    }
}