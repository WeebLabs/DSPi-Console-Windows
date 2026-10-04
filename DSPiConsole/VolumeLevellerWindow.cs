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
/// The volume leveller: upward dynamic range compression. One column, as on
/// the macOS Console: the channel masks (multichannel inputs only), then the
/// parameters. After the macOS Console's VolumeLevellerView.
/// </summary>
public sealed class VolumeLevellerWindow : Window
{
    private static readonly string[] SpeedDescriptions =
    {
        "Slow — Gentle response for music and wide dynamic range content.",
        "Medium — Balanced response for general purpose use.",
        "Fast — Tight response for speech, dialogue, and podcasts.",
    };

    private readonly MainViewModel _vm;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private readonly ToggleSwitch _enable = new() { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _lookahead = new() { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly ParameterRow _amount, _maxGain, _gate;
    private readonly SegmentedPicker _speed = new(new[] { "Slow", "Medium", "Fast" }, height: 24, fontSize: 12);
    private readonly TextBlock _speedDescription = new() { FontSize = 9, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _channels = new() { Spacing = 10 };
    private readonly Grid _detectorChips = new() { ColumnSpacing = 6 }, _applyChips = new() { ColumnSpacing = 6 };
    private readonly List<ToggleButton> _detector = new(), _apply = new();
    private readonly DropDownButton _maskPresets;
    private bool _updating;

    public VolumeLevellerWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        ToolWindowChrome.Apply(this, "Volume Leveller", 380, 560, fitContent: true);
        _speedDescription.Foreground = _secondary;

        _amount = new ParameterRow("Amount", "%", 0, 100, set: v => _vm.LevellerAmount = Math.Clamp(v, 0, 100),
            live: v => _vm.SendLevellerLive(MainViewModel.LevellerField.Amount, v))
        {
            ScrollStep = 0.1f, MaxDecimals = 1,
            Caption = "Compression strength. Higher values reduce dynamic range more aggressively.",
        };
        _maxGain = new ParameterRow("Max Gain", "dB", 0, 35, set: v => _vm.LevellerMaxGainDb = Math.Clamp(v, 0, 35),
            live: v => _vm.SendLevellerLive(MainViewModel.LevellerField.MaxGain, v))
        {
            ScrollStep = 0.1f, MaxDecimals = 1,
            Caption = "Maximum boost for quiet passages. Higher values risk amplifying noise.",
        };
        _gate = new ParameterRow("Gate Threshold", "dB", -96, 0, set: v => _vm.LevellerGateDb = Math.Clamp(v, -96, 0),
            live: v => _vm.SendLevellerLive(MainViewModel.LevellerField.Gate, v))
        {
            ScrollStep = 0.1f, MaxDecimals = 1,
            Caption = "Silence gate. Signals below this level are not boosted, preventing noise amplification.",
        };
        _speed.Picked += i => _vm.LevellerSpeed = i;

        var menu = new MenuFlyout();
        void Preset(string text, int detector, int apply)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) =>
            {
                _vm.LevellerDetectorMask = detector;
                _vm.LevellerApplyMask = apply;
            };
            menu.Items.Add(item);
        }
        Preset("All channels (Night mode)", 0xFF, 0xFF);
        Preset("Center only (Dialog boost)", 0x04, 0x04);
        Preset("Front L / R only", 0x03, 0x03);
        _maskPresets = new DropDownButton
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

        _enable.Toggled += (_, _) => { if (!_updating) _vm.LevellerEnabled = _enable.IsOn; };
        _lookahead.Toggled += (_, _) => { if (!_updating) _vm.LevellerLookahead = _lookahead.IsOn; };
        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) => _vm.PropertyChanged -= OnVmPropertyChanged;
        BuildChips();
        Refresh();
    }

    private static Border Rule() => new() { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center,
    };

    private FrameworkElement BuildHeader()
    {
        var header = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new FontIcon
        {
            Glyph = "", FontSize = 22, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColor"]),
        });
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Volume Leveller", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = "Upward Dynamic Range Compression", FontSize = 10, Foreground = _secondary });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Grid.SetColumn(_enable, 2);
        header.Children.Add(_enable);
        return header;
    }

    private FrameworkElement BuildBody()
    {
        var body = new StackPanel { Spacing = 14, Padding = new Thickness(16) };

        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(SectionLabel("CHANNELS"));
        Grid.SetColumn(_maskPresets, 1);
        title.Children.Add(_maskPresets);
        _channels.Children.Add(title);
        _channels.Children.Add(MaskRow("Detector", "sets the shared gain", _detectorChips));
        _channels.Children.Add(MaskRow("Apply", "receives the gain", _applyChips));
        _channels.Children.Add(Rule());
        body.Children.Add(_channels);

        body.Children.Add(SectionLabel("PARAMETERS"));
        body.Children.Add(_amount);
        body.Children.Add(Rule());
        var speed = new StackPanel { Spacing = 6 };
        speed.Children.Add(new TextBlock { Text = "Speed", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        speed.Children.Add(_speed);
        speed.Children.Add(_speedDescription);
        body.Children.Add(speed);
        body.Children.Add(Rule());
        body.Children.Add(_maxGain);
        body.Children.Add(Rule());
        body.Children.Add(_gate);
        body.Children.Add(Rule());

        var lookahead = new Grid();
        lookahead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lookahead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = "Lookahead", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        titles.Children.Add(new TextBlock { Text = "Adds 5ms latency. Improves transient handling.", FontSize = 9, Foreground = _secondary });
        lookahead.Children.Add(titles);
        Grid.SetColumn(_lookahead, 1);
        lookahead.Children.Add(_lookahead);
        body.Children.Add(lookahead);
        return body;
    }

    private FrameworkElement MaskRow(string label, string hint, Grid chips)
    {
        var row = new StackPanel { Spacing = 4 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        title.Children.Add(new TextBlock { Text = hint, FontSize = 9, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(title);
        row.Children.Add(chips);
        return row;
    }

    /// <summary>The live input count: the masks act on input rows, and a row
    /// the active source does not carry has nothing to level.</summary>
    private int ChannelCount => Math.Clamp(_vm.ActiveInputChannelCount, 2, 8);

    /// <summary>Masks only matter with more than two live inputs on a
    /// multichannel device whose firmware has them (wire V18).</summary>
    private bool ShowMasks => _vm.LevellerMasksSupported && _vm.NumInputChannels > 2 && ChannelCount > 2;

    private void BuildChips()
    {
        Fill(_detectorChips, _detector, detector: true);
        Fill(_applyChips, _apply, detector: false);
    }

    private void Fill(Grid grid, List<ToggleButton> list, bool detector)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        list.Clear();
        for (int ch = 0; ch < ChannelCount; ch++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int channel = ch;
            var chip = new ToggleButton
            {
                Content = (ch + 1).ToString(),
                FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 26, Padding = new Thickness(0),
            };
            ToolTipService.SetToolTip(chip, ch < Channel.AllInputs.Count ? _vm.GetChannelName(Channel.AllInputs[ch]) : $"Ch {ch + 1}");
            chip.Click += (_, _) =>
            {
                bool on = chip.IsChecked == true;
                if (detector)
                    _vm.LevellerDetectorMask = on ? _vm.LevellerDetectorMask | (1 << channel) : _vm.LevellerDetectorMask & ~(1 << channel);
                else
                    _vm.LevellerApplyMask = on ? _vm.LevellerApplyMask | (1 << channel) : _vm.LevellerApplyMask & ~(1 << channel);
            };
            Grid.SetColumn(chip, ch);
            grid.Children.Add(chip);
            list.Add(chip);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string? name = e.PropertyName;
        if (name == null) return;
        if (name is nameof(MainViewModel.ActiveInputChannelCount) or nameof(MainViewModel.Platform))
            DispatcherQueue.TryEnqueue(() => { BuildChips(); Refresh(); });
        else if (name.StartsWith("Leveller") || name == nameof(MainViewModel.IsDeviceConnected))
            DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        bool connected = _vm.IsDeviceConnected;
        _updating = true;
        _enable.IsOn = _vm.LevellerEnabled;
        _enable.IsEnabled = connected;
        _lookahead.IsOn = _vm.LevellerLookahead;
        _lookahead.IsEnabled = connected;
        _updating = false;

        _amount.Value = _vm.LevellerAmount;
        _maxGain.Value = _vm.LevellerMaxGainDb;
        _gate.Value = _vm.LevellerGateDb;
        _amount.IsEnabled = _maxGain.IsEnabled = _gate.IsEnabled = connected;
        int speed = Math.Clamp(_vm.LevellerSpeed, 0, 2);
        _speed.Selected = speed;
        _speed.IsEnabled = connected;
        _speedDescription.Text = SpeedDescriptions[speed];

        _channels.Visibility = ShowMasks ? Visibility.Visible : Visibility.Collapsed;
        _maskPresets.IsEnabled = connected;
        for (int ch = 0; ch < _detector.Count; ch++)
        {
            _detector[ch].IsChecked = (_vm.LevellerDetectorMask & (1 << ch)) != 0;
            _apply[ch].IsChecked = (_vm.LevellerApplyMask & (1 << ch)) != 0;
            _detector[ch].IsEnabled = _apply[ch].IsEnabled = connected;
        }
    }
}
