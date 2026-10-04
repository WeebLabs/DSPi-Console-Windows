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
/// Loudness compensation (ISO 226:2003). Two columns: the compensation curve,
/// growing to the height of the column beside it, and the parameters and
/// outputs. After the macOS Console's LoudnessView.
/// </summary>
public sealed class LoudnessWindow : Window
{
    /// <summary>The firmware's default: every output.</summary>
    private const int AllOutputs = 0xFFFF;
    /// <summary>The volume the curve is previewed at, as on the Mac.</summary>
    private const float PreviewVolumeDb = -40;

    private readonly MainViewModel _vm;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private readonly ToggleSwitch _enable = new() { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToolCurveGraph _graph = new();
    /// <summary>A drag's value, shown by the curve until the model commits.</summary>
    private float? _liveRef, _liveIntensity;
    /// <summary>The committed values the live ones override; they are dropped
    /// only when these change, not on every refresh.</summary>
    private (float, float)? _base;
    private readonly ParameterRow _ref, _intensity;
    private readonly StackPanel _outputs = new() { Spacing = 10 };
    private readonly Grid _chips = new() { ColumnSpacing = 6 };
    private readonly List<ToggleButton> _chipButtons = new();
    private readonly DropDownButton _presets;
    private bool _updating;

    public LoudnessWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        ToolWindowChrome.Apply(this, "Loudness Compensation", 780, 440, fitContent: true);

        _ref = new ParameterRow("Reference SPL", "dB", 40, 100, set: v => _vm.LoudnessRefSPL = v, live: v =>
        {
            _vm.SendLoudnessLive(refSpl: true, v);
            _liveRef = v;
            DrawCurve();
        })
        {
            ScrollStep = 0.1f,
            MaxDecimals = 1,
            Caption = "SPL at 1 kHz when USB volume is 0 dB. Lower = more compensation per dB of volume reduction.",
        };
        _intensity = new ParameterRow("Intensity", "%", 0, 200, set: v => _vm.LoudnessIntensity = v, live: v =>
        {
            _vm.SendLoudnessLive(refSpl: false, v);
            _liveIntensity = v;
            DrawCurve();
        })
        {
            ScrollStep = 0.1f,
            MaxDecimals = 1,
            Caption = "Scales the ISO 226 compensation. 100% = standard curve. 0% = bypassed. >100% = exaggerated.",
        };

        var menu = new MenuFlyout();
        void Preset(string text, int mask)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => _vm.LoudnessOutputMask = mask;
            menu.Items.Add(item);
        }
        Preset("All outputs", AllOutputs);
        Preset("Slot 1 only (Headphones)", 0x0003);
        Preset("None", 0x0000);
        _presets = new DropDownButton
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

        _enable.Toggled += (_, _) => { if (!_updating) _vm.LoudnessEnabled = _enable.IsOn; };
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

    private FrameworkElement BuildHeader()
    {
        var header = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new FontIcon
        {
            Glyph = "", FontSize = 22, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColor"]),
        });
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Loudness Compensation", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = "ISO 226:2003 Fletcher-Munson", FontSize = 10, Foreground = _secondary });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Grid.SetColumn(_enable, 2);
        header.Children.Add(_enable);
        return header;
    }

    /// <summary>The curve has the left column to itself and stretches to the
    /// height of the parameters and outputs, so the columns end together.</summary>
    private FrameworkElement BuildBody()
    {
        var left = new Grid { Padding = new Thickness(16), RowSpacing = 6 };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.Children.Add(SectionLabel("COMPENSATION CURVE"));
        var panel = new Border
        {
            MinHeight = 160,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Background = new SolidColorBrush(ToolWindowChrome.PanelColor),
            BorderBrush = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = _graph,
        };
        Grid.SetRow(panel, 1);
        left.Children.Add(panel);

        var right = new StackPanel { Spacing = 14, Padding = new Thickness(16) };
        right.Children.Add(SectionLabel("PARAMETERS"));
        right.Children.Add(_ref);
        right.Children.Add(Rule());
        right.Children.Add(_intensity);

        // The per-output mask arrived in wire format V19; older firmware
        // compensates every output.
        _outputs.Children.Add(Rule());
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(SectionLabel("OUTPUTS"));
        Grid.SetColumn(_presets, 1);
        title.Children.Add(_presets);
        _outputs.Children.Add(title);
        _outputs.Children.Add(new TextBlock
        {
            Text = "Compensate only the outputs feeding your low-level listening chain. Keep bass-managed pairs (mains + sub) together so the crossover stays coherent.",
            FontSize = 9, Foreground = _secondary, TextWrapping = TextWrapping.Wrap,
        });
        _outputs.Children.Add(_chips);
        right.Children.Add(_outputs);

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
            chip.Click += (_, _) =>
            {
                int mask = _vm.LoudnessOutputMask;
                _vm.LoudnessOutputMask = chip.IsChecked == true ? mask | (1 << output) : mask & ~(1 << output);
            };
            Grid.SetColumn(chip, o);
            _chips.Children.Add(chip);
            _chipButtons.Add(chip);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string? name = e.PropertyName;
        if (name == null) return;
        if (name is nameof(MainViewModel.Platform) or nameof(MainViewModel.NumOutputChannels))
            DispatcherQueue.TryEnqueue(() => { BuildChips(); Refresh(); });
        else if (name.StartsWith("Loudness") || name == nameof(MainViewModel.IsDeviceConnected))
            DispatcherQueue.TryEnqueue(Refresh);
    }

    /// <summary>The compensation at a representative -40 dB of volume.</summary>
    private void DrawCurve()
    {
        float refSpl = _liveRef ?? _vm.LoudnessRefSPL, intensity = _liveIntensity ?? _vm.LoudnessIntensity;
        float phon = Math.Min(Math.Max(refSpl + PreviewVolumeDb, 20), refSpl);
        var curve = LoudnessData.GetCompensationCurve(refSpl, phon, intensity);
        var accent = (Color)Application.Current.Resources["SystemAccentColor"];
        _graph.SetCurves(new[] { new ToolCurveGraph.Series("Compensation", accent, curve.Select(p => (p.freq, p.db)).ToArray()) },
            _vm.LoudnessEnabled, badge: "Curve at -40dB");
    }

    private void Refresh()
    {
        bool connected = _vm.IsDeviceConnected;
        _updating = true;
        _enable.IsOn = _vm.LoudnessEnabled;
        _enable.IsEnabled = connected;
        _updating = false;
        _ref.Value = _vm.LoudnessRefSPL;
        _intensity.Value = _vm.LoudnessIntensity;
        _ref.IsEnabled = _intensity.IsEnabled = connected;
        var committed = (_vm.LoudnessRefSPL, _vm.LoudnessIntensity);
        if (_base != committed) { _base = committed; _liveRef = _liveIntensity = null; }
        DrawCurve();

        _outputs.Visibility = _vm.LoudnessMaskSupported ? Visibility.Visible : Visibility.Collapsed;
        _presets.IsEnabled = connected;
        int mask = _vm.LoudnessOutputMask;
        for (int o = 0; o < _chipButtons.Count; o++)
        {
            _chipButtons[o].IsChecked = (mask & (1 << o)) != 0;
            _chipButtons[o].IsEnabled = connected;
        }
    }
}
