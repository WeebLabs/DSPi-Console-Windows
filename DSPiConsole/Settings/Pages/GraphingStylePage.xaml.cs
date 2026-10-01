using System.Globalization;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Graphing › Style — line styling and animation knobs for the EQ
/// frequency-response graph. All values live in
/// <see cref="AppSettings"/> and apply live (JSON write +
/// NotifyChanged on each control change).
///
/// <para>
/// Numeric sliders update the SettingsCard's Description slot with
/// their current value, so the user always sees the live number
/// without us needing a separate label control.
/// </para>
/// </summary>
public sealed partial class GraphingStylePage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public GraphingStylePage() { InitializeComponent(); }

    protected override void Refresh()
    {
        var s = AppSettings.Instance;
        _suppress = true;
        try
        {
            GlowToggle.IsOn = s.ShowGraphGlow;
            LineWidthSlider.Value = s.GraphLineWidth;
            AnimSpeedSlider.Value = s.GraphAnimationSpeed;
            DottedInactiveToggle.IsOn = s.DottedInactiveChannels;
            GainLevelToggle.IsOn = s.GraphLevelIncludesGain;
            PopoutFollowsToggle.IsOn = s.PopoutFollowsSelectedChannel;
            ShowPhaseToggle.IsOn = s.ShowPhase;
            PhaseUnwrapToggle.IsOn = s.PhaseUnwrapped;
            PhaseUnwrapCard.IsEnabled = s.ShowPhase;
            UpdateLineWidthDescription(s.GraphLineWidth);
            UpdateAnimSpeedDescription(s.GraphAnimationSpeed);
        }
        finally { _suppress = false; }
    }

    // The SettingsCard's Description is the natural home for a live
    // numeric readout — keeps the slider value visible without a
    // sidecar TextBlock control.
    private void UpdateLineWidthDescription(double v) =>
        LineWidthCard.Description = $"Stroke thickness for graph curves. Current: {v.ToString("F1", CultureInfo.InvariantCulture)} px";

    private void UpdateAnimSpeedDescription(double v) =>
        AnimSpeedCard.Description = $"How quickly graph changes animate. Current: {v.ToString("F2", CultureInfo.InvariantCulture)}";

    private void OnGlowToggled(object sender, RoutedEventArgs e) =>
        CommitBool(GlowToggle.IsOn, b => AppSettings.Instance.ShowGraphGlow = b);

    private void OnLineWidthChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateLineWidthDescription(e.NewValue);
        CommitDouble(e.NewValue, v => AppSettings.Instance.GraphLineWidth = v);
    }

    private void OnAnimSpeedChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateAnimSpeedDescription(e.NewValue);
        CommitDouble(e.NewValue, v => AppSettings.Instance.GraphAnimationSpeed = v);
    }

    private void OnDottedInactiveToggled(object sender, RoutedEventArgs e) =>
        CommitBool(DottedInactiveToggle.IsOn, b => AppSettings.Instance.DottedInactiveChannels = b);

    private void OnGainLevelToggled(object sender, RoutedEventArgs e) =>
        CommitBool(GainLevelToggle.IsOn, b => AppSettings.Instance.GraphLevelIncludesGain = b);

    private void OnPopoutFollowsToggled(object sender, RoutedEventArgs e) =>
        CommitBool(PopoutFollowsToggle.IsOn, b => AppSettings.Instance.PopoutFollowsSelectedChannel = b);

    private void OnShowPhaseToggled(object sender, RoutedEventArgs e)
    {
        PhaseUnwrapCard.IsEnabled = ShowPhaseToggle.IsOn;
        CommitBool(ShowPhaseToggle.IsOn, b => AppSettings.Instance.ShowPhase = b);
    }

    private void OnPhaseUnwrapToggled(object sender, RoutedEventArgs e) =>
        CommitBool(PhaseUnwrapToggle.IsOn, b => AppSettings.Instance.PhaseUnwrapped = b);

    // Shared commit helpers — every Live-apply control on this page
    // funnels through one of these. Centralises the suppress-guard,
    // JSON save, and NotifyChanged.
    private void CommitBool(bool v, System.Action<bool> setter)
    {
        if (_suppress) return;
        setter(v);
        AppSettings.Instance.Save();
        AppSettings.Instance.NotifyChanged();
    }

    private void CommitDouble(double v, System.Action<double> setter)
    {
        if (_suppress) return;
        setter(v);
        AppSettings.Instance.Save();
        AppSettings.Instance.NotifyChanged();
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "graphing.style";
    public string Title => "Style";
    public SettingsCategory Category => SettingsCategory.Graphing;
    public string IconGlyph => ""; // Brush
    public int Order => 10;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new GraphingStylePage();
        p.Attach(vm, tracker);
        return p;
    }
}
