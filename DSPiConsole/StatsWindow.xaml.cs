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
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow?.Resize(new Windows.Graphics.SizeInt32(400, 500));
        appWindow!.Title = "Stats for nerbs";

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

        Closed += (s, e) => _viewModel.Dispose();
    }
}
