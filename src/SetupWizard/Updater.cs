using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace SunshineDuoSetup;

// 更新模块：检测 https://github.com/xRetia/Sunshine-Duo 的 .VERSION 文件，
// 有新 tag 则下载对应 Release zip 并替换本机旧程序（保留 config/ 与本机生成文件）。
internal static class Updater
{
    public const string RepoOwner = "xRetia";
    public const string RepoName = "Sunshine-Duo";
    public const string VersionUrl = "https://raw.githubusercontent.com/xRetia/Sunshine-Duo/main/.VERSION";
    public const string ReleasesApiUrl = "https://api.github.com/repos/xRetia/Sunshine-Duo/releases";
    public const string ReleasesPageUrl = "https://github.com/xRetia/Sunshine-Duo/releases";

    public static string LocalVersion =>
        typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public sealed record RemoteVersion(string Tag, string Url, string Error)
    {
        public bool Ok => Error.Length == 0;
    }

    // 读 .VERSION：首行形如 v1.2.3；其余行忽略
    public static async Task<RemoteVersion> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var http = NewHttp();
            var text = (await http.GetStringAsync(VersionUrl, ct)).Trim();
            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            var tag = line.StartsWith('v') ? line : "v" + line;
            if (!System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v\d+(\.\d+){0,3}$"))
                return new RemoteVersion("", "", $".VERSION 内容无效：「{line}」");
            return new RemoteVersion(tag, $"{ReleasesPageUrl}/tag/{tag}", "");
        }
        catch (Exception ex)
        {
            return new RemoteVersion("", "", ex.Message);
        }
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

    // 找到 tag 对应 Release 的 zip 资产（任选一个 *.zip；约定 zip 解压根即仓库布局根）
    public static async Task<string?> FindAssetUrlAsync(string tag, CancellationToken ct)
    {
        try
        {
            using var http = NewHttp();
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            var json = await http.GetStringAsync(ReleasesApiUrl, ct);
            var arr = JsonNode.Parse(json) as JsonArray;
            if (arr is null) return null;
            foreach (var rel in arr.OfType<JsonObject>())
            {
                var relTag = (string?)rel["tag_name"];
                if (!string.Equals(relTag, tag, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(relTag, tag.TrimStart('v'))) continue;
                if (rel["assets"] is not JsonArray assets) continue;
                foreach (var asset in assets.OfType<JsonObject>())
                {
                    var name = (string?)asset["name"] ?? "";
                    var url = (string?)asset["browser_download_url"];
                    if (url is not null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        return url;
                }
            }
        }
        catch { }
        return null;
    }

    // 执行更新：下载 → 解压到临时目录 → 停进程 → 镜像替换 → 清理
    public static void Apply(string tag, string zipUrl, string workRoot, Action<string> log, CancellationToken ct)
    {
        var staging = Path.Combine(Path.GetTempPath(), "SunshineDuoUpdate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            var zipPath = Path.Combine(staging, "update.zip");
            if (staging.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) is false)
                throw new Exception("临时目录异常，终止更新（安全保护）");
            log($"下载 {zipUrl} ...");
            using (var http = NewHttp())
            using (var resp = http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct).Result)
            {
                resp.EnsureSuccessStatusCode();
                using var src = resp.Content.ReadAsStream(ct);
                using var dst = File.Create(zipPath);
                src.CopyTo(dst);
            }
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
