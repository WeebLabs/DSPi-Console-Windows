using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;
using Microsoft.UI.Dispatching;

namespace DSPiConsole.ViewModels;

public partial class StatsViewModel : ObservableObject, IDisposable
{
    private readonly DspDevice _device;
    private readonly DispatcherQueue _dispatcher;
    private readonly System.Timers.Timer _pollTimer;
    private bool _disposed;
    private bool _deviceInfoFetched;

    [ObservableProperty] private string _platform = "—";
    [ObservableProperty] private string _firmwareVersion = "—";
    [ObservableProperty] private string _serial = "—";

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
    [ObservableProperty] private string _usbRingOverruns = "—";

    // Buffer stats
    [ObservableProperty] private int _numSpdifInstances;
    [ObservableProperty] private bool _isPdmActive;
    [ObservableProperty] private bool _isAudioStreaming;
    [ObservableProperty] private SpdifBufferStats[] _spdifBufferStats = new SpdifBufferStats[4];
    [ObservableProperty] private PdmBufferStats _pdmBufferStats;

    // Formatted strings for SPDIF per-instance display
    [ObservableProperty] private string _spdif1Fill = "—";
    [ObservableProperty] private string _spdif1Watermarks = "—";
    [ObservableProperty] private string _spdif1Queued = "—";
    [ObservableProperty] private string _spdif2Fill = "—";
    [ObservableProperty] private string _spdif2Watermarks = "—";
    [ObservableProperty] private string _spdif2Queued = "—";
    [ObservableProperty] private string _spdif3Fill = "—";
    [ObservableProperty] private string _spdif3Watermarks = "—";
    [ObservableProperty] private string _spdif3Queued = "—";
    [ObservableProperty] private string _spdif4Fill = "—";
    [ObservableProperty] private string _spdif4Watermarks = "—";
    [ObservableProperty] private string _spdif4Queued = "—";

    // PDM buffer display
    [ObservableProperty] private string _pdmDmaFill = "—";
    [ObservableProperty] private string _pdmDmaWatermarks = "—";
    [ObservableProperty] private string _pdmRingFill = "—";
    [ObservableProperty] private string _pdmRingWatermarks = "—";

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

    private void FetchDeviceInfo()
    {
        if (_disposed || !_device.IsConnected) return;
        try
        {
            var info = _device.GetDeviceInfo();
            var serial = _device.GetDeviceSerial();
            if (info == null && serial == null) return;
            _deviceInfoFetched = true;
            _dispatcher.TryEnqueue(() =>
            {
                Platform = info?.Platform ?? "—";
                FirmwareVersion = info?.FirmwareVersion ?? "—";
                Serial = serial ?? "—";
            });
        }
        catch { }
    }

    private void PollStats()
    {
        if (_disposed || !_device.IsConnected) return;
        if (!_deviceInfoFetched) FetchDeviceInfo();

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
            var usbRingOver = _device.GetStatusUInt32(22);

            var bufferStats = _device.GetBufferStats();

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
                UsbRingOverruns = usbRingOver?.ToString() ?? "—";

                if (bufferStats != null)
                {
                    NumSpdifInstances = bufferStats.NumSpdif;
                    IsPdmActive = bufferStats.IsPdmActive;
                    IsAudioStreaming = bufferStats.IsAudioStreaming;
                    SpdifBufferStats = bufferStats.Spdif;
                    PdmBufferStats = bufferStats.Pdm;

                    UpdateSpdifInstance(0, bufferStats.Spdif[0], bufferStats.NumSpdif,
                        v => Spdif1Fill = v, v => Spdif1Watermarks = v, v => Spdif1Queued = v);
                    UpdateSpdifInstance(1, bufferStats.Spdif[1], bufferStats.NumSpdif,
                        v => Spdif2Fill = v, v => Spdif2Watermarks = v, v => Spdif2Queued = v);
                    UpdateSpdifInstance(2, bufferStats.Spdif[2], bufferStats.NumSpdif,
                        v => Spdif3Fill = v, v => Spdif3Watermarks = v, v => Spdif3Queued = v);
                    UpdateSpdifInstance(3, bufferStats.Spdif[3], bufferStats.NumSpdif,
                        v => Spdif4Fill = v, v => Spdif4Watermarks = v, v => Spdif4Queued = v);

                    var pdm = bufferStats.Pdm;
                    PdmDmaFill = $"{pdm.DmaFillPct}%";
                    PdmDmaWatermarks = $"{pdm.DmaMinFillPct}% – {pdm.DmaMaxFillPct}%";
                    PdmRingFill = $"{pdm.RingFillPct}%";
                    PdmRingWatermarks = $"{pdm.RingMinFillPct}% – {pdm.RingMaxFillPct}%";
                }
            });
        }
        catch
        {
            // Ignore polling errors
        }
    }

    private static void UpdateSpdifInstance(int index, SpdifBufferStats s, int numActive,
        Action<string> setFill, Action<string> setWatermarks, Action<string> setQueued)
    {
        if (index >= numActive)
        {
            setFill("N/A");
            setWatermarks("N/A");
            setQueued("N/A");
            return;
        }

        setFill($"{s.ConsumerFillPct}%");
        setWatermarks($"{s.ConsumerMinFillPct}% – {s.ConsumerMaxFillPct}%");
        setQueued($"{s.ConsumerPrepared}p + {s.ConsumerPlaying}a / {s.ConsumerFree + s.ConsumerPrepared + s.ConsumerPlaying} total");
    }

    public void ResetWatermarks()
    {
        Task.Run(() =>
        {
            if (_disposed || !_device.IsConnected) return;
            _device.ResetBufferStats();
        });
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
