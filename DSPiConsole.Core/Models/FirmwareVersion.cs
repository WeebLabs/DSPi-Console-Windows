namespace DSPiConsole.Core.Models;

/// <summary>
/// A firmware version: three plain numbers and a pre-release ordinal. Console
/// and firmware ship as a matched pair carrying the same version, so the app's
/// own version is also the one it expects a device to report. See the firmware
/// repo's Documentation/Features/firmware_versioning_spec.md for the wire
/// encoding. Port of the macOS Console's FirmwareVersion.
/// </summary>
public readonly record struct FirmwareVersion(int Major, int Minor, int Patch, int Beta = 0)
    : IComparable<FirmwareVersion>
{
    /// <summary>A beta that could not say which one it is. 1.1.6 beta 1 and
    /// beta 2 predate the ordinal on the wire, so they answer as plain 1.1.6.
    /// Sorts below every numbered beta.</summary>
    public const int EarlyBeta = -1;

    /// <summary>The first release whose every build, betas included, reports
    /// the beta ordinal. A reply without it that claims this version or later
    /// can only be one of the early betas, never a final release.</summary>
    public static readonly FirmwareVersion FirstWithOrdinal = new(1, 1, 6);

    /// <summary>Parses "1.1.7" and "1.1.6-beta2" (the tag spelling). A missing
    /// patch reads as 0 and an unrecognised suffix as a final release. Returns
    /// null when there is no leading number.</summary>
    public static FirmwareVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimStart('v', 'V');
        int end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
        var fields = text[..end].Split('.', StringSplitOptions.None);
        if (fields.Length == 0 || !int.TryParse(fields[0], out int major)) return null;
        int Field(int i) => fields.Length > i && int.TryParse(fields[i], out int v) ? v : 0;
        var suffix = text[end..].TrimStart('-', '.', ' ').ToLowerInvariant();
        int beta = 0;
        if (suffix.StartsWith("beta"))
        {
            var digits = new string(suffix[4..].TakeWhile(char.IsDigit).ToArray());
            beta = int.TryParse(digits, out int b) ? b : 0;
        }
        return new FirmwareVersion(major, Field(1), Field(2), beta);
    }

    /// <summary>
    /// Decodes a REQ_GET_PLATFORM reply into its platform byte and version. The
    /// request asks for 7 bytes: bytes 4-5 are full-width minor and patch
    /// (legacy byte 2 packs them a nibble each), byte 6 the beta ordinal. Older
    /// firmware answers with 6 or 4 bytes, so fall back to the nibbles, never
    /// mixing the two decodes. A short reply is a final release only below
    /// <see cref="FirstWithOrdinal"/>. Null for a reply too short to hold one.
    /// </summary>
    public static (byte Platform, FirmwareVersion Version)? FromPlatformReply(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 4) return null;
        int major = reply[1];
        int minor = reply.Length >= 6 ? reply[4] : reply[2] >> 4;
        int patch = reply.Length >= 6 ? reply[5] : reply[2] & 0x0F;
        int beta;
        if (reply.Length >= 7) beta = reply[6];
        else if (new FirmwareVersion(major, minor, patch).CompareTo(FirstWithOrdinal) >= 0) beta = EarlyBeta;
        else beta = 0;
        return (reply[0], new FirmwareVersion(major, minor, patch, beta));
    }

    /// <summary>Final is encoded as 0 but outranks every beta of its patch.
    /// <see cref="EarlyBeta"/> (-1) already sorts below beta 1.</summary>
    private int BetaRank => Beta == 0 ? 256 : Beta;

    public int CompareTo(FirmwareVersion other) =>
        (Major, Minor, Patch, BetaRank).CompareTo((other.Major, other.Minor, other.Patch, other.BetaRank));

    public static bool operator <(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => Beta switch
    {
        0 => $"{Major}.{Minor}.{Patch}",
        EarlyBeta => $"{Major}.{Minor}.{Patch} early beta",
        _ => $"{Major}.{Minor}.{Patch} beta {Beta}",
    };

    /// <summary>Tag spelling ("1.1.6-beta4"), for anything that must match a
    /// release tag or file name. Null for an early beta, which names no tag.</summary>
    public string? TagSuffix => Beta switch
    {
        0 => $"{Major}.{Minor}.{Patch}",
        EarlyBeta => null,
        _ => $"{Major}.{Minor}.{Patch}-beta{Beta}",
    };
}

/// <summary>How a connected device's firmware compares with what this Console
/// build expects.</summary>
public enum FirmwareMatch
{
    Match,
    /// <summary>The device is behind this Console; updating it is an upgrade.</summary>
    DeviceOlder,
    /// <summary>The device is ahead of this Console (an older app, say).
    /// Installing this Console's firmware would be a downgrade.</summary>
    DeviceNewer,
}
