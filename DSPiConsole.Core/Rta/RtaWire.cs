namespace DSPiConsole.Core.Rta;

/// <summary>
/// The device-side spectrum analyser's wire format (firmware
/// Documentation/Features/spectrum_analyser_spec.md, protocol V3). Every
/// structure is a byte-for-byte reading of a packed little-endian struct, so
/// the parsers check the length they were given: firmware without the analyser
/// STALLs these requests, which arrives as a null or a short read. Port of the
/// macOS Console's SpectrumAnalyser.swift wire section.
/// </summary>
public static class RtaWire
{
    public const byte SetConfig = 0x08;
    public const byte GetConfig = 0x09;
    public const byte GetCaps = 0x0A;
    public const byte GetBands = 0x0B;
    public const byte GetBins = 0x0C;
    public const byte GetStatus = 0x0D;
    public const byte Control = 0x0E;
    public const byte GetBandsAll = 0x0F;

    public const byte Version = 3;
    public const int ConfigSize = 12;
    public const int CapsSize = 16;
    public const int BandFrameSize = 82;
    public const int StatusSize = 24;
    public const int BinHeaderSize = 16;
    public const int MaxBands = 37;
    public const int BassBands = 14;
    /// <summary>Header, 512 bins (1024 points) and the repeated sequence byte.</summary>
    public const int BinFrameMax = 16 + 512 + 1;
    public const int OrderMin = 8;
    public const int OrderMax = 10;
    public const int CentresPerChunk = 32;

    public const byte TapInput = 0;
    public const byte TapOutput = 1;
    public const byte FlagManual = 0x01;

    public const byte StateIdle = 0;
    public const byte StateCapturing = 1;
    public const byte StateTransforming = 2;

    public const ushort CtlStop = 0;
    public const ushort CtlStart = 1;
    public const ushort CtlResetAvg = 2;

    public const byte LevelZeroDbfs = 243;
    public const double LevelStepDb = 0.5;
    public const byte ChannelNone = 0xFF;

    internal static ushort U16(byte[] b, int i) => (ushort)(b[i] | (b[i + 1] << 8));
    internal static uint U32(byte[] b, int i) => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
}

/// <summary>
/// The staged configuration (SET_CONFIG / GET_CONFIG, 12 bytes). A change of
/// tap, mask or size restarts the frame in flight and clears the averaging; a
/// change of averaging or peak decay alone applies at the next publish.
/// </summary>
public readonly record struct RtaConfig(
    byte Tap = RtaWire.TapOutput,
    ushort ChannelMask = 1,
    byte FftOrder = 10,
    ushort AvgMs = 300,
    byte PeakDecayDbS = 12,
    byte Flags = 0)
{
    public RtaConfig() : this(RtaWire.TapOutput) { }

    /// <summary>Points in the transform: 256, 512 or 1024.</summary>
    public int Points => 1 << FftOrder;

    public byte[] ToBytes()
    {
        var d = new byte[RtaWire.ConfigSize];
        d[0] = RtaWire.Version;
        d[1] = Tap;
        d[2] = (byte)ChannelMask;
        d[3] = (byte)(ChannelMask >> 8);
        d[4] = FftOrder;
        d[6] = (byte)AvgMs;
        d[7] = (byte)(AvgMs >> 8);
        d[8] = PeakDecayDbS;
        d[9] = Flags;
        return d;
    }

    public static RtaConfig? Parse(byte[]? b)
    {
        if (b == null || b.Length < RtaWire.ConfigSize || b[0] != RtaWire.Version) return null;
        return new RtaConfig(b[1], RtaWire.U16(b, 2), b[4], RtaWire.U16(b, 6), b[8], b[9]);
    }
}

/// <summary>
/// What this device's analyser can do (GET_CAPS wValue 0, 16 bytes). A
/// successful read is the feature probe: the analyser is transient and absent
/// from the bulk blob, so there is no wire-format version to gate on.
/// </summary>
public sealed record RtaCaps
{
    public byte Version { get; init; }
    public byte InputChannels { get; init; }
    public byte OutputChannels { get; init; }
    public byte FftOrderMin { get; init; } = RtaWire.OrderMin;
    public byte FftOrderMax { get; init; } = RtaWire.OrderMax;
    public byte FftOrderDefault { get; init; } = 10;
    public byte BassBands { get; init; }
    public byte MaxBands { get; init; } = RtaWire.MaxBands;
    public byte LevelZero { get; init; } = RtaWire.LevelZeroDbfs;
    /// <summary>Measured: 78 dB for the RP2040's Q15 kernel, 120 for the
    /// RP2350's float kernel. The floor a reading can be trusted down to.</summary>
    public byte DynamicRangeDb { get; init; }
    public ushort IdleTimeoutMs { get; init; }
    public ushort MaxBinFrame { get; init; }
    /// <summary>The bass bank's usable range, separate from the FFT's.</summary>
    public ushort BassDynamicRangeDb { get; init; }

    public int BandFrameSize => 8 + 2 * MaxBands;

    public static RtaCaps? Parse(byte[]? b)
    {
        if (b == null || b.Length < RtaWire.CapsSize) return null;
        // Band indices and strides changed in V3: never interpret another
        // version's frames with this layout.
        if (b[0] != RtaWire.Version || b[3] < RtaWire.OrderMin || b[4] > RtaWire.OrderMax
            || b[3] > b[5] || b[5] > b[4] || b[7] == 0 || b[7] > RtaWire.MaxBands || b[6] > b[7]) return null;
        return new RtaCaps
        {
            Version = b[0],
            InputChannels = b[1],
            OutputChannels = b[2],
            FftOrderMin = b[3],
            FftOrderMax = b[4],
            FftOrderDefault = b[5],
            BassBands = b[6],
            MaxBands = b[7],
            LevelZero = b[8],
            DynamicRangeDb = b[9],
            IdleTimeoutMs = RtaWire.U16(b, 10),
            MaxBinFrame = RtaWire.U16(b, 12),
            BassDynamicRangeDb = RtaWire.U16(b, 14),
        };
    }
}

/// <summary>
/// One channel's third-octave picture (GET_BANDS, 82 bytes). <see cref="Avg"/>
/// and <see cref="Peak"/> carry one slot per caps band; only the first
/// <see cref="NBands"/> mean anything at the current rate. The first
/// <c>BassBands</c> come from the continuous filter bank.
/// </summary>
public sealed record RtaBandFrame
{
    public byte Channel { get; init; }
    public byte Seq { get; init; }
    public byte NBands { get; init; }
    /// <summary>Milliseconds since this channel's last frame; 0xFFFF = never.</summary>
    public ushort AgeMs { get; init; } = 0xFFFF;
    public byte[] Avg { get; init; } = new byte[RtaWire.MaxBands];
    public byte[] Peak { get; init; } = new byte[RtaWire.MaxBands];

    /// <summary>True once the channel has produced at least one frame.</summary>
    public bool HasData => AgeMs != 0xFFFF;

    public static RtaBandFrame? Parse(byte[]? d, int offset = 0, int maxBands = RtaWire.MaxBands)
    {
        if (d == null || maxBands <= 0 || maxBands > RtaWire.MaxBands || offset < 0 || offset > d.Length) return null;
        int size = 8 + 2 * maxBands;
        if (d.Length - offset < size) return null;
        if (d[offset] != RtaWire.Version || d[offset + 3] > maxBands) return null;
        return new RtaBandFrame
        {
            Channel = d[offset + 1],
            Seq = d[offset + 2],
            NBands = d[offset + 3],
            AgeMs = RtaWire.U16(d, offset + 4),
            Avg = d.AsSpan(offset + 8, maxBands).ToArray(),
            Peak = d.AsSpan(offset + 8 + maxBands, maxBands).ToArray(),
        };
    }

    /// <summary>Whether two frames draw the same picture: sequence and age
    /// carry telemetry, apart from the never-published sentinel.</summary>
    public bool DrawsSame(RtaBandFrame o) =>
        Channel == o.Channel && NBands == o.NBands && HasData == o.HasData
        && Avg.AsSpan().SequenceEqual(o.Avg) && Peak.AsSpan().SequenceEqual(o.Peak);

    /// <summary>The same frame read as silence.</summary>
    public RtaBandFrame Silenced() => this with { Avg = new byte[Avg.Length], Peak = new byte[Peak.Length] };
}

/// <summary>
/// The most recent frame's raw magnitude bins (GET_BINS). Only the latest frame
/// is kept, tagged with its channel, so a multichannel selection rotates it.
/// The sequence number is in the header and again as the last byte; a host
/// reading while the engine publishes sees the two disagree and re-reads.
/// </summary>
public sealed record RtaBinFrame
{
    public byte Channel { get; init; }
    public byte Seq { get; init; }
    public byte FftOrder { get; init; }
    public uint SampleRateHz { get; init; }
    /// <summary>One level byte per bin; bin k is centred at k * rate / (2 * count).</summary>
    public byte[] Bins { get; init; } = Array.Empty<byte>();
    /// <summary>dBFS per bin after the engine's time average; null straight off the wire.</summary>
    public double[]? LevelsDb { get; init; }
    /// <summary>The time-averaged levels smoothed across frequency, prepared
    /// once per new frame and shared by every display.</summary>
    public double[]? SmoothedLevelsDb { get; init; }

    public double FrequencyOfBin(int k) => Bins.Length == 0 ? 0 : (double)k * SampleRateHz / (2.0 * Bins.Length);

    /// <summary>Parses a whole frame, however many chunks it was read in. Null
    /// for a short read, a header that does not describe what follows, or a
    /// torn read (tail and header sequence differ).</summary>
    public static RtaBinFrame? Parse(byte[]? b)
    {
        if (b == null || b.Length < RtaWire.BinHeaderSize || b[0] != RtaWire.Version) return null;
        byte seq = b[2];
        // 0xFF is the in-progress marker, never a published sequence number.
        if (seq == 0xFF) return null;
        int nBins = RtaWire.U16(b, 8);
        int tail = RtaWire.BinHeaderSize + nBins;
        if (nBins <= 0 || b.Length <= tail || b[tail] != seq) return null;
        return new RtaBinFrame
        {
            Channel = b[1],
            Seq = seq,
            FftOrder = b[3],
            SampleRateHz = RtaWire.U32(b, 4),
            Bins = b.AsSpan(RtaWire.BinHeaderSize, nBins).ToArray(),
        };
    }

    /// <summary>Total frame length once the header has arrived: 16 header
    /// bytes, one per bin, and the repeated sequence byte.</summary>
    public static int? FrameLength(ReadOnlySpan<byte> d)
    {
        if (d.Length < RtaWire.BinHeaderSize) return null;
        int nBins = d[8] | (d[9] << 8);
        return nBins > 0 ? RtaWire.BinHeaderSize + nBins + 1 : null;
    }

    public bool DrawsSame(RtaBinFrame o) =>
        Channel == o.Channel && FftOrder == o.FftOrder && SampleRateHz == o.SampleRateHz
        && Bins.AsSpan().SequenceEqual(o.Bins);
}

/// <summary>
/// Engine telemetry (GET_STATUS, 24 bytes). Reading it is deliberately not a
/// data read, so polling status neither starts the analyser nor keeps it alive.
/// </summary>
public sealed record RtaStatus
{
    public byte State { get; init; } = RtaWire.StateIdle;
    public byte Tap { get; init; } = RtaWire.TapOutput;
    /// <summary>The channel being captured or transformed; 0xFF while idle.</summary>
    public byte Channel { get; init; } = RtaWire.ChannelNone;
    public byte LiveCount { get; init; }
    /// <summary>Selected and live: disabled outputs and inactive input rows
    /// drop out of the rotation.</summary>
    public ushort LiveMask { get; init; }
    public ushort FramesPerSecond { get; init; }
    /// <summary>Main-loop microseconds per second spent transforming, which the
    /// CPU meter cannot see. 10,000 is 1 % of one core.</summary>
    public ushort BusyUsPerSecond { get; init; }
    public ushort LastFrameUs { get; init; }
    public ushort IdleMs { get; init; } = 0xFFFF;
    public uint SampleRateHz { get; init; }
    /// <summary>0 for a supported bass layout; 0xFF means unsupported.</summary>
    public byte FirstBand { get; init; }
    /// <summary>Summed bass tap time on both cores, already inside the audio
    /// CPU figure. 65535 is saturation, a lower bound.</summary>
    public ushort BassBusyUsPerSecond { get; init; }

    public bool IsRunning => State != RtaWire.StateIdle;

    /// <summary>The lowest band with a reading, as a band index.</summary>
    public int FirstResolvedBand => FirstBand == 0xFF ? RtaWire.MaxBands : FirstBand;

    public string BassLoadDescription =>
        $"bass {(BassBusyUsPerSecond == ushort.MaxValue ? "≥" : "")}{BassBusyUsPerSecond / 10000.0:0.00}%";

    public static RtaStatus? Parse(byte[]? b)
    {
        if (b == null || b.Length < RtaWire.StatusSize || b[0] != RtaWire.Version) return null;
        return new RtaStatus
        {
            State = b[1],
            Tap = b[2],
            Channel = b[3],
            LiveCount = b[5],
            LiveMask = RtaWire.U16(b, 6),
            FramesPerSecond = RtaWire.U16(b, 8),
            BusyUsPerSecond = RtaWire.U16(b, 10),
            LastFrameUs = RtaWire.U16(b, 12),
            IdleMs = RtaWire.U16(b, 14),
            SampleRateHz = RtaWire.U32(b, 16),
            FirstBand = b[20],
            BassBusyUsPerSecond = RtaWire.U16(b, 22),
        };
    }
}

/// <summary>The three settings the device owns: transform size, averaging and
/// peak-hold decay. Never persisted on the device, so the app keeps them and
/// pushes them whenever it starts watching.</summary>
public readonly record struct RtaOptions(byte FftOrder = 10, ushort AvgMs = 300, byte PeakDecayDbS = 12)
{
    public RtaOptions() : this(10) { }
}

/// <summary>What one on-screen analyser wants to see. The engine folds every
/// request into one device configuration, since the device has one FFT.</summary>
/// <param name="Tap">Input or output side.</param>
/// <param name="Mask">Bit i = channel i at that tap.</param>
/// <param name="WantsBins">Also wants the raw bins. A bin frame belongs to the
/// channel transformed last, so this asks for a narrow selection too.</param>
public readonly record struct RtaRequest(byte Tap, ushort Mask, bool WantsBins = false);
