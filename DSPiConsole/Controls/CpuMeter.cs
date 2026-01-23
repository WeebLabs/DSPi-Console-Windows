using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// Small CPU load meter display.
/// </summary>
public sealed class CpuMeter : UserControl
{
    private readonly TextBlock _labelText;
    private readonly Border _meterBackground;
    private readonly Border _meterForeground;
    private readonly TextBlock _valueText;

    public static readonly DependencyProperty CoreProperty =
        DependencyProperty.Register(nameof(Core), typeof(int), typeof(CpuMeter),
            new PropertyMetadata(0, OnCoreChanged));

    public static readonly DependencyProperty LoadProperty =
        DependencyProperty.Register(nameof(Load), typeof(int), typeof(CpuMeter),
            new PropertyMetadata(0, OnLoadChanged));

    public int Core
    {
        get => (int)GetValue(CoreProperty);
        set => SetValue(CoreProperty, value);
    }

    public int Load
    {
        get => (int)GetValue(LoadProperty);
        set => SetValue(LoadProperty, value);
    }

    public CpuMeter()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4
        };

        _labelText = new TextBlock
        {
            Text = "C0:",
            FontSize = 10,
            Foreground = new SolidColorBrush(Colors.Gray),
            VerticalAlignment = VerticalAlignment.Center
        };

        var meterGrid = new Grid
        {
            Width = 40,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        _meterBackground = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(76, 128, 128, 128)),
            CornerRadius = new CornerRadius(2)
        };

        _meterForeground = new Border
        {
            Background = new SolidColorBrush(Colors.DodgerBlue),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };

        meterGrid.Children.Add(_meterBackground);
        meterGrid.Children.Add(_meterForeground);

        _valueText = new TextBlock
        {
            Text = "0%",
            FontSize = 10,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 28
        };

        panel.Children.Add(_labelText);
        panel.Children.Add(meterGrid);
        panel.Children.Add(_valueText);

        Content = panel;
    }

    private static void OnCoreChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CpuMeter meter)
        {
            meter._labelText.Text = $"C{e.NewValue}:";
        }
    }

    private static void OnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CpuMeter meter)
        {
            int load = (int)e.NewValue;
            meter._valueText.Text = $"{load}%";
            meter._meterForeground.Width = 40 * (load / 100.0);
            meter._meterForeground.Background = new SolidColorBrush(
                load > 90 ? Colors.Red : Colors.DodgerBlue);
        }
    }
}
