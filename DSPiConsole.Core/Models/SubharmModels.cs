namespace DSPiConsole.Core.Models;

/// <summary>
/// Subharmonic synthesizer (subharm, firmware wire V29, extended V30) ranges and
/// defaults: a dbx 120A style octave divider. Three fixed program bands (48-72,
/// 72-112 and 112-160 Hz) each drive their own divider, producing a real
/// fundamental one octave down (24-36, 36-56 and 56-80 Hz) at its own level,
/// then a gentle 70 Hz LF boost bell. One global parameter set is applied to
/// the outputs a 16-bit mask selects. The firmware clamps every value to these
/// ranges. See firmware subharm.h and
/// Documentation/Features/subharmonic_synth_spec.md.
/// </summary>
public static class SubharmLimits
{
    /// <summary>Band level range (dB). The floor turns a band off and skips its
    /// divider; the top is above unity so the octave can sit louder than the
    /// bass that produced it.</summary>
    public const float LevelMinDb = -30f;
    public const float LevelMaxDb = 12f;

    /// <summary>LF boost bell range (dB); 0 skips the stage.</summary>
    public const float BoostMinDb = 0f;
    public const float BoostMaxDb = 6f;

    /// <summary>Selectivity depth (%): how hard unfavoured material is gated.</summary>
    public const float DepthMinPct = 0f;
    public const float DepthMaxPct = 100f;

    /// <summary>Selectivity hold (ms): the span the decision is made over.</summary>
    public const float HoldMinMs = 50f;
    public const float HoldMaxMs = 400f;

    /// <summary>Sub ceiling (dBFS), an absolute level; 0 limits nothing (off).</summary>
    public const float CeilingMinDb = -40f;
    public const float CeilingMaxDb = 0f;

    public const float DefaultLowDb = 0f;
    public const float DefaultHighDb = 0f;
    /// <summary>The third band ships off.</summary>
    public const float DefaultTopDb = LevelMinDb;
    public const float DefaultBoostDb = 0f;
    public const ushort DefaultOutputMask = 0xFFFF;
    public const int DefaultSelectMode = SubharmSelectMode.All;
    public const float DefaultDepthPct = 100f;
    public const float DefaultHoldMs = 150f;
    public const float DefaultCeilingDb = 0f;
    public const bool DefaultLinkPairs = true;

    /// <summary>Full scale of one REQ_GET_SUBHARM_METER entry, the same scale as
    /// the status peaks so one meter can show either.</summary>
    public const float MeterFullScale = 32767f;
}

/// <summary>Which kind of bass material gets a sub (wire select_mode).</summary>
public static class SubharmSelectMode
{
    public const int All = 0;
    /// <summary>Short bursts after an attack.</summary>
    public const int Percussive = 1;
    /// <summary>Notes that have already been ringing.</summary>
    public const int Sustained = 2;
    public const int Max = Sustained;
}

/// <summary>The continuous subharm values a slider can drag.</summary>
public enum SubharmField { Low, High, Top, Boost, Depth, Hold, Ceiling }
