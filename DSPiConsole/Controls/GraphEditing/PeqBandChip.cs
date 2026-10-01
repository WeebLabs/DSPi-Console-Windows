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
using Windows.System;
using Windows.UI;

namespace DSPiConsole.Controls.GraphEditing;

/// <summary>
/// The compact card beside a band's dot, after FabFilter Pro-Q 4's (and the
/// macOS Console's PeqBandHUD): a header with the shape's glyph and two-letter
/// code (click for the shape page) and bypass on the right, then one row per
/// value the shape uses. Values can be dragged vertically, scrolled, or
/// double-clicked to type one in. The shape page replaces the values in place,
/// at the same size. Deleting is the Delete key or the band menu, so the card
/// spends no width on it.
/// </summary>
public sealed class PeqBandChip : Grid
{
    private const double Pad = 8, HeaderY = 4, HeaderHeight = 17, RuleY = 24, FirstRow = 28, RowHeight = 15;
    private const double LabelWidth = 28, NumberWidth = 44, Gap = 3, UnitWidth = 19;

    private static readonly FontFamily NumberFont = new("Cascadia Code, Consolas");

    private readonly PeqGraphEditor _editor;
    private readonly Canvas _content = new();
    private readonly PeqHudButton _shapeButton = new();
    private readonly StackPanel _shapeContent = new() { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
    private readonly TextBlock _shapeCode = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _chevron = new() { Glyph = "", FontSize = 7, Foreground = new SolidColorBrush(Color.FromArgb(107, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
    private readonly PeqHudButton _power = new();
    private readonly FontIcon _powerIcon = new() { Glyph = "", FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Rectangle _rule = new() { Height = 1, Fill = new SolidColorBrush(Color.FromArgb(23, 255, 255, 255)) };
    private readonly ValueRow[] _rows;
    private readonly TextBox _editBox;
    private readonly PeqShapeChooserView _chooser = new("Back to values");

    private FilterParams? _shown;
    private (FilterType, bool, Color, bool, bool, double, double, PeqHudField?) _look;
    private bool _shownShapes;
    private PeqHudField? _editingField;
    private double _lastPressTime;
    private PeqHudField? _lastPressField;

    // A vertical drag on a value.
    private PeqHudField? _adjusting;
    private double _adjustLastY, _adjusted;

    private sealed class ValueRow
    {
        public PeqHudField Field;
        public TextBlock Label = new() { FontSize = 9.5, IsHitTestVisible = false };
        public TextBlock Number = new() { FontSize = 11, FontFamily = NumberFont, TextAlignment = TextAlignment.Right, IsHitTestVisible = false };
        public TextBlock Unit = new() { FontSize = 9.5, IsHitTestVisible = false };
        public PeqCursorArea Hit = new() { Background = new SolidColorBrush(Colors.Transparent) };
        public bool Adjustable;
        public bool Visible;
    }

    public PeqBandChip(PeqGraphEditor editor)
    {
        _editor = editor;
        Children.Add(PeqPanelChrome.Create());
        Children.Add(_content);
        PeqPanelChrome.SwallowPointer(this);

        _shapeContent.Children.Add(_shapeCode);
        _shapeContent.Children.Add(_chevron);
        _shapeButton.Children.Add(_shapeContent);
        _shapeButton.Click += () => _editor.HudShapeButton();
        _power.Children.Add(_powerIcon);
        _power.Click += () => _editor.HudBypass();
        _content.Children.Add(_shapeButton);
        _content.Children.Add(_power);
        _content.Children.Add(_rule);

        _rows = new[] { PeqHudField.Freq, PeqHudField.Gain, PeqHudField.Q }.Select(f => new ValueRow { Field = f }).ToArray();
        foreach (var row in _rows)
        {
            var r = row;
            _content.Children.Add(r.Label);
            _content.Children.Add(r.Number);
            _content.Children.Add(r.Unit);
            _content.Children.Add(r.Hit);
            r.Hit.PointerPressed += (s, e) => OnValuePressed(r, e);
            r.Hit.PointerMoved += (s, e) => OnValueMoved(r, e);
            r.Hit.PointerReleased += (s, e) => OnValueReleased(r, e);
            r.Hit.PointerCaptureLost += (s, e) => OnValueReleased(r, e);
            r.Hit.PointerWheelChanged += (s, e) => OnValueWheel(r, e);
        }

        _editBox = new TextBox
        {
            FontSize = 11,
            FontFamily = NumberFont,
            Padding = new Thickness(3, 0, 3, 0),
            MinHeight = 0,
            MinWidth = 0,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
            TextAlignment = TextAlignment.Right,
            Visibility = Visibility.Collapsed,
            IsSpellCheckEnabled = false,
        };
        _editBox.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
        _editBox.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        _editBox.KeyDown += OnEditKeyDown;
        _editBox.LostFocus += (_, _) => { if (_editingField is { } f) CommitEdit(f, moveBy: 0); };
        _content.Children.Add(_editBox);

        _chooser.Visibility = Visibility.Collapsed;
        _chooser.Picked += (shape, order) => _editor.HudPick(shape, order);
        _chooser.Left += () => _editor.HudShapePageBack();
        Children.Add(_chooser);

        _editor.BeginHudEditRequested += field => BeginEditing(field);
        _editor.EndHudEditRequested += () => EndEditing(focusGraph: false);
    }

    /// <summary>Where keyboard focus goes back to when typing ends.</summary>
    public Control? FocusReturn { get; set; }

    public bool IsEditingText => _editingField != null;

    // ── Content ─────────────────────────────────────────────────────────────

    /// <summary>Shows <paramref name="p"/> in band colour <paramref name="color"/>.
    /// Bypass dims the content and greys the power button.</summary>
    public void Show(FilterParams p, Color color, bool bypassSupported, bool showsShapes, PeqRect frame)
    {
        // During a drag only the numbers move: the glyph, tooltips, colours and
        // layout are rebuilt only when what they depend on changes.
        var look = (p.Type, p.Bypass, color, bypassSupported, showsShapes, frame.Width, frame.Height, _editingField);
        if (_shown != null && look.Equals(_look) && !_shownShapes)
        {
            UpdateNumbers(p);
            _shown = p;
            return;
        }
        _look = look;

        Width = frame.Width;
        Height = frame.Height;
        _chooser.Width = frame.Width;
        _chooser.Height = frame.Height;

        bool dim = p.Bypass;
        var muted = Color.FromArgb(102, 255, 255, 255);
        var tint = dim ? muted : color;

        bool onPage = showsShapes && PeqShapes.Of(p.Type) != null;
        if (onPage != _shownShapes)
        {
            if (onPage)
            {
                if (IsEditingText) EndEditing(focusGraph: false);
                _chooser.Configure(_editor.AvailableTypes);
            }
            else
            {
                _chooser.Reset();
            }
            _shownShapes = onPage;
        }
        _content.Visibility = onPage ? Visibility.Collapsed : Visibility.Visible;
        _chooser.Visibility = onPage ? Visibility.Visible : Visibility.Collapsed;
        if (onPage)
        {
            _chooser.Mark(PeqShapes.Of(p.Type), tint);
            _shown = p;
            return;
        }

        double width = frame.Width;
        _power.Visibility = bypassSupported ? Visibility.Visible : Visibility.Collapsed;
        _powerIcon.Foreground = new SolidColorBrush(tint);
        ToolTipService.SetToolTip(_power, dim ? "Enable band (Alt-click the dot)" : "Bypass band (Alt-click the dot)");
        Place(_shapeButton, 4, HeaderY, 60, HeaderHeight);
        Place(_power, width - 22, HeaderY, 18, HeaderHeight);
        Place(_rule, Pad, RuleY, width - Pad * 2, 1);

        var rows = RowsFor(p);
        if (_shapeContent.Children.Count > 2) _shapeContent.Children.RemoveAt(0);
        if (PeqShapes.Of(p.Type) is { } so)
        {
            var shape = so.Shape;
            _shapeButton.IsEnabledButton = true;
            _shapeContent.Children.Insert(0, PeqShapeGlyph.Create(shape, new SolidColorBrush(tint)));
            _shapeCode.Text = shape.Code();
            _shapeCode.Foreground = new SolidColorBrush(Color.FromArgb((byte)(dim ? 128 : 217), 255, 255, 255));
            _chevron.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(_shapeButton, $"{shape.Title()} - click for shapes and slopes");
        }
        else
        {
            // The Linkwitz Transform: shown here, edited in the band list.
            _shapeButton.IsEnabledButton = false;
            _shapeCode.Text = "LT";
            _shapeCode.Foreground = new SolidColorBrush(Color.FromArgb(178, 255, 255, 255));
            _chevron.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(_shapeButton, "Linkwitz Transform - edit it in the band list");
        }

        // Columns anchored to the right edge, so the numbers line up.
        double unitX = width - Pad - UnitWidth;
        double numberX = unitX - Gap - NumberWidth;
        var quiet = new SolidColorBrush(Color.FromArgb((byte)(dim ? 71 : 115), 255, 255, 255));
        foreach (var row in _rows)
        {
            int r = rows.FindIndex(x => x.Field == row.Field);
            row.Visible = r >= 0;
            var vis = r >= 0 ? Visibility.Visible : Visibility.Collapsed;
            row.Label.Visibility = row.Number.Visibility = row.Unit.Visibility = row.Hit.Visibility = vis;
            if (r < 0) continue;
            var spec = rows[r];
            double y = FirstRow + r * RowHeight;
            row.Adjustable = spec.Adjustable;
            row.Label.Text = spec.Label;
            row.Label.Foreground = quiet;
            Place(row.Label, Pad, y, numberX - Pad, RowHeight);
            row.Number.Foreground = new SolidColorBrush(Color.FromArgb((byte)(dim || !spec.Adjustable ? 115 : 224), 255, 255, 255));
            if (spec.Inline)
            {
                // Number and unit together, ending where the other units end.
                row.Unit.Visibility = Visibility.Collapsed;
                row.Number.Text = $"{spec.Number} {spec.Unit}";
                Place(row.Number, numberX - 20, y - 1, width - Pad - numberX + 20, RowHeight);
            }
            else
            {
                row.Number.Text = spec.Number;
                Place(row.Number, numberX, y - 1, NumberWidth, RowHeight);
                row.Unit.Text = spec.Unit;
                row.Unit.Foreground = quiet;
                Place(row.Unit, unitX, y, UnitWidth, RowHeight);
            }
            Place(row.Hit, numberX - 4, y - 1, NumberWidth + 8, RowHeight);
            row.Hit.SetCursor(spec.Adjustable ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.Arrow);
            if (_editingField == row.Field) Place(_editBox, numberX - 4, y - 2, NumberWidth + 8, RowHeight + 2);
        }
        _shown = p;
    }

    /// <summary>The value rows <paramref name="p"/>'s shape shows. Which rows
    /// appear depends on the type alone; their text on the values.</summary>
    private static List<(PeqHudField Field, string Label, string Number, string Unit, bool Adjustable, bool Inline)> RowsFor(FilterParams p)
    {
        var rows = new List<(PeqHudField Field, string Label, string Number, string Unit, bool Adjustable, bool Inline)>();
        if (PeqShapes.Of(p.Type) is { } so)
        {
            var f = PeqValueText.FrequencyParts(p.Frequency);
            rows.Add((PeqHudField.Freq, "Freq", f.Number, f.Unit, true, false));
            if (p.Type.HasGain())
                rows.Add((PeqHudField.Gain, "Gain", PeqValueText.Truncated(p.Gain, sign: true), "dB", true, false));
            if (p.Type.HasQ())
                rows.Add((PeqHudField.Q, "Width", PeqValueText.Truncated(p.Q), "Q", true, false));
            else if (so.Shape == PeqShape.AllPass)
                rows.Add((PeqHudField.Q, "Order", "1st", "", false, false));
            else
                rows.Add((PeqHudField.Q, "Slope", "6", "dB/oct", false, true));
        }
        else
        {
            var f0 = PeqValueText.FrequencyParts(p.Frequency);
            var fp = PeqValueText.FrequencyParts(p.Gain);
            rows.Add((PeqHudField.Freq, "f0", f0.Number, f0.Unit, false, false));
            rows.Add((PeqHudField.Gain, "fp", fp.Number, fp.Unit, false, false));
        }
        return rows;
    }

    /// <summary>The numbers alone, for a value that moved while the shape, the
    /// colours and the layout stayed put.</summary>
    private void UpdateNumbers(FilterParams p)
    {
        foreach (var spec in RowsFor(p))
        {
            var row = Array.Find(_rows, r => r.Field == spec.Field);
            if (row == null) continue;
            SetText(row.Number, spec.Inline ? $"{spec.Number} {spec.Unit}" : spec.Number);
            if (!spec.Inline) SetText(row.Unit, spec.Unit);
        }

        static void SetText(TextBlock block, string text)
        {
            if (block.Text != text) block.Text = text;
        }
    }

    private static void Place(FrameworkElement e, double x, double y, double w, double h)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        e.Width = Math.Max(w, 0);
        e.Height = Math.Max(h, 0);
    }

    // ── Drag and scroll on a value ──────────────────────────────────────────

    private void OnValuePressed(ValueRow row, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var pt = e.GetCurrentPoint(null);
        if (!pt.Properties.IsLeftButtonPressed || IsEditingText) return;
        double now = Environment.TickCount64 / 1000.0;
        bool second = _lastPressField == row.Field && now - _lastPressTime < 0.5;
        _lastPressTime = now;
        _lastPressField = row.Field;
        if (second)
        {
            _lastPressField = null;
            BeginEditing(row.Field);
            return;
        }
        if (!row.Adjustable) return;
        _adjusting = row.Field;
        _adjustLastY = pt.Position.Y;
        _adjusted = 0;
        row.Hit.CapturePointer(e.Pointer);
        _editor.HudAdjust(row.Field, 0, PeqAdjustPhase.Began);
    }

    private void OnValueMoved(ValueRow row, PointerRoutedEventArgs e)
    {
        if (_adjusting != row.Field) return;
        e.Handled = true;
        double y = e.GetCurrentPoint(null).Position.Y;
        bool fine = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        _adjusted += (_adjustLastY - y) * (fine ? PeqGraphEditor.Tuning.Fine : 1);
        _adjustLastY = y;
        _editor.HudAdjust(row.Field, _adjusted, PeqAdjustPhase.Changed);
    }

    private void OnValueReleased(ValueRow row, PointerRoutedEventArgs e)
    {
        if (_adjusting != row.Field) return;
        e.Handled = true;
        _adjusting = null;
        row.Hit.ReleasePointerCaptures();
        _editor.HudAdjust(row.Field, _adjusted, PeqAdjustPhase.Ended);
    }

    private void OnValueWheel(ValueRow row, PointerRoutedEventArgs e)
    {
        // The chip follows its dot, so a gesture adjusting the band from the
        // graph can slide a field under the pointer. That gesture keeps its
        // band: the event goes on up to the graph. Ctrl-wheel is always the
        // band's gain, so it goes up too.
        if (IsEditingText || !row.Adjustable || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)
            || _editor.GraphWheelActive) return;
        e.Handled = true;
        double delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta / 15.0;
        _editor.HudScroll(row.Field, delta, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
    }

    // ── Typing ──────────────────────────────────────────────────────────────

    public void BeginEditing(PeqHudField field)
    {
        var row = _rows[(int)field];
        if (!row.Visible || !row.Adjustable || _shown is not { } p)
        {
            if (NextField(field, backwards: false) is { } next && next != field) BeginEditing(next);
            return;
        }
        _editingField = field;
        _editor.SetHudEditing(true);
        // Type the bare number; the unit is implied, and "2k" or "A4" work.
        _editBox.Text = field switch
        {
            PeqHudField.Freq => PeqValueText.ShortFrequency(p.Frequency),
            PeqHudField.Gain => p.Gain.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            _ => p.Q.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
        };
        double y = Canvas.GetTop(row.Number);
        Place(_editBox, Canvas.GetLeft(row.Hit), y - 1, row.Hit.Width, RowHeight + 2);
        row.Number.Opacity = 0;
        _editBox.Visibility = Visibility.Visible;
        _editBox.Focus(FocusState.Programmatic);
        _editBox.SelectAll();
    }

    /// <summary>Stops typing without applying.</summary>
    public void EndEditing(bool focusGraph)
    {
        if (_editingField is not { } field) return;
        _editingField = null;
        _rows[(int)field].Number.Opacity = 1;
        _editBox.Visibility = Visibility.Collapsed;
        _editor.SetHudEditing(false);
        if (focusGraph) FocusReturn?.Focus(FocusState.Programmatic);
    }

    private void OnEditKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_editingField is not { } field) return;
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                CommitEdit(field, moveBy: 0);
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                bool back = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                CommitEdit(field, moveBy: back ? -1 : 1);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                EndEditing(focusGraph: true);
                break;
        }
    }

    /// <summary>Applies the typed text (beeping when it doesn't parse, keeping
    /// the old value), then ends typing or moves to the next field.</summary>
    private void CommitEdit(PeqHudField field, int moveBy)
    {
        string text = _editBox.Text;
        // Ending first means the refresh the edit triggers redraws the value.
        _editingField = null;
        _rows[(int)field].Number.Opacity = 1;
        _editor.HudText(field, text);
        if (moveBy != 0 && NextField(field, moveBy < 0) is { } next)
        {
            BeginEditing(next);
            return;
        }
        _editBox.Visibility = Visibility.Collapsed;
        _editor.SetHudEditing(false);
        FocusReturn?.Focus(FocusState.Programmatic);
    }

    private PeqHudField? NextField(PeqHudField field, bool backwards)
    {
        var list = backwards
            ? new[] { PeqHudField.Q, PeqHudField.Gain, PeqHudField.Freq }
            : new[] { PeqHudField.Freq, PeqHudField.Gain, PeqHudField.Q };
        int i = Array.IndexOf(list, field);
        for (int k = 1; k <= list.Length; k++)
        {
            var candidate = list[(i + k) % list.Length];
            if (_rows[(int)candidate].Visible && _rows[(int)candidate].Adjustable) return candidate;
        }
        return null;
    }
}

/// <summary>A hit area that can set its own pointer cursor (ProtectedCursor is
/// only reachable from a subclass).</summary>
public sealed class PeqCursorArea : Grid
{
    private InputSystemCursorShape? _shape;

    public void SetCursor(InputSystemCursorShape shape)
    {
        if (_shape == shape) return;
        _shape = shape;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }
}
