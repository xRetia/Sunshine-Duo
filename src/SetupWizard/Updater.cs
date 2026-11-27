using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace SunshineDuoSetup;

// 更新模块：比较本机与远程 .VERSION，有新版则下载 GitHub Release 资产
// moedesk_{tag}_win64.zip 并替换本机旧程序（保留 config/ 与本机生成文件）。
internal static class Updater
{
    public const string RepoUrl = "https://github.com/xRetia/Sunshine-Duo";
    public const string VersionUrl = "https://raw.githubusercontent.com/xRetia/Sunshine-Duo/main/.VERSION";
    public const string GitProxyFile = ".GITPROXY";
    public const string GitProxyRawUrl = "https://raw.githubusercontent.com/xRetia/Sunshine-Duo/main/.GITPROXY";
    // GitHub Release 资产（CI 在 .VERSION 变化时自动发布；tag 与资产名都用 .VERSION 原文）
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
    public static void Apply(string tag, string workRoot, Action<string> log, CancellationToken ct)
    {
        var staging = Path.Combine(Path.GetTempPath(), "SunshineDuoUpdate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            var zipPath = Path.Combine(staging, "update.zip");
            if (staging.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) is false)
                throw new Exception("临时目录异常，终止更新（安全保护）");
            var zipUrl = ReleaseZipUrl(tag);
            var proxy = GetGitProxyAsync(ct).GetAwaiter().GetResult();
            var candidates = new[] { ViaProxy(proxy, zipUrl), zipUrl }.Distinct().ToArray();
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
                        using var src = resp.Content.ReadAsStream(ct);
                        using var dst = File.Create(zipPath);
                        src.CopyTo(dst);
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

            // zip 可能有顶层目录（repo-1.2.3/），剥到真实根
            var pkgRoot = FindPayloadRoot(extractDir);
            if (pkgRoot is null || !File.Exists(Path.Combine(pkgRoot, "sunshine.exe")))
                throw new Exception("更新包内未找到 sunshine.exe（不是有效的 Sunshine-Duo 发布包）");

            log("停止旧进程与服务...");
            StopRunningProcesses();

            log("替换程序文件（保留 config、bin 本机文件与已配对数据）...");
            MirrorUpdate(pkgRoot, Paths.Root, log);

            // 服务注册不动（binPath 不变，无需重建）：只把 core 服务与托盘拉起来；
            // sunshine.exe 由 core 守卫自动拉起，不手动启；GameManager 保持停止态。
            RestartCoreService(log);
            StartTray(log);

            if (AppContext.BaseDirectory.TrimEnd('\\').Equals(Paths.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                log("注意: 安装器自身在根目录，更新文件已就位（本进程结束后自动生效）");

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
        foreach (var srcFile in Directory.EnumerateFiles(pkgRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(pkgRoot, srcFile);
            var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            // config 只有 apps.json 可整体覆盖，其余模板文件补缺
            var inConfig = first.Equals("config", StringComparison.OrdinalIgnoreCase);
            var dstFile = Path.Combine(target, rel);
            if (inConfig && File.Exists(dstFile) && !rel.Equals(@"config\apps.json", StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
            File.Copy(srcFile, dstFile, overwrite: true);
            updated++;
        }
        log($"OK: 已写入 {updated} 个文件");
    }

    // 更新后拉起：core 服务直接 sc start（注册未动）；托盘按原自启设置决定是否拉起
    private static void RestartCoreService(Action<string> log)
    {
        try
        {
            var r = Ops.ProcRun("sc.exe", $"start {Paths.SvcName}", 30_000);
            log(r.ExitCode == 0 || r.ExitCode == 1062 // 1062 = service not started? treat 0/1056(已启动)/1062 as ok
                ? $"OK: 服务 {Paths.SvcName} 已启动（sunshine.exe 由守卫自动拉起）"
                : $"警告: 服务 {Paths.SvcName} 启动返回码 {r.ExitCode}，稍后可手动执行: sc start {Paths.SvcName}");
        }
        catch (Exception ex)
        {
            log($"警告: 启动服务 {Paths.SvcName} 失败: {ex.Message}");
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

    private static void StopRunningProcesses()
    {
        foreach (var svc in new[] { Paths.SvcName, Paths.LegacySvc })
        {
            try { Ops.ProcRun("sc.exe", $"stop {svc}"); } catch { }
        }
        foreach (var name in new[] { "SunshineDuoCore", "sunshine", "SunshineDuoTray" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(true); } catch { }
                p.Dispose();
            }
        }
        Thread.Sleep(1500);
    }

    private static System.Net.Http.HttpClient NewHttp()
    {
        var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("SunshineDuoSetup/" + LocalVersion);
        return h;
    }
}
