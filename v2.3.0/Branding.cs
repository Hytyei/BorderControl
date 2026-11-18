using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace BorderControl;

/// <summary>
/// Border Control artwork, drawn as vectors at each pixel size so every icon stays crisp:
/// the app icon (graphite tile, green shield, dashed "border" ring) and the status tray shields.
/// </summary>
public static class Branding
{
    // Same shield as MainWindow.xaml's ShieldGeo, in a 0..100 box.
    private static GraphicsPath Shield(float x, float y, float size)
    {
        float k = size / 100f;
        PointF P(float px, float py) => new(x + px * k, y + py * k);

        var p = new GraphicsPath();
        p.AddBezier(P(50, 5), P(62, 13), P(75, 17), P(89, 18));
        p.AddLine(P(89, 18), P(89, 47));
        p.AddBezier(P(89, 47), P(89, 72), P(72, 87), P(50, 95));
        p.AddBezier(P(50, 95), P(28, 87), P(11, 72), P(11, 47));
        p.AddLine(P(11, 47), P(11, 18));
        p.AddBezier(P(11, 18), P(25, 17), P(38, 13), P(50, 5));
        p.CloseFigure();
        return p;
    }

    public static readonly Color Ok = Color.FromArgb(0x3D, 0xDC, 0x97);
    public static readonly Color Warn = Color.FromArgb(0xFF, 0xB0, 0x20);
    public static readonly Color Bad = Color.FromArgb(0xFF, 0x5C, 0x5C);
    public static readonly Color Idle = Color.FromArgb(0x8A, 0x91, 0xA4);
    private static readonly Color Ink = Color.FromArgb(0x0E, 0x10, 0x16);

    public static Color For(Level level) => level switch
    {
        Level.Ok => Ok,
        Level.Warn => Warn,
        Level.Bad => Bad,
        _ => Idle,
    };

    // ───────────────────────── tray ─────────────────────────

    /// <summary>Status shield for the notification area, drawn natively at <paramref name="s"/> pixels.</summary>
    public static Bitmap RenderTray(Level level, int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var c = For(level);
        // The shield's own box is 78% wide, so scale it up slightly to fill the icon cell.
        float size = s * 1.06f;
        float ox = (s - size) / 2f, oy = (s - size) / 2f + s * 0.005f;

        using (var shield = Shield(ox, oy, size))
        {
            using var fill = new LinearGradientBrush(new RectangleF(0, 0, s, s), Mix(c, Color.White, 0.28f), Mix(c, Color.Black, 0.12f), 90f);
            g.FillPath(fill, shield);

            // Thin dark rim keeps the shape readable on light taskbars.
            using var rim = new Pen(Color.FromArgb(150, 8, 10, 14), Math.Max(1f, s / 24f));
            g.DrawPath(rim, shield);
        }

        DrawGlyph(g, level, ox, oy, size, Color.FromArgb(235, Ink), Math.Max(1.75f, s * 0.1f), 0.5f);
        return bmp;
    }

    /// <summary>Multi-size tray icon; Windows picks the frame that matches the current DPI.</summary>
    public static Icon TrayIcon(Level level, Size wanted)
    {
        var bytes = BuildIco(new[] { 16, 20, 24, 32, 40, 48, 64 }, s => RenderTray(level, s));
        using var ms = new MemoryStream(bytes);
        return new Icon(ms, wanted);
    }

    // ───────────────────────── tray speed ─────────────────────────

    /// <summary>
    /// Live download speed for the notification area: the number on top, the unit below in the
    /// status colour (e.g. "2.4" / "MB"). Drawn natively at the tray icon size so it stays sharp.
    /// </summary>
    public static Bitmap RenderSpeed(int s, string value, string unit, Color accent, bool lightTaskbar = false)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };

        // Shrink the number until it fits the icon width (e.g. "1023").
        float size = s * (value.Length >= 4 ? 0.46f : 0.56f);
        using var valueFont = new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Pixel);
        using var unitFont = new Font("Segoe UI", s * 0.40f, FontStyle.Bold, GraphicsUnit.Pixel);
        // Light taskbar: dark number and a deeper accent, no shadow. Dark taskbar: white number with a soft shadow.
        using var ink = new SolidBrush(lightTaskbar ? Color.FromArgb(0x1A, 0x1D, 0x24) : Color.FromArgb(0xF4, 0xF6, 0xFA));
        using var unitBrush = new SolidBrush(lightTaskbar ? Mix(accent, Color.Black, 0.35f) : Mix(accent, Color.White, 0.25f));
        using var shadow = new SolidBrush(lightTaskbar ? Color.Transparent : Color.FromArgb(150, 0, 0, 0));

        var top = new RectangleF(0, -s * 0.06f, s, s * 0.62f);
        var bottom = new RectangleF(0, s * 0.52f, s, s * 0.48f);
        // A soft 1-px shadow keeps the white number readable on light taskbars.
        g.DrawString(value, valueFont, shadow, new RectangleF(top.X + 0.6f, top.Y + 0.8f, top.Width, top.Height), fmt);
        g.DrawString(value, valueFont, ink, top, fmt);
        g.DrawString(unit, unitFont, shadow, new RectangleF(bottom.X + 0.6f, bottom.Y + 0.8f, bottom.Width, bottom.Height), fmt);
        g.DrawString(unit, unitFont, unitBrush, bottom, fmt);
        return bmp;
    }

    // ───────────────────────── app icon / logo ─────────────────────────

    public static Bitmap RenderAppIcon(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        if (s <= 16)
        {
            // Too small for the tile – the bare green shield reads better.
            using var t = RenderTray(Level.Ok, s);
            g.DrawImage(t, 0, 0, s, s);
            return bmp;
        }

        // Tile
        float inset = s * 0.04f;
        var tile = new RectangleF(inset, inset, s - 2 * inset, s - 2 * inset);
        using (var path = RoundRect(tile, s * 0.23f))
        {
            using var bg = new LinearGradientBrush(tile, Color.FromArgb(0x1C, 0x22, 0x2E), Color.FromArgb(0x0B, 0x0D, 0x12), 90f);
            g.FillPath(bg, path);

            // Green light behind the shield
            using var glowPath = new GraphicsPath();
            glowPath.AddEllipse(s * 0.12f, s * 0.08f, s * 0.76f, s * 0.76f);
            using var glow = new PathGradientBrush(glowPath)
            {
                CenterColor = Color.FromArgb(s >= 48 ? 70 : 45, Ok),
                SurroundColors = new[] { Color.FromArgb(0, Ok) },
            };
            var clip = g.Clip;
            g.SetClip(path);
            g.FillEllipse(glow, s * 0.12f, s * 0.08f, s * 0.76f, s * 0.76f);
            g.Clip = clip;

            using var edge = new Pen(Color.FromArgb(s >= 48 ? 38 : 28, 255, 255, 255), Math.Max(1f, s / 128f));
            g.DrawPath(edge, path);
        }

        // Dashed "border" ring – the Border Control mark. Below 48 px dashes turn to mush, so use a hairline.
        float ringSize = s * 0.84f;
        using (var ring = Shield((s - ringSize) / 2f, (s - ringSize) / 2f + s * 0.01f, ringSize))
        {
            float w = Math.Max(1f, s * 0.03f);
            using var pen = new Pen(Color.FromArgb(s >= 48 ? 190 : 110, Ok), w);
            if (s >= 48)
            {
                pen.DashPattern = new[] { 2.4f, 1.6f };
                pen.DashCap = DashCap.Round;
            }
            g.DrawPath(pen, ring);
        }

        // Shield
        float sh = s * 0.6f;
        float sx = (s - sh) / 2f, sy = (s - sh) / 2f + s * 0.01f;
        using (var shield = Shield(sx, sy, sh))
        {
            using var fill = new LinearGradientBrush(new RectangleF(sx, sy, sh, sh),
                Color.FromArgb(0x7A, 0xF5, 0xC0), Color.FromArgb(0x1A, 0xA8, 0x72), 90f);
            g.FillPath(fill, shield);
            if (s >= 48)
            {
                // soft top highlight
                using var hi = new LinearGradientBrush(new RectangleF(sx, sy, sh, sh * 0.5f),
                    Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f);
                var clip = g.Clip;
                g.SetClip(shield);
                g.FillRectangle(hi, sx, sy, sh, sh * 0.5f);
                g.Clip = clip;
            }
        }

        DrawGlyph(g, Level.Ok, sx, sy, sh, Color.FromArgb(240, Ink), Math.Max(1.6f, s * 0.05f), 0.42f);
        return bmp;
    }

    public static byte[] AppIco() =>
        BuildIco(new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 }, RenderAppIcon);

    // ───────────────────────── shared drawing ─────────────────────────

    /// <summary>Draws the status glyph inside a shield drawn at (x, y, size).</summary>
    private static void DrawGlyph(Graphics g, Level level, float x, float y, float size, Color color, float width, float scale)
    {
        // Glyphs are authored in a 20×20 box (same as the window), centred slightly above the shield's middle.
        float box = size * scale;
        float gx = x + size * 0.5f - box / 2f, gy = y + size * 0.47f - box / 2f;
        PointF P(float px, float py) => new(gx + px / 20f * box, gy + py / 20f * box);

        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (level)
        {
            case Level.Ok:
                g.DrawLines(pen, new[] { P(4.5f, 10.5f), P(8.5f, 14.5f), P(15.5f, 6f) });
                break;
            case Level.Bad:
                g.DrawLine(pen, P(6, 6), P(14, 14));
                g.DrawLine(pen, P(14, 6), P(6, 14));
                break;
            case Level.Warn:
                g.DrawLine(pen, P(10, 4.5f), P(10, 11.2f));
                using (var dot = new SolidBrush(color))
                {
                    var d = P(10, 15.3f);
                    float r = width * 0.62f;
                    g.FillEllipse(dot, d.X - r, d.Y - r, r * 2, r * 2);
                }
                break;
            default:
                g.DrawLine(pen, P(5.5f, 10), P(14.5f, 10));
                break;
        }
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        a.A,
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

    // ───────────────────────── .ico writer ─────────────────────────

    /// <summary>Writes a multi-frame .ico: 32-bit DIB frames below 256 px, PNG for 256 px.</summary>
    public static byte[] BuildIco(int[] sizes, Func<int, Bitmap> render)
    {
        var frames = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using var bmp = render(sizes[i]);
            frames[i] = sizes[i] >= 256 ? Png(bmp) : Dib(bmp);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);

        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            int s = sizes[i];
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(frames[i].Length);
            w.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var f in frames) w.Write(f);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] Png(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] Dib(Bitmap bmp)
    {
        int s = bmp.Width;
        int maskStride = ((s + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // BITMAPINFOHEADER – height is doubled (colour + AND mask)
        w.Write(40);
        w.Write(s);
        w.Write(s * 2);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(0);
        w.Write(s * s * 4 + maskStride * s);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);

        var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[s * 4];
            for (int y = s - 1; y >= 0; y--) // bottom-up
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                w.Write(row);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        w.Write(new byte[maskStride * s]); // alpha channel does the masking
        w.Flush();
        return ms.ToArray();
    }
}
