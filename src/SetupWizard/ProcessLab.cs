using System.Diagnostics;
using System.Management;
using System.ServiceProcess;

namespace SunshineDuoSetup;

// 本地更新前清场练兵：只负责验证停止顺序与 PID 是否真正退出，不执行文件替换或重启。
internal static class ProcessLab
{
    private static readonly string[] ProcessNames =
    [
        "sunshine",
        "easytier-core",
        "SunshineDuoTray",
        "SunshineDuoCore",
        "GameManager",
        Paths.GvSvcName,
    ];

    private static readonly string[] ServiceNames =
    [
        Paths.SvcName,
        Paths.LegacySvc,
        Paths.GvSvcName,
        Paths.GmSvcName,
    ];

    // 拉起阶段要等它们重新出现的进程（easytier/sunshine 由核心服务自动拉起）
    private static readonly string[] ReviveWatchNames =
    [
        "sunshine",
        "easytier-core",
        "SunshineDuoTray",
        Paths.GvSvcName,
    ];

    public static int Run(string[] args)
    {
        try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }); } catch { }
        var rounds = ParseRounds(args);
        Console.WriteLine("== 更新清场练兵 ==");
        Console.WriteLine($"Root = {Paths.Root}");
        Console.WriteLine($"轮数 = {rounds}");
        Console.WriteLine("说明：每轮停止全部服务与进程并验证 PID 退出，随后重新拉起供下一轮练兵。\n");
        Console.WriteLine($"当前进程 PID={Environment.ProcessId}，祖先 PID 集合=[{string.Join(",", ProcGuard.AncestorPids().OrderBy(x => x))}]\n");

        var failures = 0;
        // 确保起点是全运行态：上一次练兵/更新可能把组件停在半路
        Console.WriteLine("准备阶段：拉起全部组件作为练兵目标...");
        StartComponents();
        if (!WaitUntilRevived(TimeSpan.FromSeconds(90)))
            Console.WriteLine("[WARN] 部分组件未拉起，按实际存量练兵");

        for (var round = 1; round <= rounds; round++)
        {
            Console.WriteLine($"-- 第 {round}/{rounds} 轮 --");
            var before = Snapshot();
            PrintSnapshot("停止前", before);

            StopServices();
            KillProcesses();

            var stopped = WaitUntilEmpty(TimeSpan.FromSeconds(20));
            var after = Snapshot();
            PrintSnapshot("复查", after);
            if (!stopped || after.Count != 0)
            {
                failures++;
                Console.WriteLine("[FAIL] 仍有服务进程或目标进程存活，不能进入文件替换阶段");
                foreach (var item in after)
                    Console.WriteLine($"       {item.Kind} {item.Name} PID={item.Pid} 状态={item.State} 路径={item.Path}");
            }
            else
            {
                Console.WriteLine("[PASS] 本轮所有目标 PID 均已退出，且观察窗口内未复活");
            }

            if (round < rounds)
            {
                Console.WriteLine("拉起组件供下一轮练兵...");
                StartComponents();
                var revived = WaitUntilRevived(TimeSpan.FromSeconds(90));
                Console.WriteLine(revived
                    ? "拉起完成，开始下一轮"
                    : "[WARN] 拉起后部分进程未出现，下一轮按实际存量练兵");
            }
        }

        Console.WriteLine("收尾：恢复组件运行态...");
        StartComponents();
        WaitUntilRevived(TimeSpan.FromSeconds(90));

        Console.WriteLine(failures == 0
            ? "== 更新清场练兵 ALL PASS =="
            : $"== 更新清场练兵 FAILED ({failures} 轮) ==");
        return failures == 0 ? 0 : 1;
    }

    private static int ParseRounds(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--rounds", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var n)
            ? Math.Clamp(n, 1, 100)
            : 1;
    }

    private sealed record Item(string Kind, string Name, int Pid, string State, string Path);

    private static List<Item> Snapshot()
    {
        var result = new List<Item>();
        foreach (var service in ServiceNames)
        {
            try
            {
                using var sc = new ServiceController(service);
                var state = sc.Status.ToString();
                var pid = ServicePid(service);
                if (pid > 0 || state is not nameof(ServiceControllerStatus.Stopped))
                    result.Add(new Item("service", service, pid, state, ServicePath(service)));
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        foreach (var name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                var path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { }
                var pid = p.Id;
                var exited = false;
                try { exited = p.HasExited; } catch { }
                p.Dispose();
                if (!exited) result.Add(new Item("process", name, pid, "Running", path));
            }
        }
        return result.GroupBy(x => (x.Kind, x.Name, x.Pid)).Select(x => x.First()).ToList();
    }

    private static void PrintSnapshot(string label, List<Item> items)
    {
        Console.WriteLine(items.Count == 0 ? $"{label}: 空" : $"{label}: {items.Count} 项");
        foreach (var item in items)
            Console.WriteLine($"  {item.Kind,-7} {item.Name,-20} PID={item.Pid,-6} {item.State,-10} {item.Path}");
    }

    private static void StopServices()
    {
        foreach (var name in ServiceNames)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                {
                    Console.WriteLine($"  服务 {name}: 已停止/停止中");
                    continue;
                }
                Console.WriteLine($"  服务 {name}: stop（停止前 PID={ServicePid(name)}）");
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                Console.WriteLine($"  服务 {name}: 已停止");
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1060) { }
            catch (Exception ex) { Console.WriteLine($"  [WARN] 服务 {name} 停止失败: {ex.Message}"); }
        }
    }

    // 拉起：核心服务、GameViewerService/GameManager（存在才拉）、托盘。
    // easytier/sunshine 由核心服务守卫自动拉起，不手动启动。
    private static void StartComponents()
    {
        foreach (var name in new[] { Paths.SvcName, Paths.GvSvcName, Paths.GmSvcName })
        {
            if (Services.Query(name) is null) { Console.WriteLine($"  服务 {name}: 未安装，跳过"); continue; }
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status == ServiceControllerStatus.Running) { Console.WriteLine($"  服务 {name}: 已在运行"); continue; }
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                Console.WriteLine($"  服务 {name}: 已拉起");
            }
            catch (Exception ex) { Console.WriteLine($"  [WARN] 服务 {name} 拉起失败: {ex.Message}"); }
        }
        try
        {
            if (File.Exists(Paths.TrayExe))
            {
                Process.Start(new ProcessStartInfo { FileName = Paths.TrayExe, WorkingDirectory = Paths.TrayDir, UseShellExecute = true });
                Console.WriteLine("  托盘: 已拉起");
            }
        }
        catch (Exception ex) { Console.WriteLine($"  [WARN] 托盘拉起失败: {ex.Message}"); }
    }

    private static bool WaitUntilRevived(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var alive = ReviveWatchNames.Count(n => Process.GetProcessesByName(n).Length > 0);
            if (alive >= ReviveWatchNames.Length - 1) return true; // 容忍一项未回（如未启用内网穿透）
            Thread.Sleep(1000);
        }
        var final = ReviveWatchNames
            .Where(n => Process.GetProcessesByName(n).Length == 0)
            .ToList();
        foreach (var n in final) Console.WriteLine($"  未复活: {n}");
        return final.Count == 0;
    }

    private static void KillProcesses()
    {
        var ancestors = ProcGuard.AncestorPids();
        foreach (var name in ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var p in processes)
            {
                var pid = p.Id;
                var label = name;
                try
                {
                    label = p.ProcessName;
                    if (!ProcGuard.TryKill(p, ancestors, out var error))
                        Console.WriteLine($"  [WARN] {label} PID={pid} 终止失败: {error}");
                    else if (ancestors.Contains(pid))
                        Console.WriteLine($"  终止 {label} PID={pid}（祖先进程，只杀自身）");
                }
                catch (Exception ex) { Console.WriteLine($"  [WARN] {label} PID={pid} 终止失败: {ex.Message}"); }
                finally { p.Dispose(); }
            }
        }
    }

    private static bool WaitUntilEmpty(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var current = Snapshot();
            if (current.Count == 0) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    private static int ServicePid(string serviceName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId FROM Win32_Service WHERE Name='{serviceName.Replace("'", "''")}'");
            foreach (ManagementObject item in searcher.Get())
                return Convert.ToInt32(item["ProcessId"] ?? 0);
        }
        catch { }
        return 0;
    }

    private static string ServicePath(string serviceName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT PathName FROM Win32_Service WHERE Name='{serviceName.Replace("'", "''")}'");
            foreach (ManagementObject item in searcher.Get())
                return (item["PathName"] as string) ?? "";
        }
        catch { }
        return "";
    }
}
