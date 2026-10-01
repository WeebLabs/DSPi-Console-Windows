using CommunityToolkit.WinUI.Controls;
using DSPiConsole.Core.Rta;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Graphing › Spectrum Analyser. Two kinds of setting sit together here. How
/// the spectrum is drawn is the app's own preference. The transform size,
/// averaging and peak-hold decay belong to the device, but its analyser is
/// transient (the firmware forgets them at every power cycle), so the app is
/// the only place they are remembered, and it pushes them whenever a view
/// starts watching. Everything applies live. Port of the macOS Console's
/// SpectrumSettingsTab.
/// </summary>
public sealed class GraphingSpectrumPage : ISettingsPage
{
    public string Id => "graphing.spectrum";
    public string Title => "Spectrum Analyser";
    public SettingsCategory Category => SettingsCategory.Graphing;
    public string IconGlyph => ""; // Diagnostic
    public int Order => 40;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker) => new SpectrumSettingsPanel(vm);
}

internal sealed class SpectrumSettingsPanel : UserControl
{
    private readonly MainViewModel _vm;
    private readonly StackPanel _root = new() { Spacing = 4 };
    private bool _building;
    /// <summary>What the page shows of the device, so it rebuilds only when
    /// that changes and not on every engine state change while in use.</summary>
    private string _builtKey = "";

    public SpectrumSettingsPanel(MainViewModel vm)
    {
        _vm = vm;
        Content = _root;
        Loaded += (_, _) =>
        {
            _vm.RtaStateChanged += OnEngineChanged;
            Build();
        };
        Unloaded += (_, _) => _vm.RtaStateChanged -= OnEngineChanged;
    }

    private static AppSettings S => AppSettings.Instance;
    private Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    /// <summary>The caps arriving (or a device without the analyser) changes
    /// which sizes are offered and the engine note.</summary>
    private void OnEngineChanged(object? sender, EventArgs e)
    {
        if (DeviceKey() != _builtKey) Build();
    }

    private string DeviceKey()
    {
        var engine = _vm.Rta;
        return $"{_vm.IsDeviceConnected}|{engine.Supported}|{engine.Caps.FftOrderMin}|{engine.Caps.FftOrderMax}|{engine.Caps.DynamicRangeDb}";
    }

    private void Build()
    {
        _building = true;
        _builtKey = DeviceKey();
        _root.Children.Clear();
        var engine = _vm.Rta;

        // ── Display ──
        _root.Children.Add(Section("Display"));
        var strength = new Slider { Minimum = 30, Maximum = 100, StepFrequency = 5, Width = 220, Value = Math.Round(S.RtaGraphOpacity * 100) };
        var strengthCard = Card($"Spectrum strength: {strength.Value:0}%",
            "How far the fill comes forward behind the curves when the spectrum is drawn on the response graph.", strength);
        strength.ValueChanged += (_, e) =>
        {
            strengthCard.Header = $"Spectrum strength: {e.NewValue:0}%";
            Commit(() => S.RtaGraphOpacity = e.NewValue / 100);
        };
        _root.Children.Add(strengthCard);
        ComboBox? decay = null;
        // Peak decay means nothing without the peak hold.
        var peak = Toggle(S.RtaShowPeakHold, b => { S.RtaShowPeakHold = b; if (decay != null) decay.IsEnabled = b; });
        _root.Children.Add(Card("Peak hold", "A cap above each band marking its recent maximum.", peak));
        _root.Children.Add(Card("Smoothing",
            "Glides the spectrum between device frames the way the peak meters glide between polls. It matters most with several channels selected, where one channel refreshes only every few hundred milliseconds and the picture would otherwise step.",
            Toggle(S.RtaSmoothingOn, b => S.RtaSmoothingOn = b)));
        _root.Children.Add(Note("Choose which channels to show, and whether to draw them on the response graph, as bars, or both, from the gear at the top right of the response graph. The analyser only runs while something is watching it, so with the spectrum hidden and the analyser window closed the device stops it and spends nothing."));

        // ── Vertical scale ──
        _root.Children.Add(Section("Vertical scale"));
        _root.Children.Add(Card("Floor", "The level at the bottom of every spectrum display.",
            Picker(new[] { -60.0, -90.0, -120.0 }, v => $"{v:0} dB", S.RtaFloorDb, v => S.RtaFloorDb = v)));
        _root.Children.Add(Card("Ceiling",
            "Six decibels of headroom above full scale is the default because upmix-derived rows and hot EQ can legitimately exceed 0 dBFS.",
            Picker(new[] { 0.0, 6.0, 12.0 }, v => v > 0 ? $"+{v:0} dBFS" : $"{v:0} dBFS", S.RtaCeilingDb, v => S.RtaCeilingDb = v)));

        // ── Engine ──
        _root.Children.Add(Section("Engine"));
        // Sizes this device offers, from its caps where read and from the
        // protocol's range before that, so no size is offered that it would refuse.
        int lo = RtaWire.OrderMin, hi = RtaWire.OrderMax;
        if (engine.Supported && engine.Caps.FftOrderMax >= engine.Caps.FftOrderMin)
            (lo, hi) = (engine.Caps.FftOrderMin, engine.Caps.FftOrderMax);
        var orders = Enumerable.Range(lo, hi - lo + 1).Select(o => (double)o).ToArray();
        _root.Children.Add(Card("Transform size",
            "More points resolve lower frequencies, but a frame takes longer to fill, so each channel refreshes less often.",
            Picker(orders, o => $"{1 << (int)o} points", Math.Clamp(S.RtaFftOrder, lo, hi), o => S.RtaFftOrder = (int)o, pushesOptions: true)));
        _root.Children.Add(Card("Averaging", "How long the bands and the FFT bins are averaged over, in power.",
            Picker(new[] { 0.0, 50, 125, 300, 1000, 3000 }, v => v == 0 ? "Off" : v >= 1000 ? $"{v / 1000:0} s" : $"{v:0} ms",
                S.RtaAvgMs, v => S.RtaAvgMs = (int)v, pushesOptions: true)));
        decay = Picker(new[] { 0.0, 4, 12, 30 }, v => v == 0 ? "Off" : $"{v:0} dB/s",
            S.RtaPeakDecayDbS, v => S.RtaPeakDecayDbS = (int)v, pushesOptions: true);
        decay.IsEnabled = S.RtaShowPeakHold;
        _root.Children.Add(Card("Peak decay", "How fast the peak-hold caps fall back.", decay));
        if (_vm.IsDeviceConnected && !engine.Supported)
            _root.Children.Add(Note("The connected firmware has no spectrum analyser, so these settings have nothing to apply to until it is updated.",
                new SolidColorBrush(Color.FromArgb(255, 255, 159, 10))));
        else if (engine.Supported)
            _root.Children.Add(Note($"This device reports {engine.Caps.DynamicRangeDb} dB of usable range and transforms up to {1 << engine.Caps.FftOrderMax} points."));
        _building = false;
    }

    // ── Building blocks ──

    private TextBlock Section(string title) => new()
    {
        Text = title,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(1, 16, 0, 4),
    };

    private static SettingsCard Card(string header, string description, FrameworkElement control) =>
        new() { Header = header, Description = description, Content = control };

    private TextBlock Note(string text, Brush? brush = null) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Foreground = brush ?? Secondary, Margin = new Thickness(2, 4, 2, 0),
    };

    private ToggleSwitch Toggle(bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { IsOn = value, OnContent = "On", OffContent = "Off" };
        t.Toggled += (_, _) => Commit(() => set(t.IsOn));
        return t;
    }

    private ComboBox Picker(double[] values, Func<double, string> label, double current, Action<double> set, bool pushesOptions = false)
    {
        var box = new ComboBox { MinWidth = 140 };
        int selected = 0;
        for (int i = 0; i < values.Length; i++)
        {
            box.Items.Add(new ComboBoxItem { Content = label(values[i]), Tag = values[i] });
            if (Math.Abs(values[i] - current) < 0.5) selected = i;
        }
        box.SelectedIndex = selected;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not ComboBoxItem { Tag: double v }) return;
            Commit(() => set(v), pushesOptions);
        };
        return box;
    }

    /// <summary>Saves and notifies; the device's own options are pushed to the
    /// engine, which sends them on its next poll.</summary>
    private void Commit(Action apply, bool pushesOptions = false)
    {
        if (_building) return;
        apply();
        S.Save();
        S.NotifyChanged();
        if (pushesOptions) _vm.Rta.SetOptions(MainViewModel.RtaOptionsFromSettings);
    }
}
