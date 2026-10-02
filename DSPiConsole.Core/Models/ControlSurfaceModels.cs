using System;

namespace DSPiConsole.Core.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Control Surfaces + IR remote (firmware control_surfaces.h; caps v20, config v2,
// IR config v2, group/macro config v1). Physical GPIO controls (buttons, switches,
// pots, encoders, LEDs, PWM LEDs), an IR receiver with learned remote commands, and
// an I2C character/OLED display, each bound to a DSP "noun" (parameter) + "action"
// (verb). Caps v9 adds target groups (one control drives a named set of channels)
// and macros (a button fires a short sequence of delayed steps); caps v10 adds the
// display component, grouped IR commands and four nouns; v11 per-line display
// alignment, v12 a per-LED brightness ceiling, v13 display level bars; v14-v16
// the subharmonic synthesizer nouns, v17/v18 aux outputs (named here, configured
// in a later release), v19 the tube nouns and v20 the output limiter nouns with
// the MS_LOG unit. All wire structs are packed, little-endian.
//
// The whole editor is CAPS-DRIVEN: the firmware serves a capabilities header, a
// per-type action/pin table, and a per-noun descriptor table over 0x86; the host
// builds its pickers from those rather than hardcoding the noun/type tables (they
// are append-only and versioned). Only a client-side display-name table for nouns
// is baked in here (the wire format carries no strings for them).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Physical control component (firmware CsType). Index into the caps
/// type table.</summary>
public enum CsType : byte
{
    None = 0, Button = 1, Switch = 2, Pot = 3, Encoder = 4,
    Led = 5, LedPwm = 6, Ir = 7,
    // caps v10: an I2C character or OLED panel. A container like the IR
    // receiver — SDA, SCL, a model and an address — with what it shows held in
    // a separate config record and page table.
    Display = 8,
    // caps v18: auxiliary outputs. Containers like the IR receiver: the slot owns
    // one output pin, a name, an invert sense, on/off delays and boot values, and
    // a control switches (or dims) it through the Aux / Aux Level nouns, whose
    // target is this slot. Their live on/off flag and level are runtime values.
    AuxOut = 9,
    AuxPwm = 10,
}

/// <summary>Aux output extras (caps v18, <see cref="CsBinding.Extras"/>).</summary>
[Flags]
public enum CsAuxExtras : byte
{
    None = 0,
    /// <summary>Boot with the output on (else off).</summary>
    BootOn = 0x01,
    /// <summary>Saving folds the live on/off flag and level into the boot fields.</summary>
    BootSaved = 0x02,
    /// <summary>AUX_PWM: linear duty instead of the squared perceptual curve (fans, heaters).</summary>
    Linear = 0x04,
}

/// <summary>DSP parameter a control drives or reflects (firmware CsNoun, 0..78).
/// The picker reads which nouns are available (and their ranges/units/targets)
/// live from caps — this enum is for the few places that special-case a noun.</summary>
public enum CsNoun : byte
{
    UserVolume = 0, MasterVolume = 1, UserMute = 2, Loudness = 3, Crossfeed = 4,
    Leveller = 5, Preset = 6, InputSource = 7, Clip = 8, EqBypass = 9,
    LgSync = 10, CrossfeedPreset = 11, CrossfeedItd = 12, LevellerAmount = 13,
    LevellerSpeed = 14, LevellerLookahead = 15, Preamp = 16, OutputGain = 17,
    OutputMute = 18, OutputEnable = 19, FilterFreq = 20, FilterGain = 21,
    FilterQ = 22, FilterType = 23, FilterBypass = 24, Siggen = 25,
    DacMuteTest = 26, ClipCh = 27, Level = 28, SpdifLock = 29, SampleRate = 30,
    UsbStreaming = 31, AdatActive = 32, LgPresent = 33, LgMuted = 34,
    // caps v4 additions. The six upmixer nouns are RP2350-only (action mask 0
    // on RP2040, the same convention as AdatActive).
    Upmix = 35, UpmixCenterMode = 36, UpmixSurroundMode = 37, UpmixStrength = 38,
    UpmixWidth = 39, UpmixPresence = 40, Psybass = 41, PsybassCutoff = 42,
    PsybassHarmonics = 43, PsybassDrive = 44, PsybassCharacter = 45,
    PsybassOriginal = 46, OutputDelay = 47, PresetReload = 48,
    // caps v7 additions: the two remaining loudness parameters.
    LoudnessSpl = 49, LoudnessIntensity = 50,
    // caps v8: loudest channel of the active input (read-only, untargeted) —
    // signal-presence sensing for amplifier trigger outputs.
    InputLevelMax = 51,
    // caps v9: an enum of macro slots. SET fires macro `value`; IND_EQUALS lights
    // while it runs. The live read is the running index, or 255 when idle.
    Macro = 52,
    // caps v10. CpuLoad is read-only percent; the three display nouns are no-ops
    // (and read idle) without a live display component, so they are valid to
    // configure in advance — the same rule as IR commands before a receiver.
    CpuLoad = 53,
    DisplayPage = 54,   // enum: which page is on screen (steps skip empty slots)
    DisplayEdit = 55,   // bool: editing of the shown page is armed
    // Virtual: resolves whatever the panel is showing at event time and steps
    // it with that item's own unit, step law and range. Value/step/range must
    // stay zero, it takes no target, and a macro step may not carry it.
    PageValue = 56,
    // caps v14-v16: the subharmonic synthesizer (band levels to +12 dB at v16).
    Subharm = 57, SubharmLow = 58, SubharmHigh = 59, SubharmBoost = 60,
    SubharmTop = 61, SubharmSelect = 62, SubharmDepth = 63, SubharmHold = 64,
    SubharmCeiling = 65, SubharmLink = 66, SubharmSolo = 67,
    // caps v17/v18: aux outputs; target = the binding slot holding one.
    Aux = 68, AuxLevel = 69,
    // caps v19: the tube modeller.
    Tube = 70, TubeDrive = 71, TubeType = 72, TubeMix = 73,
    // caps v20: the output limiter, each targeting an output. Release steps
    // logarithmically in whole ms (CsUnit.MsLog); GR is read-only.
    Limiter = 74, LimiterThreshold = 75, LimiterRelease = 76, LimiterLink = 77,
    LimiterGr = 78
}

/// <summary>Verb a control performs (firmware CsAction). Value = bit position;
/// caps action masks use <c>1 &lt;&lt; action</c>.</summary>
public enum CsAction : byte
{
    Adjust = 0,     // pot: absolute position → value range
    Step = 1,       // encoder: ± step per detent
    Inc = 2,        // button: + step per press
    Dec = 3,        // button: - step per press
    Toggle = 4,     // button: invert a bool
    Set = 5,        // button: set noun to `value`
    Follow = 6,     // switch: bool tracks switch position
    Trigger = 7,    // button: fire the noun's command
    IndEquals = 8,  // LED: lit while value == `value`
    Momentary = 9,  // button: set to `value` while held, restore on release
    IndAbove = 10,  // LED: lit while value >= `value`
    IndLevel = 11   // PWM LED: brightness follows value across range
}

/// <summary>Button gesture (firmware CsEvent); 0 for non-button types.</summary>
public enum CsEvent : byte { Press = 0, Long = 1, Double = 2 }

/// <summary>CsBinding.flags / IrCommand.flags / CsMacroStep.flags bitfield.</summary>
[Flags]
public enum CsFlags : byte
{
    None = 0,
    Invert = 0x01,   // input active-high w/ pull-down; LED active-low
    Reverse = 0x02,  // pot / encoder: invert direction
    Wrap = 0x04,     // enum STEP/INC/DEC wraps around
    Accel = 0x08,    // encoder only: fast rotation multiplies step
    Repeat = 0x10,   // button INC/DEC on press: auto-repeat while held
    // caps v9. Group re-reads `target` as a group index; the two modifiers
    // require it and the firmware rejects them without it.
    Group = 0x20,
    LinkAbs = 0x40,  // grouped pot ADJUST: drive members identical, not offset-preserving
    GroupAll = 0x80  // grouped IND_EQUALS/IND_ABOVE: lit when every member matches, not any
}

/// <summary>Noun value shape (firmware CS_KIND_*).</summary>
public enum CsKind : byte { Continuous = 0, Bool = 1, Enum = 2 }

/// <summary>Noun unit; fixes the wire encoding of value/range/step and the
/// stepping law (firmware CS_UNIT_*). <c>Ms</c> is a caps-v4 addition (8.8
/// milliseconds, linear, default step 0.1 ms) used by OutputDelay.</summary>
public enum CsUnit : byte
{
    None = 0, Db = 1, Hz = 2, Q = 3, Percent = 4, Ms = 5,
    /// <summary>caps v20: plain integer ms, log stepping (step in 8.8 octaves),
    /// for spans past 8.8's 127 ms.</summary>
    MsLog = 6
}

/// <summary>What a noun's <c>target</c> addresses (firmware CS_TARGET_*).</summary>
public enum CsTarget : byte
{
    None = 0, InputCh = 1, OutputCh = 2, DspCh = 3, DspBand = 4,
    /// <summary>caps v17: a binding slot (0..15) holding an aux output; index 0.</summary>
    Aux = 5
}

/// <summary>Pin capability required by a type (firmware CS_PINCLASS_*).</summary>
public enum CsPinClass : byte { Any = 0, Adc = 1 }

/// <summary>IR protocol of a learned code (firmware CS_IR_PROTO_*).</summary>
public enum CsIrProto : byte { None = 0, Nec = 1, Rc5 = 2, Rc6 = 3, Hash = 4 }

/// <summary>REQ_CS_IR_LEARN wValue action.</summary>
public static class CsIrLearnAction
{
    public const ushort Cancel = 0;
    public const ushort Arm = 1;
    public const ushort Read = 2;
}

/// <summary>IR learn engine state (firmware CS_IR_LEARN_*).</summary>
public enum CsIrLearnState : byte { Idle = 0, Armed = 1, Done = 2, Timeout = 3 }

/// <summary>Control-surface deferred-apply status codes. 0x00..0x05 reuse the
/// shared PIN_CONFIG_* namespace (see Usb PinConfigResult); 0x10..0x1E are the
/// CS extension.</summary>
public static class CsStatus
{
    public const byte Success = 0x00;      // PIN_CONFIG_SUCCESS
    public const byte InvalidPin = 0x01;   // (CS: also "encoder's two pins equal")
    public const byte PinInUse = 0x02;

    public const byte InvalidSlot = 0x10;
    public const byte InvalidType = 0x11;
    public const byte InvalidNoun = 0x12;
    public const byte InvalidAction = 0x13;
    public const byte InvalidValue = 0x14;
    public const byte PinNotAdc = 0x15;
    public const byte Pending = 0x16;      // accepted, apply not yet run — poll again
    public const byte InvalidTarget = 0x17;
    public const byte InvalidEvent = 0x18;
    public const byte PwmConflict = 0x19;
    public const byte EventInUse = 0x1A;
    public const byte Busy = 0x1B;
    public const byte FlashError = 0x1C;
    public const byte IrInUse = 0x1D;
    public const byte NoIr = 0x1E;
    public const byte InvalidGroup = 0x1F;  // v9: group empty, out of range, or kind-mismatched
    public const byte InvalidMacro = 0x20;  // v9: bad macro index or step count
    public const byte InvalidStep = 0x21;   // v9: macro step record invalid
    public const byte DisplayInUse = 0x22;  // v10: another slot already holds the display
    public const byte PinNotI2c = 0x23;     // v10: SDA/SCL are not a valid same-instance pair
    public const byte I2cInUse = 0x24;      // v10: that I2C instance belongs to the control interface
    public const byte InvalidPage = 0x25;   // v10: display config or page record invalid
    public const byte InvalidAux = 0x26;    // v17: target slot is not an aux output (or the wrong kind)

    /// <summary>Human-readable message for a CS status code.</summary>
    public static string Message(byte code) => code switch
    {
        Success => "OK",
        InvalidPin => "Invalid pin (or encoder pins are equal)",
        PinInUse => "Pin already in use by another peripheral",
        InvalidSlot => "Invalid slot",
        InvalidType => "Invalid component type",
        InvalidNoun => "Parameter not available on this device",
        InvalidAction => "Action not valid for this parameter",
        InvalidValue => "Invalid value",
        PinNotAdc => "This control needs an ADC pin (GPIO 26–28)",
        Pending => "Applying…",
        InvalidTarget => "Invalid channel/band target",
        InvalidEvent => "Invalid button event",
        PwmConflict => "PWM slice already driven by another LED",
        EventInUse => "That button gesture is already bound on this pin",
        Busy => "Device busy — try again",
        FlashError => "Flash write failed",
        IrInUse => "An IR receiver is already configured",
        NoIr => "No IR receiver is active",
        InvalidGroup => "Group is empty or doesn't match this parameter's channels",
        InvalidMacro => "Invalid macro",
        InvalidStep => "Invalid macro step",
        DisplayInUse => "A display is already configured",
        PinNotI2c => "Those pins aren't an I2C pair — SDA must be an even GPIO",
        I2cInUse => "That I2C bus belongs to the external control interface",
        InvalidPage => "Invalid display setting or page",
        InvalidAux => "That slot does not hold an aux output of this kind",
        _ => $"Error 0x{code:X2}"
    };
}

/// <summary>Shared constants for the control-surface feature.</summary>
public static class CsLimits
{
    public const int MaxBindings = 16;
    public const int MaxIrCommands = 16;   // caps v6 doubled the table from 8
    public const int MaxGroups = 8;        // caps v9
    public const int MaxMacros = 8;        // caps v9
    public const int MaxMacroSteps = 8;    // caps v9
    public const int MaxDisplayPages = 16; // caps v10
    public const int NameLen = 32;          // per-slot name buffer, NUL-terminated
    public const byte GpioUnused = 0xFF;
    public const ushort CapsAll = 0xFFFF;   // 0x86 wValue selecting the caps header
    public const byte LastSlotSave = 0xFF;  // CsStatusPacket.LastSlot for save/revert
    public const byte LastSlotIrFlag = 0x80;// high bit of LastSlot marks an IR sub-slot
    public const byte LastSlotGroupFlag = 0x40; // v9: LastSlot tag for a group SET
    public const byte LastSlotMacroFlag = 0x60; // v9: LastSlot tag for a macro header/step SET
    /// <summary>v10: LastSlot tag for a display SET — bare for the config,
    /// 0x50 | page for one page. The config and page 0 therefore share a byte,
    /// which is only unambiguous while one display write is in flight.</summary>
    public const byte LastSlotDisplayFlag = 0x50;
    public const byte MacroIdle = 0xFF;     // CsExtStatusPacket.MacroRunning when nothing runs
    public const ushort MacroFireCancel = 0xFFFF; // 0x25 wValue cancelling the running macro
    public const byte NdfDeferred = 0x01;   // CsNounDesc.dflags
    /// <summary>Highest per-LED brightness ceiling (caps v12). The field is a
    /// percentage of full duty; 0 means unset, which is also full.</summary>
    public const byte LedBrightMax = 100;
    /// <summary>The dwell floor the firmware enforces in either cycle mode
    /// (0.1 s units), rejected below it rather than clamped.</summary>
    public const ushort DisplayMinDwell = 10;

    /// <summary>Macro step <c>pre_delay</c> is in 10 ms units; the indicator
    /// <c>on_delay</c>/<c>off_delay</c> pair is in 0.1 s units.</summary>
    public const double MacroDelayUnitSeconds = 0.01;
    public const double IndicatorDelayUnitSeconds = 0.1;

    /// <summary>The only GPIOs a pot may occupy (ADC0..2). GPIO 29 (VSYS) excluded.</summary>
    public static readonly byte[] AdcPins = { 26, 27, 28 };
}

// ─────────────────────────────────────────────────────────────────────────────
// Fixed-point wire encoding helpers (firmware unit encoding; mirror the macOS
// csEncode*/csDecode* helpers verbatim — the 8.8 encoding and "step in octaves
// for Hz/Q" law are load-bearing).
// ─────────────────────────────────────────────────────────────────────────────
public static class CsWire
{
    /// <summary>Units whose value/range are 8.8 signed fixed point (1.0 = 256).</summary>
    public static bool UnitIsFixedPoint(CsUnit u) =>
        u is CsUnit.Db or CsUnit.Q or CsUnit.Percent or CsUnit.Ms;

    /// <summary>Units whose step is in octaves (log stepping) rather than in
    /// the unit itself.</summary>
    public static bool UnitStepsInOctaves(CsUnit u) => u is CsUnit.Hz or CsUnit.Q or CsUnit.MsLog;

    /// <summary>Encode a value/range operand for a noun's unit. DB/Q/PERCENT/MS
    /// use 8.8; NONE, HZ and MS_LOG are plain integers.</summary>
    public static short EncodeValue(double v, CsUnit u) =>
        (short)Math.Round(UnitIsFixedPoint(u) ? v * 256.0 : v);

    public static double DecodeValue(short raw, CsUnit u) =>
        UnitIsFixedPoint(u) ? raw / 256.0 : raw;

    /// <summary>Encode a step operand. Everything except NONE is scaled ×256 —
    /// for HZ/Q the value is in octaves (256 = 1 octave); for DB/PERCENT/MS it is
    /// a linear dB/%/ms step. NONE (bool/enum) is a plain position count.</summary>
    public static short EncodeStep(double v, CsUnit u) =>
        (short)Math.Round(u == CsUnit.None ? v : v * 256.0);

    public static double DecodeStep(short raw, CsUnit u) =>
        u == CsUnit.None ? raw : raw / 256.0;

    public static string UnitSymbol(CsUnit u) => u switch
    {
        CsUnit.Db => "dB",
        CsUnit.Hz => "Hz",
        CsUnit.Q => "Q",
        CsUnit.Percent => "%",
        CsUnit.Ms or CsUnit.MsLog => "ms",
        _ => ""
    };

    /// <summary>The step the firmware applies when a binding leaves <c>step</c>
    /// at 0, in the unit's own terms (octaves for the log units). Display only.</summary>
    public static double DefaultStep(CsUnit u) => u switch
    {
        CsUnit.Hz or CsUnit.Q or CsUnit.MsLog => 1.0 / 12.0,  // 1/12 octave
        CsUnit.Ms => 0.1,                     // caps v4: ms detents are finer
        CsUnit.None => 1,                     // one enum position
        _ => 1                                // 1 dB / 1 %
    };

    /// <summary>Write a NUL-terminated 32-byte name field (group / macro records).
    /// Truncated to 31 UTF-8 bytes so the terminator always fits.</summary>
    public static void WriteName(string name, byte[] dest, int offset)
    {
        if (string.IsNullOrEmpty(name)) return;
        var raw = System.Text.Encoding.UTF8.GetBytes(name);
        int len = Math.Min(raw.Length, CsLimits.NameLen - 1);
        Array.Copy(raw, 0, dest, offset, len);
    }

    /// <summary>Read a NUL-terminated 32-byte name field.</summary>
    public static string ReadName(byte[] src, int offset)
    {
        int end = offset;
        int limit = Math.Min(src.Length, offset + CsLimits.NameLen);
        while (end < limit && src[end] != 0) end++;
        return System.Text.Encoding.UTF8.GetString(src, offset, end - offset);
    }

    public static string IrProtocolName(CsIrProto p) => p switch
    {
        CsIrProto.Nec => "NEC",
        CsIrProto.Rc5 => "RC5",
        CsIrProto.Rc6 => "RC6",
        CsIrProto.Hash => "Generic",
        _ => "None"
    };
}

/// <summary>Client-side display metadata for the 79 nouns (the wire format
/// carries no strings). Kept minimal — the picker still reads availability,
/// ranges, units and targets from caps.</summary>
public static class CsNounInfo
{
    private static readonly string[] Names =
    {
        "User Volume", "Master Volume", "User Mute", "Loudness", "Crossfeed",
        "Volume Leveller", "Preset", "Input Source", "Clip", "EQ Bypass",
        "LG Sound Sync", "Crossfeed Preset", "Crossfeed ITD", "Leveller Amount",
        "Leveller Speed", "Leveller Lookahead", "Preamp", "Output Gain",
        "Output Mute", "Output Enable", "Filter Frequency", "Filter Gain",
        "Filter Q", "Filter Type", "Filter Bypass", "Signal Generator",
        "DAC Mute Test", "Clip (Channel)", "Level", "S/PDIF Lock", "Sample Rate",
        "USB Streaming", "ADAT Active", "LG Present", "LG Muted",
        // caps v4
        "Stereo Upmixer", "Upmix Centre Mode", "Upmix Surround Mode",
        "Upmix Strength", "Upmix Centre Width", "Upmix Centre Presence",
        "Psychoacoustic Bass", "Bass Cutoff Frequency", "Bass Harmonics",
        "Bass Drive", "Bass Character", "Original Bass Level",
        "Output Delay", "Reload Preset",
        // caps v7
        "Loudness Reference SPL", "Loudness Intensity",
        // caps v8 / v9
        "Input Level (Loudest)", "Macro",
        // caps v10. The three display nouns name what the control does rather
        // than leaning on a "Display" prefix — a page picker that reads "Show
        // Page" and an arming button that reads "Allow Editing" each say it
        // once. "Browse/Adjust" is both halves of PAGE_VALUE: gated behind
        // arming it moves through pages, armed it moves the shown value.
        "CPU Load", "Show Page", "Allow Editing", "Browse/Adjust",
        // caps v14-v16 (the macOS Console's names)
        "Subharmonic Synthesizer", "Subharm 24-36 Hz Level", "Subharm 36-56 Hz Level",
        "Subharm LF Boost", "Subharm 56-80 Hz Level", "Subharm Selectivity",
        "Subharm Selectivity Depth", "Subharm Selectivity Hold", "Subharm Sub Ceiling",
        "Subharm Pair Link", "Subharm Solo",
        // caps v17/v18: named after what they do; the target is the output.
        "Aux Switch", "Aux Level",
        // caps v19
        "Tube Modeller", "Tube Drive", "Tube Type", "Tube Mix",
        // caps v20
        "Output Limiter", "Limiter Threshold", "Limiter Release", "Limiter Link Group",
        "Limiter Gain Reduction"
    };

    private static readonly string[] SubharmSelectNames = { "All", "Percussive", "Sustained" };

    // Value labels for the enum-kind nouns. The picker only uses as many entries
    // as the noun's caps enum_count reports, and falls back to the bare index for
    // anything past the end, so a firmware that grows an enum still works.
    private static readonly string[] PresetNames =
        { "Preset 1", "Preset 2", "Preset 3", "Preset 4", "Preset 5",
          "Preset 6", "Preset 7", "Preset 8", "Preset 9", "Preset 10" };

    private static readonly string[] InputSourceNames =
        { "USB", "S/PDIF", "I2S", "ADAT", "S/PDIF 2", "S/PDIF 3", "S/PDIF 4" };

    private static readonly string[] CrossfeedPresetNames =
        { "Default", "Chu Moy", "Jan Meier", "Custom" };

    private static readonly string[] LevellerSpeedNames = { "Slow", "Medium", "Fast" };

    // Indexed by the wire FilterType value. Firmware only cycles 0-10 from the
    // front panel, but a band can hold any PEQ type the host wrote, so the table
    // covers the whole PEQ block.
    private static readonly string[] FilterTypeNames =
        { "Flat", "Peaking", "Low Shelf", "High Shelf", "High Cut 12dB", "Low Cut 12dB",
          "Notch", "All Pass", "All Pass (1st)", "Low Shelf (1st)",
          "High Shelf (1st)", "Linkwitz Transform", "High Cut 6dB", "Low Cut 6dB" };

    private static readonly string[] SampleRateNames = { "44.1 kHz", "48 kHz", "96 kHz" };

    // Both wire 0/1/2; the app shows the upmixer's product mode names. The centre
    // enum puts Off last (appended at caps v5) while the surround enum puts it first.
    private static readonly string[] UpmixCenterModeNames = { "Sinner", "Logician", "Off" };
    private static readonly string[] UpmixSurroundModeNames = { "Off", "Sinner", "Logician" };

    /// <summary>Label for one value of an enum-kind noun, e.g. "S/PDIF" for
    /// InputSource 1. Falls back to the plain index for unknown nouns/values.</summary>
    public static string EnumLabel(int noun, int value)
    {
        var table = (CsNoun)noun switch
        {
            CsNoun.Preset => PresetNames,
            CsNoun.InputSource => InputSourceNames,
            CsNoun.CrossfeedPreset => CrossfeedPresetNames,
            CsNoun.LevellerSpeed => LevellerSpeedNames,
            CsNoun.FilterType => FilterTypeNames,
            CsNoun.SampleRate => SampleRateNames,
            CsNoun.UpmixCenterMode => UpmixCenterModeNames,
            CsNoun.UpmixSurroundMode => UpmixSurroundModeNames,
            CsNoun.SubharmSelect => SubharmSelectNames,
            CsNoun.TubeType => TubeTables.Types.Select((_, i) => TubeTables.TypeName(i)).ToArray(),
            CsNoun.LimiterLink => Enumerable.Range(0, LimiterLimits.LinkGroupMax + 1).Select(LimiterLimits.LinkGroupName).ToArray(),
            _ => null
        };
        return table != null && value >= 0 && value < table.Length
            ? table[value]
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Name(int noun) =>
        noun >= 0 && noun < Names.Length ? Names[noun] : $"Noun {noun}";

    public static string Name(CsNoun noun) => Name((int)noun);

    /// <summary>The name a control's editor shows for a noun, which for a few
    /// depends on whether the control acts or indicates: a button clears
    /// clipping where an LED shows it. After the macOS Console.</summary>
    public static string Name(int noun, CsType type)
    {
        bool indicator = type is CsType.Led or CsType.LedPwm;
        return (CsNoun)noun switch
        {
            CsNoun.Clip when !indicator => "Clear Clipping",
            CsNoun.DacMuteTest when !indicator => "Test DAC Mute",
            CsNoun.Macro when indicator => "Running Macro",
            _ => Name(noun),
        };
    }

    /// <summary>One family of the function menu: its title, the noun that
    /// switches the feature on and off (labelled Enable/Disable inside it), and
    /// its nouns in the order the menu lists them.</summary>
    public sealed record Category(string Title, CsNoun? Enable, CsNoun[] Nouns);

    /// <summary>The function menu's families, in the macOS Console's order (with
    /// the output limiter, which it files under Other, given its own). A noun
    /// in none of them is listed under Other.</summary>
    public static readonly Category[] Categories =
    {
        new("Volume & Mute", null, new[] { CsNoun.UserVolume, CsNoun.MasterVolume, CsNoun.UserMute }),
        new("Loudness", CsNoun.Loudness, new[] { CsNoun.Loudness, CsNoun.LoudnessSpl, CsNoun.LoudnessIntensity }),
        new("Crossfeed", CsNoun.Crossfeed, new[] { CsNoun.Crossfeed, CsNoun.CrossfeedPreset, CsNoun.CrossfeedItd }),
        new("Volume Leveller", CsNoun.Leveller, new[] { CsNoun.Leveller, CsNoun.LevellerAmount, CsNoun.LevellerSpeed, CsNoun.LevellerLookahead }),
        new("Psychoacoustic Bass", CsNoun.Psybass, new[] { CsNoun.Psybass, CsNoun.PsybassCutoff, CsNoun.PsybassHarmonics,
            CsNoun.PsybassDrive, CsNoun.PsybassCharacter, CsNoun.PsybassOriginal }),
        new("Subharmonic Synth", CsNoun.Subharm, new[] { CsNoun.Subharm, CsNoun.SubharmLow, CsNoun.SubharmHigh, CsNoun.SubharmTop,
            CsNoun.SubharmBoost, CsNoun.SubharmSelect, CsNoun.SubharmDepth, CsNoun.SubharmHold, CsNoun.SubharmCeiling,
            CsNoun.SubharmLink, CsNoun.SubharmSolo }),
        new("Tube Modeller", CsNoun.Tube, new[] { CsNoun.Tube, CsNoun.TubeType, CsNoun.TubeDrive, CsNoun.TubeMix }),
        new("Output Limiter", CsNoun.Limiter, new[] { CsNoun.Limiter, CsNoun.LimiterThreshold, CsNoun.LimiterRelease,
            CsNoun.LimiterLink, CsNoun.LimiterGr }),
        new("Upmixer", CsNoun.Upmix, new[] { CsNoun.Upmix, CsNoun.UpmixCenterMode, CsNoun.UpmixSurroundMode,
            CsNoun.UpmixStrength, CsNoun.UpmixWidth, CsNoun.UpmixPresence }),
        new("Input & Presets", null, new[] { CsNoun.Preset, CsNoun.PresetReload, CsNoun.InputSource, CsNoun.LgSync }),
        new("Channels", null, new[] { CsNoun.Preamp, CsNoun.OutputGain, CsNoun.OutputMute, CsNoun.OutputEnable, CsNoun.OutputDelay }),
        new("Filters", null, new[] { CsNoun.EqBypass, CsNoun.FilterFreq, CsNoun.FilterGain, CsNoun.FilterQ,
            CsNoun.FilterType, CsNoun.FilterBypass }),
        new("Tools", null, new[] { CsNoun.Macro, CsNoun.Siggen, CsNoun.DacMuteTest, CsNoun.Clip }),
        new("Display", null, new[] { CsNoun.DisplayPage, CsNoun.PageValue, CsNoun.DisplayEdit }),
        new("Auxiliary Outputs", CsNoun.Aux, new[] { CsNoun.Aux, CsNoun.AuxLevel }),
        new("Status", null, new[] { CsNoun.CpuLoad, CsNoun.ClipCh, CsNoun.Level, CsNoun.InputLevelMax, CsNoun.SpdifLock,
            CsNoun.SampleRate, CsNoun.UsbStreaming, CsNoun.AdatActive, CsNoun.LgPresent, CsNoun.LgMuted }),
    };

    /// <summary>A noun's label inside its family's submenu, where the family
    /// title already says what it belongs to.</summary>
    public static string MenuLabel(int noun, CsType type, Category? category)
    {
        if (category?.Enable is { } enable && (int)enable == noun)
            return type is CsType.Led or CsType.LedPwm ? "Enabled" : "Enable/Disable";
        return (CsNoun)noun switch
        {
            CsNoun.LoudnessSpl => "Reference SPL",
            CsNoun.LoudnessIntensity => "Intensity",
            CsNoun.CrossfeedPreset => "Preset",
            CsNoun.CrossfeedItd => "ITD",
            CsNoun.LevellerAmount => "Amount",
            CsNoun.LevellerSpeed => "Speed",
            CsNoun.LevellerLookahead => "Lookahead",
            CsNoun.PsybassCutoff => "Cutoff Frequency",
            CsNoun.PsybassHarmonics => "Harmonics",
            CsNoun.PsybassDrive => "Drive",
            CsNoun.PsybassCharacter => "Character",
            CsNoun.SubharmLow => "24-36 Hz Level",
            CsNoun.SubharmHigh => "36-56 Hz Level",
            CsNoun.SubharmTop => "56-80 Hz Level",
            CsNoun.SubharmBoost => "LF Boost",
            CsNoun.SubharmSelect => "Selectivity",
            CsNoun.SubharmDepth => "Selectivity Depth",
            CsNoun.SubharmHold => "Selectivity Hold",
            CsNoun.SubharmCeiling => "Sub Ceiling",
            CsNoun.SubharmLink => "Pair Link",
            CsNoun.SubharmSolo => "Solo",
            CsNoun.TubeType => "Type",
            CsNoun.TubeDrive => "Drive",
            CsNoun.TubeMix => "Mix",
            CsNoun.LimiterThreshold => "Threshold",
            CsNoun.LimiterRelease => "Release",
            CsNoun.LimiterLink => "Link Group",
            CsNoun.LimiterGr => "Gain Reduction",
            CsNoun.UpmixCenterMode => "Centre Mode",
            CsNoun.UpmixSurroundMode => "Surround Mode",
            CsNoun.UpmixStrength => "Strength",
            CsNoun.UpmixWidth => "Centre Width",
            CsNoun.UpmixPresence => "Centre Presence",
            CsNoun.FilterFreq => "Frequency",
            CsNoun.FilterGain => "Gain",
            CsNoun.FilterQ => "Q",
            CsNoun.FilterType => "Type",
            CsNoun.FilterBypass => "Bypass",
            CsNoun.AuxLevel => "Level",
            _ => Name(noun, type),
        };
    }

    /// <summary>The family a noun is listed under, or null for Other.</summary>
    public static Category? CategoryOf(int noun) =>
        Categories.FirstOrDefault(c => c.Nouns.Any(n => (int)n == noun));
}

// ─────────────────────────────────────────────────────────────────────────────
// Wire structs
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The 24-byte CsBinding wire struct (REQ_SET/GET_CS_BINDING). A
/// default (all-zero) instance is the "cleared slot" blob (type = None). A
/// configured single-pin binding must set <see cref="Gpio1"/> to 0xFF.</summary>
public sealed class CsBinding
{
    public const int WireSize = 24;

    public CsType Type;         // @0
    public byte Noun;           // @1  CsNoun
    public byte Action;         // @2  CsAction
    public CsFlags Flags;       // @3
    public byte Gpio0;          // @4  primary GPIO
    public byte Gpio1 = CsLimits.GpioUnused; // @5  second GPIO (encoder); 0xFF = single-pin
    public byte Event;          // @6  CsEvent (buttons); 0 otherwise
    public byte Target;         // @7  channel address
    public byte Index;          // @8  filter band (DSP_BAND nouns)
    /// <summary>@9 caps v12: brightness ceiling as a percentage of full duty
    /// (1..100; 0 = unset = full), carved from the byte that was reserved. It
    /// scales the computed duty rather than clipping it, so an IND_LEVEL meter
    /// keeps its whole sweep and only its top end moves. LedPwm only — the
    /// firmware rejects a non-zero value on any other type, as a pre-v12 device
    /// does for any value at all.</summary>
    public byte BaseBright;
    public short Value;         // @10 SET/MOMENTARY / IND comparand (unit-encoded)
    public short Step;          // @12 STEP/INC/DEC size; 0 = unit default
    public short RangeMin;      // @14 pot/IND_LEVEL span low; both 0 = full range
    public short RangeMax;      // @16 pot/IND_LEVEL span high
    public ushort OnDelay;      // @18 caps v8: TON filter, 0.1 s units (LED IND_EQUALS/IND_ABOVE only)
    public ushort OffDelay;     // @20 caps v8: TOF filter, same units and rules
    /// <summary>@22 caps v18: type extras, <see cref="CsAuxExtras"/> on an aux
    /// slot; every other type writes 0.</summary>
    public byte Extras;
    // @23 reserved2

    /// <summary>Either aux output type (caps v18).</summary>
    public bool IsAux => Type is CsType.AuxOut or CsType.AuxPwm;

    public bool IsConfigured => Type != CsType.None;

    /// <summary>True when <see cref="Target"/> is a group index rather than a
    /// channel (caps v9).</summary>
    public bool IsGrouped => (Flags & CsFlags.Group) != 0;

    /// <summary>A fresh cleared-slot binding (gpio1 = 0 so the blob is all-zero).</summary>
    public static CsBinding Cleared() => new() { Gpio1 = 0 };

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = (byte)Type;
        b[1] = Noun;
        b[2] = Action;
        b[3] = (byte)Flags;
        b[4] = Gpio0;
        b[5] = Gpio1;
        b[6] = Event;
        b[7] = Target;
        b[8] = Index;
        b[9] = BaseBright;
        BitConverter.GetBytes(Value).CopyTo(b, 10);
        BitConverter.GetBytes(Step).CopyTo(b, 12);
        BitConverter.GetBytes(RangeMin).CopyTo(b, 14);
        BitConverter.GetBytes(RangeMax).CopyTo(b, 16);
        BitConverter.GetBytes(OnDelay).CopyTo(b, 18);
        BitConverter.GetBytes(OffDelay).CopyTo(b, 20);
        b[22] = Extras;
        return b;
    }

    public static CsBinding? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsBinding
        {
            Type = (CsType)d[0],
            Noun = d[1],
            Action = d[2],
            Flags = (CsFlags)d[3],
            Gpio0 = d[4],
            Gpio1 = d[5],
            Event = d[6],
            Target = d[7],
            Index = d[8],
            BaseBright = d[9],
            Value = BitConverter.ToInt16(d, 10),
            Step = BitConverter.ToInt16(d, 12),
            RangeMin = BitConverter.ToInt16(d, 14),
            RangeMax = BitConverter.ToInt16(d, 16),
            OnDelay = BitConverter.ToUInt16(d, 18),
            OffDelay = BitConverter.ToUInt16(d, 20),
            Extras = d[22],
        };
    }

    public CsBinding Clone() => (CsBinding)MemberwiseClone();

    /// <summary>Wire-equality: compares the serialized bytes (ignores any
    /// reserved padding differences, which are always zero).</summary>
    public bool WireEquals(CsBinding other)
    {
        if (other == null) return false;
        var a = ToBytes();
        var b = other.ToBytes();
        for (int i = 0; i < WireSize; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>The 16-byte IrCommand wire struct (REQ_SET/GET_CS_IR_CMD). A
/// button-shaped command fired by a learned code instead of a GPIO edge. There
/// is no gpio/event field — the receiver's pin lives on the container CsBinding
/// (CS_TYPE_IR). An occupied sub-slot with <see cref="Code"/> == 0 is rejected.</summary>
public sealed class IrCommand
{
    public const int WireSize = 16;

    public byte Noun;           // @0  CsNoun
    public byte Action;         // @1  CsAction (button subset: Inc/Dec/Toggle/Set/Trigger/Momentary)
    public CsFlags Flags;       // @2  Wrap | Repeat, plus Group since caps v10
    public byte Target;         // @3  channel address, or a group index under Group
    public byte Index;          // @4  filter band
    public CsIrProto Proto;     // @5  0 = empty sub-slot
    public short Value;         // @6  SET/MOMENTARY target (unit-encoded)
    public short Step;          // @8  INC/DEC size; 0 = unit default
    // @10,11 reserved
    public uint Code;           // @12 learned code, LE; 0 = never learned

    public bool IsConfigured => Proto != CsIrProto.None;

    /// <summary>True when <see cref="Target"/> is a group index rather than a
    /// channel (caps v10). A pre-v10 firmware rejects the record at validation,
    /// so such a sub-slot simply loads inactive there.</summary>
    public bool IsGrouped => (Flags & CsFlags.Group) != 0;

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = Noun;
        b[1] = Action;
        b[2] = (byte)Flags;
        b[3] = Target;
        b[4] = Index;
        b[5] = (byte)Proto;
        BitConverter.GetBytes(Value).CopyTo(b, 6);
        BitConverter.GetBytes(Step).CopyTo(b, 8);
        // b[10],b[11] reserved
        BitConverter.GetBytes(Code).CopyTo(b, 12);
        return b;
    }

    public static IrCommand? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new IrCommand
        {
            Noun = d[0],
            Action = d[1],
            Flags = (CsFlags)d[2],
            Target = d[3],
            Index = d[4],
            Proto = (CsIrProto)d[5],
            Value = BitConverter.ToInt16(d, 6),
            Step = BitConverter.ToInt16(d, 8),
            Code = BitConverter.ToUInt32(d, 12),
        };
    }

    public IrCommand Clone() => (IrCommand)MemberwiseClone();

    public bool WireEquals(IrCommand other)
    {
        if (other == null) return false;
        var a = ToBytes();
        var b = other.ToBytes();
        for (int i = 0; i < WireSize; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Display label for the learned code, e.g. "NEC 0x00FF00FF".</summary>
    public string CodeLabel => IsConfigured
        ? $"{CsWire.IrProtocolName(Proto)} 0x{Code:X8}"
        : "Not learned";
}

/// <summary>The 40-byte CsGroup wire struct (REQ_SET/GET_CS_GROUP; caps v9). A
/// named set of channels one control addresses as a unit. <see cref="Kind"/> 0
/// marks the slot empty, and the firmware then requires the whole record to be
/// zero, so <see cref="Cleared"/> is the only legal clear payload.</summary>
public sealed class CsGroup
{
    public const int WireSize = 40;

    public CsTarget Kind;       // @0  InputCh / OutputCh / DspCh; None = empty slot
    // @1..3 reserved
    public uint MemberMask;     // @4  bit N = channel N of the kind's space
    public string Name = "";    // @8  32 bytes, NUL-terminated

    public bool IsConfigured => Kind != CsTarget.None && MemberMask != 0;

    /// <summary>Number of channels in the group.</summary>
    public int MemberCount
    {
        get
        {
            int n = 0;
            for (uint m = MemberMask; m != 0; m &= m - 1) n++;
            return n;
        }
    }

    public static CsGroup Cleared() => new();

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = (byte)Kind;
        BitConverter.GetBytes(MemberMask).CopyTo(b, 4);
        CsWire.WriteName(Name, b, 8);
        return b;
    }

    public static CsGroup? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsGroup
        {
            Kind = (CsTarget)d[0],
            MemberMask = BitConverter.ToUInt32(d, 4),
            Name = CsWire.ReadName(d, 8),
        };
    }

    public CsGroup Clone() => (CsGroup)MemberwiseClone();

    public bool WireEquals(CsGroup other)
    {
        if (other == null) return false;
        var a = ToBytes();
        var b = other.ToBytes();
        for (int i = 0; i < WireSize; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>The 12-byte CsMacroStep wire struct (REQ_SET_CS_MACRO_STEP; caps v9).
/// A stripped, button-shaped binding fired by the sequencer after
/// <see cref="PreDelay"/>. An all-zero record is an empty step (skipped).</summary>
public sealed class CsMacroStep
{
    public const int WireSize = 12;

    public byte Noun;           // @0  CsNoun (never Macro — no nesting)
    public byte Action;         // @1  CsAction: Set / Toggle / Inc / Dec / Trigger
    public CsFlags Flags;       // @2  Wrap | Group only
    public byte Target;         // @3  channel, or group index when Group is set
    public byte Index;          // @4  filter band for DSP_BAND nouns
    // @5 reserved
    public short Value;         // @6  as CsBinding.Value
    public short Step;          // @8  INC/DEC size; 0 = unit default
    public ushort PreDelay;     // @10 delay before this step runs, 10 ms units

    /// <summary>An empty step is the all-zero record. Noun 0 with action 0
    /// (ADJUST, which no step may use) never occurs in a written step.</summary>
    public bool IsConfigured =>
        Noun != 0 || Action != 0 || Flags != CsFlags.None || Target != 0
        || Index != 0 || Value != 0 || Step != 0 || PreDelay != 0;

    public bool IsGrouped => (Flags & CsFlags.Group) != 0;

    /// <summary>Delay before this step runs, in seconds.</summary>
    public double PreDelaySeconds
    {
        get => PreDelay * CsLimits.MacroDelayUnitSeconds;
        set => PreDelay = (ushort)Math.Clamp(Math.Round(value / CsLimits.MacroDelayUnitSeconds), 0, ushort.MaxValue);
    }

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = Noun;
        b[1] = Action;
        b[2] = (byte)Flags;
        b[3] = Target;
        b[4] = Index;
        // b[5] reserved
        BitConverter.GetBytes(Value).CopyTo(b, 6);
        BitConverter.GetBytes(Step).CopyTo(b, 8);
        BitConverter.GetBytes(PreDelay).CopyTo(b, 10);
        return b;
    }

    public static CsMacroStep? FromBytes(byte[] d, int off = 0)
    {
        if (d == null || d.Length < off + WireSize) return null;
        return new CsMacroStep
        {
            Noun = d[off],
            Action = d[off + 1],
            Flags = (CsFlags)d[off + 2],
            Target = d[off + 3],
            Index = d[off + 4],
            Value = BitConverter.ToInt16(d, off + 6),
            Step = BitConverter.ToInt16(d, off + 8),
            PreDelay = BitConverter.ToUInt16(d, off + 10),
        };
    }

    public CsMacroStep Clone() => (CsMacroStep)MemberwiseClone();

    public bool WireEquals(CsMacroStep other)
    {
        if (other == null) return false;
        var a = ToBytes();
        var b = other.ToBytes();
        for (int i = 0; i < WireSize; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>One macro slot: the 132-byte CsMacro read back by REQ_GET_CS_MACRO
/// (caps v9). Writing is split — the 36-byte header (name + step count) goes over
/// REQ_SET_CS_MACRO and each step over REQ_SET_CS_MACRO_STEP, because a whole
/// macro exceeds the 64-byte vendor SET buffer. Write the steps first and the
/// header last so a concurrent fire never sees a count past the written steps.</summary>
public sealed class CsMacro
{
    public const int WireSize = 132;
    public const int HeaderWireSize = 36;   // CsMacroHeaderWire

    public string Name = "";    // @0  32 bytes, NUL-terminated
    public byte StepCount;      // @32 steps executed = Steps[0..StepCount-1]
    // @33..35 reserved
    public CsMacroStep[] Steps = NewSteps();   // @36, 8 × 12 bytes

    public bool IsConfigured => StepCount > 0 || Name.Length > 0;

    private static CsMacroStep[] NewSteps()
    {
        var a = new CsMacroStep[CsLimits.MaxMacroSteps];
        for (int i = 0; i < a.Length; i++) a[i] = new CsMacroStep();
        return a;
    }

    /// <summary>The 36-byte REQ_SET_CS_MACRO payload (name + step count).</summary>
    public byte[] HeaderToBytes()
    {
        var b = new byte[HeaderWireSize];
        CsWire.WriteName(Name, b, 0);
        b[32] = StepCount;
        return b;
    }

    public static CsMacro? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        var m = new CsMacro
        {
            Name = CsWire.ReadName(d, 0),
            StepCount = d[32],
        };
        for (int i = 0; i < CsLimits.MaxMacroSteps; i++)
            m.Steps[i] = CsMacroStep.FromBytes(d, 36 + i * CsMacroStep.WireSize) ?? new CsMacroStep();
        return m;
    }

    public CsMacro Clone()
    {
        var m = (CsMacro)MemberwiseClone();
        m.Steps = new CsMacroStep[Steps.Length];
        for (int i = 0; i < Steps.Length; i++) m.Steps[i] = Steps[i].Clone();
        return m;
    }

    public bool WireEquals(CsMacro other)
    {
        if (other == null) return false;
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal)) return false;
        if (StepCount != other.StepCount) return false;
        for (int i = 0; i < Steps.Length; i++)
            if (!Steps[i].WireEquals(other.Steps[i])) return false;
        return true;
    }
}

/// <summary>The 24-byte CsExtStatusPacket (REQ_GET_CS_EXT_STATUS; caps v9): the
/// group/macro companion to <see cref="CsStatusPacket"/>.</summary>
public sealed class CsExtStatusPacket
{
    public const int WireSize = 24;

    public byte MaxGroups;
    public byte MaxMacros;
    public byte MaxMacroSteps;
    public byte MacroRunning;   // running macro index; 0xFF = idle
    public byte MacroStep;      // current step index while running
    public byte[] GroupStatus = new byte[CsLimits.MaxGroups];
    public byte[] MacroStatus = new byte[CsLimits.MaxMacros];

    public bool IsMacroRunning => MacroRunning != CsLimits.MacroIdle;

    public byte GroupHealth(int idx) =>
        idx >= 0 && idx < GroupStatus.Length ? GroupStatus[idx] : (byte)0;

    public byte MacroHealth(int idx) =>
        idx >= 0 && idx < MacroStatus.Length ? MacroStatus[idx] : (byte)0;

    public static CsExtStatusPacket? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        var s = new CsExtStatusPacket
        {
            MaxGroups = d[0],
            MaxMacros = d[1],
            MaxMacroSteps = d[2],
            MacroRunning = d[3],
            MacroStep = d[4],
        };
        Array.Copy(d, 8, s.GroupStatus, 0, CsLimits.MaxGroups);
        Array.Copy(d, 16, s.MacroStatus, 0, CsLimits.MaxMacros);
        return s;
    }
}

// -----------------------------------------------------------------------------
// I2C display (caps v10; control_surfaces_display_spec.md)
//
// The panel itself is an ordinary binding of type Display — SDA, SCL, a model
// and an address. What it shows lives apart from it: one device-global config
// record and a table of pages, each page a {noun, target} drawn from the same
// noun table the bindings use, so anything a control can drive a page can show.
// Both are covered by the same live preview and the same CS Save / Revert.
// -----------------------------------------------------------------------------

/// <summary>What the panel rests on between changes (firmware CsDisplayCfg.mode).</summary>
public enum CsDisplayMode : byte
{
    Fixed = 0,          // show CsDisplayCfg.HomePage
    CycleSelected = 1,  // rotate the active pages at Dwell
    CycleAll = 2        // rotate every displayable noun; no page table needed
}

/// <summary>CsDisplayCfg.flags. Bits 3:2 and 5:4 are the two 2-bit alignment
/// fields (caps v11), exposed as <see cref="CsDisplayCfg.LabelAlign"/> and
/// <see cref="CsDisplayCfg.ValueAlign"/> rather than read as raw bits.</summary>
[Flags]
public enum CsDisplayCfgFlags : byte
{
    None = 0,
    OverlayAny = 0x01,      // the pop-up also covers unconfigured changes
    EditGated = 0x02,       // PageValue adjusts only while editing is armed
    LabelAlignMask = 0x0C,  // v11
    ValueAlignMask = 0x30   // v11
}

/// <summary>One display line's horizontal alignment (caps v11). Encoding 3 is
/// reserved and rejected.</summary>
public enum CsDisplayAlign : byte { Left = 0, Centre = 1, Right = 2 }

/// <summary>CsDisplayPage.flags. An all-zero record is an empty slot.</summary>
[Flags]
public enum CsDisplayPageFlags : byte
{
    None = 0,
    Active = 0x01,  // slot in use
    Group = 0x02,   // `Target` is a group index
    Large = 0x04,   // pixel-doubled value on the graphic OLEDs
    /// <summary>caps v13: draw a level bar for the value. Continuous nouns only
    /// — the bar plots the value inside the noun's own range, which a bool or an
    /// enum has none of, and the firmware rejects the whole page if one carries
    /// it.</summary>
    Bar = 0x08
}

/// <summary>CsDisplayStatus.init_state — how far the panel's driver has got.</summary>
public enum CsDisplayInitState : byte { Down = 0, Starting = 1, Live = 2, Error = 3 }

/// <summary>CsDisplayStatus.flags.</summary>
[Flags]
public enum CsDisplayStatusFlags : byte { None = 0, Overlay = 0x01, EditArmed = 0x02 }

/// <summary>Panel model — CsBinding.Index on a Display slot. Wire and flash
/// persistent, so these are never renumbered; geometry and bus speed are fixed
/// per model by the firmware driver.</summary>
public enum CsDisplayModel : byte
{
    None = 0,
    Lcd1602 = 1,          // HD44780 + PCF8574 backpack, 16x2
    Lcd2004 = 2,          // HD44780 + PCF8574 backpack, 20x4
    CharOled16x2 = 3,     // US2066 / RW1063
    CharOled20x2 = 4,
    CharOled20x4 = 5,
    Ssd1306_128x64 = 6,   // graphic: 21x8 small font, 12 large columns
    Ssd1306_128x32 = 7,
    Sh1106_128x64 = 8
}

/// <summary>Client-side names and traits for the panel models (the wire format
/// carries no strings for them).</summary>
public static class CsDisplayModels
{
    public const int Count = 9;   // including None at 0

    public static string Name(int model) => (CsDisplayModel)model switch
    {
        CsDisplayModel.Lcd1602 => "LCD 16x2 (HD44780)",
        CsDisplayModel.Lcd2004 => "LCD 20x4 (HD44780)",
        CsDisplayModel.CharOled16x2 => "Character OLED 16x2",
        CsDisplayModel.CharOled20x2 => "Character OLED 20x2",
        CsDisplayModel.CharOled20x4 => "Character OLED 20x4",
        CsDisplayModel.Ssd1306_128x64 => "OLED 128x64 (SSD1306)",
        CsDisplayModel.Ssd1306_128x32 => "OLED 128x32 (SSD1306)",
        CsDisplayModel.Sh1106_128x64 => "OLED 128x64 (SH1106)",
        _ => $"Model {model}",
    };

    /// <summary>The address a model answers on when the binding stores 0.</summary>
    public static byte DefaultAddress(int model) =>
        (CsDisplayModel)model is CsDisplayModel.Lcd1602 or CsDisplayModel.Lcd2004
            ? (byte)0x27 : (byte)0x3C;

    /// <summary>True for the graphic OLEDs — the only models with a large font to
    /// render, and the only ones that draw a level bar without spending a row.</summary>
    public static bool IsGraphic(int model) => model >= (int)CsDisplayModel.Ssd1306_128x64;

    /// <summary>True for the four-row character panels, which have a row to spare
    /// for a level bar and so keep the value on a line of its own.</summary>
    public static bool HasSpareRow(int model) =>
        (CsDisplayModel)model is CsDisplayModel.Lcd2004 or CsDisplayModel.CharOled20x4;

    /// <summary>The addresses worth offering: the two model defaults plus the
    /// SSD1306 / SH1106 alternates a jumper selects.</summary>
    public static readonly byte[] CommonAddresses = { 0x27, 0x3C, 0x3D, 0x3E, 0x3F };
}

/// <summary>The 12-byte CsDisplayCfg wire struct (REQ_SET/GET_CS_DISPLAY_CFG):
/// what the panel rests on when nothing has just changed, how long a change pops
/// up for, and how the two lines are placed. Timing fields are in 0.1 s units,
/// like the indicator delays.</summary>
public sealed class CsDisplayCfg
{
    public const int WireSize = 12;
    /// <summary>The GET response is a 4-byte limits header then the config.</summary>
    public const int GetWireSize = 16;

    public CsDisplayMode Mode;          // @0
    public byte HomePage;               // @1  page shown in Fixed mode
    public ushort Dwell = 30;           // @2  rotation period, 0.1 s units
    public ushort OverlayHold = 20;     // @4  pop-up hold, 0.1 s units; 0 = no pop-up
    public byte Brightness;             // @6  OLED contrast; 0 = the driver default
    public CsDisplayCfgFlags Flags;     // @7
    public ushort EditTimeout = 100;    // @8  edit auto-disarm, 0.1 s units; 0 = manual
    // @10..11 reserved

    /// <summary>Horizontal placement of the label line (caps v11), one of the two
    /// 2-bit fields packed into <see cref="Flags"/>.</summary>
    public CsDisplayAlign LabelAlign
    {
        get => (CsDisplayAlign)(((byte)Flags & (byte)CsDisplayCfgFlags.LabelAlignMask) >> 2);
        set => Flags = (CsDisplayCfgFlags)(((byte)Flags & ~(byte)CsDisplayCfgFlags.LabelAlignMask)
                                           | (((byte)value << 2) & (byte)CsDisplayCfgFlags.LabelAlignMask));
    }

    /// <summary>Horizontal placement of the value line. Centre is the one setting
    /// that keeps the value on the same columns armed and unarmed: the edit
    /// markers claim the outer column of each side either way.</summary>
    public CsDisplayAlign ValueAlign
    {
        get => (CsDisplayAlign)(((byte)Flags & (byte)CsDisplayCfgFlags.ValueAlignMask) >> 4);
        set => Flags = (CsDisplayCfgFlags)(((byte)Flags & ~(byte)CsDisplayCfgFlags.ValueAlignMask)
                                           | (((byte)value << 4) & (byte)CsDisplayCfgFlags.ValueAlignMask));
    }

    public bool HasFlag(CsDisplayCfgFlags f) => (Flags & f) != 0;

    public void SetFlag(CsDisplayCfgFlags f, bool on)
    {
        if (on) Flags |= f; else Flags &= ~f;
    }

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = (byte)Mode;
        b[1] = HomePage;
        BitConverter.GetBytes(Dwell).CopyTo(b, 2);
        BitConverter.GetBytes(OverlayHold).CopyTo(b, 4);
        b[6] = Brightness;
        b[7] = (byte)Flags;
        BitConverter.GetBytes(EditTimeout).CopyTo(b, 8);
        return b;
    }

    /// <summary>Parse the config at <paramref name="offset"/> — 0 in a SET
    /// payload, 4 in the GET response behind its limits header.</summary>
    public static CsDisplayCfg? FromBytes(byte[] d, int offset = 0)
    {
        if (d == null || d.Length < offset + WireSize) return null;
        return new CsDisplayCfg
        {
            Mode = (CsDisplayMode)d[offset],
            HomePage = d[offset + 1],
            Dwell = BitConverter.ToUInt16(d, offset + 2),
            OverlayHold = BitConverter.ToUInt16(d, offset + 4),
            Brightness = d[offset + 6],
            Flags = (CsDisplayCfgFlags)d[offset + 7],
            EditTimeout = BitConverter.ToUInt16(d, offset + 8),
        };
    }

    public CsDisplayCfg Clone() => (CsDisplayCfg)MemberwiseClone();

    public bool WireEquals(CsDisplayCfg other)
    {
        if (other == null) return false;
        var a = ToBytes();
        var b = other.ToBytes();
        for (int i = 0; i < WireSize; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>The 4-byte CsDisplayPage wire struct (REQ_SET/GET_CS_DISPLAY_PAGE):
/// one item the panel can rest on or rotate through. An all-zero record clears
/// the slot, and the firmware requires an inactive record to be exactly that.</summary>
public sealed class CsDisplayPage
{
    public const int WireSize = 4;

    public byte Noun;                 // @0  CsNoun to show
    public byte Target;               // @1  channel, or a group index under Group
    public byte Index;                // @2  filter band for DSP_BAND nouns
    public CsDisplayPageFlags Flags;  // @3

    public bool IsActive => (Flags & CsDisplayPageFlags.Active) != 0;
    public bool IsGrouped => (Flags & CsDisplayPageFlags.Group) != 0;
    public bool IsLarge => (Flags & CsDisplayPageFlags.Large) != 0;
    public bool HasBar => (Flags & CsDisplayPageFlags.Bar) != 0;

    public void SetFlag(CsDisplayPageFlags f, bool on)
    {
        if (on) Flags |= f; else Flags &= ~f;
    }

    public byte[] ToBytes() => new[] { Noun, Target, Index, (byte)Flags };

    public static CsDisplayPage? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsDisplayPage
        {
            Noun = d[0], Target = d[1], Index = d[2],
            Flags = (CsDisplayPageFlags)d[3],
        };
    }

    public CsDisplayPage Clone() => (CsDisplayPage)MemberwiseClone();

    public bool WireEquals(CsDisplayPage other) =>
        other != null && Noun == other.Noun && Target == other.Target
        && Index == other.Index && Flags == other.Flags;
}

/// <summary>The 8-byte CsDisplayStatus (REQ_GET_CS_DISPLAY_STATUS): live panel
/// state. <see cref="NakCount"/> is how a miswired or absent module announces
/// itself — the driver keeps retrying, so the abort count climbs rather than the
/// feature failing silently.</summary>
public sealed class CsDisplayStatus
{
    public const int WireSize = 8;

    /// <summary>CurrentPage when nothing (or a synthesized pop-up) is showing.</summary>
    public const byte PageNone = 0xFF;

    public CsDisplayInitState InitState;
    public byte CurrentPage = PageNone;
    public CsDisplayStatusFlags Flags;
    public byte Model;
    public ushort NakCount;     // saturating count of I2C aborts

    public bool IsLive => InitState == CsDisplayInitState.Live;
    public bool OverlayShowing => (Flags & CsDisplayStatusFlags.Overlay) != 0;
    public bool EditArmed => (Flags & CsDisplayStatusFlags.EditArmed) != 0;

    public static CsDisplayStatus? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsDisplayStatus
        {
            InitState = (CsDisplayInitState)d[0],
            CurrentPage = d[1],
            Flags = (CsDisplayStatusFlags)d[2],
            Model = d[3],
            NakCount = BitConverter.ToUInt16(d, 4),
        };
    }
}

/// <summary>The whole REQ_GET_CS_DISPLAY_CFG response: the device's own page and
/// model limits, then the config. The limits are served here rather than in the
/// caps header, so they arrive alongside the config they bound.</summary>
public sealed class CsDisplayCfgRead
{
    public byte MaxPages;
    public byte ModelCount;
    public CsDisplayCfg Cfg = new();

    public static CsDisplayCfgRead? FromBytes(byte[] d)
    {
        if (d == null || d.Length < CsDisplayCfg.GetWireSize) return null;
        var cfg = CsDisplayCfg.FromBytes(d, 4);
        if (cfg == null) return null;
        return new CsDisplayCfgRead { MaxPages = d[0], ModelCount = d[1], Cfg = cfg };
    }
}

/// <summary>One entry in the caps type table (4 bytes; CsTypeDesc).</summary>
public readonly struct CsTypeDesc
{
    public readonly ushort Actions;   // CS_ACT_BIT mask
    public readonly byte PinCount;    // 1 or 2
    public readonly CsPinClass PinClass;

    public CsTypeDesc(ushort actions, byte pinCount, CsPinClass pinClass)
    {
        Actions = actions; PinCount = pinCount; PinClass = pinClass;
    }

    public bool SupportsAction(CsAction a) => (Actions & (1u << (int)a)) != 0;
}

/// <summary>The caps header + type table (REQ_GET_CS_CAPS, wValue 0xFFFF). Length
/// is variable — the v3 tail (<see cref="MaxIrCommands"/>) sits at
/// <c>4 + 4*TypeCount</c> so a type-table growth won't move it, and the v9
/// group/macro limits follow it in the three bytes a pre-v9 firmware reserved
/// (they read 0 there). Caps v10 exercised exactly that: adding the display type
/// grew the response from 40 bytes to 44. Parse defensively rather than assuming
/// either length.</summary>
public sealed class CsCapsHeader
{
    public byte CapsVersion;
    public byte MaxBindings;
    public byte TypeCount;
    public byte NounCount;
    public CsTypeDesc[] Types = Array.Empty<CsTypeDesc>();
    public byte MaxIrCommands;   // v3; 0 on a v2 header = IR unavailable
    public byte MaxGroups;       // v9; 0 on a pre-v9 header = groups unavailable
    public byte MaxMacros;       // v9
    public byte MaxMacroSteps;   // v9

    public bool IsValid => TypeCount > 0 && NounCount > 0;

    /// <summary>Whether this firmware serves the caps-v9 group and macro
    /// commands (0x20–0x26).</summary>
    public bool HasGroupsAndMacros => MaxGroups > 0 && MaxMacros > 0;

    /// <summary>Whether this firmware offers the I2C display component (caps
    /// v10). Read from the type table rather than the version, the same
    /// self-describing rule the rest of the editor follows.</summary>
    public bool HasDisplay => TypeCount > (int)CsType.Display;

    /// <summary>Whether this firmware offers the aux output components (caps v18).</summary>
    public bool HasAux => TypeCount > (int)CsType.AuxPwm;

    public static CsCapsHeader? FromBytes(byte[] d)
    {
        if (d == null || d.Length < 4) return null;
        var h = new CsCapsHeader
        {
            CapsVersion = d[0],
            MaxBindings = d[1],
            TypeCount = d[2],
            NounCount = d[3],
        };
        int count = h.TypeCount;
        // Only parse as many type descriptors as actually fit in the buffer.
        int maxFit = (d.Length - 4) / 4;
        if (count > maxFit) count = maxFit;
        h.Types = new CsTypeDesc[count];
        for (int i = 0; i < count; i++)
        {
            int off = 4 + i * 4;
            h.Types[i] = new CsTypeDesc(
                BitConverter.ToUInt16(d, off),
                d[off + 2],
                (CsPinClass)d[off + 3]);
        }
        int irOff = 4 + h.TypeCount * 4;
        h.MaxIrCommands = irOff < d.Length ? d[irOff] : (byte)0;
        h.MaxGroups = irOff + 1 < d.Length ? d[irOff + 1] : (byte)0;
        h.MaxMacros = irOff + 2 < d.Length ? d[irOff + 2] : (byte)0;
        h.MaxMacroSteps = irOff + 3 < d.Length ? d[irOff + 3] : (byte)0;
        return h;
    }

    /// <summary>Type descriptor for a CsType, or null if out of range.</summary>
    public CsTypeDesc? DescFor(CsType t)
    {
        int i = (int)t;
        return i >= 0 && i < Types.Length ? Types[i] : null;
    }
}

/// <summary>One noun's descriptor (12 bytes; CsNounDesc), from 0x86 wValue=noun.</summary>
public sealed class CsNounDesc
{
    public const int WireSize = 12;

    public CsKind Kind;
    public byte EnumCount;
    public ushort Actions;     // accepted-action mask; 0 = unavailable on platform
    public short MinQ;         // continuous range low, unit-encoded
    public short MaxQ;         // continuous range high
    public CsUnit Unit;
    public CsTarget TargetKind;
    public byte TargetCount;   // valid targets 0..count-1; 0 = untargeted
    public byte DFlags;

    public bool IsAvailable => Actions != 0;
    public bool IsTargeted => TargetKind != CsTarget.None && TargetCount > 0;
    public bool HasBand => TargetKind == CsTarget.DspBand;
    public bool SupportsAction(CsAction a) => (Actions & (1u << (int)a)) != 0;
    public double Min => CsWire.DecodeValue(MinQ, Unit);
    public double Max => CsWire.DecodeValue(MaxQ, Unit);

    public static CsNounDesc? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsNounDesc
        {
            Kind = (CsKind)d[0],
            EnumCount = d[1],
            Actions = BitConverter.ToUInt16(d, 2),
            MinQ = BitConverter.ToInt16(d, 4),
            MaxQ = BitConverter.ToInt16(d, 6),
            Unit = (CsUnit)d[8],
            TargetKind = (CsTarget)d[9],
            TargetCount = d[10],
            DFlags = d[11],
        };
    }
}

/// <summary>The 41-byte CsStatusPacket (REQ_GET_CS_STATUS). The base v2 layout is
/// 22 bytes; the IR tail is only read when present, and moved at caps v6 (the
/// active mask widened to a uint16 for 16 sub-slots, pushing the learn state to
/// 24 and the per-sub-slot status array to 25). A v3..v5 firmware still answers
/// with the 32-byte layout, so the tail is parsed by length.</summary>
public sealed class CsStatusPacket
{
    public byte LastStatus;      // most recent deferred CS SET result
    public byte LastSlot;        // slot; 0x80|n = IR sub-slot; 0xFF = save/revert
    public byte MaxBindings;
    public bool Dirty;           // v3; live differs from flash
    public ushort ActiveMask;    // bit N = binding N live
    public byte[] SlotStatus = new byte[16];
    public ushort IrActiveMask;  // v3 (uint16 since v6); bit N = IR command N live
    public CsIrLearnState IrLearnState; // v3
    public byte[] IrCmdStatus = new byte[CsLimits.MaxIrCommands]; // v3 (16 since v6)

    public bool IsSlotActive(int slot) =>
        slot >= 0 && slot < 16 && (ActiveMask & (1 << slot)) != 0;

    public byte SlotHealth(int slot) =>
        slot >= 0 && slot < SlotStatus.Length ? SlotStatus[slot] : (byte)0;

    public bool IsIrCmdActive(int sub) =>
        sub >= 0 && sub < CsLimits.MaxIrCommands && (IrActiveMask & (1 << sub)) != 0;

    public byte IrCmdHealth(int sub) =>
        sub >= 0 && sub < IrCmdStatus.Length ? IrCmdStatus[sub] : (byte)0;

    public static CsStatusPacket? FromBytes(byte[] d)
    {
        if (d == null || d.Length < 22) return null;
        var s = new CsStatusPacket
        {
            LastStatus = d[0],
            LastSlot = d[1],
            MaxBindings = d[2],
            Dirty = d[3] != 0,
            ActiveMask = BitConverter.ToUInt16(d, 4),
        };
        Array.Copy(d, 6, s.SlotStatus, 0, 16);
        if (d.Length >= 41)
        {
            // v6 tail: 16 sub-slots.
            s.IrActiveMask = BitConverter.ToUInt16(d, 22);
            s.IrLearnState = (CsIrLearnState)d[24];
            Array.Copy(d, 25, s.IrCmdStatus, 0, 16);
        }
        else if (d.Length >= 24)
        {
            // v3..v5 tail: 8 sub-slots, byte-wide mask.
            s.IrActiveMask = d[22];
            s.IrLearnState = (CsIrLearnState)d[23];
            if (d.Length >= 32) Array.Copy(d, 24, s.IrCmdStatus, 0, 8);
        }
        return s;
    }
}

/// <summary>The 8-byte IR-learn result (REQ_CS_IR_LEARN wValue=2).</summary>
public sealed class CsIrLearnResult
{
    public const int WireSize = 8;

    public CsIrLearnState State;
    public CsIrProto Proto;
    public uint Code;   // LE

    public bool IsDone => State == CsIrLearnState.Done;
    public bool IsTimeout => State == CsIrLearnState.Timeout;

    public static CsIrLearnResult? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsIrLearnResult
        {
            State = (CsIrLearnState)d[0],
            Proto = (CsIrProto)d[1],
            Code = BitConverter.ToUInt32(d, 4),
        };
    }
}
