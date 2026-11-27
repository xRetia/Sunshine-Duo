using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace SunshineDuoSetup;

// 更新模块：比较本机与远程 .VERSION，有新版则下载 GitHub Release 资产
// moedesk_latest_win64.zip（以及按版本 tag 的资产） 并替换本机旧程序（保留 config/ 与本机生成文件）。
internal static class Updater
{
    public const string RepoUrl = "https://github.com/xRetia/Sunshine-Duo";
    public const string VersionUrl = "https://raw.githubusercontent.com/xRetia/Sunshine-Duo/main/.VERSION";
    public const string GitProxyFile = ".GITPROXY";
    public const string GitProxyRawUrl = "https://raw.githubusercontent.com/xRetia/Sunshine-Duo/main/.GITPROXY";
    // GitHub Release 版本化资产（CI tag 与资产名都用 .VERSION 原文）
    public const string ReleaseAssetTemplate = "https://github.com/xRetia/Sunshine-Duo/releases/download/{tag}/moedesk_{tag}_win64.zip";

    public static string ReleaseZipUrl(string tag) => ReleaseAssetTemplate.Replace("{tag}", tag);

    // 国内判定：UTC+8 时区（中国，无夏令时）
    public static bool IsChina => TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.FromHours(8);

    // 本地版本：读仓库根 .VERSION（更新时随镜像替换自动刷新），缺省退回程序集版本
    public static string LocalVersion
    {
        get
        {
            try
            {
                var text = File.ReadAllText(Path.Combine(Paths.Root, ".VERSION")).Trim();
                var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault() ?? "";
                var tag = line.StartsWith('v') ? line : "v" + line;
                if (System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v\d+(\.\d+){0,3}$"))
                    return tag.TrimStart('v');
            }
            catch { }
            return typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    public sealed record RemoteVersion(string Tag, string Error)
    {
        public bool Ok => Error.Length == 0;
    }

    // 在线读取 .GITPROXY（GitHub raw）：镜像前缀（如 https://ghfast.top）。
    // 仅国内时区使用；拉取失败/内容非法返回 null（直连）。
    public static async Task<string?> GetGitProxyAsync(CancellationToken ct)
    {
        if (!IsChina) return null;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SunshineDuoSetup/" + LocalVersion);
            var text = (await http.GetStringAsync(GitProxyRawUrl, ct)).Trim();
            if (text.StartsWith("https://") && text.Length < 100 && !text.Contains(' '))
                return text.TrimEnd('/');
        }
        catch { }
        return null;
    }

    // 包装 URL：有镜像则 {proxy}/{url}，否则原样
    public static string ViaProxy(string? proxy, string url) =>
        string.IsNullOrEmpty(proxy) ? url : proxy + "/" + url;

    // 读 .VERSION：首行形如 v1.2.3；其余行忽略。国内先走镜像再直连。
    public static async Task<RemoteVersion> CheckAsync(CancellationToken ct)
    {
        var proxy = await GetGitProxyAsync(ct);
        foreach (var url in new[] { ViaProxy(proxy, VersionUrl), VersionUrl }.Distinct())
        {
            try
            {
                using var http = NewHttp();
                var text = (await http.GetStringAsync(url, ct)).Trim();
                var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
                var tag = line.StartsWith('v') ? line : "v" + line;
                if (!System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v\d+(\.\d+){0,3}$"))
                    return new RemoteVersion("", $".VERSION 内容无效：「{line}」");
                return new RemoteVersion(tag, "");
            }
            catch (Exception ex)
            {
                if (url == VersionUrl)
                    return new RemoteVersion("", ex.Message);
            }
        }
        return new RemoteVersion("", "不可达");
    }

    public static bool IsNewer(string remoteTag, string localVersion)
    {
        static int[] Parse(string v)
        {
            var clean = v.TrimStart('v', 'V');
            var parts = clean.Split('.');
            var nums = new int[3];
            for (var i = 0; i < 3; i++)
                nums[i] = i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;
            return nums;
        }
        var r = Parse(remoteTag);
        var l = Parse(localVersion);
        for (var i = 0; i < 3; i++)
            if (r[i] != l[i]) return r[i] > l[i];
        return false;
    }

    // 执行更新：下载 Release 资产 zip → 解压 → 停进程 → 镜像替换 → 恢复服务与托盘
    public static void Apply(string tag, string workRoot, Action<string> log, CancellationToken ct,
        Action<long, long, double>? downloadProgress = null)
    {
        var staging = Path.Combine(Path.GetTempPath(), "SunshineDuoUpdate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            CleanupStaleSelf();
            var zipPath = Path.Combine(staging, "update.zip");
            if (staging.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) is false)
                throw new Exception("临时目录异常，终止更新（安全保护）");
            var zipUrl = ReleaseZipUrl(tag);
            var proxy = GetGitProxyAsync(ct).GetAwaiter().GetResult();
            // 版本化资产失败时兼容当前 Release 的 latest 别名。
            var urls = new[] { zipUrl,
                "https://github.com/xRetia/Sunshine-Duo/releases/download/" + tag + "/moedesk_latest_win64.zip" };
            var candidates = urls.SelectMany(url => new[] { ViaProxy(proxy, url), url }).Distinct().ToArray();
            Exception? last = null;
            foreach (var url in candidates)
            {
                try
                {
                    log($"下载 {url} ...");
                    using (var http = NewHttp())
                    using (var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).Result)
                    {
                        resp.EnsureSuccessStatusCode();
                        var total = resp.Content.Headers.ContentLength ?? -1;
                        using var src = resp.Content.ReadAsStream(ct);
                        using var dst = File.Create(zipPath);
                        var buffer = new byte[128 * 1024];
                        long done = 0;
                        var started = Stopwatch.GetTimestamp();
                        int read;
                        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            dst.Write(buffer, 0, read);
                            done += read;
                            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                            downloadProgress?.Invoke(done, total, seconds > 0 ? done / seconds : 0);
                        }
                    }
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    log($"下载失败（{ex.Message}），尝试下一来源...");
                }
            }
            if (last is not null) throw new Exception("全部下载来源均失败：" + last.Message);
            log("下载完成，解压中...");
            var extractDir = Path.Combine(staging, "pkg");
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            var pkgRoot = FindPayloadRoot(extractDir);
            if (pkgRoot is null || !File.Exists(Path.Combine(pkgRoot, "sunshine.exe")))
                throw new Exception("更新包内未找到 sunshine.exe（不是有效的 Sunshine-Duo 发布包）");

            log("停止旧进程与服务...");
            var pre = StopRunningProcesses(log);

            log("替换程序文件（保留 config、bin 本机文件与已配对数据）...");
            try
            {
                MirrorUpdate(pkgRoot, Paths.Root, log);
            }
            catch
            {
                RestartCoreService(log);
                RestoreStoppedServices(pre, log);
                StartTray(log);
                throw;
            }

            // 服务注册不动（binPath 不变）：核心、GameViewer/GameManager 服务与托盘在替换后恢复；
            // easytier/sunshine.exe 由核心服务自动拉起。
            RestartCoreService(log);
            RestoreStoppedServices(pre, log);
            StartTray(log);
            log($"OK: 已更新到 {tag}，核心服务与托盘已拉起");
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static string? FindPayloadRoot(string extractDir)
    {
        if (File.Exists(Path.Combine(extractDir, "sunshine.exe"))) return extractDir;
        foreach (var dir in Directory.EnumerateDirectories(extractDir))
            if (File.Exists(Path.Combine(dir, "sunshine.exe"))) return dir;
        return null;
    }

    // 镜像更新：pkgRoot → target。不删除 target 独有文件（保配置/凭据/状态），
    // 只覆盖同名文件与新增文件；.git/.temp/config 本机生成物不动（config 只补缺失的模板类文件）。
    private static void MirrorUpdate(string pkgRoot, string target, Action<string> log)
    {
        var updated = 0;
        var replaced = 0;
        foreach (var srcFile in Directory.EnumerateFiles(pkgRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(pkgRoot, srcFile);
            var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            var inConfig = first.Equals("config", StringComparison.OrdinalIgnoreCase);
            var dstFile = Path.Combine(target, rel);
            if (inConfig && File.Exists(dstFile) && !rel.Equals(@"config\apps.json", StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
            if (ReplaceFile(srcFile, dstFile, log))
                replaced++;
            updated++;
        }
        log(replaced > 0
            ? $"OK: 已写入 {updated} 个文件（其中 {replaced} 个被占用，已改名挪开后替换）"
            : $"OK: 已写入 {updated} 个文件");
    }

    // 覆盖单个文件。若目标正被运行中的进程占用（Windows 不允许覆写已映射的映像），
    // 走标准"改名挪开"方案：先把新内容落到 .new，把占用中的旧文件改名为 .old，
    // 再让 .new 顶上原路径。改名是目录项操作，不影响已映射的映像与运行中的进程。
    // 旧文件若仍被进程占用则当场删不掉（改名后进程仍持有句柄），留待下次启动清理。
    public static bool ReplaceFile(string srcFile, string dstFile, Action<string>? log = null)
    {
        try
        {
            File.Copy(srcFile, dstFile, overwrite: true);
            return false;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // 目标不存在时上面的 Copy 不会失败；走到这里说明是被占用
        var newFile = dstFile + ".new";
        var oldFile = dstFile + ".old";
        TryDelete(oldFile, log);
        try { File.Delete(newFile); } catch { }
        File.Copy(srcFile, newFile, overwrite: true);
        if (File.Exists(dstFile))
            File.Move(dstFile, oldFile, overwrite: true);
        File.Move(newFile, dstFile, overwrite: true);
        var stillLocked = !TryDelete(oldFile, log);
        log?.Invoke(stillLocked
            ? $"OK: {Path.GetFileName(dstFile)} 正被占用，已改名挪开后替换（旧文件待进程退出后清理）"
            : $"OK: {Path.GetFileName(dstFile)} 正被占用，已改名挪开后替换");
        return true;
    }

    private static bool TryDelete(string path, Action<string>? log)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"跳过: {Path.GetFileName(path)} 暂不可删（{ex.Message}）");
            return false;
        }
    }

    // 清理上次更新残留：setup.exe.old（旧映像，进程退出后可删）与失败的 .new
    public static void CleanupStaleSelf(Action<string>? log = null)
    {
        if (Environment.ProcessPath is null) return;
        foreach (var suffix in new[] { ".old", ".new" })
        {
            TryDelete(Environment.ProcessPath + suffix, log);
        }
    }

    // 更新后拉起：core 服务直接 sc start（注册未动）；托盘按原自启设置决定是否拉起
    private static void RestartCoreService(Action<string> log)
    {
        try
        {
            var r = Ops.ProcRun("sc.exe", $"start {Paths.SvcName}", 30_000);
            log(r.ExitCode == 0 || r.ExitCode == 1056 || r.ExitCode == 1062
                ? $"OK: 服务 {Paths.SvcName} 已启动（sunshine.exe 由守卫自动拉起）"
                : $"警告: 服务 {Paths.SvcName} 启动返回码 {r.ExitCode}，稍后可手动执行: sc start {Paths.SvcName}");
        }
        catch (Exception ex)
        {
            log($"警告: 启动服务 {Paths.SvcName} 失败: {ex.Message}");
        }
    }

    // 更新前在跑的服务原样拉起（旧服务 GameViewerSessionHook 多数机器不存在，存在且在跑才恢复）
    private static void RestoreStoppedServices(PreStopState pre, Action<string> log)
    {
        TryStartService(Paths.GvSvcName, pre.GameViewerRunning, log);
        TryStartService(Paths.GmSvcName, pre.GameManagerRunning, log);
        TryStartService(Paths.LegacySvc, pre.LegacyRunning, log);
    }

    private static void TryStartService(string name, bool condition, Action<string> log)
    {
        if (!condition) return;
        try
        {
            var r = Ops.ProcRun("sc.exe", $"start {name}", 30_000);
            log(r.ExitCode == 0 || r.ExitCode == 1056 || r.ExitCode == 1062
                ? $"OK: 服务 {name} 已启动"
                : $"警告: 服务 {name} 启动返回码 {r.ExitCode}");
        }
        catch (Exception ex)
        {
            log($"警告: 启动服务 {name} 失败: {ex.Message}");
        }
    }

    private static void StartTray(Action<string> log)
    {
        try
        {
            if (!File.Exists(Paths.TrayExe)) { log("跳过: 托盘 exe 不存在，未拉起"); return; }
            // 原本没有自启/快捷方式的新机器可能从未部署托盘：仅在部署目录已存在时拉起
            Process.Start(new ProcessStartInfo
            {
                FileName = Paths.TrayExe,
                WorkingDirectory = Paths.TrayDir,
                UseShellExecute = true,
            });
            log("OK: 托盘已启动");
        }
        catch (Exception ex)
        {
            log($"警告: 托盘启动失败: {ex.Message}");
        }
    }

    private sealed record PreStopState(bool GameManagerRunning, bool GameViewerRunning, bool LegacyRunning);

    private static PreStopState StopRunningProcesses(Action<string>? log = null)
    {
        var pre = new PreStopState(
            GameManagerRunning: ServiceRunning(Paths.GmSvcName),
            GameViewerRunning: ServiceRunning(Paths.GvSvcName),
            LegacyRunning: ServiceRunning(Paths.LegacySvc));
        foreach (var svc in new[] { Paths.SvcName, Paths.LegacySvc, Paths.GvSvcName, Paths.GmSvcName })
        {
            try
            {
                using var controller = new ServiceController(svc);
                if (controller.Status is not ServiceControllerStatus.Stopped and not ServiceControllerStatus.StopPending)
                    controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1060) { }
            catch (Exception ex) { log?.Invoke($"警告: 服务 {svc} 停止失败: {ex.Message}"); }
        }

        // 服务停止后，按当时存在的全部 PID 清理；不能只杀服务名，否则服务外孤儿和
        // GameViewerService/GameManager 进程会继续占用待替换文件（gv_hook 注入在
        // GameViewerService 进程里）。每轮都会重新枚举，防止守护进程复活。
        var names = new[] { "SunshineDuoCore", "sunshine", "SunshineDuoTray", "easytier-core",
            "GameManager", Paths.GvSvcName };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var running = names.SelectMany(Process.GetProcessesByName).ToArray();
            if (running.Length == 0) return pre;
            foreach (var p in running)
            {
                var name = "unknown";
                var pid = p.Id;
                try
                {
                    name = p.ProcessName;
                    if (!p.HasExited) p.Kill(entireProcessTree: true);
                    if (!p.WaitForExit(5_000))
                        log?.Invoke($"警告: 无法确认 {name} (PID={pid}) 已退出");
                }
                catch (Exception ex) { log?.Invoke($"警告: 终止 {name} (PID={pid}) 失败: {ex.Message}"); }
                finally { p.Dispose(); }
            }
            Thread.Sleep(250);
        }

        var survivors = names.SelectMany(Process.GetProcessesByName).ToArray();
        foreach (var p in survivors)
        {
            log?.Invoke($"错误: 清场失败，{p.ProcessName} (PID={p.Id}) 仍在运行，停止更新以避免半更新");
            p.Dispose();
        }
        if (survivors.Length > 0)
            throw new IOException("相关服务/进程未完全退出，已中止文件替换");
        return pre;
    }

    private static bool ServiceRunning(string name)
    {
        try
        {
            using var controller = new ServiceController(name);
            return controller.Status is not ServiceControllerStatus.Stopped and not ServiceControllerStatus.StopPending;
        }
        catch { return false; }
    }

    private static System.Net.Http.HttpClient NewHttp()
    {
        var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("SunshineDuoSetup/" + LocalVersion);
        return h;
    }
}
