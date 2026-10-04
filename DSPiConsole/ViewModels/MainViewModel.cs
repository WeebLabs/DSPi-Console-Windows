using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DSPiConsole.Core;
using DSPiConsole.Core.Models;
using DSPiConsole.Core.Rta;
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
    private int _audioPollCounter;   // throttles the Windows USB-format re-poll
    private bool _disposed;

    // Channel filter data: Dictionary<ChannelId, List<FilterParams>>
    private readonly Dictionary<int, ObservableCollection<FilterParams>> _channelData = new();

    // Per-output crossover bands (firmware V11+). 4 bands per OUTPUT channel,
    // addressed on the wire as band CrossoverFilter.XoverBandBase + localBand
    // (20..23). Only output channels get an entry — crossover is meaningless on
    // the master/input channels and the firmware rejects it there.
    private readonly Dictionary<int, ObservableCollection<FilterParams>> _xoverData = new();


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

    // Matrix mixer state: [inputIndex, outputIndex]. Sized to the wire maximums
    // (8 inputs × 9 outputs); the UI only surfaces the active subset.
    private readonly bool[,] _matrixRouting = new bool[MatrixMaxInputs, MatrixMaxOutputs];
    private readonly float[,] _matrixGain = new float[MatrixMaxInputs, MatrixMaxOutputs];
    private readonly bool[,] _matrixInvert = new bool[MatrixMaxInputs, MatrixMaxOutputs];
    public const int MatrixMaxInputs = 8;
    public const int MatrixMaxOutputs = 9;

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
    private byte _i2sRxPin = 4;       // firmware default (PICO_I2S_RX_PIN_DEFAULT)
    private uint _i2sInputRateHz = 48000; // selected I2S-input master rate

    // Multiple SPDIF inputs (firmware v1.1.5+). Several selectable inputs share
    // one receiver; input 0 (_spdifRxPin) is always enabled. Ext arrays cover
    // inputs 1..3 (SPDIF2/3/4). _spdifEnabledExt is the enable mask (bit0 =
    // SPDIF2 .. bit2 = SPDIF4). How many inputs exist is the device's answer,
    // not ours: firmware carried 3 before wire V28 and 4 from V28, so
    // SpdifInputCount tracks REQ_GET_SPDIF_INPUT_CONFIG and SpdifRxNumInputs is
    // only the ceiling this client can represent.
    private readonly byte[] _spdifRxPinsExt = { 20, 21, 22 }; // SPDIF2/3/4 pin defaults
    private byte _spdifEnabledExt;                            // bit0=SPDIF2 .. bit2=SPDIF4
    public const int SpdifRxNumInputs = DspDevice.MaxSpdifInputs;
    private int _spdifInputCount = 3;   // until 0xEF answers

    // Multichannel I2S input (RP2350). N channels use N/2 stereo pairs; pair 0
    // uses _i2sRxPin, pairs 1..3 use the ext pins.
    private byte _i2sInputChannels = 2;
    private readonly byte[] _i2sRxPinsExt = { 2, 3, 4 };  // pair 1,2,3 pin defaults

    // Clip tracking
    private uint _clipLatched;
    private DateTime? _clipTimestamp;

    // Preset system state
    private int _activePresetSlot = -1;
    private ushort _presetOccupiedMask;
    private readonly string[] _presetNames = new string[10];
    private byte _presetStartupMode;
    private byte _presetDefaultSlot;
    // Output-config persistence mode (output_config_independent_load_spec.md).
    //   0 = OUTPUT_CONFIG_MODE_INDEPENDENT — IO (output pins/types, I2S
    //       MCK/BCK, SPDIF RX pin) is device-global, applied at boot only,
    //       persisted via SaveOutputConfig (0x52).
    //   1 = OUTPUT_CONFIG_MODE_WITH_PRESET — IO travels with each preset.
    // Repurposed from the former `include_pins` flag (same opcode, same
    // 1:1 mapping; default stays with-preset so existing devices are unaffected).
    private byte _outputConfigMode = 1;
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

    // Non-zero while values read from the device (or reset locally on
    // disconnect) are being applied, so the preamp, master volume and bypass
    // setters neither write them back nor mirror across a linked pair. A
    // write from a reset can otherwise land on the next device opened, and a
    // linked-pair mirror would copy one side over the other on the device.
    // UI-thread only.
    private int _applyingDeviceState;

    private void ApplyFromDevice(Action apply)
    {
        _applyingDeviceState++;
        try { apply(); }
        finally { _applyingDeviceState--; }
    }

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

    // ── Multichannel DSP masks (firmware V18/V19/V20) ──
    // The mask int is the authoritative bit set the UI mutates and re-sends in
    // full on each change (there is no incremental per-bit wire protocol).
    // Leveller masks span input channels (bit k = input k); loudness spans
    // output channels (bit k = output k); crossfeed spans output pairs (bit p =
    // outputs 2p/2p+1). The *Supported flags gate each window's selector on the
    // firmware wire version (and, for the leveller, on having >2 inputs).
    [ObservableProperty]
    private int _levellerDetectorMask = 0xFF;

    [ObservableProperty]
    private int _levellerApplyMask = 0xFF;

    [ObservableProperty]
    private bool _levellerMasksSupported;

    [ObservableProperty]
    private int _loudnessOutputMask = 0xFFFF;

    [ObservableProperty]
    private bool _loudnessMaskSupported;

    [ObservableProperty]
    private int _crossfeedOutputPairMask = 0x01;

    [ObservableProperty]
    private bool _crossfeedMaskSupported;

    [ObservableProperty]
    private int _activePreset = -1;

    [ObservableProperty]
    private bool _presetsDirty;

    // True when the physical IO block has unsaved changes in INDEPENDENT output-
    // config mode — i.e. edits that won't ride with a preset and need an explicit
    // "Save Output Config" (0x52) to persist. Drives the in-window save prompt.
    // Always false in with-preset mode (those edits mark PresetsDirty instead).
    [ObservableProperty]
    private bool _outputConfigDirty;

    [ObservableProperty]
    private bool _masterPeqLinked;

    [ObservableProperty]
    private string _platform = "";

    // Input source (V7+ firmware). InputSourceSupported stays false until
    // GetInputSource succeeds at least once — older firmware STALLs on 0xE1.
    [ObservableProperty]
    private bool _inputSourceSupported;

    // I2S input (firmware V12+). True when the bulk packet carries the I2S input
    // fields (format_version >= 12). Gates the I2S item in the Source dropdown
    // and the input half of the I2S settings page.
    [ObservableProperty]
    private bool _inputI2sSupported;

    // Multiple selectable SPDIF inputs (firmware v1.1.5+). True when the device
    // answers REQ_GET_SPDIF_INPUT_CONFIG (0xEF) / the bulk enable-mask field is
    // present. Gates the SPDIF "Instances" selector; when false the S/PDIF page
    // shows a single RX pin as before.
    [ObservableProperty]
    private bool _multiSpdifSupported;

    // ADAT "bulk" optical output (V17+, RP2350 only). True when the connected
    // device is an RP2350 and the bulk blob carries the ADAT section. Gates the
    // Bulk Output settings page.
    [ObservableProperty]
    private bool _adatSupported;

    // Onboard test-signal generator. SiggenSupported is set true when the caps
    // probe (0xA8) answers; older firmware STALLs. SiggenStatus mirrors the live
    // generator state for the Test Signals window's transport/status UI.
    [ObservableProperty]
    private bool _siggenSupported;

    [ObservableProperty]
    private SiggenStatus? _siggenStatus;

    // Per-band bypass (firmware 1.1.4+). Mirrors the InputSource pattern: probe
    // once at connect via REQ_GET_BAND_BYPASS (0xD9); older firmware STALLs and
    // the UI hides the bypass toggle. See band_bypass_spec.md §8.
    [ObservableProperty]
    private bool _bandBypassSupported;

    // Per-output crossover filters (firmware V11+, wire format V11). Set true
    // when a bulk fetch returns the crossover section (BulkParams.HasCrossover).
    // Gates the PEQ/XO tab in the channel editor.
    [ObservableProperty]
    private bool _crossoverSupported;

    // Linkwitz Transform PEQ type (filter type 11, wire V22+). Gates the LT entry
    // in the PEQ type picker (output channels only — it's a driver/sealed-box
    // bass-extension tool that only makes sense on outputs feeding speakers).
    [ObservableProperty]
    private bool _linkwitzTransformSupported;

    // First-order low/high pass (FilterType.LowPass1 / HighPass1, the 6 dB/oct
    // cuts). An enum-only firmware addition that never bumped the wire format;
    // V27 is ambiguous (bumped on main while the filter branch was in flight),
    // so V28 is the first version that unambiguously carries them. Matches the
    // macOS Console's firmwareSupportsFirstOrderPass.
    [ObservableProperty]
    private bool _firstOrderPassSupported;

    /// <summary>True when the connected firmware can represent
    /// <paramref name="type"/>. Paths that write bands the user didn't pick one
    /// at a time (imports, on-graph shape changes) check this first, since an
    /// unrecognised type byte is misread by the firmware rather than ignored.
    /// Bulk sync needs V16+, which already carries notch, all-pass and the
    /// first-order all-pass/shelves (1.1.4 / V13 / V14).</summary>
    public bool FilterTypeSupported(FilterType type) => type switch
    {
        FilterType.LowPass1 or FilterType.HighPass1 => FirstOrderPassSupported,
        FilterType.LinkwitzTransform => LinkwitzTransformSupported,
        _ => !type.IsCrossover() || CrossoverSupported,
    };

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

    // The source the user last picked, until the firmware's notification for
    // it arrives (see InputSourceNotified). UI-thread only.
    private InputSource? _requestedInputSource;

    // Bumped on the notify thread for every input-source notification, so a
    // bulk read that overlapped one knows its input source is older than the
    // notification's and leaves ActiveInputSource alone.
    private int _inputSourceNotifySeq;

    // A preset load or factory reset sets a new input source, but the firmware
    // applies it in its main loop after the fade and DAC mute hold (500 ms at
    // most), which can land after the refetch that took the saved baseline.
    // Until this time, a source switch the user didn't ask for is the load's
    // own and moves the baseline with it instead of reading as an edit.
    // UI-thread only.
    private DateTime _presetSourceSettleUntil;
    private static readonly TimeSpan PresetSourceSettleTime = TimeSpan.FromSeconds(3);

    [ObservableProperty]
    private InputSource _activeInputSource = InputSource.Usb;

    public event EventHandler? InputSourceChanged;

    public IReadOnlyList<Channel> ActiveOutputs => OutputsForPlatform(Platform);

    public static IReadOnlyList<Channel> OutputsForPlatform(string? platform) => platform switch
    {
        "RP2040" => Channel.Rp2040Outputs,
        "RP2350" => Channel.Outputs,
        _        => Array.Empty<Channel>()
    };

    // Total wire channels on V16+ firmware (unified model: inputs + outputs).
    // RP2350 = 8 in + 9 out = 17; RP2040 = 2 in + 5 out = 7.
    private static int ChannelCountForPlatform(string? platform) =>
        platform == "RP2350" ? 17 : 7;

    private static int InputChannelCountForPlatform(string? platform) =>
        platform == "RP2350" ? 8 : 2;

    private static int OutputChannelCountForPlatform(string? platform) =>
        platform == "RP2350" ? 9 : 5;

    private static int OutputSlotCountForPlatform(string? platform) =>
        platform == "RP2350" ? 4 : 2;

    private static int OutputPinCountForPlatform(string? platform) =>
        platform == "RP2350" ? 5 : 3;

    private static int PdmOutputIndexForPlatform(string? platform) =>
        platform == "RP2040" ? 4 : 8;

    private static int EqWorkerEndForPlatform(string? platform) =>
        platform == "RP2040" ? 4 : 8;

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
    public byte OutputConfigMode => _outputConfigMode;
    public byte MasterVolumeMode => _masterVolumeMode;

    // Multi-device support
    [ObservableProperty]
    private ObservableCollection<DSPiDeviceInfo> _availableDevices = new();

    [ObservableProperty]
    private DSPiDeviceInfo? _selectedDeviceItem;

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
        RaiseActiveInputsChanged();
    }

    // ── PDM / EQ-worker conflict helpers ──

    public int PdmOutputIndex => PdmOutputIndexForPlatform(Platform);
    private int EqWorkerStart => 2;
    private int EqWorkerEnd => EqWorkerEndForPlatform(Platform); // exclusive

    /// <summary>The outputs (0-based, inclusive) that share Core 1 with the
    /// PDM sub: enabling PDM disables them, and enabling one disables PDM.</summary>
    public (int First, int Last) EqWorkerRange => (EqWorkerStart, EqWorkerEnd - 1);

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

    // Queued with the other writes: an enable queued before the switch (Direct
    // 1:1 enables outputs 3-8) must not land after it.
    public async Task SwitchToPdmAsync()
    {
        await DeviceWriteAsync(() =>
        {
            for (int i = EqWorkerStart; i < EqWorkerEnd; i++)
                _device.SetOutputEnable(i, false);
            _device.SetOutputEnable(PdmOutputIndex, true);
            for (int i = EqWorkerStart; i < EqWorkerEnd; i++)
                FetchOutputEnable(i);
            FetchOutputEnable(PdmOutputIndex);
        });
        // Output enables are part of the preset; the fetches above update them
        // without the setters' dirty check.
        CheckDirty();
    }

    public async Task SwitchFromPdmAsync(int enabling)
    {
        await DeviceWriteAsync(() =>
        {
            _device.SetOutputEnable(PdmOutputIndex, false);
            _device.SetOutputEnable(enabling, true);
            FetchOutputEnable(PdmOutputIndex);
            FetchOutputEnable(enabling);
        });
        CheckDirty();
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
        name = ChannelNameLimit.Fit(name.Trim()).TrimEnd();
        if (string.IsNullOrEmpty(name) || name == GetChannelName(channel)) return;
        _channelNames[(int)channel.Id] = name;
        Task.Run(() => _device.SetChannelNameOnDevice((int)channel.Id, name));
        ChannelNameChanged?.Invoke((int)channel.Id);
        CheckDirty();
    }

    /// <summary>Puts every channel of this platform back to its factory name
    /// (Settings › Advanced). No confirmation, as on the macOS Console: a name
    /// is one click to change again.</summary>
    public void ResetChannelNames()
    {
        if (!IsDeviceConnected) return;
        var inputs = Channel.AllInputs.Take(Platform == "RP2350" ? 8 : 2);
        foreach (var channel in inputs.Concat(ActiveOutputs))
            SetChannelName(channel, channel.Name);
        // An output slot set to I2S is named for it; the factory table only
        // knows S/PDIF, so put the slot-aware names back over it.
        UpdateDynamicChannelNames();
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
    public int NumOutputSlots => OutputSlotCountForPlatform(Platform);
    public byte I2SBckPin => _i2sBckPin;
    public bool MckEnabled => _mckEnabled;
    public byte MckPin => _mckPin;
    public int MckMultiplier => _mckMultiplier;
    public uint SampleRateHz => _sampleRateHz;
    public byte SpdifRxPin => _spdifRxPin;
    public byte I2sRxPin => _i2sRxPin;
    public uint I2sInputRateHz => _i2sInputRateHz;

    /// <summary>The I2S-input master sample rates the firmware accepts.</summary>
    public static readonly uint[] I2sInputRates = { 44100, 48000, 96000 };

    /// <summary>Decode the wire encoding (0/1/2) to Hz; defaults to 48000.</summary>
    public static uint DecodeI2sRate(byte encoded) => encoded switch
    {
        0 => 44100u,
        2 => 96000u,
        _ => 48000u
    };
    public bool AnySlotIsI2S => _outputSlotTypes.Take(NumOutputSlots).Any(t => t == OutputSlotType.I2S);

    public MainViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _device = new DspDevice();
        Rta = new RtaEngine(_device);
        InitializeRta();

        // Initialize channel data
        foreach (var channel in Channel.All)
        {
            var filters = new ObservableCollection<FilterParams>();
            for (int i = 0; i < channel.BandCount; i++)
            {
                filters.Add(new FilterParams());
            }
            _channelData[(int)channel.Id] = filters;
            // Seed graph visibility from the persisted dashboard pill state;
            // channels the user never toggled default to visible.
            _channelVisibility[(int)channel.Id] = PersistedChannelVisibility((int)channel.Id);
            _channelDelays[(int)channel.Id] = 0.0f;
            if (channel.IsOutput)
            {
                _channelGains[(int)channel.Id] = 0.0f;
                _channelMutes[(int)channel.Id] = false;

                // Crossover bands exist only on output channels (firmware
                // rejects them on master). Seed 4 default (off) bands.
                var xover = new ObservableCollection<FilterParams>();
                for (int i = 0; i < CrossoverFilter.MaxXoverBands; i++)
                    xover.Add(new FilterParams());
                _xoverData[(int)channel.Id] = xover;
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
                            var info = _device.GetPlatformInfo();
                            var newPlatform = info?.Platform ?? "";
                            var firmware = info?.Version;
                            _dispatcher.TryEnqueue(() => DeviceFirmwareVersion = firmware);
                            // Set channel counts for platform-aware status
                            // parsing and the app↔wire channel-index mapping.
                            // The bulk header refines these authoritatively in
                            // ApplyBulkParams; set them here so per-channel
                            // commands issued before the first bulk read map
                            // correctly on RP2350.
                            _device.NumChannels = ChannelCountForPlatform(newPlatform);
                            _device.NumInputChannels = InputChannelCountForPlatform(newPlatform);
                            _device.NumOutputChannels = OutputChannelCountForPlatform(newPlatform);
                            ApplyPlatformBeforeInitialSync(newPlatform);
                            // Use the same platform value for the first full
                            // sync. That keeps output-index mapping stable even
                            // while the rest of the window is still catching up.
                            FetchAll(newPlatform);
                            FetchPresetInfo();
                            FetchInputSource();
                            FetchBandBypassCapability();
                            // The analyser's caps read is its whole feature
                            // gate: it is transient and absent from the bulk
                            // blob. Firmware without it STALLs the read.
                            Rta.FetchCaps();
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
                                // Recompute once now that the baseline is set and
                                // suppression is lifted: refreshes OutputConfigDirty
                                // and re-syncs the settings pending entries (clears
                                // anything stale carried across a reconnect).
                                CheckDirty();
                            });
                        });
                    }
                    else
                    {
                        // Keep Platform so the UI layout stays until a new device connects
                        Rta.DeviceDisconnected();
                        ResetChannelData();
                        _presetsChecked = false;
                        ActivePreset = -1;
                        PresetsDirty = false;
                        OutputConfigDirty = false;
                        _savedSnapshot = null;
                        ClearIoUndoLog();
                        // Drop any staged output-config changes — fire the event so
                        // the settings window discards its pending entries (they'd
                        // otherwise linger past a reconnect where CheckDirty is
                        // suppressed during the initial sync).
                        _outputConfigChanges = Array.Empty<PresetDiff.IoChange>();
                        _ioSignature = "";
                        OutputConfigStateChanged?.Invoke(this, EventArgs.Empty);
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

        // Generic PARAM_CHANGED (master volume, outputs, loudness, crossfeed,
        // leveller, psybass, I2S/ADAT config, …) + discrete state events.
        WireParamNotifications();

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
                // A value the model already holds changes nothing; skip the
                // rebuild of the graph and the band list it would trigger.
                if (_channelData.TryGetValue(n.Channel, out var filters)
                    && n.Band < filters.Count && !filters[n.Band].Equals(n.Params))
                {
                    filters[n.Band] = n.Params;
                    FiltersChanged?.Invoke(this, EventArgs.Empty);
                    CheckDirty();
                }
            });
        };

        // Per-band crossover change pushed from the device (V11+). n.Band is the
        // LOCAL crossover band (0..3). Same HostSet-suppression rationale as PEQ.
        _device.XoverBandParamNotified += (_, n) =>
        {
            if (n.Source == ParamSource.HostSet) return;
            _dispatcher.TryEnqueue(() =>
            {
                if (_xoverData.TryGetValue(n.Channel, out var bands)
                    && n.Band < bands.Count && !bands[n.Band].Equals(n.Params))
                {
                    bands[n.Band] = n.Params;
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
                        UpdateSavedSnapshotAfterLoad();
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
            Interlocked.Increment(ref _inputSourceNotifySeq);

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
                // The source is part of the preset, and a switch the user asked
                // for only lands here (the firmware applies it later and
                // notifies), so the dirty check in SetInputSourceAsync runs too
                // early to see it. Only for the user's own switch: a preset
                // load also switches the source this way, after its bulk
                // refetch, and must not read as an edit.
                bool requested = _requestedInputSource == newSource;
                _requestedInputSource = null;
                if (requested)
                {
                    CheckDirty();
                }
                else if (DateTime.UtcNow < _presetSourceSettleUntil && _savedSnapshot != null)
                {
                    // The load's own source switch, landing after its baseline.
                    _presetSourceSettleUntil = default;
                    _savedSnapshot.InputSource = newSource;
                    CheckDirty();
                }
            });
        };

        // Status polling timer (60ms interval)
        _pollTimer = new System.Timers.Timer(60);
        _pollTimer.Elapsed += (s, e) =>
        {
            if (IsDeviceConnected)
            {
                FetchStatus();
                // Analyser frames, only while a view watches. It shares this
                // poll so all vendor traffic stays in one order.
                if (Rta.IsWatching) Rta.Tick();
                PollLimiterMeter();
                // Re-poll the Windows USB input format ~every 2s so a channel
                // (alt-mode) change in Sound Settings is picked up without audio.
                if (++_audioPollCounter >= 33)
                {
                    _audioPollCounter = 0;
                    System.Threading.Tasks.Task.Run(RefreshUsbInputChannelCount);
                }
            }
        };
        _pollTimer.AutoReset = true;

        // Start device monitoring
        _device.StartMonitoring();
        _pollTimer.Start();
    }

    private void ApplyPlatformBeforeInitialSync(string platform)
    {
        var platformApplied = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Platform changes intentionally clear stale output-enable state. The
        // first FetchAll must run after that clear, otherwise a busy startup UI
        // can process the queued clear after USB sync and erase fresh outputs.
        if (!_dispatcher.TryEnqueue(() =>
        {
            try { Platform = platform; }
            finally { platformApplied.TrySetResult(true); }
        }))
        {
            return;
        }

        platformApplied.Task.GetAwaiter().GetResult();
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
                    _isSwitchingDevice = true;
                    SelectedDeviceItem = _device.SelectedDeviceInfo;
                    _isSwitchingDevice = false;
                    return;
            }
        }

        _savedSnapshot = null;
        ClearIoUndoLog();
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
        // Local reset only: the device is gone, and a queued write would land on
        // whichever device opens next.
        ApplyFromDevice(() =>
        {
            InputPreampLDb = 0;
            InputPreampRDb = 0;
            MasterVolumeDb = 0;
            Bypass = false;
        });
        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateChannelSelection(Channel? channel)
    {
        SelectedChannel = channel;

        if (channel != null)
        {
            // When a linked input is selected, show both channels of its pair
            bool showPair = IsInputPairLinked((int)channel.Id);
            int partner = GetLinkedInputChannel((int)channel.Id);

            foreach (var ch in Channel.All)
            {
                if (showPair)
                    _channelVisibility[(int)ch.Id] = ch.Id == channel.Id || (int)ch.Id == partner;
                else
                    _channelVisibility[(int)ch.Id] = ch.Id == channel.Id;
            }
        }
        else
        {
            // Back on the dashboard: restore the user's persistent pill
            // configuration (the editor's narrowed view was temporary).
            foreach (var ch in Channel.All)
            {
                _channelVisibility[(int)ch.Id] = PersistedChannelVisibility((int)ch.Id);
            }
        }

        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Persisted dashboard pill state for a channel; no entry = visible.</summary>
    private static bool PersistedChannelVisibility(int channelId) =>
        !Models.AppSettings.Instance.GraphChannelVisibility.TryGetValue(channelId, out var v) || v;

    public void ToggleChannelVisibility(Channel channel)
    {
        var id = (int)channel.Id;
        _channelVisibility[id] = !_channelVisibility[id];

        // Dashboard toggles are the user's persistent configuration (restored on
        // relaunch and whenever a channel editor is closed); toggles while an
        // editor is open only adjust that temporary narrowed view.
        if (SelectedChannel == null)
        {
            var s = Models.AppSettings.Instance;
            s.GraphChannelVisibility[id] = _channelVisibility[id];
            s.Save();
        }

        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool GetChannelVisibility(Channel channel)
    {
        if (!_channelVisibility.TryGetValue((int)channel.Id, out var v) || !v)
            return false;
        return IsChannelActive(channel);
    }

    /// <summary>Whether the channel exists on this device as configured: an
    /// enabled output, or an input within the active source's channel count.
    /// Inputs 3-8 have no pill or row beyond that count, so they must not draw
    /// on the graph either.</summary>
    public bool IsChannelActive(Channel channel)
    {
        if (channel.IsOutput)
        {
            int outputIndex = GetOutputIndex((int)channel.Id);
            return outputIndex >= 0 && IsOutputEnabled(outputIndex);
        }
        int inputIndex = -1;
        for (int i = 0; i < Channel.AllInputs.Count; i++)
            if (Channel.AllInputs[i].Id == channel.Id) { inputIndex = i; break; }
        return inputIndex >= 0 && inputIndex < ActiveInputChannelCount;
    }

    public float GetChannelDelay(Channel channel) =>
        _channelDelays.TryGetValue((int)channel.Id, out var d) ? d : 0;

    public float GetChannelGain(Channel channel) =>
        _channelGains.TryGetValue((int)channel.Id, out var g) ? g : 0;

    public bool GetChannelMute(Channel channel) =>
        _channelMutes.TryGetValue((int)channel.Id, out var m) && m;

    public ObservableCollection<FilterParams> GetFilters(Channel channel) =>
        _channelData.TryGetValue((int)channel.Id, out var f) ? f : new();

    /// <summary>
    /// The crossover bands (up to 4) for an output channel. Empty for master/
    /// input channels, which have no crossover stage. localBand index 0..3 maps
    /// to wire band CrossoverFilter.XoverBandBase + localBand (20..23).
    /// </summary>
    public ObservableCollection<FilterParams> GetXoverFilters(Channel channel) =>
        _xoverData.TryGetValue((int)channel.Id, out var f) ? f : new();

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
        // Ordered: Direct 1:1 and Clear send runs of these, and an output enable
        // after a PDM disable must land in that order.
        DeviceWrite(() => _device.SetMatrixRoute(input, output, enabled, invert, gain));
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
        // Output mute is cached twice — by output index for the matrix window and
        // by channel id for the main window's card, meters and dirty tracking.
        // Both are written here so either window reflects the other's edit.
        var outputs = ActiveOutputs;
        if (output < outputs.Count) _channelMutes[(int)outputs[output].Id] = muted;
        Task.Run(() => _device.SetOutputMute(output, muted));
        OnPropertyChanged(nameof(ChannelMutes));
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
        DeviceWrite(() => _device.SetOutputEnable(output, enabled));
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
        byte before = GetOutputPinValue(pinOutputId);
        var status = _device.SetOutputPin(pinOutputId, pin);
        if (status == Usb.PinConfigResult.Success)
        {
            _outputPins[pinOutputId] = pin;
            if (before != pin) RecordIoUndo(() => SetOutputPinValue(pinOutputId, before));
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

    // ── Multiple SPDIF inputs ──
    // Accessors are index-based (0..SpdifInputCount-1); input 0 is always enabled.
    // PropertyChanged for SpdifRxPin doubles as the "SPDIF input config changed"
    // signal — pages re-read the pins/enables through these accessors on it.

    /// <summary>How many selectable S/PDIF inputs the connected firmware has
    /// (3 before wire V28, 4 from V28). Reported by the device, never assumed.</summary>
    public int SpdifInputCount => _spdifInputCount;

    /// <summary>GPIO pin for SPDIF input <paramref name="index"/>.</summary>
    public byte SpdifRxPinAt(int index) =>
        index <= 0 ? _spdifRxPin
        : index - 1 < _spdifRxPinsExt.Length ? _spdifRxPinsExt[index - 1]
        : (byte)0;

    /// <summary>Whether SPDIF input <paramref name="index"/> is enabled (input 0 always is).</summary>
    public bool SpdifInputEnabled(int index) =>
        index <= 0 || (index - 1 < _spdifRxPinsExt.Length
                       && (_spdifEnabledExt & (1 << (index - 1))) != 0);

    /// <summary>Contiguously-enabled SPDIF input count — the "Instances" value.</summary>
    public int SpdifEnabledCount
    {
        get
        {
            int count = 1; // input 0 always on
            for (int i = 1; i < _spdifInputCount; i++)
                if (SpdifInputEnabled(i)) count = i + 1;
            return count;
        }
    }

    public void FetchSpdifRxPin()
    {
        var pin = _device.GetSpdifRxPin();
        if (pin.HasValue) _spdifRxPin = pin.Value;
    }

    /// <summary>Read the full multi-SPDIF config (0xEF). Falls back to the single
    /// RX pin and clears MultiSpdifSupported if the firmware STALLs.</summary>
    public void FetchSpdifInputConfig()
    {
        var cfg = _device.GetSpdifInputConfig();
        if (cfg == null)
        {
            _dispatcher.TryEnqueue(() => MultiSpdifSupported = false);
            FetchSpdifRxPin();
            return;
        }
        var (count, mask, pins) = cfg.Value;
        // Trust the device's count, bounded by what it actually sent and by what
        // this client can hold, so a firmware with more inputs than we know about
        // degrades to the ones we can address instead of indexing past the array.
        _spdifInputCount = Math.Clamp(Math.Min(count, pins.Length), 1, SpdifRxNumInputs);
        if (pins.Length > 0) _spdifRxPin = pins[0];
        for (int i = 1; i < _spdifInputCount; i++)
            _spdifRxPinsExt[i - 1] = pins[i];
        // 0xEF byte1 mask is (ext<<1)|1: bit1=SPDIF2 .. bit3=SPDIF4.
        _spdifEnabledExt = (byte)((mask >> 1) & 0x07);
        _dispatcher.TryEnqueue(() =>
        {
            MultiSpdifSupported = true;
            OnPropertyChanged(nameof(SpdifInputCount));
            OnPropertyChanged(nameof(SpdifRxPin));
        });
    }

    /// <summary>Set the GPIO pin for SPDIF input <paramref name="index"/>.</summary>
    public byte SetSpdifRxPin(byte pin, int index = 0)
    {
        byte before = SpdifRxPinAt(index);
        var status = _device.SetSpdifRxPin(pin, index);
        if (status == PinConfigResult.Success)
        {
            if (index <= 0) _spdifRxPin = pin;
            else if (index - 1 < _spdifRxPinsExt.Length) _spdifRxPinsExt[index - 1] = pin;
            if (before != pin) RecordIoUndo(() => SetSpdifRxPin(before, index));
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(SpdifRxPin)));
            CheckDirty();
        }
        return status;
    }

    /// <summary>Enable/disable optional SPDIF input <paramref name="index"/> (1..3).</summary>
    public byte SetSpdifInputEnable(int index, bool enable)
    {
        bool before = SpdifInputEnabled(index);
        var status = _device.SetSpdifInputEnable(index, enable);
        if (status == PinConfigResult.Success && index >= 1)
        {
            if (enable) _spdifEnabledExt |= (byte)(1 << (index - 1));
            else _spdifEnabledExt &= (byte)~(1 << (index - 1));
            if (before != enable) RecordIoUndo(() => SetSpdifInputEnable(index, before));
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(SpdifRxPin)));
            CheckDirty();
        }
        return status;
    }

    /// <summary>Set the number of active SPDIF inputs: enable inputs
    /// 1..target-1, disable inputs &gt;= target. Returns the first failing status
    /// (or Success), then re-syncs from the device.</summary>
    public byte SetSpdifInputCount(int target)
    {
        byte result = PinConfigResult.Success;
        for (int i = 1; i < target && i < _spdifInputCount; i++)
        {
            var s = SetSpdifInputEnable(i, true);
            if (s != PinConfigResult.Success && result == PinConfigResult.Success) result = s;
        }
        for (int i = _spdifInputCount - 1; i >= target; i--)
        {
            var s = SetSpdifInputEnable(i, false);
            if (s != PinConfigResult.Success && result == PinConfigResult.Success) result = s;
        }
        FetchSpdifInputConfig();
        return result;
    }

    // ── ADAT bulk output (V17+, RP2350 only) ──
    // AdatSupported is baselined from the bulk blob (platform + HasAdat). Enable
    // and pin are IO-block state: setters apply live, record an undo, and mark the
    // output config dirty just like the SPDIF/I2S input pins.
    public const byte AdatDefaultPin = 12;

    private bool _adatEnabled;
    private byte _adatPin = AdatDefaultPin;

    public bool AdatEnabled => _adatEnabled;
    public byte AdatPin => _adatPin;

    /// <summary>Read the ADAT enable + pin from the device. Clears AdatSupported
    /// if the firmware doesn't answer (older firmware STALLs).</summary>
    public void FetchAdatConfig()
    {
        var en = _device.GetAdatEnable();
        var pin = _device.GetAdatPin();
        if (en == null || pin == null)
        {
            _dispatcher.TryEnqueue(() => AdatSupported = false);
            return;
        }
        _adatEnabled = en.Value != 0;
        if (pin.Value != 0) _adatPin = pin.Value;
        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(AdatEnabled));
            OnPropertyChanged(nameof(AdatPin));
        });
    }

    /// <summary>Enable/disable the ADAT optical output. Returns the firmware
    /// <see cref="PinConfigResult"/> status byte.</summary>
    public byte SetAdatEnable(bool enable)
    {
        bool before = _adatEnabled;
        var status = _device.SetAdatEnable(enable);
        if (status == PinConfigResult.Success)
        {
            _adatEnabled = enable;
            if (before != enable) RecordIoUndo(() => SetAdatEnable(before));
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(AdatEnabled)));
            CheckDirty();
        }
        return status;
    }

    /// <summary>Set the ADAT data GPIO. Returns the firmware
    /// <see cref="PinConfigResult"/> status byte.</summary>
    public byte SetAdatPin(byte pin)
    {
        byte before = _adatPin;
        var status = _device.SetAdatPin(pin);
        if (status == PinConfigResult.Success)
        {
            _adatPin = pin == 0 ? AdatDefaultPin : pin;
            if (before != _adatPin) RecordIoUndo(() => SetAdatPin(before));
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(AdatPin)));
            CheckDirty();
        }
        return status;
    }

    // ── Multichannel I2S input ──
    public int I2sInputChannels => _i2sInputChannels;
    public int I2sMaxPairs => Platform == "RP2350" ? 4 : 1;
    public int I2sMaxInputChannels => I2sMaxPairs * 2;
    public int I2sActivePairs => Math.Max(1, _i2sInputChannels / 2);

    /// <summary>GPIO pin for I2S stereo pair <paramref name="pair"/> (0..3).</summary>
    public byte I2sRxPinAt(int pair) =>
        pair <= 0 ? _i2sRxPin
        : pair - 1 < _i2sRxPinsExt.Length ? _i2sRxPinsExt[pair - 1]
        : (byte)0;

    public void FetchI2sRxPin()
    {
        var pin = _device.GetI2sRxPin();
        if (pin.HasValue) _i2sRxPin = pin.Value;
    }

    /// <summary>Read the channel count + per-pair data pins for multichannel I2S input.</summary>
    public void FetchI2sInputConfig()
    {
        var ch = _device.GetI2sInputChannels();
        if (ch.HasValue && ch.Value is 2 or 4 or 6 or 8) _i2sInputChannels = ch.Value;
        var p0 = _device.GetI2sRxPin(0);
        if (p0.HasValue) _i2sRxPin = p0.Value;
        for (int pair = 1; pair < I2sMaxPairs && pair - 1 < _i2sRxPinsExt.Length; pair++)
        {
            var p = _device.GetI2sRxPin(pair);
            if (p.HasValue && p.Value != 0) _i2sRxPinsExt[pair - 1] = p.Value;
        }
        _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(I2sRxPin)));
    }

    /// <summary>Set the I2S input data pin for stereo pair <paramref name="pair"/>.</summary>
    public byte SetI2sRxPin(byte pin, int pair = 0)
    {
        byte before = I2sRxPinAt(pair);
        var status = _device.SetI2sRxPin(pin, pair);
        if (status == PinConfigResult.Success)
        {
            if (pair <= 0) _i2sRxPin = pin;
            else if (pair - 1 < _i2sRxPinsExt.Length) _i2sRxPinsExt[pair - 1] = pin;
            if (before != pin) RecordIoUndo(() => SetI2sRxPin(before, pair));
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(I2sRxPin)));
            CheckDirty();
        }
        return status;
    }

    /// <summary>Set the I2S input channel count (2/4/6/8). Returns a
    /// <see cref="PinConfigResult"/> status byte.</summary>
    public byte SetI2sInputChannels(int count)
    {
        int before = _i2sInputChannels;
        var status = _device.SetI2sInputChannels(count);
        if (status == PinConfigResult.Success)
        {
            _i2sInputChannels = (byte)count;
            if (before != count) RecordIoUndo(() => SetI2sInputChannels(before));
            _dispatcher.TryEnqueue(() =>
            {
                OnPropertyChanged(nameof(I2sInputChannels));
                RaiseActiveInputsChanged();
                OnPropertyChanged(nameof(I2sActivePairs));
                OnPropertyChanged(nameof(I2sRxPin));
                CheckDirty();
            });
        }
        return status;
    }

    public void FetchI2sInputRate()
    {
        var rate = _device.GetInputRate();
        // Use the stored I2S-input preference (second field), not the live
        // pipeline rate (which follows whatever source is currently active).
        if (rate.HasValue && rate.Value.selectedI2sHz > 0)
            _i2sInputRateHz = rate.Value.selectedI2sHz;
    }

    /// <summary>Set the I2S-input master sample rate (44100/48000/96000 Hz).</summary>
    public bool SetI2sInputRate(uint hz)
    {
        if (!IsDeviceConnected) return false;
        uint before = _i2sInputRateHz;
        var ok = _device.SetInputRate(hz);
        if (ok)
        {
            _i2sInputRateHz = hz;
            if (before != hz) RecordIoUndo(() => SetI2sInputRate(before));
            _dispatcher.TryEnqueue(() =>
            {
                OnPropertyChanged(nameof(I2sInputRateHz));
                CheckDirty();
            });
        }
        return ok;
    }

    public byte SetOutputSlotType(int slot, OutputSlotType type)
    {
        var before = GetOutputSlotType(slot);
        var status = _device.SetOutputType(slot, type);
        if (status == PinConfigResult.Success)
        {
            _outputSlotTypes[slot] = type;
            if (before != type) RecordIoUndo(() => SetOutputSlotType(slot, before));
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
        byte before = _i2sBckPin;
        var status = _device.SetI2SBckPin(pin);
        if (status == PinConfigResult.Success)
        {
            _i2sBckPin = pin;
            if (before != pin) RecordIoUndo(() => SetI2SBckPin(before));
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(I2SBckPin)));
            CheckDirty();
        }
        return status;
    }

    public byte SetMckEnable(bool enabled)
    {
        bool before = _mckEnabled;
        var status = _device.SetMckEnable(enabled);
        if (status == PinConfigResult.Success)
        {
            _mckEnabled = enabled;
            if (before != enabled) RecordIoUndo(() => SetMckEnable(before));
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(MckEnabled)));
            CheckDirty();
        }
        return status;
    }

    public byte SetMckPin(byte pin)
    {
        byte before = _mckPin;
        var status = _device.SetMckPin(pin);
        if (status == PinConfigResult.Success)
        {
            _mckPin = pin;
            if (before != pin) RecordIoUndo(() => SetMckPin(before));
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(MckPin)));
            CheckDirty();
        }
        return status;
    }

    public byte SetMckMultiplier(int multiplier)
    {
        int before = _mckMultiplier;
        var status = _device.SetMckMultiplier(multiplier);
        if (status == PinConfigResult.Success)
        {
            _mckMultiplier = multiplier;
            if (before != multiplier) RecordIoUndo(() => SetMckMultiplier(before));
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

    private void FetchAll(string? platformOverride = null)
    {
        try
        {
            var syncPlatform = string.IsNullOrWhiteSpace(platformOverride) ? Platform : platformOverride;
            var outputs = OutputsForPlatform(syncPlatform);

            // Try bulk fetch first (firmware v2+ with 0xA0 support)
            int sourceNotifySeq = Volatile.Read(ref _inputSourceNotifySeq);
            var bulk = _device.GetAllParams();
            if (bulk != null)
            {
                var parsed = BulkParamsParser.Parse(bulk);
                if (parsed != null)
                {
                    ApplyBulkParams(parsed, outputs, sourceNotifySeq);
                    RefreshUsbInputChannelCount();
                    // Probe siggen support so features gated on it (sidebar
                    // Identify) work without opening the Test Signals window.
                    _ = FetchSiggenAsync();
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
            FetchAllLegacy(syncPlatform, outputs);
            FetchUserVolume();
            FetchLgSoundSync();
            FetchDacHwMute();
            _ = FetchSiggenAsync();
        }
        catch { }
    }

    private void ApplyBulkParams(BulkParams bp, IReadOnlyList<Channel> outputs, int sourceNotifySeq)
    {
        // Record the wire-format version so per-band GET (REQ_GET_EQ_PARAM)
        // uses the matching wValue band-field width (V11 widened it to 5 bits).
        _device.WireFormatVersion = bp.FormatVersion;

        // The bulk header carries the authoritative channel counts for the
        // unified model; refine the app↔wire mapping from them (they drive
        // ChannelMap for per-channel commands, meters and notifications).
        int numInputs = bp.NumInputChannels;
        _device.NumInputChannels = numInputs;
        _device.NumOutputChannels = bp.NumOutputChannels;
        _device.NumChannels = bp.NumChannels;

        // EQ bands — apply first BandCount bands per channel. bp.Eq is indexed
        // by wire channel; map the app channel id through ChannelMap.
        foreach (var channel in Channel.All)
        {
            int ch = (int)channel.Id;
            int wireCh = ChannelMap.AppToWire(ch, numInputs);
            // Extra inputs (11..16) map to wire 2..7 — real inputs only on a device
            // with that many wire input channels (RP2350). On RP2040 wire 2..7 are
            // outputs, so skip them to avoid reading output EQ into phantom inputs.
            if (ChannelMap.IsExtraInput(ch) && wireCh >= numInputs) continue;
            if (_channelData.TryGetValue(ch, out var filters))
            {
                for (int band = 0; band < channel.BandCount && band < bp.MaxBands; band++)
                {
                    var fp = bp.Eq[wireCh, band];
                    int b = band; // capture for closure
                    if (!filters[b].Equals(fp))
                        _dispatcher.TryEnqueue(() => filters[b] = fp);
                }
            }
        }

        // Crossover bands (V11+) — 4 per output channel. Master rows in the
        // wire payload are zeroed; we only seeded _xoverData for output
        // channels, so the TryGetValue naturally skips master.
        if (bp.HasCrossover)
        {
            foreach (var channel in Channel.All)
            {
                int ch = (int)channel.Id;
                int wireCh = ChannelMap.AppToWire(ch, numInputs);
                if (_xoverData.TryGetValue(ch, out var xbands))
                {
                    for (int i = 0; i < xbands.Count && i < CrossoverFilter.MaxXoverBands; i++)
                    {
                        var fp = bp.Xover[wireCh, i];
                        int li = i; // capture for closure
                        if (!xbands[li].Equals(fp))
                            _dispatcher.TryEnqueue(() => xbands[li] = fp);
                    }
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
        for (int inp = 0; inp < MatrixMaxInputs; inp++)
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
        {
            _spdifRxPin = bp.SpdifRxPin;
            // Multiple SPDIF inputs — bulk carries the ext pins + enable mask.
            // The pin array is one entry shorter before wire V28 (no S/PDIF 4).
            if (bp.HasSpdifExtInputs)
            {
                int n = Math.Min(bp.SpdifRxPinExt.Length, _spdifRxPinsExt.Length);
                for (int i = 0; i < n; i++)
                    _spdifRxPinsExt[i] = bp.SpdifRxPinExt[i];
                _spdifEnabledExt = bp.SpdifRxEnabledExt;
                _spdifInputCount = n + 1;
            }
        }
        // I2S input data pin + master rate (V12+ wire format)
        if (bp.HasI2sInputConfig)
        {
            _i2sRxPin = bp.I2sRxPin;
            _i2sInputRateHz = DecodeI2sRate(bp.I2sInputRateEncoded);
            // Multichannel I2S input — per-pair ext pins + channel count.
            _i2sRxPinsExt[0] = bp.I2sRxPinExt[0];
            _i2sRxPinsExt[1] = bp.I2sRxPinExt[1];
            _i2sRxPinsExt[2] = bp.I2sRxPinExt[2];
            if (bp.I2sInputChannels is 2 or 4 or 6 or 8)
                _i2sInputChannels = bp.I2sInputChannels;
        }
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

        // Channel names — bp.ChannelNames is wire-indexed; _channelNames is
        // keyed by app channel id, so map each wire row through ChannelMap.
        for (int wireCh = 0; wireCh < bp.ChannelNames.Length; wireCh++)
        {
            int appCh = ChannelMap.WireToApp(wireCh, numInputs);
            if (appCh < 0) continue;
            var name = bp.ChannelNames[wireCh];
            if (!string.IsNullOrEmpty(name))
                _channelNames[appCh] = name;
        }

        // Dispatch all UI updates
        _dispatcher.TryEnqueue(() =>
        {
            if (bp.HasPerChannelPreamp)
            {
                ApplyFromDevice(() =>
                {
                    InputPreampLDb = bp.PreampLDb;
                    InputPreampRDb = bp.PreampRDb;
                });
                for (int i = 2; i < Math.Min(8, bp.Preamp.Length); i++)
                {
                    // Raised per input, as a notification is, so open trims follow a re-seed.
                    if (_inputPreampExtDb[i - 2] == bp.Preamp[i]) continue;
                    _inputPreampExtDb[i - 2] = bp.Preamp[i];
                    InputPreampExtChanged?.Invoke(i);
                }
            }
            else
            {
                // Pre-V6 firmware: legacy uniform preamp in PreampGainDb
                ApplyFromDevice(() =>
                {
                    InputPreampLDb = bp.PreampGainDb;
                    InputPreampRDb = bp.PreampGainDb;
                });
            }
            if (bp.HasMasterVolume)
                ApplyFromDevice(() => MasterVolumeDb = bp.MasterVolumeDb);
            CrossoverSupported = bp.HasCrossover;
            InputI2sSupported = bp.HasI2sInputConfig;
            MultiSpdifSupported = bp.HasSpdifExtInputs;

            // ADAT "bulk" optical output (V17+, RP2350 only). Baseline enable/pin
            // from the bulk blob; the Bulk Output settings page edits them live.
            AdatSupported = Platform == "RP2350" && bp.HasAdat;
            if (AdatSupported)
            {
                _adatEnabled = bp.AdatEnabled;
                _adatPin = bp.AdatPin;
                OnPropertyChanged(nameof(AdatEnabled));
                OnPropertyChanged(nameof(AdatPin));
            }

            // Multichannel masks (V18/V19/V20). Gate each selector on the wire
            // version; the leveller mask is additionally hidden for ≤2 inputs
            // (stereo), where a per-input detector/apply split has no value.
            LoudnessMaskSupported = bp.FormatVersion >= 19;
            if (LoudnessMaskSupported)
                LoudnessOutputMask = bp.LoudnessOutputMask;
            CrossfeedMaskSupported = bp.FormatVersion >= 20;
            if (CrossfeedMaskSupported)
                CrossfeedOutputPairMask = bp.CrossfeedOutputPairMask;
            LinkwitzTransformSupported = bp.FormatVersion >= 22;
            FirstOrderPassSupported = bp.FormatVersion >= 28;
            SeedPsybassFromBulk(bp);
            SeedUpmixFromBulk(bp);
            SeedSubharmFromBulk(bp);
            SeedInputChannelDelaysFromBulk(bp);
            SeedTubeFromBulk(bp);
            SeedLimiterFromBulk(bp);
            SeedAdatInputFromBulk(bp);
            SeedI2sClockFromBulk(bp);
            LevellerMasksSupported = bp.FormatVersion >= 18 && bp.NumInputChannels > 2;
            if (LevellerMasksSupported)
            {
                LevellerDetectorMask = bp.LevellerDetectorMask;
                LevellerApplyMask = bp.LevellerApplyMask;
            }
            ApplyFromDevice(() => Bypass = bp.Bypass);
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
                for (int inp = 0; inp < MatrixMaxInputs; inp++)
                    MatrixRouteChanged?.Invoke(inp, o);
            }

            for (int wireCh = 0; wireCh < bp.ChannelNames.Length; wireCh++)
            {
                int appCh = ChannelMap.WireToApp(wireCh, numInputs);
                if (appCh < 0) continue;
                if (!string.IsNullOrEmpty(bp.ChannelNames[wireCh]))
                    ChannelNameChanged?.Invoke(appCh);
            }

            VisibilityChanged?.Invoke(this, EventArgs.Empty);
            FiltersChanged?.Invoke(this, EventArgs.Empty);

            // Every source the firmware can report, including the optional S/PDIF
            // inputs — a device sitting on S/PDIF 2 must not read back as unknown.
            if (bulkInputSource is { } src &&
                src is InputSource.Usb or InputSource.Spdif or InputSource.I2s
                    or InputSource.Adat or InputSource.Spdif2 or InputSource.Spdif3
                    or InputSource.Spdif4)
            {
                // A source notification since the read started is newer than
                // the read's value (a preset load's switch can land mid-read):
                // its own handler sets the source.
                if (ActiveInputSource != src && Volatile.Read(ref _inputSourceNotifySeq) == sourceNotifySeq)
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

            // When there are no unsaved user IO edits, the freshly-synced device
            // state IS the output-config baseline (RAM == flash). Advance it so a
            // follow-up/settling bulk read on reconnect (or a device/other-host
            // notification) doesn't register as a spurious "unsaved output config"
            // change. A pending user edit (non-empty undo log) preserves the dirty
            // state — we don't re-baseline over it.
            bool noUserIoEdits;
            lock (_ioUndoLock) noUserIoEdits = _ioUndoLog.Count == 0;
            if (_savedSnapshot != null && noUserIoEdits)
            {
                _savedSnapshot.CopyIoBlockFrom(PresetSnapshot.Capture(this));
                CheckDirty();
            }

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

    private void FetchAllLegacy(string platform, IReadOnlyList<Channel> outputs)
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

        foreach (var channel in outputs)
        {
            FetchDelay((int)channel.Id);
            FetchChannelGain((int)channel.Id);
            FetchChannelMute((int)channel.Id);
        }

        FetchLoudness();
        FetchCrossfeed();

        // Fetch matrix mixer state
        FetchMatrixRoutes(outputs);
        var outputCount = outputs.Count;
        for (int o = 0; o < outputCount; o++)
        {
            FetchOutputEnable(o);
            FetchOutputMuteState(o);
        }

        // Fetch pin assignments
        int pinCount = OutputPinCountForPlatform(platform);
        for (int p = 0; p < pinCount; p++)
            FetchOutputPin(p);

        // Fetch I2S configuration
        int slotCount = OutputSlotCountForPlatform(platform);
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
                uint newBits = status.ClipFlags & ~_clipLatched;
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
        CancelLiveFilterSends();   // this awaited write is authoritative — no stale live write after it
        CancelDeferredFilterSend(channel, band);
        if (IsInputPairLinked(channel)) CancelDeferredFilterSend(GetLinkedInputChannel(channel), band);
        if (_channelData.TryGetValue(channel, out var filters) && band < filters.Count)
            filters[band] = p;
        var success = await Task.Run(() => { lock (_filterWriteGate) return _device.SetFilter(channel, band, p); });

        // Mirror to the linked pair partner
        if (IsInputPairLinked(channel))
        {
            int other = GetLinkedInputChannel(channel);
            if (_channelData.TryGetValue(other, out var otherFilters) && band < otherFilters.Count)
                otherFilters[band] = p;
            await Task.Run(() => { lock (_filterWriteGate) _device.SetFilter(other, band, p); });
        }

        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();
        return success;
    }

    private readonly Dictionary<(int Channel, int WireBand), CancellationTokenSource> _filterDebounceCts = new();

    /// <summary>
    /// Update filter locally and fire events immediately, deferring the USB send.
    /// Use for rapid interactive updates (e.g. scroll adjustments).
    /// </summary>
    public void SetFilterDeferred(int channel, int band, FilterParams p)
    {
        if (_channelData.TryGetValue(channel, out var filters) && band < filters.Count)
            filters[band] = p;

        // Mirror to the linked pair partner (local state)
        if (IsInputPairLinked(channel))
        {
            int other = GetLinkedInputChannel(channel);
            if (_channelData.TryGetValue(other, out var otherFilters) && band < otherFilters.Count)
                otherFilters[band] = p;
        }

        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();

        var token = RestartDeferredFilterSend(channel, band);
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                lock (_filterWriteGate)
                {
                    // Re-checked under the gate: an authoritative write to this
                    // band may have cancelled us after the delay elapsed.
                    if (token.IsCancellationRequested) return;
                    _device.SetFilter(channel, band, p);
                    if (IsInputPairLinked(channel))
                        _device.SetFilter(GetLinkedInputChannel(channel), band, p);
                }
            }
            catch (TaskCanceledException) { }
        });
    }

    /// <summary>
    /// Starts a band's deferred-send debounce, cancelling only that band's
    /// previous one: an edit on another band must not drop this band's pending
    /// write. Keyed by wire band, so crossover bands (20..23) don't collide
    /// with PEQ bands.
    /// </summary>
    private CancellationToken RestartDeferredFilterSend(int channel, int wireBand)
    {
        var cts = new CancellationTokenSource();
        lock (_filterDebounceCts)
        {
            if (_filterDebounceCts.Remove((channel, wireBand), out var old)) old.Cancel();
            _filterDebounceCts[(channel, wireBand)] = cts;
        }
        return cts.Token;
    }

    /// <summary>
    /// Drops a band's pending deferred send, so an older wheel value can't
    /// land after an authoritative write to the same band.
    /// </summary>
    private void CancelDeferredFilterSend(int channel, int wireBand)
    {
        lock (_filterDebounceCts)
        {
            if (_filterDebounceCts.Remove((channel, wireBand), out var old)) old.Cancel();
        }
    }

    // ── Input-pair linking (Master L/R + IN3/4, IN5/6, IN7/8) ──
    // Pair 0's state lives in the observable MasterPeqLinked (persisted under
    // that name since before multichannel inputs existed); pairs 1..3 are plain
    // flags the main window loads from / persists to AppSettings.
    private readonly bool[] _inputPairLinkedExt = new bool[3];

    /// <summary>Stereo pair index (0..3) for an input channel id, or -1.</summary>
    public static int InputPairIndex(int channelId) => channelId switch
    {
        (int)ChannelId.MasterLeft or (int)ChannelId.MasterRight => 0,
        >= (int)ChannelId.Input3 and <= (int)ChannelId.Input8 =>
            1 + (channelId - (int)ChannelId.Input3) / 2,
        _ => -1,
    };

    /// <summary>The other channel of an input pair (Master L↔R, IN3↔IN4, …).</summary>
    public static int GetLinkedInputChannel(int channelId) => ChannelMap.LinkedPartnerId(channelId);

    public bool GetInputPairLinked(int pair) =>
        pair == 0 ? MasterPeqLinked : pair is >= 1 and <= 3 && _inputPairLinkedExt[pair - 1];

    public void SetInputPairLinked(int pair, bool value)
    {
        if (pair == 0) MasterPeqLinked = value;
        else if (pair is >= 1 and <= 3)
        {
            if (_inputPairLinkedExt[pair - 1] == value) return;
            _inputPairLinkedExt[pair - 1] = value;
            InputPairLinksChanged?.Invoke();
        }
    }

    /// <summary>A pair was linked or unlinked, from the link toggle, a preset
    /// or a restore; the window stores the device's links on it.</summary>
    public event Action? InputPairLinksChanged;

    partial void OnMasterPeqLinkedChanged(bool value) => InputPairLinksChanged?.Invoke();

    /// <summary>True when this channel is an input whose stereo pair is linked.</summary>
    public bool IsInputPairLinked(int channelId)
    {
        int pair = InputPairIndex(channelId);
        return pair >= 0 && GetInputPairLinked(pair);
    }

    /// <summary>Wire input index (0..7) for an input channel id.</summary>
    private static int InputWireFromId(int channelId) => channelId switch
    {
        (int)ChannelId.MasterLeft => 0,
        (int)ChannelId.MasterRight => 1,
        _ => channelId - ChannelMap.ExtraInputFirstId + 2,
    };

    /// <summary>
    /// Returns true iff an input channel's filter bank differs from its pair
    /// partner's in at least one band. Used by the Link toggle to decide whether
    /// the user must be prompted to choose a source channel before syncing.
    /// </summary>
    public bool InputPairFiltersDiffer(int channelId)
    {
        int other = GetLinkedInputChannel(channelId);
        if (!_channelData.TryGetValue(channelId, out var a)) return false;
        if (!_channelData.TryGetValue(other, out var b)) return false;
        if (a.Count != b.Count) return true;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return true;
        return false;
    }

    /// <summary>Whether a pair's input trims differ (by more than the 0.05 dB
    /// a field can show). Linking copies the trim with the filters, so it is
    /// part of what the link toggle asks about, as on the macOS Console.</summary>
    public bool InputPairPreampsDiffer(int channelId)
    {
        int other = GetLinkedInputChannel(channelId);
        return Math.Abs(InputPreampAt(InputWireFromId(channelId)) - InputPreampAt(InputWireFromId(other))) > 0.05f;
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
    public async Task<bool> SyncInputPairFilters(int sourceChannel)
    {
        int other = GetLinkedInputChannel(sourceChannel);
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
        SetInputPreampAt(InputWireFromId(other), InputPreampAt(InputWireFromId(sourceChannel)));

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

    /// <summary>Longest output delay the firmware's delay line holds at the
    /// current sample rate: MAX_DELAY_SAMPLES is 2048 on RP2350 and 1024 on
    /// RP2040 (config.h, since firmware 9ec0ca1), and the firmware converts ms
    /// to samples at the live rate, so the limit is 42.6 / 21.3 ms at 48 kHz
    /// and half that at 96 kHz. Longer requests are silently capped on the
    /// device, so the UI caps them first. (A stored delay is kept in ms, so
    /// one set at a lower rate comes back in full when the rate does.)</summary>
    public float MaxOutputDelayMs
    {
        get
        {
            int maxSamples = Platform == "RP2350" ? 2048 : 1024;
            uint rate = _sampleRateHz >= 8000 ? _sampleRateHz : 48000;
            // Rounded down to the field's 0.01 ms so the firmware's truncating
            // ms→samples conversion never lands past the end of the line.
            return MathF.Floor(maxSamples * 100000f / rate) / 100f;
        }
    }

    /// <summary>The cap the model setters apply: never below the 48 kHz limit,
    /// so a delay written by a preset import while the device runs at 96 kHz is
    /// stored in full (the firmware keeps ms and applies it in full again at
    /// 48 kHz), and never below <see cref="MaxOutputDelayMs"/>, which the
    /// editors cap new values with (higher at 44.1 kHz).</summary>
    internal float MaxStoredOutputDelayMs => Math.Max(Platform == "RP2350" ? 42f : 21f, MaxOutputDelayMs);

    public void SetDelay(int channel, float ms)
    {
        ms = MathF.Round(Math.Clamp(ms, 0f, MaxStoredOutputDelayMs), 4);
        _channelDelays[channel] = ms;
        int outputIndex = GetOutputIndex(channel);
        if (outputIndex >= 0)
            DeviceWrite(() => _device.SetOutputDelay(outputIndex, ms));
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
        DeviceWrite(() => _device.SetOutputGain(outputIndex, db));
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
        if (outputIndex < _outputMuted.Length) _outputMuted[outputIndex] = muted;
        Task.Run(() => _device.SetOutputMute(outputIndex, muted));
        OnPropertyChanged(nameof(ChannelMutes));
        MatrixOutputMuteChanged?.Invoke(outputIndex);
        CheckDirty();
    }

    public void CopyChannelParams(Channel channel)
    {
        var channelId = (int)channel.Id;
        var filters = _channelData.TryGetValue(channelId, out var f)
            ? f.Select(fp => fp.Clone()).ToList()
            : new List<FilterParams>();

        // Crossover bands exist only on output channels.
        var xover = channel.IsOutput && _xoverData.TryGetValue(channelId, out var x)
            ? x.Select(fp => fp.Clone()).ToList()
            : new List<FilterParams>();

        _channelClipboard = new ChannelClipboard
        {
            SourceIsOutput = channel.IsOutput,
            Filters = filters,
            Xover = xover,
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

    /// <summary>
    /// Replace all crossover bands on an output channel at once (used by channel
    /// paste). Mirrors <see cref="SetAllFilters"/> but addresses the crossover
    /// wire band indices (XoverBandBase + i). Output channels only.
    /// </summary>
    public void SetAllXoverFilters(int channelId, List<FilterParams> bands)
    {
        if (!_xoverData.TryGetValue(channelId, out var existing)) return;

        var count = Math.Min(bands.Count, existing.Count);
        for (int i = 0; i < count; i++)
            existing[i] = bands[i];

        var snapshot = bands.Take(count).Select(fp => fp.Clone()).ToList();
        Task.Run(() =>
        {
            for (int i = 0; i < snapshot.Count; i++)
                _device.SetFilter(channelId, CrossoverFilter.XoverBandBase + i, snapshot[i]);
        });

        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PasteChannelParams(Channel target)
    {
        if (_channelClipboard == null) return;

        var targetId = (int)target.Id;

        // Filters are universal, always pasted (deep-cloned). Bands past the
        // clipboard's are reset to flat, so the target ends up exactly as the
        // source was; a linked input's partner gets the same, as on the macOS
        // Console.
        int bandCount = _channelData.TryGetValue(targetId, out var targetBands) ? targetBands.Count : 0;
        List<FilterParams> Bands() => Enumerable.Range(0, bandCount)
            .Select(i => i < _channelClipboard.Filters.Count
                ? _channelClipboard.Filters[i].Clone()
                : new FilterParams(FilterType.Flat, 1000f, 0.707f, 0f))
            .ToList();
        SetAllFilters(targetId, Bands());
        if (!target.IsOutput && IsInputPairLinked(targetId))
            SetAllFilters(GetLinkedInputChannel(targetId), Bands());

        // Delay, gain, mute, and crossover bands are output-only — paste only
        // when both source and target are outputs.
        if (target.IsOutput && _channelClipboard.SourceIsOutput)
        {
            // Crossover only on V11+ firmware; skip the writes otherwise.
            if (CrossoverSupported)
                SetAllXoverFilters(targetId, _channelClipboard.Xover.Select(fp => fp.Clone()).ToList());
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

    private void FetchMatrixRoutes(IReadOnlyList<Channel>? outputsOverride = null)
    {
        var outputs = outputsOverride ?? ActiveOutputs;
        int numInputs = Math.Clamp(_device.NumInputChannels, 2, MatrixMaxInputs);
        for (int input = 0; input < numInputs; input++)
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
        DeviceWrite(() => _device.SetLoudnessEnabled(value));
        CheckDirty();
    }

    // Through the ordered queue, which the window's live sends share.
    partial void OnLoudnessRefSPLChanged(float value)
    {
        DeviceWrite(() => _device.SetLoudnessRefSPL(value));
        CheckDirty();
    }

    partial void OnLoudnessIntensityChanged(float value)
    {
        DeviceWrite(() => _device.SetLoudnessIntensity(value));
        CheckDirty();
    }

    partial void OnCrossfeedEnabledChanged(bool value)
    {
        DeviceWrite(() => _device.SetCrossfeedEnabled(value));
        CheckDirty();
    }

    // Preset, cutoff and feed go through the ordered queue the window's live
    // sends share, so switching to Custom lands before a drag's values.
    partial void OnCrossfeedPresetChanged(int value)
    {
        DeviceWrite(() => _device.SetCrossfeedPreset(value));
        CheckDirty();
    }

    partial void OnCrossfeedFreqChanged(float value)
    {
        DeviceWrite(() => _device.SetCrossfeedFreq(value));
        CheckDirty();
    }

    partial void OnCrossfeedFeedChanged(float value)
    {
        DeviceWrite(() => _device.SetCrossfeedFeed(value));
        CheckDirty();
    }

    partial void OnCrossfeedItdChanged(bool value)
    {
        DeviceWrite(() => _device.SetCrossfeedItd(value));
        CheckDirty();
    }

    // Volume leveller change handlers
    partial void OnLevellerEnabledChanged(bool value)
    {
        DeviceWrite(() => _device.SetLevellerEnabled(value));
        CheckDirty();
    }

    // Amount, max gain and gate go through the ordered queue the window's
    // live sends share.
    partial void OnLevellerAmountChanged(float value)
    {
        DeviceWrite(() => _device.SetLevellerAmount(value));
        CheckDirty();
    }

    partial void OnLevellerSpeedChanged(int value)
    {
        DeviceWrite(() => _device.SetLevellerSpeed(value));
        CheckDirty();
    }

    partial void OnLevellerMaxGainDbChanged(float value)
    {
        DeviceWrite(() => _device.SetLevellerMaxGain(value));
        CheckDirty();
    }

    partial void OnLevellerLookaheadChanged(bool value)
    {
        DeviceWrite(() => _device.SetLevellerLookahead(value));
        CheckDirty();
    }

    partial void OnLevellerGateDbChanged(float value)
    {
        DeviceWrite(() => _device.SetLevellerGate(value));
        CheckDirty();
    }

    // Multichannel mask push handlers. Each re-sends the whole mask (the wire
    // protocol has no incremental per-bit form). Detector and apply share one
    // 2-byte leveller command, so both partials funnel through PushLevellerMasks.
    partial void OnLevellerDetectorMaskChanged(int value) => PushLevellerMasks();
    partial void OnLevellerApplyMaskChanged(int value) => PushLevellerMasks();

    private void PushLevellerMasks()
    {
        byte detector = (byte)(LevellerDetectorMask & 0xFF);
        byte apply = (byte)(LevellerApplyMask & 0xFF);
        DeviceWrite(() => _device.SetLevellerMasks(detector, apply));
        CheckDirty();
    }

    partial void OnLoudnessOutputMaskChanged(int value)
    {
        ushort mask = (ushort)(value & 0xFFFF);
        DeviceWrite(() => _device.SetLoudnessMask(mask));
        CheckDirty();
    }

    partial void OnCrossfeedOutputPairMaskChanged(int value)
    {
        byte mask = (byte)(value & 0xFF);
        DeviceWrite(() => _device.SetCrossfeedOutputs(mask));
        CheckDirty();
    }

    /// <summary>Number of firmware input channels (drives the leveller mask chip count).</summary>
    public int NumInputChannels => _device.NumInputChannels;

    /// <summary>Number of firmware output channels (drives the loudness/crossfeed chip counts).</summary>
    public int NumOutputChannels => _device.NumOutputChannels;

    // ── Test signal generator ──
    // The Test Signals window edits _siggenConfig (the draft) in place; caps and
    // per-type descriptors drive its type list, param ranges and channel count.
    private readonly SiggenConfig _siggenConfig = new();
    private SiggenCaps? _siggenCaps;
    private SiggenTypeDesc[] _siggenTypeDescs = Array.Empty<SiggenTypeDesc>();

    public SiggenConfig SiggenConfig => _siggenConfig;
    public SiggenCaps? SiggenCaps => _siggenCaps;
    public IReadOnlyList<SiggenTypeDesc> SiggenTypeDescs => _siggenTypeDescs;

    /// <summary>The descriptor for a signal type, or null when caps have not been read.</summary>
    public SiggenTypeDesc? SiggenDescFor(SiggenType type)
    {
        foreach (var d in _siggenTypeDescs)
            if (d.Id == type) return d;
        return null;
    }

    /// <summary>
    /// Make the draft config safe and self-consistent before anything is shown or sent.
    /// A device that has never staged a config answers GET_CONFIG with a zeroed struct
    /// (0 dBFS, no channels, no params), so adopt real defaults instead of presenting
    /// full scale, and align params/timing with the selected type's descriptor.
    /// </summary>
    private void NormalizeSiggenDraft()
    {
        ushort valid = _siggenCaps?.UsableChannelMask ?? 0;

        if (_siggenConfig.IsUnconfigured)
        {
            _siggenConfig.ChannelMask = valid;
            _siggenConfig.InvertMask = 0;
            _siggenConfig.Flags = SiggenFlags.None;
            _siggenConfig.LevelDb = SiggenConfig.DefaultLevelDb;
        }
        else if (valid != 0)
        {
            _siggenConfig.ChannelMask &= valid;
        }
        _siggenConfig.InvertMask &= _siggenConfig.ChannelMask;

        if (float.IsNaN(_siggenConfig.LevelDb))
            _siggenConfig.LevelDb = SiggenConfig.DefaultLevelDb;
        else
            _siggenConfig.LevelDb = Math.Clamp(_siggenConfig.LevelDb,
                SiggenConfig.LevelMinDb, SiggenConfig.LevelMaxDb);

        var desc = SiggenDescFor(_siggenConfig.SignalType);
        if (desc != null)
        {
            desc.SeedParams(_siggenConfig);
            desc.SeedTiming(_siggenConfig, _siggenConfig.Flags);
        }
    }

    /// <summary>Probe siggen support and read caps, per-type descriptors, the applied
    /// config, and initial status. Sets SiggenSupported (false if the firmware STALLs).</summary>
    public async Task FetchSiggenAsync()
    {
        await Task.Run(() =>
        {
            var caps = _device.GetSiggenCaps();
            if (caps == null || caps.TypeCount == 0)
            {
                _dispatcher.TryEnqueue(() => SiggenSupported = false);
                return;
            }
            _siggenCaps = caps;
            var descs = new List<SiggenTypeDesc>();
            for (int i = 0; i < caps.TypeCount; i++)
            {
                var d = _device.GetSiggenTypeDesc(i);
                if (d != null) descs.Add(d);
            }
            _siggenTypeDescs = descs.ToArray();
            var cfg = _device.GetSiggenConfig();
            var status = _device.GetSiggenStatus();
            _dispatcher.TryEnqueue(() =>
            {
                // Adopt the draft on the UI thread: an open Test Signals window edits
                // the same instance, and a re-sync can land mid-edit.
                if (cfg != null) cfg.CopyTo(_siggenConfig);
                NormalizeSiggenDraft();
                SiggenSupported = true;
                SiggenStatus = status;
            });
        });
    }

    /// <summary>Stage the current draft config to the device (SET 0xA4). Does not start.</summary>
    public Task<bool> ApplySiggenConfigAsync()
    {
        var cfg = _siggenConfig.Clone();
        return Task.Run(() => _device.SetSiggenConfig(cfg));
    }

    /// <summary>Apply the draft then start playback. False if SET or START is rejected.</summary>
    public async Task<bool> StartSiggenAsync()
    {
        var cfg = _siggenConfig.Clone();
        return await Task.Run(() =>
        {
            if (!_device.SetSiggenConfig(cfg)) return false;
            bool ok = _device.SiggenControl(SiggenControl.Start);
            if (ok)
            {
                var status = _device.GetSiggenStatus();
                _dispatcher.TryEnqueue(() => SiggenStatus = status);
            }
            return ok;
        });
    }

    /// <summary>Stop playback (faded, or immediate when now=true).</summary>
    public async Task StopSiggenAsync(bool now = false)
    {
        await Task.Run(() =>
        {
            _device.SiggenControl(now ? SiggenControl.StopNow : SiggenControl.Stop);
            var status = _device.GetSiggenStatus();
            _dispatcher.TryEnqueue(() => SiggenStatus = status);
        });
    }

    /// <summary>Poll live status (0xA7) into SiggenStatus.</summary>
    public async Task PollSiggenStatusAsync()
    {
        var status = await Task.Run(() => _device.GetSiggenStatus());
        if (status != null) _dispatcher.TryEnqueue(() => SiggenStatus = status);
    }

    /// <summary>Re-read status off the notification thread (NOTIFY_EVT_SIGGEN_STATE 0x07:
    /// start, stop, completion, reconfigure). Keeps SiggenStatus fresh even when nothing
    /// is polling — e.g. after an Identify chirp finishes with the window closed.</summary>
    private void RefreshSiggenStatus() => Task.Run(() =>
    {
        var status = _device.GetSiggenStatus();
        if (status != null) _dispatcher.TryEnqueue(() => SiggenStatus = status);
    });

    /// <summary>
    /// Play the siggen's Channel ID chirp on a single output so the user can
    /// locate it physically (sidebar right-click → Identify), once, then
    /// auto-stop. Sends a standalone config; the Test Signals draft
    /// (<see cref="SiggenConfig"/>) is untouched.
    /// </summary>
    public async Task IdentifyOutputAsync(int outputIndex)
    {
        if (!SiggenSupported || outputIndex < 0 || outputIndex > 15) return;

        // Only bits the firmware accepts; anything else leaves an empty effective
        // mask, which SET_CONFIG rejects (a silent no-op from the user's side).
        ushort bit = (ushort)(1 << outputIndex);
        ushort valid = _siggenCaps?.UsableChannelMask ?? 0;
        if (valid != 0 && (bit & valid) == 0) return;

        var desc = SiggenDescFor(SiggenType.ChannelId);

        var cfg = new SiggenConfig
        {
            SignalType = SiggenType.ChannelId,
            ChannelMask = bit,
            InvertMask = 0,
            Flags = SiggenFlags.None,
            LevelDb = SiggenConfig.DefaultLevelDb,
        };
        if (desc != null)
            for (int i = 0; i < 4; i++)
                if (desc.Params[i].IsUsed) cfg.SetParam(i, desc.Params[i].Default);

        switch (desc?.TimingModel)
        {
            case SiggenTimingModel.Pattern:
                cfg.Repeat = 1;      // 0 = infinite; play the ID once then stop
                break;
            case SiggenTimingModel.Sweep:
                cfg.DurationMs = 500;
                cfg.Repeat = 1;      // sweep repeat: 0 = infinite, so ask for one
                break;
            default:                 // Continuous (or unknown desc): bounded burst
                cfg.DurationMs = 1500;
                break;
        }

        await Task.Run(() =>
        {
            if (!_device.SetSiggenConfig(cfg)) return;
            if (_device.SiggenControl(SiggenControl.Start))
            {
                var status = _device.GetSiggenStatus();
                _dispatcher.TryEnqueue(() => SiggenStatus = status);
            }
        });
    }

    partial void OnInputPreampLDbChanged(float value)
    {
        if (_applyingDeviceState == 0)
        {
            var rounded = MathF.Round(value, 1);
            DeviceWrite(() => _device.SetInputPreamp(0, rounded));
            if (_masterPeqLinked && Math.Abs(InputPreampRDb - rounded) > 0.05f)
                InputPreampRDb = rounded;
        }
        CheckDirty();
    }

    partial void OnInputPreampRDbChanged(float value)
    {
        if (_applyingDeviceState == 0)
        {
            var rounded = MathF.Round(value, 1);
            DeviceWrite(() => _device.SetInputPreamp(1, rounded));
            if (_masterPeqLinked && Math.Abs(InputPreampLDb - rounded) > 0.05f)
                InputPreampLDb = rounded;
        }
        CheckDirty();
    }

    // ── Extra-input preamps (wire inputs 2..7, RP2350 unified model) ──
    // Master L/R keep their observable InputPreampL/RDb properties; the extra
    // inputs live here so IN 3..8 get the same header preamp control.
    private readonly float[] _inputPreampExtDb = new float[6];

    /// <summary>Raised on the UI thread when an extra input's preamp changes
    /// (argument = wire input index 2..7).</summary>
    public event Action<int>? InputPreampExtChanged;

    /// <summary>Preamp for any wire input index (0..7).</summary>
    public float InputPreampAt(int wireInput) => wireInput switch
    {
        0 => InputPreampLDb,
        1 => InputPreampRDb,
        >= 2 and <= 7 => _inputPreampExtDb[wireInput - 2],
        _ => 0f,
    };

    /// <summary>Set the preamp for any wire input index (0..7). Inputs 0/1 route
    /// through the observable L/R properties (preserving Link L/R mirroring).</summary>
    public void SetInputPreampAt(int wireInput, float db)
    {
        if (wireInput == 0) { InputPreampLDb = db; return; }
        if (wireInput == 1) { InputPreampRDb = db; return; }
        if (wireInput is < 2 or > 7) return;
        var rounded = MathF.Round(db, 1);
        if (Math.Abs(_inputPreampExtDb[wireInput - 2] - rounded) < 0.05f) return;
        _inputPreampExtDb[wireInput - 2] = rounded;
        DeviceWrite(() => _device.SetInputPreamp(wireInput, rounded));
        InputPreampExtChanged?.Invoke(wireInput);

        // Mirror to the pair partner when the pair is linked (matches the L/R
        // property setters' behaviour for pair 0).
        int partnerWire = wireInput ^ 1;
        if (GetInputPairLinked(1 + (wireInput - 2) / 2)
            && Math.Abs(_inputPreampExtDb[partnerWire - 2] - rounded) > 0.05f)
        {
            _inputPreampExtDb[partnerWire - 2] = rounded;
            DeviceWrite(() => _device.SetInputPreamp(partnerWire, rounded));
            InputPreampExtChanged?.Invoke(partnerWire);
        }
        CheckDirty();
    }

    partial void OnMasterVolumeDbChanged(float value)
    {
        if (_applyingDeviceState == 0)
        {
            var send = value <= -127.5f ? -128f : MathF.Round(value, 1);
            Task.Run(() => _device.SetMasterVolume(send));
        }
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
                _dispatcher.TryEnqueue(() => ApplyFromDevice(() => InputPreampLDb = l.Value));
            if (Math.Abs(InputPreampRDb - r.Value) > 0.1f)
                _dispatcher.TryEnqueue(() => ApplyFromDevice(() => InputPreampRDb = r.Value));
            return true;
        }
        // Fallback to legacy uniform preamp for pre-V6 firmware
        var legacy = _device.GetPreamp();
        if (legacy.HasValue)
        {
            _dispatcher.TryEnqueue(() => ApplyFromDevice(() =>
            {
                InputPreampLDb = legacy.Value;
                InputPreampRDb = legacy.Value;
            }));
            return true;
        }
        _dispatcher.TryEnqueue(() => IsDeviceConnected = false);
        return false;
    }

    private void FetchMasterVolume()
    {
        var mv = _device.GetMasterVolume();
        if (mv.HasValue && Math.Abs(MasterVolumeDb - mv.Value) > 0.1f)
            _dispatcher.TryEnqueue(() => ApplyFromDevice(() => MasterVolumeDb = mv.Value));
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
        if (_applyingDeviceState == 0)
            Task.Run(() => _device.SetBypass(value));
        BypassChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();
    }

    private void FetchBypass()
    {
        var bypass = _device.GetBypass();
        if (bypass.HasValue)
        {
            _dispatcher.TryEnqueue(() => ApplyFromDevice(() => Bypass = bypass.Value));
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
                    UpdateSavedSnapshotAfterLoad();
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
                // Firmware clamps anything >1 to independent, but defend against
                // a transitional byte value by treating only the explicit 1 as
                // with-preset.
                _outputConfigMode = dir.Value.OutputConfigMode == 1 ? (byte)1 : (byte)0;
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

        if (IsInputPairLinked(channel))
        {
            int other = GetLinkedInputChannel(channel);
            if (_channelData.TryGetValue(other, out var otherFilters) && band < otherFilters.Count)
                otherFilters[band].Bypass = bypass;
            await Task.Run(() => _device.SetBandBypass(other, band, bypass));
        }

        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();
        return success;
    }

    /// <summary>
    /// Set a crossover band recipe on an output channel. Updates the local
    /// cache and sends the full 16-byte EqParamPacket via REQ_SET_EQ_PARAM with
    /// the wire band index (XoverBandBase + localBand = 20..23). Crossover has
    /// no L/R linking — it applies per output driver, not to the master bus.
    /// </summary>
    public async Task<bool> SetXoverFilter(int channel, int localBand, FilterParams p)
    {
        CancelLiveFilterSends();   // this awaited write is authoritative — no stale live write after it
        int wireBand = CrossoverFilter.XoverBandBase + localBand;
        CancelDeferredFilterSend(channel, wireBand);
        if (_xoverData.TryGetValue(channel, out var bands) && localBand < bands.Count)
            bands[localBand] = p;

        var success = await Task.Run(() => { lock (_filterWriteGate) return _device.SetFilter(channel, wireBand, p); });

        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();
        return success;
    }

    /// <summary>
    /// Crossover counterpart to <see cref="SetFilterDeferred"/>: updates the
    /// cache immediately and debounces the USB write (500 ms) so wheel-scrubbing
    /// a crossover frequency doesn't flood the bus. Debounced per band, like
    /// PEQ edits.
    /// </summary>
    public void SetXoverFilterDeferred(int channel, int localBand, FilterParams p)
    {
        if (_xoverData.TryGetValue(channel, out var bands) && localBand < bands.Count)
            bands[localBand] = p;

        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();

        int wireBand = CrossoverFilter.XoverBandBase + localBand;
        var token = RestartDeferredFilterSend(channel, wireBand);
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                lock (_filterWriteGate)
                {
                    if (token.IsCancellationRequested) return;
                    _device.SetFilter(channel, wireBand, p);
                }
            }
            catch (TaskCanceledException) { }
        });
    }

    /// <summary>
    /// Toggle the bypass flag on a single crossover band via REQ_SET_BAND_BYPASS
    /// (0xD8) with the wire band index (20..23), preserving its family/freq.
    /// </summary>
    public async Task<bool> SetXoverBandBypass(int channel, int localBand, bool bypass)
    {
        if (_xoverData.TryGetValue(channel, out var bands) && localBand < bands.Count)
            bands[localBand].Bypass = bypass;

        int wireBand = CrossoverFilter.XoverBandBase + localBand;
        var success = await Task.Run(() => _device.SetBandBypass(channel, wireBand, bypass));

        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();
        return success;
    }

    /// <summary>
    /// Bulk-toggle the bypass flag on every configured PEQ band of a channel
    /// (Flat bands are skipped — there's nothing to bypass). Sends one
    /// REQ_SET_BAND_BYPASS per band, mirrors to the linked master channel, then
    /// fires FiltersChanged / CheckDirty once. Used by the "Enable All" /
    /// "Bypass All" status-bar buttons on the PEQ page.
    /// </summary>
    public async Task SetAllBandsBypass(int channel, bool bypass)
    {
        if (!_channelData.TryGetValue(channel, out var filters)) return;
        bool linked = IsInputPairLinked(channel);
        int other = linked ? GetLinkedInputChannel(channel) : -1;

        for (int b = 0; b < filters.Count; b++)
        {
            if (filters[b].Type == FilterType.Flat) continue;
            filters[b].Bypass = bypass;
            int bb = b;
            await Task.Run(() => _device.SetBandBypass(channel, bb, bypass));

            if (linked && _channelData.TryGetValue(other, out var otherFilters) && bb < otherFilters.Count)
            {
                otherFilters[bb].Bypass = bypass;
                await Task.Run(() => _device.SetBandBypass(other, bb, bypass));
            }
        }

        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();
    }

    /// <summary>
    /// Bulk-toggle the bypass flag on every configured crossover band of an
    /// output channel (bands not set to a crossover type are skipped). Backs the
    /// "Enable All" / "Bypass All" status-bar buttons on the XO page.
    /// </summary>
    public async Task SetAllXoverBypass(int channel, bool bypass)
    {
        if (!_xoverData.TryGetValue(channel, out var bands)) return;
        for (int i = 0; i < bands.Count; i++)
        {
            if (!bands[i].Type.IsCrossover()) continue;
            bands[i].Bypass = bypass;
            int wireBand = CrossoverFilter.XoverBandBase + i;
            await Task.Run(() => _device.SetBandBypass(channel, wireBand, bypass));
        }

        FiltersChanged?.Invoke(this, EventArgs.Empty);
        CheckDirty();
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
        _requestedInputSource = source;
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
        // The device keeps 31 UTF-8 bytes: cache what it will hold, so the name
        // doesn't change on the next read (a 31-character name with accents is
        // longer than that).
        if (name != null) name = ChannelNameLimit.Fit(name);
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
        // The device keeps 31 UTF-8 bytes: cache what it will hold, so the name
        // doesn't change on the next read (a 31-character name with accents is
        // longer than that).
        if (name != null) name = ChannelNameLimit.Fit(name);
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
                    UpdatePresetBaseline();
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
                    UpdateSavedSnapshotAfterLoad();
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
                    // Firmware defers the erase and live-state reset to its main
                    // loop, after the fade and the DAC mute hold (up to ~500 ms),
                    // and sends no BULK_INVALIDATED for it, so a read taken too
                    // early would keep the old preset on screen for good. The
                    // main loop also answers control requests, so once the
                    // directory shows the slot empty the reset has run too.
                    WaitForPresetDeletion(slot);
                    _suppressDirtyCheck = true;
                    FetchAll();
                    _dispatcher.TryEnqueue(() =>
                    {
                        UpdateSavedSnapshotAfterLoad();
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
    /// Polls the preset directory until the slot reads empty (the firmware has
    /// run the deferred delete), as the macOS Console does. Gives up after
    /// 1.5 s; the caller's refetch then shows whatever the device has.
    /// </summary>
    private void WaitForPresetDeletion(int slot)
    {
        var deadline = DateTime.UtcNow.AddSeconds(1.5);
        while (DateTime.UtcNow < deadline)
        {
            if (_device.GetPresetDirectory() is { } dir && (dir.OccupiedMask & (1 << slot)) == 0)
                return;
            System.Threading.Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Rename a preset slot.
    /// </summary>
    public async Task<bool> RenamePreset(int slot, string name)
    {
        if (!IsDeviceConnected) return false;
        name = ChannelNameLimit.Fit(name);   // what the device will hold (31 UTF-8 bytes)
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

    /// <summary>
    /// Set output-config persistence mode. 0 = independent, 1 = with preset.
    /// Mode flips which diff applies (the IO block participates in preset dirty
    /// only in with-preset mode), so re-check dirty and notify listeners so the
    /// "Save Output Config" item can update its enabled state.
    /// </summary>
    public async Task<bool> SetOutputConfigMode(byte mode)
    {
        if (!IsDeviceConnected) return false;
        return await Task.Run(() =>
        {
            var ok = _device.SetOutputConfigMode(mode);
            if (ok)
            {
                _outputConfigMode = mode;
                _dispatcher.TryEnqueue(() =>
                {
                    OnPropertyChanged(nameof(OutputConfigMode));
                    CheckDirty();
                });
            }
            return ok;
        });
    }

    /// <summary>
    /// Persist the current live IO config (output pins/types, I2S MCK/BCK,
    /// SPDIF RX pin) into the device-global directory block. Accepted in both
    /// modes; dormant in with-preset mode until the user switches to independent.
    /// </summary>
    public async Task<byte> SaveOutputConfig()
    {
        if (!IsDeviceConnected) return 0xFF;
        // Nothing changed since the last save → no-op. This also makes redundant
        // batched "Save to flash" calls (one per staged IO entry) cheap: only the
        // first actually writes flash; the rest see a clean baseline and return.
        if (_savedSnapshot != null &&
            PresetDiff.IoBlockChanges(_savedSnapshot, PresetSnapshot.Capture(this), this).Count == 0)
            return 0;

        var status = await Task.Run(() => _device.SaveOutputConfig());
        if (status == 0)
        {
            // Advance the output-config baseline (synchronously on the awaiting
            // context) so the prompt clears and follow-up batch saves are no-ops.
            _savedSnapshot?.CopyIoBlockFrom(PresetSnapshot.Capture(this));
            ClearIoUndoLog(); // saved state is the new baseline — nothing to undo
            CheckDirty();
        }
        return status;
    }

    /// <summary>
    /// Revert the live physical-IO block to the last-saved baseline (independent
    /// mode). IO edits are applied to RAM immediately, so "discarding" them means
    /// undoing each recorded edit — in REVERSE order, so a GPIO shuffle unwinds
    /// through the same valid intermediate states the forward edits passed through
    /// (no transient pin conflicts). Best-effort: a field the firmware rejects on
    /// undo simply stays changed and re-appears in the prompt.
    /// </summary>
    public async Task RevertOutputConfig()
    {
        if (!IsDeviceConnected) return;

        // The limiter has no undo log: it goes back to the saved baseline
        // directly, through the gang-safe apply. Only in independent mode,
        // where it is output configuration; with-preset it is a preset edit.
        if (OutputConfigMode == 0 && _savedSnapshot is { } baseline)
        {
            bool prev = _suppressDirtyCheck;
            _suppressDirtyCheck = true;
            try { RevertLimiterTo(baseline.Limiter); }
            finally { _suppressDirtyCheck = prev; }
        }

        List<Action> log;
        lock (_ioUndoLock)
        {
            if (_ioUndoLog.Count == 0) { CheckDirty(); return; }
            log = new List<Action>(_ioUndoLog);
            _ioUndoLog.Clear();
        }

        bool prevDirty = _suppressDirtyCheck;
        bool prevUndo = _suppressUndoRecording;
        _suppressDirtyCheck = true;       // one dirty re-check at the end
        _suppressUndoRecording = true;    // undo SETs must not re-record
        try
        {
            await Task.Run(() =>
            {
                for (int i = log.Count - 1; i >= 0; i--)
                    log[i](); // restore that edit's pre-change value
            });
        }
        finally
        {
            _suppressDirtyCheck = prevDirty;
            _suppressUndoRecording = prevUndo;
        }

        CheckDirty(); // IO now back at baseline — prompt clears
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
        ClearIoUndoLog(); // new baseline — prior IO edits are no longer undoable
    }

    /// <summary>
    /// The baseline after a preset save, load or delete, or a factory reset. In
    /// independent mode the output config is device-global: the firmware neither
    /// saves it with a preset nor changes it on a load or reset, so output-config
    /// edits not yet saved with "Save Output Config" stay pending, with their
    /// baseline and undo log.
    /// </summary>
    private void UpdatePresetBaseline()
    {
        if (OutputConfigMode == 0 && _savedSnapshot is { } old)
        {
            var snap = PresetSnapshot.Capture(this);
            snap.CopyIoBlockFrom(old);
            _savedSnapshot = snap;
        }
        else
        {
            UpdateSavedSnapshot();
        }
    }

    /// <summary>The baseline after a preset load or factory reset, whose input
    /// source switch may still be on its way (see _presetSourceSettleUntil).</summary>
    private void UpdateSavedSnapshotAfterLoad()
    {
        UpdatePresetBaseline();
        _presetSourceSettleUntil = DateTime.UtcNow + PresetSourceSettleTime;
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
        // Input pins/channels/rate are plain fields updated by the bulk parse;
        // fan out their PropertyChanged so the input settings pages re-sync
        // instead of showing stale values after a (re)connect or preset load.
        OnPropertyChanged(nameof(I2sRxPin));
        OnPropertyChanged(nameof(I2sInputChannels));
        OnPropertyChanged(nameof(I2sInputRateHz));
        OnPropertyChanged(nameof(SampleRateHz));
    }

    /// <summary>
    /// Recompute PresetsDirty by comparing current state against the saved snapshot.
    /// </summary>
    private IReadOnlyList<PresetDiff.IoChange> _outputConfigChanges = Array.Empty<PresetDiff.IoChange>();
    private string _ioSignature = "";

    // Ordered undo log of individual IO-block edits since the last save/baseline.
    // Discard replays it in REVERSE (newest edit undone first) so a GPIO shuffle
    // between IO functions unwinds through the same valid intermediate states the
    // forward edits passed through — avoiding transient pin conflicts a fixed-order
    // baseline restore could hit. Each entry re-applies its field's pre-edit value.
    private readonly List<Action> _ioUndoLog = new();
    private readonly object _ioUndoLock = new();
    private bool _suppressUndoRecording;

    private void RecordIoUndo(Action revert)
    {
        if (_suppressUndoRecording) return;
        lock (_ioUndoLock) _ioUndoLog.Add(revert);
    }

    private void ClearIoUndoLog()
    {
        lock (_ioUndoLock) _ioUndoLog.Clear();
    }

    /// <summary>Raised when the set of unsaved output-config (IO block) changes
    /// may have changed — the settings window re-syncs its pending entries on it.</summary>
    public event EventHandler? OutputConfigStateChanged;

    /// <summary>Current unsaved IO-block changes (empty unless in independent
    /// mode). One entry per changed field; cached from the last CheckDirty.</summary>
    public IReadOnlyList<PresetDiff.IoChange> GetOutputConfigChanges() => _outputConfigChanges;

    private void CheckDirty()
    {
        if (_suppressDirtyCheck) return;
        if (_savedSnapshot == null) return;
        var current = PresetSnapshot.Capture(this);
        var changes = PresetDiff.Diff(_savedSnapshot, current, this);
        PresetsDirty = changes.Count > 0;

        // Independent-mode IO edits persist via "Save Output Config", not with the
        // preset. Track them per-field so the settings window shows an accurate
        // device-level change count; fire the event only when the set changes.
        var io = OutputConfigMode == 0
            ? (IReadOnlyList<PresetDiff.IoChange>)PresetDiff.IoBlockChanges(_savedSnapshot, current, this)
            : Array.Empty<PresetDiff.IoChange>();
        _outputConfigChanges = io;
        OutputConfigDirty = io.Count > 0;

        var sig = string.Join("|", io.Select(c => c.Key + "=" + c.New));
        if (sig != _ioSignature)
        {
            _ioSignature = sig;
            OutputConfigStateChanged?.Invoke(this, EventArgs.Empty);
        }
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

        // Level offset from the channel's gain control — output gain for outputs,
        // preamp for inputs. User-optional (Graphing → Style); off = pure filter
        // response.
        float levelOffset = 0f;
        if (Models.AppSettings.Instance.GraphLevelIncludesGain)
        {
            if (channel.IsOutput)
            {
                levelOffset = GetChannelGain(channel);
            }
            else
            {
                int id = (int)channel.Id;
                int wireInput = ChannelMap.IsExtraInput(id)
                    ? ChannelMap.AppInputCount + (id - ChannelMap.ExtraInputFirstId)
                    : id;
                levelOffset = InputPreampAt(wireInput);
            }
        }

        // If master channel and bypass is on, return flat response. The preamp
        // stage sits outside the EQ bypass, so its offset still applies.
        if ((channel.Id == ChannelId.MasterLeft || channel.Id == ChannelId.MasterRight) && Bypass)
        {
            var freqs = new float[201];
            var mags = new float[201];
            for (int i = 0; i < 201; i++)
            {
                float pct = i / 200.0f;
                freqs[i] = MathF.Pow(10, MathF.Log10(10) + pct * (MathF.Log10(20000) - MathF.Log10(10)));
                mags[i] = levelOffset;
            }
            return (freqs, mags);
        }

        // Output channels: fold the crossover bands into the curve alongside the
        // PEQ bands. (The global EQ-bypass flatten above only applies to master
        // channels; per the crossover spec the XO stage is never bypassed by it,
        // so output curves always include crossover.)
        IEnumerable<FilterParams> curveFilters = filters;
        if (channel.IsOutput &&
            _xoverData.TryGetValue((int)channel.Id, out var xbands) && xbands.Count > 0)
            curveFilters = filters.Concat(xbands);

        var result = DspMath.GenerateResponseCurve(curveFilters);

        if (MathF.Abs(levelOffset) > 0.001f)
        {
            for (int i = 0; i < result.magnitudes.Length; i++)
                result.magnitudes[i] += levelOffset;
        }

        return result;
    }

    /// <summary>
    /// Phase-response curve (degrees) for a channel, matching <see cref="GetResponseCurve"/>'s
    /// filter set (PEQ + folded crossover on outputs). The output gain offset is a
    /// real scalar and does not affect phase, so it is not applied here.
    /// </summary>
    public (float[] frequencies, float[] phases) GetPhaseCurve(Channel channel, bool unwrap)
    {
        if (!_channelData.TryGetValue((int)channel.Id, out var filters))
            return (Array.Empty<float>(), Array.Empty<float>());

        // Master channels with global EQ bypass → flat (zero) phase.
        if ((channel.Id == ChannelId.MasterLeft || channel.Id == ChannelId.MasterRight) && Bypass)
        {
            var freqs = new float[201];
            var phase = new float[201];
            for (int i = 0; i < 201; i++)
            {
                float pct = i / 200.0f;
                freqs[i] = MathF.Pow(10, MathF.Log10(10) + pct * (MathF.Log10(20000) - MathF.Log10(10)));
                phase[i] = 0;
            }
            return (freqs, phase);
        }

        IEnumerable<FilterParams> curveFilters = filters;
        if (channel.IsOutput &&
            _xoverData.TryGetValue((int)channel.Id, out var xbands) && xbands.Count > 0)
            curveFilters = filters.Concat(xbands);

        return DspMath.GeneratePhaseCurve(curveFilters, unwrap);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer.Stop();
        _pollTimer.Dispose();

        // Deliver queued writes, then turn off the subharmonic solo: it is
        // runtime-only state its window clears on close, but on quit that
        // window closes after the device is gone, and solo would outlive the app.
        Task queued;
        lock (_deviceQueueLock) queued = _deviceQueue;
        try { queued.Wait(500); } catch { }
        if (SubharmSolo && _device.IsConnected)
            try { _device.SetSubharmSolo(false); } catch { }

        _device.Dispose();

        GC.SuppressFinalize(this);
    }
}
