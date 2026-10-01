namespace DSPiConsole.Core.Models;

/// <summary>
/// Real-time system status from the DSP device
/// </summary>
public class SystemStatus
{
    /// <summary>
    /// Peak levels for all 11 channels (0.0 to 1.0)
    /// Index: 0=MasterL, 1=MasterR, 2=SPDIF1L, 3=SPDIF1R, 4=SPDIF2L, 5=SPDIF2R,
    ///        6=SPDIF3L, 7=SPDIF3R, 8=SPDIF4L, 9=SPDIF4R, 10=PDM
    /// </summary>
    public float[] Peaks { get; set; } = new float[ChannelMap.AppChannelCount];

    /// <summary>
    /// CPU load percentage for Core 0 (0-100)
    /// </summary>
    public int Cpu0Load { get; set; }

    /// <summary>
    /// CPU load percentage for Core 1 (0-100)
    /// </summary>
    public int Cpu1Load { get; set; }

    /// <summary>
    /// Sticky clip bitmask from firmware, one bit per app channel id (0..16 —
    /// id 16 needs the 32-bit field)
    /// </summary>
    public uint ClipFlags { get; set; }

    /// <summary>
    /// App-side latched clip flags (OR'd from firmware flags over time)
    /// </summary>
    public uint ClipLatched { get; set; }

    /// <summary>
    /// When the last clip was detected
    /// </summary>
    public DateTime? ClipTimestamp { get; set; }

    public float GetPeak(ChannelId channel) => Peaks[(int)channel];

    public bool IsClipping(ChannelId channel) =>
        (ClipLatched & (1u << (int)channel)) != 0;
}
