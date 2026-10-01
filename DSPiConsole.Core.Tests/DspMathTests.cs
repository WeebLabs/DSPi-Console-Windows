using DSPiConsole.Core;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Tests;

/// <summary>
/// Frequency-response landmarks, ported from the macOS Console's
/// DSPMathTests.swift. They check analytic points (peak gain at fc, −3 dB at a
/// Butterworth corner, −6 dB at a Linkwitz-Riley crossover) rather than
/// re-deriving the RBJ formulas, so they catch evaluation regressions on
/// their own.
/// </summary>
public class DspMathTests
{
    private static float Mag(float f, params FilterParams[] filters) => DspMath.ResponseAt(f, filters);
    private static float Phase(float f, params FilterParams[] filters) => DspMath.PhaseAt(f, filters);

    [Fact]
    public void FlatFilterIsTransparent()
    {
        var flat = new FilterParams(FilterType.Flat, 1000, 0.707f, 0);
        foreach (float f in new float[] { 20, 100, 1000, 5000, 18000 })
        {
            Assert.InRange(Mag(f, flat), -1e-4f, 1e-4f);
            Assert.InRange(Phase(f, flat), -1e-4f, 1e-4f);
        }
    }

    [Fact]
    public void PeakingGainAtCenterEqualsSetGain()
    {
        var peak = new FilterParams(FilterType.Peaking, 1000, 2f, 6f);
        Assert.InRange(Mag(1000, peak), 5.95f, 6.05f);
        Assert.InRange(Mag(20, peak), -0.3f, 0.3f);
        Assert.InRange(Mag(18000, peak), -0.3f, 0.3f);
    }

    /// <summary>The direct |H|² form lost the resonance of low, narrow
    /// sections: a 10 Hz, Q 20 bell read 0 dB at its own peak. The φ form keeps
    /// it, and the on-graph editor's dots sit on this curve.</summary>
    [Theory]
    [InlineData(10f, 20f, 12f)]
    [InlineData(12f, 20f, -12f)]
    [InlineData(15f, 10f, 6f)]
    public void LowNarrowBellKeepsItsPeak(float freq, float q, float gain)
    {
        var bell = new FilterParams(FilterType.Peaking, freq, q, gain);
        Assert.InRange(Mag(freq, bell), gain - 0.1f, gain + 0.1f);
    }

    [Fact]
    public void ButterworthLowPassIsMinus3dBAtCutoff()
    {
        var lp = new FilterParams(FilterType.LowPass, 1000, 0.7071f, 0);
        Assert.InRange(Mag(1000, lp), -3.31f, -2.71f);
        Assert.InRange(Mag(100, lp), -0.5f, 0.5f);
        Assert.True(Mag(10000, lp) < -30);
    }

    [Fact]
    public void NotchRejectsAtCenterOnly()
    {
        var notch = new FilterParams(FilterType.Notch, 1000, 5f, 0);
        Assert.True(Mag(1000, notch) < -30);
        Assert.InRange(Mag(100, notch), -0.5f, 0.5f);
        Assert.InRange(Mag(10000, notch), -0.5f, 0.5f);
    }

    [Fact]
    public void FirstOrderAllPassFlatMagnitudeAndMinus90AtFc()
    {
        var ap = new FilterParams(FilterType.AllPass1, 1000, 0.707f, 0);
        foreach (float f in new float[] { 100, 1000, 10000 })
            Assert.InRange(Mag(f, ap), -1e-3f, 1e-3f);
        Assert.InRange(Phase(1000, ap), -92f, -88f);
    }

    [Fact]
    public void FirstOrderPassIsMinus3dBAtCornerAndHalfAsSteep()
    {
        var lp1 = new FilterParams(FilterType.LowPass1, 1000, 5f, 0);
        var hp1 = new FilterParams(FilterType.HighPass1, 1000, 5f, 0);
        Assert.InRange(Mag(1000, lp1), -3.21f, -2.81f);
        Assert.InRange(Mag(1000, hp1), -3.21f, -2.81f);
        Assert.InRange(Mag(50, lp1), -0.2f, 0.2f);
        Assert.InRange(Mag(18000, hp1), -0.3f, 0.3f);
        Assert.InRange(Mag(10000, lp1), -21.5f, -18.5f);
        Assert.InRange(Mag(100, hp1), -21.5f, -18.5f);

        var lp2 = new FilterParams(FilterType.LowPass, 1000, 0.7071f, 0);
        Assert.InRange(Mag(4000, lp1) - Mag(4000, lp2) / 2, -1.5f, 1.5f);
    }

    [Fact]
    public void LinkwitzRiley4LowPassShape()
    {
        var lr4 = new FilterParams(FilterType.Lr4Lp, 1000, 0.707f, 0);
        Assert.InRange(Mag(1000, lr4), -6.62f, -5.42f);
        Assert.InRange(Mag(125, lr4), -0.6f, 0.6f);
        Assert.True(Mag(8000, lr4) < -40);
    }

    [Fact]
    public void CrossoverPhaseIsCascadedNotFlat()
    {
        var lr4 = new FilterParams(FilterType.Lr4Lp, 1000, 0.707f, 0);
        Assert.True(Math.Abs(Phase(1000, lr4)) > 90);
    }

    [Fact]
    public void LinkwitzTransformDcBoostAndUnityAbove()
    {
        var lt = new FilterParams(FilterType.LinkwitzTransform, 55, 1.1f, 25f) { Qp = 0.55f };
        float boost = 40f * MathF.Log10(55f / 25f);
        Assert.InRange(Mag(3, lt), boost - 0.4f, boost + 0.4f);
        Assert.InRange(Mag(15000, lt), -0.2f, 0.2f);
    }

    [Theory]
    [InlineData(FilterType.Peaking, 10, 20, 12)]
    [InlineData(FilterType.LowShelf, 120, 0.707, -6)]
    [InlineData(FilterType.Notch, 6000, 4, 0)]
    [InlineData(FilterType.Lr8Hp, 80, 0.707, 0)]
    public void CascadeDbIntoMatchesCascadeDb(FilterType type, double freq, double q, double gain)
    {
        var sections = DspMath.SectionsFor(new FilterParams(type, (float)freq, (float)q, (float)gain));
        var freqs = Enumerable.Range(0, 200).Select(i => 10 * Math.Pow(2000, i / 199.0)).ToArray();
        var db = new double[freqs.Length];
        DspMath.CascadeDbInto(sections, freqs.Select(f => DspMath.PhiAt(f)).ToArray(), db);
        for (int i = 0; i < freqs.Length; i++)
            Assert.Equal(DspMath.CascadeDb(sections, freqs[i]), db[i], 9);
    }
}

public class ChannelNameLimitTests
{
    [Fact]
    public void ShortNamesPassThrough() => Assert.Equal("Tweeter L", ChannelNameLimit.Fit("Tweeter L"));

    [Fact]
    public void LongAsciiNamesAreCutTo31Bytes() =>
        Assert.Equal(new string('a', 31), ChannelNameLimit.Fit(new string('a', 40)));

    [Fact]
    public void MultibyteCharactersAreNeverSplit()
    {
        // 15 × "é" (2 bytes each) = 30 bytes; a 16th would make 32.
        string fitted = ChannelNameLimit.Fit(new string('é', 20));
        Assert.Equal(new string('é', 15), fitted);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(fitted) <= 31);
    }
}
