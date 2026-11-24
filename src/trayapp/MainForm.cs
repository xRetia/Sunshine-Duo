using System.Linq;
using System.Runtime.InteropServices;

namespace SunshineDuoTray;

internal enum MainPage
{
    Console,
    Game,
    VirtualDisplay,
    Settings,
    About,
}

// 托盘双击打开的主界面：左侧导航 + 右侧分页（仿远程协助客户端的布局），支持浅色/深色主题
internal sealed class MainForm : Form
{
    private const int SidebarWidth = 236;
    private const int ContentWidth = 784;
    private const int ContentHeight = 680;

    private readonly AppConfig _cfg;
    private List<Paths.SessionInfo> _sessions;
    private Panel? _sidebar;
    private Panel? _contentHost;
    private readonly Dictionary<MainPage, Control> _pages = new();
    private readonly Dictionary<MainPage, NavItem> _nav = new();
    private SessionPage? _consolePage, _gamePage;
    private VdaPage? _vdaPage;
    private SettingsPage? _settingsPage;
    private AboutPage? _aboutPage;
    private Label? _svcText;
    private Label? _svcDot;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _polling;
    private bool _rebuilding;
    private int _ticks;
    private string _lastService = "";
    private MainPage _current = MainPage.Settings;
    private bool _pendingFocusPin;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    public MainForm(AppConfig cfg)
    {
        _cfg = cfg;
        _sessions = Paths.GetSessions(cfg);
        Theme.Apply(cfg.Theme);

        Text = "Sunshine Duo";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(SidebarWidth + ContentWidth, ContentHeight);
        BackColor = UiKit.WindowBg;
        DoubleBuffered = true;
        Font = UiKit.YaHei(9F);
        ShowInTaskbar = true;
        MaximizeBox = false;
        Icon = TrayIcons.Get(TrayState.Online);
        KeyPreview = true;

        _timer = new System.Windows.Forms.Timer { Interval = 2500 };
        _timer.Tick += async (_, _) => await PollAsync();
        MouseDown += DragAnywhere;

        BuildUi(Paths.ConsoleEnabled ? MainPage.Console
            : Paths.GameEnabled ? MainPage.Game
            : MainPage.Settings);
    }

    private void BuildUi(MainPage initial)
    {
        SuspendLayout();

        _sidebar = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(SidebarWidth, ContentHeight),
            BackColor = UiKit.SidebarBg,
        };
        Controls.Add(_sidebar);
        _sidebar.MouseDown += DragAnywhere;

        var avatar = new PictureBox
        {
            Location = new Point(24, 26),
            Size = new Size(44, 44),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = UiKit.SidebarBg,
            Image = TrayIcons.Avatar(44),
        };
        _sidebar.Controls.Add(avatar);
        avatar.MouseDown += DragAnywhere;

        var name = new Label
        {
            Text = "Sunshine Duo",
            ForeColor = UiKit.Text,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            Location = new Point(79, 27),
            AutoSize = true,
        };
        _sidebar.Controls.Add(name);
        name.MouseDown += DragAnywhere;

        _sidebar.Controls.Add(new Label
        {
            Text = Paths.GameEnabled ? "双会话串流" : "单会话串流",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(8.5F),
            Location = new Point(80, 54),
            AutoSize = true,
        });

        var y = 108;
        AddNav(MainPage.Console, "主会话", Glyphs.Monitor, ref y, Paths.ConsoleEnabled);
        AddNav(MainPage.Game, "游戏会话", Glyphs.Gamepad, ref y, Paths.GameEnabled);
        AddNav(MainPage.VirtualDisplay, "虚拟显示器", Glyphs.StackedDisplays, ref y, Paths.ConsoleEnabled);
        AddNav(MainPage.Settings, "设置", Glyphs.Gear, ref y, true);
        AddNav(MainPage.About, "关于", Glyphs.Info, ref y, true);

        _sidebar.Controls.Add(new Panel
        {
            Location = new Point(24, ContentHeight - 92),
            Size = new Size(188, 1),
            BackColor = UiKit.Border,
        });

        _svcDot = new Label
        {
            Text = "●",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9F),
            Location = new Point(24, ContentHeight - 66),
            AutoSize = true,
        };
        _sidebar.Controls.Add(_svcDot);

        _svcText = new Label
        {
            Text = "核心服务检测中",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(8.5F),
            Location = new Point(42, ContentHeight - 65),
            AutoSize = true,
        };
        _sidebar.Controls.Add(_svcText);

        var lockIcon = new IconBox(Glyphs.Lock, UiKit.Muted, 15)
        {
            Location = new Point(196, ContentHeight - 66),
        };
        _sidebar.Controls.Add(lockIcon);

        _contentHost = new Panel
        {
            Location = new Point(SidebarWidth, 0),
            Size = new Size(ContentWidth, ContentHeight),
            BackColor = UiKit.WindowBg,
        };
        Controls.Add(_contentHost);
        _contentHost.MouseDown += DragAnywhere;

        var minimize = new Button
        {
            Text = "─",
            Location = new Point(SidebarWidth + ContentWidth - 92, 14),
            Size = new Size(34, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = UiKit.WindowBg,
            ForeColor = UiKit.Muted,
            Font = new Font("Segoe UI", 9F),
            TabStop = false,
        };
        minimize.FlatAppearance.BorderSize = 0;
        minimize.FlatAppearance.MouseOverBackColor = UiKit.CardHover;
        minimize.MouseEnter += (_, _) => minimize.ForeColor = UiKit.Text;
        minimize.MouseLeave += (_, _) => minimize.ForeColor = UiKit.Muted;
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        Controls.Add(minimize);

        var close = new Button
        {
            Text = "✕",
            Location = new Point(SidebarWidth + ContentWidth - 52, 14),
            Size = new Size(34, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = UiKit.WindowBg,
            ForeColor = UiKit.Muted,
            Font = new Font("Segoe UI", 9F),
            TabStop = false,
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(196, 43, 28);
        close.MouseEnter += (_, _) =>
        {
            close.ForeColor = Color.White;
        };
        close.MouseLeave += (_, _) => close.ForeColor = UiKit.Muted;
        close.Click += (_, _) => Close();
        Controls.Add(close);

        // Controls[0] 才在最上层：后加的窗口按钮必须显式置顶，否则会被内容面板盖住
        close.BringToFront();
        minimize.BringToFront();

        Navigate(initial);
        ResumeLayout(true);
    }

    private void AddNav(MainPage page, string text,
        Action<Graphics, RectangleF, Color> icon, ref int y, bool enabled)
    {
        if (!enabled || _sidebar == null) return;
        var item = new NavItem(text, icon)
        {
            Location = new Point(12, y),
            Size = new Size(SidebarWidth - 24, 44),
        };
        item.Activated += (_, _) => Navigate(page);
        _sidebar.Controls.Add(item);
        _nav[page] = item;
        y += 50;
    }

    public void Navigate(MainPage page)
    {
        if (!_nav.ContainsKey(page))
            page = Paths.ConsoleEnabled ? MainPage.Console : MainPage.Settings;

        var view = GetPage(page);
        foreach (var kv in _pages) kv.Value.Visible = kv.Key == page;
        view.Visible = true;
        view.BringToFront();

        foreach (var kv in _nav) kv.Value.Selected = kv.Key == page;
        foreach (var kv in _nav) kv.Value.Invalidate();

        _current = page;
        if (page == MainPage.VirtualDisplay) _vdaPage?.Reload();
        if (_pendingFocusPin && page == MainPage.Console)
        {
            _pendingFocusPin = false;
            BeginInvoke(new Action(() => _consolePage?.FocusPin()));
        }
    }

    public void FocusPair()
    {
        if (!Paths.ConsoleEnabled) return;
        _pendingFocusPin = true;
        Navigate(MainPage.Console);
        if (Visible) BeginInvoke(new Action(() => _consolePage?.FocusPin()));
    }

    private Control GetPage(MainPage page)
    {
        if (_pages.TryGetValue(page, out var existing)) return existing;
        if (_contentHost == null) throw new InvalidOperationException("content host missing");

        Control view = page switch
        {
            MainPage.Console => _consolePage = new SessionPage(_cfg, Session("console"), true),
            MainPage.Game => _gamePage = new SessionPage(_cfg, Session("game"), false),
            MainPage.VirtualDisplay => _vdaPage = new VdaPage(),
            MainPage.Settings => _settingsPage = new SettingsPage(_cfg, RefreshSessionInfo, OnThemeChanged),
            MainPage.About => _aboutPage = new AboutPage(),
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };
        view.Location = new Point(24, 0);
        view.Size = new Size(736, ContentHeight);
        view.BackColor = UiKit.WindowBg;
        view.MouseDown += DragAnywhere;
        _contentHost.Controls.Add(view);
        _pages[page] = view;
        if (page == MainPage.Settings && _lastService.Length > 0)
            _settingsPage?.SetServiceState(_lastService);
        return view;
    }

    private Paths.SessionInfo Session(string id)
        => _sessions.FirstOrDefault(s => s.Id == id)
           ?? new Paths.SessionInfo { Id = id, Name = id == "console" ? "主会话" : "游戏会话" };

    private void RefreshSessionInfo()
    {
        _sessions = Paths.GetSessions(_cfg);
        _consolePage?.UpdateSession(Session("console"));
        _gamePage?.UpdateSession(Session("game"));
    }

    private void OnThemeChanged(string mode)
    {
        Theme.Apply(mode);
        BeginInvoke(new Action(RebuildForTheme));
    }

    private void RebuildForTheme()
    {
        if (_rebuilding || IsDisposed) return;
        _rebuilding = true;
        try
        {
            var page = _current;
            var old = Controls.Cast<Control>().ToList();
            Controls.Clear();
            _pages.Clear();
            _nav.Clear();
            _consolePage = _gamePage = null;
            _vdaPage = null;
            _settingsPage = null;
            _aboutPage = null;
            _sidebar = null;
            _contentHost = null;
            _svcText = null;
            _svcDot = null;
            foreach (var c in old) c.Dispose();

            BackColor = UiKit.WindowBg;
            BuildUi(page);
            if (_nav.Count > 0) UpdateServiceState(ServiceControl.Query());
        }
        finally
        {
            _rebuilding = false;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
        _ = PollAsync();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private async Task PollAsync()
    {
        if (_polling || _rebuilding) return;
        _polling = true;
        try
        {
            var jobs = _sessions.Select(async s =>
            {
                using var client = new ApolloClient(s.WebPort, _cfg.WebUser, _cfg.WebPass);
                var up = await client.Probe();
                return (s.Id, up);
            }).ToList();
            var results = await Task.WhenAll(jobs);
            foreach (var (id, up) in results)
            {
                if (id == "console") _consolePage?.SetStatus(up);
                else if (id == "game") _gamePage?.SetStatus(up);
            }
            _ticks++;
            if (_ticks % 3 == 1)
            {
                var state = await Task.Run(ServiceControl.Query);
                UpdateServiceState(state);
            }
            if (Theme.Mode == AppTheme.System && Theme.SystemIsLight() != UiKit.IsLight)
            {
                Theme.Apply("system");
                BeginInvoke(new Action(RebuildForTheme));
            }
        }
        catch { }
        finally
        {
            _polling = false;
        }
    }

    private void UpdateServiceState(string state)
    {
        if (_svcText == null || _svcDot == null) return;
        _lastService = state;
        _svcText.Text = "核心服务：" + state;
        _svcDot.ForeColor = state switch
        {
            "运行中" => UiKit.Accent,
            "启动中" or "停止中" => UiKit.Warn,
            _ => UiKit.Danger,
        };
        _settingsPage?.SetServiceState(state);
    }

    private void DragAnywhere(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 0x2, 0);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var path = UiKit.RoundRect(new RectangleF(0, 0, Width, Height), 16);
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
}