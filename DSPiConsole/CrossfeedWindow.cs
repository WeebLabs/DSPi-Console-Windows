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
/// Headphone crossfeed (BS2B). Two columns: the response graph over the presets
/// that set it, and the custom parameters, the time delay and the output pairs.
/// After the macOS Console's CrossfeedView.
/// <para>
/// The firmware keeps the cutoff and feed as the Custom preset's values whatever
/// preset runs, so choosing a built-in preset sends the preset alone and the
/// window shows that preset's values. Editing either value while a built-in
/// preset runs switches to Custom, starting from the values on screen.
/// </para>
/// </summary>
public sealed class CrossfeedWindow : Window
{
    private static readonly Color Orange = Color.FromArgb(255, 255, 159, 10);

    private readonly MainViewModel _vm;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private readonly Color _accent = (Color)Application.Current.Resources["SystemAccentColor"];
    private readonly ToggleSwitch _enable = new() { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _itd = new() { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToolCurveGraph _graph = new();
    private readonly ParameterRow _freq, _feed;
    private readonly StackPanel _parameters = new() { Spacing = 14 };
    private readonly StackPanel _presetRows = new() { Spacing = 10 };
    private readonly List<(RadioButton Radio, int Index)> _radios = new();
    private readonly StackPanel _pairs = new() { Spacing = 10 };
    private readonly Grid _chips = new() { ColumnSpacing = 6 };
    private readonly List<ToggleButton> _chipButtons = new();
    private readonly DropDownButton _pairPresets;
    private float? _liveFreq, _liveFeed;
    /// <summary>The values in effect the live ones override; they are dropped
    /// only when these change. Switching to Custom from a preset keeps them,
    /// so the first move of a drag does not snap the curve back.</summary>
    private (float, float)? _base;
    private bool _updating;

    public CrossfeedWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        ToolWindowChrome.Apply(this, "Crossfeed", 780, 500, fitContent: true);

        _freq = new ParameterRow("Cutoff Frequency", "Hz", CrossfeedData.FreqMin, CrossfeedData.FreqMax,
            set: v => Commit(freq: v), live: v => Live(freq: true, v))
        {
            ScrollStep = 0.1f,
            MaxDecimals = 1,
            Caption = "Simulates head shadow lowpass cutoff. Lower = more bass crossfeed. Typical: 650-700 Hz.",
        };
        _feed = new ParameterRow("Feed Level", "dB", CrossfeedData.FeedMin, CrossfeedData.FeedMax,
            set: v => Commit(feed: v), live: v => Live(freq: false, v))
        {
            ScrollStep = 0.1f,
            MaxDecimals = 1,
            Caption = "Crossfeed attenuation below direct signal. Higher = more crossfeed. Typical: 4.5-9.5 dB.",
        };

        var menu = new MenuFlyout();
        void PairPreset(string text, Func<int> mask)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => _vm.CrossfeedOutputPairMask = mask();
            menu.Items.Add(item);
        }
        PairPreset("All pairs", () => _vm.NumOutputSlots >= 8 ? 0xFF : (1 << _vm.NumOutputSlots) - 1);
        PairPreset("Pair 1 only (Headphones)", () => 0x01);
        PairPreset("None", () => 0x00);
        _pairPresets = new DropDownButton
        {
            Content = "Presets", FontSize = 11, Padding = new Thickness(8, 2, 8, 3), Flyout = menu,
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
        };

        var root = new Grid { Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(BuildHeader());
        var rule = Rule();
        Grid.SetRow(rule, 1);
        root.Children.Add(rule);
        var body = new ScrollViewer { Content = BuildBody(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(body, 2);
        root.Children.Add(body);
        Content = root;

        _enable.Toggled += (_, _) => { if (!_updating) _vm.CrossfeedEnabled = _enable.IsOn; };
        _itd.Toggled += (_, _) => { if (!_updating) _vm.CrossfeedItd = _itd.IsOn; };
        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _graph.Dispose();
        };
        BuildChips();
        Refresh();
    }

    private static Border Rule() => new() { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center,
    };

    private bool IsCustom => _vm.CrossfeedPreset == CrossfeedData.CustomPreset;

    /// <summary>The cutoff and feed in effect: a built-in preset's own, or the
    /// Custom values the device stores.</summary>
    private (float Freq, float Feed) Effective()
    {
        int p = _vm.CrossfeedPreset;
        return p >= 0 && p < CrossfeedData.CustomPreset
            ? (CrossfeedData.Presets[p].Freq, CrossfeedData.Presets[p].Feed)
            : (_vm.CrossfeedFreq, _vm.CrossfeedFeed);
    }

    /// <summary>Editing while a built-in preset runs switches to Custom from
    /// the values on screen, so the other value does not jump.</summary>
    private void EnsureCustom()
    {
        if (IsCustom) return;
        var (freq, feed) = Effective();
        _vm.CrossfeedFreq = freq;
        _vm.CrossfeedFeed = feed;
        _vm.CrossfeedPreset = CrossfeedData.CustomPreset;
    }

    private void Live(bool freq, float value)
    {
        EnsureCustom();
        _vm.SendCrossfeedLive(freq, value);
        if (freq) _liveFreq = value; else _liveFeed = value;
        DrawCurves();
    }

    private void Commit(float? freq = null, float? feed = null)
    {
        EnsureCustom();
        if (freq is { } f) _vm.CrossfeedFreq = Math.Clamp(f, CrossfeedData.FreqMin, CrossfeedData.FreqMax);
        if (feed is { } d) _vm.CrossfeedFeed = Math.Clamp(d, CrossfeedData.FeedMin, CrossfeedData.FeedMax);
    }

    // ── Layout ──

    private FrameworkElement BuildHeader()
    {
        var header = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new FontIcon { Glyph = "", FontSize = 22, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(_accent) });
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Crossfeed", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = "BS2B Bauer Stereophonic-to-Binaural", FontSize = 10, Foreground = _secondary });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Grid.SetColumn(_enable, 2);
        header.Children.Add(_enable);
        return header;
    }

    private FrameworkElement BuildBody()
    {
        var left = new StackPanel { Spacing = 14, Padding = new Thickness(16) };
        var graph = new StackPanel { Spacing = 6 };
        graph.Children.Add(SectionLabel("FREQUENCY RESPONSE"));
        graph.Children.Add(new Border
        {
            Height = 160,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Background = new SolidColorBrush(ToolWindowChrome.PanelColor),
            BorderBrush = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = _graph,
        });
        left.Children.Add(graph);
        left.Children.Add(Rule());
        _presetRows.Children.Add(SectionLabel("PRESET"));
        for (int i = 0; i < CrossfeedData.Presets.Length; i++) _presetRows.Children.Add(PresetRow(i));
        left.Children.Add(_presetRows);

        var right = new StackPanel { Spacing = 14, Padding = new Thickness(16) };
        _parameters.Children.Add(SectionLabel("PARAMETERS"));
        _parameters.Children.Add(_freq);
        _parameters.Children.Add(Rule());
        _parameters.Children.Add(_feed);
        right.Children.Add(_parameters);
        right.Children.Add(Rule());

        var itd = new Grid();
        itd.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        itd.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var itdTitles = new StackPanel { Spacing = 2 };
        itdTitles.Children.Add(new TextBlock { Text = "Interaural Time Delay", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        itdTitles.Children.Add(new TextBlock { Text = "Simulates ~220 µs path difference via all-pass filter", FontSize = 9, Foreground = _secondary });
        itd.Children.Add(itdTitles);
        Grid.SetColumn(_itd, 1);
        itd.Children.Add(_itd);
        right.Children.Add(itd);

        // The per-pair mask arrived in wire format V20; older firmware
        // crossfeeds a fixed set of outputs.
        _pairs.Children.Add(Rule());
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(SectionLabel("OUTPUT PAIRS"));
        Grid.SetColumn(_pairPresets, 1);
        title.Children.Add(_pairPresets);
        _pairs.Children.Add(title);
        _pairs.Children.Add(new TextBlock
        {
            Text = "Crossfeed only the stereo output pairs feeding headphones. Speaker pairs stay bit-accurate. The mono sub is never crossfed.",
            FontSize = 9, Foreground = _secondary, TextWrapping = TextWrapping.Wrap,
        });
        _pairs.Children.Add(_chips);
        right.Children.Add(_pairs);

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
        return columns;
    }

    private FrameworkElement PresetRow(int index)
    {
        var p = CrossfeedData.Presets[index];
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = p.Name, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        text.Children.Add(new TextBlock { Text = p.Description, FontSize = 9, Foreground = _secondary });
        var radio = new RadioButton { Content = text, GroupName = "crossfeed-preset", MinWidth = 0, Padding = new Thickness(6, 0, 0, 0) };
        radio.Checked += (_, _) =>
        {
            if (_updating) return;
            // A built-in preset alone: its values are the firmware's own, and
            // the Custom values stay as they were.
            _vm.CrossfeedPreset = index;
        };
        _radios.Add((radio, index));
        return radio;
    }

    private void BuildChips()
    {
        _chips.Children.Clear();
        _chips.ColumnDefinitions.Clear();
        _chipButtons.Clear();
        var outputs = _vm.ActiveOutputs;
        string Name(int o) => o < outputs.Count ? _vm.GetChannelName(outputs[o]) : $"Out {o + 1}";
        for (int p = 0; p < _vm.NumOutputSlots; p++)
        {
            _chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int pair = p;
            var chip = new ToggleButton
            {
                Content = (p + 1).ToString(),
                FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 26, Padding = new Thickness(0),
            };
            ToolTipService.SetToolTip(chip, $"{Name(2 * p)} / {Name(2 * p + 1)}");
            chip.Click += (_, _) =>
            {
                int mask = _vm.CrossfeedOutputPairMask;
                _vm.CrossfeedOutputPairMask = chip.IsChecked == true ? mask | (1 << pair) : mask & ~(1 << pair);
            };
            Grid.SetColumn(chip, p);
            _chips.Children.Add(chip);
            _chipButtons.Add(chip);
        }
    }

    // ── Sync ──

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string? name = e.PropertyName;
        if (name == null) return;
        if (name == nameof(MainViewModel.Platform))
            DispatcherQueue.TryEnqueue(() => { BuildChips(); Refresh(); });
        else if (name.StartsWith("Crossfeed") || name == nameof(MainViewModel.IsDeviceConnected))
            DispatcherQueue.TryEnqueue(Refresh);
    }

    private void DrawCurves()
    {
        var (freq, feed) = Effective();
        var (freqs, direct, cross) = CrossfeedData.GetResponseCurves(_liveFreq ?? freq, _liveFeed ?? feed);
        _graph.SetCurves(new[]
        {
            new ToolCurveGraph.Series("Direct", _accent, freqs.Select((f, i) => (f, direct[i])).ToArray()),
            new ToolCurveGraph.Series("Crossfeed", Orange, freqs.Select((f, i) => (f, cross[i])).ToArray()),
        }, _vm.CrossfeedEnabled, legend: true);
    }

    private void Refresh()
    {
        bool connected = _vm.IsDeviceConnected;
        _updating = true;
        _enable.IsOn = _vm.CrossfeedEnabled;
        _enable.IsEnabled = connected;
        _itd.IsOn = _vm.CrossfeedItd;
        _itd.IsEnabled = connected;
        foreach (var (radio, index) in _radios)
        {
            radio.IsChecked = index == _vm.CrossfeedPreset;
            radio.IsEnabled = connected;
        }
        _updating = false;

        var (freq, feed) = Effective();
        _freq.Value = freq;
        _feed.Value = feed;
        _freq.IsEnabled = _feed.IsEnabled = connected;
        // Dimmed rather than disabled outside Custom: editing switches to it.
        _parameters.Opacity = IsCustom ? 1 : 0.5;
        if (_base != (freq, feed)) { _base = (freq, feed); _liveFreq = _liveFeed = null; }
        DrawCurves();

        _pairs.Visibility = _vm.CrossfeedMaskSupported ? Visibility.Visible : Visibility.Collapsed;
        _pairPresets.IsEnabled = connected;
        int mask = _vm.CrossfeedOutputPairMask;
        for (int p = 0; p < _chipButtons.Count; p++)
        {
            _chipButtons[p].IsChecked = (mask & (1 << p)) != 0;
            _chipButtons[p].IsEnabled = connected;
        }
    }
}
