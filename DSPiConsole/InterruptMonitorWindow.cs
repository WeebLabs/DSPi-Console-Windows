using System.ComponentModel;
using System.Globalization;
using System.Text;
using DSPiConsole.Controls;
using DSPiConsole.Core;
using DSPiConsole.Usb;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// Every packet read on the device's interrupt notification endpoint, one line
/// each, after the macOS Console's InterruptMonitorView: the decode lives in
/// <see cref="NotifyDecoder"/>, the log keeps the last 2000 lines, and packets
/// arriving in a burst are shown together at most 25 times a second. Keeps the
/// Windows extras: auto-scroll, IDLE keep-alives hidden by default, and the raw
/// bytes after each line.
/// </summary>
public sealed class InterruptMonitorWindow : Window
{
    private const int MaxLines = 2000;
    private static readonly Color Green = Color.FromArgb(255, 48, 209, 88);
    private static readonly Color Orange = Color.FromArgb(255, 255, 159, 10);

    private readonly DspDevice _device;
    private readonly Queue<string> _lines = new(MaxLines + 1);
    private readonly List<string> _pending = new();
    private readonly object _pendingLock = new();
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private readonly Button _pause = new() { MinWidth = 90 };
    private readonly CheckBox _autoScroll = new() { Content = "Auto-scroll", IsChecked = true, MinWidth = 0 };
    private readonly CheckBox _showIdle = new() { Content = "Show IDLE", MinWidth = 0 };
    private readonly CheckBox _showHex = new() { Content = "Show raw bytes", MinWidth = 0 };
    private readonly TextBlock _state = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Code, Consolas") };
    private readonly TextBlock _footer = new() { FontSize = 10 };
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Auto,
    };
    private readonly TextBlock _log = new()
    {
        FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 11,
        IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap, Padding = new Thickness(12, 8, 12, 8),
    };

    // Read on the notification thread, so kept as plain fields the UI writes.
    private volatile bool _paused, _includeIdle, _includeHex;
    private long _received, _idleHidden;
    private bool _dirty;

    public InterruptMonitorWindow(DspDevice device)
    {
        _device = device;
        ToolWindowChrome.Apply(this, "Interrupt Monitor", 900, 560);

        var pauseKey = new KeyboardAccelerator { Key = VirtualKey.P, Modifiers = VirtualKeyModifiers.Control };
        _pause.KeyboardAccelerators.Add(pauseKey);
        _pause.Click += (_, _) => TogglePause();
        var clear = new Button { Content = Label("", "Clear"), MinWidth = 90 };
        clear.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control });
        clear.Click += (_, _) => Clear();
        ToolTipService.SetToolTip(_pause, "Ctrl+P");
        ToolTipService.SetToolTip(clear, "Ctrl+K");
        _showIdle.Checked += (_, _) => _includeIdle = true;
        _showIdle.Unchecked += (_, _) => _includeIdle = false;
        _showHex.Checked += (_, _) => _includeHex = true;
        _showHex.Unchecked += (_, _) => _includeHex = false;
        _autoScroll.Checked += (_, _) => ScrollToEnd();
        _count.Foreground = _secondary;
        _footer.Foreground = _secondary;

        var toolbar = new Grid { Padding = new Thickness(10), ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { _pause, clear, _autoScroll, _showIdle, _showHex },
        });
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _state, _count } };
        Grid.SetColumn(status, 1);
        toolbar.Children.Add(status);

        _scroll.Content = _log;
        var footer = new Border { Padding = new Thickness(12, 4, 12, 4), Child = _footer };

        var root = new Grid { Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(toolbar);
        Place(root, Rule(), 1);
        Place(root, _scroll, 2);
        Place(root, Rule(), 3);
        Place(root, footer, 4);
        Content = root;

        _flush.Tick += (_, _) => Flush();
        _flush.Start();
        _device.NotifyPacketReceived += OnPacketReceived;
        _device.PropertyChanged += OnDevicePropertyChanged;
        Closed += (_, _) =>
        {
            _flush.Stop();
            _device.NotifyPacketReceived -= OnPacketReceived;
            _device.PropertyChanged -= OnDevicePropertyChanged;
        };
        UpdatePauseButton();
        UpdateHeader();
        UpdateFooter();
    }

    private static void Place(Grid grid, FrameworkElement child, int row)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private static Border Rule() => new() { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };

    private static StackPanel Label(string glyph, string text) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children =
        {
            new FontIcon { Glyph = glyph, FontSize = 12 },
            new TextBlock { Text = text },
        },
    };

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DspDevice.IsConnected)) DispatcherQueue.TryEnqueue(UpdateHeader);
    }

    /// <summary>Runs on the notification thread: decodes and queues the line.</summary>
    private void OnPacketReceived(object? sender, NotifyPacket packet)
    {
        Interlocked.Increment(ref _received);
        bool idle = packet.Data.Length < 4;
        if (idle && !_includeIdle)
        {
            Interlocked.Increment(ref _idleHidden);
            return;
        }
        // While paused nothing is logged; the counts keep going.
        if (_paused) return;
        string line = packet.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + NotifyDecoder.Decode(packet.Data);
        if (_includeHex) line += "    | " + NotifyDecoder.Hex(packet.Data);
        lock (_pendingLock) _pending.Add(line);
    }

    private void Flush()
    {
        string[] batch;
        lock (_pendingLock)
        {
            batch = _pending.ToArray();
            _pending.Clear();
        }
        if (batch.Length > 0)
        {
            foreach (var line in batch) _lines.Enqueue(line);
            while (_lines.Count > MaxLines) _lines.Dequeue();
            _dirty = true;
        }
        UpdateFooter();
        if (!_dirty) return;
        _dirty = false;

        var sb = new StringBuilder(_lines.Count * 96);
        foreach (var line in _lines)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        _log.Text = sb.ToString();
        UpdateHeader();
        if (_autoScroll.IsChecked == true) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        // After layout, so the new lines are counted in the extent.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _scroll.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: true));
    }

    private void TogglePause()
    {
        _paused = !_paused;
        UpdatePauseButton();
        UpdateHeader();
    }

    private void Clear()
    {
        lock (_pendingLock) _pending.Clear();
        _lines.Clear();
        Interlocked.Exchange(ref _received, 0);
        Interlocked.Exchange(ref _idleHidden, 0);
        _log.Text = "";
        UpdateHeader();
        UpdateFooter();
    }

    private void UpdatePauseButton() =>
        _pause.Content = _paused ? Label("", "Resume") : Label("", "Pause");

    private void UpdateHeader()
    {
        if (!_device.IsConnected)
        {
            _state.Text = "Inactive";
            _state.Foreground = _secondary;
        }
        else
        {
            _state.Text = _paused ? "Paused" : "Listening";
            _state.Foreground = new SolidColorBrush(_paused ? Orange : Green);
        }
        _count.Text = _lines.Count == 1 ? "1 event" : $"{_lines.Count} events";
    }

    private void UpdateFooter()
    {
        long received = Interlocked.Read(ref _received), hidden = Interlocked.Read(ref _idleHidden);
        string text = string.Format(CultureInfo.InvariantCulture,
            "{0} packets received    {1} IDLE hidden    last {2} lines kept", received, hidden, MaxLines);
        if (_footer.Text != text) _footer.Text = text;
    }
}
