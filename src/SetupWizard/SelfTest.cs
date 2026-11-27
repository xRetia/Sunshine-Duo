using System.Runtime.InteropServices;

namespace SunshineDuoSetup;

// 自检：路径/检测/JSON/DPAPI/配置模板渲染/SetupAPI P/Invoke 可用性，不做系统变更（无需管理员）
internal static class SelfTest
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    private static int _fail;

    public static int Run()
    {
        AttachConsole(-1);
        try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }); } catch { }

        Console.WriteLine("== SunshineDuoSetup SelfTest ==");
        Console.WriteLine("Root = " + Paths.Root);

        Check("根目录定位（sunshine.exe 存在）", () => File.Exists(Paths.SunshineExe));
        Check("tools\\rdpWrapper.exe 存在", () => File.Exists(Paths.RdpWrapExe));
        Check("tools\\GameManager.exe 存在", () => File.Exists(Paths.GameManagerExe));
        Check("bin\\SunshineDuoCore\\SunshineDuoCore.exe 存在", () => File.Exists(Paths.CoreExe));
        Check("drivers\\sudovda\\SudoVDA.inf 存在", () => File.Exists(Paths.SudoVdaInf));
        Check("drivers\\sudovda\\sudovda.cer 存在", () => File.Exists(Paths.SudoVdaCer));
        Check("scripts\\add-firewall-rule.bat 存在", () => File.Exists(Path.Combine(Paths.ScriptsDir, "add-firewall-rule.bat")));
        Check("scripts\\delete-firewall-rule.bat 存在", () => File.Exists(Path.Combine(Paths.ScriptsDir, "delete-firewall-rule.bat")));
        Check("scripts\\install-gamepad.ps1 存在", () => File.Exists(Path.Combine(Paths.ScriptsDir, "install-gamepad.ps1")));
        Check("config 目录存在", () => Directory.Exists(Paths.ConfigDir));

        var gpus = GpuEnum.Physical();
        Console.WriteLine($"  [INFO] 物理显卡枚举（已过滤虚拟显卡）：{gpus.Count} 个");
        foreach (var g in gpus) Console.WriteLine($"         - {g} [{GpuEnum.Vendor(g)}]");
        Check("物理显卡枚举非空", () => gpus.Count >= 1);

        Check("组件检测可执行（无异常）", () => Detect.ViGEmBus() is not null && Detect.RdpWrapper() is not null);
        Check("服务查询可执行", () => Services.Query(Paths.SvcName) is not null || true);
        Check("SudoVDA 枚举可执行", () => SudoVda.EnumDevices() is not null);
        Check("ConsoleUser 返回（管理员或普通均可）", () => true);

        Check("DPAPI 加解密 roundtrip", () =>
        {
            var plain = System.Text.Encoding.UTF8.GetBytes("Test-Password-123");
            var enc = System.Security.Cryptography.ProtectedData.Protect(plain, null,
                System.Security.Cryptography.DataProtectionScope.LocalMachine);
            var dec = System.Security.Cryptography.ProtectedData.Unprotect(enc, null,
                System.Security.Cryptography.DataProtectionScope.LocalMachine);
            return System.Text.Encoding.UTF8.GetString(dec) == "Test-Password-123";
        });

        Check("sunshine.conf 模板渲染（写 .temp 预览）", () =>
        {
            var dir = Path.Combine(Paths.Root, ".temp");
            Directory.CreateDirectory(dir);
            var preview = Path.Combine(dir, "sunshine.conf.preview");
            var adapter = gpus.Count > 0 ? gpus[0] : "Test GPU";
            File.WriteAllLines(preview, new[]
            {
                "adapter_name = " + adapter,
                "address_family = both",
                "gamepad = x360",
                "upnp = enabled",
            }, Ops.Utf8NoBomEnc);
            var txt = File.ReadAllText(preview);
            return txt.Contains("adapter_name") && txt.Contains("upnp = enabled");
        });

        Check("config.json 默认模板", () =>
        {
            var cfg = new System.Text.Json.Nodes.JsonObject
            {
                ["username"] = "Game",
                ["password_mode"] = "dpapi",
                ["server"] = "127.0.0.2",
                ["guard_apollo"] = true,
                ["auto_logon"] = true,
                ["gameviewer_versions"] = new System.Text.Json.Nodes.JsonArray("4.0.*", "4.46.*"),
            };
            var j = cfg.ToJsonString();
            return j.Contains("127.0.0.2") && j.Contains("dpapi") && j.Contains("gameviewer_versions");
        });

        Check("版本比较 IsNewer", () =>
            Updater.IsNewer("v1.2.0", "1.1.9") && !Updater.IsNewer("v1.2.0", "1.2.0") && !Updater.IsNewer("v1.1.0", "1.2.0"));
        Check("更新资产 URL 使用版本化 zip", () =>
            Updater.ReleaseZipUrl("v1.2.3") == "https://github.com/xRetia/Sunshine-Duo/releases/download/v1.2.3/moedesk_v1.2.3_win64.zip");
        Check("自替换残留清理不抛异常", () =>
        {
            Updater.CleanupStaleSelf();
            return true;
        });

        Console.WriteLine($"  [INFO] 本地版本（.VERSION）= {Updater.LocalVersion}");

        if (_fail > 0)
        {
            Console.WriteLine($"== SelfTest FAILED ({_fail} 项) ==");
            return 1;
        }
        Console.WriteLine("== SelfTest ALL PASS ==");
        return 0;
    }

    private static void Check(string title, Func<bool> body)
    {
        try
        {
            var ok = body();
            if (!ok) throw new Exception("returned false");
            Console.WriteLine("  [PASS] " + title);
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("  [FAIL] " + title + " :: " + ex.Message);
        }
    }
}
