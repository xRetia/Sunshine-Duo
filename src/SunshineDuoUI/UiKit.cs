using System.Drawing.Drawing2D;
using System.ComponentModel;
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

// 圆角卡片容器
internal sealed class RoundCard : Panel
{
    private readonly int _radius;
    private readonly Color _fill;
    private readonly Color _stroke;

    public RoundCard(int radius = 12, Color? fill = null, Color? stroke = null)
    {
        _radius = radius;
        _fill = fill ?? UiKit.Card;
        _stroke = stroke ?? UiKit.Border;
        BackColor = _fill;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var p = UiKit.RoundRect(new RectangleF(0, 0, Width, Height), _radius);
        Region = new Region(p);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var p = UiKit.RoundRect(r, _radius))
        using (var brush = new SolidBrush(_fill))
            g.FillPath(brush, p);
        using (var p = UiKit.RoundRect(r, _radius))
        using (var pen = new Pen(_stroke))
            g.DrawPath(pen, p);
    }
}

// 状态胶囊：小圆点 + 文本
internal sealed class Pill : Control
{
    private Color _dot = UiKit.Muted;

    public Pill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        ForeColor = UiKit.Text;
        Font = UiKit.YaHei(9.5F, FontStyle.Bold);
        Size = new Size(150, 30);
    }

    public void Set(string text, Color dot)
    {
        Text = text;
        _dot = dot;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var p = UiKit.RoundRect(r, r.Height / 2f))
        using (var brush = new SolidBrush(Color.FromArgb(30, _dot)))
        {
            g.FillPath(brush, p);
            using var pen = new Pen(Color.FromArgb(90, _dot));
            g.DrawPath(pen, p);
        }
        var cy = Height / 2f;
        using (var dot = new SolidBrush(_dot))
            g.FillEllipse(dot, 14, cy - 4, 8, 8);
        TextRenderer.DrawText(g, Text, Font, new Point(28, (int)cy - Font.Height / 2 - 1), ForeColor);
    }
}

// 两/多段切换条（标准|高级 等）：自绘，主题一致
internal sealed class SegBar : Control
{
    private readonly string[] _labels;
    private readonly List<RectangleF> _segs = new();
    private int _index;

    public SegBar(params string[] labels)
    {
        _labels = labels;
        Height = 30;
        Width = labels.Length * 100 + 4;
        Cursor = Cursors.Hand;
        Font = UiKit.YaHei(9F, FontStyle.Bold);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public event EventHandler? Changed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _index;
        set
        {
            var v = Math.Clamp(value, 0, _labels.Length - 1);
            if (v == _index) return;
            _index = v;
            Invalidate();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        for (var i = 0; i < _segs.Count; i++)
        {
            if (_segs[i].Contains(e.Location)) { SelectedIndex = i; return; }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _segs.Clear();
        var outer = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var p = UiKit.RoundRect(outer, 7))
        using (var brush = new SolidBrush(UiKit.Card))
            g.FillPath(brush, p);
        using (var p = UiKit.RoundRect(outer, 7))
        using (var pen = new Pen(UiKit.Border))
            g.DrawPath(pen, p);

        var segW = (Width - 6f) / _labels.Length;
        var segH = Height - 5f;
        for (var i = 0; i < _labels.Length; i++)
        {
            var r = new RectangleF(3f + i * segW, 2.5f, segW, segH);
            _segs.Add(r);
            if (i == _index)
            {
                using var p = UiKit.RoundRect(new RectangleF(r.X + 1, r.Y, r.Width - 2, r.Height), 6);
                using var brush = new SolidBrush(UiKit.AccentDeep);
                g.FillPath(brush, p);
            }
            TextRenderer.DrawText(g, _labels[i], Font, Rectangle.Round(r),
                i == _index ? Color.White : UiKit.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

// 自绘开关：不依赖系统 CheckBox 配色，浅色/深色主题都正确
internal sealed class SwitchRow : Control
{
    private bool _checked;
    private bool _hover;

    public SwitchRow(string text, bool isChecked)
    {
        Text = text;
        _checked = isChecked;
        Cursor = Cursors.Hand;
        Font = UiKit.YaHei(9.5F);
        ForeColor = UiKit.Text;
        Size = new Size(320, 30);
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !_checked;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            Checked = !_checked;
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var textColor = UiKit.Text;
        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(0, 0, Width - 60, Height), textColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);

        var trackW = 38f;
        var trackH = 20f;
        var track = new RectangleF(Width - trackW, (Height - trackH) / 2f, trackW, trackH);
        var trackColor = _checked ? UiKit.Accent
            : UiKit.IsLight ? Color.FromArgb(198, 206, 216) : Color.FromArgb(64, 71, 82);
        using (var p = UiKit.RoundRect(track, trackH / 2f))
        using (var brush = new SolidBrush(trackColor))
            g.FillPath(brush, p);

        var knob = 14f;
        var knobX = _checked ? track.Right - knob - 3f : track.Left + 3f;
        using (var brush = new SolidBrush(Color.White))
            g.FillEllipse(brush, knobX, track.Y + (trackH - knob) / 2f, knob, knob);
        if (Focused)
        {
            using var p = UiKit.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), 6);
            using var pen = new Pen(Color.FromArgb(120, UiKit.Accent));
            g.DrawPath(pen, p);
        }
    }
}

// 侧边导航项
internal sealed class NavItem : Control
{
    private readonly Action<Graphics, RectangleF, Color> _icon;
    private bool _hover;

    public NavItem(string text, Action<Graphics, RectangleF, Color> icon)
    {
        _icon = icon;
        Text = text;
        Cursor = Cursors.Hand;
        Font = UiKit.YaHei(10.5F);
        ForeColor = UiKit.Text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get; set; }

    // 行尾状态小胶囊（空字符串 = 不显示）
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Invalidate();
        }
    }

    private string _status = "";
    public event EventHandler? Activated;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Activated?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Selected || _hover)
        {
            using var p = UiKit.RoundRect(new RectangleF(0, 0, Width, Height), 10);
            using var brush = new SolidBrush(Selected ? UiKit.AccentTint : UiKit.CardHover);
            g.FillPath(brush, p);
        }
        if (Selected)
        {
            using var bar = UiKit.RoundRect(new RectangleF(2, Height / 2f - 11, 3, 22), 1.5f);
            using var brush = new SolidBrush(UiKit.Accent);
            g.FillPath(brush, bar);
        }
        var iconColor = Selected ? UiKit.Accent : UiKit.Muted;
        var box = new RectangleF(16, Height / 2f - 11, 22, 22);
        _icon(g, box, iconColor);
        TextRenderer.DrawText(g, Text, Font, new Point(48, Height / 2 - Font.Height / 2 - 1), UiKit.Text);
        if (_status.Length > 0)
        {
            var f = UiKit.YaHei(8.5F, FontStyle.Bold);
            var tw = TextRenderer.MeasureText(_status, f).Width;
            var pill = new RectangleF(Width - tw - 34, Height / 2f - 10f, tw + 22, 20f);
            using (var p = UiKit.RoundRect(pill, 10))
            using (var brush = new SolidBrush(Color.FromArgb(30, UiKit.Accent)))
                g.FillPath(brush, p);
            using (var dot = new SolidBrush(UiKit.Accent))
                g.FillEllipse(dot, pill.X + 8, Height / 2f - 3.5f, 7, 7);
            TextRenderer.DrawText(g, _status, f,
                new Point((int)pill.X + 19, Height / 2 - f.Height / 2 - 1), UiKit.Accent);
        }
    }
}

// 大图标 + 小文字的可选模式卡
internal sealed class ModeCard : Control
{
    private readonly Action<Graphics, RectangleF, Color> _icon;
    private bool _hover;

    public ModeCard(string title, string desc, Action<Graphics, RectangleF, Color> icon)
    {
        _icon = icon;
        Title = title;
        Desc = desc;
        Cursor = Cursors.Hand;
        Size = new Size(232, 138);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Desc { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get; set; }
    public event EventHandler? Activated;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Activated?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        var back = Selected ? UiKit.AccentTint : (_hover ? UiKit.CardHover : UiKit.Card);
        using (var p = UiKit.RoundRect(r, 14))
        using (var brush = new SolidBrush(back))
            g.FillPath(brush, p);
        using (var p = UiKit.RoundRect(r, 14))
        using (var pen = new Pen(Selected ? UiKit.Accent : UiKit.Border, Selected ? 1.6f : 1f))
            g.DrawPath(pen, p);

        var iconColor = Selected ? UiKit.Accent : UiKit.Muted;
        _icon(g, new RectangleF(Width / 2f - 26, 16, 52, 52), iconColor);

        var titleFont = UiKit.YaHei(12F, FontStyle.Bold);
        TextRenderer.DrawText(g, Title, titleFont,
            new Rectangle(8, 74, Width - 16, 22), Selected ? UiKit.Accent : UiKit.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        var descFont = UiKit.YaHei(8.5F);
        TextRenderer.DrawText(g, Desc, descFont,
            new Rectangle(12, 98, Width - 24, 34), UiKit.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }
}

// 只画图标的小面板（侧栏底部锁、关于页头像等）
internal sealed class IconBox : Control
{
    private readonly Action<Graphics, RectangleF, Color> _icon;
    private readonly Color _color;

    public IconBox(Action<Graphics, RectangleF, Color> icon, Color color, int size = 16)
    {
        _icon = icon;
        _color = color;
        Size = new Size(size, size);
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        _icon(e.Graphics, new RectangleF(0, 0, Width, Height), _color);
    }
}

// 线性图标：全部按传入矩形等比绘制
internal static class Glyphs
{
    private static Pen Stroke(Color c, float w)
    {
        return new Pen(c, w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
    }

    private static void Screen(Graphics g, RectangleF r, Color c, float weight)
    {
        using var p = UiKit.RoundRect(r, r.Width * 0.10f);
        using var pen = Stroke(c, weight);
        g.DrawPath(pen, p);
    }

    public static void Monitor(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.4f, r.Width / 14f);
        var body = new RectangleF(r.X + r.Width * 0.08f, r.Y + r.Height * 0.12f,
                                  r.Width * 0.84f, r.Height * 0.56f);
        Screen(g, body, c, w);
        var cx = r.X + r.Width / 2f;
        using var pen = Stroke(c, w);
        g.DrawLine(pen, cx, body.Bottom, cx, r.Y + r.Height * 0.82f);
        g.DrawLine(pen, cx - r.Width * 0.22f, r.Y + r.Height * 0.84f,
                       cx + r.Width * 0.22f, r.Y + r.Height * 0.84f);
    }

    public static void Gamepad(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.4f, r.Width / 15f);
        var body = new RectangleF(r.X + r.Width * 0.04f, r.Y + r.Height * 0.26f,
                                  r.Width * 0.92f, r.Height * 0.48f);
        using (var p = UiKit.RoundRect(body, body.Height * 0.45f))
        using (var pen = Stroke(c, w))
            g.DrawPath(pen, p);
        using (var dp = Stroke(c, w * 0.9f))
        {
            var lx = body.X + body.Width * 0.24f;
            var cy = body.Y + body.Height / 2f;
            g.DrawLine(dp, lx - body.Height * 0.20f, cy, lx + body.Height * 0.20f, cy);
            g.DrawLine(dp, lx, cy - body.Height * 0.20f, lx, cy + body.Height * 0.20f);
        }
        using var b1 = new SolidBrush(c);
        var rx = body.X + body.Width * 0.74f;
        var rcy = body.Y + body.Height * 0.36f;
        var rad = Math.Max(1.6f, r.Width * 0.055f);
        g.FillEllipse(b1, rx - rad, rcy - rad, rad * 2, rad * 2);
        g.FillEllipse(b1, rx + rad, rcy + rad * 1.2f, rad * 2, rad * 2);
    }

    public static void StackedDisplays(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.3f, r.Width / 16f);
        using (var fade = new SolidBrush(Color.FromArgb(120, c)))
        using (var pen = new Pen(fade.Color, w) { LineJoin = LineJoin.Round })
        {
            var back = new RectangleF(r.X + r.Width * 0.30f, r.Y + r.Height * 0.06f,
                                      r.Width * 0.62f, r.Height * 0.42f);
            using var p = UiKit.RoundRect(back, back.Width * 0.10f);
            g.DrawPath(pen, p);
        }
        var front = new RectangleF(r.X + r.Width * 0.06f, r.Y + r.Height * 0.34f,
                                   r.Width * 0.66f, r.Height * 0.44f);
        Screen(g, front, c, w);
        using var pen2 = Stroke(c, w);
        var cx = front.X + front.Width / 2f;
        g.DrawLine(pen2, cx, front.Bottom, cx, r.Y + r.Height * 0.92f);
    }

    public static void Gear(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.4f, r.Width / 16f);
        var cx = r.X + r.Width / 2f;
        var cy = r.Y + r.Height / 2f;
        var outer = Math.Min(r.Width, r.Height) / 2f;
        using (var pen = Stroke(c, w * 1.9f))
        {
            for (var i = 0; i < 8; i++)
            {
                var a = Math.PI / 4 * i;
                g.DrawLine(pen, cx + (float)(Math.Cos(a) * outer * 0.62),
                                cy + (float)(Math.Sin(a) * outer * 0.62),
                                cx + (float)(Math.Cos(a) * outer * 0.95),
                                cy + (float)(Math.Sin(a) * outer * 0.95));
            }
        }
        using (var pen = Stroke(c, w))
            g.DrawEllipse(pen, cx - outer * 0.58f, cy - outer * 0.58f, outer * 1.16f, outer * 1.16f);
        using var pen2 = Stroke(c, w);
        g.DrawEllipse(pen2, cx - outer * 0.24f, cy - outer * 0.24f, outer * 0.48f, outer * 0.48f);
    }

    public static void Info(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.4f, r.Width / 15f);
        var outer = Math.Min(r.Width, r.Height);
        using (var pen = Stroke(c, w))
            g.DrawEllipse(pen, r.X + (r.Width - outer) / 2f, r.Y, outer, outer);
        var cx = r.X + r.Width / 2f;
        using (var dot = new SolidBrush(c))
            g.FillEllipse(dot, cx - w, r.Y + outer * 0.22f, w * 2, w * 2);
        using var pen2 = Stroke(c, w * 1.1f);
        g.DrawLine(pen2, cx, r.Y + outer * 0.44f, cx, r.Y + outer * 0.76f);
    }

    public static void Lock(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.2f, r.Width / 14f);
        var body = new RectangleF(r.X + r.Width * 0.16f, r.Y + r.Height * 0.44f,
                                  r.Width * 0.68f, r.Height * 0.46f);
        using (var p = UiKit.RoundRect(body, 2.5f))
        using (var brush = new SolidBrush(c))
            g.FillPath(brush, p);
        using var pen = Stroke(c, w);
        var arc = new RectangleF(body.X + body.Width * 0.20f, r.Y + r.Height * 0.14f,
                                 body.Width * 0.60f, body.Height * 0.72f);
        g.DrawArc(pen, arc, 180, 180);
    }

    public static void Mirror(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.3f, r.Width / 17f);
        var back = new RectangleF(r.X + r.Width * 0.40f, r.Y + r.Height * 0.04f,
                                  r.Width * 0.56f, r.Height * 0.32f);
        using (var pen = new Pen(Color.FromArgb(130, c), w) { LineJoin = LineJoin.Round })
        using (var p = UiKit.RoundRect(back, back.Width * 0.10f))
            g.DrawPath(pen, p);
        var front = new RectangleF(r.X + r.Width * 0.08f, r.Y + r.Height * 0.38f,
                                   r.Width * 0.56f, r.Height * 0.32f);
        Screen(g, front, c, w);
        var cx = front.X + front.Width / 2f;
        using var pen2 = Stroke(c, w);
        g.DrawLine(pen2, cx, front.Bottom, cx, r.Y + r.Height * 0.80f);
        g.DrawLine(pen2, cx - front.Width * 0.20f, r.Y + r.Height * 0.84f,
                        cx + front.Width * 0.20f, r.Y + r.Height * 0.84f);
    }

    public static void Exclusive(Graphics g, RectangleF r, Color c)
    {
        Monitor(g, r, c);
        var inner = new RectangleF(r.X + r.Width * 0.26f, r.Y + r.Height * 0.22f,
                                   r.Width * 0.48f, r.Height * 0.36f);
        using var brush = new SolidBrush(Color.FromArgb(150, c));
        using var p = UiKit.RoundRect(inner, 2.5f);
        g.FillPath(brush, p);
    }

    public static void Extend(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.3f, r.Width / 18f);
        var left = new RectangleF(r.X + r.Width * 0.04f, r.Y + r.Height * 0.14f,
                                  r.Width * 0.42f, r.Height * 0.40f);
        var right = new RectangleF(r.X + r.Width * 0.54f, r.Y + r.Height * 0.14f,
                                   r.Width * 0.42f, r.Height * 0.40f);
        Screen(g, left, c, w);
        Screen(g, right, c, w);
        using var pen = Stroke(c, w);
        foreach (var side in new[] { left, right })
        {
            var cx = side.X + side.Width / 2f;
            g.DrawLine(pen, cx, side.Bottom, cx, r.Y + r.Height * 0.70f);
            g.DrawLine(pen, cx - side.Width * 0.22f, r.Y + r.Height * 0.74f,
                            cx + side.Width * 0.22f, r.Y + r.Height * 0.74f);
        }
    }

    public static void Tunnel(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.3f, r.Width / 15f);
        var cx = r.X + r.Width / 2f;
        var cy = r.Y + r.Height / 2f;
        var rad = Math.Min(r.Width, r.Height) * 0.44f;
        using (var pen = Stroke(c, w))
        {
            g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
            g.DrawEllipse(pen, cx - rad, cy - rad * 0.42f, rad * 2, rad * 0.84f);
            g.DrawLine(pen, cx - rad, cy, cx + rad, cy);
            g.DrawLine(pen, cx, cy - rad, cx, cy + rad);
        }
    }

    public static void Refresh(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.2f, r.Width / 13f);
        var d = Math.Min(r.Width, r.Height) * 0.78f;
        var box = new RectangleF(r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
        using (var pen = Stroke(c, w))
            g.DrawArc(pen, box, 40, 250);
        var tip = box.Right - d * 0.10f;
        using var brush = new SolidBrush(c);
        g.FillPolygon(brush, new[]
        {
            new PointF(tip, box.Y + d * 0.22f),
            new PointF(tip - d * 0.22f, box.Y + d * 0.06f),
            new PointF(tip - d * 0.10f, box.Y + d * 0.40f),
        });
    }

    public static void Copy(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.1f, r.Width / 14f);
        var back = new RectangleF(r.X + r.Width * 0.30f, r.Y, r.Width * 0.60f, r.Height * 0.66f);
        var front = new RectangleF(r.X, r.Y + r.Height * 0.30f, r.Width * 0.62f, r.Height * 0.70f);
        using (var pen = Stroke(c, w))
        using (var p = UiKit.RoundRect(back, 2f))
            g.DrawPath(pen, p);
        using (var pen = Stroke(c, w))
        using (var p = UiKit.RoundRect(front, 2f))
        {
            g.FillPath(new SolidBrush(Color.FromArgb(60, c)), p);
            g.DrawPath(pen, p);
        }
    }

    // 外链：方框 + 右上箭头
    public static void External(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.2f, r.Width / 14f);
        var box = new RectangleF(r.X + r.Width * 0.06f, r.Y + r.Height * 0.20f,
                                 r.Width * 0.60f, r.Height * 0.60f);
        using (var pen = Stroke(c, w))
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
        using (var pen = Stroke(c, w))
        {
            var tip = new PointF(r.X + r.Width * 0.92f, r.Y + r.Height * 0.08f);
            g.DrawLine(pen, r.X + r.Width * 0.44f, r.Y + r.Height * 0.56f, tip.X, tip.Y);
            g.DrawLine(pen, tip.X - r.Width * 0.26f, tip.Y, tip.X, tip.Y);
            g.DrawLine(pen, tip.X, tip.Y, tip.X, tip.Y + r.Height * 0.26f);
        }
    }
}
