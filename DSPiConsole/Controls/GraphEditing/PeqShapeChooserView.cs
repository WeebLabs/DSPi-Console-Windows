using DSPiConsole.Core.GraphEditing;
using DSPiConsole.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.UI;

namespace DSPiConsole.Controls.GraphEditing;

/// <summary>A borderless panel button: a soft highlight on hover, an optional
/// "on" fill, and a faint resting fill for buttons that must read as one
/// before they are hovered. After the macOS PeqHUDButton.</summary>
public sealed class PeqHudButton : Grid
{
    private bool _hovering;
    private bool _pressed;
    private bool _isOn;
    private double _restingFill;

    public event Action? Click;
    public event Action<bool>? HoverChanged;

    public PeqHudButton()
    {
        CornerRadius = new CornerRadius(5);
        Background = new SolidColorBrush(Colors.Transparent);
        PointerEntered += (_, _) => { _hovering = true; UpdateBackground(); HoverChanged?.Invoke(true); };
        PointerExited += (_, _) => { _hovering = false; _pressed = false; UpdateBackground(); HoverChanged?.Invoke(false); };
        PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _pressed = true;
        };
        PointerReleased += (_, e) =>
        {
            e.Handled = true;
            if (!_pressed) return;
            _pressed = false;
            if (IsEnabledButton) Click?.Invoke();
        };
        RightTapped += (_, e) => e.Handled = true;
        DoubleTapped += (_, e) => e.Handled = true;
    }

    public bool IsEnabledButton { get; set; } = true;

    public bool IsOn
    {
        get => _isOn;
        set { _isOn = value; UpdateBackground(); }
    }

    public double RestingFill
    {
        get => _restingFill;
        set { _restingFill = value; UpdateBackground(); }
    }

    /// <summary>The fill, for a button that a test (or the chooser) drives.</summary>
    public void PerformClick() => Click?.Invoke();

    private void UpdateBackground()
    {
        double alpha = _isOn ? 0.16 : (_hovering && IsEnabledButton ? Math.Max(0.09, _restingFill + 0.07) : _restingFill);
        Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255));
    }
}

/// <summary>
/// Chooses a shape and its order: a header with a back arrow and the shape's
/// name, a grid of shapes, then, for a shape with two orders, its two slopes
/// (or all-pass phases). The last click reports shape and order together. The
/// chip shows it as its shape page, marking the band's own shape; the
/// Ctrl-click card shows it on a panel of its own.
/// </summary>
public sealed class PeqShapeChooserView : Canvas
{
    private static readonly SolidColorBrush Quiet = new(Color.FromArgb(178, 255, 255, 255));
    private static readonly SolidColorBrush Normal = new(Color.FromArgb(217, 255, 255, 255));

    public PeqShapeChooserState State { get; } = new();

    private readonly PeqHudButton _back = new();
    private readonly TextBlock _title = new()
    {
        FontSize = 10.5,
        Foreground = new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)),
        TextTrimming = TextTrimming.CharacterEllipsis,
        IsHitTestVisible = false,
    };
    private readonly Rectangle _rule = new() { Height = 1, Fill = new SolidColorBrush(Color.FromArgb(23, 255, 255, 255)) };
    private readonly List<(PeqShape Shape, PeqHudButton Button)> _cells = new();
    private readonly PeqHudButton[] _orders = { new(), new() };
    private Color _markColor = Colors.White;
    private readonly string _backHelp;

    /// <summary>A completed choice.</summary>
    public event Action<PeqShape, int>? Picked;
    /// <summary>The back arrow on the first step.</summary>
    public event Action? Left;

    public PeqShapeChooserView(string backHelp)
    {
        _backHelp = backHelp;
        _back.Children.Add(new FontIcon
        {
            Glyph = "", FontSize = 8, Foreground = Normal,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        _back.Click += () =>
        {
            if (State.Back()) Left?.Invoke();
            else Layout();
        };
        Children.Add(_back);
        Children.Add(_title);
        Children.Add(_rule);
        for (int i = 0; i < 2; i++)
        {
            int order = i + 1;
            var b = _orders[i];
            b.RestingFill = 0.07;
            b.Children.Add(new TextBlock
            {
                FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            });
            b.Click += () => { if (State.PickOrder(order) is { } pick) Picked?.Invoke(pick.Shape, pick.Order); };
            Children.Add(b);
        }
        SizeChanged += (_, _) => Layout();
    }

    /// <summary>Offers the shapes the firmware supports, from the first step.</summary>
    public void Configure(IReadOnlySet<FilterType> available)
    {
        State.Configure(available);
        foreach (var (_, button) in _cells) Children.Remove(button);
        _cells.Clear();
        foreach (var shape in State.Shapes)
        {
            var s = shape;
            var b = new PeqHudButton();
            ToolTipService.SetToolTip(b, s.Title());
            b.Children.Add(PeqShapeGlyph.Create(s, Quiet));
            b.Click += () => { if (State.PickShape(s) is { } pick) Picked?.Invoke(pick.Shape, pick.Order); else Layout(); };
            b.HoverChanged += hover =>
            {
                if (hover) State.HoveredShape = s;
                else if (State.HoveredShape == s) State.HoveredShape = null;
                _title.Text = State.Title;
            };
            _cells.Add((s, b));
            Children.Add(b);
        }
        Layout();
    }

    /// <summary>Marks the band's own shape (and its order on that shape) in
    /// <paramref name="color"/>; null marks nothing.</summary>
    public void Mark((PeqShape Shape, int Order)? own, Color color)
    {
        State.Own = own;
        _markColor = color;
        Layout();
    }

    /// <summary>Back to the first step.</summary>
    public void Reset()
    {
        State.Reset();
        Layout();
    }

    private void Layout()
    {
        double width = Width is double w && !double.IsNaN(w) ? w : ActualWidth;
        double height = Height is double h && !double.IsNaN(h) ? h : ActualHeight;
        if (width <= 0 || height <= 0) return;
        const double pad = 8, headerY = 4, headerHeight = 17, rule = 24;

        Place(_back, 3, headerY, 15, headerHeight);
        ToolTipService.SetToolTip(_back, State.PendingShape == null ? _backHelp : "Back to shapes");
        Place(_title, 19, headerY + 1.5, Math.Max(width - 19 - pad, 0), headerHeight);
        Place(_rule, pad, rule, Math.Max(width - pad * 2, 0), 1);
        double top = rule + 3, bottom = height - 4;
        _title.Text = State.Title;

        // Step two: the picked shape's two orders, centred in the body. Only the
        // band's own shape shows its order; another starts unselected.
        if (State.PendingShape is { } pending)
        {
            foreach (var (_, b) in _cells) b.Visibility = Visibility.Collapsed;
            const double cw = 42, ch = 20, gap = 6;
            double x0 = (width - cw * 2 - gap) / 2;
            double y = Math.Round((top + bottom) / 2 - ch / 2);
            for (int i = 0; i < 2; i++)
            {
                int order = i + 1;
                var b = _orders[i];
                bool marked = State.IsOrderMarked(order);
                b.Visibility = Visibility.Visible;
                Place(b, x0 + i * (cw + gap), y, cw, ch);
                b.IsOn = marked;
                var label = (TextBlock)b.Children[0];
                label.Text = pending.OrderLabel(order);
                label.Foreground = marked ? new SolidColorBrush(_markColor) : Normal;
                ToolTipService.SetToolTip(b, pending == PeqShape.AllPass
                    ? (order == 1 ? "First order, 180° of phase" : "Second order, 360° of phase")
                    : $"{(order == 1 ? 6 : 12)} dB per octave");
            }
            return;
        }

        // Step one: the grid fills the body; a short last row is centred.
        foreach (var b in _orders) b.Visibility = Visibility.Collapsed;
        const int columns = 4;
        int rows = (_cells.Count + columns - 1) / columns;
        double cellHeight = (bottom - top + 1) / Math.Max(rows, 1);
        double cellWidth = (width - pad * 2) / columns;
        for (int i = 0; i < _cells.Count; i++)
        {
            var (shape, b) = _cells[i];
            int r = i / columns, c = i % columns;
            int inRow = Math.Min(columns, _cells.Count - r * columns);
            double inset = (columns - inRow) * cellWidth / 2;
            bool marked = State.Own?.Shape == shape;
            b.Visibility = Visibility.Visible;
            Place(b, pad + inset + c * cellWidth, top + r * cellHeight, cellWidth, cellHeight - 1);
            b.IsOn = marked;
            if (b.Children[0] is Path glyph)
            {
                glyph.Stroke = marked ? new SolidColorBrush(_markColor) : Quiet;
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
            }
        }
    }

    private static void Place(FrameworkElement e, double x, double y, double w, double h)
    {
        SetLeft(e, x);
        SetTop(e, y);
        e.Width = Math.Max(w, 0);
        e.Height = Math.Max(h, 0);
    }
}

/// <summary>Frosted panel chrome shared by the chip and the Ctrl-click card:
/// a dark translucent card under a hairline edge.</summary>
public static class PeqPanelChrome
{
    public static Border Create()
    {
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(235, 28, 28, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>The panel's own background swallows clicks, which would
    /// otherwise fall through to the graph and create a band behind it.</summary>
    public static void SwallowPointer(UIElement e)
    {
        e.PointerPressed += (_, args) => args.Handled = true;
        e.PointerReleased += (_, args) => args.Handled = true;
        e.RightTapped += (_, args) => args.Handled = true;
        e.DoubleTapped += (_, args) => args.Handled = true;
        e.Tapped += (_, args) => args.Handled = true;
    }
}

/// <summary>The Ctrl-click card: the shape chooser on a panel of its own, the
/// size of a band's chip, for a band not made yet.</summary>
public sealed class PeqShapeCard : Grid
{
    public PeqShapeChooserView Chooser { get; } = new("Cancel");

    public PeqShapeCard()
    {
        Width = PeqGraphEditor.Tuning.CardSize.Width;
        Height = PeqGraphEditor.Tuning.CardSize.Height;
        Children.Add(PeqPanelChrome.Create());
        Chooser.Width = Width;
        Chooser.Height = Height;
        Children.Add(Chooser);
        PeqPanelChrome.SwallowPointer(this);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }
}
