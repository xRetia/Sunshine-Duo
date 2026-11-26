namespace SunshineDuoSetup;

// 路径推导：从 exe 向上找包含 sunshine.exe 的目录作为安装根（目录可整体移动）
internal static class Paths
{
    public static string Root { get; } = FindRoot();

    public static string ScriptsDir => Path.Combine(Root, "scripts");
    public static string ToolsDir => Path.Combine(Root, "tools");
    public static string DriversDir => Path.Combine(Root, "drivers");
    public static string ConfigDir => Path.Combine(Root, "config");
    public static string LinksDir => Path.Combine(Root, "Links");

    public static string SunshineExe => Path.Combine(Root, "sunshine.exe");
    public static string ConfPath => Path.Combine(ConfigDir, "sunshine.conf");
    public static string GameConfPath => Path.Combine(ConfigDir, "sunshine-game.conf");
    public static string ConsoleStatePath => Path.Combine(ConfigDir, "sunshine_state-console.json");
    public static string GameStatePath => Path.Combine(ConfigDir, "sunshine_state-game.json");
    public static string LegacyStatePath => Path.Combine(ConfigDir, "sunshine_state.json");
    public static string LogPath => Path.Combine(ConfigDir, "sunshine.log");
    public static string LogBakPath => Path.Combine(ConfigDir, "sunshine.log.backup");

    public static string RdpWrapExe => Path.Combine(ToolsDir, "rdpWrapper.exe");
    public static string GameManagerExe => Path.Combine(ToolsDir, "GameManager.exe");
    public static string GameManagerJson => Path.Combine(ToolsDir, "GameManager.json");

    public static string CoreDir => Path.Combine(Root, "bin", "SunshineDuoCore");
    public static string CoreExe => Path.Combine(CoreDir, "SunshineDuoCore.exe");
    public static string CoreCredPath => Path.Combine(CoreDir, "rdp.cred");
    public static string CoreCfgPath => Path.Combine(CoreDir, "config.json");
    public static string CoreWatchLog => Path.Combine(CoreDir, "watch.log");

    public static string TrayDir => Path.Combine(Root, "bin", "SunshineDuoTray");
    public static string TrayExe => Path.Combine(TrayDir, "SunshineDuoTray.exe");
    public static string TrayJson => Path.Combine(TrayDir, "trayapp.json");

    public static string SudoVdaDir => Path.Combine(DriversDir, "sudovda");
    public static string SudoVdaInf => Path.Combine(SudoVdaDir, "SudoVDA.inf");
    public static string SudoVdaCer => Path.Combine(SudoVdaDir, "sudovda.cer");

    public const string SvcName = "SunshineDuoCore";
    public const string SvcDispName = "Sunshine Duo 核心服务";
    public const string LegacySvc = "GameViewerSessionHook";
    public const string GmSvcName = "GameManagerService";
    public const string RdpWrapDir = @"C:\Program Files\RDP Wrapper";
    public const string LegacyTaskName = "Sunshine Autostart";
    public const string LegacyTaskName2 = "Sunshine Autostart (Game)";
    public const string LegacyRunValue = "Sunshine";
    public const string FirewallRuleName = "Apollo";

    public static string ViGEmBusSys => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "ViGEmBus.sys");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "sunshine.exe"))) return dir.FullName;
            dir = dir.Parent;
        }
        // 兜底：exe 所在目录的上级（bin 部署），再不行就 exe 目录本身
        var fallback = new DirectoryInfo(AppContext.BaseDirectory);
        return fallback.Parent?.FullName ?? fallback.FullName;
    }
}
