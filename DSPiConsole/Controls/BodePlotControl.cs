using DSPiConsole.Core.Models;
using DSPiConsole.Models;
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
/// Uses XAML Polyline for rendering with optional glow and animation.
/// </summary>
public sealed class BodePlotControl : UserControl
{
    private Canvas? _canvas;
    private MainViewModel? _viewModel;

    private const float MinFreq = 20.0f;
    private const float MaxFreq = 20000.0f;
    private const float DbRange = 20.0f;
    private const int NumPoints = 201;

    // Animation state
    private readonly Dictionary<int, float[]> _currentMagnitudes = new();
    private readonly Dictionary<int, float[]> _targetMagnitudes = new();
    private readonly DispatcherTimer _animTimer;
    private bool _isAnimating;

    // Cached polyline references per channel (for glow: 3 per channel, otherwise 1)
    private readonly Dictionary<int, List<Polyline>> _channelPolylines = new();

    public BodePlotControl()
    {
        _canvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromArgb(128, 32, 32, 36))
        };
        Content = _canvas;

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _animTimer.Tick += OnAnimationTick;

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
            AppSettings.Instance.SettingsChanged += OnSettingsChanged;

            // Initialize magnitudes
            foreach (var channel in Channel.All)
            {
                var id = (int)channel.Id;
                _currentMagnitudes[id] = new float[NumPoints];
                _targetMagnitudes[id] = new float[NumPoints];
            }

            UpdateTargets();
            // Snap current to target on first load (no animation)
            foreach (var channel in Channel.All)
            {
                var id = (int)channel.Id;
                Array.Copy(_targetMagnitudes[id], _currentMagnitudes[id], NumPoints);
            }
            Redraw();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _animTimer.Stop();
        _isAnimating = false;
        if (_viewModel != null)
        {
            _viewModel.FiltersChanged -= OnFiltersChanged;
            _viewModel.VisibilityChanged -= OnVisibilityChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        AppSettings.Instance.SettingsChanged -= OnSettingsChanged;
    }

    private void OnFiltersChanged(object? sender, EventArgs e)
    {
        UpdateTargets();
        StartAnimation();
    }

    private void OnVisibilityChanged(object? sender, EventArgs e) => Redraw();
    private void OnSettingsChanged(object? sender, EventArgs e) => Redraw();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Bypass))
        {
            UpdateTargets();
            StartAnimation();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void UpdateTargets()
    {
        if (_viewModel == null) return;

        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            var (_, magnitudes) = _viewModel.GetResponseCurve(channel);
            if (magnitudes.Length == NumPoints)
            {
                Array.Copy(magnitudes, _targetMagnitudes[id], NumPoints);
            }
            else if (magnitudes.Length > 0)
            {
                // Resize if needed
                for (int i = 0; i < NumPoints; i++)
                {
                    float pct = i / (float)(NumPoints - 1);
                    int srcIdx = Math.Clamp((int)(pct * (magnitudes.Length - 1)), 0, magnitudes.Length - 1);
                    _targetMagnitudes[id][i] = magnitudes[srcIdx];
                }
            }
            else
            {
                Array.Clear(_targetMagnitudes[id]);
            }
        }
    }

    private void StartAnimation()
    {
        if (!_isAnimating)
        {
            _isAnimating = true;
            _animTimer.Start();
        }
    }

    private void OnAnimationTick(object? sender, object e)
    {
        float speed = (float)AppSettings.Instance.GraphAnimationSpeed;
        float lerpFactor = Math.Clamp(speed, 0.05f, 0.5f);
        bool allDone = true;

        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            var current = _currentMagnitudes[id];
            var target = _targetMagnitudes[id];

            for (int i = 0; i < NumPoints; i++)
            {
                float diff = target[i] - current[i];
                if (MathF.Abs(diff) > 0.01f)
                {
                    current[i] += diff * lerpFactor;
                    allDone = false;
                }
                else
                {
                    current[i] = target[i];
                }
            }
        }

        Redraw();

        if (allDone)
        {
            _animTimer.Stop();
            _isAnimating = false;
        }
    }

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

    public void Invalidate()
    {
        UpdateTargets();
        StartAnimation();
    }

    private void Redraw()
    {
        if (_canvas == null) return;

        _canvas.Children.Clear();
        _channelPolylines.Clear();

        double width = ActualWidth;
        double height = ActualHeight;

        if (width <= 0 || height <= 0) return;

        DrawGrid(width, height);

        if (_viewModel == null) return;

        var settings = AppSettings.Instance;
        bool showGlow = settings.ShowGraphGlow;
        float lineWidth = (float)settings.GraphLineWidth;

        foreach (var channel in Channel.All)
        {
            if (!_viewModel.GetChannelVisibility(channel))
                continue;

            var id = (int)channel.Id;
            if (!_currentMagnitudes.ContainsKey(id)) continue;

            var magnitudes = _currentMagnitudes[id];
            var polylines = new List<Polyline>();

            var points = new PointCollection();
            for (int i = 0; i < NumPoints; i++)
            {
                double x = (double)i / (NumPoints - 1) * width;
                double y = YPos(magnitudes[i], height);
                points.Add(new Windows.Foundation.Point(x, y));
            }

            if (showGlow)
            {
                // Outer glow
                var outerGlow = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(50, channel.Color.R, channel.Color.G, channel.Color.B)),
                    StrokeThickness = lineWidth * 4,
                    StrokeLineJoin = PenLineJoin.Round,
                    Points = ClonePoints(points)
                };
                _canvas.Children.Add(outerGlow);
                polylines.Add(outerGlow);

                // Inner glow
                var innerGlow = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(100, channel.Color.R, channel.Color.G, channel.Color.B)),
                    StrokeThickness = lineWidth * 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    Points = ClonePoints(points)
                };
                _canvas.Children.Add(innerGlow);
                polylines.Add(innerGlow);
            }

            // Main line
            var mainLine = new Polyline
            {
                Stroke = new SolidColorBrush(channel.Color),
                StrokeThickness = lineWidth,
                StrokeLineJoin = PenLineJoin.Round,
                Points = points
            };
            _canvas.Children.Add(mainLine);
            polylines.Add(mainLine);

            _channelPolylines[id] = polylines;
        }
    }

    private void DrawGrid(double width, double height)
    {
        var gridColor = Color.FromArgb(25, 255, 255, 255);
        var zeroLineColor = Color.FromArgb(76, 255, 255, 255);

        // Vertical grid lines (frequency decades)
        foreach (var freq in new[] { 100f, 1000f, 10000f })
        {
            double x = XPos(freq, width);
            _canvas!.Children.Add(new Line
            {
                X1 = x, Y1 = 0,
                X2 = x, Y2 = height,
                Stroke = new SolidColorBrush(gridColor),
                StrokeThickness = 1
            });
        }

        // Horizontal grid lines (dB)
        foreach (var db in new[] { -10f, 0f, 10f })
        {
            double y = YPos(db, height);
            var color = db == 0 ? zeroLineColor : gridColor;
            _canvas!.Children.Add(new Line
            {
                X1 = 0, Y1 = y,
                X2 = width, Y2 = y,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1
            });
        }
    }

    private static PointCollection ClonePoints(PointCollection source)
    {
        var clone = new PointCollection();
        foreach (var pt in source)
            clone.Add(pt);
        return clone;
    }
}
