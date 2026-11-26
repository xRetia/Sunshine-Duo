namespace SunshineDuoSetup;

// 更新页（亮色）：检测 .VERSION → 下载发布包 → 停服务替换 → 拉起核心服务与托盘
internal sealed class UpdatePage : Panel
{
    private readonly RunPump _pump;
    private MainForm? _owner;

    private Label _lblLocal = null!;
    private Label _lblRemote = null!;
    private Label _lblStatus = null!;
    private RButton _btnCheck = null!;
    private RButton _btnApply = null!;
    private TextBox _txtLog = null!;
    private Badge _badge = null!;

    private string _remoteTag = "";
    private bool _autoUpdate;

    public UpdatePage(RunPump pump)
    {
        _pump = pump;
        DoubleBuffered = true;

        var title = Ui.Title("更新", 18);
        title.Location = new Point(Lay.X0, 24);
        Controls.Add(title);

        var sub = new Label
        {
            Text = "检测新版本并替换本机程序（保留配置与已配对数据）；更新完成后会自动恢复核心服务与托盘",
            ForeColor = Ui.Text2, BackColor = Color.Transparent,
            Font = Ui.F(9F), Location = new Point(Lay.X0 + 2, 60), AutoSize = true,
        };
        Controls.Add(sub);

        var card = new RoundCard { Location = new Point(Lay.X0, 96), Size = new Size(Lay.CW, 184) };
        card.Controls.Add(new Label
        {
            Text = "本机版本", ForeColor = Ui.Text3, BackColor = Color.Transparent,
            Font = Ui.F(9.5F), Location = new Point(16, 14), AutoSize = true,
        });
        _lblLocal = new Label
        {
            Text = "", ForeColor = Ui.Text, BackColor = Color.Transparent,
            Font = Ui.F(11F, FontStyle.Bold), Location = new Point(120, 12), AutoSize = true,
        };
        card.Controls.Add(_lblLocal);
        _badge = new Badge("检查中", Ui.Text3) { Location = new Point(Lay.CW - 16 - 96, 10), Size = new Size(96, 24) };
        card.Controls.Add(_badge);

        card.Controls.Add(new Label
        {
            Text = "远程版本", ForeColor = Ui.Text3, BackColor = Color.Transparent,
            Font = Ui.F(9.5F), Location = new Point(16, 50), AutoSize = true,
        });
        _lblRemote = new Label
        {
            Text = "-", ForeColor = Ui.Text, BackColor = Color.Transparent,
            Font = Ui.F(11F, FontStyle.Bold), Location = new Point(120, 48), AutoSize = true,
        };
        card.Controls.Add(_lblRemote);

        _lblStatus = new Label
        {
            Text = "将检测 " + Updater.VersionUrl,
            ForeColor = Ui.Text2, BackColor = Color.Transparent,
            Font = Ui.F(8.5F), Location = new Point(16, 82), Size = new Size(Lay.CW - 32, 40),
        };
        card.Controls.Add(_lblStatus);

        _btnCheck = Ui.Secondary("检查更新");
        _btnCheck.Size = new Size(120, 32);
        _btnCheck.Location = new Point(16, 134);
        _btnCheck.Click += (_, _) => { if (!_pump.Running) Check(silent: true); };
        card.Controls.Add(_btnCheck);

        _btnApply = Ui.Primary("立即更新");
        _btnApply.Size = new Size(130, 32);
        _btnApply.Location = new Point(Lay.CW - 16 - 130, 134);
        _btnApply.Enabled = false;
        _btnApply.Click += (_, _) => { if (!_pump.Running) Apply(); };
        card.Controls.Add(_btnApply);
        Controls.Add(card);

        var logCard = new RoundCard { Location = new Point(Lay.X0, 296), Size = new Size(Lay.CW, 326) };
        _txtLog = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(250, 250, 250), ForeColor = Color.FromArgb(43, 43, 43),
            Font = Ui.Mono(9F), Location = new Point(12, 12), Size = new Size(Lay.CW - 24, 302),
        };
        logCard.Controls.Add(_txtLog);
        Controls.Add(logCard);

        Controls.Add(new Panel { Location = new Point(0, 640), Size = new Size(Lay.PageW, 1), BackColor = Ui.Line });
        var btnBack = Ui.Secondary("← 返回总览");
        btnBack.Size = new Size(130, 34);
        btnBack.Location = new Point(6, 654);
        btnBack.Click += (_, _) => { if (!_pump.Running) _owner?.Navigate("w"); };
        Controls.Add(btnBack);
    }

    public void BindOwner(MainForm owner) => _owner = owner;

    public void RefreshLocal() => _lblLocal.Text = "v" + Updater.LocalVersion;

    public void EnterUpdateMode()
    {
        _autoUpdate = true;
        Check(silent: false);
    }

    private void Check(bool silent)
    {
        _badge.Set("检查中", Ui.Text3);
        _lblRemote.Text = "-";
        _lblStatus.Text = "正在检测 " + Updater.VersionUrl + " ...";
        _btnCheck.Enabled = false;
        _btnApply.Enabled = false;
        Task.Run(async () =>
        {
            var remote = await Updater.CheckAsync(CancellationToken.None);
            BeginInvoke(() =>
            {
                _btnCheck.Enabled = true;
                if (!remote.Ok)
                {
                    _badge.Set("检测失败", Ui.Warn);
                    _lblStatus.Text = "检测失败：" + remote.Error;
                    if (!silent)
                        MessageBox.Show(this, "检测更新失败：" + remote.Error, "更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else MaybeAutoApply();
                    return;
                }
                _remoteTag = remote.Tag;
                _lblRemote.Text = remote.Tag;
                if (!Updater.IsNewer(remote.Tag, Updater.LocalVersion))
                {
                    _badge.Set("已是最新", Ui.Success);
                    _lblStatus.Text = "本机已是最新版本（" + remote.Tag + "）。";
                    _btnApply.Enabled = false;
                    _remoteTag = "";
                    MaybeAutoApply();
                    return;
                }
                _badge.Set("有新版本", Ui.Accent);
                _lblStatus.Text = "发现新版本 " + remote.Tag + "，点击「立即更新」开始（下载 → 停止服务 → 替换文件 → 恢复服务与托盘）。";
                _btnApply.Enabled = true;
                MaybeAutoApply();
            });
        });
    }

    private void MaybeAutoApply()
    {
        if (!_autoUpdate) return;
        _autoUpdate = false;
        if (_btnApply.Enabled) Apply(autoConfirmed: true);
    }

    private void Apply(bool autoConfirmed = false)
    {
        if (_remoteTag.Length == 0) return;
        if (!autoConfirmed)
        {
            var r = MessageBox.Show(this,
                $"确定更新到 {_remoteTag} 吗？\n\n将下载更新包、停止运行中的服务与程序、替换程序文件（保留配置与本机数据），完成后自动恢复。",
                "更新确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (r != DialogResult.OK) return;
        }

        _btnApply.Enabled = false;
        _btnCheck.Enabled = false;
        _badge.Set("更新中", Ui.Accent);
        _txtLog.AppendText("[更新] 开始更新到 " + _remoteTag + Environment.NewLine);
        var tag = _remoteTag;
        Task.Run(async () =>
        {
            try
            {
                var asset = await Updater.FindAssetUrlAsync(tag, CancellationToken.None);
                if (asset is null)
                    throw new Exception($"未找到 {tag} 的发布包；请到 {Updater.ReleasesPageUrl} 手动下载。");
                BeginInvoke(() => _lblStatus.Text = "下载更新包 ...");
                Updater.Apply(tag, asset, Paths.Root, s => BeginInvoke(() =>
                {
                    _txtLog.AppendText(s + Environment.NewLine);
                }), CancellationToken.None);
                BeginInvoke(() =>
                {
                    _badge.Set("更新完成", Ui.Success);
                    _lblStatus.Text = $"已更新到 {tag}，核心服务与托盘已恢复。";
                    _btnCheck.Enabled = true;
                    MessageBox.Show(this, $"更新完成：已替换本机程序到 {tag}。", "更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            }
            catch (Exception ex)
            {
                BeginInvoke(() =>
                {
                    _badge.Set("更新失败", Ui.Danger);
                    _txtLog.AppendText("失败: " + ex.Message + Environment.NewLine);
                    _lblStatus.Text = "更新失败：" + ex.Message;
                    _btnCheck.Enabled = true;
                    _btnApply.Enabled = true;
                });
            }
        });
    }
}