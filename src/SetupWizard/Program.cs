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

        // 前置依赖检测（自检与截图模式不做，仅正式安装/更新路径）
        if (!EnsurePrerequisites())
            return 1;

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(updateMode));
        return 0;
    }

    // 确认安装前置依赖：OS 版本 + .NET 9 运行时阻断；WebView2 缺失只提示不阻断
    private static bool EnsurePrerequisites()
    {
        var os = Detect.OsVersion();
        if (!os.Installed)
        {
            MessageBox.Show(
                "操作系统版本过低，安装无法继续。\n\n" +
                "当前版本：" + os.Detail + "\n" +
                "要求：Windows 10 1803（版本 17134）或更高。\n请升级系统后再运行本程序。",
                "系统版本过低", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        var dotnet = Detect.DotNetDesktop9();
        if (!dotnet.Installed)
        {
            MessageBox.Show(
                "缺少必需的运行时组件，安装无法继续。\n\n" +
                dotnet.Detail + "\n\n" +
                "请先下载并安装 .NET 9 桌面运行时（x64），再重新运行本程序。\n" +
                "下载地址：\nhttps://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe",
                "缺少 .NET 9 运行时", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        var webview2 = Detect.WebView2();
        if (!webview2.Installed)
        {
            var dr = MessageBox.Show(
                "建议先安装 Microsoft Edge WebView2 运行时，本程序界面基于它呈现。\n\n" +
                webview2.Detail + "\n" +
                "若不安装，界面将采用老旧风格，可能影响体验和感观，但仍可继续安装。\n\n" +
                "下载地址：\nhttps://go.microsoft.com/fwlink/?linkid=2124703\n\n" +
                "点「确定」继续安装（老旧界面）；点「取消」退出，装好 WebView2 后再运行本程序。",
                "推荐安装 WebView2 运行时", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            return dr == DialogResult.OK;
        }

        return true;
    }

    public static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
