using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

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
    // 0x52 — repurposed by the firmware (output_config_independent_load_spec.md).
    // Originally REQ_LOAD_PARAMS; on current firmware this is REQ_SAVE_OUTPUT_CONFIG,
    // which snapshots the live IO config (output pins/types, I2S MCK/BCK, SPDIF RX
    // pin) into the device-global directory block. Hosts use REQ_PRESET_LOAD (0x91)
    // for "revert to saved".
    public const byte SaveOutputConfig = 0x52;
    public const byte FactoryReset = 0x53;
    public const byte SetChannelGain = 0x54;
    public const byte GetChannelGain = 0x55;
    public const byte SetChannelMute = 0x56;
    public const byte GetChannelMute = 0x57;
    // Stereo upmixer (wire V26, RP2350 only; upmixer_spec.md). On RP2040 the
    // SETs STALL and the GETs return all-zero payloads.
    public const byte UpmixSetConfig = 0x4A; // OUT, 44-byte UpmixConfigPacket
    public const byte UpmixGetConfig = 0x4B; // IN, 44 bytes
    public const byte UpmixSetParam  = 0x4C; // OUT, wValue=param id (0-13), 4-byte float
    public const byte UpmixGetParam  = 0x4D; // IN, wValue=param id, 4-byte float
    public const byte UpmixGetStatus = 0x4E; // IN, 16-byte UpmixStatus

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
    /// <summary>64-byte provenance blob: [0..47] git describe, [48..59] build
    /// date. For people only; nothing may gate on it.</summary>
    public const byte GetBuildInfo = 0x80;
    /// <summary>Core 1 mode (0 idle, 1 PDM, 2 EQ worker), 1 byte.</summary>
    public const byte GetCore1Mode = 0x7A;
    /// <summary>wValue = output: 1 when enabling it would collide with Core 1's
    /// other use (PDM against the EQ workers), 1 byte.</summary>
    public const byte GetCore1Conflict = 0x7B;
    public const byte ClearClips = 0x83;
    public const byte GetAllParams = 0xA0;
    public const byte GetAllParamsChunk = 0xA2; // chunked GET (WinUSB 4 KB control cap)
    public const byte SetAllParamsChunk = 0xA3; // chunked SET

    // Onboard test-signal generator (siggen). Own struct version (1), independent
    // of the bulk wire version. CONTROL is issued as an IN transfer ("write as
    // read") with the action in wValue.
    public const byte SiggenSetConfig = 0xA4; // OUT, 36-byte SiggenConfig
    public const byte SiggenGetConfig = 0xA5; // IN, 36 bytes
    public const byte SiggenControl   = 0xA6; // IN, wValue=SiggenControl.*, 1-byte status
    public const byte SiggenGetStatus = 0xA7; // IN, 16 bytes
    public const byte SiggenGetCaps   = 0xA8; // IN, wValue=0xFFFF (header 8B) or type idx (desc 62B)

    // Buffer statistics (firmware v3+)
    public const byte GetBufferStats  = 0xB0;
    public const byte ResetBufferStats = 0xB1;

    // Psychoacoustic bass (psybass, wire V23). Missing-fundamental bass enhancement:
    // one global parameter set applied per output channel selected by a 16-bit mask
    // (like loudness). Value SETs carry a 4-byte LE float; enable is 1 byte; mask is
    // 2 bytes LE. Firmware clamps every value; SETs apply live and are saved via
    // preset/save-params.
    public const byte SetPsybass          = 0x30; // OUT 1 byte bool
    public const byte GetPsybass          = 0x31; // IN 1 byte bool
    public const byte SetPsybassCutoff    = 0x32; // OUT 4-byte float (30..300 Hz)
    public const byte GetPsybassCutoff    = 0x33;
    public const byte SetPsybassHarmonics = 0x34; // OUT 4-byte float (-24..+12 dB)
    public const byte GetPsybassHarmonics = 0x35;
    public const byte SetPsybassDrive     = 0x36; // OUT 4-byte float (0..18 dB)
    public const byte GetPsybassDrive     = 0x37;
    public const byte SetPsybassCharacter = 0x38; // OUT 4-byte float (0..100 %)
    public const byte GetPsybassCharacter = 0x39;
    public const byte SetPsybassOriginal  = 0x3A; // OUT 4-byte float (-60..0 dB)
    public const byte GetPsybassOriginal  = 0x3B;
    public const byte SetPsybassMask      = 0x3C; // OUT 2-byte uint16 LE (per-output)
    public const byte GetPsybassMask      = 0x3D;

    // Subharmonic synthesizer (subharm, wire V29; extended V30). A dbx-style
    // octave divider: one global parameter set applied per output channel
    // selected by a 16-bit mask, like psybass. Levels are 4-byte LE floats,
    // enable/select/link/solo 1 byte, the mask 2 bytes LE. Solo is runtime-only.
    // The block spans 0x10-0x1F, 0x2C-0x2F and 0xA9-0xAE.
    public const byte SetSubharm          = 0x10; // OUT 1 byte bool
    public const byte GetSubharm          = 0x11;
    public const byte SetSubharmLow       = 0x12; // OUT float dB, 24-36 Hz band
    public const byte GetSubharmLow       = 0x13;
    public const byte SetSubharmHigh      = 0x14; // OUT float dB, 36-56 Hz band
    public const byte GetSubharmHigh      = 0x15;
    public const byte SetSubharmBoost     = 0x16; // OUT float dB, 70 Hz LF boost
    public const byte GetSubharmBoost     = 0x17;
    public const byte SetSubharmMask      = 0x18; // OUT uint16 LE output mask
    public const byte GetSubharmMask      = 0x19;
    public const byte GetSubharmHeadroom  = 0x1A; // IN float dB: worst-case gain, live
    public const byte SetSubharmTop       = 0x1B; // OUT float dB, 56-80 Hz band (V30)
    public const byte GetSubharmTop       = 0x1C;
    public const byte SetSubharmSelect    = 0x1D; // OUT 1 byte selectivity mode (V30)
    public const byte GetSubharmSelect    = 0x1E;
    public const byte GetSubharmMeter     = 0x1F; // IN outputs x uint16 LE sub peaks
    public const byte SetSubharmSolo      = 0x2C; // OUT 1 byte; runtime only
    public const byte GetSubharmSolo      = 0x2D;
    public const byte SetSubharmLink      = 0x2E; // OUT 1 byte, pair-linked synthesis
    public const byte GetSubharmLink      = 0x2F;
    public const byte SetSubharmDepth     = 0xA9; // OUT float %, selectivity depth
    public const byte GetSubharmDepth     = 0xAA;
    public const byte SetSubharmHold      = 0xAB; // OUT float ms, selectivity hold
    public const byte GetSubharmHold      = 0xAC;
    public const byte SetSubharmCeiling   = 0xAD; // OUT float dBFS sub ceiling (0 = off)
    public const byte GetSubharmCeiling   = 0xAE;

    // Tube modeller (wire V31). One indexed pair carries every parameter as a
    // float32, bools, mask and enums included: wValue = TubeParam index.
    public const byte SetTubeParam        = 0x3E;
    public const byte GetTubeParam        = 0x3F;

    // Output limiter (wire V32). One opcode: OUT sets, IN gets, wValue =
    // (output << 8) | LimiterParam index, float32 payload. Output 0xFF sets
    // every output; GET indices 0x80/0x81 read the meter and status blocks.
    public const byte Limiter             = 0x81;

    // Preset system (firmware v3+)
    public const byte PresetSave           = 0x90;
    public const byte PresetLoad           = 0x91;
    public const byte PresetDelete         = 0x92;
    public const byte PresetGetName        = 0x93;
    public const byte PresetSetName        = 0x94;
    public const byte PresetGetDir         = 0x95;
    public const byte PresetSetStartup     = 0x96;
    public const byte PresetGetStartup     = 0x97;
    // Output config mode (formerly REQ_PRESET_SET/GET_INCLUDE_PINS — same opcodes,
    // same 1:1 value mapping (1=with preset, 0=independent), now governs the entire
    // physical IO block (output pins/types, I2S MCK/BCK, SPDIF RX pin) rather than
    // just the GPIO pin assignments. See output_config_independent_load_spec.md.
    public const byte SetOutputConfigMode  = 0x98;
    public const byte GetOutputConfigMode  = 0x99;
    public const byte PresetGetActive      = 0x9A;
    public const byte SetChannelName       = 0x9B;
    public const byte GetChannelName       = 0x9C;

    // Control surfaces + IR remote (firmware control_surfaces.h). Physical GPIO
    // controls and an IR receiver bound to DSP parameters. SET of a binding/name/
    // IR-command is a live-only preview that applies asynchronously — the OUT
    // latches, firmware records CS_STATUS_PENDING, and the host polls GetCsStatus
    // (0x87) until LastSlot matches (0x80|sub for IR, 0xFF for save/revert) and
    // LastStatus leaves Pending. 0x88-0x8A are reserved (I2S slave-mode branch).
    // Control Surfaces aux outputs (caps v18); wValue = the slot holding one.
    // Runtime only and immediate, so never deferred-polled.
    public const byte SetCsAuxState        = 0x04; // OUT 1 byte 0/1; STALL unless an aux slot
    public const byte GetCsAuxState        = 0x05; // IN 1 byte; wValue 0xFFFF -> 48 bytes {state[16], level_q8[16] LE}
    public const byte SetCsAuxLevel        = 0x06; // OUT 2 bytes 8.8 percent LE, clamped; AUX_PWM only
    public const byte GetCsAuxLevel        = 0x07; // IN 2 bytes 8.8 percent LE
    public const byte SetCsBinding         = 0x84; // OUT 24-byte CsBinding, wValue=slot (0-15)
    public const byte GetCsBinding         = 0x85; // IN 24-byte CsBinding, wValue=slot
    public const byte GetCsCaps            = 0x86; // IN wValue=0xFFFF→header; wValue=noun→12-byte desc
    public const byte GetCsStatus          = 0x87; // IN 41-byte CsStatusPacket (32 pre-caps-v6)
    public const byte SetCsName            = 0x8B; // OUT 1-32 byte name, wValue=slot (single NUL clears)
    public const byte GetCsName            = 0x8C; // IN 32-byte NUL-terminated name, wValue=slot
    public const byte SetCsIrCmd           = 0x8D; // OUT 16-byte IrCommand, wValue=sub-slot (0-15)
    public const byte GetCsIrCmd           = 0x8E; // IN 16-byte IrCommand, wValue=sub-slot
    public const byte CsIrLearn            = 0x8F; // IN wValue 1=arm/0=cancel→1 ack; 2=read→8-byte result
    public const byte CsSave               = 0x9D; // IN 1 ack byte; persist whole live config (deferred)
    public const byte CsRevert             = 0x9E; // IN 1 ack byte; discard preview, reload flash (deferred)

    // Control-surface target groups and macros (caps v9). Group SETs poll on
    // 0x40|group and both macro SETs on 0x60|macro; the fire command is immediate
    // (it STALLs on a bad macro, leaving the reason in the status packet).
    public const byte SetCsGroup           = 0x20; // OUT 40-byte CsGroup, wValue=group (0-7)
    public const byte GetCsGroup           = 0x21; // IN 40-byte CsGroup, wValue=group
    public const byte SetCsMacro           = 0x22; // OUT 36-byte header (name+count), wValue=macro
    public const byte GetCsMacro           = 0x23; // IN 132-byte CsMacro, wValue=macro
    public const byte SetCsMacroStep       = 0x24; // OUT 12-byte CsMacroStep, wValue=(step<<8)|macro
    public const byte CsMacroFire          = 0x25; // IN 1 ack byte; wValue=macro, 0xFFFF cancels
    public const byte GetCsExtStatus       = 0x26; // IN 24-byte CsExtStatusPacket

    // Control-surface I2C display (caps v10). Same deferred-preview shape, polled
    // on 0x50 for the config and 0x50|page for a page — so the config and page 0
    // share a poll key, which is only unambiguous while one display SET is in
    // flight (the view model's write lock is what guarantees that).
    public const byte SetCsDisplayCfg      = 0x27; // OUT 12-byte CsDisplayCfg
    public const byte GetCsDisplayCfg      = 0x28; // IN 16 bytes: limits header + CsDisplayCfg
    public const byte SetCsDisplayPage     = 0x29; // OUT 4-byte CsDisplayPage, wValue=page (0-15)
    public const byte GetCsDisplayPage     = 0x2A; // IN 4-byte CsDisplayPage, wValue=page
    public const byte GetCsDisplayStatus   = 0x2B; // IN 8-byte CsDisplayStatus

    // I2S output configuration
    public const byte SetOutputType    = 0xC0;
    public const byte GetOutputType    = 0xC1;
    public const byte SetI2SBckPin     = 0xC2; // IN, wValue=(role<<8)|GPIO, status byte
    public const byte GetI2SBckPin     = 0xC3; // IN, wValue=role, 1 byte (BCK GPIO)

    // I2S clock master/slave mode (clock_pins_spec.md). SET is a deferred OUT.
    public const byte SetI2SClockMode    = 0x88; // OUT 1 byte (0=master, 1=slave)
    public const byte GetI2SClockMode    = 0x89; // IN 1 byte (live mode)
    public const byte GetI2SSlaveStatus  = 0x8A; // IN 16-byte I2sSlaveStatusPacket
    // I2S clock-pin mode (unified vs split BCK+LRCLK pairs). SET is a synchronous IN
    // returning a PIN_CONFIG_* status byte.
    public const byte SetI2SClockPinMode = 0xFE; // IN, wValue=0 unified/1 split, status byte
    public const byte GetI2SClockPinMode = 0xFF; // IN 1 byte (live pin mode)
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
    // Multichannel DSP masks. All carry the mask in the data payload (wValue=0).
    public const byte SetLevellerMasks     = 0xDE; // V18: 2 bytes [detector, apply] over input channels
    public const byte GetLevellerMasks     = 0xDF;
    public const byte SetLoudnessMask      = 0xFA; // V19: uint16 LE over output channels
    public const byte GetLoudnessMask      = 0xFB;
    public const byte SetCrossfeedOutputs  = 0xFC; // V20: uint8 over output pairs (bit p = outputs 2p/2p+1)
    public const byte GetCrossfeedOutputs  = 0xFD;

    // ADAT "bulk" output (V17+, RP2350 only). Streams all 8 output channels as one
    // optical ADAT lightpipe (44.1/48 kHz, 24-bit) from a single data GPIO. All are
    // IN transfers returning a 1-byte status / value; the SETs carry the argument in
    // wValue. RP2040 returns INVALID_OUTPUT / zeros.
    public const byte SetAdatEnable        = 0xCA; // IN, wValue=0/1, status byte
    public const byte GetAdatEnable        = 0xCB; // IN, 1 byte (0/1)
    public const byte SetAdatPin           = 0xCC; // IN, wValue=GPIO (0 = default), status byte
    public const byte GetAdatPin           = 0xCD; // IN, 1 byte (GPIO)
    public const byte GetAdatStatus        = 0xCE; // IN, 8-byte AdatStatus

    // ADAT input (8-channel lightpipe input source, wire V24, RP2350 only). A
    // selectable input source (INPUT_SOURCE_ADAT = 3) distinct from the ADAT
    // output (0xCA-0xCE). SETs are IN transfers carrying the value in wValue and
    // returning a PIN_CONFIG_* status byte. Set the pin (0x6A) before enabling.
    public const byte SetAdatInputEnable    = 0x68; // IN, wValue=0/1, status byte
    public const byte GetAdatInputEnable    = 0x69; // IN, 1 byte (0/1)
    public const byte SetAdatInputPin       = 0x6A; // IN, wValue=GPIO (0xFF clears), status byte
    public const byte GetAdatInputPin       = 0x6B; // IN, 1 byte (GPIO; 0xFF unset)
    public const byte SetAdatInputClockMode = 0x6C; // IN, wValue=0/1 (deferred), status byte
    public const byte GetAdatInputClockMode = 0x6D; // IN, 1 byte (live mode)
    public const byte GetAdatInputStatus    = 0x6E; // IN, 20-byte AdatInputStatusPacket

    // Input source switching (V7+)
    public const byte SetInputSource       = 0xE0;
    public const byte GetInputSource       = 0xE1;
    public const byte GetSpdifRxStatus     = 0xE2;
    public const byte GetSpdifRxChStatus   = 0xE3;
    public const byte SetSpdifRxPin        = 0xE4; // IN, wValue=(index<<8)|gpio, status byte
    public const byte GetSpdifRxPin        = 0xE5; // IN, wValue=index, 1 byte (GPIO)
    // Multiple selectable SPDIF inputs; index 0 is always enabled. Firmware
    // reports how many it has (3 before wire V28, 4 from V28) — never assume.
    public const byte SetSpdifInputEnable  = 0xE9; // IN, wValue=(index<<8)|enable, status byte
    public const byte GetSpdifInputConfig  = 0xEF; // IN, 2+count bytes: count, mask, one GPIO per input

    // I2S input (V12+). The device is the I2S clock master, so the host picks
    // the sample rate (44.1/48/96 kHz) the device drives. BCK/LRCK/MCK clock
    // pins are shared with the I2S output path (REQ_SET_I2S_BCK_PIN etc.).
    public const byte SetInputRate         = 0xED; // OUT, uint32 Hz (44100/48000/96000)
    public const byte GetInputRate         = 0xEE; // IN, 8 bytes: current Hz + selected I2S Hz
    public const byte SetI2sRxPin          = 0xF1; // IN, wValue=(pair<<8)|gpio, status byte
    public const byte GetI2sRxPin          = 0xF2; // IN, wValue=pair, 1 byte (GPIO)
    public const byte SetI2sInputChannels  = 0xF3; // IN, wValue=count (2/4/6/8), status byte
    public const byte GetI2sInputChannels  = 0xF4; // IN, 1 byte (channel count)

    // UART / I2C control interfaces (control_interfaces_spec.md). The device speaks
    // the vendor command set over an external UART or as an I2C target. SETs are
    // USB-only, carry the whole 8-byte config as an OUT payload, and are deferred:
    // firmware applies + persists on its main loop, so the authoritative PIN_CONFIG_*
    // outcome is read back via GetCtrlIfaceStatus (0xF9).
    public const byte SetUartConfig        = 0xF5; // OUT 8-byte UartCtrlConfig (USB only)
    public const byte GetUartConfig        = 0xF6; // IN 8-byte UartCtrlConfig
    public const byte SetI2cConfig         = 0xF7; // OUT 8-byte I2cCtrlConfig (USB only)
    public const byte GetI2cConfig         = 0xF8; // IN 8-byte I2cCtrlConfig
    public const byte GetCtrlIfaceStatus   = 0xF9; // IN 8-byte CtrlIfaceStatus

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
    Spdif = 1,
    I2s = 2,
    Adat = 3,     // 8-channel ADAT optical input (RP2350, V24+)
    Spdif2 = 4,
    Spdif3 = 5,
    Spdif4 = 6    // Fourth optional S/PDIF input (wire V28+)
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

/// <summary>An aux output's live state (NOTIFY_EVT_CS_AUX): its slot, on/off,
/// and level as 8.8 percent (0 on an on/off output).</summary>
public readonly record struct CsAuxNotification(byte Slot, bool On, ushort LevelQ8);

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
/// A generic device-pushed PARAM_CHANGED for a bulk-params field that isn't
/// handled by a dedicated typed notification (EQ / crossover / channel name /
/// input source / user volume). Carries the byte <see cref="Offset"/> into
/// WireBulkParams, the payload <see cref="Size"/>, the change <see cref="Source"/>,
/// and the raw <see cref="Payload"/>. The VM decodes it by offset range.
/// </summary>
public readonly struct ParamChangedNotification
{
    public ushort Offset { get; init; }
    public ushort Size { get; init; }
    public ParamSource Source { get; init; }
    public byte[] Payload { get; init; }
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
    /// <summary>The receiver library's own state: 0 no signal, 1 waiting
    /// for a stable signal, 2 stable (debug byte 14).</summary>
    public byte LibState;
    /// <summary>High nibble: on-stable callbacks; low nibble: on-lost-stable
    /// callbacks (debug byte 15).</summary>
    public byte CallbackCounts;
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
    public const byte InvalidParam = 0x05;
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
    // Output config persistence mode (byte 5 of GET_DIR). 0=independent
    // (IO block lives in the directory and is applied at boot only),
    // 1=with-preset (IO travels with each preset slot). Firmware clamps
    // >1 to independent. See output_config_independent_load_spec.md.
    public byte OutputConfigMode;
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
    // Device identification
    private const int VendorId = 0x2E8B;
    private const int ProductId = 0xFEAA;

    // Interface 2 is the vendor-specific control interface
    private const int VendorInterfaceNumber = 2;

    // USB Request Types (matching Python script)
    // 0x41 = 01000001 (Dir: Host-to-Device | Type: Vendor | Recipient: Interface)
    // 0xC1 = 11000001 (Dir: Device-to-Host | Type: Vendor | Recipient: Interface)
    private const byte RequestTypeOut = 0x41;
    private const byte RequestTypeIn = 0xC1;

    private readonly UsbContext _context = new();
    private IUsbDevice? _device;
    private bool _interfaceClaimed;
    private byte _openBusNumber;
    private byte _openAddress;
    private readonly object _lock = new();
    private readonly System.Timers.Timer _pollTimer;
    private readonly System.Timers.Timer _statusPollTimer;
    private bool _disposed;

    // Multi-device tracking
    private List<DSPiDeviceInfo> _availableDevices = new();
    private DSPiDeviceInfo? _selectedDeviceInfo;
    private string? _lastSelectedSerial;
    private string? _openDeviceSerial; // serial of the currently open _device handle

    /// <summary>
    /// Total audio channels in the wire model (set after GetDeviceInfo /
    /// refined from the bulk header). RP2040=7 (2 in + 5 out), RP2350=17
    /// (8 in + 9 out) on V16+ firmware.
    /// </summary>
    public int NumChannels { get; set; } = 5; // Legacy default (5 peaks)

    /// <summary>
    /// Number of firmware input channels (RP2040=2, RP2350=8 on V16+). Drives
    /// the app↔wire channel-index mapping (<see cref="ChannelMap"/>) used by
    /// per-channel commands, meter/notify decoding and bulk-array reads.
    /// Defaults to 2 (matches pre-V16 and RP2040) until platform/bulk sets it.
    /// </summary>
    public int NumInputChannels { get; set; } = 2;

    /// <summary>
    /// Number of firmware output channels (RP2040=5, RP2350=9).
    /// </summary>
    public int NumOutputChannels { get; set; } = 5;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private SystemStatus? _currentStatus;

    /// <summary>All currently connected DSPi devices.</summary>
    public IReadOnlyList<DSPiDeviceInfo> AvailableDevicesList => _availableDevices;

    /// <summary>The currently selected/active device.</summary>
    public DSPiDeviceInfo? SelectedDeviceInfo
    {
        get => _selectedDeviceInfo;
        private set
        {
            if (_selectedDeviceInfo == value) return;
            _selectedDeviceInfo = value;
            OnPropertyChanged(nameof(SelectedDeviceInfo));
        }
    }

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

    /// <summary>Fired when the firmware emits a per-band PARAM_CHANGED notification
    /// whose offset falls inside WireBulkParams.crossovers (V11+, size 16).
    /// <see cref="BandParamNotification.Band"/> carries the <em>local</em>
    /// crossover band (0..3), not the wire band index (20..23). Origin tag lets
    /// subscribers suppress their own EP0 echoes.</summary>
    public event EventHandler<BandParamNotification>? XoverBandParamNotified;

    /// <summary>Fired when the firmware emits a PARAM_CHANGED notification for
    /// audio_state.volume (the vendor-channel user volume, in dB). Origin tag
    /// distinguishes a UAC1 host echo (system tray, hardware volume keys) from
    /// our own REQ_SET_USER_VOLUME write — subscribers should suppress
    /// <see cref="ParamSource.HostSet"/> to avoid round-tripping their own writes.</summary>
    public event EventHandler<UserVolumeNotification>? UserVolumeNotified;

    /// <summary>Fired for every raw packet read from the notification endpoint —
    /// IDLE keep-alives, decoded events, unknown event IDs, malformed packets.
    /// Diagnostic hook used by the Interrupt Monitor window. Fires on the
    /// notify background thread; subscribers must marshal to the UI thread.</summary>
    public event EventHandler<NotifyPacket>? NotifyPacketReceived;

    /// <summary>Fired for a PARAM_CHANGED whose offset isn't covered by a dedicated
    /// typed event above (master volume, outputs, loudness, crossfeed, leveller,
    /// psybass, I2S/ADAT config, etc.). The VM applies it by offset range.</summary>
    public event EventHandler<ParamChangedNotification>? ParamChangedNotified;

    /// <summary>NOTIFY_EVT_INPUT_FORMAT (0x05): the active USB input channel count
    /// changed. Argument is the channel count.</summary>
    public event EventHandler<byte>? InputFormatNotified;

    /// <summary>Discrete hardware-state events (0x07 siggen, 0x08 ADAT output,
    /// 0x09 I2S slave clock, 0x0B ADAT input). Argument is the event id; the VM
    /// re-reads the corresponding status packet.</summary>
    public event EventHandler<byte>? StatusEventNotified;

    /// <summary>NOTIFY_EVT_CS_AUX (0x0C, caps v18): an aux output's live state or
    /// level changed, from any source (a bound control included). Both values
    /// ride every event.</summary>
    public event EventHandler<CsAuxNotification>? CsAuxNotified;

    // Notification endpoint state (bulk IN EP 0x83, V7+ firmware).
    private UsbEndpointReader? _notifyReader;
    private Thread? _notifyThread;
    private volatile bool _notifyStop;
    private const int NotifyPacketSize = 64;
    private const int ChannelNamesWireOffset = BulkParamsParser.OffsetChannelNames; // offsetof(WireBulkParams, channel_names)
    private const int WireChannelNameLen = 32;

    // V7 input config block sits at offsetof(WireBulkParams, input_config) = 2896.
    // input_source occupies the first byte of the 16-byte WireInputConfig struct.
    private const int InputSourceWireOffset = BulkParamsParser.OffsetInputCfg;

    // V9+ user volume block sits at offsetof(WireBulkParams, user_volume) = 2928
    // (after input_config @ 2896 and lg_sound_sync @ 2912). user_volume_db
    // occupies the first 4 bytes (float dB) of the 16-byte WireUserVolume
    // struct; user_mute lives at +4.
    private const int UserVolumeWireOffset = BulkParamsParser.OffsetUserVolume;

    // V20 crossover block sits at offsetof(WireBulkParams, crossovers) = 4780.
    // WireCrossoverConfig is WireBandParams[17][4]; each band is 16 bytes.
    private const int CrossoverWireOffset = BulkParamsParser.OffsetCrossover;
    private const int CrossoverBandCount = BulkParamsParser.WireMaxXoverBands;

    /// <summary>
    /// Wire-format version reported by the most recent bulk params fetch
    /// (WireBulkParams.header.format_version). 0 until the first bulk read.
    /// Gates the <see cref="EncodeEqValue"/> band-field width: V11 widened the
    /// REQ_GET_EQ_PARAM band field from 4 to 5 bits to address crossover bands
    /// 20..23. See crossover_filters_spec.md §3.2.
    /// </summary>
    public int WireFormatVersion { get; set; }

    public DspDevice()
    {
        // Poll for devices every 500ms
        _pollTimer = new System.Timers.Timer(500);
        _pollTimer.Elapsed += (_, _) => ScanDevices();
        _pollTimer.AutoReset = true;

        // Poll for status every 100ms when connected
        _statusPollTimer = new System.Timers.Timer(100);
        _statusPollTimer.Elapsed += (_, _) => PollStatus();
        _statusPollTimer.AutoReset = true;
    }

    public void StartMonitoring()
    {
        _pollTimer.Start();
        ScanDevices();
    }

    public void StopMonitoring()
    {
        _pollTimer.Stop();
    }

    /// <summary>
    /// Read the serial number from a temporarily opened USB device using the
    /// vendor GET_SERIAL control request. The device must already be open and
    /// have its interface claimed.
    /// </summary>
    private static string? ReadSerialFromDevice(IUsbDevice tempDevice)
    {
        var setupPacket = new UsbSetupPacket(RequestTypeIn, VendorCommands.GetSerial, 0, VendorInterfaceNumber, 16);
        var buffer = new byte[16];
        int transferred;
        try
        {
            transferred = tempDevice.ControlTransfer(setupPacket, buffer, 0, buffer.Length);
        }
        catch
        {
            // Hot-plug races and brief WinUSB stalls can throw here during
            // enumeration / open-by-serial. Treat them as "no serial" so the
            // poll loop just skips this device and retries on the next tick.
            return null;
        }
        if (transferred > 0)
            return System.Text.Encoding.ASCII.GetString(buffer, 0, transferred).TrimEnd('\0');
        return null;
    }

    /// <summary>
    /// Configure (idempotent on Windows/WinUSB) and claim the vendor interface
    /// on a freshly opened device. Returns true on success.
    /// </summary>
    private static bool ConfigureAndClaim(IUsbDevice dev)
    {
        try { dev.SetConfiguration(1); }
        catch { /* already configured by Windows; libusb winusb backend tolerates this */ }
        return dev.ClaimInterface(VendorInterfaceNumber);
    }

    /// <summary>
    /// Cached serials keyed by (bus, address). Avoids opening matching devices
    /// repeatedly on every scan tick — a duplicate open of the currently-active
    /// device disturbs the in-flight control transfer queue on the original
    /// handle (WinUSB lets the second open through, then SetConfiguration /
    /// ClaimInterface on the duplicate handle stomps the original's state and
    /// every subsequent control GET silently returns 0 bytes).
    /// </summary>
    private readonly Dictionary<(byte bus, byte addr), string> _serialCache = new();

    private static (byte bus, byte addr) GetBusAddr(IUsbDevice dev)
    {
        // BusNumber/Address live on the concrete UsbDevice — IUsbDevice doesn't
        // expose them. Fall back to (0,0) if the cast fails (won't happen with
        // the libusb-1.0 backend, which always returns UsbDevice instances).
        if (dev is UsbDevice ud) return (ud.BusNumber, ud.Address);
        return (0, 0);
    }

    // Guards ScanDevices against re-entrancy. The scan runs on a 500 ms
    // auto-reset timer, but enumerating and opening a device (list + clone +
    // open + claim + read serial) can take longer than that interval. Without
    // this guard the timer re-enters ScanDevices on another thread-pool thread
    // while a previous scan is still mid-open; the overlapping scans each call
    // OpenDevice, which tears down any existing connection at entry, so they
    // repeatedly disconnect the device the previous scan just opened — leaving
    // it perpetually reconnecting and leaking scan threads.
    private int _scanActive;

    /// <summary>
    /// Scan for all connected DSPi devices, update the available list,
    /// and auto-select/reconnect as needed.
    /// </summary>
    private void ScanDevices()
    {
        if (_disposed) return;

        // Only one scan at a time (see _scanActive).
        if (System.Threading.Interlocked.CompareExchange(ref _scanActive, 1, 0) != 0)
            return;

        try
        {
            using var allDevicesList = _context.List();
            var matching = allDevicesList
                .Where(d => d.VendorId == VendorId && d.ProductId == ProductId)
                .ToList();

            // Drop cache entries for devices that have unplugged.
            var liveKeys = matching.Select(GetBusAddr).ToHashSet();
            foreach (var stale in _serialCache.Keys.Where(k => !liveKeys.Contains(k)).ToList())
                _serialCache.Remove(stale);

            // Build the current device list. For the device we already have open,
            // skip the open/claim/read cycle entirely — we know its serial.
            var currentDevices = new List<DSPiDeviceInfo>();
            (byte bus, byte addr) openKey = (_openBusNumber, _openAddress);
            bool weHaveOpen = _device != null && IsConnected && _selectedDeviceInfo != null;

            foreach (var dev in matching)
            {
                var key = GetBusAddr(dev);

                if (weHaveOpen && key.bus == openKey.bus && key.addr == openKey.addr)
                {
                    currentDevices.Add(_selectedDeviceInfo!);
                    continue;
                }

                if (_serialCache.TryGetValue(key, out var cachedSerial))
                {
                    currentDevices.Add(new DSPiDeviceInfo(cachedSerial,
                        $"vid_{VendorId:X4}&pid_{ProductId:X4}#{cachedSerial}"));
                    continue;
                }

                // Unknown device — open briefly to read serial, then close.
                try
                {
                    if (!dev.TryOpen()) continue;
                    try
                    {
                        if (!ConfigureAndClaim(dev)) continue;
                        var serial = ReadSerialFromDevice(dev);
                        if (!string.IsNullOrEmpty(serial))
                        {
                            _serialCache[key] = serial!;
                            currentDevices.Add(new DSPiDeviceInfo(serial!,
                                $"vid_{VendorId:X4}&pid_{ProductId:X4}#{serial}"));
                        }
                    }
                    finally
                    {
                        try { dev.ReleaseInterface(VendorInterfaceNumber); } catch { }
                        try { dev.Close(); } catch { }
                    }
                }
                catch { }
            }

            var oldSerials = _availableDevices.Select(d => d.Serial).ToHashSet();
            var newSerials = currentDevices.Select(d => d.Serial).ToHashSet();
            if (!oldSerials.SetEquals(newSerials))
            {
                _availableDevices = currentDevices;
                AvailableDevicesChanged?.Invoke(this, EventArgs.Empty);
            }

            if (_selectedDeviceInfo != null && !newSerials.Contains(_selectedDeviceInfo.Serial))
            {
                lock (_lock) { HandleDisconnect(); }
                if (currentDevices.Count == 0) SelectedDeviceInfo = null;
            }

            if (_device != null && IsConnected)
            {
                if (matching.Count == 0)
                    lock (_lock) { HandleDisconnect(); }
                return;
            }

            if (_device == null && currentDevices.Count > 0)
            {
                var reconnectTarget = _lastSelectedSerial != null
                    ? currentDevices.FirstOrDefault(d => d.Serial == _lastSelectedSerial)
                    : null;
                OpenDevice(reconnectTarget ?? currentDevices[0]);
            }
            else if (currentDevices.Count == 0)
            {
                if (ErrorMessage == null || ErrorMessage == "Disconnected")
                {
                    using var anyDevicesList = _context.List();
                    if (anyDevicesList.Count == 0)
                        ErrorMessage = "No USB devices visible to libusb-1.0. Install the WinUSB driver for the DSPi vendor interface.";
                    else
                        ErrorMessage = "Disconnected";
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ScanDevices error: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _scanActive, 0);
        }
    }

    /// <summary>
    /// Open and connect to a specific device by its info. The opened device is
    /// retained outside the listing collection so it survives the collection's
    /// disposal (libusb-1.0 holds an internal ref while the handle is open).
    /// </summary>
    private void OpenDevice(DSPiDeviceInfo deviceInfo)
    {
        lock (_lock)
        {
            try
            {
                if (_device != null)
                {
                    HandleDisconnect();
                }

                // libusb-1.0 wrapper detail: UsbDeviceCollection.Dispose() also
                // disposes every IUsbDevice instance it contains. Retaining one
                // past the listing's `using` block leaves us with a disposed
                // wrapper — every subsequent ControlTransfer raises
                // ObjectDisposedException, which the viewmodels' broad try/catch
                // swallows silently (so the device looks "connected" but no GETs
                // ever return data). Clone() the candidates first so they
                // outlive the collection.
                List<IUsbDevice> candidates = new();
                using (var devices = _context.List())
                {
                    foreach (var d in devices.Where(d => d.VendorId == VendorId && d.ProductId == ProductId))
                        candidates.Add(d.Clone());
                }

                IUsbDevice? opened = null;
                bool openedClaimed = false;
                (byte bus, byte addr) openedKey = (0, 0);

                foreach (var dev in candidates)
                {
                    if (opened != null)
                    {
                        // Already found our match — discard remaining clones.
                        try { dev.Close(); } catch { }
                        continue;
                    }

                    if (!dev.TryOpen())
                    {
                        try { dev.Close(); } catch { }
                        continue;
                    }

                    bool claimed = false;
                    try
                    {
                        if (!ConfigureAndClaim(dev))
                        {
                            try { dev.Close(); } catch { }
                            continue;
                        }
                        claimed = true;

                        var serial = ReadSerialFromDevice(dev);
                        if (serial == deviceInfo.Serial)
                        {
                            opened = dev;
                            openedClaimed = true;
                            openedKey = GetBusAddr(dev);
                            // Don't release/close — keep this clone alive.
                        }
                        else
                        {
                            try { dev.ReleaseInterface(VendorInterfaceNumber); } catch { }
                            try { dev.Close(); } catch { }
                        }
                    }
                    catch
                    {
                        if (claimed)
                            try { dev.ReleaseInterface(VendorInterfaceNumber); } catch { }
                        try { dev.Close(); } catch { }
                    }
                }

                if (opened == null)
                {
                    ErrorMessage = "Failed to open device";
                    return;
                }

                _device = opened;
                _interfaceClaimed = openedClaimed;
                _openBusNumber = openedKey.bus;
                _openAddress = openedKey.addr;
                _openDeviceSerial = deviceInfo.Serial;
                _selectedDeviceInfo = deviceInfo;
                _lastSelectedSerial = deviceInfo.Serial;
                SelectedDeviceInfo = deviceInfo;

                IsConnected = true;
                ErrorMessage = null;

                _statusPollTimer.Start();
                StartNotifyListener(opened);
                DeviceConnected?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error: {ex.Message}";
                if (_device != null)
                {
                    if (_interfaceClaimed)
                        try { _device.ReleaseInterface(VendorInterfaceNumber); } catch { }
                    try { _device.Close(); } catch { }
                }
                _device = null;
                _interfaceClaimed = false;
                _openBusNumber = 0;
                _openAddress = 0;
                _openDeviceSerial = null;
            }
        }
    }

    /// <summary>
    /// Switch to a different connected device. Called from ViewModel after unsaved changes check.
    /// </summary>
    public void SelectDevice(DSPiDeviceInfo device)
    {
        if (device.Serial == _openDeviceSerial && IsConnected) return;
        OpenDevice(device);
    }

    /// <summary>
    /// Poll status using control transfer. Called by timer.
    /// </summary>
    private void PollStatus()
    {
        if (_disposed || !IsConnected) return;

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

        // Firmware indexes peaks and clip bits by WIRE channel (unified model:
        // inputs then outputs). Remap to the app's stable ChannelId space so
        // meters and clip indicators land on the correct rows.
        var peaks = new float[ChannelMap.AppChannelCount]; // app id space (0..16)
        for (int i = 0; i < numCh && (i * 2 + 1) < buffer.Length; i++)
        {
            int appId = ChannelMap.WireToApp(i, NumInputChannels);
            if (appId < 0) continue; // wire slot with no app-channel representation
            peaks[appId] = BitConverter.ToUInt16(buffer, i * 2) / 32767.0f;
        }

        int cpuOffset = peakBytes;
        int cpu0 = cpuOffset < buffer.Length ? buffer[cpuOffset] : 0;
        int cpu1 = cpuOffset + 1 < buffer.Length ? buffer[cpuOffset + 1] : 0;

        uint clipFlags = 0;
        int clipOffset = cpuOffset + 2;
        if (clipOffset + 1 < buffer.Length)
        {
            // clip_flags is 32 bits on the wire (one bit per wire channel, so
            // RP2350's PDM at wire channel 16 fits). Remap each set bit from wire
            // channel to app channel id.
            uint wireClip = clipOffset + 3 < buffer.Length
                ? BitConverter.ToUInt32(buffer, clipOffset)
                : BitConverter.ToUInt16(buffer, clipOffset);
            for (int i = 0; i < numCh && i < 32; i++)
            {
                if ((wireClip & (1u << i)) == 0) continue;
                int appId = ChannelMap.WireToApp(i, NumInputChannels);
                if (appId >= 0 && appId < 32) clipFlags |= 1u << appId;
            }
        }

        return new SystemStatus
        {
            Peaks = peaks,
            Cpu0Load = cpu0,
            Cpu1Load = cpu1,
            ClipFlags = clipFlags
        };
    }

    private void HandleDisconnect()
    {
        _statusPollTimer.Stop();
        StopNotifyListener();

        var wasConnected = IsConnected;

        if (_device != null)
        {
            if (_interfaceClaimed)
                try { _device.ReleaseInterface(VendorInterfaceNumber); } catch { }
            try { _device.Close(); } catch { }
        }
        _device = null;
        _interfaceClaimed = false;
        _openBusNumber = 0;
        _openAddress = 0;
        _openDeviceSerial = null;

        IsConnected = false;

        if (wasConnected)
        {
            ErrorMessage = "Disconnected";
            DeviceDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            HandleDisconnect();
        }
    }

    public void Reconnect()
    {
        lock (_lock)
        {
            HandleDisconnect();
        }
        ScanDevices();
    }

    /// <summary>
    /// Send a vendor control OUT transfer (host to device).
    /// libusb-1.0 returns the number of bytes transferred, or a negative
    /// LIBUSB_ERROR_* on failure.
    /// </summary>
    private bool ControlTransferOut(byte request, ushort value = 0, byte[]? data = null)
    {
        lock (_lock)
        {
            if (_device == null) return false;

            var buffer = data ?? Array.Empty<byte>();
            var setupPacket = new UsbSetupPacket(
                RequestTypeOut,
                request,
                value,
                VendorInterfaceNumber,
                buffer.Length);

            try
            {
                int transferred = _device.ControlTransfer(setupPacket, buffer, 0, buffer.Length);
                return transferred >= 0;
            }
            catch
            {
                // libusb / LibUsbDotNet throws on stall, device disappearance
                // mid-transfer, NAK timeout, etc. Surface those as "transfer
                // failed" rather than unwinding through an async void handler
                // and killing the process. Callers already treat false as a
                // generic USB failure.
                return false;
            }
        }
    }

    /// <summary>
    /// Send a vendor control IN transfer (device to host).
    /// </summary>
    private byte[]? ControlTransferIn(byte request, ushort value = 0, int length = 4)
    {
        lock (_lock)
        {
            if (_device == null) return null;

            var setupPacket = new UsbSetupPacket(
                RequestTypeIn,
                request,
                value,
                VendorInterfaceNumber,
                length);

            var buffer = new byte[length];
            int transferred;
            try
            {
                transferred = _device.ControlTransfer(setupPacket, buffer, 0, buffer.Length);
            }
            catch
            {
                // See ControlTransferOut for the rationale — keep USB stack
                // exceptions from propagating through async void handlers.
                return null;
            }

            if (transferred > 0)
            {
                if (transferred < length)
                {
                    var result = new byte[transferred];
                    Array.Copy(buffer, result, transferred);
                    return result;
                }
                return buffer;
            }

            return null;
        }
    }

    #region High-Level Commands

    // EQ parameter indices for wValue encoding
    private const int EqParamType = 0;
    private const int EqParamFreq = 1;
    private const int EqParamQ = 2;
    private const int EqParamGain = 3;
    private const int EqParamQp = 5;   // Linkwitz Transform target Q (qp×512), V22+

    /// <summary>
    /// Encode wValue for REQ_GET_EQ_PARAM access. The band-field width depends
    /// on the firmware wire-format version (crossover_filters_spec.md §3.2):
    ///   • V11+: 5-bit band, 3-bit param — (channel &lt;&lt; 8) | (band &lt;&lt; 3) | param.
    ///     Required so crossover bands 20..23 are addressable.
    ///   • &lt; V11: legacy 4-bit band, 4-bit param — (channel &lt;&lt; 8) | (band &lt;&lt; 4) | param.
    /// Only REQ_GET_EQ_PARAM changed; SET and the bulk transfer carry the band
    /// in a full byte and are unaffected.
    /// </summary>
    private ushort EncodeEqValue(int channel, int band, int param = 0)
    {
        return WireFormatVersion >= 11
            ? (ushort)((channel << 8) | (band << 3) | param)
            : (ushort)((channel << 8) | (band << 4) | param);
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
        // Linkwitz Transform (type 11) appends a 2-byte qp sidecar (Qp×512) → an
        // 18-byte payload; the firmware then latches the target Q. The 16-byte form
        // preserves the stored qp, so always send the long form for LT bands
        // (peq_filters.md §4.1).
        bool isLt = p.Type == FilterType.LinkwitzTransform;
        var data = new byte[isLt ? 18 : 16];
        data[0] = (byte)ChannelMap.AppToWire(channel, NumInputChannels);
        data[1] = (byte)band;
        data[2] = (byte)p.Type;
        data[3] = p.Bypass ? (byte)1 : (byte)0;
        BitConverter.GetBytes(p.Frequency).CopyTo(data, 4);
        BitConverter.GetBytes(p.Q).CopyTo(data, 8);
        BitConverter.GetBytes(p.Gain).CopyTo(data, 12);
        if (isLt)
            BitConverter.GetBytes(p.QpEncoded).CopyTo(data, 16);

        ExpectEcho(channel, band, p);
        return ControlTransferOut(VendorCommands.SetEqParam, 0, data);
    }

    // ── Echoes of our own filter writes ──
    // The firmware applies an EQ write later, in its main loop, outside the
    // USB request that carried it, so the PARAM_CHANGED it reports for the
    // write is tagged UNKNOWN rather than HOST_SET. Without this, every write
    // the app makes (a graph drag sends about 30 a second) would come back
    // looking like another host's change. Each write is remembered until its
    // echo arrives; the firmware applies only the latest of writes that queue
    // up, so an echo also retires every older write to the same band.

    private const int MaxPendingEchoes = 32;
    private readonly Dictionary<(int Channel, int WireBand), List<FilterParams>> _pendingEchoes = new();

    private void ExpectEcho(int channel, int wireBand, FilterParams p)
    {
        lock (_pendingEchoes)
        {
            if (!_pendingEchoes.TryGetValue((channel, wireBand), out var list))
                _pendingEchoes[(channel, wireBand)] = list = new List<FilterParams>();
            list.Add(p.Clone());
            if (list.Count > MaxPendingEchoes) list.RemoveAt(0);
        }
    }

    /// <summary>True when <paramref name="p"/> is the firmware reporting a value
    /// this app wrote to (channel, wire band); consumes that write.</summary>
    private bool IsOwnEcho(int channel, int wireBand, FilterParams p)
    {
        lock (_pendingEchoes)
        {
            if (!_pendingEchoes.TryGetValue((channel, wireBand), out var list)) return false;
            int i = list.FindLastIndex(w => SameOnWire(w, p));
            if (i < 0) return false;
            list.RemoveRange(0, i + 1);
            return true;
        }
    }

    private static bool SameOnWire(FilterParams a, FilterParams b) =>
        a.Type == b.Type && a.Frequency == b.Frequency && a.Q == b.Q && a.Gain == b.Gain
        && a.Bypass == b.Bypass && a.QpEncoded == b.QpEncoded;

    /// <summary>
    /// Get EQ filter parameters for a specific channel and band.
    /// Reads each parameter individually (4 bytes each) like the Python script.
    /// </summary>
    public FilterParams? GetFilter(int channel, int band)
    {
        int wireCh = ChannelMap.AppToWire(channel, NumInputChannels);

        // Read type (returned as uint32)
        var typeData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(wireCh, band, EqParamType), 4);
        if (typeData == null || typeData.Length < 4) return null;
        var type = BitConverter.ToUInt32(typeData, 0);

        // Read frequency (float)
        var freqData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(wireCh, band, EqParamFreq), 4);
        if (freqData == null || freqData.Length < 4) return null;
        var freq = BitConverter.ToSingle(freqData, 0);

        // Read Q (float)
        var qData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(wireCh, band, EqParamQ), 4);
        if (qData == null || qData.Length < 4) return null;
        var q = BitConverter.ToSingle(qData, 0);

        // Read gain (float)
        var gainData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(wireCh, band, EqParamGain), 4);
        if (gainData == null || gainData.Length < 4) return null;
        var gain = BitConverter.ToSingle(gainData, 0);

        var fp = new FilterParams
        {
            Type = (FilterType)type,
            Frequency = freq,
            Q = q,
            Gain = gain
        };

        // Linkwitz Transform (V22+): read the target Q from param 5 (qp×512 in the
        // low 16 bits); 0 decodes to the 0.707 default.
        if ((FilterType)type == FilterType.LinkwitzTransform)
        {
            var qpData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(wireCh, band, EqParamQp), 4);
            if (qpData != null && qpData.Length >= 4)
                fp.Qp = FilterParams.DecodeQp((ushort)(BitConverter.ToUInt32(qpData, 0) & 0xFFFF));
        }

        return fp;
    }

    /// <summary>
    /// Toggle per-band bypass via the dedicated single-byte opcode 0xD8 (firmware
    /// 1.1.4+). Cheaper than re-sending the whole 16-byte EqParamPacket and the
    /// firmware preserves the band's current freq/Q/gain. Older firmware STALLs
    /// the request and this returns false — gate the call on a capability probe.
    /// </summary>
    public bool SetBandBypass(int channel, int band, bool bypass)
    {
        int wireCh = ChannelMap.AppToWire(channel, NumInputChannels);
        ushort wValue = (ushort)((wireCh << 8) | (band & 0xFF));
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
        int wireCh = ChannelMap.AppToWire(channel, NumInputChannels);
        ushort wValue = (ushort)((wireCh << 8) | (band & 0xFF));
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
        return ControlTransferOut(VendorCommands.SetDelay,
            (ushort)ChannelMap.AppToWire(channel, NumInputChannels), data);
    }

    /// <summary>
    /// Get delay for a specific channel in milliseconds.
    /// </summary>
    public float? GetDelay(int channel)
    {
        var response = ControlTransferIn(VendorCommands.GetDelay,
            (ushort)ChannelMap.AppToWire(channel, NumInputChannels), 4);

        if (response == null || response.Length < 4)
            return null;

        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Get system status (peak levels, CPU load, clip flags).
    /// wValue=9 requests full status: peaks[numChannels]×2, cpu0, cpu1,
    /// clip_flags (u32), active_input_channels (u8).
    /// </summary>
    public SystemStatus? GetStatus()
    {
        int packetSize = NumChannels * 2 + 7; // peaks + cpu0 + cpu1 + clipFlags(4) + active inputs
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
    /// Persist the current live IO config (output pins/types, I2S MCK/BCK, SPDIF
    /// RX pin) into the device-global directory block. Used in independent mode
    /// to survive reboot — runtime changes via the per-field setters take effect
    /// immediately, but only this save makes them stick. Returns PresetResult.
    /// </summary>
    public byte SaveOutputConfig()
    {
        var response = ControlTransferIn(VendorCommands.SaveOutputConfig, 0, 1);
        return response != null && response.Length >= 1 ? response[0] : PresetResult.FlashWriteError;
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
        if (GetPlatformInfo() is not { } info) return null;
        return (info.Platform, "v" + (info.Version.TagSuffix ?? info.Version.ToString()));
    }

    /// <summary>
    /// REQ_GET_PLATFORM, asked for the full 7 bytes: platform, version, and the
    /// beta ordinal. Older firmware answers with 6 or 4 bytes; see
    /// <see cref="FirmwareVersion.FromPlatformReply"/>.
    /// </summary>
    public (string Platform, FirmwareVersion Version)? GetPlatformInfo()
    {
        var response = ControlTransferIn(VendorCommands.GetPlatform, 0, 7);
        if (response == null || FirmwareVersion.FromPlatformReply(response) is not { } reply) return null;
        var platform = reply.Platform switch { 1 => "RP2350", 2 => "STM32H723", _ => "RP2040" };
        return (platform, reply.Version);
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
    /// Set an I2S BCK (bit clock) pin. LRCLK is always BCK + 1. <paramref name="role"/>
    /// 0 = master/unified pair, 1 = slave pair (SPLIT mode only). Role rides in the
    /// wValue high byte; a bare GPIO (role 0) matches the legacy behavior.
    /// Returns status byte, or 0xFF on transfer failure.
    /// </summary>
    public byte SetI2SBckPin(byte pin, byte role = 0)
    {
        ushort wValue = (ushort)((role << 8) | pin);
        var response = ControlTransferIn(VendorCommands.SetI2SBckPin, wValue, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get a pair's I2S BCK pin (LRCLK = value + 1). <paramref name="role"/> 0 =
    /// master/unified, 1 = slave pair. Null on failure.
    /// </summary>
    public byte? GetI2SBckPin(byte role = 0)
    {
        var response = ControlTransferIn(VendorCommands.GetI2SBckPin, role, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    // ── I2S clock master/slave + clock-pin unified/split ─────────────────────

    /// <summary>Set the I2S clock mode (0x88; 0=master, 1=slave). Deferred fire-and-
    /// forget OUT; confirm via GetI2SClockMode / the slave status. False on failure.</summary>
    public bool SetI2SClockMode(byte mode) =>
        ControlTransferOut(VendorCommands.SetI2SClockMode, 0, new[] { (byte)(mode == 1 ? 1 : 0) });

    /// <summary>Live I2S clock mode (0x89). Null on transfer failure / unsupported.</summary>
    public byte? GetI2SClockMode()
    {
        var r = ControlTransferIn(VendorCommands.GetI2SClockMode, 0, 1);
        return r != null && r.Length >= 1 ? r[0] : (byte?)null;
    }

    /// <summary>Read the 16-byte I2S slave-clock status (0x8A). Null on failure.</summary>
    public I2sSlaveStatus? GetI2SSlaveStatus()
    {
        var r = ControlTransferIn(VendorCommands.GetI2SSlaveStatus, 0, I2sSlaveStatus.WireSize);
        return r == null ? null : I2sSlaveStatus.FromBytes(r);
    }

    /// <summary>Set the I2S clock-pin mode (0xFE; 0=unified, 1=split). Synchronous IN
    /// returning a PIN_CONFIG_* status byte (0xFF on transfer failure).</summary>
    public byte SetI2SClockPinMode(byte mode)
    {
        var r = ControlTransferIn(VendorCommands.SetI2SClockPinMode, (ushort)(mode == 1 ? 1 : 0), 1);
        return r != null && r.Length >= 1 ? r[0] : (byte)0xFF;
    }

    /// <summary>Live I2S clock-pin mode (0xFF). Null on failure / unsupported.</summary>
    public byte? GetI2SClockPinMode()
    {
        var r = ControlTransferIn(VendorCommands.GetI2SClockPinMode, 0, 1);
        return r != null && r.Length >= 1 ? r[0] : (byte?)null;
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

    // ── Multichannel DSP masks (V18/V19/V20) ──
    // Each SET carries the whole mask in the data payload and is re-sent in full
    // on every change (there is no incremental per-bit wire protocol). GETs read
    // the mask back via a data-IN stage. Bit spaces: leveller = input channels
    // (0..7), loudness = output channels (0..8), crossfeed = output pairs.

    /// <summary>Set the volume-leveller detector and apply channel masks (0xDE, V18).</summary>
    public bool SetLevellerMasks(byte detectorMask, byte applyMask)
    {
        return ControlTransferOut(VendorCommands.SetLevellerMasks, 0,
            new[] { detectorMask, applyMask });
    }

    /// <summary>Get the leveller detector/apply masks (0xDF). Null on STALL/short read.</summary>
    public (byte detector, byte apply)? GetLevellerMasks()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerMasks, 0, 2);
        if (response == null || response.Length < 2) return null;
        return (response[0], response[1]);
    }

    /// <summary>Set the per-output loudness compensation mask (0xFA, V19). uint16 LE.</summary>
    public bool SetLoudnessMask(ushort mask)
    {
        return ControlTransferOut(VendorCommands.SetLoudnessMask, 0, BitConverter.GetBytes(mask));
    }

    /// <summary>Get the per-output loudness mask (0xFB). Null on STALL/short read.</summary>
    public ushort? GetLoudnessMask()
    {
        var response = ControlTransferIn(VendorCommands.GetLoudnessMask, 0, 2);
        if (response == null || response.Length < 2) return null;
        return BitConverter.ToUInt16(response, 0);
    }

    /// <summary>Set the per-output-pair crossfeed mask (0xFC, V20). uint8; bit p = outputs 2p/2p+1.</summary>
    public bool SetCrossfeedOutputs(byte pairMask)
    {
        return ControlTransferOut(VendorCommands.SetCrossfeedOutputs, 0, new[] { pairMask });
    }

    /// <summary>Get the crossfeed output-pair mask (0xFD). Null on STALL/short read.</summary>
    public byte? GetCrossfeedOutputs()
    {
        var response = ControlTransferIn(VendorCommands.GetCrossfeedOutputs, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    #endregion

    /// <summary>
    /// Reboot the device into UF2 bootloader mode. Device disconnects immediately.
    /// </summary>
    public void EnterBootloaderMode()
    {
        ControlTransferIn(VendorCommands.EnterBootloader, 0, 1);
    }

    #region Notification Endpoint (Bulk IN, V7+ firmware)

    /// <summary>
    /// Open EP 0x83 (bulk IN, 64-byte packets) and start a background reader
    /// that decodes v2 PARAM_CHANGED / BULK_INVALIDATED / PRESET_LOADED events.
    /// The firmware always keeps this endpoint armed with a 1-byte IDLE
    /// keep-alive when nothing is pending, so reads return promptly.
    /// </summary>
    private void StartNotifyListener(IUsbDevice dev)
    {
        try
        {
            _notifyReader = dev.OpenEndpointReader(ReadEndpointID.Ep03, NotifyPacketSize, EndpointType.Bulk);
        }
        catch
        {
            _notifyReader = null;
            return;
        }

        _notifyStop = false;
        _notifyThread = new Thread(NotifyReadLoop)
        {
            IsBackground = true,
            Name = "DSPi notify"
        };
        _notifyThread.Start();
    }

    private void StopNotifyListener()
    {
        _notifyStop = true;
        // Closing the device handle (in HandleDisconnect, right after this call)
        // will cause any pending Read() to error out and the loop to exit. We
        // join with a short timeout so a stuck thread doesn't block disconnect.
        var thread = _notifyThread;
        _notifyThread = null;
        _notifyReader = null;
        if (thread != null && thread.IsAlive)
        {
            try { thread.Join(500); } catch { }
        }
    }

    private void NotifyReadLoop()
    {
        var buf = new byte[NotifyPacketSize];
        while (!_notifyStop)
        {
            UsbEndpointReader? reader = _notifyReader;
            if (reader == null) break;

            int len;
            LibUsbDotNet.Error err;
            try
            {
                err = reader.Read(buf, 1000, out len);
            }
            catch
            {
                // Device closed underneath us; exit cleanly.
                break;
            }

            if (_notifyStop) break;

            if (err == LibUsbDotNet.Error.Timeout || err == LibUsbDotNet.Error.Interrupted)
                continue;
            if (err == LibUsbDotNet.Error.NoDevice || err == LibUsbDotNet.Error.NotFound)
                break;
            if (err != LibUsbDotNet.Error.Success || len <= 0)
                continue;

            // Fire the raw-packet event before decoding so the Interrupt
            // Monitor sees IDLE keep-alives, unknown event IDs, and malformed
            // packets too — not just the subset ProcessNotifyPacket understands.
            // Copy the slice we care about; the next read overwrites buf.
            var rawListeners = NotifyPacketReceived;
            if (rawListeners != null)
            {
                var copy = new byte[len];
                Buffer.BlockCopy(buf, 0, copy, 0, len);
                try { rawListeners(this, new NotifyPacket { Data = copy, Timestamp = DateTime.Now }); }
                catch { /* a misbehaving subscriber must not break the reader */ }
            }

            try { ProcessNotifyPacket(buf, len); }
            catch { /* a malformed packet is not fatal */ }
        }
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
                    // Map wire channel → app channel id; ignore changes on wire
                    // channels the app doesn't model (extra inputs 2..7 / padding).
                    int appCh = ChannelMap.WireToApp(ch, NumInputChannels);
                    if (appCh < 0) return;
                    // Parser expects the band entry at the given offset within the
                    // buffer. The notify packet places the payload at offset 12,
                    // so shift accordingly by passing the payload region.
                    var bandBuf = new byte[BulkParamsParser.WireBandSize];
                    Buffer.BlockCopy(buf, 12, bandBuf, 0, BulkParamsParser.WireBandSize);
                    var fp = BulkParamsParser.ParseBand(bandBuf, 0);
                    if (IsOwnEcho(appCh, b, fp)) return;
                    BandParamNotified?.Invoke(this, new BandParamNotification
                    {
                        Channel = appCh,
                        Band = b,
                        Params = fp,
                        Source = source
                    });
                }
                // Crossover band change (V11+): offset inside
                // WireBulkParams.crossovers (WireBandParams[11][4]), size 16.
                // Reports the LOCAL crossover band (0..3); the subscriber maps
                // it back to wire band 20..23 if needed.
                else if (size == BulkParamsParser.WireBandSize
                    && offset >= CrossoverWireOffset
                    && offset <  CrossoverWireOffset
                                 + BulkParamsParser.WireMaxChannels
                                 * CrossoverBandCount
                                 * BulkParamsParser.WireBandSize
                    && (offset - CrossoverWireOffset)
                       % BulkParamsParser.WireBandSize == 0)
                {
                    int flat = (offset - CrossoverWireOffset) / BulkParamsParser.WireBandSize;
                    int ch = flat / CrossoverBandCount;
                    int local = flat % CrossoverBandCount;
                    int appCh = ChannelMap.WireToApp(ch, NumInputChannels);
                    if (appCh < 0) return; // crossover only lives on output channels
                    var bandBuf = new byte[BulkParamsParser.WireBandSize];
                    Buffer.BlockCopy(buf, 12, bandBuf, 0, BulkParamsParser.WireBandSize);
                    var fp = BulkParamsParser.ParseBand(bandBuf, 0);
                    if (IsOwnEcho(appCh, CrossoverFilter.XoverBandBase + local, fp)) return;
                    XoverBandParamNotified?.Invoke(this, new BandParamNotification
                    {
                        Channel = appCh,
                        Band = local,
                        Params = fp,
                        Source = source
                    });
                }
                // Channel name change: offset is in WireBulkParams.channel_names range,
                // size is exactly WIRE_NAME_LEN (32).
                else if (size == WireChannelNameLen
                    && offset >= ChannelNamesWireOffset
                    && offset <  ChannelNamesWireOffset + BulkParamsParser.WireMaxChannels * WireChannelNameLen
                    && (offset - ChannelNamesWireOffset) % WireChannelNameLen == 0)
                {
                    int ch = (offset - ChannelNamesWireOffset) / WireChannelNameLen;
                    int appCh = ChannelMap.WireToApp(ch, NumInputChannels);
                    if (appCh < 0) return;
                    var name = System.Text.Encoding.UTF8.GetString(buf, 12, size).TrimEnd('\0');
                    ChannelNameNotified?.Invoke(this, new ChannelNameNotification
                    {
                        ChannelIndex = appCh,
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
                // Everything else (master volume, outputs, loudness, crossfeed,
                // leveller, psybass, I2S/ADAT/DAC/LG config, delays, preamp, …):
                // surface generically so the VM can keep those UIs live.
                else
                {
                    var payload = new byte[size];
                    Buffer.BlockCopy(buf, 12, payload, 0, size);
                    ParamChangedNotified?.Invoke(this, new ParamChangedNotification
                    {
                        Offset = offset,
                        Size = size,
                        Source = source,
                        Payload = payload
                    });
                }
                break;

            case 0x03: // BULK_INVALIDATED
                if (len < 5) return;
                BulkInvalidated?.Invoke(this, (ParamSource)buf[4]);
                break;

            case 0x04: // PRESET_LOADED (followed by BULK_INVALIDATED)
                if (len < 5) return;
                PresetLoadedNotified?.Invoke(this, buf[4]);
                break;

            case 0x05: // INPUT_FORMAT — active USB input channel count changed
                if (len < 5) return;
                InputFormatNotified?.Invoke(this, buf[4]);
                break;

            case 0x0C: // CS_AUX: [ver, evt, flags, seq, slot, state, level_q8 LE, src]
                if (len < 9) return;
                CsAuxNotified?.Invoke(this, new CsAuxNotification(buf[4], buf[5] != 0, (ushort)(buf[6] | buf[7] << 8)));
                break;

            case 0x07: // SIGGEN_STATE
            case 0x08: // ADAT (output) STATE
            case 0x09: // I2S_SLAVE_STATE
            case 0x0B: // ADAT_INPUT_STATE
                StatusEventNotified?.Invoke(this, eventId);
                break;
        }
    }

    #endregion

    /// <summary>
    /// Fetch all DSP parameters (wire-format V24, 5900 bytes). The V16+ payload
    /// exceeds WinUSB's documented 4096-byte control-transfer cap, so it is read
    /// in ≤2048-byte sequential chunks via REQ_GET_ALL_PARAMS_CHUNK (0xA2). The
    /// actual length comes from the header, so this adapts to any wire version.
    ///
    /// Requesting offset 0 makes the device snapshot the whole struct into an
    /// internal buffer, so every chunk comes from one coherent image. The
    /// firmware reaps the session if any non-chunk vendor request interleaves,
    /// so the entire read is done while holding <c>_lock</c> — the reentrant
    /// lock keeps the status-poll thread out until all chunks land. A STALL or
    /// short read aborts and returns null; the caller retries the whole fetch.
    /// </summary>
    public byte[]? GetAllParams()
    {
        const int chunkSize = 2048; // ≤ 4096 WinUSB cap; 3 transfers for 5876 B

        lock (_lock)
        {
            if (_device == null) return null;

            // First chunk (offset 0) opens the session and carries the header,
            // from which we read the authoritative payload length.
            var first = ControlTransferIn(VendorCommands.GetAllParamsChunk, 0, chunkSize);
            if (first == null || first.Length < 16)
                return null;

            int total = BitConverter.ToUInt16(first, 6); // WireHeader.payload_length
            if (total < BulkParamsParser.PacketSizeV20)
                total = BulkParamsParser.PacketSizeV20; // header underreport safety net

            var buffer = new byte[total];
            int offset = Math.Min(first.Length, total);
            Buffer.BlockCopy(first, 0, buffer, 0, offset);

            while (offset < total)
            {
                int want = Math.Min(chunkSize, total - offset);
                var chunk = ControlTransferIn(VendorCommands.GetAllParamsChunk, (ushort)offset, want);
                if (chunk == null || chunk.Length == 0)
                    return null; // STALL/short → session lost; caller re-fetches
                int n = Math.Min(chunk.Length, total - offset);
                Buffer.BlockCopy(chunk, 0, buffer, offset, n);
                offset += n;
            }

            return buffer;
        }
    }

    // ── Test signal generator (0xA4–0xA8) ──

    /// <summary>Probe siggen support + read caps (0xA8, wValue 0xFFFF). Null if the
    /// firmware STALLs (feature unsupported).</summary>
    public SiggenCaps? GetSiggenCaps()
    {
        var r = ControlTransferIn(VendorCommands.SiggenGetCaps, 0xFFFF, SiggenCaps.WireSize);
        return r == null ? null : SiggenCaps.FromBytes(r);
    }

    /// <summary>Read one signal type's descriptor (0xA8, wValue = type index).</summary>
    public SiggenTypeDesc? GetSiggenTypeDesc(int index)
    {
        var r = ControlTransferIn(VendorCommands.SiggenGetCaps, (ushort)(index & 0xFF), SiggenTypeDesc.WireSize);
        return r == null ? null : SiggenTypeDesc.FromBytes(r);
    }

    /// <summary>Read the applied siggen config (0xA5).</summary>
    public SiggenConfig? GetSiggenConfig()
    {
        var r = ControlTransferIn(VendorCommands.SiggenGetConfig, 0, SiggenConfig.WireSize);
        return r == null ? null : SiggenConfig.FromBytes(r);
    }

    /// <summary>Stage a siggen config (0xA4). Does NOT start playback — follow with
    /// SiggenControl(Start). Returns false if the firmware rejects (STALL).</summary>
    public bool SetSiggenConfig(SiggenConfig config) =>
        ControlTransferOut(VendorCommands.SiggenSetConfig, 0, config.ToBytes());

    /// <summary>Start/stop the generator (0xA6). action = SiggenControl.Start/Stop/StopNow.
    /// Issued as an IN transfer per the firmware "write as read" contract.</summary>
    public bool SiggenControl(byte action)
    {
        var r = ControlTransferIn(VendorCommands.SiggenControl, action, 1);
        return r != null && r.Length >= 1 && r[0] == 1;
    }

    /// <summary>Read live generator status (0xA7). Null on STALL/failure.</summary>
    public SiggenStatus? GetSiggenStatus()
    {
        var r = ControlTransferIn(VendorCommands.SiggenGetStatus, 0, SiggenStatus.WireSize);
        return r == null ? null : SiggenStatus.FromBytes(r);
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
            FifoFillPct = BitConverter.ToUInt16(r, 12),
            LibState = r[14],
            CallbackCounts = r[15],
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
    /// Get the GPIO pin configured for a S/PDIF receive input (index 0..2;
    /// there are always 3 selectable inputs sharing one receiver).
    /// </summary>
    public byte? GetSpdifRxPin(int index = 0)
    {
        var response = ControlTransferIn(VendorCommands.GetSpdifRxPin, (ushort)(index & 0xFF), 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    /// <summary>Largest S/PDIF input count this client can represent. Firmware
    /// carried three before wire V28 and four from V28; the device reports its
    /// own count, so this only bounds the read and the returned array.</summary>
    public const int MaxSpdifInputs = 4;

    /// <summary>
    /// Get the multi-SPDIF input config (0xEF): input count, an enable mask
    /// (bit0=input1 always on, bit1=SPDIF2 .. bit3=SPDIF4), and one GPIO per
    /// input. The response is 2 + count bytes, so a three-input firmware answers
    /// one byte short of a four-input one; the returned array is sized to what
    /// actually arrived. Null if the firmware STALLs (single-input firmware).
    /// </summary>
    public (byte count, byte enableMask, byte[] pins)? GetSpdifInputConfig()
    {
        var r = ControlTransferIn(VendorCommands.GetSpdifInputConfig, 0, 2 + MaxSpdifInputs);
        if (r == null || r.Length < 3) return null;
        int count = Math.Min(r[0], r.Length - 2);
        var pins = new byte[count];
        Array.Copy(r, 2, pins, 0, count);
        return (r[0], r[1], pins);
    }

    /// <summary>
    /// Enable or disable an optional S/PDIF input (index 1..3; input 0 is always
    /// enabled). Encodes (index&lt;&lt;8)|enable in wValue; returns a
    /// <see cref="PinConfigResult"/> status byte (0xFF on transfer failure).
    /// </summary>
    public byte SetSpdifInputEnable(int index, bool enable)
    {
        ushort wValue = (ushort)(((index & 0xFF) << 8) | (enable ? 1 : 0));
        var response = ControlTransferIn(VendorCommands.SetSpdifInputEnable, wValue, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Change the S/PDIF RX GPIO pin. The pin number is encoded in wValue and the
    /// firmware returns a 1-byte status code (0 = success, 1 = invalid pin,
    /// 2 = pin in use, 3 = output active — must switch to USB first).
    /// Returns 0xFF on transfer failure.
    /// </summary>
    public byte SetSpdifRxPin(byte pin, int index = 0)
    {
        ushort wValue = (ushort)(((index & 0xFF) << 8) | pin);
        var response = ControlTransferIn(VendorCommands.SetSpdifRxPin, wValue, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    // ── ADAT bulk output (V17+, RP2350 only) ──

    /// <summary>Enable/disable the ADAT optical output. Returns a
    /// <see cref="PinConfigResult"/> status byte (0xFF on transfer failure).
    /// Enabling validates the configured pin; RP2040 returns InvalidOutput.</summary>
    public byte SetAdatEnable(bool enable)
    {
        var r = ControlTransferIn(VendorCommands.SetAdatEnable, (ushort)(enable ? 1 : 0), 1);
        return r != null && r.Length >= 1 ? r[0] : (byte)0xFF;
    }

    /// <summary>Configured ADAT enable flag (0/1). Null on transfer failure.</summary>
    public byte? GetAdatEnable()
    {
        var r = ControlTransferIn(VendorCommands.GetAdatEnable, 0, 1);
        return r != null && r.Length >= 1 ? r[0] : (byte?)null;
    }

    /// <summary>Set the ADAT data GPIO (0 resets to the platform default, GPIO 12).
    /// Returns a <see cref="PinConfigResult"/> status byte (0xFF on failure).</summary>
    public byte SetAdatPin(byte pin)
    {
        var r = ControlTransferIn(VendorCommands.SetAdatPin, pin, 1);
        return r != null && r.Length >= 1 ? r[0] : (byte)0xFF;
    }

    /// <summary>Configured ADAT data GPIO. Null on transfer failure.</summary>
    public byte? GetAdatPin()
    {
        var r = ControlTransferIn(VendorCommands.GetAdatPin, 0, 1);
        return r != null && r.Length >= 1 ? r[0] : (byte?)null;
    }

    /// <summary>Read the 8-byte live ADAT status (0xCE). Null on transfer failure.</summary>
    public AdatStatus? GetAdatStatus()
    {
        var r = ControlTransferIn(VendorCommands.GetAdatStatus, 0, AdatStatus.WireSize);
        return r == null ? null : AdatStatus.FromBytes(r);
    }

    // ── Control surfaces + IR remote (0x84–0x8F, 0x9D/0x9E) ──────────────────
    //
    // Binding/name/IR-command SETs are deferred: the OUT (or write-as-read GET)
    // latches into a single-deep pending buffer and firmware immediately records
    // CS_STATUS_PENDING; the 1 kHz loop runs the apply and overwrites with the
    // real verdict. The host polls GetCsStatus until LastSlot names the op and
    // LastStatus leaves Pending. Every deferred op (binding, name, IR cmd, group,
    // macro header, macro step, save, revert) shares one LastStatus/LastSlot
    // channel — serialize them. LastSlot tags: the plain slot for a binding or
    // name, 0x40|idx for a group, 0x60|idx for a macro, 0x80|sub for an IR
    // command, 0xFF for save/revert.

    /// <summary>Read the caps header + type table (0x86 wValue 0xFFFF). Null if the
    /// firmware STALLs (older firmware without the feature). Request generously so a
    /// larger future type table still fits (parse is length-tolerant).</summary>
    public CsCapsHeader? GetCsCaps()
    {
        var r = ControlTransferIn(VendorCommands.GetCsCaps, CsLimits.CapsAll, 64);
        return r == null ? null : CsCapsHeader.FromBytes(r);
    }

    /// <summary>Read one noun's 12-byte descriptor (0x86 wValue = noun index).</summary>
    public CsNounDesc? GetCsNounDesc(int noun)
    {
        var r = ControlTransferIn(VendorCommands.GetCsCaps, (ushort)(noun & 0xFF), CsNounDesc.WireSize);
        return r == null ? null : CsNounDesc.FromBytes(r);
    }

    /// <summary>Every slot's aux on/off flag and 8.8 level (0x05, wValue 0xFFFF;
    /// zeros on slots that hold no aux output). Null on transfer failure.</summary>
    public (bool[] On, ushort[] LevelQ8)? GetCsAuxStates()
    {
        var r = ControlTransferIn(VendorCommands.GetCsAuxState, 0xFFFF, 3 * CsLimits.MaxBindings);
        if (r == null || r.Length < 3 * CsLimits.MaxBindings) return null;
        var on = new bool[CsLimits.MaxBindings];
        var level = new ushort[CsLimits.MaxBindings];
        for (int i = 0; i < CsLimits.MaxBindings; i++)
        {
            on[i] = r[i] != 0;
            level[i] = BitConverter.ToUInt16(r, CsLimits.MaxBindings + 2 * i);
        }
        return (on, level);
    }

    /// <summary>Switch an aux output on or off now (0x04). Not persisted; false if
    /// the slot holds no live aux output.</summary>
    public bool SetCsAuxState(int slot, bool on) =>
        ControlTransferOut(VendorCommands.SetCsAuxState, (ushort)(slot & 0xFF), new[] { (byte)(on ? 1 : 0) });

    /// <summary>Set a dimmable aux output's level now (0x06), 8.8 percent,
    /// clamped to 100 % by the device. AUX_PWM only.</summary>
    public bool SetCsAuxLevel(int slot, ushort levelQ8) =>
        ControlTransferOut(VendorCommands.SetCsAuxLevel, (ushort)(slot & 0xFF), BitConverter.GetBytes(levelQ8));

    /// <summary>Read the live 24-byte binding for a slot (0x85).</summary>
    public CsBinding? GetCsBinding(int slot)
    {
        var r = ControlTransferIn(VendorCommands.GetCsBinding, (ushort)(slot & 0xFF), CsBinding.WireSize);
        return r == null ? null : CsBinding.FromBytes(r);
    }

    /// <summary>Read the live 41-byte status packet (0x87). Null on transfer failure.
    /// A pre-caps-v6 firmware answers 32 bytes; the parse handles both.</summary>
    public CsStatusPacket? GetCsStatus()
    {
        var r = ControlTransferIn(VendorCommands.GetCsStatus, 0, 41);
        return r == null ? null : CsStatusPacket.FromBytes(r);
    }

    /// <summary>Read a slot's live name (0x8C), NUL-terminated. "" if unset.</summary>
    public string GetCsName(int slot)
    {
        var r = ControlTransferIn(VendorCommands.GetCsName, (ushort)(slot & 0xFF), CsLimits.NameLen);
        if (r == null) return "";
        int len = System.Array.IndexOf(r, (byte)0);
        if (len < 0) len = r.Length;
        return System.Text.Encoding.UTF8.GetString(r, 0, len);
    }

    /// <summary>Read a live 16-byte IR command sub-slot (0x8E).</summary>
    public IrCommand? GetCsIrCommand(int sub)
    {
        var r = ControlTransferIn(VendorCommands.GetCsIrCmd, (ushort)(sub & 0xFF), IrCommand.WireSize);
        return r == null ? null : IrCommand.FromBytes(r);
    }

    /// <summary>Poll GetCsStatus until the deferred op for <paramref name="expectedSlot"/>
    /// resolves (LastSlot matches and LastStatus != Pending), or a ~600 ms budget
    /// elapses. Returns the resolved CS status byte (or Pending if it never settled,
    /// 0xFF on a status-read failure). Blocks — call off the UI thread.</summary>
    public byte PollCsDeferred(byte expectedSlot)
    {
        for (int i = 0; i < 30; i++)
        {
            var st = GetCsStatus();
            if (st == null) return 0xFF;
            if (st.LastSlot == expectedSlot && st.LastStatus != CsStatus.Pending)
                return st.LastStatus;
            System.Threading.Thread.Sleep(20);
        }
        return CsStatus.Pending;
    }

    /// <summary>Stage a binding (0x84 OUT) and wait for the deferred apply. Returns
    /// the CS status byte.</summary>
    public byte SetCsBinding(int slot, CsBinding binding)
    {
        if (!ControlTransferOut(VendorCommands.SetCsBinding, (ushort)(slot & 0xFF), binding.ToBytes()))
            return 0xFF;
        return PollCsDeferred((byte)(slot & 0xFF));
    }

    /// <summary>Stage a slot name (0x8B OUT). Empty clears the name (single NUL —
    /// an empty payload is rejected). Truncated to 31 UTF-8 bytes + NUL.</summary>
    public byte SetCsName(int slot, string name)
    {
        byte[] payload;
        if (string.IsNullOrEmpty(name))
        {
            payload = new byte[] { 0 };
        }
        else
        {
            var raw = System.Text.Encoding.UTF8.GetBytes(name);
            int len = System.Math.Min(raw.Length, CsLimits.NameLen - 1);
            payload = new byte[len + 1];
            System.Array.Copy(raw, payload, len);
            // payload[len] stays 0 (NUL terminator)
        }
        if (!ControlTransferOut(VendorCommands.SetCsName, (ushort)(slot & 0xFF), payload))
            return 0xFF;
        return PollCsDeferred((byte)(slot & 0xFF));
    }

    /// <summary>Stage an IR command sub-slot (0x8D OUT) and wait for the apply.
    /// The poll key is 0x80|sub.</summary>
    public byte SetCsIrCommand(int sub, IrCommand cmd)
    {
        if (!ControlTransferOut(VendorCommands.SetCsIrCmd, (ushort)(sub & 0xFF), cmd.ToBytes()))
            return 0xFF;
        return PollCsDeferred((byte)(CsLimits.LastSlotIrFlag | (sub & 0x7F)));
    }

    /// <summary>Persist the whole live config to flash (0x9D) and wait for the
    /// deferred write (poll key 0xFF).</summary>
    public byte CsSave()
    {
        var r = ControlTransferIn(VendorCommands.CsSave, 0, 1);
        if (r == null) return 0xFF;
        return PollCsDeferred(CsLimits.LastSlotSave);
    }

    /// <summary>Discard the live preview and re-apply the stored config (0x9E).</summary>
    public byte CsRevert()
    {
        var r = ControlTransferIn(VendorCommands.CsRevert, 0, 1);
        if (r == null) return 0xFF;
        return PollCsDeferred(CsLimits.LastSlotSave);
    }

    // ── Control-surface groups and macros (0x20–0x26, caps v9) ───────────────

    /// <summary>Read one live 40-byte target group (0x21). Null on transfer failure
    /// or a pre-caps-v9 firmware (which STALLs).</summary>
    public CsGroup? GetCsGroup(int idx)
    {
        var r = ControlTransferIn(VendorCommands.GetCsGroup, (ushort)(idx & 0xFF), CsGroup.WireSize);
        return r == null ? null : CsGroup.FromBytes(r);
    }

    /// <summary>Stage a target group (0x20 OUT) and wait for the deferred apply.
    /// The poll key is 0x40|idx. An all-zero record clears the slot.</summary>
    public byte SetCsGroup(int idx, CsGroup group)
    {
        if (!ControlTransferOut(VendorCommands.SetCsGroup, (ushort)(idx & 0xFF), group.ToBytes()))
            return 0xFF;
        return PollCsDeferred((byte)(CsLimits.LastSlotGroupFlag | (idx & 0x3F)));
    }

    /// <summary>Read one live 132-byte macro, steps included (0x23).</summary>
    public CsMacro? GetCsMacro(int idx)
    {
        var r = ControlTransferIn(VendorCommands.GetCsMacro, (ushort)(idx & 0xFF), CsMacro.WireSize);
        return r == null ? null : CsMacro.FromBytes(r);
    }

    /// <summary>Stage a macro header — name and step count (0x22 OUT). Poll key
    /// 0x60|idx. Write the steps first so a concurrent fire never sees a count
    /// past the written steps.</summary>
    public byte SetCsMacroHeader(int idx, CsMacro macro)
    {
        byte key = (byte)(CsLimits.LastSlotMacroFlag | (idx & 0x1F));
        return SetCsDeferredWithRetry(VendorCommands.SetCsMacro, (ushort)(idx & 0xFF),
                                      macro.HeaderToBytes(), key);
    }

    /// <summary>Stage one macro step (0x24 OUT, wValue = (step &lt;&lt; 8) | macro).
    /// An all-zero record clears it. Poll key 0x60|macro, shared with the header.</summary>
    public byte SetCsMacroStep(int idx, int step, CsMacroStep value)
    {
        ushort wValue = (ushort)(((step & 0xFF) << 8) | (idx & 0xFF));
        byte key = (byte)(CsLimits.LastSlotMacroFlag | (idx & 0x1F));
        return SetCsDeferredWithRetry(VendorCommands.SetCsMacroStep, wValue, value.ToBytes(), key);
    }

    /// <summary>Stage a deferred CS SET, retrying on BUSY. Writing a whole macro
    /// is a burst of same-key SETs, and the poll can only key off the shared
    /// LastStatus/LastSlot pair: if the firmware hasn't picked up the previous
    /// handoff yet, the poll can read that one's verdict and let the next SET land
    /// on a still-occupied pending slot. BUSY explicitly means "retry shortly".</summary>
    private byte SetCsDeferredWithRetry(byte request, ushort wValue, byte[] payload, byte pollKey)
    {
        byte status = CsStatus.Busy;
        for (int attempt = 0; attempt < 4 && status == CsStatus.Busy; attempt++)
        {
            if (attempt > 0) System.Threading.Thread.Sleep(10);
            if (!ControlTransferOut(request, wValue, payload)) return 0xFF;
            status = PollCsDeferred(pollKey);
        }
        return status;
    }

    /// <summary>Fire a macro (0x25). Immediate, not deferred: false means the
    /// firmware STALLed (bad index or step count), with the reason left in the
    /// status packet.</summary>
    public bool CsMacroFire(int idx) =>
        ControlTransferIn(VendorCommands.CsMacroFire, (ushort)(idx & 0xFF), 1) != null;

    /// <summary>Cancel the running macro (0x25 wValue 0xFFFF). Remaining steps are
    /// dropped; anything already dispatched stands.</summary>
    public void CsMacroCancel() =>
        ControlTransferIn(VendorCommands.CsMacroFire, CsLimits.MacroFireCancel, 1);

    /// <summary>Read the 24-byte group/macro status packet (0x26). Null on a
    /// pre-caps-v9 firmware.</summary>
    public CsExtStatusPacket? GetCsExtStatus()
    {
        var r = ControlTransferIn(VendorCommands.GetCsExtStatus, 0, CsExtStatusPacket.WireSize);
        return r == null ? null : CsExtStatusPacket.FromBytes(r);
    }

    // -- Control-surface I2C display (0x27-0x2B, caps v10) --------------------

    /// <summary>Read the display config and the device's own page / model limits
    /// (0x28). Null on transfer failure or a pre-v10 firmware, which STALLs.</summary>
    public CsDisplayCfgRead? GetCsDisplayCfg()
    {
        var r = ControlTransferIn(VendorCommands.GetCsDisplayCfg, 0, CsDisplayCfg.GetWireSize);
        return r == null ? null : CsDisplayCfgRead.FromBytes(r);
    }

    /// <summary>Read one live 4-byte page record (0x2A).</summary>
    public CsDisplayPage? GetCsDisplayPage(int index)
    {
        var r = ControlTransferIn(VendorCommands.GetCsDisplayPage, (ushort)(index & 0xFF), CsDisplayPage.WireSize);
        return r == null ? null : CsDisplayPage.FromBytes(r);
    }

    /// <summary>Read the live 8-byte panel state (0x2B): whether it came up, what
    /// it is showing, and the running I2C abort count.</summary>
    public CsDisplayStatus? GetCsDisplayStatus()
    {
        var r = ControlTransferIn(VendorCommands.GetCsDisplayStatus, 0, CsDisplayStatus.WireSize);
        return r == null ? null : CsDisplayStatus.FromBytes(r);
    }

    /// <summary>Stage the display config (0x27 OUT) and wait for the deferred
    /// apply, which reports under the bare 0x50 tag.</summary>
    public byte SetCsDisplayCfg(CsDisplayCfg cfg)
    {
        if (!ControlTransferOut(VendorCommands.SetCsDisplayCfg, 0, cfg.ToBytes()))
            return CsStatus.Busy;
        return PollCsDeferred(CsLimits.LastSlotDisplayFlag);
    }

    /// <summary>Stage one display page (0x29 OUT; an all-zero record clears it) and
    /// wait for the apply, reported under 0x50 | page.</summary>
    public byte SetCsDisplayPage(int index, CsDisplayPage page)
    {
        if (!ControlTransferOut(VendorCommands.SetCsDisplayPage, (ushort)(index & 0xFF), page.ToBytes()))
            return CsStatus.Busy;
        return PollCsDeferred((byte)(CsLimits.LastSlotDisplayFlag | (index & 0x0F)));
    }

    /// <summary>Arm IR learn (0x8F wValue=1). False if the firmware STALLs (no live
    /// IR receiver → CS_STATUS_NO_IR).</summary>
    public bool CsIrLearnArm() =>
        ControlTransferIn(VendorCommands.CsIrLearn, CsIrLearnAction.Arm, 1) != null;

    /// <summary>Cancel IR learn (0x8F wValue=0).</summary>
    public void CsIrLearnCancel() =>
        ControlTransferIn(VendorCommands.CsIrLearn, CsIrLearnAction.Cancel, 1);

    /// <summary>Read the IR-learn result (0x8F wValue=2). Null on transfer failure.</summary>
    public CsIrLearnResult? CsIrLearnRead()
    {
        var r = ControlTransferIn(VendorCommands.CsIrLearn, CsIrLearnAction.Read, CsIrLearnResult.WireSize);
        return r == null ? null : CsIrLearnResult.FromBytes(r);
    }

    // ── UART / I2C control interfaces (0xF5–0xF9) ────────────────────────────

    /// <summary>Read the persisted UART control-interface config (0xF6). Null if
    /// the firmware STALLs (older firmware without the feature).</summary>
    public UartCtrlConfig? GetUartCtrlConfig()
    {
        var r = ControlTransferIn(VendorCommands.GetUartConfig, 0, UartCtrlConfig.WireSize);
        return r == null ? null : UartCtrlConfig.FromBytes(r);
    }

    /// <summary>Read the persisted I2C control-interface config (0xF8).</summary>
    public I2cCtrlConfig? GetI2cCtrlConfig()
    {
        var r = ControlTransferIn(VendorCommands.GetI2cConfig, 0, I2cCtrlConfig.WireSize);
        return r == null ? null : I2cCtrlConfig.FromBytes(r);
    }

    /// <summary>Read the live control-interface status (0xF9). Null on failure.</summary>
    public CtrlIfaceStatus? GetCtrlIfaceStatus()
    {
        var r = ControlTransferIn(VendorCommands.GetCtrlIfaceStatus, 0, CtrlIfaceStatus.WireSize);
        return r == null ? null : CtrlIfaceStatus.FromBytes(r);
    }

    /// <summary>Apply a UART control-interface config (0xF5 OUT, USB only). The
    /// apply is deferred + flash-persisted on the firmware's main loop, so this
    /// waits then reads the authoritative PIN_CONFIG_* outcome via 0xF9. Returns
    /// 0xFF on a transfer failure. Blocking — call off the UI thread.</summary>
    public byte SetUartCtrlConfig(UartCtrlConfig config)
    {
        if (!ControlTransferOut(VendorCommands.SetUartConfig, 0, config.ToBytes()))
            return 0xFF;
        System.Threading.Thread.Sleep(250); // deferred apply + ~45 ms flash blackout
        var st = GetCtrlIfaceStatus();
        return st?.UartLastStatus ?? (byte)0xFF;
    }

    /// <summary>Apply an I2C control-interface config (0xF7 OUT, USB only). Deferred
    /// apply; outcome read back via 0xF9 (I2cLastStatus).</summary>
    public byte SetI2cCtrlConfig(I2cCtrlConfig config)
    {
        if (!ControlTransferOut(VendorCommands.SetI2cConfig, 0, config.ToBytes()))
            return 0xFF;
        System.Threading.Thread.Sleep(250);
        var st = GetCtrlIfaceStatus();
        return st?.I2cLastStatus ?? (byte)0xFF;
    }

    // ── Stereo upmixer (0x4A–0x4E, RP2350 only) ──────────────────────────────

    /// <summary>Apply a whole upmix config atomically (0x4A, 44 bytes).</summary>
    public bool SetUpmixConfig(UpmixConfig cfg) =>
        ControlTransferOut(VendorCommands.UpmixSetConfig, 0, cfg.ToBytes());

    /// <summary>Read the applied upmix config (0x4B). Null on STALL/failure;
    /// on RP2040 the read succeeds but every field is zero.</summary>
    public UpmixConfig? GetUpmixConfig()
    {
        var r = ControlTransferIn(VendorCommands.UpmixGetConfig, 0, UpmixConfig.WireSize);
        return r == null ? null : UpmixConfig.FromBytes(r);
    }

    /// <summary>Set one upmix param (0x4C). Everything travels as a float,
    /// including enable and the mode params. Preferred for live sliders (no
    /// read-modify-write race against other controllers).</summary>
    public bool SetUpmixParam(ushort paramId, float value) =>
        ControlTransferOut(VendorCommands.UpmixSetParam, paramId, BitConverter.GetBytes(value));

    /// <summary>Read one upmix param as a float (0x4D).</summary>
    public float? GetUpmixParam(ushort paramId)
    {
        var r = ControlTransferIn(VendorCommands.UpmixGetParam, paramId, 4);
        return r != null && r.Length >= 4 ? BitConverter.ToSingle(r, 0) : (float?)null;
    }

    /// <summary>Read live upmixer telemetry (0x4E, 16 bytes). Poll at 5-20 Hz.</summary>
    public UpmixStatus? GetUpmixStatus()
    {
        var r = ControlTransferIn(VendorCommands.UpmixGetStatus, 0, UpmixStatus.WireSize);
        return r == null ? null : UpmixStatus.FromBytes(r);
    }

    // ── Psychoacoustic bass (0x30–0x3D) ──────────────────────────────────────

    private bool SetPsybassFloat(byte req, float value) =>
        ControlTransferOut(req, 0, BitConverter.GetBytes(value));

    private float? GetPsybassFloat(byte req)
    {
        var r = ControlTransferIn(req, 0, 4);
        return r != null && r.Length >= 4 ? BitConverter.ToSingle(r, 0) : (float?)null;
    }

    public bool SetPsybassEnabled(bool on) =>
        ControlTransferOut(VendorCommands.SetPsybass, 0, new[] { on ? (byte)1 : (byte)0 });

    /// <summary>Read psybass enable (0x31). Null if the firmware STALLs (feature
    /// unsupported / pre-V23).</summary>
    public bool? GetPsybassEnabled()
    {
        var r = ControlTransferIn(VendorCommands.GetPsybass, 0, 1);
        return r != null && r.Length >= 1 ? r[0] != 0 : (bool?)null;
    }

    public bool SetPsybassCutoff(float hz) => SetPsybassFloat(VendorCommands.SetPsybassCutoff, hz);
    public float? GetPsybassCutoff() => GetPsybassFloat(VendorCommands.GetPsybassCutoff);
    public bool SetPsybassHarmonics(float db) => SetPsybassFloat(VendorCommands.SetPsybassHarmonics, db);
    public float? GetPsybassHarmonics() => GetPsybassFloat(VendorCommands.GetPsybassHarmonics);
    public bool SetPsybassDrive(float db) => SetPsybassFloat(VendorCommands.SetPsybassDrive, db);
    public float? GetPsybassDrive() => GetPsybassFloat(VendorCommands.GetPsybassDrive);
    public bool SetPsybassCharacter(float pct) => SetPsybassFloat(VendorCommands.SetPsybassCharacter, pct);
    public float? GetPsybassCharacter() => GetPsybassFloat(VendorCommands.GetPsybassCharacter);
    public bool SetPsybassOriginal(float db) => SetPsybassFloat(VendorCommands.SetPsybassOriginal, db);
    public float? GetPsybassOriginal() => GetPsybassFloat(VendorCommands.GetPsybassOriginal);

    public bool SetPsybassMask(ushort mask) =>
        ControlTransferOut(VendorCommands.SetPsybassMask, 0,
            new[] { (byte)(mask & 0xFF), (byte)(mask >> 8) });

    public ushort? GetPsybassMask()
    {
        var r = ControlTransferIn(VendorCommands.GetPsybassMask, 0, 2);
        return r != null && r.Length >= 2 ? BitConverter.ToUInt16(r, 0) : (ushort?)null;
    }

    // ── Subharmonic synthesizer (0x10–0x1F, 0x2C–0x2F, 0xA9–0xAE) ──────────────

    private bool SetFloat(byte req, float value) => ControlTransferOut(req, 0, BitConverter.GetBytes(value));

    private float? GetFloat(byte req, ushort value = 0)
    {
        var r = ControlTransferIn(req, value, 4);
        return r != null && r.Length >= 4 ? BitConverter.ToSingle(r, 0) : (float?)null;
    }

    private bool SetByte(byte req, byte value) => ControlTransferOut(req, 0, new[] { value });

    private byte? GetByte(byte req, ushort value = 0)
    {
        var r = ControlTransferIn(req, value, 1);
        return r != null && r.Length >= 1 ? r[0] : (byte?)null;
    }

    public bool SetSubharmEnabled(bool on) => SetByte(VendorCommands.SetSubharm, on ? (byte)1 : (byte)0);
    /// <summary>Null if the firmware STALLs (pre-V29).</summary>
    public bool? GetSubharmEnabled() => GetByte(VendorCommands.GetSubharm) is { } b ? b != 0 : null;
    public bool SetSubharmLow(float db) => SetFloat(VendorCommands.SetSubharmLow, db);
    public float? GetSubharmLow() => GetFloat(VendorCommands.GetSubharmLow);
    public bool SetSubharmHigh(float db) => SetFloat(VendorCommands.SetSubharmHigh, db);
    public float? GetSubharmHigh() => GetFloat(VendorCommands.GetSubharmHigh);
    public bool SetSubharmTop(float db) => SetFloat(VendorCommands.SetSubharmTop, db);
    public float? GetSubharmTop() => GetFloat(VendorCommands.GetSubharmTop);
    public bool SetSubharmBoost(float db) => SetFloat(VendorCommands.SetSubharmBoost, db);
    public float? GetSubharmBoost() => GetFloat(VendorCommands.GetSubharmBoost);
    public bool SetSubharmSelectDepth(float pct) => SetFloat(VendorCommands.SetSubharmDepth, pct);
    public float? GetSubharmSelectDepth() => GetFloat(VendorCommands.GetSubharmDepth);
    public bool SetSubharmSelectHold(float ms) => SetFloat(VendorCommands.SetSubharmHold, ms);
    public float? GetSubharmSelectHold() => GetFloat(VendorCommands.GetSubharmHold);
    public bool SetSubharmCeiling(float dbfs) => SetFloat(VendorCommands.SetSubharmCeiling, dbfs);
    public float? GetSubharmCeiling() => GetFloat(VendorCommands.GetSubharmCeiling);
    public bool SetSubharmSelectMode(int mode) =>
        SetByte(VendorCommands.SetSubharmSelect, (byte)Math.Clamp(mode, 0, SubharmSelectMode.Max));
    public int? GetSubharmSelectMode() =>
        GetByte(VendorCommands.GetSubharmSelect) is { } b ? Math.Min((int)b, SubharmSelectMode.Max) : null;
    public bool SetSubharmLinkPairs(bool linked) => SetByte(VendorCommands.SetSubharmLink, linked ? (byte)1 : (byte)0);
    public bool? GetSubharmLinkPairs() => GetByte(VendorCommands.GetSubharmLink) is { } b ? b != 0 : null;
    /// <summary>Runtime-only monitoring aid: masked outputs carry the sub with
    /// the program removed. Never saved, so clear it when the window closes.</summary>
    public bool SetSubharmSolo(bool solo) => SetByte(VendorCommands.SetSubharmSolo, solo ? (byte)1 : (byte)0);
    public bool? GetSubharmSolo() => GetByte(VendorCommands.GetSubharmSolo) is { } b ? b != 0 : null;

    public bool SetSubharmMask(ushort mask) =>
        ControlTransferOut(VendorCommands.SetSubharmMask, 0, new[] { (byte)(mask & 0xFF), (byte)(mask >> 8) });

    public ushort? GetSubharmMask()
    {
        var r = ControlTransferIn(VendorCommands.GetSubharmMask, 0, 2);
        return r != null && r.Length >= 2 ? BitConverter.ToUInt16(r, 0) : (ushort?)null;
    }

    /// <summary>The worst-case gain of the live configuration (dB, 0 while
    /// disabled), computed by the firmware on each GET, so it may follow a SET
    /// at once. Cutting the preamp by this much is an exact fix.</summary>
    public float? GetSubharmHeadroom() => GetFloat(VendorCommands.GetSubharmHeadroom);

    /// <summary>A decaying peak of the synthesized sub per output, normalized to
    /// 0..1 like the status peaks. A short read keeps the entries that arrived.</summary>
    public float[]? GetSubharmMeter(int outputs)
    {
        if (outputs <= 0) return null;
        var r = ControlTransferIn(VendorCommands.GetSubharmMeter, 0, outputs * 2);
        if (r == null || r.Length < 2) return null;
        var levels = new float[Math.Min(outputs, r.Length / 2)];
        for (int i = 0; i < levels.Length; i++)
            levels[i] = BitConverter.ToUInt16(r, i * 2) / SubharmLimits.MeterFullScale;
        return levels;
    }

    // ── Tube modeller (0x3E/0x3F) ─────────────────────────────────────────────

    /// <summary>Sets one tube parameter (<see cref="TubeParam"/>); every value
    /// is a float32, the bools, mask and enums included.</summary>
    public bool SetTubeParam(ushort index, float value) =>
        ControlTransferOut(VendorCommands.SetTubeParam, index, BitConverter.GetBytes(value));

    /// <summary>Null when the GET STALLs (bad index, or firmware before V31).</summary>
    public float? GetTubeParam(ushort index) => GetFloat(VendorCommands.GetTubeParam, index);

    // ── Output limiter (0x81) ─────────────────────────────────────────────────

    /// <summary>Sets one limiter parameter (<see cref="LimiterParam"/>) on an
    /// output, or on every output with <see cref="LimiterParam.AllOutputs"/>.
    /// A linked output's SET reaches its whole group in the firmware.</summary>
    public bool SetLimiterParam(int output, byte index, float value) =>
        ControlTransferOut(VendorCommands.Limiter, (ushort)((output & 0xFF) << 8 | index), BitConverter.GetBytes(value));

    /// <summary>Null when the GET STALLs (bad output or index, or pre-V32).</summary>
    public float? GetLimiterParam(int output, byte index) =>
        output is >= 0 and < 0xFF ? GetFloat(VendorCommands.Limiter, (ushort)(output << 8 | index)) : null;

    /// <summary>Gain reduction per output in dB (0 = none), from the 0.01 dB
    /// meter block.</summary>
    public float[]? GetLimiterMeter(int outputs)
    {
        if (outputs <= 0) return null;
        var r = ControlTransferIn(VendorCommands.Limiter, LimiterParam.GetMeter, outputs * 2);
        if (r == null || r.Length < 2) return null;
        var gr = new float[Math.Min(outputs, r.Length / 2)];
        for (int i = 0; i < gr.Length; i++) gr[i] = BitConverter.ToUInt16(r, i * 2) / 100f;
        return gr;
    }

    /// <summary>The limiter status block: whether any limiter is engaged, the
    /// lookahead and block sizes in samples, and the output count. Reading it
    /// feature-detects the limiter.</summary>
    public (bool Engaged, int LookaheadSamples, int BlockSamples, int Outputs)? GetLimiterStatus()
    {
        var r = ControlTransferIn(VendorCommands.Limiter, LimiterParam.GetStatus, 4);
        if (r == null || r.Length < 4) return null;
        return (r[0] != 0, r[1], r[2], r[3]);
    }

    // ── Core 1, build info, LG Sound Sync status ──────────────────────────────

    /// <summary>Core 1 mode: 0 idle, 1 PDM, 2 EQ worker.</summary>
    public int? GetCore1Mode() => GetByte(VendorCommands.GetCore1Mode);

    /// <summary>True when enabling <paramref name="output"/> would collide with
    /// what Core 1 is already doing (PDM against the EQ workers).</summary>
    public bool? GetCore1Conflict(int output) =>
        GetByte(VendorCommands.GetCore1Conflict, (ushort)output) is { } b ? b != 0 : null;

    /// <summary>The firmware's git describe and build date, for people only.</summary>
    public (string Describe, string Date)? GetBuildInfo()
    {
        var r = ControlTransferIn(VendorCommands.GetBuildInfo, 0, 64);
        if (r == null || r.Length < 48) return null;
        static string Text(byte[] b, int start, int length)
        {
            int end = start;
            while (end < Math.Min(start + length, b.Length) && b[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(b, start, end - start);
        }
        return (Text(r, 0, 48), r.Length >= 60 ? Text(r, 48, 12) : "");
    }

    /// <summary>LG Sound Sync runtime status: enabled, a TV present, its volume
    /// (0..100, or 0xFF before the first decode) and mute.</summary>
    public (bool Enabled, bool Present, byte Volume, bool Muted)? GetLgSoundSyncStatus()
    {
        var r = ControlTransferIn(VendorCommands.GetLgSoundSyncStatus, 0, 16);
        if (r == null || r.Length < 4) return null;
        return (r[0] != 0, r[1] != 0, r[2], r[3] != 0);
    }

    // ── ADAT input (0x68–0x6E, RP2350) ───────────────────────────────────────

    /// <summary>Enable/disable the ADAT input (0x68). Returns a PIN_CONFIG_* status
    /// byte (0xFF on transfer failure). Enabling without a pin → InvalidPin; RP2040
    /// → InvalidOutput.</summary>
    public byte SetAdatInputEnable(bool enable)
    {
        var r = ControlTransferIn(VendorCommands.SetAdatInputEnable, (ushort)(enable ? 1 : 0), 1);
        return r != null && r.Length >= 1 ? r[0] : (byte)0xFF;
    }

    /// <summary>Configured ADAT-input enable (0x69). Null on transfer failure (used
    /// as the feature probe — pre-V24/RP2040 firmware STALLs).</summary>
    public bool? GetAdatInputEnable()
    {
        var r = ControlTransferIn(VendorCommands.GetAdatInputEnable, 0, 1);
        return r != null && r.Length >= 1 ? r[0] != 0 : (bool?)null;
    }

    /// <summary>Set the ADAT-input RX GPIO (0x6A; 0xFF clears). PIN_CONFIG_* status.</summary>
    public byte SetAdatInputPin(byte pin)
    {
        var r = ControlTransferIn(VendorCommands.SetAdatInputPin, pin, 1);
        return r != null && r.Length >= 1 ? r[0] : (byte)0xFF;
    }

    /// <summary>Configured ADAT-input RX GPIO (0x6B; 0xFF = unset). Null on failure.</summary>
    public byte? GetAdatInputPin()
    {
        var r = ControlTransferIn(VendorCommands.GetAdatInputPin, 0, 1);
        return r != null && r.Length >= 1 ? r[0] : (byte?)null;
    }

    /// <summary>Set the ADAT-input clock mode (0x6C; 0=master, 1=slave; deferred).</summary>
    public byte SetAdatInputClockMode(byte mode)
    {
        var r = ControlTransferIn(VendorCommands.SetAdatInputClockMode, (ushort)(mode == 1 ? 1 : 0), 1);
        return r != null && r.Length >= 1 ? r[0] : (byte)0xFF;
    }

    /// <summary>Live ADAT-input clock mode (0x6D). Null on failure.</summary>
    public byte? GetAdatInputClockMode()
    {
        var r = ControlTransferIn(VendorCommands.GetAdatInputClockMode, 0, 1);
        return r != null && r.Length >= 1 ? r[0] : (byte?)null;
    }

    /// <summary>Read the 20-byte live ADAT-input status (0x6E). Null on failure.</summary>
    public AdatInputStatus? GetAdatInputStatus()
    {
        var r = ControlTransferIn(VendorCommands.GetAdatInputStatus, 0, AdatInputStatus.WireSize);
        return r == null ? null : AdatInputStatus.FromBytes(r);
    }

    /// <summary>
    /// Get the GPIO pin for an I2S input data line / stereo pair (V12+; pair
    /// 0..3 on RP2350, 0 on RP2040).
    /// </summary>
    public byte? GetI2sRxPin(int pair = 0)
    {
        var response = ControlTransferIn(VendorCommands.GetI2sRxPin, (ushort)(pair & 0xFF), 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    /// <summary>
    /// Change the I2S input data GPIO pin for a stereo pair (V12+). Encodes
    /// (pair&lt;&lt;8)|gpio in wValue; firmware returns a 1-byte
    /// <see cref="PinConfigResult"/> status. Returns 0xFF on transfer failure.
    /// </summary>
    public byte SetI2sRxPin(byte pin, int pair = 0)
    {
        ushort wValue = (ushort)(((pair & 0xFF) << 8) | pin);
        var response = ControlTransferIn(VendorCommands.SetI2sRxPin, wValue, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get the active I2S input channel count (2/4/6/8). Null on STALL/failure.
    /// </summary>
    public byte? GetI2sInputChannels()
    {
        var response = ControlTransferIn(VendorCommands.GetI2sInputChannels, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    /// <summary>
    /// Set the active I2S input channel count (2/4/6/8; count/2 stereo pairs).
    /// Count in wValue; firmware returns a <see cref="PinConfigResult"/> status
    /// (INVALID_PIN for an odd/out-of-range count, INVALID_OUTPUT if the platform
    /// has too few pairs). Returns 0xFF on transfer failure.
    /// </summary>
    public byte SetI2sInputChannels(int count)
    {
        var response = ControlTransferIn(VendorCommands.SetI2sInputChannels, (ushort)(count & 0xFF), 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Set the I2S-input master sample rate (V12+). The device is the I2S clock
    /// master, so it drives this rate. Only 44100/48000/96000 are accepted;
    /// other values are silently ignored by the firmware. Stored as a preference
    /// and applied when I2S is (or becomes) the active input source.
    /// </summary>
    public bool SetInputRate(uint hz) =>
        ControlTransferOut(VendorCommands.SetInputRate, 0, BitConverter.GetBytes(hz));

    /// <summary>
    /// Get the input rate state (V12+): returns (currentPipelineHz, selectedI2sHz).
    /// The first is the live pipeline rate for any source; the second is the
    /// stored I2S-input rate preference. Null on transfer failure / older firmware.
    /// </summary>
    public (uint currentHz, uint selectedI2sHz)? GetInputRate()
    {
        var response = ControlTransferIn(VendorCommands.GetInputRate, 0, 8);
        if (response == null || response.Length < 8) return null;
        return (BitConverter.ToUInt32(response, 0), BitConverter.ToUInt32(response, 4));
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
            OutputConfigMode = response[5],
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
    /// Set output-config persistence mode. 0 = independent (IO config is
    /// device-global, applied at boot only, persisted via SaveOutputConfig);
    /// 1 = with preset (IO travels with each preset slot). Firmware clamps
    /// other values to independent. See output_config_independent_load_spec.md.
    /// </summary>
    public bool SetOutputConfigMode(byte mode)
    {
        return ControlTransferOut(VendorCommands.SetOutputConfigMode, 0, new[] { mode });
    }

    /// <summary>
    /// Get the current output-config persistence mode. Returns null on failure.
    /// </summary>
    public byte? GetOutputConfigMode()
    {
        var response = ControlTransferIn(VendorCommands.GetOutputConfigMode, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
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
        return ControlTransferOut(VendorCommands.SetChannelName,
            (ushort)ChannelMap.AppToWire(channel, NumInputChannels), data);
    }

    /// <summary>
    /// Get a channel name from the device. wValue = channel index. Returns null on failure.
    /// </summary>
    public string? GetChannelNameFromDevice(int channel)
    {
        var response = ControlTransferIn(VendorCommands.GetChannelName,
            (ushort)ChannelMap.AppToWire(channel, NumInputChannels), 32);
        if (response == null || response.Length < 1) return null;
        return System.Text.Encoding.UTF8.GetString(response).TrimEnd('\0');
    }

    #endregion

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer.Stop();
        _pollTimer.Dispose();
        _statusPollTimer.Stop();
        _statusPollTimer.Dispose();
        Disconnect();
        _context.Dispose();

        GC.SuppressFinalize(this);
    }
}
