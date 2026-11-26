using System.Diagnostics;
using System.Net.Http;
using System.Reflection;

namespace SunshineDuoTray;

// 版本更新检测：远端 .VERSION（GitHub raw）与托盘程序集版本比较；
// setup.exe 入口：根目录 setup.exe 优先，缺省退回 bin\SunshineDuoSetup\setup.exe
internal static class UpdateCheck
{
    private const string VersionUrl = "https://raw.githubusercontent.com/xRetia/Sunshine-Duo/main/.VERSION";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static bool? _hasUpdate;
    private static string _remoteVersion = "";
    private static DateTime _checkedAt = DateTime.MinValue;

    public static string LocalVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "1.0.0" : v.ToString(3);
        }
    }

    public static (bool Has, string Remote, string Local) Snapshot => (_hasUpdate ?? false, _remoteVersion, LocalVersion);

    // 10 分钟缓存，避免频繁切页打 GitHub raw
    public static async Task<(bool Has, string Remote, string Local)> CheckAsync(bool force = false)
    {
        if (!force && _hasUpdate != null && DateTime.Now - _checkedAt < TimeSpan.FromMinutes(10))
            return Snapshot;
        try
        {
            var text = (await Http.GetStringAsync(VersionUrl)).Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
            var local = ParseVersion(LocalVersion);
            var remote = ParseVersion(text);
            _remoteVersion = text;
            _hasUpdate = remote != null && local != null && remote > local;
            _checkedAt = DateTime.Now;
        }
        catch
        {
            // 网络不可达或版本文件缺失：保留上次结果，本次按无更新处理
            if (_hasUpdate == null) _hasUpdate = false;
        }
        return Snapshot;
    }

    // 返回 null 表示已启动；否则为用户可读的错误信息
    public static string? StartUpdate()
    {
        var setup = Path.Combine(Paths.Root, "setup.exe");
        if (!File.Exists(setup))
            setup = Path.Combine(Paths.Root, "bin", "SunshineDuoSetup", "setup.exe");
        if (!File.Exists(setup))
            return "未找到安装向导 setup.exe";
        try
        {
            Process.Start(new ProcessStartInfo(setup, "--update") { UseShellExecute = true });
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
