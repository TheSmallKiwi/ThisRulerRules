using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ruler;

/// <summary>
/// Shared drawing for a single measurement (line + tick caps + numbered badge +
/// label). Used both by the live overlay and by the transparent-PNG export so
/// the two always look identical.
/// </summary>
internal static class MeasureRender
{
    private static readonly Color LineColor = Color.FromRgb(0xFF, 0x2D, 0x2D);
    private static readonly Color LiveColor = Color.FromRgb(0xFF, 0x9F, 0x0A);

    public static readonly Brush LineBrush = Frozen(new SolidColorBrush(LineColor));
    public static readonly Brush LiveBrush = Frozen(new SolidColorBrush(LiveColor));
    public static readonly Brush LabelBg = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x14, 0x16)));
    private static readonly Brush Halo = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush BadgeBg = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16)));

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    public static FormattedText Fmt(string text, double size, Brush brush, Typeface face, double ppd)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, ppd);

    public static void DrawMeasurement(DrawingContext dc, Point a, Point b, int index, string label,
        bool live, double ppd, Typeface face, double boundsW, double boundsH)
    {
        Brush color = live ? LiveBrush : LineBrush;
        var haloPen = new Pen(Halo, 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var mainPen = new Pen(color, 2.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (live)
            mainPen.DashStyle = new DashStyle(new double[] { 5, 3 }, 0);

        dc.DrawLine(haloPen, a, b);
        dc.DrawLine(mainPen, a, b);

        Vector dir = Unit(b - a);
        Vector normal = new(-dir.Y, dir.X);
        var tick = new Pen(color, 2.0);
        dc.DrawLine(tick, a - normal * 6, a + normal * 6);
        dc.DrawLine(tick, b - normal * 6, b + normal * 6);

        DrawBadge(dc, a - dir * 16, index, face, ppd);

        Point mid = new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        DrawLabel(dc, label, mid + normal * 16, face, ppd, boundsW, boundsH);
    }

    private static void DrawBadge(DrawingContext dc, Point center, int index, Typeface face, double ppd)
    {
        const double r = 11;
        dc.DrawEllipse(BadgeBg, new Pen(Brushes.White, 1.5), center, r, r);
        var ft = Fmt(index.ToString(), 11.5, Brushes.White, face, ppd);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    private static void DrawLabel(DrawingContext dc, string text, Point center, Typeface face, double ppd,
        double boundsW, double boundsH)
    {
        var ft = Fmt(text, 12, Brushes.White, face, ppd);
        double padX = 7, padY = 3;
        double w = ft.Width + padX * 2;
        double h = ft.Height + padY * 2;
        double nx = Math.Clamp(center.X - w / 2, 2, Math.Max(2, boundsW - w - 2));
        double ny = Math.Clamp(center.Y - h / 2, 2, Math.Max(2, boundsH - h - 2));
        var rect = new Rect(nx, ny, w, h);
        dc.DrawRoundedRectangle(LabelBg, null, rect, 5, 5);
        dc.DrawText(ft, new Point(rect.X + padX, rect.Y + padY));
    }

    private static Vector Unit(Vector v)
    {
        double len = v.Length;
        return len < 1e-6 ? new Vector(0, 0) : new Vector(v.X / len, v.Y / len);
    }

    /// <summary>
    /// Render the measurements onto a transparent PNG (32-bit, alpha) sized to
    /// their bounding box plus padding. Coordinates are absolute screen pixels,
    /// so the image lines up with a screenshot of what was measured.
    /// Returns false if there is nothing to export.
    /// </summary>
    public static bool ExportPng(IReadOnlyList<Measurement> items, string path)
    {
        if (items.Count == 0)
            return false;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var m in items)
        {
            minX = Math.Min(minX, Math.Min(m.Start.X, m.End.X));
            minY = Math.Min(minY, Math.Min(m.Start.Y, m.End.Y));
            maxX = Math.Max(maxX, Math.Max(m.Start.X, m.End.X));
            maxY = Math.Max(maxY, Math.Max(m.Start.Y, m.End.Y));
        }

        const double pad = 120; // room for badges + labels around the extreme lines
        double originX = minX - pad, originY = minY - pad;
        int width = Math.Max(1, (int)Math.Ceiling(maxX - minX + 2 * pad));
        int height = Math.Max(1, (int)Math.Ceiling(maxY - minY + 2 * pad));

        var face = new Typeface("Segoe UI");
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            foreach (var m in items)
            {
                Point a = new(m.Start.X - originX, m.Start.Y - originY);
                Point b = new(m.End.X - originX, m.End.Y - originY);
                DrawMeasurement(dc, a, b, m.Index, m.OverlayLabel, false, 1.0, face, width, height);
            }
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); // untouched pixels stay transparent

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
        return true;
    }
}
