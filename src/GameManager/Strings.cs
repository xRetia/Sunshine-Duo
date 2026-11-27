// Bilingual UI strings. Resolved once from the user's preferred UI language:
// zh* (zh-CN, zh-TW, zh-HK, ...) -> Chinese, anything else -> English.

using System.Globalization;

namespace GameManager;

public static class Strings
{
    private static readonly bool Zh =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh",
            StringComparison.OrdinalIgnoreCase);

    // ---- chrome ----

    public static string AppTitle => Zh ? "游戏管理器" : "Game Manager";
    public static string BtnRefresh => Zh ? "刷新" : "Refresh";
    public static string BtnFullScreen => Zh ? "全屏" : "Full Screen";
    public static string BtnAdvanced => Zh ? "高级" : "Advanced";
    public static string BtnPower => Zh ? "电源" : "Power";
    public static string BtnAbout => Zh ? "关于" : "About";

    // ---- power menu & power dialog ----

    public static string PowerLogOff => Zh ? "注销用户" : "Sign Out";
    public static string PowerShutdown => Zh ? "关机" : "Shut Down";
    public static string PowerRestart => Zh ? "重启" : "Restart";

    // ---- power dialog (one panel for every power entry point) ----

    public static string PowerTitle => Zh ? "电源操作" : "Power";
    public static string PowerNote => Zh
        ? "关机和重启将强制执行，未保存的进度会丢失"
        : "Shut down and restart are forced; unsaved work will be lost";
    public static string PowerCancel => Zh ? "取消" : "Cancel";
    public static string PowerConfirm => Zh ? "确认" : "Confirm";
    public static string PowerEnterKey => "Enter";
    public static string PowerPadA => "A";

    // ---- second-stage confirmation (shut down / restart) ----

    public static string ConfirmShutdownTitle => Zh ? "关机" : "Shut Down";
    public static string ConfirmShutdownBody => Zh
        ? "确定要立即强制关机吗？未保存的进度会丢失。"
        : "Force shut down now? Unsaved work will be lost.";
    public static string ConfirmRestartTitle => Zh ? "重启" : "Restart";
    public static string ConfirmRestartBody => Zh
        ? "确定要立即强制重启吗？未保存的进度会丢失。"
        : "Force restart now? Unsaved work will be lost.";

    // ---- input hint (bottom-right) ----

    public static string HintNavigate => Zh ? "移动光标" : "Navigate";
    public static string HintOpen => Zh ? "打开应用" : "Open";

    // ---- library ----

    public static string CountNone => Zh ? "暂无游戏" : "No games";
    public static string CountOne => Zh ? "1 个游戏" : "1 game";
    public static string CountMany(int n) => Zh ? $"{n} 个游戏" : $"{n} games";

    public static string EmptyHint(string folder) => Zh
        ? $"未找到游戏。\n\n请将 .lnk 快捷方式放入：\n{folder}"
        : $"No games found.\n\nAdd .lnk files to:\n{folder}";

    // ---- Advanced menu ----

    public static string MenuRegisterAll => Zh ? "为所有用户注册协议..." : "Register protocol for all users...";
    public static string MenuUnregisterAll => Zh ? "从系统注销协议" : "Unregister protocol from system";
    public static string MenuServiceInstalled => Zh ? "系统服务：已安装" : "System service: installed";
    public static string MenuServiceNotInstalled => Zh ? "系统服务：未安装" : "System service: not installed";
    public static string MenuInstallService => Zh ? "安装系统服务（开机自启）" : "Install system service (auto start)";
    public static string MenuRemoveService => Zh ? "卸载系统服务" : "Remove system service";

    // ---- protocol status (Advanced menu, first line) ----

    public static string ProtocolRegisteredAll => Zh ? "steam:// 已注册给所有用户" : "steam:// registered for all users";
    public static string ProtocolRegisteredUser => Zh ? "steam:// 仅当前用户已注册" : "steam:// registered for this user only";
    public static string ProtocolNotRegistered => Zh ? "steam:// 未注册" : "steam:// not registered";

    // ---- messages ----

    public static string MsgRegisteredAll => Zh
        ? "已为所有用户注册 steam:// 协议。"
        : "steam:// protocol registered for all users.";

    public static string MsgNeedAdmin => Zh
        ? "此操作需要管理员权限。\n请在 UAC 提示中确认。"
        : "Administrator rights are required.\nPlease accept the UAC prompt.";

    public static string MsgRemoved => Zh
        ? "已移除 steam:// 协议注册。"
        : "steam:// protocol registration removed.";

    public static string MsgNothingToRemove => Zh
        ? "未找到本程序的注册信息，无需注销。"
        : "No Game Manager registration was found. Nothing to unregister.";

    public static string MsgGameNotFound(string name) => Zh
        ? $"未找到游戏：{name}\n将改为打开游戏管理器。"
        : $"Game not found: {name}\nOpening Game Manager instead.";

    // ---- launch overlay ----

    public static string Launching(string name) => Zh
        ? $"正在启动 {name}..."
        : $"Starting {name}...";

    // ---- About ----

    public static string AboutTitle => Zh ? "关于游戏管理器" : "About Game Manager";
    public static string AboutAuthorLabel => Zh ? "作者" : "Author";
    public static string AboutAuthor => "苏羽茜工作室";
    public static string AboutCopyright => Zh
        ? "版权所有 © 2026 苏羽茜工作室"
        : "Copyright © 2026 SuYuQian Studio";
    public static string AboutProjectLabel => Zh ? "项目主页" : "Project";
    public static string AboutProjectUrl => "https://github.com/xRetia/game-manager";
    public static string AboutOpenLink => Zh ? "点击打开项目主页" : "Click to open the project page";

    public static string AboutBody(string folder) => Zh
        ? "游戏管理器 2.2.1 (WPF)\n\n" +
          "触控友好的游戏启动器\n" +
          "快捷方式目录：" + folder + "\n\n" +
          "操作\n" +
          "· 方向键 / 手柄十字键：移动光标\n" +
          "· Enter / A 键：打开应用\n" +
          "· 鼠标：点击选中，再次点击启动（双击亦可）\n" +
          "· 触摸：拖动滚动（带惯性）\n" +
          "· F5 刷新　F11 全屏　Esc 退出全屏\n\n" +
          "命令行与协议\n" +
          "· GameManager --fullscreen 无边框全屏启动\n" +
          "· steam://run/<游戏名> 直接启动游戏\n" +
          "· steam://open/bigpicture 唤起主界面\n\n" +
          "「高级」菜单管理 steam:// 协议与系统服务"
        : "Game Manager 2.2.1 (WPF)\n\n" +
          "A touch-friendly game launcher\n" +
          "Shortcuts folder: " + folder + "\n\n" +
          "Controls\n" +
          "· Arrows / gamepad dpad: move the cursor\n" +
          "· Enter / A: open the app\n" +
          "· Mouse: click to select, click again to launch (double-click works)\n" +
          "· Touch: drag to scroll with inertia\n" +
          "· F5 refresh　F11 fullscreen　Esc exit fullscreen\n\n" +
          "Command line & protocol\n" +
          "· GameManager --fullscreen　borderless fullscreen start\n" +
          "· steam://run/<Game>　launch a game directly\n" +
          "· steam://open/bigpicture　bring up the launcher\n\n" +
          "The Advanced menu manages the steam:// protocol and the system service";

    // ---- render diagnostics (About, last line) ----

    public static string DiagRender(string detail) => Zh ? $"渲染：{detail}" : $"Render: {detail}";
    public static string DiagHwOn => Zh ? "硬件加速已启用" : "hardware acceleration on";
    public static string DiagHwDisabled => Zh
        ? "硬件加速已被系统禁用（软件渲染）"
        : "hardware acceleration disabled system-wide (software rendering)";
    public static string DiagRemoteSessionGpuOn => Zh
        ? "远程会话（应用开关已启用；系统 GPU 策略已启用，应使用物理显卡）"
        : "remote session (app switch on; GPU policy on - should be on the physical GPU)";
    public static string DiagRemoteSessionAppSwitchOn => Zh
        ? "远程会话（系统 GPU 策略未启用，会话内可能仍为软件渲染；配置方法见 README）"
        : "remote session (system GPU policy off - may still be software rendering in this session; see the README)";
    public static string DiagTier0 => Zh
        ? "无可用显卡加速能力（软件渲染）"
        : "no usable GPU acceleration (software rendering)";
    public static string DiagFps(double fps) => Zh ? $"活跃帧率 ~{fps:0} fps" : $"active framerate ~{fps:0} fps";
}
