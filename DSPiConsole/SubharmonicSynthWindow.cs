using System.ComponentModel;
using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// The subharmonic synthesizer (firmware 1.1.6 beta 4, wire V29/V30): a dbx 120A
/// style octave divider. Two columns: the band graph over the band levels that
/// move it, and the rest of the signal path (selectivity, sub ceiling, LF
/// boost, outputs). After the macOS Console's SubharmonicSynthView. Solo is
/// runtime-only and is switched off when the window closes, so a device is
/// never left playing without its program signal.
/// </summary>
public sealed class SubharmonicSynthWindow : Window
{
    private sealed record StartingPoint(string Name, string Detail, float Low, float High, float Boost);

    /// <summary>The spec's table (§6). None uses the 56-80 Hz band, which is why
    /// applying one sets it back to its floor: a half-applied starting point is
    /// worse than none.</summary>
    private static readonly StartingPoint[] StartingPoints =
    {
        new("Subwoofer feed", "Subtle added weight", -6, -6, 0),
        new("Club / large PA", "Lower two bands, full", 0, 0, 3),
        new("Thin recordings", "Add a missing bottom", 0, -6, 3),
        new("Cinema LFE", "Lowest octave only", 3, -12, 0),
    };

    private readonly MainViewModel _vm;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private readonly Grid _root = new() { Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
    private readonly ContentControl _bodyHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly ToggleSwitch _enable = new() { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _solo = new() { Content = "SOLO", FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Padding = new Thickness(8, 2, 8, 2), MinHeight = 0, Height = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly DispatcherQueueTimer _meterTimer;
    private bool _updating;
    private bool _meterBusy;

    // Body controls, rebuilt with the body.
    private SubharmBandGraph? _graph;
    private TextBlock? _headroom;
    /// <summary>What the headroom and selectivity tooltips hang on. Kept, not
    /// reached through Parent, which is null for a panel child built in code.</summary>
    private FrameworkElement? _headroomRow, _selectPicker;
    private ParameterRow? _low, _high, _top, _depth, _hold, _ceiling, _boost;
    private SegmentedPicker? _select;
    private TextBlock? _selectSummary;
    private StackPanel? _selectRows;
    private ToggleSwitch? _linkPairs;
    private readonly List<(ToggleButton Chip, Border Meter, Grid Track)> _chips = new();
    private DropDownButton? _presets, _maskPresets;

    public SubharmonicSynthWindow(MainViewModel vm)
    {
        _vm = vm;
        ToolWindowChrome.Apply(this, "Subharmonic Synthesizer", 900, 660);

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(BuildHeader());
        var rule = Rule();
        Grid.SetRow(rule, 1);
        _root.Children.Add(rule);
        Grid.SetRow(_bodyHost, 2);
        _root.Children.Add(_bodyHost);
        Content = _root;

        _enable.Toggled += (_, _) => { if (!_updating) _vm.SubharmEnabled = _enable.IsOn; };
        _solo.Click += (_, _) => _vm.SubharmSolo = !_vm.SubharmSolo;

        // The sub meter is a decaying peak; 10 Hz is enough to watch a level land.
        _meterTimer = DispatcherQueue.CreateTimer();
        _meterTimer.Interval = TimeSpan.FromMilliseconds(100);
        _meterTimer.Tick += (_, _) => PollMeter();
        _meterTimer.Start();

        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) =>
        {
            _meterTimer.Stop();
            _vm.PropertyChanged -= OnVmPropertyChanged;
            // Put the program signal back: solo must never outlive this window.
            if (_vm.SubharmSolo) _vm.SubharmSolo = false;
        };

        BuildBody();
        // The bulk image has everything but the headroom (derived) and solo
        // (runtime only); read both so the window opens with live figures.
        if (_vm.SubharmSupported) Task.Run(_vm.FetchSubharmRuntimeState);
    }

    // ── Layout helpers ──

    private static Border Rule() => new() { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 10,
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        Foreground = _secondary,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Grid LabelRow(string label, FrameworkElement right)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(SectionLabel(label));
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    private static DropDownButton MenuButton(string text, MenuFlyout menu) => new()
    {
        Content = text,
        FontSize = 11,
        Padding = new Thickness(8, 2, 8, 3),
        Flyout = menu,
        Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0),
    };

    private static MenuFlyoutItem MenuItem(string text, Action act)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => act();
        return item;
    }

    // ── Header ──

    private FrameworkElement BuildHeader()
    {
        var header = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon
        {
            Glyph = "",
            FontSize = 22,
            Foreground = new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColor"]),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Subharmonic Synthesizer", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = "Generates a subharmonic at half the frequency of the source's bass", FontSize = 10, Foreground = _secondary });
        header.Children.Add(icon);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Grid.SetColumn(_solo, 2);
        header.Children.Add(_solo);
        Grid.SetColumn(_enable, 3);
        header.Children.Add(_enable);
        return header;
    }

    // ── Body ──

    private void BuildBody()
    {
        _chips.Clear();
        if (!_vm.SubharmSupported)
        {
            var note = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(24) };
            note.Children.Add(new FontIcon { Glyph = "", FontSize = 28, Foreground = _secondary });
            note.Children.Add(new TextBlock { Text = "Requires DSPi firmware 1.1.6 beta 4 or later.", FontSize = 12, Foreground = _secondary, HorizontalAlignment = HorizontalAlignment.Center });
            note.Children.Add(new TextBlock { Text = "Update the DSPi firmware to use the Subharmonic Synthesizer.", FontSize = 10, Foreground = _secondary, HorizontalAlignment = HorizontalAlignment.Center });
            _bodyHost.Content = note;
            _graph = null;
            Refresh();
            return;
        }

        bool extended = _vm.SubharmExtendedSupported;
        var left = new StackPanel { Spacing = 14, Padding = new Thickness(16) };
        var right = new StackPanel { Spacing = 14, Padding = new Thickness(16) };

        // Left: the graph over the levels that move it.
        var startMenu = new MenuFlyout();
        foreach (var p in StartingPoints)
            startMenu.Items.Add(MenuItem($"{p.Name} - {p.Detail}", () =>
            {
                _vm.SubharmLowDb = p.Low;
                _vm.SubharmHighDb = p.High;
                _vm.SubharmBoostDb = p.Boost;
                if (_vm.SubharmExtendedSupported) _vm.SubharmTopDb = SubharmLimits.LevelMinDb;
            }));
        _presets = MenuButton("Apply preset", startMenu);
        left.Children.Add(LabelRow("BANDS", _presets));

        _graph = new SubharmBandGraph();
        left.Children.Add(new Border
        {
            Height = 188,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Background = new SolidColorBrush(ToolWindowChrome.PanelColor),
            BorderBrush = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = _graph,
        });
        _headroom = new TextBlock { FontSize = 11 };
        _headroomRow = LabelRow("HEADROOM COST", _headroom);
        _headroomRow.Margin = new Thickness(0, -6, 0, 0);
        left.Children.Add(_headroomRow);
        left.Children.Add(Rule());
        left.Children.Add(SectionLabel("LEVELS"));
        _low = BandRow("24 - 36 Hz", "48 - 72 Hz", SubharmField.Low, v => _vm.SubharmLowDb = v,
            "Level of the sub synthesized from program content between 48 and 72 Hz. At 0 dB it comes out 1.4 dB below the bass that produced it, which is the divider's own gain.");
        left.Children.Add(_low);
        left.Children.Add(Rule());
        _high = BandRow("36 - 56 Hz", "72 - 112 Hz", SubharmField.High, v => _vm.SubharmHighDb = v,
            "Level of the sub synthesized from program content between 72 and 112 Hz. This band has its own divider, so a bass note here and a kick in the band below are tracked independently.");
        left.Children.Add(_high);
        if (extended)
        {
            left.Children.Add(Rule());
            _top = BandRow("56 - 80 Hz", "112 - 160 Hz", SubharmField.Top, v => _vm.SubharmTopDb = v,
                "Level of the sub synthesized from program content between 112 and 160 Hz. It ships off: this band reaches up into the range where a divided sub starts to compete with the program's own fundamentals. Turn it up for a subwoofer that cannot reach the lowest octave.");
            left.Children.Add(_top);
        }
        else _top = null;

        // Right: the rest of the signal path.
        if (extended)
        {
            right.Children.Add(BuildSelectivity());
            right.Children.Add(Rule());
            right.Children.Add(SectionLabel("SUB CEILING"));
            _ceiling = new ParameterRow("Threshold", "dB", SubharmLimits.CeilingMinDb, SubharmLimits.CeilingMaxDb,
                set: v => _vm.SubharmCeilingDb = v, live: v => Live(SubharmField.Ceiling, v))
            {
                ScrollStep = 1,
                MaxDecimals = 0,
                Ends = ("-40 dBFS", "Off"),
                DisplayOverride = v => v >= SubharmLimits.CeilingMaxDb ? "Off" : null,
                Help = "A soft limit on the synthesized sub just before it is mixed back in, capping how far it can push a driver without touching the program signal. It is an absolute level, so a ceiling at full scale limits nothing and means the stage is off. With it on, the headroom cost is only the ceiling's worth. A loud onset overshoots it by a few dB for the first few milliseconds while the limiter's 3 ms attack catches up.",
            };
            right.Children.Add(_ceiling);
            right.Children.Add(Rule());
        }
        else
        {
            _select = null; _selectRows = null; _depth = null; _hold = null; _ceiling = null;
        }

        right.Children.Add(SectionLabel("LF BOOST"));
        _boost = new ParameterRow("70 Hz bell", "dB", SubharmLimits.BoostMinDb, SubharmLimits.BoostMaxDb,
            set: v => _vm.SubharmBoostDb = v, live: v => Live(SubharmField.Boost, v))
        {
            ScrollStep = 0.5f,
            MaxDecimals = 1,
            Ends = ("Off", $"+{SubharmLimits.BoostMaxDb:0} dB"),
            Help = "A gentle bell at 70 Hz, Q 0.9, applied to the whole output after the subs are summed. It fills the gap between the synthesized sub and the program's own mid-bass. Meant to stay gentle, as on the dbx.",
        };
        right.Children.Add(_boost);
        right.Children.Add(Rule());
        right.Children.Add(BuildOutputs(extended));

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(left);
        var divider = new Border { Width = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };
        Grid.SetColumn(divider, 1);
        columns.Children.Add(divider);
        Grid.SetColumn(right, 2);
        columns.Children.Add(right);
        _bodyHost.Content = new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Refresh();
    }

    /// <summary>One band level. Its floor is a real setting, not the end of a
    /// range: it switches the band off and skips its divider, so it reads "Off".</summary>
    private ParameterRow BandRow(string title, string source, SubharmField field, Action<float> set, string help) =>
        new(title, "dB", SubharmLimits.LevelMinDb, SubharmLimits.LevelMaxDb, set: set, live: v => Live(field, v))
        {
            Subtitle = $"Derived from {source}",
            ScrollStep = 0.5f,
            MaxDecimals = 1,
            Ends = ("Off", $"+{SubharmLimits.LevelMaxDb:0} dB"),
            DisplayOverride = v => v <= SubharmLimits.LevelMinDb ? "Off" : null,
            Help = help,
        };

    /// <summary>A drag's value: to the device, and to the graph, which follows.</summary>
    private void Live(SubharmField field, float value)
    {
        _vm.SendSubharmLive(field, value);
        _graph?.SetLive(field, value);
    }

    private FrameworkElement BuildSelectivity()
    {
        var section = new StackPanel { Spacing = 14 };
        section.Children.Add(SectionLabel("SELECTIVITY"));
        _select = new SegmentedPicker(new[] { "All material", "Percussive", "Sustained" }, height: 26, fontSize: 12);
        _select.Picked += mode => _vm.SubharmSelectMode = mode;
        _selectSummary = new TextBlock { FontSize = 9, Foreground = _secondary, TextWrapping = TextWrapping.Wrap };
        var picker = new StackPanel { Spacing = 6 };
        picker.Children.Add(_select);
        picker.Children.Add(_selectSummary);
        section.Children.Add(picker);
        _selectPicker = picker;

        // Depth and hold do nothing in "all material", so they are hidden there.
        _selectRows = new StackPanel { Spacing = 14 };
        _depth = new ParameterRow("Depth", "%", SubharmLimits.DepthMinPct, SubharmLimits.DepthMaxPct,
            set: v => _vm.SubharmSelectDepthPct = v, live: v => _vm.SendSubharmLive(SubharmField.Depth, v))
        {
            ScrollStep = 5,
            MaxDecimals = 0,
            Help = "How far the material this mode does not favour is gated down. At 0% the selectivity is inaudible whatever the mode is set to; 100% is full gating.",
        };
        _hold = new ParameterRow("Hold", "ms", SubharmLimits.HoldMinMs, SubharmLimits.HoldMaxMs,
            set: v => _vm.SubharmSelectHoldMs = v, live: v => _vm.SendSubharmLive(SubharmField.Hold, v))
        {
            ScrollStep = 10,
            MaxDecimals = 0,
        };
        _selectRows.Children.Add(_depth);
        _selectRows.Children.Add(_hold);
        section.Children.Add(_selectRows);
        return section;
    }

    private FrameworkElement BuildOutputs(bool extended)
    {
        var section = new StackPanel { Spacing = 10 };
        int all = _vm.NumOutputChannels >= 16 ? 0xFFFF : (1 << _vm.NumOutputChannels) - 1;
        var maskMenu = new MenuFlyout();
        maskMenu.Items.Add(MenuItem("Sub only (recommended)", () => _vm.SubharmOutputMask = 1 << _vm.PdmOutputIndex));
        maskMenu.Items.Add(MenuItem("All outputs", () => _vm.SubharmOutputMask = all));
        maskMenu.Items.Add(MenuItem("None", () => _vm.SubharmOutputMask = 0));
        _maskPresets = MenuButton("Presets", maskMenu);
        section.Children.Add(LabelRow("OUTPUTS", _maskPresets));

        var chips = new Grid { ColumnSpacing = 6 };
        ToolTipService.SetToolTip(chips, "Select the outputs that can actually play 24 to 80 Hz. Subharm runs before the crossover, so a satellite with a highpass loses the sub again: mask it off and save the CPU instead.");
        var outputs = _vm.ActiveOutputs;
        for (int o = 0; o < _vm.NumOutputChannels; o++)
        {
            chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int output = o;
            var chip = new ToggleButton
            {
                Content = (o + 1).ToString(),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 26,
                Padding = new Thickness(0),
            };
            ToolTipService.SetToolTip(chip, o < outputs.Count ? _vm.GetChannelName(outputs[o]) : $"Out {o + 1}");
            chip.Click += (_, _) => _vm.SetSubharmOutputChannel(output, chip.IsChecked == true);
            var track = new Grid { Height = 3, Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), Visibility = extended ? Visibility.Visible : Visibility.Collapsed };
            // The bar is the synthesized sub alone, not the output.
            var meter = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColor"]) };
            track.Children.Add(meter);
            var cell = new StackPanel { Spacing = 3 };
            cell.Children.Add(chip);
            cell.Children.Add(track);
            Grid.SetColumn(cell, o);
            chips.Children.Add(cell);
            _chips.Add((chip, meter, track));
        }
        section.Children.Add(chips);

        if (extended)
        {
            var titles = new StackPanel { Spacing = 2 };
            titles.Children.Add(new TextBlock { Text = "Link output pairs", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
            titles.Children.Add(new TextBlock { Text = "One sub per pair, from its mono sum.", FontSize = 9, Foreground = _secondary });
            _linkPairs = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            _linkPairs.Toggled += (_, _) => { if (!_updating) _vm.SubharmLinkPairs = _linkPairs.IsOn; };
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(titles);
            Grid.SetColumn(_linkPairs, 1);
            row.Children.Add(_linkPairs);
            ToolTipService.SetToolTip(row, "Synthesize one sub per output pair from its mono sum and feed it to both channels, as the dbx does. Bass is near-mono in most material, and two independent dividers can land on opposite polarities, which cancels a centred note's sub between the speakers.");
            section.Children.Add(row);
        }
        else _linkPairs = null;
        return section;
    }

    // ── Sync with the view model ──

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string? name = e.PropertyName;
        if (name == null || !(name.StartsWith("Subharm") || name == nameof(MainViewModel.IsDeviceConnected))) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (name is nameof(MainViewModel.SubharmSupported) or nameof(MainViewModel.SubharmExtendedSupported))
                BuildBody();
            else
                Refresh();
        });
    }

    private void Refresh()
    {
        bool connected = _vm.IsDeviceConnected, supported = _vm.SubharmSupported, extended = _vm.SubharmExtendedSupported;
        _updating = true;
        _enable.IsOn = _vm.SubharmEnabled;
        _enable.IsEnabled = connected && supported;
        _updating = false;

        _solo.Visibility = extended ? Visibility.Visible : Visibility.Collapsed;
        bool solo = _vm.SubharmSolo;
        _solo.IsEnabled = connected && _vm.SubharmEnabled;
        _solo.Opacity = _vm.SubharmEnabled ? 1 : 0.4;
        _solo.Background = new SolidColorBrush(solo ? Color.FromArgb(255, 255, 159, 10) : Color.FromArgb(31, 128, 128, 128));
        _solo.Foreground = solo ? new SolidColorBrush(Colors.White) : _secondary;
        ToolTipService.SetToolTip(_solo, solo
            ? "The selected outputs are carrying the synthesized sub only: the program signal is muted on them. Closing this window switches it off."
            : "Mute the program signal on the selected outputs so the synthesized sub can be heard or measured on its own. Never saved to a preset.");

        if (_graph == null) return;

        _graph.SetValues(_vm.SubharmLowDb, _vm.SubharmHighDb,
            extended ? _vm.SubharmTopDb : SubharmLimits.LevelMinDb, _vm.SubharmBoostDb,
            extended ? _vm.SubharmCeilingDb : SubharmLimits.CeilingMaxDb, _vm.SubharmEnabled);

        float headroom = _vm.SubharmHeadroomDb;
        if (_headroom != null)
        {
            _headroom.Text = headroom > 0 ? $"{headroom:+0.0;-0.0} dB" : "none";
            _headroom.Foreground = headroom > 0 ? new SolidColorBrush(Color.FromArgb(255, 255, 159, 10)) : _secondary;
            ToolTipService.SetToolTip(_headroomRow, headroom > 0
                ? $"This setting can add up to {headroom:0.0} dB. Lower the preamp on the inputs feeding the selected outputs by that much, or a loud passage will clip."
                : "This setting cannot push the signal past full scale.");
        }

        foreach (var row in new[] { _low, _high, _top, _depth, _hold, _ceiling, _boost })
            if (row != null) row.IsEnabled = connected;
        _low!.Value = _vm.SubharmLowDb;
        _high!.Value = _vm.SubharmHighDb;
        if (_top != null) _top.Value = _vm.SubharmTopDb;
        _boost!.Value = _vm.SubharmBoostDb;
        if (_ceiling != null) _ceiling.Value = _vm.SubharmCeilingDb;
        if (_presets != null) _presets.IsEnabled = connected;
        if (_maskPresets != null) _maskPresets.IsEnabled = connected;

        if (_select != null)
        {
            int mode = _vm.SubharmSelectMode;
            _select.Selected = mode;
            _select.IsEnabled = connected;
            _selectSummary!.Text = mode switch
            {
                SubharmSelectMode.Percussive => "A short sub burst after each attack: extends kicks, not the bass line.",
                SubharmSelectMode.Sustained => "The sub opens once a band has been ringing: extends bass notes, not kicks.",
                _ => "Every band signal is treated alike.",
            };
            ToolTipService.SetToolTip(_selectPicker, mode switch
            {
                SubharmSelectMode.Percussive => "A short sub burst after each attack, so a kick can be extended without extending the bass line under it. The most robust of the three: a held note gets nothing between kicks.",
                SubharmSelectMode.Sustained => "The sub opens only after a band has been ringing, so bass notes are extended and kicks are not. A kick landing in the same band ducks the held note's sub, which pumps on a four-on-the-floor line: shorten the hold or lower the depth to soften it.",
                _ => "Every band signal is treated alike. Choose percussive or sustained to weight the synthesized sub toward one kind of bass material; the gate decides per band from time behaviour, so it cannot separate two sources sounding at once in the same band.",
            });
            _selectRows!.Visibility = mode != SubharmSelectMode.All ? Visibility.Visible : Visibility.Collapsed;
            _depth!.Value = _vm.SubharmSelectDepthPct;
            _hold!.Value = _vm.SubharmSelectHoldMs;
            _hold.Help = mode == SubharmSelectMode.Percussive
                ? "The length of the sub burst after each attack."
                : "How long a band must ring before its sub opens. Every note's first hold period has no sub, so staccato bass lines get little.";
        }

        int mask = _vm.SubharmOutputMask;
        for (int o = 0; o < _chips.Count; o++)
        {
            bool on = (mask & (1 << o)) != 0;
            _chips[o].Chip.IsChecked = on;
            _chips[o].Chip.IsEnabled = connected;
            _chips[o].Track.Opacity = on ? 1 : 0.25;
        }

        if (_linkPairs != null)
        {
            _updating = true;
            _linkPairs.IsOn = _vm.SubharmLinkPairs;
            _linkPairs.IsEnabled = connected;
            _updating = false;
        }
    }

    // ── Meter ──

    private void PollMeter()
    {
        if (_meterBusy || _chips.Count == 0) return;
        if (!_vm.SubharmExtendedSupported || !_vm.IsDeviceConnected || !_vm.SubharmEnabled)
        {
            foreach (var c in _chips) c.Meter.Width = 0;
            return;
        }
        _meterBusy = true;
        Task.Run(() =>
        {
            var levels = _vm.ReadSubharmMeter();
            DispatcherQueue.TryEnqueue(() =>
            {
                _meterBusy = false;
                if (levels == null) return;
                for (int o = 0; o < _chips.Count; o++)
                {
                    double level = o < levels.Length ? Math.Clamp(levels[o], 0, 1) : 0;
                    _chips[o].Meter.Width = _chips[o].Track.ActualWidth * level;
                }
            });
        });
    }
}
