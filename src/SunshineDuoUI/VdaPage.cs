using System.Text.Json;
using System.Text.RegularExpressions;

namespace SunshineDuoTray;

// 虚拟显示器页：三种工作模式（大图标）+ 分辨率列表编辑
internal sealed class VdaPage : Panel
{
    private static readonly Regex ResPattern = new(@"^\d{2,5}x\d{2,5}@\d{2,3}$", RegexOptions.Compiled);
    private static readonly Regex ResSize = new(@"^(\d+)x(\d+)", RegexOptions.Compiled);

    private readonly Dictionary<string, ModeCard> _modes = new();
    private readonly Label _modeStatus, _active, _saveStatus, _note;
    private readonly ListBox _list;
    private readonly TextBox _input;
    private readonly Button _add, _remove, _setActive, _save;
    private int _curW, _curH;
    private bool _busy;

    public VdaPage()
    {
        BackColor = UiKit.WindowBg;
        Font = UiKit.YaHei(9F);
        AutoScroll = true;

        var title = UiKit.Title("虚拟显示器");
        title.Location = new Point(0, 24);
        Controls.Add(title);

        var sub = UiKit.Sub("SudoVDA 虚拟屏工作模式与分辨率 · 仅作用于当前会话");
        sub.Location = new Point(0, 62);
        Controls.Add(sub);

        var modeTitle = UiKit.Title("工作模式", 12F);
        modeTitle.Location = new Point(0, 104);
        Controls.Add(modeTitle);

        AddMode("mirror", "镜像", "串流物理主屏\n摘除虚拟屏", Glyphs.Mirror, 0);
        AddMode("only", "独占", "仅串流虚拟屏\n物理屏保持点亮", Glyphs.Exclusive, 252);
        AddMode("extend", "扩展", "物理屏与虚拟屏\n同时可用", Glyphs.Extend, 504);

        _modeStatus = new Label
        {
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9F),
            Location = new Point(0, 280),
            Size = new Size(736, 20),
            AutoEllipsis = true,
        };
        Controls.Add(_modeStatus);

        Controls.Add(new Panel
        {
            Location = new Point(0, 310),
            Size = new Size(736, 1),
            BackColor = UiKit.Border,
        });

        var resTitle = UiKit.Title("分辨率", 12F);
        resTitle.Location = new Point(0, 326);
        Controls.Add(resTitle);

        _active = new Label
        {
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9F),
            Location = new Point(436, 327),
            Size = new Size(300, 18),
            TextAlign = ContentAlignment.MiddleRight,
        };
        Controls.Add(_active);

        _list = new ListBox
        {
            Location = new Point(0, 358),
            Size = new Size(468, 176),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 28,
            Font = UiKit.Mono(10.5F),
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            BackColor = UiKit.Card,
            ForeColor = UiKit.Text,
        };
        _list.DrawItem += DrawItem;
        _list.MouseDoubleClick += (_, _) => SetActiveFromSelection();
        Controls.Add(_list);

        _input = UiKit.Field();
        _input.Font = UiKit.Mono(10.5F);
        _input.Location = new Point(488, 358);
        _input.Width = 248;
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddItem(); }
        };
        Controls.Add(_input);

        _add = Ghost("添加", new Point(488, 394), new Size(120, 32));
        _add.Click += (_, _) => AddItem();
        Controls.Add(_add);

        _remove = Ghost("删除所选", new Point(616, 394), new Size(120, 32));
        _remove.Click += (_, _) =>
        {
            if (_list.SelectedIndex >= 0) _list.Items.RemoveAt(_list.SelectedIndex);
            else SetSaveStatus("先在列表中选中要删除的分辨率。", UiKit.Warn);
        };
        Controls.Add(_remove);

        _setActive = Ghost("设为活动分辨率", new Point(488, 434), new Size(248, 32));
        _setActive.Click += (_, _) => SetActiveFromSelection();
        Controls.Add(_setActive);

        _save = UiKit.Primary("保存分辨率列表");
        _save.Location = new Point(488, 474);
        _save.Size = new Size(248, 34);
        _save.Click += async (_, _) => await SaveAsync();
        Controls.Add(_save);

        var hint = new Label
        {
            Text = "格式：宽x高@刷新率\n例如 2560x1440@60\n双击列表项也可设为活动。",
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(8.5F),
            Location = new Point(488, 518),
            Size = new Size(248, 54),
        };
        Controls.Add(hint);

        _saveStatus = new Label
        {
            ForeColor = UiKit.Muted,
            BackColor = Color.Transparent,
            Font = UiKit.YaHei(9F),
            Location = new Point(0, 548),
            Size = new Size(736, 20),
            AutoEllipsis = true,
        };
        Controls.Add(_saveStatus);

        _note = UiKit.Caption("工作模式切换会立即调整显示拓扑；分辨率在下一场串流建立时生效。");
        _note.Location = new Point(0, 580);
        Controls.Add(_note);

        Reload();
    }

    private void AddMode(string id, string title, string desc,
        Action<Graphics, RectangleF, Color> icon, int x)
    {
        var card = new ModeCard(title, desc, icon)
        {
            Location = new Point(x, 134),
            Size = new Size(232, 138),
        };
        card.Activated += async (_, _) => await ApplyModeAsync(id);
        _modes[id] = card;
        Controls.Add(card);
    }

    public void Reload()
    {
        var mode = Paths.CoreString("default_display_mode", "only");
        if (!_modes.ContainsKey(mode)) mode = "only";
        SelectMode(mode);
        (_curW, _curH) = ReadResolution();
        _active.Text = $"当前活动：{_curW} × {_curH}";
        _list.Items.Clear();
        foreach (var r in LoadResolutions()) _list.Items.Add(r);
        _list.Invalidate();
    }

    private void SelectMode(string mode)
    {
        foreach (var kv in _modes) kv.Value.Selected = kv.Key == mode;
        _modes[mode].Invalidate();
        foreach (var kv in _modes)
            if (kv.Key != mode) kv.Value.Invalidate();
    }

    private async Task ApplyModeAsync(string mode)
    {
        if (_busy) return;
        _busy = true;
        SelectMode(mode);
        _modeStatus.ForeColor = UiKit.Muted;
        _modeStatus.Text = "正在切换显示拓扑…";
        try
        {
            var err = await GuardPipe.SetDisplayMode(mode);
            var name = _modes[mode].Title;
            if (err == null)
            {
                _modeStatus.ForeColor = UiKit.Accent;
                _modeStatus.Text = $"已切换为「{name}」。串流中的拓扑已立即调整。";
            }
            else
            {
                _modeStatus.ForeColor = UiKit.Danger;
                _modeStatus.Text = "切换失败：" + err;
                Reload();
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var g = e.Graphics;
        using (var brush = new SolidBrush(selected ? Color.FromArgb(30, 44, 34) : UiKit.Card))
            g.FillRectangle(brush, e.Bounds);
        var text = _list.Items[e.Index]?.ToString() ?? "";
        if (ParseSize(text, out var w, out var h) && w == _curW && h == _curH)
            text += "   （当前活动）";
        TextRenderer.DrawText(g, text, e.Font ?? _list.Font,
            new Point(e.Bounds.Left + 12, e.Bounds.Top + 6),
            selected ? UiKit.Accent : UiKit.Text);
    }

    private static bool ParseSize(string s, out int w, out int h)
    {
        w = h = 0;
        var m = ResSize.Match(s);
        if (!m.Success) return false;
        w = int.Parse(m.Groups[1].Value);
        h = int.Parse(m.Groups[2].Value);
        return true;
    }

    private void AddItem()
    {
        var s = _input.Text.Trim().ToLowerInvariant();
        if (!ResPattern.IsMatch(s) || !ParseSize(s, out _, out _))
        {
            SetSaveStatus("格式不对：必须带刷新率，例如 2560x1440@60。", UiKit.Danger);
            return;
        }
        for (var i = 0; i < _list.Items.Count; i++)
        {
            if (string.Equals(_list.Items[i]?.ToString(), s, StringComparison.OrdinalIgnoreCase))
            {
                SetSaveStatus("该分辨率已在列表中。", UiKit.Warn);
                return;
            }
        }
        _list.Items.Add(s);
        _input.Clear();
        _input.Focus();
        SetSaveStatus("已添加，记得保存。", UiKit.Muted);
    }

    private void SetActiveFromSelection()
    {
        if (_list.SelectedIndex < 0)
        {
            SetSaveStatus("先在列表中选中一个分辨率。", UiKit.Warn);
            return;
        }
        var text = _list.SelectedItem?.ToString() ?? "";
        if (!ParseSize(text, out var w, out var h))
        {
            SetSaveStatus("无法解析该分辨率。", UiKit.Danger);
            return;
        }
        _curW = w;
        _curH = h;
        _active.Text = $"当前活动：{_curW} × {_curH}";
        _list.Invalidate();
        SetSaveStatus("已设为活动分辨率，保存后生效。", UiKit.Text);
    }

    private async Task SaveAsync()
    {
        if (_busy) return;
        var items = _list.Items.Cast<object>()
            .Select(o => o?.ToString() ?? "")
            .Where(s => s.Length > 0)
            .ToList();
        foreach (var s in items)
        {
            if (!ResPattern.IsMatch(s))
            {
                SetSaveStatus("存在无效条目：" + s, UiKit.Danger);
                return;
            }
        }
        _busy = true;
        _save.Enabled = _add.Enabled = _remove.Enabled = _setActive.Enabled = false;
        SetSaveStatus("正在保存…", UiKit.Muted);
        try
        {
            var err = await GuardPipe.SetResolution(_curW, _curH, items.ToArray());
            if (err == null)
                SetSaveStatus($"已保存，下一场串流按 {_curW} × {_curH} 建立。", UiKit.Accent);
            else
                SetSaveStatus("保存失败：" + err, UiKit.Danger);
        }
        finally
        {
            _save.Enabled = _add.Enabled = _remove.Enabled = _setActive.Enabled = true;
            _busy = false;
        }
    }

    private void SetSaveStatus(string text, Color color)
    {
        _saveStatus.ForeColor = color;
        _saveStatus.Text = text;
    }

    private static Button Ghost(string text, Point loc, Size size)
    {
        var b = UiKit.Ghost(text);
        b.Location = loc;
        b.Size = size;
        return b;
    }

    private static (int w, int h) ReadResolution()
    {
        try
        {
            var cfg = Path.Combine(Paths.Root, "bin", "SunshineDuoCore", "config.json");
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
            var cfg = Path.Combine(Paths.Root, "bin", "SunshineDuoCore", "config.json");
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
}