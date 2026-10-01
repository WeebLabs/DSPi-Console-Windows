using System.Collections.ObjectModel;
using System.Text;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Captures a snapshot of all DSP state for change detection.
/// </summary>
public class PresetSnapshot
{
    public float InputPreampLDb;
    public float InputPreampRDb;
    // Preamps of wire inputs 2..7 (RP2350 multichannel inputs).
    public float[] InputPreampExt = new float[6];
    public float MasterVolumeDb;
    // V15+ preset slot persists audio_state.volume as user_vol_index. Captured
    // unconditionally — unlike MasterVolumeDb, user volume isn't gated by a
    // mode flag, the firmware always restores it on preset load (if the slot
    // is V15+; pre-V15 slots leave it untouched, which is correct legacy
    // behavior, but new slots written by current firmware always include it).
    public float UserVolumeDb;
    public bool Bypass;

    public bool LoudnessEnabled;
    public float LoudnessRefSpl;
    public float LoudnessIntensity;

    public bool CrossfeedEnabled;
    public int CrossfeedPreset;
    public float CrossfeedFreq;
    public float CrossfeedFeed;
    public bool CrossfeedItd;

    // Multichannel DSP masks (V18/V19/V20). Part of the preset slot: loudness
    // output mask, crossfeed output-pair mask, and the leveller detector/apply
    // channel masks. Captured so mask edits register as preset-dirty.
    public int LoudnessOutputMask;
    public int CrossfeedOutputPairMask;
    public int LevellerDetectorMask;
    public int LevellerApplyMask;

    public Dictionary<int, float> Delays = new();
    // Input channels' own delays (REQ_SET_DELAY), by app channel id. An output's
    // channel delay is its output delay, already in Delays above.
    public Dictionary<int, float> InputDelays = new();
    public bool[,] MatrixRouting = new bool[MainViewModel.MatrixMaxInputs, 9];
    public float[,] MatrixGain = new float[MainViewModel.MatrixMaxInputs, 9];
    public bool[,] MatrixInvert = new bool[MainViewModel.MatrixMaxInputs, 9];

    public Dictionary<int, bool> OutputEnabled = new();
    public bool[] OutputMuted = new bool[9];
    public Dictionary<int, float> OutputGains = new();

    public FilterParams[,] Eq = new FilterParams[ChannelMap.AppChannelCount, 12];

    // Crossover bands (V11+ firmware): 4 per output channel. Master/input rows
    // stay null. Captured so crossover edits register as preset-dirty.
    public FilterParams[,] Xover = new FilterParams[ChannelMap.AppChannelCount, 4];

    public Dictionary<int, string> ChannelNames = new();

    public Dictionary<int, byte> OutputPins = new();
    public byte[] OutputSlotTypes = new byte[4];

    public byte I2SBckPin;
    public bool MckEnabled;
    public byte MckPin;
    public int MckMultiplier;

    // Input source (V7+ wire format, V13+ slot data) — saved with each preset.
    public InputSource InputSource;

    // SPDIF RX pin: V13+ slots persist it alongside the output pins.
    // Tracked the same way as OutputPins — captured unconditionally; the
    // directory's output_config_mode flag only controls whether preset_load
    // applies it (with-preset) or leaves the live IO untouched (independent).
    public byte SpdifRxPin;

    // I2S input data pin + master rate (V12+ wire, V17+ slot). Same IO-block
    // treatment as SpdifRxPin — gated on output_config_mode in the diff.
    public byte I2sRxPin;
    public uint I2sInputRateHz;

    // Multiple SPDIF inputs + multichannel I2S input (IO block). Ext SPDIF pins
    // for inputs 1..3, the 3-bit SPDIF enable mask, I2S channel count, and the
    // per-pair I2S data pins for pairs 1..3.
    public byte[] SpdifRxPinsExt = new byte[3];
    public byte SpdifEnabledExt;
    public int I2sInputChannels;
    public byte[] I2sRxPinsExt = new byte[3];

    // ADAT bulk optical output (V17+, RP2350). Enable + data pin — IO block,
    // same output_config_mode gating as the SPDIF/I2S input pins.
    public bool AdatEnabled;
    public byte AdatPin;

    // ADAT optical input (V24+, RP2350). Enable + RX pin + clock mode (0=master,
    // 1=slave). IO block, same output_config_mode gating as the other input pins.
    public bool AdatInputEnabled;
    public byte AdatInputPin;
    public byte AdatInputClockMode;

    // I2S clock master/slave mode + clock-pin unified/split + slave-pair BCK.
    public byte I2sClockMode;      // 0=master, 1=slave
    public byte I2sClockPinMode;   // 0=unified, 1=split
    public byte I2sBckPinSlave;

    // LG Sound Sync enable (V8+ preset slot field). Only the user-writable
    // `enabled` flag is preset state; runtime fields (present, volume, muted)
    // are diagnostic and not captured. Tracks via REQ_SET/GET_LG_SOUND_SYNC
    // (0xE6/0xE7) when the toggle moves and is honored on bulk SET.
    public bool LgSoundSyncEnabled;

    // Stereo upmixer (slot V34): all 14 fields round-trip with the preset.
    // Null when the firmware has no upmixer (pre-V25 / RP2040).
    public UpmixConfig? Upmix;

    // Volume leveller core settings (the masks are above).
    public bool LevellerEnabled;
    public float LevellerAmount;
    public int LevellerSpeed;
    public float LevellerMaxGainDb;
    public bool LevellerLookahead;
    public float LevellerGateDb;

    // Psychoacoustic bass (V23, preset slot).
    public bool PsybassEnabled;
    public int PsybassOutputMask;
    public float PsybassCutoffHz;
    public float PsybassHarmonicsDb;
    public float PsybassDriveDb;
    public float PsybassCharacterPct;
    public float PsybassOriginalDb;

    // Subharmonic synthesizer (V29/V30, preset slot). Solo is runtime-only and
    // the headroom is derived, so neither is here.
    public bool SubharmEnabled;
    public int SubharmOutputMask;
    public float SubharmLowDb;
    public float SubharmHighDb;
    public float SubharmTopDb;
    public float SubharmBoostDb;
    public int SubharmSelectMode;
    public float SubharmSelectDepthPct;
    public float SubharmSelectHoldMs;
    public float SubharmCeilingDb;
    public bool SubharmLinkPairs;

    // Tube modeller (V31, preset slot).
    public bool TubeEnabled;
    public int TubeOutputMask;
    public int TubeType;
    public float TubeDriveDb;
    public float TubeBiasPct;
    public float TubeAsymDb;
    public float TubeHardnessPct;
    public float TubeSagPct;
    public int TubeRectifier;
    public bool TubeXfmrEnabled;
    public float TubeXfmrDamping;
    public float TubeXfmrResHz;
    public float TubeMixPct;
    public float TubeTrimDb;

    // Output limiter (V32), one entry per wire output slot. Follows
    // output_config_mode like the pins, so it is part of the IO block.
    public LimiterOutputSettings[] Limiter = Array.Empty<LimiterOutputSettings>();

    /// <summary>
    /// Capture a snapshot from the current ViewModel state.
    /// </summary>
    public static PresetSnapshot Capture(MainViewModel vm)
    {
        var snap = new PresetSnapshot
        {
            InputPreampLDb = vm.InputPreampLDb,
            InputPreampRDb = vm.InputPreampRDb,
            MasterVolumeDb = vm.MasterVolumeDb,
            UserVolumeDb = vm.UserVolumeDb,
            Bypass = vm.Bypass,
            LoudnessEnabled = vm.LoudnessEnabled,
            LoudnessRefSpl = vm.LoudnessRefSPL,
            LoudnessIntensity = vm.LoudnessIntensity,
            CrossfeedEnabled = vm.CrossfeedEnabled,
            CrossfeedPreset = vm.CrossfeedPreset,
            CrossfeedFreq = vm.CrossfeedFreq,
            CrossfeedFeed = vm.CrossfeedFeed,
            CrossfeedItd = vm.CrossfeedItd,
            LoudnessOutputMask = vm.LoudnessOutputMask,
            CrossfeedOutputPairMask = vm.CrossfeedOutputPairMask,
            LevellerDetectorMask = vm.LevellerDetectorMask,
            LevellerApplyMask = vm.LevellerApplyMask,
        };

        // Delays and gains
        foreach (var ch in Channel.All)
        {
            int id = (int)ch.Id;
            snap.Delays[id] = vm.GetChannelDelay(ch);
            if (!ch.IsOutput) snap.InputDelays[id] = vm.GetPreMatrixDelay(id);
            if (ch.IsOutput)
                snap.OutputGains[id] = vm.GetChannelGain(ch);
        }

        // Matrix
        var outputs = vm.ActiveOutputs;
        for (int inp = 0; inp < MainViewModel.MatrixMaxInputs; inp++)
        {
            for (int o = 0; o < outputs.Count && o < 9; o++)
            {
                snap.MatrixRouting[inp, o] = vm.GetMatrixRouting(inp, o);
                snap.MatrixGain[inp, o] = vm.GetMatrixGain(inp, o);
                snap.MatrixInvert[inp, o] = vm.GetMatrixInvert(inp, o);
            }
        }

        // Output enabled/muted
        for (int o = 0; o < outputs.Count && o < 9; o++)
        {
            snap.OutputEnabled[o] = vm.IsOutputEnabled(o);
            snap.OutputMuted[o] = vm.GetOutputMuted(o);
        }

        // EQ bands
        foreach (var ch in Channel.All)
        {
            int id = (int)ch.Id;
            var filters = vm.GetFilters(ch);
            for (int b = 0; b < filters.Count && b < 12; b++)
            {
                snap.Eq[id, b] = filters[b].Clone();
            }
        }

        // Crossover bands (output channels only)
        foreach (var ch in Channel.All)
        {
            if (!ch.IsOutput) continue;
            int id = (int)ch.Id;
            var xbands = vm.GetXoverFilters(ch);
            for (int b = 0; b < xbands.Count && b < 4; b++)
                snap.Xover[id, b] = xbands[b].Clone();
        }

        // Channel names
        foreach (var ch in Channel.All)
        {
            int id = (int)ch.Id;
            var name = vm.GetChannelName(ch);
            if (name != ch.Name)
                snap.ChannelNames[id] = name;
        }

        // Pin assignments (per pin-output id) and output slot types
        for (int id = 0; id < 5; id++)
            snap.OutputPins[id] = vm.GetOutputPinValue(id);
        for (int s = 0; s < snap.OutputSlotTypes.Length; s++)
            snap.OutputSlotTypes[s] = (byte)vm.GetOutputSlotType(s);

        // I2S hardware config
        snap.I2SBckPin = vm.I2SBckPin;
        snap.MckEnabled = vm.MckEnabled;
        snap.MckPin = vm.MckPin;
        snap.MckMultiplier = vm.MckMultiplier;

        // Input source (preset state on V7+/V13+ firmware; harmless USB default otherwise)
        snap.InputSource = vm.ActiveInputSource;

        // SPDIF RX pin (V13+ slot data — persists alongside output_pins)
        snap.SpdifRxPin = vm.SpdifRxPin;

        // I2S input data pin + master rate (V17+ slot data — IO block)
        snap.I2sRxPin = vm.I2sRxPin;
        snap.I2sInputRateHz = vm.I2sInputRateHz;

        // Multiple SPDIF inputs + multichannel I2S input (IO block)
        for (int i = 0; i < snap.SpdifRxPinsExt.Length; i++)
        {
            snap.SpdifRxPinsExt[i] = vm.SpdifRxPinAt(i + 1);
            if (vm.SpdifInputEnabled(i + 1)) snap.SpdifEnabledExt |= (byte)(1 << i);
        }
        snap.I2sInputChannels = vm.I2sInputChannels;
        snap.I2sRxPinsExt[0] = vm.I2sRxPinAt(1);
        snap.I2sRxPinsExt[1] = vm.I2sRxPinAt(2);
        snap.I2sRxPinsExt[2] = vm.I2sRxPinAt(3);

        // ADAT bulk output (V17+ slot data — IO block)
        snap.AdatEnabled = vm.AdatEnabled;
        snap.AdatPin = vm.AdatPin;
        snap.AdatInputEnabled = vm.AdatInputEnabled;
        snap.AdatInputPin = vm.AdatInputPin;
        snap.AdatInputClockMode = vm.AdatInputClockMode;
        snap.I2sClockMode = vm.I2sClockMode;
        snap.I2sClockPinMode = vm.I2sClockPinMode;
        snap.I2sBckPinSlave = vm.I2sBckPinSlave;

        // LG Sound Sync enable flag (V8+ preset slot field)
        snap.LgSoundSyncEnabled = vm.LgSoundSyncEnabled;

        // Stereo upmixer (slot V34)
        if (vm.UpmixSupported)
            snap.Upmix = vm.CaptureUpmixConfig();

        for (int i = 0; i < snap.InputPreampExt.Length; i++)
            snap.InputPreampExt[i] = vm.InputPreampAt(i + 2);

        snap.LevellerEnabled = vm.LevellerEnabled;
        snap.LevellerAmount = vm.LevellerAmount;
        snap.LevellerSpeed = vm.LevellerSpeed;
        snap.LevellerMaxGainDb = vm.LevellerMaxGainDb;
        snap.LevellerLookahead = vm.LevellerLookahead;
        snap.LevellerGateDb = vm.LevellerGateDb;

        snap.PsybassEnabled = vm.PsybassEnabled;
        snap.PsybassOutputMask = vm.PsybassOutputMask;
        snap.PsybassCutoffHz = vm.PsybassCutoffHz;
        snap.PsybassHarmonicsDb = vm.PsybassHarmonicsDb;
        snap.PsybassDriveDb = vm.PsybassDriveDb;
        snap.PsybassCharacterPct = vm.PsybassCharacterPct;
        snap.PsybassOriginalDb = vm.PsybassOriginalDb;

        snap.SubharmEnabled = vm.SubharmEnabled;
        snap.SubharmOutputMask = vm.SubharmOutputMask;
        snap.SubharmLowDb = vm.SubharmLowDb;
        snap.SubharmHighDb = vm.SubharmHighDb;
        snap.SubharmTopDb = vm.SubharmTopDb;
        snap.SubharmBoostDb = vm.SubharmBoostDb;
        snap.SubharmSelectMode = vm.SubharmSelectMode;
        snap.SubharmSelectDepthPct = vm.SubharmSelectDepthPct;
        snap.SubharmSelectHoldMs = vm.SubharmSelectHoldMs;
        snap.SubharmCeilingDb = vm.SubharmCeilingDb;
        snap.SubharmLinkPairs = vm.SubharmLinkPairs;

        snap.TubeEnabled = vm.TubeEnabled;
        snap.TubeOutputMask = vm.TubeOutputMask;
        snap.TubeType = vm.TubeType;
        snap.TubeDriveDb = vm.TubeDriveDb;
        snap.TubeBiasPct = vm.TubeBiasPct;
        snap.TubeAsymDb = vm.TubeAsymDb;
        snap.TubeHardnessPct = vm.TubeHardnessPct;
        snap.TubeSagPct = vm.TubeSagPct;
        snap.TubeRectifier = vm.TubeRectifier;
        snap.TubeXfmrEnabled = vm.TubeXfmrEnabled;
        snap.TubeXfmrDamping = vm.TubeXfmrDamping;
        snap.TubeXfmrResHz = vm.TubeXfmrResHz;
        snap.TubeMixPct = vm.TubeMixPct;
        snap.TubeTrimDb = vm.TubeTrimDb;

        snap.Limiter = vm.LimiterOutputs.ToArray();

        return snap;
    }

    /// <summary>
    /// Copy just the physical IO-block fields from <paramref name="src"/> into
    /// this snapshot. Used to advance the output-config baseline after a
    /// "Save Output Config" without disturbing the preset (non-IO) baseline.
    /// </summary>
    public void CopyIoBlockFrom(PresetSnapshot src)
    {
        OutputPins = new Dictionary<int, byte>(src.OutputPins);
        OutputSlotTypes = (byte[])src.OutputSlotTypes.Clone();
        I2SBckPin = src.I2SBckPin;
        MckEnabled = src.MckEnabled;
        MckPin = src.MckPin;
        MckMultiplier = src.MckMultiplier;
        SpdifRxPin = src.SpdifRxPin;
        I2sRxPin = src.I2sRxPin;
        I2sInputRateHz = src.I2sInputRateHz;
        SpdifRxPinsExt = (byte[])src.SpdifRxPinsExt.Clone();
        SpdifEnabledExt = src.SpdifEnabledExt;
        I2sInputChannels = src.I2sInputChannels;
        I2sRxPinsExt = (byte[])src.I2sRxPinsExt.Clone();
        AdatEnabled = src.AdatEnabled;
        AdatPin = src.AdatPin;
        AdatInputEnabled = src.AdatInputEnabled;
        AdatInputPin = src.AdatInputPin;
        AdatInputClockMode = src.AdatInputClockMode;
        I2sClockMode = src.I2sClockMode;
        I2sClockPinMode = src.I2sClockPinMode;
        I2sBckPinSlave = src.I2sBckPinSlave;
        Limiter = (LimiterOutputSettings[])src.Limiter.Clone();
    }
}

/// <summary>
/// Computes a human-readable diff between two PresetSnapshots.
/// </summary>
public static class PresetDiff
{
    private const int MaxLines = 15;

    private static string FormatDb(float v) => $"{v:F1} dB";

    private static string FormatVal(float v) =>
        v == (int)v && Math.Abs(v) < 100000 ? $"{(int)v}" : $"{v:F1}";

    // Number of enabled S/PDIF inputs from the 3-bit ext enable mask (input 0
    // is always enabled).
    private static int SpdifInputCount(byte extMask) =>
        1 + ((extMask & 1) != 0 ? 1 : 0) + ((extMask & 2) != 0 ? 1 : 0)
          + ((extMask & 4) != 0 ? 1 : 0);

    private static bool Differs(float a, float b) => Math.Abs(a - b) > 0.05f;

    private static int Count(params bool[] changed) => changed.Count(c => c);

    private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    // Renders a channel/pair bitmask as a 1-based comma list, e.g. "1,2,5".
    private static string MaskList(int mask)
    {
        var nums = new List<string>();
        for (int i = 0; i < 16; i++)
            if ((mask & (1 << i)) != 0) nums.Add((i + 1).ToString());
        return nums.Count == 0 ? "none" : string.Join(",", nums);
    }

    public static List<string> Diff(PresetSnapshot old, PresetSnapshot cur, MainViewModel vm)
    {
        var changes = new List<string>();

        // Global
        if (Math.Abs(old.InputPreampLDb - cur.InputPreampLDb) > 0.05f)
            changes.Add($"Input L preamp: {FormatDb(old.InputPreampLDb)} → {FormatDb(cur.InputPreampLDb)}");
        if (Math.Abs(old.InputPreampRDb - cur.InputPreampRDb) > 0.05f)
            changes.Add($"Input R preamp: {FormatDb(old.InputPreampRDb)} → {FormatDb(cur.InputPreampRDb)}");
        for (int i = 0; i < Math.Min(old.InputPreampExt.Length, cur.InputPreampExt.Length); i++)
            if (Math.Abs(old.InputPreampExt[i] - cur.InputPreampExt[i]) > 0.05f)
                changes.Add($"Input {i + 3} preamp: {FormatDb(old.InputPreampExt[i])} → {FormatDb(cur.InputPreampExt[i])}");
        // Master volume only participates in preset dirty state when the firmware
        // is configured to store it with each preset. In independent mode it is
        // managed separately via "Save Master Volume".
        if (vm.MasterVolumeMode == 1 &&
            Math.Abs(old.MasterVolumeDb - cur.MasterVolumeDb) > 0.05f)
        {
            string FormatMv(float v) => v <= -127.5f ? "mute" : FormatDb(v);
            changes.Add($"Master volume: {FormatMv(old.MasterVolumeDb)} → {FormatMv(cur.MasterVolumeDb)}");
        }
        // User volume is unconditionally per-preset on V15+ firmware. 0.5 dB
        // threshold (vs. 0.05 elsewhere) absorbs the firmware's int-dB
        // quantization — the slot stores vol_index in whole-dB steps, so a
        // saved -12.0 reloads as exactly -12.0; we only care about real
        // user-driven moves, not sub-dB ripples from notification echoes.
        if (Math.Abs(old.UserVolumeDb - cur.UserVolumeDb) > 0.5f)
            changes.Add($"User volume: {FormatDb(old.UserVolumeDb)} → {FormatDb(cur.UserVolumeDb)}");
        if (old.Bypass != cur.Bypass)
            changes.Add($"Master EQ bypass: {(old.Bypass ? "on" : "off")} \u2192 {(cur.Bypass ? "on" : "off")}");

        // Loudness
        if (old.LoudnessEnabled != cur.LoudnessEnabled)
            changes.Add($"Loudness: {(cur.LoudnessEnabled ? "enabled" : "disabled")}");
        if (Math.Abs(old.LoudnessRefSpl - cur.LoudnessRefSpl) > 0.05f)
            changes.Add($"Loudness ref SPL: {FormatVal(old.LoudnessRefSpl)} \u2192 {FormatVal(cur.LoudnessRefSpl)}");
        if (Math.Abs(old.LoudnessIntensity - cur.LoudnessIntensity) > 0.05f)
            changes.Add($"Loudness intensity: {FormatVal(old.LoudnessIntensity)}% \u2192 {FormatVal(cur.LoudnessIntensity)}%");
        if (old.LoudnessOutputMask != cur.LoudnessOutputMask)
            changes.Add($"Loudness outputs: {MaskList(old.LoudnessOutputMask)} \u2192 {MaskList(cur.LoudnessOutputMask)}");

        // Crossfeed
        if (old.CrossfeedEnabled != cur.CrossfeedEnabled)
            changes.Add($"Crossfeed: {(cur.CrossfeedEnabled ? "enabled" : "disabled")}");
        if (old.CrossfeedPreset != cur.CrossfeedPreset)
            changes.Add($"Crossfeed preset: {old.CrossfeedPreset} \u2192 {cur.CrossfeedPreset}");
        if (Math.Abs(old.CrossfeedFreq - cur.CrossfeedFreq) > 0.5f)
            changes.Add($"Crossfeed frequency: {FormatVal(old.CrossfeedFreq)} \u2192 {FormatVal(cur.CrossfeedFreq)} Hz");
        if (Math.Abs(old.CrossfeedFeed - cur.CrossfeedFeed) > 0.05f)
            changes.Add($"Crossfeed feed: {FormatDb(old.CrossfeedFeed)} \u2192 {FormatDb(cur.CrossfeedFeed)}");
        if (old.CrossfeedItd != cur.CrossfeedItd)
            changes.Add($"Crossfeed ITD: {(cur.CrossfeedItd ? "enabled" : "disabled")}");
        if (old.CrossfeedOutputPairMask != cur.CrossfeedOutputPairMask)
            changes.Add($"Crossfeed pairs: {MaskList(old.CrossfeedOutputPairMask)} → {MaskList(cur.CrossfeedOutputPairMask)}");

        // Volume leveller detector / apply channel masks
        if (old.LevellerDetectorMask != cur.LevellerDetectorMask)
            changes.Add($"Leveller detector: {MaskList(old.LevellerDetectorMask)} → {MaskList(cur.LevellerDetectorMask)}");
        if (old.LevellerApplyMask != cur.LevellerApplyMask)
            changes.Add($"Leveller apply: {MaskList(old.LevellerApplyMask)} → {MaskList(cur.LevellerApplyMask)}");

        // Volume leveller
        if (old.LevellerEnabled != cur.LevellerEnabled)
            changes.Add($"Volume leveller: {(cur.LevellerEnabled ? "enabled" : "disabled")}");
        int levellerChanges = Count(
            Differs(old.LevellerAmount, cur.LevellerAmount),
            old.LevellerSpeed != cur.LevellerSpeed,
            Differs(old.LevellerMaxGainDb, cur.LevellerMaxGainDb),
            old.LevellerLookahead != cur.LevellerLookahead,
            Differs(old.LevellerGateDb, cur.LevellerGateDb));
        if (levellerChanges > 0)
            changes.Add(Plural(levellerChanges, "volume leveller setting") + " changed");

        // Psychoacoustic bass
        if (old.PsybassEnabled != cur.PsybassEnabled)
            changes.Add($"Psychoacoustic bass: {(cur.PsybassEnabled ? "enabled" : "disabled")}");
        if (old.PsybassOutputMask != cur.PsybassOutputMask)
            changes.Add($"Psychoacoustic bass outputs: {MaskList(old.PsybassOutputMask)} → {MaskList(cur.PsybassOutputMask)}");
        int psybassChanges = Count(
            Differs(old.PsybassCutoffHz, cur.PsybassCutoffHz),
            Differs(old.PsybassHarmonicsDb, cur.PsybassHarmonicsDb),
            Differs(old.PsybassDriveDb, cur.PsybassDriveDb),
            Differs(old.PsybassCharacterPct, cur.PsybassCharacterPct),
            Differs(old.PsybassOriginalDb, cur.PsybassOriginalDb));
        if (psybassChanges > 0)
            changes.Add(Plural(psybassChanges, "psychoacoustic bass setting") + " changed");

        // Subharmonic synthesizer
        if (old.SubharmEnabled != cur.SubharmEnabled)
            changes.Add($"Subharmonic synth: {(cur.SubharmEnabled ? "enabled" : "disabled")}");
        if (old.SubharmOutputMask != cur.SubharmOutputMask)
            changes.Add($"Subharmonic synth outputs: {MaskList(old.SubharmOutputMask)} → {MaskList(cur.SubharmOutputMask)}");
        int subharmChanges = Count(
            Differs(old.SubharmLowDb, cur.SubharmLowDb),
            Differs(old.SubharmHighDb, cur.SubharmHighDb),
            Differs(old.SubharmTopDb, cur.SubharmTopDb),
            Differs(old.SubharmBoostDb, cur.SubharmBoostDb),
            old.SubharmSelectMode != cur.SubharmSelectMode,
            Differs(old.SubharmSelectDepthPct, cur.SubharmSelectDepthPct),
            Differs(old.SubharmSelectHoldMs, cur.SubharmSelectHoldMs),
            Differs(old.SubharmCeilingDb, cur.SubharmCeilingDb),
            old.SubharmLinkPairs != cur.SubharmLinkPairs);
        if (subharmChanges > 0)
            changes.Add(Plural(subharmChanges, "subharmonic synth setting") + " changed");

        // Tube modeller
        if (old.TubeEnabled != cur.TubeEnabled)
            changes.Add($"Tube modeller: {(cur.TubeEnabled ? "enabled" : "disabled")}");
        if (old.TubeOutputMask != cur.TubeOutputMask)
            changes.Add($"Tube modeller outputs: {MaskList(old.TubeOutputMask)} → {MaskList(cur.TubeOutputMask)}");
        if (old.TubeType != cur.TubeType)
            changes.Add($"Tube type: {TubeTables.TypeName(old.TubeType)} → {TubeTables.TypeName(cur.TubeType)}");
        int tubeChanges = Count(
            Differs(old.TubeDriveDb, cur.TubeDriveDb),
            Differs(old.TubeBiasPct, cur.TubeBiasPct),
            Differs(old.TubeAsymDb, cur.TubeAsymDb),
            Differs(old.TubeHardnessPct, cur.TubeHardnessPct),
            Differs(old.TubeSagPct, cur.TubeSagPct),
            old.TubeRectifier != cur.TubeRectifier,
            old.TubeXfmrEnabled != cur.TubeXfmrEnabled,
            Differs(old.TubeXfmrDamping, cur.TubeXfmrDamping),
            Differs(old.TubeXfmrResHz, cur.TubeXfmrResHz),
            Differs(old.TubeMixPct, cur.TubeMixPct),
            Differs(old.TubeTrimDb, cur.TubeTrimDb));
        if (tubeChanges > 0)
            changes.Add(Plural(tubeChanges, "tube modeller setting") + " changed");

        // Stereo upmixer
        if (old.Upmix != null && cur.Upmix != null)
        {
            var (ou, cu) = (old.Upmix, cur.Upmix);
            if (ou.Enabled != cu.Enabled)
                changes.Add($"Upmixer: {(cu.Enabled ? "enabled" : "disabled")}");
            // Product mode names: PASSIVE = Sinner, ADAPTIVE = Logician.
            string ModeC(byte m) => m == 0 ? "Sinner" : m == 1 ? "Logician" : "Off";
            string ModeS(byte m) => m == 0 ? "Off" : m == 1 ? "Sinner" : "Logician";
            if (ou.CenterMode != cu.CenterMode)
                changes.Add($"Upmixer centre mode: {ModeC(ou.CenterMode)} → {ModeC(cu.CenterMode)}");
            if (ou.SurroundMode != cu.SurroundMode)
                changes.Add($"Upmixer surround mode: {ModeS(ou.SurroundMode)} → {ModeS(cu.SurroundMode)}");
            int upmixParamChanges = 0;
            void CmpF(float a, float b) { if (Math.Abs(a - b) > 0.05f) upmixParamChanges++; }
            CmpF(ou.StrengthPct, cu.StrengthPct);
            CmpF(ou.CenterWidthPct, cu.CenterWidthPct);
            CmpF(ou.CorrThresholdPct, cu.CorrThresholdPct);
            CmpF(ou.AttackMs, cu.AttackMs);
            CmpF(ou.ReleaseMs, cu.ReleaseMs);
            CmpF(ou.DetectorHpfHz, cu.DetectorHpfHz);
            CmpF(ou.SurroundDelayMs, cu.SurroundDelayMs);
            CmpF(ou.SurroundHpfHz, cu.SurroundHpfHz);
            CmpF(ou.SurroundLpfHz, cu.SurroundLpfHz);
            CmpF(ou.DecorrPct, cu.DecorrPct);
            CmpF(ou.PresenceDb, cu.PresenceDb);
            if (upmixParamChanges > 0)
                changes.Add($"{upmixParamChanges} upmixer parameter{(upmixParamChanges == 1 ? "" : "s")} changed");
        }

        // Channel delays
        foreach (var ch in Channel.All)
        {
            int id = (int)ch.Id;
            float oldD = old.Delays.TryGetValue(id, out var od) ? od : 0;
            float curD = cur.Delays.TryGetValue(id, out var cd) ? cd : 0;
            if (Math.Abs(oldD - curD) > 0.00005f)
            {
                var name = vm.GetChannelName(ch);
                changes.Add($"{name} delay: {FormatVal(oldD)} ms \u2192 {FormatVal(curD)} ms");
            }
        }

        // Input delays (an output's delay is covered above).
        foreach (var ch in Channel.All)
        {
            if (ch.IsOutput) continue;
            int id = (int)ch.Id;
            float a = old.InputDelays.TryGetValue(id, out var od2) ? od2 : 0;
            float b = cur.InputDelays.TryGetValue(id, out var cd2) ? cd2 : 0;
            if (Math.Abs(a - b) > 0.00005f)
                changes.Add($"{vm.GetChannelName(ch)} delay: {FormatVal(a)} ms \u2192 {FormatVal(b)} ms");
        }

        // Matrix crosspoints
        var outputs = vm.ActiveOutputs;
        int crosspointChanges = 0;
        for (int inp = 0; inp < MainViewModel.MatrixMaxInputs; inp++)
        {
            for (int o = 0; o < outputs.Count && o < 9; o++)
            {
                if (old.MatrixRouting[inp, o] != cur.MatrixRouting[inp, o] ||
                    old.MatrixInvert[inp, o] != cur.MatrixInvert[inp, o] ||
                    Math.Abs(old.MatrixGain[inp, o] - cur.MatrixGain[inp, o]) > 0.005f)
                    crosspointChanges++;
            }
        }
        if (crosspointChanges > 0)
            changes.Add($"{crosspointChanges} crosspoint{(crosspointChanges == 1 ? "" : "s")} changed");

        // Output settings (enabled, muted, gain)
        for (int o = 0; o < outputs.Count && o < 9; o++)
        {
            var name = vm.GetChannelName(outputs[o]);
            bool oldEn = old.OutputEnabled.TryGetValue(o, out var oe) && oe;
            bool curEn = cur.OutputEnabled.TryGetValue(o, out var ce) && ce;
            if (oldEn != curEn)
                changes.Add($"{name}: {(curEn ? "enabled" : "disabled")}");
            if (old.OutputMuted[o] != cur.OutputMuted[o])
                changes.Add($"{name}: {(cur.OutputMuted[o] ? "muted" : "unmuted")}");

            int chId = (int)outputs[o].Id;
            float oldG = old.OutputGains.TryGetValue(chId, out var og) ? og : 0;
            float curG = cur.OutputGains.TryGetValue(chId, out var cg) ? cg : 0;
            if (Math.Abs(oldG - curG) > 0.005f)
                changes.Add($"{name} gain: {FormatDb(oldG)} \u2192 {FormatDb(curG)}");
        }

        // EQ bands per channel
        foreach (var ch in Channel.All)
        {
            int id = (int)ch.Id;
            int bandChanges = 0;
            for (int b = 0; b < ch.BandCount && b < 12; b++)
            {
                var oldF = old.Eq[id, b];
                var curF = cur.Eq[id, b];
                if (oldF == null && curF == null) continue;
                if (oldF == null || curF == null || !oldF.Equals(curF))
                    bandChanges++;
            }
            if (bandChanges > 0)
            {
                var name = vm.GetChannelName(ch);
                changes.Add($"{bandChanges} band{(bandChanges == 1 ? "" : "s")} changed on {name}");
            }
        }

        // Crossover bands per output channel
        foreach (var ch in Channel.All)
        {
            if (!ch.IsOutput) continue;
            int id = (int)ch.Id;
            int xChanges = 0;
            for (int b = 0; b < 4; b++)
            {
                var oldF = old.Xover[id, b];
                var curF = cur.Xover[id, b];
                if (oldF == null && curF == null) continue;
                if (oldF == null || curF == null || !oldF.Equals(curF))
                    xChanges++;
            }
            if (xChanges > 0)
            {
                var name = vm.GetChannelName(ch);
                changes.Add($"{xChanges} crossover band{(xChanges == 1 ? "" : "s")} changed on {name}");
            }
        }

        // Channel names
        foreach (var ch in Channel.All)
        {
            int id = (int)ch.Id;
            string oldName = old.ChannelNames.TryGetValue(id, out var on) ? on : ch.Name;
            string curName = cur.ChannelNames.TryGetValue(id, out var cn) ? cn : ch.Name;
            if (oldName != curName)
                changes.Add($"{oldName} \u2192 {curName}");
        }

        // Physical IO block (output pins/types, I2S MCK/BCK, SPDIF/I2S RX pins,
        // SPDIF instances, I2S channel count). Participates in preset dirty only
        // in with-preset mode; in independent mode it's device-global and saved
        // via "Save Output Config" (surfaced separately as OutputConfigDirty).
        if (vm.OutputConfigMode == 1)
            changes.AddRange(IoBlockDiff(old, cur, vm));

        // Input source (USB / SPDIF / I2S) is NOT part of the IO block — it stays
        // per-preset in both modes, as it's a listening choice not wiring.
        if (old.InputSource != cur.InputSource)
        {
            string Name(InputSource s) => s switch
            {
                InputSource.Spdif => "S/PDIF",
                InputSource.Spdif2 => "S/PDIF 2",
                InputSource.Spdif3 => "S/PDIF 3",
                InputSource.Spdif4 => "S/PDIF 4",
                InputSource.I2s => "I2S",
                InputSource.Adat => "ADAT",
                _ => "USB"
            };
            changes.Add($"Input source: {Name(old.InputSource)} → {Name(cur.InputSource)}");
        }

        // LG Sound Sync
        if (old.LgSoundSyncEnabled != cur.LgSoundSyncEnabled)
            changes.Add($"LG Sound Sync: {(cur.LgSoundSyncEnabled ? "enabled" : "disabled")}");

        return changes;
    }

    /// <summary>One changed physical-IO field: a stable key (for tracker
    /// dedup), a human label, and the before/after display strings.</summary>
    public readonly record struct IoChange(string Key, string Label, string Old, string New);

    /// <summary>
    /// Per-field diff of the physical IO block — the fields whose persistence
    /// follows output_config_mode. One entry per changed field (not aggregated)
    /// so callers can surface an accurate device-level change count. Keys are
    /// prefixed "io." and are stable per field.
    /// </summary>
    public static List<IoChange> IoBlockChanges(PresetSnapshot old, PresetSnapshot cur, MainViewModel vm)
    {
        var changes = new List<IoChange>();

        // Output GPIO pin assignments (one entry per pin-output slot)
        foreach (var key in old.OutputPins.Keys)
        {
            byte oldPin = old.OutputPins[key];
            byte curPin = cur.OutputPins.TryGetValue(key, out var cp) ? cp : (byte)0;
            if (oldPin != curPin)
                changes.Add(new($"io.pin.{key}", $"Output {key + 1} GPIO", $"GPIO {oldPin}", $"GPIO {curPin}"));
        }

        // Output slot types (SPDIF/I2S)
        for (int s = 0; s < old.OutputSlotTypes.Length; s++)
            if (old.OutputSlotTypes[s] != cur.OutputSlotTypes[s])
                changes.Add(new($"io.slot.{s}", $"Output {s + 1} type",
                    old.OutputSlotTypes[s] == 1 ? "I2S" : "S/PDIF",
                    cur.OutputSlotTypes[s] == 1 ? "I2S" : "S/PDIF"));

        // I2S output clock config
        if (old.I2SBckPin != cur.I2SBckPin)
            changes.Add(new("io.bck", "I2S BCK pin", $"GPIO {old.I2SBckPin}", $"GPIO {cur.I2SBckPin}"));
        if (old.MckEnabled != cur.MckEnabled)
            changes.Add(new("io.mck-en", "MCK", old.MckEnabled ? "enabled" : "disabled", cur.MckEnabled ? "enabled" : "disabled"));
        if (old.MckPin != cur.MckPin)
            changes.Add(new("io.mck-pin", "MCK pin", $"GPIO {old.MckPin}", $"GPIO {cur.MckPin}"));
        if (old.MckMultiplier != cur.MckMultiplier)
            changes.Add(new("io.mck-mult", "MCK multiplier", $"{old.MckMultiplier}x", $"{cur.MckMultiplier}x"));
        if (old.I2sClockMode != cur.I2sClockMode)
            changes.Add(new("io.i2s-clock", "I2S clock", old.I2sClockMode == 1 ? "slave" : "master", cur.I2sClockMode == 1 ? "slave" : "master"));
        if (old.I2sClockPinMode != cur.I2sClockPinMode)
            changes.Add(new("io.i2s-clock-pins", "I2S clock pins", old.I2sClockPinMode == 1 ? "split" : "unified", cur.I2sClockPinMode == 1 ? "split" : "unified"));
        if (old.I2sBckPinSlave != cur.I2sBckPinSlave)
            changes.Add(new("io.bck-slave", "I2S slave BCK pin", $"GPIO {old.I2sBckPinSlave}", $"GPIO {cur.I2sBckPinSlave}"));

        // S/PDIF RX pins + instance enable
        if (old.SpdifRxPin != cur.SpdifRxPin)
            changes.Add(new("io.spdif-rx.0", "S/PDIF RX pin", $"GPIO {old.SpdifRxPin}", $"GPIO {cur.SpdifRxPin}"));
        if (old.SpdifEnabledExt != cur.SpdifEnabledExt)
            changes.Add(new("io.spdif-inst", "S/PDIF inputs", $"{SpdifInputCount(old.SpdifEnabledExt)}", $"{SpdifInputCount(cur.SpdifEnabledExt)}"));
        for (int i = 0; i < Math.Min(old.SpdifRxPinsExt.Length, cur.SpdifRxPinsExt.Length); i++)
            if (old.SpdifRxPinsExt[i] != cur.SpdifRxPinsExt[i])
                changes.Add(new($"io.spdif-rx.{i + 1}", $"S/PDIF {i + 2} RX pin", $"GPIO {old.SpdifRxPinsExt[i]}", $"GPIO {cur.SpdifRxPinsExt[i]}"));

        // I2S input: data pins, channel count, master rate
        if (old.I2sRxPin != cur.I2sRxPin)
            changes.Add(new("io.i2s-rx.0", "I2S RX pin", $"GPIO {old.I2sRxPin}", $"GPIO {cur.I2sRxPin}"));
        if (old.I2sInputChannels != cur.I2sInputChannels)
            changes.Add(new("io.i2s-ch", "I2S input channels", $"{old.I2sInputChannels}", $"{cur.I2sInputChannels}"));
        for (int i = 0; i < 3; i++)
            if (old.I2sRxPinsExt[i] != cur.I2sRxPinsExt[i])
                changes.Add(new($"io.i2s-rx.{i + 1}", $"I2S Serial Data {i + 2} pin", $"GPIO {old.I2sRxPinsExt[i]}", $"GPIO {cur.I2sRxPinsExt[i]}"));
        if (old.I2sInputRateHz != cur.I2sInputRateHz)
            changes.Add(new("io.i2s-rate", "I2S sample rate", $"{old.I2sInputRateHz / 1000.0:0.#} kHz", $"{cur.I2sInputRateHz / 1000.0:0.#} kHz"));

        // ADAT bulk output
        if (old.AdatEnabled != cur.AdatEnabled)
            changes.Add(new("io.adat-en", "ADAT output", old.AdatEnabled ? "enabled" : "disabled", cur.AdatEnabled ? "enabled" : "disabled"));
        if (old.AdatPin != cur.AdatPin)
            changes.Add(new("io.adat-pin", "ADAT pin", $"GPIO {old.AdatPin}", $"GPIO {cur.AdatPin}"));

        // Output limiter, per output
        var limiterOutputs = vm.ActiveOutputs;
        for (int o = 0; o < Math.Min(old.Limiter.Length, cur.Limiter.Length) && o < limiterOutputs.Count; o++)
        {
            var (a, b) = (old.Limiter[o], cur.Limiter[o]);
            if (a == b) continue;
            string Describe(LimiterOutputSettings s) =>
                (s.Enabled ? $"on, {s.ThresholdDb:0.#} dB, {s.ReleaseMs:0} ms" : "off")
                + (s.LinkGroup != 0 ? $", {LimiterLimits.LinkGroupName(s.LinkGroup).ToLowerInvariant()}" : "");
            changes.Add(new($"io.limiter.{o}", $"{vm.GetChannelName(limiterOutputs[o])} limiter", Describe(a), Describe(b)));
        }

        // ADAT optical input
        if (old.AdatInputEnabled != cur.AdatInputEnabled)
            changes.Add(new("io.adat-in-en", "ADAT input", old.AdatInputEnabled ? "enabled" : "disabled", cur.AdatInputEnabled ? "enabled" : "disabled"));
        if (old.AdatInputPin != cur.AdatInputPin)
            changes.Add(new("io.adat-in-pin", "ADAT input pin", $"GPIO {old.AdatInputPin}", $"GPIO {cur.AdatInputPin}"));
        if (old.AdatInputClockMode != cur.AdatInputClockMode)
            changes.Add(new("io.adat-in-clock", "ADAT input clock", old.AdatInputClockMode == 1 ? "slave" : "master", cur.AdatInputClockMode == 1 ? "slave" : "master"));

        return changes;
    }

    /// <summary>IO-block diff as human-readable summary lines (for the preset
    /// change summary). Derived from <see cref="IoBlockChanges"/>.</summary>
    public static List<string> IoBlockDiff(PresetSnapshot old, PresetSnapshot cur, MainViewModel vm) =>
        IoBlockChanges(old, cur, vm).ConvertAll(c => $"{c.Label}: {c.Old} → {c.New}");

    /// <summary>
    /// Format a list of changes as a bullet-point summary string.
    /// </summary>
    public static string FormatSummary(List<string> changes)
    {
        if (changes.Count == 0)
            return "No changes detected.";

        var sb = new StringBuilder();
        int shown = Math.Min(changes.Count, MaxLines);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append("\u2022 ");
            sb.Append(changes[i]);
        }

        int remaining = changes.Count - shown;
        if (remaining > 0)
        {
            sb.AppendLine();
            sb.Append($"...and {remaining} more change{(remaining == 1 ? "" : "s")}");
        }

        return sb.ToString();
    }
}
