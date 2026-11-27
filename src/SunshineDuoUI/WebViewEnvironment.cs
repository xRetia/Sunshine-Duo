using Microsoft.Web.WebView2.Core;

namespace SunshineDuoTray;

// 主界面与 Apollo WebUI 窗口共用一个浏览器进程组，避免每个窗口各起一套进程。
internal static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? _creating;

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
        return await CoreWebView2Environment.CreateAsync(null, folder, options);
    }
}
