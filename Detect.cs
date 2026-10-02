using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ruler;

/// <summary>Random-access RGB view of the magnifier snapshot.</summary>
internal sealed class Pixels
{
    public readonly int W, H;
    private readonly int[] _px;

    public Pixels(BitmapSource src)
    {
        if (src.Format != PixelFormats.Bgr32 && src.Format != PixelFormats.Bgra32)
            src = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
        W = src.PixelWidth;
        H = src.PixelHeight;
        _px = new int[W * H];
        src.CopyPixels(_px, W * 4, 0);
    }

    public bool Inside(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;
    public int Rgb(int x, int y) => _px[y * W + x] & 0xFFFFFF;

    public double Lum(int x, int y)
    {
        int c = _px[y * W + x];
        return 0.299 * ((c >> 16) & 255) + 0.587 * ((c >> 8) & 255) + 0.114 * (c & 255);
    }

    public static bool Close(int a, int b, int tol)
        => Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= tol
        && Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= tol
        && Math.Abs((a & 255) - (b & 255)) <= tol;
}

/// <summary>
/// One-click detection on the snapshot: solid scale bars, and the edges of an
/// image sitting on a flat canvas (e.g. PureRef). All coordinates are snapshot
/// pixels, with pixel x spanning [x, x+1).
/// </summary>
internal static class Detect
{
    /// <summary>
    /// Find a high-contrast horizontal or vertical bar (light-on-dark or
    /// dark-on-light) under the click. Returns its centreline with sub-pixel
    /// ends estimated from anti-aliased edge coverage.
    /// </summary>
    public static bool ScaleBar(Pixels p, int x, int y, out Point a, out Point b)
    {
        a = b = default;
        if (!p.Inside(x, y))
            return false;

        const int Win = 30;
        double lo = 255, hi = 0;
        for (int yy = Math.Max(0, y - Win); yy <= Math.Min(p.H - 1, y + Win); yy++)
            for (int xx = Math.Max(0, x - Win); xx <= Math.Min(p.W - 1, x + Win); xx++)
            {
                double l = p.Lum(xx, yy);
                if (l < lo) lo = l;
                if (l > hi) hi = l;
            }
        if (hi - lo < 50)
            return false;
        double thr = (lo + hi) / 2;

        bool light = p.Lum(x, y) >= thr;
        if (TryBar(p, x, y, light, thr, lo, hi, out a, out b))
            return true;

        // The click may have landed just beside a thin bar: try the nearest
        // pixel of the opposite class.
        for (int r = 1; r <= 4; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r || !p.Inside(x + dx, y + dy))
                        continue;
                    if ((p.Lum(x + dx, y + dy) >= thr) != light)
                        return TryBar(p, x + dx, y + dy, !light, thr, lo, hi, out a, out b);
                }
        return false;
    }

    private static bool TryBar(Pixels p, int sx, int sy, bool light, double thr, double lo, double hi,
        out Point a, out Point b)
    {
        a = b = default;
        bool In(int x, int y) => p.Inside(x, y) && (p.Lum(x, y) >= thr) == light;

        // Bounded 4-connected flood fill.
        const int MaxArea = 80_000;
        var seen = new HashSet<int> { sy * p.W + sx };
        var stack = new Stack<(int X, int Y)>();
        stack.Push((sx, sy));
        int x0 = sx, x1 = sx, y0 = sy, y1 = sy;
        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            x0 = Math.Min(x0, cx); x1 = Math.Max(x1, cx);
            y0 = Math.Min(y0, cy); y1 = Math.Max(y1, cy);
            foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
            {
                if (!In(nx, ny) || !seen.Add(ny * p.W + nx))
                    continue;
                if (seen.Count > MaxArea)
                    return false;
                stack.Push((nx, ny));
            }
        }

        bool horiz = x1 - x0 >= y1 - y0;
        int len = horiz ? x1 - x0 + 1 : y1 - y0 + 1;
        int thick = horiz ? y1 - y0 + 1 : x1 - x0 + 1;
        if (len < 12 || len < thick * 3)
            return false;

        // Map (u along the bar, v across it) back to x/y.
        bool Mem(int u, int v) => seen.Contains(horiz ? v * p.W + u : u * p.W + v);
        double Lum(int u, int v) => horiz ? p.Lum(u, v) : p.Lum(v, u);
        bool InsideUv(int u, int v) => horiz ? p.Inside(u, v) : p.Inside(v, u);
        int u0 = horiz ? x0 : y0, u1 = horiz ? x1 : y1;
        int v0 = horiz ? y0 : x0, v1 = horiz ? y1 : x1;

        // The bar body = rows that span (nearly) the full length; end ticks don't.
        var count = new int[v1 - v0 + 1];
        for (int v = v0; v <= v1; v++)
            for (int u = u0; u <= u1; u++)
                if (Mem(u, v)) count[v - v0]++;
        int maxCount = count.Max();
        var rows = Enumerable.Range(v0, count.Length).Where(v => count[v - v0] >= maxCount * 0.9).ToList();

        int vb0 = rows.Min(), vb1 = rows.Max();

        // Sub-pixel length. Real scale bars are often thin and soft, so their
        // centre never reaches the brightness of nearby label text. Instead,
        // collapse the bar across its thickness into a 1-D profile along its
        // length (background subtracted), normalise by the profile's own median
        // along the middle of the bar, and sum: a fully covered column counts 1,
        // a partly covered end column its fraction, blur or not.
        int mid0 = u0 + (u1 - u0) / 5, mid1 = u1 - (u1 - u0) / 5;
        var bgSamples = new List<double>();
        for (int u = mid0; u <= mid1; u++)
            foreach (int v in new[] { vb0 - 5, vb0 - 4, vb1 + 4, vb1 + 5 })
                if (InsideUv(u, v))
                    bgSamples.Add(Lum(u, v));
        if (bgSamples.Count == 0)
            return false;
        double bgL = Median(bgSamples);
        double sign = light ? 1 : -1;

        double Profile(int u)
        {
            double s = 0;
            for (int v = vb0 - 2; v <= vb1 + 2; v++)
                if (InsideUv(u, v))
                    s += sign * (Lum(u, v) - bgL);
            return s;
        }

        var midProfile = new List<double>();
        for (int u = mid0; u <= mid1; u++)
            midProfile.Add(Profile(u));
        double full = Median(midProfile);
        if (full <= 0)
            return false;
        double Cov(int u) => Math.Clamp(Profile(u) / full, 0, 1);

        double length = 0;
        for (int u = u0; u <= u1; u++)
            length += Cov(u);
        // Pick up the soft ends beyond the thresholded extent.
        for (int dir = -1; dir <= 1; dir += 2)
            for (int i = 1, u = dir < 0 ? u0 - 1 : u1 + 1; i <= 12 && InsideUv(u, vb0); i++, u += dir)
            {
                double c = Cov(u);
                if (c < 0.03)
                    break;
                length += c;
            }

        double uc = (u0 + u1 + 1) / 2.0;
        double vc = (vb0 + vb1 + 1) / 2.0;

        a = horiz ? new Point(uc - length / 2, vc) : new Point(vc, uc - length / 2);
        b = horiz ? new Point(uc + length / 2, vc) : new Point(vc, uc + length / 2);
        return true;
    }

    private static double Median(List<double> xs)
    {
        xs.Sort();
        int n = xs.Count;
        return n % 2 == 1 ? xs[n / 2] : (xs[n / 2 - 1] + xs[n / 2]) / 2;
    }

    /// <summary>
    /// Find the rectangle of an image displayed on a flat-colour canvas: walk
    /// out from the click in four directions to the first long uniform run of
    /// a shared colour, then check that colour borders all four sides. A flat
    /// photo background (e.g. black behind a specimen) also passes that test,
    /// so the largest valid rectangle wins: the canvas surrounds the image.
    /// </summary>
    public static bool ImageFrame(Pixels p, int x, int y, out Int32Rect frame)
    {
        frame = default;
        if (!p.Inside(x, y))
            return false;

        var left = Runs(p, x, y, -1, 0);
        var right = Runs(p, x, y, 1, 0);
        var up = Runs(p, x, y, 0, -1);
        var down = Runs(p, x, y, 0, 1);

        bool found = false;
        foreach (var (lpos, color) in left)
        {
            int? r = First(right, color), u = First(up, color), d = First(down, color);
            if (r == null || u == null || d == null)
                continue;
            int fx = lpos + 1, fy = u.Value + 1, fr = r.Value, fb = d.Value;
            if (fr - fx < 16 || fb - fy < 16)
                continue;
            if (Border(p, color, lpos, fy, 0, 1, fb - fy) && Border(p, color, fr, fy, 0, 1, fb - fy)
                && Border(p, color, fx, u.Value, 1, 0, fr - fx) && Border(p, color, fx, fb, 1, 0, fr - fx))
            {
                if (!found || (long)(fr - fx) * (fb - fy) > (long)frame.Width * frame.Height)
                    frame = new Int32Rect(fx, fy, fr - fx, fb - fy);
                found = true;
            }
        }
        return found;
    }

    private const int Tol = 3;

    /// <summary>Uniform runs (≥ 24 px) met walking from (x,y): position nearest the click + colour.</summary>
    private static List<(int Pos, int Color)> Runs(Pixels p, int x, int y, int dx, int dy)
    {
        const int MinRun = 24;
        var runs = new List<(int, int)>();
        int cx = x + dx, cy = y + dy;
        while (p.Inside(cx, cy) && runs.Count < 12)
        {
            int c = p.Rgb(cx, cy);
            int n = 1;
            while (p.Inside(cx + dx * n, cy + dy * n) && Pixels.Close(p.Rgb(cx + dx * n, cy + dy * n), c, Tol))
                n++;
            if (n >= MinRun)
                runs.Add((dx != 0 ? cx : cy, c));
            cx += dx * n;
            cy += dy * n;
        }
        return runs;
    }

    private static int? First(List<(int Pos, int Color)> runs, int color)
    {
        foreach (var (pos, c) in runs)
            if (Pixels.Close(c, color, Tol))
                return pos;
        return null;
    }

    /// <summary>At least 90% of the line outside one edge is canvas colour.</summary>
    private static bool Border(Pixels p, int color, int x, int y, int dx, int dy, int n)
    {
        int ok = 0;
        for (int i = 0; i < n; i++)
            if (p.Inside(x + dx * i, y + dy * i) && Pixels.Close(p.Rgb(x + dx * i, y + dy * i), color, Tol))
                ok++;
        return ok >= n * 0.9;
    }
}
