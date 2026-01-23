using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DSPiConsole.Core;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;
using Microsoft.UI.Dispatching;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Main ViewModel for the DSPi Console application.
/// Manages all DSP state, USB communication, and UI bindings.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DspDevice _device;
    private readonly DispatcherQueue _dispatcher;
    private readonly System.Timers.Timer _pollTimer;
    private bool _disposed;

    // Channel filter data: Dictionary<ChannelId, List<FilterParams>>
    private readonly Dictionary<int, ObservableCollection<FilterParams>> _channelData = new();
    
    // Channel visibility for graph: Dictionary<ChannelId, bool>
    private readonly Dictionary<int, bool> _channelVisibility = new();
    
    // Channel delays: Dictionary<ChannelId, float>
    private readonly Dictionary<int, float> _channelDelays = new();

    [ObservableProperty]
    private float _preampDb;

    [ObservableProperty]
    private bool _bypass;

    [ObservableProperty]
    private bool _isDeviceConnected;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private SystemStatus _status = new();

    [ObservableProperty]
    private Channel? _selectedChannel;

    public IReadOnlyDictionary<int, ObservableCollection<FilterParams>> ChannelData => _channelData;
    public IReadOnlyDictionary<int, bool> ChannelVisibility => _channelVisibility;
    public IReadOnlyDictionary<int, float> ChannelDelays => _channelDelays;

    // Event for notifying UI when graph needs redraw
    public event EventHandler? FiltersChanged;
    public event EventHandler? VisibilityChanged;

    public MainViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _device = new DspDevice();

        // Initialize channel data
        foreach (var channel in Channel.All)
        {
            var filters = new ObservableCollection<FilterParams>();
            for (int i = 0; i < channel.BandCount; i++)
            {
                filters.Add(new FilterParams());
            }
            _channelData[(int)channel.Id] = filters;
            _channelVisibility[(int)channel.Id] = true;
            _channelDelays[(int)channel.Id] = 0.0f;
        }

        // Subscribe to device events
        _device.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DspDevice.IsConnected))
            {
                _dispatcher.TryEnqueue(() =>
                {
                    IsDeviceConnected = _device.IsConnected;
                    if (_device.IsConnected)
                    {
                        // Delay fetch slightly to ensure device is ready
                        Task.Delay(100).ContinueWith(_ => 
                            _dispatcher.TryEnqueue(FetchAll));
                    }
                });
            }
            else if (e.PropertyName == nameof(DspDevice.ErrorMessage))
            {
                _dispatcher.TryEnqueue(() => ErrorMessage = _device.ErrorMessage);
            }
        };

        // Status polling timer (60ms interval)
        _pollTimer = new System.Timers.Timer(60);
        _pollTimer.Elapsed += (s, e) =>
        {
            if (IsDeviceConnected)
            {
                FetchStatus();
            }
        };
        _pollTimer.AutoReset = true;

        // Start device monitoring
        _device.StartMonitoring();
        _pollTimer.Start();
    }

    public void UpdateChannelSelection(Channel? channel)
    {
        SelectedChannel = channel;

        if (channel != null)
        {
            // Show only selected channel
            foreach (var ch in Channel.All)
            {
                _channelVisibility[(int)ch.Id] = ch.Id == channel.Id;
            }
        }
        else
        {
            // Show all channels
            foreach (var ch in Channel.All)
            {
                _channelVisibility[(int)ch.Id] = true;
            }
        }

        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleChannelVisibility(Channel channel)
    {
        var id = (int)channel.Id;
        _channelVisibility[id] = !_channelVisibility[id];
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool GetChannelVisibility(Channel channel) => 
        _channelVisibility.TryGetValue((int)channel.Id, out var v) && v;

    public float GetChannelDelay(Channel channel) =>
        _channelDelays.TryGetValue((int)channel.Id, out var d) ? d : 0;

    public ObservableCollection<FilterParams> GetFilters(Channel channel) =>
        _channelData.TryGetValue((int)channel.Id, out var f) ? f : new();

    #region USB Commands

    private void FetchAll()
    {
        if (!FetchPreamp()) return;
        FetchBypass();

        foreach (var channel in Channel.All)
        {
            for (int band = 0; band < channel.BandCount; band++)
            {
                FetchFilter((int)channel.Id, band);
            }
            if (channel.IsOutput)
            {
                FetchDelay((int)channel.Id);
            }
        }

        // Dispatch FiltersChanged to run after all filter updates are processed
        _dispatcher.TryEnqueue(() => FiltersChanged?.Invoke(this, EventArgs.Empty));
    }

    private void FetchStatus()
    {
        var status = _device.GetStatus();
        if (status != null)
        {
            _dispatcher.TryEnqueue(() => Status = status);
        }
    }

    public void SetFilter(int channel, int band, FilterParams p)
    {
        if (_channelData.TryGetValue(channel, out var filters) && band < filters.Count)
        {
            filters[band] = p;
        }
        _device.SetFilter(channel, band, p);
        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FetchFilter(int channel, int band)
    {
        var p = _device.GetFilter(channel, band);
        if (p != null && _channelData.TryGetValue(channel, out var filters) && band < filters.Count)
        {
            if (!filters[band].Equals(p))
            {
                _dispatcher.TryEnqueue(() => filters[band] = p);
            }
        }
    }

    public void SetDelay(int channel, float ms)
    {
        _channelDelays[channel] = ms;
        _device.SetDelay(channel, ms);
        OnPropertyChanged(nameof(ChannelDelays));
    }

    private void FetchDelay(int channel)
    {
        var delay = _device.GetDelay(channel);
        if (delay.HasValue)
        {
            var current = _channelDelays.TryGetValue(channel, out var d) ? d : 0;
            if (Math.Abs(current - delay.Value) > 0.01f)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _channelDelays[channel] = delay.Value;
                    OnPropertyChanged(nameof(ChannelDelays));
                });
            }
        }
    }

    partial void OnPreampDbChanged(float value)
    {
        _device.SetPreamp(value);
    }

    private bool FetchPreamp()
    {
        var preamp = _device.GetPreamp();
        if (preamp.HasValue)
        {
            if (Math.Abs(PreampDb - preamp.Value) > 0.1f)
            {
                _dispatcher.TryEnqueue(() => PreampDb = preamp.Value);
            }
            return true;
        }
        _dispatcher.TryEnqueue(() => IsDeviceConnected = false);
        return false;
    }

    partial void OnBypassChanged(bool value)
    {
        _device.SetBypass(value);
        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FetchBypass()
    {
        var bypass = _device.GetBypass();
        if (bypass.HasValue)
        {
            _dispatcher.TryEnqueue(() => Bypass = bypass.Value);
        }
    }

    [RelayCommand]
    private void ClearAllMaster()
    {
        var defaultFilter = new FilterParams(FilterType.Flat, 1000, 0.707f, 0);
        var masterChannels = new[] { (int)ChannelId.MasterLeft, (int)ChannelId.MasterRight };

        foreach (var ch in masterChannels)
        {
            if (_channelData.TryGetValue(ch, out var filters))
            {
                for (int b = 0; b < filters.Count; b++)
                {
                    SetFilter(ch, b, defaultFilter.Clone());
                }
            }
        }
    }

    [RelayCommand]
    private void Reconnect()
    {
        _device.Reconnect();
    }

    /// <summary>
    /// Save current parameters to device flash.
    /// </summary>
    public byte SaveParams()
    {
        if (!IsDeviceConnected) return FlashResult.ErrWrite;
        return _device.SaveParams();
    }

    /// <summary>
    /// Load parameters from device flash, refreshing UI.
    /// </summary>
    public byte LoadParams()
    {
        if (!IsDeviceConnected) return FlashResult.ErrWrite;
        var result = _device.LoadParams();
        if (result == FlashResult.Ok)
        {
            FetchAll();
        }
        return result;
    }

    /// <summary>
    /// Reset all parameters to factory defaults, refreshing UI.
    /// </summary>
    public byte FactoryResetParams()
    {
        if (!IsDeviceConnected) return FlashResult.ErrWrite;
        var result = _device.FactoryReset();
        if (result == FlashResult.Ok)
        {
            FetchAll();
        }
        return result;
    }

    #endregion

    #region Graph Data Generation

    public (float[] frequencies, float[] magnitudes) GetResponseCurve(Channel channel)
    {
        if (!_channelData.TryGetValue((int)channel.Id, out var filters))
            return (Array.Empty<float>(), Array.Empty<float>());

        // If master channel and bypass is on, return flat response
        if ((channel.Id == ChannelId.MasterLeft || channel.Id == ChannelId.MasterRight) && Bypass)
        {
            var freqs = new float[201];
            var mags = new float[201];
            for (int i = 0; i < 201; i++)
            {
                float pct = i / 200.0f;
                freqs[i] = MathF.Pow(10, MathF.Log10(20) + pct * (MathF.Log10(20000) - MathF.Log10(20)));
                mags[i] = 0;
            }
            return (freqs, mags);
        }

        return DspMath.GenerateResponseCurve(filters);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer.Stop();
        _pollTimer.Dispose();
        _device.Dispose();

        GC.SuppressFinalize(this);
    }
}
