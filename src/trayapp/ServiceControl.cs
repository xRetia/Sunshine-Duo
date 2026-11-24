using System.Diagnostics;

namespace SunshineDuoTray;

// SunshineDuoCore 服务的查询与启停（sc.exe；启停按 AGENTS 铁律走 gsudo64 提权缓存）
public static class ServiceControl
{
    public static string Query()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query SunshineDuoCore")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            if (outp.Contains("RUNNING")) return "运行中";
            if (outp.Contains("STOPPED")) return "已停止";
            if (outp.Contains("START_PENDING")) return "启动中";
            if (outp.Contains("STOP_PENDING")) return "停止中";
            return outp.Contains("1060") ? "未安装" : "未知";
        }
        catch (Exception e)
        {
            return "查询失败: " + e.Message;
        }
    }

    public static Task<string?> SetRunningAsync(bool run) => Task.Run(() => SetRunning(run));

    public static string? SetRunning(bool run) // null=ok
    {
        var gsudo = FindGsudo();
        if (gsudo == null) return "未找到 gsudo64.exe，无法提权启停服务";

        // AGENTS 铁律：先开全局提权缓存（-p 0 任意进程复用，-d -1 直到注销）
        var cache = Run(gsudo, "cache on -p 0 -d -1", 90000);
        if (cache.Timeout) return "开启 gsudo64 缓存超时（可能正在等待 UAC 确认）";

        var arg = run ? "start" : "stop";
        var res = Run(gsudo, $"sc.exe {arg} SunshineDuoCore", 90000);
        if (res.Timeout) return "提权命令超时（可能正在等待 UAC 确认）";
        if (res.ExitCode != 0 || res.Out.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
        {
            var detail = res.Err.Trim().Length > 0 ? res.Err.Trim() : res.Out.Trim();
            return $"gsudo64 sc {arg} 失败（返回码 {res.ExitCode}）{(detail.Length > 0 ? "：" + detail : "")}";
        }
        return null;
    }

    private static string? FindGsudo()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir.Trim().Trim('"'), "gsudo64.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        foreach (var candidate in new[]
                 {
                     @"C:\Komandare\Module\gsudo\gsudo64.exe",
                     @"C:\Program Files\gsudo\Current\gsudo64.exe",
                     @"C:\Program Files\gsudo\gsudo64.exe",
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static (int ExitCode, string Out, string Err, bool Timeout) Run(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return (-1, "", "", true);
            }
            return (p.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult(), false);
        }
        catch (Exception e)
        {
            return (-1, "", e.Message, false);
        }
    }
}