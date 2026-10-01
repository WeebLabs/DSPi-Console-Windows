using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Graphing › Grid &amp; Labels — five booleans for graph visibility
/// of grids and tick labels. Live-applied via <see cref="AppSettings"/>.
/// </summary>
public sealed partial class GraphingGridLabelsPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public GraphingGridLabelsPage() { InitializeComponent(); }

    protected override void Refresh()
    {
        var s = AppSettings.Instance;
        _suppress = true;
        try
        {
            FreqGridToggle.IsOn = s.ShowFrequencyGrid;
            FreqLabelsToggle.IsOn = s.ShowFrequencyLabels;
            DbGridToggle.IsOn = s.ShowDbGrid;
            DbLabelsToggle.IsOn = s.ShowDbLabels;
            DbUnitsToggle.IsOn = s.ShowDbUnits;
            GridOpacitySlider.Value = Math.Round(s.GraphGridOpacity * 100);
            UpdateGridOpacityDescription(s.GraphGridOpacity);
            FreqReadoutToggle.IsOn = s.ShowFrequencyReadout;
            GainReadoutToggle.IsOn = s.ShowGainReadout;
        }
        finally { _suppress = false; }
    }

    private void OnFreqGridToggled(object sender, RoutedEventArgs e)
        => Commit(b => AppSettings.Instance.ShowFrequencyGrid = b, FreqGridToggle.IsOn);

    private void OnFreqLabelsToggled(object sender, RoutedEventArgs e)
        => Commit(b => AppSettings.Instance.ShowFrequencyLabels = b, FreqLabelsToggle.IsOn);

    private void OnDbGridToggled(object sender, RoutedEventArgs e)
        => Commit(b => AppSettings.Instance.ShowDbGrid = b, DbGridToggle.IsOn);

    private void OnDbLabelsToggled(object sender, RoutedEventArgs e)
        => Commit(b => AppSettings.Instance.ShowDbLabels = b, DbLabelsToggle.IsOn);

    private void OnDbUnitsToggled(object sender, RoutedEventArgs e)
        => Commit(b => AppSettings.Instance.ShowDbUnits = b, DbUnitsToggle.IsOn);

    private void UpdateGridOpacityDescription(double v) =>
        GridOpacityCard.Description = $"0% hides the grid, 100% is standard, 200% twice as strong. Current: {Math.Round(v * 100)}%";

    private void OnGridOpacityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateGridOpacityDescription(e.NewValue / 100.0);
        if (_suppress) return;
        AppSettings.Instance.GraphGridOpacity = e.NewValue / 100.0;
        AppSettings.Instance.Save();
        AppSettings.Instance.NotifyChanged();
    }

    private void OnFreqReadoutToggled(object sender, RoutedEventArgs e)
        => Commit(b => AppSettings.Instance.ShowFrequencyReadout = b, FreqReadoutToggle.IsOn);

    private void OnGainReadoutToggled(object sender, RoutedEventArgs e)
        => Commit(b => AppSettings.Instance.ShowGainReadout = b, GainReadoutToggle.IsOn);

    private void Commit(System.Action<bool> setter, bool v)
    {
        if (_suppress) return;
        setter(v);
        AppSettings.Instance.Save();
        AppSettings.Instance.NotifyChanged();
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "graphing.grid-labels";
    public string Title => "Grid & Labels";
    public SettingsCategory Category => SettingsCategory.Graphing;
    public string IconGlyph => ""; // GridView
    public int Order => 30;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new GraphingGridLabelsPage();
        p.Attach(vm, tracker);
        return p;
    }
}
