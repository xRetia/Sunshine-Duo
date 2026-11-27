using System.Drawing.Drawing2D;

using Microsoft.Win32;

namespace SunshineDuoTray;

internal enum AppTheme
{
    System,
    Light,
    Dark,
}

// 主题解析：system 跟随注册表 AppsUseLightTheme，light/dark 手动锁定
internal static class Theme
{
    public static AppTheme Mode { get; private set; } = AppTheme.System;

    public static void Apply(string mode)
    {
        Mode = mode switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.System,
        };
        UiKit.SetLight(Mode == AppTheme.Light || (Mode == AppTheme.System && SystemIsLight()));
    }

    public static bool SystemIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int i) return i == 1;
        }
        catch { }
        return false;
    }
}

// 主界面调色板与控件：深色/浅色两套，全部自绘，避免系统控件在深色下失真
internal static class UiKit
{
    private static bool _light;

    public static bool IsLight => _light;
    public static void SetLight(bool light) => _light = light;

    public static Color WindowBg => _light ? Color.FromArgb(246, 247, 250) : Color.FromArgb(14, 16, 19);
    public static Color SidebarBg => _light ? Color.FromArgb(238, 240, 244) : Color.FromArgb(19, 22, 26);
    public static Color Card => _light ? Color.White : Color.FromArgb(28, 32, 38);
    public static Color CardHover => _light ? Color.FromArgb(242, 244, 248) : Color.FromArgb(36, 41, 48);
    public static Color FieldBg => _light ? Color.FromArgb(249, 250, 252) : Color.FromArgb(20, 23, 28);
    public static Color Border => _light ? Color.FromArgb(220, 225, 232) : Color.FromArgb(45, 51, 60);
    public static Color Text => _light ? Color.FromArgb(26, 30, 36) : Color.FromArgb(240, 243, 247);
    public static Color Muted => _light ? Color.FromArgb(104, 113, 125) : Color.FromArgb(146, 155, 167);
    public static Color Accent => _light ? Color.FromArgb(22, 163, 74) : Color.FromArgb(34, 197, 94);
    public static Color AccentDeep => _light ? Color.FromArgb(21, 128, 61) : Color.FromArgb(22, 140, 68);
    public static Color AccentTint => _light ? Color.FromArgb(232, 247, 237) : Color.FromArgb(26, 40, 32);
    public static Color Danger => _light ? Color.FromArgb(214, 69, 58) : Color.FromArgb(240, 92, 82);
    public static Color Warn => _light ? Color.FromArgb(186, 118, 18) : Color.FromArgb(240, 168, 56);

    public static Font YaHei(float size, FontStyle style = FontStyle.Regular)
        => new("Microsoft YaHei UI", size, style);

    public static Font Mono(float size, FontStyle style = FontStyle.Regular)
        => new("Consolas", size, style);

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var d = Math.Max(1f, Math.Min(radius * 2f, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        if (d <= 1f || r.Width <= 0 || r.Height <= 0)
        {
            p.AddRectangle(r);
            p.CloseFigure();
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Label Title(string text, float size = 18F)
        => new()
        {
            Text = text,
            ForeColor = Text,
            BackColor = Color.Transparent,
            Font = YaHei(size, FontStyle.Bold),
            AutoSize = true,
        };

    public static Label Sub(string text)
        => new()
        {
            Text = text,
            ForeColor = Muted,
            BackColor = Color.Transparent,
            Font = YaHei(9.5F),
            AutoSize = true,
        };

    public static Label Caption(string text, Color? color = null, float size = 9.5F)
        => new()
        {
            Text = text,
            ForeColor = color ?? Muted,
            BackColor = Color.Transparent,
            Font = YaHei(size),
            AutoSize = true,
        };

    public static Button Primary(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = AccentDeep,
            ForeColor = Color.White,
            Font = YaHei(10.5F, FontStyle.Bold),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Accent;
        b.FlatAppearance.MouseDownBackColor = AccentDeep;
        return b;
    }

    public static Button Ghost(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Card,
            ForeColor = Text,
            Font = YaHei(9.5F),
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = CardHover;
        return b;
    }

    // 带前置图标的浅色按钮（外链/操作类，与 WebUI 的图标按钮观感对齐）
    public static Button GhostIcon(string text, Action<Graphics, RectangleF, Color> icon)
    {
        var b = new Button
        {
            Text = string.Empty,
            FlatStyle = FlatStyle.Flat,
            BackColor = Card,
            ForeColor = Text,
            Font = YaHei(9.5F),
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = CardHover;
        var w = TextRenderer.MeasureText(text, b.Font).Width;
        b.Size = new Size(Math.Max(96, w + 44), 34);
        b.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            icon(g, new RectangleF(11, (b.Height - 14) / 2f, 14, 14), Muted);
            TextRenderer.DrawText(g, text, b.Font,
                new Rectangle(31, 0, b.Width - 36, b.Height), Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        };
        return b;
    }

    public static TextBox Field(string text = "", bool password = false)
    {
        var t = new TextBox
        {
            Text = text,
            BackColor = FieldBg,
            ForeColor = Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = YaHei(10F),
        };
        if (password) t.UseSystemPasswordChar = true;
        return t;
    }


}
