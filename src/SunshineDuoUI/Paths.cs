using System.Diagnostics;
using System.Text.Json;

namespace SunshineDuoTray;

// 部署目录发现：从 exe 向上找包含 sunshine.exe 的目录作为仓库/安装根，
// 再从 SunshineDuoCore/config.json 读取会话开关与两个 Apollo 的 conf 路径
// （单一事实来源；guard 开关为 false 的会话=单会话安装，托盘不出对应菜单）。
public static class Paths
{
    public static string Root { get; } = FindRoot();
    public static string SunshineExe => Path.Combine(Root, "sunshine.exe");
    public static string VersionFile => Path.Combine(Root, ".VERSION");

    public static string ConsoleConf { get; private set; } = "";
    public static string GameConf { get; private set; } = "";
    public static bool ConsoleEnabled { get; private set; } = true;
    public static bool GameEnabled { get; private set; } = true;

    public class SessionInfo
    {
        public string Id { get; set; } = "";        // console | game
        public string Name { get; set; } = "";      // 当前会话 | 游戏会话
        public string ConfPath { get; set; } = "";
        public int StreamPort { get; set; }         // conf 里的 port=，0 表示未知
        public int WebPort { get; set; }            // WebUI 端口（探测/打开后台用）
    }

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
                ConsoleEnabled = Flag(doc.RootElement, "guard_apollo_console", true);
                GameEnabled = Flag(doc.RootElement, "guard_apollo", true);
            }
        }
        catch { }
    }

    // 读取 core 的实时配置项（托盘菜单勾选态用，避免静态缓存显示旧值）
    public static bool CoreFlag(string key, bool fallback)
    {
        try
        {
            var cfg = Path.Combine(Root, "bin", "SunshineDuoCore", "config.json");
            if (!File.Exists(cfg)) return fallback;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            return Flag(doc.RootElement, key, fallback);
        }
        catch { return fallback; }
    }

    // 实时读取会话开关（避免静态缓存的 ConsoleEnabled/GameEnabled 在会话模式切换后过期）
    public static bool IsConsoleEnabled() => CoreFlag("guard_apollo_console", true);
    public static bool IsGameEnabled() => CoreFlag("guard_apollo", true);

    public static string CoreString(string key, string fallback)
    {
        try
        {
            var cfg = Path.Combine(Root, "bin", "SunshineDuoCore", "config.json");
            if (!File.Exists(cfg)) return fallback;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? fallback
                : fallback;
        }
        catch { return fallback; }
    }

    // 已启用会话列表（顺序 console → game）；端口以 conf 的 port= 行为准，
    // 读不到时退回 trayapp.json 保存的端口。
    public static List<SessionInfo> GetSessions(AppConfig cfg)
    {
        var list = new List<SessionInfo>();
        if (ConsoleEnabled) list.Add(MakeSession("console", "当前会话", ConsoleConf, cfg.ConsolePort));
        if (GameEnabled) list.Add(MakeSession("game", "游戏会话", GameConf, cfg.GamePort));
        return list;
    }

    private static SessionInfo MakeSession(string id, string name, string conf, int fallbackPort)
    {
        var port = ReadConfPort(conf);
        return new SessionInfo
        {
            Id = id,
            Name = name,
            ConfPath = conf,
            StreamPort = port,
            WebPort = port > 0 ? port + 1 : fallbackPort + 1,
        };
    }

    private static int ReadConfPort(string conf)
    {
        try
        {
            foreach (var line in File.ReadLines(conf))
            {
                var t = line.Trim();
                if (t.StartsWith("port ", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("port=", StringComparison.OrdinalIgnoreCase))
                {
                    var v = t[4..].TrimStart(' ', '\t', '=').Trim('"', '\'');
                    if (int.TryParse(v, out var p) && p is > 0 and < 65536) return p;
                }
            }
        }
        catch { }
        return 0;
    }

    private static bool Flag(JsonElement el, string key, bool fallback)
    {
        if (!el.TryGetProperty(key, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
        return fallback;
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
// 每个 Apollo conf 的 state（与 setup 安装时同一机制）。写入后需重启 Apollo 生效。
// conf 缺失（单会话安装）直接跳过。
public static class CredentialReset
{
    public static string? Apply(string user, string pass)
    {
        if (!File.Exists(Paths.SunshineExe))
            return "未找到 " + Paths.SunshineExe;
        var done = 0;
        foreach (var conf in new[] { Paths.ConsoleConf, Paths.GameConf })
        {
            if (!File.Exists(conf)) continue;
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
            done++;
        }
        return done == 0 ? "没有可用的 Apollo 配置文件" : null;
    }
}
