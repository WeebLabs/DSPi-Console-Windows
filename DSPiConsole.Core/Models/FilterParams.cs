namespace DSPiConsole.Core.Models;

/// <summary>
/// Filter types matching the firmware definitions (config.h FilterType).
/// Value space is partitioned: 0-13 PEQ biquad types, 14-31 reserved,
/// 32-63 crossover filter types (Linkwitz-Riley / Butterworth / Bessel ×
/// order × HP/LP, used only in crossover bands), 64+ reserved.
/// firmware's filter_is_peq_type(t) == (t &lt; 32). The crossover block moved
/// from 8-39 to 32-63 at wire V13; PEQ types 8/9/10 (first-order all-pass and
/// shelves) were added at V13/V14, 12/13 (first-order low/high pass) later.
/// See peq_filters.md and crossover_filters_spec.md.
/// </summary>
public enum FilterType
{
    Flat = 0,
    Peaking = 1,
    LowShelf = 2,
    HighShelf = 3,
    LowPass = 4,
    HighPass = 5,
    Notch = 6,
    AllPass = 7,       // 2nd-order (RBJ) all-pass
    AllPass1 = 8,      // 1st-order all-pass (V13+): frequency only
    LowShelf1 = 9,     // 1st-order low shelf (V14+): frequency + gain, no Q
    HighShelf1 = 10,   // 1st-order high shelf (V14+): frequency + gain, no Q

    // Linkwitz Transform (V22+): pole/zero bass-extension biquad that replaces a
    // sealed-box woofer's (f0, Q0) rolloff with a target (fp, Qp). Reuses the wire
    // fields — Frequency = f0, Q = Q0, Gain = fp (Hz, NOT dB) — plus the Qp sidecar.
    LinkwitzTransform = 11,

    // First-order (6 dB/oct) low/high pass: single-pole rolloff, -3 dB at the
    // corner, no resonance. Frequency only — Q and gain are unused. New enum
    // values in an existing wire field, so no wire-format bump (see peq_filters.md).
    LowPass1 = 12,
    HighPass1 = 13,

    // 14-31 reserved

    // ── Crossover types (FILTER_XOVER_FIRST = 32 .. FILTER_XOVER_LAST = 63) ──
    // Linkwitz-Riley (orders 2/4/6/8)
    Lr2Lp = 32, Lr2Hp = 33,
    Lr4Lp = 34, Lr4Hp = 35,
    Lr6Lp = 36, Lr6Hp = 37,
    Lr8Lp = 38, Lr8Hp = 39,
    // Butterworth (orders 1..8)
    Bw1Lp = 40, Bw1Hp = 41,
    Bw2Lp = 42, Bw2Hp = 43,
    Bw3Lp = 44, Bw3Hp = 45,
    Bw4Lp = 46, Bw4Hp = 47,
    Bw5Lp = 48, Bw5Hp = 49,
    Bw6Lp = 50, Bw6Hp = 51,
    Bw7Lp = 52, Bw7Hp = 53,
    Bw8Lp = 54, Bw8Hp = 55,
    // Bessel (orders 2/4/6/8)
    Bes2Lp = 56, Bes2Hp = 57,
    Bes4Lp = 58, Bes4Hp = 59,
    Bes6Lp = 60, Bes6Hp = 61,
    Bes8Lp = 62, Bes8Hp = 63
}

/// <summary>
/// Extension methods for FilterType
/// </summary>
public static class FilterTypeExtensions
{
    /// <summary>
    /// The PEQ-only filter types (enum values 0-13). The crossover types
    /// (32-63) share the same enum but are never offered in a PEQ band's type
    /// picker — enumerate this instead of <c>Enum.GetValues</c> there.
    /// </summary>
    public static readonly FilterType[] PeqTypes =
    {
        FilterType.Flat, FilterType.Peaking, FilterType.LowShelf, FilterType.HighShelf,
        FilterType.LowPass, FilterType.HighPass, FilterType.Notch, FilterType.AllPass,
        FilterType.AllPass1, FilterType.LowShelf1, FilterType.HighShelf1,
        FilterType.LinkwitzTransform, FilterType.LowPass1, FilterType.HighPass1
    };

    /// <summary>True for the crossover filter types (32-63).</summary>
    public static bool IsCrossover(this FilterType type) =>
        (int)type >= (int)FilterType.Lr2Lp && (int)type <= (int)FilterType.Bes8Hp;

    /// <summary>The Linkwitz Transform reuses the wire fields with bespoke meaning
    /// (Frequency = f0, Q = Q0, Gain = fp in Hz) plus the Qp sidecar, so it needs a
    /// dedicated editor row and an 18-byte SET payload.</summary>
    public static bool IsLinkwitzTransform(this FilterType type) =>
        type == FilterType.LinkwitzTransform;

    public static string GetDisplayName(this FilterType type) => type switch
    {
        FilterType.Flat => "Off",
        FilterType.Peaking => "Peaking",
        FilterType.LowShelf => "Low Shelf",
        FilterType.HighShelf => "High Shelf",
        FilterType.LowPass => "High Cut 12dB",
        FilterType.HighPass => "Low Cut 12dB",
        FilterType.LowPass1 => "High Cut 6dB",
        FilterType.HighPass1 => "Low Cut 6dB",
        FilterType.Notch => "Notch",
        FilterType.AllPass => "All Pass",
        FilterType.AllPass1 => "All Pass (6dB)",
        FilterType.LowShelf1 => "Low Shelf (6dB)",
        FilterType.HighShelf1 => "High Shelf (6dB)",
        FilterType.LinkwitzTransform => "Linkwitz Transform",
        _ => "Unknown"
    };

    /// <summary>The dashboard's type code, as on the macOS Console: a "1"
    /// suffix marks the first-order (6 dB/oct) variant, and a crossover reads
    /// as its family, order and direction ("LR4 LP").</summary>
    public static string GetShortName(this FilterType type) => type switch
    {
        FilterType.Flat => "OFF",
        FilterType.Peaking => "PK",
        FilterType.LowShelf => "LS",
        FilterType.HighShelf => "HS",
        FilterType.LowPass => "HC",
        FilterType.HighPass => "LC",
        FilterType.LowPass1 => "HC1",
        FilterType.HighPass1 => "LC1",
        FilterType.Notch => "NO",
        FilterType.AllPass => "AP",
        FilterType.AllPass1 => "AP1",
        FilterType.LowShelf1 => "LS1",
        FilterType.HighShelf1 => "HS1",
        FilterType.LinkwitzTransform => "LT",
        _ when CrossoverFilter.TryGetMeta(type, out var m) =>
            $"{(m.Family == XoverFamily.Bessel ? "BES" : CrossoverFilter.FamilyShortName(m.Family))}{m.Order} {(m.IsHighPass ? "HP" : "LP")}",
        _ => "?"
    };

    public static bool HasGain(this FilterType type) =>
        type is FilterType.Peaking or FilterType.LowShelf or FilterType.HighShelf
              or FilterType.LowShelf1 or FilterType.HighShelf1;

    // First-order types (all-pass, shelves, low/high pass) are defined by
    // frequency alone (no Q).
    public static bool HasQ(this FilterType type) =>
        type is FilterType.Peaking or FilterType.LowShelf or FilterType.HighShelf
              or FilterType.LowPass or FilterType.HighPass
              or FilterType.Notch or FilterType.AllPass;

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
    public float Q { get; set; } = DefaultQ;
    public float Gain { get; set; } = 0.0f;

    /// <summary>Neutral (Butterworth) Q — the value a band falls back to when it
    /// has no meaningful Q of its own.</summary>
    public const float DefaultQ = 0.707f;
    public bool IsActive { get; set; } = true; // For UI visibility toggle only

    /// <summary>
    /// User-bypass for this single band, preserving freq/Q/gain so the band can
    /// be re-enabled to its previous response. Firmware 1.1.4+. Wire encoding is
    /// strict: only the value 1 means bypassed — see band_bypass_spec.md §5.
    /// </summary>
    public bool Bypass { get; set; } = false;

    /// <summary>
    /// Linkwitz Transform target pole Q (Qp). Only meaningful when
    /// <see cref="Type"/> == <see cref="FilterType.LinkwitzTransform"/>; ignored by
    /// every other type. For LT, <see cref="Frequency"/> = f0 (driver resonance),
    /// <see cref="Q"/> = Q0 (driver Q), and <see cref="Gain"/> carries fp in Hz
    /// (NOT dB). Wire encoding is Qp×512 in the band's reserved bytes; a stored 0
    /// decodes to this 0.707 (Butterworth) default. Firmware wire V22+.
    /// </summary>
    public float Qp { get; set; } = DefaultQp;

    /// <summary>Target Q used when the wire <c>qp_x512</c> is 0 (firmware default).</summary>
    public const float DefaultQp = 0.707f;

    /// <summary>Wire encoding of the LT target Q: round(Qp×512) clamped to the
    /// firmware's [0.1, 20] Q range. 0 for non-LT bands (reserved bytes stay 0).</summary>
    public ushort QpEncoded => Type == FilterType.LinkwitzTransform
        ? (ushort)Math.Round(Math.Clamp(Qp, 0.1f, 20f) * 512f)
        : (ushort)0;

    /// <summary>Decode a wire <c>qp_x512</c> to Qp; 0 selects the 0.707 default.</summary>
    public static float DecodeQp(ushort raw) => raw == 0 ? DefaultQp : raw / 512.0f;

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
        IsActive = IsActive,
        Bypass = Bypass,
        Qp = Qp
    };

    public bool Equals(FilterParams? other)
    {
        if (other is null) return false;
        return Type == other.Type &&
               Math.Abs(Frequency - other.Frequency) < 0.01f &&
               Math.Abs(Q - other.Q) < 0.001f &&
               Math.Abs(Gain - other.Gain) < 0.01f &&
               Math.Abs(Qp - other.Qp) < 0.001f &&
               Bypass == other.Bypass;
    }

    public override bool Equals(object? obj) => Equals(obj as FilterParams);
    public override int GetHashCode() => HashCode.Combine(Type, Frequency, Q, Gain, Bypass, Qp);
}
