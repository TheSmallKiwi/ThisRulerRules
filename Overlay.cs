using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Ruler;

/// <summary>
/// Transparent, top-most window that covers the monitor under the cursor and
/// captures the mouse while measurement mode is active. Measurements are stored
/// in absolute physical (virtual-screen) pixels so they stay correct regardless
/// of which monitor they were drawn on.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly ControlPanel _panel;
    private readonly OverlayCanvas _canvas = new();

    private bool _dragging;
    private bool _dialogOpen;
    private bool _measuring;   // true = interactive; false = passive line display
    private bool _frameDrag;   // Shift held at mouse-down: drawing the image frame
    private Point _startDip;
    private Native.MonitorSpec _spec;
    private Pixels? _pixels;   // lazily decoded snapshot for click detection
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private const double SnapTan = 0.12; // ~7° tolerance for mild H/V snapping

    public OverlayWindow(ControlPanel panel)
    {
        _panel = panel;
        Owner = panel;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // Alpha = 1 (essentially invisible) is the most reliable click-catcher for
        // a layered WPF window; a fully-transparent brush can vary by system.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Focusable = true;
        Content = _canvas;

        Deactivated += (_, _) =>
        {
            // Only while actively measuring — the passive display layer is never focused.
            if (_measuring && !_dialogOpen && IsVisible)
                _panel.Deactivate();
        };

        KeyDown += OnKey;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonDown += OnRightDown;

        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _canvas.Toast = ""; _canvas.InvalidateVisual(); };
    }

    /// <summary>Brief message under the hint bar.</summary>
    private void ShowToast(string text)
    {
        _canvas.Toast = text;
        _toastTimer.Stop();
        _toastTimer.Start();
        _canvas.InvalidateVisual();
    }

    /// <summary>Enter measurement mode covering the given monitor.</summary>
    internal void Begin(Native.MonitorSpec spec, BitmapSource shot, ObservableCollection<Measurement> lines)
    {
        _spec = spec;
        _measuring = true;
        _pixels = null;
        _canvas.Configure(shot, spec.Scale, spec.Left, spec.Top, lines, _panel);
        _canvas.ShowLoupe = true;
        _canvas.ShowChrome = true;
        _canvas.Dragging = false;
        _canvas.CursorPt = new Point((spec.CursorX - spec.Left) / spec.Scale,
                                     (spec.CursorY - spec.Top) / spec.Scale);

        Show();
        // Position in physical pixels — the reliable way to cover a specific
        // monitor under per-monitor DPI (WPF Left/Top DIPs don't map cleanly
        // across monitors of different scale).
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Native.SetClickThrough(hwnd, false); // interactive again
        Native.PlaceWindow(hwnd, spec.Left, spec.Top, spec.Width, spec.Height);

        Activate();
        Focus();
        Keyboard.Focus(this);
        _canvas.InvalidateVisual();

        // Runtime self-check: confirm Windows will actually route clicks to us.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            int cx = spec.Left + spec.Width / 2;
            int cy = spec.Top + spec.Height / 2;
            IntPtr at = Native.WindowAt(cx, cy);
            Diag.Log($"begin monitor=({spec.Left},{spec.Top},{spec.Width}x{spec.Height}) scale={spec.Scale:0.##} " +
                     $"hwnd={hwnd} centerCatchesClicks={(at == hwnd)} (winAtCenter={at})");
        }));
    }

    /// <summary>
    /// Leave interactive measuring but keep the drawn lines on screen as a
    /// passive, click-through layer (no loupe, no hint bar).
    /// </summary>
    public void ToDisplayMode()
    {
        _measuring = false;
        _dragging = false;
        _canvas.Dragging = false;
        _canvas.ShowLoupe = false;
        _canvas.ShowChrome = false;

        if (_spec.Width <= 0 || _spec.Height <= 0)
        {
            Hide();
            return;
        }

        if (!IsVisible)
            Show();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Native.SetClickThrough(hwnd, true);
        Native.PlaceWindow(hwnd, _spec.Left, _spec.Top, _spec.Width, _spec.Height);
        _canvas.InvalidateVisual();
    }

    /// <summary>Hide the overlay completely (app minimized, or lines cleared).</summary>
    public void HideOverlay()
    {
        _measuring = false;
        _dragging = false;
        _canvas.Dragging = false;
        Hide();
    }

    public void Redraw() => _canvas.InvalidateVisual();

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _dragging = false;
                _canvas.Dragging = false;
                _panel.Deactivate();
                e.Handled = true;
                break;

            case Key.Space:
            case Key.R:
                Refresh();
                e.Handled = true;
                break;

            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                if (_panel.CopyPng())
                    ShowToast("Copied PNG to the clipboard");
                e.Handled = true;
                break;

            case Key.Enter:
                if (_frameDrag)
                    break; // no snapping for the frame rectangle
                _panel.ToggleSnap();
                if (_dragging)
                    ApplyDragPoint(_canvas.CursorPt); // re-evaluate the line in flight
                _canvas.InvalidateVisual();
                e.Handled = true;
                break;
        }
    }

    private void Refresh()
    {
        _canvas.Visibility = Visibility.Hidden;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var shot = Native.CaptureRegion(_spec.Left, _spec.Top, _spec.Width, _spec.Height);
            _canvas.UpdateShot(shot);
            _pixels = null;
            _canvas.Visibility = Visibility.Visible;
            _canvas.InvalidateVisual();
        }));
    }

    // Right-click interrupts: cancel the line being dragged, or (if idle) leave
    // measurement mode.
    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            _canvas.Dragging = false;
            _canvas.Snap = "";
            ReleaseMouseCapture();
            _canvas.InvalidateVisual();
        }
        else
        {
            _panel.Deactivate();
        }
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _startDip = e.GetPosition(_canvas);
        _dragging = true;
        _frameDrag = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _canvas.FrameDrag = _frameDrag;
        _canvas.Dragging = true;
        _canvas.StartDip = _startDip;
        _canvas.CurDip = _startDip;
        _canvas.CursorPt = _startDip;
        _canvas.Snap = "";
        CaptureMouse();
        _canvas.InvalidateVisual();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        _canvas.CursorPt = p;
        if (_dragging)
            ApplyDragPoint(p);
        _canvas.InvalidateVisual();
    }

    /// <summary>Set the dragged endpoint, applying snapping only if it's enabled.</summary>
    private void ApplyDragPoint(Point p)
    {
        if (_panel.SnapEnabled && !_frameDrag)
        {
            _canvas.CurDip = SnapPoint(_startDip, p, out string snap);
            _canvas.Snap = snap;
        }
        else
        {
            _canvas.CurDip = p;
            _canvas.Snap = "";
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        _canvas.Dragging = false;
        ReleaseMouseCapture();

        Point end = _canvas.CurDip;
        bool click = (end - _startDip).Length < 3;

        if (_frameDrag)
        {
            _frameDrag = _canvas.FrameDrag = false;
            if (click)
                DetectFrame(_startDip);
            else
                SetFrame(new Rect(Phys(_startDip), Phys(end)));
            return;
        }

        if (click)
        {
            DetectScaleBar(_startDip);
            return;
        }

        Commit(Phys(_startDip), Phys(end), (end - _startDip).Length * _spec.Scale, calibrate: false);
    }

    /// <summary>Window-local DIP -> absolute physical pixel.</summary>
    private Point Phys(Point dip) => new(_spec.Left + dip.X * _spec.Scale, _spec.Top + dip.Y * _spec.Scale);

    private Pixels? SnapshotPixels()
    {
        if (_pixels == null && _canvas.Shot != null)
            _pixels = new Pixels(_canvas.Shot);
        return _pixels;
    }

    /// <summary>Plain click: measure the scale bar under the cursor and calibrate from it.</summary>
    private void DetectScaleBar(Point dip)
    {
        var px = SnapshotPixels();
        int x = (int)Math.Floor(dip.X * _spec.Scale), y = (int)Math.Floor(dip.Y * _spec.Scale);
        if (px == null || !Detect.ScaleBar(px, x, y, out Point a, out Point b))
        {
            ShowToast("No scale bar found there - click right on a solid bar, or drag to measure");
            return;
        }
        var origin = new Vector(_spec.Left, _spec.Top);
        Commit(a + origin, b + origin, (b - a).Length, calibrate: true);
    }

    /// <summary>Shift+click: find the edges of the image under the cursor.</summary>
    private void DetectFrame(Point dip)
    {
        var px = SnapshotPixels();
        int x = (int)Math.Floor(dip.X * _spec.Scale), y = (int)Math.Floor(dip.Y * _spec.Scale);
        if (px == null || !Detect.ImageFrame(px, x, y, out Int32Rect r))
        {
            ShowToast("Couldn't find the image edges - Shift+drag to draw the frame instead");
            return;
        }
        SetFrame(new Rect(_spec.Left + r.X, _spec.Top + r.Y, r.Width, r.Height));
    }

    private void SetFrame(Rect phys)
    {
        if (phys.Width < 8 || phys.Height < 8)
        {
            _canvas.InvalidateVisual();
            return;
        }
        ShowToast(_panel.SetFrame(phys));
    }

    /// <summary>Hand a finished line to the panel (which may show the scale dialog), then refocus.</summary>
    private void Commit(Point sPhys, Point ePhys, double px, bool calibrate)
    {
        _dialogOpen = true;
        _canvas.ShowLoupe = false;
        _canvas.InvalidateVisual();

        _panel.HandleMeasurement(sPhys, ePhys, px, this, calibrate);

        _dialogOpen = false;
        if (IsVisible)
        {
            _canvas.ShowLoupe = true;
            Activate();
            Focus();
            Keyboard.Focus(this);
            var c = Native.CursorPos();
            _canvas.CursorPt = new Point((c.X - _spec.Left) / _spec.Scale, (c.Y - _spec.Top) / _spec.Scale);
            _canvas.InvalidateVisual();
        }
    }

    private static Point SnapPoint(Point a, Point b, out string snap)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double adx = Math.Abs(dx), ady = Math.Abs(dy);
        if (ady <= adx * SnapTan) { snap = "H"; return new Point(b.X, a.Y); }
        if (adx <= ady * SnapTan) { snap = "V"; return new Point(a.X, b.Y); }
        snap = "";
        return b;
    }
}

/// <summary>
/// Immediate-mode visual layer. Committed measurements arrive in absolute
/// physical coordinates and are converted to window-local DIPs for drawing;
/// the in-progress line is tracked directly in local DIPs.
/// </summary>
public sealed class OverlayCanvas : FrameworkElement
{
    private BitmapSource? _shot;
    private int _shotW, _shotH;
    private double _scale = 1.0;
    private double _originX, _originY; // monitor top-left in physical px
    private ObservableCollection<Measurement>? _lines;
    private ControlPanel? _panel;
    private readonly Typeface _face = new("Segoe UI");
    private double _ppd = 1.0;

    public bool Dragging;
    public bool FrameDrag;
    public bool ShowLoupe;
    public bool ShowChrome = true; // hint bar; off in passive display mode
    public Point StartDip;
    public Point CurDip;
    public Point CursorPt; // window-local DIP
    public string Snap = "";
    public string Toast = "";

    public BitmapSource? Shot => _shot;

    private const double LoupeHalfExtent = 14;
    private const double LoupeDiameter = 190;

    private static readonly Brush HintBg = new SolidColorBrush(Color.FromArgb(0xB0, 0x14, 0x14, 0x16));
    private static readonly Brush FrameBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0xC8, 0xFF));
    private static readonly Brush FrameTagBg = new SolidColorBrush(Color.FromArgb(0xC0, 0x0B, 0x50, 0x6A));

    public OverlayCanvas()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        HintBg.Freeze();
        FrameBrush.Freeze();
        FrameTagBg.Freeze();
    }

    public void Configure(BitmapSource shot, double scale, double originX, double originY,
        ObservableCollection<Measurement> lines, ControlPanel panel)
    {
        _shot = shot;
        _shotW = shot.PixelWidth;
        _shotH = shot.PixelHeight;
        _scale = scale;
        _originX = originX;
        _originY = originY;
        _lines = lines;
        _panel = panel;
    }

    public void UpdateShot(BitmapSource shot)
    {
        _shot = shot;
        _shotW = shot.PixelWidth;
        _shotH = shot.PixelHeight;
    }

    /// <summary>Absolute physical point -> window-local DIP.</summary>
    private Point LocalOf(Point physical)
        => new((physical.X - _originX) / _scale, (physical.Y - _originY) / _scale);

    protected override void OnRender(DrawingContext dc)
    {
        _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Full-bounds transparent fill so the whole overlay receives mouse input.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (ShowChrome)
        {
            DrawHint(dc);
            if (_panel?.Frame is { } frame)
                DrawFrame(dc, new Rect(LocalOf(frame.Screen.TopLeft), LocalOf(frame.Screen.BottomRight)),
                    $"Image  {frame.NativeW} × {frame.NativeH} px" + (frame.FromClipboard ? "" : "  (screen size)"));
        }

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var specs = new List<LineSpec>();
        if (_lines != null)
            foreach (var m in _lines)
            {
                Point a = LocalOf(m.Start), b = LocalOf(m.End);
                if (bounds.IntersectsWith(new Rect(a, b))) // skip lines on other monitors
                    specs.Add(new LineSpec(a, b, m.Index, m.OverlayLabel, false));
            }
        if (Dragging && !FrameDrag)
            specs.Add(new LineSpec(StartDip, CurDip, (_lines?.Count ?? 0) + 1, LiveLabel(), true));
        MeasureRender.DrawAll(dc, specs, _ppd, _face, bounds);

        if (Dragging && FrameDrag)
        {
            var r = new Rect(StartDip, CurDip);
            DrawFrame(dc, r, $"{r.Width * _scale:0} × {r.Height * _scale:0} px");
        }

        if (ShowLoupe && _shot != null)
            DrawLoupe(dc);
    }

    private void DrawHint(DrawingContext dc)
    {
        string snap = _panel?.SnapEnabled == false ? "Snap OFF" : "Snap ON";
        var ft = Fmt($"Drag: measure   •   Click bar: set scale   •   Shift+click/drag: image frame   •   Ctrl+C: copy PNG   •   Enter: {snap}   •   Esc exit",
            12.5, Brushes.White);
        double pad = 10;
        double w = ft.Width + pad * 2;
        double h = ft.Height + pad;
        double x = (ActualWidth - w) / 2;
        double y = 12;
        dc.DrawRoundedRectangle(HintBg, null, new Rect(x, y, w, h), 8, 8);
        dc.DrawText(ft, new Point(x + pad, y + h / 2 - ft.Height / 2));

        if (Toast.Length > 0)
        {
            var tt = Fmt(Toast, 12.5, Brushes.White);
            double tw = tt.Width + pad * 2;
            var tr = new Rect((ActualWidth - tw) / 2, y + h + 6, tw, tt.Height + pad);
            dc.DrawRoundedRectangle(HintBg, null, tr, 8, 8);
            dc.DrawText(tt, new Point(tr.X + pad, tr.Y + pad / 2));
        }
    }

    private void DrawFrame(DrawingContext dc, Rect r, string tag)
    {
        var pen = new Pen(FrameBrush, 1.5) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        dc.DrawRectangle(null, pen, r);
        var ft = Fmt(tag, 11, Brushes.White);
        var tr = new Rect(r.X + 1, r.Y + 1, ft.Width + 10, ft.Height + 4);
        dc.DrawRectangle(FrameTagBg, null, tr);
        dc.DrawText(ft, new Point(tr.X + 5, tr.Y + 2));
    }

    private void DrawLoupe(DrawingContext dc)
    {
        if (_shot == null)
            return;

        int rPhys = Math.Max(2, (int)Math.Round(LoupeHalfExtent * _scale));
        if (_shotW < 2 * rPhys + 1 || _shotH < 2 * rPhys + 1)
            return;

        int cx = (int)Math.Round(CursorPt.X * _scale);
        int cy = (int)Math.Round(CursorPt.Y * _scale);
        int sx = Math.Clamp(cx - rPhys, 0, _shotW - 2 * rPhys);
        int sy = Math.Clamp(cy - rPhys, 0, _shotH - 2 * rPhys);

        CroppedBitmap crop;
        try { crop = new CroppedBitmap(_shot, new Int32Rect(sx, sy, 2 * rPhys, 2 * rPhys)); }
        catch { return; }

        double d = LoupeDiameter;
        double gap = 26;
        double lx = CursorPt.X + gap;
        double ly = CursorPt.Y + gap;
        if (lx + d > ActualWidth) lx = CursorPt.X - gap - d;
        if (ly + d > ActualHeight) ly = CursorPt.Y - gap - d;
        if (lx < 0) lx = Math.Max(4, Math.Min(CursorPt.X + gap, ActualWidth - d));
        if (ly < 0) ly = Math.Max(4, Math.Min(CursorPt.Y + gap, ActualHeight - d));

        var rect = new Rect(lx, ly, d, d);
        var center = new Point(lx + d / 2, ly + d / 2);
        double k = d / (2.0 * rPhys);

        Point ToLoupe(Point localDip) => new(rect.X + (localDip.X * _scale - sx) * k, rect.Y + (localDip.Y * _scale - sy) * k);

        dc.PushClip(new EllipseGeometry(center, d / 2, d / 2));
        dc.DrawImage(crop, rect);

        var lp = new Pen(MeasureRender.LineBrush, 1.6);
        if (_lines != null)
            foreach (var m in _lines)
                dc.DrawLine(lp, ToLoupe(LocalOf(m.Start)), ToLoupe(LocalOf(m.End)));
        if (Dragging)
            dc.DrawLine(new Pen(MeasureRender.LiveBrush, 1.6), ToLoupe(StartDip), ToLoupe(CurDip));

        dc.Pop();

        dc.DrawEllipse(null, new Pen(Brushes.White, 3), center, d / 2, d / 2);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), 1), center, d / 2 + 1.5, d / 2 + 1.5);

        Point ch = ToLoupe(CursorPt);
        var chPen = new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0x2C, 0xC8, 0xFF)), 1);
        dc.DrawLine(chPen, new Point(ch.X - 9, ch.Y), new Point(ch.X + 9, ch.Y));
        dc.DrawLine(chPen, new Point(ch.X, ch.Y - 9), new Point(ch.X, ch.Y + 9));

        var ft = Fmt($"{d / (2 * LoupeHalfExtent):0.#}×", 11, Brushes.White);
        double bw = ft.Width + 10;
        var br = new Rect(center.X - bw / 2, rect.Bottom + 4, bw, ft.Height + 4);
        if (br.Bottom > ActualHeight) br = new Rect(center.X - bw / 2, rect.Top - ft.Height - 8, bw, ft.Height + 4);
        dc.DrawRoundedRectangle(MeasureRender.LabelBg, null, br, 4, 4);
        dc.DrawText(ft, new Point(br.X + 5, br.Y + 2));
    }

    private string LiveLabel()
    {
        double px = (CurDip - StartDip).Length * _scale;
        string s = $"{px:0} px";
        var cal = _panel?.Cal;
        if (cal != null)
            s += $"  =  {px * cal.UnitsPerPixel:0.##} {cal.Units}";
        if (Snap == "H") s += "   ─ H";
        else if (Snap == "V") s += "   │ V";
        return s;
    }

    private FormattedText Fmt(string text, double size, Brush brush)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _face, size, brush, _ppd);
}
