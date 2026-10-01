using System.Numerics;

namespace DSPiConsole.Core.Rta;

/// <summary>Only the engine properties that affect a picture, so a renderer
/// captures this value rather than reading a live engine as it draws.</summary>
public sealed record RtaDisplayConfiguration(
    byte Tap, IReadOnlyList<double> Centres, uint SampleRateHz, int FftOrder,
    int BassBands, int FirstResolvedBand, byte LevelZero, int BandCount)
{
    public static RtaDisplayConfiguration From(RtaEngine engine)
    {
        var d = engine.Display;
        return new(d.Tap, engine.BandCentresHz, d.SampleRateHz, engine.Options.FftOrder,
                   engine.Caps.BassBands, d.FirstResolvedBand, engine.Caps.LevelZero, d.BandCount);
    }

    public double LevelDb(byte v) => RtaMath.LevelDb(v, LevelZero);

    /// <summary>Band slots a bar display lays out: the device's count once it
    /// has reported one, the centre table's before that.</summary>
    public int BarCount => BandCount > 0 ? Math.Min(BandCount, RtaWire.MaxBands) : Math.Max(Centres.Count, 34);
}

/// <summary>An axis-aligned plot rectangle in device-independent pixels.</summary>
public readonly record struct RtaPlot(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
}

/// <summary>
/// One bounded cache per drawing surface: band visibility and positions, the
/// bin-to-column projection and the curve's Hermite basis, each rebuilt only
/// when its inputs change. Port of the macOS Console's RtaRenderCache.
/// </summary>
public sealed class RtaRenderCache
{
    private (RtaDisplayConfiguration Config, int Count)? _bandKey;
    private int[] _bands = Array.Empty<int>();

    /// <summary>Bands that are measured at this size and rate.</summary>
    public int[] VisibleBands(RtaDisplayConfiguration configuration, int count)
    {
        if (_bandKey is { } k && k.Config == configuration && k.Count == count) return _bands;
        double rate = configuration.SampleRateHz > 0 ? configuration.SampleRateHz : 48000;
        _bands = Enumerable.Range(0, count)
            .Where(i => i >= configuration.FirstResolvedBand
                        && RtaMath.BandIsPopulated(i, rate, configuration.FftOrder, configuration.BassBands))
            .ToArray();
        _bandKey = (configuration, count);
        return _bands;
    }

    private (IReadOnlyList<double> Centres, double Min, double Max, float Width)? _axisKey;
    private float[] _bandX = Array.Empty<float>();

    public float[] BandPositions(IReadOnlyList<double> centres, double minFreq, double maxFreq, float width)
    {
        if (_axisKey is { } k && ReferenceEquals(k.Centres, centres) && k.Min == minFreq && k.Max == maxFreq && k.Width == width)
            return _bandX;
        _axisKey = (centres, minFreq, maxFreq, width);
        if (minFreq <= 0 || maxFreq <= minFreq)
            return _bandX = new float[centres.Count];
        double lo = Math.Log10(minFreq), span = Math.Log10(maxFreq) - lo;
        _bandX = centres.Select(c => (float)((Math.Log10(Math.Max(c, 1)) - lo) / span * width)).ToArray();
        return _bandX;
    }

    public readonly record struct BinGeometry(int Count, uint SampleRateHz, double MinFreq, double MaxFreq, float Width);

    private BinGeometry? _binGeometry;
    private readonly List<(int Bin, int Column)> _binColumns = new();
    private double[]? _sourceLevels;
    private (int FirstColumn, double[] Levels) _projection = (0, Array.Empty<double>());

    /// <summary>The loudest bin in each occupied column, then a straight line
    /// across the gaps: above a few hundred hertz several bins share a column
    /// and a maximum keeps a tone from vanishing between them; at the bottom
    /// of a log axis most columns fall between bins, and interpolating makes
    /// that a slope rather than a staircase.</summary>
    public (int FirstColumn, double[] Levels) ProjectBins(double[] levels, BinGeometry geometry)
    {
        int columns = Math.Max((int)Math.Round(geometry.Width), 2);
        if (_binGeometry != geometry)
        {
            _binGeometry = geometry;
            _sourceLevels = null;
            _binColumns.Clear();
            if (geometry.Count > 1 && geometry.MinFreq > 0 && geometry.MaxFreq > geometry.MinFreq)
            {
                double lo = Math.Log10(geometry.MinFreq), span = Math.Log10(geometry.MaxFreq) - lo;
                for (int k = 1; k < geometry.Count; k++)
                {
                    double hz = (double)k * geometry.SampleRateHz / (2.0 * geometry.Count);
                    if (hz < geometry.MinFreq || hz > geometry.MaxFreq) continue;
                    double x = (Math.Log10(Math.Max(hz, 1)) - lo) / span * geometry.Width;
                    _binColumns.Add((k, Math.Clamp((int)Math.Round(x), 0, columns - 1)));
                }
            }
        }
        if (_sourceLevels != null && levels.AsSpan().SequenceEqual(_sourceLevels)) return _projection;
        _sourceLevels = (double[])levels.Clone();
        var target = new double[columns];
        Array.Fill(target, double.NegativeInfinity);
        foreach (var (k, c) in _binColumns)
            if (k < levels.Length) target[c] = Math.Max(target[c], levels[k]);
        int first = Array.FindIndex(target, double.IsFinite), last = Array.FindLastIndex(target, double.IsFinite);
        if (first < 0 || last <= first) return _projection = (0, Array.Empty<double>());
        int previous = first;
        for (int c = first + 1; c <= last; c++)
        {
            if (!double.IsFinite(target[c])) continue;
            int gap = c - previous;
            if (gap > 1)
            {
                double a = target[previous], b = target[c];
                for (int g = 1; g < gap; g++) target[previous + g] = a + (b - a) * g / gap;
            }
            previous = c;
        }
        return _projection = (first, target[first..(last + 1)]);
    }

    private (float[] Positions, float Origin, int Columns)? _samplingKey;
    private readonly List<(int Column, int Segment, float A, float B, float C, float D)> _samples = new();

    /// <summary>The Hermite curve through <paramref name="points"/> at every
    /// whole column it spans, clamped into the plot. The basis is cached: band
    /// x positions do not move between frames, only their heights.</summary>
    public float?[] SampleBands(IReadOnlyList<Vector2> points, RtaPlot plot, int columns)
    {
        var xs = points.Select(p => p.X).ToArray();
        if (_samplingKey is not { } key || key.Origin != plot.X || key.Columns != columns || !key.Positions.AsSpan().SequenceEqual(xs))
        {
            _samplingKey = (xs, plot.X, columns);
            _samples.Clear();
            if (points.Count > 1)
            {
                int seg = 0;
                for (int column = 0; column < columns; column++)
                {
                    float x = plot.X + column;
                    if (x < points[0].X || x > points[^1].X) continue;
                    while (seg < points.Count - 2 && x > points[seg + 1].X) seg++;
                    float h = points[seg + 1].X - points[seg].X;
                    if (h <= 0)
                    {
                        _samples.Add((column, seg, 1, 0, 0, 0));
                        continue;
                    }
                    float t = (x - points[seg].X) / h, t2 = t * t, t3 = t2 * t;
                    _samples.Add((column, seg, 2 * t3 - 3 * t2 + 1, (t3 - 2 * t2 + t) * h, -2 * t3 + 3 * t2, (t3 - t2) * h));
                }
            }
        }
        var slopes = new float[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[Math.Max(i - 1, 0)];
            var b = points[Math.Min(i + 1, points.Count - 1)];
            slopes[i] = b.X > a.X ? (b.Y - a.Y) / (b.X - a.X) : 0;
        }
        var output = new float?[columns];
        foreach (var (column, i, a, b, c, d) in _samples)
        {
            float y = a * points[i].Y + b * slopes[i] + c * points[i + 1].Y + d * slopes[i + 1];
            output[column] = Math.Clamp(y, plot.Y, plot.Bottom);
        }
        return output;
    }
}

/// <summary>The per-channel filters a curve view needs: an average and a peak
/// contour for the bands, and one for the bins, with the shared cache.</summary>
public sealed class RtaCurveSmoothing
{
    public RtaRenderCache Cache { get; } = new();
    private readonly Dictionary<int, RtaBarSmoother> _filters = new();

    /// <summary><paramref name="kind"/>: 0 average bands, 1 peak bands, 2 bins.</summary>
    public RtaBarSmoother Smoother(int channel, int kind)
    {
        int key = channel << 4 | kind;
        if (!_filters.TryGetValue(key, out var s)) _filters[key] = s = new RtaBarSmoother();
        return s;
    }
}

/// <summary>
/// Turns the analyser's frames into points on a logarithmic frequency axis,
/// shared by the graph overlay and the analyser window: the bass bank's bands
/// where the bins are too coarse, the bins above them, one point per pixel
/// column. Port of the macOS Console's RtaCurveBuilder.
/// </summary>
public readonly struct RtaCurveBuilder
{
    private readonly RtaDisplayConfiguration _configuration;
    private readonly RtaCurveSmoothing _smoothing;
    private readonly double _minFreq, _maxFreq;
    private readonly RtaPlot _plot;
    private readonly RtaScale _scale;
    /// <summary>The display time, or null to draw the device's numbers as they are.</summary>
    private readonly double? _now;
    private readonly double _tau;

    public RtaCurveBuilder(RtaDisplayConfiguration configuration, RtaCurveSmoothing smoothing, double minFreq, double maxFreq,
                           RtaPlot plot, RtaScale scale, double? now, double tau)
    {
        _configuration = configuration;
        _smoothing = smoothing;
        _minFreq = minFreq;
        _maxFreq = maxFreq;
        _plot = plot;
        _scale = scale;
        _now = now;
        _tau = tau;
    }

    public float X(double hz)
    {
        if (_minFreq <= 0 || _maxFreq <= _minFreq) return _plot.X;
        double lo = Math.Log10(_minFreq), hi = Math.Log10(_maxFreq);
        return _plot.X + (float)((Math.Log10(Math.Max(hz, 1)) - lo) / (hi - lo)) * _plot.Width;
    }

    public float Y(double db) => _plot.Bottom - _plot.Height * (float)_scale.Norm(db);

    /// <summary>The third-octave picture as points. Bands with no bin at this
    /// size are left out rather than drawn at the floor, so the curve starts
    /// where the measurement does. One band either side of the axis range is
    /// kept, so the curve runs off the plot edges.</summary>
    public List<Vector2> BandPoints(RtaBandFrame frame, bool peak, int channel)
    {
        var points = new List<Vector2>();
        var centres = _configuration.Centres;
        if (centres.Count == 0) return points;
        int slots = Math.Min(frame.NBands > 0 ? frame.NBands : centres.Count, RtaWire.MaxBands);
        var source = peak ? frame.Peak : frame.Avg;
        var levels = new double[slots];
        for (int i = 0; i < slots; i++) levels[i] = i < source.Length ? _configuration.LevelDb(source[i]) : _scale.FloorDb;

        if (_now is { } now)
        {
            // A peak cap that eased upward would stop being a peak.
            levels = _smoothing.Smoother(channel, peak ? 1 : 0)
                .Step(now, levels, channel << 8 | slots, peak ? 0 : _tau * 0.4, _tau);
        }

        var visible = _smoothing.Cache.VisibleBands(_configuration, slots);
        var positions = _smoothing.Cache.BandPositions(centres, _minFreq, _maxFreq, _plot.Width);
        Vector2? below = null;
        foreach (int i in visible)
        {
            if (i >= centres.Count || i >= positions.Length || i >= levels.Length) continue;
            double hz = centres[i];
            var point = new Vector2(_plot.X + positions[i], _plot.Bottom - _plot.Height * (float)_scale.Norm(levels[i]));
            if (hz < _minFreq) { below = point; continue; }
            if (below is { } b) { points.Add(b); below = null; }
            points.Add(point);
            if (hz > _maxFreq) break;
        }
        return points;
    }

    /// <summary>One channel's picture from both products: the bands up to the
    /// top of the bass bank, where the bins are too coarse, and the bins above,
    /// crossfaded over about an octave so there is no step. One point per
    /// column; where only one product has data it is used alone.</summary>
    public List<Vector2> Blend(List<Vector2> bands, List<Vector2> bins)
    {
        if (bins.Count <= 1) return bands;
        int columns = Math.Max((int)Math.Round(_plot.Width), 2);
        int firstBinColumn = (int)Math.Round(bins[0].X - _plot.X);
        var bandY = _smoothing.Cache.SampleBands(bands, _plot, columns);

        var centres = _configuration.Centres;
        int bass = _configuration.BassBands;
        float loX = float.NegativeInfinity, hiX = float.NegativeInfinity;
        if (bass > 0 && bass <= centres.Count)
        {
            loX = X(centres[bass - 1]);
            hiX = X(centres[Math.Min(bass + 2, centres.Count - 1)]);
        }

        var points = new List<Vector2>(columns);
        for (int c = 0; c < columns; c++)
        {
            float x = _plot.X + c;
            int binIndex = c - firstBinColumn;
            float? binY = binIndex >= 0 && binIndex < bins.Count ? bins[binIndex].Y : null;
            float y;
            switch (bandY[c], binY)
            {
                case ({ } a, { } b):
                    float w = hiX > loX ? Math.Clamp((x - loX) / (hiX - loX), 0, 1) : 1;
                    y = a + (b - a) * w;
                    break;
                case ({ } a, null): y = a; break;
                case (null, { } b): y = b; break;
                default: continue;
            }
            points.Add(new Vector2(x, y));
        }
        return points;
    }

    /// <summary>The bins, averaged over time and smoothed across frequency,
    /// one point per column.</summary>
    public List<Vector2> BinPoints(RtaBinFrame frame, int channel)
    {
        var points = new List<Vector2>();
        if (frame.Bins.Length <= 1) return points;
        var config = _configuration;
        var smoothed = frame.SmoothedLevelsDb ?? RtaMath.SmoothBins(
            frame.LevelsDb ?? frame.Bins.Select(config.LevelDb).ToArray(), RtaMath.BinSmoothingOctaves);
        var (first, span) = _smoothing.Cache.ProjectBins(smoothed,
            new RtaRenderCache.BinGeometry(frame.Bins.Length, frame.SampleRateHz, _minFreq, _maxFreq, _plot.Width));
        if (span.Length <= 1) return points;
        if (_now is { } now)
            span = _smoothing.Smoother(channel, 2).Step(now, span, channel << 16 | span.Length, _tau * 0.4, _tau);
        for (int i = 0; i < span.Length; i++) points.Add(new Vector2(_plot.X + first + i, Y(span[i])));
        return points;
    }
}
