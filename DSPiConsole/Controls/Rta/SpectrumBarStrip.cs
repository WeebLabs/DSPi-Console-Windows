using System.ComponentModel;
using DSPiConsole.Core.Rta;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole.Controls.Rta;

/// <summary>
/// Third-octave bars for the open page's channels, between the response graph
/// and the dashboard or the channel page, when the page has RTA Bars switched
/// on. One card with a cell per channel, side by side up to the chosen column
/// count and then wrapping. Each cell names its channel; a gear in the card's
/// corner (shown on hover) chooses the columns and opens the analyser window;
/// the card's bottom edge drags its height. Port of the macOS Console's
/// SpectrumBarStrip.
/// </summary>
public sealed class SpectrumBarStrip : UserControl
{
    /// <summary>Limits for a single-channel cell's dragged height.</summary>
    private const double MinBarHeight = 64, MaxBarHeight = 240;
    private const double NameRowHeight = 14, GearWidth = 16;

    private readonly MainViewModel _vm;
    private readonly Window _window;
    private readonly Action _openWindow;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private readonly Border _card = new()
    {
        Padding = new Thickness(10),
        CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(1),
        // The dashboard channel cards' fill, so the strip reads as one of them.
        Background = new SolidColorBrush(Color.FromArgb(178, 36, 36, 36)),
    };
    private readonly Grid _cells = new() { ColumnSpacing = 12, RowSpacing = 8 };
    private readonly Button _gear;
    private readonly ResizeGripper _resize = new() { Height = 10, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Colors.Transparent) };
    private readonly List<RtaBandsView> _views = new();
    private readonly List<FrameworkElement> _barHosts = new();

    private string _builtKey = "";
    private bool _loaded, _hovered, _optionsOpen, _single, _hostActive = true;
    private int _rows = 1;
    private Guid? _token;
    private RtaRequest? _request;
    private double _dragStartY, _dragStartHeight;
    private bool _dragging;

    public SpectrumBarStrip(MainViewModel vm, Window window, Action openWindow)
    {
        _vm = vm;
        _window = window;
        _openWindow = openWindow;
        Visibility = Visibility.Collapsed;

        _gear = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 10 },
            Width = GearWidth + 8, Height = NameRowHeight + 6, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        ToolTipService.SetToolTip(_gear, "Spectrum strip options");
        _gear.Click += (_, _) => ShowOptions();

        var root = new Grid();
        _card.Child = _cells;
        root.Children.Add(_card);
        root.Children.Add(_gear);
        root.Children.Add(_resize);
        Content = root;
        ToolTipService.SetToolTip(_resize, "Drag to resize the spectrum bars");

        PointerEntered += (_, _) => { _hovered = true; UpdateGear(); };
        PointerExited += (_, _) => { _hovered = false; UpdateGear(); };
        _resize.PointerPressed += OnResizePressed;
        _resize.PointerMoved += OnResizeMoved;
        _resize.PointerReleased += OnResizeReleased;
        _resize.PointerCaptureLost += (_, _) => EndResize();

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private static AppSettings S => AppSettings.Instance;

    /// <summary>False while the host window is minimised: the bars stop
    /// animating. The subscription stays, as the response graph's does.</summary>
    public bool HostActive
    {
        get => _hostActive;
        set
        {
            if (_hostActive == value) return;
            _hostActive = value;
            foreach (var v in _views) v.Active = value;
        }
    }

    private void Attach()
    {
        if (_loaded) return;
        _loaded = true;
        _vm.RtaSelectionChanged += OnChanged;
        _vm.RtaStateChanged += OnChanged;
        _vm.ActiveOutputsChanged += OnChanged;
        _vm.OutputEnabledChanged += OnOutputEnabledChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        S.SettingsChanged += OnChanged;
        Rebuild(force: true);
    }

    private void Detach()
    {
        if (!_loaded) return;
        _loaded = false;
        _vm.RtaSelectionChanged -= OnChanged;
        _vm.RtaStateChanged -= OnChanged;
        _vm.ActiveOutputsChanged -= OnChanged;
        _vm.OutputEnabledChanged -= OnOutputEnabledChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        S.SettingsChanged -= OnChanged;
        Rebuild(force: true);
    }

    private void OnChanged(object? sender, EventArgs e) => Rebuild(force: false);

    private void OnOutputEnabledChanged(int output, bool enabled) => DispatcherQueue.TryEnqueue(() => Rebuild(force: false));

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsDeviceConnected) or nameof(MainViewModel.ActiveInputSource)
            or nameof(MainViewModel.UsbInputChannelCount) or nameof(MainViewModel.ActiveInputChannelCount))
            DispatcherQueue.TryEnqueue(() => Rebuild(force: false));
    }

    private int ChosenColumns => Math.Clamp(S.RtaBarColumns, 1, 4);

    /// <summary>One cell's bars plus its label row. Several channels keep three
    /// quarters of a lone channel's height, so one drag resizes both.</summary>
    private double CellHeight(bool single)
    {
        double h = Math.Clamp(S.RtaBarHeight, MinBarHeight, MaxBarHeight);
        return single ? h : Math.Round(h * 0.75);
    }

    // ── Layout ──

    /// <summary>Rebuilds the cells when what they lay out changed; a height
    /// change only resizes them, so the bars keep their smoothing.</summary>
    private void Rebuild(bool force)
    {
        var selection = _vm.RtaSelection;
        bool show = _loaded && _vm.IsDeviceConnected && _vm.Rta.Supported && !selection.IsEmpty
                    && S.RtaShows(bars: true, _vm.RtaOnDashboard);
        int columns = Math.Min(Math.Max(selection.Channels.Count, 1), ChosenColumns);
        string key = show
            ? $"{selection.StorageKey}|{columns}|{string.Join(",", selection.Channels.Select(c => _vm.RtaChannelName(selection.Tap, c)))}"
            : "";
        if (force || key != _builtKey)
        {
            _builtKey = key;
            Clear();
            if (show) Build(selection, columns);
        }
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // A strip hidden under the pointer gets no PointerExited.
        if (!show && _hovered) { _hovered = false; UpdateGear(); }
        _request = show ? new RtaRequest(selection.Tap, selection.Mask) : null;
        ApplyHeights();
        foreach (var v in _views) v.Redraw();
        SyncSubscription();
    }

    private void Clear()
    {
        foreach (var v in _views) v.Dispose();
        _views.Clear();
        _barHosts.Clear();
        _cells.Children.Clear();
        _cells.ColumnDefinitions.Clear();
        _cells.RowDefinitions.Clear();
    }

    private void Build(RtaChannelSelection selection, int columns)
    {
        int count = selection.Channels.Count;
        _single = count == 1;
        _rows = (count + columns - 1) / columns;
        for (int c = 0; c < columns; c++) _cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < _rows; r++) _cells.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < count; i++)
        {
            int ch = selection.Channels[i];
            var color = _vm.RtaChannelColor(selection.Tap, ch);
            var cell = new StackPanel { Spacing = 2 };
            // Every name row keeps the gear's slot clear, so names truncate at
            // the same point across a row whether or not the gear is there.
            var name = new Grid { Height = NameRowHeight, ColumnSpacing = 5 };
            name.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            name.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            name.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GearWidth) });
            name.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock
            {
                Text = _vm.RtaChannelName(selection.Tap, ch),
                FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = _secondary, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);
            name.Children.Add(label);
            cell.Children.Add(name);
            // Every cell carries its own frequency axis, since side-by-side
            // cells share no row below them.
            var view = new RtaBandsView(_vm, selection.Tap, ch, color, showLabels: true, showLevelLabels: false) { Active = _hostActive };
            _views.Add(view);
            _barHosts.Add(view);
            cell.Children.Add(view);
            Grid.SetRow(cell, i / columns);
            Grid.SetColumn(cell, i % columns);
            _cells.Children.Add(cell);
        }
        _card.BorderBrush = new SolidColorBrush(_single
            ? RtaBandsView.With(_vm.RtaChannelColor(selection.Tap, selection.Channels[0]), 0.3)
            : Color.FromArgb(51, 128, 128, 128));
    }

    private void ApplyHeights()
    {
        double h = CellHeight(_single);
        foreach (var host in _barHosts) host.Height = h;
    }

    private void SyncSubscription()
    {
        if (_request is { } r && _loaded)
        {
            if (_token is { } t) _vm.Rta.Update(t, r);
            else
            {
                _vm.Rta.SetOptions(MainViewModel.RtaOptionsFromSettings);
                _token = _vm.Rta.Subscribe(r);
            }
        }
        else if (_token is { } t)
        {
            _vm.Rta.Release(t);
            _token = null;
        }
    }

    // ── Gear ──

    /// <summary>The gear shows only on hover, like the graph's, and holds
    /// while its options are open.</summary>
    private void UpdateGear()
    {
        bool shown = _hovered || _optionsOpen;
        _gear.Opacity = shown ? (_optionsOpen ? 0.95 : 0.7) : 0;
        _gear.IsHitTestVisible = shown;
    }

    private void ShowOptions()
    {
        var flyout = new Flyout { FlyoutPresenterStyle = MainWindow.GraphOptionsPresenterStyle() };
        var panel = new StackPanel { Width = 200 };
        // Layout means nothing with a single channel.
        if (!_single)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "LAYOUT", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = _secondary, Margin = new Thickness(12, 12, 12, 8),
            });
            var tiles = new Grid { ColumnSpacing = 6, Margin = new Thickness(12, 0, 12, 12) };
            for (int n = 1; n <= 4; n++)
            {
                tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var tile = LayoutTile(n, flyout);
                Grid.SetColumn(tile, n - 1);
                tiles.Children.Add(tile);
            }
            panel.Children.Add(tiles);
            panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });
        }
        var open = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new FontIcon { Glyph = "", FontSize = 12 }, new TextBlock { Text = "Open in Window", FontSize = 12 } },
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(4, 6, 4, 6),
            MinHeight = 0,
        };
        open.Click += (_, _) =>
        {
            flyout.Hide();
            _openWindow();
        };
        panel.Children.Add(open);
        flyout.Content = panel;
        flyout.Opened += (_, _) => { _optionsOpen = true; UpdateGear(); };
        flyout.Closed += (_, _) => { _optionsOpen = false; UpdateGear(); };
        GraphOptionsPlacement.ShowAt(flyout, _gear, _window, panelWidth: 200);
    }

    private Button LayoutTile(int n, Flyout flyout)
    {
        bool on = ChosenColumns == n;
        var accent = (Color)Application.Current.Resources["SystemAccentColor"];
        var tint = on ? new SolidColorBrush(accent) : _secondary;
        var glyph = new Grid { Width = 22, Height = 13, RowSpacing = 2, ColumnSpacing = 2 };
        glyph.RowDefinitions.Add(new RowDefinition());
        glyph.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < n; c++)
        {
            glyph.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r = 0; r < 2; r++)
            {
                var block = new Rectangle { RadiusX = 1.5, RadiusY = 1.5, Fill = tint };
                Grid.SetRow(block, r);
                Grid.SetColumn(block, c);
                glyph.Children.Add(block);
            }
        }
        var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock
        {
            Text = n.ToString(), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, Foreground = tint,
            FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        });
        var tile = new Button
        {
            Content = content,
            Height = 38, MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(on ? RtaBandsView.With(accent, 0.16) : Color.FromArgb(13, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(on ? RtaBandsView.With(accent, 0.7) : Colors.Transparent),
            BorderThickness = new Thickness(1),
        };
        ToolTipService.SetToolTip(tile, n == 1 ? "Stack the channels in one column" : $"Lay the channels out in up to {n} columns");
        tile.Click += (_, _) =>
        {
            S.RtaBarColumns = n;
            S.Save();
            S.NotifyChanged();
            flyout.Hide();
        };
        return tile;
    }

    // ── Resize ──

    private void OnResizePressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = _resize.CapturePointer(e.Pointer);
        _dragStartY = e.GetCurrentPoint(this).Position.Y;
        _dragStartHeight = Math.Clamp(S.RtaBarHeight, MinBarHeight, MaxBarHeight);
        e.Handled = true;
    }

    /// <summary>Every row grows with the setting, and several channels by three
    /// quarters of it, so the drag is scaled to keep the edge under the pointer.</summary>
    private void OnResizeMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        double perUnit = _rows * (_single ? 1 : 0.75);
        double dy = e.GetCurrentPoint(this).Position.Y - _dragStartY;
        S.RtaBarHeight = Math.Clamp(_dragStartHeight + dy / perUnit, MinBarHeight, MaxBarHeight);
        ApplyHeights();
        e.Handled = true;
    }

    private void OnResizeReleased(object sender, PointerRoutedEventArgs e)
    {
        _resize.ReleasePointerCapture(e.Pointer);
        EndResize();
        e.Handled = true;
    }

    private void EndResize()
    {
        if (!_dragging) return;
        _dragging = false;
        S.Save();
    }
}
