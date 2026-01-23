using System.Text.Json;
using System.Text.Json.Serialization;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Services;

/// <summary>
/// Manages AutoEQ headphone profiles database.
/// </summary>
public class AutoEQManager
{
    private static AutoEQManager? _instance;
    public static AutoEQManager Instance => _instance ??= new AutoEQManager();

    private List<HeadphoneEntry> _entries = new();
    private HashSet<string> _favoriteIds = new();

    public IReadOnlyList<HeadphoneEntry> Entries => _entries;
    public IReadOnlyList<HeadphoneEntry> Favorites => _entries.Where(e => _favoriteIds.Contains(e.Id)).ToList();
    public string? DatabaseDate { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsLoaded => _entries.Count > 0;

    private readonly string _appDataPath;
    private readonly string _favoritesPath;

    private AutoEQManager()
    {
        _appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DSPiConsole");
        _favoritesPath = Path.Combine(_appDataPath, "autoeq_favorites.json");

        Directory.CreateDirectory(_appDataPath);
    }

    public async Task LoadDatabaseAsync()
    {
        try
        {
            // Try loading from file next to executable
            var basePath = AppContext.BaseDirectory;
            var filePath = Path.Combine(basePath, "autoeq_database.json");

            if (File.Exists(filePath))
            {
                var json = await File.ReadAllTextAsync(filePath);
                ParseDatabase(json);
            }
            else
            {
                // Also check the user's app data folder
                var userPath = Path.Combine(_appDataPath, "autoeq_database.json");
                if (File.Exists(userPath))
                {
                    var json = await File.ReadAllTextAsync(userPath);
                    ParseDatabase(json);
                }
                else
                {
                    ErrorMessage = $"AutoEQ database not found at {filePath}";
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load AutoEQ database: {ex.Message}";
        }

        LoadFavorites();
    }

    public void LoadFromJson(string json)
    {
        ParseDatabase(json);
        LoadFavorites();
    }

    private void ParseDatabase(string json)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var database = JsonSerializer.Deserialize<AutoEQDatabase>(json, options);
            if (database != null)
            {
                _entries = database.Entries?.ToList() ?? new List<HeadphoneEntry>();
                DatabaseDate = FormatDatabaseDate(database.GeneratedAt);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to parse database: {ex.Message}";
        }
    }

    private string? FormatDatabaseDate(string? isoDate)
    {
        if (string.IsNullOrEmpty(isoDate)) return null;
        if (DateTime.TryParse(isoDate, out var date))
        {
            return date.ToString("MMMM d, yyyy");
        }
        return isoDate;
    }

    public IEnumerable<HeadphoneEntry> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _entries;

        var lower = query.ToLowerInvariant();
        return _entries.Where(e =>
            e.Manufacturer.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
            e.Model.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
            e.DisplayName.Contains(lower, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsFavorite(HeadphoneEntry entry) => _favoriteIds.Contains(entry.Id);

    public void ToggleFavorite(HeadphoneEntry entry)
    {
        if (_favoriteIds.Contains(entry.Id))
            _favoriteIds.Remove(entry.Id);
        else
            _favoriteIds.Add(entry.Id);

        SaveFavorites();
    }

    public void ClearFavorites()
    {
        _favoriteIds.Clear();
        SaveFavorites();
    }

    private void LoadFavorites()
    {
        try
        {
            if (File.Exists(_favoritesPath))
            {
                var json = File.ReadAllText(_favoritesPath);
                var ids = JsonSerializer.Deserialize<List<string>>(json);
                _favoriteIds = ids?.ToHashSet() ?? new HashSet<string>();
            }
        }
        catch
        {
            _favoriteIds = new HashSet<string>();
        }
    }

    private void SaveFavorites()
    {
        try
        {
            var json = JsonSerializer.Serialize(_favoriteIds.ToList());
            File.WriteAllText(_favoritesPath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    /// <summary>
    /// Converts an AutoEQ profile to filter parameters.
    /// </summary>
    public static List<FilterParams> ConvertFilters(HeadphoneEntry entry)
    {
        return entry.Filters.Select(f => new FilterParams
        {
            Type = ParseFilterType(f.Type),
            Frequency = (float)f.Freq,
            Q = (float)f.Q,
            Gain = (float)f.Gain
        }).ToList();
    }

    private static FilterType ParseFilterType(string type) => type.ToLowerInvariant() switch
    {
        "peaking" => FilterType.Peaking,
        "lowshelf" => FilterType.LowShelf,
        "highshelf" => FilterType.HighShelf,
        "lowpass" => FilterType.LowPass,
        "highpass" => FilterType.HighPass,
        _ => FilterType.Flat
    };
}

#region JSON Models

public class AutoEQDatabase
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("generatedAt")]
    public string? GeneratedAt { get; set; }

    [JsonPropertyName("entryCount")]
    public int EntryCount { get; set; }

    [JsonPropertyName("entries")]
    public List<HeadphoneEntry>? Entries { get; set; }
}

public class HeadphoneEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("manufacturer")]
    public string Manufacturer { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("formFactor")]
    public string FormFactor { get; set; } = "";

    [JsonPropertyName("preamp")]
    public double Preamp { get; set; }

    [JsonPropertyName("filters")]
    public List<EmbeddedFilter> Filters { get; set; } = new();

    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(Model) ? Manufacturer : $"{Manufacturer} {Model}";

    [JsonIgnore]
    public string SourceDisplayName => Source switch
    {
        "oratory1990" => "oratory1990",
        "crinacle" => "Crinacle",
        "rtings" => "Rtings",
        "innerfidelity" => "InnerFidelity",
        "headphone.com" => "Headphone.com",
        _ => Source
    };

    [JsonIgnore]
    public string FormFactorIcon => FormFactor switch
    {
        "over-ear" => "\uE8D6",      // Headphones icon
        "in-ear" => "\uE720",        // Audio icon
        "earbud" => "\uE720",
        _ => "\uE8D6"
    };
}

public class EmbeddedFilter
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("freq")]
    public double Freq { get; set; }

    [JsonPropertyName("q")]
    public double Q { get; set; }

    [JsonPropertyName("gain")]
    public double Gain { get; set; }
}

#endregion
