using Windows.UI;

namespace DSPiConsole.Core.Models;

/// <summary>
/// Audio channel definitions matching firmware
/// </summary>
public enum ChannelId
{
    MasterLeft = 0,
    MasterRight = 1,
    OutLeft = 2,
    OutRight = 3,
    Sub = 4
}

/// <summary>
/// Channel configuration and metadata
/// </summary>
public class Channel
{
    public ChannelId Id { get; }
    public string Name { get; }
    public string ShortName { get; }
    public string Descriptor { get; }
    public int BandCount { get; }
    public bool IsOutput { get; }
    public Color Color { get; }

    private Channel(ChannelId id, string name, string shortName, string descriptor, 
                    int bandCount, bool isOutput, Color color)
    {
        Id = id;
        Name = name;
        ShortName = shortName;
        Descriptor = descriptor;
        BandCount = bandCount;
        IsOutput = isOutput;
        Color = color;
    }

    public static readonly Channel MasterLeft = new(
        ChannelId.MasterLeft, "Master L", "ML", "USB",
        10, false, Color.FromArgb(255, 74, 143, 227)); // Blue

    public static readonly Channel MasterRight = new(
        ChannelId.MasterRight, "Master R", "MR", "USB",
        10, false, Color.FromArgb(255, 245, 115, 115)); // Red

    public static readonly Channel OutLeft = new(
        ChannelId.OutLeft, "Out L", "OL", "SPDIF",
        2, true, Color.FromArgb(255, 69, 194, 163)); // Teal

    public static readonly Channel OutRight = new(
        ChannelId.OutRight, "Out R", "OR", "SPDIF",
        2, true, Color.FromArgb(255, 240, 196, 89)); // Yellow

    public static readonly Channel Sub = new(
        ChannelId.Sub, "Sub", "SUB", "PDM (Pin 10)",
        2, true, Color.FromArgb(255, 186, 135, 243)); // Purple

    public static IReadOnlyList<Channel> All { get; } = new[]
    {
        MasterLeft, MasterRight, OutLeft, OutRight, Sub
    };

    public static IReadOnlyList<Channel> Inputs { get; } = new[]
    {
        MasterLeft, MasterRight
    };

    public static IReadOnlyList<Channel> Outputs { get; } = new[]
    {
        OutLeft, OutRight, Sub
    };

    public static Channel FromId(ChannelId id) => id switch
    {
        ChannelId.MasterLeft => MasterLeft,
        ChannelId.MasterRight => MasterRight,
        ChannelId.OutLeft => OutLeft,
        ChannelId.OutRight => OutRight,
        ChannelId.Sub => Sub,
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    public static Channel FromIndex(int index) => FromId((ChannelId)index);
}
