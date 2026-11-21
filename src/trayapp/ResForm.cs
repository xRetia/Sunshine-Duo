using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SunshineDuoTray;

public class ResForm : Form
{
    private readonly ListBox _list;
    private readonly TextBox _input;
    private readonly Button _add, _remove, _ok, _cancel;
    private readonly Label _active;
    private readonly int _curW, _curH;

    private static readonly Color Bg = Color.FromArgb(24, 26, 30);
    private static readonly Color Surface = Color.FromArgb(38, 41, 47);
    private static readonly Color SurfaceHover = Color.FromArgb(50, 54, 61);
    private static readonly Color Accent = Color.FromArgb(16, 124, 16);
    private static readonly Color AccentHover = Color.FromArgb(28, 148, 28);
    private static readonly Color TextC = Color.FromArgb(240, 242, 245);
    private static readonly Color Muted = Color.FromArgb(150, 156, 166);
    private static readonly Color Border = Color.FromArgb(70, 75, 84);

    private static readonly Regex ResPattern = new(@"^\d{2,5}x\d{2,5}@\d{2,3}$", RegexOptions.Compiled);

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    public ResForm()
    {
        Text = "虚拟屏分辨率";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 420);
        BackColor = Bg;
        Font = new Font("Segoe UI", 10F);
        DoubleBuffered = true;

        var path = new GraphicsPath();
        var r = 12;
        path.AddArc(0, 0, r * 2, r * 2, 180, 90);
        path.AddArc(Width - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
        path.AddArc(Width - r * 2 - 1, Height - r * 2 - 1, r * 2, r * 2, 0, 90);
        path.AddArc(0, Height - r * 2 - 1, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        Region = new Region(path);

        var title = new Label
        {
            Text = "虚拟屏分辨率",
            ForeColor = TextC,
            BackColor = Color.Transparent,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            Location = new Point(24, 16),
            AutoSize = true,
        };
        title.MouseDown += DragAnywhere;
        var close = new Button
        {
            Text = "✕", Location = new Point(ClientSize.Width - 44, 8), Size = new Size(36, 32),
            FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent, ForeColor = TextC,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(196, 43, 28) },
            Font = new Font("Segoe UI", 11F),
        };
        close.Click += (_, _) => Close();
        Controls.Add(title);
        Controls.Add(close);

        (_curW, _curH) = ReadResolution();
        _active = new Label
        {
            Text = $"当前活动：{_curW} × {_curH}",
            ForeColor = Muted,
            Location = new Point(24, 50),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        Controls.Add(_active);

        var lblList = new Label
        {
            Text = "可选分辨率（串流时可选用的虚拟屏模式）：",
            ForeColor = Muted,
            Location = new Point(24, 80),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        Controls.Add(lblList);

        _list = new ListBox
        {
            Location = new Point(24, 106),
            Size = new Size(412, 150),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 28,
            Font = new Font("Microsoft YaHei UI", 10F),
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
        };
        _list.DrawItem += DrawItem;
        _list.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) _list.SelectedIndex = _list.IndexFromPoint(e.Location); };
        Controls.Add(_list);

        _input = new TextBox
        {
            Location = new Point(24, 270),
            Width = 200,
            BackColor = Surface,
            ForeColor = TextC,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 11F),
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddItem(); }
        };
        Controls.Add(_input);

        _add = FlatButton("添加", new Point(232, 268), new Size(96, 30), false);
        _add.Click += (_, _) => AddItem();
        _remove = FlatButton("删除所选", new Point(340, 268), new Size(96, 30), false);
        _remove.Click += (_, _) =>
        {
            if (_list.SelectedIndex >= 0) _list.Items.RemoveAt(_list.SelectedIndex);
        };
        Controls.Add(_add);
        Controls.Add(_remove);

        var hint = new Label
        {
            Text = "格式：宽x高@刷新率，如 2560x1440@60（必须带刷新率）",
            ForeColor = Muted,
            Location = new Point(24, 306),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        Controls.Add(hint);

        _ok = FlatButton("保存", new Point(246, 366), new Size(88, 34), true);
        _ok.Click += async (_, _) => await Save();
        _cancel = FlatButton("取消", new Point(348, 366), new Size(88, 34), false);
        _cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(_ok);
        Controls.Add(_cancel);
        CancelButton = _cancel;

        foreach (var item in LoadResolutions()) _list.Items.Add(item);
        MouseDown += DragAnywhere;
    }

    private void DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var back = selected ? Color.FromArgb(30, 60, 30) : Surface;
        using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
        var text = _list.Items[e.Index]?.ToString() ?? "";
        if (ParseRes(text, out var w, out var h) && w == _curW && h == _curH)
            text += "   （当前活动）";
        TextRenderer.DrawText(e.Graphics, text, e.Font ?? Font,
            new Point(e.Bounds.Left + 10, e.Bounds.Top + 4),
            selected ? Color.White : TextC);
    }

    private static bool ParseRes(string s, out int w, out int h)
    {
        w = h = 0;
        var m = Regex.Match(s, @"^(\d+)x(\d+)");
        if (!m.Success) return false;
        w = int.Parse(m.Groups[1].Value);
        h = int.Parse(m.Groups[2].Value);
        return true;
    }

    private void AddItem()
    {
        var s = _input.Text.Trim().ToLowerInvariant();
        if (!ResPattern.IsMatch(s))
        {
            MessageBox.Show("必须带刷新率，格式：宽x高@刷新率，如 2560x1440@60", "虚拟屏分辨率");
            return;
        }
        if (ParseRes(s, out _, out _) == false)
        {
            MessageBox.Show("分辨率数值无效。", "虚拟屏分辨率");
            return;
        }
        for (var i = 0; i < _list.Items.Count; i++)
        {
            if (string.Equals((string)_list.Items[i], s, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("该分辨率已在列表中。", "虚拟屏分辨率");
                return;
            }
        }
        _list.Items.Add(s);
        _input.Clear();
        _input.Focus();
    }

    private static (int w, int h) ReadResolution()
    {
        try
        {
            var cfg = Path.Combine(Paths.Root, "wsguard", "config.json");
            if (!File.Exists(cfg)) return (1600, 720);
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            var w = doc.RootElement.TryGetProperty("width", out var vw) ? vw.GetInt32() : 1600;
            var h = doc.RootElement.TryGetProperty("height", out var vh) ? vh.GetInt32() : 720;
            return (w, h);
        }
        catch { return (1600, 720); }
    }

    private static List<string> LoadResolutions()
    {
        try
        {
            var cfg = Path.Combine(Paths.Root, "wsguard", "config.json");
            if (!File.Exists(cfg)) return new List<string>();
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (!doc.RootElement.TryGetProperty("sudovda_resolutions", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return new List<string>();
            var list = new List<string>();
            foreach (var v in arr.EnumerateArray())
                if (v.GetString() is { Length: > 0 } s) list.Add(s);
            return list;
        }
        catch { return new List<string>(); }
    }

    private async Task Save()
    {
        var items = new List<string>();
        foreach (var it in _list.Items)
            if (it?.ToString() is { Length: > 0 } s) items.Add(s);
        foreach (var s in items)
        {
            if (!ResPattern.IsMatch(s))
            {
                MessageBox.Show("存在无效条目：" + s, "虚拟屏分辨率");
                return;
            }
        }
        SetBusy(true);
        try
        {
            var err = await GuardPipe.SetResolution(_curW, _curH, items.ToArray());
            if (err != null)
            {
                MessageBox.Show("保存失败：" + err, "虚拟屏分辨率");
                return;
            }
            DialogResult = DialogResult.OK;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _ok.Enabled = _cancel.Enabled = _add.Enabled = _remove.Enabled = !busy;
    }

    private Button FlatButton(string text, Point loc, Size size, bool accent)
    {
        return new Button
        {
            Text = text,
            Location = loc,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = accent ? Accent : Surface,
            ForeColor = accent ? Color.White : TextC,
            Font = new Font("Microsoft YaHei UI", 10F),
            FlatAppearance = { BorderSize = accent ? 0 : 1, BorderColor = Border, MouseOverBackColor = accent ? AccentHover : SurfaceHover },
        };
    }

    private void DragAnywhere(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 0x2, 0);
        }
    }
}
