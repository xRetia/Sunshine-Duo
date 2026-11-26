namespace SunshineDuoSetup;

// 总览页：亮色 WinUI 居中内容列
internal sealed class WelcomePage : Panel
{
    private readonly Action<string> _navigate;
    private readonly RunPump _pump;

    private Panel? _checksHost;

    public WelcomePage(Action<string> navigate, RunPump pump)
    {
        _navigate = navigate;
        _pump = pump;
        DoubleBuffered = true;

        var title = Ui.Title("欢迎使用 萌远控 Duo", 22);
        title.Location = new Point(Lay.X0, 24);
        Controls.Add(title);

        var sub = new Label
        {
            Text = "跟随安装向导，在这台电脑上完成萌远控 Duo 的部署与配置。",
            ForeColor = Ui.Text2, BackColor = Color.Transparent,
            Font = Ui.F(10.5F), Location = new Point(Lay.X0 + 2, 72),
            Size = new Size(Lay.CW, 20),
        };
        Controls.Add(sub);

        // 安装位置单行
        var info = new RoundCard { Location = new Point(Lay.X0, 118), Size = new Size(Lay.CW, 42) };
        Controls.Add(info);
        var lblRoot = new Label
        {
            Text = "安装位置  " + Paths.Root, ForeColor = Ui.Text3, BackColor = Color.Transparent,
            Font = Ui.F(9.5F), Location = new Point(16, 11), AutoSize = true,
        };
        var tip = new ToolTip();
        tip.SetToolTip(lblRoot, Paths.Root);
        info.Controls.Add(lblRoot);

        // 组件检测：行区按 7 行 × 30px 收紧，不再留大片空白
        var checkCard = new RoundCard { Location = new Point(Lay.X0, 174), Size = new Size(Lay.CW, 296) };
        Controls.Add(checkCard);
        var checkHead = Ui.Head("组件检测", 11F);
        checkHead.Location = new Point(16, 14);
        checkCard.Controls.Add(checkHead);
        var checkSub = new Label
        {
            Text = "已安装的组件会打勾，安装时自动跳过",
            ForeColor = Ui.Text3, BackColor = Color.Transparent,
            Font = Ui.F(8.5F), Location = new Point(126, 17), AutoSize = true,
        };
        checkCard.Controls.Add(checkSub);
        _checksHost = new Panel { Location = new Point(16, 44), Size = new Size(Lay.CW - 32, 240), BackColor = Ui.Card };
        checkCard.Controls.Add(_checksHost);

        Controls.Add(TipBanner(Lay.X0, 484, "安装前请保存工作", "安装会注册系统服务与驱动；如提示重启，请在完成后重启电脑。"));

        var btn = Ui.Primary("开始安装配置 >");
        btn.Size = new Size(210, 38);
        btn.Location = new Point(Lay.X0 + (Lay.CW - 210) / 2, 566);
        btn.Click += (_, _) => { if (!_pump.Running) _navigate("s"); };
        Controls.Add(btn);

        Reload();
    }

    private static RoundCard TipBanner(int x, int y, string bold, string rest)
    {
        var card = new RoundCard(fill: Color.White, stroke: Ui.Line)
        {
            Location = new Point(x, y), Size = new Size(Lay.CW, 68),
        };
        var dot = new Panel { Location = new Point(18, 18), Size = new Size(32, 32), BackColor = Color.FromArgb(255, 140, 0) };
        using (var p = Ui.RoundRect(new RectangleF(0, 0, 32, 32), 16))
            dot.Region = new Region(p);
        var check = new Label
        {
            Text = "✓", ForeColor = Color.White, BackColor = Color.Transparent,
            Font = Ui.F(13F, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        };
        dot.Controls.Add(check);
        card.Controls.Add(dot);
        card.Controls.Add(new Label
        {
            Text = bold, ForeColor = Color.Black, BackColor = Color.Transparent,
            Font = Ui.F(11F, FontStyle.Bold), Location = new Point(64, 9), AutoSize = true,
        });
        card.Controls.Add(new Label
        {
            Text = rest, ForeColor = Color.FromArgb(150, 32, 24), BackColor = Color.Transparent,
            Font = Ui.F(9.5F), Location = new Point(64, 35), Size = new Size(Lay.CW - 84, 24),
        });
        return card;
    }

    public void Reload()
    {
        BuildChecks();
    }

    private void BuildChecks()
    {
        if (_checksHost is null) return;
        _checksHost.Controls.Clear();
        var rows = new (string Name, Detect.State State)[]
        {
            ("游戏手柄支持", Detect.ViGEmBus()),
            ("多会话登录", Detect.RdpWrapper()),
            ("画面渲染优化", Detect.GpuPolicy()),
            ("网络访问规则", Detect.FirewallApollo()),
            ("虚拟显卡", Detect.SudoVdaState()),
            ("游戏管理器", Detect.GameManager()),
            ("会话服务", Detect.CoreService()),
        };
        var w = _checksHost.Width;
        for (var i = 0; i < rows.Length; i++)
        {
            var (name, state) = rows[i];
            var y = i * 34;
            var lbl = new Label
            {
                Text = name, ForeColor = Ui.Text, BackColor = Color.Transparent,
                Font = Ui.F(9F), Location = new Point(6, y + 8), AutoSize = true,
            };
            var detail = new Label
            {
                Text = state.Detail, ForeColor = Ui.Text3, BackColor = Color.Transparent,
                Font = Ui.F(8.5F), Location = new Point(176, y + 10),
                Size = new Size(w - 176 - 104, 16), AutoEllipsis = true,
            };
            var badge = new Badge(state.Installed ? "已安装" : "未安装", state.Installed ? Ui.Success : Ui.Text3)
            {
                Location = new Point(w - 96, y + 7), Size = new Size(88, 20),
            };
            _checksHost.Controls.Add(lbl);
            _checksHost.Controls.Add(detail);
            _checksHost.Controls.Add(badge);
        }
    }

    internal static string CurrentUser()
    {
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var parts = id.Name.Split('\\');
            return parts.Length > 0 ? parts[^1] : Environment.UserName;
        }
        catch { return Environment.UserName; }
    }
}

// 关于页（亮色）
internal sealed class AboutPage : Panel
{
    public AboutPage()
    {
        DoubleBuffered = true;
        var version = typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        var title = Ui.Title("关于", 20);
        title.Location = new Point(Lay.X0, 32);
        Controls.Add(title);

        var card = new RoundCard { Location = new Point(Lay.X0, 90), Size = new Size(Lay.CW, 380) };
        Controls.Add(card);

        card.Controls.Add(new Label
        {
            Text = "萌远控 Duo", ForeColor = Ui.Text, BackColor = Color.Transparent,
            Font = Ui.F(16F, FontStyle.Bold), Location = new Point(20, 22), AutoSize = true,
        });
        card.Controls.Add(new Label
        {
            Text = "部署工具 v" + version,
            ForeColor = Ui.Text3, BackColor = Color.Transparent,
            Font = Ui.F(9F), Location = new Point(20, 62), AutoSize = true,
        });
        card.Controls.Add(new Label
        {
            Text = "来自萌艺科技的远程控制产品，萌远控 Duo。\n\n" +
                   "支持双会话并行：一个会话用于远程或本地办公，另一个会话在手机上畅玩 3A 大作，" +
                   "游戏办公两不误、互不干扰。\n\n" +
                   "部署工具负责安装与卸载串流环境（画面服务、多会话登录、会话守护、虚拟显示器与托盘）；" +
                   "日常操作（会话管理、配对、显示模式切换）请在托盘程序里完成。\n\n" +
                   "更新：左侧「更新」页自动检测新版本并替换本机程序，保留配置与已配对数据。",
            ForeColor = Ui.Text2, BackColor = Color.Transparent,
            Font = Ui.F(9.5F), Location = new Point(20, 100), Size = new Size(Lay.CW - 40, 264),
        });

        var credit = new Label
        {
            Text = "版权所有 (c) 苏羽茜工作室 · 基于 Sunshine / Apollo（GPL-3.0）",
            ForeColor = Ui.Text3, BackColor = Color.Transparent,
            Font = Ui.F(8.5F), Location = new Point(Lay.X0 + 2, 494), AutoSize = true,
        };
        Controls.Add(credit);
    }
}