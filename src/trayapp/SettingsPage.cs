namespace SunshineDuoTray;

// 设置页：会话端口 / 启动 / WebUI 账号 / 键盘鼠标 / 服务维护，分两列归类
internal sealed class SettingsPage : Panel
{
    private readonly AppConfig _cfg;
    private readonly Action _onChanged;
    private readonly Action<string> _onThemeChanged;
    private readonly TextBox _consolePort, _gamePort;
    private readonly TextBox _user, _pass, _confirm;
    private readonly SwitchRow _autoStart, _winRalt, _winlRalt, _hideCursor;
    private readonly Label _portStatus, _credStatus, _hkStatus, _svcState, _svcStatus;
    private readonly Button _svcBtn;
    private readonly Dictionary<string, Button> _themeBtns = new();
    private bool _init;

    public SettingsPage(AppConfig cfg, Action onChanged, Action<string> onThemeChanged)
    {
        _cfg = cfg;
        _onChanged = onChanged;
        _onThemeChanged = onThemeChanged;
        BackColor = UiKit.WindowBg;
        Font = UiKit.YaHei(9F);
        AutoScroll = true;

        var title = UiKit.Title("设置");
        title.Location = new Point(0, 24);
        Controls.Add(title);
        var sub = UiKit.Sub("会话端口、启动项、账号与维护工具集中在这里。");
        sub.Location = new Point(0, 62);
        Controls.Add(sub);

        Controls.Add(new Label
        {
            Text = "主题",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9F),
            Location = new Point(372, 31),
            AutoSize = true,
        });
        _themeBtns["system"] = MakeThemeButton("跟随系统", "system", 410);
        _themeBtns["light"] = MakeThemeButton("浅色", "light", 492);
        _themeBtns["dark"] = MakeThemeButton("深色", "dark", 574);
        UpdateThemeButtons();

        // 左列
        var ports = Card(0, 100, 360, 214, "会话端口",
            "conf 里的 port，WebUI 用端口 +1；保存后重启会话。");
        _consolePort = UiKit.Field();
        _consolePort.Font = UiKit.Mono(10.5F);
        _gamePort = UiKit.Field();
        _gamePort.Font = UiKit.Mono(10.5F);
        var consoleLabel = AddLabeled(ports, "主会话串流端口", _consolePort, 16, 74, 150);
        var gameLabel = AddLabeled(ports, "游戏会话串流端口", _gamePort, 194, 74, 150);
        if (!Paths.ConsoleEnabled)
        {
            consoleLabel.Visible = false;
            _consolePort.Visible = false;
        }
        if (!Paths.GameEnabled)
        {
            gameLabel.Visible = false;
            _gamePort.Visible = false;
            _consolePort.Location = new Point(16, 94);
        }
        var savePorts = UiKit.Ghost("保存端口");
        savePorts.Location = new Point(16, 140);
        savePorts.Size = new Size(110, 32);
        savePorts.Click += async (_, _) => await SavePortsAsync();
        ports.Controls.Add(savePorts);
        _portStatus = CardStatus(ports, 178);

        var start = Card(0, 330, 360, 92, "启动", "");
        _autoStart = new SwitchRow("登录 Windows 时自动启动托盘", SettingsForm.IsAutoStartEnabled())
        {
            Location = new Point(16, 50),
        };
        _autoStart.CheckedChanged += (_, _) => SaveAutoStart();
        start.Controls.Add(_autoStart);

        var keys = Card(0, 438, 360, 222, "键盘与鼠标",
            "仅作用于主会话桌面；勾选即生效，无需重启。");
        _winRalt = new SwitchRow("R-Alt 替代 Win", Paths.CoreFlag("win_from_ralt", false))
        {
            Location = new Point(16, 62),
        };
        _winlRalt = new SwitchRow("R-Alt+L 替代 Win+L", Paths.CoreFlag("winl_from_ralt_l", true))
        {
            Location = new Point(16, 96),
        };
        _hideCursor = new SwitchRow("串流期间隐藏系统鼠标指针", Paths.CoreFlag("hide_cursor_on_stream", false))
        {
            Location = new Point(16, 130),
        };
        keys.Controls.Add(_winRalt);
        keys.Controls.Add(_winlRalt);
        keys.Controls.Add(_hideCursor);
        _hkStatus = CardStatus(keys, 168);

        // 右列
        var cred = Card(376, 100, 360, 300, "WebUI 账号密码",
            "写入两个 Apollo 的状态文件，并重启会话使其生效。");
        _user = UiKit.Field(cfg.WebUser);
        _pass = UiKit.Field("", true);
        _confirm = UiKit.Field("", true);
        AddLabeled(cred, "用户名", _user, 16, 72, 320);
        AddLabeled(cred, "新密码", _pass, 16, 128, 320);
        AddLabeled(cred, "确认新密码", _confirm, 16, 184, 320);
        var applyCred = UiKit.Primary("应用并重启会话");
        applyCred.Location = new Point(16, 242);
        applyCred.Size = new Size(180, 34);
        applyCred.Click += async (_, _) => await ApplyCredAsync();
        cred.Controls.Add(applyCred);
        _credStatus = CardStatus(cred, 282);

        var svc = Card(376, 416, 360, 244, "服务与维护", "");
        _svcState = new Label
        {
            Text = "SunshineDuoCore：检测中",
            ForeColor = UiKit.Text,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9.5F, FontStyle.Bold),
            Location = new Point(16, 44),
            Size = new Size(328, 20),
            AutoEllipsis = true,
        };
        svc.Controls.Add(_svcState);
        _svcBtn = UiKit.Ghost("启动服务");
        _svcBtn.Location = new Point(16, 74);
        _svcBtn.Size = new Size(140, 32);
        _svcBtn.Enabled = false;
        _svcBtn.Click += async (_, _) => await ToggleServiceAsync();
        svc.Controls.Add(_svcBtn);

        var restartConsole = UiKit.Ghost("重启主会话");
        restartConsole.Location = new Point(16, 118);
        restartConsole.Size = new Size(140, 32);
        restartConsole.Click += async (_, _) => await RestartSessionAsync("console", "主会话");
        restartConsole.Enabled = Paths.ConsoleEnabled;
        svc.Controls.Add(restartConsole);

        var restartGame = UiKit.Ghost("重启游戏会话");
        restartGame.Location = new Point(172, 118);
        restartGame.Size = new Size(140, 32);
        restartGame.Click += async (_, _) => await RestartSessionAsync("game", "游戏会话");
        restartGame.Enabled = Paths.GameEnabled;
        svc.Controls.Add(restartGame);

        var note = new Label
        {
            Text = "服务启停经 gsudo64 提权；重启会话由核心服务执行。",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(8.5F),
            Location = new Point(16, 160),
            Size = new Size(328, 34),
        };
        svc.Controls.Add(note);
        _svcStatus = CardStatus(svc, 202);

        LoadPorts();

        _init = true;
        _winRalt.CheckedChanged += async (_, _) => await SaveHotkeysAsync();
        _winlRalt.CheckedChanged += async (_, _) => await SaveHotkeysAsync();
        _hideCursor.CheckedChanged += async (_, _) => await SaveHideCursorAsync();
    }

    private Button MakeThemeButton(string text, string mode, int x)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, 24),
            Size = new Size(76, 30),
            FlatStyle = FlatStyle.Flat,
            Font = UiKit.YaHei(9F),
            Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => SelectTheme(mode);
        Controls.Add(b);
        return b;
    }

    private void SelectTheme(string mode)
    {
        if (string.Equals(_cfg.Theme, mode, StringComparison.Ordinal)) return;
        _cfg.Theme = mode;
        _cfg.Save();
        UpdateThemeButtons();
        _onThemeChanged(mode);
    }

    private void UpdateThemeButtons()
    {
        foreach (var kv in _themeBtns)
        {
            var selected = string.Equals(_cfg.Theme ?? "system", kv.Key, StringComparison.Ordinal);
            var b = kv.Value;
            b.BackColor = selected ? UiKit.AccentDeep : UiKit.Card;
            b.ForeColor = selected ? Color.White : UiKit.Text;
            b.FlatAppearance.BorderSize = selected ? 0 : 1;
            b.FlatAppearance.BorderColor = UiKit.Border;
            b.FlatAppearance.MouseOverBackColor = selected ? UiKit.Accent : UiKit.CardHover;
        }
    }

    private RoundCard Card(int x, int y, int w, int h, string title, string desc)
    {
        var card = new RoundCard { Location = new Point(x, y), Size = new Size(w, h) };
        Controls.Add(card);
        card.Controls.Add(new Label
        {
            Text = title,
            ForeColor = UiKit.Text,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(11F, FontStyle.Bold),
            Location = new Point(16, 14),
            AutoSize = true,
        });
        if (desc.Length > 0)
            card.Controls.Add(new Label
            {
                Text = desc,
                ForeColor = UiKit.Muted,
                BackColor = Color.Transparent,
                Font = UiKit.YaHei(8.5F),
                Location = new Point(16, 38),
                Size = new Size(w - 32, 34),
            });
        return card;
    }

    private static Label CardStatus(Control card, int y)
    {
        var label = new Label
        {
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(8.5F),
            Location = new Point(16, y),
            Size = new Size(card.Width - 32, 18),
            AutoEllipsis = true,
        };
        card.Controls.Add(label);
        return label;
    }

    private static Label AddLabeled(Control card, string label, TextBox box, int x, int y, int width)
    {
        var l = new Label
        {
            Text = label,
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(8.5F),
            Location = new Point(x, y),
            AutoSize = true,
        };
        card.Controls.Add(l);
        box.Location = new Point(x, y + 20);
        box.Width = width;
        card.Controls.Add(box);
        return l;
    }

    private void LoadPorts()
    {
        var sessions = Paths.GetSessions(_cfg);
        _consolePort.Text = (sessions.FirstOrDefault(s => s.Id == "console")?.StreamPort ?? _cfg.ConsolePort).ToString();
        _gamePort.Text = (sessions.FirstOrDefault(s => s.Id == "game")?.StreamPort ?? _cfg.GamePort).ToString();
    }

    private void SetStatus(Label label, string text, Color color)
    {
        label.ForeColor = color;
        label.Text = text;
    }

    private async Task SavePortsAsync()
    {
        int cpv = 0, gpv = 0;
        var cpOk = int.TryParse(_consolePort.Text.Trim(), out cpv) && cpv is >= 1024 and <= 65535;
        var gpOk = int.TryParse(_gamePort.Text.Trim(), out gpv) && gpv is >= 1024 and <= 65535;
        if (Paths.ConsoleEnabled && !cpOk)
        {
            SetStatus(_portStatus, "主会话端口必须是 1024-65535 的数字。", UiKit.Danger);
            return;
        }
        if (Paths.GameEnabled && !gpOk)
        {
            SetStatus(_portStatus, "游戏会话端口必须是 1024-65535 的数字。", UiKit.Danger);
            return;
        }
        if (Paths.ConsoleEnabled && Paths.GameEnabled && Math.Abs(cpv - gpv) < 50)
        {
            SetStatus(_portStatus, "两个端口差需 ≥ 50，避免端口族重叠。", UiKit.Danger);
            return;
        }

        var changed = new List<(string id, string name)>();
        var errors = new List<string>();
        if (Paths.ConsoleEnabled)
        {
            var s = Paths.GetSessions(_cfg).FirstOrDefault(x => x.Id == "console");
            if (s != null && s.StreamPort != cpv)
            {
                if (WriteConfPort(s.ConfPath, cpv)) changed.Add(("console", "主会话"));
                else errors.Add("写入主会话 conf 失败");
                _cfg.ConsolePort = cpv;
            }
        }
        if (Paths.GameEnabled)
        {
            var s = Paths.GetSessions(_cfg).FirstOrDefault(x => x.Id == "game");
            if (s != null && s.StreamPort != gpv)
            {
                if (WriteConfPort(s.ConfPath, gpv)) changed.Add(("game", "游戏会话"));
                else errors.Add("写入游戏会话 conf 失败");
                _cfg.GamePort = gpv;
            }
        }
        _cfg.Save();
        _onChanged();

        if (changed.Count == 0)
        {
            SetStatus(_portStatus,
                errors.Count > 0 ? string.Join("；", errors) : "端口未变化。",
                errors.Count > 0 ? UiKit.Danger : UiKit.Muted);
            return;
        }

        SetStatus(_portStatus, "端口已写入，正在重启 " + string.Join("、", changed.Select(c => c.name)) + "…", UiKit.Muted);
        foreach (var (id, name) in changed)
        {
            var err = await GuardPipe.RestartApollo(id);
            if (err != null) errors.Add($"{name}重启失败：{err}");
        }
        if (errors.Count > 0)
            SetStatus(_portStatus, string.Join("；", errors), UiKit.Danger);
        else
            SetStatus(_portStatus, "端口已保存，会话已重启。", UiKit.Accent);
    }

    private static bool WriteConfPort(string conf, int port)
    {
        try
        {
            if (!File.Exists(conf)) return false;
            var lines = File.ReadAllLines(conf).ToList();
            var found = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.Equals("port", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("port ", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("port=", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("port\t", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = "port = " + port;
                    found = true;
                    break;
                }
            }
            if (!found) lines.Add("port = " + port);
            File.WriteAllLines(conf, lines);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveAutoStart()
    {
        _cfg.AutoStart = _autoStart.Checked;
        _cfg.Save();
        SettingsForm.SetAutoStart(_autoStart.Checked);
    }

    private async Task ApplyCredAsync()
    {
        var user = _user.Text.Trim();
        var pass = _pass.Text;
        if (user.Length == 0 || pass.Length < 4)
        {
            SetStatus(_credStatus, "用户名不能为空，密码至少 4 位。", UiKit.Danger);
            return;
        }
        if (pass != _confirm.Text)
        {
            SetStatus(_credStatus, "两次输入的密码不一致。", UiKit.Danger);
            return;
        }
        SetStatus(_credStatus, "正在写入凭证…", UiKit.Muted);
        try
        {
            string? err;
            try
            {
                err = await Task.Run(() => CredentialReset.Apply(user, pass));
            }
            catch (Exception e)
            {
                err = e.Message;
            }
            if (err != null)
            {
                SetStatus(_credStatus, "写入失败：" + err, UiKit.Danger);
                return;
            }
            _cfg.WebUser = user;
            _cfg.SetPass(pass);
            _cfg.Save();
            _pass.Clear();
            _confirm.Clear();

            var failures = new List<string>();
            if (Paths.ConsoleEnabled)
            {
                var e = await GuardPipe.RestartApollo("console");
                if (e != null) failures.Add("主会话：" + e);
            }
            if (Paths.GameEnabled)
            {
                var e = await GuardPipe.RestartApollo("game");
                if (e != null) failures.Add("游戏会话：" + e);
            }
            SetStatus(_credStatus,
                failures.Count == 0 ? "凭证已应用，Apollo 正在重启。" : "凭证已写入，但重启失败：" + string.Join("；", failures),
                failures.Count == 0 ? UiKit.Accent : UiKit.Warn);
        }
        finally
        {
        }
    }

    private async Task SaveHotkeysAsync()
    {
        if (!_init) return;
        var err = await GuardPipe.SetHotkeys(_winRalt.Checked, _winlRalt.Checked);
        if (err == null)
        {
            SetStatus(_hkStatus, "快捷键设置已应用。", UiKit.Accent);
        }
        else
        {
            SetStatus(_hkStatus, "设置失败：" + err, UiKit.Danger);
            _init = false;
            _winRalt.Checked = Paths.CoreFlag("win_from_ralt", false);
            _winlRalt.Checked = Paths.CoreFlag("winl_from_ralt_l", true);
            _init = true;
        }
    }

    private async Task SaveHideCursorAsync()
    {
        if (!_init) return;
        var err = await GuardPipe.SetHideCursor(_hideCursor.Checked);
        if (err == null)
            SetStatus(_hkStatus, _hideCursor.Checked ? "连接后将隐藏系统鼠标指针。" : "已恢复显示系统鼠标指针。", UiKit.Accent);
        else
        {
            SetStatus(_hkStatus, "设置失败：" + err, UiKit.Danger);
            _init = false;
            _hideCursor.Checked = Paths.CoreFlag("hide_cursor_on_stream", false);
            _init = true;
        }
    }

    public void SetServiceState(string state)
    {
        var running = state is "运行中" or "启动中";
        _svcState.Text = "SunshineDuoCore：" + state;
        _svcState.ForeColor = state switch
        {
            "运行中" => UiKit.Accent,
            "启动中" or "停止中" => UiKit.Warn,
            _ => UiKit.Danger,
        };
        _svcBtn.Text = running ? "停止服务" : "启动服务";
        _svcBtn.Enabled = state != "未安装" && state != "查询失败";
    }

    private async Task ToggleServiceAsync()
    {
        var state = await Task.Run(ServiceControl.Query);
        var running = state is "运行中" or "启动中";
        SetStatus(_svcStatus, "正在请求提权（gsudo64）…", UiKit.Muted);
        _svcBtn.Enabled = false;
        try
        {
            var err = await ServiceControl.SetRunningAsync(!running);
            if (err != null)
            {
                SetStatus(_svcStatus, err, UiKit.Danger);
                return;
            }
            SetStatus(_svcStatus, $"{(running ? "停止" : "启动")}指令已执行。", UiKit.Accent);
        }
        finally
        {
            await Task.Delay(700);
            var now = await Task.Run(ServiceControl.Query);
            SetServiceState(now);
        }
    }

    private async Task RestartSessionAsync(string id, string name)
    {
        SetStatus(_svcStatus, $"正在重启{name}…", UiKit.Muted);
        var err = await GuardPipe.RestartApollo(id);
        SetStatus(_svcStatus, err == null ? $"{name}重启指令已执行。" : "重启失败：" + err,
            err == null ? UiKit.Accent : UiKit.Danger);
    }
}