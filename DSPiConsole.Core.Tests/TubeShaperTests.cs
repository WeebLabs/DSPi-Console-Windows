using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Tests;

/// <summary>The tube transfer curve the Tube Modeller graphs.</summary>
public class TubeShaperTests
{
    private static TubeShaper Make(float drive = -12, float bias = 10, float asym = 3, float hardness = 40, float mix = 100, float trim = 0) =>
        new(drive, bias, asym, hardness, mix, trim);

    [Fact]
    public void SmallSignalGainIsUnityAtEveryDrive()
    {
        foreach (float drive in new[] { -30f, -12f, 0f, 12f, 24f })
        {
            var s = Make(drive: drive, bias: 0, asym: 0);
            double slope = (s.Output(1e-4) - s.Output(-1e-4)) / 2e-4;
            Assert.InRange(slope, 0.99, 1.01);
            Assert.Equal(0, s.Output(0), 9);
        }
    }

    [Fact]
    public void SymmetricStageMakesNoEvenHarmonic()
    {
        var h = Make(drive: 12, bias: 0, asym: 0).Harmonics();
        Assert.True(h.Second <= -100, $"second {h.Second}");
        Assert.True(h.Third > -40, $"third {h.Third}");
    }

    [Fact]
    public void BiasAddsTheSecondHarmonic()
    {
        var biased = Make(drive: 12, bias: 30, asym: 0).Harmonics();
        Assert.True(biased.Second > -60, $"second {biased.Second}");
    }

    [Fact]
    public void DryMixIsTransparent()
    {
        var s = Make(drive: 24, mix: 0);
        foreach (double x in new[] { -1.0, -0.3, 0.5, 1.0 }) Assert.Equal(x, s.Output(x), 9);
        Assert.True(s.Harmonics().Third <= -100);
    }

    [Fact]
    public void ClipPointsSitAtTheKnees()
    {
        var s = Make(drive: 6, bias: 20, asym: 6);
        var (pos, neg) = s.ClipPoints;
        Assert.Equal(1, s.Knee(pos), 9);
        Assert.Equal(-1, s.Knee(neg), 9);
    }

    [Fact]
    public void FamiliesFollowTheFirmwareTypes()
    {
        Assert.Equal(TubeFamily.NovalTriode, TubeFamilies.Of(TubeLimits.TypeCustom));
        Assert.Equal(TubeFamily.NovalTriode, TubeFamilies.Of(1));
        Assert.Equal(TubeFamily.OctalPowerLarge, TubeFamilies.Of(12));
        Assert.Equal(TubeFamily.DirectlyHeated, TubeFamilies.Of(16));
        Assert.Equal(16, TubeTables.Types.Count - 1);
    }
}
