using DSPiConsole.Core.Rta;
using static DSPiConsole.Core.Tests.RtaWireTests;

namespace DSPiConsole.Core.Tests;

/// <summary>The analyser engine against a scripted device: the caps probe, how
/// subscriptions fold into one configuration, the give-up on a refused config,
/// which reads each tick makes, and the stop when nobody watches.</summary>
public class RtaEngineTests
{
    private sealed class FakeDevice : IRtaTransport
    {
        public readonly List<(byte Request, ushort Value)> Reads = new();
        public readonly List<RtaConfig> Pushed = new();
        public RtaConfig? Applied;
        public bool RefuseConfig;
        public bool Stall;
        public byte[]? Bins = BinFrameBytes(nBins: 512, channel: 0);
        public byte[] Status = StatusBytes(liveCount: 1, firstBand: 0, fps: 46);
        public Func<byte, byte[]>? Bands;

        public byte[]? RtaIn(byte request, ushort value, int length)
        {
            lock (Reads) Reads.Add((request, value));
            if (Stall) return null;
            return request switch
            {
                RtaWire.GetCaps when value == 0 => CapsBytes(),
                RtaWire.GetCaps => Centres(value, length),
                RtaWire.GetBands => Band((byte)value),
                RtaWire.GetBandsAll => Pushed.Count == 0 ? null
                    : Enumerable.Range(0, 16).Where(c => (Pushed[^1].ChannelMask & (1 << c)) != 0)
                        .SelectMany(c => Band((byte)c)).ToArray(),
                RtaWire.GetBins => Bins == null ? null : Bins.Skip(value).Take(length).ToArray(),
                RtaWire.GetStatus => Status,
                RtaWire.GetConfig => Applied?.ToBytes(),
                RtaWire.Control => new byte[] { 0 },
                _ => null,
            };
        }

        public bool RtaOut(byte request, ushort value, byte[] data)
        {
            if (request != RtaWire.SetConfig) return false;
            var cfg = RtaConfig.Parse(data)!.Value;
            lock (Reads) Pushed.Add(cfg);
            if (!RefuseConfig) Applied = cfg;
            return !RefuseConfig;
        }

        private byte[] Band(byte channel) => Bands?.Invoke(channel) ?? BandFrameBytes(channel: channel, ageMs: 10);

        private static byte[] Centres(ushort chunk, int length)
        {
            double[] all = Enumerable.Range(0, RtaWire.MaxBands).Select(i => Math.Round(10 * Math.Pow(10, i / 10.0))).ToArray();
            var slice = all.Skip((chunk - 1) * RtaWire.CentresPerChunk).Take(length / 2).ToArray();
            return slice.SelectMany(hz => new[] { (byte)(ushort)hz, (byte)((ushort)hz >> 8) }).ToArray();
        }

        public int Count(byte request) { lock (Reads) return Reads.Count(r => r.Request == request); }
    }

    private double _now;
    private (RtaEngine Engine, FakeDevice Device) Make(bool fetch = true)
    {
        var device = new FakeDevice();
        var engine = new RtaEngine(device, () => _now);
        if (fetch) engine.FetchCaps();
        return (engine, device);
    }

    private void Advance(double seconds) => _now += seconds;

    [Fact]
    public void CapsProbeReadsTheCentreTableInChunks()
    {
        var (engine, device) = Make();
        Assert.True(engine.Supported);
        Assert.Equal(RtaWire.MaxBands, engine.BandCentresHz.Count);
        Assert.Equal(10, engine.BandCentresHz[0]);
        Assert.Equal(1000, engine.BandCentresHz[20]);
        // Header, then 32 + 5 centres in two chunks.
        Assert.Equal(new ushort[] { 0, 1, 2 }, device.Reads.Where(r => r.Request == RtaWire.GetCaps).Select(r => r.Value));
    }

    [Fact]
    public void AStalledProbeLeavesItUnsupportedAndTicksDoNothing()
    {
        var device = new FakeDevice { Stall = true };
        var engine = new RtaEngine(device, () => _now);
        engine.FetchCaps();
        Assert.False(engine.Supported);
        engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 1));
        device.Reads.Clear();
        engine.Tick();
        Assert.Empty(device.Reads);
    }

    [Fact]
    public void NothingIsReadWhileNobodyWatches()
    {
        var (engine, device) = Make();
        device.Reads.Clear();
        engine.Tick();
        Assert.Empty(device.Reads);
        Assert.False(engine.IsWatching);
    }

    [Fact]
    public void OneChannelPushesTheConfigAndReadsItsBandsAndBins()
    {
        var (engine, device) = Make();
        engine.SetOptions(new RtaOptions(9, 500, 20));
        engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b1, WantsBins: true));
        engine.Tick();

        Assert.Equal(new RtaConfig(RtaWire.TapOutput, 1, 9, 500, 20, 0), Assert.Single(device.Pushed));
        Assert.Equal(1, device.Count(RtaWire.GetBands));
        Assert.Equal(0, device.Count(RtaWire.GetBandsAll));
        Assert.Equal(1, device.Count(RtaWire.GetBins));
        Assert.Equal(1, device.Count(RtaWire.GetStatus));

        var s = engine.Snapshot;
        Assert.Equal(RtaWire.TapOutput, s.Tap);
        Assert.NotNull(s.Frame(3, RtaWire.TapOutput) ?? s.Frame(0, RtaWire.TapOutput));
        Assert.NotNull(s.Bins);
        Assert.Equal(512, s.Bins!.LevelsDb!.Length);
        Assert.Equal(512, s.Bins.SmoothedLevelsDb!.Length);
        Assert.Null(s.Frame(0, RtaWire.TapInput));
    }

    [Fact]
    public void SeveralChannelsReadEveryFrameInOneTransfer()
    {
        var (engine, device) = Make();
        engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b101));
        engine.Tick();
        Assert.Equal(1, device.Count(RtaWire.GetBandsAll));
        Assert.Equal(0, device.Count(RtaWire.GetBands));
        Assert.Equal(new byte[] { 0, 2 }, engine.Snapshot.Frames.Keys.OrderBy(k => k));
        // No view asked for bins.
        Assert.Equal(0, device.Count(RtaWire.GetBins));
    }

    [Fact]
    public void RequestsAtTheNewestTapAreMergedAndAppliedConfigIsNotPushedAgain()
    {
        var (engine, device) = Make();
        var a = engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b0001));
        engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b0100));
        engine.Tick();
        Assert.Equal(0b0101, device.Pushed[^1].ChannelMask);

        Advance(0.06);
        engine.Tick();
        Assert.Single(device.Pushed);

        // A newer request at the other tap takes the device; only its own mask.
        var c = engine.Subscribe(new RtaRequest(RtaWire.TapInput, 0b0010));
        Advance(0.06);
        engine.Tick();
        Assert.Equal(new RtaConfig(RtaWire.TapInput, 0b0010), device.Pushed[^1]);
        Assert.Equal(RtaWire.TapInput, engine.Snapshot.Tap);

        // Re-publishing an unchanged request does not steal the tap back.
        engine.Update(a, new RtaRequest(RtaWire.TapOutput, 0b0001));
        Advance(0.06);
        engine.Tick();
        Assert.Equal(RtaWire.TapInput, device.Pushed[^1].Tap);
        engine.Release(c);
        Advance(0.06);
        engine.Tick();
        Assert.Equal(new RtaConfig(RtaWire.TapOutput, 0b0101), device.Pushed[^1]);
    }

    [Fact]
    public void ARefusedConfigIsTriedThreeTimesThenReported()
    {
        var (engine, device) = Make();
        // A real device answers GET_CONFIG with what it still runs.
        device.RefuseConfig = true;
        device.Applied = new RtaConfig();
        int changes = 0;
        engine.StateChanged += () => changes++;
        engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 0b10));
        for (int i = 0; i < 8; i++)
        {
            engine.Tick();
            Advance(0.6);
        }
        Assert.Equal(3, device.Pushed.Count);
        Assert.True(engine.ConfigRejected);
        Assert.True(changes > 0);

        // Asking for something else starts the count again.
        engine.SetOptions(new RtaOptions(9));
        Assert.False(engine.ConfigRejected);
        engine.Tick();
        Assert.Equal(4, device.Pushed.Count);
    }

    [Fact]
    public void StatusAndBinsFollowTheirOwnCadence()
    {
        var (engine, device) = Make();
        engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 1, WantsBins: true));
        engine.Tick();
        Advance(0.03);
        engine.Tick();
        // 30 ms later: bins wait for 50 ms, status for 500 ms.
        Assert.Equal(1, device.Count(RtaWire.GetBins));
        Assert.Equal(1, device.Count(RtaWire.GetStatus));
        Assert.Equal(2, device.Count(RtaWire.GetBands));
        Advance(0.03);
        engine.Tick();
        Assert.Equal(2, device.Count(RtaWire.GetBins));
        Advance(0.5);
        engine.Tick();
        Assert.Equal(2, device.Count(RtaWire.GetStatus));
    }

    [Fact]
    public void ATornBinReadIsReadAgain()
    {
        int reads = 0;
        var good = BinFrameBytes(nBins: 512, channel: 0);
        var torn = BinFrameBytes(nBins: 512, channel: 0, seq: 9, tail: 10);
        // The first whole-frame read is torn; the second sees the republished frame.
        var device = new WrappingDevice(new FakeDevice(), () => ++reads == 1 ? torn : good);
        var engine = new RtaEngine(device, () => _now);
        engine.FetchCaps();
        engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 1, WantsBins: true));
        engine.Tick();
        Assert.Equal(2, reads);
        Assert.NotNull(engine.Snapshot.Bins);
    }

    private sealed class WrappingDevice : IRtaTransport
    {
        private readonly FakeDevice _inner;
        private readonly Func<byte[]> _bins;
        public WrappingDevice(FakeDevice inner, Func<byte[]> bins) { _inner = inner; _bins = bins; }
        public byte[]? RtaIn(byte request, ushort value, int length) =>
            request == RtaWire.GetBins && value == 0 ? _bins().Take(length).ToArray() : _inner.RtaIn(request, value, length);
        public bool RtaOut(byte request, ushort value, byte[] data) => _inner.RtaOut(request, value, data);
    }

    [Fact]
    public void AStoppedStreamIsDrawnAsSilenceAndDropsItsBins()
    {
        var (engine, device) = Make();
        engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 1, WantsBins: true));
        device.Bands = c => BandFrameBytes(channel: c, ageMs: 10);
        engine.Tick();
        Assert.NotNull(engine.Snapshot.Bins);
        Assert.NotEqual(0, engine.Snapshot.Frame(0, RtaWire.TapOutput)!.Avg[0]);

        device.Bands = c => BandFrameBytes(channel: c, ageMs: 9000);
        Advance(0.06);
        engine.Tick();
        Assert.All(engine.Snapshot.Frame(0, RtaWire.TapOutput)!.Avg, v => Assert.Equal(0, v));
        Assert.Null(engine.Snapshot.Bins);
    }

    [Fact]
    public void TheFrameVersionMovesOnlyWhenThePictureChanges()
    {
        var (engine, device) = Make();
        engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 1));
        engine.Tick();
        long v = engine.FrameVersion;
        // Same levels, only age and sequence moved.
        device.Bands = c => BandFrameBytes(channel: c, seq: 99, ageMs: 30);
        Advance(0.06);
        engine.Tick();
        Assert.Equal(v, engine.FrameVersion);
        device.Bands = c => BandFrameBytes(channel: c, ageMs: 30, avg: 120);
        Advance(0.06);
        engine.Tick();
        Assert.True(engine.FrameVersion > v);
    }

    [Fact]
    public void ReleasingTheLastViewStopsTheDeviceAndClears()
    {
        var (engine, device) = Make();
        var id = engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 1));
        engine.Tick();
        Assert.NotEmpty(engine.Snapshot.Frames);
        engine.Release(id);
        SpinWait.SpinUntil(() => engine.Snapshot.Frames.Count == 0, 2000);
        Assert.Empty(engine.Snapshot.Frames);
        Assert.Contains((RtaWire.Control, RtaWire.CtlStop), device.Reads);
        Assert.False(engine.IsWatching);
    }

    [Fact]
    public void AnOutOfRangeSizeFallsBackToTheDevicesDefault()
    {
        var device = new FakeDevice();
        var engine = new RtaEngine(device, () => _now);
        engine.SetOptions(new RtaOptions(12));
        engine.FetchCaps();
        Assert.Equal(10, engine.Options.FftOrder);
    }

    [Fact]
    public void DisconnectForgetsTheDevice()
    {
        var (engine, device) = Make();
        engine.Subscribe(new RtaRequest(RtaWire.TapOutput, 1));
        engine.Tick();
        engine.DeviceDisconnected();
        Assert.False(engine.Supported);
        Assert.Empty(engine.Snapshot.Frames);
        // Reconnect: the same view's config is pushed to the new device.
        engine.FetchCaps();
        device.Pushed.Clear();
        engine.Tick();
        Assert.Single(device.Pushed);
    }

    [Fact]
    public void LevelsUseTheCapsZeroPoint()
    {
        var (engine, _) = Make();
        Assert.Equal(-20, engine.LevelDb(203), 6);
        Assert.Equal(-121.5, engine.FloorDb, 6);
    }
}
