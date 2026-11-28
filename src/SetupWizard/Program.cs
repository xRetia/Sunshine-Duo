using System.Diagnostics;
using System.IO;

namespace SunshineDuoSetup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run();

        if (args.Any(a => a.Equals("--replace-probe", StringComparison.OrdinalIgnoreCase)))
            return ReplaceProbe.Run();

        if (args.Any(a => a.Equals("--process-lab", StringComparison.OrdinalIgnoreCase)))
            return ProcessLab.Run(args);

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

        // 前置依赖检测（自检与截图模式不做，仅正式安装/更新路径）
        if (!EnsurePrerequisites())
            return 1;
        Application.Run(new MainForm(updateMode));
        return 0;
    }

    // 确认安装前置依赖：OS 版本 + .NET 9 运行时 + WebView2 运行时，均阻断安装
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

        // .NET 9 Desktop Runtime：硬性前置，必须检测到系统或当前用户实际安装的运行时。
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

        // WebView2 运行时：托盘界面依赖它，缺失阻断安装（可下载或直接退出）
        if (!WebView2Gate.EnsureInstalled(null))
            return false;

        // 核心组件完整性检查：SunshineDuoCore.exe 缺失通常被杀毒软件误删，提示用户关杀软并重新下载安装包
        if (!File.Exists(Paths.CoreExe))
        {
            MessageBox.Show(
                "核心组件文件缺失：\n" + Paths.CoreExe + "\n\n" +
                "这通常是杀毒软件（如管家/360/Windows Defender）误删了程序文件。\n\n" +
                "请执行以下操作：\n" +
                "1. 关闭或卸载相关杀毒软件（或将其加入白名单）；\n" +
                "2. 重新从官方渠道下载完整安装包；\n" +
                "3. 解压到全新目录后再次运行本程序。\n\n" +
                "本程序无法在组件缺失的情况下继续安装。",
                "核心组件缺失", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
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
