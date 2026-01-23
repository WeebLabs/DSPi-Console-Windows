using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// Horizontal meter bar for displaying audio levels.
/// </summary>
public sealed class HorizontalMeterBar : UserControl
{
    private readonly Border _background;
    private readonly Border _foreground;

    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(double), typeof(HorizontalMeterBar),
            new PropertyMetadata(0.0, OnLevelChanged));

    public static readonly DependencyProperty MeterColorProperty =
        DependencyProperty.Register(nameof(MeterColor), typeof(Color), typeof(HorizontalMeterBar),
            new PropertyMetadata(Colors.DodgerBlue, OnMeterColorChanged));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Color MeterColor
    {
        get => (Color)GetValue(MeterColorProperty);
        set => SetValue(MeterColorProperty, value);
    }

    public HorizontalMeterBar()
    {
        Height = 8;
        
        var grid = new Grid();

        _background = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(76, 0, 0, 0)),
            CornerRadius = new CornerRadius(2)
        };

        _foreground = new Border
        {
            Background = new SolidColorBrush(MeterColor),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        grid.Children.Add(_background);
        grid.Children.Add(_foreground);

        Content = grid;

        SizeChanged += (s, e) => UpdateMeterWidth();
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HorizontalMeterBar meter)
        {
            meter.UpdateMeterWidth();
        }
    }

    private static void OnMeterColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HorizontalMeterBar meter)
        {
            meter._foreground.Background = new SolidColorBrush((Color)e.NewValue);
        }
    }

    private void UpdateMeterWidth()
    {
        double level = Math.Max(0, Math.Min(1, Level));
        _foreground.Width = ActualWidth * level;
    }
}
