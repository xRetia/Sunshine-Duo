using System.Diagnostics;

namespace SunshineDuoTray;

// 内网穿透页：EasyTier 隧道开关、网络名与高级参数（服务器/IP/TCP 打洞）；
// 核心（easytier-core）由核心服务托管，本页经控制管道下发与查询
internal sealed class TunnelPage : Panel
{
    private const string MoonVPlusUrl = "https://github.com/qiin2333/moonlight-vplus/releases";
    private const string EasyTierUrl = "https://easytier.cn/guide/download.html";

    private readonly SegBar _segLevel, _segServer, _segIp;
    private readonly SwitchRow _enable, _tcp;
    private readonly TextBox _network, _secret, _peers, _manualIp;
    private readonly Button _save;
    private readonly Label _status, _vipValue, _serverHint, _ipHint, _note;
    private readonly Button _btnMoon, _btnEt;
    private readonly ToolTip _tip = new();
    private readonly List<Control> _advanced = new();
    private GuardPipe.EasyTierState? _st;
    private bool _busy, _suppress;

    public TunnelPage()
    {
        BackColor = UiKit.WindowBg;
        Font = UiKit.YaHei(9F);
        AutoScroll = true;

        var title = UiKit.Title("内网穿透");
        title.Location = new Point(0, 24);
        Controls.Add(title);

        var sub = UiKit.Sub("通过 P2P 技术使用萌远控");
        sub.Location = new Point(0, 62);
        Controls.Add(sub);

        _segLevel = new SegBar("标准", "高级")
        {
            Location = new Point(560, 61),
            Size = new Size(176, 28),
        };
        _segLevel.Changed += (_, _) => Relayout();
        Controls.Add(_segLevel);

        _enable = new SwitchRow("启用内网穿透", false)
        {
            Location = new Point(0, 104),
            Size = new Size(360, 30),
        };
        _enable.CheckedChanged += async (_, _) => await ApplyAsync();
        Controls.Add(_enable);

        var vipCard = new RoundCard { Location = new Point(376, 104), Size = new Size(360, 104) };
        Controls.Add(vipCard);
        vipCard.Controls.Add(new Label
        {
            Text = "虚拟 IP",
            ForeColor = UiKit.Text,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9.5F, FontStyle.Bold),
            Location = new Point(16, 12),
            AutoSize = true,
        });
        _vipValue = new Label
        {
            ForeColor = UiKit.Text,
            BackColor = Color.Transparent,
            Font = UiKit.Mono(14F, FontStyle.Bold),
            Location = new Point(16, 38),
            Size = new Size(280, 26),
            AutoEllipsis = true,
            Text = "—",
        };
        vipCard.Controls.Add(_vipValue);
        var vipCopy = SmallButton("复制", 292, 12);
        vipCopy.Click += (_, _) => CopyVip();
        vipCard.Controls.Add(vipCopy);

        Controls.Add(FieldLabel("网络名称", 150));
        _network = UiKit.Field();
        _network.Location = new Point(0, 172);
        _network.Width = 360;
        Controls.Add(_network);

        Controls.Add(FieldLabel("网络密钥", 212));
        _secret = UiKit.Field(password: true);
        _secret.Location = new Point(0, 234);
        _secret.Width = 360;
        Controls.Add(_secret);

        _save = UiKit.Primary("保存并应用");
        _save.Location = new Point(0, 276);
        _save.Size = new Size(360, 34);
        _save.Click += async (_, _) => await ApplyAsync();
        Controls.Add(_save);

        _status = new Label
        {
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9F),
            Location = new Point(0, 318),
            Size = new Size(736, 20),
            AutoEllipsis = true,
        };
        Controls.Add(_status);

        // ---- 高级设置（标准模式下隐藏）----
        AddAdvanced(FieldLabel("服务器", 356));
        _segServer = new SegBar("公共服务器", "指定")
        {
            Location = new Point(0, 378),
            Size = new Size(200, 30),
        };
        _segServer.Changed += (_, _) => Relayout();
        AddAdvanced(_segServer);

        _serverHint = new Label
        {
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(8.5F),
            Location = new Point(0, 416),
            Size = new Size(736, 36),
            Text = "默认公共服务器：tcp://easytier.zcm.cloudns.ch:11010",
        };
        AddAdvanced(_serverHint);

        _peers = UiKit.Field();
        _peers.Font = UiKit.Mono(9.5F);
        _peers.Location = new Point(0, 416);
        _peers.Width = 360;
        AddAdvanced(_peers);

        AddAdvanced(FieldLabel("虚拟 IP 地址", 462));
        _segIp = new SegBar("自动（DHCP）", "手动")
        {
            Location = new Point(0, 484),
            Size = new Size(200, 30),
        };
        _segIp.Changed += (_, _) => Relayout();
        AddAdvanced(_segIp);

        _manualIp = UiKit.Field();
        _manualIp.Font = UiKit.Mono(9.5F);
        _manualIp.Location = new Point(216, 484);
        _manualIp.Width = 144;
        AddAdvanced(_manualIp);

        _ipHint = new Label
        {
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(8.5F),
            Location = new Point(0, 520),
            Size = new Size(736, 20),
            Text = "自动模式由内网穿透自动分配（10.126.126.0/24 起）。",
        };
        AddAdvanced(_ipHint);

        _tcp = new SwitchRow("使用 TCP 打洞", false)
        {
            Location = new Point(0, 546),
            Size = new Size(360, 30),
        };
        _tcp.CheckedChanged += async (_, _) => await ApplyAsync();
        AddAdvanced(_tcp);


        // ---- 底部链接（外链按钮，与 WebUI 图标按钮观感一致）----
        _btnMoon = UiKit.GhostIcon("Moonlight V+", Glyphs.External);
        _btnMoon.Click += (_, _) => OpenUrl(MoonVPlusUrl);
        Controls.Add(_btnMoon);
        _btnEt = UiKit.GhostIcon("EasyTier 客户端", Glyphs.External);
        _btnEt.Click += (_, _) => OpenUrl(EasyTierUrl);
        Controls.Add(_btnEt);

        _note = UiKit.Caption("输出与错误见 bin\\SunshineDuoCore\\easytier.out；核心由服务自动保活。");
        Controls.Add(_note);

        Relayout();
        _ = RefreshStatus();
    }

    private Label FieldLabel(string text, int y) => new()
    {
        Text = text,
        ForeColor = UiKit.Muted,
        BackColor = Color.Transparent,
        Font = UiKit.YaHei(9F),
        Location = new Point(0, y),
        AutoSize = true,
    };

    private void AddAdvanced(Control c)
    {
        _advanced.Add(c);
        Controls.Add(c);
    }

    private static Button SmallButton(string text, int x, int y)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(52, 24),
            FlatStyle = FlatStyle.Flat,
            BackColor = UiKit.Card,
            ForeColor = UiKit.Muted,
            Font = UiKit.YaHei(8.5F),
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = UiKit.Border;
        b.FlatAppearance.MouseOverBackColor = UiKit.CardHover;
        return b;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void Relayout()
    {
        var adv = _segLevel.SelectedIndex == 1;
        var custom = _segServer.SelectedIndex == 1;
        var manual = _segIp.SelectedIndex == 1;
        foreach (var c in _advanced) c.Visible = adv;
        _segServer.Visible = adv;
        _peers.Visible = adv && custom;
        _serverHint.Visible = adv && !custom;
        _manualIp.Visible = adv && manual;
        _segIp.Visible = adv;

        var y = adv ? 600 : 356;
        _btnMoon.Location = new Point(0, y);
        _btnEt.Location = new Point(_btnMoon.Width + 12, y);
        _note.Location = new Point(0, y + 46);
    }

    public void Reload() => _ = RefreshStatus();

    public async Task RefreshStatus()
    {
        if (_busy) return;
        var st = await GuardPipe.EasyTierStatus();
        if (st == null)
        {
            _status.ForeColor = UiKit.Warn;
            _status.Text = "核心服务不可达，状态未知。";
            return;
        }
        ApplyState(st);
    }

    public void SetState(GuardPipe.EasyTierState st) => ApplyState(st);

    private void ApplyState(GuardPipe.EasyTierState st)
    {
        _st = st;
        _suppress = true;
        try
        {
            _enable.Checked = st.Enabled;
            if (_network.Text.Trim().Length == 0) _network.Text = st.Network;
            _segServer.SelectedIndex = st.ServerMode == "custom" ? 1 : 0;
            _segIp.SelectedIndex = st.Ipv4.Length > 0 ? 1 : 0;
            _manualIp.Text = st.Ipv4;
            _peers.Text = string.Join("，", st.Peers);
            _tcp.Checked = st.TcpHolePunch;
        }
        finally
        {
            _suppress = false;
        }
        Relayout();

        _network.PlaceholderText = "留空使用计算机名";
        _tip.SetToolTip(_network, "本机计算机名：" + st.Machine);
        _secret.PlaceholderText = st.SecretSet ? "已设置，留空不修改" : "留空为无密钥";
        _peers.PlaceholderText = "tcp://host:11010，多个用逗号分隔";
        _manualIp.PlaceholderText = "如 10.126.126.8";

        if (st.VirtualIp.Length > 0)
        {
            _vipValue.ForeColor = UiKit.Text;
            _vipValue.Text = st.VirtualIp;
        }
        else
        {
            _vipValue.ForeColor = UiKit.Muted;
            _vipValue.Text = st.Running ? "等待分配…" : "—";
        }
    }

    private void CopyVip()
    {
        if (_st == null || _st.VirtualIp.Length == 0) return;
        try
        {
            Clipboard.SetText(_st.VirtualIp);
            _status.ForeColor = UiKit.Accent;
            _status.Text = "已复制虚拟 IP：" + _st.VirtualIp;
        }
        catch { }
    }

    private async Task ApplyAsync()
    {
        if (_busy || _suppress) return;
        var want = _enable.Checked;
        var custom = _segServer.SelectedIndex == 1;
        var peers = ParsePeers(_peers.Text);
        if (custom && peers.Length == 0)
        {
            _status.ForeColor = UiKit.Warn;
            _status.Text = "「指定」需填写节点地址。";
            return;
        }
        var manual = _segIp.SelectedIndex == 1;
        var ipv4 = manual ? _manualIp.Text.Trim() : "";
        if (manual && ipv4.Length == 0)
        {
            _status.ForeColor = UiKit.Warn;
            _status.Text = "手动模式需填写虚拟 IP。";
            return;
        }

        _busy = true;
        _save.Enabled = _enable.Enabled = false;
        _status.ForeColor = UiKit.Muted;
        _status.Text = "正在应用…";
        try
        {
            var (st, err) = await GuardPipe.EasyTierSet(new GuardPipe.EasyTierApply
            {
                Enabled = want,
                Network = _network.Text.Trim(),
                Secret = _secret.Text.Length > 0 ? _secret.Text : null,
                ServerMode = custom ? "custom" : "public",
                Peers = peers,
                Ipv4 = ipv4,
                TcpHolePunch = _tcp.Checked,
            });
            if (st == null)
            {
                _status.ForeColor = UiKit.Danger;
                _status.Text = "应用失败：" + err;
                _suppress = true;
                try { _enable.Checked = !want; } finally { _suppress = false; }
            }
            else
            {
                ApplyState(st);
                _status.ForeColor = err != null ? UiKit.Danger : UiKit.Accent;
                _status.Text = err != null
                    ? "已保存，但启动失败：" + err
                    : want ? (st.Running ? "内网穿透已启动。" : "已启用，等待核心就绪。") : "内网穿透已停止。";
                if (st.SecretSet && _secret.Text.Length > 0) _secret.Clear();
            }
        }
        finally
        {
            _busy = false;
            _save.Enabled = _enable.Enabled = true;
        }
    }

    private static string[] ParsePeers(string text)
        => text.Split(new[] { ',', '，', ';', '；', ' ', '\r', '\n', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}