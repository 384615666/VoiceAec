using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VoiceAec;

// The tray menu mirrors the pastel control panel: cream surface, rounded
// corners, one accent colour per action, generous row rhythm.
internal static class TrayPalette
{
    public static readonly Color Surface = Color.FromArgb(0xFF, 0xFC, 0xF8);
    public static readonly Color SurfaceBottom = Color.FromArgb(0xFF, 0xF7, 0xF4);
    public static readonly Color Ink = Color.FromArgb(0x46, 0x42, 0x57);
    public static readonly Color InkSoft = Color.FromArgb(0x5A, 0x55, 0x66);
    public static readonly Color Muted = Color.FromArgb(0x8A, 0x6D, 0x9C);
    public static readonly Color Line = Color.FromArgb(0xEE, 0xE2, 0xEC);
    public static readonly Color Hover = Color.FromArgb(0xF1, 0xFA, 0xF6);
    public static readonly Color HoverDanger = Color.FromArgb(0xFD, 0xEE, 0xF0);
    public static readonly Color Green = Color.FromArgb(0x2E, 0xC4, 0x89);
    public static readonly Color GreenDeep = Color.FromArgb(0x11, 0x80, 0x5A);
    public static readonly Color GreenSoft = Color.FromArgb(0xE7, 0xFA, 0xF1);
    public static readonly Color Berry = Color.FromArgb(0xF2, 0x7B, 0xA9);
    public static readonly Color Blue = Color.FromArgb(0x4F, 0xA8, 0xE8);
    public static readonly Color Violet = Color.FromArgb(0xA7, 0x8B, 0xFA);
    public static readonly Color Coral = Color.FromArgb(0xE0, 0x5E, 0x68);
    public static readonly Color CoralSoft = Color.FromArgb(0xFD, 0xF0, 0xF1);
    public static readonly Color Track = Color.FromArgb(0xE6, 0xDB, 0xE8);
    public static readonly Color Border = Color.FromArgb(0xEB, 0xDE, 0xE8);
}

internal enum TrayGlyph
{
    None,
    Panel,
    Pause,
    Resume,
    Mode,
    Autostart,
    Exit,
    Dot,
}

internal static class TrayFonts
{
    public static readonly string Family = PickFamily();

    private static string PickFamily()
    {
        foreach (var candidate in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI Variable Text", "Segoe UI" })
        {
            try
            {
                _ = new FontFamily(candidate);
                return candidate;
            }
            catch (ArgumentException) { }
        }
        return SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Segoe UI";
    }

    // Pixel-sized fonts keep the layout independent of WinForms autoscaling;
    // rows are rebuilt from DeviceDpi so moving between monitors stays sharp.
    public static Font Body(float scale) => new(Family, 14f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    public static Font Title(float scale) => new(Family, 15f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
    public static Font Caption(float scale) => new(Family, 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    public static Font Pill(float scale) => new(Family, 11f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
}

internal sealed record TrayModeEntry(string Key, string Label, Color Accent, bool Available);

internal sealed record TrayMenuState(
    string Mode,
    string ModeLabel,
    bool Paused,
    bool Autostart,
    IReadOnlyList<TrayModeEntry> Modes);

internal static class TraySupport
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const string AppIconResource = "VoiceAec.assets.VoiceAec.ico";
    private const string TrayIconResource = "VoiceAec.assets.VoiceAecTray.ico";

    public static void HideConsole()
    {
        var h = GetConsoleWindow();
        if (h != IntPtr.Zero) ShowWindow(h, 0 /* SW_HIDE */);
    }

    public static Icon AppIcon(int size) => LoadIcon(AppIconResource, size);

    public static Icon TrayIcon() => LoadIcon(TrayIconResource, SystemInformation.SmallIconSize.Width);

    public static Bitmap AppMark(int size)
    {
        using var icon = LoadIcon(AppIconResource, Math.Max(size, 32));
        using var source = icon.ToBitmap();
        var target = new Bitmap(size, size);
        using var g = Graphics.FromImage(target);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, size, size));
        return target;
    }

    private static Icon LoadIcon(string name, int size)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"缺少图标资源 {name}");
        return new Icon(stream, Math.Max(16, size), Math.Max(16, size));
    }
}

// Windows 11 rounds popup windows through DWM (anti-aliased, shadow follows).
// Older Windows falls back to a window region.
internal static class RoundedWindow
{
    private const int CornerPreference = 33;
    private const int Round = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Control control, int radius)
    {
        if (control.Handle == IntPtr.Zero) return;
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            var preference = Round;
            if (DwmSetWindowAttribute(control.Handle, CornerPreference, ref preference, sizeof(int)) == 0
                && control.Height > 0 && control.Width > 0)
            {
                control.Region = null;
                return;
            }
        }
        ApplyRegion(control, radius);
    }

    public static void ApplyRegion(Control control, int radius)
    {
        if (control.Width <= 0 || control.Height <= 0) return;
        using var path = TrayDrawing.RoundedRect(new Rectangle(0, 0, control.Width, control.Height), radius);
        var old = control.Region;
        control.Region = new Region(path);
        old?.Dispose();
    }
}

internal static class TrayDrawing
{
    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1, radius * 2);
        if (d >= bounds.Width) d = bounds.Width - 1;
        if (d >= bounds.Height) d = bounds.Height - 1;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static GraphicsPath RoundedRectScaled(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void DrawGlyph(Graphics g, Rectangle box, TrayGlyph glyph, Color color)
    {
        if (glyph == TrayGlyph.None) return;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var s = box.Width / 18f;
        g.TranslateTransform(box.Left, box.Top);
        g.ScaleTransform(s, s);
        using var pen = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        switch (glyph)
        {
            case TrayGlyph.Panel:
                using (var window = RoundedRectScaled(new RectangleF(2.4f, 3.4f, 13.2f, 11.2f), 2.4f))
                    g.DrawPath(pen, window);
                g.DrawLine(pen, 3.0f, 6.9f, 15.0f, 6.9f);
                g.FillEllipse(brush, 4.9f, 4.7f, 1.5f, 1.5f);
                break;
            case TrayGlyph.Pause:
                using (var bar = RoundedRectScaled(new RectangleF(5.1f, 4.2f, 2.6f, 9.6f), 1.3f))
                    g.FillPath(brush, bar);
                using (var bar = RoundedRectScaled(new RectangleF(10.3f, 4.2f, 2.6f, 9.6f), 1.3f))
                    g.FillPath(brush, bar);
                break;
            case TrayGlyph.Resume:
                using (var play = new GraphicsPath())
                {
                    play.AddPolygon(new[]
                    {
                        new PointF(6.2f, 3.8f),
                        new PointF(14.0f, 9.0f),
                        new PointF(6.2f, 14.2f),
                    });
                    g.FillPath(brush, play);
                }
                break;
            case TrayGlyph.Mode:
                g.DrawLine(pen, 3.4f, 5.2f, 14.6f, 5.2f);
                g.DrawLine(pen, 3.4f, 9.0f, 14.6f, 9.0f);
                g.DrawLine(pen, 3.4f, 12.8f, 14.6f, 12.8f);
                DrawKnob(g, brush, 11.3f, 5.2f, 2.1f);
                DrawKnob(g, brush, 6.3f, 9.0f, 2.1f);
                DrawKnob(g, brush, 12.1f, 12.8f, 2.1f);
                break;
            case TrayGlyph.Autostart:
                using (var arc = new GraphicsPath())
                {
                    arc.AddArc(3.7f, 3.9f, 10.6f, 10.6f, 120, 285);
                    g.DrawPath(pen, arc);
                }
                using (var arrow = new GraphicsPath())
                {
                    arrow.AddPolygon(new[]
                    {
                        new PointF(12.4f, 2.5f),
                        new PointF(12.9f, 7.1f),
                        new PointF(8.7f, 5.0f),
                    });
                    g.FillPath(brush, arrow);
                }
                break;
            case TrayGlyph.Exit:
                g.DrawLines(pen, new[]
                {
                    new PointF(9.4f, 3.5f),
                    new PointF(4.9f, 3.5f),
                    new PointF(4.9f, 14.5f),
                    new PointF(9.4f, 14.5f),
                });
                g.DrawLine(pen, 8.6f, 9.0f, 14.8f, 9.0f);
                using (var arrow = new GraphicsPath())
                {
                    arrow.AddPolygon(new[]
                    {
                        new PointF(14.9f, 9.0f),
                        new PointF(11.4f, 6.2f),
                        new PointF(11.4f, 11.8f),
                    });
                    g.FillPath(brush, arrow);
                }
                break;
            case TrayGlyph.Dot:
                using (var halo = new SolidBrush(Color.White))
                    g.FillEllipse(halo, 5.0f, 5.0f, 8.0f, 8.0f);
                using (var soft = new SolidBrush(Color.FromArgb(70, color)))
                    g.FillEllipse(soft, 4.0f, 4.0f, 10.0f, 10.0f);
                g.FillEllipse(brush, 6.0f, 6.0f, 6.0f, 6.0f);
                break;
        }
        g.Restore(state);
    }

    private static void DrawKnob(Graphics g, Brush brush, float cx, float cy, float r)
    {
        using var halo = new SolidBrush(Color.White);
        g.FillEllipse(halo, cx - r - 0.7f, cy - r - 0.7f, (r + 0.7f) * 2, (r + 0.7f) * 2);
        g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
    }

    public static void DrawCheck(Graphics g, Rectangle box, Color color)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = box.Width / 16f;
        using var pen = new Pen(color, 1.9f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(box.Left + 3.0f * s, box.Top + 8.6f * s),
            new PointF(box.Left + 6.4f * s, box.Top + 12.0f * s),
            new PointF(box.Left + 13.0f * s, box.Top + 4.2f * s),
        });
        g.Restore(state);
    }

    public static void DrawChevron(Graphics g, Rectangle box, Color color)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(box.Left + 2.0f, box.Top + 3.0f),
            new PointF(box.Left + 6.5f, box.Top + 8.0f),
            new PointF(box.Left + 2.0f, box.Top + 13.0f),
        });
        g.Restore(state);
    }
}

// Every row paints itself (pill, glyph, label, hint, switch, check) so the
// geometry does not depend on ToolStrip's image-margin heuristics.
internal sealed class TrayMenuItem : ToolStripMenuItem
{
    private readonly float scale;
    private Font? captionFont;

    public TrayMenuItem(string text, TrayGlyph glyph, float scale, Color? accent = null, string? hint = null)
        : base(text)
    {
        this.scale = scale;
        Glyph = glyph;
        Accent = accent ?? TrayPalette.InkSoft;
        Hint = hint;
        AutoSize = false;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public TrayGlyph Glyph { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Accent { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Hint { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowSwitch { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SwitchOn { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Danger { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Highlight { get; set; }

    internal int Px(float value) => (int)Math.Round(value * scale);

    public Rectangle PillBounds => new(Px(4), Px(2), Width - Px(8), Height - Px(4));
    public Rectangle GlyphBounds => new(Px(16), (Height - Px(18)) / 2, Px(18), Px(18));
    public Rectangle SwitchBounds => new(Width - Px(14) - Px(34), (Height - Px(18)) / 2, Px(34), Px(18));
    public Rectangle CheckBounds => new(Width - Px(15) - Px(16), (Height - Px(16)) / 2, Px(16), Px(16));
    public Rectangle ChevronBounds => new(Width - Px(13) - Px(10), (Height - Px(16)) / 2, Px(10), Px(16));

    protected override void Dispose(bool disposing)
    {
        if (disposing) captionFont?.Dispose();
        base.Dispose(disposing);
    }

    public override Size GetPreferredSize(Size constrainingSize) => Size;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var accent = Enabled ? Accent : TrayPalette.Track;

        var active = (Selected || Highlight) && Enabled;
        if (active)
        {
            using var path = TrayDrawing.RoundedRect(PillBounds, Px(9));
            using var fill = new SolidBrush(Danger ? TrayPalette.HoverDanger : TrayPalette.Hover);
            g.FillPath(fill, path);
        }
        TrayDrawing.DrawGlyph(g, GlyphBounds, Glyph, accent);

        var rightReserve = Px(18);
        if (ShowSwitch) rightReserve = Px(58);
        else if (HasDropDownItems) rightReserve = Px(30);
        else if (Checked) rightReserve = Px(36);

        var hintWidth = 0;
        captionFont ??= TrayFonts.Caption(scale);
        if (!string.IsNullOrEmpty(Hint))
        {
            hintWidth = TextRenderer.MeasureText(g, Hint!, captionFont, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            var hintRight = Width - (HasDropDownItems ? Px(30) : Px(16));
            TextRenderer.DrawText(g, Hint, captionFont,
                new Rectangle(hintRight - hintWidth, 0, hintWidth, Height),
                active ? TrayPalette.InkSoft : TrayPalette.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        var textRight = Width - rightReserve - (hintWidth > 0 ? hintWidth + Px(10) : 0);
        var textRect = new Rectangle(Px(44), 0, Math.Max(0, textRight - Px(44)), Height);
        var color = !Enabled ? TrayPalette.Muted : Selected && Danger ? TrayPalette.Coral : TrayPalette.Ink;
        TextRenderer.DrawText(g, Text, Font ?? SystemFonts.MenuFont, textRect, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        if (ShowSwitch)
        {
            var track = SwitchBounds;
            using var trackPath = TrayDrawing.RoundedRect(track, track.Height / 2);
            using var trackFill = new SolidBrush(SwitchOn ? TrayPalette.Green : TrayPalette.Track);
            g.FillPath(trackFill, trackPath);
            var knobSize = Px(14);
            var knob = new Rectangle(
                SwitchOn ? track.Right - knobSize - Px(2) : track.Left + Px(2),
                track.Top + Px(2), knobSize, knobSize);
            using var knobFill = new SolidBrush(Color.White);
            g.FillEllipse(knobFill, knob);
        }
        else if (HasDropDownItems)
        {
            TrayDrawing.DrawChevron(g, ChevronBounds, TrayPalette.Muted);
        }
        else if (Checked)
        {
            TrayDrawing.DrawCheck(g, CheckBounds, Accent);
        }
    }
}

internal sealed class TraySeparator : ToolStripSeparator
{
    private readonly int width;
    private readonly int height;
    public TraySeparator(int width, int height)
    {
        this.width = width;
        this.height = height;
        AutoSize = false;
        Size = new Size(width, height);
        Margin = Padding.Empty;
    }
    public override Size GetPreferredSize(Size constrainingSize) => new(width, height);
}

internal sealed class TraySpacer : ToolStripItem
{
    public TraySpacer(int width, int height)
    {
        AutoSize = false;
        Size = new Size(width, height);
        Margin = Padding.Empty;
    }
    public override bool CanSelect => false;
    public override Size GetPreferredSize(Size constrainingSize) => Size;
    protected override void OnPaint(PaintEventArgs e) { }
}

internal sealed class TrayHeaderItem : ToolStripItem
{    private readonly Func<TrayMenuState> state;
    private readonly float scale;
    private readonly Font titleFont;
    private readonly Font captionFont;
    private readonly Font pillFont;
    private Bitmap? mark;

    public TrayHeaderItem(Func<TrayMenuState> state, float scale, int width)
    {
        this.state = state;
        this.scale = scale;
        titleFont = TrayFonts.Title(scale);
        captionFont = TrayFonts.Caption(scale);
        pillFont = TrayFonts.Pill(scale);
        AutoSize = false;
        Size = new Size(width, Px(60));
    }

    public override bool CanSelect => false;

    private int Px(float value) => (int)Math.Round(value * scale);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            mark?.Dispose();
            titleFont.Dispose();
            captionFont.Dispose();
            pillFont.Dispose();
        }
        base.Dispose(disposing);
    }

    public override Size GetPreferredSize(Size constrainingSize) => Size;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var snapshot = state();
        // Padded items are placed at an x offset while menu rows start at 0;
        // compensate so the header icon lines up with the row glyph column.
        var menuWidth = Owner?.ClientSize.Width ?? Width;
        var left = Px(16) - Bounds.Left;
        var right = Math.Min(Width + Bounds.Left, menuWidth) - Bounds.Left;
        var iconBox = new Rectangle(left, Px(13), Px(30), Px(30));
        mark ??= TraySupport.AppMark(iconBox.Width);
        g.DrawImage(mark, iconBox);

        var textLeft = iconBox.Right + Px(10);
        TextRenderer.DrawText(g, "VoiceAec", titleFont, new Point(textLeft, Px(12)), TrayPalette.Ink,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, "本地实时音频控制台", captionFont, new Point(textLeft, Px(35)), TrayPalette.Muted,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        var label = snapshot.Paused ? "已暂停" : "运行中";
        var pillSize = TextRenderer.MeasureText(g, label, pillFont, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var pill = new Rectangle(right - Px(14) - pillSize.Width - Px(14), Px(13),
            pillSize.Width + Px(14), Px(20));
        using (var path = TrayDrawing.RoundedRect(pill, pill.Height / 2))
        using (var fill = new SolidBrush(snapshot.Paused ? TrayPalette.CoralSoft : TrayPalette.GreenSoft))
        {
            g.FillPath(fill, path);
        }
        var pillColor = snapshot.Paused ? TrayPalette.Coral : TrayPalette.GreenDeep;
        TextRenderer.DrawText(g, label, pillFont, pill, pillColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly float scale;
    public TrayMenuRenderer(float scale) : base(new TrayColorTable())
    {
        this.scale = scale;
        RoundedEdges = false;
    }

    private int Px(float value) => (int)Math.Round(value * scale);

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var bounds = e.ToolStrip.ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var brush = new LinearGradientBrush(
            new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height + 1),
            TrayPalette.Surface, TrayPalette.SurfaceBottom, 90f);
        e.Graphics.FillRectangle(brush, bounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var bounds = e.ToolStrip.ClientRectangle;
        if (bounds.Width < 3 || bounds.Height < 3) return;
        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(TrayPalette.Border);
        using var path = TrayDrawing.RoundedRect(new Rectangle(0, 0, bounds.Width - 1, bounds.Height - 1), Px(13));
        e.Graphics.DrawPath(pen, path);
        e.Graphics.Restore(state);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e) { }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) { }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(TrayPalette.Line);
        var right = Math.Min(e.Item.Width, e.Item.Owner?.Width ?? e.Item.Width);
        e.Graphics.DrawLine(pen, Px(14), e.Item.Height / 2, right - Px(14), e.Item.Height / 2);
    }

    private sealed class TrayColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected => TrayPalette.Hover;
        public override Color MenuItemSelectedGradientBegin => TrayPalette.Hover;
        public override Color MenuItemSelectedGradientEnd => TrayPalette.Hover;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuBorder => TrayPalette.Border;
        public override Color ToolStripDropDownBackground => TrayPalette.Surface;
        public override Color ImageMarginGradientBegin => TrayPalette.Surface;
        public override Color ImageMarginGradientMiddle => TrayPalette.Surface;
        public override Color ImageMarginGradientEnd => TrayPalette.Surface;
        public override Color SeparatorDark => TrayPalette.Line;
        public override Color SeparatorLight => TrayPalette.Surface;
    }
}

internal sealed class TrayMenu : ContextMenuStrip
{
    private readonly Func<TrayMenuState> stateSource;
    private readonly float scale;
    private readonly int innerWidth;
    private readonly TrayHeaderItem header;
    private TrayMenuItem? pauseItem;
    private TrayMenuItem? autostartItem;
    private TrayMenuItem? modeHead;
    private bool chromeApplied;

    public event Action? OpenPanelRequested;
    public event Action? PauseRequested;
    public event Action<string>? ModeRequested;
    public event Action<bool>? AutostartRequested;
    public event Action? QuitRequested;

    public TrayMenu(Func<TrayMenuState> stateSource) : base()
    {
        this.stateSource = stateSource;
        scale = DeviceDpi / 96f;
        innerWidth = Px(232);

        Font = TrayFonts.Body(scale);
        ForeColor = TrayPalette.Ink;
        BackColor = TrayPalette.Surface;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        DropShadowEnabled = true;
        AutoSize = false;
        Padding = new Padding(Px(6));
        Renderer = new TrayMenuRenderer(scale);

        header = new TrayHeaderItem(stateSource, scale, innerWidth);
        Items.Add(header);
        Items.Add(Separator());

        var open = Row("打开控制面板", TrayGlyph.Panel, TrayPalette.Blue);
        open.Click += (_, _) => OpenPanelRequested?.Invoke();
        Items.Add(open);

        pauseItem = Row("暂停并静音", TrayGlyph.Pause, TrayPalette.Violet);
        pauseItem.Click += (_, _) => PauseRequested?.Invoke();
        Items.Add(pauseItem);

        modeHead = Row("处理模式", TrayGlyph.Mode, TrayPalette.Berry);
        modeHead.DropDown = new TrayDropDown(scale, Px(202));
        modeHead.DropDownOpened += (_, _) => { modeHead.Highlight = true; modeHead.Invalidate(); };
        modeHead.DropDownClosed += (_, _) => { modeHead.Highlight = false; modeHead.Invalidate(); };
        Items.Add(modeHead);

        Items.Add(Separator());

        autostartItem = Row("开机自启", TrayGlyph.Autostart, TrayPalette.Green);
        autostartItem.ShowSwitch = true;
        autostartItem.Click += (_, _) => AutostartRequested?.Invoke(!autostartItem.SwitchOn);
        Items.Add(autostartItem);

        Items.Add(Separator());

        var quit = Row("退出 VoiceAec", TrayGlyph.Exit, TrayPalette.Coral);
        quit.Danger = true;
        quit.Click += (_, _) => QuitRequested?.Invoke();
        Items.Add(quit);

        Items.Add(new TraySpacer(innerWidth, Px(7)));
        MinimumSize = new Size(innerWidth, 0);
        ApplyState(stateSource());
        SizeMenu();
    }

    private void SizeMenu()
    {
        var top = Items.Count > 0 ? Items[0].Bounds.Top : 0;
        var content = 0;
        foreach (ToolStripItem item in Items) content += item.Height + item.Margin.Vertical;
        ClientSize = new Size(innerWidth, top + content + Px(3));
        PerformLayout();
    }

    private int Px(float value) => (int)Math.Round(value * scale);

    private TrayMenuItem Row(string text, TrayGlyph glyph, Color accent) =>
        new(text, glyph, scale, accent)
        {
            Size = new Size(innerWidth, Px(38)),
            Font = Font,
            ForeColor = TrayPalette.Ink,
        };

    private ToolStripSeparator Separator() => new TraySeparator(innerWidth, Px(10));


    private void ApplyState(TrayMenuState state)
    {
        if (pauseItem != null)
        {
            pauseItem.Text = state.Paused ? "恢复处理与输出" : "暂停并静音";
            pauseItem.Glyph = state.Paused ? TrayGlyph.Resume : TrayGlyph.Pause;
            pauseItem.Accent = state.Paused ? TrayPalette.Green : TrayPalette.Violet;
            pauseItem.Invalidate();
        }
        if (autostartItem != null)
        {
            autostartItem.SwitchOn = state.Autostart;
            autostartItem.Invalidate();
        }
        if (modeHead != null)
        {
            modeHead.Hint = state.ModeLabel;
            modeHead.Invalidate();
        }
        if (modeHead?.DropDown is TrayDropDown dropDown)
            dropDown.Rebuild(state, ModeRequested ?? (_ => { }));
        header.Invalidate();
        Invalidate();
    }

    protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
    {
        base.OnOpening(e);
        ApplyState(stateSource());
        SizeMenu();
        if (!chromeApplied)
        {
            chromeApplied = true;
            RoundedWindow.Apply(this, Px(13));
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (chromeApplied) RoundedWindow.Apply(this, Px(13));
    }
}

internal sealed class TrayDropDown : ToolStripDropDownMenu
{
    private readonly float scale;
    private readonly int innerWidth;
    private bool chromeApplied;
    private string? builtSignature;

    public TrayDropDown(float scale, int innerWidth)
    {
        this.scale = scale;
        this.innerWidth = innerWidth;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        DropShadowEnabled = true;
        AutoSize = false;
        BackColor = TrayPalette.Surface;
        ForeColor = TrayPalette.Ink;
        Font = TrayFonts.Body(scale);
        Padding = new Padding(Px(6));
        Renderer = new TrayMenuRenderer(scale);
        LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
    }

    private int Px(float value) => (int)Math.Round(value * scale);

    public void Rebuild(TrayMenuState state, Action<string> onSelect)
    {
        var signature = string.Join("|", state.Modes.Select(m => $"{m.Key}:{m.Label}:{m.Available}"));
        if (signature == builtSignature && Items.Count == state.Modes.Count)
        {
            foreach (ToolStripItem item in Items)
                if (item is TrayMenuItem modeItem)
                {
                    modeItem.Checked = modeItem.Tag as string == state.Mode;
                    modeItem.Invalidate();
                }
            return;
        }
        builtSignature = signature;
        Items.Clear();
        Items.Add(new TraySpacer(innerWidth, Px(7)));
        foreach (var mode in state.Modes)
        {
            var item = new TrayMenuItem(mode.Label, TrayGlyph.Dot, scale, mode.Accent)
            {
                Size = new Size(innerWidth, Px(38)),
                Font = Font,
                ForeColor = TrayPalette.Ink,
                Checked = mode.Key == state.Mode,
                Enabled = mode.Available,
                Tag = mode.Key,
            };
            item.Click += (_, _) => onSelect((string)item.Tag!);
            Items.Add(item);
        }
        Items.Add(new TraySpacer(innerWidth, Px(7)));
        MinimumSize = new Size(innerWidth, 0);
        SizeMenu();
    }

    private void SizeMenu()
    {
        var top = Items.Count > 0 ? Items[0].Bounds.Top : 0;
        var content = 0;
        foreach (ToolStripItem item in Items) content += item.Height + item.Margin.Vertical;
        ClientSize = new Size(innerWidth, top + content + Px(3));
        PerformLayout();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!chromeApplied)
        {
            chromeApplied = true;
            RoundedWindow.Apply(this, Px(13));
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (chromeApplied) RoundedWindow.Apply(this, Px(13));
    }
}
