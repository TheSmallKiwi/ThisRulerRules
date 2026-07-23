using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Ruler;

/// <summary>
/// Dev-only render + input check:  Ruler.exe --selftest [outputDir]
///  - renders the panel and overlay to PNGs
///  - spins up the REAL transparent overlay and asks Windows (WindowFromPoint)
///    whether clicks land on it — an OS-level test of hit testing, not a
///    disconnected-visual approximation.
/// </summary>
internal static class SelfTest
{
    public static void Run(string[] args)
    {
        string outDir = args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault()
                        ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            var panel = new ControlPanel { Left = -8000, Top = -8000 };
            panel.Show();
            panel.SelfTestSeed();

            panel.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                try
                {
                    RenderPanel(panel, Path.Combine(outDir, "panel.png"));
                    RenderOverlay(panel, outDir);
                    MeasureRender.ExportPng(panel.Items, Path.Combine(outDir, "lines.png"));
                    VerifyInput(panel, outDir, app); // async; shuts the app down when done
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(outDir, "selftest.err"), ex.ToString());
                    app.Shutdown();
                }
            }));
        };
        app.Run();
    }

    /// <summary>
    /// Empirically verify the measure-mode translucency: put the panel over a
    /// known black backdrop, capture its screen pixels, apply the layered alpha,
    /// capture again, and compare. Pixels must actually change.
    /// </summary>
    public static void RunOpacity(string[] args)
    {
        string outDir = args.SkipWhile(a => a != "--opacitytest").Skip(1).FirstOrDefault()
                        ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            // Known dark backdrop so a 50% blend is unmistakable.
            var backdrop = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = Brushes.Black,
                Left = 100, Top = 100, Width = 760, Height = 760,
            };
            backdrop.Show();

            // Manual placement, otherwise CenterScreen moves it off the backdrop.
            var panel = new ControlPanel
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 150,
                Top = 150,
            };
            panel.Show();
            panel.SelfTestSeed();

            var sb = new StringBuilder();
            Settle(700, () =>
            {
                IntPtr h = new WindowInteropHelper(panel).Handle;
                var r = Native.WindowRect(h);
                int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                var before = Native.CaptureRegion(r.Left, r.Top, w, ht);
                double mb = MeanLuma(before);
                sb.AppendLine($"panel rect=({r.Left},{r.Top}) {w}x{ht}, over a black backdrop");
                sb.AppendLine($"opaque       meanLuma={mb:0.00}");

                panel.Opacity = 0.5;  // exactly what entering measure mode does

                Settle(700, () =>
                {
                    var half = Native.CaptureRegion(r.Left, r.Top, w, ht);
                    double mh = MeanLuma(half);
                    sb.AppendLine($"Opacity=0.5  meanLuma={mh:0.00}  delta={Math.Abs(mh - mb):0.00}");
                    bool dimmed = Math.Abs(mh - mb) > 5.0;

                    panel.Opacity = 1.0;  // and what leaving it does

                    Settle(700, () =>
                    {
                        var back = Native.CaptureRegion(r.Left, r.Top, w, ht);
                        double mr = MeanLuma(back);
                        sb.AppendLine($"restored     meanLuma={mr:0.00}  driftFromOpaque={Math.Abs(mr - mb):0.00}");
                        bool restored = Math.Abs(mr - mb) < 5.0;
                        sb.AppendLine();
                        sb.AppendLine(dimmed && restored
                            ? "RESULT: PASS — panel dims to 50% while measuring and restores after."
                            : $"RESULT: FAIL — dimmed={dimmed} restored={restored}");
                        try
                        {
                            Encode(before, Path.Combine(outDir, "op_opaque.png"));
                            Encode(half, Path.Combine(outDir, "op_half.png"));
                        }
                        catch { }
                        File.WriteAllText(Path.Combine(outDir, "opacity_check.txt"), sb.ToString());
                        backdrop.Close();
                        app.Shutdown();
                    });
                });
            });
        };
        app.Run();
    }

    private static void Settle(int ms, Action then)
    {
        var t = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ms),
        };
        t.Tick += (_, _) => { t.Stop(); then(); };
        t.Start();
    }

    private static double MeanLuma(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        int stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);
        double sum = 0;
        for (int i = 0; i + 2 < px.Length; i += 4)
            sum += 0.299 * px[i + 2] + 0.587 * px[i + 1] + 0.114 * px[i];
        return sum / (px.Length / 4);
    }

    private static void RenderPanel(ControlPanel panel, string path)
    {
        panel.UpdateLayout();
        double w = panel.ActualWidth > 0 ? panel.ActualWidth : panel.Width;
        double h = panel.ActualHeight > 0 ? panel.ActualHeight : panel.Height;
        Save(panel, w, h, path);
    }

    private static void RenderOverlay(ControlPanel panel, string outDir)
    {
        var (pw, ph) = Native.PrimaryPhysical();
        double dipW = SystemParameters.PrimaryScreenWidth;
        double dipH = SystemParameters.PrimaryScreenHeight;
        double scale = pw / dipW;
        var shot = Native.CaptureRegion(0, 0, pw, ph);

        var canvas = new OverlayCanvas();
        canvas.Configure(shot, scale, 0, 0, panel.Items, panel);
        canvas.ShowLoupe = true;
        canvas.Dragging = true;
        canvas.StartDip = new Point(0.42 * dipW, 0.30 * dipH);
        canvas.CurDip = new Point(0.42 * dipW, 0.55 * dipH);
        canvas.CursorPt = canvas.CurDip;
        canvas.Snap = "V";

        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x36, 0x3C)),
            Child = canvas,
            Width = dipW,
            Height = dipH,
        };
        host.Measure(new Size(dipW, dipH));
        host.Arrange(new Rect(0, 0, dipW, dipH));
        host.UpdateLayout();

        var full = Save(host, dipW, dipH, Path.Combine(outDir, "overlay.png"));

        double cx = canvas.CurDip.X, cy = canvas.CurDip.Y;
        int zx = (int)Math.Clamp(cx - 60, 0, dipW - 10);
        int zy = (int)Math.Clamp(cy - 60, 0, dipH - 10);
        int zw = (int)Math.Min(560, dipW - zx);
        int zh = (int)Math.Min(520, dipH - zy);
        try
        {
            var crop = new CroppedBitmap(full, new Int32Rect(zx, zy, zw, zh));
            Encode(crop, Path.Combine(outDir, "overlay_zoom.png"));
        }
        catch { /* best effort */ }
    }

    private static void VerifyInput(ControlPanel panel, string outDir, Application app)
    {
        var monitors = Native.AllMonitors();
        var sb = new StringBuilder();
        sb.AppendLine($"Monitors found: {monitors.Count}");
        sb.AppendLine();
        TestMonitor(monitors, 0, panel, outDir, app, sb, true);
    }

    // Each monitor gets the real overlay placed on it, then we ask the OS
    // (WindowFromPoint) whether a click at its centre lands on our window.
    private static void TestMonitor(System.Collections.Generic.List<Native.MonitorSpec> monitors,
        int i, ControlPanel panel, string outDir, Application app, StringBuilder sb, bool allPass)
    {
        if (i >= monitors.Count)
        {
            sb.AppendLine();
            sb.AppendLine(allPass
                ? "RESULT: PASS — overlay captures mouse input on every monitor."
                : "RESULT: FAIL — at least one monitor passes clicks through.");
            File.WriteAllText(Path.Combine(outDir, "input_check.txt"), sb.ToString());
            File.WriteAllText(Path.Combine(outDir, allPass ? "selftest.ok" : "selftest.err"), sb.ToString());
            app.Shutdown();
            return;
        }

        var spec = monitors[i];
        var shot = Native.CaptureRegion(spec.Left, spec.Top, spec.Width, spec.Height);
        var overlay = new OverlayWindow(panel);
        overlay.Begin(spec, shot, panel.Items);
        IntPtr our = new WindowInteropHelper(overlay).Handle;

        overlay.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            int cx = spec.Left + spec.Width / 2;
            int cy = spec.Top + spec.Height / 2;
            IntPtr at = Native.WindowAt(cx, cy);
            bool ours = at == our;
            sb.AppendLine($"monitor #{i}  ({spec.Left},{spec.Top})  {spec.Width}x{spec.Height}  scale={spec.Scale:0.##}");
            sb.AppendLine($"   measuring : {(ours ? "OUR WINDOW (catches clicks)" : $"other hwnd {at} (passes through!)")}");

            // Passive display mode must do the opposite: let clicks through.
            overlay.ToDisplayMode();
            app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                IntPtr at2 = Native.WindowAt(cx, cy);
                bool through = at2 != our;
                sb.AppendLine($"   display   : {(through ? "clicks PASS THROUGH (ok), lines stay visible" : "STILL BLOCKING (bad)")}");
                overlay.Close();
                app.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => TestMonitor(monitors, i + 1, panel, outDir, app, sb, allPass && ours && through)));
            }));
        }));
    }

    private static RenderTargetBitmap Save(Visual visual, double w, double h, string path)
    {
        int pw = Math.Max(1, (int)Math.Ceiling(w));
        int ph = Math.Max(1, (int)Math.Ceiling(h));
        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        Encode(rtb, path);
        return rtb;
    }

    private static void Encode(BitmapSource src, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
