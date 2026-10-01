using System.Diagnostics;
using System.Runtime.InteropServices;
using DSPiConsole.Core.GraphEditing;
using DSPiConsole.Core.Models;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace DSPiConsole.Controls.GraphEditing;

/// <summary>
/// The on-graph PEQ editor laid over the response graph's plot area: draws the
/// edited channel's bands and curve with Win2D, shows the band chip, the
/// Ctrl-click card and the pointer readouts, and forwards input to
/// <see cref="PeqGraphEditor"/>, which holds all the behaviour.
/// </summary>
public sealed class PeqGraphEditorView : UserControl
{
    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>The graph's backdrop as it appears, for a selected dot's centre.</summary>
    private static readonly Color GraphBackground = Color.FromArgb(255, 38, 38, 41);

    private readonly IPeqEditorHost _host;
    private readonly Grid _root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Canvas _overlay = new() { IsHitTestVisible = true };
    private readonly Border _axisLabel = Label();
    private readonly Border _gainLabel = Label();
    private PeqGraphRenderer? _renderer;
    private CanvasControl? _canvas;
    private PeqGraphEditor? _editor;
    private PeqBandChip? _chip;
    private PeqShapeCard? _card;
    private PeqEditorConfig _config = new();
    private bool _rendering;
    /// <summary>The editor changed since the last frame was drawn.</summary>
    private bool _dirty;
    private bool _chipShown;
    private bool _cardShown;
    private bool _leftDown;
    private Point _lastPressPoint;
    private long _lastPressTicks;
    private PeqCursor _cursor = PeqCursor.Arrow;

    public PeqGraphEditorView(IPeqEditorHost host)
    {
        _host = host;
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        IsHitTestVisible = false;
        Content = _root;
        _root.Children.Add(_overlay);
        _overlay.Children.Add(_axisLabel);
        _overlay.Children.Add(_gainLabel);

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => { if (_leftDown) { _leftDown = false; _editor?.PointerCanceled(); } };
        PointerExited += (_, _) => { if (!_leftDown) _editor?.PointerExited(); };
        PointerWheelChanged += OnPointerWheelChanged;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        SizeChanged += (_, e) => _editor?.Resize(e.NewSize.Width, e.NewSize.Height);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Draws so far and the time spent in them, for profiling.</summary>
    public long DrawCount { get; private set; }
    public long DrawTicks { get; private set; }

    /// <summary>The editor's behaviour, for the host to inspect.</summary>
    public PeqGraphEditor? Editor => _editor;

    /// <summary>Configures the editor; a config with no channel leaves the graph
    /// display-only and lets pointer input through.</summary>
    public void Apply(PeqEditorConfig config)
    {
        _config = config;
        IsHitTestVisible = config.Channel != null;
        _editor?.Apply(config);
        if (config.Channel == null && _canvas != null) _canvas.Invalidate();
    }

    private static Border Label() => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(235, 23, 23, 28)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 1),
        Height = 16,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
        Child = new TextBlock
        {
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            Foreground = new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    // ── Lifetime ────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_editor != null) return;
        _canvas = new CanvasControl { IsHitTestVisible = false };
        _canvas.Draw += (s, args) =>
        {
            if (_editor is { Editing: true } ed && _renderer != null)
            {
                long t0 = Stopwatch.GetTimestamp();
                _renderer.Draw(args.DrawingSession, ed.Picture, GraphBackground);
                DrawCount++;
                DrawTicks += Stopwatch.GetTimestamp() - t0;
            }
        };
        _root.Children.Insert(0, _canvas);
        _renderer = new PeqGraphRenderer();

        _editor = new PeqGraphEditor(_host, new DispatcherScheduler(DispatcherQueue));
        _editor.Changed += Sync;
        _editor.Beep += () => MessageBeep(0);

        _chip = new PeqBandChip(_editor) { Visibility = Visibility.Collapsed, Opacity = 0, FocusReturn = this };
        _card = new PeqShapeCard { Visibility = Visibility.Collapsed };
        _card.Chooser.Picked += (shape, order) => _editor.CardPick(shape, order);
        _card.Chooser.Left += () => _editor.CardCancel();
        _overlay.Children.Add(_chip);
        _overlay.Children.Add(_card);

        _editor.Resize(ActualWidth, ActualHeight);
        _editor.Apply(_config);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRendering();
        if (_editor != null)
        {
            _editor.Changed -= Sync;
            _editor.Dispose();
            _editor = null;
        }
        if (_chip != null) _overlay.Children.Remove(_chip);
        if (_card != null) _overlay.Children.Remove(_card);
        _chip = null;
        _card = null;
        _chipShown = _cardShown = false;
        if (_canvas != null)
        {
            _canvas.RemoveFromVisualTree();
            _root.Children.Remove(_canvas);
            _canvas = null;
        }
        _renderer?.Dispose();
        _renderer = null;
    }

    // ── Drawing and overlay ─────────────────────────────────────────────────

    /// <summary>
    /// The editor changed. Pointer and wheel input can arrive far faster than
    /// the screen refreshes, so the drawing and the chip, card and readouts
    /// follow once a frame, on the next composition tick; only the cursor
    /// changes at once.
    /// </summary>
    private void Sync()
    {
        if (_editor is not { } ed) return;
        _dirty = true;
        StartRendering();
        SetCursor(ed.Editing ? ed.Cursor : PeqCursor.Arrow);
    }

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        if (_editor is not { } ed) { StopRendering(); return; }
        bool running = ed.Frame(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        if (_dirty || running)
        {
            _dirty = false;
            _canvas?.Invalidate();
            SyncOverlay();
        }
        if (!running) StopRendering();
    }

    private void SyncOverlay()
    {
        if (_editor is not { } ed) return;
        ShowLabel(_axisLabel, ed.AxisLabel, l =>
        {
            double w = l.DesiredSize.Width;
            Place(l, Math.Min(Math.Max(ed.AxisLabelX - w / 2, 4), Math.Max(ActualWidth - w - 4, 4)), ActualHeight - 16 - 3);
        });
        ShowLabel(_gainLabel, ed.GainLabel, l =>
        {
            Place(l, 4, Math.Min(Math.Max(ed.GainLabelY - 8, 4), Math.Max(ActualHeight - 16 - 4, 4)));
        });

        if (_chip != null)
        {
            if (ed.HudVisible && ed.HudBand is { } band && ed.HudParams is { } p)
            {
                var (r, g, b) = PeqBandPalette.Bytes(band);
                var frame = ed.HudFrame;
                _chip.Show(p, Color.FromArgb(255, r, g, b), ed.BypassSupported, ed.ShowsShapes, frame);
                Place(_chip, frame.X, frame.Y);
                if (!_chipShown)
                {
                    _chipShown = true;
                    _chip.Visibility = Visibility.Visible;
                    Fade(_chip, 1, 120, null);
                }
            }
            else if (_chipShown)
            {
                _chipShown = false;
                var chip = _chip;
                Fade(chip, 0, 150, () => { if (!_chipShown) chip.Visibility = Visibility.Collapsed; });
            }
        }

        if (_card != null)
        {
            if (ed.CardOpen)
            {
                if (!_cardShown)
                {
                    _cardShown = true;
                    _card.Chooser.Configure(ed.AvailableTypes);
                    _card.Chooser.Mark(null, Colors.White);
                    _card.Visibility = Visibility.Visible;
                    _card.Opacity = 1;
                }
                Place(_card, ed.CardOrigin.X, ed.CardOrigin.Y);
            }
            else if (_cardShown)
            {
                _cardShown = false;
                var card = _card;
                Fade(card, 0, 120, () => { if (!_cardShown) { card.Visibility = Visibility.Collapsed; card.Chooser.Reset(); } });
            }
        }
    }

    private static void ShowLabel(Border label, string? text, Action<Border> place)
    {
        if (text == null)
        {
            if (label.Visibility != Visibility.Collapsed) label.Visibility = Visibility.Collapsed;
            return;
        }
        var block = (TextBlock)label.Child;
        if (block.Text != text || label.Visibility != Visibility.Visible)
        {
            // Measured only when the text changes; placing it needs its width.
            block.Text = text;
            label.Visibility = Visibility.Visible;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        place(label);
    }

    private static void Place(UIElement element, double x, double y)
    {
        if (Canvas.GetLeft(element) != x) Canvas.SetLeft(element, x);
        if (Canvas.GetTop(element) != y) Canvas.SetTop(element, y);
    }

    private static void Fade(UIElement element, double to, int ms, Action? done)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EnableDependentAnimation = false,
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(animation);
        if (done != null) sb.Completed += (_, _) => done();
        sb.Begin();
    }

    private void SetCursor(PeqCursor cursor)
    {
        if (cursor == _cursor && ProtectedCursor != null) return;
        _cursor = cursor;
        ProtectedCursor = InputSystemCursor.Create(cursor switch
        {
            PeqCursor.OpenHand => InputSystemCursorShape.Hand,
            PeqCursor.ClosedHand => InputSystemCursorShape.SizeAll,
            PeqCursor.UpDown => InputSystemCursorShape.SizeNorthSouth,
            _ => InputSystemCursorShape.Arrow,
        });
    }

    // ── Input ───────────────────────────────────────────────────────────────

    private static PeqMods Mods(VirtualKeyModifiers m) =>
        (m.HasFlag(VirtualKeyModifiers.Control) ? PeqMods.Ctrl : 0)
        | (m.HasFlag(VirtualKeyModifiers.Menu) ? PeqMods.Alt : 0)
        | (m.HasFlag(VirtualKeyModifiers.Shift) ? PeqMods.Shift : 0);

    private static PeqMods CurrentMods()
    {
        static bool Down(VirtualKey k) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        return (Down(VirtualKey.Control) ? PeqMods.Ctrl : 0)
             | (Down(VirtualKey.Menu) ? PeqMods.Alt : 0)
             | (Down(VirtualKey.Shift) ? PeqMods.Shift : 0);
    }

    private static PeqPoint ToPoint(Point p) => new(p.X, p.Y);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_editor is not { Editing: true } ed) return;
        var pt = e.GetCurrentPoint(this);
        var p = ToPoint(pt.Position);
        Focus(FocusState.Pointer);

        if (pt.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            if (ed.RightPressed(p) is { } menu) ShowMenu(menu, pt.Position);
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        e.Handled = true;

        // Double-click: a second press soon after, close to the first.
        long now = Environment.TickCount64;
        bool second = now - _lastPressTicks <= GetDoubleClickTime()
                      && Math.Abs(pt.Position.X - _lastPressPoint.X) <= 4
                      && Math.Abs(pt.Position.Y - _lastPressPoint.Y) <= 4;
        _lastPressTicks = second ? 0 : now;
        _lastPressPoint = pt.Position;

        _leftDown = true;
        CapturePointer(e.Pointer);
        ed.PointerPressed(p, Mods(e.KeyModifiers), second ? 2 : 1);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_editor is not { Editing: true } ed) return;
        var p = ToPoint(e.GetCurrentPoint(this).Position);
        if (_leftDown) ed.PointerDragged(p, Mods(e.KeyModifiers));
        else ed.PointerMoved(p);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_leftDown || _editor is not { } ed) return;
        e.Handled = true;
        _leftDown = false;
        ReleasePointerCaptures();
        ed.PointerReleased(ToPoint(e.GetCurrentPoint(this).Position));
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_editor is not { Editing: true } ed) return;
        var pt = e.GetCurrentPoint(this);
        // A mouse notch is 120; the editor works in points, about 8 a notch.
        double delta = pt.Properties.MouseWheelDelta / 15.0;
        var mods = Mods(e.KeyModifiers);
        // A scroll that began on a chip field keeps that field, until Ctrl turns
        // it into a gain gesture.
        if ((mods & PeqMods.Ctrl) == 0 && ed.HudWheelField is { } field)
        {
            ed.HudScroll(field, delta, (mods & PeqMods.Shift) != 0);
            e.Handled = true;
            return;
        }
        if (ed.Wheel(ToPoint(pt.Position), delta, mods)) e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_editor is not { Editing: true } ed || e.OriginalSource is TextBox) return;
        var mods = CurrentMods();
        if (e.Key == VirtualKey.Control) { ed.ModifiersChanged(mods); return; }
        var key = e.Key switch
        {
            VirtualKey.Delete or VirtualKey.Back => PeqKey.Delete,
            VirtualKey.Escape => PeqKey.Escape,
            VirtualKey.Tab => PeqKey.Tab,
            VirtualKey.Left => PeqKey.Left,
            VirtualKey.Right => PeqKey.Right,
            VirtualKey.Up => PeqKey.Up,
            VirtualKey.Down => PeqKey.Down,
            VirtualKey.A => PeqKey.A,
            _ => PeqKey.Other,
        };
        if (key != PeqKey.Other && ed.KeyDown(key, mods)) e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_editor is not { Editing: true } ed) return;
        if (e.Key == VirtualKey.Control) ed.ModifiersChanged(CurrentMods());
    }

    private void ShowMenu(IReadOnlyList<PeqMenuItem> items, Point at)
    {
        var flyout = new MenuFlyout();
        foreach (var item in items) flyout.Items.Add(Build(item));
        flyout.ShowAt(this, new FlyoutShowOptions { Position = at });

        static MenuFlyoutItemBase Build(PeqMenuItem item)
        {
            if (item.IsSeparator) return new MenuFlyoutSeparator();
            if (item.Submenu is { } sub)
            {
                var s = new MenuFlyoutSubItem { Text = item.Title, IsEnabled = item.Enabled };
                foreach (var child in sub) s.Items.Add(Build(child));
                return s;
            }
            MenuFlyoutItem mi = item.Checked
                ? new ToggleMenuFlyoutItem { Text = item.Title, IsChecked = true }
                : new MenuFlyoutItem { Text = item.Title };
            mi.IsEnabled = item.Enabled && !item.IsHeader;
            if (item.IsHeader) mi.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            if (item.Invoke is { } invoke) mi.Click += (_, _) => invoke();
            return mi;
        }
    }

    /// <summary>One-shot timers on the UI thread for the editor.</summary>
    private sealed class DispatcherScheduler : IPeqScheduler
    {
        private readonly DispatcherQueue _queue;

        public DispatcherScheduler(DispatcherQueue queue) => _queue = queue;

        public double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        public IPeqTimer Schedule(double seconds, Action action)
        {
            var timer = _queue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(Math.Max(seconds, 0.001));
            timer.IsRepeating = false;
            var handle = new Handle(timer);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!handle.Cancelled) action();
            };
            timer.Start();
            return handle;
        }

        private sealed class Handle : IPeqTimer
        {
            private readonly DispatcherQueueTimer _timer;
            public bool Cancelled { get; private set; }
            public Handle(DispatcherQueueTimer timer) => _timer = timer;
            public void Cancel()
            {
                Cancelled = true;
                _timer.Stop();
            }
        }
    }
}
