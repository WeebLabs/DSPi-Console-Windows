using System.ComponentModel;
using DSPiConsole.Controls;
using DSPiConsole.Controls.Rta;
using DSPiConsole.Core.Rta;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// A larger, separate view of the spectrum the main window has asked for. The
/// main window decides what the engine does (the tap, the channels, whether
/// there is a spectrum at all); this window always registers for the open
/// page's selection, so it can never change that. Within that data it draws
/// curves, bars or both, with any channel hidden here only. The engine options
/// live in Settings. Port of the macOS Console's SpectrumAnalyserView.
/// </summary>
public sealed class SpectrumAnalyserWindow : Window
{
    private enum Mode { Curves, Bars, Both }

    private readonly MainViewModel _vm;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private readonly ContentControl _bodyHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly StackPanel _headerLeft = new() { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly SegmentedPicker _modePicker = new(new[] { "Curves", "Bars", "Both" }, height: 22, fontSize: 11) { Width = 180, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _statusLeft = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    private readonly TextBlock _statusRight = new() { FontSize = 10, FontFamily = new FontFamily("Cascadia Code, Consolas"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _statusBar = new() { Padding = new Thickness(12, 6, 12, 6) };

    private Mode _mode;
    /// <summary>Channels hidden in this window only, keyed by tap and channel.</summary>
    private readonly HashSet<(byte Tap, int Channel)> _hidden = new();
    private bool _visible = true, _minimized;
    private string _builtKey = "";
    private Guid? _barsToken;
    private RtaRequest? _barsRequest;

    // Body parts, rebuilt with the body.
    private GraphSpectrumOverlay? _overlay;
    private CanvasControl? _grid;
    private readonly List<RtaBandsView> _bandViews = new();

    public SpectrumAnalyserWindow(MainViewModel vm)
    {
        _vm = vm;
        ToolWindowChrome.Apply(this, "Spectrum Analyser", 760, 560);

        var root = new Grid { Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Height = 36, Padding = new Thickness(12, 0, 12, 0), ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new ScrollViewer
        {
            Content = _headerLeft,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(_modePicker, 1);
        header.Children.Add(_modePicker);
        ToolTipService.SetToolTip(_modePicker, "How this window draws the page's spectrum. It does not change the main window.");
        root.Children.Add(header);
        AddRow(root, Rule(), 1);
        AddRow(root, _bodyHost, 2);
        AddRow(root, Rule(), 3);
        var status = new Grid();
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.Children.Add(_statusLeft);
        Grid.SetColumn(_statusRight, 1);
        status.Children.Add(_statusRight);
        _statusBar.Child = status;
        AddRow(root, _statusBar, 4);
        Content = root;

        _modePicker.Picked += i =>
        {
            _mode = (Mode)i;
            Rebuild(force: true);
        };

        _vm.RtaSelectionChanged += OnChanged;
        _vm.RtaStateChanged += OnChanged;
        _vm.RtaTelemetryChanged += OnTelemetry;
        _vm.ActiveOutputsChanged += OnChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        AppSettings.Instance.SettingsChanged += OnChanged;
        VisibilityChanged += (_, e) => { _visible = e.Visible; ActiveChanged(); };
        AppWindow.Changed += (s, e) =>
        {
            if (!e.DidPresenterChange && !e.DidSizeChange) return;
            bool minimized = s.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            if (minimized != _minimized) { _minimized = minimized; ActiveChanged(); }
        };
        Closed += (_, _) =>
        {
            _vm.RtaSelectionChanged -= OnChanged;
            _vm.RtaStateChanged -= OnChanged;
            _vm.RtaTelemetryChanged -= OnTelemetry;
            _vm.ActiveOutputsChanged -= OnChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            AppSettings.Instance.SettingsChanged -= OnChanged;
            _visible = false;
            ReleaseBars();
            DisposeBody();
        };

        StartFromPage();
        Rebuild(force: true);
    }

    private static void AddRow(Grid grid, FrameworkElement e, int row)
    {
        Grid.SetRow(e, row);
        grid.Children.Add(e);
    }

    private static Border Rule() => new() { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };

    /// <summary>Opening the window starts it drawing what the page draws.</summary>
    public void StartFromPage()
    {
        var s = AppSettings.Instance;
        bool graph = s.RtaShows(bars: false, _vm.RtaOnDashboard), bars = s.RtaShows(bars: true, _vm.RtaOnDashboard);
        _mode = graph && bars ? Mode.Both : bars ? Mode.Bars : Mode.Curves;
        _modePicker.Selected = (int)_mode;
        Rebuild(force: true);
    }

    private bool Active => _visible && !_minimized;

    private void ActiveChanged()
    {
        if (_overlay != null) _overlay.Active = Active;
        foreach (var v in _bandViews) v.Active = Active;
        SyncBarsSubscription();
    }

    private void OnChanged(object? sender, EventArgs e) => Rebuild(force: false);

    private void OnTelemetry(object? sender, EventArgs e) => RefreshStatus();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsDeviceConnected) or nameof(MainViewModel.ActiveInputSource)
            or nameof(MainViewModel.UsbInputChannelCount) or nameof(MainViewModel.Platform))
            DispatcherQueue.TryEnqueue(() => Rebuild(force: false));
    }

    // ── Body ──

    /// <summary>Rebuilds the header and body when what they lay out changed,
    /// and refreshes the status line either way. Frames never come through
    /// here: the views read them from the engine as they draw.</summary>
    private void Rebuild(bool force)
    {
        var selection = _vm.RtaSelection;
        bool ready = _vm.IsDeviceConnected && _vm.Rta.Supported;
        // With nothing shown on the page, the main window asks the engine for
        // nothing, so there is no data to present here.
        bool available = ready && _vm.RtaPageShowsSpectrum && !selection.IsEmpty;
        // A channel that leaves the selection forgets it was hidden.
        _hidden.RemoveWhere(k => !selection.Contains(k.Tap, k.Channel));
        var visible = selection.Channels.Where(c => !_hidden.Contains((selection.Tap, c))).ToArray();
        int columns = Math.Clamp(AppSettings.Instance.RtaBarColumns, 1, 4);
        string key = $"{ready}|{available}|{_vm.RtaPageTitle}|{selection.StorageKey}|{string.Join(",", visible)}|{_mode}|{columns}"
                     + $"|{string.Join(",", selection.Channels.Select(c => _vm.RtaChannelName(selection.Tap, c)))}";
        if (force || key != _builtKey)
        {
            _builtKey = key;
            DisposeBody();
            BuildHeader(selection, ready, available);
            _modePicker.IsEnabled = available;
            _bodyHost.Content = !ready ? UnavailableNotice()
                : !available ? HiddenNotice(selection)
                : visible.Length == 0 ? AllHiddenNotice()
                : BuildDisplay(selection, visible, columns);
            _statusBar.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        }
        _grid?.Invalidate();
        SyncBarsSubscription();
        RefreshStatus();
    }

    private void DisposeBody()
    {
        _overlay?.Dispose();
        _overlay = null;
        _grid?.RemoveFromVisualTree();
        _grid = null;
        foreach (var v in _bandViews) v.Dispose();
        _bandViews.Clear();
    }

    /// <summary>The page being mirrored, its channels as show/hide toggles
    /// (which are also the curves' legend).</summary>
    private void BuildHeader(RtaChannelSelection selection, bool ready, bool available)
    {
        _headerLeft.Children.Clear();
        if (!ready) return;
        _headerLeft.Children.Add(new TextBlock { Text = _vm.RtaPageTitle, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (!available) return;
        _headerLeft.Children.Add(new TextBlock { Text = selection.Tap == RtaWire.TapInput ? "Inputs" : "Outputs", FontSize = 11, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center });
        _headerLeft.Children.Add(new Border { Width = 1, Height = 12, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], VerticalAlignment = VerticalAlignment.Center });
        foreach (int ch in selection.Channels) _headerLeft.Children.Add(ChannelToggle(selection.Tap, ch));
    }

    private FrameworkElement ChannelToggle(byte tap, int ch)
    {
        bool shown = !_hidden.Contains((tap, ch));
        var color = _vm.RtaChannelColor(tap, ch);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(RtaBandsView.With(color, shown ? 1 : 0.25)), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock
        {
            Text = _vm.RtaChannelName(tap, ch),
            FontSize = 10,
            Foreground = _secondary,
            Opacity = shown ? 1 : 0.4,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var button = new Button
        {
            Content = row,
            Padding = new Thickness(4, 2, 4, 2),
            MinHeight = 0,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, shown ? "Hide in this window" : "Show in this window");
        button.Click += (_, _) =>
        {
            if (!_hidden.Remove((tap, ch))) _hidden.Add((tap, ch));
            Rebuild(force: true);
        };
        return button;
    }

    private FrameworkElement BuildDisplay(RtaChannelSelection selection, int[] visible, int columns)
    {
        var body = new Grid
        {
            Padding = new Thickness(12),
            RowSpacing = 12,
            Background = new SolidColorBrush(Color.FromArgb(51, 0, 0, 0)),
        };
        if (_mode != Mode.Bars)
        {
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            AddRow(body, Curves(selection), body.RowDefinitions.Count - 1);
        }
        if (_mode != Mode.Curves)
        {
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            AddRow(body, Bars(selection, visible, columns), body.RowDefinitions.Count - 1);
        }
        return body;
    }

    /// <summary>The response graph's spectrum without the filter curves, over
    /// the analyser's dB grid, across the graph's frequency range.</summary>
    private FrameworkElement Curves(RtaChannelSelection selection)
    {
        var s = AppSettings.Instance;
        double minHz = s.GraphMinFrequency, maxHz = s.GraphMaxFrequency;
        var host = new Grid();
        _grid = new CanvasControl { IsHitTestVisible = false };
        _grid.Draw += (c, e) =>
        {
            float w = (float)c.ActualWidth, h = (float)c.ActualHeight;
            if (w <= 4 || h <= 16) return;
            RtaBandsView.DrawLogGrid(e.DrawingSession, new RtaPlot(0, 0, w, h - 12), _vm.RtaScale,
                AppSettings.Instance.GraphMinFrequency, AppSettings.Instance.GraphMaxFrequency, h - 11);
        };
        host.Children.Add(_grid);
        // The grid keeps a 12 px label row; the overlay's plot stops above it
        // so both map the same dB scale to the same height.
        _overlay = new GraphSpectrumOverlay(_vm, followsGraphPreference: false)
        {
            Margin = new Thickness(0, 0, 0, 12),
            Active = Active,
            HiddenChannels = new HashSet<int>(selection.Channels.Where(c => _hidden.Contains((selection.Tap, c)))),
        };
        _overlay.SetAxis(minHz, maxHz);
        host.Children.Add(_overlay);
        return host;
    }

    /// <summary>The visible channels' bars in the strip's column layout, grown
    /// to fill the window. The window subscribes for the whole selection.</summary>
    private FrameworkElement Bars(RtaChannelSelection selection, int[] visible, int maxColumns)
    {
        int columns = Math.Min(Math.Max(visible.Length, 1), maxColumns);
        int rows = (visible.Length + columns - 1) / columns;
        var grid = new Grid { RowSpacing = 12, ColumnSpacing = 12 };
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < visible.Length; i++)
        {
            int ch = visible[i];
            var color = _vm.RtaChannelColor(selection.Tap, ch);
            var cell = new Grid { RowSpacing = 3 };
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            title.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = _vm.RtaChannelName(selection.Tap, ch), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = _secondary });
            cell.Children.Add(title);
            var view = new RtaBandsView(_vm, selection.Tap, ch, color) { Active = Active };
            _bandViews.Add(view);
            AddRow(cell, view, 1);
            Grid.SetRow(cell, i / columns);
            Grid.SetColumn(cell, i % columns);
            grid.Children.Add(cell);
        }
        _barsRequest = new RtaRequest(selection.Tap, selection.Mask);
        return grid;
    }

    /// <summary>The bars watch the engine while they are on screen; the curves'
    /// overlay keeps its own subscription.</summary>
    private void SyncBarsSubscription()
    {
        var request = _bandViews.Count > 0 && Active ? _barsRequest : null;
        if (request is { } r)
        {
            if (_barsToken is { } t) _vm.Rta.Update(t, r);
            else
            {
                _vm.Rta.SetOptions(MainViewModel.RtaOptionsFromSettings);
                _barsToken = _vm.Rta.Subscribe(r);
            }
        }
        else ReleaseBars();
    }

    private void ReleaseBars()
    {
        if (_barsToken is { } t) _vm.Rta.Release(t);
        _barsToken = null;
    }

    // ── Notices ──

    private FrameworkElement Notice(string glyph, string title, string detail)
    {
        var panel = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(40), MaxWidth = 440 };
        if (glyph.Length > 0) panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 28, Foreground = _secondary });
        panel.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = _secondary, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        return panel;
    }

    private FrameworkElement UnavailableNotice() => Notice("", "Spectrum analyser unavailable",
        _vm.IsDeviceConnected
            ? "The connected firmware does not provide a compatible analyser. Update the firmware to use it."
            : "Connect a DSPi to use the analyser.");

    private FrameworkElement HiddenNotice(RtaChannelSelection selection) => Notice("",
        selection.IsEmpty ? "No channels selected" : "Spectrum switched off",
        $"This window shows the spectrum of the {(_vm.RtaOnDashboard ? "dashboard" : "open channel page")}. Choose channels and switch the spectrum on from the gear on the response graph.");

    private FrameworkElement AllHiddenNotice() => Notice("", "Every channel is hidden in this window", "Click a channel name above to show it again.");

    // ── Status ──

    private TextBlock StatusText(string text, string? tip = null)
    {
        var t = new TextBlock { Text = text, FontSize = 10, FontFamily = new FontFamily("Cascadia Code, Consolas"), Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center };
        if (tip != null) ToolTipService.SetToolTip(t, tip);
        return t;
    }

    private void RefreshStatus()
    {
        var engine = _vm.Rta;
        var s = engine.Snapshot.Status;
        _statusLeft.Children.Clear();
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        state.Children.Add(new Ellipse
        {
            Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(s.IsRunning ? Color.FromArgb(255, 48, 209, 88) : Color.FromArgb(128, 160, 160, 160)),
        });
        state.Children.Add(StatusText(s.IsRunning ? "Running" : "Idle"));
        _statusLeft.Children.Add(state);
        _statusLeft.Children.Add(StatusText(engine.RefreshDescription));
        if (s.FramesPerSecond > 0) _statusLeft.Children.Add(StatusText($"{s.FramesPerSecond} frames/s"));
        if (s.LastFrameUs > 0) _statusLeft.Children.Add(StatusText($"transform {s.LastFrameUs} µs"));
        // Main-loop time, which the packet-callback CPU figure cannot see.
        if (s.BusyUsPerSecond > 0) _statusLeft.Children.Add(StatusText($"main loop {s.BusyUsPerSecond / 10000.0:0.0}%"));
        if (s.BassBusyUsPerSecond > 0)
            _statusLeft.Children.Add(StatusText(s.BassLoadDescription,
                "Bass processing time summed across both cores. Already included in the audio CPU meters; ≥ means the counter is saturated."));

        if (engine.ConfigRejected)
        {
            _statusRight.Text = "⚠ Device refused this configuration";
            _statusRight.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 159, 10));
            ToolTipService.SetToolTip(_statusRight, null);
        }
        else if (engine.Caps.DynamicRangeDb > 0)
        {
            _statusRight.Text = $"{engine.Caps.DynamicRangeDb}/{engine.Caps.BassDynamicRangeDb} dB range";
            _statusRight.Foreground = _secondary;
            ToolTipService.SetToolTip(_statusRight, "FFT / bass usable dynamic range. Bass bands use overlapping filters calibrated for tones.");
        }
        else _statusRight.Text = "";
    }
}
