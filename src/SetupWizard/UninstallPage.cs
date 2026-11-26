namespace SunshineDuoSetup;

// 卸载页（亮色）：紧凑分组列表；默认勾选与旧版一致
internal sealed class UninstallPage : Panel
{
    private readonly RunPump _pump;
    private MainForm? _owner;

    private SwitchRow _swFirewall = null!;
    private SwitchRow _swGamepad = null!;
    private SwitchRow _swGpuAccel = null!;
    private SwitchRow _swRdpWrap = null!;
    private SwitchRow _swCore = null!;
    private SwitchRow _swVda = null!;
    private SwitchRow _swGameManager = null!;
    private SwitchRow _swTray = null!;

    public UninstallPage(RunPump pump)
    {
        _pump = pump;
        DoubleBuffered = true;

        var title = Ui.Title("卸载", 18);
        title.Location = new Point(Lay.X0, 20);
        Controls.Add(title);
        var sub = new Label
        {
            Text = "选择要移除的功能（不删除程序文件、配置与已保存的密码）",
            ForeColor = Ui.Text3, BackColor = Color.Transparent,
            Font = Ui.F(9F), Location = new Point(Lay.X0 + 2, 60), AutoSize = true,
        };
        Controls.Add(sub);

        var scroll = new Panel
        {
            Location = new Point(Lay.X0, 100), Size = new Size(Lay.CW, 528),
            BackColor = Ui.Bg, AutoScroll = true,
        };
        Controls.Add(scroll);

        var stack = new VStack(12) { Location = new Point(0, 0), Size = new Size(Lay.CW - 8, 500) };
        scroll.Controls.Add(stack);

        var c1 = new RoundCard { Size = new Size(Lay.CW - 8, 162) };
        c1.Controls.Add(Head(16, 14, "连接与登录"));
        _swFirewall = new SwitchRow("移除网络访问规则", true) { Location = new Point(16, 48), Size = new Size(520, 28) };
        c1.Controls.Add(_swFirewall);
        _swRdpWrap = new SwitchRow("移除多会话登录支持", true) { Location = new Point(16, 82), Size = new Size(520, 28) };
        c1.Controls.Add(_swRdpWrap);
        _swGpuAccel = new SwitchRow("移除画面渲染优化", true) { Location = new Point(16, 116), Size = new Size(520, 28) };
        c1.Controls.Add(_swGpuAccel);
        stack.Add(c1);

        var c2 = new RoundCard { Size = new Size(Lay.CW - 8, 140) };
        c2.Controls.Add(Head(16, 14, "服务与显示"));
        _swCore = new SwitchRow("移除会话守护服务（并取消开机自动拉起）", true) { Location = new Point(16, 48), Size = new Size(520, 28) };
        c2.Controls.Add(_swCore);
        _swVda = new SwitchRow("移除虚拟显示器驱动", false) { Location = new Point(16, 82), Size = new Size(520, 28) };
        c2.Controls.Add(_swVda);
        c2.Controls.Add(new Label
        {
            Text = "仅在安装过虚拟显示器时勾选；移除时屏幕可能闪烁。",
            ForeColor = Ui.Text3, BackColor = Color.Transparent,
            Font = Ui.F(8.5F), Location = new Point(36, 112), Size = new Size(520, 16),
        });
        stack.Add(c2);

        var c3 = new RoundCard { Size = new Size(Lay.CW - 8, 162) };
        c3.Controls.Add(Head(16, 14, "应用程序"));
        _swGameManager = new SwitchRow("移除游戏启动器", true) { Location = new Point(16, 48), Size = new Size(520, 28) };
        c3.Controls.Add(_swGameManager);
        _swGamepad = new SwitchRow("移除游戏手柄支持", true) { Location = new Point(16, 82), Size = new Size(520, 28) };
        c3.Controls.Add(_swGamepad);
        _swTray = new SwitchRow("移除托盘程序（含快捷方式与开机自启）", true) { Location = new Point(16, 116) };
        _swTray.Size = new Size(520, 28);
        c3.Controls.Add(_swTray);
        stack.Add(c3);

        stack.Add(new Label
        {
            Text = "卸载不会删除程序文件与游戏存档；如正通过远程操作本机，连接可能短暂中断。",
            ForeColor = Ui.Text3, BackColor = Color.Transparent,
            Font = Ui.F(8.5F), Size = new Size(Lay.CW - 16, 18),
        });

        Controls.Add(new Panel { Location = new Point(0, 640), Size = new Size(Lay.PageW, 1), BackColor = Ui.Line });

        var btnBack = Ui.Secondary("← 返回总览");
        btnBack.Size = new Size(130, 34);
        btnBack.Location = new Point(6, 654);
        btnBack.Click += (_, _) => { if (!_pump.Running) _owner?.Navigate("w"); };
        Controls.Add(btnBack);

        var btnStart = Ui.DangerBtn("开 始 卸 载");
        btnStart.Size = new Size(150, 34);
        btnStart.Location = new Point(Lay.PageW - 10 - 150, 654);
        btnStart.Click += (_, _) => { if (!_pump.Running) StartUninstall(); };
        Controls.Add(btnStart);
    }

    private static Label Head(int x, int y, string text)
    {
        var l = Ui.Head(text, 10.5F);
        l.Location = new Point(x, y);
        return l;
    }

    public void BindOwner(MainForm owner) => _owner = owner;

    public void RefreshChecks() => _swVda.Checked = false;

    private void StartUninstall()
    {
        var o = new Opts
        {
            UnFirewall = _swFirewall.Checked,
            UnGamepad = _swGamepad.Checked,
            UnRdpWrap = _swRdpWrap.Checked,
            UnGpuAccel = _swGpuAccel.Checked,
            UnCore = _swCore.Checked,
            UnVda = _swVda.Checked,
            UnGameManager = _swGameManager.Checked,
            UnTray = _swTray.Checked,
        };
        var any = o.UnFirewall || o.UnGamepad || o.UnRdpWrap || o.UnGpuAccel || o.UnCore || o.UnVda || o.UnGameManager || o.UnTray;
        if (!any)
        {
            MessageBox.Show(this, "请至少勾选一项。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var r = MessageBox.Show(this, "确定执行卸载吗？\n（不删除任何程序文件、配置与已保存的密码）", "确认",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        var reporter = _pump.Start(isInstall: false);
        var steps = Ops.UninstallSteps(o, reporter);
        _pump.SetLabels(steps.ToDictionary(s => s.Key, s => s.Label));
        _owner!.GoRun();
        _owner.RunPage?.Prepare(false, steps.Select(s => (s.Key, s.Label)));
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