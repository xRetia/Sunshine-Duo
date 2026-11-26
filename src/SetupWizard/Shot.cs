using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SunshineDuoSetup;

// 离屏渲染各页面用于 UI 自检：窗口以不激活方式短暂闪现在屏幕坐标之外
// （PrintWindow 对从未显示的窗体会得到空图，必须真实显示一帧）。
// 不抢焦点、不进任务栏常驻，逐页显示-抓图-隐藏，全程约 2 秒。
internal static class Shot
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndAfter, int X, int Y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOREDRAW = 0x0008;
    private const int SW_SHOWNOACTIVATE = 4;

    public static int Run(string dir)
    {
        AttachConsole(-1);
        try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }); } catch { }
        try
        {
            Directory.CreateDirectory(dir);
            ApplicationConfiguration.Initialize();
            using var form = new MainForm(false);
            form.CreateControl();
            Application.DoEvents();

            // 屏幕外坐标（多屏/单屏均远离可视区域），不激活显示一帧让控件完成真实绘制
            var h = form.Handle;
            SetWindowPos(h, IntPtr.Zero, 2600, 1600, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            ShowWindow(h, SW_SHOWNOACTIVATE);
            form.Visible = true;
            Application.DoEvents();
            ForceRepaint(form);
            Thread.Sleep(600);

            foreach (var id in new[] { "w", "s", "u", "v", "about" })
            {
                form.Navigate(id);
                ForceRepaint(form);
                Snap(h, form, Path.Combine(dir, "page-" + id + ".png"));
            }

            var wizard = form.Wizard;
            if (wizard is not null)
            {
                form.Navigate("s");
                for (var i = 0; i < 4; i++)
                {
                    wizard.DebugStep(i);
                    ForceRepaint(form);
                    Console.WriteLine("DUMP " + wizard.DebugDump(i));
                    Snap(h, form, Path.Combine(dir, "wizard-" + (i + 1) + ".png"));
                }
                // 单会话态的账户页（左卡留白与右卡等高）
                wizard.DebugSelectMode(dual: false);
                wizard.DebugStep(1);
                ForceRepaint(form);
                Snap(h, form, Path.Combine(dir, "wizard-2-single.png"));
                wizard.DebugSelectMode(dual: true);
            }

            // 结果页：用样例数据渲染（双会话成功态）
            if (form.Page("f") is ResultPage result)
            {
                var mock = new Opts
                {
                    SingleSession = false, WebUser = "demo", WebUiPassword = "Demo-Pass-1234",
                    PortMain = 48100, PortGame = 48200,
                };
                result.ShowResult(true, hasFail: false, aborted: false, mock,
                    new List<(string Key, string Label, bool Ok)>
                    {
                        ("conf", "写入 串流配置", true),
                        ("firewall", "配置网络访问", true),
                        ("core", "会话守护服务与自动启动", true),
                        ("vda", "虚拟显示器驱动", true),
                        ("tray", "托盘与快捷方式", true),
                    });
                form.Navigate("f");
                ForceRepaint(form);
                Snap(h, form, Path.Combine(dir, "page-f.png"));
            }

            form.Hide();
            Console.WriteLine("shots -> " + dir);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("shot failed: " + ex);
            return 1;
        }
    }

    // 递归标记全部子窗口失效并同步刷新一次，确保 PrintWindow 抓到的是首次真实绘制而非空缓存
    private static void ForceRepaint(Control root)
    {
        if (root is ContainerControl cc) cc.ActiveControl = null; // 避免焦点框出现在截图里
        root.Invalidate(true);
        Application.DoEvents();
        root.Update();
        Application.DoEvents();
        Thread.Sleep(120);
    }

    private static void Snap(IntPtr hWnd, Control host, string path)
    {
        if (host.Width <= 0 || host.Height <= 0) return;
        using var bmp = new Bitmap(host.Width, host.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Ui.Bg);
            var hdc = g.GetHdc();
            try { PrintWindow(hWnd, hdc, 2 /* PW_CLIENTONLY | PW_RENDERFULLCONTENT */); }
            finally { g.ReleaseHdc(hdc); }
        }
        bmp.Save(path, ImageFormat.Png);
    }
}
