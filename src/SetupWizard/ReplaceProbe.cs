using System.Diagnostics;

namespace SunshineDuoSetup;

// 占用替换实验：真启动一个进程占住目标 exe，验证 Updater.ReplaceFile 的"改名挪开"路径。
// 不触碰真实安装目录，全部在 .temp 下做。
internal static class ReplaceProbe
{
    public static int Run()
    {
        try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }); } catch { }
        Console.WriteLine("== 占用替换实验 ==");

        var dir = Path.Combine(Paths.Root, ".temp", "replace-probe");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        var target = Path.Combine(dir, "occupied.exe");
        var src = Path.Combine(dir, "payload.exe");
        File.WriteAllText(target, "OLD-CONTENT");
        File.WriteAllText(src, "NEW-CONTENT");

        // 1. 无人占用时，普通 Copy 应该成功（走快速路径）
        var occupied = false;
        try
        {
            File.Copy(src, target, overwrite: true);
            Console.WriteLine("  [PASS] 未占用文件直接覆写成功");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [FAIL] 未占用文件直接覆写失败: " + ex.Message);
            return 1;
        }

        // 2. 让目标 exe 真的被一个运行中的进程占住
        //    复制一份真实可执行文件充当被占用者，启动它并保持运行（Windows 会锁定其映像）
        var helperExe = Path.Combine(dir, "holder.exe");
        var seed = Path.Combine(Environment.SystemDirectory, "ping.exe");
        if (!File.Exists(seed))
        {
            Console.WriteLine("  [FAIL] 找不到 ping.exe 作为占位宿主: " + seed);
            return 1;
        }
        File.Copy(seed, helperExe, overwrite: true);
        var psi = new ProcessStartInfo
        {
            FileName = helperExe,
            Arguments = "-t 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var holder = Process.Start(psi)!;
        Thread.Sleep(1500);
        Console.WriteLine($"  [INFO] 占位进程 pid={holder.Id} 运行中={!holder.HasExited}");
        if (holder.HasExited)
        {
            Console.WriteLine("  [FAIL] 占位进程启动后立即退出，无法验证占用");
            return 1;
        }

        // 3. 让占位进程持有 helper.exe 的映像（Windows 会锁定运行中的 exe）
        //    我们要占用的其实是"目标文件"，所以把目标换成占位进程正在运行的 exe 本身
        var lockedTarget = helperExe;
        var lockedSrc = Path.Combine(dir, "payload2.exe");
        File.WriteAllText(lockedSrc, "NEW-CONTENT-2");

        // 直接覆写：预期失败（这正是线上报错的那类情况）
        var directFailed = false;
        string directError = "";
        try
        {
            File.Copy(lockedSrc, lockedTarget, overwrite: true);
        }
        catch (Exception ex)
        {
            directFailed = true;
            directError = ex.GetType().Name + ": " + ex.Message;
        }
        if (directFailed)
            Console.WriteLine("  [PASS] 直接覆写被占用文件如期失败（复现线上报错）: " + directError);
        else
            Console.WriteLine("  [WARN] 直接覆写竟然成功了，需确认占位是否真的生效");

        // 4. 走 Updater.ReplaceFile（改名挪开路径）
        var replaced = false;
        string? replaceLog = null;
        try
        {
            replaced = Updater.ReplaceFile(lockedSrc, lockedTarget, s => replaceLog = s);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [FAIL] ReplaceFile 抛异常: " + ex);
            try { holder.Kill(true); } catch { }
            return 1;
        }

        var newContent = File.ReadAllText(lockedTarget);
        var oldExists = File.Exists(lockedTarget + ".old");
        var newExists = File.Exists(lockedTarget + ".new");
        var holderAlive = !holder.HasExited;

        Console.WriteLine($"  [INFO] ReplaceFile 返回 replaced={replaced}, log={replaceLog}");
        Console.WriteLine($"  [INFO] 新文件内容={newContent}, .old 存在={oldExists}, .new 存在={newExists}, 占位进程仍活着={holderAlive}");

        var fail = 0;
        if (!replaced) { Console.WriteLine("  [FAIL] ReplaceFile 未走改名路径"); fail++; }
        if (newContent != "NEW-CONTENT-2") { Console.WriteLine("  [FAIL] 新内容未落到位"); fail++; }
        if (!oldExists) { Console.WriteLine("  [FAIL] 旧文件未被改名为 .old"); fail++; }
        if (newExists) { Console.WriteLine("  [FAIL] 残留 .new"); fail++; }
        if (!holderAlive) { Console.WriteLine("  [FAIL] 占位进程被影响（不应被杀）"); fail++; }

        // 5. 进程退出后 .old 应可删
        try { holder.Kill(true); } catch { }
        try { holder.WaitForExit(10_000); } catch { }
        Thread.Sleep(500);
        var oldDeleted = false;
        try
        {
            if (File.Exists(lockedTarget + ".old")) File.Delete(lockedTarget + ".old");
            oldDeleted = !File.Exists(lockedTarget + ".old");
        }
        catch (Exception ex) { Console.WriteLine("  [FAIL] 进程退出后 .old 仍不可删: " + ex.Message); }
        if (oldDeleted) Console.WriteLine("  [PASS] 进程退出后 .old 可正常清理");
        else { Console.WriteLine("  [FAIL] 进程退出后 .old 仍不可删"); fail++; }

        try { Directory.Delete(dir, recursive: true); } catch { }

        Console.WriteLine(fail == 0 ? "== 占用替换实验 ALL PASS ==" : $"== 占用替换实验 FAILED ({fail} 项) ==");
        return fail == 0 ? 0 : 1;
    }
}
