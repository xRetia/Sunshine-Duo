using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

namespace SunshineDuoTray;

public class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly AppConfig _cfg;
    private readonly Dictionary<string, ToolStripMenuItem> _statusItems = new();
    private readonly ToolStripMenuItem _mMirror, _mOnly, _mExtend;
    private ToolStripMenuItem? _displayMenu;
    private ToolStripSeparator? _displaySep;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _eventTimer;
    private IMainUi? _mainForm;
    private volatile bool _openRequested;
    private bool _polling;
    private bool _pairDialogOpen;
    private bool _svcWarned;
    private bool _svcDialogShown;

    public TrayContext()
    {
        _cfg = AppConfig.Load();
        Theme.Apply(_cfg.Theme);
        var sessions = Paths.AllSessions(_cfg);

        foreach (var s in sessions)
            _statusItems[s.Id] = new ToolStripMenuItem($"{s.Name} [检测中] [https://*:{s.WebPort}]") { Enabled = false };

        _mMirror = new ToolStripMenuItem("镜像") { Tag = "mirror" };
        _mOnly = new ToolStripMenuItem("独占") { Tag = "only" };
        _mExtend = new ToolStripMenuItem("扩展") { Tag = "extend" };
        foreach (var it in new[] { _mMirror, _mOnly, _mExtend })
        {
            var mode = (string)it.Tag!;
            it.Click += async (_, _) =>
            {
                SetCheckedMode(mode); // 三个模式互斥单选，点击先立即更新勾选态
                await SetDisplayMode(mode);
            };
        }

        var menu = new ContextMenuStrip();
        foreach (var s in sessions) menu.Items.Add(_statusItems[s.Id]);
        menu.Items.Add(new ToolStripSeparator());

        // 只保留 5 个常用项：主界面 / 显示模式 / 高级设置 / 设备配对 / 检查更新
        // 其余（虚拟显示器、内网穿透、快捷键、隐藏指针、重启会话、服务启停）都在主界面里
        menu.Items.Add(M("打开主界面", () => ShowMain(FirstPage)));
        // 显示模式菜单恒建，可见性由 SyncMenuVisibility 按实时 console 开关控制
        _displaySep = new ToolStripSeparator();
        menu.Items.Add(_displaySep);
        _displayMenu = new ToolStripMenuItem("显示模式");
        _displayMenu.DropDownItems.Add(_mMirror);
        _displayMenu.DropDownItems.Add(_mOnly);
        _displayMenu.DropDownItems.Add(_mExtend);
        UpdateDisplayCheckmarks();
        menu.Items.Add(_displayMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(M("高级设置…", () => ShowMain(MainPage.Settings)));
        menu.Items.Add(M("设备配对…", () => ShowMain(MainPage.Console, true)));
        menu.Items.Add(M("检查更新…", () => _ = CheckUpdateFromMenu()));
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
        _tray.DoubleClick += (_, _) => ShowMain(FirstPage);
        SyncMenuVisibility();

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();

        // 核心服务配对事件轮询：不碰 Apollo，只走本地控制管道（SunshineDuoCore 自己监听通知/日志）
        _eventTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _eventTimer.Tick += async (_, _) => await PollPairEvents();
        _eventTimer.Start();

        // 第二实例（再次运行 exe）通过命名管道请求呼出主界面
        StartUiPipe();
    }

    private void StartUiPipe()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        "SunshineDuoTray-ui", PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server);
                    if (await reader.ReadLineAsync() == "open") _openRequested = true;
                }
                catch
                {
                    await Task.Delay(500);
                }
            }
        });
    }

    // 右键菜单「检查更新」：有新版直接拉起安装向导的更新流程，无新版则提示已是最新
    private async Task CheckUpdateFromMenu()
    {
        var (has, remote, local) = await UpdateCheck.CheckAsync(force: true);
        if (!has)
        {
            MessageBox.Show($"已是最新版本（v{local}）。", "萌远控 Duo",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var r = MessageBox.Show($"发现新版本 v{remote}（当前 v{local}）。\n\n现在更新？",
            "萌远控 Duo", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        var err = UpdateCheck.StartUpdate();
        if (err is not null)
            MessageBox.Show(err, "萌远控 Duo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            if (_openRequested)
            {
                _openRequested = false;
                ShowMain(FirstPage);
            }
            SyncMenuVisibility(); // 会话开关实时读取，单/双会话切换后菜单动态跟随
            var sessions = Paths.GetSessions(_cfg);
            var up = new List<bool>();
            foreach (var s in sessions)
            {
                using var c = new ApolloClient(s.WebPort, _cfg.WebUser, _cfg.WebPass);
                var ok = await c.Probe();
                up.Add(ok);
                if (_statusItems.TryGetValue(s.Id, out var item))
                    item.Text = $"{s.Name} [{(ok ? "运行中" : "未运行")}] [https://*:{s.WebPort}]";
            }
            var online = up.Count(b => b);
            var state = online == up.Count && up.Count > 0 ? TrayState.Online
                : online > 0 ? TrayState.Partial : TrayState.Offline;
            _tray.Icon = TrayIcons.Get(state);
            _tray.Text = "Sunshine Duo 托盘\n" + string.Join("\n",
                sessions.Select((s, i) => $"{s.Name} {(up[i] ? "运行中" : "未运行")}"));
            UpdateDisplayCheckmarks();
            await CheckCoreServiceAsync();
        }
        catch { }
        finally
        {
            _polling = false;
        }
    }

    // 核心服务异常时弹托盘气泡提示（用户不打开主界面也能看到），同一状态只提示一次
    private async Task CheckCoreServiceAsync()
    {
        var svc = await Task.Run(ServiceControl.Query);
        var bad = svc != "运行中" && svc != "启动中";
        if (!bad)
        {
            _svcWarned = false;
            _svcDialogShown = false;
            return;
        }
        if (_svcWarned) return;
        _svcWarned = true;
        var detail = svc == "未安装" ? "核心服务未安装，串流与配对都无法使用。"
            : svc == "查询失败" ? "无法查询核心服务状态。"
            : $"核心服务当前为「{svc}」，串流与配对无法使用。";
        _tray.ShowBalloonTip(8000, "萌远控服务异常", detail, ToolTipIcon.Warning);
        if (_mainForm is { Visible: true } || _svcDialogShown) return;
        _svcDialogShown = true;
        BeginInvoke(new Action(() =>
        {
            if (_mainForm is { Visible: true }) return;
            var result = MessageBox.Show(detail + "\n\n是否现在打开设置？", "萌远控服务异常",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result == DialogResult.Yes) ShowMain(MainPage.Settings);
        }));
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
        var enabled = new HashSet<string>(Paths.GetSessions(_cfg).Select(s => s.Id));
        var latest = new Dictionary<bool, GuardPipe.PairEvent>();
        foreach (var ev in events)
        {
            if (!enabled.Contains(ev.Target)) continue; // 单会话安装：忽略未启用会话的事件
            latest[ev.Target != "game"] = ev;
        }
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

    // 依据实时会话开关同步托盘菜单显隐（单/双会话切换后无需重启托盘）
    private void SyncMenuVisibility()
    {
        var con = Paths.IsConsoleEnabled();
        // 导航状态项（console/game）：按当前启用的会话显示/隐藏
        foreach (var kv in _statusItems)
            kv.Item.Visible = kv.Key == "console" ? con : Paths.IsGameEnabled();
        // 显示模式菜单（含前置分隔线）：仅当 console 启用时可见
        if (_displayMenu != null) _displayMenu.Visible = con;
        if (_displaySep != null) _displaySep.Visible = con;
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
        using var f = new PairForm(_cfg, console, Paths.IsGameEnabled());
        f.Text = $"设备配对 — {deviceName}";
        f.ShowDialog();
        _ = PollAsync();
    }

    // 首页选择跟随实时会话开关（单会话安装后不再落在被禁用的页面）
    private static MainPage FirstPage =>
        Paths.IsConsoleEnabled() ? MainPage.Console
        : Paths.IsGameEnabled() ? MainPage.Game
        : MainPage.Settings;

    private void ShowMain(MainPage page, bool focusPin = false)
    {
        try
        {
            if (_mainForm == null || _mainForm.IsDisposed)
            {
                _mainForm = new WebUiForm(_cfg);
            }
            _mainForm.Navigate(page);
            if (!_mainForm.Visible) _mainForm.Show();
            if (_mainForm.WindowState == FormWindowState.Minimized)
                _mainForm.WindowState = FormWindowState.Normal;
            _mainForm.Activate();
            if (focusPin) _mainForm.FocusPair();
            _ = PollAsync();
        }
        catch (Exception e)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "trayapp-ui.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 打开主界面失败\r\n{e}\r\n\r\n");
            }
            catch { }
            try
            {
                _tray.ShowBalloonTip(5000, "打开主界面失败",
                    "详情见 bin\\SunshineDuoTray\\trayapp-ui.log", ToolTipIcon.Error);
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
