using System.ComponentModel;
using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// The stereo upmixer (firmware wire V25+, RP2350): derives Centre and Surround
/// from a stereo input. One column, as on the macOS Console: a STATUS line with
/// live gauges, the two engines, then a section for each engine that is on, and
/// a routing note. An engine that is off has nothing to configure, so its whole
/// section goes. Telemetry is polled at 10 Hz while the window is open. After
/// the macOS Console's UpmixerView.
/// </summary>
public sealed class UpmixerWindow : Window
{
    /// <summary>The centre picker lists Off first to line up with Surround;
    /// on the wire Off was appended as 2.</summary>
    private static readonly int[] CentreWire = { (int)UpmixCenterMode.Off, (int)UpmixCenterMode.Passive, (int)UpmixCenterMode.Adaptive };

    private readonly MainViewModel _vm;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private readonly ContentControl _bodyHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly ToggleSwitch _enable = new() { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly SegmentedPicker _centreMode = new(new[] { "Off", "Sinner", "Logician" }, height: 24, fontSize: 12);
    private readonly SegmentedPicker _surroundMode = new(new[] { "Off", "Sinner", "Logician" }, height: 24, fontSize: 12);
    private readonly ParameterRow _strength, _width, _presence, _threshold, _attack, _release, _detHpf;
    private readonly ParameterRow _surDelay, _surHpf, _surLpf, _decorr;
    private readonly StackPanel _centreSection = new() { Spacing = 14 }, _surroundSection = new() { Spacing = 14 };
    private readonly StackPanel _logicianRows = new() { Spacing = 14 };
    private readonly Border _centreRule = Rule(), _surroundRule = Rule();
    private readonly Ellipse _statusDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusText = new() { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
    private readonly Gauge _corr, _centreGain, _lsGain, _rsGain;
    private FrameworkElement? _body;
    private bool _updating;

    public UpmixerWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        ToolWindowChrome.Apply(this, "Stereo Upmixer", 400, 720);

        var accent = (Color)Application.Current.Resources["SystemAccentColor"];
        _corr = new Gauge("Correlation", accent, _secondary);
        _centreGain = new Gauge("Centre gain", Color.FromArgb(255, 48, 209, 88), _secondary);
        _lsGain = new Gauge("Ls gain", Color.FromArgb(255, 191, 90, 242), _secondary);
        _rsGain = new Gauge("Rs gain", Color.FromArgb(255, 255, 55, 95), _secondary);

        _strength = Row("Strength", "%", UpmixLimits.StrengthMinPct, UpmixLimits.StrengthMaxPct, 0, 1, UpmixParam.Strength, v => _vm.UpmixStrengthPct = v,
            "Centre extraction strength; scales both the C output and how much centre energy is removed from L/R. In Sinner mode this is the fixed centre gain.");
        _width = Row("Centre Width", "%", UpmixLimits.WidthMinPct, UpmixLimits.WidthMaxPct, 0, 1, UpmixParam.CenterWidth, v => _vm.UpmixCenterWidthPct = v,
            "How much extracted centre stays in L/R. 0 = full removal (discrete centre); 100 = L/R untouched (expect combing if a real centre speaker plays).");
        _presence = Row("Presence", "dB", UpmixLimits.PresenceMinDb, UpmixLimits.PresenceMaxDb, 1, 0.5f, UpmixParam.Presence, v => _vm.UpmixPresenceDb = v,
            "Voice presence bell at 3 kHz (Q 0.6). Positive brings voices forward, negative pushes them back (Syn-style). Stored in 0.5 dB steps.");
        _threshold = Row("Correlation Threshold", "%", UpmixLimits.ThresholdMinPct, UpmixLimits.ThresholdMaxPct, 0, 1, UpmixParam.Threshold, v => _vm.UpmixThresholdPct = v,
            "Correlation gate. Below this, nothing is extracted; above it, extraction scales up to full. Raise to extract only strongly-correlated content.");
        _attack = Row("Attack", "ms", UpmixLimits.AttackMinMs, UpmixLimits.AttackMaxMs, 0, 1, UpmixParam.Attack, v => _vm.UpmixAttackMs = v,
            "Centre gain rise time (Logician mode).");
        _release = Row("Release", "ms", UpmixLimits.ReleaseMinMs, UpmixLimits.ReleaseMaxMs, 0, 5, UpmixParam.Release, v => _vm.UpmixReleaseMs = v,
            "Centre gain fall time (Logician mode).");
        _detHpf = Row("Detector HPF", "Hz", UpmixLimits.DetHpfMinHz, UpmixLimits.DetHpfMaxHz, 0, 5, UpmixParam.DetectorHpf, v => _vm.UpmixDetectorHpfHz = v,
            "Detector bass-cut corner. Content below this is ignored by the steering detector (the audio itself is not filtered) so bass does not pump the centre.");
        _surDelay = Row("Delay", "ms", UpmixLimits.SurDelayMinMs, UpmixLimits.SurDelayMaxMs, 1, 0.5f, UpmixParam.SurroundDelay, v => _vm.UpmixSurroundDelayMs = v,
            "Haas delay on Ls/Rs (precedence effect). Rule of thumb ~1 ms per foot of listener distance.");
        _surHpf = Row("Band-limit HPF", "Hz", UpmixLimits.SurHpfMinHz, UpmixLimits.SurHpfMaxHz, 0, 5, UpmixParam.SurroundHpf, v => _vm.UpmixSurroundHpfHz = v,
            "Surround high-pass; keeps rumble out of the rears.");
        _surLpf = Row("Band-limit LPF", "Hz", UpmixLimits.SurLpfMinHz, UpmixLimits.SurLpfMaxHz, 0, 100, UpmixParam.SurroundLpf, v => _vm.UpmixSurroundLpfHz = v,
            "Surround low-pass. 7 kHz is the classic surround voicing; raise for full-band rears.");
        _decorr = Row("Decorrelation", "%", UpmixLimits.DecorrMinPct, UpmixLimits.DecorrMaxPct, 0, 1, UpmixParam.Decorr, v => _vm.UpmixDecorrPct = v,
            "Schroeder allpass decorrelator amount. 0 disables decorrelation.");

        _centreMode.Picked += i => _vm.UpmixCenterMode = CentreWire[i];
        _surroundMode.Picked += i => _vm.UpmixSurroundMode = i;

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

        _enable.Toggled += (_, _) => { if (!_updating) _vm.UpmixEnabled = _enable.IsOn; };
        // 10 Hz telemetry while the window is open (the spec asks for 5-20 Hz).
        _statusTimer.Tick += async (_, _) =>
        {
            if (_vm.UpmixSupported && _vm.IsDeviceConnected)
                await _vm.PollUpmixStatusAsync();
        };
        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _vm.PropertyChanged -= OnVmPropertyChanged;
        };
        Refresh();
        _statusTimer.Start();
    }

    private ParameterRow Row(string title, string unit, float min, float max, int decimals, float step, ushort id, Action<float> set, string caption) =>
        new(title, unit, min, max, set: v => set(Math.Clamp(v, min, max)), live: v => _vm.SendUpmixLive(id, Math.Clamp(v, min, max)))
        {
            ScrollStep = step,
            MaxDecimals = decimals,
            FieldWidth = 64,
            Caption = caption,
        };

    private static Border Rule() => new() { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = _secondary,
    };

    private TextBlock Note(string text) => new() { Text = text, FontSize = 9, Foreground = _secondary, TextWrapping = TextWrapping.Wrap };

    private FrameworkElement BuildHeader()
    {
        var header = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new FontIcon
        {
            Glyph = "", FontSize = 22, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColor"]),
        });
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Stereo Upmixer", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = "Derive Centre and Surround from stereo", FontSize = 10, Foreground = _secondary });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Grid.SetColumn(_enable, 2);
        header.Children.Add(_enable);
        return header;
    }

    private FrameworkElement BuildBody()
    {
        var body = new StackPanel { Spacing = 20, Padding = new Thickness(16) };

        var status = new StackPanel { Spacing = 10 };
        status.Children.Add(SectionLabel("STATUS"));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(_statusDot);
        line.Children.Add(_statusText);
        status.Children.Add(line);
        // Gauges that do not apply are hidden at zero opacity rather than
        // removed, so the section's height never changes as they come and go.
        foreach (var g in new[] { _corr, _centreGain, _lsGain, _rsGain }) status.Children.Add(g.Root);
        body.Children.Add(status);
        body.Children.Add(Rule());

        var engines = new StackPanel { Spacing = 14 };
        engines.Children.Add(SectionLabel("ENGINES"));
        engines.Children.Add(EngineRow("Centre", _centreMode));
        engines.Children.Add(EngineRow("Surround", _surroundMode));
        engines.Children.Add(Note("Logician centre gates extraction on running L/R correlation; Logician surround uses a Pro Logic II-style matrix decoder. Sinner modes are fixed (C = 0.7071(L+R), surround = L-R) - a Hafler-style passive matrix like the one in the Schiit Syn."));
        body.Children.Add(engines);

        body.Children.Add(_centreRule);
        _centreSection.Children.Add(SectionLabel("CENTRE"));
        // Strength, Width and Presence work in both centre modes; the steering
        // controls only drive the Logician engine.
        _centreSection.Children.Add(_strength);
        _centreSection.Children.Add(Rule());
        _centreSection.Children.Add(_width);
        _centreSection.Children.Add(Rule());
        _centreSection.Children.Add(_presence);
        _logicianRows.Children.Add(Rule());
        _logicianRows.Children.Add(_threshold);
        _logicianRows.Children.Add(Rule());
        _logicianRows.Children.Add(_attack);
        _logicianRows.Children.Add(Rule());
        _logicianRows.Children.Add(_release);
        _logicianRows.Children.Add(Rule());
        _logicianRows.Children.Add(_detHpf);
        _centreSection.Children.Add(_logicianRows);
        body.Children.Add(_centreSection);

        body.Children.Add(_surroundRule);
        _surroundSection.Children.Add(SectionLabel("SURROUND"));
        _surroundSection.Children.Add(_surDelay);
        _surroundSection.Children.Add(Rule());
        _surroundSection.Children.Add(_surHpf);
        _surroundSection.Children.Add(Rule());
        _surroundSection.Children.Add(_surLpf);
        _surroundSection.Children.Add(Rule());
        _surroundSection.Children.Add(_decorr);
        body.Children.Add(_surroundSection);

        body.Children.Add(Rule());
        var routing = new StackPanel { Spacing = 6 };
        routing.Children.Add(SectionLabel("ROUTING"));
        routing.Children.Add(Note("The derived channels appear as matrix source rows: row 2 = Centre, row 3 = Left Surround, row 4 = Right Surround. Open the Matrix Mixer to route them to your output slots (a centre crosspoint gain of -3 dB is a safe start, since the centre row can reach +3 dBFS)."));
        body.Children.Add(routing);

        return new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static FrameworkElement EngineRow(string label, SegmentedPicker picker)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(picker, 1);
        row.Children.Add(picker);
        return row;
    }

    private FrameworkElement UnsupportedNote()
    {
        var note = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(24) };
        note.Children.Add(new FontIcon { Glyph = "", FontSize = 28, Foreground = _secondary });
        note.Children.Add(new TextBlock { Text = "Requires an RP2350 device with firmware wire format V25 or newer.", FontSize = 12, Foreground = _secondary, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        note.Children.Add(new TextBlock { Text = "The upmixer runs on stereo input at 48 kHz or below.", FontSize = 10, Foreground = _secondary, HorizontalAlignment = HorizontalAlignment.Center });
        return note;
    }

    // ── Sync ──

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string? name = e.PropertyName;
        if (name == nameof(MainViewModel.UpmixStatus)) DispatcherQueue.TryEnqueue(RefreshStatus);
        else if (name != null && (name.StartsWith("Upmix") || name == nameof(MainViewModel.IsDeviceConnected)))
            DispatcherQueue.TryEnqueue(Refresh);
    }

    private bool CentreOff => _vm.UpmixCenterMode == (int)UpmixCenterMode.Off;
    private bool SurroundOn => _vm.UpmixSurroundMode != (int)UpmixSurroundMode.Off;

    private void Refresh()
    {
        bool connected = _vm.IsDeviceConnected, supported = _vm.UpmixSupported;
        _updating = true;
        _enable.IsOn = _vm.UpmixEnabled;
        _enable.IsEnabled = connected && supported;
        _updating = false;

        if (!supported)
        {
            if (_bodyHost.Content is not StackPanel) _bodyHost.Content = UnsupportedNote();
            return;
        }
        _body ??= BuildBody();
        if (_bodyHost.Content != _body) _bodyHost.Content = _body;

        _centreMode.Selected = Array.IndexOf(CentreWire, _vm.UpmixCenterMode) is var c and >= 0 ? c : 2;
        _surroundMode.Selected = Math.Clamp(_vm.UpmixSurroundMode, 0, 2);
        _centreMode.IsEnabled = _surroundMode.IsEnabled = connected;

        // An engine that is off has nothing to configure: its section goes.
        _centreRule.Visibility = _centreSection.Visibility = CentreOff ? Visibility.Collapsed : Visibility.Visible;
        _surroundRule.Visibility = _surroundSection.Visibility = SurroundOn ? Visibility.Visible : Visibility.Collapsed;
        _logicianRows.Visibility = _vm.UpmixCenterMode == (int)UpmixCenterMode.Adaptive ? Visibility.Visible : Visibility.Collapsed;

        _strength.Value = _vm.UpmixStrengthPct;
        _width.Value = _vm.UpmixCenterWidthPct;
        _presence.Value = _vm.UpmixPresenceDb;
        _threshold.Value = _vm.UpmixThresholdPct;
        _attack.Value = _vm.UpmixAttackMs;
        _release.Value = _vm.UpmixReleaseMs;
        _detHpf.Value = _vm.UpmixDetectorHpfHz;
        _surDelay.Value = _vm.UpmixSurroundDelayMs;
        _surHpf.Value = _vm.UpmixSurroundHpfHz;
        _surLpf.Value = _vm.UpmixSurroundLpfHz;
        _decorr.Value = _vm.UpmixDecorrPct;
        foreach (var r in new[] { _strength, _width, _presence, _threshold, _attack, _release, _detHpf, _surDelay, _surHpf, _surLpf, _decorr })
            r.IsEnabled = connected;
        RefreshStatus();
    }

    /// <summary>The status line, and the gauges while the upmixer is active.</summary>
    private void RefreshStatus()
    {
        var s = _vm.UpmixStatus;
        bool connected = _vm.IsDeviceConnected, active = connected && s is { Active: true };
        _statusText.Text = !connected ? "No device connected"
            : active ? "Active - processing audio"
            : s?.ParkedReason switch
            {
                1 => "Idle: upmixer disabled",
                2 => "Idle: input is not stereo",
                3 => "Idle: sample rate above 48 kHz",
                _ => "Idle",
            };
        _statusText.Foreground = active ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] : _secondary;
        _statusDot.Fill = new SolidColorBrush(!connected ? Color.FromArgb(128, 160, 160, 160)
            : active ? Color.FromArgb(255, 48, 209, 88) : Color.FromArgb(255, 255, 159, 10));

        float corr = s?.Correlation ?? 0;
        _corr.Set((corr + 1) / 2, $"{corr:+0.00;-0.00}", active);
        _centreGain.Set(s?.CenterGain ?? 0, $"{(s?.CenterGain ?? 0) * 100:0}%", active && !CentreOff);
        _lsGain.Set(s?.LsGain ?? 0, $"{(s?.LsGain ?? 0) * 100:0}%", active && SurroundOn);
        _rsGain.Set(s?.RsGain ?? 0, $"{(s?.RsGain ?? 0) * 100:0}%", active && SurroundOn);
    }

    /// <summary>One labelled telemetry bar with its reading.</summary>
    private sealed class Gauge
    {
        public Grid Root { get; } = new() { ColumnSpacing = 8 };
        private readonly Grid _track = new() { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _fill;
        private readonly TextBlock _reading;

        public Gauge(string label, Color color, Brush secondary)
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            Root.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center });
            _track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(38, 128, 128, 128)) });
            _fill = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _track.Children.Add(_fill);
            Grid.SetColumn(_track, 1);
            Root.Children.Add(_track);
            _reading = new TextBlock
            {
                FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Medium, FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Foreground = secondary, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_reading, 2);
            Root.Children.Add(_reading);
        }

        public void Set(float value, string reading, bool shown)
        {
            Root.Opacity = shown ? 1 : 0;
            _fill.Width = Math.Clamp(value, 0, 1) * _track.ActualWidth;
            _reading.Text = reading;
        }
    }
}
