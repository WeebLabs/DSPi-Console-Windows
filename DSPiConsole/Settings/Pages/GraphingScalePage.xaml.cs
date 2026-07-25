using System.Globalization;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Graphing › Scale — vertical range / center, plus min and max
/// frequency. All values are <see cref="AppSettings"/> JSON; live-apply.
///
/// <para>
/// The dB range slider's Description shows the symmetric ±dB span;
/// the center slider's Description shows the resulting visible range
/// (bottom dB → top dB), updated as either slider moves.
/// </para>
/// </summary>
public sealed partial class GraphingScalePage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public GraphingScalePage() { InitializeComponent(); }

    protected override void Refresh()
    {
        var s = AppSettings.Instance;
        _suppress = true;
        try
        {
            DbRangeSlider.Value = s.GraphDbRange;
            DbCenterSlider.Value = s.GraphDbCenter;
            SelectComboByTag(MinFreqCombo, s.GraphMinFrequency);
            SelectComboByTag(MaxFreqCombo, s.GraphMaxFrequency);
            UpdateRangeDescription(s.GraphDbRange);
            UpdateCenterDescription(s.GraphDbCenter, s.GraphDbRange);
        }
        finally { _suppress = false; }
    }

    private void UpdateRangeDescription(double range) =>
        DbRangeCard.Description = $"How tall the graph is in dB. Current: ±{range / 2:0} dB";

    private void UpdateCenterDescription(double center, double range)
    {
        var bottom = center - range / 2;
        var top = center + range / 2;
        DbCenterCard.Description = $"Vertical centre of the graph. Range: {bottom:0} to {top:+0;-0;0} dB";
    }

    private void OnDbRangeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // ValueChanged fires during InitializeComponent — setting Minimum="10"
        // in XAML coerces Value off its 0 default — before the sibling elements
        // below this slider (DbCenterSlider/DbCenterCard) have been created.
        // Bail out until the page is fully built to avoid a NullReferenceException.
        if (DbCenterSlider is null || DbCenterCard is null || DbRangeCard is null) return;
        UpdateRangeDescription(e.NewValue);
        UpdateCenterDescription(DbCenterSlider.Value, e.NewValue);
        Commit(() => AppSettings.Instance.GraphDbRange = e.NewValue);
    }

    private void OnDbCenterChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (DbRangeSlider is null || DbCenterCard is null) return;
        UpdateCenterDescription(e.NewValue, DbRangeSlider.Value);
        Commit(() => AppSettings.Instance.GraphDbCenter = e.NewValue);
    }

    private void OnMinFreqChanged(object sender, SelectionChangedEventArgs e) =>
        CommitCombo(MinFreqCombo, 20.0, v => AppSettings.Instance.GraphMinFrequency = v);

    private void OnMaxFreqChanged(object sender, SelectionChangedEventArgs e) =>
        CommitCombo(MaxFreqCombo, 20000.0, v => AppSettings.Instance.GraphMaxFrequency = v);

    private void Commit(System.Action mutate)
    {
        if (_suppress) return;
        mutate();
        AppSettings.Instance.Save();
        AppSettings.Instance.NotifyChanged();
    }

    private void CommitCombo(ComboBox combo, double fallback, System.Action<double> setter)
    {
        if (_suppress) return;
        var v = ReadComboTagDouble(combo, fallback);
        setter(v);
        AppSettings.Instance.Save();
        AppSettings.Instance.NotifyChanged();
    }

    private static void SelectComboByTag(ComboBox combo, double value)
    {
        var valStr = value.ToString("0");
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag is string tag && tag == valStr)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static double ReadComboTagDouble(ComboBox combo, double fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        return fallback;
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "graphing.scale";
    public string Title => "Scale";
    public SettingsCategory Category => SettingsCategory.Graphing;
    public string IconGlyph => ""; // Ruler
    public int Order => 20;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new GraphingScalePage();
        p.Attach(vm, tracker);
        return p;
    }
}
