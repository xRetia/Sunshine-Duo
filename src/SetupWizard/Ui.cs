using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace SunshineDuoSetup;

// 页面几何：右侧内容列（居中 600 宽；页面 732 宽）
internal static class Lay
{
    public const int PageW = 732;
    public const int CW = 600;
    public const int X0 = (PageW - CW) / 2;
    public const int Gap = 12;
}

// 亮色 WinUI 调色板与控件工厂（整个安装器仅亮色）
internal static class Ui
{
    public static Color Bg => Color.FromArgb(243, 243, 243);
    public static Color Sidebar => Color.FromArgb(250, 250, 250);
    public static Color Card => Color.White;
    public static Color CardHover => Color.FromArgb(246, 246, 246);
    public static Color Field => Color.White;
    public static Color FieldBorder => Color.FromArgb(209, 209, 209);
    public static Color Text => Color.FromArgb(26, 26, 26);
    public static Color Text2 => Color.FromArgb(95, 95, 95);
    public static Color Text3 => Color.FromArgb(138, 138, 138);
    public static Color Line => Color.FromArgb(229, 229, 229);
    public static Color Accent => Color.FromArgb(34, 197, 94);       // green
    public static Color AccentHover => Color.FromArgb(45, 205, 105);
    public static Color AccentPressed => Color.FromArgb(28, 178, 78);
    public static Color AccentTint => Color.FromArgb(232, 247, 237);
    public static Color Success => Color.FromArgb(21, 128, 61);
    public static Color SuccessTint => Color.FromArgb(223, 246, 221);
    public static Color Danger => Color.FromArgb(196, 43, 28);
    public static Color Warn => Color.FromArgb(157, 93, 0);
    public static Color WarnTint => Color.FromArgb(255, 244, 206);
    public static Color TipDark => Color.FromArgb(31, 31, 31);
    public static Color TipText => Color.FromArgb(200, 200, 200);
    public static Color SwitchOff => Color.FromArgb(139, 139, 139);

    public static Font F(float size, FontStyle style = FontStyle.Regular)
        => new("Microsoft YaHei UI", size, style);
    public static Font Mono(float size) => new("Consolas", size);

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

    public static Label Title(string text, float size = 20F) => new()
    {
        Text = text, ForeColor = Text, BackColor = Color.Transparent,
        Font = F(size, FontStyle.Bold), AutoSize = true,
    };

    public static Label Head(string text, float size = 11F) => new()
    {
        Text = text, ForeColor = Text, BackColor = Color.Transparent,
        Font = F(size, FontStyle.Bold), AutoSize = true,
    };

    public static Label Sub(string text, float size = 9.5F) => new()
    {
        Text = text, ForeColor = Text2, BackColor = Color.Transparent,
        Font = F(size), AutoSize = true,
    };

    public static Label Muted(string text, float size = 8.5F) => new()
    {
        Text = text, ForeColor = Text3, BackColor = Color.Transparent,
        Font = F(size), AutoSize = true,
    };

    public static RButton Primary(string text) => new(text, RButton.Kind.Primary);
    public static RButton Secondary(string text) => new(text, RButton.Kind.Secondary);
    public static RButton DangerBtn(string text) => new(text, RButton.Kind.Danger);
    public static RButton Subtle(string text) => new(text, RButton.Kind.Subtle);

    public static TextBox FieldBox(string text = "", bool password = false)
    {
        var t = new TextBox
        {
            Text = text,
            BackColor = Color.White,
            ForeColor = Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = F(10F),
        };
        if (password) t.UseSystemPasswordChar = true;
        return t;
    }
}

// WinUI 风格按钮：圆角 4、悬停态自绘
internal sealed class RButton : Button
{
    public enum Kind { Primary, Secondary, Danger, Subtle }

    private readonly Kind _kind;
    private bool _hover, _pressed;

    public RButton(string text, Kind kind)
    {
        Text = text;
        _kind = kind;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Ui.F(9.5F, kind == Kind.Primary || kind == Kind.Danger ? FontStyle.Bold : FontStyle.Regular);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var p = Ui.RoundRect(new RectangleF(0, 0, Width, Height), 4);
        Region = new Region(p);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        Color fill, border, fore;
        switch (_kind)
        {
            case Kind.Primary:
                fill = !Enabled ? Color.FromArgb(180, 200, 220)
                     : _pressed ? Ui.AccentPressed : _hover ? Ui.AccentHover : Ui.Accent;
                border = fill; fore = Color.White;
                break;
            case Kind.Danger:
                fill = !Enabled ? Color.FromArgb(220, 170, 165)
                     : _pressed ? Color.FromArgb(170, 35, 22) : _hover ? Color.FromArgb(214, 55, 40) : Ui.Danger;
                border = fill; fore = Color.White;
                break;
            case Kind.Secondary:
                fill = _pressed ? Color.FromArgb(237, 237, 237) : _hover ? Ui.CardHover : Color.White;
                border = Ui.FieldBorder; fore = Ui.Text;
                break;
            default: // Subtle
                fill = _pressed ? Color.FromArgb(232, 232, 232) : _hover ? Color.FromArgb(242, 242, 242) : Color.Transparent;
                border = fill; fore = Ui.Text2;
                break;
        }
        if (!Enabled) fore = Color.FromArgb(160, 160, 160);

        using (var p = Ui.RoundRect(r, 4))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, p);
        if (_kind is Kind.Secondary)
        {
            using var p = Ui.RoundRect(r, 4);
            using var pen = new Pen(border);
            g.DrawPath(pen, p);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

// 圆角卡片
internal sealed class RoundCard : Panel
{
    private readonly int _radius;
    private readonly Color _fill;
    private readonly Color _stroke;

    public RoundCard(int radius = 8, Color? fill = null, Color? stroke = null)
    {
        _radius = radius;
        _fill = fill ?? Ui.Card;
        _stroke = stroke ?? Ui.Line;
        BackColor = _fill;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var p = Ui.RoundRect(new RectangleF(0, 0, Width, Height), _radius);
        Region = new Region(p);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var p = Ui.RoundRect(r, _radius))
        using (var brush = new SolidBrush(_fill))
            g.FillPath(brush, p);
        using (var p = Ui.RoundRect(r, _radius))
        using (var pen = new Pen(_stroke))
            g.DrawPath(pen, p);
    }
}

// 自动换行标签：TextRenderer 强制 WordBreak，中文长句也按宽度断行（原生 Label 不保证）
internal sealed class WrapLabel : Control
{
    private bool _sizing;

    public WrapLabel(string text, Color color, float size)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Text = text;
        ForeColor = color;
        BackColor = Color.Transparent;
        Font = Ui.F(size);
    }

    // 按当前宽度实测换行高度并自适应，避免第二行被裁
    private void FitHeight()
    {
        if (_sizing || Width <= 0) return;
        _sizing = true;
        try
        {
            var h = TextRenderer.MeasureText(Text, Font, new Size(Width, int.MaxValue),
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + 4;
            if (Height != h) Height = h;
        }
        finally { _sizing = false; }
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); FitHeight(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitHeight(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitHeight(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }
}

// 纵向自动堆叠容器：Add 控件后按加入顺序流式排布（替代手算 y +=）；
// 子项 Visible 变化后调用 Relayout() 重新收拢
internal sealed class VStack : Panel
{
    private readonly int _gap;

    public VStack(int gap = 12)
    {
        _gap = gap;
        DoubleBuffered = true;
        VisibleChanged += (_, _) => { if (Visible) Relayout(); };
    }

    public T Add<T>(T c, int left = 0) where T : Control
    {
        c.Location = new Point(left, NextY());
        Controls.Add(c);
        Height = Math.Max(Height, c.Bottom);
        return c;
    }

    public void Relayout()
    {
        foreach (Control c in Controls)
            c.Location = new Point(c.Left, NextY(c));
        var bottom = 0;
        foreach (Control c in Controls)
            if (EffVisible(c)) bottom = Math.Max(bottom, c.Bottom);
        Height = Math.Max(bottom, 1);
    }

    // 容器自身不可见时子控件的 Visible 恒为 false，此处按“未隐藏”参与布局，
    // 避免父面板隐藏期间把堆叠压扁（重新可见时由 VisibleChanged 触发重排）
    private bool EffVisible(Control c) => !Visible || c.Visible;

    private int NextY(Control? skip = null)
    {
        var y = 0;
        foreach (Control c in Controls)
        {
            if (ReferenceEquals(c, skip)) break;
            if (EffVisible(c)) y = c.Bottom + _gap;
        }
        return y;
    }
}

// 状态徽标
internal sealed class Badge : Control
{
    private Color _color = Ui.Text3;
    private Color _tint = Color.FromArgb(240, 240, 240);

    public Badge(string text, Color color)
    {
        Text = text;
        Font = Ui.F(8.5F, FontStyle.Bold);
        Size = new Size(92, 22);
        Set(text, color);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    public void Set(string text, Color color)
    {
        Text = text;
        _color = color;
        _tint = Color.FromArgb(28, color);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var p = Ui.RoundRect(r, 4))
        using (var brush = new SolidBrush(_tint))
            g.FillPath(brush, p);
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), _color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

// WinUI 开关行
internal sealed class SwitchRow : Control
{
    private bool _checked;
    private bool _hover;

    public SwitchRow(string text, bool isChecked)
    {
        Text = text;
        _checked = isChecked;
        Cursor = Cursors.Hand;
        Font = Ui.F(9.5F);
        ForeColor = Ui.Text;
        Size = new Size(360, 30);
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
        if (Enabled) Checked = !_checked;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && Enabled) { Checked = !_checked; e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var textColor = Enabled ? Ui.Text : Ui.Text3;
        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(0, 0, Width - 64, Height), textColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);

        const float trackW = 42f, trackH = 20f;
        var track = new RectangleF(Width - trackW, (Height - trackH) / 2f, trackW, trackH);
        var on = _checked && Enabled;
        var trackColor = on ? Ui.Accent
            : Enabled ? (Color.FromArgb(175, 175, 175))
            : Color.FromArgb(210, 210, 210);
        using (var p = Ui.RoundRect(track, trackH / 2f))
        using (var brush = new SolidBrush(trackColor))
            g.FillPath(brush, p);

        const float knob = 14f;
        var knobX = _checked ? track.Right - knob - 3f : track.Left + 3f;
        using var brush2 = new SolidBrush(Color.White);
        g.FillEllipse(brush2, knobX, track.Y + (trackH - knob) / 2f, knob, knob);
        if (_hover && Enabled)
        {
            using var pen = new Pen(Color.FromArgb(60, Ui.Accent), 1.2f);
            using var hp = Ui.RoundRect(track, trackH / 2f);
            g.DrawPath(pen, hp);
        }
    }
}

// 步骤圆点（01-04）
internal sealed class StepDot : Control
{
    public readonly string Number;
    private bool _active;

    public StepDot(string number)
    {
        Number = number;
        Size = new Size(30, 30);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set { _active = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        if (_active)
        {
            using var p = Ui.RoundRect(r, r.Width / 2f);
            using var brush = new SolidBrush(Ui.Accent);
            g.FillPath(brush, p);
        }
        else
        {
            using var p = Ui.RoundRect(r, r.Width / 2f);
            using (var brush = new SolidBrush(Color.White))
                g.FillPath(brush, p);
            using var pen = new Pen(Color.FromArgb(209, 209, 209));
            g.DrawPath(pen, p);
        }
        TextRenderer.DrawText(g, Number, Ui.F(9F, FontStyle.Bold), ClientRectangle,
            _active ? Color.White : Ui.Text2,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

// 可选择的模式卡（双会话 / 单会话）
internal sealed class ModeCard : Control
{
    private bool _hover;

    public ModeCard(string title, string desc)
    {
        Title = title;
        Desc = desc;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
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

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var p = Ui.RoundRect(new RectangleF(0, 0, Width, Height), 8);
        Region = new Region(p);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        using (var p = Ui.RoundRect(r, 8))
        using (var brush = new SolidBrush(Selected ? Ui.AccentTint : (_hover ? Ui.CardHover : Color.White)))
            g.FillPath(brush, p);
        using (var p = Ui.RoundRect(r, 8))
        using (var pen = new Pen(Selected ? Ui.Accent : Ui.Line, Selected ? 2f : 1f))
            g.DrawPath(pen, p);

        TextRenderer.DrawText(g, Title, Ui.F(11.5F, FontStyle.Bold),
            new Rectangle(16, 14, Width - 32, 22), Selected ? Ui.Accent : Ui.Text,
            TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Desc, Ui.F(9F),
            new Rectangle(16, 40, Width - 32, Height - 50), Ui.Text2,
            TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        if (Selected)
        {
            using var pen = new Pen(Ui.Accent, 2f);
            g.DrawEllipse(pen, Width - 28, 14, 14, 14);
            using var brush = new SolidBrush(Ui.Accent);
            g.FillEllipse(brush, Width - 24, 18, 6, 6);
        }
    }
}

// 进度页步骤行
internal sealed class StepRow : Control
{
    private string _glyph = "○";
    private Color _glyphColor = Ui.Text3;

    public StepRow(string label)
    {
        Text = label;
        Font = Ui.F(9.5F);
        ForeColor = Ui.Text;
        Height = 28;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    public void SetRunning() { _glyph = "●"; _glyphColor = Ui.Accent; Tag = "running"; Invalidate(); }
    public void SetOk() { _glyph = "✓"; _glyphColor = Ui.Success; Tag = "ok"; Invalidate(); }
    public void SetFail() { _glyph = "✗"; _glyphColor = Ui.Danger; Tag = "fail"; Invalidate(); }
    public void Reset() { _glyph = "○"; _glyphColor = Ui.Text3; Tag = null; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, _glyph, new Font("Segoe UI", 11F), new Rectangle(0, 0, 26, Height),
            _glyphColor, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(28, 0, Width - 28, Height),
            Ui.Text2, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}

// 进度条
internal sealed class ProgressBar : Control
{
    private double _value;

    public ProgressBar()
    {
        Height = 8;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 100); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var p = Ui.RoundRect(r, r.Height / 2f))
        using (var brush = new SolidBrush(Ui.Line))
            g.FillPath(brush, p);
        if (_value <= 0) return;
        var w = (float)(r.Width * _value / 100.0);
        if (w < r.Height) w = r.Height;
        var fill = new RectangleF(0.5f, 0.5f, w, Height - 1f);
        using (var p = Ui.RoundRect(fill, fill.Height / 2f))
        using (var brush = new SolidBrush(Ui.Accent))
            g.FillPath(brush, p);
    }
}

// 线性图标（亮色）
internal static class Glyphs
{
    private static Pen Stroke(Color c, float w) =>
        new(c, w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

    private static void Screen(Graphics g, RectangleF r, Color c, float weight)
    {
        using var p = Ui.RoundRect(r, r.Width * 0.10f);
        using var pen = Stroke(c, weight);
        g.DrawPath(pen, p);
    }

    public static void Home(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.3f, r.Width / 15f);
        var cx = r.X + r.Width / 2f;
        var peak = new PointF(cx, r.Y + r.Height * 0.10f);
        var left = new PointF(r.X + r.Width * 0.06f, r.Y + r.Height * 0.46f);
        var right = new PointF(r.X + r.Width * 0.94f, r.Y + r.Height * 0.46f);
        using (var pen = Stroke(c, w)) g.DrawLines(pen, [left, peak, right]);
        var body = new RectangleF(r.X + r.Width * 0.20f, r.Y + r.Height * 0.46f, r.Width * 0.60f, r.Height * 0.42f);
        using (var p = Ui.RoundRect(body, 3f))
        using (var pen = Stroke(c, w))
            g.DrawPath(pen, p);
    }

    public static void Monitor(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.4f, r.Width / 14f);
        var body = new RectangleF(r.X + r.Width * 0.08f, r.Y + r.Height * 0.12f, r.Width * 0.84f, r.Height * 0.56f);
        Screen(g, body, c, w);
        var cx = r.X + r.Width / 2f;
        using var pen = Stroke(c, w);
        g.DrawLine(pen, cx, body.Bottom, cx, r.Y + r.Height * 0.82f);
        g.DrawLine(pen, cx - r.Width * 0.22f, r.Y + r.Height * 0.84f, cx + r.Width * 0.22f, r.Y + r.Height * 0.84f);
    }

    public static void Trash(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.2f, r.Width / 15f);
        using (var pen = Stroke(c, w))
            g.DrawLine(pen, r.X + r.Width * 0.10f, r.Y + r.Height * 0.22f, r.X + r.Width * 0.90f, r.Y + r.Height * 0.22f);
        var body = new RectangleF(r.X + r.Width * 0.22f, r.Y + r.Height * 0.22f, r.Width * 0.56f, r.Height * 0.66f);
        using (var p = Ui.RoundRect(body, 3f))
        using (var pen = Stroke(c, w))
            g.DrawPath(pen, p);
        using var pen2 = Stroke(c, w);
        foreach (var off in new[] { 0.38f, 0.50f, 0.62f })
        {
            var x = r.X + r.Width * off;
            g.DrawLine(pen2, x, body.Y + body.Height * 0.18f, x, body.Bottom - body.Height * 0.10f);
        }
    }

    public static void Refresh(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.2f, r.Width / 13f);
        var d = Math.Min(r.Width, r.Height) * 0.78f;
        var box = new RectangleF(r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
        using (var pen = Stroke(c, w)) g.DrawArc(pen, box, 40, 250);
        var tip = box.Right - d * 0.10f;
        using var brush = new SolidBrush(c);
        g.FillPolygon(brush, new[]
        {
            new PointF(tip, box.Y + d * 0.22f),
            new PointF(tip - d * 0.22f, box.Y + d * 0.06f),
            new PointF(tip - d * 0.10f, box.Y + d * 0.40f),
        });
    }

    public static void Info(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(1.4f, r.Width / 15f);
        var outer = Math.Min(r.Width, r.Height);
        using (var pen = Stroke(c, w)) g.DrawEllipse(pen, r.X + (r.Width - outer) / 2f, r.Y, outer, outer);
        var cx = r.X + r.Width / 2f;
        using (var dot = new SolidBrush(c)) g.FillEllipse(dot, cx - w, r.Y + outer * 0.22f, w * 2, w * 2);
        using var pen2 = Stroke(c, w * 1.1f);
        g.DrawLine(pen2, cx, r.Y + outer * 0.44f, cx, r.Y + outer * 0.76f);
    }

    public static void Check(Graphics g, RectangleF r, Color c)
    {
        var w = Math.Max(2f, r.Width / 9f);
        using var pen = Stroke(c, w);
        g.DrawLines(pen, new[]
        {
            new PointF(r.X + r.Width * 0.16f, r.Y + r.Height * 0.52f),
            new PointF(r.X + r.Width * 0.40f, r.Y + r.Height * 0.76f),
            new PointF(r.X + r.Width * 0.84f, r.Y + r.Height * 0.26f),
        });
    }
}