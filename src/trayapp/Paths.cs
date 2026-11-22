using System.Diagnostics;
using System.Text.Json;

namespace SunshineDuoTray;

// 部署目录发现：从 exe 向上找包含 sunshine.exe 的目录作为仓库/安装根，
// 再从 SunshineDuoCore/config.json 读取两个 Apollo 的 conf 路径（单一事实来源）。
public static class Paths
{
    public static string Root { get; } = FindRoot();
    public static string SunshineExe => Path.Combine(Root, "sunshine.exe");

    public static string ConsoleConf { get; private set; } = "";
    public static string GameConf { get; private set; } = "";

    static Paths()
    {
        ConsoleConf = Path.Combine(Root, "config", "sunshine.conf");
        GameConf = Path.Combine(Root, "config", "sunshine-game.conf");
        try
        {
            var wcfg = Path.Combine(Root, "bin", "SunshineDuoCore", "config.json");
            if (File.Exists(wcfg))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(wcfg));
                ConsoleConf = Resolve(doc.RootElement, "console_conf_path", ConsoleConf);
                GameConf = Resolve(doc.RootElement, "game_conf_path", GameConf);
            }
        }
        catch { }
    }

    private static string Resolve(JsonElement el, string key, string fallback)
    {
        if (!el.TryGetProperty(key, out var v) || v.GetString() is not { Length: > 0 } p)
            return fallback;
        return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(Root, p));
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "sunshine.exe")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
}

// WebUI 凭证重置：不走 Apollo API，直接用 sunshine.exe --creds 离线写入
// 两个 conf 的 state（与 setup 安装时同一机制）。写入后需重启 Apollo 生效。
public static class CredentialReset
{
    public static string? Apply(string user, string pass)
    {
        if (!File.Exists(Paths.SunshineExe))
            return "未找到 " + Paths.SunshineExe;
        foreach (var conf in new[] { Paths.ConsoleConf, Paths.GameConf })
        {
            if (!File.Exists(conf))
                return "未找到配置文件 " + conf;
            var psi = new ProcessStartInfo
            {
                FileName = Paths.SunshineExe,
                Arguments = $"\"{conf}\" --creds \"{user}\" \"{pass}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null)
                return "无法启动 sunshine.exe";
            if (!p.WaitForExit(60000))
            {
                try { p.Kill(); } catch { }
                return "sunshine --creds 超时（" + conf + "）";
            }
            if (p.ExitCode != 0)
                return $"sunshine --creds 返回码 {p.ExitCode}（{conf}）";
        }
        return null;
    }
}
