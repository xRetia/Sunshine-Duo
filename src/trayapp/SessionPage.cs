using System.Linq;

namespace SunshineDuoTray;

// 右侧会话页：本机 IP / WAN IP / 会话端口 + PIN 配对
internal sealed class SessionPage : Panel
{
    private readonly AppConfig _cfg;
    private readonly bool _isConsole;
    private Paths.SessionInfo _s;
    private readonly Label _title, _sub, _localIp, _wanIp, _port, _addr, _pairStatus, _toast;
    private readonly Pill _pill;
    private readonly TextBox[] _pin = new TextBox[4];
    private readonly Button _pair, _wanBtn;
    private readonly System.Windows.Forms.Timer _toastTimer;
    private readonly ToolTip _tip = new();
    private bool _busy;
    private bool _wanBusy;

    public SessionPage(AppConfig cfg, Paths.SessionInfo session, bool isConsole)
    {
        _cfg = cfg;
        _isConsole = isConsole;
        _s = session;
        BackColor = UiKit.WindowBg;
        Font = UiKit.YaHei(9F);
        AutoScroll = true;

        _title = UiKit.Title(isConsole ? "主会话" : "游戏会话");
        _title.Location = new Point(0, 24);
        Controls.Add(_title);

        _sub = UiKit.Sub(isConsole ? "物理屏与虚拟屏串流 · Console SYSTEM 会话" : "RDP 用户桌面 · 游戏会话");
        _sub.Location = new Point(0, 62);
        Controls.Add(_sub);

        _pill = new Pill { Location = new Point(468, 26), Size = new Size(168, 30) };
        _pill.Set("检测中", UiKit.Muted);
        Controls.Add(_pill);

        // 三张信息卡
        _localIp = AddCard(0, "本机 IP", "局域网内 Moonlight 直接连接", out var c1);
        _wanIp = AddCard(252, "WAN 公网 IP", "ip.sb 实时获取 · 5 分钟缓存", out var c2);
        _port = AddCard(504, "会话端口", "Moonlight 使用的串流端口", out var c3);
        _localIp.Font = UiKit.Mono(13F, FontStyle.Bold);
        _wanIp.Font = UiKit.Mono(13F, FontStyle.Bold);
        _port.Font = UiKit.Mono(13F, FontStyle.Bold);

        _wanBtn = SmallButton("刷新", 166, 12);
        _wanBtn.Click += async (_, _) => await RefreshWanAsync(true);
        c2.Controls.Add(_wanBtn);

        var copyLocal = SmallButton("复制", 166, 68);
        copyLocal.Click += (_, _) => CopyToClipboard(_localIp.Text);
        c1.Controls.Add(copyLocal);

        var copyWan = SmallButton("复制", 166, 68);
        copyWan.Click += (_, _) => CopyToClipboard(_wanIp.Text);
        c2.Controls.Add(copyWan);

        var copyPort = SmallButton("复制", 166, 68);
        copyPort.Click += (_, _) => CopyToClipboard(_port.Text);
        c3.Controls.Add(copyPort);

        // Moonlight 连接地址
        var addrCard = new RoundCard { Location = new Point(0, 220), Size = new Size(736, 56) };
        Controls.Add(addrCard);
        addrCard.Controls.Add(new Label
        {
            Text = "Moonlight 连接地址",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(8.5F),
            Location = new Point(16, 8),
            AutoSize = true,
        });
        _addr = new Label
        {
            ForeColor = UiKit.Text,
            BackColor = Color.Transparent,
            Font = UiKit.Mono(13F, FontStyle.Bold),
            Location = new Point(16, 25),
            AutoSize = true,
        };
        addrCard.Controls.Add(_addr);
        var copyAddr = SmallButton("复制", 0, 0);
        copyAddr.Location = new Point(656, 14);
        copyAddr.Size = new Size(64, 28);
        copyAddr.Click += (_, _) => CopyToClipboard(_addr.Text);
        addrCard.Controls.Add(copyAddr);

        // PIN 配对
        var pinTitle = UiKit.Title("PIN 配对", 13F);
        pinTitle.Location = new Point(0, 302);
        Controls.Add(pinTitle);
        var pinHint = UiKit.Sub("在 Moonlight 中连接本机后，把它显示的 4 位配对码填到下面。");
        pinHint.Location = new Point(0, 330);
        Controls.Add(pinHint);

        for (var i = 0; i < 4; i++)
        {
            var idx = i;
            _pin[i] = new TextBox
            {
                Location = new Point(i * 76, 360),
                Size = new Size(64, 52),
                MaxLength = 1,
                TextAlign = HorizontalAlignment.Center,
                Font = UiKit.Mono(18F, FontStyle.Bold),
                BackColor = UiKit.FieldBg,
                ForeColor = UiKit.Text,
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
                else if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    _ = SubmitPinAsync();
                }
            };
            Controls.Add(_pin[i]);
        }

        _pair = UiKit.Primary("配对");
        _pair.Location = new Point(304, 360);
        _pair.Size = new Size(132, 52);
        _pair.Font = UiKit.YaHei(11F, FontStyle.Bold);
        _pair.Click += async (_, _) => await SubmitPinAsync();
        Controls.Add(_pair);

        _pairStatus = new Label
        {
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9F),
            Location = new Point(0, 424),
            Size = new Size(736, 22),
            AutoEllipsis = true,
        };
        Controls.Add(_pairStatus);

        var webPair = UiKit.Ghost("打开 WebUI 配对页");
        webPair.Location = new Point(0, 456);
        webPair.Size = new Size(152, 34);
        webPair.Click += (_, _) => OpenWeb("/pin#PIN");
        Controls.Add(webPair);

        var webConfig = UiKit.Ghost("打开 WebUI 配置");
        webConfig.Location = new Point(164, 456);
        webConfig.Size = new Size(144, 34);
        webConfig.Click += (_, _) => OpenWeb("/config");
        Controls.Add(webConfig);

        _toast = new Label
        {
            ForeColor = UiKit.Accent,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9F),
            Location = new Point(0, 504),
            AutoSize = true,
        };
        Controls.Add(_toast);

        _toastTimer = new System.Windows.Forms.Timer { Interval = 1800 };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Text = ""; };

        UpdateSession(session);
        if (!_cfg.HasCreds)
        {
            _pairStatus.ForeColor = UiKit.Warn;
            _pairStatus.Text = "尚未配置 WebUI 账号密码，请先到「设置」填写后才能在本机配对。";
        }
        _ = RefreshWanAsync(false);
    }

    private Label AddCard(int x, string title, string hint, out RoundCard card)
    {
        card = new RoundCard { Location = new Point(x, 100), Size = new Size(232, 104) };
        Controls.Add(card);
        card.Controls.Add(new Label
        {
            Text = title,
            ForeColor = UiKit.Text,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9.5F, FontStyle.Bold),
            Location = new Point(16, 12),
            AutoSize = true,
        });
        card.Controls.Add(new Label
        {
            Text = hint,
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(8F),
            Location = new Point(16, 34),
            Size = new Size(200, 16),
            AutoEllipsis = true,
        });
        var value = new Label
        {
            ForeColor = UiKit.Text,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(12F, FontStyle.Bold),
            Location = new Point(16, 52),
            Size = new Size(144, 26),
            AutoEllipsis = true,
        };
        card.Controls.Add(value);
        return value;
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

    public void UpdateSession(Paths.SessionInfo session)
    {
        _s = session;
        var local = NetInfo.LocalIp();
        _localIp.Text = local;
        _port.Text = session.StreamPort > 0 ? session.StreamPort.ToString() : "未知";
        _addr.Text = session.StreamPort > 0 ? $"{local}:{session.StreamPort}" : local;
        _sub.Text = (_isConsole ? "物理屏与虚拟屏串流 · Console SYSTEM 会话" : "RDP 用户桌面 · 游戏会话")
                    + (session.StreamPort > 0 ? "" : "（未读取到 conf 端口）");
    }

    public void SetStatus(bool? up)
    {
        if (up == true) _pill.Set("运行中", UiKit.Accent);
        else if (up == false) _pill.Set("未运行", UiKit.Danger);
        else _pill.Set("检测中", UiKit.Muted);
    }

    public void FocusPin() => _pin[0].Focus();

    private async Task RefreshWanAsync(bool force)
    {
        if (_wanBusy) return;
        _wanBusy = true;
        _wanBtn.Enabled = false;
        if (_wanIp.Text.Length == 0 || force) _wanIp.Text = "获取中…";
        try
        {
            var (ip, err) = await NetInfo.WanIpAsync(force);
            if (ip.Length > 0)
            {
                _wanIp.Text = ip;
                _wanBtn.Text = "刷新";
                _tip.SetToolTip(_wanIp, err.Length > 0 ? "上次刷新失败：" + err : "");
            }
            else
            {
                _wanIp.Text = "获取失败";
                _wanBtn.Text = "重试";
                _tip.SetToolTip(_wanIp, err);
            }
        }
        finally
        {
            _wanBtn.Enabled = true;
            _wanBusy = false;
        }
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "获取中…" || text == "获取失败") return;
        try
        {
            Clipboard.SetText(text);
            _toast.Text = "已复制：" + text;
            _toastTimer.Stop();
            _toastTimer.Start();
        }
        catch (Exception e)
        {
            _toast.ForeColor = UiKit.Danger;
            _toast.Text = "复制失败：" + e.Message;
            _toastTimer.Stop();
            _toastTimer.Start();
            _toast.ForeColor = UiKit.Accent;
        }
    }

    private async Task SubmitPinAsync()
    {
        if (_busy || _s.WebPort <= 0) return;
        var pin = string.Concat(_pin.Select(t => t.Text.Trim()));
        if (pin.Length != 4 || !pin.All(char.IsDigit))
        {
            SetPairStatus("请输入设备上显示的 4 位配对码。", UiKit.Danger);
            return;
        }
        SetBusy(true);
        SetPairStatus("正在提交…", UiKit.Muted);
        try
        {
            using var client = new ApolloClient(_s.WebPort, _cfg.WebUser, _cfg.WebPass);
            var (before, listErr) = await client.ListClients();
            var err = await client.SendPin(pin);
            if (!string.IsNullOrEmpty(err))
            {
                SetPairStatus("配对失败：" + err, UiKit.Danger);
                return;
            }
            var grantErr = listErr;
            if (grantErr.Length == 0)
            {
                var (after, lerr) = await client.ListClients();
                grantErr = lerr;
                if (grantErr.Length == 0)
                {
                    var old = before.Select(d => d.Uuid).ToHashSet();
                    foreach (var d in after.Where(d => !old.Contains(d.Uuid)))
                    {
                        var gerr = await client.GrantAllPermissions(d);
                        if (!string.IsNullOrEmpty(gerr)) grantErr = gerr;
                    }
                }
            }
            if (grantErr.Length > 0)
            {
                SetPairStatus("配对成功，但自动授权失败：" + grantErr + "。请到 WebUI 设备管理手动授权。", UiKit.Warn);
            }
            else
            {
                SetPairStatus("配对成功，已授予全部权限。请在 Moonlight 上确认。", UiKit.Accent);
                foreach (var t in _pin) t.Clear();
                _pin[0].Focus();
            }
        }
        catch (Exception e)
        {
            SetPairStatus("连接失败：" + e.Message, UiKit.Danger);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetPairStatus(string text, Color color)
    {
        _pairStatus.ForeColor = color;
        _pairStatus.Text = text;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _pair.Enabled = !busy;
        _pair.BackColor = busy ? Color.FromArgb(28, 84, 52) : UiKit.AccentDeep;
        _pair.Text = busy ? "提交中…" : "配对";
    }

    private void OpenWeb(string path)
    {
        if (_s.WebPort <= 0) return;
        try
        {
            using var f = new WebViewForm(_cfg, $"{_s.Name}配置", _s.WebPort, path);
            var owner = FindForm();
            if (owner != null) f.ShowDialog(owner);
            else f.ShowDialog();
        }
        catch (Exception e)
        {
            SetPairStatus("无法打开网页窗口：" + e.Message, UiKit.Danger);
        }
    }
}