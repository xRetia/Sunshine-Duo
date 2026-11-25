using System.Drawing.Drawing2D;
using System.Reflection;

namespace SunshineDuoTray;

internal enum TrayState
{
    Offline,
    Partial,
    Online,
}

internal static class TrayIcons
{
    private static readonly Dictionary<TrayState, Icon> Cache = new();
    private static Icon? _appIcon;

    // 窗口/任务栏/exe 使用不带状态角标的太阳图标
    public static Icon AppIcon()
    {
        lock (Cache)
        {
            if (_appIcon != null) return _appIcon;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("sun.ico")
                ?? throw new InvalidOperationException("sun.ico resource missing");
            _appIcon = new Icon(stream, 256, 256);
            return _appIcon;
        }
    }

    public static Icon Get(TrayState state)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(state, out var icon)) return icon;
            icon = Build(state);
            Cache[state] = icon;
            return icon;
        }
    }

    private static Icon Build(TrayState state)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("sun.png")
            ?? throw new InvalidOperationException("sun.png resource missing");
        using var source = new Bitmap(stream);
        var rect = AlphaBounds(source);
        using var sun = source.Clone(rect, source.PixelFormat);

        const int size = 64;
        using var canvas = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawImage(sun, new Rectangle(2, 2, size - 4, size - 4));

        var badgeColor = state switch
        {
            TrayState.Online => Color.FromArgb(34, 197, 94),
            TrayState.Partial => Color.FromArgb(245, 158, 11),
            _ => Color.FromArgb(148, 163, 184),
        };
        var badge = new Rectangle(42, 42, 20, 20);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(badge);
            using var ring = new Pen(Color.FromArgb(24, 32, 48), 2.5f);
            using var brush = new SolidBrush(badgeColor);
            g.FillPath(brush, path);
            g.DrawPath(ring, path);
        }

        var glyph = state switch
        {
            TrayState.Online => "✓",
            TrayState.Partial => "!",
            _ => "×",
        };
        using var font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(Color.White);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, textBrush, new RectangleF(badge.X, badge.Y - 1, badge.Width, badge.Height), sf);

        var hicon = canvas.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hicon);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hicon);
        }
    }

    // 侧栏/关于页头像：内嵌太阳图裁成圆形
    public static Bitmap Avatar(int size)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("sun.png")
            ?? throw new InvalidOperationException("sun.png resource missing");
        using var source = new Bitmap(stream);
        using var cropped = source.Clone(AlphaBounds(source), source.PixelFormat);
        var canvas = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);
        using var clip = new GraphicsPath();
        clip.AddEllipse(0.5f, 0.5f, size - 1f, size - 1f);
        g.SetClip(clip);
        g.DrawImage(cropped, new Rectangle(0, 0, size, size));
        return canvas;
    }

    private static Rectangle AlphaBounds(Bitmap bitmap)
    {
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).A < 8) continue;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }
        if (maxX < 0) return new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool DestroyIcon(IntPtr handle);
    }
}
