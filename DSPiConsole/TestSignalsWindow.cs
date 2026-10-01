using System.ComponentModel;
using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// The onboard signal generator. What plays and where on the left (signal
/// tiles, output chips), how it plays on the right (level, parameters, timing,
/// options), and a transport bar. Edits to a running generator re-apply after
/// a short pause. The generator keeps playing when the window closes, as on the
/// macOS Console; reopening the window shows it and stops it. After the macOS
/// Console's TestSignalsView.
/// </summary>
public sealed class TestSignalsWindow : Window
{
    private const float LevelMin = -80, LevelMax = 0;

    /// <summary>Host-side presentation per type; parameter ranges and defaults
    /// come from the device's descriptors when read, else these fallbacks.</summary>
    private sealed record TypeInfo(SiggenType Id, string Tile, string Display, string Blurb, string?[] ParamLabels,
                                   SiggenTimingModel Timing, SiggenParamDesc[] Fallback);

    private static SiggenParamDesc Pd(SiggenParamSemantic s, float min, float max, float def) => new(s, min, max, def);
    private static readonly SiggenParamDesc Unused = default;

    private static TypeInfo T(SiggenType id, string tile, string display, string blurb, string?[] labels, SiggenTimingModel timing, params SiggenParamDesc[] p) =>
        new(id, tile, display, blurb, labels, timing, p.Concat(Enumerable.Repeat(Unused, 4 - p.Length)).ToArray());

    private static readonly TypeInfo[] Types =
    {
        T(SiggenType.Sine, "Sine", "Sine", "Pure tone, THD approx -139 dB", new[] { "Frequency", null, null, null }, SiggenTimingModel.Continuous,
          Pd(SiggenParamSemantic.FreqHz, 1, 30000, 1000)),
        T(SiggenType.Square, "Square", "Square wave", "Band-limited (polyBLEP) square", new[] { "Frequency", null, null, null }, SiggenTimingModel.Continuous,
          Pd(SiggenParamSemantic.FreqHz, 1, 30000, 100)),
        T(SiggenType.White, "White", "White noise", "Uniform white noise", new string?[4], SiggenTimingModel.Continuous),
        T(SiggenType.Pink, "Pink", "Pink noise", "-3 dB/oct, level-safe normalized", new string?[4], SiggenTimingModel.Continuous),
        T(SiggenType.SweepLog, "Log Swp", "Log sweep", "Exponential sweep for room measurement", new[] { "Start", "End", null, null }, SiggenTimingModel.Sweep,
          Pd(SiggenParamSemantic.FreqHz, 1, 30000, 20), Pd(SiggenParamSemantic.FreqHz, 1, 30000, 20000)),
        T(SiggenType.SweepLin, "Lin Swp", "Linear sweep", "Linear frequency sweep", new[] { "Start", "End", null, null }, SiggenTimingModel.Sweep,
          Pd(SiggenParamSemantic.FreqHz, 1, 30000, 20), Pd(SiggenParamSemantic.FreqHz, 1, 30000, 20000)),
        T(SiggenType.SweepStep, "Step Swp", "Stepped sweep", "Discrete tones stepping up the band", new[] { "Start", "End", "Steps/octave", "Dwell" }, SiggenTimingModel.Sweep,
          Pd(SiggenParamSemantic.FreqHz, 1, 30000, 20), Pd(SiggenParamSemantic.FreqHz, 1, 30000, 20000),
          Pd(SiggenParamSemantic.Count, 1, 24, 3), Pd(SiggenParamSemantic.Ms, 20, 10000, 250)),
        T(SiggenType.Impulse, "Impulse", "Impulse", "Single-sample unit impulses", new[] { "Period", null, null, null }, SiggenTimingModel.Pattern,
          Pd(SiggenParamSemantic.Ms, 10, 60000, 500)),
        T(SiggenType.ClicksAlt, "Clicks", "Alternating clicks", "Clicks with alternating polarity", new[] { "Period", null, null, null }, SiggenTimingModel.Pattern,
          Pd(SiggenParamSemantic.Ms, 10, 60000, 500)),
        T(SiggenType.Polarity, "Polarity", "Polarity pulse", "Positive half-sine lobe per period", new[] { "Pulse width", "Period", null, null }, SiggenTimingModel.Pattern,
          Pd(SiggenParamSemantic.Ms, 1, 100, 5), Pd(SiggenParamSemantic.Ms, 10, 60000, 500)),
        T(SiggenType.ToneBurst, "Burst", "Tone burst", "Sine bursts with raised-cosine edges", new[] { "Frequency", "On cycles", "Off cycles", "Edge cycles" }, SiggenTimingModel.Pattern,
          Pd(SiggenParamSemantic.FreqHz, 1, 30000, 1000), Pd(SiggenParamSemantic.Cycles, 1, 1000, 8),
          Pd(SiggenParamSemantic.Cycles, 0, 1000, 8), Pd(SiggenParamSemantic.Cycles, 0, 100, 2)),
        T(SiggenType.TonePair, "2-Tone", "Tone pair", "IMD test pair (SMPTE / CCIF)", new[] { "Tone 1", "Tone 2", "Ratio A1/A2", null }, SiggenTimingModel.Continuous,
          Pd(SiggenParamSemantic.FreqHz, 1, 30000, 60), Pd(SiggenParamSemantic.FreqHz, 1, 30000, 7000), Pd(SiggenParamSemantic.Ratio, 0.1f, 10, 4)),
        T(SiggenType.Multitone, "Multi", "Multitone", "Log-spaced tones, Schroeder phases", new[] { "Tones", "Low", "High", null }, SiggenTimingModel.Continuous,
          Pd(SiggenParamSemantic.Count, 2, 16, 10), Pd(SiggenParamSemantic.FreqHz, 1, 30000, 20), Pd(SiggenParamSemantic.FreqHz, 1, 30000, 20000)),
        T(SiggenType.Isp, "ISP", "ISP test", "Inter-sample-peak over patterns", new[] { "Pattern", null, null, null }, SiggenTimingModel.Continuous,
          Pd(SiggenParamSemantic.Pattern, 0, 1, 0)),
        T(SiggenType.ChannelId, "Chan ID", "Channel ID", "Counted pentatonic blips per channel", new[] { "Blip length", null, null, null }, SiggenTimingModel.Pattern,
          Pd(SiggenParamSemantic.Ms, 30, 1000, 120)),
    };

    private static TypeInfo Info(SiggenType id) => Types.FirstOrDefault(t => t.Id == id) ?? Types[0];

    private readonly MainViewModel _vm;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private readonly Color _accent = (Color)Application.Current.Resources["SystemAccentColor"];
    private readonly ContentControl _bodyHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    // The columns and their scroll viewers are built once; a rebuild replaces
    // what is inside them (an element can have only one parent, and the right
    // column keeps its scroll position across edits).
    private readonly Grid _columns = new();
    private readonly ScrollViewer _leftScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ScrollViewer _rightScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _right = new() { Spacing = 16, Padding = new Thickness(16, 14, 16, 14) };
    private Grid _chips = new();
    private readonly Ellipse _pillDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pillText = new() { FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _pill = new() { Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
    private readonly TextBlock _transportTitle = new() { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium };
    private readonly TextBlock _transportDetail = new() { FontSize = 9 };
    private readonly Button _start, _stop, _stopNow;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Storyboard? _pulse;
    private bool _building;

    private SiggenConfig Draft => _vm.SiggenConfig;
    private SiggenStatus? Status => _vm.SiggenStatus;
    private bool Running => Status?.IsRunning ?? false;
    private bool ControlsEnabled => _vm.IsDeviceConnected && _vm.SiggenSupported;
    private TypeInfo CurrentInfo => Info(Draft.SignalType);

    /// <summary>The device's descriptor for the selected type, or the fallback.</summary>
    private (SiggenTimingModel Timing, SiggenParamDesc[] Params) Desc
    {
        get
        {
            var d = _vm.SiggenDescFor(Draft.SignalType);
            return d != null ? (d.TimingModel, d.Params) : (CurrentInfo.Timing, CurrentInfo.Fallback);
        }
    }

    private int OutputCount => _vm.SiggenCaps is { OutputChannels: > 0 } c ? c.OutputChannels : _vm.NumOutputChannels;

    private ushort AllOutputsMask => _vm.SiggenCaps is { ValidChannelMask: not 0 } c ? c.ValidChannelMask : (ushort)((1 << OutputCount) - 1);

    public TestSignalsWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        ToolWindowChrome.Apply(this, "Signal Generator", 780, 560);
        _transportDetail.Foreground = _secondary;
        _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _columns.Children.Add(_leftScroll);
        var divider = new Border { Width = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };
        Grid.SetColumn(divider, 1);
        _columns.Children.Add(divider);
        // The right column's length depends on the type, so it scrolls alone.
        _rightScroll.Content = _right;
        Grid.SetColumn(_rightScroll, 2);
        _columns.Children.Add(_rightScroll);

        _start = new Button { Content = Labelled("", "Start") };
        _start.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _start.Click += (_, _) => Start();
        _stop = new Button
        {
            Content = Labelled("", "Stop"),
            Background = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28)),
            Foreground = new SolidColorBrush(Colors.White),
        };
        _stop.Click += (_, _) => Stop(immediate: false);
        _stopNow = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8, 5, 8, 6) };
        ToolTipService.SetToolTip(_stopNow, "Stop immediately, no fade");
        _stopNow.Click += (_, _) => Stop(immediate: true);

        var root = new Grid { Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        root.Children.Add(BuildHeader());
        AddRow(root, Rule(), 1);
        AddRow(root, _bodyHost, 2);
        AddRow(root, Rule(), 3);
        AddRow(root, BuildTransport(), 4);
        Content = root;
        // Space starts and stops, except while typing in a field.
        root.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Space || FocusManager.GetFocusedElement(root.XamlRoot) is TextBox) return;
            e.Handled = true;
            if (Running) Stop(immediate: false); else if (StartBlocker == null) Start();
        };

        _statusTimer.Tick += async (_, _) => { if (ControlsEnabled && Running) await _vm.PollSiggenStatusAsync(); };
        _applyTimer.Tick += async (_, _) =>
        {
            _applyTimer.Stop();
            if (Running && StartBlocker == null) await _vm.ApplySiggenConfigAsync();
        };
        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) =>
        {
            // The generator keeps playing, as on the Mac: reopening the window
            // shows it running and stops it. A pending edit is dropped.
            _statusTimer.Stop();
            _applyTimer.Stop();
            _pulse?.Stop();
            _vm.PropertyChanged -= OnVmPropertyChanged;
        };
        Rebuild();
        if (_vm.IsDeviceConnected) _ = _vm.PollSiggenStatusAsync();
    }

    private static void AddRow(Grid g, FrameworkElement e, int row) { Grid.SetRow(e, row); g.Children.Add(e); }
    private static Border Rule() => new() { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };

    private static StackPanel Labelled(string glyph, string text) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children = { new FontIcon { Glyph = glyph, FontSize = 12 }, new TextBlock { Text = text } },
    };

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBlock Caption(string text) => new() { Text = text, FontSize = 9, Foreground = _secondary, TextWrapping = TextWrapping.Wrap };

    private static Grid TitleRow(FrameworkElement left, FrameworkElement right)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(left);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    // ── Header ──

    private FrameworkElement BuildHeader()
    {
        var header = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new FontIcon { Glyph = "", FontSize = 22, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(_accent) });
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Signal Generator", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = "Onboard test and measurement signals", FontSize = 10, Foreground = _secondary });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        _pill.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _pillDot, _pillText } };
        _pill.Background = new SolidColorBrush(ToolWindowChrome.PanelColor);
        _pill.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_pill, 2);
        header.Children.Add(_pill);
        return header;
    }

    private (string Label, Color Color) StateLabel => Status?.State switch
    {
        SiggenState.FadeIn => ("Fading in", Color.FromArgb(255, 48, 209, 88)),
        SiggenState.Run => ("Running", Color.FromArgb(255, 48, 209, 88)),
        SiggenState.Gap => ("Gap", Color.FromArgb(255, 255, 214, 10)),
        SiggenState.FadeOut => ("Fading out", Color.FromArgb(255, 255, 159, 10)),
        _ => ("Idle", Color.FromArgb(255, 160, 160, 165)),
    };

    // ── Body ──

    private void Rebuild()
    {
        _building = true;
        if (_vm.IsDeviceConnected && !_vm.SiggenSupported)
        {
            var note = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(40) };
            note.Children.Add(new FontIcon { Glyph = "", FontSize = 34, Foreground = _secondary });
            note.Children.Add(new TextBlock { Text = "Signal generator not available", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            note.Children.Add(new TextBlock
            {
                Text = "The connected firmware does not include the onboard test signal generator. Update the DSPi firmware to use this tool.",
                FontSize = 10, Foreground = _secondary, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            });
            _bodyHost.Content = note;
        }
        else
        {
            var left = new StackPanel { Spacing = 16, Padding = new Thickness(16, 14, 16, 14) };
            left.Children.Add(BuildSignals());
            left.Children.Add(Rule());
            left.Children.Add(BuildOutputs());
            BuildRight();
            _leftScroll.Content = left;
            if (_bodyHost.Content != _columns) _bodyHost.Content = _columns;
        }
        _building = false;
        RefreshStatus();
    }

    private FrameworkElement BuildSignals()
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(SectionLabel("SIGNAL"));
        var tiles = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (int c = 0; c < 5; c++) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < (Types.Length + 4) / 5; r++) tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < Types.Length; i++)
        {
            var tile = Tile(Types[i]);
            Grid.SetRow(tile, i / 5);
            Grid.SetColumn(tile, i % 5);
            tiles.Children.Add(tile);
        }
        section.Children.Add(tiles);
        section.Children.Add(new TextBlock { Text = CurrentInfo.Blurb, FontSize = 9, Foreground = _secondary });
        return section;
    }

    private Button Tile(TypeInfo ti)
    {
        bool selected = Draft.SignalType == ti.Id;
        var tint = selected ? new SolidColorBrush(_accent) : _secondary;
        var glyph = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = SiggenGlyphs.For(ti.Id, 44, 22),
            Stroke = tint, StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 44, Height = 22, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock
        {
            Text = ti.Tile, FontSize = 8, Foreground = tint, HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        });
        var tile = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(2, 6, 2, 6),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(selected ? With(_accent, 0.16) : ToolWindowChrome.PanelColor),
            BorderBrush = new SolidColorBrush(selected ? With(_accent, 0.7) : Color.FromArgb(51, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            IsEnabled = ControlsEnabled,
        };
        ToolTipService.SetToolTip(tile, ti.Blurb);
        tile.Click += (_, _) => SelectType(ti.Id);
        return tile;
    }

    private FrameworkElement BuildOutputs()
    {
        var section = new StackPanel { Spacing = 8 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttons.Children.Add(LinkButton("All", () => Update(d => d.ChannelMask = AllOutputsMask)));
        buttons.Children.Add(LinkButton("None", () => Update(d => { d.ChannelMask = 0; d.InvertMask = 0; })));
        section.Children.Add(TitleRow(SectionLabel("OUTPUTS"), buttons));
        _chips = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (int c = 0; c < 3; c++) _chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < (OutputCount + 2) / 3; r++) _chips.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int o = 0; o < OutputCount; o++)
        {
            var chip = OutputChip(o);
            Grid.SetRow(chip, o / 3);
            Grid.SetColumn(chip, o % 3);
            _chips.Children.Add(chip);
        }
        section.Children.Add(_chips);
        section.Children.Add(Caption("Click to select, click again to invert polarity (ø). Dimmed outputs are disabled in the matrix mixer and stay silent."));
        return section;
    }

    private HyperlinkButton LinkButton(string text, Action act)
    {
        var b = new HyperlinkButton { Content = text, FontSize = 9, Padding = new Thickness(4, 0, 4, 0), IsEnabled = ControlsEnabled };
        b.Click += (_, _) => act();
        return b;
    }

    private string OutputName(int o)
    {
        var outputs = _vm.ActiveOutputs;
        return o < outputs.Count ? _vm.GetChannelName(outputs[o]) : $"Out {o + 1}";
    }

    private Button OutputChip(int o)
    {
        ushort bit = (ushort)(1 << o);
        bool selected = (Draft.ChannelMask & bit) != 0, inverted = (Draft.InvertMask & bit) != 0;
        bool matrixEnabled = _vm.IsOutputEnabled(o);
        bool walking = Running && Status?.ActiveChannel == o;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(new TextBlock
        {
            Text = OutputName(o), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = selected ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] : _secondary,
        });
        if (inverted)
            row.Children.Add(new TextBlock
            {
                Text = "ø", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 159, 10)),
            });
        var chip = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4, 5, 4, 5),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(selected ? With(_accent, 0.18) : ToolWindowChrome.PanelColor),
            // The output a walk is playing right now is outlined.
            BorderBrush = new SolidColorBrush(walking ? Color.FromArgb(255, 48, 209, 88)
                : selected ? With(_accent, 0.6) : Color.FromArgb(51, 128, 128, 128)),
            BorderThickness = new Thickness(walking ? 1.5 : 1),
            Opacity = matrixEnabled ? 1 : 0.4,
            IsEnabled = ControlsEnabled,
        };
        ToolTipService.SetToolTip(chip, matrixEnabled
            ? "Click: on → inverted → off"
            : "Output disabled in the matrix mixer - selected but silent until enabled");
        chip.Click += (_, _) => CycleOutput(o);
        return chip;
    }

    private void CycleOutput(int o)
    {
        ushort bit = (ushort)(1 << o);
        Update(d =>
        {
            if ((d.ChannelMask & bit) == 0) d.ChannelMask |= bit;                  // off -> on
            else if ((d.InvertMask & bit) == 0) d.InvertMask |= bit;               // on -> inverted
            else { d.ChannelMask &= (ushort)~bit; d.InvertMask &= (ushort)~bit; }  // inverted -> off
        });
    }

    private void BuildRight()
    {
        _right.Children.Clear();
        var desc = Desc;

        // Level
        var level = new StackPanel { Spacing = 4 };
        var field = new ValueField("dB", Draft.LevelDb, v => Update(d => d.LevelDb = Math.Clamp(v, LevelMin, LevelMax)),
            LevelMin, LevelMax, step: 1, decimals: 1, width: 60) { IsEnabled = ControlsEnabled };
        level.Children.Add(TitleRow(SectionLabel("LEVEL"), field));
        var slider = new Slider { Minimum = LevelMin, Maximum = LevelMax, StepFrequency = 0.5, Value = Math.Clamp(Draft.LevelDb, LevelMin, LevelMax), IsEnabled = ControlsEnabled };
        slider.ValueChanged += (_, e) =>
        {
            if (_building) return;
            field.Value = (float)e.NewValue;
            Update(d => d.LevelDb = (float)e.NewValue, rebuild: false);
        };
        level.Children.Add(slider);
        level.Children.Add(Caption("Peak level in dBFS. Output trim, master volume and mute still apply downstream."));
        _right.Children.Add(level);

        // Parameters
        var used = Enumerable.Range(0, 4).Where(i => desc.Params[i].IsUsed).ToArray();
        if (used.Length > 0)
        {
            var parameters = new StackPanel { Spacing = 8 };
            parameters.Children.Add(SectionLabel("PARAMETERS"));
            if (Draft.SignalType == SiggenType.Isp) parameters.Children.Add(IspPicker());
            else foreach (int i in used) parameters.Children.Add(ParamRow(i, desc.Params[i]));
            if (Draft.SignalType == SiggenType.TonePair)
            {
                var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                presets.Children.Add(new TextBlock { Text = "Presets:", FontSize = 9, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center });
                presets.Children.Add(LinkButton("SMPTE 60/7k", () => Update(d => { d.P1 = 60; d.P2 = 7000; d.P3 = 4; })));
                presets.Children.Add(LinkButton("CCIF 19k/20k", () => Update(d => { d.P1 = 19000; d.P2 = 20000; d.P3 = 1; })));
                parameters.Children.Add(presets);
            }
            if (Draft.SignalType == SiggenType.Multitone && _vm.SiggenCaps is { MultitoneMax: > 0 } caps)
                parameters.Children.Add(Caption($"Up to {caps.MultitoneMax} tones on this device."));
            _right.Children.Add(parameters);
        }

        // Timing
        var timing = new StackPanel { Spacing = 8 };
        timing.Children.Add(SectionLabel("TIMING"));
        bool walk = Draft.Flags.HasFlag(SiggenFlags.Walk) || Draft.SignalType == SiggenType.ChannelId;
        switch (desc.Timing)
        {
            case SiggenTimingModel.Sweep:
                timing.Children.Add(TimingRow("Sweep length", null, Seconds(0.01f)));
                timing.Children.Add(TimingRow("Repeat", "0 = repeat forever", RepeatField()));
                timing.Children.Add(TimingRow("Gap between sweeps", null, GapField()));
                break;
            case SiggenTimingModel.Pattern:
                timing.Children.Add(TimingRow("Repeat", Draft.SignalType == SiggenType.ChannelId
                    ? "Passes over the selected outputs. 0 = forever" : "Pattern periods. 0 = repeat forever", RepeatField()));
                timing.Children.Add(TimingRow("Extra gap per period", null, GapField()));
                break;
            default:
                if (walk)
                {
                    timing.Children.Add(TimingRow("Dwell per channel", "0 = 2 s default", Seconds(0)));
                    timing.Children.Add(TimingRow("Passes", "Full passes over the outputs. 0 = forever", RepeatField()));
                }
                else timing.Children.Add(TimingRow("Duration", "0 = play until stopped", Seconds(0)));
                break;
        }
        _right.Children.Add(timing);
        _right.Children.Add(Rule());

        // Options
        var options = new StackPanel { Spacing = 10 };
        options.Children.Add(SectionLabel("OPTIONS"));
        options.Children.Add(OptionRow("Bypass output EQ (RAW)",
            "Skips crossover and PEQ on the selected outputs. Trim, master volume, mute and delay still apply.",
            Draft.Flags.HasFlag(SiggenFlags.Raw), SetRaw));
        if (Draft.SignalType is SiggenType.White or SiggenType.Pink)
            options.Children.Add(OptionRow("Decorrelate channels", "Independent noise per output instead of one copied signal.",
                Draft.Flags.HasFlag(SiggenFlags.Decorrelate), (t, on) => SetFlag(SiggenFlags.Decorrelate, on)));
        if (Draft.SignalType == SiggenType.ChannelId)
        {
            var row = OptionRow("Walk outputs one at a time", "Channel ID always walks: each output plays its channel number as counted blips.", true, (_, _) => { });
            ((ToggleSwitch)((Grid)row).Children[1]).IsEnabled = false;
            options.Children.Add(row);
        }
        else
            options.Children.Add(OptionRow("Walk outputs one at a time", "Plays the selected outputs sequentially instead of together.",
                Draft.Flags.HasFlag(SiggenFlags.Walk), (t, on) => SetFlag(SiggenFlags.Walk, on)));
        _right.Children.Add(options);
    }

    private FrameworkElement ParamRow(int i, SiggenParamDesc p)
    {
        var labels = CurrentInfo.ParamLabels;
        string label = i < labels.Length ? labels[i] ?? $"p{i + 1}" : $"p{i + 1}";
        bool whole = p.Semantic is SiggenParamSemantic.Count or SiggenParamSemantic.Cycles;
        bool ratio = p.Semantic == SiggenParamSemantic.Ratio;
        string unit = p.Semantic switch
        {
            SiggenParamSemantic.FreqHz => "Hz",
            SiggenParamSemantic.Ms => "ms",
            SiggenParamSemantic.Cycles => "cyc",
            SiggenParamSemantic.Ratio => "×",
            _ => "",
        };
        float max = p.Max == 0 ? float.MaxValue : p.Max;
        var field = new ValueField(unit, Draft.GetParam(i), v => Update(d => d.SetParam(i, whole ? MathF.Round(v) : v), rebuild: false),
            p.Min, max, step: whole ? 1 : ratio ? 0.1f : 1, decimals: whole ? 0 : ratio ? 2 : 1) { IsEnabled = ControlsEnabled };
        return TitleRow(new TextBlock { Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center }, field);
    }

    /// <summary>The ISP patterns by what they prove: a sample-peak-normalized
    /// sequence with a known inter-sample true-peak over.</summary>
    private FrameworkElement IspPicker()
    {
        var picker = new SegmentedPicker(new[] { "fs/4 · +3.01 dBTP", "fs/6 · +1.25 dBTP" }, height: 24, fontSize: 11)
        {
            Selected = (int)MathF.Round(Draft.P1) == 1 ? 1 : 0,
            IsEnabled = ControlsEnabled,
        };
        picker.Picked += i => Update(d => d.P1 = i);
        ToolTipService.SetToolTip(picker, "Sample-peak-normalized sequences with a known inter-sample true-peak over. Set the level at or below the headroom the over should fit into.");
        return picker;
    }

    private FrameworkElement TimingRow(string label, string? caption, FrameworkElement field)
    {
        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        if (caption != null) titles.Children.Add(Caption(caption));
        return TitleRow(titles, field);
    }

    private ValueField Seconds(float min) =>
        new("s", Draft.DurationMs / 1000f, v => Update(d => d.DurationMs = (uint)(Math.Max(min, v) * 1000), rebuild: false), min, 4_000_000, step: 0.5f, decimals: 2)
        { IsEnabled = ControlsEnabled };

    private ValueField RepeatField() =>
        new("", Draft.Repeat, v => Update(d => d.Repeat = (ushort)Math.Clamp(MathF.Round(v), 0, 65535), rebuild: false), 0, 65535, step: 1, decimals: 0)
        { IsEnabled = ControlsEnabled };

    private ValueField GapField() =>
        new("ms", Draft.GapMs, v => Update(d => d.GapMs = (ushort)Math.Clamp(MathF.Round(v), 0, 65535), rebuild: false), 0, 65535, step: 50, decimals: 0)
        { IsEnabled = ControlsEnabled };

    private FrameworkElement OptionRow(string title, string caption, bool on, Action<ToggleSwitch, bool> set)
    {
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        titles.Children.Add(Caption(caption));
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = ControlsEnabled, VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += (_, _) => { if (!_building) set(toggle, toggle.IsOn); };
        return TitleRow(titles, toggle);
    }

    private void SetFlag(SiggenFlags flag, bool on) => Update(d => d.Flags = on ? d.Flags | flag : d.Flags & ~flag);

    /// <summary>RAW skips the crossover too, so the selected outputs get
    /// full-range signal: ask first, as bypassing a crossover does.</summary>
    private async void SetRaw(ToggleSwitch toggle, bool on)
    {
        if (on)
        {
            var dialog = new ContentDialog
            {
                Title = "Bypass the output EQ?",
                Content = "This sends the test signal to the selected outputs with no crossover protection, which can damage unprotected drivers such as tweeters. Continue only if you are sure.",
                PrimaryButtonText = "Bypass",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _building = true;
                toggle.IsOn = false;
                _building = false;
                return;
            }
        }
        SetFlag(SiggenFlags.Raw, on);
    }

    // ── Transport ──

    private FrameworkElement BuildTransport()
    {
        var bar = new Grid { Padding = new Thickness(16, 10, 16, 10), ColumnSpacing = 10 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_transportTitle);
        text.Children.Add(_transportDetail);
        bar.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(_stopNow);
        buttons.Children.Add(_stop);
        buttons.Children.Add(_start);
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);
        return bar;
    }

    private string? StartBlocker =>
        !_vm.IsDeviceConnected ? "No device connected"
        : !_vm.SiggenSupported ? "Firmware has no signal generator"
        : Draft.ChannelMask == 0 ? "Select at least one output"
        : Desc.Timing == SiggenTimingModel.Sweep && Draft.DurationMs == 0 ? "Sweep length must be greater than 0"
        : null;

    private async void Start()
    {
        if (StartBlocker != null) return;
        _applyTimer.Stop();
        if (!await _vm.StartSiggenAsync())
        {
            _transportTitle.Text = "Rejected";
            _transportDetail.Text = "The device declined the signal configuration.";
        }
    }

    private void Stop(bool immediate)
    {
        // A debounced edit landing during the fade-out would be a restart.
        _applyTimer.Stop();
        _ = _vm.StopSiggenAsync(now: immediate);
    }

    // ── Edits ──

    private void SelectType(SiggenType id)
    {
        var d = _vm.SiggenDescFor(id);
        var defaults = d?.Params ?? Info(id).Fallback;
        var timing = d?.TimingModel ?? Info(id).Timing;
        // A new signal starts from its own defaults, not the last one's values.
        Update(c =>
        {
            c.SignalType = id;
            for (int i = 0; i < 4; i++) c.SetParam(i, defaults[i].Default);
            c.DurationMs = timing == SiggenTimingModel.Sweep ? 5000u : 0u;
            c.Repeat = 0;
            c.GapMs = 0;
        });
    }

    /// <summary>Edits the draft and, while the generator runs, re-applies it
    /// after a short pause (each SET_CONFIG restarts with a fade).</summary>
    private void Update(Action<SiggenConfig> mutate, bool rebuild = true)
    {
        if (_building) return;
        var before = Draft.ToBytes();
        mutate(Draft);
        // An unchanged draft is not re-applied: each apply fades out and in.
        if (Running && ControlsEnabled && !before.AsSpan().SequenceEqual(Draft.ToBytes()))
        {
            _applyTimer.Stop();
            // The firmware refuses an empty output mask, so None silences instead.
            if (Draft.ChannelMask == 0) _ = _vm.StopSiggenAsync(now: false);
            else _applyTimer.Start();
        }
        if (rebuild) Rebuild(); else RefreshStatus();
    }

    // ── Status ──

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SiggenStatus):
                DispatcherQueue.TryEnqueue(RefreshStatus);
                break;
            case nameof(MainViewModel.SiggenSupported) or nameof(MainViewModel.IsDeviceConnected) or nameof(MainViewModel.Platform):
                DispatcherQueue.TryEnqueue(Rebuild);
                break;
        }
    }

    private int _lastWalkChannel = -1;

    private void RefreshStatus()
    {
        var (label, color) = StateLabel;
        bool running = Running;
        _pillDot.Fill = new SolidColorBrush(color);
        _pillText.Text = label;
        _pillText.Foreground = running ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] : _secondary;
        _pill.BorderBrush = new SolidColorBrush(With(color, running ? 0.5 : 0.2));
        SetPulse(running);
        if (running) _statusTimer.Start(); else _statusTimer.Stop();

        _start.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _stop.Visibility = _stopNow.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        var blocker = StartBlocker;
        _start.IsEnabled = blocker == null;
        ToolTipService.SetToolTip(_start, blocker ?? "Start the generator (Space)");
        ToolTipService.SetToolTip(_stop, "Stop with a fade (Space)");

        var st = Status;
        if (running && st != null)
        {
            _transportTitle.Text = $"{label} · {Info(st.SignalType).Display}";
            var parts = new List<string> { Elapsed(st.ElapsedMs) };
            if (st.CurrentFreq > 0) parts.Add(st.CurrentFreq >= 1000 ? $"{st.CurrentFreq / 1000:0.00} kHz" : $"{st.CurrentFreq:0} Hz");
            if (st.CyclesDone > 0) parts.Add($"cycle {st.CyclesDone}");
            if (st.ActiveChannel != 0xFF) parts.Add(OutputName(st.ActiveChannel));
            parts.Add("edits apply live");
            _transportDetail.Text = string.Join(" · ", parts);
        }
        else
        {
            _transportTitle.Text = blocker != null && _vm.IsDeviceConnected && _vm.SiggenSupported ? blocker : $"Ready · {CurrentInfo.Display}";
            int n = System.Numerics.BitOperations.PopCount(Draft.ChannelMask);
            _transportDetail.Text = st?.StopReason switch
            {
                2 => $"Finished after {Elapsed(st.ElapsedMs)}",
                1 => "Stopped",
                3 => "Stopped by preset load",
                _ => $"{n} output{(n == 1 ? "" : "s")} · peak {Draft.LevelDb:0.0} dBFS",
            };
        }

        // The walked output's outline moves with the walk.
        int walkChannel = running && st != null && st.ActiveChannel != 0xFF ? st.ActiveChannel : -1;
        if (walkChannel != _lastWalkChannel && _bodyHost.Content is Grid)
        {
            _lastWalkChannel = walkChannel;
            for (int o = 0; o < _chips.Children.Count && o < OutputCount; o++)
            {
                if (_chips.Children[o] is not Button chip) continue;
                bool selected = (Draft.ChannelMask & (1 << o)) != 0, walking = o == walkChannel;
                chip.BorderBrush = new SolidColorBrush(walking ? Color.FromArgb(255, 48, 209, 88)
                    : selected ? With(_accent, 0.6) : Color.FromArgb(51, 128, 128, 128));
                chip.BorderThickness = new Thickness(walking ? 1.5 : 1);
            }
        }
    }

    /// <summary>The status dot breathes while the generator runs.</summary>
    private void SetPulse(bool on)
    {
        if (on && _pulse == null)
        {
            var animation = new DoubleAnimation
            {
                From = 1, To = 0.45, Duration = new Duration(TimeSpan.FromSeconds(0.7)),
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(animation, _pillDot);
            Storyboard.SetTargetProperty(animation, "Opacity");
            _pulse = new Storyboard();
            _pulse.Children.Add(animation);
            _pulse.Begin();
        }
        else if (!on && _pulse != null)
        {
            _pulse.Stop();
            _pulse = null;
            _pillDot.Opacity = 1;
        }
    }

    private static string Elapsed(uint ms)
    {
        double s = ms / 1000.0;
        return s < 60 ? $"{s:0.0} s" : $"{(int)s / 60}:{s % 60:00.0}";
    }

    private static Color With(Color c, double opacity) => Color.FromArgb((byte)(opacity * 255), c.R, c.G, c.B);
}
