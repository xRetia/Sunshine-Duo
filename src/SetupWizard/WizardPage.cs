namespace SunshineDuoSetup;

// 安装向导：4 步（01 选择模式 / 02 账户信息 / 03 连接设置 / 04 确认安装）
// 可见项：模式、游戏账号与密码、自动登录、网页控制台账号、端口、托盘快捷方式（自启隐藏默认开）、
//         单会话时的游戏启动器、多显卡时的图形设备选择。
// 隐藏项（默认执行，仅在进度中展示）：网络访问规则、画面渲染优化、手柄支持、虚拟显示器驱动、分辨率列表。
internal sealed class WizardPage : Panel
{
    private const int StepCount = 4;
    private static readonly string[] StepNames = ["选择模式", "账户信息", "连接设置", "确认安装"];

    // 虚拟显示器常见分辨率（16:9 / 16:10 / 21:9，统一 60Hz）
    private static readonly string[] ResPresets =
    [
        "1280x720@60", "1920x1080@60", "2560x1440@60", "3840x2160@60",
        "1920x1200@60", "2560x1600@60",
        "2560x1080@60", "3440x1440@60",
    ];

    private readonly MainForm _owner;
    private readonly RunPump _pump;

    private readonly StepDot[] _dots = new StepDot[StepCount];
    private readonly Label[] _dotNames = new Label[StepCount];
    private readonly Panel[] _stepPanels = new Panel[StepCount];
    private int _step;

    // 01 模式
    private ModeCard _cardDual = null!;
    private ModeCard _cardSingle = null!;
    private bool _dual = true;

    // 02 账户
    private RoundCard _gameCard = null!;
    private RoundCard _consoleHintCard = null!;
    private TextBox _txtStreamUser = null!;
    private TextBox _pwdRdp = null!;
    private RButton _btnVerify = null!;
    private Label _lblPwState = null!;
    private Label _lblCredState = null!;
    private RoundCard _adminCard = null!;
    private TextBox _txtWebUser = null!;
    private TextBox _pwdWebUi = null!;
    private TextBox _pwdWebUi2 = null!;
    private Label _lblWebPwState = null!;

    // 03 连接
    private VStack? _stack2;
    private RoundCard _gpuCard = null!;
    private ComboBox _cmbGpu = null!;
    private TextBox _txtPortMain = null!;
    private TextBox _txtPortGame = null!;
    private SwitchRow _swTrayDesktop = null!;
    private SwitchRow _swTrayStart = null!;
    private RoundCard _gmCard = null!;
    private SwitchRow _swGameMgr = null!;

    // 04 确认
    private Label _lblSummary = null!;

    private RButton _btnBack = null!;
    private RButton _btnNext = null!;
    private RButton _btnInstall = null!;

    private bool _pwVerified;
    private string _pwVerifiedFor = "";
    private List<string> _gpus = [];

    public WizardPage(MainForm owner, RunPump pump)
    {
        _owner = owner;
        _pump = pump;
        DoubleBuffered = true;

        var title = Ui.Title("安装配置", 18);
        title.Location = new Point(Lay.X0, 18);
        Controls.Add(title);

        // 步骤指示：01-04 圆点 + 名称（横排；连接线只画在圆点之间，不穿过圆点）
        var dotStart = Lay.X0 + 26;
        const int spacing = 146;
        const int dotTop = 82;      // 圆点 y（与标题拉开间距）
        const int nameTop = 120;
        for (var i = 0; i < StepCount; i++)
        {
            var dot = new StepDot("0" + (i + 1)) { Location = new Point(dotStart + spacing * i, dotTop) };
            Controls.Add(dot);
            _dots[i] = dot;
            var lbl = new Label
            {
                Text = StepNames[i], ForeColor = Ui.Text2, BackColor = Color.Transparent,
                Font = Ui.F(9F), Size = new Size(90, 18), TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(dot.Left + 15 - 45, nameTop),
            };
            Controls.Add(lbl);
            _dotNames[i] = lbl;
            if (i > 0)
            {
                // 线段位于上一圆点右缘与本圆点左缘之间
                var prev = _dots[i - 1];
                Controls.Add(new Panel
                {
                    Location = new Point(prev.Right + 6, dotTop + 14),
                    Size = new Size(dot.Left - prev.Right - 12, 1),
                    BackColor = Ui.Line,
                });
            }
        }

        BuildStep0();
        BuildStep1();
        BuildStep2();
        BuildStep3();
        foreach (var p in _stepPanels) Controls.Add(p);
        foreach (var p in _stepPanels) p.Visible = false;

        // 底部导航条
        Controls.Add(new Panel
        {
            Location = new Point(0, 640), Size = new Size(Lay.PageW, 1), BackColor = Ui.Line,
        });
        _btnBack = Ui.Secondary("< 上一步");
        _btnBack.Size = new Size(110, 34);
        _btnBack.Location = new Point(6, 654);
        _btnBack.Visible = false;
        _btnBack.Click += (_, _) => { if (!_pump.Running) SetStep(_step - 1); };
        Controls.Add(_btnBack);

        _btnNext = Ui.Primary("下一步 >");
        _btnNext.Size = new Size(130, 34);
        _btnNext.Location = new Point(Lay.PageW - 10 - 130, 654);
        _btnNext.Click += (_, _) => { if (!_pump.Running) OnNext(); };
        Controls.Add(_btnNext);

        _btnInstall = Ui.Primary("开 始 安 装");
        _btnInstall.Size = new Size(150, 34);
        _btnInstall.Location = new Point(Lay.PageW - 10 - 150, 654);
        _btnInstall.Visible = false;
        _btnInstall.Click += (_, _) => { if (!_pump.Running) StartInstall(); };
        Controls.Add(_btnInstall);

        UpdateModeDependent(); // 初始即按默认模式（双会话）校正卡片显隐
        SetStep(0);
    }

    // ------------------------------------------------------------------
    // 01 选择模式
    // ------------------------------------------------------------------
    private void BuildStep0()
    {
        var p = new Panel { Location = new Point(Lay.X0, 156), Size = new Size(Lay.CW, 474), BackColor = Ui.Bg };
        _stepPanels[0] = p;

        var head = Ui.Head("选择安装模式", 12F);
        head.Location = new Point(2, 0);
        p.Controls.Add(head);
        var sub = Ui.Sub("决定串流端看到的会话数量；安装后可在托盘查看，但模式本身需重新运行本工具更改");
        sub.Location = new Point(4, 30);
        p.Controls.Add(sub);

        _cardDual = new ModeCard("双会话（推荐）",
            "本机主画面 + 为远程设备单独准备一套游戏画面。手机 / 平板连接游戏画面时，本地使用互不打断。")
        {
            Location = new Point(2, 66), Size = new Size(288, 150), Selected = true,
        };
        _cardDual.Activated += (_, _) => SelectMode(dual: true);
        p.Controls.Add(_cardDual);

        _cardSingle = new ModeCard("单会话",
            "只提供本机主画面：适合只在一台设备上使用、或本机与远程交替使用的场景。")
        {
            Location = new Point(302, 66), Size = new Size(288, 150),
        };
        _cardSingle.Activated += (_, _) => SelectMode(dual: false);
        p.Controls.Add(_cardSingle);

        var hint = new RoundCard { Location = new Point(2, 234), Size = new Size(588, 74) };
        var hintText = new Label
        {
            Text = "模式只影响是否创建远程游戏画面与会话隔离。选了双会话后，游戏账号必须能正常登录；" +
                   "单会话则直接使用当前登录用户，无需密码。",
            ForeColor = Ui.Text2, BackColor = Color.Transparent,
            Font = Ui.F(9F), Location = new Point(16, 14), Size = new Size(556, 48),
        };
        hint.Controls.Add(hintText);
        p.Controls.Add(hint);
    }

    private void SelectMode(bool dual)
    {
        _dual = dual;
        _cardDual.Selected = dual;
        _cardSingle.Selected = !dual;
        _cardDual.Invalidate();
        _cardSingle.Invalidate();
        UpdateModeDependent();
    }

    // ------------------------------------------------------------------
    // 02 账户信息
    // ------------------------------------------------------------------
    private void BuildStep1()
    {
        var p = new Panel { Location = new Point(Lay.X0, 156), Size = new Size(Lay.CW, 480), BackColor = Ui.Bg, AutoScroll = true };
        _stepPanels[1] = p;

        // 左右结构：左列远程游戏账号 / 单会话占位，右列网页控制台账号
        const int colW = 286;
        const int leftX = 0;
        const int rightX = 298;

        // 游戏账号（仅双会话，左列）
        _gameCard = new RoundCard { Location = new Point(leftX, 0), Size = new Size(colW, 316) };
        _gameCard.Controls.Add(Head(16, 14, "远程游戏账号"));
        _gameCard.Controls.Add(SubAt(16, 40, "手机 / 平板连接时使用的本机账户（需已有此账户）", colW - 32, 2));
        _gameCard.Controls.Add(LabelAt(16, 92, "用户名"));
        _txtStreamUser = Ui.FieldBox();
        _txtStreamUser.Location = new Point(16, 112);
        _txtStreamUser.Size = new Size(colW - 32, 26);
        _gameCard.Controls.Add(_txtStreamUser);
        _gameCard.Controls.Add(LabelAt(16, 156, "登录密码"));
        _pwdRdp = Ui.FieldBox(password: true);
        _pwdRdp.Location = new Point(16, 176);
        _pwdRdp.Size = new Size(colW - 32 - 78, 26);
        _gameCard.Controls.Add(_pwdRdp);
        _btnVerify = Ui.Secondary("验证");
        _btnVerify.Location = new Point(colW - 16 - 72, 176);
        _btnVerify.Size = new Size(72, 26);
        _btnVerify.Click += (_, _) => VerifyPassword();
        _gameCard.Controls.Add(_btnVerify);
        _lblCredState = SmallAt(16, 216);
        _lblPwState = SmallAt(16, 234);
        _gameCard.Controls.Add(_lblCredState);
        _gameCard.Controls.Add(_lblPwState);
        // 开机自动登录此账户：隐藏，默认开启（AutoLogon 恒 true）
        p.Controls.Add(_gameCard);

        // 单会话占位提示（左列）
        _consoleHintCard = new RoundCard { Location = new Point(leftX, 0), Size = new Size(colW, 316) };
        _consoleHintCard.Controls.Add(Head(16, 14, "当前登录用户"));
        var who = new Label
        {
            Text = WelcomePage.CurrentUser(), ForeColor = Ui.Text, BackColor = Color.Transparent,
            Font = Ui.F(13F, FontStyle.Bold), Location = new Point(16, 44), AutoSize = true,
        };
        _consoleHintCard.Controls.Add(who);
        var pill = new Badge("无需密码", Ui.Success) { Location = new Point(16, 76), Size = new Size(92, 24) };
        _consoleHintCard.Controls.Add(pill);
        var desc = new Label
        {
            Text = "单会话直接使用当前登录用户接管本机画面，不创建额外账户。",
            ForeColor = Ui.Text2, BackColor = Color.Transparent,
            Font = Ui.F(9F), Location = new Point(16, 112), Size = new Size(colW - 32, 44),
        };
        _consoleHintCard.Controls.Add(desc);
        p.Controls.Add(_consoleHintCard);

        // 网页控制台账号（右列；两行说明文字自动换行，预留高度防切割）
        var admin = new RoundCard { Location = new Point(rightX, 0), Size = new Size(colW, 316) };
        admin.Controls.Add(Head(16, 14, "网页控制台账号"));
        admin.Controls.Add(SubAt(16, 40, "在浏览器中管理设备配对与串流设置；与远程游戏账号无关", colW - 32, 2));
        admin.Controls.Add(LabelAt(16, 88, "用户名"));
        _txtWebUser = Ui.FieldBox();
        _txtWebUser.Location = new Point(16, 108);
        _txtWebUser.Size = new Size(colW - 32, 26);
        admin.Controls.Add(_txtWebUser);
        admin.Controls.Add(LabelAt(16, 144, "密码"));
        _pwdWebUi = Ui.FieldBox(password: true);
        _pwdWebUi.Location = new Point(16, 164);
        _pwdWebUi.Size = new Size(colW - 32, 26);
        admin.Controls.Add(_pwdWebUi);
        admin.Controls.Add(LabelAt(16, 200, "确认密码"));
        _pwdWebUi2 = Ui.FieldBox(password: true);
        _pwdWebUi2.Location = new Point(16, 220);
        _pwdWebUi2.Size = new Size(colW - 32, 26);
        admin.Controls.Add(_pwdWebUi2);
        _lblWebPwState = SmallAt(16, 254);
        admin.Controls.Add(_lblWebPwState);
        admin.Controls.Add(MutedAt(16, 274, "安装完成页会列出控制台地址；两处画面共用这一个账号", colW - 32, 2));
        p.Controls.Add(admin);
        _adminCard = admin;

        _pwdRdp.TextChanged += (_, _) =>
        {
            _pwVerified = false;
            _pwVerifiedFor = "";
            if (_lblPwState.Text == "密码正确")
            {
                _lblPwState.Text = "密码已改，请重新验证";
                _lblPwState.ForeColor = Ui.Danger;
            }
        };
        _txtStreamUser.TextChanged += (_, _) =>
        {
            _pwVerified = false;
            _pwVerifiedFor = "";
            _lblPwState.Text = "用户已变更，请重新验证";
            _lblPwState.ForeColor = Ui.Danger;
        };
        _lblCredState.Text = Detect.RdpCredExists() ? "此前已保存过密码，可留空沿用" : "请填写密码并点「验证」";
        UpdateModeDependent();
    }

    
    private void VerifyPassword()
    {
        var pw = _pwdRdp.Text;
        var user = _txtStreamUser.Text.Trim();
        if (pw.Length == 0)
        {
            _lblPwState.Text = "请先输入密码";
            _lblPwState.ForeColor = Ui.Warn;
            return;
        }
        _lblPwState.Text = "验证中...";
        _lblPwState.ForeColor = Ui.Text3;
        _btnVerify.Enabled = false;
        Task.Run(async () =>
        {
            var ok = await WinLogon.VerifyAsync(user, pw);
            BeginInvoke(() =>
            {
                _lblPwState.Text = ok ? "密码正确" : "密码不正确，请重新输入";
                _lblPwState.ForeColor = ok ? Ui.Success : Ui.Danger;
                _pwVerified = ok;
                _pwVerifiedFor = ok ? user + ":" + pw : "";
                _btnVerify.Enabled = true;
            });
        });
    }

    // ------------------------------------------------------------------
    // 03 连接设置
    // ------------------------------------------------------------------
    private void BuildStep2()
    {
        var p = new Panel { Location = new Point(Lay.X0, 156), Size = new Size(Lay.CW, 480), BackColor = Ui.Bg, AutoScroll = true };
        _stepPanels[2] = p;

        _gpus = GpuEnum.Physical();
        var showGpu = _gpus.Count > 0;

        var stack = new VStack(10)
        {
            Location = new Point(0, 0), Size = new Size(Lay.CW, 480),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
        };
        p.Controls.Add(stack);
        _stack2 = stack;

        _gpuCard = new RoundCard { Size = new Size(Lay.CW, 96), Anchor = CardAnchor };
        _gpuCard.Controls.Add(Head(16, 14, "图形设备"));
        _cmbGpu = new ComboBox
        {
            Location = new Point(16, 44), Size = new Size(420, 26),
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Color.White, ForeColor = Ui.Text, Font = Ui.F(9.5F),
        };
        foreach (var g in _gpus)
        {
            var vendor = GpuEnum.Vendor(g);
            _cmbGpu.Items.Add(g + "  [" + vendor + "]");
        }
        if (_cmbGpu.Items.Count > 0) _cmbGpu.SelectedIndex = 0;
        _gpuCard.Controls.Add(_cmbGpu);
        _gpuCard.Controls.Add(MutedAt(16, 74, "用于画面渲染的图形设备；检测到多块时请选择正确的那一块"));
        if (showGpu) stack.Add(_gpuCard);

        var ports = new RoundCard { Size = new Size(Lay.CW, 118), Anchor = CardAnchor };
        ports.Controls.Add(Head(16, 14, "端口设置"));
        ports.Controls.Add(LabelAt(16, 50, "主画面端口"));
        _txtPortMain = Ui.FieldBox("48100");
        _txtPortMain.Location = new Point(130, 46);
        _txtPortMain.Size = new Size(80, 24);
        ports.Controls.Add(_txtPortMain);
        ports.Controls.Add(LabelAt(240, 50, "游戏画面端口"));
        _txtPortGame = Ui.FieldBox("48200");
        _txtPortGame.Location = new Point(354, 46);
        _txtPortGame.Size = new Size(80, 24);
        ports.Controls.Add(_txtPortGame);
        ports.Controls.Add(MutedAt(16, 82, "保持默认即可。两端口基址需相距至少 50；网页控制台使用「端口+1」。"));
        stack.Add(ports);

        var tray = new RoundCard { Size = new Size(Lay.CW, 132), Anchor = CardAnchor };
        tray.Controls.Add(Head(16, 14, "快捷方式"));
        _swTrayDesktop = new SwitchRow("创建桌面快捷方式", true) { Location = new Point(16, 44), Size = new Size(540, 28) };
        tray.Controls.Add(_swTrayDesktop);
        _swTrayStart = new SwitchRow("创建开始菜单快捷方式", true) { Location = new Point(16, 74), Size = new Size(540, 28) };
        tray.Controls.Add(_swTrayStart);
        tray.Controls.Add(MutedAt(36, 106, "托盘程序用于查看会话状态、打开网页控制台与切换显示模式"));
        stack.Add(tray);

        _gmCard = new RoundCard { Size = new Size(Lay.CW, 100), Anchor = CardAnchor };
        _gmCard.Controls.Add(Head(16, 14, "游戏启动器"));
        _swGameMgr = new SwitchRow("安装游戏启动器（推荐）", true) { Location = new Point(16, 44), Size = new Size(540, 28) };
        _gmCard.Controls.Add(_swGameMgr);
        _gmCard.Controls.Add(MutedAt(36, 74, "可直接启动需要管理员权限的游戏；双会话模式会自动安装"));
        stack.Add(_gmCard);
    }

    private const AnchorStyles CardAnchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

    // ------------------------------------------------------------------
    // 04 确认安装
    // ------------------------------------------------------------------
    private void BuildStep3()
    {
        var p = new Panel { Location = new Point(Lay.X0, 156), Size = new Size(Lay.CW, 480), BackColor = Ui.Bg };
        _stepPanels[3] = p;

        var card = new RoundCard { Location = new Point(0, 0), Size = new Size(Lay.CW, 342) };
        card.Controls.Add(Head(16, 14, "安装摘要"));
        _lblSummary = new Label
        {
            Text = "", ForeColor = Ui.Text2, BackColor = Color.Transparent,
            Font = Ui.F(9.5F), Location = new Point(16, 48), Size = new Size(Lay.CW - 32, 280),
        };
        card.Controls.Add(_lblSummary);
        p.Controls.Add(card);

        var note = new Label
        {
            Text = "点击「开始安装」后请勿关闭本窗口。已安装的组件会自动跳过，安装过程在后台执行。",
            ForeColor = Ui.Text3, BackColor = Color.Transparent,
            Font = Ui.F(9F), Location = new Point(4, 356), Size = new Size(Lay.CW - 8, 20),
        };
        p.Controls.Add(note);
    }

    private void UpdateSummary()
    {
        if (_lblSummary is null) return;
        var gpu = SelectedGpuName();
        var skipped = new List<string>();
        if (Detect.ViGEmBus().Installed) skipped.Add("手柄支持");
        if (Detect.RdpWrapper().Installed && _dual) skipped.Add("多会话登录");
        if (Detect.GpuPolicy().Installed && _dual) skipped.Add("画面渲染优化");
        if (Detect.FirewallApollo().Installed) skipped.Add("网络访问规则");
        if (Detect.SudoVdaState().Installed) skipped.Add("虚拟显示器驱动");
        if (Detect.GameManager().Installed) skipped.Add("游戏启动器");

        var lines = new List<(string k, string v)>
        {
            ("安装模式", _dual ? "双会话（本机 + 远程游戏画面）" : "单会话（仅本机画面）"),
            ("图形设备", gpu.Length > 0 ? gpu + "（自动）" : "自动检测"),
            ("登录账户", _dual ? _txtStreamUser.Text.Trim() : WelcomePage.CurrentUser() + "（当前用户）"),
            ("网页控制台", _txtWebUser.Text.Trim()),
            ("端口", _dual
                ? $"主 {_txtPortMain.Text.Trim()} / 游戏 {_txtPortGame.Text.Trim()}"
                : $"主 {_txtPortMain.Text.Trim()}"),
            ("快捷方式", string.Join("、", new[]
            {
                _swTrayDesktop.Checked ? "桌面" : "",
                _swTrayStart.Checked ? "开始菜单" : "",
                "开机自启",
            }.Where(s => s.Length > 0))),
            ("已装跳过", skipped.Count > 0 ? string.Join("、", skipped) : "无"),
        };
        _lblSummary.Text = string.Join(Environment.NewLine + Environment.NewLine,
            lines.Select(l => l.k + "：" + l.v));
    }

    // ------------------------------------------------------------------
    private void UpdateModeDependent()
    {
        if (_gameCard is not null) _gameCard.Visible = _dual;
        if (_consoleHintCard is not null) _consoleHintCard.Visible = !_dual;
        if (_txtPortGame is not null) _txtPortGame.Enabled = _dual;
        if (_gmCard is not null) _gmCard.Visible = !_dual;
        _stack2?.Relayout();
    }

    // ------------------------------------------------------------------
    // ------------------------------------------------------------------
    // 自检用：离屏渲染指定步骤（不校验、不改状态）
    // ------------------------------------------------------------------
    internal void DebugStep(int step)
    {
        _step = Math.Clamp(step, 0, StepCount - 1);
        for (var i = 0; i < StepCount; i++)
        {
            _stepPanels[i].Visible = i == _step;
            _dots[i].Active = i == _step;
            _dotNames[i].ForeColor = i == _step ? Ui.Accent : Ui.Text2;
        }
        _btnBack.Visible = _step > 0;
        _btnNext.Visible = _step < StepCount - 1;
        _btnInstall.Visible = _step == StepCount - 1;
        _stack2?.Relayout();
        if (_step == 3) UpdateSummary();
    }

    // 自检用：切换安装模式后渲染
    internal void DebugSelectMode(bool dual) => SelectMode(dual);

    // 自检用：转储某步面板的直接子控件布局
    internal string DebugDump(int step)
    {
        var p = _stepPanels[Math.Clamp(step, 0, StepCount - 1)];
        var parts = new List<string> { $"panel vis={p.Visible} loc={p.Location} size={p.Size}" };
        foreach (Control c in p.Controls)
            parts.Add($"{c.GetType().Name} vis={c.Visible} loc={c.Location} size={c.Size}");
        var stack = _stack2;
        if (stack is not null)
        {
            parts.Add($"stack loc={stack.Location} size={stack.Size}");
            foreach (Control c in stack.Controls)
                parts.Add($"  child {c.GetType().Name} vis={c.Visible} loc={c.Location} size={c.Size}");
        }
        return string.Join(" | ", parts);
    }

    private void SetStep(int step)
    {
        _step = Math.Clamp(step, 0, StepCount - 1);
        for (var i = 0; i < StepCount; i++)
        {
            _stepPanels[i].Visible = i == _step;
            _dots[i].Active = i == _step;
            _dotNames[i].ForeColor = i == _step ? Ui.Accent : Ui.Text2;
            _dotNames[i].Font = i == _step ? Ui.F(9F, FontStyle.Bold) : Ui.F(9F);
        }
        _btnBack.Visible = _step > 0;
        _btnNext.Visible = _step < StepCount - 1;
        _btnInstall.Visible = _step == StepCount - 1;
        _stack2?.Relayout();
        if (_step == 1 && _txtWebUser.Text.Length == 0)
            _txtWebUser.Text = WelcomePage.CurrentUser();
        if (_step == 3) UpdateSummary();
    }


    // ------------------------------------------------------------------
    // 布局小工具
    // ------------------------------------------------------------------
    private static Label Head(int x, int y, string text)
    {
        var l = Ui.Head(text, 10.5F);
        l.Location = new Point(x, y);
        return l;
    }

    private static Control SubAt(int x, int y, string text, int width = 0, int lines = 1)
        => lines > 1
            ? Wrap(x, y, text, Ui.Text2, 8.5F, width > 0 ? width : Lay.CW - 34, lines)
            : Sized(x, y, text, Ui.Text2, 8.5F);

    private static Label LabelAt(int x, int y, string text) => Sized(x, y, text, Ui.Text2, 9F, auto: true);

    private static Label SmallAt(int x, int y) => Sized(x, y, "", Ui.Text3, 8.5F);

    private static Control MutedAt(int x, int y, string text, int width = 0, int lines = 1)
        => lines > 1
            ? Wrap(x, y, text, Ui.Text3, 8.5F, width > 0 ? width : Lay.CW - 34, lines)
            : Sized(x, y, text, Ui.Text3, 8.5F);

    private static WrapLabel Wrap(int x, int y, string text, Color color, float size, int width, int lines)
        => new(text, color, size) { Location = new Point(x, y), Size = new Size(width, 16 * lines) };

    // 单行标签：超出宽度省略号截断
    private static Label Sized(int x, int y, string text, Color color, float size,
        int width = 0, bool auto = false)
        => new()
        {
            Text = text, ForeColor = color, BackColor = Color.Transparent,
            Font = Ui.F(size),
            Location = new Point(x, y),
            AutoSize = auto,
            Size = auto ? Size.Empty : new Size(width > 0 ? width : Lay.CW - 34, 16),
            AutoEllipsis = true,
        };

    private string SelectedGpuName()
    {
        if (_gpus.Count == 0) return "";
        var idx = _cmbGpu is { SelectedIndex: >= 0 } ? _cmbGpu.SelectedIndex : 0;
        return idx < _gpus.Count ? _gpus[idx] : _gpus[0];
    }

    // ------------------------------------------------------------------
    // 步进校验
    // ------------------------------------------------------------------
    private void OnNext()
    {
        switch (_step)
        {
            case 0:
                SetStep(1);
                return;
            case 1:
                if (!ValidateAccounts()) return;
                SetStep(2);
                return;
            case 2:
                if (!ValidatePorts()) return;
                SetStep(3);
                return;
        }
    }

    private bool ValidateAccounts()
    {
        if (_dual)
        {
            var user = _txtStreamUser.Text.Trim();
            if (user.Length == 0 || !UserExists(user))
            {
                MessageBox.Show(this, $"远程游戏账号「{user}」不是本机用户或不存在，请先创建该账户。", "账户不存在",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            var pw = _pwdRdp.Text;
            if (pw.Length == 0)
            {
                if (Detect.RdpCredExists())
                {
                    var r = MessageBox.Show(this, "密码留空：将沿用此前保存的密码。继续吗？", "沿用已保存密码",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r != DialogResult.Yes) return false;
                }
                else
                {
                    MessageBox.Show(this, "请填写密码并点「验证」后再继续。", "需要密码",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
            }
            else if (!_pwVerified || _pwVerifiedFor != user + ":" + pw)
            {
                MessageBox.Show(this, "密码未通过验证，请点「验证」后再继续。", "密码验证",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        var webUser = _txtWebUser.Text.Trim();
        var webPw = _pwdWebUi.Text;
        if (webUser.Length == 0)
        {
            MessageBox.Show(this, "请填写网页控制台用户名。", "网页控制台", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtWebUser.Focus();
            return false;
        }
        if (webPw.Length == 0)
        {
            MessageBox.Show(this, "请填写网页控制台密码。", "网页控制台", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _pwdWebUi.Focus();
            return false;
        }
        if (webPw != _pwdWebUi2.Text)
        {
            _lblWebPwState.Text = "两次密码不一致";
            _lblWebPwState.ForeColor = Ui.Danger;
            MessageBox.Show(this, "两次密码不一致，请重新输入。", "网页控制台", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        _lblWebPwState.Text = "密码一致";
        _lblWebPwState.ForeColor = Ui.Success;
        return true;
    }

    private bool ValidatePorts()
    {
        if (!int.TryParse(_txtPortMain.Text.Trim(), out var main) || main is < 1024 or > 65520)
        {
            MessageBox.Show(this, "主画面端口须为 1024 至 65520 的整数。", "端口无效",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtPortMain.Focus();
            return false;
        }
        if (_dual)
        {
            if (!int.TryParse(_txtPortGame.Text.Trim(), out var game) || game is < 1024 or > 65520)
            {
                MessageBox.Show(this, "游戏画面端口须为 1024 至 65520 的整数。", "端口无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtPortGame.Focus();
                return false;
            }
            if (Math.Abs(game - main) < 50)
            {
                MessageBox.Show(this, "两个端口基址须相距至少 50，否则端口会重叠。", "端口冲突",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtPortGame.Focus();
                return false;
            }
        }
        return true;
    }

    private static bool UserExists(string name)
    {
        try
        {
            return new System.Security.Principal.NTAccount(name)
                .Translate(typeof(System.Security.Principal.SecurityIdentifier)) is not null;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------
    // 收集选项：可见项取界面值；隐藏项按决策取默认
    // ------------------------------------------------------------------
    private Opts? Collect()
    {
        var o = new Opts
        {
            SingleSession = !_dual,
            Adapter = SelectedGpuName(),
            User = _dual ? _txtStreamUser.Text.Trim() : WelcomePage.CurrentUser(),
            Password = _dual ? _pwdRdp.Text : "",
            AutoLogon = true,
            WebUser = _txtWebUser.Text.Trim(),
            WebUiPassword = _pwdWebUi.Text,
            PortMain = int.TryParse(_txtPortMain.Text.Trim(), out var pm) ? pm : 48100,
            PortGame = _dual && int.TryParse(_txtPortGame.Text.Trim(), out var pg) ? pg : 0,
            // 隐藏项：默认安装
            Gamepad = true,
            GpuAccel = true,
            GameManager = _dual || _swGameMgr.Checked,
            Reset = false,
            // 托盘：自启动隐藏默认开
            TrayDesktop = _swTrayDesktop.Checked,
            TrayStart = _swTrayStart.Checked,
            TrayAuto = true,
            // 虚拟显示器：常见分辨率 16:9 / 16:10 / 21:9，统一 60Hz（界面不展示）
            SudovdaResolutions = new List<string>(ResPresets),
            Vw = 0,
            Vh = 0,
        };
        if (o.Adapter.Length == 0)
        {
            MessageBox.Show(this, "未检测到可用图形设备，请确认显卡驱动已安装。", "图形设备",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        if (o.WebUiPassword.Length < 6)
        {
            MessageBox.Show(this, "网页控制台密码长度不能少于 6 位。", "密码过短",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        if (o.PortMain < 1024 || o.PortMain > 65520)
        {
            MessageBox.Show(this, "主画面端口无效。", "端口无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        if (_dual && (o.PortGame < 1024 || o.PortGame > 65520 || Math.Abs(o.PortGame - o.PortMain) < 50))
        {
            MessageBox.Show(this, "游戏画面端口无效或与主端口过近。", "端口无效",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        return o;
    }

    private void StartInstall()
    {
        var o = Collect();
        if (o is null) return;

        if (_dual && o.Password.Length > 0 && (!_pwVerified || _pwVerifiedFor != o.User + ":" + o.Password))
        {
            _lblPwState.Text = "验证中...";
            _lblPwState.ForeColor = Ui.Text3;
            _btnInstall.Enabled = false;
            Task.Run(async () =>
            {
                var ok = await WinLogon.VerifyAsync(o.User, o.Password);
                BeginInvoke(() =>
                {
                    _btnInstall.Enabled = true;
                    _lblPwState.Text = ok ? "密码正确" : "密码不正确，请重新输入";
                    _lblPwState.ForeColor = ok ? Ui.Success : Ui.Danger;
                    if (ok)
                    {
                        _pwVerified = true;
                        _pwVerifiedFor = o.User + ":" + o.Password;
                        LaunchInstall(o);
                    }
                });
            });
            return;
        }
        LaunchInstall(o);
    }

    private void LaunchInstall(Opts o)
    {
        var reporter = _pump.Start(isInstall: true);
        var steps = Ops.InstallSteps(o, reporter);
        _pump.LastOpts = o;
        _pump.SetLabels(steps.ToDictionary(s => s.Key, s => s.Label));
        _owner.RunPage?.Prepare(true, steps.Select(s => (s.Key, s.Label)));
        _owner.GoRun();
        Task.Run(() =>
        {
            var hasFail = false;
            foreach (var s in steps)
            {
                reporter.StepStart(s.Key);
                try
                {
                    s.Action();
                    reporter.StepDone(s.Key, true);
                }
                catch (Exception ex)
                {
                    reporter.Log("失败: " + ex.Message);
                    reporter.StepDone(s.Key, false);
                    hasFail = true;
                }
            }
            _pump.Finish(hasFail);
        });
    }
}
