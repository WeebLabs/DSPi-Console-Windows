using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace DSPiConsole;

public sealed partial class StatsWindow : Window
{
    private readonly StatsViewModel _viewModel;

    public StatsWindow(DspDevice device)
    {
        InitializeComponent();

        _viewModel = new StatsViewModel(device);

        // Set window size
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId) ??
            throw new InvalidOperationException("Failed to retrieve AppWindow");
        appWindow.Resize(new Windows.Graphics.SizeInt32(400, 800));
        appWindow.Title = "Stats for nerbs";

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

        // Bind ViewModel changes to UI
        _viewModel.PropertyChanged += (s, e) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PlatformText.Text = _viewModel.Platform;
                FirmwareVersionText.Text = _viewModel.FirmwareVersion;
                SerialText.Text = _viewModel.Serial;
                ClockText.Text = _viewModel.ClockHz;
                VoltageText.Text = _viewModel.VoltageMv;
                SampleRateText.Text = _viewModel.SampleRateHz;
                TempText.Text = _viewModel.TemperatureC;
                PdmRingOverText.Text = _viewModel.PdmRingOverruns;
                PdmRingUnderText.Text = _viewModel.PdmRingUnderruns;
                PdmDmaOverText.Text = _viewModel.PdmDmaOverruns;
                PdmDmaUnderText.Text = _viewModel.PdmDmaUnderruns;
                SpdifOverText.Text = _viewModel.SpdifOverruns;
                SpdifUnderText.Text = _viewModel.SpdifUnderruns;
            });
        };

        RootGrid.Loaded += (s, e) =>
        {
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            int nonClientH = appWindow.Size.Height - (int)Math.Round(RootGrid.ActualHeight * scale);
            RootGrid.Measure(new Windows.Foundation.Size(RootGrid.ActualWidth, double.PositiveInfinity));
            var desired = RootGrid.DesiredSize;
            appWindow.Resize(new Windows.Graphics.SizeInt32(
                appWindow.Size.Width,
                (int)Math.Ceiling(desired.Height * scale) + nonClientH));
        };

        Closed += (s, e) => _viewModel.Dispose();
    }
}
