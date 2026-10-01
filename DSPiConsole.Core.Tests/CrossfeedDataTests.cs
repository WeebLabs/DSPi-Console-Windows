using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Tests;

/// <summary>The crossfeed response the Crossfeed window draws: the firmware's
/// one-pole lowpass with complementary gain, as the macOS Console draws it.</summary>
public class CrossfeedDataTests
{
    [Fact]
    public void TheBassIsSharedAtTheComplementaryGains()
    {
        var (freqs, direct, cross) = CrossfeedData.GetResponseCurves(700, 4.5f);
        Assert.Equal(20, freqs[0], 3);
        Assert.Equal(20000, freqs[^1], 0);
        float g = 1f / (1f + MathF.Pow(10, 4.5f / 20));
        // Well below the cutoff the lowpass passes G and the direct path 1 - G.
        Assert.Equal(20 * MathF.Log10(g), cross[0], 1);
        Assert.Equal(20 * MathF.Log10(1 - g), direct[0], 1);
    }

    [Fact]
    public void TheTrebleStaysOnItsOwnSide()
    {
        var (_, direct, cross) = CrossfeedData.GetResponseCurves(700, 4.5f);
        Assert.InRange(direct[^1], -0.5f, 0.5f);
        Assert.True(cross[^1] < cross[0] - 20);
    }

    [Fact]
    public void MoreFeedMeansLessCrossfeed()
    {
        var light = CrossfeedData.GetResponseCurves(700, 9.5f).CrossfeedDb[0];
        var strong = CrossfeedData.GetResponseCurves(700, 4.5f).CrossfeedDb[0];
        Assert.True(light < strong);
        Assert.Equal("Custom", CrossfeedData.Presets[CrossfeedData.CustomPreset].Name);
    }
}
