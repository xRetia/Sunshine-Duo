using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SunshineDuoTray;

public class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly AppConfig _cfg;
    private readonly ToolStripMenuItem _mConsoleStatus;
    private readonly ToolStripMenuItem _mGameStatus;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _polling;
    private bool _consoleUp;
    private bool _gameUp;

    public TrayContext()
    {
        _cfg = AppConfig.Load();

        _mConsoleStatus = new ToolStripMenuItem("Console Apollo：检测中…") { Enabled = false };
        _mGameStatus = new ToolStripMenuItem("Game Apollo：检测中…") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_mConsoleStatus);
        menu.Items.Add(_mGameStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(M("打开 Console WebUI", () => OpenWeb(true)));
        menu.Items.Add(M("打开 Game WebUI", () => OpenWeb(false)));
        menu.Items.Add(M("设备配对管理…", ShowPairForm));
        menu.Items.Add(M("修改 WebUI 账号密码…", ShowPasswordForm));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(M("重启 Console Apollo", () => RestartApollo(true)));
        menu.Items.Add(M("重启 Game Apollo", () => RestartApollo(false)));
        menu.Items.Add(M("重启 wsguard 服务", ToggleService));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(M("设置…", ShowSettings));
        menu.Items.Add(M("退出", Exit));

        _tray = new NotifyIcon
        {
            Icon = LoadIcon("apollo-locked.ico"),
            Text = "Sunshine-Duo 托盘",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenWeb(true);

        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    private static ToolStripMenuItem M(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    private static Icon LoadIcon(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return s != null ? new Icon(s) : SystemIcons.Application;
    }

    private ApolloClient Client(bool console)
    {
        var port = (console ? _cfg.ConsolePort : _cfg.GamePort) + 1;
        return new ApolloClient(port, _cfg.WebUser, _cfg.WebPass);
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            using var c = Client(true);
            using var g = Client(false);
            _consoleUp = await c.Probe();
            _gameUp = await g.Probe();
            _mConsoleStatus.Text = $"Console Apollo：{(_consoleUp ? "运行中" : "未运行")}（HTTPS {_cfg.ConsolePort + 1}）";
            _mGameStatus.Text = $"Game Apollo：{(_gameUp ? "运行中" : "未运行")}（HTTPS {_cfg.GamePort + 1}）";
            var oldIcon = _tray.Icon;
            _tray.Icon = LoadIcon(_consoleUp && _gameUp ? "apollo-playing.ico"
                : _consoleUp || _gameUp ? "apollo-pausing.ico" : "apollo-locked.ico");
            oldIcon?.Dispose();
            _tray.Text = $"Sunshine-Duo 托盘\nConsole: {(_consoleUp ? "在线" : "离线")}  Game: {(_gameUp ? "在线" : "离线")}";
        }
        catch { }
        finally
        {
            _polling = false;
        }
    }

    private void OpenWeb(bool console)
    {
        var port = (console ? _cfg.ConsolePort : _cfg.GamePort) + 1;
        try
        {
            Process.Start(new ProcessStartInfo($"https://127.0.0.1:{port}/") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            MessageBox.Show("无法打开浏览器: " + e.Message, "Sunshine-Duo 托盘");
        }
    }

    private void RestartApollo(bool console)
    {
        var name = console ? "Console" : "Game";
        var target = console ? "console" : "game";
        if (MessageBox.Show($"确定重启 {name} Apollo？（会断开当前该会话的串流）",
                "重启 Apollo", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        try
        {
            var err = GuardPipe.RestartApollo(target).GetAwaiter().GetResult();
            MessageBox.Show(err == null ? $"{name} Apollo 重启指令已执行。" : "重启失败: " + err,
                "重启 Apollo", MessageBoxButtons.OK, err == null ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        catch (Exception e)
        {
            MessageBox.Show("重启失败: " + e.Message, "重启 Apollo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        _ = PollAsync();
    }

    private void ToggleService()
    {
        var state = ServiceControl.Query();
        bool running = state == "运行中" || state == "启动中";
        if (MessageBox.Show($"wsguard 服务当前：{state}。确定{(running ? "停止" : "启动")}？",
                "wsguard 服务", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        var err = ServiceControl.SetRunning(!running);
        MessageBox.Show(err ?? "指令已执行。", "wsguard 服务",
            MessageBoxButtons.OK, err == null ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        _ = PollAsync();
    }

    private void ShowPairForm()
    {
        using var f = new PairForm(_cfg);
        f.ShowDialog();
        _ = PollAsync();
    }

    private void ShowPasswordForm()
    {
        using var f = new PasswordForm(_cfg);
        if (f.ShowDialog() == DialogResult.OK)
            _ = PollAsync();
    }

    private void ShowSettings()
    {
        using var f = new SettingsForm(_cfg);
        f.ShowDialog();
        _ = PollAsync();
    }

    private void Exit()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }
}
