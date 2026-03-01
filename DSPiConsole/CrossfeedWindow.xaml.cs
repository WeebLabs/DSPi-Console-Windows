using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinRT.Interop;

namespace DSPiConsole;

public sealed partial class CrossfeedWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isUpdating = true;
    private readonly DispatcherTimer _scrollbarFadeTimer;
    private bool _isPointerOverScrollViewer;
    private UIElement? _verticalScrollBar;

    private const float LogMin = 1.30103f;  // log10(20)
    private const float LogMax = 4.30103f;  // log10(20000)

    public CrossfeedWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        // Initialize scrollbar fade timer
        _scrollbarFadeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _scrollbarFadeTimer.Tick += (s, e) =>
        {
            _scrollbarFadeTimer.Stop();
            if (!_isPointerOverScrollViewer && _verticalScrollBar != null)
                AnimateScrollBarOpacity(0.0, 300);
        };

        InitializeComponent();

        // Set window size and dark titlebar (380x560)
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId) ??
            throw new InvalidOperationException("AppWindow not available");
        appWindow.Resize(new Windows.Graphics.SizeInt32(380, 560));
        appWindow.Title = "Crossfeed";

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
        EnableToggle.IsOn = _viewModel.CrossfeedEnabled;
        PresetRadio.SelectedIndex = Math.Clamp(_viewModel.CrossfeedPreset, 0, 3);
        SyncFreqFeedControls();
        UpdateCustomControlsEnabled();
        ItdToggle.IsOn = _viewModel.CrossfeedItd;
        _isUpdating = false;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        CurveCanvas.SizeChanged += (s, e) => DrawGraph();

        // Find scrollbar after layout is complete
        MainScrollViewer.Loaded += (s, e) =>
        {
            _verticalScrollBar = FindVisualChild<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>(MainScrollViewer);
            if (_verticalScrollBar != null)
                _scrollbarFadeTimer.Start();
        };
    }

    private bool IsCustom => _viewModel.CrossfeedPreset == 3;

    /// <summary>
    /// Push current ViewModel freq/feed values into the slider and text controls.
    /// Must be called while _isUpdating is true.
    /// </summary>
    private void SyncFreqFeedControls()
    {
        FreqSlider.Value = _viewModel.CrossfeedFreq;
        FreqTextBox.Text = _viewModel.CrossfeedFreq.ToString("F0");
        FeedSlider.Value = _viewModel.CrossfeedFeed;
        FeedTextBox.Text = _viewModel.CrossfeedFeed.ToString("F1");
    }

    /// <summary>
    /// Enable freq/feed controls only when Custom preset is active.
    /// </summary>
    private void UpdateCustomControlsEnabled()
    {
        bool custom = IsCustom;
        FreqSlider.IsEnabled = custom;
        FreqTextBox.IsEnabled = custom;
        FeedSlider.IsEnabled = custom;
        FeedTextBox.IsEnabled = custom;
    }

    /// <summary>
    /// Handles ViewModel property changes from external sources (device fetch).
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isUpdating) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            _isUpdating = true;
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.CrossfeedEnabled):
                    EnableToggle.IsOn = _viewModel.CrossfeedEnabled;
                    break;
                case nameof(MainViewModel.CrossfeedPreset):
                    PresetRadio.SelectedIndex = Math.Clamp(_viewModel.CrossfeedPreset, 0, 3);
                    SyncFreqFeedControls();
                    UpdateCustomControlsEnabled();
                    DrawGraph();
                    break;
                case nameof(MainViewModel.CrossfeedFreq):
                    FreqSlider.Value = _viewModel.CrossfeedFreq;
                    FreqTextBox.Text = _viewModel.CrossfeedFreq.ToString("F0");
                    DrawGraph();
                    break;
                case nameof(MainViewModel.CrossfeedFeed):
                    FeedSlider.Value = _viewModel.CrossfeedFeed;
                    FeedTextBox.Text = _viewModel.CrossfeedFeed.ToString("F1");
                    DrawGraph();
                    break;
                case nameof(MainViewModel.CrossfeedItd):
                    ItdToggle.IsOn = _viewModel.CrossfeedItd;
                    break;
            }
            _isUpdating = false;
        });
    }

    private void OnEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        _viewModel.CrossfeedEnabled = EnableToggle.IsOn;
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating) return;

        int presetIndex = PresetRadio.SelectedIndex;
        if (presetIndex < 0 || presetIndex >= CrossfeedData.Presets.Length) return;

        _isUpdating = true;
        _viewModel.CrossfeedPreset = presetIndex;

        // For non-Custom presets, push the preset's freq/feed into the ViewModel
        if (presetIndex < CrossfeedData.Presets.Length - 1)
        {
            var (freq, feed, _) = CrossfeedData.Presets[presetIndex];
            _viewModel.CrossfeedFreq = freq;
            _viewModel.CrossfeedFeed = feed;
        }

        SyncFreqFeedControls();
        UpdateCustomControlsEnabled();
        _isUpdating = false;
        DrawGraph();
    }

    // --- Freq/Feed slider and text handlers (only active in Custom mode) ---

    private void OnFreqSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        _viewModel.CrossfeedFreq = (float)e.NewValue;
        FreqTextBox.Text = e.NewValue.ToString("F0");
        _isUpdating = false;
        DrawGraph();
    }

    private void OnFreqTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating) return;
        if (float.TryParse(FreqTextBox.Text, out float value))
        {
            _isUpdating = true;
            value = Math.Clamp(value, 500, 2000);
            _viewModel.CrossfeedFreq = value;
            FreqSlider.Value = value;
            _isUpdating = false;
            DrawGraph();
        }
    }

    private void OnFeedSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        _viewModel.CrossfeedFeed = (float)e.NewValue;
        FeedTextBox.Text = e.NewValue.ToString("F1");
        _isUpdating = false;
        DrawGraph();
    }

    private void OnFeedTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating) return;
        if (float.TryParse(FeedTextBox.Text, out float value))
        {
            _isUpdating = true;
            value = Math.Clamp(value, 0, 15);
            _viewModel.CrossfeedFeed = value;
            FeedSlider.Value = value;
            _isUpdating = false;
            DrawGraph();
        }
    }

    private void OnItdToggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        _viewModel.CrossfeedItd = ItdToggle.IsOn;
    }

    // --- Graph rendering ---

    private void DrawGraph()
    {
        CurveCanvas.Children.Clear();
        YAxisCanvas.Children.Clear();
        XAxisCanvas.Children.Clear();

        double plotW = CurveCanvas.ActualWidth;
        double plotH = CurveCanvas.ActualHeight;
        if (plotW <= 0 || plotH <= 0) return;

        var (freqs, directMags, crossfeedMags) = CrossfeedData.GetResponseCurves(
            _viewModel.CrossfeedFreq, _viewModel.CrossfeedFeed);

        const float dbTop = 5f;
        const float dbBottom = -35f;
        const float dbTotal = dbTop - dbBottom;
        const float tickStep = 10f;

        var labelBrush = new SolidColorBrush(Color.FromArgb(140, 180, 180, 180));
        var gridBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        var zeroBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));

        for (float db = dbBottom; db <= dbTop; db += tickStep)
        {
            double y = (dbTop - db) / dbTotal * plotH;

            CurveCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = y, X2 = plotW, Y2 = y,
                Stroke = Math.Abs(db) < 0.01f ? zeroBrush : gridBrush,
                StrokeThickness = 1
            });

            var label = new TextBlock
            {
                Text = db > 0 ? $"+{db:F0}" : $"{db:F0}",
                FontSize = 9,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Foreground = labelBrush,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right,
                Width = 32
            };
            Canvas.SetTop(label, y - 6);
            Canvas.SetLeft(label, 0);
            YAxisCanvas.Children.Add(label);
        }

        float[] freqTicks = { 100, 1000, 10000 };
        foreach (var freq in freqTicks)
        {
            float logF = MathF.Log10(freq);
            double x = (logF - LogMin) / (LogMax - LogMin) * plotW;

            CurveCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = plotH,
                Stroke = gridBrush,
                StrokeThickness = 1
            });

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

        // Direct path curve (blue)
        var directLine = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(255, 100, 180, 246)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };
        var directPoints = new PointCollection();
        for (int i = 0; i < freqs.Length; i++)
        {
            float logF = MathF.Log10(freqs[i]);
            double x = (logF - LogMin) / (LogMax - LogMin) * plotW;
            double y = (dbTop - directMags[i]) / dbTotal * plotH;
            y = Math.Clamp(y, 0, plotH);
            directPoints.Add(new Windows.Foundation.Point(x, y));
        }
        directLine.Points = directPoints;
        CurveCanvas.Children.Add(directLine);

        // Crossfeed path curve (orange)
        var crossfeedLine = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 140, 66)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };
        var crossfeedPoints = new PointCollection();
        for (int i = 0; i < freqs.Length; i++)
        {
            float logF = MathF.Log10(freqs[i]);
            double x = (logF - LogMin) / (LogMax - LogMin) * plotW;
            double y = (dbTop - crossfeedMags[i]) / dbTotal * plotH;
            y = Math.Clamp(y, 0, plotH);
            crossfeedPoints.Add(new Windows.Foundation.Point(x, y));
        }
        crossfeedLine.Points = crossfeedPoints;
        CurveCanvas.Children.Add(crossfeedLine);
    }

    // --- Scrollbar auto-hide ---

    private void OnScrollViewerPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerOverScrollViewer = true;
        _scrollbarFadeTimer.Stop();
        if (_verticalScrollBar != null)
            AnimateScrollBarOpacity(1.0, 200);
    }

    private void OnScrollViewerPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerOverScrollViewer = false;
        _scrollbarFadeTimer.Stop();
        _scrollbarFadeTimer.Start();
    }

    private void OnScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_verticalScrollBar == null) return;

        if (!e.IsIntermediate)
        {
            _scrollbarFadeTimer.Stop();
            _scrollbarFadeTimer.Start();
        }
        else
        {
            AnimateScrollBarOpacity(1.0, 200);
        }
    }

    private void AnimateScrollBarOpacity(double toOpacity, int durationMs)
    {
        if (_verticalScrollBar == null) return;

        var animation = new DoubleAnimation
        {
            To = toOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, _verticalScrollBar);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Begin();
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? childName = null) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild && (childName == null || (child is FrameworkElement fe && fe.Name == childName)))
                return typedChild;

            var foundChild = FindVisualChild<T>(child, childName);
            if (foundChild != null)
                return foundChild;
        }
        return null;
    }
}
