using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinRT.Interop;

namespace DSPiConsole;

public sealed partial class LoudnessWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isUpdating = true;

    private const float LogMin = 1.30103f;  // log10(20)
    private const float LogMax = 4.09691f;  // log10(12500)

    public LoudnessWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        // Set window size and dark titlebar
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow?.Resize(new Windows.Graphics.SizeInt32(400, 600));
        appWindow!.Title = "Loudness Compensation";

        if (appWindow.TitleBar is { } titleBar)
        {
            titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
            titleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 140, 140);
            titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
            titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 140, 140);
            titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 50, 50, 50);
        }

        // Initialize controls from ViewModel
        _isUpdating = true;
        EnableToggle.IsOn = _viewModel.LoudnessEnabled;
        RefSPLSlider.Value = _viewModel.LoudnessRefSPL;
        RefSPLTextBox.Text = _viewModel.LoudnessRefSPL.ToString("F0");
        IntensitySlider.Value = _viewModel.LoudnessIntensity;
        IntensityTextBox.Text = _viewModel.LoudnessIntensity.ToString("F0");
        _isUpdating = false;

        // Subscribe to ViewModel changes
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Draw initial curve on size
        CurveCanvas.SizeChanged += (s, e) => DrawCurve();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isUpdating) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            _isUpdating = true;
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.LoudnessEnabled):
                    EnableToggle.IsOn = _viewModel.LoudnessEnabled;
                    break;
                case nameof(MainViewModel.LoudnessRefSPL):
                    RefSPLSlider.Value = _viewModel.LoudnessRefSPL;
                    RefSPLTextBox.Text = _viewModel.LoudnessRefSPL.ToString("F0");
                    break;
                case nameof(MainViewModel.LoudnessIntensity):
                    IntensitySlider.Value = _viewModel.LoudnessIntensity;
                    IntensityTextBox.Text = _viewModel.LoudnessIntensity.ToString("F0");
                    break;
            }
            _isUpdating = false;
        });
    }

    private void OnEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        _viewModel.LoudnessEnabled = EnableToggle.IsOn;
    }

    private void OnRefSPLChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        _viewModel.LoudnessRefSPL = (float)e.NewValue;
        RefSPLTextBox.Text = e.NewValue.ToString("F0");
        _isUpdating = false;
        DrawCurve();
    }

    private void OnRefSPLTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating) return;
        if (float.TryParse(RefSPLTextBox.Text, out float value))
        {
            _isUpdating = true;
            value = Math.Clamp(value, 40, 100);
            _viewModel.LoudnessRefSPL = value;
            RefSPLSlider.Value = value;
            _isUpdating = false;
            DrawCurve();
        }
    }

    private void OnIntensityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        _viewModel.LoudnessIntensity = (float)e.NewValue;
        IntensityTextBox.Text = e.NewValue.ToString("F0");
        _isUpdating = false;
        DrawCurve();
    }

    private void OnIntensityTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating) return;
        if (float.TryParse(IntensityTextBox.Text, out float value))
        {
            _isUpdating = true;
            value = Math.Clamp(value, 0, 200);
            _viewModel.LoudnessIntensity = value;
            IntensitySlider.Value = value;
            _isUpdating = false;
            DrawCurve();
        }
    }

    private void DrawCurve()
    {
        CurveCanvas.Children.Clear();
        YAxisCanvas.Children.Clear();
        XAxisCanvas.Children.Clear();

        double plotW = CurveCanvas.ActualWidth;
        double plotH = CurveCanvas.ActualHeight;
        if (plotW <= 0 || plotH <= 0) return;

        // Get compensation curve
        float refSPL = _viewModel.LoudnessRefSPL;
        float intensity = _viewModel.LoudnessIntensity;
        float effectivePhon = Math.Clamp(refSPL - 20, 20, 80);

        var curve = LoudnessData.GetCompensationCurve(refSPL, effectivePhon, intensity);

        // Fixed scale: +15 dB (top) to -45 dB (bottom)
        const float dbTop = 15f;
        const float dbBottom = -45f;
        const float dbTotal = dbTop - dbBottom; // 60
        const float tickStep = 15f;

        // --- Y-axis labels + horizontal grid ---
        var labelBrush = new SolidColorBrush(Color.FromArgb(140, 180, 180, 180));
        var gridBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        var zeroBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));

        for (float db = dbBottom; db <= dbTop; db += tickStep)
        {
            double y = (dbTop - db) / dbTotal * plotH;

            // Grid line on plot canvas
            CurveCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = y, X2 = plotW, Y2 = y,
                Stroke = Math.Abs(db) < 0.01f ? zeroBrush : gridBrush,
                StrokeThickness = 1
            });

            // Y-axis label
            var label = new TextBlock
            {
                Text = db > 0 ? $"+{db:F0}" : $"{db:F0}",
                FontSize = 9,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Foreground = labelBrush,
            };
            Canvas.SetTop(label, y - 6);
            Canvas.SetLeft(label, 0);
            label.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right;
            label.Width = 32;
            YAxisCanvas.Children.Add(label);
        }

        // --- X-axis labels + vertical grid ---
        float[] freqTicks = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
        foreach (var freq in freqTicks)
        {
            float logF = MathF.Log10(freq);
            double x = (logF - LogMin) / (LogMax - LogMin) * plotW;

            // Vertical grid line
            CurveCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = plotH,
                Stroke = gridBrush,
                StrokeThickness = 1
            });

            // X-axis label
            string text = freq >= 1000 ? $"{freq / 1000}k" : $"{freq:F0}";
            var label = new TextBlock
            {
                Text = text,
                FontSize = 9,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Foreground = labelBrush,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                Width = 28
            };
            Canvas.SetLeft(label, x - 14);
            Canvas.SetTop(label, 2);
            XAxisCanvas.Children.Add(label);
        }

        // --- Compensation curve ---
        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(255, 100, 180, 255)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };

        var points = new PointCollection();
        foreach (var (freq, db) in curve)
        {
            float logF = MathF.Log10(freq);
            double x = (logF - LogMin) / (LogMax - LogMin) * plotW;
            double y = (dbTop - db) / dbTotal * plotH;
            points.Add(new Windows.Foundation.Point(x, y));
        }

        polyline.Points = points;
        CurveCanvas.Children.Add(polyline);
    }
}
