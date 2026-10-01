using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// The output limiter on an output's channel page: an icon under the mute
/// button, grey when off, the accent colour when on, orange while it is
/// reducing gain. A click toggles it and a right-click opens its settings. The
/// card gains no height, so the filter list keeps every row it had.
/// </summary>
public sealed partial class MainWindow
{
    private Button? _limiterButton;
    private LimiterIcon? _limiterIcon;
    private int _limiterOutput = -1;
    private bool _limiterEventsHooked;

    /// <summary>Orange, as the macOS Console shows a limiter reducing gain.</summary>
    private static readonly Color LimitingColor = Color.FromArgb(255, 255, 159, 10);

    /// <summary>The icon for <paramref name="output"/>, or null when the
    /// firmware has no limiter. Placed by the output card's builder.</summary>
    private FrameworkElement? CreateLimiterCell(int output)
    {
        if (!ViewModel.LimiterSupported || output < 0) return null;
        if (!_limiterEventsHooked)
        {
            _limiterEventsHooked = true;
            ViewModel.LimiterChanged += (_, _) => UpdateLimiterCell();
            ViewModel.LimiterMeterChanged += (_, _) => UpdateLimiterCell();
        }

        var icon = new LimiterIcon();
        var button = new Button
        {
            Content = icon,
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        button.Click += (_, _) =>
        {
            if (!ViewModel.IsDeviceConnected || output >= ViewModel.LimiterOutputs.Count) return;
            ViewModel.SetLimiterEnabled(output, !ViewModel.LimiterOutputs[output].Enabled);
        };
        button.RightTapped += (_, e) =>
        {
            e.Handled = true;
            if (!ViewModel.IsDeviceConnected) return;
            var flyout = new Flyout
            {
                Content = new OutputLimiterSettings(ViewModel, output),
                Placement = FlyoutPlacementMode.LeftEdgeAlignedTop,
            };
            flyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters =
                {
                    new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)),
                    new Setter(FlyoutPresenter.MaxWidthProperty, 400.0),
                },
            };
            flyout.ShowAt(button);
        };
        // The meter is read only while an icon is on screen. Loaded can repeat
        // without an Unloaded between, so the count moves once per state change.
        bool watching = false;
        button.Loaded += (_, _) => { if (!watching) { watching = true; ViewModel.WatchLimiterMeter(true); } };
        button.Unloaded += (_, _) => { if (watching) { watching = false; ViewModel.WatchLimiterMeter(false); } };

        _limiterButton = button;
        _limiterIcon = icon;
        _limiterOutput = output;
        UpdateLimiterCell();
        return button;
    }

    private void UpdateLimiterCell()
    {
        if (_limiterButton == null || _limiterIcon == null) return;
        int output = _limiterOutput;
        var limiter = ViewModel.LimiterOutputs;
        var s = output >= 0 && output < limiter.Count ? limiter[output] : new LimiterOutputSettings();
        var gr = ViewModel.LimiterReductionDb;
        bool limiting = s.Enabled && output < gr.Count && gr[output] >= 0.05f;
        bool connected = ViewModel.IsDeviceConnected;

        _limiterIcon.Brush = limiting ? new SolidColorBrush(LimitingColor)
            : s.Enabled ? new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColor"])
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _limiterButton.Opacity = connected ? 1 : 0.5;
        ToolTipService.SetToolTip(_limiterButton, s.Enabled
            ? $"Output limiter on, ceiling {s.ThresholdDb:0.0} dBFS. Click to switch off, right-click for settings."
            : "Output limiter off. Click to switch on, right-click for settings.");
    }
}
