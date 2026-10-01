namespace DSPiConsole.Core.Models;

/// <summary>
/// The last 15 s of buffer fill samples, one ring per series, traced under the
/// statistics window's buffer rows. Sample s lives at ring index s % capacity,
/// so it keeps one number, and one x position, for as long as it is kept. Port
/// of the macOS Console's BufferFillHistory.
/// </summary>
public sealed class BufferFillHistory
{
    /// <summary>About 15 s at the 60 ms buffer poll.</summary>
    public const int Capacity = 256;
    /// <summary>S/PDIF (or I2S) slots 0 to 3, then the PDM DMA and ring buffers.</summary>
    public const int SeriesCount = 6;
    public const int PdmDmaSeries = 4;
    public const int PdmRingSeries = 5;
    private const byte Absent = 0xFF;

    private readonly byte[][] _rings = Enumerable.Range(0, SeriesCount)
        .Select(_ => Enumerable.Repeat(Absent, Capacity).ToArray()).ToArray();

    /// <summary>Samples appended so far.</summary>
    public int Total { get; private set; }

    /// <summary>Raised with the number of each new sample.</summary>
    public event Action<int>? Appended;

    public void Append(BufferStatsPacket packet)
    {
        int slot = Total % Capacity;
        int spdif = Math.Min((int)packet.NumSpdif, 4);
        for (int i = 0; i < 4; i++)
            _rings[i][slot] = i < spdif ? packet.Spdif[i].ConsumerFillPct : Absent;
        _rings[PdmDmaSeries][slot] = packet.IsPdmActive ? packet.Pdm.DmaFillPct : Absent;
        _rings[PdmRingSeries][slot] = packet.IsPdmActive ? packet.Pdm.RingFillPct : Absent;
        Total++;
        Appended?.Invoke(Total - 1);
    }

    /// <summary>The fill percentage of a series at sample s, or null when the
    /// sample has aged out or that buffer was not running.</summary>
    public byte? Value(int series, int sample)
    {
        if (sample < 0 || sample >= Total || sample < Total - Capacity) return null;
        byte v = _rings[series][sample % Capacity];
        return v == Absent ? null : v;
    }
}
