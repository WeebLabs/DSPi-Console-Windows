using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Services;

/// <summary>
/// Service for importing and exporting filter settings to/from files.
/// Supports DSPi Console format (multi-channel) and REW format (single-channel).
/// </summary>
public static class FilterFileService
{
    /// <summary>
    /// Generates export string in DSPi Console format. When <paramref name="xoverData"/>
    /// is supplied, each output channel's crossover bands (wire bands 20-23) are
    /// written as <c>Crossover N:</c> lines after its PEQ filters. These lines use a
    /// distinct prefix so older parsers (and the REW reader) skip them harmlessly.
    /// </summary>
    public static string GenerateExportString(
        IReadOnlyDictionary<int, IReadOnlyList<FilterParams>> channelData,
        IReadOnlyDictionary<int, IReadOnlyList<FilterParams>>? xoverData = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DSPi Console Filter Settings");
        sb.AppendLine($"# Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        foreach (var channel in Channel.All)
        {
            channelData.TryGetValue((int)channel.Id, out var filters);
            bool hasPeq = filters != null && filters.Any(f => f.Type != FilterType.Flat);

            IReadOnlyList<FilterParams>? xover = null;
            xoverData?.TryGetValue((int)channel.Id, out xover);
            bool hasXover = xover != null && xover.Any(f => f.Type.IsCrossover());

            if (!hasPeq && !hasXover)
                continue;

            sb.AppendLine($"[{channel.Name}]");

            if (filters != null)
            {
                for (int i = 0; i < filters.Count; i++)
                    sb.AppendLine(FormatFilter(i + 1, filters[i]));
            }

            if (hasXover)
            {
                for (int i = 0; i < xover!.Count; i++)
                    sb.AppendLine(FormatXoverFilter(i + 1, xover[i]));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatFilter(int index, FilterParams filter)
    {
        var inv = CultureInfo.InvariantCulture;
        if (filter.Type == FilterType.Flat)
        {
            return string.Format(inv, "Filter {0,2}: OFF", index);
        }

        // The 2nd-order types keep their REW codes so exports stay readable by
        // other tools. The first-order variants (V13/V14) and the Linkwitz
        // Transform (V22) have no REW equivalent, so they use DSPi-only codes —
        // folding them onto the standard codes would silently change the filter.
        var typeCode = filter.Type switch
        {
            FilterType.Peaking => "PK",
            FilterType.LowShelf => "LS",
            FilterType.HighShelf => "HS",
            FilterType.LowPass => "LP",
            FilterType.HighPass => "HP",
            FilterType.Notch => "NO",
            FilterType.AllPass => "AP",
            FilterType.AllPass1 => "AP1",
            FilterType.LowShelf1 => "LS1",
            FilterType.HighShelf1 => "HS1",
            FilterType.LowPass1 => "LP1",
            FilterType.HighPass1 => "HP1",
            FilterType.LinkwitzTransform => "LT",
            _ => "PK"
        };

        // The Linkwitz Transform reuses the wire fields with bespoke meaning:
        // Fc = f0 and Q = Q0 (the driver's sealed-box rolloff), Gain = fp in Hz
        // and Qp = the target pole. fp is written as an explicit "Fp ... Hz"
        // token rather than "Gain ... dB", which would misread as a level.
        if (filter.Type == FilterType.LinkwitzTransform)
        {
            return string.Format(inv,
                "Filter {0,2}: ON  {1,-8}Fc {2,8:F2} Hz  Q {3,6:F3}  Fp {4,8:F2} Hz  Qp {5,6:F3}",
                index, typeCode, filter.Frequency, filter.Q, filter.Gain, filter.Qp)
                + BypassTag(filter);
        }

        // Enough decimals that an export/import round trip doesn't move a band:
        // a graph drag leaves fractional values, and 0.707 must not become 0.71.
        var line = string.Format(inv, "Filter {0,2}: ON  {1,-8}Fc {2,8:F2} Hz",
            index, typeCode, filter.Frequency);

        if (filter.Type.HasGain())
        {
            line += string.Format(inv, "  Gain {0,7:+0.000;-0.000} dB", filter.Gain);
        }

        if (filter.Type.HasQ())
        {
            line += string.Format(inv, "  Q {0,6:F3}", filter.Q);
        }

        return line + BypassTag(filter);
    }

    /// <summary>
    /// Trailing marker for a band the user has bypassed (firmware 1.1.4+).
    /// Absent for normal bands, so ordinary exports keep their REW-like shape.
    /// </summary>
    private static string BypassTag(FilterParams filter) => filter.Bypass ? "  BYP" : "";

    private static string FormatXoverFilter(int index, FilterParams filter)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!CrossoverFilter.TryGetMeta(filter.Type, out var meta))
        {
            return string.Format(inv, "Crossover {0,2}: OFF", index);
        }

        return string.Format(inv,
            "Crossover {0,2}: ON  {1,-6} {2}  Fc {3,8:F2} Hz  Slope {4,3} dB/oct",
            index,
            CrossoverFilter.FamilyShortName(meta.Family),
            meta.IsHighPass ? "HP" : "LP",
            filter.Frequency,
            meta.SlopeDbPerOct);
    }

    /// <summary>
    /// Parses a filter file and returns the detected format and parsed data.
    /// <paramref name="outputs"/> are the connected device's outputs, which the
    /// macOS Console's index-keyed "[Output N: ...]" sections refer to.
    /// </summary>
    public static ParseResult ParseFile(string contents, IReadOnlyList<Channel> outputs)
    {
        if (contents.TrimStart().StartsWith("# DSPi Console"))
        {
            var parsed = ParseDSPiFormat(contents, outputs);
            if (parsed != null && (parsed.Value.Peq.Count > 0 || parsed.Value.Xover.Count > 0))
            {
                return new ParseResult
                {
                    Format = FilterFileFormat.DSPiConsole,
                    ChannelFilters = parsed.Value.Peq,
                    ChannelXoverFilters = parsed.Value.Xover.Count > 0 ? parsed.Value.Xover : null,
                    ChannelPreamps = parsed.Value.Preamps.Count > 0 ? parsed.Value.Preamps : null,
                };
            }
        }

        // Try REW format
        var filters = ParseREWFormat(contents, out var preamp);
        if (filters != null && filters.Count > 0)
        {
            return new ParseResult
            {
                Format = FilterFileFormat.REW,
                SingleChannelFilters = filters,
                SinglePreamp = preamp,
            };
        }

        return new ParseResult { Format = FilterFileFormat.Unknown };
    }

    /// <summary>
    /// Parses DSPi Console format (multi-channel). Returns the PEQ filters and the
    /// crossover bands as separate per-channel dictionaries (crossover bands are
    /// only present for output channels written by V11+ exports).
    /// </summary>
    private static (Dictionary<int, List<FilterParams>> Peq, Dictionary<int, List<FilterParams>> Xover, Dictionary<int, float> Preamps)?
        ParseDSPiFormat(string contents, IReadOnlyList<Channel> outputs)
    {
        var result = new Dictionary<int, List<FilterParams>>();
        var xoverResult = new Dictionary<int, List<FilterParams>>();
        var preamps = new Dictionary<int, float>();
        int? currentChannel = null;

        foreach (var line in contents.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Check for channel header [Channel Name]
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentChannel = ResolveChannelHeader(trimmed[1..^1].Trim(), outputs);
                continue;
            }

            if (currentChannel == null) continue;

            // Input trim ahead of the bands, as the macOS Console writes per input.
            if (TryParsePreampLine(trimmed, out var preampDb))
            {
                preamps[currentChannel.Value] = preampDb;
                continue;
            }

            // Crossover band line (output channels, V11+). Checked before the PEQ
            // branch; "Crossover" (this app) and "Xover" (the macOS Console) lines
            // deliberately don't contain "Filter".
            if (trimmed.StartsWith("Crossover", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Xover", StringComparison.OrdinalIgnoreCase))
            {
                if (!trimmed.Contains(':')) continue;
                var xo = ParseXoverLine(trimmed);
                if (xo != null)
                {
                    if (!xoverResult.TryGetValue(currentChannel.Value, out var xbands))
                        xoverResult[currentChannel.Value] = xbands = new List<FilterParams>();
                    xbands.Add(xo);
                }
                continue;
            }

            // Parse PEQ filter line
            if (!trimmed.Contains("Filter") || !trimmed.Contains(':')) continue;

            // The PEQ list is created on the first parsed band, not at the
            // channel header — a crossover-only section must not be read as
            // "this channel has zero PEQ bands", which would wipe its EQ.
            var filter = ParseFilterLine(trimmed);
            if (filter != null)
            {
                if (!result.TryGetValue(currentChannel.Value, out var bands))
                    result[currentChannel.Value] = bands = new List<FilterParams>();
                bands.Add(filter);
            }
        }

        return result.Count > 0 || xoverResult.Count > 0 ? (result, xoverResult, preamps) : null;
    }

    /// <summary>
    /// The channel a section header names: this app's bare channel name
    /// ("Master L", "SPDIF 2 R", "PDM"), or the macOS Console's index-keyed
    /// "Input N: name" (wire input N) and "Output N: name (Enabled)" (output N
    /// of the connected device). Null for a header naming no channel here.
    /// </summary>
    private static int? ResolveChannelHeader(string header, IReadOnlyList<Channel> outputs)
    {
        var indexed = Regex.Match(header, @"^(Input|Output)\s+(\d+)\s*:", RegexOptions.IgnoreCase);
        if (indexed.Success)
        {
            if (!int.TryParse(indexed.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                return null;
            bool isInput = indexed.Groups[1].Value.Equals("Input", StringComparison.OrdinalIgnoreCase);
            var list = isInput ? Channel.AllInputs : outputs;
            return index < list.Count ? (int)list[index].Id : null;
        }

        foreach (var ch in Channel.All)
            if (ch.Name.Equals(header, StringComparison.OrdinalIgnoreCase))
                return (int)ch.Id;
        return null;
    }

    /// <summary>A "Preamp -6.5 dB" line (REW, AutoEQ and the macOS Console),
    /// also REW's "Preamp: -6.5 dB".</summary>
    private static bool TryParsePreampLine(string line, out float db)
    {
        db = 0f;
        var m = Regex.Match(line, @"^Preamp\s*:?\s*([+-]?[\d.,]+)", RegexOptions.IgnoreCase);
        return m.Success && TryParseDecimal(m.Groups[1].Value, out db);
    }

    /// <summary>
    /// Parses a single crossover band line, e.g.
    /// <c>Crossover  1: ON  LR     HP  Fc    80.0 Hz  Slope  24 dB/oct</c>.
    /// Returns a Flat band for OFF lines (to keep band indices aligned), or null
    /// if the family/slope can't be resolved to a real crossover type.
    /// </summary>
    private static FilterParams? ParseXoverLine(string line)
    {
        var upper = line.ToUpperInvariant();

        // Disabled band → flat placeholder so subsequent band indices stay aligned.
        if (upper.Contains(" OFF") || !upper.Contains(" ON "))
        {
            return new FilterParams(FilterType.Flat, 1000, 0.707f, 0);
        }

        // The macOS Console writes one code: family, order and shape, e.g.
        // "ON  LR4LP" or "ON  BES2HP" (an order, not a slope).
        var codeMatch = Regex.Match(line, @"\bON\s+(LR|BW|BES)(\d)(LP|HP)\b", RegexOptions.IgnoreCase);
        int? codeOrder = null;
        XoverFamily family;
        bool isHighPass;
        if (codeMatch.Success)
        {
            family = codeMatch.Groups[1].Value.ToUpperInvariant() switch
            {
                "LR" => XoverFamily.LinkwitzRiley,
                "BW" => XoverFamily.Butterworth,
                _ => XoverFamily.Bessel,
            };
            codeOrder = codeMatch.Groups[2].Value[0] - '0';
            isHighPass = codeMatch.Groups[3].Value.Equals("HP", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Family + shape tags, e.g. "ON  LR     HP" or "ON  Bessel LP". Read as
            // one anchored pair so the tokens can't be picked up out of order.
            var tagMatch = Regex.Match(line, @"\bON\s+(\S+)\s+(HP|LP)\b", RegexOptions.IgnoreCase);
            if (!tagMatch.Success) return null;

            var parsedFamily = CrossoverFilter.ParseShortFamily(tagMatch.Groups[1].Value);
            if (parsedFamily is null or XoverFamily.None) return null;
            family = parsedFamily.Value;

            isHighPass = tagMatch.Groups[2].Value.Equals("HP", StringComparison.OrdinalIgnoreCase);
        }

        // Frequency (Fc XXX Hz)
        float freq = 1000f;
        var fcMatch = Regex.Match(line, @"Fc\s+([\d.,]+)", RegexOptions.IgnoreCase);
        if (fcMatch.Success && TryParseDecimal(fcMatch.Groups[1].Value, out var freqVal))
        {
            freq = freqVal;
        }

        // Slope (NN dB/oct) → filter order = slope / 6
        int order = codeOrder ?? 4;
        var slopeMatch = Regex.Match(line, @"Slope\s+(\d+)", RegexOptions.IgnoreCase);
        if (codeOrder == null && slopeMatch.Success && int.TryParse(slopeMatch.Groups[1].Value, out var slope) && slope >= 6)
        {
            order = slope / 6;
        }

        var type = CrossoverFilter.Compose(family, isHighPass, order);
        if (type == null || type == FilterType.Flat) return null;

        return new FilterParams(type.Value, freq, 0.707f, 0);
    }

    /// <summary>
    /// Parses REW format (single-channel).
    /// </summary>
    private static List<FilterParams>? ParseREWFormat(string contents, out float? preamp)
    {
        var filters = new List<FilterParams>();
        preamp = null;

        foreach (var line in contents.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            // AutoEQ's ParametricEQ.txt pairs its boosts with a negative preamp;
            // dropping it would apply the boosts without their headroom.
            if (preamp == null && TryParsePreampLine(trimmed, out var db))
            {
                preamp = db;
                continue;
            }
            if (!trimmed.Contains("Filter") || !trimmed.Contains(':')) continue;

            var filter = ParseFilterLine(trimmed);
            if (filter != null && filter.Type != FilterType.Flat)
            {
                filters.Add(filter);
            }
        }

        return filters.Count > 0 ? filters : null;
    }

    /// <summary>
    /// Parses a single filter line in REW format.
    /// </summary>
    private static FilterParams? ParseFilterLine(string line)
    {
        var upper = line.ToUpperInvariant();

        // Check if filter is enabled
        if (upper.Contains(" OFF") || !upper.Contains(" ON "))
        {
            return new FilterParams(FilterType.Flat, 1000, 0.707f, 0);
        }

        // Detect filter type. The DSPi-only codes are tested first so a
        // first-order band can never be read back as its 2nd-order namesake.
        FilterType filterType;
        if (upper.Contains(" AP1 "))
            filterType = FilterType.AllPass1;
        else if (upper.Contains(" LS1 "))
            filterType = FilterType.LowShelf1;
        else if (upper.Contains(" HS1 "))
            filterType = FilterType.HighShelf1;
        else if (upper.Contains(" LP1 "))
            filterType = FilterType.LowPass1;
        else if (upper.Contains(" HP1 "))
            filterType = FilterType.HighPass1;
        else if (upper.Contains(" LT "))
            filterType = FilterType.LinkwitzTransform;
        else if (upper.Contains(" PK ") || upper.Contains(" PEQ "))
            filterType = FilterType.Peaking;
        else if (upper.Contains(" LP ") || upper.Contains(" LPQ "))
            filterType = FilterType.LowPass;
        else if (upper.Contains(" HP ") || upper.Contains(" HPQ "))
            filterType = FilterType.HighPass;
        else if (upper.Contains(" LS ") || upper.Contains(" LSC ") || upper.Contains(" LSQ "))
            filterType = FilterType.LowShelf;
        else if (upper.Contains(" HS ") || upper.Contains(" HSC ") || upper.Contains(" HSQ "))
            filterType = FilterType.HighShelf;
        else if (upper.Contains(" NO ") || upper.Contains(" NT ") || upper.Contains(" NOTCH "))
            filterType = FilterType.Notch;
        else if (upper.Contains(" AP ") || upper.Contains(" ALLPASS "))
            filterType = FilterType.AllPass;
        else
            return null;

        // Extract frequency (Fc XXX Hz)
        float freq = 1000f;
        var fcMatch = Regex.Match(line, @"Fc\s+([\d.,]+)", RegexOptions.IgnoreCase);
        if (fcMatch.Success && TryParseDecimal(fcMatch.Groups[1].Value, out var freqVal))
        {
            freq = freqVal;
        }

        // Extract gain (Gain XXX dB)
        float gain = 0f;
        var gainMatch = Regex.Match(line, @"Gain\s+([+-]?[\d.,]+)", RegexOptions.IgnoreCase);
        if (gainMatch.Success && TryParseDecimal(gainMatch.Groups[1].Value, out var gainVal))
        {
            gain = gainVal;
        }

        // Extract Q. The \s guard keeps this off the Linkwitz Transform's "Qp".
        float q = 0.707f;
        var qMatch = Regex.Match(line, @"\sQ\s+([\d.,]+)", RegexOptions.IgnoreCase);
        if (qMatch.Success && TryParseDecimal(qMatch.Groups[1].Value, out var qVal))
        {
            q = qVal;
        }

        var filter = new FilterParams(filterType, freq, q, gain);

        // Linkwitz Transform sidecars: Fp lands in Gain (Hz, not dB) and Qp in
        // its own field. A file missing Fp falls back to fp = f0, which is the
        // neutral "no shift" case rather than a silent 0 Hz pole.
        if (filterType == FilterType.LinkwitzTransform)
        {
            var fpMatch = Regex.Match(line, @"\bFp\s+([\d.,]+)", RegexOptions.IgnoreCase);
            filter.Gain = fpMatch.Success && TryParseDecimal(fpMatch.Groups[1].Value, out var fpVal)
                ? fpVal
                : freq;

            var qpMatch = Regex.Match(line, @"\bQp\s+([\d.,]+)", RegexOptions.IgnoreCase);
            if (qpMatch.Success && TryParseDecimal(qpMatch.Groups[1].Value, out var qpVal))
                filter.Qp = qpVal;
        }

        // Per-band bypass marker (firmware 1.1.4+): " BYP" here, "[Bypassed]" in
        // the macOS Console's files.
        filter.Bypass = upper.Contains(" BYP") || upper.Contains("[BYPASSED]");

        return filter;
    }

    /// <summary>
    /// Parses a numeric token that may use either '.' or ',' as the decimal
    /// separator (REW exports from non-US locales sometimes use commas).
    /// Treats the last separator as the decimal point and strips any thousands
    /// separators before it.
    /// </summary>
    private static bool TryParseDecimal(string token, out float value)
    {
        if (string.IsNullOrEmpty(token))
        {
            value = 0f;
            return false;
        }

        int lastDot = token.LastIndexOf('.');
        int lastComma = token.LastIndexOf(',');
        string normalized;
        if (lastDot < 0 && lastComma < 0)
        {
            normalized = token;
        }
        else
        {
            int sepIndex = Math.Max(lastDot, lastComma);
            char sep = token[sepIndex];
            // Strip the other separator (thousands grouping) and replace the
            // decimal separator with '.' for InvariantCulture parsing.
            normalized = token.Replace(sep == '.' ? "," : ".", string.Empty);
            normalized = normalized.Replace(',', '.');
        }
        return float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

public enum FilterFileFormat
{
    Unknown,
    DSPiConsole,
    REW
}

public class ParseResult
{
    public FilterFileFormat Format { get; set; }
    public Dictionary<int, List<FilterParams>>? ChannelFilters { get; set; }

    /// <summary>
    /// Per-output-channel crossover bands parsed from a DSPi Console file, or null
    /// when the file contains no crossover sections (e.g. legacy or REW exports).
    /// </summary>
    public Dictionary<int, List<FilterParams>>? ChannelXoverFilters { get; set; }

    /// <summary>Per-input preamp (dB) from a DSPi Console file's "Preamp" lines,
    /// keyed by channel id, or null when it has none.</summary>
    public Dictionary<int, float>? ChannelPreamps { get; set; }

    public List<FilterParams>? SingleChannelFilters { get; set; }

    /// <summary>A REW/AutoEQ file's "Preamp" (dB), or null when it has none.</summary>
    public float? SinglePreamp { get; set; }
}
