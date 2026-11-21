using System.Diagnostics;

namespace SunshineDuoTray;

// wsguard 服务的查询与启停（sc.exe，启停需 UAC 提权）
public static class ServiceControl
{
    public static string Query()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query WSGuard")
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

    public static string? SetRunning(bool run) // null=ok
    {
        try
        {
            var arg = run ? "start" : "stop";
            var psi = new ProcessStartInfo("sc.exe", $"{arg} WSGuard")
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(30000);
            if (p.ExitCode != 0)
                return $"sc {arg} 返回码 {p.ExitCode}";
            return null;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "已取消 UAC 授权";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}
