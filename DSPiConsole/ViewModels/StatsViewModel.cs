using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Usb;
using Microsoft.UI.Dispatching;

namespace DSPiConsole.ViewModels;

public partial class StatsViewModel : ObservableObject, IDisposable
{
    private readonly DspDevice _device;
    private readonly DispatcherQueue _dispatcher;
    private readonly System.Timers.Timer _pollTimer;
    private bool _disposed;

    [ObservableProperty] private string _clockHz = "—";
    [ObservableProperty] private string _voltageMv = "—";
    [ObservableProperty] private string _sampleRateHz = "—";
    [ObservableProperty] private string _temperatureC = "—";
    [ObservableProperty] private string _pdmRingOverruns = "—";
    [ObservableProperty] private string _pdmRingUnderruns = "—";
    [ObservableProperty] private string _pdmDmaOverruns = "—";
    [ObservableProperty] private string _pdmDmaUnderruns = "—";
    [ObservableProperty] private string _spdifOverruns = "—";
    [ObservableProperty] private string _spdifUnderruns = "—";

    public StatsViewModel(DspDevice device)
    {
        _device = device;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _pollTimer = new System.Timers.Timer(2000);
        _pollTimer.Elapsed += (_, _) => PollStats();
        _pollTimer.AutoReset = true;
        _pollTimer.Start();

        // Initial poll
        Task.Run(PollStats);
    }

    private void PollStats()
    {
        if (_disposed || !_device.IsConnected) return;

        try
        {
            var clockHz = _device.GetStatusUInt32(13);
            var voltageMv = _device.GetStatusUInt32(14);
            var sampleRate = _device.GetStatusUInt32(15);
            var tempCenti = _device.GetStatusInt32(16);

            var pdmRingOver = _device.GetStatusUInt32(3);
            var pdmRingUnder = _device.GetStatusUInt32(4);
            var pdmDmaOver = _device.GetStatusUInt32(5);
            var pdmDmaUnder = _device.GetStatusUInt32(6);
            var spdifOver = _device.GetStatusUInt32(7);
            var spdifUnder = _device.GetStatusUInt32(8);

            _dispatcher.TryEnqueue(() =>
            {
                ClockHz = clockHz.HasValue ? $"{clockHz.Value / 1_000_000.0:F1} MHz" : "—";
                VoltageMv = voltageMv.HasValue ? $"{voltageMv.Value / 1000.0:F2} V" : "—";
                SampleRateHz = sampleRate.HasValue ? $"{sampleRate.Value / 1000.0:F1} kHz" : "—";
                TemperatureC = tempCenti.HasValue ? $"{tempCenti.Value / 100.0:F1} °C" : "—";

                PdmRingOverruns = pdmRingOver?.ToString() ?? "—";
                PdmRingUnderruns = pdmRingUnder?.ToString() ?? "—";
                PdmDmaOverruns = pdmDmaOver?.ToString() ?? "—";
                PdmDmaUnderruns = pdmDmaUnder?.ToString() ?? "—";
                SpdifOverruns = spdifOver?.ToString() ?? "—";
                SpdifUnderruns = spdifUnder?.ToString() ?? "—";
            });
        }
        catch
        {
            // Ignore polling errors
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _pollTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}
