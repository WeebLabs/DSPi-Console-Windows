using System.Numerics;
using DSPiConsole.Core.Rta;

namespace DSPiConsole.Core.Tests;

/// <summary>The analyser's drawing geometry, ported from the macOS Console's
/// RtaRenderingTests.</summary>
public class RtaCurveTests
{
    private static readonly double[] Centres = Enumerable.Range(0, RtaWire.MaxBands).Select(i => Math.Round(10 * Math.Pow(10, i / 10.0))).ToArray();

    private static RtaDisplayConfiguration Config(int firstResolved = 0, uint rate = 48000, int order = 10) =>
        new(RtaWire.TapOutput, Centres, rate, order, RtaWire.BassBands, firstResolved, RtaWire.LevelZeroDbfs, 34);

    [Fact]
    public void ProjectionRetainsANarrowPeakAndInterpolatesEmptyColumns()
    {
        var cache = new RtaRenderCache();
        // Bin spacing is 1 Hz; on this log axis bins 1, 2 and 4 land at 0, 50, 100.
        var geometry = new RtaRenderCache.BinGeometry(8, 16, 1, 4, 100);
        var levels = Enumerable.Repeat(-100.0, 8).ToArray();
        levels[1] = -60;
        levels[2] = -20;
        var result = cache.ProjectBins(levels, geometry);
        Assert.Equal(0, result.FirstColumn);
        Assert.Equal(-60, result.Levels[0]);
        Assert.Equal(-40, result.Levels[25], 9);
        Assert.Equal(-20, result.Levels[50]);
        levels[2] = -10;
        Assert.Equal(-10, cache.ProjectBins(levels, geometry).Levels[50]);
        Assert.Equal(new double[] { -60, -10 }, cache.ProjectBins(levels, geometry with { Width = 2 }).Levels);
    }

    [Fact]
    public void ProjectionFollowsRateRangeAndSizeWithUnchangedLevels()
    {
        var cache = new RtaRenderCache();
        var levels = Enumerable.Range(0, 512).Select(i => (double)(i % 37) - 80).ToArray();
        foreach (int count in new[] { 512, 128 })
            foreach (uint rate in new uint[] { 48000, 96000 })
                foreach (float width in new[] { 52f, 760f, 1000.5f })
                    foreach (double lo in new[] { 10.0, 1000.0 })
                    {
                        var g = new RtaRenderCache.BinGeometry(count, rate, lo, 20000, width);
                        var reused = cache.ProjectBins(levels, g);
                        var fresh = new RtaRenderCache().ProjectBins(levels, g);
                        Assert.Equal(fresh.FirstColumn, reused.FirstColumn);
                        Assert.Equal(fresh.Levels, reused.Levels);
                    }
    }

    [Fact]
    public void BandGeometryFollowsConfigurationAndResize()
    {
        var cache = new RtaRenderCache();
        Assert.Equal(new[] { 0, 1, 2 }, cache.VisibleBands(Config(), 10)[..3]);
        Assert.All(cache.VisibleBands(Config(firstResolved: 5), 10), b => Assert.True(b >= 5));
        var high = Config(rate: 96000, order: 8);
        Assert.Equal(new RtaRenderCache().VisibleBands(high, 20), cache.VisibleBands(high, 20));
        var three = new double[] { 10, 100, 1000 };
        Assert.Equal(new float[] { 0, 50, 100 }, cache.BandPositions(three, 10, 1000, 100));
        Assert.Equal(new float[] { 0, 100, 200 }, cache.BandPositions(three, 10, 1000, 200));
    }

    [Fact]
    public void CachedBandSamplingKeepsTheLineAndFollowsHeightsAndClipping()
    {
        var cache = new RtaRenderCache();
        var plot = new RtaPlot(10, 0, 100, 100);
        var points = new[] { new Vector2(10, 20), new Vector2(60, 50), new Vector2(110, 80) };
        foreach (float shift in new[] { 0f, 10f, 70f })
        {
            var samples = cache.SampleBands(points.Select(p => p with { Y = p.Y + shift }).ToArray(), plot, 100);
            for (int c = 0; c < 100; c++)
                Assert.Equal(Math.Min(100, 20 + c * 0.6f + shift), samples[c]!.Value, 3);
        }
        Assert.All(cache.SampleBands(Array.Empty<Vector2>(), plot, 100), v => Assert.Null(v));
    }

    [Fact]
    public void SixtyFramesASecondKeepTheSmoothingTimeConstant()
    {
        Assert.Equal(1.0 / 60.0, RtaMath.FrameInterval);
        double Interpolate(int fps, double target)
        {
            var s = new RtaBarSmoother();
            s.Step(0, new double[] { -40 }, 0, 0.08, 0.2);
            double[] v = { -40 };
            for (int f = 1; f <= fps; f++) v = s.Step((double)f / fps, new[] { target }, 0, 0.08, 0.2);
            return v[0];
        }
        foreach (double target in new[] { -10.0, -80.0 })
            Assert.Equal(Interpolate(30, target), Interpolate(60, target), 9);
    }

    [Fact]
    public void BlendCrossfadesFromTheBassBandsToTheBins()
    {
        var smoothing = new RtaCurveSmoothing();
        var plot = new RtaPlot(0, 0, 400, 100);
        var builder = new RtaCurveBuilder(Config(), smoothing, 10, 20000, plot, new RtaScale(-90, 6), null, 0);
        var frame = new RtaBandFrame { NBands = 34, AgeMs = 0, Avg = Enumerable.Repeat((byte)203, RtaWire.MaxBands).ToArray() };
        var bands = builder.BandPoints(frame, peak: false, channel: 0);
        Assert.True(bands.Count > 10);
        float bandY = builder.Y(-20);
        Assert.All(bands, p => Assert.Equal(bandY, p.Y, 3));
        // Bins a level higher across the whole axis.
        var bins = Enumerable.Range(0, 400).Select(c => new Vector2(c, builder.Y(-10))).ToList();
        var blend = builder.Blend(bands, bins);
        Assert.Equal(bandY, blend.First(p => p.X > builder.X(20)).Y, 3);
        Assert.Equal(builder.Y(-10), blend.Last().Y, 3);
    }
}
