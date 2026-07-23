using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ruler;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Contains("--selftest"))
        {
            SelfTest.Run(args);
            return;
        }
        if (args.Contains("--opacitytest"))
        {
            SelfTest.RunOpacity(args);
            return;
        }

        Diag.Reset();

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.ToString(), "Ruler — unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        var panel = new ControlPanel();
        app.MainWindow = panel;
        panel.Show();
        app.Run();
    }
}

/// <summary>Pixels &lt;-&gt; real-world units. UnitsPerPixel = value / pixelLength.</summary>
public sealed class Calibration
{
    public string Units { get; }
    public double UnitsPerPixel { get; }

    public Calibration(string units, double unitsPerPixel)
    {
        Units = units;
        UnitsPerPixel = unitsPerPixel;
    }
}

/// <summary>A single numbered measurement line.</summary>
public sealed class Measurement : INotifyPropertyChanged
{
    public Point Start;
    public Point End;

    private int _index;
    private double _pixels;
    private double _scaled;
    private string _units = "";
    private bool _calibrated;

    public int Index
    {
        get => _index;
        set { _index = value; On(nameof(Index)); }
    }

    public double Pixels
    {
        get => _pixels;
        set { _pixels = value; On(nameof(Pixels)); On(nameof(PixelsText)); }
    }

    public double Scaled
    {
        get => _scaled;
        set { _scaled = value; On(nameof(Scaled)); On(nameof(ScaledText)); }
    }

    public string Units
    {
        get => _units;
        set { _units = value; On(nameof(Units)); On(nameof(ScaledText)); }
    }

    public bool Calibrated
    {
        get => _calibrated;
        set { _calibrated = value; On(nameof(Calibrated)); On(nameof(ScaledText)); }
    }

    public string PixelsText => $"{Pixels:0.0}";
    public string ScaledText => Calibrated ? $"{Scaled:0.###} {Units}" : "—";

    /// <summary>Label drawn next to the line on the overlay and in the PNG export.</summary>
    public string OverlayLabel => Calibrated
        ? $"#{Index}  {Pixels:0} px  =  {Scaled:0.##} {Units}"
        : $"#{Index}  {Pixels:0} px";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Main window: shows the numbered results, owns the calibration, and drives
/// the measurement overlay.
/// </summary>
public sealed class ControlPanel : Window
{
    public ObservableCollection<Measurement> Items { get; } = new();
    public Calibration? Cal { get; private set; }

    private OverlayWindow? _overlay;
    private bool _active;

    private readonly DataGrid _grid;
    private readonly TextBlock _status;
    private readonly Button _snapButton;

    /// <summary>Mild horizontal/vertical snapping while dragging (toggle with Enter).</summary>
    public bool SnapEnabled { get; private set; } = true;

    public ControlPanel()
    {
        Title = "This Ruler Rules";
        Width = 430;
        Height = 460;
        MinWidth = 380;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF8));

        var outer = new DockPanel();

        var menu = new Menu();
        DockPanel.SetDock(menu, Dock.Top);
        var fileMenu = new MenuItem { Header = "_File" };
        fileMenu.Items.Add(MakeMenuItem("Set scale…", SetScale));
        fileMenu.Items.Add(new Separator());
        fileMenu.Items.Add(MakeMenuItem("Save PNG…", SavePng));
        fileMenu.Items.Add(MakeMenuItem("Save CSV…", SaveCsv));
        fileMenu.Items.Add(new Separator());
        fileMenu.Items.Add(MakeMenuItem("Exit", Close));
        menu.Items.Add(fileMenu);
        outer.Children.Add(menu);

        var root = new DockPanel { Margin = new Thickness(12) };

        var help = new TextBlock
        {
            Text = "Press Space to start measuring. Drag to draw ruler lines. Esc exits measurement mode.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(help, Dock.Top);
        root.Children.Add(help);

        // Bottom: status + buttons.
        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);

        _status = new TextBlock
        {
            Text = "Scale: not set — the first measurement will calibrate it.",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        bottom.Children.Add(_status);

        _snapButton = MakeButton("Snap: On", (_, _) => ToggleSnap());
        _snapButton.Width = 98; // fixed so the row doesn't jitter on On/Off

        var buttons = new WrapPanel();
        buttons.Children.Add(MakeButton("Measure", (_, _) => Activate_()));
        buttons.Children.Add(_snapButton);
        buttons.Children.Add(MakeButton("Delete", (_, _) => DeleteSelected()));
        buttons.Children.Add(MakeButton("Clear all", (_, _) => ClearAll()));
        bottom.Children.Add(buttons);
        root.Children.Add(bottom);

        // Center: results grid.
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            RowBackground = Brushes.White,
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF3, 0xF8)),
            ItemsSource = Items,
        };
        _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(Measurement.Index)), Width = 44 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Pixels", Binding = new Binding(nameof(Measurement.PixelsText)), Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Measurement",
            Binding = new Binding(nameof(Measurement.ScaledText)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        });
        root.Children.Add(_grid);

        outer.Children.Add(root);
        Content = outer;

        // Space activates measurement mode from anywhere in the window.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && !_active)
            {
                Activate_();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                // Preempt the focused button / DataGrid so Enter always toggles snap.
                ToggleSnap();
                e.Handled = true;
            }
        };

        // Lines stay on screen until cleared or the app is minimized.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                _overlay?.HideOverlay();
            else if (!_active && Items.Count > 0)
                _overlay?.ToDisplayMode();
        };

        Closed += (_, _) => _overlay?.Close();
    }

    private static MenuItem MakeMenuItem(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static Button MakeButton(string text, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 80,
        };
        b.Click += onClick;
        return b;
    }

    // ---- measurement mode ------------------------------------------------

    private void Activate_()
    {
        if (_active)
            return;

        _active = true;

        if (_overlay is { IsVisible: true })
        {
            // Hide the persistent line layer first, otherwise those lines get
            // baked into the magnifier snapshot. Capture on the next tick so the
            // compositor has actually painted the hidden state.
            _overlay.HideOverlay();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartMeasuring));
        }
        else
        {
            StartMeasuring();
        }
    }

    private void StartMeasuring()
    {
        // Cover whichever monitor the cursor is on (handles multi-monitor and
        // per-monitor DPI). Capture that monitor for the magnifier.
        var spec = Native.MonitorUnderCursor();
        var shot = Native.CaptureRegion(spec.Left, spec.Top, spec.Width, spec.Height);
        _overlay ??= new OverlayWindow(this);
        _overlay.Begin(spec, shot, Items);
        SetPanelTranslucent(true);
    }

    /// <summary>
    /// Half-transparent while measuring so the panel stays out of the way.
    /// WPF's Window.Opacity is the mechanism that actually works here — adding
    /// WS_EX_LAYERED by hand is silently refused on a WPF-managed window.
    /// </summary>
    private void SetPanelTranslucent(bool on) => Opacity = on ? 0.5 : 1.0;

    /// <summary>Flip H/V snapping. Reflected on the button and in the overlay hint.</summary>
    public void ToggleSnap()
    {
        SnapEnabled = !SnapEnabled;
        _snapButton.Content = SnapEnabled ? "Snap: On" : "Snap: Off";
        _overlay?.Redraw();
    }

    /// <summary>Called by the overlay on Esc / focus loss.</summary>
    public void Deactivate()
    {
        if (!_active)
            return;

        _active = false;
        SetPanelTranslucent(false);
        // Keep the drawn lines on screen as a passive click-through layer.
        if (Items.Count > 0)
            _overlay?.ToDisplayMode();
        else
            _overlay?.HideOverlay();
        Activate();  // bring the panel back to the foreground
        Focus();
    }

    /// <summary>
    /// Called by the overlay when a drag finishes. Calibrates on the first
    /// measurement, then records the numbered result.
    /// </summary>
    public void HandleMeasurement(Point start, Point end, double pixels, Window dialogOwner)
    {
        if (Cal == null)
        {
            if (!ShowScaleDialog(pixels, dialogOwner))
                return; // user cancelled calibration — discard this line
        }

        var m = new Measurement
        {
            Index = Items.Count + 1,
            Start = start,
            End = end,
            Pixels = pixels,
            Units = Cal!.Units,
            Scaled = pixels * Cal.UnitsPerPixel,
            Calibrated = true,
        };
        Items.Add(m);
        _grid.SelectedItem = m;
        _grid.ScrollIntoView(m);
        UpdateStatus();
        _overlay?.Redraw();
    }

    private void SetScale()
    {
        double? pixels = (_grid.SelectedItem as Measurement)?.Pixels
                         ?? (Items.Count > 0 ? Items[^1].Pixels : null);
        if (pixels == null)
        {
            MessageBox.Show(this, "Measure something first, then set its scale.",
                "Ruler", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShowScaleDialog(pixels.Value, this);
    }

    private bool ShowScaleDialog(double pixels, Window owner)
    {
        var dlg = new ScaleDialog(pixels) { Owner = owner };
        if (dlg.ShowDialog() == true)
        {
            Cal = new Calibration(dlg.Units, dlg.Value / pixels);
            RecomputeAll();
            UpdateStatus();
            _overlay?.Redraw();
            return true;
        }
        return false;
    }

    private void RecomputeAll()
    {
        if (Cal == null)
            return;
        foreach (var m in Items)
        {
            m.Units = Cal.Units;
            m.Scaled = m.Pixels * Cal.UnitsPerPixel;
            m.Calibrated = true;
        }
    }

    private void DeleteSelected()
    {
        if (_grid.SelectedItem is Measurement m)
        {
            Items.Remove(m);
            for (int i = 0; i < Items.Count; i++)
                Items[i].Index = i + 1;
            UpdateStatus();
            if (Items.Count == 0 && !_active)
                _overlay?.HideOverlay();
            else
                _overlay?.Redraw();
        }
    }

    private void ClearAll()
    {
        if (Items.Count == 0)
            return;
        if (MessageBox.Show(this, "Clear all measurements and the current scale?",
                "Ruler", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        Items.Clear();
        Cal = null;
        UpdateStatus();
        if (_active)
            _overlay?.Redraw();
        else
            _overlay?.HideOverlay();
    }

    private void SavePng()
    {
        if (Items.Count == 0)
        {
            MessageBox.Show(this, "Draw some measurements first.", "Ruler",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = "measurements.png",
        };
        if (dlg.ShowDialog(this) != true)
            return;
        try
        {
            MeasureRender.ExportPng(Items, dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save PNG:\n" + ex.Message, "Ruler",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveCsv()
    {
        if (Items.Count == 0)
        {
            MessageBox.Show(this, "Nothing to save yet.", "Ruler",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = "measurements.csv",
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("Index,Pixels,Scaled,Units");
        foreach (var m in Items)
        {
            string scaled = m.Calibrated ? m.Scaled.ToString("0.###", CultureInfo.InvariantCulture) : "";
            string units = m.Calibrated ? m.Units : "";
            sb.AppendLine($"{m.Index},{m.Pixels.ToString("0.###", CultureInfo.InvariantCulture)},{scaled},{units}");
        }
        File.WriteAllText(dlg.FileName, sb.ToString());
    }

    /// <summary>Populate sample data for --selftest rendering.</summary>
    internal void SelfTestSeed()
    {
        double dipW = SystemParameters.PrimaryScreenWidth;
        double dipH = SystemParameters.PrimaryScreenHeight;
        var (pw, _) = Native.PrimaryPhysical();
        double scale = pw / dipW;
        Cal = new Calibration("cm", 0.05);

        // Store in absolute physical coords (origin 0,0 for the primary here).
        void Add(double x1, double y1, double x2, double y2)
        {
            var s = new Point(x1 * scale, y1 * scale);
            var e = new Point(x2 * scale, y2 * scale);
            double px = (e - s).Length;
            Items.Add(new Measurement
            {
                Index = Items.Count + 1,
                Start = s,
                End = e,
                Pixels = px,
                Units = "cm",
                Scaled = px * Cal.UnitsPerPixel,
                Calibrated = true,
            });
        }

        Add(0.20 * dipW, 0.30 * dipH, 0.38 * dipW, 0.30 * dipH); // horizontal
        Add(0.20 * dipW, 0.42 * dipH, 0.20 * dipW, 0.66 * dipH); // vertical
        Add(0.55 * dipW, 0.35 * dipH, 0.72 * dipW, 0.52 * dipH); // diagonal
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (Cal == null)
        {
            _status.Text = "Scale: not set — the first measurement will calibrate it.";
            return;
        }
        double perPx = Cal.UnitsPerPixel;
        double pxPerUnit = perPx > 0 ? 1 / perPx : 0;
        _status.Text =
            $"Scale: 1 px = {perPx:0.####} {Cal.Units}   ({pxPerUnit:0.##} px = 1 {Cal.Units})"
            + $"     •     {Items.Count} measurement(s)";
    }
}
