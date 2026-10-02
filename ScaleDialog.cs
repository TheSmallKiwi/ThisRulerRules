using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ruler;

/// <summary>
/// Modal dialog shown after the first measurement (or via "Set scale…").
/// Asks what real-world length the drawn span represents, plus the unit.
/// </summary>
public sealed class ScaleDialog : Window
{
    private readonly TextBox _value = new() { Width = 96, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _unit = new() { Width = 96, IsEditable = true };

    public double Value { get; private set; }
    public string Units { get; private set; } = "";

    public ScaleDialog(double pixels, double? lastValue = null, string? lastUnits = null)
    {
        Title = "Set scale";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));

        var root = new StackPanel { Margin = new Thickness(18) };

        root.Children.Add(new TextBlock
        {
            Text = $"Measured span:  {pixels:0.0} px",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 12),
        });

        root.Children.Add(new TextBlock
        {
            Text = "This span represents:",
            Margin = new Thickness(0, 0, 0, 6),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _unit.ItemsSource = new[] { "px", "mm", "cm", "m", "in", "ft", "µm", "°" };
        _unit.Text = lastUnits ?? "cm";
        if (lastValue is { } v)
            _value.Text = v.ToString(CultureInfo.CurrentCulture);
        row.Children.Add(_value);
        row.Children.Add(new TextBlock { Width = 8 });
        row.Children.Add(_unit);
        root.Children.Add(row);

        root.Children.Add(new TextBlock
        {
            Text = "e.g. 100 cm  →  the line you drew equals 100 cm.\nAll later measurements use this scale.",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 14),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += OnOk;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => { _value.Focus(); _value.SelectAll(); };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(_value.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) &&
            v > 0 &&
            !string.IsNullOrWhiteSpace(_unit.Text))
        {
            Value = v;
            Units = _unit.Text.Trim();
            DialogResult = true;
        }
        else
        {
            MessageBox.Show(this, "Enter a positive number and a unit (e.g. 100 and cm).",
                "Ruler", MessageBoxButton.OK, MessageBoxImage.Warning);
            _value.Focus();
            _value.SelectAll();
        }
    }
}
