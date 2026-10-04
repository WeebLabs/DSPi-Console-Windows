using System.ComponentModel;
using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// Psychoacoustic bass (firmware wire V23): phantom-fundamental bass for
/// speakers that cannot reproduce it. Two columns: the spectrum over the cutoff
/// and level that shape its harmonic band, and the outputs and the shaping.
/// After the macOS Console's PsychoacousticBassView.
/// </summary>
public sealed class PsychoacousticBassWindow : Window
{
    private sealed record StartingPoint(string Name, string Detail, float Cutoff, float Harmonics, float Drive, float Character, float Original);

    /// <summary>The spec's starting points (§6), as the macOS Console has them.</summary>
    private static readonly StartingPoint[] StartingPoints =
    {
        new("Bookshelf speakers", "Gentle low-end help", 60, 0, 6, 50, 0),
        new("Small Bluetooth", "Portable speaker", 100, 3, 9, 40, -12),
        new("Laptop / tablet", "Tiny drivers, protect them", 180, 6, 12, 50, -24),
        new("Headphone bass feel", "Extra sub sensation", 45, -3, 6, 30, 0),
    };

    private readonly MainViewModel _vm;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private readonly ContentControl _bodyHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly ToggleSwitch _enable = new() { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly PsybassSpectrumGraph _graph = new();
    private readonly ParameterRow _cutoff, _harmonics, _drive, _character, _original;
    private readonly Grid _chips = new() { ColumnSpacing = 6 };
    private readonly List<ToggleButton> _chipButtons = new();
    private readonly DropDownButton _presets, _maskPresets;
    private FrameworkElement? _body;
    private bool _updating;

    public PsychoacousticBassWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        ToolWindowChrome.Apply(this, "Psychoacoustic Bass", 780, 520, fitContent: true);

        _cutoff = Row("Cutoff Frequency", "Hz", PsybassLimits.CutoffMinHz, PsybassLimits.CutoffMaxHz, MainViewModel.PsybassField.Cutoff,
            v => _vm.PsybassCutoffHz = v, 1, 0,
            "The speaker's low-frequency limit. Content below this feeds the harmonic generator; generated harmonics span roughly this to 4x.");
        _harmonics = Row("Harmonics", "dB", PsybassLimits.HarmonicsMinDb, PsybassLimits.HarmonicsMaxDb, MainViewModel.PsybassField.Harmonics,
            v => _vm.PsybassHarmonicsDb = v, 0.5f, 1,
            "Level of the synthesized harmonics. The primary amount-of-effect control. Higher = more perceived bass.");
        _drive = Row("Drive", "dB", PsybassLimits.DriveMinDb, PsybassLimits.DriveMaxDb, MainViewModel.PsybassField.Drive,
            v => _vm.PsybassDriveDb = v, 0.5f, 1,
            "Pre-gain into the odd-harmonic soft clipper. Higher makes the effect audible on quieter passages. Mostly affects aggressive character.");
        _character = Row("Character", "%", PsybassLimits.CharacterMinPct, PsybassLimits.CharacterMaxPct, MainViewModel.PsybassField.Character,
            v => _vm.PsybassCharacterPct = v, 1, 0, null);
        _character.Ends = ("Warm", "Aggressive");
        _original = Row("Original Bass", "dB", PsybassLimits.OriginalMinDb, PsybassLimits.OriginalMaxDb, MainViewModel.PsybassField.Original,
            v => _vm.PsybassOriginalDb = v, 1, 1,
            "Level of the un-reproducible fundamental below the cutoff. Lower attenuates it, freeing driver excursion and headroom. -60 dB is full removal. Speaker protection.");

        var start = new MenuFlyout();
        foreach (var p in StartingPoints)
        {
            var item = new MenuFlyoutItem { Text = $"{p.Name} - {p.Detail}" };
            item.Click += (_, _) =>
            {
                _vm.PsybassCutoffHz = p.Cutoff;
                _vm.PsybassHarmonicsDb = p.Harmonics;
                _vm.PsybassDriveDb = p.Drive;
                _vm.PsybassCharacterPct = p.Character;
                _vm.PsybassOriginalDb = p.Original;
            };
            start.Items.Add(item);
        }
        _presets = MenuButton("Apply preset", start);

        var mask = new MenuFlyout();
        void MaskPreset(string text, Func<int> value)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => _vm.PsybassOutputMask = value();
            mask.Items.Add(item);
        }
        MaskPreset("All outputs", AllOutputs);
        // Harmonics on a channel that can play real bass are counterproductive.
        MaskPreset("Exclude sub (recommended)", () => AllOutputs() & ~(1 << _vm.PdmOutputIndex));
        MaskPreset("None", () => 0);
        _maskPresets = MenuButton("Presets", mask);

        var root = new Grid { Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(BuildHeader());
        var rule = Rule();
        Grid.SetRow(rule, 1);
        root.Children.Add(rule);
        Grid.SetRow(_bodyHost, 2);
        root.Children.Add(_bodyHost);
        Content = root;

        _enable.Toggled += (_, _) => { if (!_updating) _vm.PsybassEnabled = _enable.IsOn; };
        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _graph.Dispose();
        };
        BuildChips();
        Refresh();
    }

    private int AllOutputs() => _vm.NumOutputChannels >= 16 ? 0xFFFF : (1 << _vm.NumOutputChannels) - 1;

    private ParameterRow Row(string title, string unit, float min, float max, MainViewModel.PsybassField field, Action<float> set,
                             float step, int decimals, string? caption)
    {
        var row = new ParameterRow(title, unit, min, max, set: v => set(Math.Clamp(v, min, max)), live: v =>
        {
            float c = Math.Clamp(v, min, max);
            _vm.SendPsybassLive(field, c);
            switch (field)
            {
                case MainViewModel.PsybassField.Cutoff: _graph.SetLiveCutoff(c); break;
                case MainViewModel.PsybassField.Harmonics: _graph.SetLiveHarmonics(c); break;
                case MainViewModel.PsybassField.Original: _graph.SetLiveOriginal(c); break;
            }
        })
        {
            ScrollStep = step,
            MaxDecimals = decimals,
        };
        if (caption != null) row.Caption = caption;
        return row;
    }

    private static DropDownButton MenuButton(string text, MenuFlyout menu) => new()
    {
        Content = text, FontSize = 11, Padding = new Thickness(8, 2, 8, 3), Flyout = menu,
        Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
    };

    private static Border Rule() => new() { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center,
    };

    private static Grid LabelRow(FrameworkElement label, FrameworkElement right)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(label);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    private FrameworkElement BuildHeader()
    {
        var header = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new FontIcon
        {
            Glyph = "", FontSize = 22, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColor"]),
        });
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Psychoacoustic Bass", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = "Phantom fundamental bass enhancement", FontSize = 10, Foreground = _secondary });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Grid.SetColumn(_enable, 2);
        header.Children.Add(_enable);
        return header;
    }

    private FrameworkElement BuildBody()
    {
        var left = new StackPanel { Spacing = 14, Padding = new Thickness(16) };
        var spectrum = new StackPanel { Spacing = 6 };
        spectrum.Children.Add(LabelRow(SectionLabel("SPECTRUM"), _presets));
        spectrum.Children.Add(new Border
        {
            Height = 160,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Background = new SolidColorBrush(ToolWindowChrome.PanelColor),
            BorderBrush = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = _graph,
        });
        left.Children.Add(spectrum);
        left.Children.Add(Rule());
        left.Children.Add(SectionLabel("HARMONICS"));
        left.Children.Add(_cutoff);
        left.Children.Add(Rule());
        left.Children.Add(_harmonics);

        var right = new StackPanel { Spacing = 14, Padding = new Thickness(16) };
        var outputs = new StackPanel { Spacing = 10 };
        outputs.Children.Add(LabelRow(SectionLabel("OUTPUTS"), _maskPresets));
        outputs.Children.Add(new TextBlock
        {
            Text = "Enhance only the small-speaker outputs. Mask off the sub and any full-range outputs - synthesizing harmonics on a channel that can reproduce real bass is counterproductive.",
            FontSize = 9, Foreground = _secondary, TextWrapping = TextWrapping.Wrap,
        });
        outputs.Children.Add(_chips);
        right.Children.Add(outputs);
        right.Children.Add(Rule());
        right.Children.Add(SectionLabel("SHAPING"));
        right.Children.Add(_drive);
        right.Children.Add(Rule());
        right.Children.Add(_character);
        right.Children.Add(Rule());
        right.Children.Add(_original);

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
        return new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private FrameworkElement UnsupportedNote()
    {
        var note = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(24) };
        note.Children.Add(new FontIcon { Glyph = "", FontSize = 28, Foreground = _secondary });
        note.Children.Add(new TextBlock { Text = "Requires firmware with wire format V23 or newer.", FontSize = 12, Foreground = _secondary, HorizontalAlignment = HorizontalAlignment.Center });
        note.Children.Add(new TextBlock { Text = "Update the DSPi firmware to use Psychoacoustic Bass.", FontSize = 10, Foreground = _secondary, HorizontalAlignment = HorizontalAlignment.Center });
        return note;
    }

    private void BuildChips()
    {
        _chips.Children.Clear();
        _chips.ColumnDefinitions.Clear();
        _chipButtons.Clear();
        var outputs = _vm.ActiveOutputs;
        for (int o = 0; o < _vm.NumOutputChannels; o++)
        {
            _chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int output = o;
            var chip = new ToggleButton
            {
                Content = (o + 1).ToString(),
                FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 26, Padding = new Thickness(0),
            };
            ToolTipService.SetToolTip(chip, o < outputs.Count ? _vm.GetChannelName(outputs[o]) : $"Out {o + 1}");
            chip.Click += (_, _) => _vm.SetPsybassOutputChannel(output, chip.IsChecked == true);
            Grid.SetColumn(chip, o);
            _chips.Children.Add(chip);
            _chipButtons.Add(chip);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string? name = e.PropertyName;
        if (name == null) return;
        if (name == nameof(MainViewModel.Platform))
            DispatcherQueue.TryEnqueue(() => { BuildChips(); Refresh(); });
        else if (name.StartsWith("Psybass") || name == nameof(MainViewModel.IsDeviceConnected))
            DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        bool connected = _vm.IsDeviceConnected, supported = _vm.PsybassSupported;
        _updating = true;
        _enable.IsOn = _vm.PsybassEnabled;
        _enable.IsEnabled = connected && supported;
        _updating = false;

        // The whole body needs wire V23; older firmware gets a note instead.
        if (!supported)
        {
            if (_bodyHost.Content is not StackPanel) _bodyHost.Content = UnsupportedNote();
            return;
        }
        _body ??= BuildBody();
        if (_bodyHost.Content != _body) _bodyHost.Content = _body;

        _cutoff.Value = _vm.PsybassCutoffHz;
        _harmonics.Value = _vm.PsybassHarmonicsDb;
        _drive.Value = _vm.PsybassDriveDb;
        _character.Value = _vm.PsybassCharacterPct;
        _original.Value = _vm.PsybassOriginalDb;
        foreach (var r in new[] { _cutoff, _harmonics, _drive, _character, _original }) r.IsEnabled = connected;
        _presets.IsEnabled = _maskPresets.IsEnabled = connected;
        _graph.SetValues(_vm.PsybassCutoffHz, _vm.PsybassHarmonicsDb, _vm.PsybassOriginalDb, _vm.PsybassEnabled);

        int mask = _vm.PsybassOutputMask;
        for (int o = 0; o < _chipButtons.Count; o++)
        {
            _chipButtons[o].IsChecked = (mask & (1 << o)) != 0;
            _chipButtons[o].IsEnabled = connected;
        }
    }
}
