namespace DSPiConsole.Core.Models;

/// <summary>
/// Output limiter (firmware wire V32): a brickwall lookahead peak limiter on
/// every output, each with its own enable, threshold, release and link group.
/// One opcode (0x81) carries everything: OUT sets, IN gets, wValue =
/// (output &lt;&lt; 8) | index, a float32 payload for every parameter. See
/// firmware limiter.h and Documentation/Features/output_limiter_spec.md.
/// </summary>
public static class LimiterLimits
{
    public const float ThresholdMinDb = -30f;
    public const float ThresholdMaxDb = 0f;
    public const float ReleaseMinMs = 10f;
    public const float ReleaseMaxMs = 1000f;
    /// <summary>0 = unlinked; outputs sharing a group 1..4 apply the deepest
    /// reduction any member needs.</summary>
    public const int LinkGroupMax = 4;

    /// <summary>-1 dBFS leaves room for inter-sample overshoot in a DAC (spec §7).</summary>
    public const float DefaultThresholdDb = -1f;
    public const float DefaultReleaseMs = 100f;

    /// <summary>Fixed by the algorithm: audio is delayed two 16-sample decision
    /// blocks on every output while any limiter is on, and not at all otherwise.</summary>
    public const int LookaheadSamples = 32;
    public const int BlockSamples = 16;

    public static float ClampThreshold(float db) => Math.Clamp(db, ThresholdMinDb, ThresholdMaxDb);
    public static float ClampRelease(float ms) => Math.Clamp(ms, ReleaseMinMs, ReleaseMaxMs);
    /// <summary>A SET clamps the group (the firmware rounds after clamping).</summary>
    public static int ClampLinkGroup(int group) => Math.Clamp(group, 0, LinkGroupMax);
    /// <summary>A stored group above the maximum reads as unlinked.</summary>
    public static int StoredLinkGroup(byte raw) => raw > LinkGroupMax ? 0 : raw;

    public static string LinkGroupName(int group) => group == 0 ? "Unlinked" : $"Group {group}";
}

/// <summary>Parameter indices for REQ_LIMITER (wValue low byte).</summary>
public static class LimiterParam
{
    public const byte Enabled = 0;
    public const byte ThresholdDb = 1;
    public const byte ReleaseMs = 2;
    public const byte LinkGroup = 3;
    /// <summary>Read-only GET block: one uint16 LE per output, gain reduction
    /// in 0.01 dB. The output byte is ignored.</summary>
    public const byte GetMeter = 0x80;
    /// <summary>Read-only GET block: engaged, lookahead samples, block samples,
    /// output count. Reading it feature-detects.</summary>
    public const byte GetStatus = 0x81;
    /// <summary>Output byte that makes a SET apply to every output.</summary>
    public const byte AllOutputs = 0xFF;
}

/// <summary>One output's limiter settings (spec §3). Defaults are the firmware's.</summary>
public sealed record LimiterOutputSettings
{
    public bool Enabled { get; init; }
    public float ThresholdDb { get; init; } = LimiterLimits.DefaultThresholdDb;
    public float ReleaseMs { get; init; } = LimiterLimits.DefaultReleaseMs;
    /// <summary>0 = unlinked, 1..4 = link group.</summary>
    public int LinkGroup { get; init; }

    /// <summary>Wire size of one WireLimiterOutput.</summary>
    public const int WireSize = 12;

    public static LimiterOutputSettings FromBytes(byte[] buffer, int offset) => new()
    {
        Enabled = buffer[offset] != 0,
        LinkGroup = LimiterLimits.StoredLinkGroup(buffer[offset + 1]),
        ThresholdDb = BitConverter.ToSingle(buffer, offset + 4),
        ReleaseMs = BitConverter.ToSingle(buffer, offset + 8),
    };
}

/// <summary>
/// The firmware's link-group ganging (spec §2.2, limiter.c), mirrored because
/// the app's own writes are never echoed back to it. Outputs sharing a non-zero
/// group share enable, threshold and release. Each function works on the first
/// <c>count</c> entries, the outputs this platform has.
/// </summary>
public static class LimiterGang
{
    /// <summary>Lowest-index output other than <paramref name="output"/> in
    /// <paramref name="group"/>, or null. The lowest member leads when stored
    /// settings disagree.</summary>
    public static int? Peer(int output, int group, IList<LimiterOutputSettings> outputs, int count)
    {
        if (group == 0) return null;
        for (int k = 0; k < Math.Min(count, outputs.Count); k++)
            if (k != output && outputs[k].LinkGroup == group) return k;
        return null;
    }

    /// <summary>An enable, threshold or release edit on <paramref name="output"/>
    /// reaches every member of its group.</summary>
    public static void Edit(int output, IList<LimiterOutputSettings> outputs, int count,
                            Func<LimiterOutputSettings, LimiterOutputSettings> change)
    {
        int group = outputs[output].LinkGroup;
        for (int k = 0; k < Math.Min(count, outputs.Count); k++)
            if (k == output || (group != 0 && outputs[k].LinkGroup == group))
                outputs[k] = change(outputs[k]);
    }

    /// <summary>Joining a group adopts its settings; the first member keeps its
    /// own, and leaving (group 0) keeps the current ones.</summary>
    public static void SetGroup(int output, int group, IList<LimiterOutputSettings> outputs, int count)
    {
        outputs[output] = outputs[output] with { LinkGroup = group };
        if (Peer(output, group, outputs, count) is { } p)
            outputs[output] = Adopt(outputs[output], outputs[p]);
    }

    /// <summary>What the firmware does to a raw bulk or preset restore: every
    /// member copies its group's lowest-numbered member.</summary>
    public static void GangAll(IList<LimiterOutputSettings> outputs, int count)
    {
        for (int k = 0; k < Math.Min(count, outputs.Count); k++)
        {
            if (Peer(k, outputs[k].LinkGroup, outputs, count) is { } lead && lead < k)
                outputs[k] = Adopt(outputs[k], outputs[lead]);
        }
    }

    private static LimiterOutputSettings Adopt(LimiterOutputSettings member, LimiterOutputSettings lead) =>
        member with { Enabled = lead.Enabled, ThresholdDb = lead.ThresholdDb, ReleaseMs = lead.ReleaseMs };
}
