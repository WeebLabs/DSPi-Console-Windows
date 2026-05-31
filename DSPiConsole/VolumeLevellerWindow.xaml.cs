using System.Globalization;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace DSPiConsole;

public sealed partial class VolumeLevellerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isUpdating = true;

    public VolumeLevellerWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        // Set window size and dark titlebar
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow!.Title = "Volume Leveller";

        // Non-resizable
        if (appWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsResizable = false;

        // Size window to fit content after first layout
        if (Content is FrameworkElement root)
        {
            root.Loaded += (s, e) =>
            {
                root.Measure(new Windows.Foundation.Size(400, double.PositiveInfinity));
                var scale = root.XamlRoot?.RasterizationScale ?? 1.0;
                int w = (int)(400 * scale);
                int h = (int)((root.DesiredSize.Height + 52) * scale); // +32 title bar +20 padding
                appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            };
        }

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
        EnableToggle.IsOn = _viewModel.LevellerEnabled;
        AmountSlider.Value = _viewModel.LevellerAmount;
        AmountTextBox.Text = _viewModel.LevellerAmount.ToString("F0", CultureInfo.InvariantCulture);
        MaxGainSlider.Value = _viewModel.LevellerMaxGainDb;
        MaxGainTextBox.Text = _viewModel.LevellerMaxGainDb.ToString("F1", CultureInfo.InvariantCulture);
        GateSlider.Value = _viewModel.LevellerGateDb;
        GateTextBox.Text = _viewModel.LevellerGateDb.ToString("F0", CultureInfo.InvariantCulture);
        LookaheadToggle.IsOn = _viewModel.LevellerLookahead;
        SetSpeedRadio(_viewModel.LevellerSpeed);
        UpdateSpeedDescription(_viewModel.LevellerSpeed);
        _isUpdating = false;

        // Subscribe to ViewModel changes
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isUpdating) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            _isUpdating = true;
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.LevellerEnabled):
                    EnableToggle.IsOn = _viewModel.LevellerEnabled;
                    break;
                case nameof(MainViewModel.LevellerAmount):
                    AmountSlider.Value = _viewModel.LevellerAmount;
                    AmountTextBox.Text = _viewModel.LevellerAmount.ToString("F0", CultureInfo.InvariantCulture);
                    break;
                case nameof(MainViewModel.LevellerSpeed):
                    SetSpeedRadio(_viewModel.LevellerSpeed);
                    UpdateSpeedDescription(_viewModel.LevellerSpeed);
                    break;
                case nameof(MainViewModel.LevellerMaxGainDb):
                    MaxGainSlider.Value = _viewModel.LevellerMaxGainDb;
                    MaxGainTextBox.Text = _viewModel.LevellerMaxGainDb.ToString("F1", CultureInfo.InvariantCulture);
                    break;
                case nameof(MainViewModel.LevellerLookahead):
                    LookaheadToggle.IsOn = _viewModel.LevellerLookahead;
                    break;
                case nameof(MainViewModel.LevellerGateDb):
                    GateSlider.Value = _viewModel.LevellerGateDb;
                    GateTextBox.Text = _viewModel.LevellerGateDb.ToString("F0", CultureInfo.InvariantCulture);
                    break;
            }
            _isUpdating = false;
        });
    }

    private void OnEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        _viewModel.LevellerEnabled = EnableToggle.IsOn;
    }

    // Amount
    private void OnAmountSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        _viewModel.LevellerAmount = (float)e.NewValue;
        AmountTextBox.Text = e.NewValue.ToString("F0", CultureInfo.InvariantCulture);
        _isUpdating = false;
    }

    private void OnAmountTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating) return;
        if (float.TryParse(AmountTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            _isUpdating = true;
            value = Math.Clamp(value, 0, 100);
            _viewModel.LevellerAmount = value;
            AmountSlider.Value = value;
            _isUpdating = false;
        }
    }

    // Speed
    private void OnSpeedChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        int speed = ReferenceEquals(sender, SpeedSlow) ? 0 : ReferenceEquals(sender, SpeedMedium) ? 1 : 2;
        _viewModel.LevellerSpeed = speed;
        UpdateSpeedDescription(speed);
    }

    private void SetSpeedRadio(int speed)
    {
        SpeedSlow.IsChecked = speed == 0;
        SpeedMedium.IsChecked = speed == 1;
        SpeedFast.IsChecked = speed == 2;
    }

    private void UpdateSpeedDescription(int speed)
    {
        SpeedDescription.Text = speed switch
        {
            0 => "Slow \u2014 Gentle response for music and wide dynamic range content.",
            2 => "Fast \u2014 Tight response for speech, dialogue, and podcasts.",
            _ => "Medium \u2014 Balanced response for general purpose use."
        };
    }

    // Max Gain
    private void OnMaxGainSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        _viewModel.LevellerMaxGainDb = (float)e.NewValue;
        MaxGainTextBox.Text = e.NewValue.ToString("F1", CultureInfo.InvariantCulture);
        _isUpdating = false;
    }

    private void OnMaxGainTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating) return;
        if (float.TryParse(MaxGainTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            _isUpdating = true;
            value = Math.Clamp(value, 0, 35);
            _viewModel.LevellerMaxGainDb = value;
            MaxGainSlider.Value = value;
            _isUpdating = false;
        }
    }

    // Gate Threshold
    private void OnGateSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        _viewModel.LevellerGateDb = (float)e.NewValue;
        GateTextBox.Text = e.NewValue.ToString("F0", CultureInfo.InvariantCulture);
        _isUpdating = false;
    }

    private void OnGateTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating) return;
        if (float.TryParse(GateTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            _isUpdating = true;
            value = Math.Clamp(value, -96, 0);
            _viewModel.LevellerGateDb = value;
            GateSlider.Value = value;
            _isUpdating = false;
        }
    }

    // Lookahead
    private void OnLookaheadToggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        _viewModel.LevellerLookahead = LookaheadToggle.IsOn;
    }
}
