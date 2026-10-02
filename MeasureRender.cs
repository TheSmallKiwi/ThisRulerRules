using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ruler;

/// <summary>One line to draw, in whatever coordinate space the caller renders in.</summary>
internal readonly record struct LineSpec(Point A, Point B, int Index, string Label, bool Live);

/// <summary>
/// The on-screen rectangle of the image being measured (absolute physical px)
/// and that image's native pixel size. When set, PNG output is cropped to the
/// frame and resampled to the native size, so it drops straight onto the image.
/// </summary>
public sealed record ImageFrame(Rect Screen, int NativeW, int NativeH, bool FromClipboard);

/// <summary>
/// Shared drawing for measurements (line + tick caps + numbered badge + label),
/// with greedy label placement that avoids other labels, badges and lines.
/// Used both by the live overlay and by the PNG export / clipboard copy so the
/// two always look identical.
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
    private static readonly Pen LeaderPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x14, 0x14, 0x16)), 1.2));

    private const double BadgeR = 11;
    private const double PadX = 7, PadY = 3;

    // Label search: distance from the line, position along it, then side.
    private static readonly double[] Gaps = { 6, 22, 40, 60 };
    private static readonly double[] Along = { 0.5, 0.35, 0.65, 0.2, 0.8 };

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Pen p) { p.Freeze(); return p; }

    public static FormattedText Fmt(string text, double size, Brush brush, Typeface face, double ppd)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, ppd);

    /// <summary>Draw every line, then badges and labels laid out to avoid each other.</summary>
    public static void DrawAll(DrawingContext dc, IReadOnlyList<LineSpec> lines, double ppd, Typeface face, Rect bounds)
    {
        foreach (var l in lines)
            DrawLine(dc, l.A, l.B, l.Live);

        var texts = new FormattedText[lines.Count];
        for (int i = 0; i < lines.Count; i++)
            texts[i] = Fmt(lines[i].Label, 12, Brushes.White, face, ppd);

        var (badges, labels, leaders) = Layout(lines, texts, bounds);

        for (int i = 0; i < lines.Count; i++)
            if (leaders[i] is { } anchor)
                dc.DrawLine(LeaderPen, anchor, Nearest(labels[i], anchor));
        for (int i = 0; i < lines.Count; i++)
        {
            dc.DrawRoundedRectangle(LabelBg, null, labels[i], 5, 5);
            dc.DrawText(texts[i], new Point(labels[i].X + PadX, labels[i].Y + PadY));
        }
        for (int i = 0; i < lines.Count; i++)
            DrawBadge(dc, badges[i], lines[i].Index, face, ppd);
    }

    private static void DrawLine(DrawingContext dc, Point a, Point b, bool live)
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
    }

    private static void DrawBadge(DrawingContext dc, Point center, int index, Typeface face, double ppd)
    {
        dc.DrawEllipse(BadgeBg, new Pen(Brushes.White, 1.5), center, BadgeR, BadgeR);
        var ft = Fmt(index.ToString(), 11.5, Brushes.White, face, ppd);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    // ---- layout ------------------------------------------------------------

    /// <summary>
    /// Greedy placement in index order: badges first (they hug an endpoint),
    /// then labels. Each candidate is scored by overlap with everything already
    /// placed plus any line it would cover; the first collision-free candidate
    /// in preference order wins, else the cheapest. Labels pushed away from
    /// their line get a leader back to it.
    /// </summary>
    private static (Point[] Badges, Rect[] Labels, Point?[] Leaders) Layout(
        IReadOnlyList<LineSpec> lines, FormattedText[] texts, Rect bounds)
    {
        int n = lines.Count;
        var taken = new List<Rect>(2 * n);
        var badges = new Point[n];
        var labels = new Rect[n];
        var leaders = new Point?[n];

        for (int i = 0; i < n; i++)
        {
            var (a, b) = (lines[i].A, lines[i].B);
            Vector dir = Unit(b - a);
            if (dir.LengthSquared == 0) dir = new Vector(1, 0);
            Vector normal = new(-dir.Y, dir.X);
            Point[] cands = { a - dir * 16, b + dir * 16, a + normal * 18, a - normal * 18, b + normal * 18, b - normal * 18 };

            Rect best = default;
            double bestCost = double.MaxValue;
            for (int c = 0; c < cands.Length; c++)
            {
                var r = Clamp(new Rect(cands[c].X - BadgeR, cands[c].Y - BadgeR, 2 * BadgeR, 2 * BadgeR), bounds);
                double cost = Cost(r, taken, lines) + c * 2;
                if (cost < bestCost) { bestCost = cost; best = r; }
                if (cost == c * 2) break;
            }
            taken.Add(best);
            badges[i] = new Point(best.X + BadgeR, best.Y + BadgeR);
        }

        for (int i = 0; i < n; i++)
        {
            var (a, b) = (lines[i].A, lines[i].B);
            Vector dir = Unit(b - a);
            Vector normal = dir.LengthSquared == 0 ? new Vector(0, 1) : new Vector(-dir.Y, dir.X);
            double w = texts[i].Width + PadX * 2;
            double h = texts[i].Height + PadY * 2;

            Rect best = default;
            Point bestAnchor = default;
            double bestCost = double.MaxValue;
            bool done = false;
            int pref = 0;
            for (int g = 0; g < Gaps.Length && !done; g++)
                foreach (double t in Along)
                {
                    foreach (int side in new[] { 1, -1 })
                    {
                        Point p = a + (b - a) * t;
                        Vector nn = normal * side;
                        // Offset so the label's nearest edge sits `gap` from the line.
                        double ext = Math.Abs(nn.X) * w / 2 + Math.Abs(nn.Y) * h / 2;
                        Point c = p + nn * (Gaps[g] + ext);
                        var r = Clamp(new Rect(c.X - w / 2, c.Y - h / 2, w, h), bounds);
                        double collision = Cost(r, taken, lines);
                        double cost = collision + pref++ * 3;
                        if (cost < bestCost) { bestCost = cost; best = r; bestAnchor = p; }
                        if (collision == 0) { done = true; break; }
                    }
                    if (done) break;
                }
            taken.Add(best);
            labels[i] = best;
            if (Distance(best, bestAnchor) > 14)
                leaders[i] = bestAnchor;
        }

        return (badges, labels, leaders);
    }

    private static double Cost(Rect r, List<Rect> taken, IReadOnlyList<LineSpec> lines)
    {
        double cost = 0;
        foreach (var t in taken)
        {
            var x = Rect.Intersect(r, t);
            if (!x.IsEmpty) cost += x.Width * x.Height + 50;
        }
        var hit = r;
        hit.Inflate(3, 3);
        foreach (var l in lines)
            if (SegmentHitsRect(l.A, l.B, hit))
                cost += 400;
        return cost;
    }

    /// <summary>Liang–Barsky clip test.</summary>
    private static bool SegmentHitsRect(Point a, Point b, Rect r)
    {
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        bool Clip(double p, double q)
        {
            if (p == 0) return q >= 0;
            double t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        return Clip(-dx, a.X - r.Left) && Clip(dx, r.Right - a.X)
            && Clip(-dy, a.Y - r.Top) && Clip(dy, r.Bottom - a.Y);
    }

    private static Rect Clamp(Rect r, Rect bounds)
    {
        double x = Math.Clamp(r.X, bounds.Left + 2, Math.Max(bounds.Left + 2, bounds.Right - r.Width - 2));
        double y = Math.Clamp(r.Y, bounds.Top + 2, Math.Max(bounds.Top + 2, bounds.Bottom - r.Height - 2));
        return new Rect(x, y, r.Width, r.Height);
    }

    private static Point Nearest(Rect r, Point p)
        => new(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));

    private static double Distance(Rect r, Point p) => (Nearest(r, p) - p).Length;

    private static Vector Unit(Vector v)
    {
        double len = v.Length;
        return len < 1e-6 ? new Vector(0, 0) : new Vector(v.X / len, v.Y / len);
    }

    // ---- PNG output ----------------------------------------------------------

    /// <summary>
    /// Render the measurements onto a transparent 32-bit bitmap. With a frame,
    /// the output is cropped to it and scaled to the image's native size;
    /// without one, it's the lines' bounding box (plus padding) in absolute
    /// screen pixels. Returns null if there is nothing to render.
    /// </summary>
    public static BitmapSource? Render(IReadOnlyList<Measurement> items, ImageFrame? frame)
    {
        if (items.Count == 0)
            return null;

        double originX, originY, kx = 1, ky = 1;
        int width, height;
        Rect bounds;
        if (frame != null)
        {
            originX = frame.Screen.X;
            originY = frame.Screen.Y;
            bounds = new Rect(0, 0, frame.Screen.Width, frame.Screen.Height);
            width = frame.NativeW;
            height = frame.NativeH;
            kx = width / bounds.Width;
            ky = height / bounds.Height;
        }
        else
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var m in items)
            {
                minX = Math.Min(minX, Math.Min(m.Start.X, m.End.X));
                minY = Math.Min(minY, Math.Min(m.Start.Y, m.End.Y));
                maxX = Math.Max(maxX, Math.Max(m.Start.X, m.End.X));
                maxY = Math.Max(maxY, Math.Max(m.Start.Y, m.End.Y));
            }
            const double pad = 120; // room for badges + labels around the extreme lines
            originX = minX - pad;
            originY = minY - pad;
            width = Math.Max(1, (int)Math.Ceiling(maxX - minX + 2 * pad));
            height = Math.Max(1, (int)Math.Ceiling(maxY - minY + 2 * pad));
            bounds = new Rect(0, 0, width, height);
        }

        var origin = new Vector(originX, originY);
        var specs = new List<LineSpec>(items.Count);
        foreach (var m in items)
            specs.Add(new LineSpec(m.Start - origin, m.End - origin, m.Index, m.OverlayLabel, false));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(kx, ky));
            DrawAll(dc, specs, 1.0, new Typeface("Segoe UI"), bounds);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); // untouched pixels stay transparent
        rtb.Freeze();
        return rtb;
    }

    public static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Write the rendered measurements to a PNG file. False if nothing to export.</summary>
    public static bool ExportPng(IReadOnlyList<Measurement> items, ImageFrame? frame, string path)
    {
        var bmp = Render(items, frame);
        if (bmp == null)
            return false;
        File.WriteAllBytes(path, EncodePng(bmp));
        return true;
    }
}
