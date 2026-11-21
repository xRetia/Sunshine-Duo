using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using Microsoft.Win32;

namespace SunshineDuoTray;

public class WebViewForm : Form
{
    private readonly WebView2 _view;
    private readonly AppConfig _cfg;
    private readonly int _port;
    private readonly string _path;
    private bool _bootstrapped;

    public WebViewForm(AppConfig cfg, string title, int port, string path)
    {
        _cfg = cfg;
        _port = port;
        _path = path;
        Text = title;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1200, 820);
        MinimumSize = new Size(760, 520);
        Font = new Font("Microsoft YaHei UI", 9F);

        _view = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_view);
        _view.NavigationCompleted += ViewOnNavigationCompleted;
        Shown += async (_, _) => await InitAsync();
    }

    // 系统深色/浅色主题检测
    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key == null) return false;
            var v = key.GetValue("AppsUseLightTheme");
            if (v is int i) return i == 1;
        }
        catch { }
        return false;
    }

    private async Task InitAsync()
    {
        if (_bootstrapped) return;
        _bootstrapped = true;
        try
        {
            var light = IsSystemLightTheme();
            var args = "--ignore-certificate-errors --allow-insecure-localhost";
            if (!light) args += " --force-dark-mode";
            var options = new CoreWebView2EnvironmentOptions(args);
            var env = await CoreWebView2Environment.CreateAsync(null, null, options);
            await _view.EnsureCoreWebView2Async(env);

            if (!string.IsNullOrEmpty(_cfg.WebUser))
            {
                using var client = new ApolloClient(_port, _cfg.WebUser, _cfg.WebPass);
                var err = await client.EnsureLoginAsync();
                if (err == null && client.AuthCookie != null)
                {
                    var cookie = _view.CoreWebView2!.CookieManager.CreateCookie(
                        "auth", client.AuthCookie, "127.0.0.1", "/");
                    cookie.IsSecure = true;
                    cookie.IsHttpOnly = true;
                    _view.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
                }
            }
            _view.CoreWebView2!.Navigate($"https://127.0.0.1:{_port}{_path}");
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "内置网页控件初始化失败：" + e.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async void ViewOnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _view.CoreWebView2 == null) return;
        var url = _view.CoreWebView2.Source;
        if (!url.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(_cfg.WebUser)) return;
        var user = JsonSerializer.Serialize(_cfg.WebUser);
        var pass = JsonSerializer.Serialize(_cfg.WebPass);
        var script = $$"""
            (() => {
              const set = (el, v) => {
                if (!el) return;
                const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
                setter.call(el, v);
                el.dispatchEvent(new Event('input', { bubbles: true }));
              };
              set(document.querySelector('#usernameInput'), {{user}});
              set(document.querySelector('#passwordInput'), {{pass}});
              document.querySelector('form button[type=submit]')?.click();
            })();
            """;
        try
        {
            await _view.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch { }
    }
}
