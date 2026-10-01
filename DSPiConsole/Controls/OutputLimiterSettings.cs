using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// One output's limiter settings, shown in the flyout its icon opens on a
/// right-click: the switch, threshold, release, link group, and the actions
/// that reach across outputs. After the macOS Console's OutputLimiterSettings.
/// The settings stay present but greyed while the limiter is off, so an output
/// without one never looks as if a ceiling were in force, and the flyout never
/// changes size under the pointer.
/// </summary>
public sealed class OutputLimiterSettings : UserControl
{
    private readonly MainViewModel _vm;
    private readonly int _output;
    private readonly ToggleSwitch _switch = new() { OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly ParameterRow _threshold;
    private readonly ParameterRow _release;
    private readonly Button[] _segments = new Button[LimiterLimits.LinkGroupMax + 1];
    private readonly TextBlock _linkSummary = new() { FontSize = 9, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _settings = new() { Spacing = 14 };
    private readonly StackPanel _actions;
    private bool _updating;
    private bool _attached;

    public OutputLimiterSettings(MainViewModel vm, int output)
    {
        _vm = vm;
        _output = output;
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _linkSummary.Foreground = secondary;

        // ── Header: title, output name, switch ──
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Output Limiter", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = OutputName(output), FontSize = 10, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        _switch.Toggled += (_, _) => { if (!_updating) _vm.SetLimiterEnabled(_output, _switch.IsOn); };
        ToolTipService.SetToolTip(_switch,
            "A test signal from the Signal Generator is limited like any other signal. Switch this output's limiter off for an unaltered full-scale measurement.");
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(titles);
        Grid.SetColumn(_switch, 1);
        header.Children.Add(_switch);

        // ── Threshold and release ──
        _threshold = new ParameterRow("Threshold", "dB", LimiterLimits.ThresholdMinDb, LimiterLimits.ThresholdMaxDb,
            set: v => _vm.SetLimiterThreshold(_output, v),
            live: v => _vm.SendLimiterParamLive(_output, LimiterParam.ThresholdDb, v))
        {
            Subtitle = "The ceiling, in dBFS. No sample leaves this output above it.",
            ScrollStep = 0.5f,
            MaxDecimals = 1,
            Ends = ("-30 dBFS", "0 dBFS"),
            Help = "The limiter runs after every gain stage, so this is the absolute level leaving the device. "
                 + "The -1 dBFS default leaves room for the small overshoot a DAC can produce between samples.",
        };
        _release = new ParameterRow("Release", "ms", LimiterLimits.ReleaseMinMs, LimiterLimits.ReleaseMaxMs,
            set: v => _vm.SetLimiterRelease(_output, v),
            live: v => _vm.SendLimiterParamLive(_output, LimiterParam.ReleaseMs, v))
        {
            Subtitle = "How fast the gain recovers after a peak",
            ScrollStep = 10,
            MaxDecimals = 0,
            Ends = ("10 ms", "1000 ms"),
            Help = "The gain recovers 8.7 dB per release time. Short releases keep the level up but can be heard "
                 + "pumping on dense material; long ones are smoother but hold the level down for longer after a "
                 + "peak. Attack is fixed at 16 samples and always completes before the peak arrives.",
        };

        // ── Link group ──
        var link = new StackPanel { Spacing = 6 };
        link.Children.Add(new TextBlock { Text = "Link group", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        link.Children.Add(BuildSegments());
        link.Children.Add(_linkSummary);
        ToolTipService.SetToolTip(link,
            "Outputs in the same group act as one limiter: they share on/off, threshold and release, so changing one "
            + "changes them all, and each applies the deepest gain reduction any of them needs. A stereo image cannot "
            + "shift and a pair of woofers stays matched. An output joining a group takes on the group's settings; "
            + "leaving keeps them.");

        // ── Actions across outputs ──
        var copy = new Button { Content = "Copy to all outputs", FontSize = 12, Padding = new Thickness(10, 3, 10, 4) };
        copy.Click += (_, _) => _vm.CopyLimiterToAllOutputs(_output);
        ToolTipService.SetToolTip(copy, "Give every output this output's threshold, release and on/off state. Link groups are left as they are.");
        var allMenu = new MenuFlyout();
        void Add(string text, Action act)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => act();
            allMenu.Items.Add(item);
        }
        Add("Link all stereo pairs", () => _vm.SetLimiterLinkGroups(StereoPairGroups()));
        Add("Unlink all outputs", () => _vm.SetLimiterLinkGroups(Array.Empty<int>()));
        allMenu.Items.Add(new MenuFlyoutSeparator());
        Add("Switch every limiter off", () => _vm.SetLimiterEnabledOnAll(false));
        var all = new DropDownButton { Content = "All outputs", FontSize = 11, Padding = new Thickness(8, 2, 8, 3), Flyout = allMenu };
        var actionsGrid = new Grid();
        actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionsGrid.Children.Add(copy);
        Grid.SetColumn(all, 1);
        actionsGrid.Children.Add(all);
        _actions = new StackPanel { Spacing = 14 };
        _actions.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });
        _actions.Children.Add(actionsGrid);

        _settings.Children.Add(_threshold);
        _settings.Children.Add(_release);
        _settings.Children.Add(link);
        _settings.Children.Add(_actions);

        var root = new StackPanel { Spacing = 14, Padding = new Thickness(16), Width = 320 };
        root.Children.Add(header);
        root.Children.Add(_settings);
        Content = root;

        // Loaded can repeat without an Unloaded between, so hook once per state change.
        Loaded += (_, _) =>
        {
            if (!_attached)
            {
                _attached = true;
                _vm.LimiterChanged += OnLimiterChanged;
                _vm.PropertyChanged += OnVmPropertyChanged;
                _vm.WatchLimiterMeter(true);
            }
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (!_attached) return;
            _attached = false;
            _vm.LimiterChanged -= OnLimiterChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.WatchLimiterMeter(false);
        };
    }

    private void OnLimiterChanged(object? sender, EventArgs e) => Refresh();

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDeviceConnected)) DispatcherQueue.TryEnqueue(Refresh);
    }

    private string OutputName(int output)
    {
        var outputs = _vm.ActiveOutputs;
        return output < outputs.Count ? _vm.GetChannelName(outputs[output]) : $"Out {output + 1}";
    }

    private int OutputCount => Math.Min(_vm.ActiveOutputs.Count, _vm.LimiterOutputs.Count);

    private void Refresh()
    {
        if (_output >= _vm.LimiterOutputs.Count) return;
        var s = _vm.LimiterOutputs[_output];
        bool connected = _vm.IsDeviceConnected;
        _updating = true;
        _switch.IsOn = s.Enabled;
        _switch.IsEnabled = connected;
        _updating = false;
        _threshold.Value = s.ThresholdDb;
        _release.Value = s.ReleaseMs;
        for (int g = 0; g < _segments.Length; g++)
            _segments[g].Background = new SolidColorBrush(g == s.LinkGroup ? Color.FromArgb(74, 255, 255, 255) : Colors.Transparent);
        _linkSummary.Text = LinkSummary(s.LinkGroup);
        _settings.IsHitTestVisible = s.Enabled && connected;
        _settings.Opacity = s.Enabled ? 1 : 0.4;
        _threshold.IsEnabled = _release.IsEnabled = s.Enabled && connected;
        foreach (var b in _segments) b.IsEnabled = s.Enabled && connected;
        foreach (var c in ((Grid)_actions.Children[1]).Children) ((Control)c).IsEnabled = connected;
    }

    private FrameworkElement BuildSegments()
    {
        var grid = new Grid { Height = 24 };
        for (int g = 0; g < _segments.Length; g++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int group = g;
            var b = new Button
            {
                Content = g == 0 ? "Off" : g.ToString(),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(0),
                Margin = new Thickness(1),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Colors.Transparent),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, g == 0 ? "Not linked" : $"Link group {g}");
            b.Click += (_, _) => _vm.SetLimiterLinkGroup(_output, group);
            Grid.SetColumn(b, g);
            grid.Children.Add(b);
            _segments[g] = b;
        }
        return new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(33, 255, 255, 255)),
            BorderThickness = new Thickness(1),
        };
    }

    private string LinkSummary(int group)
    {
        if (group == 0) return "Not linked.";
        var others = Enumerable.Range(0, OutputCount)
            .Where(k => k != _output && _vm.LimiterOutputs[k].LinkGroup == group)
            .Select(OutputName).ToList();
        if (others.Count == 0) return $"No other outputs in group {group}.";
        string list = others.Count == 1 ? others[0] : string.Join(", ", others.Take(others.Count - 1)) + " and " + others[^1];
        return $"Linked with {list}.";
    }

    /// <summary>Outputs 1+2 in group 1, 3+4 in group 2 and so on, as far as the
    /// four groups go. The PDM sub, the odd output out on both platforms, stays
    /// unlinked.</summary>
    private int[] StereoPairGroups() =>
        Enumerable.Range(0, OutputCount)
            .Select(o => o < _vm.PdmOutputIndex && o / 2 + 1 <= LimiterLimits.LinkGroupMax ? o / 2 + 1 : 0)
            .ToArray();
}
