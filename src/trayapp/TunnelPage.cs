namespace SunshineDuoTray;

// 内网穿透页：EasyTier 组网开关、网络名称与密钥；
// 组网核心（easytier-core）由核心服务托管，本页只经控制管道下发开关与查询状态
internal sealed class TunnelPage : Panel
{
    private readonly Label _state, _status;
    private readonly SwitchRow _enable;
    private readonly TextBox _network, _secret;
    private readonly Button _apply;
    private bool _busy, _suppress;

    public TunnelPage()
    {
        BackColor = UiKit.WindowBg;
        Font = UiKit.YaHei(9F);
        AutoScroll = true;

        var title = UiKit.Title("内网穿透");
        title.Location = new Point(0, 24);
        Controls.Add(title);

        var sub = UiKit.Sub("EasyTier 组网 · 组网核心由后台服务托管");
        sub.Location = new Point(0, 62);
        Controls.Add(sub);

        _state = new Label
        {
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9.5F),
            Location = new Point(0, 96),
            Size = new Size(736, 20),
            AutoEllipsis = true,
        };
        Controls.Add(_state);

        _enable = new SwitchRow("启用组网", false)
        {
            Location = new Point(0, 132),
            Size = new Size(360, 30),
        };
        _enable.CheckedChanged += async (_, _) => await ApplyAsync();
        Controls.Add(_enable);

        Controls.Add(new Label
        {
            Text = "网络名称",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9F),
            Location = new Point(0, 174),
            AutoSize = true,
        });
        _network = UiKit.Field();
        _network.Location = new Point(0, 198);
        _network.Width = 360;
        Controls.Add(_network);

        Controls.Add(new Label
        {
            Text = "网络密钥",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9F),
            Location = new Point(0, 238),
            AutoSize = true,
        });
        _secret = UiKit.Field(password: true);
        _secret.Location = new Point(0, 262);
        _secret.Width = 360;
        Controls.Add(_secret);

        _apply = UiKit.Primary("保存并应用");
        _apply.Location = new Point(0, 306);
        _apply.Size = new Size(360, 34);
        _apply.Click += async (_, _) => await ApplyAsync();
        Controls.Add(_apply);

        _status = new Label
        {
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9F),
            Location = new Point(0, 352),
            Size = new Size(736, 20),
            AutoEllipsis = true,
        };
        Controls.Add(_status);

        Controls.Add(new Label
        {
            Text = "对端设备安装 EasyTier 并加入相同网络后，即可经虚拟 IP 互访。",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(8.5F),
            Location = new Point(0, 396),
            Size = new Size(736, 40),
        });

        var note = UiKit.Caption("启用后由核心服务自动拉起并保活；输出与错误见 bin\\SunshineDuoCore\\easytier.out 与 watch.log。");
        note.Location = new Point(0, 440);
        Controls.Add(note);

        _ = RefreshStatus();
    }

    public void Reload() => _ = RefreshStatus();

    public async Task RefreshStatus()
    {
        if (_busy) return;
        var st = await GuardPipe.EasyTierStatus();
        if (st == null)
        {
            _state.ForeColor = UiKit.Warn;
            _state.Text = "核心服务不可达，组网状态未知。";
            return;
        }
        ApplyState(st);
    }

    private void ApplyState(GuardPipe.EasyTierState st)
    {
        _suppress = true;
        try
        {
            _enable.Checked = st.Enabled;
            if (_network.Text.Trim().Length == 0) _network.Text = st.Network;
        }
        finally
        {
            _suppress = false;
        }
        if (!st.ExeExists)
        {
            _state.ForeColor = UiKit.Danger;
            _state.Text = "未找到组网核心：" + st.Exe;
        }
        else if (st.Running)
        {
            _state.ForeColor = UiKit.Accent;
            _state.Text = "运行中 · PID " + st.Pid + (st.Managed ? "" : "（服务收养的既有实例）");
        }
        else
        {
            _state.ForeColor = UiKit.Muted;
            _state.Text = "已停止";
        }
        _network.PlaceholderText = st.Network.Length > 0 ? "" : "例如 moeart-home";
    }

    private async Task ApplyAsync()
    {
        if (_busy || _suppress) return;
        var want = _enable.Checked;
        if (want && _network.Text.Trim().Length == 0)
        {
            _suppress = true;
            try { _enable.Checked = false; } finally { _suppress = false; }
            _status.ForeColor = UiKit.Warn;
            _status.Text = "启用前先填写网络名称。";
            return;
        }
        _busy = true;
        _apply.Enabled = _enable.Enabled = false;
        _status.ForeColor = UiKit.Muted;
        _status.Text = "正在应用…";
        try
        {
            var (st, err) = await GuardPipe.EasyTierSet(want, _network.Text.Trim(), _secret.Text);
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
                    : want ? (st.Running ? "组网已启动。" : "已启用，等待组网核心就绪。") : "组网已停止。";
            }
        }
        finally
        {
            _busy = false;
            _apply.Enabled = _enable.Enabled = true;
        }
    }
}
