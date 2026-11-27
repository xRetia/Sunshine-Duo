using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SunshineDuoTray;

internal enum MainPage
{
    Console,
    Game,
    VirtualDisplay,
    Tunnel,
    Settings,
    About,
}

internal interface IMainUi : IDisposable
{
    bool Visible { get; }
    bool IsDisposed { get; }
    FormWindowState WindowState { get; set; }
    void Navigate(MainPage page);
    void FocusPair();
    void Show();
    void Activate();
}

// WebView2 版主界面：原生窗口外壳 + 本地 HTML UI，托盘菜单/通知仍由 WinForms 承担。
internal sealed class WebUiForm : Form, IMainUi
{
    private readonly AppConfig _cfg;
    private readonly WebView2 _view = new();
    private readonly System.Windows.Forms.Timer _timer;
    private bool _ready, _polling, _focusPin;
    private MainPage _page = MainPage.Settings;
    private string _wanIp = "";
    private DateTime _wanAt = DateTime.MinValue;

    public static bool IsSupported
    {
        get
        {
            try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
            catch { return false; }
        }
    }

    public WebUiForm(AppConfig cfg)
    {
        _cfg = cfg;
        Text = "Sunshine Duo";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1020, 680);
        BackColor = WindowColor();
        DoubleBuffered = true;
        ShowInTaskbar = true;
        MaximizeBox = false;
        Icon = TrayIcons.AppIcon();
        KeyPreview = true;

        _view.Dock = DockStyle.Fill;
        _view.DefaultBackgroundColor = WindowColor();
        Controls.Add(_view);

        _view.WebMessageReceived += OnMessage;
        OnSizeChanged(null!);

        _timer = new System.Windows.Forms.Timer { Interval = 2500 };
        _timer.Tick += async (_, _) => await PollAsync();

        _ = InitAsync();
        _ = WarmWanAsync();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var path = UiKit.RoundRect(new RectangleF(0, 0, Width, Height), 8);
        Region = new Region(path);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var preference = (int)DwmWindowCornerPreference.Round;
        if (DwmSetWindowAttribute(Handle, DwmWindowAttribute.CornerPreference,
                ref preference, sizeof(int)) != 0) return;
        var none = DwmColorNone;
        DwmSetWindowAttribute(Handle, DwmWindowAttribute.BorderColor, ref none, sizeof(uint));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW：轻量投影
            return cp;
        }
    }

    private const uint DwmColorNone = 0xFFFFFFFE;

    private enum DwmWindowAttribute
    {
        CornerPreference = 33,
        BorderColor = 34,
    }

    private enum DwmWindowCornerPreference
    {
        Round = 2,
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute,
        ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute,
        ref uint value, int size);

    private Color WindowColor()
        => UiKit.IsLight ? Color.FromArgb(246, 247, 250) : Color.FromArgb(14, 16, 19);

    private static string WebFolder => Path.Combine(AppContext.BaseDirectory, "webui");

    private async Task InitAsync()
    {
        try
        {
            var env = await WebViewEnvironment.GetAsync();
            await _view.EnsureCoreWebView2Async(env);
            var core = _view.CoreWebView2!;
            var st = core.Settings;
            st.AreBrowserAcceleratorKeysEnabled = false;
            st.AreDefaultContextMenusEnabled = false;
            st.AreDevToolsEnabled = false;
            st.IsStatusBarEnabled = false;
            st.IsZoomControlEnabled = false;
            st.IsPinchZoomEnabled = false;
            st.IsSwipeNavigationEnabled = false;
            st.IsGeneralAutofillEnabled = false;
            st.IsPasswordAutosaveEnabled = false;
            st.IsBuiltInErrorPageEnabled = false;
            st.IsNonClientRegionSupportEnabled = true;
            st.IsWebMessageEnabled = true;
            _view.DefaultBackgroundColor = WindowColor();
            if (core.Profile != null)
                core.Profile.PreferredColorScheme = UiKit.IsLight
                    ? CoreWebView2PreferredColorScheme.Light
                    : CoreWebView2PreferredColorScheme.Dark;

            core.SetVirtualHostNameToFolderMapping(
                "sunduo.local", WebFolder, CoreWebView2HostResourceAccessKind.Allow);
            core.Navigate("https://sunduo.local/index.html");
        }
        catch (WebViewEnvironment.RuntimeMissingException)
        {
            // WebView2 运行时缺失：WebViewEnvironment 已弹过原生对话框（可选打开下载页），
            // 这里不再重复弹框，走与通用失败相同的优雅退出路径（关闭窗口、不留僵尸进程）。
            Log("WebView2 运行时缺失，托盘界面退出。");
            DialogResult = DialogResult.Cancel;
            Close();
        }
        catch (Exception e)
        {
            Log("WebView2 初始化失败：" + e.Message);
            MessageBox.Show(this, "内置界面初始化失败，已切换到兼容界面：" + e.Message,
                "Sunshine Duo", MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    public void Navigate(MainPage page)
    {
        _page = page;
        if (_ready) Push("navigate", PageId(page));
    }

    public void FocusPair()
    {
        _focusPin = true;
        _page = MainPage.Console;
        if (_ready)
        {
            Push("navigate", "console");
            Push("focusPair", null);
        }
    }

    private static string PageId(MainPage page) => page switch
    {
        MainPage.Console => "console",
        MainPage.Game => "game",
        MainPage.VirtualDisplay => "vda",
        MainPage.Tunnel => "tunnel",
        MainPage.Settings => "settings",
        _ => "about",
    };

    private void Push(string name, object? data)
    {
        if (IsDisposed || _view.CoreWebView2 == null) return;
        _view.CoreWebView2.PostWebMessageAsJson(
            JsonSerializer.Serialize(new { @event = name, data }));
    }

    /* ===================== RPC ===================== */

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(e.WebMessageAsJson); }
        catch { return; }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var mv)) return;
            var method = mv.GetString() ?? "";
            var id = root.TryGetProperty("id", out var iv) ? iv.GetInt32() : 0;
            var args = root.TryGetProperty("args", out var av) ? av.Clone() : default;

            string reply;
            try
            {
                var result = await DispatchAsync(method, args);
                reply = JsonSerializer.Serialize(new { id, ok = true, result });
            }
            catch (Exception ex)
            {
                reply = JsonSerializer.Serialize(new { id, ok = false, error = ex.Message });
            }

            if (!IsDisposed && _view.CoreWebView2 != null)
                _view.CoreWebView2.PostWebMessageAsJson(reply);
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement a)
    {
        switch (method)
        {
            case "init":
                _ready = true;
                var snap = await SnapshotAsync();
                _focusPin = false;
                return snap;

            case "easytierGet":
            {
                var st = await GuardPipe.EasyTierStatus()
                    ?? throw new Exception("核心服务控制通道不可用");
                return EasyTierObj(st);
            }

            case "easytierSet":
            {
                var peers = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("peers", out var pa)
                    && pa.ValueKind == JsonValueKind.Array
                        ? pa.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                        : Array.Empty<string>();
                var (st, startErr) = await GuardPipe.EasyTierSet(new GuardPipe.EasyTierApply
                {
                    Enabled = Bool(a, "enabled"),
                    Network = Str(a, "network"),
                    // 未携带 secret（或为 null）表示不改动已配置的密钥
                    Secret = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("secret", out var sv)
                             && sv.ValueKind == JsonValueKind.String ? sv.GetString() : null,
                    ServerMode = Str(a, "server_mode", "public"),
                    Peers = peers,
                    Ipv4 = Str(a, "ipv4"),
                    TcpHolePunch = Bool(a, "tcp_hole_punch"),
                });
                if (st == null) throw new Exception(startErr ?? "核心服务控制通道不可用");
                var msg = startErr != null
                    ? "已保存，但启动失败：" + startErr
                    : Bool(a, "enabled")
                        ? (st.Running ? "内网穿透已启动。" : "已启用，等待核心就绪。")
                        : "内网穿透已停止。";
                return new { state = EasyTierObj(st), message = msg, failed = startErr != null };
            }

            case "openUrl":
            {
                var url = Str(a, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                    throw new Exception("仅支持 http/https 链接");
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return true;
            }

            case "log":
                Log(Str(a, "message"));
                return true;

            case "poll":
            {
                var p = await PollSnapshot();
                return new { sessions = p.sessions, service = p.service, wan = p.wan, tunnel = p.tunnel };
            }

            case "getWanIp":
            {
                var (ip, err) = await NetInfo.WanIpAsync(true);
                if (ip.Length == 0) throw new Exception(err.Length > 0 ? err : "获取失败");
                _wanIp = ip;
                _wanAt = DateTime.Now;
                return ip;
            }

            case "copyText":
                Clipboard.SetText(Str(a, "text"));
                return true;

            case "sendPin":
                return await SendPin(Str(a, "sid"), Str(a, "code"));

            case "setDisplayMode":
            {
                var err = await GuardPipe.SetDisplayMode(Str(a, "mode"));
                if (err != null) throw new Exception(err);
                return true;
            }

            case "setResolution":
            {
                var list = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("list", out var la)
                    && la.ValueKind == JsonValueKind.Array
                        ? la.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                        : Array.Empty<string>();
                var err = await GuardPipe.SetResolution(Int(a, "width"), Int(a, "height"), list);
                if (err != null) throw new Exception(err);
                return $"已保存，下一场串流按 {Int(a, "width")} × {Int(a, "height")} 建立。";
            }

            case "setHotkeys":
            {
                var err = await GuardPipe.SetHotkeys(Bool(a, "ralt"), Bool(a, "raltl"));
                if (err != null) throw new Exception(err);
                return "快捷键设置已应用。";
            }

            case "setHideCursor":
            {
                var on = Bool(a, "enabled");
                var err = await GuardPipe.SetHideCursor(on);
                if (err != null) throw new Exception(err);
                return on ? "连接后将隐藏系统鼠标指针。" : "已恢复显示系统鼠标指针。";
            }

            case "setAutoStart":
            {
                var on = Bool(a, "enabled");
                AutoStart.Set(on);
                _cfg.AutoStart = on;
                _cfg.Save();
                return true;
            }

            case "restartApollo":
            {
                var id = Str(a, "id", "console");
                var name = id == "console" ? "当前会话" : "游戏会话";
                var err = await GuardPipe.RestartApollo(id);
                if (err != null) throw new Exception(err);
                return name + "重启指令已执行。";
            }

            case "savePorts":
                return await SavePorts(Int(a, "cport"), Int(a, "gport"));

            case "applyCredentials":
                return await ApplyCredentials(Str(a, "user"), Str(a, "pass"));

            case "toggleService":
            {
                var state = await Task.Run(ServiceControl.Query);
                var running = state is "运行中" or "启动中";
                var err = await ServiceControl.SetRunningAsync(!running);
                if (err != null) throw new Exception(err);
                await Task.Delay(700);
                var now = await Task.Run(ServiceControl.Query);
                return new { service = now, message = (running ? "停止" : "启动") + "指令已执行。" };
            }

            case "setTheme":
            {
                var mode = Str(a, "mode", "system");
                _cfg.Theme = mode;
                _cfg.Save();
                Theme.Apply(mode);
                BackColor = WindowColor();
                if (_view.CoreWebView2 != null)
                    _view.DefaultBackgroundColor = WindowColor();
                return true;
            }

            case "openWebUi":
            {
                var sid = Str(a, "sid", "console");
                var path = Str(a, "path", "/config");
                var s = Paths.GetSessions(_cfg).FirstOrDefault(x => x.Id == sid)
                        ?? throw new Exception("会话不可用");
                // 不能在本消息回调里直接 ShowDialog：会阻塞 WebView2 事件泵，
                // 新窗口停在 about:blank；推迟到下一拍消息循环再弹。
                var title = path.StartsWith("/pin", StringComparison.OrdinalIgnoreCase)
                    ? "已配对设备管理" : "高级设置";
                BeginInvoke(() =>
                {
                    using var f = new WebViewForm(_cfg, title, s.WebPort, path);
                    f.ShowDialog(this);
                });
                return true;
            }

            case "openHomepage":
                Process.Start(new ProcessStartInfo("https://yk.acgdraw.com")
                { UseShellExecute = true });
                return true;

            case "checkUpdate":
            {
                var (has, remote, local) = await UpdateCheck.CheckAsync();
                return new { has, remote, local };
            }

            case "runUpdate":
            {
                var err = UpdateCheck.StartUpdate();
                if (err != null) throw new Exception(err);
                return true;
            }

            case "windowMinimize":
                WindowState = FormWindowState.Minimized;
                return true;

            case "windowClose":
                BeginInvoke(() => Close());
                return true;

            default:
                throw new Exception("未知操作：" + method);
        }
    }

    private static string Str(JsonElement a, string key, string def = "")
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : def;

    private static int Int(JsonElement a, string key, int def = 0)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : def;

    private static bool Bool(JsonElement a, string key, bool def = false)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v)
           && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;

    /* ===================== 状态快照与轮询 ===================== */

    private static object EasyTierObj(GuardPipe.EasyTierState st) => new
    {
        running = st.Running,
        pid = st.Pid,
        managed = st.Managed,
        enabled = st.Enabled,
        network = st.Network,
        machine = st.Machine,
        secretSet = st.SecretSet,
        serverMode = st.ServerMode,
        defaultServer = st.DefaultServer,
        peers = st.Peers,
        ipv4 = st.Ipv4,
        tcpHolePunch = st.TcpHolePunch,
        virtualIp = st.VirtualIp,
        publicIp = st.PublicIp,
        exe = st.Exe,
        exeExists = st.ExeExists,
    };

    private async Task<object> SnapshotAsync()
    {
        var sessions = new Dictionary<string, object>();
        foreach (var s in Paths.GetSessions(_cfg))
            sessions[s.Id] = new
            {
                id = s.Id,
                name = s.Name,
                sub = s.Id == "console" ? "远程控制当前用户" : "远程控制独立运行游戏的用户",
                stream = s.StreamPort,
                web = s.WebPort,
            };

        var (aw, ah, list) = ReadResolution();
        var vda = new
        {
            mode = Paths.CoreString("default_display_mode", "only"),
            aw, ah, list, sel = -1,
        };
        var et = await GuardPipe.EasyTierStatus();
        var tunnel = et == null ? null : EasyTierObj(et);

        return new
        {
            theme = _cfg.Theme ?? "system",
            page = PageId(_page),
            focusPin = _focusPin,
            consoleEnabled = Paths.ConsoleEnabled,
            gameEnabled = Paths.GameEnabled,
            sessions,
            net = new { lan = NetInfo.LocalIp(), wan = _wanIp },
            core = new
            {
                cport = _cfg.ConsolePort,
                gport = _cfg.GamePort,
                user = _cfg.WebUser,
                autoStart = AutoStart.IsEnabled(),
                ralt = Paths.CoreFlag("win_from_ralt", false),
                raltl = Paths.CoreFlag("winl_from_ralt_l", true),
                hideCursor = Paths.CoreFlag("hide_cursor_on_stream", false),
                service = ServiceControl.Query(),
            },
            vda,
            tunnel,
        };
    }

    private async Task<(Dictionary<string, bool?> sessions, string service, string wan, object? tunnel)> PollSnapshot()
    {
        var sessions = new Dictionary<string, bool?>();
        foreach (var s in Paths.GetSessions(_cfg))
        {
            try
            {
                using var client = new ApolloClient(s.WebPort, _cfg.WebUser, _cfg.WebPass);
                sessions[s.Id] = await client.Probe();
            }
            catch { sessions[s.Id] = false; }
        }
        var service = await Task.Run(ServiceControl.Query);
        var tunnel = await GuardPipe.EasyTierStatus();
        return (sessions, service, _wanIp, tunnel == null ? null : EasyTierObj(tunnel));
    }

    private async Task PollAsync()
    {
        if (_polling || !_ready) return;
        _polling = true;
        try
        {
            var s = await PollSnapshot();
            Push("session", s.sessions);
            Push("service", s.service);
            if (s.tunnel != null) Push("tunnel", s.tunnel);
            if (_wanIp.Length > 0) Push("wan", _wanIp);
            if (DateTime.Now - _wanAt > TimeSpan.FromHours(1)) _ = WarmWanAsync();
        }
        catch { }
        finally { _polling = false; }
    }

    private async Task WarmWanAsync()
    {
        try
        {
            var (ip, _) = await NetInfo.WanIpAsync(false);
            if (ip.Length > 0) { _wanIp = ip; _wanAt = DateTime.Now; }
        }
        catch { }
    }

    private static (int w, int h, string[] list) ReadResolution()
    {
        try
        {
            var cfg = Path.Combine(Paths.Root, "bin", "SunshineDuoCore", "config.json");
            if (!File.Exists(cfg)) return (1600, 720, Array.Empty<string>());
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            var r = doc.RootElement;
            var w = r.TryGetProperty("width", out var vw) ? vw.GetInt32() : 1600;
            var h = r.TryGetProperty("height", out var vh) ? vh.GetInt32() : 720;
            var list = r.TryGetProperty("sudovda_resolutions", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToArray()
                : Array.Empty<string>();
            return (w, h, list);
        }
        catch { return (1600, 720, Array.Empty<string>()); }
    }

    /* ===================== 后端动作 ===================== */

    private async Task<string> SendPin(string sid, string pin)
    {
        if (pin.Length != 4 || !pin.All(char.IsDigit)) throw new Exception("请输入设备上显示的 4 位配对码。");
        var s = Paths.GetSessions(_cfg).FirstOrDefault(x => x.Id == sid)
                ?? throw new Exception("会话不可用");
        using var client = new ApolloClient(s.WebPort, _cfg.WebUser, _cfg.WebPass);
        var (before, listErr) = await client.ListClients();
        var err = await client.SendPin(pin);
        if (!string.IsNullOrEmpty(err)) throw new Exception(err);

        var grantErr = listErr;
        if (grantErr.Length == 0)
        {
            var (after, lerr) = await client.ListClients();
            grantErr = lerr;
            if (grantErr.Length == 0)
            {
                var old = before.Select(d => d.Uuid).ToHashSet();
                foreach (var d in after.Where(x => !old.Contains(x.Uuid)))
                {
                    var g = await client.GrantAllPermissions(d);
                    if (!string.IsNullOrEmpty(g)) grantErr = g;
                }
            }
        }
        if (grantErr.Length > 0)
            throw new Exception("配对成功，但自动授权失败：" + grantErr + "。请到 WebUI 设备管理手动授权。");
        return "配对成功，已授予全部权限。请在 Moonlight 上确认。";
    }

    private async Task<string> SavePorts(int cp, int gp)
    {
        var errors = new List<string>();
        var changed = new List<string>();
        foreach (var (id, name) in new[] { ("console", "当前会话"), ("game", "游戏会话") })
        {
            var s = Paths.GetSessions(_cfg).FirstOrDefault(x => x.Id == id);
            var port = id == "console" ? cp : gp;
            if (s != null && s.StreamPort != port)
            {
                if (WriteConfPort(s.ConfPath, port)) changed.Add(name);
                else errors.Add("写入" + name + "conf 失败");
                if (id == "console") _cfg.ConsolePort = port; else _cfg.GamePort = port;
            }
        }
        _cfg.Save();
        foreach (var (id, name) in new[] { ("console", "当前会话"), ("game", "游戏会话") })
            if (changed.Contains(name))
            {
                var err = await GuardPipe.RestartApollo(id);
                if (err != null) errors.Add(name + "重启失败：" + err);
            }
        if (errors.Count > 0) throw new Exception(string.Join("；", errors));
        return changed.Count == 0 ? "端口未变化。" : "端口已保存，会话已重启。";
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
        catch { return false; }
    }

    private async Task<string> ApplyCredentials(string user, string pass)
    {
        if (user.Length == 0 || pass.Length < 4) throw new Exception("用户名不能为空，密码至少 4 位。");
        string? err;
        try { err = await Task.Run(() => CredentialReset.Apply(user, pass)); }
        catch (Exception e) { err = e.Message; }
        if (err != null) throw new Exception(err);

        _cfg.WebUser = user;
        _cfg.SetPass(pass);
        _cfg.Save();
        var failures = new List<string>();
        foreach (var id in new[] { "console", "game" })
        {
            var re = await GuardPipe.RestartApollo(id);
            if (re != null) failures.Add((id == "console" ? "当前会话：" : "游戏会话：") + re);
        }
        return failures.Count == 0
            ? "凭证已应用，Apollo 正在重启。"
            : "凭证已写入，但重启失败：" + string.Join("；", failures);
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "trayapp-ui.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
        }
        catch { }
    }
}
