using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Hardware › External Mute Control — flat SettingsCard list, no
/// Expander. All five persistent fields (enable, polarity, pin, hold,
/// release) ride a single firmware command, so every edit stages
/// under one tracker key with the full updated config; the InfoBar
/// shows a single pending entry regardless of how many fields the
/// user touched.
///
/// <para>
/// The Test Pulse button stays live — it's a one-shot device action
/// with no persistent state, so there's nothing to stage. Slider
/// edits coalesce via a short DispatcherTimer so a steady drag
/// produces one Stage call per pause rather than one per pixel.
/// </para>
/// </summary>
public sealed partial class HardwareDacMutePage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    private const string ConfigKey = "hardware.dac-mute.config";

    // The config last staged from this page. While its entry is still pending,
    // the page shows it rather than the device's: navigating away and back
    // used to reset the controls to the device values, and the next edit then
    // restaged the whole config from them, silently dropping the pending one.
    private DacHwMuteConfig? _staged;

    public HardwareDacMutePage()
    {
        InitializeComponent();

        // The "No pin (disabled)" sentinel is permanent — it stays in
        // the combo at index 0 across rebuilds and is how the user
        // disables the feature without burning a GPIO. The GPIO
        // entries past it are rebuilt by RebuildPinCombo (filter on
        // populate: pins owned elsewhere are omitted).
        PinCombo.Items.Add(new ComboBoxItem
        {
            Content = "No pin (disabled)",
            Tag = DacHwMuteConfig.PinNone
        });

        // Subscriptions in Loaded/Unloaded so they survive sidebar
        // navigation cycles (see HardwareOutputAssignmentPage for why).
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        base.Attach(vm, tracker);
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        HardwarePins.PinAssignmentsChanged += OnExternalPinChange;
        if (Vm != null)
        {
            Vm.PropertyChanged -= OnVmPropertyChanged;
            Vm.PropertyChanged += OnVmPropertyChanged;
            // Re-sync from VM state in case events were missed while
            // we were unloaded.
            Refresh();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnExternalPinChange() =>
        // External pin change (e.g. BCK moved on the I²S page).
        // Preserve whatever pin the user currently has selected —
        // that's either the device value (no pending edit) or the
        // user's pending choice. Either way it remains pickable.
        DispatcherQueue.TryEnqueue(() => RebuildPinCombo(SelectedPin()));

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // VM signals a fresh device read (e.g. after a preset load or
        // reconnect) via these properties. Pull the UI back into sync.
        if (e.PropertyName == nameof(MainViewModel.DacHwMute)
            || e.PropertyName == nameof(MainViewModel.DacHwMuteSupported))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;

        bool pending = _staged != null && Tracker?.Pending.Any(p => p.Key == ConfigKey) == true;
        var cfg = pending ? _staged! : Vm.DacHwMute;
        _suppress = true;
        try
        {
            EnableToggle.IsOn = cfg.Enabled;
            // Index 0 = Active Low, 1 = Active High.
            PolarityCombo.SelectedIndex = cfg.ActiveLow ? 0 : 1;
            SelectMsInCombo(HoldCombo, cfg.HoldMs);
            SelectMsInCombo(ReleaseCombo, cfg.ReleaseMs);
        }
        finally { _suppress = false; }

        // Rebuild the GPIO combo with cfg.Pin as the preserved
        // selection — guarantees the device's current pin is visible
        // even if some other feature owns it on this map.
        RebuildPinCombo(cfg.Pin);
        UpdateTestButtonEnablement();

        // Where the mute pin is set, for a click on it in the Overview's map. The
        // picker, as everywhere: the flash rings the control you came to change,
        // which every page hands it so that arriving looks the same wherever you
        // arrive. Registered on the same condition the map claims the pin -
        // configured, enabled or not, since a disabled mute still holds its
        // GPIO. Gating this on Enabled too left the map offering a click that
        // arrived here with nothing registered to ring.
        // The device's pin, which is what the map shows, even while a change is pending.
        ClearPinTargets();
        byte mapPin = Vm.DacHwMute.Pin;
        if (mapPin != DacHwMuteConfig.PinNone) RegisterPinTarget(mapPin, PinCombo);
    }

    private void SelectPinInCombo(byte pin)
    {
        for (int i = 0; i < PinCombo.Items.Count; i++)
        {
            if (PinCombo.Items[i] is ComboBoxItem item && item.Tag is byte p && p == pin)
            {
                PinCombo.SelectedIndex = i;
                return;
            }
        }
        // Pin not in our list — fall back to "No pin" so the UI doesn't
        // show a confusing blank selection.
        PinCombo.SelectedIndex = 0;
    }

    private byte SelectedPin() =>
        PinCombo.SelectedItem is ComboBoxItem item && item.Tag is byte b
            ? b : DacHwMuteConfig.PinNone;

    /// <summary>Find the combo entry whose string Tag matches <paramref name="ms"/>
    /// and select it. If no entry matches (e.g. a legacy preset with a value
    /// outside the 5/10/25/50/100 set), leave the combo's selection cleared so
    /// the user sees a blank rather than a silently-snapped value.</summary>
    private static void SelectMsInCombo(ComboBox combo, ushort ms)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item
                && ushort.TryParse(item.Tag?.ToString(), out var v)
                && v == ms)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = -1;
    }

    /// <summary>Read a ms value from a Hold / Release combo. XAML Tags
    /// are strings; parse defensively and fall back to the current
    /// device value if nothing is selected (legacy out-of-range value).</summary>
    private ushort SelectedMs(ComboBox combo, ushort fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item
            && ushort.TryParse(item.Tag?.ToString(), out var v))
            return v;
        return fallback;
    }

    /// <summary>Rebuild the GPIO part of the pin combo so it lists
    /// only pins this picker can actually use — <paramref name="preserve"/>
    /// (always included, so the user's current / pending choice
    /// stays visible) plus any audio-capable GPIO not claimed by
    /// another feature. The "No pin (disabled)" sentinel at index 0
    /// is permanent and stays across rebuilds.</summary>
    private void RebuildPinCombo(byte preserve)
    {
        if (Vm == null) return;
        var owners = HardwarePins.BuildOwnerMap(Vm, excludeDacMuteSelf: true);

        _suppress = true;
        try
        {
            // Strip everything past the "No pin" sentinel at index 0.
            while (PinCombo.Items.Count > 1) PinCombo.Items.RemoveAt(1);

            foreach (var pin in HardwarePins.ValidPins)
            {
                if (pin == preserve || !owners.ContainsKey(pin))
                    PinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
            }
            SelectPinInCombo(preserve);
        }
        finally { _suppress = false; }
    }

    private void UpdateTestButtonEnablement()
    {
        // The firmware rejects test pulses when the feature is disabled
        // or no pin is configured. Disabling the button locally saves
        // a wasted USB round-trip and clarifies UX.
        TestButton.IsEnabled = EnableToggle.IsOn && SelectedPin() != DacHwMuteConfig.PinNone;
    }

    // ── Field handlers ────────────────────────────────────────────────

    private void OnConfigChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        UpdateTestButtonEnablement();

        // Pin selection changing should also notify the cross-page
        // bus so other Hardware pages refresh their conflict labels
        // — but only AFTER the change actually applies. Phase 2's
        // tracker pattern: stage now; the Apply lambda raises the
        // event on success.
        StageConfigSnapshot();
    }

    private void OnConfigChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        UpdateTestButtonEnablement();
        StageConfigSnapshot();
    }

    /// <summary>
    /// Capture the current UI state into a <see cref="DacHwMuteConfig"/>
    /// and stage it. All five fields share one key so the InfoBar
    /// shows a single entry, with the old/new display strings
    /// derived from the most-changed field (or a generic label when
    /// multiple fields differ).
    /// </summary>
    private void StageConfigSnapshot()
    {
        if (Vm == null || Tracker == null) return;

        var current = Vm.DacHwMute;
        var pending = current.With(
            enabled: EnableToggle.IsOn,
            activeLow: PolarityCombo.SelectedIndex != 1,
            pin: SelectedPin(),
            holdMs: SelectedMs(HoldCombo, current.HoldMs),
            releaseMs: SelectedMs(ReleaseCombo, current.ReleaseMs));

        _staged = pending;
        var vm = Vm;
        Tracker.Stage(new PendingChange(
            Key: ConfigKey,
            PageId: Id,
            FieldLabel: "External mute config",
            OldDisplay: DescribeConfig(current),
            NewDisplay: DescribeConfig(pending),
            Apply: async () =>
            {
                var result = await vm.ApplyDacHwMuteAsync(pending);
                if (result == null) return (byte)0xFF;
                // Pin change → tell other Hardware pages to refresh
                // their conflict labels.
                if (result.Pin != current.Pin)
                    HardwarePins.RaisePinAssignmentsChanged();
                return (byte)0;
            }));
    }

    private static string DescribeConfig(DacHwMuteConfig c)
    {
        // Always include every field. The tracker dedupes staged changes
        // by OldDisplay == NewDisplay; collapsing the disabled state to
        // a bare "Off" string caused edits made while disabled (pin,
        // polarity, hold, release) to compare equal to the saved state
        // and silently drop from the pending list.
        var pinLabel = c.Pin == DacHwMuteConfig.PinNone ? "no pin" : $"GPIO {c.Pin}";
        var polarity = c.ActiveLow ? "active-low" : "active-high";
        var enabled = c.Enabled ? "on" : "off";
        return string.Create(CultureInfo.InvariantCulture,
            $"{enabled}, {pinLabel}, {polarity}, {c.HoldMs}/{c.ReleaseMs} ms");
    }

    // ── Test pulse (live action, not staged) ──────────────────────────

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        TestButton.IsEnabled = false;
        TestFeedback.Text = "Pulsing mute for ~1 second…";
        TestFeedback.Visibility = Visibility.Visible;
        try
        {
            var status = await Vm.TestDacHwMuteAsync();
            TestFeedback.Text = status switch
            {
                0 => "Test pulse fired. The DAC should mute audibly for ~1 s.",
                0xFF => "USB transfer failed — is the device still connected?",
                _ => $"Firmware rejected the test (status 0x{status:X2})."
            };
        }
        catch (Exception ex)
        {
            TestFeedback.Text = "Test failed: " + ex.Message;
        }
        finally
        {
            // 1.2 s window: firmware test pulse is ~1 s; this prevents
            // double-clicks landing while the firmware is still pulsing.
            await Task.Delay(1200);
            UpdateTestButtonEnablement();
        }
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "hardware.dac-mute";
    public string Title => "External Mute Control";
    public SettingsCategory Category => SettingsCategory.Control;
    public string IconGlyph => ""; // Mute
    public int Order => 50;
    // V10+ feature — hide the sidebar entry entirely on older firmware.
    public bool IsAvailable(MainViewModel vm) => vm.DacHwMuteSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareDacMutePage();
        p.Attach(vm, tracker);
        return p;
    }
}
