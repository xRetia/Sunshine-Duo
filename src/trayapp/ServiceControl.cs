using System.Diagnostics;

namespace SunshineDuoTray;

// SunshineDuoCore 服务的查询与启停（sc.exe 查询；启停经 ShellExecute runas 走标准 UAC 提权）
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
        var arg = run ? "start" : "stop";
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"{arg} SunshineDuoCore")
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(90000))
            {
                try { p.Kill(); } catch { }
                return "服务启停超时（可能正在等待 UAC 确认）";
            }
            // UAC 被取消时 Process.Start 直接抛 Win32Exception，进 catch；
            // 这里 ExitCode 非 0 视为 sc 自身失败。
            if (p.ExitCode != 0)
                return $"sc {arg} 失败（返回码 {p.ExitCode}）";
            return null;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "已取消提权";
        }
        catch (Exception e)
        {
            return $"服务启停失败：{e.Message}";
        }
    }
}
