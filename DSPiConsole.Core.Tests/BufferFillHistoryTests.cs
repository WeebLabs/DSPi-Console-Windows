using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Tests;

/// <summary>The statistics window's buffer fill history.</summary>
public class BufferFillHistoryTests
{
    private static BufferStatsPacket Packet(byte numSpdif, bool pdm, byte fill)
    {
        var p = new BufferStatsPacket { NumSpdif = numSpdif, Flags = (byte)(pdm ? 1 : 0) };
        for (int i = 0; i < 4; i++) p.Spdif[i] = new SpdifBufferStats { ConsumerFillPct = (byte)(fill + i) };
        p.Pdm = new PdmBufferStats { DmaFillPct = 30, RingFillPct = 10 };
        return p;
    }

    [Fact]
    public void KeepsLiveSeriesAndMarksAbsentOnes()
    {
        var h = new BufferFillHistory();
        int raised = -1;
        h.Appended += s => raised = s;
        h.Append(Packet(2, pdm: false, fill: 50));
        Assert.Equal(1, h.Total);
        Assert.Equal(0, raised);
        Assert.Equal((byte)50, h.Value(0, 0));
        Assert.Equal((byte)51, h.Value(1, 0));
        Assert.Null(h.Value(2, 0));
        Assert.Null(h.Value(BufferFillHistory.PdmDmaSeries, 0));
        h.Append(Packet(2, pdm: true, fill: 40));
        Assert.Equal((byte)30, h.Value(BufferFillHistory.PdmDmaSeries, 1));
        Assert.Equal((byte)10, h.Value(BufferFillHistory.PdmRingSeries, 1));
    }

    [Fact]
    public void OldSamplesAgeOutAfterTheCapacity()
    {
        var h = new BufferFillHistory();
        for (int i = 0; i < BufferFillHistory.Capacity + 10; i++) h.Append(Packet(1, false, (byte)(i % 100)));
        Assert.Null(h.Value(0, 5));
        Assert.Equal((byte)(10 % 100), h.Value(0, 10));
        Assert.NotNull(h.Value(0, h.Total - 1));
        Assert.Null(h.Value(0, h.Total));
    }
}
