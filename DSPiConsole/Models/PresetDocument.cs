using System.Text.Json;
using System.Text.Json.Serialization;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Models;

/// <summary>
/// A complete DSP configuration saved to (or loaded from) a file. Mirrors the
/// state the firmware carries in a bulk-params packet (bulk_params.h) — i.e.
/// everything a preset slot holds — so a document round-trips a device rather
/// than just its EQ.
///
/// Filter types, masks and pin numbers are stored as their raw wire values, not
/// as enum names: those values are the firmware's own and stay stable across app
/// releases, whereas a renamed C# enum member would silently break old files.
/// </summary>
public sealed class PresetDocument
{
    /// <summary>Bumped when the layout changes incompatibly. Readers reject a
    /// document from the future rather than guessing at missing blocks.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public PresetDocumentMeta Meta { get; set; } = new();
    public PresetGlobalBlock Global { get; set; } = new();
    public PresetLoudnessBlock Loudness { get; set; } = new();
    public PresetCrossfeedBlock Crossfeed { get; set; } = new();
    public PresetLevellerBlock Leveller { get; set; } = new();

    /// <summary>Null when the source device had no psychoacoustic bass (pre-V23).</summary>
    public PresetPsybassBlock? Psybass { get; set; }

    /// <summary>Null when the source device had no upmixer (pre-V25 / RP2040).</summary>
    public PresetUpmixBlock? Upmix { get; set; }

    /// <summary>Null when the source device had no subharmonic synth (pre-V29).</summary>
    public PresetSubharmBlock? Subharm { get; set; }

    /// <summary>Null when the source device had no tube modeller (pre-V31).</summary>
    public PresetTubeBlock? Tube { get; set; }

    public List<PresetChannelBlock> Channels { get; set; } = new();
    public List<PresetCrosspointBlock> Matrix { get; set; } = new();

    /// <summary>Physical wiring. Applied only when the user opts in on import,
    /// since GPIO assignments belong to a board rather than to a listening setup.</summary>
    public PresetIoBlock Io { get; set; } = new();
}

/// <summary>Provenance. Informational only — nothing here gates the import,
/// but a mismatch is worth telling the user about.</summary>
public sealed class PresetDocumentMeta
{
    public string? Name { get; set; }
    public DateTimeOffset SavedUtc { get; set; }
    public string? AppVersion { get; set; }
    public string? Platform { get; set; }            // "RP2040" / "RP2350"
    public string? FirmwareVersion { get; set; }
    public int WireFormatVersion { get; set; }
    public int InputChannelCount { get; set; }
    public int OutputChannelCount { get; set; }

    /// <summary>The source device's persistence modes (0 = independent, 1 = with
    /// preset) for master volume and the output configuration. Informational,
    /// as on the macOS Console; null in files from before they were written.</summary>
    public int? MasterVolumeMode { get; set; }
    public int? OutputConfigMode { get; set; }
}

public sealed class PresetGlobalBlock
{
    /// <summary>Per-input preamp trim, indexed by wire input 0..7.</summary>
    public float[] InputPreampsDb { get; set; } = new float[8];

    public bool Bypass { get; set; }

    /// <summary>Master volume. Per-preset only when the device's master-volume
    /// mode says so; otherwise it is device-global and left alone on import.</summary>
    public float MasterVolumeDb { get; set; }

    /// <summary>The user/listening volume the firmware restores with a preset.</summary>
    public float UserVolumeDb { get; set; }

    /// <summary>Wire InputSource value (0=USB, 1=S/PDIF, 2=I2S, 3=ADAT, ...).
    /// A listening choice rather than wiring, so it travels with the audio
    /// settings and not with the IO block.</summary>
    public byte InputSource { get; set; }

    public bool LgSoundSyncEnabled { get; set; }

    /// <summary>Per-input-pair PEQ link state (pairs 0..3). App-side only — the
    /// firmware has no notion of it, but losing it on import would be surprising.</summary>
    public bool[] InputPairLinked { get; set; } = new bool[4];
}

public sealed class PresetLoudnessBlock
{
    public bool Enabled { get; set; }
    public float RefSpl { get; set; }
    public float IntensityPct { get; set; }
    public int OutputMask { get; set; }
}

public sealed class PresetCrossfeedBlock
{
    public bool Enabled { get; set; }
    public int Preset { get; set; }
    public float FreqHz { get; set; }
    public float FeedDb { get; set; }
    public bool Itd { get; set; }
    public int OutputPairMask { get; set; }
}

public sealed class PresetLevellerBlock
{
    public bool Enabled { get; set; }
    public int Speed { get; set; }          // 0=Slow, 1=Medium, 2=Fast
    public bool Lookahead { get; set; }
    public float AmountPct { get; set; }
    public float MaxGainDb { get; set; }
    public float GateDb { get; set; }
    public int DetectorMask { get; set; }
    public int ApplyMask { get; set; }
}

public sealed class PresetPsybassBlock
{
    public bool Enabled { get; set; }
    public float CutoffHz { get; set; }
    public float HarmonicsDb { get; set; }
    public float DriveDb { get; set; }
    public float CharacterPct { get; set; }
    public float OriginalDb { get; set; }
    public int OutputMask { get; set; }
}

/// <summary>Subharmonic synthesizer (V29, extended V30). Defaults are the
/// firmware's, so a block missing a field reads as a fresh device.</summary>
public sealed class PresetSubharmBlock
{
    public bool Enabled { get; set; }
    public float LowDb { get; set; } = SubharmLimits.DefaultLowDb;
    public float HighDb { get; set; } = SubharmLimits.DefaultHighDb;
    public float TopDb { get; set; } = SubharmLimits.DefaultTopDb;
    public float BoostDb { get; set; } = SubharmLimits.DefaultBoostDb;
    public int OutputMask { get; set; } = SubharmLimits.DefaultOutputMask;
    public int SelectMode { get; set; } = SubharmLimits.DefaultSelectMode;
    public float SelectDepthPct { get; set; } = SubharmLimits.DefaultDepthPct;
    public float SelectHoldMs { get; set; } = SubharmLimits.DefaultHoldMs;
    public float CeilingDb { get; set; } = SubharmLimits.DefaultCeilingDb;
    public bool LinkPairs { get; set; } = SubharmLimits.DefaultLinkPairs;
}

/// <summary>Tube modeller (V31). Defaults are the firmware's.</summary>
public sealed class PresetTubeBlock
{
    public bool Enabled { get; set; }
    public int OutputMask { get; set; } = TubeLimits.DefaultOutputMask;
    public int TubeType { get; set; } = TubeLimits.DefaultType;
    public float DriveDb { get; set; } = TubeLimits.DefaultDriveDb;
    public float BiasPct { get; set; } = TubeLimits.DefaultBiasPct;
    public float AsymDb { get; set; } = TubeLimits.DefaultAsymDb;
    public float HardnessPct { get; set; } = TubeLimits.DefaultHardnessPct;
    public float SagPct { get; set; } = TubeLimits.DefaultSagPct;
    public int Rectifier { get; set; } = TubeLimits.DefaultRectifier;
    public bool XfmrEnabled { get; set; } = TubeLimits.DefaultXfmrEnabled;
    public float XfmrDamping { get; set; } = TubeLimits.DefaultXfmrDamping;
    public float XfmrResHz { get; set; } = TubeLimits.DefaultXfmrResHz;
    public float MixPct { get; set; } = TubeLimits.DefaultMixPct;
    public float TrimDb { get; set; } = TubeLimits.DefaultTrimDb;
}

public sealed class PresetUpmixBlock
{
    public bool Enabled { get; set; }
    public int CenterMode { get; set; }
    public int SurroundMode { get; set; }
    public float StrengthPct { get; set; }
    public float CenterWidthPct { get; set; }
    public float ThresholdPct { get; set; }
    public float AttackMs { get; set; }
    public float ReleaseMs { get; set; }
    public float DetectorHpfHz { get; set; }
    public float SurroundDelayMs { get; set; }
    public float SurroundHpfHz { get; set; }
    public float SurroundLpfHz { get; set; }
    public float DecorrPct { get; set; }
    public float PresenceDb { get; set; }
}

/// <summary>
/// One channel's full state. <see cref="ChannelId"/> is the app channel id
/// (ChannelId enum), which is also what every persisted structure in the app is
/// keyed by; <see cref="Name"/> is carried alongside so a document is readable
/// and so a channel can be reported by name when it can't be applied.
/// </summary>
public sealed class PresetChannelBlock
{
    public int ChannelId { get; set; }
    public string Name { get; set; } = "";
    public bool IsOutput { get; set; }

    /// <summary>The wire (unified EQ) channel, and the input or output index
    /// behind it: the macOS Console's own numbering. Read in preference to
    /// <see cref="ChannelId"/> when present, so a document survives a channel
    /// id change on either side. Null in older Windows files.</summary>
    public int? EqChannel { get; set; }
    public int? InputIndex { get; set; }
    public int? OutputIndex { get; set; }

    /// <summary>Channel delay (REQ_SET_DELAY): an input's own delay, or an
    /// output's output delay (one value in the firmware). Older Windows files
    /// held only outputs' delays here.</summary>
    public float DelayMs { get; set; }

    /// <summary>Output channels only.</summary>
    public float GainDb { get; set; }
    public bool Muted { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Output delay (REQ_SET_OUTPUT_DELAY), the value the output Delay
    /// control sets; preferred over <see cref="DelayMs"/> for an output. Null in
    /// older Windows files.</summary>
    public float? OutputDelayMs { get; set; }

    /// <summary>This output's limiter (V32+). Absent on inputs and when the
    /// source device had none, which leaves the device's alone. Applied with
    /// the hardware option, since the firmware keeps it with the wiring.</summary>
    public PresetLimiterBlock? Limiter { get; set; }

    public List<PresetBandBlock> Eq { get; set; } = new();

    /// <summary>Crossover bands 0..3. Empty for inputs and for pre-V11 sources.</summary>
    public List<PresetBandBlock> Crossover { get; set; } = new();
}

/// <summary>One output's limiter, as the firmware holds it.</summary>
public sealed class PresetLimiterBlock
{
    public bool Enabled { get; set; }
    public float ThresholdDb { get; set; } = LimiterLimits.DefaultThresholdDb;
    public float ReleaseMs { get; set; } = LimiterLimits.DefaultReleaseMs;
    /// <summary>0 = unlinked, 1..4.</summary>
    public int LinkGroup { get; set; }

    public static PresetLimiterBlock From(LimiterOutputSettings s) => new()
    {
        Enabled = s.Enabled,
        ThresholdDb = s.ThresholdDb,
        ReleaseMs = s.ReleaseMs,
        LinkGroup = s.LinkGroup,
    };

    /// <summary>Clamped as the firmware would; NaN takes the default.</summary>
    public LimiterOutputSettings ToSettings() => new()
    {
        Enabled = Enabled,
        ThresholdDb = float.IsNaN(ThresholdDb) ? LimiterLimits.DefaultThresholdDb : LimiterLimits.ClampThreshold(ThresholdDb),
        ReleaseMs = float.IsNaN(ReleaseMs) ? LimiterLimits.DefaultReleaseMs : LimiterLimits.ClampRelease(ReleaseMs),
        LinkGroup = LimiterLimits.ClampLinkGroup(LinkGroup),
    };
}

/// <summary>One filter band. Field meanings follow the wire encoding, including
/// the Linkwitz Transform's reuse of them (Freq = f0, Q = Q0, Gain = fp in Hz,
/// Qp = target pole Q).</summary>
public sealed class PresetBandBlock
{
    public int Type { get; set; }           // wire FilterType value
    public float FreqHz { get; set; } = 1000f;
    public float Q { get; set; } = 0.707f;
    public float Gain { get; set; }
    public float Qp { get; set; } = FilterParams.DefaultQp;
    public bool Bypass { get; set; }

    public static PresetBandBlock From(FilterParams p) => new()
    {
        Type = (int)p.Type,
        FreqHz = p.Frequency,
        Q = p.Q,
        Gain = p.Gain,
        Qp = p.Qp,
        Bypass = p.Bypass,
    };

    public FilterParams ToFilterParams() => new()
    {
        Type = (FilterType)Type,
        Frequency = FreqHz,
        Q = Q,
        Gain = Gain,
        Qp = Qp,
        Bypass = Bypass,
    };
}

public sealed class PresetCrosspointBlock
{
    public int Input { get; set; }
    public int Output { get; set; }
    public bool Enabled { get; set; }
    public bool Invert { get; set; }
    public float GainDb { get; set; }
}

/// <summary>
/// Physical IO: GPIO assignments, clocking, and the optical/serial input wiring.
/// Kept in its own block because it describes a board, not a listening setup —
/// the same split the firmware makes with output_config_mode.
/// </summary>
public sealed class PresetIoBlock
{
    public byte[] OutputPins { get; set; } = new byte[5];
    public byte[] OutputSlotTypes { get; set; } = new byte[4];  // 0=S/PDIF, 1=I2S

    public byte I2sBckPin { get; set; }
    public bool MckEnabled { get; set; }
    public byte MckPin { get; set; }
    public int MckMultiplier { get; set; } = 128;
    public byte I2sClockMode { get; set; }      // 0=master, 1=slave
    public byte I2sClockPinMode { get; set; }   // 0=unified, 1=split
    public byte I2sBckPinSlave { get; set; }

    /// <summary>S/PDIF RX GPIOs for inputs 1..3 (index 0 is the primary). Three
    /// entries, matching the schema the macOS Console shares; the fourth input's
    /// pin travels in <see cref="SpdifRxPin4"/> so a reader that indexes this
    /// array by a fixed length can't walk off the end. Older Windows files wrote
    /// four entries here — <see cref="SpdifRxPinAt"/> still honours those.</summary>
    public byte[] SpdifRxPins { get; set; } = new byte[3];

    /// <summary>GPIO for the fourth S/PDIF input, which the shared schema predates.</summary>
    public byte? SpdifRxPin4 { get; set; }

    /// <summary>Enable mask for the optional S/PDIF inputs: bit 0 = input 2,
    /// bit 1 = input 3, bit 2 = input 4.</summary>
    public byte SpdifEnabledExt { get; set; }

    /// <summary>I2S RX data GPIOs for pairs 0..3.</summary>
    public byte[] I2sRxPins { get; set; } = new byte[4];
    public int I2sInputChannels { get; set; } = 2;
    public uint I2sInputRateHz { get; set; } = 48000;

    public bool AdatEnabled { get; set; }
    public byte AdatPin { get; set; }
    public bool AdatInputEnabled { get; set; }
    public byte AdatInputPin { get; set; }
    public byte AdatInputClockMode { get; set; }

    public PresetDacHwMuteBlock? DacHwMute { get; set; }

    /// <summary>Number of S/PDIF input pins the document carries.</summary>
    [JsonIgnore]
    public int SpdifRxPinCount =>
        SpdifRxPin4.HasValue ? Math.Max(SpdifRxPins.Length, 4) : SpdifRxPins.Length;

    /// <summary>The RX pin for S/PDIF input <paramref name="index"/> (0-based),
    /// whichever of the two layouts the file used.</summary>
    public byte SpdifRxPinAt(int index) =>
        index == 3 && SpdifRxPin4.HasValue ? SpdifRxPin4.Value
        : index < SpdifRxPins.Length ? SpdifRxPins[index] : (byte)0;
}

public sealed class PresetDacHwMuteBlock
{
    public bool Enabled { get; set; }
    public bool ActiveLow { get; set; } = true;
    public byte Pin { get; set; } = DacHwMuteConfig.PinNone;
    public ushort HoldMs { get; set; }
    public ushort ReleaseMs { get; set; }
}

/// <summary>
/// JSON settings for <see cref="PresetDocument"/>. Byte arrays are written as
/// number arrays — the form the macOS Console reads and writes — rather than
/// System.Text.Json's default base64 string, which a Mac reader can't decode.
/// </summary>
public static class PresetDocumentJson
{
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new ByteArrayAsNumbersConverter() },
    };

    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new ByteArrayAsNumbersConverter() },
    };
}

/// <summary>Writes <c>byte[]</c> as <c>[6, 7, 8]</c>; reads either that or the
/// base64 string earlier Windows builds wrote.</summary>
public sealed class ByteArrayAsNumbersConverter : JsonConverter<byte[]>
{
    public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetBytesFromBase64();
            case JsonTokenType.StartArray:
                var bytes = new List<byte>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int v) || v < 0 || v > 255)
                        throw new JsonException("Expected a byte value (0-255).");
                    bytes.Add((byte)v);
                }
                return bytes.ToArray();
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for a byte array.");
        }
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (byte b in value) writer.WriteNumberValue(b);
        writer.WriteEndArray();
    }
}
