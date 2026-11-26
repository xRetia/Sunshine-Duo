namespace SunshineDuoSetup;

// 执行页（亮色）
internal sealed class RunPage : Panel
{
    private readonly RunPump _pump;
    private readonly Dictionary<string, StepRow> _rows = [];
    private readonly List<string> _order = [];
    private int _running; // 当前执行步骤的序号（1 起，0 = 没有在执行的步骤）
    private Label _lblTitle = null!;
    private Label _lblCurrent = null!;
    private Label _lblStepCount = null!;
    private ProgressBar _bar = null!;
    private Panel _stepsHost = null!;
    private TextBox _txtLog = null!;

    public RunPage(RunPump pump)
    {
        _pump = pump;
        DoubleBuffered = true;

        _lblTitle = new Label
        {
            Text = "正在安装...", ForeColor = Ui.Text, BackColor = Color.Transparent,
            Font = Ui.F(14F, FontStyle.Bold), Location = new Point(Lay.X0, 22), AutoSize = true,
        };
        Controls.Add(_lblTitle);

        _lblCurrent = new Label
        {
            Text = "", ForeColor = Ui.Text2, BackColor = Color.Transparent,
            Font = Ui.F(9.5F), Location = new Point(Lay.X0 + 1, 56),
            Size = new Size(Lay.CW, 18), AutoEllipsis = true,
        };
        Controls.Add(_lblCurrent);

        _bar = new ProgressBar { Location = new Point(Lay.X0, 84), Size = new Size(Lay.CW, 8) };
        Controls.Add(_bar);

        _lblStepCount = new Label
        {
            Text = "", ForeColor = Ui.Text3, BackColor = Color.Transparent,
            Font = Ui.F(8.5F), Location = new Point(Lay.X0, 98), AutoSize = true,
        };
        Controls.Add(_lblStepCount);

        _stepsHost = new Panel { Location = new Point(Lay.X0, 124), Size = new Size(Lay.CW, 250), BackColor = Ui.Bg };
        Controls.Add(_stepsHost);

        var logCard = new RoundCard { Location = new Point(Lay.X0, 384), Size = new Size(Lay.CW, 286) };
        _txtLog = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(250, 250, 250), ForeColor = Color.FromArgb(43, 43, 43),
            Font = Ui.Mono(9F), Location = new Point(12, 12), Size = new Size(Lay.CW - 24, 262),
        };
        logCard.Controls.Add(_txtLog);
        Controls.Add(logCard);

        _pump.Logged += line => BeginInvoke(() =>
        {
            _txtLog.AppendText(line + Environment.NewLine);
            if (_txtLog.TextLength > 0) _txtLog.SelectionStart = _txtLog.TextLength;
        });
        _pump.StepStarted += key => BeginInvoke(() =>
        {
            if (_rows.TryGetValue(key, out var r))
            {
                r.SetRunning();
                _lblCurrent.Text = "正在处理：" + r.Text;
                var idx = _order.IndexOf(key);
                if (idx >= 0) _running = idx + 1;
            }
            UpdateProgress();
        });
        _pump.StepFinished += (key, ok) => BeginInvoke(() =>
        {
            if (_rows.TryGetValue(key, out var r))
            {
                if (ok) r.SetOk(); else r.SetFail();
            }
            _running = 0;
            UpdateProgress();
        });
        _pump.RunFinished += _ => BeginInvoke(UpdateProgress);
    }

    public void Prepare(bool isInstall, IEnumerable<(string Key, string Label)> steps)
    {
        _rows.Clear();
        _order.Clear();
        _running = 0;
        _stepsHost.Controls.Clear();
        _txtLog.Clear();
        _bar.Value = 0;
        _lblCurrent.Text = "";
        _lblStepCount.Text = "";
        _lblTitle.Text = isInstall ? "正在安装..." : "正在卸载...";

        var y = 0;
        foreach (var (key, label) in steps)
        {
            var row = new StepRow(label) { Location = new Point(2, y), Size = new Size(Lay.CW - 4, 26) };
            _stepsHost.Controls.Add(row);
            _rows[key] = row;
            _order.Add(key);
            y += 26;
        }
    }

    private void UpdateProgress()
    {
        var total = _order.Count;
        if (total == 0) return;
        var finished = _order.Count(k => _rows[k].Tag is "ok" or "fail");
        _bar.Value = finished * 100.0 / total;
        // 与旧版一致：显示当前正在执行的步骤序号；没有执行中的步骤时显示已完成数
        _lblStepCount.Text = $"步骤 {(_running > 0 ? _running : finished)} / {total}";
    }

    public List<(string Key, string Label, bool Ok)> Rows()
        => _order.Select(k => (k, _rows[k].Text, _rows[k].Tag as string == "ok")).ToList();
}

// 结果页（亮色）
internal sealed class ResultPage : Panel
{
    private readonly RunPump _pump;
    private Label _lblIcon = null!;
    private Label _lblTitle = null!;
    private Label _lblNote = null!;
    private Panel _resHost = null!;
    private RoundCard _webCard = null!;
    private RoundCard _resCard = null!;
    private Label _lblWebUiInfo = null!;
    private RButton _btnViewLog = null!;
    private RButton _btnFinish = null!;
    private MainForm? _owner;

    public ResultPage(RunPump pump)
    {
        _pump = pump;
        DoubleBuffered = true;

        _lblIcon = new Label
        {
            Text = "✓", ForeColor = Ui.Success, BackColor = Color.Transparent,
            Font = Ui.F(34F, FontStyle.Bold), Location = new Point(346, 34), AutoSize = true,
        };
        Controls.Add(_lblIcon);

        _lblTitle = new Label
        {
            Text = "安装完成", ForeColor = Ui.Text, BackColor = Color.Transparent,
            Font = Ui.F(17F, FontStyle.Bold), Location = new Point(0, 106), Size = new Size(Lay.PageW, 30),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(_lblTitle);

        _lblNote = new Label
        {
            Text = "", ForeColor = Ui.Text2, BackColor = Color.Transparent,
            Font = Ui.F(9.5F), Location = new Point(Lay.X0, 142), Size = new Size(Lay.CW, 20),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(_lblNote);

        _webCard = new RoundCard { Location = new Point(Lay.X0, 176), Size = new Size(Lay.CW, 126) };
        var webHead = Ui.Head("网页控制台登录", 11.5F);
        webHead.Location = new Point(16, 12);
        _webCard.Controls.Add(webHead);
        _lblWebUiInfo = new Label
        {
            Text = "", ForeColor = Ui.Text2, BackColor = Color.Transparent,
            Font = Ui.F(9.5F), Location = new Point(16, 42), Size = new Size(Lay.CW - 32, 76),
        };
        _webCard.Controls.Add(_lblWebUiInfo);
        _webCard.Visible = false;
        Controls.Add(_webCard);

        var resCard = new RoundCard { Location = new Point(Lay.X0, 314), Size = new Size(Lay.CW, 240) };
        _resCard = resCard;
        _resHost = new Panel { Location = new Point(14, 12), Size = new Size(Lay.CW - 28, 216), BackColor = Ui.Card, AutoScroll = true };
        resCard.Controls.Add(_resHost);
        Controls.Add(resCard);

        _btnViewLog = Ui.Secondary("查看完整日志");
        _btnViewLog.Size = new Size(150, 36);
        _btnViewLog.Location = new Point(Lay.X0 + (Lay.CW - 300) / 2, 566);
        _btnViewLog.Click += (_, _) => { if (!_pump.Running) _owner?.GoRun(); };
        Controls.Add(_btnViewLog);

        _btnFinish = Ui.Primary("完 成");
        _btnFinish.Size = new Size(140, 36);
        _btnFinish.Location = new Point(_btnViewLog.Right + 10, 566);
        _btnFinish.Click += (_, _) => FindForm()?.Close();
        Controls.Add(_btnFinish);
    }

    public void BindOwner(MainForm owner) => _owner = owner;

    public void ShowResult(bool isInstall, bool hasFail, bool aborted, Opts? opts, List<(string Key, string Label, bool Ok)> results)
    {
        _resHost.Controls.Clear();
        var failed = 0;
        var y = 0;
        foreach (var (_, label, ok) in results)
        {
            if (!ok) failed++;
            var row = new StepRow(label) { Location = new Point(4, y), Size = new Size(Lay.CW - 36, 26) };
            if (ok) row.SetOk(); else row.SetFail();
            _resHost.Controls.Add(row);
            y += 26;
        }

        _lblIcon.Text = aborted || failed > 0 ? "!" : "✓";
        _lblIcon.ForeColor = failed == 0 && !aborted ? Ui.Success : Ui.Warn;
        _lblTitle.Text = aborted ? (isInstall ? "安装已中断" : "卸载已中断")
            : failed == 0 ? (isInstall ? "安装完成" : "卸载完成")
            : $"{(isInstall ? "安装" : "卸载")}完成，{failed} 项未成功";
        _lblNote.Text = aborted ? "流程异常中断，后续步骤已停止。请点「查看完整日志」查看详情。"
            : failed == 0 ? "全部步骤执行成功。详细过程请点「查看完整日志」查看。"
            : "部分步骤未成功，请查看日志排查；失败项不影响其他已成功项。";

        if (isInstall && !aborted && opts is not null)
        {
            var lines = new List<string> { "用户名：" + opts.WebUser };
            if (opts.WebUiPassword.Length > 0) lines.Add("密码：" + opts.WebUiPassword);
            lines.Add("主画面控制台：https://localhost:" + (opts.PortMain + 1));
            if (opts.GameEnabled && opts.PortGame > 0)
                lines.Add("游戏画面控制台：https://localhost:" + (opts.PortGame + 1));
            lines.Add(opts.GameEnabled
                ? "（两处地址使用同一账号；浏览器提示证书不受信任时继续访问即可）"
                : "（单会话：仅本机画面）");
            _lblWebUiInfo.Text = string.Join(Environment.NewLine, lines);

            // 按实测文本高度定卡片高度，避免多行说明被裁掉
            var h = TextRenderer.MeasureText(_lblWebUiInfo.Text, _lblWebUiInfo.Font,
                new Size(Lay.CW - 32, int.MaxValue),
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + 4;
            _lblWebUiInfo.Size = new Size(Lay.CW - 32, h);
            _webCard.Height = 42 + h + 12;
            _resCard.Top = _webCard.Bottom + 12;
            _resCard.Height = Math.Max(120, 566 - 12 - _resCard.Top);
            _webCard.Visible = true;
        }
        else
        {
            _webCard.Visible = false;
            _resCard.Top = 314;
            _resCard.Height = 240;
        }
    }
}