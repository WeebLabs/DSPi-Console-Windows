using System.Text;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Usb;

/// <summary>
/// Parsed result from a bulk parameter fetch (REQ_GET_ALL_PARAMS, 0xA0 /
/// chunked 0xA2), wire formats V16 to V32 (unified channel model).
///
/// V16 broke bulk-params backward compatibility with no migration: inputs are
/// first-class channels (no "master"), and the channel index space is
/// [ inputs 0..NumInputChannels-1 ][ outputs NumInputChannels..NumChannels-1 ].
///
/// Every later version only reinterpreted reserved bytes in place or appended
/// a section, so every offset of an older image is unchanged in a newer one:
/// V21 I2S clock mode, V22 Linkwitz Transform Q in the EQ reserved bytes, V23
/// psybass (→ 5900), V24 ADAT input, V25/V26 upmixer (→ 5944), V28 a fourth
/// S/PDIF input inside the input-config section, V29 subharm (→ 5960), V30 its
/// extension (→ 5980), V31 tube (→ 6028) and V32 the output limiter (→ 6136).
/// Each appended section is parsed only when the version and the length both
/// say it is there, and flagged with its Has* field.
/// </summary>
public class BulkParams
{
    // ── Header (offset 0, 16 bytes) ──
    public byte FormatVersion;
    public byte PlatformId;         // 0=RP2040, 1=RP2350
    public byte NumChannels;        // total valid channels (7 on RP2040, 17 on RP2350)
    public byte NumOutputChannels;  // valid outputs (5 or 9)
    public byte NumInputChannels;   // valid inputs (2 or 8)
    public byte MaxBands;           // PEQ bands per channel (12)
    public ushort PayloadLength;    // total packet size including header
    public ushort FwVersionMajor;
    public ushort FwVersionMinor;

    // ── Global (offset 16, 16 bytes) ──
    public float PreampGainDb;
    public bool Bypass;
    public bool LoudnessEnabled;
    public ushort LoudnessOutputMask;  // bit k = loudness processes output channel k (V19+)
    public float LoudnessRefSpl;
    public float LoudnessIntensityPct;

    // ── Crossfeed (offset 32, 16 bytes) ──
    public bool CrossfeedEnabled;
    public byte CrossfeedPreset;
    public bool CrossfeedItd;
    public byte CrossfeedOutputPairMask;  // bit p = crossfeed runs on output pair p (V20+)
    public float CrossfeedFreq;
    public float CrossfeedFeedDb;

    // ── Delays (offset 64, 68 bytes) — float[17] ──
    public float[] Delays = Array.Empty<float>();

    // ── Matrix crosspoints (offset 132, 576 bytes) — [input 0..7, output 0..8] ──
    public (bool enabled, bool invert, float gain)[,] Crosspoints =
        new (bool, bool, float)[BulkParamsParser.WireMaxInputChannels, BulkParamsParser.WireMaxOutputChannels];

    // ── Outputs (offset 708, 108 bytes) — 9 × 12 bytes, output-relative ──
    public (bool enabled, bool muted, float gain, float delay)[] Outputs =
        new (bool, bool, float, float)[BulkParamsParser.WireMaxOutputChannels];

    // ── Pin config (offset 816, 8 bytes) ──
    public byte NumPinOutputs;
    public byte[] Pins = Array.Empty<byte>();

    // ── EQ bands (offset 824, 3264 bytes) — [channel 0..16, band 0..11] ──
    public FilterParams[,] Eq =
        new FilterParams[BulkParamsParser.WireMaxChannels, BulkParamsParser.WireMaxBands];

    // ── Channel names (offset 4088, 544 bytes) — 17 × 32-char strings ──
    public string[] ChannelNames = Array.Empty<string>();

    // ── I2S output config (offset 4632, 16 bytes) ──
    public byte[] OutputSlotTypes = new byte[4]; // per-slot: 0=S/PDIF, 1=I2S
    public byte BckPin;
    public byte MckPin;
    public bool MckEnabled;
    public byte MckMultiplierEncoded; // 0=128x, 1=256x
    public bool HasI2SConfig;

    // I2S clock-pin mode (unified/split BCK+LRCLK pairs). Claimed from I2S-config
    // reserved bytes without a wire bump: clock_pin_mode_p1 is +1-encoded
    // (0=absent, 1=unified, 2=split); bck_pin_slave is the slave-pair BCK GPIO
    // (LRCLK = +1). Present only when clock_pin_mode_p1 != 0.
    public byte I2sClockPinMode;       // 0=unified, 1=split (decoded)
    public byte I2sBckPinSlave;        // slave-pair BCK GPIO
    public bool HasI2sClockPinMode;

    // ── Volume leveller (offset 4648, 20 bytes) ──
    public bool LevellerEnabled;
    public byte LevellerSpeed;       // 0=Slow, 1=Medium, 2=Fast
    public bool LevellerLookahead;
    public float LevellerAmount;     // 0-100
    public float LevellerMaxGainDb;  // 0-35
    public float LevellerGateDb;     // -96 to 0
    public byte LevellerDetectorMask; // bit k = input channel k feeds the detector (V18+)
    public byte LevellerApplyMask;    // bit k = gain applied to input channel k (V18+)
    public bool HasLevellerConfig;

    // ── Per-channel preamp (offset 4668, 32 bytes) — float[8], per input channel ──
    public float[] Preamp = Array.Empty<float>();
    public float PreampLDb;          // convenience: Preamp[0]
    public float PreampRDb;          // convenience: Preamp[1]
    public bool HasPerChannelPreamp;

    // ── Master volume (offset 4700, 16 bytes) ──
    public float MasterVolumeDb;
    public bool HasMasterVolume;

    // ── Input source config (offset 4716, 16 bytes) ──
    public byte InputSource;         // 0 = USB, 1 = S/PDIF, 2 = I2S
    public byte SpdifRxPin;          // primary SPDIF RX GPIO
    public bool HasInputConfig;

    // I2S input fields within WireInputConfig — I2sInputRateEncoded: 0=44100, 1=48000, 2=96000.
    public byte I2sRxPin;            // I2S input data GPIO, stereo pair 0
    public byte I2sInputRateEncoded; // 0=44100, 1=48000, 2=96000 (NOT Hz)
    public byte I2sInputChannels;    // active I2S input channels: 2/4/6/8 (0 = absent)
    public byte[] I2sRxPinExt = new byte[3];   // I2S RX GPIOs for pairs 1..3
    public bool HasI2sInputConfig;

    // Optional SPDIF inputs 2/3 (stored enable-mask PLUS ONE on the wire).
    public byte[] SpdifRxPinExt = new byte[2];      // SPDIF RX 2/3/4 GPIOs (0 = absent); 3 entries at V28+
    public byte SpdifRxEnabledExt;                  // decoded enable mask (0 = all disabled)
    public bool HasSpdifExtInputs;

    // I2S clock master/slave mode (input-config byte, V21+). 0=master, 1=slave.
    public byte I2sClockMode;
    public bool HasI2sClockMode;

    // ADAT input (input-config bytes, V24+, RP2350). enable/clock are +1-encoded
    // on the wire (0=absent); decoded here. Pin is the raw RX GPIO (0xFF = unset).
    public byte AdatInputPin;
    public bool AdatInputEnabled;
    public byte AdatInputClockMode;   // 0=master, 1=slave
    public bool HasAdatInput;

    // ── LG Sound Sync (offset 4732, 16 bytes) ──
    public bool LgSoundSyncEnabled;
    public bool HasLgSoundSync;

    // ── User volume / vendor mute (offset 4748, 16 bytes) ──
    public float UserVolumeDb;
    public bool UserMute;
    public bool HasUserVolume;

    // ── External DAC hardware mute (offset 4764, 16 bytes) ──
    public DacHwMuteConfig? DacHwMute;
    public bool HasDacHwMute;

    // ── Crossover bands (offset 4780, 1088 bytes) — [channel 0..16, localBand 0..3] ──
    // localBand 0..3 maps to wire band index 20..23. Input rows are zeroed by
    // firmware (crossover is output-only).
    public FilterParams[,] Xover =
        new FilterParams[BulkParamsParser.WireMaxChannels, BulkParamsParser.WireMaxXoverBands];
    public bool HasCrossover;

    // ── ADAT output config (offset 5868, 8 bytes) — RP2350-only (V17+) ──
    public bool AdatEnabled;
    public byte AdatPin;
    public bool HasAdat;

    // ── Psychoacoustic bass (offset 5876, 24 bytes) — appended V23+ ──
    public bool PsybassEnabled;
    public ushort PsybassOutputMask;   // bit k = process output channel k
    public float PsybassCutoffHz;      // 30..300
    public float PsybassHarmonicsDb;   // -24..+12
    public float PsybassDriveDb;       // 0..18
    public float PsybassCharacterPct;  // 0..100
    public float PsybassOriginalDb;    // -60..0
    public bool HasPsybass;

    // ── Stereo upmixer (offset 5900, 44 bytes) — appended V25+, presence V26 ──
    // Byte-identical to the 0x4A/0x4B UpmixConfigPacket. On RP2040 the section
    // is present but zero.
    public UpmixConfig? Upmix;
    public bool HasUpmix;

    // ── Subharmonic synthesizer (offset 5944) — 16 bytes V29, 36 bytes V30+ ──
    // Solo is runtime-only and never on the wire.
    public bool SubharmEnabled;
    public ushort SubharmOutputMask;
    public float SubharmLowDb;
    public float SubharmHighDb;
    public float SubharmBoostDb;
    public bool HasSubharm;
    // V30 tail
    public float SubharmTopDb;
    public float SubharmSelectDepthPct;
    public float SubharmSelectHoldMs;
    public float SubharmCeilingDb;
    public byte SubharmSelectMode;
    public bool SubharmLinkPairs;
    public bool HasSubharmExtended;

    // ── Tube modeller (offset 5980, 48 bytes) — appended V31+ ──
    public bool TubeEnabled;
    public byte TubeType;
    public byte TubeRectifier;
    public bool TubeXfmrEnabled;
    public ushort TubeOutputMask;
    public float TubeDriveDb;
    public float TubeBiasPct;
    public float TubeAsymDb;
    public float TubeHardnessPct;
    public float TubeSagPct;
    public float TubeXfmrDamping;
    public float TubeXfmrResHz;
    public float TubeMixPct;
    public float TubeTrimDb;
    public bool HasTube;

    // ── Output limiter (offset 6028, 108 bytes) — appended V32+ ──
    // One entry per wire output slot (9); entries past NumOutputChannels are zero.
    public LimiterOutputSettings[] Limiter = Array.Empty<LimiterOutputSettings>();
    public bool HasLimiter;
}

/// <summary>
/// Parses the V20 bulk parameter packet (5876 bytes, 17-channel unified model).
/// The firmware only emits the full current layout, so this parser requires the
/// full size; older/truncated payloads are rejected (return null) rather than
/// mis-parsed. See firmware bulk_params.h for the authoritative struct.
/// </summary>
public static class BulkParamsParser
{
    // Wire-format maximums (must match firmware bulk_params.h WIRE_MAX_* defines).
    internal const int WireMaxInputChannels = 8;
    public const int WireMaxOutputChannels = 9;
    internal const int WireMaxChannels = 17;     // inputs + outputs
    internal const int WireMaxBands = 12;
    internal const int WireMaxXoverBands = 4;
    internal const int WireMaxPinOutputs = 5;
    internal const int WireNameLen = 32;
    internal const int WireBandSize = 16;        // sizeof(WireBandParams)

    public const int PacketSizeV20 = 5876;       // sizeof(WireBulkParams) at V20
    public const int PacketSizeV24 = 5900;       // sizeof(WireBulkParams) at V24 (+psybass)
    public const int PacketSizeV26 = 5944;       // sizeof(WireBulkParams) at V25-V28 (+upmix)
    public const int PacketSizeV29 = 5960;       // + 16-byte subharm section
    public const int PacketSizeV30 = 5980;       // subharm section grows to 36 bytes
    public const int PacketSizeV31 = 6028;       // + 48-byte tube section
    public const int PacketSizeV32 = 6136;       // + 108-byte limiter section
    public const byte MinFormatVersion = 16;     // unified channel model floor
    public const byte CurrentFormatVersion = 32; // V32: output limiter

    /// <summary>Number of spdif_rx_pin_ext entries in WireInputConfig for a given
    /// wire version. V28 grew it from 2 to 3 (S/PDIF 4), which shifts every field
    /// after it down one byte — the notify dispatch has to shift with it, so both
    /// sides read the layout from here rather than hard-coding offsets.</summary>
    public static int SpdifRxPinExtCount(int formatVersion) => formatVersion >= 28 ? 3 : 2;

    /// <summary>Offset within WireInputConfig of the first field after
    /// spdif_rx_pin_ext (the enable mask). Fields follow in order: enable mask,
    /// i2s_clock_mode, adat pin, adat enable, adat clock mode.</summary>
    public static int InputCfgTailOffset(int formatVersion) => 8 + SpdifRxPinExtCount(formatVersion);

    // Raw wire value of FILTER_LINKWITZ_TRANSFORM (FilterType.LinkwitzTransform).
    // Referenced by value here so band decoding is independent of the Core enum.
    private const byte WireFilterLinkwitzTransform = 11;

    // Section offsets (bytes) into WireBulkParams. Derived directly from the
    // struct member order + sizes in firmware bulk_params.h. Public so the notify
    // dispatch (DspDevice) and the VM's PARAM_CHANGED apply can range-match them.
    public const int OffsetHeader = 0;          // 16
    public const int OffsetGlobal = 16;         // 16
    public const int OffsetCrossfeed = 32;      // 16
    public const int OffsetLegacy = 48;         // 16 (ignored)
    public const int OffsetDelays = 64;         // 68  (17 × float)
    public const int OffsetCrosspoints = 132;   // 576 (8 × 9 × 8)
    public const int OffsetOutputs = 708;       // 108 (9 × 12)
    public const int OffsetPinConfig = 816;     // 8
    public const int OffsetEq = 824;            // 3264 (17 × 12 × 16)
    public const int OffsetChannelNames = 4088; // 544 (17 × 32)
    public const int OffsetI2S = 4632;          // 16
    public const int OffsetLeveller = 4648;     // 20
    public const int OffsetPreamp = 4668;       // 32 (8 × float)
    public const int OffsetMasterVol = 4700;    // 16
    public const int OffsetInputCfg = 4716;     // 16
    public const int OffsetLgSoundSync = 4732;  // 16
    public const int OffsetUserVolume = 4748;   // 16
    public const int OffsetDacHwMute = 4764;    // 16
    public const int OffsetCrossover = 4780;    // 1088 (17 × 4 × 16)
    public const int OffsetAdat = 5868;         // 8
    public const int OffsetPsybass = 5876;       // 24 (appended V23+)
    public const int OffsetUpmix = 5900;        // 44 (appended V25+; presence byte V26)
    public const int OffsetSubharm = 5944;      // 16 (V29), 36 (V30+)
    public const int OffsetTube = 5980;         // 48 (appended V31+)
    public const int OffsetLimiter = 6028;      // 108 (9 × 12, appended V32+)
    public const int SubharmSizeV29 = 16;
    public const int SubharmSizeV30 = 36;
    public const int TubeSize = 48;
    public const int LimiterSize = WireMaxOutputChannels * LimiterOutputSettings.WireSize;

    public static BulkParams? Parse(byte[] buffer)
    {
        if (buffer == null || buffer.Length < PacketSizeV20)
            return null;

        var p = new BulkParams();

        // ── Header (16 bytes) ──
        p.FormatVersion = buffer[OffsetHeader + 0];
        if (p.FormatVersion < MinFormatVersion)
            return null;

        p.PlatformId = buffer[OffsetHeader + 1];
        p.NumChannels = buffer[OffsetHeader + 2];
        p.NumOutputChannels = buffer[OffsetHeader + 3];
        p.NumInputChannels = buffer[OffsetHeader + 4];
        p.MaxBands = buffer[OffsetHeader + 5];
        p.PayloadLength = BitConverter.ToUInt16(buffer, OffsetHeader + 6);
        p.FwVersionMajor = BitConverter.ToUInt16(buffer, OffsetHeader + 8);
        p.FwVersionMinor = BitConverter.ToUInt16(buffer, OffsetHeader + 10);

        // ── Global (16 bytes) ──
        p.PreampGainDb = BitConverter.ToSingle(buffer, OffsetGlobal + 0);
        p.Bypass = buffer[OffsetGlobal + 4] != 0;
        p.LoudnessEnabled = buffer[OffsetGlobal + 5] != 0;
        p.LoudnessOutputMask = BitConverter.ToUInt16(buffer, OffsetGlobal + 6);
        p.LoudnessRefSpl = BitConverter.ToSingle(buffer, OffsetGlobal + 8);
        p.LoudnessIntensityPct = BitConverter.ToSingle(buffer, OffsetGlobal + 12);

        // ── Crossfeed (16 bytes) ──
        p.CrossfeedEnabled = buffer[OffsetCrossfeed + 0] != 0;
        p.CrossfeedPreset = buffer[OffsetCrossfeed + 1];
        p.CrossfeedItd = buffer[OffsetCrossfeed + 2] != 0;
        p.CrossfeedOutputPairMask = buffer[OffsetCrossfeed + 3];
        p.CrossfeedFreq = BitConverter.ToSingle(buffer, OffsetCrossfeed + 4);
        p.CrossfeedFeedDb = BitConverter.ToSingle(buffer, OffsetCrossfeed + 8);

        // ── Delays (68 bytes = 17 × float) ──
        p.Delays = new float[WireMaxChannels];
        for (int i = 0; i < WireMaxChannels; i++)
            p.Delays[i] = BitConverter.ToSingle(buffer, OffsetDelays + i * 4);

        // ── Crosspoints (576 bytes = 8 inputs × 9 outputs × 8 bytes) ──
        // Each crosspoint: enabled(1), phase_invert(1), reserved(2), gain(4)
        p.Crosspoints = new (bool, bool, float)[WireMaxInputChannels, WireMaxOutputChannels];
        for (int inp = 0; inp < WireMaxInputChannels; inp++)
        {
            for (int outp = 0; outp < WireMaxOutputChannels; outp++)
            {
                int off = OffsetCrosspoints + (inp * WireMaxOutputChannels + outp) * 8;
                bool enabled = buffer[off + 0] != 0;
                bool invert = buffer[off + 1] != 0;
                float gain = BitConverter.ToSingle(buffer, off + 4);
                p.Crosspoints[inp, outp] = (enabled, invert, gain);
            }
        }

        // ── Outputs (108 bytes = 9 × 12 bytes) ──
        // Each: enabled(1), mute(1), reserved(2), gain(4), delay(4)
        p.Outputs = new (bool, bool, float, float)[WireMaxOutputChannels];
        for (int o = 0; o < WireMaxOutputChannels; o++)
        {
            int off = OffsetOutputs + o * 12;
            bool enabled = buffer[off + 0] != 0;
            bool muted = buffer[off + 1] != 0;
            float gain = BitConverter.ToSingle(buffer, off + 4);
            float delay = BitConverter.ToSingle(buffer, off + 8);
            p.Outputs[o] = (enabled, muted, gain, delay);
        }

        // ── Pin config (8 bytes) ──
        p.NumPinOutputs = buffer[OffsetPinConfig + 0];
        p.Pins = new byte[WireMaxPinOutputs];
        for (int i = 0; i < WireMaxPinOutputs; i++)
            p.Pins[i] = buffer[OffsetPinConfig + 1 + i];

        // ── EQ bands (3264 bytes = 17 channels × 12 bands × 16 bytes) ──
        p.Eq = new FilterParams[WireMaxChannels, WireMaxBands];
        for (int ch = 0; ch < WireMaxChannels; ch++)
        {
            for (int band = 0; band < WireMaxBands; band++)
            {
                int off = OffsetEq + (ch * WireMaxBands + band) * WireBandSize;
                p.Eq[ch, band] = ParseBand(buffer, off);
            }
        }

        // ── Channel names (544 bytes = 17 × 32-char null-terminated strings) ──
        p.ChannelNames = new string[WireMaxChannels];
        for (int ch = 0; ch < WireMaxChannels; ch++)
        {
            int off = OffsetChannelNames + ch * WireNameLen;
            int len = 0;
            while (len < WireNameLen && buffer[off + len] != 0) len++;
            p.ChannelNames[ch] = Encoding.UTF8.GetString(buffer, off, len);
        }

        // ── I2S output config (16 bytes) ──
        p.HasI2SConfig = true;
        for (int i = 0; i < 4; i++)
            p.OutputSlotTypes[i] = buffer[OffsetI2S + i];
        p.BckPin = buffer[OffsetI2S + 4];
        p.MckPin = buffer[OffsetI2S + 5];
        p.MckEnabled = buffer[OffsetI2S + 6] != 0;
        p.MckMultiplierEncoded = buffer[OffsetI2S + 7];
        // Clock-pin mode (+1 encoded at +8) and slave-pair BCK GPIO (+9).
        byte clockPinModeP1 = buffer[OffsetI2S + 8];
        if (clockPinModeP1 != 0)
        {
            p.HasI2sClockPinMode = true;
            p.I2sClockPinMode = (byte)(clockPinModeP1 - 1); // 0=unified, 1=split
            p.I2sBckPinSlave = buffer[OffsetI2S + 9];
        }

        // ── Volume leveller (20 bytes) ──
        p.HasLevellerConfig = true;
        p.LevellerEnabled = buffer[OffsetLeveller + 0] != 0;
        p.LevellerSpeed = buffer[OffsetLeveller + 1];
        p.LevellerLookahead = buffer[OffsetLeveller + 2] != 0;
        p.LevellerAmount = BitConverter.ToSingle(buffer, OffsetLeveller + 4);
        p.LevellerMaxGainDb = BitConverter.ToSingle(buffer, OffsetLeveller + 8);
        p.LevellerGateDb = BitConverter.ToSingle(buffer, OffsetLeveller + 12);
        p.LevellerDetectorMask = buffer[OffsetLeveller + 16];
        p.LevellerApplyMask = buffer[OffsetLeveller + 17];

        // ── Per-channel preamp (32 bytes = 8 × float) ──
        p.HasPerChannelPreamp = true;
        p.Preamp = new float[WireMaxInputChannels];
        for (int i = 0; i < WireMaxInputChannels; i++)
            p.Preamp[i] = BitConverter.ToSingle(buffer, OffsetPreamp + i * 4);
        p.PreampLDb = p.Preamp[0];
        p.PreampRDb = p.Preamp.Length > 1 ? p.Preamp[1] : p.Preamp[0];

        // ── Master volume (16 bytes) ──
        p.HasMasterVolume = true;
        p.MasterVolumeDb = BitConverter.ToSingle(buffer, OffsetMasterVol + 0);

        // ── Input source config (16 bytes) ──
        // WireInputConfig: input_source(1), spdif_rx_pin(1), i2s_rx_pin(1),
        // i2s_input_rate(1), i2s_input_channels(1), i2s_rx_pin_ext[3],
        // spdif_rx_pin_ext[N], spdif_rx_enabled_ext_p1(1), i2s_clock_mode(1),
        // adat_input_pin(1), adat_input_enabled_p1(1), adat_clock_mode_p1(1).
        // V28 grew spdif_rx_pin_ext from 2 to 3 entries (S/PDIF 4), consuming the
        // section's last reserved byte and pushing every field after it down one.
        p.HasInputConfig = true;
        p.InputSource = buffer[OffsetInputCfg + 0];
        p.SpdifRxPin = buffer[OffsetInputCfg + 1];
        p.HasI2sInputConfig = true;
        p.I2sRxPin = buffer[OffsetInputCfg + 2];
        p.I2sInputRateEncoded = buffer[OffsetInputCfg + 3];
        p.I2sInputChannels = buffer[OffsetInputCfg + 4];
        p.I2sRxPinExt = new byte[3];
        for (int i = 0; i < 3; i++)
            p.I2sRxPinExt[i] = buffer[OffsetInputCfg + 5 + i];
        int spdifExtCount = SpdifRxPinExtCount(p.FormatVersion);
        p.SpdifRxPinExt = new byte[spdifExtCount];
        for (int i = 0; i < spdifExtCount; i++)
            p.SpdifRxPinExt[i] = buffer[OffsetInputCfg + 8 + i];
        // Everything below the pin array shifts with its length.
        int tail = OffsetInputCfg + InputCfgTailOffset(p.FormatVersion);
        // Enable mask is stored PLUS ONE (0 = absent/keep-live); decode by subtracting 1.
        byte spdifEnP1 = buffer[tail + 0];
        if (spdifEnP1 != 0)
        {
            p.HasSpdifExtInputs = true;
            p.SpdifRxEnabledExt = (byte)(spdifEnP1 - 1);
        }

        // I2S clock master/slave mode (V21+, plain 0/1).
        if (p.FormatVersion >= 21)
        {
            p.HasI2sClockMode = true;
            p.I2sClockMode = buffer[tail + 1];
        }

        // ADAT input (V24+, RP2350). pin (0xFF = unset), then enable and clock
        // mode, both +1-encoded so 0 = absent.
        if (p.FormatVersion >= 24)
        {
            p.HasAdatInput = true;
            p.AdatInputPin = buffer[tail + 2];
            byte adatEnP1 = buffer[tail + 3];
            p.AdatInputEnabled = adatEnP1 == 2;   // 1=disabled, 2=enabled
            byte adatClkP1 = buffer[tail + 4];
            p.AdatInputClockMode = (byte)(adatClkP1 == 2 ? 1 : 0); // 1=master, 2=slave
        }

        // ── LG Sound Sync (16 bytes) ──
        p.HasLgSoundSync = true;
        p.LgSoundSyncEnabled = buffer[OffsetLgSoundSync + 0] != 0;

        // ── User volume / vendor mute (16 bytes) ──
        p.HasUserVolume = true;
        p.UserVolumeDb = BitConverter.ToSingle(buffer, OffsetUserVolume + 0);
        p.UserMute = buffer[OffsetUserVolume + 4] != 0;

        // ── External DAC hardware mute (16 bytes) ──
        p.DacHwMute = DacHwMuteConfig.TryParse(buffer, OffsetDacHwMute);
        p.HasDacHwMute = p.DacHwMute != null;

        // ── Crossover bands (1088 bytes = 17 × 4 × 16) ──
        p.HasCrossover = true;
        p.Xover = new FilterParams[WireMaxChannels, WireMaxXoverBands];
        for (int ch = 0; ch < WireMaxChannels; ch++)
        {
            for (int band = 0; band < WireMaxXoverBands; band++)
            {
                int off = OffsetCrossover + (ch * WireMaxXoverBands + band) * WireBandSize;
                p.Xover[ch, band] = ParseBand(buffer, off);
            }
        }

        // ── ADAT output config (8 bytes) ──
        p.HasAdat = true;
        p.AdatEnabled = buffer[OffsetAdat + 0] != 0;
        p.AdatPin = buffer[OffsetAdat + 1];

        // ── Psychoacoustic bass (24 bytes, appended V23+) ──
        // Present only when the payload actually carries the tail section.
        if (buffer.Length >= PacketSizeV24)
        {
            p.HasPsybass = true;
            p.PsybassEnabled = buffer[OffsetPsybass + 0] != 0;
            p.PsybassOutputMask = BitConverter.ToUInt16(buffer, OffsetPsybass + 2);
            p.PsybassCutoffHz = BitConverter.ToSingle(buffer, OffsetPsybass + 4);
            p.PsybassHarmonicsDb = BitConverter.ToSingle(buffer, OffsetPsybass + 8);
            p.PsybassDriveDb = BitConverter.ToSingle(buffer, OffsetPsybass + 12);
            p.PsybassCharacterPct = BitConverter.ToSingle(buffer, OffsetPsybass + 16);
            p.PsybassOriginalDb = BitConverter.ToSingle(buffer, OffsetPsybass + 20);
        }

        // ── Stereo upmixer (44 bytes, appended V25+; presence byte claimed V26,
        //    centre-mode enum widened to 0-2 at V27 with no layout change) ──
        if (buffer.Length >= PacketSizeV26)
        {
            p.Upmix = UpmixConfig.FromBytes(buffer, OffsetUpmix);
            p.HasUpmix = p.Upmix != null;
        }

        // Each tail section is read only when both the version says it exists
        // and the payload carries it, so a short or older image never reads
        // the next section's bytes as this one.

        // ── Subharmonic synthesizer (V29: 16 bytes; V30: 36 bytes) ──
        if (p.FormatVersion >= 29 && buffer.Length >= PacketSizeV29)
        {
            int o = OffsetSubharm;
            p.HasSubharm = true;
            p.SubharmEnabled = buffer[o] != 0;
            p.SubharmOutputMask = BitConverter.ToUInt16(buffer, o + 2);
            p.SubharmLowDb = BitConverter.ToSingle(buffer, o + 4);
            p.SubharmHighDb = BitConverter.ToSingle(buffer, o + 8);
            p.SubharmBoostDb = BitConverter.ToSingle(buffer, o + 12);
            if (p.FormatVersion >= 30 && buffer.Length >= PacketSizeV30)
            {
                p.HasSubharmExtended = true;
                p.SubharmTopDb = BitConverter.ToSingle(buffer, o + 16);
                p.SubharmSelectDepthPct = BitConverter.ToSingle(buffer, o + 20);
                p.SubharmSelectHoldMs = BitConverter.ToSingle(buffer, o + 24);
                p.SubharmCeilingDb = BitConverter.ToSingle(buffer, o + 28);
                p.SubharmSelectMode = (byte)Math.Min((int)buffer[o + 32], SubharmSelectMode.Max);
                p.SubharmLinkPairs = buffer[o + 33] != 0;
            }
        }

        // ── Tube modeller (48 bytes, V31+) ──
        if (p.FormatVersion >= 31 && buffer.Length >= PacketSizeV31)
        {
            int o = OffsetTube;
            p.HasTube = true;
            p.TubeEnabled = buffer[o] != 0;
            p.TubeType = (byte)Math.Min((int)buffer[o + 1], TubeLimits.TypeMax);
            p.TubeRectifier = (byte)Math.Min((int)buffer[o + 2], TubeLimits.RectifierMax);
            p.TubeXfmrEnabled = buffer[o + 3] != 0;
            p.TubeOutputMask = BitConverter.ToUInt16(buffer, o + 4);
            p.TubeDriveDb = BitConverter.ToSingle(buffer, o + 8);
            p.TubeBiasPct = BitConverter.ToSingle(buffer, o + 12);
            p.TubeAsymDb = BitConverter.ToSingle(buffer, o + 16);
            p.TubeHardnessPct = BitConverter.ToSingle(buffer, o + 20);
            p.TubeSagPct = BitConverter.ToSingle(buffer, o + 24);
            p.TubeXfmrDamping = BitConverter.ToSingle(buffer, o + 28);
            p.TubeXfmrResHz = BitConverter.ToSingle(buffer, o + 32);
            p.TubeMixPct = BitConverter.ToSingle(buffer, o + 36);
            p.TubeTrimDb = BitConverter.ToSingle(buffer, o + 40);
        }

        // ── Output limiter (9 × 12 bytes, V32+) ──
        if (p.FormatVersion >= 32 && buffer.Length >= PacketSizeV32)
        {
            p.HasLimiter = true;
            p.Limiter = new LimiterOutputSettings[WireMaxOutputChannels];
            for (int k = 0; k < WireMaxOutputChannels; k++)
                p.Limiter[k] = LimiterOutputSettings.FromBytes(buffer, OffsetLimiter + k * LimiterOutputSettings.WireSize);
        }

        return p;
    }

    /// <summary>
    /// Parse a single 16-byte WireBandParams entry at the given buffer offset.
    /// Shared between the bulk-params path and the notify-endpoint path so
    /// both decode the wire layout identically (incl. the bypass byte at +1).
    /// </summary>
    internal static FilterParams ParseBand(byte[] buffer, int offset)
    {
        byte typeByte = buffer[offset + 0];
        var fp = new FilterParams
        {
            Type = (FilterType)typeByte,
            Bypass = buffer[offset + 1] == 1,
            Frequency = BitConverter.ToSingle(buffer, offset + 4),
            Q = BitConverter.ToSingle(buffer, offset + 8),
            Gain = BitConverter.ToSingle(buffer, offset + 12)
        };
        // Linkwitz Transform (type 11, wire V22+): the band's reserved bytes carry
        // the target Q as qp×512 (LE u16); 0 decodes to the 0.707 default.
        if (typeByte == WireFilterLinkwitzTransform)
        {
            ushort qpRaw = BitConverter.ToUInt16(buffer, offset + 2);
            fp.Qp = qpRaw == 0 ? FilterParams.DefaultQp : qpRaw / 512.0f;
        }
        return fp;
    }
}
