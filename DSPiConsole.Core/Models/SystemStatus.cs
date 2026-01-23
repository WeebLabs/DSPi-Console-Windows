namespace DSPiConsole.Core.Models;

/// <summary>
/// Real-time system status from the DSP device
/// </summary>
public class SystemStatus
{
    /// <summary>
    /// Peak levels for all 5 channels (0.0 to 1.0)
    /// Index: 0=MasterL, 1=MasterR, 2=OutL, 3=OutR, 4=Sub
    /// </summary>
    public float[] Peaks { get; set; } = new float[5];

    /// <summary>
    /// CPU load percentage for Core 0 (0-100)
    /// </summary>
    public int Cpu0Load { get; set; }

    /// <summary>
    /// CPU load percentage for Core 1 (0-100)
    /// </summary>
    public int Cpu1Load { get; set; }

    public float GetPeak(ChannelId channel) => Peaks[(int)channel];
}
