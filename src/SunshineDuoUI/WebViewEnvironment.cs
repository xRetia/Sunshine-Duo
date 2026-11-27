using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace SunshineDuoTray;

// 主界面与 Apollo WebUI 窗口共用一个浏览器进程组，避免每个窗口各起一套进程。
internal static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? _creating;

    // WebView2 运行时缺失：CreateAsync 已弹原生对话框提示（可选打开官方下载页），
    // 调用方捕获本异常后应终止初始化并优雅退出，不要走通用错误分支再弹第二次框。
    internal sealed class RuntimeMissingException : Exception
    {
        public RuntimeMissingException() : base("未检测到 WebView2 运行时") { }
    }

    public static Task<CoreWebView2Environment> GetAsync()
        => _creating ??= CreateAsync();

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SunshineDuoTray", "WebView2");
        Directory.CreateDirectory(folder);
        var debug = Environment.GetEnvironmentVariable("SUNDUO_WEBUI_DEBUG") == "1"
            ? " --remote-debugging-port=9222" : "";
        var options = new CoreWebView2EnvironmentOptions(
            "--ignore-certificate-errors --allow-insecure-localhost" + debug);
        try
        {
            return await CoreWebView2Environment.CreateAsync(null, folder, options);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw Missing();
        }
        catch (Exception e) when (e.HResult == HrFileNotFound)
        {
            throw Missing();
        }
    }

    // HRESULT 0x80070002 / -2147024894（ERROR_FILE_NOT_FOUND）：运行时未安装的典型返回值
    private static readonly int HrFileNotFound = unchecked((int)0x80070002);

    // 弹原生对话框：确定 → 打开 WebView2 Evergreen Bootstrapper 官方直链；取消 → 不打开。
    // 无论选哪个，都返回待抛出的运行时缺失异常，由调用方负责优雅退出。
    private static RuntimeMissingException Missing()
    {
        _creating = null; // 失败结果不缓存，用户安装运行时后无需重启即可重试
        const string text =
            "未检测到 WebView2 运行时，托盘界面无法显示。\n\n"
            + "点击「确定」打开下载页面安装后重试。";
        if (Native.MessageBoxW(IntPtr.Zero, text, "萌远控 Duo", 0x00000001 /* MB_OKCANCEL */)
            == 1 /* IDOK */)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703")
                {
                    UseShellExecute = true,
                });
            }
            catch { /* 打不开浏览器不影响退出流程 */ }
        }
        return new RuntimeMissingException();
    }

    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    }
}
