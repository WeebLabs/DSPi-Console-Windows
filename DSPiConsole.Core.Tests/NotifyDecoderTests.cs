namespace DSPiConsole.Core.Tests;

/// <summary>The Interrupt Monitor's packet decoder.</summary>
public class NotifyDecoderTests
{
    private static byte[] ParamChanged(int offset, byte source, params byte[] value)
    {
        var b = new byte[12 + value.Length];
        b[0] = 0x02; b[1] = 0x02; b[3] = 7;
        b[4] = (byte)offset; b[5] = (byte)(offset >> 8);
        b[6] = (byte)value.Length; b[7] = (byte)(value.Length >> 8);
        b[8] = source;
        value.CopyTo(b, 12);
        return b;
    }

    private static byte[] Float(float f) => BitConverter.GetBytes(f);

    [Fact]
    public void IdleAndV1MasterVolume()
    {
        Assert.Equal("IDLE", NotifyDecoder.Decode(new byte[] { 0 }));
        var v1 = new byte[8];
        v1[0] = 0x01;
        Float(-12.5f).CopyTo(v1, 4);
        Assert.Equal("v1.MasterVolume   -12.50 dB", NotifyDecoder.Decode(v1));
        Float(-130f).CopyTo(v1, 4);
        Assert.EndsWith("MUTE", NotifyDecoder.Decode(v1));
    }

    [Fact]
    public void ParamChangedNamesTheFieldAndSource()
    {
        string line = NotifyDecoder.Decode(ParamChanged(4700, 2, Float(-6f)));
        Assert.StartsWith("[  7] [BULK    ] master_volume.master_volume_db", line);
        Assert.EndsWith("-6.00 dB", line);
    }

    [Theory]
    [InlineData(64 + 16 * 4, 4, "delays.delay_ms[16]")]
    [InlineData(4668 + 7 * 4, 4, "preamp.preamp_db[7]")]
    [InlineData(708 + 8 * 12 + 4, 4, "outputs[8].gain_db")]
    [InlineData(35, 1, "crossfeed.output_pair_mask")]
    [InlineData(4728, 1, "input_config.i2s_clock_mode")]
    [InlineData(4731, 1, "input_config.adat_clock_mode_p1")]
    [InlineData(5900 + 40, 4, "upmix.decorr_pct")]
    [InlineData(5944 + 33, 1, "subharm.link_pairs")]
    [InlineData(5980 + 40, 4, "tube.trim_db")]
    [InlineData(6028 + 8 * 12 + 8, 4, "limiter[8].release_ms")]
    public void FieldNamesAtV32Offsets(int offset, int size, string name)
    {
        var (n, _) = NotifyDecoder.Param(offset, size, new byte[size]);
        Assert.Equal(name, n);
    }

    [Fact]
    public void CrosspointAndBandRecords()
    {
        var cp = new byte[8];
        cp[0] = 1; cp[1] = 1;
        Float(-3f).CopyTo(cp, 4);
        // Input 7, output 8: the last of the 8 x 9 crosspoints.
        var (name, value) = NotifyDecoder.Param(132 + (7 * 9 + 8) * 8, 8, cp);
        Assert.Equal("crosspoints[7][8]", name);
        Assert.Equal("en=1 inv=1  -3.00 dB", value);

        var band = new byte[16];
        band[0] = 2;
        Float(1000f).CopyTo(band, 4);
        Float(0.71f).CopyTo(band, 8);
        Float(4f).CopyTo(band, 12);
        Assert.Equal("eq[16][11]", NotifyDecoder.Param(824 + (16 * 12 + 11) * 16, 16, band).Name);
        var xo = NotifyDecoder.Param(4780 + (2 * 4 + 3) * 16, 16, band);
        Assert.Equal("crossover[2][3]", xo.Name);
        Assert.Equal("type=2  byp=0  f=1000.0 Hz  Q=0.71  g=+4.00 dB", xo.Value);
    }

    [Fact]
    public void EnumValuesAreNamed()
    {
        Assert.Equal("2 (Off)", NotifyDecoder.Param(5901, 1, new byte[] { 2 }).Value);
        Assert.Equal("2 (Adaptive)", NotifyDecoder.Param(5902, 1, new byte[] { 2 }).Value);
        Assert.Equal("Group 3", NotifyDecoder.Param(6028 + 1, 1, new byte[] { 3 }).Value);
        Assert.Equal("Unlinked", NotifyDecoder.Param(6028 + 1, 1, new byte[] { 0 }).Value);
        Assert.Equal("+1.5 dB", NotifyDecoder.Param(5903, 1, new byte[] { 3 }).Value);
    }

    [Fact]
    public void DiscreteEvents()
    {
        Assert.Equal("[  4] v2.SiggenState                 RUN reason=HOST type=3 ch=-",
            NotifyDecoder.Decode(new byte[] { 2, 0x07, 0, 4, 2, 1, 3, 0xFF }));
        Assert.Equal("[  1] v2.I2sSlaveState              LOCKED rate=48000",
            NotifyDecoder.Decode(new byte[] { 2, 0x09, 0, 1, 3, 0x80, 0xBB, 0, 0 }));
        Assert.Equal("[  1] v2.AdatInputState             SYNCING rate=44100 mode=slave",
            NotifyDecoder.Decode(new byte[] { 2, 0x0B, 0, 1, 2, 0x44, 0xAC, 0, 0, 1 }));
        Assert.Equal("[  9] v2.CsAux                      slot=1 state=2 level=50.0% src=GPIO",
            NotifyDecoder.Decode(new byte[] { 2, 0x0C, 0, 9, 1, 2, 0x00, 0x32, 5 }));
        Assert.Equal("[  3] v2.IrLearn                    DONE NEC code=0x00FF10EF",
            NotifyDecoder.Decode(new byte[] { 2, 0x0A, 0, 3, 2, 1, 0, 0, 0xEF, 0x10, 0xFF, 0x00 }));
        Assert.Equal("[  3] v2.InputFormat                 channels=8",
            NotifyDecoder.Decode(new byte[] { 2, 0x05, 0, 3, 8, 0, 0, 0 }));
        Assert.Equal("UART", NotifyDecoder.SourceName(8));
        Assert.Equal("[  2] v2.PresetLoaded                slot=5",
            NotifyDecoder.Decode(new byte[] { 2, 0x04, 0, 2, 5, 0, 0, 0 }));
    }

    [Fact]
    public void UnknownOffsetFallsBackToHex()
    {
        var (name, value) = NotifyDecoder.Param(6200, 2, new byte[] { 0xAB, 0x01 });
        Assert.Equal("offset=0x1838 size=2", name);
        Assert.Equal("AB 01", value);
    }
}
