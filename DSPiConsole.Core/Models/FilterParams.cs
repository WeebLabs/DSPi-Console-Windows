namespace DSPiConsole.Core.Models;

/// <summary>
/// Filter types matching the firmware definitions
/// </summary>
public enum FilterType
{
    Flat = 0,
    Peaking = 1,
    LowShelf = 2,
    HighShelf = 3,
    LowPass = 4,
    HighPass = 5
}

/// <summary>
/// Extension methods for FilterType
/// </summary>
public static class FilterTypeExtensions
{
    public static string GetDisplayName(this FilterType type) => type switch
    {
        FilterType.Flat => "Off",
        FilterType.Peaking => "Peaking",
        FilterType.LowShelf => "Low Shelf",
        FilterType.HighShelf => "High Shelf",
        FilterType.LowPass => "Low Pass",
        FilterType.HighPass => "High Pass",
        _ => "Unknown"
    };

    public static string GetShortName(this FilterType type) => type switch
    {
        FilterType.Flat => "OFF",
        FilterType.Peaking => "PK",
        FilterType.LowShelf => "LS",
        FilterType.HighShelf => "HS",
        FilterType.LowPass => "LP",
        FilterType.HighPass => "HP",
        _ => "?"
    };

    public static bool HasGain(this FilterType type) =>
        type is FilterType.Peaking or FilterType.LowShelf or FilterType.HighShelf;

    public static bool HasQ(this FilterType type) =>
        type == FilterType.Peaking;

    public static bool HasFrequency(this FilterType type) =>
        type != FilterType.Flat;
}

/// <summary>
/// Parameters for a single biquad filter band
/// </summary>
public class FilterParams : IEquatable<FilterParams>
{
    public Guid Id { get; } = Guid.NewGuid();
    public FilterType Type { get; set; } = FilterType.Flat;
    public float Frequency { get; set; } = 1000.0f;
    public float Q { get; set; } = 0.707f;
    public float Gain { get; set; } = 0.0f;
    public bool IsActive { get; set; } = true; // For UI visibility toggle only

    public FilterParams() { }

    public FilterParams(FilterType type, float freq, float q, float gain)
    {
        Type = type;
        Frequency = freq;
        Q = q;
        Gain = gain;
    }

    public FilterParams Clone() => new()
    {
        Type = Type,
        Frequency = Frequency,
        Q = Q,
        Gain = Gain,
        IsActive = IsActive
    };

    public bool Equals(FilterParams? other)
    {
        if (other is null) return false;
        return Type == other.Type &&
               Math.Abs(Frequency - other.Frequency) < 0.01f &&
               Math.Abs(Q - other.Q) < 0.001f &&
               Math.Abs(Gain - other.Gain) < 0.01f;
    }

    public override bool Equals(object? obj) => Equals(obj as FilterParams);
    public override int GetHashCode() => HashCode.Combine(Type, Frequency, Q, Gain);
}
