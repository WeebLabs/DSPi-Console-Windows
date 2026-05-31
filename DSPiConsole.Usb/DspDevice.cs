using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
namespace DSPiConsole.Usb;

/// <summary>
/// Command IDs for the vendor interface (matching firmware REQ_* defines)
/// These are sent as bRequest in USB control transfers to Interface 2.
/// </summary>
public static class VendorCommands
{
    public const byte SetEqParam = 0x42;
    public const byte GetEqParam = 0x43;
    public const byte SetPreamp = 0x44;
    public const byte GetPreamp = 0x45;
    public const byte SetBypass = 0x46;
    public const byte GetBypass = 0x47;
    public const byte SetDelay = 0x48;
    public const byte GetDelay = 0x49;
    public const byte GetStatus = 0x50;
    public const byte SaveParams = 0x51;
    public const byte LoadParams = 0x52;
    public const byte FactoryReset = 0x53;
    public const byte SetChannelGain = 0x54;
    public const byte GetChannelGain = 0x55;
    public const byte SetChannelMute = 0x56;
    public const byte GetChannelMute = 0x57;
    public const byte SetLoudnessEnabled = 0x58;
    public const byte GetLoudnessEnabled = 0x59;
    public const byte SetLoudnessRefSPL = 0x5A;
    public const byte GetLoudnessRefSPL = 0x5B;
    public const byte SetLoudnessIntensity = 0x5C;
    public const byte GetLoudnessIntensity = 0x5D;
    public const byte SetCrossfeedEnabled = 0x5E;
    public const byte GetCrossfeedEnabled = 0x5F;
    public const byte SetCrossfeedPreset = 0x60;
    public const byte GetCrossfeedPreset = 0x61;
    public const byte SetCrossfeedFreq = 0x62;
    public const byte GetCrossfeedFreq = 0x63;
    public const byte SetCrossfeedFeed = 0x64;
    public const byte GetCrossfeedFeed = 0x65;
    public const byte SetCrossfeedItd = 0x66;
    public const byte GetCrossfeedItd = 0x67;
    public const byte SetMatrixRoute = 0x70;
    public const byte GetMatrixRoute = 0x71;
    public const byte SetOutputEnable = 0x72;
    public const byte GetOutputEnable = 0x73;
    public const byte SetOutputGain = 0x74;
    public const byte GetOutputGain = 0x75;
    public const byte SetOutputMute = 0x76;
    public const byte GetOutputMute = 0x77;
    public const byte SetOutputDelay = 0x78;
    public const byte GetOutputDelay = 0x79;
    public const byte SetOutputPin = 0x7C;
    public const byte GetOutputPin = 0x7D;
    public const byte GetSerial   = 0x7E;
    public const byte GetPlatform = 0x7F;
    public const byte ClearClips = 0x83;
    public const byte GetAllParams = 0xA0;

    // Buffer statistics (firmware v3+)
    public const byte GetBufferStats  = 0xB0;
    public const byte ResetBufferStats = 0xB1;

    // Preset system (firmware v3+)
    public const byte PresetSave           = 0x90;
    public const byte PresetLoad           = 0x91;
    public const byte PresetDelete         = 0x92;
    public const byte PresetGetName        = 0x93;
    public const byte PresetSetName        = 0x94;
    public const byte PresetGetDir         = 0x95;
    public const byte PresetSetStartup     = 0x96;
    public const byte PresetGetStartup     = 0x97;
    public const byte PresetSetIncludePins = 0x98;
    public const byte PresetGetIncludePins = 0x99;
    public const byte PresetGetActive      = 0x9A;
    public const byte SetChannelName       = 0x9B;
    public const byte GetChannelName       = 0x9C;

    // I2S output configuration
    public const byte SetOutputType    = 0xC0;
    public const byte GetOutputType    = 0xC1;
    public const byte SetI2SBckPin     = 0xC2;
    public const byte GetI2SBckPin     = 0xC3;
    public const byte SetMckEnable     = 0xC4;
    public const byte GetMckEnable     = 0xC5;
    public const byte SetMckPin        = 0xC6;
    public const byte GetMckPin        = 0xC7;
    public const byte SetMckMultiplier = 0xC8;
    public const byte GetMckMultiplier = 0xC9;

    // Per-input-channel preamp and master volume (V6+)
    public const byte SetPreampCh            = 0xD0;
    public const byte GetPreampCh            = 0xD1;
    public const byte SetMasterVolume        = 0xD2;
    public const byte GetMasterVolume        = 0xD3;
    public const byte SetMasterVolumeMode    = 0xD4;
    public const byte GetMasterVolumeMode    = 0xD5;
    public const byte SaveMasterVolume       = 0xD6;
    public const byte GetSavedMasterVolume   = 0xD7;

    // Per-band bypass (firmware 1.1.4+). See band_bypass_spec.md.
    public const byte SetBandBypass          = 0xD8;
    public const byte GetBandBypass          = 0xD9;

    // Vendor-channel user volume (V9+). Same audio_state.volume field the UAC1
    // host slider drives, exposed here as float dB. Applied regardless of input
    // source. Mute is a separate vendor flag (0xDC/0xDD) — not used by the
    // sidebar control today, which exposes only the dB axis like the macOS app.
    public const byte SetUserVolume          = 0xDA;
    public const byte GetUserVolume          = 0xDB;
    public const byte SetUserMute            = 0xDC;
    public const byte GetUserMute            = 0xDD;

    // Volume leveller
    public const byte SetLevellerEnabled   = 0xB4;
    public const byte GetLevellerEnabled   = 0xB5;
    public const byte SetLevellerAmount    = 0xB6;
    public const byte GetLevellerAmount    = 0xB7;
    public const byte SetLevellerSpeed     = 0xB8;
    public const byte GetLevellerSpeed     = 0xB9;
    public const byte SetLevellerMaxGain   = 0xBA;
    public const byte GetLevellerMaxGain   = 0xBB;
    public const byte SetLevellerLookahead = 0xBC;
    public const byte GetLevellerLookahead = 0xBD;
    public const byte SetLevellerGate      = 0xBE;
    public const byte GetLevellerGate      = 0xBF;

    // Input source switching (V7+)
    public const byte SetInputSource       = 0xE0;
    public const byte GetInputSource       = 0xE1;
    public const byte GetSpdifRxStatus     = 0xE2;
    public const byte GetSpdifRxChStatus   = 0xE3;
    public const byte SetSpdifRxPin        = 0xE4;
    public const byte GetSpdifRxPin        = 0xE5;

    // External DAC hardware mute (V10+). Fire-and-forget SET — validation and
    // flash persistence happen in the firmware's main loop; the USB response
    // returns before the apply lands. Hosts confirm by following up with GET
    // (see dac_hardware_mute_spec.md §3.2).
    public const byte SetDacHwMuteConfig   = 0xEA;
    public const byte GetDacHwMuteConfig   = 0xEB;
    public const byte TestDacHwMute        = 0xEC;

    // LG Sound Sync (V8+). Decodes the LG TV's TOSLINK volume / mute messages
    // and applies them through the user-volume path. Only the enable toggle is
    // host-writable; volume/mute/present state are runtime-only fields exposed
    // through the bulk params and the 16-byte status struct (0xE8). Older
    // firmware STALLs the GET so the host treats null as "feature unsupported".
    public const byte SetLgSoundSyncEnable = 0xE6;
    public const byte GetLgSoundSyncEnable = 0xE7;
    public const byte GetLgSoundSyncStatus = 0xE8;

    // Bootloader
    public const byte EnterBootloader = 0xF0;
}

/// <summary>
/// Input source enum (V7+ firmware).
/// </summary>
public enum InputSource : byte
{
    Usb = 0,
    Spdif = 1
}

/// <summary>
/// S/PDIF receiver state machine (V7+).
/// </summary>
public enum SpdifInputState : byte
{
    Inactive = 0,
    Acquiring = 1,
    Locked = 2,
    Relocking = 3
}

/// <summary>
/// Origin tag attached to every PARAM_CHANGED / BULK_INVALIDATED notification
/// the device pushes via the bulk IN endpoint. Lets the host suppress its own
/// echoes (e.g., ignore a host-set master volume notification because the UI
/// already moved the slider).
/// </summary>
public enum ParamSource : byte
{
    Unknown  = 0,
    HostSet  = 1,  // host EP0 SET (vendor REQ_SET_*)
    BulkSet  = 2,  // REQ_SET_ALL_PARAMS
    Preset   = 3,  // preset load
    Factory  = 4,  // factory reset
    Gpio     = 5,  // hardware control (knobs, encoders)
    Internal = 6,  // firmware-initiated (clamp, recalc)
    Uac1     = 7   // UAC1 Feature Unit SET_CUR (OS volume slider, mute key)
}

/// <summary>
/// A device-pushed channel name change. Decoded from a v2 PARAM_CHANGED packet
/// targeting WireBulkParams.channel_names.names[ChannelIndex].
/// </summary>
public readonly struct ChannelNameNotification
{
    public int ChannelIndex { get; init; }
    public string Name { get; init; }
    public ParamSource Source { get; init; }
}

/// <summary>
/// A device-pushed EQ band parameter change. Decoded from a v2 PARAM_CHANGED
/// packet targeting WireBulkParams.eq[Channel][Band] (16-byte WireBandParams).
/// Fired for any origin — GPIO knob turns, preset loads, factory resets, other
/// hosts' EP0 writes. Subscribers should suppress <see cref="ParamSource.HostSet"/>
/// to skip echoes of their own writes.
/// </summary>
public readonly struct BandParamNotification
{
    public int Channel { get; init; }
    public int Band { get; init; }
    public FilterParams Params { get; init; }
    public ParamSource Source { get; init; }
}

/// <summary>
/// A device-pushed user-volume change. Decoded from a v2 PARAM_CHANGED packet
/// targeting WireBulkParams.user_volume.user_volume_db (4-byte float dB).
/// Fired for any origin: <see cref="ParamSource.HostSet"/> for echoes of our
/// own REQ_SET_USER_VOLUME writes, <see cref="ParamSource.Unknown"/> when the
/// UAC1 class driver mirrors a system-tray / hardware-key volume change into
/// audio_state.volume, and other sources for future GPIO knob support.
/// Subscribers should suppress HostSet to avoid round-tripping their own writes.
/// </summary>
public readonly struct UserVolumeNotification
{
    public float Db { get; init; }
    public ParamSource Source { get; init; }
}

/// <summary>
/// Raw notification-endpoint packet — emitted by <c>DspDevice.NotifyPacketReceived</c>
/// for every read on EP3 (IDLE keep-alives, recognized events, unknown event IDs).
/// The byte array is a defensive copy of the actual length the wire returned,
/// safe to retain past the next notify-loop iteration.
/// </summary>
public readonly struct NotifyPacket
{
    public byte[] Data { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Parsed REQ_GET_SPDIF_RX_STATUS (0xE2) response — 16 bytes.
/// </summary>
public struct SpdifRxStatus
{
    public SpdifInputState State;
    public InputSource ActiveSource;
    public byte LockCount;
    public byte LossCount;
    public uint SampleRate;
    public uint ParityErrors;
    public ushort FifoFillPct;
}

/// <summary>
/// Flash operation result codes from firmware.
/// </summary>
public static class FlashResult
{
    public const byte Ok = 0;
    public const byte ErrWrite = 1;
    public const byte ErrNoData = 2;
    public const byte ErrCrc = 3;
}

/// <summary>
/// Pin configuration result codes from firmware.
/// </summary>
public static class PinConfigResult
{
    public const byte Success = 0x00;
    public const byte InvalidPin = 0x01;
    public const byte PinInUse = 0x02;
    public const byte InvalidOutput = 0x03;
    public const byte OutputActive = 0x04;
}

/// <summary>
/// Preset operation result codes from firmware.
/// </summary>
public static class PresetResult
{
    public const byte Ok = 0x00;
    public const byte InvalidSlot = 0x01;
    public const byte SlotEmpty = 0x02;
    public const byte CrcFailure = 0x03;
    public const byte FlashWriteError = 0x04;
}

/// <summary>
/// Output slot type: S/PDIF or I2S.
/// </summary>
public enum OutputSlotType : byte
{
    Spdif = 0,
    I2S = 1
}

/// <summary>
/// Directory info returned by PresetGetDir (0x95): 6 bytes (legacy) or 7 bytes (V12+).
/// </summary>
public struct PresetDirectoryInfo
{
    public ushort OccupiedMask;   // bit N = slot N occupied
    public byte StartupMode;     // 0=last used, 1=specific slot, 2=factory defaults
    public byte DefaultSlot;
    public byte LastActiveSlot;   // 0xFF if none
    public bool IncludePins;
    public byte MasterVolumeMode; // 0=independent/global, 1=with preset (V12+)
}

/// <summary>
/// Identifies a discovered DSPi device without holding an open handle.
/// </summary>
public record DSPiDeviceInfo(string Serial, string DevicePath)
{
    public string DisplayName => Serial.Length >= 8 ? $"DSPi ({Serial[^8..]})" : "DSPi";
}

/// <summary>
/// Manages USB communication with the DSPi device using LibUsbDotNet.
/// Uses USB Control Transfers on Interface 2 (vendor-specific, control-only).
/// </summary>
public partial class DspDevice : ObservableObject, IDisposable
{
    private readonly IDspiTransfer _usb;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Is this a USB or Remote device?
    /// </summary>
    public bool IsDeviceUsb => _usb.DeviceType == "USB";

    /// <summary>
    /// Number of audio channels (set after GetDeviceInfo). RP2040=7, RP2350=11.
    /// </summary>
    public int NumChannels { get; set; } = 5; // Legacy default (5 peaks)

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private SystemStatus? _currentStatus;

    /// <summary>All currently connected DSPi devices.</summary>
    public IReadOnlyList<DSPiDeviceInfo> AvailableDevicesList => _usb.AvailableDevices;

    /// <summary>The currently selected/active device.</summary>
    public DSPiDeviceInfo? SelectedDeviceInfo => _usb.SelectedDeviceInfo;

    public event EventHandler? DeviceConnected;
    public event EventHandler? DeviceDisconnected;
    public event EventHandler<SystemStatus>? StatusUpdated;
    public event EventHandler? AvailableDevicesChanged;

    /// <summary>Fired when the device pushes a channel-name update via the
    /// bulk IN notification endpoint. Channel index is in [0, 10].</summary>
    public event EventHandler<ChannelNameNotification>? ChannelNameNotified;

    /// <summary>Fired when the device tells the host to re-read full state
    /// (preset load, factory reset, bulk SET).</summary>
    public event EventHandler<ParamSource>? BulkInvalidated;

    /// <summary>Fired when a preset slot was loaded on the device. Always
    /// followed shortly by <see cref="BulkInvalidated"/>.</summary>
    public event EventHandler<byte>? PresetLoadedNotified;

    /// <summary>Fired when the device pushes an active-input-source change via
    /// the notification endpoint. Catches the late-arriving notification after
    /// the firmware's main loop applies a deferred input source switch (preset
    /// load may emit BULK_INVALIDATED before the switch lands, so the bulk
    /// fetch can return stale data; this event closes that race).</summary>
    public event EventHandler<InputSource>? InputSourceNotified;

    /// <summary>Fired when the firmware emits a per-band PARAM_CHANGED notification
    /// (offset inside WireBulkParams.eq[][], size 16). Origin tag lets subscribers
    /// suppress their own EP0 echoes. Will become the live-update path for GPIO
    /// knobs once the firmware ships that feature.</summary>
    public event EventHandler<BandParamNotification>? BandParamNotified;

    /// <summary>Fired when the firmware emits a PARAM_CHANGED notification for
    /// audio_state.volume (the vendor-channel user volume, in dB). Origin tag
    /// distinguishes a UAC1 host echo (system tray, hardware volume keys) from
    /// our own REQ_SET_USER_VOLUME write — subscribers should suppress
    /// <see cref="ParamSource.HostSet"/> to avoid round-tripping their own writes.</summary>
    public event EventHandler<UserVolumeNotification>? UserVolumeNotified;

    /// <summary>Fired for every raw packet read from the notification endpoint —
    /// IDLE keep-alives, decoded events, unknown event IDs, malformed packets.
    /// Diagnostic hook used by the Bulk Endpoint Monitor window. Fires on the
    /// notify background thread; subscribers must marshal to the UI thread.</summary>
    public event EventHandler<NotifyPacket>? NotifyPacketReceived;

    private const int ChannelNamesWireOffset = 2480; // offsetof(WireBulkParams, channel_names)
    private const int WireChannelNameLen = 32;

    // V7 input config block sits at offsetof(WireBulkParams, input_config) = 2896.
    // input_source occupies the first byte of the 16-byte WireInputConfig struct.
    private const int InputSourceWireOffset = 2896;
    
    // V9+ user volume block sits at offsetof(WireBulkParams, user_volume) = 2928
    // (after input_config @ 2896 and lg_sound_sync @ 2912). user_volume_db
    // occupies the first 4 bytes (float dB) of the 16-byte WireUserVolume
    // struct; user_mute lives at +4.
    private const int UserVolumeWireOffset = 2928;
    
    public DspDevice() : this(CreateDefaultTransfer()) { }

    private static IDspiTransfer CreateDefaultTransfer()
    {
        // Check for arguments to the command to decide between USB and Remote
        // So sample arguments would be
        //        DSPiConsole -r <host> [-p <port>] for remote support
        // if there is no -r argument then assumed USB
        string[] cmdargs = Environment.GetCommandLineArgs();
        string remoteHost = string.Empty;
        int remotePort = 0;
        for (int cmd = 0; cmd < (cmdargs.Length-1); cmd++)
        {
            var argname = cmdargs[cmd];
            var argvalue = cmdargs[cmd + 1];
            switch (argname)
            {
                case "-r":
                    remoteHost = argvalue;
                    break;
                case "-p":
                    if (!int.TryParse(argvalue, out remotePort))
                        remotePort = 0;
                    break;
                default:
                    break;
            }
        }
        // pick either or
        if (!string.IsNullOrEmpty(remoteHost))
        {
            if (remotePort > 0)
                return new DspiRemote(remoteHost, remotePort);
            else // use default port
                return new DspiRemote(remoteHost);
        }
        return new DspiUsb();
    }

    public DspDevice(IDspiTransfer usb)
    {
        _usb = usb;
        _usb.NotifyPacketReceived += (s, data) => ProcessNotifyPacket(data, data.Length);
        _usb.AvailableDevicesChanged += (s, e) => {
            OnPropertyChanged(nameof(AvailableDevicesList));
            AvailableDevicesChanged?.Invoke(this, EventArgs.Empty);
        };
        _usb.DeviceConnected += (s, e) => {
            IsConnected = true;
            ErrorMessage = null;
            OnPropertyChanged(nameof(SelectedDeviceInfo));
            DeviceConnected?.Invoke(this, EventArgs.Empty);
        };
        _usb.DeviceDisconnected += (s, e) => {
            IsConnected = false;
            ErrorMessage = "Disconnected";
            OnPropertyChanged(nameof(SelectedDeviceInfo));
            DeviceDisconnected?.Invoke(this, EventArgs.Empty);
        };
        _usb.StatusPollRequested += (s, e) => PollStatus();
    }

    public void StartMonitoring()
    {
        _usb.StartMonitoring();
    }

    public void StopMonitoring()
    {
        _usb.StopMonitoring();
    }


    /// <summary>
    /// Switch to a different connected device. Called from ViewModel after unsaved changes check.
    /// </summary>
    public void SelectDevice(DSPiDeviceInfo device)
    {
        if (device.Serial == _usb.OpenDeviceSerial && IsConnected) return;
        _usb.OpenDevice(device);
    }

    /// <summary>
    /// Poll status using control transfer. Called by timer.
    /// </summary>
    private void PollStatus()
    {
        if (_disposed || !IsConnected) 
            return;

        try
        {
            var status = GetStatus();
            if (status != null)
            {
                CurrentStatus = status;
                StatusUpdated?.Invoke(this, status);
            }
        }
        catch
        {
            // Ignore polling errors
        }
    }

    private SystemStatus ParseStatusResponse(byte[] buffer)
    {
        // Platform-aware status packet:
        // numChannels * uint16 peaks + cpu0(1) + cpu1(1) + clipFlags uint16(2)
        int numCh = NumChannels;
        int peakBytes = numCh * 2;

        var peaks = new float[11]; // Always 11 slots (max channels)
        for (int i = 0; i < numCh && (i * 2 + 1) < buffer.Length; i++)
        {
            peaks[i] = BitConverter.ToUInt16(buffer, i * 2) / 32767.0f;
        }

        int cpuOffset = peakBytes;
        int cpu0 = cpuOffset < buffer.Length ? buffer[cpuOffset] : 0;
        int cpu1 = cpuOffset + 1 < buffer.Length ? buffer[cpuOffset + 1] : 0;

        ushort clipFlags = 0;
        int clipOffset = cpuOffset + 2;
        if (clipOffset + 1 < buffer.Length)
        {
            clipFlags = BitConverter.ToUInt16(buffer, clipOffset);
        }

        return new SystemStatus
        {
            Peaks = peaks,
            Cpu0Load = cpu0,
            Cpu1Load = cpu1,
            ClipFlags = clipFlags
        };
    }

    public void Disconnect()
    {
        _usb.Disconnect();
    }

    public void Reconnect()
    {
        _usb.Reconnect();
    }

    /// <summary>
    /// Send a vendor control OUT transfer (host to device).
    /// libusb-1.0 returns the number of bytes transferred, or a negative
    /// LIBUSB_ERROR_* on failure.
    /// </summary>
    private bool ControlTransferOut(byte request, ushort value = 0, byte[]? data = null)
    {
        return _usb.ControlTransferOut(request, value, data);
    }

    /// <summary>
    /// Send a vendor control IN transfer (device to host).
    /// </summary>
    private byte[]? ControlTransferIn(byte request, ushort value = 0, int length = 4)
    {
        return _usb.ControlTransferIn(request, value, length);
    }

    #region High-Level Commands

    // EQ parameter indices for wValue encoding
    private const int EqParamType = 0;
    private const int EqParamFreq = 1;
    private const int EqParamQ = 2;
    private const int EqParamGain = 3;

    /// <summary>
    /// Encode wValue for EQ parameter access: (channel &lt;&lt; 8) | (band &lt;&lt; 4) | param
    /// </summary>
    private static ushort EncodeEqValue(int channel, int band, int param = 0)
    {
        return (ushort)((channel << 8) | (band << 4) | param);
    }

    /// <summary>
    /// Set EQ filter parameters for a specific channel and band.
    /// Sends 16-byte EqParamPacket: channel(1), band(1), type(1), bypass(1), freq(4), Q(4), gain(4).
    /// The bypass byte at offset 3 is firmware 1.1.4+ (formerly `reserved`); older
    /// firmware ignores it, so it is always safe to populate. Firmware treats
    /// strictly the value 1 as bypassed — see band_bypass_spec.md §5.
    /// </summary>
    public bool SetFilter(int channel, int band, FilterParams p)
    {
        var data = new byte[16];
        data[0] = (byte)channel;
        data[1] = (byte)band;
        data[2] = (byte)p.Type;
        data[3] = p.Bypass ? (byte)1 : (byte)0;
        BitConverter.GetBytes(p.Frequency).CopyTo(data, 4);
        BitConverter.GetBytes(p.Q).CopyTo(data, 8);
        BitConverter.GetBytes(p.Gain).CopyTo(data, 12);

        return ControlTransferOut(VendorCommands.SetEqParam, 0, data);
    }

    /// <summary>
    /// Get EQ filter parameters for a specific channel and band.
    /// Reads each parameter individually (4 bytes each) like the Python script.
    /// </summary>
    public FilterParams? GetFilter(int channel, int band)
    {
        // Read type (returned as uint32)
        var typeData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(channel, band, EqParamType), 4);
        if (typeData == null || typeData.Length < 4) return null;
        var type = BitConverter.ToUInt32(typeData, 0);

        // Read frequency (float)
        var freqData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(channel, band, EqParamFreq), 4);
        if (freqData == null || freqData.Length < 4) return null;
        var freq = BitConverter.ToSingle(freqData, 0);

        // Read Q (float)
        var qData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(channel, band, EqParamQ), 4);
        if (qData == null || qData.Length < 4) return null;
        var q = BitConverter.ToSingle(qData, 0);

        // Read gain (float)
        var gainData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(channel, band, EqParamGain), 4);
        if (gainData == null || gainData.Length < 4) return null;
        var gain = BitConverter.ToSingle(gainData, 0);

        return new FilterParams
        {
            Type = (FilterType)type,
            Frequency = freq,
            Q = q,
            Gain = gain
        };
    }

    /// <summary>
    /// Toggle per-band bypass via the dedicated single-byte opcode 0xD8 (firmware
    /// 1.1.4+). Cheaper than re-sending the whole 16-byte EqParamPacket and the
    /// firmware preserves the band's current freq/Q/gain. Older firmware STALLs
    /// the request and this returns false — gate the call on a capability probe.
    /// </summary>
    public bool SetBandBypass(int channel, int band, bool bypass)
    {
        ushort wValue = (ushort)((channel << 8) | (band & 0xFF));
        return ControlTransferOut(VendorCommands.SetBandBypass, wValue,
            new byte[] { bypass ? (byte)1 : (byte)0 });
    }

    /// <summary>
    /// Read per-band bypass via opcode 0xD9 (firmware 1.1.4+). Returns null if
    /// the device STALLs (older firmware) — call once after connect to detect
    /// support, then use the cached capability flag for subsequent toggles.
    /// </summary>
    public bool? GetBandBypass(int channel, int band)
    {
        ushort wValue = (ushort)((channel << 8) | (band & 0xFF));
        var data = ControlTransferIn(VendorCommands.GetBandBypass, wValue, 1);
        if (data == null || data.Length < 1) return null;
        return data[0] == 1;
    }

    /// <summary>
    /// Legacy set preamp (opcode 0x44). Firmware applies the value to all
    /// input channels uniformly. Prefer SetInputPreamp for V6+ firmware.
    /// </summary>
    public bool SetPreamp(float db)
    {
        var data = BitConverter.GetBytes(db);
        return ControlTransferOut(VendorCommands.SetPreamp, 0, data);
    }

    /// <summary>
    /// Legacy get preamp (opcode 0x45). Reads channel 0's value. Prefer
    /// GetInputPreamp for V6+ firmware.
    /// </summary>
    public float? GetPreamp()
    {
        var response = ControlTransferIn(VendorCommands.GetPreamp, 0, 4);

        if (response == null || response.Length < 4)
            return null;

        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set per-input-channel preamp in dB. Channel 0 = left, 1 = right.
    /// </summary>
    public bool SetInputPreamp(int channel, float db)
    {
        var data = BitConverter.GetBytes(db);
        return ControlTransferOut(VendorCommands.SetPreampCh, (ushort)channel, data);
    }

    /// <summary>
    /// Get per-input-channel preamp in dB. Channel 0 = left, 1 = right.
    /// </summary>
    public float? GetInputPreamp(int channel)
    {
        var response = ControlTransferIn(VendorCommands.GetPreampCh, (ushort)channel, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set global master volume in dB. Valid adjustment range is
    /// [-127, 0]; -128 is a mute sentinel.
    /// </summary>
    public bool SetMasterVolume(float db)
    {
        var data = BitConverter.GetBytes(db);
        return ControlTransferOut(VendorCommands.SetMasterVolume, 0, data);
    }

    /// <summary>
    /// Get global master volume in dB.
    /// </summary>
    public float? GetMasterVolume()
    {
        var response = ControlTransferIn(VendorCommands.GetMasterVolume, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set master volume persistence mode:
    ///   0 = independent/global (volume is separate from presets, saved via SaveMasterVolume)
    ///   1 = with preset (volume travels with each preset)
    /// </summary>
    public bool SetMasterVolumeMode(byte mode)
    {
        return ControlTransferOut(VendorCommands.SetMasterVolumeMode, 0, new[] { mode });
    }

    /// <summary>
    /// Get master volume persistence mode (0 = independent, 1 = with preset).
    /// </summary>
    public byte? GetMasterVolumeMode()
    {
        var response = ControlTransferIn(VendorCommands.GetMasterVolumeMode, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    /// <summary>
    /// Persist the current live master volume to the directory sector.
    /// Action-style IN transfer (matches REQ_FACTORY_RESET): 1-byte status
    /// response, PRESET_OK (0) on acceptance. Accepted in both modes; dormant
    /// in mode 1. Returns 0xFF on USB transfer failure.
    /// </summary>
    public byte SaveMasterVolume()
    {
        var response = ControlTransferIn(VendorCommands.SaveMasterVolume, 0, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Read the directory's saved master volume (independent mode's baseline).
    /// </summary>
    public float? GetSavedMasterVolume()
    {
        var response = ControlTransferIn(VendorCommands.GetSavedMasterVolume, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set the vendor-channel user volume in dB. Firmware clamps to its
    /// supported range (today: [USER_VOLUME_MIN_DB, 0], -60..0 dB). Applied
    /// regardless of input source; mirrors the UAC1 host slider value
    /// (audio_state.volume). Older firmware (pre-V9) STALLs this opcode.
    /// </summary>
    public bool SetUserVolume(float db)
    {
        var data = BitConverter.GetBytes(db);
        return ControlTransferOut(VendorCommands.SetUserVolume, 0, data);
    }

    /// <summary>
    /// Read the current vendor-channel user volume in dB. Returns null if
    /// the device STALLs (pre-V9 firmware) or the transfer fails.
    /// </summary>
    public float? GetUserVolume()
    {
        var response = ControlTransferIn(VendorCommands.GetUserVolume, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Enable or disable master EQ bypass.
    /// </summary>
    public bool SetBypass(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetBypass, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Get current bypass state.
    /// </summary>
    public bool? GetBypass()
    {
        var response = ControlTransferIn(VendorCommands.GetBypass, 0, 1);

        if (response == null || response.Length < 1)
            return null;

        return response[0] != 0;
    }

    /// <summary>
    /// Set delay for a specific channel in milliseconds.
    /// Channel is encoded in wValue.
    /// </summary>
    public bool SetDelay(int channel, float ms)
    {
        var data = BitConverter.GetBytes(ms);
        return ControlTransferOut(VendorCommands.SetDelay, (ushort)channel, data);
    }

    /// <summary>
    /// Get delay for a specific channel in milliseconds.
    /// </summary>
    public float? GetDelay(int channel)
    {
        var response = ControlTransferIn(VendorCommands.GetDelay, (ushort)channel, 4);

        if (response == null || response.Length < 4)
            return null;

        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Get system status (peak levels, CPU load, clip flags).
    /// wValue=9 requests full status. Packet size = numChannels*2 + 2 (CPU) + 2 (clipFlags).
    /// </summary>
    public SystemStatus? GetStatus()
    {
        int packetSize = NumChannels * 2 + 4; // peaks + cpu0 + cpu1 + clipFlags(2)
        var response = ControlTransferIn(VendorCommands.GetStatus, 9, packetSize);

        if (response == null || response.Length < NumChannels * 2 + 2)
            return null;

        return ParseStatusResponse(response);
    }

    /// <summary>
    /// Save current parameters to flash memory.
    /// Returns FlashResult code.
    /// </summary>
    public byte SaveParams()
    {
        var response = ControlTransferIn(VendorCommands.SaveParams, 0, 1);
        return response != null && response.Length >= 1 ? response[0] : FlashResult.ErrWrite;
    }

    /// <summary>
    /// Load parameters from flash memory.
    /// Returns FlashResult code.
    /// </summary>
    public byte LoadParams()
    {
        var response = ControlTransferIn(VendorCommands.LoadParams, 0, 1);
        return response != null && response.Length >= 1 ? response[0] : FlashResult.ErrWrite;
    }

    /// <summary>
    /// Reset all parameters to factory defaults.
    /// Returns FlashResult code.
    /// </summary>
    public byte FactoryReset()
    {
        var response = ControlTransferIn(VendorCommands.FactoryReset, 0, 1);
        return response != null && response.Length >= 1 ? response[0] : FlashResult.ErrWrite;
    }

    /// <summary>
    /// Set output channel gain in dB. wValue = output index (0=OutL, 1=OutR, 2=Sub).
    /// </summary>
    public bool SetChannelGain(int outputChannel, float db)
    {
        var data = BitConverter.GetBytes(db);
        return ControlTransferOut(VendorCommands.SetChannelGain, (ushort)outputChannel, data);
    }

    /// <summary>
    /// Get output channel gain in dB. wValue = output index (0=OutL, 1=OutR, 2=Sub).
    /// </summary>
    public float? GetChannelGain(int outputChannel)
    {
        var response = ControlTransferIn(VendorCommands.GetChannelGain, (ushort)outputChannel, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set output channel mute state. wValue = output index (0=OutL, 1=OutR, 2=Sub).
    /// </summary>
    public bool SetChannelMute(int outputChannel, bool muted)
    {
        return ControlTransferOut(VendorCommands.SetChannelMute, (ushort)outputChannel, new[] { (byte)(muted ? 1 : 0) });
    }

    /// <summary>
    /// Get output channel mute state. wValue = output index (0=OutL, 1=OutR, 2=Sub).
    /// </summary>
    public bool? GetChannelMute(int outputChannel)
    {
        var response = ControlTransferIn(VendorCommands.GetChannelMute, (ushort)outputChannel, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set loudness compensation enabled state.
    /// </summary>
    public bool SetLoudnessEnabled(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetLoudnessEnabled, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Get loudness compensation enabled state.
    /// </summary>
    public bool? GetLoudnessEnabled()
    {
        var response = ControlTransferIn(VendorCommands.GetLoudnessEnabled, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set loudness reference SPL (40-100 dB, default 83).
    /// </summary>
    public bool SetLoudnessRefSPL(float spl)
    {
        var data = BitConverter.GetBytes(spl);
        return ControlTransferOut(VendorCommands.SetLoudnessRefSPL, 0, data);
    }

    /// <summary>
    /// Get loudness reference SPL.
    /// </summary>
    public float? GetLoudnessRefSPL()
    {
        var response = ControlTransferIn(VendorCommands.GetLoudnessRefSPL, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set loudness intensity (0-200%, default 100).
    /// </summary>
    public bool SetLoudnessIntensity(float intensity)
    {
        var data = BitConverter.GetBytes(intensity);
        return ControlTransferOut(VendorCommands.SetLoudnessIntensity, 0, data);
    }

    /// <summary>
    /// Get loudness intensity.
    /// </summary>
    public float? GetLoudnessIntensity()
    {
        var response = ControlTransferIn(VendorCommands.GetLoudnessIntensity, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set crossfeed enabled state.
    /// </summary>
    public bool SetCrossfeedEnabled(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetCrossfeedEnabled, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Get crossfeed enabled state.
    /// </summary>
    public bool? GetCrossfeedEnabled()
    {
        var response = ControlTransferIn(VendorCommands.GetCrossfeedEnabled, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set crossfeed preset (0=Default, 1=Chu Moy, 2=Jan Meier, 3=Custom).
    /// </summary>
    public bool SetCrossfeedPreset(int preset)
    {
        return ControlTransferOut(VendorCommands.SetCrossfeedPreset, 0, new[] { (byte)preset });
    }

    /// <summary>
    /// Get crossfeed preset.
    /// </summary>
    public int? GetCrossfeedPreset()
    {
        var response = ControlTransferIn(VendorCommands.GetCrossfeedPreset, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    /// <summary>
    /// Set crossfeed cutoff frequency in Hz (500-2000).
    /// </summary>
    public bool SetCrossfeedFreq(float freq)
    {
        var data = BitConverter.GetBytes(freq);
        return ControlTransferOut(VendorCommands.SetCrossfeedFreq, 0, data);
    }

    /// <summary>
    /// Get crossfeed cutoff frequency.
    /// </summary>
    public float? GetCrossfeedFreq()
    {
        var response = ControlTransferIn(VendorCommands.GetCrossfeedFreq, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set crossfeed feed level in dB (0-15).
    /// </summary>
    public bool SetCrossfeedFeed(float feed)
    {
        var data = BitConverter.GetBytes(feed);
        return ControlTransferOut(VendorCommands.SetCrossfeedFeed, 0, data);
    }

    /// <summary>
    /// Get crossfeed feed level.
    /// </summary>
    public float? GetCrossfeedFeed()
    {
        var response = ControlTransferIn(VendorCommands.GetCrossfeedFeed, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set interaural time delay (ITD) enabled state.
    /// </summary>
    public bool SetCrossfeedItd(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetCrossfeedItd, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Get interaural time delay enabled state.
    /// </summary>
    public bool? GetCrossfeedItd()
    {
        var response = ControlTransferIn(VendorCommands.GetCrossfeedItd, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Get a 4-byte unsigned status value. wValue selects the stat type.
    /// </summary>
    public uint? GetStatusUInt32(ushort wValue)
    {
        var response = ControlTransferIn(VendorCommands.GetStatus, wValue, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToUInt32(response, 0);
    }

    /// <summary>
    /// Get a 4-byte signed status value. wValue selects the stat type.
    /// </summary>
    public int? GetStatusInt32(ushort wValue)
    {
        var response = ControlTransferIn(VendorCommands.GetStatus, wValue, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToInt32(response, 0);
    }

    /// <summary>
    /// Set a matrix route: enabled, invert, and gain for a given input/output pair.
    /// 8-byte packet matching firmware MatrixRoutePacket: input(1), output(1),
    /// enabled(1), invert(1), gain(4).
    /// </summary>
    public bool SetMatrixRoute(int input, int output, bool enabled, bool invert, float gain)
    {
        var data = new byte[8];
        data[0] = (byte)input;
        data[1] = (byte)output;
        data[2] = (byte)(enabled ? 1 : 0);
        data[3] = (byte)(invert ? 1 : 0);
        BitConverter.GetBytes(gain).CopyTo(data, 4);
        return ControlTransferOut(VendorCommands.SetMatrixRoute, 0, data);
    }

    /// <summary>
    /// Get a matrix route. wValue = (input &lt;&lt; 8) | output. Returns 8-byte response.
    /// </summary>
    public (bool enabled, bool invert, float gain)? GetMatrixRoute(int input, int output)
    {
        ushort wValue = (ushort)((input << 8) | output);
        var response = ControlTransferIn(VendorCommands.GetMatrixRoute, wValue, 8);
        if (response == null || response.Length < 8) return null;
        bool enabled = response[2] != 0;
        bool invert = response[3] != 0;
        float gain = BitConverter.ToSingle(response, 4);
        return (enabled, invert, gain);
    }

    /// <summary>
    /// Set output enable state. wValue = output index.
    /// </summary>
    public bool SetOutputEnable(int output, bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetOutputEnable, (ushort)output,
            new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Get output enable state. wValue = output index.
    /// </summary>
    public bool? GetOutputEnable(int output)
    {
        var response = ControlTransferIn(VendorCommands.GetOutputEnable, (ushort)output, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set output gain in dB (matrix mixer output gain). wValue = output index.
    /// </summary>
    public bool SetOutputGain(int output, float db)
    {
        var data = BitConverter.GetBytes(db);
        return ControlTransferOut(VendorCommands.SetOutputGain, (ushort)output, data);
    }

    /// <summary>
    /// Get output gain in dB (matrix mixer output gain). wValue = output index.
    /// </summary>
    public float? GetOutputGain(int output)
    {
        var response = ControlTransferIn(VendorCommands.GetOutputGain, (ushort)output, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set output mute state (matrix mixer). wValue = output index.
    /// </summary>
    public bool SetOutputMute(int output, bool muted)
    {
        return ControlTransferOut(VendorCommands.SetOutputMute, (ushort)output,
            new[] { (byte)(muted ? 1 : 0) });
    }

    /// <summary>
    /// Get output mute state (matrix mixer). wValue = output index.
    /// </summary>
    public bool? GetOutputMute(int output)
    {
        var response = ControlTransferIn(VendorCommands.GetOutputMute, (ushort)output, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set output delay in ms (matrix mixer). wValue = output index.
    /// </summary>
    public bool SetOutputDelay(int output, float ms)
    {
        var data = BitConverter.GetBytes(ms);
        return ControlTransferOut(VendorCommands.SetOutputDelay, (ushort)output, data);
    }

    /// <summary>
    /// Get output delay in ms (matrix mixer). wValue = output index.
    /// </summary>
    public float? GetOutputDelay(int output)
    {
        var response = ControlTransferIn(VendorCommands.GetOutputDelay, (ushort)output, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    public string? GetDeviceSerial()
    {
        var response = ControlTransferIn(VendorCommands.GetSerial, 0, 16);
        if (response == null || response.Length < 1) return null;
        return System.Text.Encoding.ASCII.GetString(response).TrimEnd('\0');
    }

    /// <summary>
    /// Clear clip flags on the device.
    /// </summary>
    public void ClearClips()
    {
        ControlTransferIn(VendorCommands.ClearClips, 0, 2);
    }

    public (string Platform, string FirmwareVersion)? GetDeviceInfo()
    {
        var response = ControlTransferIn(VendorCommands.GetPlatform, 0, 4);
        if (response == null || response.Length < 3) return null;
        var platform = response[0] == 1 ? "RP2350" : "RP2040";
        var major = response[1];
        var minor = response[2] >> 4;
        var patch = response[2] & 0x0F;
        return (platform, $"v{major}.{minor}.{patch}");
    }

    /// <summary>
    /// Set output pin assignment. wValue = (pin &lt;&lt; 8) | outputIndex.
    /// Returns status byte (PinConfigResult), or 0xFF on transfer failure.
    /// </summary>
    public byte SetOutputPin(int output, byte pin)
    {
        ushort wValue = (ushort)((pin << 8) | output);
        var response = ControlTransferIn(VendorCommands.SetOutputPin, wValue, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get current GPIO pin for an output. wValue = outputIndex.
    /// Returns pin number, or null on failure.
    /// </summary>
    public byte? GetOutputPin(int output)
    {
        var response = ControlTransferIn(VendorCommands.GetOutputPin, (ushort)output, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    #region I2S Configuration

    /// <summary>
    /// Set output slot type (S/PDIF or I2S). wValue = (type &lt;&lt; 8) | slot.
    /// Returns status byte (PinConfigResult codes), or 0xFF on transfer failure.
    /// </summary>
    public byte SetOutputType(int slot, OutputSlotType type)
    {
        ushort wValue = (ushort)(((byte)type << 8) | slot);
        var response = ControlTransferIn(VendorCommands.SetOutputType, wValue, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get current output type for a slot. wValue = slot index.
    /// </summary>
    public OutputSlotType? GetOutputType(int slot)
    {
        var response = ControlTransferIn(VendorCommands.GetOutputType, (ushort)slot, 1);
        if (response == null || response.Length < 1) return null;
        return (OutputSlotType)response[0];
    }

    /// <summary>
    /// Set I2S BCK (bit clock) pin. LRCLK is always BCK + 1.
    /// Returns status byte, or 0xFF on transfer failure.
    /// </summary>
    public byte SetI2SBckPin(byte pin)
    {
        var response = ControlTransferIn(VendorCommands.SetI2SBckPin, pin, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get current I2S BCK pin number, or null on failure.
    /// </summary>
    public byte? GetI2SBckPin()
    {
        var response = ControlTransferIn(VendorCommands.GetI2SBckPin, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    /// <summary>
    /// Enable or disable master clock (MCK) output.
    /// Returns status byte, or 0xFF on transfer failure.
    /// </summary>
    public byte SetMckEnable(bool enabled)
    {
        ushort wValue = (ushort)(enabled ? 1 : 0);
        var response = ControlTransferIn(VendorCommands.SetMckEnable, wValue, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get whether master clock (MCK) is enabled, or null on failure.
    /// </summary>
    public bool? GetMckEnable()
    {
        var response = ControlTransferIn(VendorCommands.GetMckEnable, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set MCK GPIO pin. MCK must be disabled first.
    /// Returns status byte, or 0xFF on transfer failure.
    /// </summary>
    public byte SetMckPin(byte pin)
    {
        var response = ControlTransferIn(VendorCommands.SetMckPin, pin, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get current MCK GPIO pin, or null on failure.
    /// </summary>
    public byte? GetMckPin()
    {
        var response = ControlTransferIn(VendorCommands.GetMckPin, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    /// <summary>
    /// Set MCK multiplier. Accepts 128 or 256 and encodes to the firmware's
    /// wire value (0 = 128x, 1 = 256x). Returns status byte, or 0xFF on
    /// transfer failure.
    /// </summary>
    public byte SetMckMultiplier(int multiplier)
    {
        ushort encoded = multiplier == 256 ? (ushort)1 : (ushort)0;
        var response = ControlTransferIn(VendorCommands.SetMckMultiplier, encoded, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get current MCK multiplier (128 or 256), or null on failure.
    /// </summary>
    public int? GetMckMultiplier()
    {
        var response = ControlTransferIn(VendorCommands.GetMckMultiplier, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] == 1 ? 256 : 128;
    }

    #endregion

    #region Volume Leveller

    public bool SetLevellerEnabled(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetLevellerEnabled, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    public bool? GetLevellerEnabled()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerEnabled, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    public bool SetLevellerAmount(float amount)
    {
        return ControlTransferOut(VendorCommands.SetLevellerAmount, 0, BitConverter.GetBytes(amount));
    }

    public float? GetLevellerAmount()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerAmount, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    public bool SetLevellerSpeed(int speed)
    {
        return ControlTransferOut(VendorCommands.SetLevellerSpeed, 0, new[] { (byte)speed });
    }

    public int? GetLevellerSpeed()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerSpeed, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    public bool SetLevellerMaxGain(float db)
    {
        return ControlTransferOut(VendorCommands.SetLevellerMaxGain, 0, BitConverter.GetBytes(db));
    }

    public float? GetLevellerMaxGain()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerMaxGain, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    public bool SetLevellerLookahead(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetLevellerLookahead, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    public bool? GetLevellerLookahead()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerLookahead, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    public bool SetLevellerGate(float db)
    {
        return ControlTransferOut(VendorCommands.SetLevellerGate, 0, BitConverter.GetBytes(db));
    }

    public float? GetLevellerGate()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerGate, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    #endregion

    /// <summary>
    /// Reboot the device into UF2 bootloader mode. Device disconnects immediately.
    /// </summary>
    public void EnterBootloaderMode()
    {
        ControlTransferIn(VendorCommands.EnterBootloader, 0, 1);
    }


    private void ProcessNotifyPacket(byte[] buf, int len)
    {
        // 1-byte IDLE keep-alive: discard. Older v1 hosts also see this.
        if (len < 4) return;

        byte version = buf[0];
        byte eventId = buf[1];
        // buf[2] = flags (must be 0 in v2)
        // buf[3] = monotonic seq (gap = loss; could surface metric later)

        // v1 master-volume packet [0x01, 0, 0, 0, float_db_LE]: ignored — the
        // firmware also emits the v2 equivalent.
        if (version == 0x01 || eventId == 0x00) return;
        if (version != 0x02) return;

        switch (eventId)
        {
            case 0x02: // PARAM_CHANGED
                if (len < 12) return;
                ushort offset = BitConverter.ToUInt16(buf, 4);
                ushort size   = BitConverter.ToUInt16(buf, 6);
                var source    = (ParamSource)buf[8];
                if (12 + size > len) return;

                // EQ band change: offset inside WireBulkParams.eq[][], size 16.
                // Single source of truth for layout/decoding is BulkParamsParser.
                // GPIO knob changes will arrive here once firmware ships that feature.
                if (size == BulkParamsParser.WireBandSize
                    && offset >= BulkParamsParser.OffsetEq
                    && offset <  BulkParamsParser.OffsetEq
                                 + BulkParamsParser.WireMaxChannels
                                 * BulkParamsParser.WireMaxBands
                                 * BulkParamsParser.WireBandSize
                    && (offset - BulkParamsParser.OffsetEq)
                       % BulkParamsParser.WireBandSize == 0)
                {
                    int flat = (offset - BulkParamsParser.OffsetEq) / BulkParamsParser.WireBandSize;
                    int ch = flat / BulkParamsParser.WireMaxBands;
                    int b  = flat % BulkParamsParser.WireMaxBands;
                    // Parser expects the band entry at the given offset within the
                    // buffer. The notify packet places the payload at offset 12,
                    // so shift accordingly by passing the payload region.
                    var bandBuf = new byte[BulkParamsParser.WireBandSize];
                    Buffer.BlockCopy(buf, 12, bandBuf, 0, BulkParamsParser.WireBandSize);
                    var fp = BulkParamsParser.ParseBand(bandBuf, 0);
                    BandParamNotified?.Invoke(this, new BandParamNotification
                    {
                        Channel = ch,
                        Band = b,
                        Params = fp,
                        Source = source
                    });
                }
                // Channel name change: offset is in WireBulkParams.channel_names range,
                // size is exactly WIRE_NAME_LEN (32).
                else if (size == WireChannelNameLen
                    && offset >= ChannelNamesWireOffset
                    && offset <  ChannelNamesWireOffset + 11 * WireChannelNameLen
                    && (offset - ChannelNamesWireOffset) % WireChannelNameLen == 0)
                {
                    int ch = (offset - ChannelNamesWireOffset) / WireChannelNameLen;
                    var name = System.Text.Encoding.UTF8.GetString(buf, 12, size).TrimEnd('\0');
                    ChannelNameNotified?.Invoke(this, new ChannelNameNotification
                    {
                        ChannelIndex = ch,
                        Name = name,
                        Source = source
                    });
                }
                // Active input source switch: offset == input_config.input_source, size 1.
                else if (size == 1 && offset == InputSourceWireOffset)
                {
                    InputSourceNotified?.Invoke(this, (InputSource)buf[12]);
                }
                // User volume change: offset == user_volume.user_volume_db, size 4 (float dB).
                // Fires for both UAC1 host echoes (Source=Unknown) and our own
                // REQ_SET_USER_VOLUME writes (Source=HostSet).
                else if (size == 4 && offset == UserVolumeWireOffset)
                {
                    float db = BitConverter.ToSingle(buf, 12);
                    UserVolumeNotified?.Invoke(this, new UserVolumeNotification
                    {
                        Db = db,
                        Source = source
                    });
                }
                // Other PARAM_CHANGED offsets are ignored for now — the host
                // already polls status / refetches on BULK_INVALIDATED.
                break;

            case 0x03: // BULK_INVALIDATED
                if (len < 5) return;
                BulkInvalidated?.Invoke(this, (ParamSource)buf[4]);
                break;

            case 0x04: // PRESET_LOADED (followed by BULK_INVALIDATED)
                if (len < 5) return;
                PresetLoadedNotified?.Invoke(this, buf[4]);
                break;
        }
    }

    #endregion

    /// <summary>
    /// Fetch all DSP parameters in a single bulk transfer (firmware v2+).
    /// Wire-format V10 is 2960 bytes (V7 2912 + LG Sound Sync + user volume
    /// + DAC HW mute, 16 bytes each). Older firmware returns fewer bytes and
    /// <see cref="BulkParamsParser"/> keys feature presence off the actual
    /// transfer length so this stays correct against any version &gt;= V2.
    /// Bump this only when a new wire-format section is added on the firmware
    /// side — the parser already handles a shorter response gracefully, so
    /// future versions need a length bump here to deliver the new bytes.
    /// Returns the response, or null if the transfer failed.
    /// </summary>
    public byte[]? GetAllParams()
    {
        return ControlTransferIn(VendorCommands.GetAllParams, 0, BulkParamsParser.PacketSizeV10);
    }

    #region Input Source (V7+)

    /// <summary>
    /// Set the active input source (USB or S/PDIF). Non-blocking: the
    /// firmware defers the hardware switch to its main loop and mutes
    /// output during the transition.
    /// </summary>
    public bool SetInputSource(InputSource source)
    {
        return ControlTransferOut(VendorCommands.SetInputSource, 0, new[] { (byte)source });
    }

    /// <summary>
    /// Get the currently active input source. Returns null if the firmware
    /// pre-dates V7 (USB STALL on unsupported request) or the transfer failed.
    /// </summary>
    public InputSource? GetInputSource()
    {
        var response = ControlTransferIn(VendorCommands.GetInputSource, 0, 1);
        if (response == null || response.Length < 1) return null;
        return (InputSource)response[0];
    }

    /// <summary>
    /// Get the 16-byte S/PDIF receiver status packet.
    /// </summary>
    public SpdifRxStatus? GetSpdifRxStatus()
    {
        var r = ControlTransferIn(VendorCommands.GetSpdifRxStatus, 0, 16);
        if (r == null || r.Length < 16) return null;
        return new SpdifRxStatus
        {
            State = (SpdifInputState)r[0],
            ActiveSource = (InputSource)r[1],
            LockCount = r[2],
            LossCount = r[3],
            SampleRate = BitConverter.ToUInt32(r, 4),
            ParityErrors = BitConverter.ToUInt32(r, 8),
            FifoFillPct = BitConverter.ToUInt16(r, 12)
        };
    }

    /// <summary>
    /// Get the 24-byte IEC 60958 channel status block from the locked S/PDIF stream.
    /// Only meaningful when the receiver is in the LOCKED state.
    /// </summary>
    public byte[]? GetSpdifRxChannelStatus()
    {
        return ControlTransferIn(VendorCommands.GetSpdifRxChStatus, 0, 24);
    }

    /// <summary>
    /// Get the GPIO pin currently configured for S/PDIF receive input.
    /// </summary>
    public byte? GetSpdifRxPin()
    {
        var response = ControlTransferIn(VendorCommands.GetSpdifRxPin, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    /// <summary>
    /// Change the S/PDIF RX GPIO pin. The pin number is encoded in wValue and the
    /// firmware returns a 1-byte status code (0 = success, 1 = invalid pin,
    /// 2 = pin in use, 3 = output active — must switch to USB first).
    /// Returns 0xFF on transfer failure.
    /// </summary>
    public byte SetSpdifRxPin(byte pin)
    {
        var response = ControlTransferIn(VendorCommands.SetSpdifRxPin, pin, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    #endregion

    #region External DAC Hardware Mute (V10+)

    /// <summary>
    /// Push a new <see cref="DacHwMuteConfig"/> to the device. The control
    /// transfer is fire-and-forget — the firmware copies the 16-byte payload
    /// to a pending slot and defers validation, pin claim, and flash write to
    /// its main loop (tens of milliseconds for flash erase + program). The
    /// USB response cannot reflect success/failure of that deferred apply.
    /// <para>
    /// Hosts that need definitive confirmation should follow up with
    /// <see cref="GetDacHwMute"/> after a short delay and compare against the
    /// value they sent. The DSPi Console takes the simpler "optimistic local
    /// update" path: a rejected SET surfaces as a discrepancy on the next
    /// bulk re-fetch (preset load, factory reset).
    /// </para>
    /// <returns><c>true</c> if the control transfer itself succeeded;
    /// <c>false</c> on USB error or older firmware that STALLs the opcode.</returns>
    /// </summary>
    public bool SetDacHwMute(DacHwMuteConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ControlTransferOut(VendorCommands.SetDacHwMuteConfig, 0, config.ToWireBytes());
    }

    /// <summary>
    /// Read the live DAC hardware-mute configuration from the device.
    /// Returns <c>null</c> on transfer failure or older firmware (pre-V10
    /// STALLs the opcode). The host treats null as "feature unsupported"
    /// and hides the Settings UI accordingly — see
    /// <c>MainViewModel.DacHwMuteSupported</c>.
    /// </summary>
    public DacHwMuteConfig? GetDacHwMute()
    {
        var response = ControlTransferIn(VendorCommands.GetDacHwMuteConfig, 0, DacHwMuteConfig.WireSize);
        return DacHwMuteConfig.TryParse(response);
    }

    /// <summary>
    /// Fire a one-shot ~1-second mute pulse for installer verification: the
    /// firmware asserts the configured mute GPIO, waits ~1 s, then releases.
    /// Returns the firmware status byte (0 = queued, non-zero = rejected
    /// because feature is disabled or no pin is configured). Returns
    /// <c>0xFF</c> on USB transfer failure.
    /// </summary>
    public byte TestDacHwMute()
    {
        var response = ControlTransferIn(VendorCommands.TestDacHwMute, 0, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    #endregion

    #region LG Sound Sync (V8+)

    /// <summary>
    /// Enable or disable LG Sound Sync — the TOSLINK side-channel that decodes
    /// LG TV remote volume / mute commands and applies them through the user
    /// volume path. Single-byte payload (0 = off, 1 = on). Returns
    /// <c>false</c> on USB transfer failure or older firmware that STALLs the
    /// opcode.
    /// </summary>
    public bool SetLgSoundSyncEnabled(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetLgSoundSyncEnable, 0,
                                  new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Read the current LG Sound Sync enable flag. Returns <c>null</c> on USB
    /// transfer failure or pre-V8 firmware that STALLs the opcode — the host
    /// treats null as "feature unsupported" and hides the toggle accordingly
    /// (see <c>MainViewModel.LgSoundSyncSupported</c>).
    /// </summary>
    public bool? GetLgSoundSyncEnabled()
    {
        var response = ControlTransferIn(VendorCommands.GetLgSoundSyncEnable, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    #endregion

    #region Buffer Statistics

    /// <summary>
    /// Fetch the 44-byte buffer statistics snapshot (REQ_GET_BUFFER_STATS 0xB0).
    /// </summary>
    public BufferStatsPacket? GetBufferStats()
    {
        var response = ControlTransferIn(VendorCommands.GetBufferStats, 0, BufferStatsPacket.PacketSize);
        return response != null ? BufferStatsPacket.Parse(response) : null;
    }

    /// <summary>
    /// Reset buffer statistics watermarks (REQ_RESET_BUFFER_STATS 0xB1).
    /// wValue bit 0 = reset watermarks.
    /// </summary>
    public bool ResetBufferStats()
    {
        var response = ControlTransferIn(VendorCommands.ResetBufferStats, 1, 1);
        return response != null && response.Length >= 1 && response[0] == 0x01;
    }

    #endregion

    #region Preset Commands

    /// <summary>
    /// Save current parameters to a preset slot (0-9).
    /// Returns PresetResult code.
    /// </summary>
    public byte SavePreset(int slot)
    {
        var response = ControlTransferIn(VendorCommands.PresetSave, (ushort)slot, 1);
        return response != null && response.Length >= 1 ? response[0] : PresetResult.FlashWriteError;
    }

    /// <summary>
    /// Load a preset slot (0-9) into active parameters.
    /// Returns PresetResult code.
    /// </summary>
    public byte LoadPreset(int slot)
    {
        var response = ControlTransferIn(VendorCommands.PresetLoad, (ushort)slot, 1);
        return response != null && response.Length >= 1 ? response[0] : PresetResult.FlashWriteError;
    }

    /// <summary>
    /// Delete a preset slot (0-9).
    /// Returns PresetResult code.
    /// </summary>
    public byte DeletePreset(int slot)
    {
        var response = ControlTransferIn(VendorCommands.PresetDelete, (ushort)slot, 1);
        return response != null && response.Length >= 1 ? response[0] : PresetResult.FlashWriteError;
    }

    /// <summary>
    /// Set the name for a preset slot. Max 31 chars (32-byte UTF-8 buffer, null-terminated).
    /// </summary>
    public bool SetPresetName(int slot, string name)
    {
        var data = new byte[32];
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        Array.Copy(bytes, data, Math.Min(bytes.Length, 31));
        return ControlTransferOut(VendorCommands.PresetSetName, (ushort)slot, data);
    }

    /// <summary>
    /// Get the name for a preset slot. Returns null on failure.
    /// </summary>
    public string? GetPresetName(int slot)
    {
        var response = ControlTransferIn(VendorCommands.PresetGetName, (ushort)slot, 32);
        if (response == null || response.Length < 1) return null;
        return System.Text.Encoding.UTF8.GetString(response).TrimEnd('\0');
    }

    /// <summary>
    /// Get the currently active preset slot. Returns -1 if no preset is active.
    /// </summary>
    public int GetActivePreset()
    {
        var response = ControlTransferIn(VendorCommands.PresetGetActive, 0, 1);
        if (response == null || response.Length < 1) return -1;
        return response[0] == 0xFF ? -1 : response[0];
    }

    /// <summary>
    /// Get the full preset directory: occupied mask, startup config, last active, include-pins.
    /// GET_DIR (0x95) returns 7 bytes on V12+ firmware (adds include_master_volume at byte 6)
    /// and 6 bytes on earlier firmware. Request 7 so newer firmware doesn't overflow the
    /// host's buffer (WinUSB treats a device-overrun as a babble error and fails the transfer).
    /// </summary>
    public PresetDirectoryInfo? GetPresetDirectory()
    {
        var response = ControlTransferIn(VendorCommands.PresetGetDir, 0, 7);
        if (response == null || response.Length < 6) return null;
        return new PresetDirectoryInfo
        {
            OccupiedMask = BitConverter.ToUInt16(response, 0),
            StartupMode = response[2],
            DefaultSlot = response[3],
            LastActiveSlot = response[4],
            IncludePins = response[5] != 0,
            MasterVolumeMode = response.Length >= 7 ? response[6] : (byte)0
        };
    }

    /// <summary>
    /// Set preset startup mode and default slot.
    /// Mode: 0=last used, 1=specific slot, 2=factory defaults.
    /// </summary>
    public bool SetPresetStartup(byte mode, byte defaultSlot)
    {
        return ControlTransferOut(VendorCommands.PresetSetStartup, 0, new[] { mode, defaultSlot });
    }

    /// <summary>
    /// Set whether pin assignments are included in presets.
    /// </summary>
    public bool SetPresetIncludePins(bool include)
    {
        return ControlTransferOut(VendorCommands.PresetSetIncludePins, 0, new[] { (byte)(include ? 1 : 0) });
    }

    /// <summary>
    /// Clear all presets by deleting each slot individually.
    /// Returns PresetResult code (first failure, or Ok if all succeed).
    /// </summary>
    public byte ClearAllPresets()
    {
        for (int i = 0; i < 10; i++)
        {
            var result = DeletePreset(i);
            if (result != PresetResult.Ok && result != PresetResult.SlotEmpty)
                return result;
            // Firmware defers each delete to its main loop (~45ms flash erase
            // with interrupts disabled). Pacing avoids ramming the next control
            // transfer into a USB blackout window, which otherwise shows up as
            // a transport failure even though every slot still gets cleared.
            if (i < 9) System.Threading.Thread.Sleep(50);
        }
        return PresetResult.Ok;
    }

    #endregion

    #region Channel Name Commands

    /// <summary>
    /// Set a channel name on the device. wValue = channel index, 32-byte UTF-8 buffer.
    /// </summary>
    public bool SetChannelNameOnDevice(int channel, string name)
    {
        var data = new byte[32];
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        Array.Copy(bytes, data, Math.Min(bytes.Length, 31));
        return ControlTransferOut(VendorCommands.SetChannelName, (ushort)channel, data);
    }

    /// <summary>
    /// Get a channel name from the device. wValue = channel index. Returns null on failure.
    /// </summary>
    public string? GetChannelNameFromDevice(int channel)
    {
        var response = ControlTransferIn(VendorCommands.GetChannelName, (ushort)channel, 32);
        if (response == null || response.Length < 1) return null;
        return System.Text.Encoding.UTF8.GetString(response).TrimEnd('\0');
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _usb.Dispose();

        GC.SuppressFinalize(this);
    }
}
