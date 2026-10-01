using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// System statistics, after the macOS Console's StatsView: device facts and the
/// output counters, then buffer health (S/PDIF DMA starvation and a 15 s trace
/// per buffer), then the optional inputs and interfaces in a third column of
/// their own, shown only when the device reports them. The 2 s figures rebuild
/// the columns they live in; buffer samples update their rows in place.
/// </summary>
public sealed class StatsWindow : Window
{
    private static readonly Color Green = Color.FromArgb(255, 48, 209, 88);
    private static readonly Color Yellow = Color.FromArgb(255, 255, 214, 10);
    private static readonly Color Orange = Color.FromArgb(255, 255, 159, 10);
    private static readonly Color Red = Color.FromArgb(255, 255, 69, 58);
    private static readonly Color Gray = Color.FromArgb(255, 142, 142, 147);

    private readonly MainViewModel _main;
    private readonly StatsViewModel _vm;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private readonly Grid _columns = new();
    private readonly StackPanel _left = new() { Spacing = 12, Padding = new Thickness(16) };
    private readonly StackPanel _middle = new() { Spacing = 12, Padding = new Thickness(16) };
    private readonly StackPanel _right = new() { Spacing = 12, Padding = new Thickness(16) };
    private readonly StackPanel _starvation = new() { Spacing = 6 };
    private readonly StackPanel _bufferRows = new() { Spacing = 6 };
    private readonly Ellipse _streamingDot = Dot(Gray), _pdmDot = Dot(Gray), _footerDot = Dot(Gray);
    private readonly TextBlock _footerText = new() { FontSize = 10 };
    private readonly List<BufferRow> _rows = new();
    private readonly Dictionary<Color, SolidColorBrush> _brushes = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextBlock? _sinceLast;
    private string _bufferLayout = "";

    public StatsWindow(MainViewModel main)
    {
        _main = main;
        _vm = new StatsViewModel(main.Device);
        ToolWindowChrome.Apply(this, "System Statistics", 980, 620);
        _footerText.Foreground = _secondary;

        var root = new Grid { Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new ScrollViewer { Content = _columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var rule = Rule();
        Grid.SetRow(rule, 1);
        root.Children.Add(rule);
        var footer = new Grid { Padding = new Thickness(16, 8, 16, 8) };
        footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _footerDot, _footerText } });
        footer.Children.Add(new TextBlock { Text = "Updated every 2 seconds", FontSize = 10, Foreground = _secondary, HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;

        _vm.Changed += Refresh;
        _vm.BufferChanged += RefreshBuffers;
        // "Time since last event" counts up between the 2 s polls.
        _clock.Tick += (_, _) => UpdateSince();
        _clock.Start();
        Closed += (_, _) =>
        {
            _clock.Stop();
            _vm.Changed -= Refresh;
            _vm.BufferChanged -= RefreshBuffers;
            _vm.Dispose();
            foreach (var r in _rows) r.Trace.Dispose();
        };
        Refresh();
    }

    private static Ellipse Dot(Color c) => new() { Width = 6, Height = 6, Fill = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center };
    private static Border Rule() => new() { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = _secondary,
    };

    private static Grid TitleRow(FrameworkElement left, FrameworkElement right)
    {
        var g = new Grid { Padding = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(left);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    private FrameworkElement Info(string title, string value) => TitleRow(
        new TextBlock { Text = title, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center },
        new TextBlock { Text = value, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontFamily = new FontFamily("Cascadia Code, Consolas") });

    /// <summary>A row whose value carries a coloured state dot.</summary>
    private FrameworkElement State(string title, Color color, string value) => TitleRow(
        new TextBlock { Text = title, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center },
        new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { Dot(color), new TextBlock { Text = value, FontSize = 11, Foreground = _secondary } },
        });

    /// <summary>An overrun / underrun pair, coloured once either is nonzero.</summary>
    private FrameworkElement Counter(string title, string subtitle, uint? over, uint? under)
    {
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        titles.Children.Add(new TextBlock { Text = subtitle, FontSize = 9, Foreground = _secondary });
        var values = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        FrameworkElement Figure(uint v, string label, Color warn) => new StackPanel
        {
            Spacing = 1,
            Children =
            {
                new TextBlock
                {
                    Text = v.ToString(), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    FontFamily = new FontFamily("Cascadia Code, Consolas"), HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = v > 0 ? new SolidColorBrush(warn) : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                },
                new TextBlock { Text = label, FontSize = 8, Foreground = _secondary, HorizontalAlignment = HorizontalAlignment.Right },
            },
        };
        if (over is { } o) values.Children.Add(Figure(o, "over", Orange));
        if (under is { } u) values.Children.Add(Figure(u, "under", Red));
        return TitleRow(titles, values);
    }

    private StackPanel Section(string title, params FrameworkElement[] rows)
    {
        var s = new StackPanel { Spacing = 12 };
        s.Children.Add(SectionLabel(title));
        var body = new StackPanel { Spacing = 6 };
        foreach (var r in rows) body.Children.Add(r);
        s.Children.Add(body);
        return s;
    }

    // ── The 2 s figures ──

    private void Refresh()
    {
        _footerDot.Fill = new SolidColorBrush(_vm.IsConnected ? Green : Red);
        _footerText.Text = _vm.IsConnected ? "Connected" : "Disconnected";

        _left.Children.Clear();
        _left.Children.Add(Section("DEVICE INFORMATION",
            Info("Platform", _vm.Platform), Info("Firmware", _vm.Firmware), Info("Serial", _vm.Serial),
            Info("Reconnects", _vm.ReconnectCount.ToString())));
        _left.Children.Add(Rule());
        _left.Children.Add(Section("SYSTEM INFORMATION",
            Info("Clock Frequency", $"{_vm.ClockHz / 1_000_000.0:0.0} MHz"),
            Info("Core Voltage", $"{_vm.CoreMillivolts / 1000.0:0.00} V"),
            Info("Sample Rate", $"{_vm.SampleRateHz / 1000.0:0.0} kHz"),
            Info("Temperature", $"{_vm.TempCentiC / 100.0:0.0} °C")));
        _left.Children.Add(Rule());
        _left.Children.Add(Section("AUDIO OUTPUT",
            Counter("USB Ring", "ISR → Main Loop", _vm.UsbRingOverruns, null),
            Counter("Buffer Pool", "USB → DMA", _vm.SpdifOverruns, _vm.SpdifUnderruns)));
        _left.Children.Add(Rule());
        _left.Children.Add(Section("PDM (SUBWOOFER)",
            Counter("Ring Buffer", "Core 0 → Core 1", _vm.PdmRingOverruns, _vm.PdmRingUnderruns),
            Counter("DMA Buffer", "Core 1 → PIO", _vm.PdmDmaOverruns, _vm.PdmDmaUnderruns)));

        RefreshStarvation();
        if (_middle.Children.Count == 0)
        {
            _middle.Children.Add(_starvation);
            _middle.Children.Add(Rule());
            var reset = new Button { Content = "Reset Watermarks", FontSize = 11, Padding = new Thickness(8, 2, 8, 3), MinHeight = 0 };
            reset.Click += (_, _) => _vm.ResetWatermarks();
            var flags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            flags.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _streamingDot, new TextBlock { Text = "Audio Streaming", FontSize = 10, Foreground = _secondary } } });
            flags.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _pdmDot, new TextBlock { Text = "PDM Active", FontSize = 10, Foreground = _secondary } } });
            var buffers = new StackPanel { Spacing = 12 };
            buffers.Children.Add(TitleRow(SectionLabel("BUFFER FILL LEVELS"), reset));
            buffers.Children.Add(flags);
            buffers.Children.Add(_bufferRows);
            _middle.Children.Add(buffers);
        }

        RefreshInterfaces();
        LayoutColumns();
    }

    private void RefreshStarvation()
    {
        _starvation.Children.Clear();
        _starvation.Children.Add(SectionLabel("SPDIF DMA STARVATION"));
        var totalValues = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (_vm.StarvationDelta > 0)
            totalValues.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(38, Red.R, Red.G, Red.B)), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = $"+{_vm.StarvationDelta}", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Red), FontFamily = new FontFamily("Cascadia Code, Consolas") },
            });
        totalValues.Children.Add(new TextBlock
        {
            Text = _vm.StarvationTotal.ToString(), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontFamily = new FontFamily("Cascadia Code, Consolas"),
            Foreground = _vm.StarvationTotal > 0 ? new SolidColorBrush(Red) : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        });
        _starvation.Children.Add(TitleRow(new TextBlock { Text = "Total", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center }, totalValues));
        int instances = Math.Max((int)_vm.Buffers.NumSpdif, 2);
        for (int i = 0; i < instances && i < 4; i++)
        {
            uint n = _vm.StarvationPerInstance[i];
            _starvation.Children.Add(TitleRow(
                new TextBlock { Text = $"Out {i * 2 + 1}/{i * 2 + 2}", FontSize = 11, Foreground = _secondary },
                new TextBlock
                {
                    Text = n.ToString(), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, FontFamily = new FontFamily("Cascadia Code, Consolas"),
                    Foreground = n > 0 ? new SolidColorBrush(Red) : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                }));
        }
        _sinceLast = new TextBlock { FontSize = 10, Foreground = _secondary, FontFamily = new FontFamily("Cascadia Code, Consolas") };
        _starvation.Children.Add(TitleRow(new TextBlock { Text = "Time since last event", FontSize = 10, Foreground = _secondary }, _sinceLast));
        string between = _vm.StarvationLastEvent is { } last && _vm.StarvationPreviousEvent is { } prev ? Elapsed(last - prev) : "-";
        _starvation.Children.Add(TitleRow(new TextBlock { Text = "Time between last two", FontSize = 10, Foreground = _secondary },
            new TextBlock { Text = between, FontSize = 10, Foreground = _secondary, FontFamily = new FontFamily("Cascadia Code, Consolas") }));
        UpdateSince();
    }

    private void UpdateSince()
    {
        if (_sinceLast != null)
            _sinceLast.Text = _vm.StarvationLastEvent is { } t ? Elapsed(DateTime.Now - t) : "-";
    }

    private static string Elapsed(TimeSpan t)
    {
        int s = (int)t.TotalSeconds;
        return s >= 3600 ? $"{s / 3600}h {s % 3600 / 60:00}m {s % 60:00}s" : $"{s / 60}m {s % 60:00}s";
    }

    private void RefreshInterfaces()
    {
        _right.Children.Clear();
        void Add(StackPanel section)
        {
            if (_right.Children.Count > 0) _right.Children.Add(Rule());
            _right.Children.Add(section);
        }

        if (_vm.InputSourceSupported)
        {
            var rx = _vm.SpdifRx;
            bool locked = rx.State == SpdifInputState.Locked;
            var (stateText, stateColor) = rx.State switch
            {
                SpdifInputState.Inactive => ("Inactive", Gray),
                SpdifInputState.Acquiring => ("Acquiring", Yellow),
                SpdifInputState.Locked => ("Locked", Green),
                SpdifInputState.Relocking => ("Relocking", Orange),
                _ => ($"Unknown ({(byte)rx.State})", Gray),
            };
            var rows = new List<FrameworkElement>
            {
                State("State", stateColor, stateText),
                Info("Active Source", rx.ActiveSource switch
                {
                    InputSource.Spdif => "S/PDIF 1", InputSource.Spdif2 => "S/PDIF 2", InputSource.Spdif3 => "S/PDIF 3",
                    InputSource.Spdif4 => "S/PDIF 4", InputSource.I2s => "I2S", _ => "USB",
                }),
                Info("Sample Rate", locked && rx.SampleRate > 0 ? $"{rx.SampleRate / 1000.0:0.0} kHz" : "-"),
                Info("Lock Count", rx.LockCount.ToString()),
                Info("Loss Count", rx.LossCount.ToString()),
                Info("Parity Errors", locked ? rx.ParityErrors.ToString() : "-"),
                Info("FIFO Fill", locked ? $"{rx.FifoFillPct}%" : "-"),
                Info("RX Pin", $"GPIO {_vm.SpdifActivePin}"),
            };
            // IEC 60958 channel status, only when locked.
            if (locked && _vm.SpdifChannelStatus is { Length: >= 24 } cs)
            {
                rows.Add(Rule());
                rows.Add(SectionLabel("CHANNEL STATUS"));
                rows.Add(Info("Format", (cs[0] & 0x01) == 0 ? "Consumer" : "Professional"));
                rows.Add(Info("Audio", (cs[0] & 0x02) == 0 ? "PCM" : "Non-PCM"));
                rows.Add(Info("Category", cs[1] switch
                {
                    0x00 => "General", 0x01 => "CD Player", 0x02 => "DAT", 0x03 => "DCC", 0x04 => "MiniDisc",
                    0x06 => "Synthesizer", 0x08 => "Broadcast Receiver", 0x09 => "Musical Instrument",
                    0x0A => "A/D Converter", 0x0C => "Mixer", 0x0D => "Rate Converter", 0x0E => "Sampler",
                    0x0F => "Digital Signal Processor", var c => $"0x{c:X2}",
                }));
                rows.Add(Info("Word Length", (cs[4] & 0x0F) switch
                {
                    0x00 => "Not indicated", 0x02 => "16-bit", 0x04 => "20-bit", 0x08 => "17-bit", 0x0A => "22-bit", 0x0B => "24-bit", _ => "-",
                }));
                rows.Add(Info("Copy", (cs[0] & 0x04) != 0 ? "Permitted" : "Prohibited"));
            }
            if (rx.State != SpdifInputState.Inactive)
            {
                rows.Add(Rule());
                rows.Add(SectionLabel("DEBUG"));
                rows.Add(Info("Library State", rx.LibState switch { 0 => "No Signal", 1 => "Waiting Stable", 2 => "Stable", var l => $"Unknown ({l})" }));
                rows.Add(Info("Stable Callbacks", ((rx.CallbackCounts >> 4) & 0x0F).ToString()));
                rows.Add(Info("Lost Callbacks", (rx.CallbackCounts & 0x0F).ToString()));
            }
            Add(Section("S/PDIF INPUT", rows.ToArray()));
        }

        if (_vm.LgSoundSync is { } lg)
            Add(Section("LG SOUND SYNC",
                Info("Enabled", lg.Enabled ? "Yes" : "No"),
                State("Present", lg.Present ? Green : Gray, lg.Present ? "Yes" : "No"),
                Info("TV Volume", lg.Volume == 0xFF ? "-" : $"{lg.Volume} / 100"),
                Info("TV Mute", lg.Muted ? "On" : "Off")));

        if (_vm.Adat is { } adat)
        {
            var (text, color) = !adat.Enabled ? ("Disabled", Gray)
                : adat.Active ? ("Streaming", Green)
                : !adat.RateOk ? ("Suspended (rate above 48 kHz)", Orange)
                : ("Enabled", Yellow);
            Add(Section("ADAT BULK OUTPUT",
                State("State", color, text),
                Info("Streaming", adat.Active ? "Yes" : "No"),
                Info("Rate Supported", adat.RateOk ? "Yes" : "No"),
                Info("Data Pin", $"GPIO {adat.Pin}"),
                Info("Resync Count", adat.ResyncCount.ToString()),
                // Should stay 0: nonzero flags a stalled main loop or a DMA fault.
                Info("Slip Count", adat.SlipCount.ToString())));
        }

        // Shown only while the device is the I2S clock slave.
        if (_vm.I2sSlave is { IsSlave: true } i2s)
        {
            Color color = i2s.State switch
            {
                I2sSlaveState.Acquiring => Yellow, I2sSlaveState.Relocking => Orange, I2sSlaveState.Locked => Green, _ => Gray,
            };
            Add(Section("I2S INPUT (SLAVE CLOCK)",
                State("State", color, i2s.StateText),
                Info("Detected Rate", i2s.DetectedRateText),
                // Nonzero but never locked: an unsupported rate or a wrong BCK ratio.
                Info("Measured Rate", i2s.MeasuredHz > 0 ? $"{i2s.MeasuredHz:N0} Hz" : "-"),
                Info("Lock Count", i2s.LockCount.ToString()),
                Info("Loss Count", i2s.LossCount.ToString())));
        }
    }

    /// <summary>Two columns, or three when the device reports any optional
    /// input or interface, so those never pile up under a fixed column.</summary>
    private void LayoutColumns()
    {
        bool third = _right.Children.Count > 0;
        int wanted = third ? 5 : 3;
        if (_columns.ColumnDefinitions.Count == wanted) return;
        _columns.Children.Clear();
        _columns.ColumnDefinitions.Clear();
        void Column(FrameworkElement e, bool divider)
        {
            if (divider)
            {
                _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                var d = new Border { Width = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };
                Grid.SetColumn(d, _columns.ColumnDefinitions.Count - 1);
                _columns.Children.Add(d);
            }
            _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(e, _columns.ColumnDefinitions.Count - 1);
            _columns.Children.Add(e);
        }
        Column(_left, false);
        Column(_middle, true);
        if (third) Column(_right, true);
    }

    // ── The 60 ms buffer rows ──

    private sealed record BufferRow(Grid Root, TextBlock Range, TextBlock Fill, BufferFillTrace Trace, Func<BufferStatsPacket, (byte Fill, byte Min, byte Max)> Read, Func<byte, Color> Threshold);

    /// <summary>The rows follow the active buffers; their values and traces
    /// update in place on every sample.</summary>
    /// <summary>One brush per colour, for the values updated every 60 ms.</summary>
    private SolidColorBrush Brush(Color c)
    {
        if (!_brushes.TryGetValue(c, out var brush)) _brushes[c] = brush = new SolidColorBrush(c);
        return brush;
    }

    private void RefreshBuffers()
    {
        var b = _vm.Buffers;
        _streamingDot.Fill = Brush(b.IsAudioStreaming ? Green : Gray);
        _pdmDot.Fill = Brush(b.IsPdmActive ? Green : Gray);

        string layout = $"{b.NumSpdif}|{b.IsPdmActive}|{string.Join(",", Enumerable.Range(0, 4).Select(i => _main.GetOutputSlotType(i)))}";
        if (layout != _bufferLayout)
        {
            _bufferLayout = layout;
            foreach (var r in _rows) r.Trace.Dispose();
            _rows.Clear();
            _bufferRows.Children.Clear();
            var outputs = _main.ActiveOutputs;
            Color OutputColor(int o) => o < outputs.Count ? outputs[o].Color : Gray;
            for (int i = 0; i < Math.Min((int)b.NumSpdif, 4); i++)
            {
                int slot = i;
                string kind = _main.GetOutputSlotType(i) == OutputSlotType.I2S ? "I2S" : "SPDIF";
                AddRow($"Out {i * 2 + 1}/{i * 2 + 2} ({kind})", OutputColor(i * 2), false, slot,
                    p => (p.Spdif[slot].ConsumerFillPct, p.Spdif[slot].ConsumerMinFillPct, p.Spdif[slot].ConsumerMaxFillPct), SpdifThreshold);
            }
            if (b.IsPdmActive)
            {
                var pdm = Channel.Pdm.Color;
                AddRow("PDM DMA", pdm, false, BufferFillHistory.PdmDmaSeries,
                    p => (p.Pdm.DmaFillPct, p.Pdm.DmaMinFillPct, p.Pdm.DmaMaxFillPct), PdmDmaThreshold);
                AddRow("PDM Ring", pdm, true, BufferFillHistory.PdmRingSeries,
                    p => (p.Pdm.RingFillPct, p.Pdm.RingMinFillPct, p.Pdm.RingMaxFillPct), PdmRingThreshold);
            }
        }
        foreach (var row in _rows)
        {
            var (fill, min, max) = row.Read(b);
            row.Range.Text = $"{min}–{max}%";
            row.Fill.Text = $"{fill}%";
            row.Fill.Foreground = Brush(row.Threshold(fill));
            row.Trace.Update(min, max);
        }
    }

    private void AddRow(string title, Color color, bool hollow, int series,
                        Func<BufferStatsPacket, (byte, byte, byte)> read, Func<byte, Color> threshold)
    {
        var trace = new BufferFillTrace(_vm.BufferHistory, series, color);
        var root = new Grid { Height = 48, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)) };
        ToolTipService.SetToolTip(root, $"{title} fill level over the last 15 seconds");
        root.Children.Add(trace);
        var header = new Grid { Padding = new Thickness(6, 3, 6, 0), VerticalAlignment = VerticalAlignment.Top, ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        // Hollow marks the PDM ring buffer, whose trace is dashed.
        header.Children.Add(new Ellipse
        {
            Width = 7, Height = 7, StrokeThickness = 1.5, Stroke = new SolidColorBrush(color),
            Fill = hollow ? null : new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center,
        });
        var name = new TextBlock { Text = title, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        var range = new TextBlock { FontSize = 9, Foreground = _secondary, FontFamily = new FontFamily("Cascadia Code, Consolas"), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(range, "Lowest and highest fill since the watermarks were reset");
        Grid.SetColumn(range, 2);
        header.Children.Add(range);
        var fill = new TextBlock { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontFamily = new FontFamily("Cascadia Code, Consolas"), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(fill, 3);
        header.Children.Add(fill);
        root.Children.Add(header);
        _bufferRows.Children.Add(root);
        _rows.Add(new BufferRow(root, range, fill, trace, read, threshold));
    }

    private static Color SpdifThreshold(byte v) => v is 0 or 100 ? Red : v < 25 || v > 75 ? Yellow : Green;
    private static Color PdmDmaThreshold(byte v) => v > 50 ? Red : v < 5 || v > 30 ? Yellow : Green;
    private static Color PdmRingThreshold(byte v) => v > 50 ? Red : v > 20 ? Yellow : Green;
}
