using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace SunshineDuoSetup;

// 已安装组件检测：界面打勾展示 + 执行阶段据此跳过
internal static class Detect
{
    public sealed record State(bool Installed, string Detail);

    // 操作系统版本：Win10 1803（build 17134）及以上
    public static State OsVersion()
    {
        var v = Environment.OSVersion.Version;
        var ok = v >= new Version(10, 0, 17134);
        return new State(ok, ok ? $"Windows {v}" : $"过低（{v}，需 10.0.17134/1803 以上）");
    }

    // .NET 9 桌面运行时：安装程序必须存在，缺失则阻断安装
    // 探测系统级、当前用户级安装及 DOTNET_ROOT，避免 Program Files 重定向时漏检。
    public static State DotNetDesktop9()
    {
        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
            // 用户级安装（.NET 安装器默认 %LocalAppData%\Microsoft\dotnet）
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "shared", "Microsoft.WindowsDesktop.App"),
        };
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot)
            roots.Add(Path.Combine(dotnetRoot, "shared", "Microsoft.WindowsDesktop.App"));

        foreach (var dir in roots.Where(r => r.Length > 0 && Directory.Exists(r)))
        {
            foreach (var ver in Directory.GetDirectories(dir))
            {
                if (Path.GetFileName(ver).StartsWith("9.", StringComparison.Ordinal))
                    return new State(true, "已安装 " + Path.GetFileName(ver));
            }
        }

        return new State(false, "未安装（需下载并安装 .NET 9 桌面运行时）");
    }

    // WebView2 运行时：使用官方 API 判断当前系统是否真的可创建运行时，避免把 EdgeUpdate 残留键当成已安装。
    public static State WebView2()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (!string.IsNullOrWhiteSpace(version))
                return new State(true, "已安装 " + version);
        }
        catch { }
        return new State(false, "未安装");
    }


    // ViGEmBus 虚拟手柄驱动：drivers\ViGEmBus.sys 存在即视为已装
    public static State ViGEmBus()
    {
        var sys = Paths.ViGEmBusSys;
        if (!File.Exists(sys)) return new State(false, "未安装");
        var ver = "";
        try { ver = FileVersionInfo.GetVersionInfo(sys).FileVersion ?? ""; } catch { }
        return new State(true, ver.Length > 0 ? "已安装 " + ver : "已安装");
    }

    // RDP Wrapper：默认安装目录存在即视为已装
    public static State RdpWrapper()
    {
        var dir = Paths.RdpWrapDir;
        if (!Directory.Exists(dir)) return new State(false, "未安装");
        return new State(true, "已安装 " + dir);
    }

    // 会话 GPU 加速策略：Terminal Services 三个 DWORD 全为 1
    public static State GpuPolicy()
    {
        const string key = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
        string[] names = ["bEnumerateHWBeforeSW", "bAVC444Mode", "AVCHardwareEncodePreferred"];
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            if (k is null) return new State(false, "未配置");
            var all = names.All(n => k.GetValue(n) is int i && i == 1);
            return new State(all, all ? "已启用" : "未启用");
        }
        catch { return new State(false, "未配置"); }
    }

    // 防火墙规则 Apollo（放行 Sunshine）：防火墙策略注册表扫描（不依赖 NetSecurity 模块）
    public static State FirewallApollo()
    {
        const string rulesKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(rulesKey);
            if (k is null) return new State(false, "无规则");
            var found = 0;
            foreach (var name in k.GetValueNames())
            {
                if (k.GetValue(name) is not string s) continue;
                if (s.Contains("|Apollo|") || s.Contains("Apollo")) found++;
            }
            return found > 0
                ? new State(true, $"已放行（{found} 条规则）")
                : new State(false, "无规则");
        }
        catch { return new State(false, "无规则"); }
    }

    // SudoVDA 虚拟显卡：SudoVdaQuery() 的封装（方法名避开类型名）
    public static State SudoVdaState()
    {
        var d = SudoVda.Query();
        return d.State switch
        {
            SudoVda.DevState.Started => new State(true, "已安装并运行"),
            SudoVda.DevState.NeedRestart => new State(true, "已安装，重启后生效"),
            SudoVda.DevState.Failed => new State(true, "设备存在但驱动异常（代码 " + d.Problem + "）"),
            _ => new State(false, "未安装"),
        };
    }

    // GameManager：服务存在即视为已装
    public static State GameManager()
    {
        var svc = Services.Query(Paths.GmSvcName);
        if (svc is null) return new State(false, "未安装");
        return new State(true, "已安装（" + svc.State + "）");
    }

    // SunshineDuoCore 服务（总览信息用）
    public static State CoreService()
    {
        var svc = Services.Query(Paths.SvcName);
        if (svc is null) return new State(false, "未注册");
        return new State(true, "已注册（" + svc.State + "）");
    }

    // rdp.cred 是否已存在
    public static bool RdpCredExists() => File.Exists(Paths.CoreCredPath);

    // 托盘是否已部署
    public static bool TrayDeployed() => File.Exists(Paths.TrayExe);
}

// 服务查询/注册工具（sc.exe 免进程调用 ServiceBase，直接走advapi32 更稳但代码量大；此处用 ServiceController）
internal static class Services
{
    public sealed record Info(string Name, string State)
    {
        public bool Exists => true;
    }

    public static Info? Query(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return new Info(name, sc.Status.ToString());
        }
        catch { return null; }
    }

    // 有界轮询等待服务进入 Running：StartPending/StopPending 属启动过渡态，继续等；
    // Running 即成功；始终不 Running 返回 false 并把最终状态/诊断写入 diagnostic。
    // 服务不存在/查询失败时立即返回 false（不空等），diagnostic 带异常描述。
    // 与 Updater.ConfirmServiceRunning 同语义（每轮新建控制器读实时状态而非缓存），
    // 安装路径（Ops.InstallCoreService）与更新路径（Updater）共用此判定。
    public static bool ConfirmRunning(string name, int timeoutMs, out string diagnostic)
    {
        diagnostic = "";
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var lastState = "未知";
        while (true)
        {
            try
            {
                using var controller = new ServiceController(name);
                var status = controller.Status;
                lastState = status.ToString();
                if (status == ServiceControllerStatus.Running) return true;
            }
            catch (Exception ex)
            {
                diagnostic = "状态查询失败: " + ex.GetType().Name
                    + (string.IsNullOrWhiteSpace(ex.Message) ? "" : $": {ex.Message}");
                return false;
            }
            if (DateTime.UtcNow >= deadline) break;
            Thread.Sleep(500);
        }
        diagnostic = $"最终状态: {lastState}";
        return false;
    }
}
