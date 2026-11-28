using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SunshineDuoSetup;

// 执行阶段日志与步骤上报（后台线程写入，UI 线程定时器泵出）
internal interface IRunReporter
{
    void Log(string message);
    void StepStart(string key);
    void StepDone(string key, bool ok);
}

// 一个可执行步骤
internal sealed record OpsStep(string Key, string Label, Action Action);

// 进程执行工具
internal static class Proc
{
    public static (int ExitCode, string Output, string Error) Run(string exe, string args, int timeoutMs = 120_000, string? workDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir ?? Paths.Root,
        };
        using var p = Process.Start(psi) ?? throw new Exception($"无法启动 {exe}");
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"{exe} 执行超时（{timeoutMs / 1000} 秒）");
        }
        return (p.ExitCode, so.Result.TrimEnd(), se.Result.TrimEnd());
    }
}

// 安装/卸载操作主体（从 scripts\sunshine-setup.ps1 移植）
internal static class Ops
{
    /// <summary>安装结束后置位：虚拟显卡装好但需重启电脑才生效（结果页据此弹窗询问是否重启）。</summary>
    public static bool VdaRestartPending { get; private set; }

    // ------------------------------------------------------------------
    // 步骤工厂：安装（跳过已安装项）
    // ------------------------------------------------------------------
    public static List<OpsStep> InstallSteps(Opts o, IRunReporter log)
    {
        VdaRestartPending = false; // 每次安装运行重新累积，避免上一次运行的残留触发误弹窗
        var steps = new List<OpsStep>();
        var skip = new List<string>();

        void Add(bool enabled, string key, string label, Action body, Func<Detect.State>? detect = null)
        {
            if (!enabled) return;
            if (detect is not null)
            {
                var d = detect();
                if (d.Installed)
                {
                    skip.Add($"{label}（{d.Detail}）");
                    return;
                }
            }
            steps.Add(new OpsStep(key, label, body));
        }

        Add(true, "conf", "写入 串流配置", () => DoConf(o, log));
        if (o.Reset) steps.Add(new OpsStep("reset", "清除已配对设备", () =>
        {
            StopStreamingProcesses(log);
            DoResetState(log);
        }));
        if (!o.SingleSession && o.GpuAccel) Add(true, "gpuaccel", "画面渲染优化",
            () => DoGpuAccel(log), Detect.GpuPolicy);
        Add(true, "firewall", "配置网络访问", () => DoFirewall(log), Detect.FirewallApollo);
        if (o.Gamepad) Add(true, "gamepad", "游戏手柄支持", () => DoGamepad(log), Detect.ViGEmBus);
        if (!o.SingleSession) Add(true, "rdpwrap", "多会话登录支持", () => DoRdpWrapper(log), Detect.RdpWrapper);
        steps.Add(new OpsStep("core", "会话守护服务与自动启动", () => DoCore(o, log)));
        steps.Add(new OpsStep("vda", "虚拟显示器驱动", () => DoSudoVda(log)));
        if (o.GameManager) Add(true, "gamemgr", "游戏启动器", () => DoGameManager(log), Detect.GameManager);
        if (o.TrayAny) steps.Add(new OpsStep("tray", "托盘与快捷方式", () => DoTray(o, log)));

        if (skip.Count > 0)
            foreach (var s in skip) log.Log("跳过: " + s);
        return steps;
    }

    public static List<OpsStep> UninstallSteps(Opts o, IRunReporter log)
    {
        var steps = new List<OpsStep>();
        void Add(bool on, string key, string label, Action body)
        {
            if (on) steps.Add(new OpsStep(key, label, body));
        }
        Add(o.UnFirewall, "firewall", "移除防火墙放行规则", () => UnFirewall(log));
        Add(o.UnGamepad, "gamepad", "移除游戏手柄支持", () => UnGamepad(log));
        Add(o.UnRdpWrap, "rdpwrap", "移除多会话登录支持", () => UnRdpWrapper(log));
        Add(o.UnGpuAccel, "gpuaccel", "删除画面渲染优化", () => UnGpuAccel(log));
        Add(o.UnCore, "core", "移除会话守护服务", () =>
        {
            UnApolloGuard(log);
            UnCoreService(log);
        });
        Add(o.UnVda, "vda", "卸载虚拟显示器驱动", () => SudoVda.Uninstall(log.Log, CancellationToken.None));
        Add(o.UnGameManager, "gamemgr", "移除游戏启动器", () => UnGameManager(log));
        Add(o.UnTray, "trayapp", "卸载托盘与快捷方式", () => UnTray(log));
        return steps;
    }

    // ------------------------------------------------------------------
    // 配置
    // ------------------------------------------------------------------
    public static void DoConf(Opts o, IRunReporter log)
    {
        if (string.IsNullOrWhiteSpace(o.Adapter)) throw new Exception("未选择显卡（adapter_name）");
        Directory.CreateDirectory(Paths.ConfigDir);
        var lines = new List<string>
        {
            $"adapter_name = {o.Adapter}",
            "address_family = both",
            "gamepad = x360",
            "install_steam_audio_drivers = disabled",
            "keep_sink_default = disabled",
            "server_cmd = [{\"cmd\":\"bubbles.scr\",\"elevated\":false,\"name\":\"Bubbles\"}]",
            "upnp = enabled",
            "locale = zh",
            "sunshine_name = 主会话",
            $"file_state = {Paths.ConsoleStatePath}",
        };
        if (o.PortMain > 0) lines.Add($"port = {o.PortMain}");
        lines.Add($"log_path = {Path.Combine(Paths.ConfigDir, "sunshine-console.log")}");
        WriteAllLines(Paths.ConfPath, lines);
        var port = o.PortMain > 0 ? $", port = {o.PortMain}" : "";
        log.Log($"OK: 串流配置已写入（图形设备 {o.Adapter}{port}，镜像本机画面）");
        SplitLegacyState(log);
    }

    private static void SplitLegacyState(IRunReporter log)
    {
        if (!File.Exists(Paths.LegacyStatePath)) return;
        var json = File.ReadAllText(Paths.LegacyStatePath);
        foreach (var target in new[] { Paths.ConsoleStatePath, Paths.GameStatePath })
        {
            if (File.Exists(target)) continue;
            File.WriteAllText(target, json, Utf8NoBomEnc);
            log.Log("OK: 已把旧配对数据迁移到 " + Path.GetFileName(target));
        }
    }

    public static void DoGpuAccel(IRunReporter log)
    {
        const string key = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
        using (var k = Registry.LocalMachine.CreateSubKey(key))
        {
            foreach (var v in new[] { "bEnumerateHWBeforeSW", "bAVC444Mode", "AVCHardwareEncodePreferred" })
                k.SetValue(v, 1, RegistryValueKind.DWord);
        }
        log.Log("OK: 画面渲染优化已启用（已连接的远程会话需断开重连或重启后生效）");
    }

    public static void UnGpuAccel(IRunReporter log)
    {
        const string key = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
        using (var k = Registry.LocalMachine.OpenSubKey(key, writable: true))
        {
            if (k is null) { log.Log("跳过: 画面渲染策略不存在"); return; }
            foreach (var v in new[] { "bEnumerateHWBeforeSW", "bAVC444Mode", "AVCHardwareEncodePreferred" })
                k.DeleteValue(v, throwOnMissingValue: false);
        }
        log.Log("OK: 画面渲染优化已移除（恢复系统默认渲染策略）");
    }

    public static void DoFirewall(IRunReporter log)
    {
        var bat = Path.Combine(Paths.ScriptsDir, "add-firewall-rule.bat");
        if (!File.Exists(bat)) throw new FileNotFoundException("缺少网络访问配置组件，请确认程序目录完整");
        log.Log("正在配置网络访问规则...");
        Proc.Run("cmd.exe", "/c \"\"" + bat + "\"\"");
        var count = CountFirewallRules();
        if (count >= 1) log.Log($"OK: 网络访问规则已配置（{count} 条）");
        else log.Log("警告: 网络访问规则未生效，请重新运行本工具");
    }

    public static void UnFirewall(IRunReporter log)
    {
        var bat = Path.Combine(Paths.ScriptsDir, "delete-firewall-rule.bat");
        if (!File.Exists(bat)) throw new FileNotFoundException("缺少网络访问配置组件，请确认程序目录完整");
        log.Log("正在移除网络访问规则...");
        Proc.Run("cmd.exe", "/c \"\"" + bat + "\"\"");
        var count = CountFirewallRules();
        log.Log(count == 0 ? "OK: 网络访问规则已移除" : $"警告: 网络访问规则未完全移除，请重启后重试");
    }

    private static int CountFirewallRules()
    {
        const string rulesKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
        var count = 0;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(rulesKey);
            if (k is null) return 0;
            foreach (var name in k.GetValueNames())
            {
                if (k.GetValue(name) is string s && s.Contains("Apollo")) count++;
            }
        }
        catch { }
        return count;
    }

    public static void DoGamepad(IRunReporter log)
    {
        var ps1 = Path.Combine(Paths.ScriptsDir, "install-gamepad.ps1");
        if (!File.Exists(ps1)) throw new FileNotFoundException("缺少游戏手柄组件，请确认程序目录完整");
        var sys = Paths.ViGEmBusSys;
        var had = File.Exists(sys);
        log.Log("正在安装游戏手柄支持（如有重启提示请稍后重启）...");
        RunHiddenWait("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"", 120_000);
        for (var i = 0; i < 300; i++)
        {
            if (!had && File.Exists(sys)) break;
            var busy = Process.GetProcessesByName("vigembus_installer").Length
                     + Process.GetProcessesByName("ViGEmBus_installer").Length;
            if (had && busy == 0) break;
            Thread.Sleep(1000);
        }
        if (File.Exists(sys))
        {
            var ver = "";
            try { ver = FileVersionInfo.GetVersionInfo(sys).FileVersion ?? ""; } catch { }
            log.Log(ver.Length > 0 ? $"OK: 游戏手柄支持已就绪" : "OK: 游戏手柄支持已就绪");
        }
        else log.Log("警告: 游戏手柄支持未就绪，重启电脑后可重试本工具确认");
    }

    public static void UnGamepad(IRunReporter log)
    {
        var ps1 = Path.Combine(Paths.ScriptsDir, "uninstall-gamepad.ps1");
        if (!File.Exists(ps1)) throw new FileNotFoundException("缺少游戏手柄组件，请确认程序目录完整");
        log.Log("正在移除游戏手柄支持（可能需要几分钟）...");
        RunHiddenWait("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"", 300_000);
        var sys = Paths.ViGEmBusSys;
        log.Log(File.Exists(sys)
            ? "警告: 需重启电脑后彻底移除"
            : "OK: 游戏手柄支持已移除");
    }

    public static void DoRdpWrapper(IRunReporter log)
    {
        if (!File.Exists(Paths.RdpWrapExe)) throw new FileNotFoundException("缺少多会话登录组件，请确认程序目录完整");
        log.Log("执行 启用多会话登录...");
        var p = StartHidden(Paths.RdpWrapExe, "-install");
        if (!p.WaitForExit(180_000))
        {
            try { p.Kill(); } catch { }
            throw new Exception("多会话组件安装超时，请重试");
        }
        if (p.ExitCode != 0)
            throw new Exception($"多会话组件安装未完成，请重试（返回码 {p.ExitCode}）");
        log.Log(Directory.Exists(Paths.RdpWrapDir)
            ? $"OK: 多会话登录支持已启用"
            : $"警告: 多会话登录支持未生效，请重启电脑后重试");
    }

    public static void UnRdpWrapper(IRunReporter log)
    {
        if (!File.Exists(Paths.RdpWrapExe)) throw new FileNotFoundException("缺少多会话登录组件，请确认程序目录完整");
        if (!Directory.Exists(Paths.RdpWrapDir)) { log.Log($"跳过: 多会话登录支持未安装"); return; }
        log.Log("执行 移除多会话登录支持 ...");
        log.Log("提示: 移除时远程连接可能短暂中断");
        var p = StartHidden(Paths.RdpWrapExe, "-uninstall");
        if (!p.WaitForExit(180_000))
        {
            try { p.Kill(); } catch { }
            throw new Exception("移除多会话登录超时，请重试");
        }
        if (Directory.Exists(Paths.RdpWrapDir))
            throw new Exception($"移除未完成，请重启电脑后重试");
        log.Log("OK: 多会话登录支持已移除");
    }

    // ------------------------------------------------------------------
    // SudoVDA / 虚拟显卡
    // ------------------------------------------------------------------
    public static void DoSudoVda(IRunReporter log)
    {
        var d = SudoVda.Query();
        if (d.State is SudoVda.DevState.Started or SudoVda.DevState.NeedRestart)
        {
            VdaRestartPending |= d.State == SudoVda.DevState.NeedRestart;
            log.Log($"跳过: 虚拟显示器驱动已存在（{d.State}），如需重装请先卸载");
            return;
        }
        log.Log("正在安装虚拟显示器驱动...");
        VdaRestartPending |= SudoVda.Install(log.Log, CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // SunshineDuoCore
    // ------------------------------------------------------------------
    public static void DoCore(Opts o, IRunReporter log)
    {
        if (!File.Exists(Paths.CoreExe)) throw new FileNotFoundException("缺少会话守护组件，请确认程序目录完整");

        StopStreamingProcesses(log);

        if (o.GameEnabled && o.PortGame > 0)
        {
            WriteGameConf(o, log);
        }
        SetWebUiCredentials(o, log);
        EnsureDistinctUniqueIds(log);
        InstallCoreService(o, log);
        SendCoreTick(log);
        WaitApolloWeb(o, log);
        InstallApolloGuard(o, log);
    }

    private static void WriteGameConf(Opts o, IRunReporter log)
    {
        if (!File.Exists(Paths.ConfPath)) throw new FileNotFoundException("串流配置缺失，请重新安装");
        var lines = File.ReadAllLines(Paths.ConfPath)
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*log_path\s*="))
            .ToList();
        lines.Add($"log_path = {Path.Combine(Paths.ConfigDir, "sunshine-game.log")}");
        if (o.PortGame > 0)
        {
            lines = lines.Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*port\s*=")).ToList();
            lines.Add($"port = {o.PortGame}");
        }
        lines = lines.Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l,
            @"^\s*(dd_\w+|always_use_virtual_display|isolated_virtual_display_option|output_name_windows|sunshine_name|file_state)\s*=")).ToList();
        lines.Add("sunshine_name = 游戏会话");
        lines.Add($"file_state = {Paths.GameStatePath}");
        WriteAllLines(Paths.GameConfPath, lines);
        log.Log($"OK: 游戏会话配置文件已生成 [{Paths.GameConfPath}]，端口 {o.PortGame}（独立配置，共享应用与证书）");
    }

    private static void SetWebUiCredentials(Opts o, IRunReporter log)
    {
        if (!File.Exists(Paths.SunshineExe)) throw new FileNotFoundException("缺少串流主程序，请确认程序目录完整");
        var confs = new List<string> { Paths.ConfPath };
        if (o.GameEnabled && o.PortGame > 0) confs.Add(Paths.GameConfPath);
        foreach (var conf in confs)
        {
            if (!File.Exists(conf)) throw new FileNotFoundException("配置文件缺失，无法设置网页控制台账号");
            var psi = new ProcessStartInfo
            {
                FileName = Paths.SunshineExe,
                Arguments = $"\"{conf}\" --creds \"{o.WebUser}\" \"{o.WebUiPassword}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi) ?? throw new Exception("网页控制台账号设置失败");
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new Exception($"网页控制台账号设置失败（返回码 {p.ExitCode}），请手动在网页控制台设置用户名和密码");
        }
        log.Log("OK: 网页控制台账号已设置" + (o.GameEnabled && o.PortGame > 0 ? "与游戏会话" : "") + "各存一份，用户名/密码相同）");
        var addr = "提示: 网页控制台 https://localhost:" + (o.PortMain + 1) + "（主画面）";
        if (o.GameEnabled && o.PortGame > 0) addr += " / https://localhost:" + (o.PortGame + 1) + "（游戏画面）";
        log.Log(addr + "，用户名 " + o.WebUser);
    }

    private static void EnsureDistinctUniqueIds(IRunReporter log)
    {
        foreach (var (name, path) in new[] { ("控制台", Paths.ConsoleStatePath), ("游戏", Paths.GameStatePath) })
        {
            if (!File.Exists(path)) continue;
            var node = JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject();
            var root = node["root"] as JsonObject ?? new JsonObject();
            node["root"] = root;
            var uid = (string?)root["uniqueid"] ?? "";
            if (uid.Length == 0 || !Guid.TryParse(uid, out _))
            {
                uid = Guid.NewGuid().ToString();
                root["uniqueid"] = uid;
                File.WriteAllText(path, node.ToJsonString(JsonOpts), Utf8NoBomEnc);
                log.Log($"OK: {name}主机身份已生成（设备标识 {uid}）");
            }
            else log.Log($"OK: {name}主机身份（设备标识 {uid}）");
        }
        // 两份 state 若身份相同则给游戏换新（Moonlight 才能同时显示两个主机）
        if (File.Exists(Paths.ConsoleStatePath) && File.Exists(Paths.GameStatePath))
        {
            var c = JsonNode.Parse(File.ReadAllText(Paths.ConsoleStatePath));
            var g = JsonNode.Parse(File.ReadAllText(Paths.GameStatePath));
            var cu = (string?)c?["root"]?["uniqueid"];
            var gu = (string?)g?["root"]?["uniqueid"];
            if (cu is not null && cu == gu && g is not null)
            {
                var nu = Guid.NewGuid().ToString();
                g["root"]!["uniqueid"] = nu;
                File.WriteAllText(Paths.GameStatePath, g.ToJsonString(JsonOpts), Utf8NoBomEnc);
                log.Log($"OK: 游戏主机身份与控制台重复，已换成 {nu}");
            }
        }
    }

    private static void InstallCoreService(Opts o, IRunReporter log)
    {
        // rdp.cred
        if (!string.IsNullOrEmpty(o.Password))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(o.Password);
            var enc = System.Security.Cryptography.ProtectedData.Protect(bytes, null,
                System.Security.Cryptography.DataProtectionScope.LocalMachine);
            File.WriteAllBytes(Paths.CoreCredPath, enc);
            Proc.Run("icacls.exe", $"\"{Paths.CoreCredPath}\" /inheritance:r /grant \"SYSTEM:F\" /grant \"Administrators:F\"");
            log.Log($"OK: 登录凭据已加密保存（仅系统账户可读取）");
        }
        else if (File.Exists(Paths.CoreCredPath))
        {
            log.Log("密码留空: 沿用此前保存的登录凭据");
        }
        else
        {
            // 单会话不持续依赖远程游戏账号密码，允许留空继续安装（无需管理员授权启动游戏）
            if (!o.GameEnabled)
            {
                log.Log("未提供密码且无已保存凭据（单会话）：跳过凭据写入，继续安装");
            }
            else throw new Exception("未提供密码且无已保存凭据，无法继续");
        }

        // config.json
        // guard_uu：GameViewer hook 守护开关——仅当本机装了网易 GameViewerService 服务时开启
        //（SCM 只读查询，不发控制码、不停启该外部服务）；未装则写 false，避免 PS 守护空转报错。
        var gvInstalled = GvServiceInstalled();
        if (File.Exists(Paths.CoreCfgPath))
        {
            var cfg = JsonNode.Parse(File.ReadAllText(Paths.CoreCfgPath)) as JsonObject ?? new JsonObject();
            cfg["guard_uu"] = gvInstalled;
            cfg["guard_apollo"] = o.GameEnabled;
            cfg["guard_apollo_console"] = true;
            cfg["auto_logon"] = o.AutoLogon;
            cfg["default_display_mode"] = "mirror";
            cfg["display_exclusive"] = false;
            cfg["console_port"] = o.PortMain;
            cfg["game_port"] = o.PortGame;
            if (o.SudovdaResolutions.Count > 0)
                cfg["sudovda_resolutions"] = new JsonArray(o.SudovdaResolutions.Select(r => JsonValue.Create(r)).ToArray());
            if (o.Vw >= 320 && o.Vh >= 240)
            {
                cfg["width"] = o.Vw;
                cfg["height"] = o.Vh;
            }
            File.WriteAllText(Paths.CoreCfgPath, cfg.ToJsonString(JsonOpts), Utf8NoBomEnc);
            log.Log($"OK: 会话配置已同步（GameViewer 守护: {(gvInstalled ? "已启用" : "未装服务，已关闭")}）");
        }
        else
        {
            var w = o.Vw >= 320 ? o.Vw : 1600;
            var h = o.Vh >= 240 ? o.Vh : 720;
            var cfg = new JsonObject
            {
                ["username"] = o.User,
                ["password_mode"] = "dpapi",
                ["cred_path"] = Paths.CoreCredPath,
                ["server"] = "127.0.0.2",
                ["width"] = w,
                ["height"] = h,
                ["keepalive"] = true,
                ["guard_uu"] = gvInstalled,
                ["guard_apollo"] = o.GameEnabled,
                ["display_keepalive"] = true,
                ["console_port"] = o.PortMain,
                ["game_port"] = o.PortGame,
                ["default_display_mode"] = "mirror",
                ["display_exclusive"] = false,
                ["auto_logon"] = o.AutoLogon,
                ["log_level"] = "warn",
                ["interval"] = 60,
                ["recycle_minutes"] = 30,
                ["gameviewer_versions"] = new JsonArray("4.0.*", "4.46.*"),
            };
            if (o.SudovdaResolutions.Count > 0)
                cfg["sudovda_resolutions"] = new JsonArray(o.SudovdaResolutions.Select(r => JsonValue.Create(r)).ToArray());
            File.WriteAllText(Paths.CoreCfgPath, cfg.ToJsonString(JsonOpts), Utf8NoBomEnc);
            log.Log($"OK: 会话配置已生成（{w}x{h}，30 分钟重建，开机自动拉起，{(o.AutoLogon ? "开机自动登录开" : "开机自动登录关")}，GameViewer 守护: {(gvInstalled ? "已启用" : "未装服务，已关闭")}）");
        }

        // 服务幂等注册
        foreach (var svc in new[] { Paths.SvcName, Paths.LegacySvc })
        {
            if (Services.Query(svc) is null) continue;
            log.Log($"正在更新已有服务...");
            Proc.Run("sc.exe", $"stop {svc}");
            Thread.Sleep(2000);
            KillProc("SunshineDuoCore");
            Proc.Run("sc.exe", $"delete {svc}");
            Thread.Sleep(1000);
        }
        var r = Proc.Run("sc.exe", $"create {Paths.SvcName} binPath= \"{Paths.CoreExe}\" start= auto DisplayName= \"{Paths.SvcDispName}\"");
        if (r.ExitCode != 0) throw new Exception($"服务创建失败（错误码 {r.ExitCode}）");
        Proc.Run("sc.exe", $"description {Paths.SvcName} \"Keeps the streaming user RDP session alive (headless client) and GameViewer pinned to the console session.\"");
        Proc.Run("sc.exe", $"failure {Paths.SvcName} reset= 86400 actions= restart/60000/restart/60000/restart/60000");
        var startedAt = DateTime.Now;
        Proc.Run("sc.exe", $"start {Paths.SvcName}");
        // 启动是异步的：sc start 返回≠服务已 Running。SCM 先报 START_PENDING，
        // 服务初始化（SCM dispatcher + keepalive 线程）完成后才转 RUNNING。
        // 此前固定睡 3s 后单次查询，机器慢/杀软扫描启动镜像时极易误判失败。
        // 改为有界轮询：StartPending/StopPending 属过渡态继续等，Running 即成功，超时报最终状态。
        var svcRunning = Services.ConfirmRunning(Paths.SvcName, timeoutMs: 30_000, out var diag);
        if (!svcRunning)
            throw new Exception($"会话守护服务未能进入运行状态（{diag}），请重启电脑后重试");
        log.Log($"OK: 会话守护服务运行中（系统账户 / 自动启动）");

        // 回显本次启动后的 watch.log 新增行
        if (File.Exists(Paths.CoreWatchLog))
        {
            var fresh = File.ReadAllLines(Paths.CoreWatchLog)
                .Where(l => DateTime.TryParse(l.Length >= 19 ? l[..19] : "", out var ts) && ts >= startedAt)
                .TakeLast(6);
            foreach (var l in fresh) log.Log("  [状态] " + l);
        }
    }

    public static void InstallApolloGuard(Opts o, IRunReporter log)
    {
        if (!File.Exists(Paths.SunshineExe)) throw new FileNotFoundException($"缺少必需组件，请确认程序目录完整");
        if (!File.Exists(Paths.CoreCfgPath)) throw new FileNotFoundException("未找到 " + Paths.CoreCfgPath + "（会话守护服务未安装？）");
        var cfg = JsonNode.Parse(File.ReadAllText(Paths.CoreCfgPath)) as JsonObject ?? new JsonObject();
        cfg["guard_apollo"] = o.GameEnabled;
        cfg["guard_apollo_console"] = true;
        File.WriteAllText(Paths.CoreCfgPath, cfg.ToJsonString(JsonOpts), Utf8NoBomEnc);
        log.Log($"OK: 开机自动拉起={(o.GameEnabled ? "已启用" : "已关闭（单会话模式）")}");
        CleanLegacyAutostart(log, "旧");
        var game = o.GameEnabled ? "（含游戏画面）" : "（单会话模式）";
        log.Log($"提示: {o.User} 登录 {o.User} 后会自动恢复串流画面{game}");
    }

    private static void CleanLegacyAutostart(IRunReporter log, string prefix)
    {
        var any = false;
        foreach (var sid in UserSids())
        {
            using var run = Registry.Users.OpenSubKey($@"{sid}\Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue(Paths.LegacyRunValue) is not null)
            {
                run.DeleteValue(Paths.LegacyRunValue, false);
                log.Log($"OK: 已清理用户 [{sid}] 的{prefix}开机启动项");
                any = true;
            }
        }
        foreach (var task in new[] { Paths.LegacyTaskName, Paths.LegacyTaskName2 })
        {
            if (SchTaskExists(task))
            {
                Proc.Run("schtasks.exe", $"/Delete /TN \"{task}\" /F");
                log.Log($"OK: 已清理{prefix}自动任务");
                any = true;
            }
        }
        if (!any) log.Log("提示: 未发现旧版自启残留");
    }

    public static void UnApolloGuard(IRunReporter log)
    {
        if (File.Exists(Paths.CoreCfgPath))
        {
            try
            {
                var cfg = JsonNode.Parse(File.ReadAllText(Paths.CoreCfgPath)) as JsonObject ?? new JsonObject();
                cfg["guard_apollo"] = false;
                File.WriteAllText(Paths.CoreCfgPath, cfg.ToJsonString(JsonOpts), Utf8NoBomEnc);
                log.Log("OK: 开机自动拉起已关闭");
            }
            catch (Exception ex) { log.Log("警告: 无法更新会话配置: " + ex.Message); }
        }
        CleanLegacyAutostart(log, "");
    }

    public static void UnCoreService(IRunReporter log)
    {
        var any = false;
        foreach (var n in new[] { Paths.SvcName, Paths.LegacySvc })
        {
            if (Services.Query(n) is null) continue;
            Proc.Run("sc.exe", $"stop {n}");
            for (var i = 0; i < 10; i++)
            {
                if (Process.GetProcessesByName("SunshineDuoCore").Length == 0) break;
                Thread.Sleep(1000);
            }
            KillProc("SunshineDuoCore");
            Proc.Run("sc.exe", $"delete {n}");
            log.Log(Services.Query(n) is null
                ? $"OK: 服务 {n} 已移除（程序文件与凭据保留）"
                : $"警告: 服务移除未完成，请重启电脑后重试");
            any = true;
        }
        if (!any) log.Log("跳过: 会话守护服务不存在");
        // 服务外仍有守护/串流残留（服务已删但进程未退、多会话实例）：无条件清掉
        StopStreamingProcesses(log);
        foreach (var n in new[] { Paths.SvcName, Paths.LegacySvc })
            if (Services.Query(n) is not null)
                log.Log($"警告: 服务 {n} 仍注册，请重启电脑后重试");
    }

    // ------------------------------------------------------------------
    // 进程清场 / tick / 端口等待
    // ------------------------------------------------------------------
    public static void StopStreamingProcesses(IRunReporter log)
    {
        foreach (var n in new[] { Paths.SvcName, Paths.LegacySvc })
            if (Services.Query(n) is not null)
                Proc.Run("sc.exe", $"stop {n}");
        Thread.Sleep(1000);
        var killed = new List<string>();
        foreach (var n in new[] { "SunshineDuoCore", "sunshine", "SunshineDuoTray", "easytier-core" })
        {
            var ps = Process.GetProcessesByName(n);
            if (ps.Length > 0)
            {
                foreach (var p in ps) { try { p.Kill(true); } catch { } p.Dispose(); }
                killed.Add($"{n switch
                {
                    "SunshineDuoCore" => "会话守护",
                    "sunshine" => "串流主程序",
                    "SunshineDuoTray" => "托盘程序",
                    "easytier-core" => "内网穿透",
                    _ => n,
                }} x{ps.Length}");
            }
        }
        if (killed.Count > 0)
        {
            Thread.Sleep(2000);
            log.Log("OK: 已停止运行中的相关程序（" + string.Join("、", killed) + "）");
        }
        else log.Log("提示: 未发现运行中的相关程序");
    }

    public static void SendCoreTick(IRunReporter log, int timeoutSec = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "SunshineDuoCore-ctrl", PipeDirection.InOut);
                pipe.Connect(2000);
                using var writer = new StreamWriter(pipe) { NewLine = "\n", AutoFlush = true };
                writer.WriteLine("{\"cmd\":\"tick\"}");
                using var reader = new StreamReader(pipe);
                var resp = reader.ReadLine() ?? "";
                log.Log(resp.Contains("\"ok\"") && resp.Contains("true")
                    ? "OK: 已通知会话守护服务立即恢复画面"
                    : $"警告: 会话守护服务响应异常");
                return;
            }
            catch { Thread.Sleep(2000); }
        }
        log.Log("警告: 会话守护服务尚未就绪，稍后会自动恢复画面");
    }

    public static void WaitApolloWeb(Opts o, IRunReporter log, int timeoutSec = 90)
    {
        var targets = new List<(string Name, int Port)> { ("控制台", o.PortMain + 1) };
        if (o.GameEnabled && o.PortGame > 0) targets.Add(("游戏", o.PortGame + 1));
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        var up = new HashSet<string>();
        while (DateTime.UtcNow < deadline)
        {
            foreach (var (name, port) in targets)
            {
                if (up.Contains(name)) continue;
                try
                {
                    using var c = new System.Net.Sockets.TcpClient();
                    if (c.ConnectAsync("127.0.0.1", port).Wait(1200) && c.Connected)
                    {
                        up.Add(name);
                        log.Log($"OK: {name}画面已就绪（控制台 https://localhost:{port}）");
                    }
                }
                catch { }
            }
            if (up.Count >= targets.Count) return;
            Thread.Sleep(2000);
        }
        foreach (var (name, port) in targets.Where(t => !up.Contains(t.Name)))
            log.Log($"警告: {name}画面控制台未就绪（稍后会自动恢复）");
    }

    // ------------------------------------------------------------------
    // GameManager
    // ------------------------------------------------------------------
    public static void DoGameManager(IRunReporter log)
    {
        if (!File.Exists(Paths.GameManagerExe)) throw new FileNotFoundException("缺少游戏启动器组件，请确认程序目录完整");
        if (Services.Query(Paths.GmSvcName) is null)
        {
            log.Log("执行 安装游戏启动器服务...");
            StartHidden(Paths.GameManagerExe, "--install-service");
            var started = false;
            for (var i = 0; i < 30; i++)
            {
                Thread.Sleep(1000);
                if (Services.Query(Paths.GmSvcName)?.State == "Running") { started = true; break; }
            }
            if (!started) throw new Exception("游戏启动器未能启动，请重试（请查看安装日志");
            log.Log($"OK: 游戏启动器服务运行中");
        }
        else log.Log($"跳过: 游戏启动器服务已存在");

        log.Log("执行 注册游戏启动权限...");
        RunHiddenWait(Paths.GameManagerExe, "--register-all", 60_000);
        log.Log(Registry.ClassesRoot.OpenSubKey("steam") is not null
            ? "OK: 游戏启动权限已注册"
            : "警告: 未检测到游戏启动权限");

        try
        {
            var json = File.Exists(Paths.GameManagerJson)
                ? JsonNode.Parse(File.ReadAllText(Paths.GameManagerJson)) as JsonObject ?? new JsonObject()
                : new JsonObject();
            json["LinksFolder"] = Paths.LinksDir;
            if (!json.ContainsKey("StartFullScreen")) json["StartFullScreen"] = true;
            File.WriteAllText(Paths.GameManagerJson, json.ToJsonString(), Utf8NoBomEnc);
            Directory.CreateDirectory(Paths.LinksDir);
            log.Log($"OK: 游戏菜单目录已设为 {Paths.LinksDir}（把游戏快捷方式放进去）");
        }
        catch (Exception ex) { log.Log("警告: 保存游戏菜单配置失败: " + ex.Message); }
    }

    public static void UnGameManager(IRunReporter log)
    {
        if (!File.Exists(Paths.GameManagerExe)) throw new FileNotFoundException("缺少游戏启动器组件，请确认程序目录完整");
        if (Services.Query(Paths.GmSvcName) is not null)
        {
            log.Log("执行 移除游戏启动器服务 ...");
            StartHidden(Paths.GameManagerExe, "--uninstall-service");
            var gone = false;
            for (var i = 0; i < 30; i++)
            {
                Thread.Sleep(1000);
                if (Services.Query(Paths.GmSvcName) is null) { gone = true; break; }
            }
            log.Log(gone ? $"OK: 游戏启动器服务已移除" : $"警告: 游戏启动器服务仍存在，请手动移除");
        }
        else log.Log($"跳过: 未安装游戏启动器服务");
        log.Log("正在取消游戏启动权限 ...");
        RunHiddenWait(Paths.GameManagerExe, "--unregister-all", 60_000);
        KillProc("GameManager");
        log.Log(Registry.ClassesRoot.OpenSubKey("steam") is not null
            ? "警告: 游戏启动权限未完全移除"
            : "OK: 游戏启动权限已移除");
    }

    // ------------------------------------------------------------------
    // 托盘
    // ------------------------------------------------------------------
    public static void DoTray(Opts o, IRunReporter log)
    {
        var deployDir = Paths.TrayDir;
        var deployExe = Paths.TrayExe;
        var src = FindTrayAppExe();
        if (src is null)
        {
            log.Log("警告: 未找到托盘组件，已跳过托盘部署");
            return;
        }
        KillProc("SunshineDuoTray");
        Directory.CreateDirectory(deployDir);
        if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(deployExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(src, deployExe, overwrite: true);
        log.Log($"OK: 托盘程序已部署");

        if (o.PortMain > 0)
        {
            try
            {
                var json = File.Exists(Paths.TrayJson)
                    ? JsonNode.Parse(File.ReadAllText(Paths.TrayJson)) as JsonObject ?? new JsonObject()
                    : new JsonObject();
                json["ConsolePort"] = o.PortMain;
                json["GamePort"] = o.GameEnabled ? o.PortGame : 0;
                if (!string.IsNullOrEmpty(o.WebUser)) json["WebUser"] = o.WebUser;
                if (!json.ContainsKey("WebPassEnc")) json["WebPassEnc"] = "";
                if (!json.ContainsKey("AutoStart")) json["AutoStart"] = o.TrayAuto;
                File.WriteAllText(Paths.TrayJson, json.ToJsonString(), Utf8NoBomEnc);
                log.Log($"OK: 托盘配置已同步");
            }
            catch (Exception ex) { log.Log("警告: 保存托盘配置失败: " + ex.Message); }
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = deployExe, WorkingDirectory = deployDir, UseShellExecute = true });
            log.Log("OK: 托盘程序已启动");
        }
        catch (Exception ex) { log.Log("警告: 托盘启动失败: " + ex.Message); }

        var u = ConsoleUser();
        if (u is null) { log.Log("警告: 未能确定当前登录用户，快捷方式/开机自启已跳过"); return; }
        if (o.TrayDesktop || o.TrayStart)
        {
            foreach (var (on, dir) in new[] { (o.TrayDesktop, u.Value.Desktop), (o.TrayStart, u.Value.StartMenu) })
            {
                if (!on) continue;
                Directory.CreateDirectory(dir);
                var lnk = Path.Combine(dir, "萌远控 Duo.lnk");
                CreateShortcut(lnk, deployExe, deployDir, "萌远控 Duo：会话状态与常用操作");
                log.Log("OK: 已创建快捷方式 " + lnk);
            }
        }
        if (o.TrayAuto)
        {
            try
            {
                using var run = Registry.Users.OpenSubKey($@"{u.Value.Sid}\Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
                    ?? Registry.Users.CreateSubKey($@"{u.Value.Sid}\Software\Microsoft\Windows\CurrentVersion\Run");
                run.SetValue("SunshineDuoTray", "\"" + deployExe + "\"");
                log.Log($"OK: 已配置开机自启（用户 {u.Value.Name}）");
            }
            catch (Exception ex) { log.Log("警告: 开机自启设置失败: " + ex.Message); }
        }
    }

    public static void UnTray(IRunReporter log)
    {
        var any = false;
        KillProc("SunshineDuoTray");
        foreach (var sid in UserSids())
        {
            using var run = Registry.Users.OpenSubKey($@"{sid}\Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue("SunshineDuoTray") is not null)
            {
                run.DeleteValue("SunshineDuoTray", false);
                log.Log($"OK: 已删除用户 hive [{sid}] 的托盘自启键");
                any = true;
            }
            var profile = ProfilePath(sid);
            if (profile is null) continue;
            foreach (var lnk in new[]
            {
                // 现名 + 旧名（老安装升级后也残留旧名，卸载时一并清理）
                Path.Combine(profile, "Desktop", "萌远控 Duo.lnk"),
                Path.Combine(profile, "Desktop", "Sunshine-Duo 托盘.lnk"),
                Path.Combine(profile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs", "萌远控 Duo.lnk"),
                Path.Combine(profile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs", "Sunshine-Duo 托盘.lnk"),
            })
            {
                if (File.Exists(lnk))
                {
                    try { File.Delete(lnk); log.Log("OK: 已删除快捷方式 " + lnk); any = true; } catch { }
                }
            }
        }
        if (File.Exists(Paths.TrayExe))
        {
            try { File.Delete(Paths.TrayExe); log.Log($"OK: 已移除托盘程序（保留配置）"); any = true; } catch { }
        }
        if (!any) log.Log("提示: 未发现托盘残留");
    }

    // ------------------------------------------------------------------
    // 状态清空
    // ------------------------------------------------------------------
    public static void DoResetState(IRunReporter log)
    {
        foreach (var state in new[] { Paths.ConsoleStatePath, Paths.GameStatePath, Paths.LegacyStatePath })
        {
            if (File.Exists(state))
            {
                File.WriteAllText(state, "{}", Utf8NoBomEnc);
                log.Log($"OK: {Path.GetFileName(state)} 已清除（客户端需重新配对）");
            }
            else log.Log($"跳过: {Path.GetFileName(state)} 不存在");
        }
        if (File.Exists(Paths.LogPath)) { File.WriteAllText(Paths.LogPath, ""); log.Log("OK: 运行日志已清空"); }
        if (File.Exists(Paths.LogBakPath)) { File.Delete(Paths.LogBakPath); log.Log("OK: 日志备份已删除"); }
        else log.Log("跳过: 日志备份不存在");
    }

    // ------------------------------------------------------------------
    // 工具函数
    // ------------------------------------------------------------------
    internal static readonly System.Text.Json.JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static readonly System.Text.Encoding Utf8NoBomEnc = new System.Text.UTF8Encoding(false);
    private static void WriteAllLines(string path, List<string> lines)
        => File.WriteAllLines(path, lines, Utf8NoBomEnc);

    private static void RunHiddenWait(string exe, string args, int timeoutMs)
    {
        var p = StartHidden(exe, args);
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException($"{exe} 执行超时");
        }
    }

    private static Process StartHidden(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        return Process.Start(psi) ?? throw new Exception($"无法启动 {exe}");
    }

    public static (int ExitCode, string Output, string Error) ProcRun(string exe, string args, int timeoutMs = 120_000)
        => Proc.Run(exe, args, timeoutMs);

    internal static void KillProc(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { p.Kill(true); } catch { }
            p.Dispose();
        }
    }

    private static string? FindTrayAppExe()
    {
        string[] cands =
        [
            Path.Combine(Paths.Root, "src", "SunshineDuoUI", "bin", "Release", "net9.0-windows", "win-x64", "publish", "SunshineDuoTray.exe"),
            Path.Combine(Paths.Root, "src", "SunshineDuoUI", "SunshineDuoTray.exe"),
            Path.Combine(Paths.Root, "src", "SunshineDuoUI", "publish", "SunshineDuoTray.exe"),
            Paths.TrayExe,
        ];
        return cands.FirstOrDefault(File.Exists);
    }

    internal static IEnumerable<string> UserSids()
    {
        using var users = Registry.Users;
        foreach (var name in users.GetSubKeyNames())
        {
            if (name.EndsWith("_Classes") || !name.StartsWith("S-1-5-21-")) continue;
            yield return name;
        }
    }

    internal static string? ProfilePath(string sid)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            var p = k?.GetValue("ProfileImagePath") as string;
            return p is null ? null : Environment.ExpandEnvironmentVariables(p);
        }
        catch { return null; }
    }

    // WTS 取当前活跃 Console 会话用户（对齐旧版 Win32_ComputerSystem.UserName）
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(nint hServer, int sessionId, int infoClass, out nint ppBuffer, out int pBytesReturned);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint pMemory);
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    public static (string Name, string Sid, string Desktop, string StartMenu)? ConsoleUser()
    {
        string? userName = null;
        var sessionId = (int)WTSGetActiveConsoleSessionId();
        if (WTSQuerySessionInformation(nint.Zero, sessionId, 5 /*WTSUserName*/, out var pName, out _))
        {
            userName = Marshal.PtrToStringUni(pName);
            WTSFreeMemory(pName);
        }
        if (string.IsNullOrEmpty(userName))
            userName = Environment.UserName; // 无活跃 Console 会话时退回当前进程用户
        try
        {
            var account = new NTAccount(userName);
            var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            var profile = ProfilePath(sid.Value) ?? "";
            return (userName, sid.Value,
                Path.Combine(profile, "Desktop"),
                Path.Combine(profile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"));
        }
        catch { return null; }
    }

    public static void CreateShortcut(string lnkPath, string target, string workDir, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new Exception("WScript.Shell COM 不可用");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(lnkPath);
        link.TargetPath = target;
        link.WorkingDirectory = workDir;
        link.IconLocation = target + ",0";
        link.Description = description;
        link.Save();
    }

    // 计划任务存在性（不弹窗）：schtasks /Query 单次查询
    internal static bool SchTaskExists(string taskName)
    {
        try
        {
            var r = Proc.Run("schtasks.exe", $"/Query /TN \"{taskName}\"", 15_000);
            return r.ExitCode == 0;
        }
        catch { return false; }
    }

    // GameViewerService（网易远程助手服务，gv_hook 注入目标）是否已安装。
    // 只做存在性查询（ServiceController 读 SCM 状态），绝不向该外部服务发控制码/停启。
    // 任何异常（权限/服务名解析失败）一律按未安装处理：guard_uu=false 不会误开启 hook 守护。
    public static bool GvServiceInstalled() => Services.Query(Paths.GvSvcName) is not null;
}
