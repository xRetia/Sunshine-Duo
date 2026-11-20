namespace SunshineDuoTray;

// 修改 WebUI 账号密码：不走 Apollo API，直接 sunshine.exe --creds 离线写入
// 两个 conf 的 state，然后经 wsguard 管道重启两个 Apollo 使其生效。
public class PasswordForm : Form
{
    private readonly AppConfig _cfg;
    private readonly TextBox _user, _pass, _confirm;
    private readonly Button _ok, _cancel;

    public PasswordForm(AppConfig cfg)
    {
        _cfg = cfg;
        Text = "修改 WebUI 账号密码";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 190);
        Font = new Font("Microsoft YaHei UI", 9F);

        void Add(string label, TextBox box, int y, bool isPass = false)
        {
            Controls.Add(new Label { Text = label, Location = new Point(16, y + 3), AutoSize = true });
            box.Location = new Point(150, y);
            box.Width = 240;
            if (isPass) box.UseSystemPasswordChar = true;
            Controls.Add(box);
        }

        _user = new TextBox { Text = cfg.WebUser };
        _pass = new TextBox();
        _confirm = new TextBox();
        Add("新用户名：", _user, 20);
        Add("新密码：", _pass, 60, true);
        Add("确认新密码：", _confirm, 100, true);

        _ok = new Button { Text = "应用（写入并重启 Apollo）", Location = new Point(90, 145), Width = 220 };
        _ok.Click += async (_, _) => await Apply();
        _cancel = new Button { Text = "取消", Location = new Point(330, 145), Width = 80, DialogResult = DialogResult.Cancel };
        Controls.Add(_ok);
        Controls.Add(_cancel);
        CancelButton = _cancel;
        AcceptButton = _ok;
    }

    private async Task Apply()
    {
        var user = _user.Text.Trim();
        var pass = _pass.Text;
        if (user.Length == 0 || pass.Length < 4)
        {
            MessageBox.Show("用户名不能为空，密码至少 4 位。", "修改 WebUI 账号密码");
            return;
        }
        if (pass != _confirm.Text)
        {
            MessageBox.Show("两次输入的密码不一致。", "修改 WebUI 账号密码");
            return;
        }
        SetBusy(true);
        try
        {
            var err = await Task.Run(() => CredentialReset.Apply(user, pass));
            if (err != null)
            {
                MessageBox.Show("写入凭证失败: " + err, "修改 WebUI 账号密码", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            // 保存到托盘自身配置，配对管理等 API 功能继续可用
            _cfg.WebUser = user;
            _cfg.SetPass(pass);
            _cfg.Save();

            // 重启两个 Apollo 让新凭证生效（走 wsguard 管道，不走 API）
            var errC = await GuardPipe.RestartApollo("console");
            var errG = await GuardPipe.RestartApollo("game");
            var msg = "凭证已写入，Apollo 正在重启以应用新密码。";
            if (errC != null || errG != null)
                msg += $"\n（重启通知失败：Console {errC ?? "ok"} / Game {errG ?? "ok"}；请稍后手动重启 Apollo）";
            MessageBox.Show(msg, "修改 WebUI 账号密码", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _ok.Enabled = _cancel.Enabled = !busy;
    }
}
