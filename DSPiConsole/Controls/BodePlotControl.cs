using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// Custom control for rendering Bode plot frequency response curves.
/// Uses XAML Polyline for rendering.
/// </summary>
public sealed class BodePlotControl : UserControl
{
    private Canvas? _canvas;
    private MainViewModel? _viewModel;

    private const float MinFreq = 20.0f;
    private const float MaxFreq = 20000.0f;
    private const float DbRange = 20.0f;

    public BodePlotControl()
    {
        _canvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromArgb(128, 32, 32, 36))
        };
        Content = _canvas;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            _viewModel = vm;
            _viewModel.FiltersChanged += OnFiltersChanged;
            _viewModel.VisibilityChanged += OnVisibilityChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Redraw();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.FiltersChanged -= OnFiltersChanged;
            _viewModel.VisibilityChanged -= OnVisibilityChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnFiltersChanged(object? sender, EventArgs e) => Redraw();
    private void OnVisibilityChanged(object? sender, EventArgs e) => Redraw();
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Bypass))
            Redraw();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private double XPos(float freq, double width)
    {
        float logMin = MathF.Log10(MinFreq);
        float logMax = MathF.Log10(MaxFreq);
        float logVal = MathF.Log10(freq);
        return (logVal - logMin) / (logMax - logMin) * width;
    }

    private double YPos(float db, double height)
    {
        float normalized = (db + DbRange) / (2.0f * DbRange);
        return height - (normalized * height);
    }

    public void Invalidate() => Redraw();

    private void Redraw()
    {
        if (_canvas == null) return;

        _canvas.Children.Clear();

        double width = ActualWidth;
        double height = ActualHeight;

        if (width <= 0 || height <= 0) return;

        // Draw grid
        var gridColor = Color.FromArgb(25, 255, 255, 255);
        var zeroLineColor = Color.FromArgb(76, 255, 255, 255);

        // Vertical grid lines (frequency decades)
        foreach (var freq in new[] { 100f, 1000f, 10000f })
        {
            double x = XPos(freq, width);
            var line = new Line
            {
                X1 = x, Y1 = 0,
                X2 = x, Y2 = height,
                Stroke = new SolidColorBrush(gridColor),
                StrokeThickness = 1
            };
            _canvas.Children.Add(line);
        }

        // Horizontal grid lines (dB)
        foreach (var db in new[] { -10f, 0f, 10f })
        {
            double y = YPos(db, height);
            var color = db == 0 ? zeroLineColor : gridColor;
            var line = new Line
            {
                X1 = 0, Y1 = y,
                X2 = width, Y2 = y,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = db == 0 ? 1 : 1
            };
            _canvas.Children.Add(line);
        }

        if (_viewModel == null) return;

        // Draw each visible channel's response curve
        foreach (var channel in Channel.All)
        {
            if (!_viewModel.GetChannelVisibility(channel))
                continue;

            var (frequencies, magnitudes) = _viewModel.GetResponseCurve(channel);
            if (frequencies.Length == 0) continue;

            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(channel.Color),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            };

            var points = new PointCollection();
            for (int i = 0; i < frequencies.Length; i++)
            {
                double x = (double)i / (frequencies.Length - 1) * width;
                double y = YPos(magnitudes[i], height);
                points.Add(new Windows.Foundation.Point(x, y));
            }

            polyline.Points = points;
            _canvas.Children.Add(polyline);
        }
    }
}

