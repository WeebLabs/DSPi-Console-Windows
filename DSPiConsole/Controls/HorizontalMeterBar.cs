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
    private readonly DispatcherTimer _smoothingTimer;
    private double _currentLevel;
    private double _targetLevel;

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

        // Smoothing timer at ~60fps
        _smoothingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _smoothingTimer.Tick += OnSmoothingTick;
        _smoothingTimer.Start();
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HorizontalMeterBar meter)
        {
            meter._targetLevel = (double)e.NewValue;
        }
    }

    private static void OnMeterColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HorizontalMeterBar meter)
        {
            meter._foreground.Background = new SolidColorBrush((Color)e.NewValue);
        }
    }

    private void OnSmoothingTick(object? sender, object e)
    {
        // Lerp towards target with different speeds for attack and decay
        double diff = _targetLevel - _currentLevel;

        if (diff > 0)
        {
            // Attack (rising) - faster response
            _currentLevel += diff * 0.4;
        }
        else
        {
            // Decay (falling) - slower response for smoother falloff
            _currentLevel += diff * 0.15;
        }

        // Snap to target if very close
        if (Math.Abs(diff) < 0.001)
        {
            _currentLevel = _targetLevel;
        }

        UpdateMeterWidth();
    }

    private void UpdateMeterWidth()
    {
        double level = Math.Max(0, Math.Min(1, _currentLevel));
        _foreground.Width = ActualWidth * level;
    }
}
