using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace SunshineDuoSetup;

// 安装前置检查的阻断弹窗：托盘界面依赖 WebView2 运行时，缺失时给出「下载并安装」或「退出安装」两个动作。
// 检测与弹窗收口在 EnsureInstalled，安装流程任意入口都能复用，判定逻辑只此一份。
internal sealed class WebView2Gate : Form
{
    public const string DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private WebView2Gate()
    {
        Text = "缺少 WebView2 运行时";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 216);
        BackColor = Ui.Bg;
        Font = Ui.F(9.5F);
        DoubleBuffered = true;

        var card = new RoundCard
        {
            Location = new Point(12, 12), Size = new Size(436, 192),
        };
        Controls.Add(card);
        card.Controls.Add(new Label
        {
            Text = "安装被阻止", ForeColor = Ui.Danger, BackColor = Color.Transparent,
            Font = Ui.F(13F, FontStyle.Bold), Location = new Point(20, 16), AutoSize = true,
        });
        card.Controls.Add(new Label
        {
            Text = "托盘界面依赖 WebView2 运行时，未安装无法继续安装。",
            ForeColor = Ui.Text, BackColor = Color.Transparent,
            Font = Ui.F(10F), Location = new Point(20, 46), Size = new Size(400, 30),
        });

        var download = Ui.Primary("下载并安装 WebView2");
        download.Size = new Size(170, 34);
        download.Location = new Point(220, 150);
        download.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        card.Controls.Add(download);

        var exit = Ui.Secondary("退出安装");
        exit.Size = new Size(130, 34);
        exit.Location = new Point(20, 150);
        exit.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        card.Controls.Add(exit);
    }

    public static bool EnsureInstalled(IWin32Window? owner)
    {
        if (Detect.WebView2().Installed) return true;

        var dlg = new WebView2Gate();
        if (dlg.ShowDialog(owner) != DialogResult.OK) return false;

        try
        {
            Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
        }
        catch { }
        MessageBox.Show("已打开 WebView2 下载页。\n安装完成后，请重新运行本程序。",
            "WebView2 运行时", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false; // 未装完前不继续安装
    }
}
