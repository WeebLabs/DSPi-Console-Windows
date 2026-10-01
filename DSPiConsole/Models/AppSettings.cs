using System.Text.Json;

namespace DSPiConsole.Models;

public class AppSettings
{
    private static AppSettings? _instance;
    public static AppSettings Instance => _instance ??= Load();

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DSPiConsole", "settings.json");

    public bool ShowGraphGlow { get; set; } = true;

    /// <summary>The Tube Modeller window shows every control (Advanced) rather
    /// than the tube, drive and mix (Basic).</summary>
    public bool TubeModellerAdvanced { get; set; }
    public double GraphLineWidth { get; set; } = 2.0;
    public double GraphAnimationSpeed { get; set; } = 0.2;
    public bool ShowDebugInfo { get; set; }

    // Graph scale
    public double GraphDbRange { get; set; } = 50.0;
    public double GraphDbCenter { get; set; } = 0.0;
    public double GraphMinFrequency { get; set; } = 15.0;
    public double GraphMaxFrequency { get; set; } = 20000.0;

    // Grid/label visibility
    public bool ShowFrequencyGrid { get; set; } = true;
    public bool ShowFrequencyLabels { get; set; } = true;
    public bool ShowDbGrid { get; set; } = true;
    public bool ShowDbLabels { get; set; } = true;
    public bool ShowDbUnits { get; set; } = true;

    // Grid line strength: 0 hides the grid, 1 is standard, 2 twice as strong.
    public double GraphGridOpacity { get; set; } = 0.5;

    // Readouts that follow the pointer over empty graph while editing bands:
    // frequency along the bottom, level along the left edge.
    public bool ShowFrequencyReadout { get; set; } = true;
    public bool ShowGainReadout { get; set; } = true;

    // Dotted lines for non-selected channels
    public bool DottedInactiveChannels { get; set; } = true;

    // Phase-response overlay (dotted curve on a right-side degree axis).
    public bool ShowPhase { get; set; } = false;
    public bool PhaseUnwrapped { get; set; } = false;

    // Whether the popout graph follows the selected channel editor page
    public bool PopoutFollowsSelectedChannel { get; set; } = true;

    // Whether output gain / input preamp offsets the level shown in the
    // response graph (off = pure filter response).
    public bool GraphLevelIncludesGain { get; set; } = true;

    // Master L/R PEQ link (input pair 0 — name kept for settings-file compat)
    public bool MasterPeqLinked { get; set; }

    // PEQ link for the extra input pairs: [0]=IN3/4, [1]=IN5/6, [2]=IN7/8
    public bool[] InputPairLinkedExt { get; set; } = new bool[3];

    // Per-channel gain/delay lock state (key = ChannelId int)
    public Dictionary<int, bool> GainLocked { get; set; } = new();
    public Dictionary<int, bool> DelayLocked { get; set; } = new();

    // Dashboard graph-visibility pills (key = ChannelId int). Sparse: a channel
    // with no entry is visible. Only dashboard toggles are recorded here — the
    // narrowed view while a channel editor is open is temporary.
    public Dictionary<int, bool> GraphChannelVisibility { get; set; } = new();

    // Show the quick-save button next to the preset dropdown when dirty
    public bool ShowPresetSaveButton { get; set; } = true;

    // Sidebar volume control mode: "master" (hardware master volume,
    // REQ_SET_MASTER_VOLUME 0xD2) or "user" (vendor-channel user volume,
    // REQ_SET_USER_VOLUME 0xDA — mirrors the UAC1 host slider). Default
    // is "master" to preserve current behavior for existing users.
    public string SidebarVolumeMode { get; set; } = "master";

    public event EventHandler? SettingsChanged;

    public void NotifyChanged()
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Ignore load errors
        }
        return new AppSettings();
    }
}
