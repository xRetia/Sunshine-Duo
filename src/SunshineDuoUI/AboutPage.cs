using System.Diagnostics;

namespace SunshineDuoTray;

internal sealed class AboutPage : Panel
{
    private Label _versionLabel = null!;
    private Panel? _updateDot;

    public AboutPage()
    {
        BackColor = UiKit.WindowBg;
        Font = UiKit.YaHei(9F);

        var title = UiKit.Title("关于");
        title.Location = new Point(0, 24);
        Controls.Add(title);
        var sub = UiKit.Sub("Sunshine Duo 托盘 · 双 Apollo 串流管理");
        sub.Location = new Point(0, 62);
        Controls.Add(sub);

        var avatar = new PictureBox
        {
            Location = new Point(0, 108),
            Size = new Size(64, 64),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = UiKit.Card,
            Image = TrayIcons.Avatar(64),
        };
        Controls.Add(avatar);

        Controls.Add(new Label
        {
            Text = "Sunshine Duo",
            ForeColor = UiKit.Text,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 18F, FontStyle.Bold),
            Location = new Point(80, 110),
            AutoSize = true,
        });

        _versionLabel = new Label
        {
            Text = "托盘 v" + UpdateCheck.LocalVersion + " · 莫氏萌艺风格的双会话串流派生",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9F),
            Location = new Point(82, 146),
            AutoSize = true,
            Cursor = Cursors.Hand,
        };
        _versionLabel.Click += (_, _) => StartUpdate();
        Controls.Add(_versionLabel);

        _updateDot = NewUpdateDot();
        _updateDot.Location = new Point(82 + _versionLabel.PreferredWidth + 8, 148);
        Controls.Add(_updateDot);

        _ = CheckUpdateAsync();

        var author = new RoundCard { Location = new Point(0, 204), Size = new Size(736, 104) };
        Controls.Add(author);
        author.Controls.Add(UiKit.Caption("作者", UiKit.Muted, 8.5F));
        author.Controls[0].Location = new Point(16, 14);
        author.Controls.Add(new Label
        {
            Text = "苏羽茜工作室（xRetia Labs.）",
            ForeColor = UiKit.Text,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(10.5F),
            Location = new Point(16, 36),
            AutoSize = true,
        });
        author.Controls.Add(new Label
        {
            Text = "产品主页 · yk.acgdraw.com",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.Mono(9F),
            Location = new Point(16, 66),
            AutoSize = true,
        });
        var checkUpdate = UiKit.Ghost("检查更新");
        checkUpdate.Location = new Point(440, 40);
        checkUpdate.Size = new Size(120, 32);
        checkUpdate.Click += async (_, _) =>
        {
            checkUpdate.Enabled = false;
            checkUpdate.Text = "检查中…";
            try
            {
                var (has, remote, local) = await UpdateCheck.CheckAsync(force: true);
                if (has)
                {
                    _updateDot!.Visible = true;
                    _updateDot.BringToFront();
                    if (MessageBox.Show($"发现新版本 v{remote}（当前 v{local}），是否立即打开安装向导更新？",
                        "发现更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        StartUpdate();
                }
                else
                {
                    MessageBox.Show($"当前已是最新版本（v{local}）。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("检查失败：" + ex.Message, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                checkUpdate.Text = "检查更新";
                checkUpdate.Enabled = true;
            }
        };
        author.Controls.Add(checkUpdate);

        var open = UiKit.Ghost("打开主页");
        open.Location = new Point(568, 40);
        open.Size = new Size(136, 32);
        open.Click += (_, _) => OpenUrl("https://yk.acgdraw.com");
        author.Controls.Add(open);

        var tech = new RoundCard { Location = new Point(0, 324), Size = new Size(736, 132) };
        Controls.Add(tech);
        tech.Controls.Add(UiKit.Caption("架构", UiKit.Muted, 8.5F));
        tech.Controls[0].Location = new Point(16, 14);
        var lines = new[]
        {
            "双 Apollo 实例：Console SYSTEM 物理屏 / Game 用户桌面 RDP 会话",
            "SudoVDA 虚拟显示器 · 拓扑由 SunshineDuoCore 按各自流生命周期管理",
            "Moonlight / Apollo 协议 · 本机控制管道：\\\\.\\pipe\\SunshineDuoCore-ctrl",
        };
        for (var i = 0; i < lines.Length; i++)
            tech.Controls.Add(new Label
            {
                Text = "·  " + lines[i],
                ForeColor = i == 0 ? UiKit.Text : UiKit.Muted,
                BackColor = Color.Transparent,
                Font = UiKit.YaHei(9F),
                Location = new Point(16, 38 + i * 26),
                AutoSize = true,
            });

        Controls.Add(new Label
        {
            Text = "本工具只管理本机串流服务，不收集、不上传任何数据。",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(8.5F),
            Location = new Point(0, 476),
            AutoSize = true,
        });
    }

    private Panel NewUpdateDot()
    {
        var dot = new Panel
        {
            Size = new Size(8, 8),
            BackColor = UiKit.Danger,
            Visible = false,
            Cursor = Cursors.Hand,
        };
        using var path = UiKit.RoundRect(new RectangleF(0, 0, 8, 8), 4);
        dot.Region = new Region(path);
        dot.Click += (_, _) => StartUpdate();
        return dot;
    }

    private async Task CheckUpdateAsync()
    {
        var (has, _, _) = await UpdateCheck.CheckAsync();
        if (has && IsHandleCreated && !IsDisposed)
            BeginInvoke(() =>
            {
                _updateDot!.Visible = true;
                _updateDot.BringToFront();
            });
    }

    private void StartUpdate() => UpdateCheck.StartUpdate();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}