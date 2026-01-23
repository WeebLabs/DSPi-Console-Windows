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
    /// Generates export string in DSPi Console format.
    /// </summary>
    public static string GenerateExportString(IReadOnlyDictionary<int, IReadOnlyList<FilterParams>> channelData)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DSPi Console Filter Settings");
        sb.AppendLine($"# Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        foreach (var channel in Channel.All)
        {
            sb.AppendLine($"[{channel.Name}]");

            if (channelData.TryGetValue((int)channel.Id, out var filters))
            {
                for (int i = 0; i < filters.Count; i++)
                {
                    sb.AppendLine(FormatFilter(i + 1, filters[i]));
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatFilter(int index, FilterParams filter)
    {
        if (filter.Type == FilterType.Flat)
        {
            return $"Filter {index,2}: OFF";
        }

        var typeCode = filter.Type switch
        {
            FilterType.Peaking => "PK",
            FilterType.LowShelf => "LS",
            FilterType.HighShelf => "HS",
            FilterType.LowPass => "LP",
            FilterType.HighPass => "HP",
            _ => "PK"
        };

        var line = $"Filter {index,2}: ON  {typeCode,-8}Fc {filter.Frequency,7:F1} Hz";

        if (filter.Type.HasGain())
        {
            line += $"  Gain {filter.Gain,+5:+0.0;-0.0} dB";
        }

        if (filter.Type.HasQ())
        {
            line += $"  Q {filter.Q,5:F2}";
        }

        return line;
    }

    /// <summary>
    /// Parses a filter file and returns the detected format and parsed data.
    /// </summary>
    public static ParseResult ParseFile(string contents)
    {
        if (contents.TrimStart().StartsWith("# DSPi Console"))
        {
            var channelFilters = ParseDSPiFormat(contents);
            if (channelFilters != null && channelFilters.Count > 0)
            {
                return new ParseResult
                {
                    Format = FilterFileFormat.DSPiConsole,
                    ChannelFilters = channelFilters
                };
            }
        }

        // Try REW format
        var filters = ParseREWFormat(contents);
        if (filters != null && filters.Count > 0)
        {
            return new ParseResult
            {
                Format = FilterFileFormat.REW,
                SingleChannelFilters = filters
            };
        }

        return new ParseResult { Format = FilterFileFormat.Unknown };
    }

    /// <summary>
    /// Parses DSPi Console format (multi-channel).
    /// </summary>
    private static Dictionary<int, List<FilterParams>>? ParseDSPiFormat(string contents)
    {
        var result = new Dictionary<int, List<FilterParams>>();
        int? currentChannel = null;

        foreach (var line in contents.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Check for channel header [Channel Name]
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var channelName = trimmed[1..^1];
                foreach (var ch in Channel.All)
                {
                    if (ch.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase))
                    {
                        currentChannel = (int)ch.Id;
                        result[currentChannel.Value] = new List<FilterParams>();
                        break;
                    }
                }
                continue;
            }

            // Parse filter line
            if (currentChannel == null) continue;
            if (!trimmed.Contains("Filter") || !trimmed.Contains(':')) continue;

            var filter = ParseFilterLine(trimmed);
            if (filter != null)
            {
                result[currentChannel.Value].Add(filter);
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Parses REW format (single-channel).
    /// </summary>
    private static List<FilterParams>? ParseREWFormat(string contents)
    {
        var filters = new List<FilterParams>();

        foreach (var line in contents.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
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

        // Detect filter type
        FilterType filterType;
        if (upper.Contains(" PK ") || upper.Contains(" PEQ "))
            filterType = FilterType.Peaking;
        else if (upper.Contains(" LP ") || upper.Contains(" LPQ "))
            filterType = FilterType.LowPass;
        else if (upper.Contains(" HP ") || upper.Contains(" HPQ "))
            filterType = FilterType.HighPass;
        else if (upper.Contains(" LS ") || upper.Contains(" LSC ") || upper.Contains(" LSQ "))
            filterType = FilterType.LowShelf;
        else if (upper.Contains(" HS ") || upper.Contains(" HSC ") || upper.Contains(" HSQ "))
            filterType = FilterType.HighShelf;
        else
            return null;

        // Extract frequency (Fc XXX Hz)
        float freq = 1000f;
        var fcMatch = Regex.Match(line, @"Fc\s+([\d.]+)", RegexOptions.IgnoreCase);
        if (fcMatch.Success && float.TryParse(fcMatch.Groups[1].Value, out var freqVal))
        {
            freq = freqVal;
        }

        // Extract gain (Gain XXX dB)
        float gain = 0f;
        var gainMatch = Regex.Match(line, @"Gain\s+([+-]?[\d.]+)", RegexOptions.IgnoreCase);
        if (gainMatch.Success && float.TryParse(gainMatch.Groups[1].Value, out var gainVal))
        {
            gain = gainVal;
        }

        // Extract Q
        float q = 0.707f;
        var qMatch = Regex.Match(line, @"\sQ\s+([\d.]+)", RegexOptions.IgnoreCase);
        if (qMatch.Success && float.TryParse(qMatch.Groups[1].Value, out var qVal))
        {
            q = qVal;
        }

        return new FilterParams(filterType, freq, q, gain);
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
    public List<FilterParams>? SingleChannelFilters { get; set; }
}
