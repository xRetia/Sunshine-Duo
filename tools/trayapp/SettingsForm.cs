using Microsoft.Win32;

namespace SunshineDuoTray;

public class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly TextBox _user, _pass, _consolePort, _gamePort;
    private readonly CheckBox _autoStart;
    private readonly Button _ok, _cancel;

    public SettingsForm(AppConfig cfg)
    {
        _cfg = cfg;
        Text = "设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 240);
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
        _pass = new TextBox { Text = cfg.WebPass };
        Add("WebUI 用户名：", _user, 20);
        Add("WebUI 密码：", _pass, 55, true);

        _consolePort = new TextBox { Text = cfg.ConsolePort.ToString() };
        _gamePort = new TextBox { Text = cfg.GamePort.ToString() };
        Add("Console 串流端口：", _consolePort, 95);
        Add("Game 串流端口：", _gamePort, 130);

        _autoStart = new CheckBox
        {
            Text = "开机自启（当前用户登录时）",
            Location = new Point(16, 170),
            AutoSize = true,
            Checked = cfg.AutoStart,
        };
        Controls.Add(_autoStart);

        _ok = new Button { Text = "保存", Location = new Point(230, 200), Width = 80 };
        _ok.Click += (_, _) => Save();
        _cancel = new Button { Text = "取消", Location = new Point(320, 200), Width = 80, DialogResult = DialogResult.Cancel };
        Controls.Add(_ok);
        Controls.Add(_cancel);
        CancelButton = _cancel;
        AcceptButton = _ok;
    }

    private void Save()
    {
        if (!int.TryParse(_consolePort.Text.Trim(), out var cp) || cp is < 1024 or > 65535 ||
            !int.TryParse(_gamePort.Text.Trim(), out var gp) || gp is < 1024 or > 65535)
        {
            MessageBox.Show("端口必须是 1024-65535 之间的数字，且两个端口差 ≥ 20。", "设置");
            return;
        }
        if (Math.Abs(cp - gp) < 20)
        {
            MessageBox.Show("两个端口差需 ≥ 20（RTSP/UDP 端口族不能重叠）。", "设置");
            return;
        }
        _cfg.ConsolePort = cp;
        _cfg.GamePort = gp;
        _cfg.WebUser = _user.Text.Trim();
        _cfg.SetPass(_pass.Text);
        _cfg.AutoStart = _autoStart.Checked;
        _cfg.Save();
        SetAutoStart(_autoStart.Checked);
        DialogResult = DialogResult.OK;
    }

    public static void SetAutoStart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (on)
                key.SetValue("SunshineDuoTray", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("SunshineDuoTray", false);
        }
        catch { }
    }
}
