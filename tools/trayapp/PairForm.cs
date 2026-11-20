namespace SunshineDuoTray;

public class PairForm : Form
{
    private readonly AppConfig _cfg;
    private readonly ComboBox _target;
    private readonly TextBox _pin;
    private readonly ListView _list;
    private readonly Button _submit, _unpair, _unpairAll, _refresh, _close;

    public PairForm(AppConfig cfg)
    {
        _cfg = cfg;
        Text = "设备配对管理";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 400);
        Font = new Font("Microsoft YaHei UI", 9F);

        var lblTarget = new Label { Text = "目标实例：", Location = new Point(12, 15), AutoSize = true };
        _target = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(85, 12),
            Width = 160,
        };
        _target.Items.Add($"Console Apollo（HTTPS {cfg.ConsolePort + 1}）");
        _target.Items.Add($"Game Apollo（HTTPS {cfg.GamePort + 1}）");
        _target.SelectedIndex = 0;
        _target.SelectedIndexChanged += (_, _) => _ = RefreshList();

        var lblPin = new Label { Text = "配对码：", Location = new Point(262, 15), AutoSize = true };
        _pin = new TextBox { Location = new Point(318, 12), Width = 70, MaxLength = 4, CharacterCasing = CharacterCasing.Upper };
        _submit = new Button { Text = "提交配对码", Location = new Point(396, 10), Width = 100 };
        _submit.Click += async (_, _) => await SubmitPin();

        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            Location = new Point(12, 45),
            Size = new Size(536, 290),
            HideSelection = false,
        };
        _list.Columns.Add("设备名称", 170);
        _list.Columns.Add("UUID", 250);
        _list.Columns.Add("已连接", 80);

        _refresh = new Button { Text = "刷新", Location = new Point(12, 350), Width = 80 };
        _refresh.Click += async (_, _) => await RefreshList();
        _unpair = new Button { Text = "解除配对（选中）", Location = new Point(100, 350), Width = 140 };
        _unpair.Click += async (_, _) => await UnpairSelected();
        _unpairAll = new Button { Text = "解除全部", Location = new Point(248, 350), Width = 90 };
        _unpairAll.Click += async (_, _) => await UnpairAll();
        _close = new Button { Text = "关闭", Location = new Point(468, 350), Width = 80, DialogResult = DialogResult.Cancel };

        Controls.AddRange(new Control[] { lblTarget, _target, lblPin, _pin, _submit, _list, _refresh, _unpair, _unpairAll, _close });
        CancelButton = _close;
        Shown += (_, _) => _ = RefreshList();
    }

    private ApolloClient Client()
    {
        var console = _target.SelectedIndex == 0;
        var port = (console ? _cfg.ConsolePort : _cfg.GamePort) + 1;
        return new ApolloClient(port, _cfg.WebUser, _cfg.WebPass);
    }

    private async Task RefreshList()
    {
        if (!_cfg.HasCreds)
        {
            MessageBox.Show("尚未配置 WebUI 账号，请先在「设置」里填写。", "设备配对管理");
            return;
        }
        SetBusy(true);
        try
        {
            using var client = Client();
            var (devices, err) = await client.ListClients();
            if (err != null && err.Length > 0)
            {
                MessageBox.Show("获取设备列表失败: " + err, "设备配对管理", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _list.Items.Clear();
            foreach (var d in devices)
            {
                var it = new ListViewItem(d.Name);
                it.SubItems.Add(d.Uuid);
                it.SubItems.Add(d.Connected ? "是" : "否");
                it.Tag = d.Uuid;
                _list.Items.Add(it);
            }
            if (devices.Count == 0)
                _list.Items.Add(new ListViewItem("（暂无已配对设备）") { ForeColor = Color.Gray });
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SubmitPin()
    {
        var pin = _pin.Text.Trim();
        if (pin.Length != 4 || !pin.All(char.IsDigit))
        {
            MessageBox.Show("请输入 Moonlight 上显示的 4 位数字配对码。", "设备配对管理");
            return;
        }
        SetBusy(true);
        try
        {
            using var client = Client();
            var err = await client.SendPin(pin);
            if (err != null && err.Length > 0)
                MessageBox.Show("配对失败: " + err, "设备配对管理", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
            {
                _pin.Clear();
                await RefreshList();
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UnpairSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not string uuid)
        {
            MessageBox.Show("请先选择一个已配对设备。", "设备配对管理");
            return;
        }
        SetBusy(true);
        try
        {
            using var client = Client();
            var err = await client.Unpair(uuid);
            if (err != null && err.Length > 0)
                MessageBox.Show("解除配对失败: " + err, "设备配对管理", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                await RefreshList();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UnpairAll()
    {
        if (_list.Items.Count == 0 || MessageBox.Show("确定解除该实例上全部已配对设备？",
                "设备配对管理", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;
        SetBusy(true);
        try
        {
            using var client = Client();
            foreach (ListViewItem it in _list.Items)
            {
                if (it.Tag is string uuid)
                    await client.Unpair(uuid);
            }
            await RefreshList();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _submit.Enabled = _unpair.Enabled = _unpairAll.Enabled = _refresh.Enabled = !busy;
    }
}
