using DSPiConsole.Core.Rta;

namespace DSPiConsole.Core.Tests;

/// <summary>Byte-exact wire format and host arithmetic of the spectrum
/// analyser (firmware spectrum_analyser_spec.md, V3). Ported from the macOS
/// Console's RtaWireTests, RtaStaleFrameTests and RtaChannelSelectionTests.</summary>
public class RtaWireTests
{
    // ── Command surface ──

    [Fact]
    public void RequestCodesAreTheSpecsAndDistinct()
    {
        byte[] codes = { RtaWire.SetConfig, RtaWire.GetConfig, RtaWire.GetCaps, RtaWire.GetBands,
                         RtaWire.GetBins, RtaWire.GetStatus, RtaWire.Control, RtaWire.GetBandsAll };
        Assert.Equal(new byte[] { 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F }, codes);
        Assert.Equal(8, codes.Distinct().Count());
        Assert.Equal(529, RtaWire.BinFrameMax);
        Assert.Equal(82, 8 + 2 * RtaWire.MaxBands);
    }

    // ── Config ──

    [Fact]
    public void ConfigEncodesTwelveLittleEndianBytes()
    {
        var d = new RtaConfig(RtaWire.TapOutput, 0x0123, 10, 300, 12, 0).ToBytes();
        Assert.Equal(new byte[] { RtaWire.Version, 1, 0x23, 0x01, 10, 0, 0x2C, 0x01, 12, 0, 0, 0 }, d);
    }

    [Fact]
    public void ConfigRoundTripsAndRejectsShortOrWrongVersion()
    {
        var cfg = new RtaConfig(RtaWire.TapInput, 0x00FF, 9, 1000, 30, RtaWire.FlagManual);
        Assert.Equal(cfg, RtaConfig.Parse(cfg.ToBytes()));
        Assert.Null(RtaConfig.Parse(new byte[11]));
        var d = new RtaConfig().ToBytes();
        d[0] = 1;
        Assert.Null(RtaConfig.Parse(d));
        Assert.Equal(256, new RtaConfig(FftOrder: 8).Points);
        Assert.Equal(1024, new RtaConfig().Points);
    }

    // ── Caps ──

    internal static byte[] CapsBytes(byte version = RtaWire.Version, byte orderMin = 8, byte orderMax = 10, byte orderDefault = 10)
    {
        var d = new byte[RtaWire.CapsSize];
        d[0] = version; d[1] = 8; d[2] = 9; d[3] = orderMin; d[4] = orderMax; d[5] = orderDefault;
        d[6] = 14; d[7] = RtaWire.MaxBands; d[8] = RtaWire.LevelZeroDbfs; d[9] = 120;
        d[10] = 0x88; d[11] = 0x13; d[12] = 0x11; d[13] = 0x02; d[14] = 70;
        return d;
    }

    [Fact]
    public void CapsDecode()
    {
        var caps = RtaCaps.Parse(CapsBytes())!;
        Assert.Equal(8, caps.InputChannels);
        Assert.Equal(9, caps.OutputChannels);
        Assert.Equal(10, caps.FftOrderMax);
        Assert.Equal(14, caps.BassBands);
        Assert.Equal(120, caps.DynamicRangeDb);
        Assert.Equal(70, caps.BassDynamicRangeDb);
        Assert.Equal(82, caps.BandFrameSize);
        Assert.Equal(5000, caps.IdleTimeoutMs);
        Assert.Equal(529, caps.MaxBinFrame);
    }

    [Fact]
    public void CapsRejectZeroedOtherVersionsShortReadsAndBadRanges()
    {
        Assert.Null(RtaCaps.Parse(CapsBytes(version: 0)));
        Assert.Null(RtaCaps.Parse(CapsBytes(version: 2)));
        Assert.Null(RtaCaps.Parse(CapsBytes(version: 4)));
        Assert.Null(RtaCaps.Parse(Enumerable.Repeat((byte)0xFF, 15).ToArray()));
        Assert.Null(RtaCaps.Parse(CapsBytes(orderDefault: 11)));
        var caps = CapsBytes();
        caps[6] = 38;
        Assert.Null(RtaCaps.Parse(caps));
    }

    [Fact]
    public void LevelEncodingAnchorPoints()
    {
        Assert.Equal(0, RtaMath.LevelDb(243, 243), 6);
        Assert.Equal(6, RtaMath.LevelDb(255, 243), 6);
        Assert.Equal(-20, RtaMath.LevelDb(203, 243), 6);
        Assert.Equal(-121.5, RtaMath.LevelDb(0, 243), 6);
        // A zero zero-point falls back to the spec's 243.
        Assert.Equal(0, RtaMath.LevelDb(243, 0), 6);
    }

    // ── Band frames ──

    internal static byte[] BandFrameBytes(byte channel = 3, byte seq = 7, byte nBands = 34, ushort ageMs = 42, byte avg = 100)
    {
        var d = new byte[RtaWire.BandFrameSize];
        d[0] = RtaWire.Version; d[1] = channel; d[2] = seq; d[3] = nBands;
        d[4] = (byte)ageMs; d[5] = (byte)(ageMs >> 8);
        for (int i = 0; i < RtaWire.MaxBands; i++)
        {
            d[8 + i] = (byte)(avg + i);
            d[8 + RtaWire.MaxBands + i] = (byte)(140 + i);
        }
        return d;
    }

    [Fact]
    public void BandFrameDecode()
    {
        var f = RtaBandFrame.Parse(BandFrameBytes())!;
        Assert.Equal(3, f.Channel);
        Assert.Equal(7, f.Seq);
        Assert.Equal(34, f.NBands);
        Assert.Equal(42, f.AgeMs);
        Assert.Equal(RtaWire.MaxBands, f.Avg.Length);
        Assert.Equal(100, f.Avg[0]);
        Assert.Equal(100 + RtaWire.MaxBands - 1, f.Avg[^1]);
        Assert.Equal(140, f.Peak[0]);
        Assert.True(f.HasData);
        Assert.False(RtaBandFrame.Parse(BandFrameBytes(ageMs: 0xFFFF))!.HasData);
    }

    [Fact]
    public void BandFramesParseBackToBackAtOffsets()
    {
        var all = BandFrameBytes(0, 1).Concat(BandFrameBytes(4, 2)).Concat(BandFrameBytes(8, 3)).ToArray();
        var seen = new Dictionary<byte, byte>();
        for (int off = 0; off + RtaWire.BandFrameSize <= all.Length; off += RtaWire.BandFrameSize)
            if (RtaBandFrame.Parse(all, off) is { } f) seen[f.Channel] = f.Seq;
        Assert.Equal(new Dictionary<byte, byte> { [0] = 1, [4] = 2, [8] = 3 }, seen);
    }

    [Fact]
    public void BandFrameRejectsShortReadsBadOffsetsAndOtherVersions()
    {
        Assert.Null(RtaBandFrame.Parse(Enumerable.Repeat((byte)1, 81).ToArray()));
        Assert.Null(RtaBandFrame.Parse(BandFrameBytes(), 8));
        Assert.Null(RtaBandFrame.Parse(BandFrameBytes(), -1));
        Assert.Null(RtaBandFrame.Parse(BandFrameBytes(), int.MaxValue));
        Assert.Null(RtaBandFrame.Parse(BandFrameBytes(nBands: 38)));
        var old = BandFrameBytes();
        old[0] = 2;
        Assert.Null(RtaBandFrame.Parse(old));
    }

    [Fact]
    public void BandStrideComesFromCapability()
    {
        const int slots = 34, stride = 8 + 2 * slots;
        var bytes = new byte[stride * 2];
        foreach (int offset in new[] { 0, stride })
        {
            bytes[offset] = 3; bytes[offset + 3] = slots;
            bytes[offset + 8] = 201; bytes[offset + 8 + slots] = 211;
        }
        bytes[stride + 1] = 8;
        var last = RtaBandFrame.Parse(bytes, stride, slots)!;
        Assert.Equal(8, last.Channel);
        Assert.Equal(slots, last.Avg.Length);
        Assert.Equal(201, last.Avg[0]);
        Assert.Equal(211, last.Peak[0]);
    }

    [Fact]
    public void DrawsSameIgnoresAgeAndSequenceButNotTheSentinel()
    {
        var a = RtaBandFrame.Parse(BandFrameBytes(seq: 1, ageMs: 10))!;
        Assert.True(a.DrawsSame(RtaBandFrame.Parse(BandFrameBytes(seq: 2, ageMs: 900))!));
        Assert.False(a.DrawsSame(RtaBandFrame.Parse(BandFrameBytes(ageMs: 0xFFFF))!));
        Assert.False(a.DrawsSame(RtaBandFrame.Parse(BandFrameBytes(avg: 101))!));
    }

    // ── Bin frames ──

    internal static byte[] BinFrameBytes(byte seq = 9, int nBins = 8, byte order = 10, uint rate = 48000, byte? tail = null, byte channel = 2, byte fill = 200)
    {
        var d = new byte[RtaWire.BinHeaderSize + nBins + 1];
        d[0] = RtaWire.Version; d[1] = channel; d[2] = seq; d[3] = order;
        d[4] = (byte)rate; d[5] = (byte)(rate >> 8); d[6] = (byte)(rate >> 16); d[7] = (byte)(rate >> 24);
        d[8] = (byte)nBins; d[9] = (byte)(nBins >> 8);
        for (int i = 0; i < nBins; i++) d[RtaWire.BinHeaderSize + i] = (byte)(fill + i);
        d[RtaWire.BinHeaderSize + nBins] = tail ?? seq;
        return d;
    }

    [Fact]
    public void BinFrameDecodeAndFrequencies()
    {
        var f = RtaBinFrame.Parse(BinFrameBytes())!;
        Assert.Equal(2, f.Channel);
        Assert.Equal(9, f.Seq);
        Assert.Equal(48000u, f.SampleRateHz);
        Assert.Equal(8, f.Bins.Length);
        Assert.Equal(200, f.Bins[0]);

        var half = RtaBinFrame.Parse(BinFrameBytes(nBins: 256, order: 9))!;
        Assert.Equal(0, half.FrequencyOfBin(0), 3);
        Assert.Equal(48000.0 / 512, half.FrequencyOfBin(1), 3);
        Assert.Equal(24000, half.FrequencyOfBin(256), 3);
        var big = RtaBinFrame.Parse(BinFrameBytes(nBins: 512))!;
        Assert.Equal(RtaWire.BinFrameMax, RtaWire.BinHeaderSize + big.Bins.Length + 1);
        Assert.Equal(RtaWire.BinFrameMax, RtaBinFrame.FrameLength(BinFrameBytes(nBins: 512)));
    }

    [Fact]
    public void BinFrameRejectsTornInProgressAndTruncatedReads()
    {
        Assert.Null(RtaBinFrame.Parse(BinFrameBytes(seq: 9, tail: 10)));
        Assert.Null(RtaBinFrame.Parse(BinFrameBytes(seq: 0xFF)));
        var d = BinFrameBytes();
        Assert.Null(RtaBinFrame.Parse(d[..^3]));
    }

    // ── Status ──

    internal static byte[] StatusBytes(byte state = RtaWire.StateCapturing, byte liveCount = 4, byte firstBand = 12, ushort fps = 46)
    {
        var d = new byte[RtaWire.StatusSize];
        d[0] = RtaWire.Version; d[1] = state; d[2] = RtaWire.TapOutput; d[3] = 2; d[5] = liveCount;
        d[6] = 0x0F; d[8] = (byte)fps; d[9] = (byte)(fps >> 8);
        d[10] = 0x10; d[11] = 0x27; d[12] = 0x70; d[13] = 0x01; d[14] = 0x0A;
        d[16] = 0x80; d[17] = 0xBB; d[20] = firstBand; d[22] = 0xD2; d[23] = 0x04;
        return d;
    }

    [Fact]
    public void StatusDecode()
    {
        var s = RtaStatus.Parse(StatusBytes())!;
        Assert.Equal(RtaWire.StateCapturing, s.State);
        Assert.Equal(RtaWire.TapOutput, s.Tap);
        Assert.Equal(2, s.Channel);
        Assert.Equal(4, s.LiveCount);
        Assert.Equal(0x000F, s.LiveMask);
        Assert.Equal(46, s.FramesPerSecond);
        Assert.Equal(10000, s.BusyUsPerSecond);
        Assert.Equal(1234, s.BassBusyUsPerSecond);
        Assert.Equal(368, s.LastFrameUs);
        Assert.Equal(48000u, s.SampleRateHz);
        Assert.True(s.IsRunning);
        Assert.Equal(12, s.FirstResolvedBand);
        Assert.Equal(RtaWire.MaxBands, RtaStatus.Parse(StatusBytes(firstBand: 0xFF))!.FirstResolvedBand);
        Assert.Null(RtaStatus.Parse(Enumerable.Repeat((byte)1, 23).ToArray()));
    }

    [Fact]
    public void BassTimingSaturationIsALowerBound()
    {
        var b = StatusBytes();
        b[22] = 255; b[23] = 255;
        var s = RtaStatus.Parse(b)!;
        Assert.Equal(ushort.MaxValue, s.BassBusyUsPerSecond);
        Assert.Contains("≥", s.BassLoadDescription);
    }

    // ── Which bands the transform can measure ──

    [Fact]
    public void BandsWithNoBinAt48kAnd1024Points()
    {
        foreach (double hz in new[] { 20, 25, 31.5, 40, 63, 80, 125, 160 })
            Assert.False(RtaMath.BandHasBin(hz, 48000, 10), $"{hz} Hz");
        foreach (double hz in new double[] { 50, 100, 200, 250, 500, 1000, 4000, 16000 })
            Assert.True(RtaMath.BandHasBin(hz, 48000, 10), $"{hz} Hz");
        Assert.False(RtaMath.BandHasBin(50, 48000, 9));
        Assert.True(RtaMath.BandHasBin(100, 48000, 9));
        // Nonsense fails open, so a live band is never greyed out.
        Assert.True(RtaMath.BandHasBin(0, 48000, 10));
        Assert.True(RtaMath.BandHasBin(1000, 0, 10));
        Assert.True(RtaMath.BandHasBin(1000, 48000, 0));
    }

    [Fact]
    public void BandIsPopulatedMatchesFirmwareTables()
    {
        foreach (double rate in new[] { 44100.0, 48000.0, 96000.0 })
            for (int order = 8; order <= 10; order++)
                for (int band = 0; band < 14; band++)
                    Assert.True(RtaMath.BandIsPopulated(band, rate, order));
        Assert.False(RtaMath.BandIsPopulated(14, 48000, 8)); // 250 Hz
        Assert.False(RtaMath.BandIsPopulated(15, 48000, 8)); // 315 Hz
        Assert.True(RtaMath.BandIsPopulated(16, 48000, 8));  // 400 Hz
        Assert.True(RtaMath.BandIsPopulated(33, 48000, 10)); // 20 kHz
        Assert.False(RtaMath.BandIsPopulated(0, 48000, 10, bassBands: 0));
        Assert.True(RtaMath.BandIsPopulated(7, 48000, 10, bassBands: 0));
    }

    // ── Display interpolation ──

    [Fact]
    public void FallTauTracksRefreshIntervalAndClamps()
    {
        Assert.Equal(0, RtaMath.FallTau(0.19, 0));
        Assert.Equal(0.035, RtaMath.FallTau(0.021, 0.6), 4);
        Assert.Equal(0.1152, RtaMath.FallTau(0.192, 0.6), 4);
        Assert.Equal(0.40, RtaMath.FallTau(5.0, 1.0), 4);
    }

    [Fact]
    public void SmootherAdoptsTheFirstFrameThenMovesPartway()
    {
        var s = new RtaBarSmoother();
        Assert.Equal(new double[] { -20, -30 }, s.Step(0, new double[] { -20, -30 }, 1, 0.1, 0.1));
        var t = new RtaBarSmoother();
        t.Step(0, new double[] { -60 }, 1, 0.1, 0.1);
        double v = t.Step(0.033, new double[] { -20 }, 1, 0.1, 0.1)[0];
        Assert.InRange(v, -59.999, -20.001);
    }

    [Fact]
    public void SmootherConverges()
    {
        var s = new RtaBarSmoother();
        s.Step(0, new double[] { -60 }, 1, 0.05, 0.05);
        double[] output = Array.Empty<double>();
        for (int i = 1; i <= 60; i++) output = s.Step(i / 30.0, new double[] { -20 }, 1, 0.05, 0.05);
        Assert.Equal(-20, output[0], 2);
    }

    [Fact]
    public void ZeroRiseTauSnapsUpButStillEasesDown()
    {
        var s = new RtaBarSmoother();
        s.Step(0, new double[] { -60 }, 1, 0, 0.2);
        Assert.Equal(-10, s.Step(0.033, new double[] { -10 }, 1, 0, 0.2)[0], 4);
        double down = s.Step(0.066, new double[] { -60 }, 1, 0, 0.2)[0];
        Assert.InRange(down, -59.999, -10.001);
    }

    [Fact]
    public void SmootherSnapsOnANewSeriesOrLengthAndIgnoresARepeatedTime()
    {
        var s = new RtaBarSmoother();
        s.Step(0, new double[] { -60, -60 }, 1, 0.2, 0.2);
        Assert.Equal(new double[] { -10, -10 }, s.Step(0.033, new double[] { -10, -10 }, 2, 0.2, 0.2));
        Assert.Equal(new double[] { -10, -10, -10 }, s.Step(0.066, new double[] { -10, -10, -10 }, 2, 0.2, 0.2));

        var r = new RtaBarSmoother();
        r.Step(0, new double[] { -60 }, 1, 0.1, 0.1);
        double first = r.Step(0.033, new double[] { -20 }, 1, 0.1, 0.1)[0];
        double again = r.Step(0.033, new double[] { -20 }, 1, 0.1, 0.1)[0];
        Assert.Equal(first, again);
    }

    // ── Frequency smoothing of the bins ──

    [Fact]
    public void BinSmoothingLeavesTheLowEndAloneAndAFlatSpectrumFlat()
    {
        var levels = Enumerable.Range(0, 512).Select(i => (double)(i * 37 % 60) - 80).ToArray();
        var output = RtaMath.SmoothBins(levels, 1.0 / 6.0);
        Assert.Equal(levels[..17], output[..17]);
        Assert.NotEqual(levels[300], output[300]);

        var flat = RtaMath.SmoothBins(Enumerable.Repeat(-40.0, 512).ToArray(), 1.0 / 6.0);
        Assert.All(flat, v => Assert.True(Math.Abs(v + 40) < 1e-9));
    }

    [Fact]
    public void BinSmoothingAveragesPowerNotDecibels()
    {
        var levels = Enumerable.Range(0, 512).Select(i => i % 2 == 0 ? -20.0 : -40.0).ToArray();
        Assert.InRange(RtaMath.SmoothBins(levels, 1.0 / 6.0)[300], 10 * Math.Log10(0.0101 / 2) - 0.3, 10 * Math.Log10(0.0101 / 2) + 0.3);
    }

    [Fact]
    public void BinSmoothingSpreadsAnIsolatedTreblePeakAndOffPassesThrough()
    {
        var levels = Enumerable.Repeat(-120.0, 512).ToArray();
        levels[400] = -20;
        var output = RtaMath.SmoothBins(levels, 1.0 / 6.0);
        Assert.InRange(output[400], -40, -30);
        Assert.True(output[395] > -60);
        Assert.Equal(-120, output[300], 6);

        var other = Enumerable.Range(0, 512).Select(i => (double)(i % 7) - 50).ToArray();
        Assert.Same(other, RtaMath.SmoothBins(other, 0));
    }

    // ── Time averaging of the bins ──

    private static RtaBinFrame Bins(byte[] bins, byte seq, byte channel = 0, byte order = 10) =>
        new() { Channel = channel, Seq = seq, FftOrder = order, SampleRateHz = 48000, Bins = bins };

    private static double Wire(byte v) => RtaMath.LevelDb(v, RtaWire.LevelZeroDbfs);

    [Fact]
    public void BinAverageAdoptsTheFirstFrameAndMatchesTheDeviceFormula()
    {
        var avg = new RtaBinAverage();
        var first = avg.Add(Bins(new byte[] { 203, 163 }, 1), RtaWire.TapOutput, 0, 300, Wire);
        Assert.Equal(-20, first[0], 9);
        Assert.Equal(-40, first[1], 9);

        var step = new RtaBinAverage();
        step.Add(Bins(new byte[] { 163 }, 1), RtaWire.TapOutput, 0, 300, Wire);
        var output = step.Add(Bins(new byte[] { 203 }, 2), RtaWire.TapOutput, 0.1, 300, Wire);
        Assert.Equal(10 * Math.Log10(0.0001 + 0.25 * (0.01 - 0.0001)), output[0], 6);
    }

    [Fact]
    public void BinAverageIgnoresARepeatedFrameAndKeepsASteadyLevel()
    {
        var avg = new RtaBinAverage();
        avg.Add(Bins(new byte[] { 163 }, 1), RtaWire.TapOutput, 0, 300, Wire);
        var once = avg.Add(Bins(new byte[] { 203 }, 2), RtaWire.TapOutput, 0.1, 300, Wire);
        var again = avg.Add(Bins(new byte[] { 203 }, 2), RtaWire.TapOutput, 0.2, 300, Wire);
        Assert.Equal(once, again);

        var steady = new RtaBinAverage();
        double[] output = Array.Empty<double>();
        for (int s = 0; s < 40; s++) output = steady.Add(Bins(new byte[] { 203 }, (byte)s), RtaWire.TapOutput, s * 0.05, 1000, Wire);
        Assert.Equal(-20, output[0], 9);
    }

    [Fact]
    public void BinAverageOffPassesFramesThroughAndStartsOverOnANewSeries()
    {
        var off = new RtaBinAverage();
        off.Add(Bins(new byte[] { 163 }, 1), RtaWire.TapOutput, 0, 0, Wire);
        Assert.Equal(-20, off.Add(Bins(new byte[] { 203 }, 2), RtaWire.TapOutput, 0.1, 0, Wire)[0], 9);

        var avg = new RtaBinAverage();
        avg.Add(Bins(new byte[] { 163 }, 1), RtaWire.TapOutput, 0, 300, Wire);
        Assert.Equal(-20, avg.Add(Bins(new byte[] { 203 }, 2), RtaWire.TapInput, 0.1, 300, Wire)[0], 9);
        Assert.Equal(-40, avg.Add(Bins(new byte[] { 163, 163 }, 3, order: 9), RtaWire.TapInput, 0.2, 300, Wire)[0], 9);
        avg.Reset();
        Assert.Equal(-20, avg.Add(Bins(new byte[] { 203, 203 }, 4, order: 9), RtaWire.TapInput, 0.3, 300, Wire)[0], 9);
    }

    // ── Stale frames ──

    [Fact]
    public void AnOldFrameIsSilencedAndReported()
    {
        var frames = new Dictionary<byte, RtaBandFrame>
        {
            [0] = RtaBandFrame.Parse(BandFrameBytes(channel: 0, ageMs: 4000))!,
            [1] = RtaBandFrame.Parse(BandFrameBytes(channel: 1, ageMs: 40))!,
            [2] = RtaBandFrame.Parse(BandFrameBytes(channel: 2, ageMs: 0xFFFF))!,
        };
        var (output, stale) = RtaMath.SilencingStale(frames, 500);
        Assert.Equal(new HashSet<byte> { 0 }, stale);
        Assert.All(output[0].Avg, v => Assert.Equal(0, v));
        Assert.All(output[0].Peak, v => Assert.Equal(0, v));
        Assert.Same(frames[1], output[1]);
        Assert.Same(frames[2], output[2]);
    }

    // ── Channel selection ──

    [Fact]
    public void SelectionStorageKeysRoundTripAndRejectMalformed()
    {
        var s = new RtaChannelSelection(RtaWire.TapInput, new[] { 2, 0, 2 });
        Assert.Equal("in:0,2", s.StorageKey);
        Assert.Equal(s, RtaChannelSelection.FromStorageKey("in:0,2"));
        Assert.Equal(new RtaChannelSelection(RtaWire.TapOutput, new[] { 1 }), RtaChannelSelection.FromStorageKey("out:1"));
        var empty = RtaChannelSelection.FromStorageKey("out:")!;
        Assert.True(empty.IsEmpty);
        Assert.Equal(RtaWire.TapOutput, empty.Tap);
        foreach (var bad in new[] { "", "sideways:1", "in", "out:x", "out:16", "in:-1" })
            Assert.Null(RtaChannelSelection.FromStorageKey(bad));
        Assert.Equal(0b101, s.Mask);
    }

    [Fact]
    public void TogglingStaysAtOneTap()
    {
        var s = new RtaChannelSelection(RtaWire.TapOutput, new[] { 0 });
        Assert.Equal(new[] { 0, 3 }, s.Toggling(RtaWire.TapOutput, 3).Channels);
        Assert.True(s.Toggling(RtaWire.TapOutput, 0).IsEmpty);
        // The other tap cannot be mixed in; an empty selection takes either.
        Assert.Same(s, s.Toggling(RtaWire.TapInput, 1));
        var fromEmpty = RtaChannelSelection.None.Toggling(RtaWire.TapInput, 1);
        Assert.Equal(RtaWire.TapInput, fromEmpty.Tap);
        Assert.Equal(new[] { 1 }, fromEmpty.Channels);
    }

    [Fact]
    public void SwitchingSidesRemembersEachSide()
    {
        var outs = new RtaChannelSelection(RtaWire.TapOutput, new[] { 0, 1 });
        var (active, remembered) = RtaChannelSelection.SwitchingSides(outs, null, RtaWire.TapInput);
        Assert.True(active.IsEmpty);
        Assert.Equal(RtaWire.TapInput, active.Tap);
        Assert.Equal(outs, remembered);
        var ins = active.Toggling(RtaWire.TapInput, 1);
        var back = RtaChannelSelection.SwitchingSides(ins, remembered, RtaWire.TapOutput);
        Assert.Equal(outs, back.Active);
        Assert.Equal(ins, back.Remembered);
        // The side already showing changes nothing; a remembered selection
        // from the wrong side is ignored.
        Assert.Equal((outs, (RtaChannelSelection?)ins), RtaChannelSelection.SwitchingSides(outs, ins, RtaWire.TapOutput));
        Assert.True(RtaChannelSelection.SwitchingSides(outs, outs, RtaWire.TapInput).Active.IsEmpty);
        Assert.Equal(new[] { 1 }, outs.Restricted(new[] { 1, 5 }).Channels);
    }
}
