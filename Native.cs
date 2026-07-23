using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ruler;

/// <summary>
/// Win32 wrappers for per-monitor geometry, screen capture, window placement,
/// and OS-level hit testing. Uses only user32 / gdi32 / shcore — no packages.
/// </summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    /// <summary>Physical bounds + DPI scale of one monitor, plus the cursor.</summary>
    public struct MonitorSpec
    {
        public int Left, Top, Width, Height; // physical pixels, virtual-screen coords
        public double Scale;                 // physical pixels per DIP
        public int CursorX, CursorY;         // physical pixels
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);

    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int SRCCOPY = 0x00CC0020;
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    public static (int Width, int Height) PrimaryPhysical()
        => (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    public static POINT CursorPos() { GetCursorPos(out var p); return p; }

    /// <summary>The monitor the cursor is currently on (physical bounds + DPI).</summary>
    public static MonitorSpec MonitorUnderCursor()
    {
        GetCursorPos(out var p);
        IntPtr hMon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var spec = SpecFromMonitor(hMon);
        spec.CursorX = p.X;
        spec.CursorY = p.Y;
        return spec;
    }

    /// <summary>Every monitor's physical bounds + DPI (cursor set to each center).</summary>
    public static List<MonitorSpec> AllMonitors()
    {
        var list = new List<MonitorSpec>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var s = SpecFromMonitor(h);
            s.CursorX = s.Left + s.Width / 2;
            s.CursorY = s.Top + s.Height / 2;
            list.Add(s);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static MonitorSpec SpecFromMonitor(IntPtr hMon)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);

        double scale = 1.0;
        try { if (GetDpiForMonitor(hMon, 0, out uint dx, out _) == 0 && dx > 0) scale = dx / 96.0; }
        catch { /* pre-8.1 fallback: assume 1.0 */ }

        return new MonitorSpec
        {
            Left = mi.rcMonitor.Left,
            Top = mi.rcMonitor.Top,
            Width = mi.rcMonitor.Right - mi.rcMonitor.Left,
            Height = mi.rcMonitor.Bottom - mi.rcMonitor.Top,
            Scale = scale,
        };
    }

    /// <summary>Place &amp; size a window using physical pixels, on top.</summary>
    public static void PlaceWindow(IntPtr hwnd, int x, int y, int w, int h)
        => SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_SHOWWINDOW);

    /// <summary>
    /// Uniform window translucency including the title bar (255 = opaque).
    /// Done via WS_EX_LAYERED because WPF's Window.Opacity doesn't apply to
    /// non-client chrome on a normal (non-AllowsTransparency) window.
    /// </summary>
    /// <summary>Make a window ignore the mouse entirely (clicks fall through).</summary>
    public static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        if (hwnd == IntPtr.Zero)
            return;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        int updated = clickThrough
            ? ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
            : ex & ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        if (updated != ex)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, updated);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    public static RECT WindowRect(IntPtr hwnd) { GetWindowRect(hwnd, out var r); return r; }

    /// <summary>The top-level window Windows would route a click at (x,y) to.</summary>
    public static IntPtr WindowAt(int x, int y) => WindowFromPoint(new POINT(x, y));

    /// <summary>Capture a physical screen region (works across monitors).</summary>
    public static BitmapSource CaptureRegion(int x, int y, int w, int h)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY);

            var bi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            int stride = w * 4;
            byte[] bits = new byte[stride * h];
            GetDIBits(mem, bmp, 0, (uint)h, bits, ref bi, 0);

            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bits, stride);
            src.Freeze();
            return src;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static BitmapSource CaptureScreen(int w, int h) => CaptureRegion(0, 0, w, h);
}

/// <summary>Tiny append-only log to help diagnose input issues in the field.</summary>
internal static class Diag
{
    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RulerLog.txt");

    public static string PathName => LogPath;

    public static void Reset()
    {
        try { System.IO.File.WriteAllText(LogPath, $"=== Ruler started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n"); }
        catch { }
    }

    public static void Log(string msg)
    {
        try { System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}\n"); }
        catch { }
    }
}
