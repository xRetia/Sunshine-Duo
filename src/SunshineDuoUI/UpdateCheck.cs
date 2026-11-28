using System.Diagnostics;
using System.Net.Http;
using System.Reflection;

namespace SunshineDuoTray;

// 版本更新检测：远端 .VERSION（GitHub raw）与本地 .VERSION 比较；
// 国内（UTC+8）先走 .GITPROXY 在线下发的镜像前缀，失败退直连；
// setup.exe 入口：仅根目录 setup.exe（安装器随包分发在根目录）
internal static class UpdateCheck
{
    public const string VersionUrl = "https://raw.githubusercontent.com/xRetia/Sunshine-Duo/main/.VERSION";
    public const string GitProxyRawUrl = "https://raw.githubusercontent.com/xRetia/Sunshine-Duo/main/.GITPROXY";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly HttpClient ProxyHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static bool? _hasUpdate;
    private static string _remoteVersion = "";
    private static DateTime _checkedAt = DateTime.MinValue;

    // 本地版本：读安装根 .VERSION（与发布/更新同源），读不到退回程序集版本
    public static string LocalVersion
    {
        get
        {
            try
            {
                var text = File.ReadAllText(Paths.VersionFile).Trim();
                if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
                if (Version.TryParse(text, out var v)) return v.ToString(3);
            }
            catch { }
            var a = Assembly.GetExecutingAssembly().GetName().Version;
            return a == null ? "1.0.0" : a.ToString(3);
        }
    }

    public static (bool Has, string Remote, string Local) Snapshot => (_hasUpdate ?? false, _remoteVersion, LocalVersion);

    // 国内判定：UTC+8 时区（中国，无夏令时）
    public static bool IsChina => TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.FromHours(8);

    // 在线读取 .GITPROXY：镜像前缀（如 https://ghfast.top）；失败/非法返回 null
    public static async Task<string?> GetGitProxyAsync()
    {
        if (!IsChina) return null;
        try
        {
            var text = (await ProxyHttp.GetStringAsync(GitProxyRawUrl)).Trim();
            if (text.StartsWith("https://") && text.Length < 100 && !text.Contains(' '))
                return text.TrimEnd('/');
        }
        catch { }
        return null;
    }

    private static string ViaProxy(string? proxy, string url) =>
        string.IsNullOrEmpty(proxy) ? url : proxy + "/" + url;

    // 10 分钟缓存，避免频繁切页打 GitHub raw
    public static async Task<(bool Has, string Remote, string Local)> CheckAsync(bool force = false)
    {
        if (!force && _hasUpdate != null && DateTime.Now - _checkedAt < TimeSpan.FromMinutes(10))
            return Snapshot;
        var proxy = await GetGitProxyAsync();
        foreach (var url in new[] { ViaProxy(proxy, VersionUrl), VersionUrl }.Distinct())
        {
            try
            {
                var text = (await Http.GetStringAsync(url)).Trim();
                if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
                var local = ParseVersion(LocalVersion);
                var remote = ParseVersion(text);
                _remoteVersion = text;
                _hasUpdate = remote != null && local != null && remote > local;
                _checkedAt = DateTime.Now;
                return Snapshot;
            }
            catch
            {
                // 镜像失败试直连；全部失败时保留上次成功结果，不把网络故障伪装成无更新。
            }
        }
        return Snapshot;
    }

    // 返回 null 表示已启动；否则为用户可读的错误信息
    // updateMode=false 时启动安装向导（安装/修复模式，服务注册幂等）；true 时走更新流程
    public static string? StartUpdate(bool updateMode = true)
    {
        var setup = Path.Combine(Paths.Root, "setup.exe");
        if (!File.Exists(setup))
            return "未找到安装向导 setup.exe";
        try
        {
            var args = updateMode ? "--update" : "";
            Process.Start(new ProcessStartInfo(setup, args) { UseShellExecute = true });
            return null;
        }
        catch (Exception e)
        {
            return "启动安装向导失败：" + e.Message;
        }
    }

    private static Version? ParseVersion(string text)
    {
        var t = text.Trim();
        var i = 0;
        if (i < t.Length && (t[i] == 'v' || t[i] == 'V')) i++;
        var start = i;
        while (i < t.Length && (char.IsDigit(t[i]) || t[i] == '.')) i++;
        return i > start && Version.TryParse(t[start..i], out var v) ? v : null;
    }
}
