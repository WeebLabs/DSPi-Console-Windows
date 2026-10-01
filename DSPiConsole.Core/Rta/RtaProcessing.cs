namespace DSPiConsole.Core.Rta;

/// <summary>The analyser's host-side arithmetic, shared by the engine and every
/// display. Ports of the macOS Console's free functions of the same names.</summary>
public static class RtaMath
{
    /// <summary>Width each FFT bin is averaged over. Bins are evenly spaced, so
    /// the treble otherwise shows far finer detail than the bass bands beside it.</summary>
    public const double BinSmoothingOctaves = 1.0 / 6.0;

    /// <summary>Displays interpolate at 60 fps, independently of the device.</summary>
    public const double FrameInterval = 1.0 / 60.0;

    /// <summary>Bins are read no faster than this, however small the transform.</summary>
    public const double MinBinInterval = 0.05;

    /// <summary>Status and the applied config are read this often.</summary>
    public const double StatusInterval = 0.5;

    /// <summary>dBFS of one wire level byte against the caps' zero point.</summary>
    public static double LevelDb(byte v, byte levelZero) =>
        (v - (levelZero != 0 ? levelZero : RtaWire.LevelZeroDbfs)) * RtaWire.LevelStepDb;

    /// <summary>Each bin's level averaged in power over <paramref name="octaves"/>
    /// centred on it. Where that window is narrower than a bin, as across the low
    /// end, the bin is left alone. A pure treble tone is spread and reads low.</summary>
    public static double[] SmoothBins(double[] levelsDb, double octaves)
    {
        int n = levelsDb.Length;
        if (octaves <= 0 || n <= 2) return levelsDb;
        var prefix = new double[n + 1];
        for (int k = 0; k < n; k++) prefix[k + 1] = prefix[k] + Math.Pow(10, levelsDb[k] / 10);
        // Bin frequency is proportional to its index, so the window is a ratio.
        double half = Math.Pow(2, octaves / 2);
        var output = (double[])levelsDb.Clone();
        for (int k = 1; k < n; k++)
        {
            int lo = Math.Max(1, (int)Math.Ceiling(k / half));
            int hi = Math.Min(n - 1, (int)Math.Floor(k * half));
            if (hi <= lo) continue;
            double mean = (prefix[hi + 1] - prefix[lo]) / (hi - lo + 1);
            output[k] = 10 * Math.Log10(Math.Max(mean, 1e-30));
        }
        return output;
    }

    /// <summary>Whether an FFT of <paramref name="fftOrder"/> points has any
    /// bin inside the third-octave band centred on <paramref name="centreHz"/>.
    /// A display heuristic from nominal edges: it fails open on nonsense, so it
    /// can explain an empty band but never hide a live one.</summary>
    public static bool BandHasBin(double centreHz, double sampleRateHz, int fftOrder)
    {
        if (centreHz <= 0 || sampleRateHz <= 0 || fftOrder <= 0) return true;
        double edge = Math.Pow(2, 1.0 / 6.0);
        double lo = centreHz / edge, hi = centreHz * edge;
        double binHz = sampleRateHz / (1 << fftOrder);
        if (binHz <= 0) return true;
        // DC belongs to no band, so the search starts at bin 1.
        double firstBin = Math.Max(1, Math.Ceiling(lo / binHz));
        return firstBin * binHz <= hi;
    }

    /// <summary>Bass bands are always populated. Higher bands follow the
    /// firmware's FFT geometry: base-10 centre 1000 * 10^((i - 20) / 10), edges
    /// at 10^(+/-0.05), DC and Nyquist excluded.</summary>
    public static bool BandIsPopulated(int band, double sampleRateHz, int fftOrder, int bassBands = RtaWire.BassBands)
    {
        if (band >= 0 && band < bassBands) return true;
        if (band < 0 || sampleRateHz <= 0 || fftOrder <= 0) return true;
        double fc = 1000.0 * Math.Pow(10, (band - 20) / 10.0);
        double lo = fc * Math.Pow(10, -0.05), hi = fc * Math.Pow(10, 0.05);
        double n = 1 << fftOrder;
        double binHz = sampleRateHz / n;
        double firstBin = Math.Max(1, Math.Ceiling(lo / binHz));
        double lastBin = Math.Min(n / 2 - 1, Math.Floor(hi / binHz));
        return firstBin <= lastBin;
    }

    /// <summary>The smoothing preference as a fall time constant for a rotation
    /// interval: roughly one interval to travel most of the way, clamped so one
    /// fast channel does not step and nine slow ones do not turn to syrup. Zero
    /// means smoothing is off.</summary>
    public static double FallTau(double refreshInterval, double amount) =>
        amount <= 0 ? 0 : Math.Min(0.40, Math.Max(0.035, refreshInterval * amount));

    /// <summary>How long one channel waits between frames: from the device's
    /// own frame rate when it has one, else the fill time, times the channels
    /// sharing the rotation.</summary>
    public static double ChannelRefreshInterval(RtaStatus s, int fftOrder)
    {
        int live = Math.Max((int)s.LiveCount, 1);
        if (s.FramesPerSecond > 0) return (double)live / s.FramesPerSecond;
        double rate = s.SampleRateHz > 0 ? s.SampleRateHz : 48000;
        return (1 << fftOrder) / rate * live;
    }

    /// <summary>Frames that have stopped updating, replaced by silence. The
    /// device only publishes while audio arrives; when the host stops streaming,
    /// each channel keeps its last levels forever and only its age grows.</summary>
    public static (Dictionary<byte, RtaBandFrame> Frames, HashSet<byte> Stale) SilencingStale(
        IReadOnlyDictionary<byte, RtaBandFrame> frames, double staleAfterMs)
    {
        var output = new Dictionary<byte, RtaBandFrame>(frames);
        var stale = new HashSet<byte>();
        foreach (var (ch, f) in frames)
        {
            if (!f.HasData || f.AgeMs <= staleAfterMs) continue;
            output[ch] = f.Silenced();
            stale.Add(ch);
        }
        return (output, stale);
    }
}

/// <summary>
/// The bins averaged over time the way the device averages its bands: power,
/// moved toward each new frame by dt / (avg + dt). The device never averages
/// the bins it publishes, so without this the Averaging setting would stop at
/// the bass. Times are in seconds.
/// </summary>
public sealed class RtaBinAverage
{
    private (byte Tap, byte Channel, byte Order, uint Rate, int Count)? _series;
    private int _seq = -1;
    private double _time;
    private double[] _power = Array.Empty<double>();

    public double[] Levels { get; private set; } = Array.Empty<double>();
    public double[] SmoothedLevels { get; private set; } = Array.Empty<double>();

    public void Reset() => _series = null;

    /// <summary>Averaged dBFS per bin. A frame already counted (same sequence)
    /// changes nothing; another tap, channel, size or rate starts over.</summary>
    public double[] Add(RtaBinFrame frame, byte tap, double now, int avgMs, Func<byte, double> levelDb)
    {
        var key = (tap, frame.Channel, frame.FftOrder, frame.SampleRateHz, frame.Bins.Length);
        if (_series == key && frame.Seq == _seq) return Levels;

        var fresh = new double[frame.Bins.Length];
        for (int i = 0; i < fresh.Length; i++) fresh[i] = Math.Pow(10, levelDb(frame.Bins[i]) / 10);
        if (_series != key || avgMs == 0)
        {
            _power = fresh;
        }
        else
        {
            double dt = Math.Max(now - _time, 0);
            double a = dt / (avgMs / 1000.0 + dt);
            for (int i = 0; i < _power.Length; i++) _power[i] += (fresh[i] - _power[i]) * a;
        }
        _series = key;
        _seq = frame.Seq;
        _time = now;
        var levels = new double[_power.Length];
        for (int i = 0; i < levels.Length; i++) levels[i] = 10 * Math.Log10(Math.Max(_power[i], 1e-30));
        Levels = levels;
        SmoothedLevels = RtaMath.SmoothBins(levels, RtaMath.BinSmoothingOctaves);
        return Levels;
    }
}

/// <summary>
/// Glides a display between device frames: one pole per value, stepped by real
/// elapsed time so a dropped display frame catches up rather than lagging. The
/// fall is slower than the rise. A new identity (channel, band count) or length
/// snaps rather than slides, since sliding would show the new series wearing
/// the old one's levels. A tau of zero moves instantly in that direction.
/// </summary>
public sealed class RtaBarSmoother
{
    private double[] _values = Array.Empty<double>();
    private double? _lastTime;
    private long _identity = long.MinValue;

    public double[] Step(double now, double[] target, long identity, double riseTau, double fallTau)
    {
        if (identity != _identity || _values.Length != target.Length)
        {
            _identity = identity;
            _values = (double[])target.Clone();
            _lastTime = now;
            return _values;
        }
        double dt = _lastTime is { } t ? now - t : 0;
        if (dt <= 0) return _values;
        _lastTime = now;
        double riseK = riseTau > 0 ? 1 - Math.Exp(-dt / riseTau) : 1;
        double fallK = fallTau > 0 ? 1 - Math.Exp(-dt / fallTau) : 1;
        for (int i = 0; i < _values.Length; i++)
        {
            double ti = target[i], vi = _values[i];
            _values[i] = vi + (ti - vi) * (ti > vi ? riseK : fallK);
        }
        return _values;
    }
}

/// <summary>The vertical scale every analyser view shares: the ceiling at the
/// top, the floor at the bottom.</summary>
public readonly record struct RtaScale(double FloorDb, double CeilingDb)
{
    public double Norm(double db) => CeilingDb <= FloorDb ? 0 : Math.Clamp((db - FloorDb) / (CeilingDb - FloorDb), 0, 1);
}

/// <summary>
/// The channels a page's spectrum shows: any number, all at one tap. Inputs and
/// outputs never mix, because the device listens at one tap at a time.
/// </summary>
public sealed record RtaChannelSelection
{
    public byte Tap { get; }
    /// <summary>Channels at <see cref="Tap"/>, ascending and distinct.</summary>
    public IReadOnlyList<int> Channels { get; }

    public RtaChannelSelection(byte tap, IEnumerable<int> channels)
    {
        Tap = tap;
        Channels = channels.Distinct().OrderBy(c => c).ToArray();
    }

    public static RtaChannelSelection None { get; } = new(RtaWire.TapOutput, Array.Empty<int>());

    public bool IsEmpty => Channels.Count == 0;

    public ushort Mask => (ushort)Channels.Aggregate(0, (m, c) => m | (1 << c));

    /// <summary>"in:0,2" or "out:1". An empty list is a deliberate nothing.
    /// The same spelling as the macOS Console's preference.</summary>
    public string StorageKey => (Tap == RtaWire.TapInput ? "in:" : "out:") + string.Join(",", Channels);

    public static RtaChannelSelection? FromStorageKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        int colon = key.IndexOf(':');
        if (colon < 0) return null;
        byte tap;
        switch (key[..colon])
        {
            case "in": tap = RtaWire.TapInput; break;
            case "out": tap = RtaWire.TapOutput; break;
            default: return null;
        }
        var channels = new List<int>();
        foreach (var item in key[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(item, out int n) || n < 0 || n >= 16) return null;
            channels.Add(n);
        }
        return new RtaChannelSelection(tap, channels);
    }

    public bool Contains(byte tap, int channel) => Tap == tap && Channels.Contains(channel);

    /// <summary>Whether a channel at <paramref name="tap"/> can be checked
    /// without mixing taps.</summary>
    public bool Accepts(byte tap) => IsEmpty || Tap == tap;

    /// <summary>This selection with one channel checked or unchecked. A channel
    /// at the other tap is ignored; the menus disable those.</summary>
    public RtaChannelSelection Toggling(byte tap, int channel)
    {
        if (!Accepts(tap)) return this;
        var next = Channels.Contains(channel) && Tap == tap
            ? Channels.Where(c => c != channel)
            : (Tap == tap ? Channels : Array.Empty<int>()).Append(channel);
        return new RtaChannelSelection(tap, next);
    }

    /// <summary>Only the channels in <paramref name="live"/>, keeping the tap.</summary>
    public RtaChannelSelection Restricted(IReadOnlyCollection<int> live) => new(Tap, Channels.Where(live.Contains));

    /// <summary>Moving to the other side: the side being left is remembered, and
    /// the side being entered comes back as it was left, or empty. Asking for
    /// the side already showing changes nothing.</summary>
    public static (RtaChannelSelection Active, RtaChannelSelection? Remembered) SwitchingSides(
        RtaChannelSelection active, RtaChannelSelection? remembered, byte tap)
    {
        if (tap == active.Tap) return (active, remembered);
        var restored = remembered != null && remembered.Tap == tap ? remembered : new RtaChannelSelection(tap, Array.Empty<int>());
        return (restored, active);
    }

    public bool Equals(RtaChannelSelection? other) =>
        other is not null && Tap == other.Tap && Channels.SequenceEqual(other.Channels);

    public override int GetHashCode() => HashCode.Combine(Tap, Mask);
}
