using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace SunshineDuoTray;

public class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly AppConfig _cfg;
    private readonly ToolStripMenuItem _mConsoleStatus;
    private readonly ToolStripMenuItem _mGameStatus;
    private readonly ToolStripMenuItem _mMirror, _mOnly, _mExtend;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _polling;
    private bool _consoleUp;
    private bool _gameUp;

    public TrayContext()
    {
        _cfg = AppConfig.Load();

        _mConsoleStatus = new ToolStripMenuItem($"主会话 [检测中] [https://*:{_cfg.ConsolePort + 1}]") { Enabled = false };
        _mGameStatus = new ToolStripMenuItem($"游戏会话 [检测中] [https://*:{_cfg.GamePort + 1}]") { Enabled = false };

        _mMirror = new ToolStripMenuItem("镜像 · 串流物理主屏") { Tag = "mirror", CheckOnClick = true };
        _mOnly = new ToolStripMenuItem("独占 · 仅虚拟显示器") { Tag = "only", CheckOnClick = true };
        _mExtend = new ToolStripMenuItem("扩展 · 物理屏 + 虚拟屏") { Tag = "extend", CheckOnClick = true };
        foreach (var it in new[] { _mMirror, _mOnly, _mExtend })
            it.Click += async (_, _) => await SetDisplayMode((string)it.Tag!);

        var displayMenu = new ToolStripMenuItem("显示模式");
        displayMenu.DropDownItems.Add(_mMirror);
        displayMenu.DropDownItems.Add(_mOnly);
        displayMenu.DropDownItems.Add(_mExtend);
        UpdateDisplayCheckmarks();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_mConsoleStatus);
        menu.Items.Add(_mGameStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(M("打开主会话配置", () => OpenWeb(true)));
        menu.Items.Add(M("打开游戏会话配置", () => OpenWeb(false)));
        menu.Items.Add(displayMenu);
        menu.Items.Add(M("虚拟屏分辨率…", ShowResForm));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(M("设备配对…", () => ShowPairForm()));
        menu.Items.Add(M("修改 WebUI 账号密码…", ShowPasswordForm));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(M("重启主会话", () => RestartApollo(true)));
        menu.Items.Add(M("重启游戏会话", () => RestartApollo(false)));
        menu.Items.Add(M("重启 Sunshine Duo 核心服务", ToggleService));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(M("设置…", ShowSettings));
        menu.Items.Add(M("关于 Sunshine Duo…", ShowAbout));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(M("退出", Exit));

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.Get(TrayState.Offline),
            Text = "Sunshine Duo 托盘",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenWeb(true);

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
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
            _mConsoleStatus.Text = $"主会话 [{( _consoleUp ? "运行中" : "未运行" )}] [https://*:{_cfg.ConsolePort + 1}]";
            _mGameStatus.Text = $"游戏会话 [{( _gameUp ? "运行中" : "未运行" )}] [https://*:{_cfg.GamePort + 1}]";
            var state = _consoleUp && _gameUp ? TrayState.Online
                : _consoleUp || _gameUp ? TrayState.Partial : TrayState.Offline;
            _tray.Icon = TrayIcons.Get(state);
            _tray.Text = $"Sunshine Duo 托盘\n主会话 {(_consoleUp ? "运行中" : "未运行")}\n游戏会话 {(_gameUp ? "运行中" : "未运行")}";
            UpdateDisplayCheckmarks();
        }
        catch { }
        finally
        {
            _polling = false;
        }
    }

    private async void OpenWeb(bool console)
    {
        var port = (console ? _cfg.ConsolePort : _cfg.GamePort) + 1;
        var title = console ? "主会话配置" : "游戏会话配置";
        try
        {
            using var f = new WebViewForm(_cfg, title, port, "/config");
            f.ShowDialog();
        }
        catch (Exception e)
        {
            MessageBox.Show("无法打开内置网页窗口: " + e.Message, "Sunshine Duo 托盘");
        }
        await Task.CompletedTask;
    }

    private async void OpenPinPage(bool console)
    {
        var port = (console ? _cfg.ConsolePort : _cfg.GamePort) + 1;
        var title = console ? "主会话配对" : "游戏会话配对";
        try
        {
            using var f = new WebViewForm(_cfg, title, port, "/pin#PIN");
            f.ShowDialog();
        }
        catch (Exception e)
        {
            MessageBox.Show("无法打开配对页面: " + e.Message, "Sunshine Duo 托盘");
        }
        await Task.CompletedTask;
    }

    private async void RestartApollo(bool console)
    {
        var name = console ? "主会话" : "游戏会话";
        var target = console ? "console" : "game";
        if (MessageBox.Show($"确定重启{name}？（会断开当前该会话的串流）",
                "重启会话", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        _tray.ShowBalloonTip(4000, "重启会话", $"正在向{name}发送重启指令…", ToolTipIcon.Info);
        string? err;
        try
        {
            err = await GuardPipe.RestartApollo(target);
        }
        catch (Exception e)
        {
            err = e.Message;
        }
        _tray.ShowBalloonTip(4000, "重启会话",
            err == null ? $"{name}重启指令已执行" : "重启失败: " + err,
            err == null ? ToolTipIcon.Info : ToolTipIcon.Error);
        await PollAsync();
    }

    private async void ToggleService()
    {
        var state = await Task.Run(() => ServiceControl.Query());
        bool running = state == "运行中" || state == "启动中";
        if (MessageBox.Show($"Sunshine Duo 核心服务当前：{state}。确定{(running ? "停止" : "启动")}？",
                "Sunshine Duo 核心服务", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        var err = await Task.Run(() => ServiceControl.SetRunning(!running));
        _tray.ShowBalloonTip(4000, "Sunshine Duo 核心服务",
            err ?? "指令已执行。",
            err == null ? ToolTipIcon.Info : ToolTipIcon.Error);
        await PollAsync();
    }

    private async Task SetDisplayMode(string mode)
    {
        var name = mode switch
        {
            "only" => "独占 · 仅虚拟显示器",
            "extend" => "扩展 · 物理屏 + 虚拟屏",
            _ => "镜像 · 串流物理主屏",
        };
        if (MessageBox.Show($"切换显示模式为「{name}」？（由 Sunshine Duo 核心服务运行期执行，串流中会立即切换拓扑）",
                "显示模式", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        _tray.ShowBalloonTip(4000, "显示模式", $"正在切换为「{name}」…", ToolTipIcon.Info);
        string? err;
        try
        {
            err = await GuardPipe.SetDisplayMode(mode);
        }
        catch (Exception e)
        {
            err = e.Message;
        }
        _tray.ShowBalloonTip(4000, "显示模式",
            err == null ? $"已切换为「{name}」" : "切换失败: " + err,
            err == null ? ToolTipIcon.Info : ToolTipIcon.Error);
        UpdateDisplayCheckmarks();
    }

    private void UpdateDisplayCheckmarks()
    {
        try
        {
            var cfg = Path.Combine(Paths.Root, "wsguard", "config.json");
            if (!File.Exists(cfg)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (!doc.RootElement.TryGetProperty("default_display_mode", out var v)) return;
            var mode = v.GetString() ?? "mirror";
            _mMirror.Checked = mode == "mirror";
            _mOnly.Checked = mode == "only";
            _mExtend.Checked = mode == "extend";
        }
        catch { }
    }

    private void ShowPairForm()
    {
        using var f = new PairForm(_cfg);
        f.ShowDialog();
        _ = PollAsync();
    }

    private void ShowResForm()
    {
        using var f = new ResForm();
        f.ShowDialog();
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

    private void ShowAbout()
    {
        var url = "https://github.com/xRetia/Sunshine-Duo";
        var text = $"Sunshine Duo 托盘\n\n作者：苏羽茜工作室（xRetia Labs.）\nGitHub 项目地址：{url}";
        if (MessageBox.Show(text + "\n\n是否打开项目主页？", "关于 Sunshine Duo",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }
    }

    private void Exit()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }
}
