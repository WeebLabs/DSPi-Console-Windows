using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Tests;

/// <summary>The loudness compensation curve the Loudness window previews,
/// as the macOS Console computes it.</summary>
public class LoudnessDataTests
{
    [Fact]
    public void TheCurveRunsFrom20HzTo16kHz()
    {
        var curve = LoudnessData.GetCompensationCurve(83, 43, 100);
        Assert.Equal(30, curve.Length);
        Assert.Equal(20, curve[0].freq);
        Assert.Equal(16000, curve[^1].freq);
    }

    [Fact]
    public void OneKilohertzNeedsNoCompensationAndTheBassNeedsMost()
    {
        var curve = LoudnessData.GetCompensationCurve(83, 43, 100);
        var at1k = curve.Single(p => p.freq == 1000).db;
        // The flat drop in level is not compensated, so 1 kHz is near 0 dB.
        Assert.InRange(at1k, -1, 1);
        Assert.True(curve[0].db > 15, $"20 Hz reads {curve[0].db}");
        Assert.True(curve[0].db > curve.Single(p => p.freq == 100).db);
    }

    [Fact]
    public void IntensityScalesAndTheReferenceLevelNeedsNone()
    {
        var full = LoudnessData.GetCompensationCurve(83, 43, 100);
        var half = LoudnessData.GetCompensationCurve(83, 43, 50);
        Assert.Equal(full[0].db / 2, half[0].db, 3);
        Assert.All(LoudnessData.GetCompensationCurve(83, 83, 100), p => Assert.Equal(0, p.db));
    }
}
