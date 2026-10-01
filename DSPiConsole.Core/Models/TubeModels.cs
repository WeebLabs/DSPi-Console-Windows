namespace DSPiConsole.Core.Models;

/// <summary>
/// Tube modeller (firmware wire V31): a biased asymmetric waveshaper with supply
/// sag and a damping-factor output stage, applied to the outputs a 16-bit mask
/// selects. One indexed SET/GET pair (0x3E/0x3F) carries all fourteen parameters
/// as float32, the bools, mask and enums included. The firmware clamps every
/// value to these ranges. See firmware tube.h and
/// Documentation/Features/tube_preamp_spec.md.
/// </summary>
public static class TubeLimits
{
    public const float DriveMinDb = -30f;
    public const float DriveMaxDb = 24f;
    public const float BiasMinPct = -100f;
    public const float BiasMaxPct = 100f;
    public const float AsymMinDb = -12f;
    public const float AsymMaxDb = 12f;
    public const float HardnessMinPct = 0f;
    public const float HardnessMaxPct = 100f;
    public const float SagMinPct = 0f;
    public const float SagMaxPct = 100f;
    public const float XfmrDampingMin = 1f;
    public const float XfmrDampingMax = 20f;
    public const float XfmrResMinHz = 30f;
    public const float XfmrResMaxHz = 150f;
    public const float MixMinPct = 0f;
    public const float MixMaxPct = 100f;
    public const float TrimMinDb = -12f;
    public const float TrimMaxDb = 12f;

    public const int TypeCustom = 0;
    public const int TypeMax = 16;
    public const int RectifierSolidState = 0;
    public const int RectifierMax = 3;

    public const int DefaultType = 1;            // 12AX7
    public const int DefaultRectifier = 1;       // GZ34
    public const ushort DefaultOutputMask = 0xFFFF;
    public const float DefaultDriveDb = -12f;
    public const float DefaultBiasPct = 10f;
    public const float DefaultAsymDb = 3f;
    public const float DefaultHardnessPct = 40f;
    public const float DefaultSagPct = 15f;
    public const bool DefaultXfmrEnabled = true;
    public const float DefaultXfmrDamping = 2f;
    public const float DefaultXfmrResHz = 95f;
    public const float DefaultMixPct = 100f;
    public const float DefaultTrimDb = 0f;
}

/// <summary>Parameter indices for REQ_SET/GET_TUBE_PARAM (wValue low byte).</summary>
public static class TubeParam
{
    public const ushort Enabled = 0;
    public const ushort OutputMask = 1;
    public const ushort TubeType = 2;
    public const ushort DriveDb = 3;
    public const ushort BiasPct = 4;
    public const ushort AsymDb = 5;
    public const ushort HardnessPct = 6;
    public const ushort SagPct = 7;
    public const ushort Rectifier = 8;
    public const ushort XfmrEnabled = 9;
    public const ushort XfmrDamping = 10;
    public const ushort XfmrResHz = 11;
    public const ushort MixPct = 12;
    public const ushort TrimDb = 13;
    public const ushort Count = 14;
}

/// <summary>
/// One tube type (spec §2.3). Selecting a type makes the firmware copy these
/// four values into the character controls, and editing any of them drops the
/// type back to Custom; the app mirrors both rules, since its own writes are
/// never echoed back to correct it.
/// </summary>
public sealed record TubeTypeRow(string Name, string Style, float BiasPct, float AsymDb,
                                 float HardnessPct, float SagPct, bool PushPull = false)
{
    /// <summary>The first of the equivalent names, short enough for a chip.</summary>
    public string ShortName => Name.Split(" / ")[0];
}

/// <summary>One rectifier style (spec §2.9). The depth scale multiplies sag;
/// solid state switches sag off.</summary>
public sealed record TubeRectifierRow(string Name, float DepthScale, float AttackMs, float ReleaseMs);

public static class TubeTables
{
    /// <summary>Indexed by tube_type; index 0 is Custom and has no row. Rows
    /// never renumber in the firmware, so the index is safe to persist.</summary>
    public static readonly IReadOnlyList<TubeTypeRow?> Types = new TubeTypeRow?[]
    {
        null,
        new("12AX7 / ECC83", "High-gain preamp triode", 10, 3, 40, 15),
        new("5751", "Cooler 12AX7", 8, 3, 35, 12),
        new("12AT7 / ECC81", "Medium-gain driver, more odd-order", 5, 2, 55, 10),
        new("12AY7", "Tweed front end, gentle", 7, 4, 25, 15),
        new("12AU7 / ECC82", "Clean line stage", 5, 5, 20, 8),
        new("6SN7", "Octal hi-fi line stage, sweet", 7, 6, 15, 10),
        new("6SL7", "Octal high-mu, rounder knee", 10, 3, 30, 15),
        new("6DJ8 / ECC88 / 6922", "Clean, hard when pushed", 3, 2, 60, 5),
        new("EF86 / 6267", "Pentode preamp, symmetric bite", 2, 0, 75, 12),
        new("6SJ7", "Octal pentode, softer than EF86", 3, 1, 65, 15),
        new("EL84 / 6BQ5", "Push-pull power, chimey", 0, 0, 50, 25, PushPull: true),
        new("EL34", "Push-pull power, mid crunch, deep sag", 0, 0, 60, 30, PushPull: true),
        new("6L6 / 5881", "Push-pull power, tight", 0, 0, 55, 18, PushPull: true),
        new("6V6", "Push-pull power, early breakup, heavy sag", 0, 0, 35, 30, PushPull: true),
        new("KT88 / 6550", "Push-pull hi-fi power, near linear", 0, 0, 45, 10, PushPull: true),
        new("300B / 2A3", "Single-ended DHT, pure even harmonics", 12, 6, 10, 12),
    };

    public static readonly IReadOnlyList<TubeRectifierRow> Rectifiers = new TubeRectifierRow[]
    {
        new("Solid state", 0.0f, 0, 0),
        new("GZ34 / 5AR4", 0.6f, 5, 120),
        new("5U4", 1.0f, 8, 200),
        new("5Y3", 1.3f, 10, 300),
    };

    public static string TypeName(int type) =>
        type == TubeLimits.TypeCustom ? "Custom"
        : type > 0 && type < Types.Count && Types[type] is { } row ? row.Name
        : $"Type {type}";

    public static string RectifierName(int rectifier) =>
        rectifier >= 0 && rectifier < Rectifiers.Count ? Rectifiers[rectifier].Name : $"Rectifier {rectifier}";
}
