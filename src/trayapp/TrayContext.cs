using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

namespace SunshineDuoTray;

public class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly AppConfig _cfg;
    private readonly ToolStripMenuItem _mConsoleStatus;
    private readonly ToolStripMenuItem _mGameStatus;
    private readonly ToolStripMenuItem _mMirror, _mOnly, _mExtend;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _eventTimer;
    private bool _polling;
    private bool _consoleUp;
    private bool _gameUp;
    private bool _pairDialogOpen;

    public TrayContext()
    {
        _cfg = AppConfig.Load();

        _mConsoleStatus = new ToolStripMenuItem($"主会话 [检测中] [https://*:{_cfg.ConsolePort + 1}]") { Enabled = false };
        _mGameStatus = new ToolStripMenuItem($"游戏会话 [检测中] [https://*:{_cfg.GamePort + 1}]") { Enabled = false };

        _mMirror = new ToolStripMenuItem("镜像 · 串流物理主屏") { Tag = "mirror" };
        _mOnly = new ToolStripMenuItem("独占 · 仅虚拟显示器") { Tag = "only" };
        _mExtend = new ToolStripMenuItem("扩展 · 物理屏 + 虚拟屏") { Tag = "extend" };
        foreach (var it in new[] { _mMirror, _mOnly, _mExtend })
        {
            var mode = (string)it.Tag!;
            it.Click += async (_, _) =>
            {
                SetCheckedMode(mode); // 三个模式互斥单选，点击先立即更新勾选态
                await SetDisplayMode(mode);
            };
        }

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

        // 右键菜单跟随系统深色/浅色主题
        var light = IsSystemLightTheme();
        ApplyThemeToMenu(menu, light);

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

        // 核心服务配对事件轮询：不碰 Apollo，只走本地控制管道（SunshineDuoCore 自己监听通知/日志）
        _eventTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _eventTimer.Tick += async (_, _) => await PollPairEvents();
        _eventTimer.Start();
    }

    private static ToolStripMenuItem M(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    // 系统深色/浅色主题检测：读注册表 AppsUseLightTheme
    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key == null) return false;
            var v = key.GetValue("AppsUseLightTheme");
            if (v is int i) return i == 1;
        }
        catch { }
        return false;
    }

    // 把右键菜单的渲染色设为跟随系统主题（浅色/深色）
    private static void ApplyThemeToMenu(ContextMenuStrip menu, bool light)
    {
        var colors = new ThemeColors(light);
        menu.BackColor = light ? Color.White : Color.FromArgb(24, 26, 30);
        menu.ForeColor = light ? Color.Black : Color.FromArgb(240, 242, 245);
        menu.Renderer = new MenuRenderer(light, colors);
        foreach (ToolStripItem it in menu.Items)
            ApplyThemeToItem(it, light);
    }

    private static void ApplyThemeToItem(ToolStripItem item, bool light)
    {
        var back = light ? Color.White : Color.FromArgb(24, 26, 30);
        var fore = light ? Color.Black : Color.FromArgb(240, 242, 245);
        item.BackColor = back;
        item.ForeColor = fore;
        if (item is ToolStripMenuItem mi)
        {
            mi.DropDown.BackColor = back;
            mi.DropDown.ForeColor = fore;
            foreach (ToolStripItem sub in mi.DropDownItems)
                ApplyThemeToItem(sub, light);
        }
    }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly bool _light;

        public MenuRenderer(bool light, ProfessionalColorTable colors) : base(colors)
            => _light = light;

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            var back = e.ToolStrip.BackColor.IsEmpty
                ? (_light ? Color.White : Color.FromArgb(24, 26, 30))
                : e.ToolStrip.BackColor;
            using var brush = new SolidBrush(back);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var back = e.Item.BackColor.IsEmpty
                ? (_light ? Color.White : Color.FromArgb(24, 26, 30))
                : e.Item.BackColor;
            using (var brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, e.Item.Bounds);
            var lineColor = back.GetBrightness() > 0.5f
                ? Color.FromArgb(226, 230, 236)
                : Color.FromArgb(55, 60, 68);
            using var pen = new Pen(lineColor);
            var r = e.Item.ContentRectangle;
            if (r.Width <= 0 || r.Height <= 0)
                r = e.Item.Bounds;
            if (e.Vertical)
            {
                var x = r.Left + r.Width / 2;
                e.Graphics.DrawLine(pen, x, r.Top + 2, x, Math.Max(r.Top + 2, r.Bottom - 2));
            }
            else
            {
                var y = r.Top + r.Height / 2;
                e.Graphics.DrawLine(pen, r.Left + 1, y, Math.Max(r.Left + 1, r.Right - 1), y);
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            if (e.Item is not { } item) return;
            e.ArrowColor = item.Enabled
                ? item.ForeColor
                : (_light ? SystemColors.GrayText : Color.FromArgb(110, 115, 124));
            base.OnRenderArrow(e);
        }
    }

    // 专用颜色表：让 ToolStripProfessionalRenderer 按系统主题渲染
    private sealed class ThemeColors : ProfessionalColorTable
    {
        private readonly bool _light;
        public ThemeColors(bool light) => _light = light;
        public override Color MenuItemSelected => _light
            ? Color.FromArgb(230, 235, 240)
            : Color.FromArgb(50, 54, 61);
        public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override Color MenuBorder => _light
            ? Color.FromArgb(200, 200, 200)
            : Color.FromArgb(70, 75, 84);
        public override Color MenuItemBorder => MenuBorder;
        // 图标栏与菜单底色一致，否则浅色主题下左侧会出现一条灰带
        public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
        public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override Color ToolStripDropDownBackground => _light
            ? Color.White
            : Color.FromArgb(24, 26, 30);
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

    // 被动配对事件：SunshineDuoCore 监听两个会话的 Apollo 通知/日志并压入事件队列，
    // trayapp 只轮询本地命名管道（不向 Apollo 发任何请求），拿到事件即弹出配对窗。
    // 弹窗打开期间跳过轮询（事件留在 SunshineDuoCore 队列，关窗后下个周期再取），
    // 同一批事件按会话合并，避免一次连接突发弹出多个窗。
    private async Task PollPairEvents()
    {
        if (_pairDialogOpen) return;
        var events = await GuardPipe.PollEvents();
        if (events == null || events.Count == 0) return;
        var latest = new Dictionary<bool, GuardPipe.PairEvent>();
        foreach (var ev in events)
            latest[ev.Target != "game"] = ev;
        _pairDialogOpen = true;
        try
        {
            foreach (var kv in latest)
                ShowPairFormPreset(kv.Key, kv.Value.Payload);
        }
        finally
        {
            _pairDialogOpen = false;
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
        {
            UpdateDisplayCheckmarks(); // 用户取消：勾选态恢复为配置里的模式
            return;
        }
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

    private void SetCheckedMode(string mode)
    {
        _mMirror.Checked = mode == "mirror";
        _mOnly.Checked = mode == "only";
        _mExtend.Checked = mode == "extend";
    }

    private void UpdateDisplayCheckmarks()
    {
        try
        {
            var cfg = Path.Combine(Paths.Root, "bin", "SunshineDuoCore", "config.json");
            if (!File.Exists(cfg)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (!doc.RootElement.TryGetProperty("default_display_mode", out var v)) return;
            SetCheckedMode(v.GetString() ?? "mirror");
        }
        catch { }
    }

    private void ShowPairFormPreset(bool console, string deviceName)
    {
        // WinForms Timer 的 Tick 在 UI 线程触发，这里天然在 UI 线程
        using var f = new PairForm(_cfg, console);
        f.Text = $"设备配对 — {deviceName}";
        f.ShowDialog();
        _ = PollAsync();
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
        _eventTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }
}
