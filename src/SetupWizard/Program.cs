using System.Diagnostics;

namespace SunshineDuoSetup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run();

        var shotIdx = Array.FindIndex(args, a => a.Equals("--shots", StringComparison.OrdinalIgnoreCase));
        if (shotIdx >= 0)
            return Shot.Run(args.Length > shotIdx + 1 ? args[shotIdx + 1] : Path.Combine(Paths.Root, ".temp", "uitest"));

        var updateMode = args.Any(a => a.Equals("--update", StringComparison.OrdinalIgnoreCase));

        // UAC 由 app.manifest 的 requireAdministrator 统一保证（双击/命令行/计划任务都会触发提权确认）
        if (!IsAdmin())
        {
            MessageBox.Show(
                "本工具需要管理员权限运行（安装服务、驱动与防火墙规则）。\n请右键「以管理员身份运行」，或直接双击让 UAC 弹窗授权。",
                "萌远控 Duo 部署工具", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(updateMode));
        return 0;
    }

    public static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
