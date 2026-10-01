using System.Diagnostics;

namespace DSPiConsole.Core.Rta;

/// <summary>The vendor transfers the analyser needs. The device implements it;
/// tests substitute a fake.</summary>
public interface IRtaTransport
{
    /// <summary>An IN transfer, or null on a STALL or failure.</summary>
    byte[]? RtaIn(byte request, ushort value, int length);
    bool RtaOut(byte request, ushort value, byte[] data);
}

/// <summary>Everything the engine holds after a poll. Immutable: the engine
/// swaps in a new one, so a renderer can read it from any thread.</summary>
public sealed record RtaSnapshot
{
    public static RtaSnapshot Empty { get; } = new();

    /// <summary>Latest band frame per channel at <see cref="Tap"/>.</summary>
    public IReadOnlyDictionary<byte, RtaBandFrame> Frames { get; init; } = new Dictionary<byte, RtaBandFrame>();
    public RtaBinFrame? Bins { get; init; }
    public RtaStatus Status { get; init; } = new();
    /// <summary>The tap the frames were taken at. A view at the other tap must
    /// not draw them as its own: channel 0 means something else there.</summary>
    public byte Tap { get; init; } = RtaWire.TapOutput;

    /// <summary>The latest frame for a channel, only when taken at that tap.</summary>
    public RtaBandFrame? Frame(int channel, byte tap) =>
        Tap == tap && channel is >= 0 and < 256 && Frames.TryGetValue((byte)channel, out var f) ? f : null;

    /// <summary>Bands in the latest frames; 0 until one arrives.</summary>
    public int BandCount => Frames.Count == 0 ? 0 : Frames.Values.Max(f => (int)f.NBands);
}

/// <summary>The frame-derived state that decides a display's layout rather than
/// what it shows; it changes with the configuration or the rate, not per frame.</summary>
public readonly record struct RtaDisplayState(byte Tap, uint SampleRateHz, int FirstResolvedBand, int BandCount)
{
    public static RtaDisplayState From(RtaSnapshot s) =>
        new(s.Tap, s.Status.SampleRateHz, s.Status.FirstResolvedBand, s.BandCount);
}

/// <summary>
/// The app's half of the spectrum analyser: it owns the device configuration,
/// polls the band and bin frames, and republishes them. Port of the macOS
/// Console's RtaEngine.
/// <para>
/// Each on-screen analyser subscribes with what it wants; the engine folds the
/// requests into the one configuration the device's single FFT can run, the
/// newest request choosing the tap. <see cref="Tick"/> runs from the view
/// model's existing status poll, so all vendor traffic stays in one order, and
/// does nothing while nobody watches. Dropping the last subscription stops the
/// analyser on the device, which is how it costs nothing when nobody looks.
/// </para>
/// <para>
/// Frames never go through property-change notification: the latest
/// <see cref="Snapshot"/> is swapped in whole and <see cref="FrameVersion"/>
/// advances when the picture changed, for renderers to read as they draw.
/// <see cref="StateChanged"/> and <see cref="TelemetryChanged"/> are raised on
/// whichever thread made the change; listeners marshal to the UI themselves.
/// </para>
/// </summary>
public sealed class RtaEngine
{
    private readonly IRtaTransport _transport;
    private readonly Func<double> _clock;
    private readonly object _lock = new();
    private readonly object _publishLock = new();

    private readonly Dictionary<Guid, (long Seq, RtaRequest Request)> _requests = new();
    private long _requestSeq;
    /// <summary>What we believe the device applied; null means push again.</summary>
    private RtaConfig? _appliedConfig;
    /// <summary>The last config sent, so asking for something different resets
    /// the give-up count rather than inheriting it.</summary>
    private RtaConfig? _lastAttempted;
    private int _pushAttempts;
    private RtaOptions _pollOptions = new();
    /// <summary>How long the device takes to publish one frame, from the
    /// status; the bin reads follow it.</summary>
    private double _pollFrameInterval = 1024.0 / 48000.0;
    private double _lastBinRead = double.NegativeInfinity;
    private double _lastStatusRead = double.NegativeInfinity;
    private readonly RtaBinAverage _binAverage = new();
    private int _ticking;
    private int _generation;

    private volatile RtaSnapshot _snapshot = RtaSnapshot.Empty;
    private long _frameVersion;

    public RtaEngine(IRtaTransport transport, Func<double>? clock = null)
    {
        _transport = transport;
        _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }

    // ── Published state ──

    /// <summary>True once GET_CAPS answered with a complete centre table.
    /// Firmware without the analyser STALLs it: the whole feature gate.</summary>
    public bool Supported { get; private set; }
    public RtaCaps Caps { get; private set; } = new();
    /// <summary>Nominal third-octave centres from the caps table, so axis labels
    /// and band edges share one source.</summary>
    public IReadOnlyList<double> BandCentresHz { get; private set; } = Array.Empty<double>();
    /// <summary>The device-side options the app is asking for.</summary>
    public RtaOptions Options { get; private set; } = new();
    /// <summary>Set when the device has refused the configuration three times;
    /// views show the reason rather than an empty graph.</summary>
    public bool ConfigRejected { get; private set; }
    public RtaDisplayState Display { get; private set; }

    /// <summary>The latest frames, bins and status.</summary>
    public RtaSnapshot Snapshot => _snapshot;
    /// <summary>Advances whenever what a display draws changed.</summary>
    public long FrameVersion => Interlocked.Read(ref _frameVersion);

    /// <summary>Supported, caps, options, display state or rejection changed.</summary>
    public event Action? StateChanged;
    /// <summary>The status changed (about twice a second while running).</summary>
    public event Action? TelemetryChanged;

    public bool IsWatching { get { lock (_lock) return _requests.Count > 0; } }

    // ── Subscription ──

    /// <summary>Start watching; hand the token back to <see cref="Release"/>.</summary>
    public Guid Subscribe(RtaRequest request)
    {
        var id = Guid.NewGuid();
        Update(id, request);
        return id;
    }

    /// <summary>Change what a subscription wants without losing its place. An
    /// unchanged request keeps its sequence, so republishing the same thing
    /// does not steal the tap from a newer view.</summary>
    public void Update(Guid id, RtaRequest request)
    {
        lock (_lock)
        {
            if (_requests.TryGetValue(id, out var existing) && existing.Request == request) return;
            _requests[id] = (++_requestSeq, request);
        }
    }

    public void Release(Guid id)
    {
        bool empty;
        lock (_lock)
        {
            if (!_requests.Remove(id)) return;
            empty = _requests.Count == 0;
            if (empty) ResetPush();
        }
        if (!empty) return;
        // Nobody is watching. The device would stop itself in five seconds;
        // stopping it now hands the CPU back at once.
        Task.Run(() =>
        {
            if (IsWatching) return;
            _transport.RtaIn(RtaWire.Control, RtaWire.CtlStop, 1);
            // A view can come back before STOP completes; never clear data
            // that now belongs to it.
            if (IsWatching) return;
            lock (_publishLock)
            {
                _binAverage.Reset();
                Publish(RtaSnapshot.Empty);
            }
        });
    }

    /// <summary>Clear the running averages (the device's bands and the bins
    /// here) and the peak hold without disturbing the frame in flight.</summary>
    public void ResetAveraging()
    {
        lock (_publishLock) _binAverage.Reset();
        Task.Run(() => _transport.RtaIn(RtaWire.Control, RtaWire.CtlResetAvg, 1));
    }

    /// <summary>Adopt new device-side options, pushed on the next tick.</summary>
    public void SetOptions(RtaOptions options)
    {
        lock (_lock)
        {
            if (options == Options) return;
            Options = options;
            _pollOptions = options;
            ResetPush();
        }
        Interlocked.Increment(ref _frameVersion);
        ConfigRejected = false;
        StateChanged?.Invoke();
    }

    /// <summary>Push the configuration again on the next tick, after a device
    /// switch or anything else that could have reset the device's engine.</summary>
    public void ConfigurationChanged()
    {
        lock (_lock) ResetPush();
        if (ConfigRejected)
        {
            ConfigRejected = false;
            StateChanged?.Invoke();
        }
    }

    private void ResetPush()
    {
        _appliedConfig = null;
        _lastAttempted = null;
        _pushAttempts = 0;
    }

    // ── Capability probe ──

    /// <summary>Reads the caps header and the band-centre table, once per
    /// connect. A STALL leaves <see cref="Supported"/> false. Blocking.</summary>
    public void FetchCaps()
    {
        int generation = Volatile.Read(ref _generation);
        var caps = RtaCaps.Parse(_transport.RtaIn(RtaWire.GetCaps, 0, RtaWire.CapsSize));
        if (caps == null)
        {
            if (generation != Volatile.Read(ref _generation)) return;
            Supported = false;
            BandCentresHz = Array.Empty<double>();
            StateChanged?.Invoke();
            return;
        }

        // Centres arrive 32 to a chunk from wValue 1; the table ends with a
        // short or refused chunk.
        var centres = new List<double>();
        for (ushort chunk = 1; centres.Count < caps.MaxBands && chunk < 16; chunk++)
        {
            if (generation != Volatile.Read(ref _generation)) return;
            var c = _transport.RtaIn(RtaWire.GetCaps, chunk, RtaWire.CentresPerChunk * 2);
            if (c == null || c.Length < 2) break;
            for (int i = 0; i + 1 < c.Length; i += 2) centres.Add(c[i] | (c[i + 1] << 8));
            if (c.Length < RtaWire.CentresPerChunk * 2) break;
        }
        if (generation != Volatile.Read(ref _generation)) return;

        Caps = caps;
        BandCentresHz = centres.Take(caps.MaxBands).ToArray();
        Supported = centres.Count >= caps.MaxBands;
        // A size outside this device's range would be STALLed on every push.
        var o = Options;
        if (o.FftOrder < caps.FftOrderMin || o.FftOrder > caps.FftOrderMax)
            o = o with { FftOrder = caps.FftOrderDefault };
        Interlocked.Increment(ref _frameVersion);
        StateChanged?.Invoke();
        SetOptions(o);
        // A fresh device knows nothing of the previous one's configuration.
        ConfigurationChanged();
    }

    /// <summary>Forget everything about the device that went away.</summary>
    public void DeviceDisconnected()
    {
        Interlocked.Increment(ref _generation);
        lock (_lock) ResetPush();
        Supported = false;
        ConfigRejected = false;
        lock (_publishLock)
        {
            _binAverage.Reset();
            Publish(RtaSnapshot.Empty);
        }
        StateChanged?.Invoke();
    }

    // ── Polling ──

    /// <summary>
    /// One poll, from the view model's status poll thread. Each product has its
    /// own cadence in elapsed time, so a slower timer or a larger transform
    /// changes how often things are read, not what is read. Overlapping calls
    /// (a slow transfer outlasting the timer) are skipped.
    /// </summary>
    public void Tick()
    {
        if (!Supported) return;
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try { TickCore(); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    private void TickCore()
    {
        RtaConfig want;
        bool needsPush = false, wantsBins, readBins, readStatus;
        int bandSlots, bandFrameSize, binFrameLength;
        double now = _clock();
        lock (_lock)
        {
            if (_requests.Count == 0) return;
            var primary = _requests.Values.MaxBy(r => r.Seq).Request;
            ushort mask = primary.WantsBins
                ? primary.Mask
                : (ushort)_requests.Values.Where(r => r.Request.Tap == primary.Tap).Aggregate(0, (m, r) => m | r.Request.Mask);
            wantsBins = _requests.Values.Any(r => r.Request.WantsBins);
            want = new RtaConfig(primary.Tap, mask, _pollOptions.FftOrder, _pollOptions.AvgMs, _pollOptions.PeakDecayDbS, 0);
            // An empty mask is a STALL, and there is nothing to draw either.
            if (want.ChannelMask == 0) return;
            // Give up after three rejected pushes of the same config, so a
            // refused setting is not a write on every tick; asking for
            // something different starts the count again.
            if (_appliedConfig != want)
            {
                if (_lastAttempted != want)
                {
                    _lastAttempted = want;
                    _pushAttempts = 0;
                }
                if (_pushAttempts < 3)
                {
                    _pushAttempts++;
                    _appliedConfig = want;
                    needsPush = true;
                }
            }
            // Bins no faster than the device publishes them (a frame time
            // apart: 21 ms at 1024 points) and never faster than 20 a second.
            readBins = wantsBins && now - _lastBinRead >= Math.Max(_pollFrameInterval, RtaMath.MinBinInterval);
            readStatus = now - _lastStatusRead >= RtaMath.StatusInterval;
            if (readBins) _lastBinRead = now;
            if (readStatus) _lastStatusRead = now;
            bandSlots = Caps.MaxBands;
            bandFrameSize = Caps.BandFrameSize;
            binFrameLength = Caps.MaxBinFrame > 0 ? Caps.MaxBinFrame : RtaWire.BinFrameMax;
        }

        if (needsPush) _transport.RtaOut(RtaWire.SetConfig, 0, want.ToBytes());

        // Band frames. GET_BANDS_ALL answers for every live channel in one
        // transfer, each frame naming its own channel.
        var frames = new Dictionary<byte, RtaBandFrame>();
        if (System.Numerics.BitOperations.PopCount(want.ChannelMask) > 1)
        {
            var d = _transport.RtaIn(RtaWire.GetBandsAll, 0, bandFrameSize * 16);
            if (d != null)
                for (int off = 0; off + bandFrameSize <= d.Length; off += bandFrameSize)
                    if (RtaBandFrame.Parse(d, off, bandSlots) is { } f) frames[f.Channel] = f;
        }
        else
        {
            int ch = System.Numerics.BitOperations.TrailingZeroCount(want.ChannelMask);
            if (RtaBandFrame.Parse(_transport.RtaIn(RtaWire.GetBands, (ushort)ch, bandFrameSize), 0, bandSlots) is { } f)
                frames[f.Channel] = f;
        }

        var bins = readBins ? ReadBinFrame(binFrameLength) : null;
        double binsReadAt = _clock();

        RtaStatus? status = null;
        RtaConfig? applied = null;
        if (readStatus)
        {
            status = RtaStatus.Parse(_transport.RtaIn(RtaWire.GetStatus, 0, RtaWire.StatusSize));
            applied = RtaConfig.Parse(_transport.RtaIn(RtaWire.GetConfig, 0, RtaWire.ConfigSize));
        }
        if (status != null)
            lock (_lock) _pollFrameInterval = RtaMath.ChannelRefreshInterval(status, want.FftOrder);

        // The device clamps averaging and peak decay rather than refusing
        // them, so compare only the fields it either takes or STALLs on.
        bool? rejected = null;
        if (applied is { } a)
        {
            lock (_lock)
            {
                if (a.Tap == want.Tap && a.ChannelMask == want.ChannelMask && a.FftOrder == want.FftOrder)
                {
                    _pushAttempts = 0;
                    want = want with { AvgMs = a.AvgMs, PeakDecayDbS = a.PeakDecayDbS };
                    _appliedConfig = want;
                    rejected = false;
                }
                else if (_pushAttempts >= 3) rejected = true;
                // Not applied yet (the device applies a staged config from its
                // main loop) or refused: try again next tick.
                else _appliedConfig = null;
            }
        }

        lock (_publishLock)
        {
            // Released while this tick was reading: leave the cleared state.
            if (!IsWatching) return;
            var s = _snapshot;
            var merged = s.Tap == want.Tap ? new Dictionary<byte, RtaBandFrame>(s.Frames) : new Dictionary<byte, RtaBandFrame>();
            var held = s.Tap == want.Tap ? s.Bins : null;
            var (fresh, stale) = RtaMath.SilencingStale(frames, StaleAfterMs(s.Status));
            foreach (var (ch, f) in fresh) merged[ch] = f;
            if (bins != null)
            {
                var levels = _binAverage.Add(bins, want.Tap, binsReadAt, want.AvgMs, LevelDb);
                held = bins with { LevelsDb = levels, SmoothedLevelsDb = _binAverage.SmoothedLevels };
            }
            // Bins carry no age of their own; they go stale with their channel.
            if (held != null && stale.Contains(held.Channel))
            {
                held = null;
                _binAverage.Reset();
            }
            Publish(new RtaSnapshot { Tap = want.Tap, Frames = merged, Bins = held, Status = status ?? s.Status });
        }
        if (rejected is { } r && r != ConfigRejected)
        {
            ConfigRejected = r;
            StateChanged?.Invoke();
        }
    }

    /// <summary>A few rotation intervals, so a slow rotation through many
    /// channels is not mistaken for a stopped stream.</summary>
    private double StaleAfterMs(RtaStatus status) =>
        Math.Max(500, RtaMath.ChannelRefreshInterval(status, Options.FftOrder) * 4000);

    /// <summary>How often one channel refreshes, for the displays' smoothing.</summary>
    public double ChannelRefreshInterval => RtaMath.ChannelRefreshInterval(_snapshot.Status, Options.FftOrder);

    /// <summary>Adopt a snapshot: the frame version moves only when something a
    /// display draws changed, telemetry and display state only when they did.
    /// Caller holds <see cref="_publishLock"/>.</summary>
    private void Publish(RtaSnapshot s)
    {
        var old = _snapshot;
        _snapshot = s;
        bool statusChanged = s.Status != old.Status;
        if (s.Tap != old.Tap || statusChanged || !BinsDrawSame(s.Bins, old.Bins) || !FramesDrawSame(s.Frames, old.Frames))
            Interlocked.Increment(ref _frameVersion);
        if (statusChanged) TelemetryChanged?.Invoke();
        var d = RtaDisplayState.From(s);
        if (d != Display)
        {
            Display = d;
            StateChanged?.Invoke();
        }
    }

    private static bool BinsDrawSame(RtaBinFrame? a, RtaBinFrame? b) =>
        a == null ? b == null : b != null && a.DrawsSame(b) && ReferenceEquals(a.LevelsDb, b.LevelsDb);

    /// <summary>Whether two sets of band frames draw the same picture.</summary>
    public static bool FramesDrawSame(IReadOnlyDictionary<byte, RtaBandFrame> a, IReadOnlyDictionary<byte, RtaBandFrame> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (ch, f) in a)
            if (!b.TryGetValue(ch, out var g) || !f.DrawsSame(g)) return false;
        return true;
    }

    // ── Reads that need more than one transfer ──

    /// <summary>Reads and validates the published bin frame. wValue is a byte
    /// offset, so a short answer is picked up where it stopped; a torn read
    /// (the engine republished mid-read) is read again rather than drawn.</summary>
    private RtaBinFrame? ReadBinFrame(int length)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var frame = new List<byte>(length);
            while (frame.Count < length)
            {
                var chunk = _transport.RtaIn(RtaWire.GetBins, (ushort)frame.Count, length - frame.Count);
                if (chunk == null || chunk.Length == 0) break;
                frame.AddRange(chunk);
                // A frame is shorter than the ceiling below the largest size:
                // stop once the header's own length is in.
                if (RtaBinFrame.FrameLength(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(frame)) is { } n && frame.Count >= n) break;
            }
            if (RtaBinFrame.Parse(frame.ToArray()) is { } f) return f;
        }
        return null;
    }

    // ── Levels ──

    /// <summary>dBFS of one wire level byte, against the caps' zero point.</summary>
    public double LevelDb(byte v) => RtaMath.LevelDb(v, Caps.LevelZero);

    /// <summary>The lowest level the wire carries: what an empty band and a
    /// silent channel both read.</summary>
    public double FloorDb => LevelDb(0);

    // ── Reading helpers ──

    /// <summary>Whether a band is measured at the current size and rate: the
    /// bass bank always, the FFT bands when a bin falls inside them.</summary>
    public bool TransformHasBin(int band)
    {
        if (band < 0 || band >= BandCentresHz.Count) return true;
        double rate = Display.SampleRateHz > 0 ? Display.SampleRateHz : 48000;
        return RtaMath.BandIsPopulated(band, rate, Options.FftOrder, Caps.BassBands);
    }

    /// <summary>"2 channels, each refreshed every 43 ms", or "idle".</summary>
    public string RefreshDescription
    {
        get
        {
            var s = _snapshot.Status;
            if (!s.IsRunning || s.LiveCount == 0) return "idle";
            int ms = (int)Math.Round(ChannelRefreshInterval * 1000);
            return $"{(s.LiveCount == 1 ? "1 channel" : $"{s.LiveCount} channels")}, each refreshed every {ms} ms";
        }
    }
}
