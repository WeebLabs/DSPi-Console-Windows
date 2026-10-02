using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.Core.Tests;

/// <summary>
/// The sections appended after the upmixer: subharm (V29, extended V30), tube
/// (V31) and the output limiter (V32). Offsets follow the firmware's
/// bulk_params.h; each section must be read only when the version and the
/// length both say it is present.
/// </summary>
public class BulkParamsTailTests
{
    private static byte[] Image(byte version, int length)
    {
        var b = new byte[length];
        b[0] = version;
        b[1] = 1;    // RP2350
        b[2] = 17;
        b[3] = 9;
        b[4] = 8;
        b[5] = 12;
        BitConverter.GetBytes((ushort)length).CopyTo(b, 6);
        return b;
    }

    private static void Float(byte[] b, int offset, float value) => BitConverter.GetBytes(value).CopyTo(b, offset);

    [Fact]
    public void SectionOffsetsFollowTheFirmwareLayout()
    {
        Assert.Equal(BulkParamsParser.OffsetUpmix + 44, BulkParamsParser.OffsetSubharm);
        Assert.Equal(BulkParamsParser.OffsetSubharm + BulkParamsParser.SubharmSizeV29, BulkParamsParser.PacketSizeV29);
        Assert.Equal(BulkParamsParser.OffsetSubharm + BulkParamsParser.SubharmSizeV30, BulkParamsParser.OffsetTube);
        Assert.Equal(BulkParamsParser.OffsetTube + BulkParamsParser.TubeSize, BulkParamsParser.OffsetLimiter);
        Assert.Equal(BulkParamsParser.OffsetLimiter + BulkParamsParser.LimiterSize, BulkParamsParser.PacketSizeV32);
        Assert.Equal(6136, BulkParamsParser.PacketSizeV32);
    }

    [Fact]
    public void V32ImageCarriesAllThreeSections()
    {
        var b = Image(32, BulkParamsParser.PacketSizeV32);
        int s = BulkParamsParser.OffsetSubharm;
        b[s] = 1;
        BitConverter.GetBytes((ushort)0x0101).CopyTo(b, s + 2);
        Float(b, s + 4, -3f);
        Float(b, s + 8, 2.5f);
        Float(b, s + 12, 4f);
        Float(b, s + 16, -30f);
        Float(b, s + 20, 60f);
        Float(b, s + 24, 200f);
        Float(b, s + 28, -12f);
        b[s + 32] = 2;
        b[s + 33] = 1;

        int t = BulkParamsParser.OffsetTube;
        b[t] = 1;
        b[t + 1] = 12;
        b[t + 2] = 3;
        b[t + 3] = 1;
        BitConverter.GetBytes((ushort)0x0003).CopyTo(b, t + 4);
        Float(b, t + 8, 6f);
        Float(b, t + 12, -20f);
        Float(b, t + 16, 1.5f);
        Float(b, t + 20, 70f);
        Float(b, t + 24, 33f);
        Float(b, t + 28, 4f);
        Float(b, t + 32, 80f);
        Float(b, t + 36, 50f);
        Float(b, t + 40, -2f);

        int l = BulkParamsParser.OffsetLimiter;
        b[l + 12 * 2] = 1;
        b[l + 12 * 2 + 1] = 3;
        Float(b, l + 12 * 2 + 4, -6f);
        Float(b, l + 12 * 2 + 8, 250f);
        b[l + 12 * 3 + 1] = 9;    // out of range: reads as unlinked

        var p = BulkParamsParser.Parse(b)!;

        Assert.True(p.HasSubharm);
        Assert.True(p.HasSubharmExtended);
        Assert.True(p.SubharmEnabled);
        Assert.Equal(0x0101, p.SubharmOutputMask);
        Assert.Equal(-3f, p.SubharmLowDb);
        Assert.Equal(2.5f, p.SubharmHighDb);
        Assert.Equal(4f, p.SubharmBoostDb);
        Assert.Equal(-30f, p.SubharmTopDb);
        Assert.Equal(60f, p.SubharmSelectDepthPct);
        Assert.Equal(200f, p.SubharmSelectHoldMs);
        Assert.Equal(-12f, p.SubharmCeilingDb);
        Assert.Equal(SubharmSelectMode.Sustained, p.SubharmSelectMode);
        Assert.True(p.SubharmLinkPairs);

        Assert.True(p.HasTube);
        Assert.True(p.TubeEnabled);
        Assert.Equal(12, p.TubeType);
        Assert.Equal(3, p.TubeRectifier);
        Assert.True(p.TubeXfmrEnabled);
        Assert.Equal(0x0003, p.TubeOutputMask);
        Assert.Equal(6f, p.TubeDriveDb);
        Assert.Equal(-20f, p.TubeBiasPct);
        Assert.Equal(1.5f, p.TubeAsymDb);
        Assert.Equal(70f, p.TubeHardnessPct);
        Assert.Equal(33f, p.TubeSagPct);
        Assert.Equal(4f, p.TubeXfmrDamping);
        Assert.Equal(80f, p.TubeXfmrResHz);
        Assert.Equal(50f, p.TubeMixPct);
        Assert.Equal(-2f, p.TubeTrimDb);

        Assert.True(p.HasLimiter);
        Assert.Equal(9, p.Limiter.Length);
        Assert.Equal(new LimiterOutputSettings { Enabled = true, LinkGroup = 3, ThresholdDb = -6f, ReleaseMs = 250f }, p.Limiter[2]);
        Assert.Equal(0, p.Limiter[3].LinkGroup);
        Assert.False(p.Limiter[0].Enabled);
    }

    [Fact]
    public void OlderImagesLeaveLaterSectionsOut()
    {
        var v29 = BulkParamsParser.Parse(Image(29, BulkParamsParser.PacketSizeV29))!;
        Assert.True(v29.HasSubharm);
        Assert.False(v29.HasSubharmExtended);
        Assert.False(v29.HasTube);
        Assert.False(v29.HasLimiter);

        var v31 = BulkParamsParser.Parse(Image(31, BulkParamsParser.PacketSizeV31))!;
        Assert.True(v31.HasSubharmExtended);
        Assert.True(v31.HasTube);
        Assert.False(v31.HasLimiter);

        // A V28 image never reads a subharm section, even padded to V32 length.
        var v28 = BulkParamsParser.Parse(Image(28, BulkParamsParser.PacketSizeV32))!;
        Assert.False(v28.HasSubharm);
        Assert.False(v28.HasTube);
        Assert.False(v28.HasLimiter);

        // A V32 header on a truncated payload keeps only what arrived.
        var cut = BulkParamsParser.Parse(Image(32, BulkParamsParser.PacketSizeV31))!;
        Assert.True(cut.HasTube);
        Assert.False(cut.HasLimiter);
    }
}

/// <summary>The firmware's limiter link-group ganging, as the app mirrors it.</summary>
public class LimiterGangTests
{
    private static List<LimiterOutputSettings> Outputs(int n = 4) =>
        Enumerable.Range(0, n).Select(i => new LimiterOutputSettings { ThresholdDb = -1 - i }).ToList();

    [Fact]
    public void JoiningAGroupAdoptsItsSettingsAndTheFirstKeepsItsOwn()
    {
        var outs = Outputs();
        LimiterGang.SetGroup(1, 2, outs, 4);
        Assert.Equal(-2f, outs[1].ThresholdDb);
        LimiterGang.SetGroup(3, 2, outs, 4);
        Assert.Equal(-2f, outs[3].ThresholdDb);
        Assert.Equal(2, outs[3].LinkGroup);
        LimiterGang.SetGroup(3, 0, outs, 4);
        Assert.Equal(-2f, outs[3].ThresholdDb);
    }

    [Fact]
    public void AnEditReachesTheWholeGroupOnly()
    {
        var outs = Outputs();
        LimiterGang.SetGroup(0, 1, outs, 4);
        LimiterGang.SetGroup(2, 1, outs, 4);
        LimiterGang.Edit(2, outs, 4, o => o with { ReleaseMs = 500 });
        Assert.Equal(500f, outs[0].ReleaseMs);
        Assert.Equal(500f, outs[2].ReleaseMs);
        Assert.Equal(LimiterLimits.DefaultReleaseMs, outs[1].ReleaseMs);
    }

    [Fact]
    public void GangAllCopiesTheLowestMember()
    {
        var outs = Outputs();
        outs[1] = outs[1] with { LinkGroup = 4 };
        outs[3] = outs[3] with { LinkGroup = 4, Enabled = true };
        LimiterGang.GangAll(outs, 4);
        Assert.Equal(-2f, outs[3].ThresholdDb);
        Assert.False(outs[3].Enabled);
        Assert.Equal(-3f, outs[2].ThresholdDb);
    }

    [Fact]
    public void OnlyThePlatformsOutputsTakePart()
    {
        var outs = Outputs();
        outs[3] = outs[3] with { LinkGroup = 1 };
        LimiterGang.SetGroup(0, 1, outs, count: 3);
        Assert.Equal(-1f, outs[0].ThresholdDb);
    }
}

/// <summary>Control-surface nouns through caps v20: every noun the firmware
/// defines has a name and a family in the function menu.</summary>
public class ControlSurfaceNounTests
{
    [Fact]
    public void EveryNounThroughCapsV20IsNamed()
    {
        for (int n = 0; n <= (int)CsNoun.LimiterGr; n++)
        {
            Assert.DoesNotContain("Noun ", CsNounInfo.Name(n));
            Assert.NotNull(CsNounInfo.CategoryOf(n));
        }
        Assert.Equal("Subharm 56-80 Hz Level", CsNounInfo.Name(CsNoun.SubharmTop));
        Assert.Equal("Limiter Gain Reduction", CsNounInfo.Name(CsNoun.LimiterGr));
    }

    [Fact]
    public void AuxOutputsCarryTheirExtrasAndAreMenuFamilies()
    {
        // caps v18: extras at @22, the aux types from a 52-byte header (11 types).
        var b = new CsBinding { Type = CsType.AuxPwm, Gpio0 = 14, Value = 12800, Extras = (byte)(CsAuxExtras.BootOn | CsAuxExtras.Linear) };
        var wire = b.ToBytes();
        Assert.Equal(0x05, wire[22]);
        var back = CsBinding.FromBytes(wire)!;
        Assert.True(back.IsAux);
        Assert.Equal(b.Extras, back.Extras);
        Assert.True(back.WireEquals(b));

        var header = new byte[52];
        header[0] = 18; header[1] = 16; header[2] = 11; header[3] = 79;
        Assert.True(CsCapsHeader.FromBytes(header)!.HasAux);
        header[2] = 9;
        Assert.False(CsCapsHeader.FromBytes(header)!.HasAux);

        var aux = CsNounInfo.CategoryOf((int)CsNoun.AuxLevel)!;
        Assert.Equal("Auxiliary Outputs", aux.Title);
        Assert.Equal("Enable/Disable", CsNounInfo.MenuLabel((int)CsNoun.Aux, CsType.Button, aux));
        Assert.Equal("Enabled", CsNounInfo.MenuLabel((int)CsNoun.Aux, CsType.Led, aux));
        Assert.Equal("Level", CsNounInfo.MenuLabel((int)CsNoun.AuxLevel, CsType.Pot, aux));
        Assert.Equal("Clear Clipping", CsNounInfo.Name((int)CsNoun.Clip, CsType.Button));
        Assert.Equal("Clip", CsNounInfo.Name((int)CsNoun.Clip, CsType.Led));
    }

    [Fact]
    public void NewEnumNounsHaveValueLabels()
    {
        Assert.Equal("Percussive", CsNounInfo.EnumLabel((int)CsNoun.SubharmSelect, 1));
        Assert.Equal("Custom", CsNounInfo.EnumLabel((int)CsNoun.TubeType, 0));
        Assert.Equal("EL34", CsNounInfo.EnumLabel((int)CsNoun.TubeType, 12));
        Assert.Equal("Group 3", CsNounInfo.EnumLabel((int)CsNoun.LimiterLink, 3));
    }

    [Fact]
    public void LogMillisecondsArePlainIntegersSteppedInOctaves()
    {
        Assert.Equal(250, CsWire.EncodeValue(250, CsUnit.MsLog));
        Assert.True(CsWire.UnitStepsInOctaves(CsUnit.MsLog));
        Assert.Equal(1.0 / 12.0, CsWire.DefaultStep(CsUnit.MsLog));
        Assert.Equal("ms", CsWire.UnitSymbol(CsUnit.MsLog));
    }
}
