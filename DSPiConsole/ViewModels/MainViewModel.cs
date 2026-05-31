using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DSPiConsole.Core;
using DSPiConsole.Core.Models;
using DSPiConsole.Services;
using DSPiConsole.Usb;
using Microsoft.UI.Dispatching;

namespace DSPiConsole.ViewModels;

public enum UnsavedAction { Save, Discard, Cancel }

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

    // Channel gains: Dictionary<ChannelId, float> (output channels only)
    private readonly Dictionary<int, float> _channelGains = new();

    // Channel mutes: Dictionary<ChannelId, bool> (output channels only)
    private readonly Dictionary<int, bool> _channelMutes = new();

    // Output enabled in matrix mixer: Dictionary<outputIndex, bool>
    private readonly Dictionary<int, bool> _outputEnabled = new();

    // Matrix mixer state: [inputIndex, outputIndex]
    private readonly bool[,] _matrixRouting = new bool[2, 9];
    private readonly float[,] _matrixGain = new float[2, 9];
    private readonly bool[,] _matrixInvert = new bool[2, 9];

    // Per-output matrix mixer state (indexed by output position in ActiveOutputs)
    private readonly bool[] _outputMuted = new bool[9];

    // Output pin assignments: Dictionary<pinOutputId, byte>
    private readonly Dictionary<int, byte> _outputPins = new();

    // I2S configuration state
    private readonly OutputSlotType[] _outputSlotTypes = new OutputSlotType[4];
    private byte _i2sBckPin = 14;     // firmware default
    private bool _mckEnabled;
    private byte _mckPin = 13;        // firmware default
    private int _mckMultiplier = 128;
    private uint _sampleRateHz;
    private byte _spdifRxPin = 11;    // firmware default (PICO_SPDIF_RX_PIN_DEFAULT)

    // Clip tracking
    private ushort _clipLatched;
    private DateTime? _clipTimestamp;

    // Preset system state
    private int _activePresetSlot = -1;
    private ushort _presetOccupiedMask;
    private readonly string[] _presetNames = new string[10];
    private byte _presetStartupMode;
    private byte _presetDefaultSlot;
    private bool _presetIncludePins;
    // Master volume persistence mode:
    //   0 = MASTER_VOLUME_MODE_INDEPENDENT — volume is independent of presets
    //       and is explicitly persisted via SaveMasterVolume (0xD6).
    //   1 = MASTER_VOLUME_MODE_WITH_PRESET — volume travels with each preset.
    private byte _masterVolumeMode;
    private PresetSnapshot? _savedSnapshot;

    // Suppress dirty detection while bulk-fetching state from the device.
    // Setters fired during FetchAll would otherwise each capture a snapshot
    // and diff it against the previous (stale) snapshot, flipping PresetsDirty
    // true. FetchAll callers set this, then clear it after UpdateSavedSnapshot.
    private volatile bool _suppressDirtyCheck;

    // Set while applying a user-volume change pushed FROM the firmware (UAC1
    // host echo, GPIO knob, etc.) so OnUserVolumeDbChanged skips the
    // REQ_SET_USER_VOLUME round-trip. Both written and read on the UI
    // dispatcher inside the same synchronous setter→partial-method chain,
    // so no volatility is needed.
    private bool _suppressUserVolumeSend;

    // Channel copy/paste clipboard
    private ChannelClipboard? _channelClipboard;
    public bool HasChannelClipboard => _channelClipboard != null;

    // Per-input-channel preamp (dB). Index 0 = L (MasterLeft), 1 = R (MasterRight).
    [ObservableProperty]
    private float _inputPreampLDb;

    [ObservableProperty]
    private float _inputPreampRDb;

    // Global master volume (dB). Adjustment range [-127, 0]; -128 = mute sentinel.
    [ObservableProperty]
    private float _masterVolumeDb;

    // Vendor-channel user volume (dB), V9+ firmware. Same audio_state.volume
    // field the UAC1 host slider writes to; sidebar can drive this directly via
    // REQ_SET_USER_VOLUME (0xDA) when in "user" mode. Range [-60, 0] dB.
    [ObservableProperty]
    private float _userVolumeDb;

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

    [ObservableProperty]
    private bool _loudnessEnabled;

    [ObservableProperty]
    private float _loudnessRefSPL = 83.0f;

    [ObservableProperty]
    private float _loudnessIntensity = 100.0f;

    [ObservableProperty]
    private bool _crossfeedEnabled;

    [ObservableProperty]
    private int _crossfeedPreset = 0; // 0=Default, 1=Chu Moy, 2=Jan Meier, 3=Custom

    [ObservableProperty]
    private float _crossfeedFreq = 700.0f; // Hz (500-2000)

    [ObservableProperty]
    private float _crossfeedFeed = 4.5f; // dB (0-15)

    [ObservableProperty]
    private bool _crossfeedItd = true; // Inter-aural Time Delay

    // Volume leveller
    [ObservableProperty]
    private bool _levellerEnabled;

    [ObservableProperty]
    private float _levellerAmount = 50.0f;

    [ObservableProperty]
    private int _levellerSpeed; // 0=Slow, 1=Medium, 2=Fast

    [ObservableProperty]
    private float _levellerMaxGainDb = 15.0f;

    [ObservableProperty]
    private bool _levellerLookahead = true;

    [ObservableProperty]
    private float _levellerGateDb = -96.0f;

    [ObservableProperty]
    private int _activePreset = -1;

    [ObservableProperty]
    private bool _presetsDirty;

    [ObservableProperty]
    private bool _masterPeqLinked;

    [ObservableProperty]
    private string _platform = "";

    // Input source (V7+ firmware). InputSourceSupported stays false until
    // GetInputSource succeeds at least once — older firmware STALLs on 0xE1.
    [ObservableProperty]
    private bool _inputSourceSupported;

    // Per-band bypass (firmware 1.1.4+). Mirrors the InputSource pattern: probe
    // once at connect via REQ_GET_BAND_BYPASS (0xD9); older firmware STALLs and
    // the UI hides the bypass toggle. See band_bypass_spec.md §8.
    [ObservableProperty]
    private bool _bandBypassSupported;

    // External DAC hardware mute (firmware V10+). One typed config object as
    // the unit of read/write — avoids parameter-order bugs and lets future
    // fields land via DacHwMuteConfig.With(...) without touching every caller.
    // Probe once at connect via REQ_GET_DAC_HW_MUTE_CONFIG (0xEB); older
    // firmware STALLs and the Settings UI shows an "unsupported" notice.
    // See Documentation/Features/dac_hardware_mute_spec.md.
    [ObservableProperty]
    private DacHwMuteConfig _dacHwMute = DacHwMuteConfig.CreateDefault();

    [ObservableProperty]
    private bool _dacHwMuteSupported;

    // LG Sound Sync (firmware V8+). Per-preset enable flag; the firmware
    // decodes the TV's TOSLINK volume / mute messages and applies them through
    // the user-volume path. Probe at connect via REQ_GET_LG_SOUND_SYNC_ENABLE
    // (0xE7); older firmware STALLs and the SPDIF Input settings page hides
    // the toggle accordingly. Runtime status fields (present, volume, muted)
    // aren't exposed here — only the user-writable enable matters for the UI.
    [ObservableProperty]
    private bool _lgSoundSyncEnabled;

    [ObservableProperty]
    private bool _lgSoundSyncSupported;

    // Tracks the source value the firmware most recently *notified* us about
    // (i.e. landed in its main loop). Distinct from ActiveInputSource, which
    // is preemptively updated by SetInputSourceAsync's read-back before the
    // deferred apply lands — that race would otherwise hide the SPDIF→USB
    // transition from the reconciliation check below.
    private readonly object _lastNotifiedSourceLock = new();
    private InputSource? _lastNotifiedSource;

    [ObservableProperty]
    private InputSource _activeInputSource = InputSource.Usb;

    public event EventHandler? InputSourceChanged;

    public IReadOnlyList<Channel> ActiveOutputs => Platform switch
    {
        "RP2040" => Channel.Rp2040Outputs,
        "RP2350" => Channel.Outputs,
        _        => Array.Empty<Channel>()
    };

    public event EventHandler? ActiveOutputsChanged;

    // Preset events and accessors
    public event EventHandler? PresetsChanged;
    public const int PresetSlotCount = 10;

    public bool IsPresetOccupied(int slot) => (_presetOccupiedMask & (1 << slot)) != 0;
    public ushort PresetOccupiedMask => _presetOccupiedMask;
    public string GetPresetName(int slot) => !string.IsNullOrEmpty(_presetNames[slot]) ? _presetNames[slot] : $"Preset {slot + 1}";
    public string GetPresetDisplayName(int slot)
    {
        if (!IsPresetOccupied(slot)) return "Empty";
        return !string.IsNullOrEmpty(_presetNames[slot]) ? _presetNames[slot] : $"Preset {slot + 1}";
    }
    public byte PresetStartupMode => _presetStartupMode;
    public byte PresetDefaultSlot => _presetDefaultSlot;
    public bool PresetIncludePins => _presetIncludePins;
    public byte MasterVolumeMode => _masterVolumeMode;

    // Multi-device support
    [ObservableProperty]
    private ObservableCollection<DSPiDeviceInfo> _availableDevices = new();

    [ObservableProperty]
    private DSPiDeviceInfo? _selectedDeviceItem;

    [ObservableProperty]
    private bool _isSwitchingDevice;

    /// <summary>Callback for showing unsaved changes dialog. Registered by MainWindow.</summary>
    public Func<string?, Task<UnsavedAction>>? ShowUnsavedChangesDialog { get; set; }

    /// <summary>Callback to prompt the user for a name when saving to an empty slot.
    /// Returns the chosen name, or null if the user cancelled.</summary>
    public Func<int, Task<string?>>? PromptForPresetName { get; set; }

    partial void OnPlatformChanged(string value)
    {
        _outputEnabled.Clear();
        ActiveOutputsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── PDM / EQ-worker conflict helpers ──

    public int PdmOutputIndex => Platform == "RP2040" ? 4 : 8;
    private int EqWorkerStart => 2;
    private int EqWorkerEnd => Platform == "RP2040" ? 4 : 8; // exclusive

    public bool WouldConflict(int outputIndex)
    {
        if (outputIndex == PdmOutputIndex)
        {
            for (int i = EqWorkerStart; i < EqWorkerEnd; i++)
                if (IsOutputEnabled(i)) return true;
            return false;
        }
        if (outputIndex >= EqWorkerStart && outputIndex < EqWorkerEnd)
            return IsOutputEnabled(PdmOutputIndex);
        return false;
    }

    public async Task SwitchToPdmAsync()
    {
        await Task.Run(() =>
        {
            for (int i = EqWorkerStart; i < EqWorkerEnd; i++)
                _device.SetOutputEnable(i, false);
            _device.SetOutputEnable(PdmOutputIndex, true);
            for (int i = EqWorkerStart; i < EqWorkerEnd; i++)
                FetchOutputEnable(i);
            FetchOutputEnable(PdmOutputIndex);
        });
    }

    public async Task SwitchFromPdmAsync(int enabling)
    {
        await Task.Run(() =>
        {
            _device.SetOutputEnable(PdmOutputIndex, false);
            _device.SetOutputEnable(enabling, true);
            FetchOutputEnable(PdmOutputIndex);
            FetchOutputEnable(enabling);
        });
    }

    public IReadOnlyDictionary<int, ObservableCollection<FilterParams>> ChannelData => _channelData;
    public IReadOnlyDictionary<int, bool> ChannelVisibility => _channelVisibility;
    public IReadOnlyDictionary<int, float> ChannelDelays => _channelDelays;
    public IReadOnlyDictionary<int, float> ChannelGains => _channelGains;
    public IReadOnlyDictionary<int, bool> ChannelMutes => _channelMutes;

    public DspDevice Device => _device;

    // Channel name overrides: Dictionary<ChannelId, string>
    private readonly Dictionary<int, string> _channelNames = new();

    public string GetChannelName(Channel channel) =>
        _channelNames.TryGetValue((int)channel.Id, out var n) ? n : channel.Name;

    public void SetChannelName(Channel channel, string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name == GetChannelName(channel)) return;
        _channelNames[(int)channel.Id] = name;
        Task.Run(() => _device.SetChannelNameOnDevice((int)channel.Id, name));
        ChannelNameChanged?.Invoke((int)channel.Id);
        CheckDirty();
    }

    // Output enabled state for matrix mixer / sidebar filtering
    public bool IsOutputEnabled(int outputIndex) =>
        _outputEnabled.TryGetValue(outputIndex, out var v) && v;

    public void SetOutputEnabled(int outputIndex, bool enabled)
    {
        _outputEnabled[outputIndex] = enabled;
        OutputEnabledChanged?.Invoke(outputIndex, enabled);
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public event Action<int, bool>? OutputEnabledChanged;

    // Matrix mixer change events
    public event Action<int, int>? MatrixRouteChanged;   // (input, output)
    public event Action<int>? MatrixOutputGainChanged;    // (outputIndex)
    public event Action<int>? MatrixOutputMuteChanged;    // (outputIndex)
    public event Action<int>? MatrixOutputDelayChanged;   // (outputIndex)

    // Event for notifying UI when graph needs redraw
    public event Action<int>? ChannelNameChanged;
    public event EventHandler? FiltersChanged;
    public event EventHandler? BypassChanged;
    public event EventHandler? VisibilityChanged;

    /// <summary>
    /// Fires after a bulk-params fetch (connect, preset load, factory
    /// reset, or BULK_INVALIDATED) finishes refreshing all VM state.
    /// Listeners that mirror multiple fields silently updated by the
    /// bulk path — pin assignments, slot types, leveller, etc. — read
    /// from the VM here to repaint, without needing one PropertyChanged
    /// per field. Always raised on the UI thread.
    /// </summary>
    public event EventHandler? BulkRefreshed;

    // I2S configuration accessors. (PropertyChanged on the individual
    // properties below — I2SBckPin, MckEnabled, MckPin, MckMultiplier,
    // AnySlotIsI2S — is the one and only notification surface; an old
    // I2SConfigChanged event existed but had zero subscribers and was
    // removed.)
    public OutputSlotType GetOutputSlotType(int slot) =>
        slot >= 0 && slot < _outputSlotTypes.Length ? _outputSlotTypes[slot] : OutputSlotType.Spdif;
    public int NumOutputSlots => Platform == "RP2350" ? 4 : 2;
    public byte I2SBckPin => _i2sBckPin;
    public bool MckEnabled => _mckEnabled;
    public byte MckPin => _mckPin;
    public int MckMultiplier => _mckMultiplier;
    public uint SampleRateHz => _sampleRateHz;
    public byte SpdifRxPin => _spdifRxPin;
    public bool AnySlotIsI2S => _outputSlotTypes.Take(NumOutputSlots).Any(t => t == OutputSlotType.I2S);

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
            if (channel.IsOutput)
            {
                _channelGains[(int)channel.Id] = 0.0f;
                _channelMutes[(int)channel.Id] = false;
            }
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
                        _suppressDirtyCheck = true;
                        Task.Run(() =>
                        {
                            var info = _device.GetDeviceInfo();
                            var newPlatform = info?.Platform ?? "";
                            // Set channel count for platform-aware status parsing
                            _device.NumChannels = newPlatform == "RP2350" ? 11 : 7;
                            _dispatcher.TryEnqueue(() => Platform = newPlatform);
                            System.Threading.Thread.Sleep(100);
                            FetchAll();
                            FetchPresetInfo();
                            FetchInputSource();
                            FetchBandBypassCapability();
                            // DAC HW mute (V10+) and LG Sound Sync (V8+)
                            // arrive via the bulk packet's WireDacHwMute /
                            // WireLgSoundSync sections — ApplyBulkParams
                            // sets *Supported and the live config. The
                            // FetchAll legacy fallback covers the pre-V2
                            // edge case explicitly.
                            _dispatcher.TryEnqueue(() =>
                            {
                                // Seed the notified-source tracker so the first
                                // user-initiated source switch after connect can
                                // detect the SPDIF→USB transition and reconcile
                                // the host endpoint to firmware user_volume.
                                lock (_lastNotifiedSourceLock)
                                    _lastNotifiedSource = ActiveInputSource;
                                UpdateSavedSnapshot();
                                PresetsDirty = false;
                                _suppressDirtyCheck = false;
                            });
                        });
                    }
                    else
                    {
                        // Keep Platform so the UI layout stays until a new device connects
                        ResetChannelData();
                        _presetsChecked = false;
                        ActivePreset = -1;
                        PresetsDirty = false;
                        _savedSnapshot = null;
                    }
                });
            }
            else if (e.PropertyName == nameof(DspDevice.ErrorMessage))
            {
                _dispatcher.TryEnqueue(() => ErrorMessage = _device.ErrorMessage);
            }
            else if (e.PropertyName == nameof(DspDevice.SelectedDeviceInfo))
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _isSwitchingDevice = true;
                    SelectedDeviceItem = _device.SelectedDeviceInfo;
                    _isSwitchingDevice = false;
                });
            }
        };

        _device.AvailableDevicesChanged += (s, e) =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                AvailableDevices.Clear();
                foreach (var d in _device.AvailableDevicesList)
                    AvailableDevices.Add(d);
            });
        };

        // V7+ notification endpoint: device pushes channel-name changes (and
        // other parameter changes) over bulk IN. Apply them to local state and
        // raise the UI event. Suppress echoes from our own host SETs since we
        // already updated the UI when the user typed.
        _device.ChannelNameNotified += (_, n) =>
        {
            if (n.Source == ParamSource.HostSet) return;
            _dispatcher.TryEnqueue(() =>
            {
                if (n.ChannelIndex < 0) return;
                _channelNames[n.ChannelIndex] = n.Name;
                ChannelNameChanged?.Invoke(n.ChannelIndex);
            });
        };

        // Per-band EQ change pushed from the device (GPIO knob, another host,
        // preset load, factory reset, deferred firmware apply). HostSet is
        // suppressed because the cache was already updated synchronously by the
        // ViewModel's own setter — replaying could clobber an in-flight edit.
        // Future GPIO support flows through this path with Source == Gpio.
        _device.BandParamNotified += (_, n) =>
        {
            if (n.Source == ParamSource.HostSet) return;
            _dispatcher.TryEnqueue(() =>
            {
                if (_channelData.TryGetValue(n.Channel, out var filters)
                    && n.Band < filters.Count)
                {
                    filters[n.Band] = n.Params;
                    FiltersChanged?.Invoke(this, EventArgs.Empty);
                    CheckDirty();
                }
            });
        };

        // User-volume change pushed from the device. The firmware tags:
        //   • HostSet — echo of our own REQ_SET_USER_VOLUME write; UserVolumeDb
        //               already holds this value, so the notification is
        //               redundant. Drop it.
        //   • Uac1    — UAC1 Feature Unit SET_CUR from the OS (system tray
        //               slider, keyboard volume keys); update UserVolumeDb so
        //               the sidebar slider tracks the OS volume live.
        //   • Other   — Preset / BulkSet / Gpio / Internal; apply as well.
        // For non-HostSet sources we set _suppressUserVolumeSend before writing
        // UserVolumeDb so OnUserVolumeDbChanged doesn't round-trip the value
        // back to firmware.
        _device.UserVolumeNotified += (_, n) =>
        {
            if (n.Source == ParamSource.HostSet) return;
            _dispatcher.TryEnqueue(() =>
            {
                if (Math.Abs(UserVolumeDb - n.Db) <= 0.05f) return;
                _suppressUserVolumeSend = true;
                try { UserVolumeDb = n.Db; }
                finally { _suppressUserVolumeSend = false; }
            });
        };

        // BULK_INVALIDATED is the firmware's "I changed many things at once,
        // re-read the full state" signal. It comes from several origins:
        //   • Preset / Factory  → a NEW baseline was loaded; reset the saved
        //                          snapshot and clear dirty.
        //   • HostSet / BulkSet → WE just wrote a parameter; the firmware
        //                          may have side-effected related fields, but
        //                          the saved-snapshot baseline must NOT be
        //                          touched — doing so would erase the dirty
        //                          state we just established for the user's
        //                          change. Re-CheckDirty after the refetch
        //                          so any side effects propagate.
        //   • Gpio / Internal   → hardware knob or firmware clamp; treat
        //                          like a host edit (legitimate divergence
        //                          from the saved preset, baseline stays).
        //   • Unknown           → conservative default: keep baseline,
        //                          re-check dirty.
        _device.BulkInvalidated += (_, src) =>
        {
            if (!IsDeviceConnected) return;
            Task.Run(() =>
            {
                // Mirror LoadPreset's pattern: every property setter fired by
                // FetchAll calls CheckDirty in its partial method, and without
                // this gate each one would diff the in-flight new state against
                // the still-old _savedSnapshot and flip PresetsDirty true for
                // the dispatcher tick or two before the cleanup block below
                // resets it — visible as a ~300ms dirty flash whenever the
                // firmware emits BULK_INVALIDATED(Preset) for a preset switch.
                _suppressDirtyCheck = true;
                FetchAll();
                _dispatcher.TryEnqueue(() =>
                {
                    if (src == ParamSource.Preset || src == ParamSource.Factory)
                    {
                        UpdateSavedSnapshot();
                        PresetsDirty = false;
                        _suppressDirtyCheck = false;
                    }
                    else
                    {
                        // Field values may have changed during FetchAll; re-
                        // compute dirty against the existing baseline. Must
                        // clear the suppression BEFORE the CheckDirty call,
                        // otherwise CheckDirty short-circuits and the host-
                        // initiated edit's dirty bit never lights up.
                        _suppressDirtyCheck = false;
                        CheckDirty();
                    }
                });
            });
        };

        // Active input source switch — fires when firmware's main loop applies
        // a deferred source change. Catches the race where BULK_INVALIDATED is
        // sent before the deferred apply lands, leaving the bulk fetch with
        // stale active_input_source.
        _device.InputSourceNotified += (_, newSource) =>
        {
            // Capture and update the notified-source tracker atomically so the
            // SPDIF→USB detection isn't fooled by other writers to
            // ActiveInputSource. Lock is uncontended in practice — the only
            // other writer is the connect-flow init below.
            InputSource? previousSource;
            lock (_lastNotifiedSourceLock)
            {
                previousSource = _lastNotifiedSource;
                _lastNotifiedSource = newSource;
            }

            // Fetch the firmware's user_volume on the notify thread BEFORE we
            // dispatch any UI state changes. The sidebar slider tracks
            // UserVolumeDb directly, so resyncing it in the same dispatcher
            // tick as ActiveInputSource keeps the slider from snapping
            // through a stale value when the source changes.
            //
            // previousSource is unused now that we no longer push anything
            // to the Windows endpoint — kept the variable for parity with
            // the firmware-side notification, in case future logic needs it.
            _ = previousSource;
            var uv = _device.GetUserVolume();

            _dispatcher.TryEnqueue(() =>
            {
                if (uv.HasValue && Math.Abs(UserVolumeDb - uv.Value) > 0.1f)
                    UserVolumeDb = uv.Value;

                if (ActiveInputSource != newSource)
                    ActiveInputSource = newSource;
                InputSourceSupported = true;
                InputSourceChanged?.Invoke(this, EventArgs.Empty);
            });
        };

        // Status polling timer (60ms interval if USB or 3000ms if remote)
        _pollTimer = new System.Timers.Timer(_device.IsDeviceUsb ? 60 : 3000);
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

    [RelayCommand]
    private async Task SwitchToDevice(DSPiDeviceInfo? device)
    {
        if (device == null || device == _device.SelectedDeviceInfo) return;

        if (IsDeviceConnected && PresetsDirty && ShowUnsavedChangesDialog != null)
        {
            var summary = GetChangeSummary();
            var result = await ShowUnsavedChangesDialog(summary);

            switch (result)
            {
                case UnsavedAction.Save:
                    if (ActivePreset >= 0)
                    {
                        string? name = null;
                        if (!IsPresetOccupied(ActivePreset))
                        {
                            if (PromptForPresetName == null) return;
                            name = await PromptForPresetName(ActivePreset);
                            if (name == null) return; // user cancelled
                        }
                        var saveResult = await SavePreset(ActivePreset, name);
                        if (saveResult != Usb.PresetResult.Ok)
                            return; // save failed, abort switch
                    }
                    break;
                case UnsavedAction.Discard:
                    break;
                case UnsavedAction.Cancel:
                    // Revert selection in UI
                    IsSwitchingDevice = true;
                    SelectedDeviceItem = _device.SelectedDeviceInfo;
                    IsSwitchingDevice = false;
                    return;
            }
        }

        _savedSnapshot = null;
        _ = Task.Run(() => _device.SelectDevice(device));
    }

    private void ResetChannelData()
    {
        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            if (_channelData.TryGetValue(id, out var filters))
            {
                for (int i = 0; i < filters.Count; i++)
                    filters[i] = new FilterParams();
            }
            _channelDelays[id] = 0.0f;
            if (channel.IsOutput)
            {
                _channelGains[id] = 0.0f;
                _channelMutes[id] = false;
            }
        }
        InputPreampLDb = 0;
        InputPreampRDb = 0;
        MasterVolumeDb = 0;
        Bypass = false;
        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateChannelSelection(Channel? channel)
    {
        SelectedChannel = channel;

        if (channel != null)
        {
            // When linked and a master channel is selected, show both Master L and R
            bool showBothMasters = MasterPeqLinked && IsMasterChannel((int)channel.Id);

            foreach (var ch in Channel.All)
            {
                if (showBothMasters)
                    _channelVisibility[(int)ch.Id] = IsMasterChannel((int)ch.Id);
                else
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

    public bool GetChannelVisibility(Channel channel)
    {
        if (!_channelVisibility.TryGetValue((int)channel.Id, out var v) || !v)
            return false;
        if (channel.IsOutput)
        {
            int outputIndex = GetOutputIndex((int)channel.Id);
            if (outputIndex < 0 || !IsOutputEnabled(outputIndex))
                return false;
        }
        return true;
    }

    public float GetChannelDelay(Channel channel) =>
        _channelDelays.TryGetValue((int)channel.Id, out var d) ? d : 0;

    public float GetChannelGain(Channel channel) =>
        _channelGains.TryGetValue((int)channel.Id, out var g) ? g : 0;

    public bool GetChannelMute(Channel channel) =>
        _channelMutes.TryGetValue((int)channel.Id, out var m) && m;

    public ObservableCollection<FilterParams> GetFilters(Channel channel) =>
        _channelData.TryGetValue((int)channel.Id, out var f) ? f : new();

    // ── Matrix mixer accessors ──

    public bool GetMatrixRouting(int input, int output) => _matrixRouting[input, output];
    public float GetMatrixGain(int input, int output) => _matrixGain[input, output];
    public bool GetMatrixInvert(int input, int output) => _matrixInvert[input, output];
    public float GetOutputGainDb(int output)
    {
        var outputs = ActiveOutputs;
        if (output < 0 || output >= outputs.Count) return 0f;
        return _channelGains.TryGetValue((int)outputs[output].Id, out var g) ? g : 0f;
    }
    public bool GetOutputMuted(int output) =>
        output >= 0 && output < _outputMuted.Length && _outputMuted[output];
    public float GetOutputDelayMs(int output)
    {
        var outputs = ActiveOutputs;
        if (output < 0 || output >= outputs.Count) return 0f;
        return _channelDelays.TryGetValue((int)outputs[output].Id, out var d) ? d : 0f;
    }

    public void SetMatrixRoute(int input, int output, bool enabled, float gain, bool invert)
    {
        _matrixRouting[input, output] = enabled;
        _matrixGain[input, output] = gain;
        _matrixInvert[input, output] = invert;
        Task.Run(() => _device.SetMatrixRoute(input, output, enabled, invert, gain));
        MatrixRouteChanged?.Invoke(input, output);
        CheckDirty();
    }

    public void SetOutputGainDb(int output, float db)
    {
        var outputs = ActiveOutputs;
        if (output < 0 || output >= outputs.Count) return;
        int channelId = (int)outputs[output].Id;
        SetChannelGain(channelId, db);
    }

    public void SetOutputMuted(int output, bool muted)
    {
        if (output < 0 || output >= _outputMuted.Length) return;
        _outputMuted[output] = muted;
        Task.Run(() => _device.SetOutputMute(output, muted));
        MatrixOutputMuteChanged?.Invoke(output);
        CheckDirty();
    }

    public void SetOutputDelayMs(int output, float ms)
    {
        var outputs = ActiveOutputs;
        if (output < 0 || output >= outputs.Count) return;
        int channelId = (int)outputs[output].Id;
        SetDelay(channelId, ms);
    }

    public void SetOutputEnableUsb(int output, bool enabled)
    {
        Task.Run(() => _device.SetOutputEnable(output, enabled));
        CheckDirty();
    }

    // ── Pin assignment accessors ──

    public byte GetOutputPinValue(int pinOutputId) =>
        _outputPins.TryGetValue(pinOutputId, out var v) ? v : (byte)0;

    public void FetchOutputPin(int pinOutputId)
    {
        var pin = _device.GetOutputPin(pinOutputId);
        if (pin.HasValue)
            _outputPins[pinOutputId] = pin.Value;
    }

    public byte SetOutputPinValue(int pinOutputId, byte pin)
    {
        var status = _device.SetOutputPin(pinOutputId, pin);
        if (status == Usb.PinConfigResult.Success)
        {
            _outputPins[pinOutputId] = pin;
            CheckDirty();
        }
        return status;
    }

    // ── I2S configuration accessors ──

    public void FetchOutputSlotType(int slot)
    {
        var type = _device.GetOutputType(slot);
        if (type.HasValue)
            _outputSlotTypes[slot] = type.Value;
    }

    public void FetchI2SBckPin()
    {
        var pin = _device.GetI2SBckPin();
        if (pin.HasValue) _i2sBckPin = pin.Value;
    }

    public void FetchMckEnable()
    {
        var enabled = _device.GetMckEnable();
        if (enabled.HasValue) _mckEnabled = enabled.Value;
    }

    public void FetchMckPin()
    {
        var pin = _device.GetMckPin();
        if (pin.HasValue) _mckPin = pin.Value;
    }

    public void FetchMckMultiplier()
    {
        var mult = _device.GetMckMultiplier();
        if (mult.HasValue) _mckMultiplier = mult.Value;
    }

    public void FetchSpdifRxPin()
    {
        var pin = _device.GetSpdifRxPin();
        if (pin.HasValue) _spdifRxPin = pin.Value;
    }

    public byte SetSpdifRxPin(byte pin)
    {
        var status = _device.SetSpdifRxPin(pin);
        if (status == PinConfigResult.Success)
        {
            _spdifRxPin = pin;
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(SpdifRxPin)));
            CheckDirty();
        }
        return status;
    }

    public byte SetOutputSlotType(int slot, OutputSlotType type)
    {
        var status = _device.SetOutputType(slot, type);
        if (status == PinConfigResult.Success)
        {
            _outputSlotTypes[slot] = type;
            UpdateDynamicChannelNames();
            // AnySlotIsI2S is a computed property that depends on the
            // slot-type array; settings UI subscribers (Hardware pages)
            // watch it to decide whether BCK/MCK combos are editable.
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(AnySlotIsI2S)));
            CheckDirty();
        }
        return status;
    }

    public byte SetI2SBckPin(byte pin)
    {
        var status = _device.SetI2SBckPin(pin);
        if (status == PinConfigResult.Success)
        {
            _i2sBckPin = pin;
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(I2SBckPin)));
            CheckDirty();
        }
        return status;
    }

    public byte SetMckEnable(bool enabled)
    {
        var status = _device.SetMckEnable(enabled);
        if (status == PinConfigResult.Success)
        {
            _mckEnabled = enabled;
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(MckEnabled)));
            CheckDirty();
        }
        return status;
    }

    public byte SetMckPin(byte pin)
    {
        var status = _device.SetMckPin(pin);
        if (status == PinConfigResult.Success)
        {
            _mckPin = pin;
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(MckPin)));
            CheckDirty();
        }
        return status;
    }

    public byte SetMckMultiplier(int multiplier)
    {
        var status = _device.SetMckMultiplier(multiplier);
        if (status == PinConfigResult.Success)
        {
            _mckMultiplier = multiplier;
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(MckMultiplier)));
            CheckDirty();
        }
        return status;
    }

    /// <summary>
    /// Update channel names for output slots based on their current type (S/PDIF vs I2S).
    /// Only overwrites names that match a known auto-generated pattern.
    /// </summary>
    private void UpdateDynamicChannelNames()
    {
        int slotCount = Platform == "RP2350" ? 4 : 2;
        for (int slot = 0; slot < slotCount; slot++)
        {
            var type = _outputSlotTypes[slot];
            string prefix = type == OutputSlotType.I2S ? "I2S" : "SPDIF";
            int num = slot + 1;

            int leftId = 2 + slot * 2;   // ChannelId: Spdif1L=2, Spdif2L=4, etc.
            int rightId = leftId + 1;

            string newNameL = $"{prefix} {num} L";
            string newNameR = $"{prefix} {num} R";

            // Only update if the current name is an auto-generated name (not user-customized)
            var defaultNameL = Channel.FromIndex(leftId).Name;
            var currentL = _channelNames.TryGetValue(leftId, out var nl) ? nl : defaultNameL;
            if (IsAutoGeneratedName(currentL, num, defaultNameL))
            {
                _channelNames[leftId] = newNameL;
                _dispatcher.TryEnqueue(() => ChannelNameChanged?.Invoke(leftId));
            }

            var defaultNameR = Channel.FromIndex(rightId).Name;
            var currentR = _channelNames.TryGetValue(rightId, out var nr) ? nr : defaultNameR;
            if (IsAutoGeneratedName(currentR, num, defaultNameR))
            {
                _channelNames[rightId] = newNameR;
                _dispatcher.TryEnqueue(() => ChannelNameChanged?.Invoke(rightId));
            }
        }
    }

    private static bool IsAutoGeneratedName(string name, int slotNum, string defaultName) =>
        name == defaultName ||
        name == $"SPDIF {slotNum} L" || name == $"SPDIF {slotNum} R" ||
        name == $"I2S {slotNum} L" || name == $"I2S {slotNum} R";

    #region USB Commands

    private void FetchAll()
    {
        try
        {
            // Try bulk fetch first (firmware v2+ with 0xA0 support)
            var bulk = _device.GetAllParams();
            if (bulk != null)
            {
                var parsed = BulkParamsParser.Parse(bulk);
                if (parsed != null)
                {
                    ApplyBulkParams(parsed);
                    return;
                }
            }

            // Fallback to legacy per-command fetching. Exercised when 0xA0
            // STALLs (pre-V2 firmware) or the parser rejects the response.
            // The V8+/V9+/V10+ feature fetches use dedicated opcodes that
            // STALL gracefully on older firmware (leaving *Supported = false),
            // so calling them unconditionally here is correct for both cases:
            // ancient firmware sees a clean "unsupported", and modern firmware
            // with a transient bulk-fetch failure still gets its state filled
            // in from the per-feature getters.
            FetchAllLegacy();
            FetchUserVolume();
            FetchLgSoundSync();
            FetchDacHwMute();
        }
        catch { }
    }

    private void ApplyBulkParams(BulkParams bp)
    {
        var outputs = ActiveOutputs;

        // EQ bands — apply first BandCount bands per channel
        foreach (var channel in Channel.All)
        {
            int ch = (int)channel.Id;
            if (_channelData.TryGetValue(ch, out var filters))
            {
                for (int band = 0; band < channel.BandCount && band < bp.MaxBands; band++)
                {
                    var fp = bp.Eq[ch, band];
                    int b = band; // capture for closure
                    if (!filters[b].Equals(fp))
                        _dispatcher.TryEnqueue(() => filters[b] = fp);
                }
            }
        }

        // Output channel gains, mutes, delays, and enable states
        for (int o = 0; o < outputs.Count && o < bp.Outputs.Length; o++)
        {
            var (enabled, muted, gain, delay) = bp.Outputs[o];
            int channelId = (int)outputs[o].Id;

            _channelGains[channelId] = gain;
            _channelMutes[channelId] = muted;
            _channelDelays[channelId] = delay;
            _outputEnabled[o] = enabled;
            if (o < _outputMuted.Length)
                _outputMuted[o] = muted;
        }

        // Matrix crosspoints
        for (int inp = 0; inp < 2; inp++)
        {
            for (int o = 0; o < outputs.Count && o < 9; o++)
            {
                var (enabled, invert, gain) = bp.Crosspoints[inp, o];
                _matrixRouting[inp, o] = enabled;
                _matrixInvert[inp, o] = invert;
                _matrixGain[inp, o] = gain;
            }
        }

        // Pin assignments
        for (int i = 0; i < bp.Pins.Length; i++)
            _outputPins[i] = bp.Pins[i];

        // I2S config (if present in bulk packet)
        if (bp.HasI2SConfig)
        {
            for (int i = 0; i < 4; i++)
                _outputSlotTypes[i] = (OutputSlotType)bp.OutputSlotTypes[i];
            _i2sBckPin = bp.BckPin;
            _mckPin = bp.MckPin;
            _mckEnabled = bp.MckEnabled;
            _mckMultiplier = bp.MckMultiplierEncoded == 1 ? 256 : 128;
        }

        // Input source / SPDIF RX pin (V7+ wire format)
        if (bp.HasInputConfig)
            _spdifRxPin = bp.SpdifRxPin;
        // Capture input source for the dispatcher block below — ActiveInputSource
        // is an ObservableProperty that fires on the UI thread. The firmware may
        // still be a few ms away from applying a deferred input_source switch when
        // it sends BULK_INVALIDATED (preset load); a follow-up PARAM_CHANGED at
        // input_config.input_source closes that race in DspDevice.ProcessNotifyPacket.
        InputSource? bulkInputSource = bp.HasInputConfig
            ? (InputSource?)bp.InputSource
            : null;

        // Fetch sample rate for MCK multiplier constraint
        var sr = _device.GetStatusUInt32(15);
        if (sr.HasValue) _sampleRateHz = sr.Value;

        // Channel names
        for (int ch = 0; ch < bp.ChannelNames.Length; ch++)
        {
            var name = bp.ChannelNames[ch];
            if (!string.IsNullOrEmpty(name))
                _channelNames[ch] = name;
        }

        // Dispatch all UI updates
        _dispatcher.TryEnqueue(() =>
        {
            if (bp.HasPerChannelPreamp)
            {
                InputPreampLDb = bp.PreampLDb;
                InputPreampRDb = bp.PreampRDb;
            }
            else
            {
                // Pre-V6 firmware: legacy uniform preamp in PreampGainDb
                InputPreampLDb = bp.PreampGainDb;
                InputPreampRDb = bp.PreampGainDb;
            }
            if (bp.HasMasterVolume)
                MasterVolumeDb = bp.MasterVolumeDb;
            Bypass = bp.Bypass;
            LoudnessEnabled = bp.LoudnessEnabled;
            LoudnessRefSPL = bp.LoudnessRefSpl;
            LoudnessIntensity = bp.LoudnessIntensityPct;
            CrossfeedEnabled = bp.CrossfeedEnabled;
            CrossfeedPreset = bp.CrossfeedPreset;
            CrossfeedItd = bp.CrossfeedItd;
            CrossfeedFreq = bp.CrossfeedFreq;
            CrossfeedFeed = bp.CrossfeedFeedDb;

            OnPropertyChanged(nameof(ChannelDelays));
            OnPropertyChanged(nameof(ChannelGains));
            OnPropertyChanged(nameof(ChannelMutes));

            for (int o = 0; o < outputs.Count; o++)
            {
                OutputEnabledChanged?.Invoke(o, _outputEnabled.TryGetValue(o, out var v) && v);
                MatrixOutputGainChanged?.Invoke(o);
                MatrixOutputMuteChanged?.Invoke(o);
                MatrixOutputDelayChanged?.Invoke(o);
                for (int inp = 0; inp < 2; inp++)
                    MatrixRouteChanged?.Invoke(inp, o);
            }

            for (int ch = 0; ch < bp.ChannelNames.Length; ch++)
            {
                if (!string.IsNullOrEmpty(bp.ChannelNames[ch]))
                    ChannelNameChanged?.Invoke(ch);
            }

            VisibilityChanged?.Invoke(this, EventArgs.Empty);
            FiltersChanged?.Invoke(this, EventArgs.Empty);

            if (bulkInputSource is { } src && (src == InputSource.Usb || src == InputSource.Spdif))
            {
                if (ActiveInputSource != src)
                    ActiveInputSource = src;
                InputSourceSupported = true;
                InputSourceChanged?.Invoke(this, EventArgs.Empty);
            }

            if (bp.HasI2SConfig)
                UpdateDynamicChannelNames();

            // Bulk parse updated I²S / SPDIF / sample-rate fields silently;
            // notify the Settings UI in one call. Re-notifying an unchanged
            // property is idempotent.
            NotifyHardwareConfigPropertiesChanged();
            BulkRefreshed?.Invoke(this, EventArgs.Empty);

            if (bp.HasLevellerConfig)
            {
                LevellerEnabled = bp.LevellerEnabled;
                LevellerAmount = bp.LevellerAmount;
                LevellerSpeed = bp.LevellerSpeed;
                LevellerMaxGainDb = bp.LevellerMaxGainDb;
                LevellerLookahead = bp.LevellerLookahead;
                LevellerGateDb = bp.LevellerGateDb;
            }

            // V8+ LG Sound Sync. HasLgSoundSync mirrors what the dedicated
            // 0xE7 fetch would tell us — when the bulk packet carries the
            // section, the firmware supports the feature, and vice versa.
            // The [ObservableProperty] equality check on the LgSoundSyncEnabled
            // setter means an unchanged value won't re-trigger the partial
            // OnLgSoundSyncEnabledChanged (which would write back via 0xE6).
            if (bp.HasLgSoundSync)
            {
                LgSoundSyncSupported = true;
                LgSoundSyncEnabled = bp.LgSoundSyncEnabled;
            }
            else
            {
                LgSoundSyncSupported = false;
            }

            // V9+ user volume. UserVolumeDb's partial setter normally writes
            // back via REQ_SET_USER_VOLUME (0xDA), which would round-trip the
            // value we just read. Suppress the write-back for the duration of
            // the assignment — the same flag the host-volume / GPIO-knob
            // notify paths use (see OnUserVolumeDbChanged at line 1825).
            if (bp.HasUserVolume && Math.Abs(UserVolumeDb - bp.UserVolumeDb) > 0.05f)
            {
                _suppressUserVolumeSend = true;
                try { UserVolumeDb = bp.UserVolumeDb; }
                finally { _suppressUserVolumeSend = false; }
            }

            // V10+ external DAC hardware mute. The DacHwMute property has no
            // partial setter so there's no write-back to suppress — assigning
            // a structurally-equal instance is a no-op courtesy of
            // DacHwMuteConfig.Equals.
            if (bp.HasDacHwMute && bp.DacHwMute != null)
            {
                DacHwMuteSupported = true;
                if (!bp.DacHwMute.Equals(DacHwMute))
                    DacHwMute = bp.DacHwMute;
            }
            else
            {
                DacHwMuteSupported = false;
            }
        });
    }

    private void FetchAllLegacy()
    {
        if (!FetchInputPreamps()) return;
        FetchMasterVolume();
        FetchBypass();

        foreach (var channel in Channel.All)
        {
            for (int band = 0; band < channel.BandCount; band++)
            {
                FetchFilter((int)channel.Id, band);
            }
        }

        foreach (var channel in ActiveOutputs)
        {
            FetchDelay((int)channel.Id);
            FetchChannelGain((int)channel.Id);
            FetchChannelMute((int)channel.Id);
        }

        FetchLoudness();
        FetchCrossfeed();

        // Fetch matrix mixer state
        FetchMatrixRoutes();
        var outputCount = ActiveOutputs.Count;
        for (int o = 0; o < outputCount; o++)
        {
            FetchOutputEnable(o);
            FetchOutputMuteState(o);
        }

        // Fetch pin assignments
        int pinCount = Platform == "RP2350" ? 5 : 3;
        for (int p = 0; p < pinCount; p++)
            FetchOutputPin(p);

        // Fetch I2S configuration
        int slotCount = Platform == "RP2350" ? 4 : 2;
        for (int s = 0; s < slotCount; s++)
            FetchOutputSlotType(s);
        FetchI2SBckPin();
        FetchMckEnable();
        FetchMckPin();
        FetchMckMultiplier();
        FetchSpdifRxPin();
        var sr = _device.GetStatusUInt32(15);
        if (sr.HasValue) _sampleRateHz = sr.Value;

        // Fetch volume leveller
        var lvlEnabled = _device.GetLevellerEnabled();
        var lvlAmount = _device.GetLevellerAmount();
        var lvlSpeed = _device.GetLevellerSpeed();
        var lvlMaxGain = _device.GetLevellerMaxGain();
        var lvlLookahead = _device.GetLevellerLookahead();
        var lvlGate = _device.GetLevellerGate();

        // Dispatch FiltersChanged to run after all filter updates are processed
        _dispatcher.TryEnqueue(() =>
        {
            FiltersChanged?.Invoke(this, EventArgs.Empty);
            UpdateDynamicChannelNames();
            NotifyHardwareConfigPropertiesChanged();

            if (lvlEnabled.HasValue) LevellerEnabled = lvlEnabled.Value;
            if (lvlAmount.HasValue) LevellerAmount = lvlAmount.Value;
            if (lvlSpeed.HasValue) LevellerSpeed = lvlSpeed.Value;
            if (lvlMaxGain.HasValue) LevellerMaxGainDb = lvlMaxGain.Value;
            if (lvlLookahead.HasValue) LevellerLookahead = lvlLookahead.Value;
            if (lvlGate.HasValue) LevellerGateDb = lvlGate.Value;

            // Signal listeners that bulk state is freshly synced.
            BulkRefreshed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void FetchStatus()
    {
        try
        {
            var status = _device.GetStatus();
            if (status != null)
            {
                // Clip latching: OR incoming clip flags into latched state
                ushort newBits = (ushort)(status.ClipFlags & ~_clipLatched);
                if (newBits != 0)
                {
                    _clipLatched |= status.ClipFlags;
                    _clipTimestamp = DateTime.UtcNow;
                }

                // Auto-clear after 10 seconds of no new clips
                if (_clipLatched != 0 && _clipTimestamp.HasValue &&
                    (DateTime.UtcNow - _clipTimestamp.Value).TotalSeconds > 10)
                {
                    _clipLatched = 0;
                    _clipTimestamp = null;
                    try { _device.ClearClips(); } catch { }
                }

                status.ClipLatched = _clipLatched;
                status.ClipTimestamp = _clipTimestamp;

                _dispatcher.TryEnqueue(() => Status = status);
            }
        }
        catch { }
    }

    public async Task<bool> SetFilter(int channel, int band, FilterParams p)
    {
        if (_channelData.TryGetValue(channel, out var filters) && band < filters.Count)
            filters[band] = p;
        var success = await Task.Run(() => _device.SetFilter(channel, band, p));

        // Mirror to linked master channel
        if (MasterPeqLinked && IsMasterChannel(channel))
        {
            int other = GetLinkedMasterChannel(channel);
            if (_channelData.TryGetValue(other, out var otherFilters) && band < otherFilters.Count)
                otherFilters[band] = p;
            await Task.Run(() => _device.SetFilter(other, band, p));
        }

        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();
        return success;
    }

    private CancellationTokenSource? _filterDebounceCts;

    /// <summary>
    /// Update filter locally and fire events immediately, deferring the USB send.
    /// Use for rapid interactive updates (e.g. scroll adjustments).
    /// </summary>
    public void SetFilterDeferred(int channel, int band, FilterParams p)
    {
        if (_channelData.TryGetValue(channel, out var filters) && band < filters.Count)
            filters[band] = p;

        // Mirror to linked master channel (local state)
        if (MasterPeqLinked && IsMasterChannel(channel))
        {
            int other = GetLinkedMasterChannel(channel);
            if (_channelData.TryGetValue(other, out var otherFilters) && band < otherFilters.Count)
                otherFilters[band] = p;
        }

        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();

        _filterDebounceCts?.Cancel();
        _filterDebounceCts = new CancellationTokenSource();
        var token = _filterDebounceCts.Token;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                _device.SetFilter(channel, band, p);
                if (MasterPeqLinked && IsMasterChannel(channel))
                    _device.SetFilter(GetLinkedMasterChannel(channel), band, p);
            }
            catch (TaskCanceledException) { }
        });
    }

    private static bool IsMasterChannel(int channelId) =>
        channelId == (int)ChannelId.MasterLeft || channelId == (int)ChannelId.MasterRight;

    private static int GetLinkedMasterChannel(int channelId) =>
        channelId == (int)ChannelId.MasterLeft ? (int)ChannelId.MasterRight : (int)ChannelId.MasterLeft;

    /// <summary>
    /// Returns true iff the Master L and Master R filter banks have at least
    /// one differing band. Used by the Link L/R toggle to decide whether the
    /// user must be prompted to choose a source channel before syncing.
    /// </summary>
    public bool MasterFiltersDiffer()
    {
        if (!_channelData.TryGetValue((int)ChannelId.MasterLeft, out var left)) return false;
        if (!_channelData.TryGetValue((int)ChannelId.MasterRight, out var right)) return false;
        if (left.Count != right.Count) return true;
        for (int i = 0; i < left.Count; i++)
            if (!left[i].Equals(right[i])) return true;
        return false;
    }

    /// <summary>
    /// Copy every filter band from <paramref name="sourceChannel"/> to its
    /// linked sibling and align the input preamp. Each per-band write goes
    /// through <see cref="SetFilterWithRetryRaw"/> so transient USB hiccups
    /// don't silently lose a band — and a persistent failure returns false
    /// so the caller can revert the Link toggle and surface an error.
    /// Local state for the destination channel is only updated after the
    /// device confirms each band, keeping local and device state in lockstep
    /// even on mid-loop failure.
    /// </summary>
    public async Task<bool> SyncMasterFilters(int sourceChannel)
    {
        int other = GetLinkedMasterChannel(sourceChannel);
        if (!_channelData.TryGetValue(sourceChannel, out var srcFilters)) return false;
        if (!_channelData.TryGetValue(other, out var dstFilters)) return false;

        for (int i = 0; i < srcFilters.Count; i++)
        {
            // Skip bands that already match — avoids 12 redundant USB writes
            // (and the resulting audio glitch from coefficient recalc) when
            // the user enables Link with the channels already in agreement,
            // and trims partial writes when only a subset of bands differ.
            if (i < dstFilters.Count && srcFilters[i].Equals(dstFilters[i]))
                continue;

            var p = srcFilters[i];
            if (!await SetFilterWithRetryRaw(other, i, p))
            {
                // Surface whatever local state we have to listeners so the UI
                // reflects the partial write before the caller error-handles.
                FiltersChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }
            if (i < dstFilters.Count) dstFilters[i] = p;
        }

        // Mirror the preamp from source to other so the two input channels are
        // fully aligned when link is turned on.
        if (sourceChannel == (int)ChannelId.MasterLeft)
            InputPreampRDb = InputPreampLDb;
        else
            InputPreampLDb = InputPreampRDb;

        FiltersChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Retry a raw device-level filter write up to 5 times. Bypasses
    /// <see cref="SetFilter"/>'s linked-channel mirror to avoid recursive
    /// double-writes when the caller is already iterating both channels.
    /// </summary>
    private async Task<bool> SetFilterWithRetryRaw(int channel, int band, FilterParams p)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (await Task.Run(() => _device.SetFilter(channel, band, p)))
                return true;
        }
        return false;
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
        ms = MathF.Round(ms, 4);
        _channelDelays[channel] = ms;
        int outputIndex = GetOutputIndex(channel);
        if (outputIndex >= 0)
            Task.Run(() => _device.SetOutputDelay(outputIndex, ms));
        OnPropertyChanged(nameof(ChannelDelays));
        if (outputIndex >= 0)
            MatrixOutputDelayChanged?.Invoke(outputIndex);
        CheckDirty();
    }

    private void FetchDelay(int channel)
    {
        int outputIndex = GetOutputIndex(channel);
        if (outputIndex < 0) return;
        var delay = _device.GetOutputDelay(outputIndex);
        if (delay.HasValue)
        {
            var current = _channelDelays.TryGetValue(channel, out var d) ? d : 0;
            if (Math.Abs(current - delay.Value) > 0.01f)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _channelDelays[channel] = delay.Value;
                    OnPropertyChanged(nameof(ChannelDelays));
                    MatrixOutputDelayChanged?.Invoke(outputIndex);
                });
            }
        }
    }

    public int GetOutputIndex(int channelId)
    {
        var outputs = ActiveOutputs;
        for (int i = 0; i < outputs.Count; i++)
            if ((int)outputs[i].Id == channelId) return i;
        return -1;
    }

    public void SetChannelGain(int channelId, float db)
    {
        db = MathF.Round(db, 2);
        _channelGains[channelId] = db;
        int outputIndex = GetOutputIndex(channelId);
        if (outputIndex < 0) return;
        Task.Run(() => _device.SetOutputGain(outputIndex, db));
        OnPropertyChanged(nameof(ChannelGains));
        MatrixOutputGainChanged?.Invoke(outputIndex);
        CheckDirty();
    }

    private void FetchChannelGain(int channelId)
    {
        int outputIndex = GetOutputIndex(channelId);
        if (outputIndex < 0) return;
        var gain = _device.GetOutputGain(outputIndex);
        if (gain.HasValue)
        {
            var current = _channelGains.TryGetValue(channelId, out var g) ? g : 0;
            if (Math.Abs(current - gain.Value) > 0.01f)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _channelGains[channelId] = gain.Value;
                    OnPropertyChanged(nameof(ChannelGains));
                    MatrixOutputGainChanged?.Invoke(outputIndex);
                    FiltersChanged?.Invoke(this, EventArgs.Empty);
                });
            }
        }
    }

    public void SetChannelMute(int channelId, bool muted)
    {
        _channelMutes[channelId] = muted;
        int outputIndex = GetOutputIndex(channelId);
        if (outputIndex < 0) return;
        Task.Run(() => _device.SetOutputMute(outputIndex, muted));
        OnPropertyChanged(nameof(ChannelMutes));
        CheckDirty();
    }

    public void CopyChannelParams(Channel channel)
    {
        var channelId = (int)channel.Id;
        var filters = _channelData.TryGetValue(channelId, out var f)
            ? f.Select(fp => fp.Clone()).ToList()
            : new List<FilterParams>();

        _channelClipboard = new ChannelClipboard
        {
            SourceIsOutput = channel.IsOutput,
            Filters = filters,
            Delay = channel.IsOutput && _channelDelays.TryGetValue(channelId, out var d) ? d : null,
            Gain = channel.IsOutput && _channelGains.TryGetValue(channelId, out var g) ? g : null,
            Mute = channel.IsOutput && _channelMutes.TryGetValue(channelId, out var m) ? m : null,
        };
    }

    public void SetAllFilters(int channelId, List<FilterParams> filters)
    {
        if (!_channelData.TryGetValue(channelId, out var existing)) return;

        var count = Math.Min(filters.Count, existing.Count);
        for (int i = 0; i < count; i++)
            existing[i] = filters[i];

        // Send all bands to device in one background task
        var snapshot = filters.Take(count).Select(fp => fp.Clone()).ToList();
        Task.Run(() =>
        {
            for (int i = 0; i < snapshot.Count; i++)
                _device.SetFilter(channelId, i, snapshot[i]);
        });

        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PasteChannelParams(Channel target)
    {
        if (_channelClipboard == null) return;

        var targetId = (int)target.Id;

        // Filters are universal — always paste (deep-cloned)
        SetAllFilters(targetId, _channelClipboard.Filters.Select(fp => fp.Clone()).ToList());

        // Delay, gain, mute: only paste when both source and target are outputs
        if (target.IsOutput && _channelClipboard.SourceIsOutput)
        {
            if (_channelClipboard.Delay.HasValue)
                SetDelay(targetId, _channelClipboard.Delay.Value);
            if (_channelClipboard.Gain.HasValue)
                SetChannelGain(targetId, _channelClipboard.Gain.Value);
            if (_channelClipboard.Mute.HasValue)
                SetChannelMute(targetId, _channelClipboard.Mute.Value);
        }

        CheckDirty();
    }

    private void FetchChannelMute(int channelId)
    {
        int outputIndex = GetOutputIndex(channelId);
        if (outputIndex < 0) return;
        var muted = _device.GetOutputMute(outputIndex);
        if (muted.HasValue)
        {
            var current = _channelMutes.TryGetValue(channelId, out var m) && m;
            if (current != muted.Value)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _channelMutes[channelId] = muted.Value;
                    OnPropertyChanged(nameof(ChannelMutes));
                });
            }
        }
    }

    private void FetchLoudness()
    {
        var enabled = _device.GetLoudnessEnabled();
        if (enabled.HasValue)
            _dispatcher.TryEnqueue(() => LoudnessEnabled = enabled.Value);

        var refSpl = _device.GetLoudnessRefSPL();
        if (refSpl.HasValue)
            _dispatcher.TryEnqueue(() => LoudnessRefSPL = refSpl.Value);

        var intensity = _device.GetLoudnessIntensity();
        if (intensity.HasValue)
            _dispatcher.TryEnqueue(() => LoudnessIntensity = intensity.Value);
    }

    private void FetchCrossfeed()
    {
        var enabled = _device.GetCrossfeedEnabled();
        if (enabled.HasValue)
            _dispatcher.TryEnqueue(() => CrossfeedEnabled = enabled.Value);

        var preset = _device.GetCrossfeedPreset();
        if (preset.HasValue)
            _dispatcher.TryEnqueue(() => CrossfeedPreset = preset.Value);

        var freq = _device.GetCrossfeedFreq();
        if (freq.HasValue)
            _dispatcher.TryEnqueue(() => CrossfeedFreq = freq.Value);

        var feed = _device.GetCrossfeedFeed();
        if (feed.HasValue)
            _dispatcher.TryEnqueue(() => CrossfeedFeed = feed.Value);

        var itd = _device.GetCrossfeedItd();
        if (itd.HasValue)
            _dispatcher.TryEnqueue(() => CrossfeedItd = itd.Value);
    }

    private void FetchMatrixRoutes()
    {
        var outputs = ActiveOutputs;
        for (int input = 0; input < 2; input++)
        {
            for (int o = 0; o < outputs.Count; o++)
            {
                var route = _device.GetMatrixRoute(input, o);
                if (route.HasValue)
                {
                    _matrixRouting[input, o] = route.Value.enabled;
                    _matrixInvert[input, o] = route.Value.invert;
                    _matrixGain[input, o] = route.Value.gain;
                }
            }
        }
    }

    private void FetchOutputEnable(int output)
    {
        var enabled = _device.GetOutputEnable(output);
        if (enabled.HasValue)
        {
            _outputEnabled[output] = enabled.Value;
            _dispatcher.TryEnqueue(() =>
            {
                OutputEnabledChanged?.Invoke(output, enabled.Value);
                VisibilityChanged?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    private void FetchOutputMuteState(int output)
    {
        if (output < 0 || output >= _outputMuted.Length) return;
        var muted = _device.GetOutputMute(output);
        if (muted.HasValue)
            _outputMuted[output] = muted.Value;
    }

    partial void OnLoudnessEnabledChanged(bool value)
    {
        Task.Run(() => _device.SetLoudnessEnabled(value));
        CheckDirty();
    }

    partial void OnLoudnessRefSPLChanged(float value)
    {
        Task.Run(() => _device.SetLoudnessRefSPL(value));
        CheckDirty();
    }

    partial void OnLoudnessIntensityChanged(float value)
    {
        Task.Run(() => _device.SetLoudnessIntensity(value));
        CheckDirty();
    }

    partial void OnCrossfeedEnabledChanged(bool value)
    {
        Task.Run(() => _device.SetCrossfeedEnabled(value));
        CheckDirty();
    }

    partial void OnCrossfeedPresetChanged(int value)
    {
        Task.Run(() => _device.SetCrossfeedPreset(value));
        CheckDirty();
    }

    partial void OnCrossfeedFreqChanged(float value)
    {
        Task.Run(() => _device.SetCrossfeedFreq(value));
        CheckDirty();
    }

    partial void OnCrossfeedFeedChanged(float value)
    {
        Task.Run(() => _device.SetCrossfeedFeed(value));
        CheckDirty();
    }

    partial void OnCrossfeedItdChanged(bool value)
    {
        Task.Run(() => _device.SetCrossfeedItd(value));
        CheckDirty();
    }

    // Volume leveller change handlers
    partial void OnLevellerEnabledChanged(bool value)
    {
        Task.Run(() => _device.SetLevellerEnabled(value));
        CheckDirty();
    }

    partial void OnLevellerAmountChanged(float value)
    {
        Task.Run(() => _device.SetLevellerAmount(value));
        CheckDirty();
    }

    partial void OnLevellerSpeedChanged(int value)
    {
        Task.Run(() => _device.SetLevellerSpeed(value));
        CheckDirty();
    }

    partial void OnLevellerMaxGainDbChanged(float value)
    {
        Task.Run(() => _device.SetLevellerMaxGain(value));
        CheckDirty();
    }

    partial void OnLevellerLookaheadChanged(bool value)
    {
        Task.Run(() => _device.SetLevellerLookahead(value));
        CheckDirty();
    }

    partial void OnLevellerGateDbChanged(float value)
    {
        Task.Run(() => _device.SetLevellerGate(value));
        CheckDirty();
    }

    partial void OnInputPreampLDbChanged(float value)
    {
        var rounded = MathF.Round(value, 1);
        Task.Run(() => _device.SetInputPreamp(0, rounded));
        if (MasterPeqLinked && Math.Abs(InputPreampRDb - rounded) > 0.05f)
            InputPreampRDb = rounded;
        CheckDirty();
    }

    partial void OnInputPreampRDbChanged(float value)
    {
        var rounded = MathF.Round(value, 1);
        Task.Run(() => _device.SetInputPreamp(1, rounded));
        if (MasterPeqLinked && Math.Abs(InputPreampLDb - rounded) > 0.05f)
            InputPreampLDb = rounded;
        CheckDirty();
    }

    partial void OnMasterVolumeDbChanged(float value)
    {
        var send = value <= -127.5f ? -128f : MathF.Round(value, 1);
        Task.Run(() => _device.SetMasterVolume(send));
        CheckDirty();
    }

    partial void OnUserVolumeDbChanged(float value)
    {
        // Skip the firmware write when the value originated from the device
        // itself (UAC1 host change, GPIO knob, etc.) — the sidebar slider only
        // transmits when the user is dragging it directly. CheckDirty still
        // runs so the change still counts toward preset-dirty bookkeeping.
        if (!_suppressUserVolumeSend)
        {
            // User volume range is [-60, 0]; firmware clamps but rounding here
            // keeps the dB readout stable and matches MasterVolumeDb's precision.
            var send = MathF.Round(Math.Clamp(value, -60f, 0f), 1);
            Task.Run(() => _device.SetUserVolume(send));
        }
        CheckDirty();
    }

    partial void OnLgSoundSyncEnabledChanged(bool value)
    {
        Task.Run(() => _device.SetLgSoundSyncEnabled(value));
        CheckDirty();
    }

    private bool FetchInputPreamps()
    {
        var l = _device.GetInputPreamp(0);
        var r = _device.GetInputPreamp(1);
        if (l.HasValue && r.HasValue)
        {
            if (Math.Abs(InputPreampLDb - l.Value) > 0.1f)
                _dispatcher.TryEnqueue(() => InputPreampLDb = l.Value);
            if (Math.Abs(InputPreampRDb - r.Value) > 0.1f)
                _dispatcher.TryEnqueue(() => InputPreampRDb = r.Value);
            return true;
        }
        // Fallback to legacy uniform preamp for pre-V6 firmware
        var legacy = _device.GetPreamp();
        if (legacy.HasValue)
        {
            _dispatcher.TryEnqueue(() =>
            {
                InputPreampLDb = legacy.Value;
                InputPreampRDb = legacy.Value;
            });
            return true;
        }
        _dispatcher.TryEnqueue(() => IsDeviceConnected = false);
        return false;
    }

    private void FetchMasterVolume()
    {
        var mv = _device.GetMasterVolume();
        if (mv.HasValue && Math.Abs(MasterVolumeDb - mv.Value) > 0.1f)
            _dispatcher.TryEnqueue(() => MasterVolumeDb = mv.Value);
    }

    /// <summary>
    /// Fetch the vendor-channel user volume (V9+). STALL on older firmware
    /// leaves <see cref="UserVolumeDb"/> at its default (0 dB), which is also
    /// the firmware's boot value — safe to display until the user changes it.
    /// </summary>
    private void FetchUserVolume()
    {
        var uv = _device.GetUserVolume();
        if (uv.HasValue && Math.Abs(UserVolumeDb - uv.Value) > 0.1f)
            _dispatcher.TryEnqueue(() => UserVolumeDb = uv.Value);
    }

    partial void OnBypassChanged(bool value)
    {
        Task.Run(() => _device.SetBypass(value));
        BypassChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();
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
    private async Task ClearAllMaster()
    {
        var defaultFilter = new FilterParams(FilterType.Flat, 1000, 0.707f, 0);
        var masterChannels = new[] { (int)ChannelId.MasterLeft, (int)ChannelId.MasterRight };

        foreach (var ch in masterChannels)
        {
            if (_channelData.TryGetValue(ch, out var filters))
            {
                for (int b = 0; b < filters.Count; b++)
                {
                    await SetFilter(ch, b, defaultFilter.Clone());
                }
            }
        }
    }

    [RelayCommand]
    private void Reconnect()
    {
        Task.Run(() => _device.Reconnect());
    }

    /// <summary>
    /// Save current parameters to device flash.
    /// </summary>
    public async Task<byte> SaveParams()
    {
        if (!IsDeviceConnected) return FlashResult.ErrWrite;
        return await Task.Run(() =>
        {
            var result = _device.SaveParams();
            if (result == FlashResult.Ok)
                _dispatcher.TryEnqueue(() => { PresetsDirty = false; UpdateSavedSnapshot(); });
            return result;
        });
    }

    /// <summary>
    /// Load parameters from device flash, refreshing UI.
    /// </summary>
    public async Task<byte> LoadParams()
    {
        if (!IsDeviceConnected) return FlashResult.ErrWrite;
        return await Task.Run(() =>
        {
            var result = _device.LoadParams();
            if (result == FlashResult.Ok)
            {
                _suppressDirtyCheck = true;
                FetchAll();
                _dispatcher.TryEnqueue(() =>
                {
                    UpdateSavedSnapshot();
                    PresetsDirty = false;
                    _suppressDirtyCheck = false;
                });
            }
            return result;
        });
    }

    /// <summary>
    /// Reset all parameters to factory defaults, refreshing UI.
    /// </summary>
    public async Task<byte> FactoryResetParams()
    {
        if (!IsDeviceConnected) return FlashResult.ErrWrite;
        return await Task.Run(() =>
        {
            var result = _device.FactoryReset();
            if (result == FlashResult.Ok)
            {
                _suppressDirtyCheck = true;
                FetchAll();
                _dispatcher.TryEnqueue(() =>
                {
                    UpdateSavedSnapshot();
                    PresetsDirty = false;
                    _suppressDirtyCheck = false;
                });
            }
            return result;
        });
    }

    #endregion

    #region Preset Operations

    /// <summary>
    /// Fetch preset metadata from the device: occupied mask, names, active slot, startup info.
    /// Called after FetchAll() in the connect flow.
    /// </summary>
    public void FetchPresetInfo()
    {
        try
        {
            _presetsChecked = true;

            // Fetch directory (occupied mask, startup config, active slot, include-pins)
            var dir = _device.GetPresetDirectory();
            if (dir != null)
            {
                _presetOccupiedMask = dir.Value.OccupiedMask;
                _presetStartupMode = dir.Value.StartupMode;
                _presetDefaultSlot = dir.Value.DefaultSlot;
                var lastActive = dir.Value.LastActiveSlot;
                _presetIncludePins = dir.Value.IncludePins;
                _masterVolumeMode = dir.Value.MasterVolumeMode;

                // Use firmware's active slot if valid and occupied, otherwise default to slot 0
                _activePresetSlot = (lastActive < PresetSlotCount && (_presetOccupiedMask & (1 << lastActive)) != 0)
                    ? lastActive : 0;
            }

            // Fetch names for all slots unconditionally (matching macOS). The occupied
            // mask and the name query are independent on the device — don't gate one on
            // the other, or a transient directory failure would leave names blank.
            for (int i = 0; i < PresetSlotCount; i++)
            {
                var name = _device.GetPresetName(i);
                _presetNames[i] = name ?? "";
            }

            _dispatcher.TryEnqueue(() =>
            {
                ActivePreset = _activePresetSlot;
                PresetsDirty = false;
                PresetsChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch { }
    }

    /// <summary>
    /// Probe the firmware for input-switching support (V7+) and pick up the
    /// current source. STALL on 0xE1 (older firmware) leaves the feature off
    /// and the UI hides the dropdown.
    /// </summary>
    public void FetchInputSource()
    {
        try
        {
            var src = _device.GetInputSource();
            _dispatcher.TryEnqueue(() =>
            {
                if (src.HasValue)
                {
                    InputSourceSupported = true;
                    ActiveInputSource = src.Value;
                }
                else
                {
                    InputSourceSupported = false;
                }
                InputSourceChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch
        {
            _dispatcher.TryEnqueue(() =>
            {
                InputSourceSupported = false;
                InputSourceChanged?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    /// <summary>
    /// Probe firmware support for per-band bypass (0xD8/0xD9, firmware 1.1.4+).
    /// Older firmware STALLs on GetBandBypass — we set the flag based on whether
    /// the (ch=0, band=0) query returns a value. Bulk-params parsing always reads
    /// the bypass byte regardless; this flag only gates the UI toggle and the
    /// dedicated single-byte set opcode.
    /// </summary>
    public void FetchBandBypassCapability()
    {
        try
        {
            var supported = _device.GetBandBypass(0, 0).HasValue;
            _dispatcher.TryEnqueue(() => BandBypassSupported = supported);
        }
        catch
        {
            _dispatcher.TryEnqueue(() => BandBypassSupported = false);
        }
    }

    /// <summary>
    /// Probe firmware support for external DAC hardware mute (V10+, opcodes
    /// 0xEA/0xEB/0xEC) and pull the live config. Older firmware STALLs the
    /// GET so <see cref="DspDevice.GetDacHwMute"/> returns null — we treat
    /// that as "feature unsupported" and the Settings UI surfaces an inline
    /// notice instead of empty controls. On success, <see cref="DacHwMute"/>
    /// is set to the firmware-current value and <see cref="DacHwMuteSupported"/>
    /// is true.
    /// </summary>
    public void FetchDacHwMute()
    {
        try
        {
            var cfg = _device.GetDacHwMute();
            _dispatcher.TryEnqueue(() =>
            {
                if (cfg != null)
                {
                    DacHwMuteSupported = true;
                    if (!cfg.Equals(DacHwMute)) DacHwMute = cfg;
                }
                else
                {
                    DacHwMuteSupported = false;
                }
            });
        }
        catch
        {
            _dispatcher.TryEnqueue(() => DacHwMuteSupported = false);
        }
    }

    /// <summary>
    /// Probe firmware support for LG Sound Sync (V8+, opcodes 0xE6/0xE7) and
    /// pull the live enable state. Older firmware STALLs the GET so
    /// <see cref="DspDevice.GetLgSoundSyncEnabled"/> returns null — we treat
    /// that as "feature unsupported" and the Settings UI hides the toggle.
    /// </summary>
    public void FetchLgSoundSync()
    {
        try
        {
            var enabled = _device.GetLgSoundSyncEnabled();
            _dispatcher.TryEnqueue(() =>
            {
                if (enabled.HasValue)
                {
                    LgSoundSyncSupported = true;
                    if (LgSoundSyncEnabled != enabled.Value)
                        LgSoundSyncEnabled = enabled.Value;
                }
                else
                {
                    LgSoundSyncSupported = false;
                }
            });
        }
        catch
        {
            _dispatcher.TryEnqueue(() => LgSoundSyncSupported = false);
        }
    }

    /// <summary>
    /// Push a new <see cref="DacHwMuteConfig"/> to the device and optimistically
    /// update the local property. The firmware SET is fire-and-forget (see
    /// <see cref="DspDevice.SetDacHwMute"/>'s remarks), so a validation
    /// rejection won't immediately surface as a failure — it surfaces on the
    /// next bulk re-fetch when our cached value diverges from firmware reality.
    /// Returns the config we attempted to write (echoed for chaining), or null
    /// if the USB transfer failed outright.
    /// </summary>
    public async Task<DacHwMuteConfig?> ApplyDacHwMuteAsync(DacHwMuteConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // Local update first so the UI doesn't snap back during the USB
        // round-trip. If the transfer fails the next FetchDacHwMute will reset
        // it; if the firmware silently rejects, the next bulk refresh wins.
        if (!config.Equals(DacHwMute)) DacHwMute = config;
        var ok = await Task.Run(() => _device.SetDacHwMute(config));
        CheckDirty();
        return ok ? config : null;
    }

    /// <summary>
    /// Fire the firmware's installer-verification test pulse (~1 s mute).
    /// Returns the firmware status byte: 0 = queued, non-zero = rejected
    /// (feature disabled or no pin), 0xFF = USB transfer failure.
    /// </summary>
    public Task<byte> TestDacHwMuteAsync() =>
        Task.Run(() => _device.TestDacHwMute());

    /// <summary>
    /// Toggle the bypass flag on a single EQ band. Updates local cache, sends
    /// the cheap REQ_SET_BAND_BYPASS opcode (0xD8) so freq/Q/gain are preserved
    /// on the firmware side, mirrors to the linked master channel if applicable,
    /// and fires FiltersChanged so the response curve redraws.
    /// </summary>
    public async Task<bool> SetBandBypass(int channel, int band, bool bypass)
    {
        if (_channelData.TryGetValue(channel, out var filters) && band < filters.Count)
            filters[band].Bypass = bypass;

        var success = await Task.Run(() => _device.SetBandBypass(channel, band, bypass));

        if (MasterPeqLinked && IsMasterChannel(channel))
        {
            int other = GetLinkedMasterChannel(channel);
            if (_channelData.TryGetValue(other, out var otherFilters) && band < otherFilters.Count)
                otherFilters[band].Bypass = bypass;
            await Task.Run(() => _device.SetBandBypass(other, band, bypass));
        }

        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();
        return success;
    }

    /// <summary>
    /// Switch the active input source. Non-blocking on the firmware side —
    /// the host transfer returns immediately; the actual hardware switch is
    /// deferred and audible mute period is ~5–80 ms (USB→S/PDIF can be longer
    /// while the receiver is acquiring lock).
    /// </summary>
    public Task SetInputSourceAsync(InputSource source)
    {
        if (!IsDeviceConnected || !InputSourceSupported) return Task.CompletedTask;
        return Task.Run(() =>
        {
            _device.SetInputSource(source);
            // Do NOT preemptively assign ActiveInputSource here — let the
            // firmware's InputSourceNotified callback drive that update once
            // the apply lands. The notify path refetches user_volume so the
            // sidebar slider lands at the right value in a single dispatcher
            // tick. The source dropdown is already showing the user's choice
            // from the click itself, so there's no UX cost to waiting.
            _dispatcher.TryEnqueue(() =>
            {
                InputSourceChanged?.Invoke(this, EventArgs.Empty);
                CheckDirty();
            });
        });
    }

    /// <summary>
    /// Save current parameters to a preset slot, optionally setting a name.
    /// </summary>
    /// <summary>
    /// Write the current configuration to another preset slot without changing
    /// the active preset. Does not update PresetsDirty or the saved snapshot.
    /// </summary>
    public async Task<byte> CopyToPreset(int slot, string? name)
    {
        if (!IsDeviceConnected) return PresetResult.FlashWriteError;
        return await Task.Run(() =>
        {
            if (!string.IsNullOrEmpty(name))
                _device.SetPresetName(slot, name);

            var result = _device.SavePreset(slot);
            if (result == PresetResult.Ok)
            {
                _presetOccupiedMask |= (ushort)(1 << slot);
                if (!string.IsNullOrEmpty(name))
                    _presetNames[slot] = name;
                _dispatcher.TryEnqueue(() => PresetsChanged?.Invoke(this, EventArgs.Empty));
            }
            return result;
        });
    }

    public async Task<byte> SavePreset(int slot, string? name)
    {
        if (!IsDeviceConnected) return PresetResult.FlashWriteError;
        return await Task.Run(() =>
        {
            if (!string.IsNullOrEmpty(name))
                _device.SetPresetName(slot, name);

            var result = _device.SavePreset(slot);
            if (result == PresetResult.Ok)
            {
                // Firmware defers both REQ_PRESET_SET_NAME and REQ_PRESET_SAVE
                // flash writes to its main loop. Reading the directory or names
                // back immediately (via GetPresetDirectory / GetPresetName) races
                // those deferred writes and returns stale data — which would
                // overwrite the name we just set. Update local state
                // optimistically instead, matching the macOS app's behavior.
                _presetOccupiedMask |= (ushort)(1 << slot);
                _activePresetSlot = slot;
                if (!string.IsNullOrEmpty(name))
                    _presetNames[slot] = name;

                _dispatcher.TryEnqueue(() =>
                {
                    ActivePreset = slot;
                    PresetsDirty = false;
                    UpdateSavedSnapshot();
                    PresetsChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            return result;
        });
    }

    /// <summary>
    /// Load a preset slot, resync all parameters from device.
    /// </summary>
    public async Task<byte> LoadPreset(int slot)
    {
        if (!IsDeviceConnected) return PresetResult.FlashWriteError;
        return await Task.Run(() =>
        {
            var result = _device.LoadPreset(slot);
            if (result == PresetResult.Ok)
            {
                // Advance active-slot state before resyncing so property-change
                // listeners triggered by FetchAll see the new slot immediately.
                _activePresetSlot = slot;
                _dispatcher.TryEnqueue(() => ActivePreset = slot);

                // Wait for firmware to finish copying preset from flash to RAM
                System.Threading.Thread.Sleep(100);
                _suppressDirtyCheck = true;
                FetchAll();

                _dispatcher.TryEnqueue(() =>
                {
                    UpdateSavedSnapshot();
                    PresetsDirty = false;
                    _suppressDirtyCheck = false;
                    PresetsChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            return result;
        });
    }

    /// <summary>
    /// Delete a preset slot.
    /// </summary>
    public async Task<byte> DeletePreset(int slot)
    {
        if (!IsDeviceConnected) return PresetResult.FlashWriteError;
        return await Task.Run(() =>
        {
            var result = _device.DeletePreset(slot);
            if (result == PresetResult.Ok)
            {
                // Firmware defers the delete to its main loop, so reading back
                // the directory immediately is racy. Update local state
                // optimistically: clear the occupied bit and name.
                _presetOccupiedMask &= (ushort)~(1 << slot);
                _presetNames[slot] = "";

                // Firmware factory-resets live state when the active slot is
                // deleted. Mirror that locally so the UI reflects the actual
                // device state and the saved snapshot isn't stale.
                if (slot == _activePresetSlot)
                {
                    // Firmware defers the flash erase + live-state reset to its
                    // main loop (~45ms). Wait before fetching so we don't read
                    // back the old parameters.
                    System.Threading.Thread.Sleep(50);
                    _suppressDirtyCheck = true;
                    FetchAll();
                    _dispatcher.TryEnqueue(() =>
                    {
                        UpdateSavedSnapshot();
                        PresetsDirty = false;
                        _suppressDirtyCheck = false;
                        PresetsChanged?.Invoke(this, EventArgs.Empty);
                    });
                }
                else
                {
                    _dispatcher.TryEnqueue(() => PresetsChanged?.Invoke(this, EventArgs.Empty));
                }
            }
            return result;
        });
    }

    /// <summary>
    /// Rename a preset slot.
    /// </summary>
    public async Task<bool> RenamePreset(int slot, string name)
    {
        if (!IsDeviceConnected) return false;
        return await Task.Run(() =>
        {
            var ok = _device.SetPresetName(slot, name);
            if (ok)
            {
                _presetNames[slot] = name;
                _dispatcher.TryEnqueue(() => PresetsChanged?.Invoke(this, EventArgs.Empty));
            }
            return ok;
        });
    }

    /// <summary>
    /// Clear all presets from flash.
    /// </summary>
    public async Task<byte> ClearAllPresets()
    {
        if (!IsDeviceConnected) return PresetResult.FlashWriteError;
        return await Task.Run(() =>
        {
            var result = _device.ClearAllPresets();
            if (result == PresetResult.Ok)
            {
                _presetOccupiedMask = 0;
                _activePresetSlot = 0;
                for (int i = 0; i < PresetSlotCount; i++)
                    _presetNames[i] = "";
                _dispatcher.TryEnqueue(() =>
                {
                    ActivePreset = 0;
                    PresetsDirty = false;
                    PresetsChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            return result;
        });
    }

    public async Task<bool> SetPresetStartup(byte mode, byte defaultSlot)
    {
        if (!IsDeviceConnected) return false;
        return await Task.Run(() =>
        {
            var ok = _device.SetPresetStartup(mode, defaultSlot);
            if (ok)
            {
                _presetStartupMode = mode;
                _presetDefaultSlot = defaultSlot;
            }
            return ok;
        });
    }

    public async Task<bool> SetPresetIncludePins(bool include)
    {
        if (!IsDeviceConnected) return false;
        return await Task.Run(() =>
        {
            var ok = _device.SetPresetIncludePins(include);
            if (ok) _presetIncludePins = include;
            return ok;
        });
    }

    /// <summary>
    /// Set master volume persistence mode. 0 = independent, 1 = with preset.
    /// </summary>
    public async Task<bool> SetMasterVolumeMode(byte mode)
    {
        if (!IsDeviceConnected) return false;
        return await Task.Run(() =>
        {
            var ok = _device.SetMasterVolumeMode(mode);
            if (ok)
            {
                _masterVolumeMode = mode;
                // Mode flips which diff applies. Re-check dirty and notify
                // listeners so the "Save Master Volume" item can update its
                // enabled state.
                _dispatcher.TryEnqueue(() =>
                {
                    OnPropertyChanged(nameof(MasterVolumeMode));
                    CheckDirty();
                });
            }
            return ok;
        });
    }

    /// <summary>
    /// Persist the current live master volume to the directory sector. Returns
    /// the firmware status byte (PRESET_OK == 0 on acceptance). Accepted in
    /// both modes; dormant in mode 1 until the user switches to mode 0.
    /// </summary>
    public async Task<byte> SaveMasterVolume()
    {
        if (!IsDeviceConnected) return 0xFF;
        return await Task.Run(() => _device.SaveMasterVolume());
    }

    /// <summary>
    /// Capture the current state as the "saved" baseline for change detection.
    /// </summary>
    public PresetSnapshot? SavedSnapshot => _savedSnapshot;

    public void UpdateSavedSnapshot()
    {
        _savedSnapshot = PresetSnapshot.Capture(this);
    }

    /// <summary>
    /// Fan out PropertyChanged for every I²S/SPDIF-related property the
    /// Settings UI subscribes to. Used after silent bulk field updates
    /// (bulk-params parse, BulkRefresh) so pages refresh their combos.
    /// Idempotent — re-notifying a property whose value didn't change
    /// is cheap.
    /// </summary>
    private void NotifyHardwareConfigPropertiesChanged()
    {
        OnPropertyChanged(nameof(I2SBckPin));
        OnPropertyChanged(nameof(MckPin));
        OnPropertyChanged(nameof(MckEnabled));
        OnPropertyChanged(nameof(MckMultiplier));
        OnPropertyChanged(nameof(AnySlotIsI2S));
        OnPropertyChanged(nameof(SpdifRxPin));
        OnPropertyChanged(nameof(SampleRateHz));
    }

    /// <summary>
    /// Recompute PresetsDirty by comparing current state against the saved snapshot.
    /// </summary>
    private void CheckDirty()
    {
        if (_suppressDirtyCheck) return;
        if (_savedSnapshot == null) return;
        var current = PresetSnapshot.Capture(this);
        var changes = PresetDiff.Diff(_savedSnapshot, current, this);
        PresetsDirty = changes.Count > 0;
    }

    /// <summary>
    /// Get a human-readable summary of changes since the last save/load.
    /// Returns null if no snapshot is available or no changes detected.
    /// </summary>
    public string? GetChangeSummary()
    {
        if (_savedSnapshot == null) return null;
        var current = PresetSnapshot.Capture(this);
        var changes = PresetDiff.Diff(_savedSnapshot, current, this);
        return changes.Count > 0 ? PresetDiff.FormatSummary(changes) : null;
    }

    /// <summary>
    /// Whether the device firmware supports presets (GetPresetDirectory succeeded).
    /// </summary>
    public bool PresetsSupported => _presetsChecked;
    private bool _presetsChecked;

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
                freqs[i] = MathF.Pow(10, MathF.Log10(10) + pct * (MathF.Log10(20000) - MathF.Log10(10)));
                mags[i] = 0;
            }
            return (freqs, mags);
        }

        var result = DspMath.GenerateResponseCurve(filters);

        // Apply output channel gain offset to the curve
        if (channel.IsOutput)
        {
            float gain = GetChannelGain(channel);
            if (MathF.Abs(gain) > 0.001f)
            {
                for (int i = 0; i < result.magnitudes.Length; i++)
                    result.magnitudes[i] += gain;
            }
        }

        return result;
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
