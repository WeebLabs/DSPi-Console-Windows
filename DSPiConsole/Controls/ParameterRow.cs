using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DSPiConsole.Controls;

/// <summary>
/// The labelled value field and slider row of the tool windows, after the macOS
/// Console's ParameterRow. The thumb and the readout follow every event; a
/// drag sends each value straight to the device through <c>live</c> (coalesced,
/// see <see cref="SliderDrag"/>) without touching the view model, and the
/// model is set once, on release or on a typed entry, through <c>set</c>. The
/// field commits on Enter or on leaving it, never per keystroke, and steps by
/// <see cref="ScrollStep"/> with Ctrl and the wheel. While a drag runs, model
/// echoes are ignored.
/// </summary>
public sealed class ParameterRow : UserControl
{
    private readonly Action<float> _set;
    private readonly double _min, _max;
    private readonly TextBlock _title = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _subtitle = new() { FontSize = 9, Visibility = Visibility.Collapsed };
    private readonly TextBox _field;
    private readonly TextBlock _unit = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    private readonly Slider _slider;
    private readonly SliderDrag _drag;
    private readonly Grid _ends = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _endLeft = new() { FontSize = 9 };
    private readonly TextBlock _endRight = new() { FontSize = 9, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _caption = new() { FontSize = 9, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private float _value;

    /// <param name="set">Clamps, publishes and sends: called once on release,
    /// and on a typed or stepped entry.</param>
    /// <param name="live">Device only, for values during a drag; null sends
    /// nothing until release.</param>
    public ParameterRow(string title, string unit, double min, double max, Action<float> set, Action<float>? live = null)
    {
        _set = set;
        _min = min;
        _max = max;
        _title.Text = title;
        _unit.Text = unit;

        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _subtitle.Foreground = secondary;
        _unit.Foreground = secondary;
        _endLeft.Foreground = secondary;
        _endRight.Foreground = secondary;
        _caption.Foreground = secondary;

        _field = new TextBox
        {
            Width = 60,
            MinWidth = 0,
            FontSize = 12,
            Padding = new Thickness(6, 2, 6, 2),
            MinHeight = 0,
            TextAlignment = TextAlignment.Right,
            IsSpellCheckEnabled = false,
        };
        _field.KeyDown += OnFieldKeyDown;
        _field.LostFocus += (_, _) => CommitField();
        _field.PointerWheelChanged += OnFieldWheel;

        _slider = new Slider { Minimum = min, Maximum = max, StepFrequency = 0.01, Margin = new Thickness(0, -4, 0, -4) };
        _drag = new SliderDrag(_slider, live, v => Commit(v), v => (float)Math.Round(v, MaxDecimals));
        _drag.Moved += v => ShowText(Format(v, live: _drag.IsDragging));

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        var fieldPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        fieldPanel.Children.Add(_field);
        fieldPanel.Children.Add(_unit);
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(titles);
        Grid.SetColumn(fieldPanel, 1);
        header.Children.Add(fieldPanel);

        _ends.Children.Add(_endLeft);
        _ends.Children.Add(_endRight);

        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(header);
        root.Children.Add(_slider);
        root.Children.Add(_ends);
        root.Children.Add(_caption);
        Content = root;
        IsEnabledChanged += (_, _) => _field.IsEnabled = _slider.IsEnabled = IsEnabled;
    }

    /// <summary>A second, smaller line under the title.</summary>
    public string? Subtitle
    {
        set { _subtitle.Text = value ?? ""; _subtitle.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible; }
    }

    /// <summary>Secondary text under the slider, for windows that caption their
    /// controls rather than keeping the explanation in a tooltip.</summary>
    public string? Caption
    {
        set { _caption.Text = value ?? ""; _caption.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible; }
    }

    /// <summary>Tooltip for the whole row.</summary>
    public string? Help
    {
        set => ToolTipService.SetToolTip(this, string.IsNullOrEmpty(value) ? null : value);
    }

    /// <summary>Labels under the slider's two ends.</summary>
    public (string Left, string Right)? Ends
    {
        set
        {
            _ends.Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
            _endLeft.Text = value?.Left ?? "";
            _endRight.Text = value?.Right ?? "";
        }
    }

    /// <summary>Decimals the value is rounded to, shown with, and committed at.</summary>
    public int MaxDecimals { get; set; }

    /// <summary>The Ctrl+wheel step on the field.</summary>
    public float ScrollStep { get; set; } = 1;

    public double FieldWidth { set => _field.Width = value; }

    /// <summary>Text shown for a value instead of the number, or null for the
    /// number: "Off" at a band's floor, say.</summary>
    public Func<float, string?>? DisplayOverride { get; set; }

    /// <summary>The model value. Ignored as an input while a drag runs.</summary>
    public float Value
    {
        get => _value;
        set
        {
            if (_drag.IsDragging) return;
            _value = value;
            _drag.Show(value);
            // Text being typed into the field is not overwritten by an echo.
            if (_field.FocusState == FocusState.Unfocused)
                ShowText(Format(value, live: false));
        }
    }

    private void Commit(float v)
    {
        v = (float)Math.Clamp(Math.Round(v, MaxDecimals), _min, _max);
        _value = v;
        _set(v);
        ShowText(Format(v, live: false));
    }

    private string Format(float v, bool live)
    {
        if (DisplayOverride?.Invoke(v) is { } text) return text;
        var s = v.ToString("F" + MaxDecimals, CultureInfo.InvariantCulture);
        // Committed values drop trailing zeros; a live readout keeps its width.
        if (!live && MaxDecimals > 0) s = s.TrimEnd('0').TrimEnd('.');
        return s == "-0" ? "0" : s;
    }

    /// <summary>The text this row last put in the field. A field left as it
    /// was shown has nothing to commit, even if the value changed underneath
    /// it while it had focus (a preset loaded elsewhere, say).</summary>
    private string _shownText = "";

    private void ShowText(string text)
    {
        _shownText = text;
        _field.Text = text;
    }

    private void CommitField()
    {
        if (_field.Text == _shownText)
        {
            ShowText(Format(_value, live: false));
            return;
        }
        var text = _field.Text.Trim();
        if (DisplayOverride?.Invoke(_value) is { } shown && string.Equals(text, shown, StringComparison.OrdinalIgnoreCase))
            return;
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float typed))
        {
            if (Math.Abs(typed - _value) > 1e-6f) Commit(typed);
            else ShowText(Format(_value, live: false));
        }
        else
        {
            ShowText(Format(_value, live: false));
        }
    }

    private void OnFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            CommitField();
            _field.SelectAll();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ShowText(Format(_value, live: false));
            _field.SelectAll();
        }
    }

    private void OnFieldWheel(object sender, PointerRoutedEventArgs e)
    {
        // Ctrl+wheel steps the value; a plain wheel scrolls the page.
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || !IsEnabled) return;
        int delta = e.GetCurrentPoint(_field).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        Commit(_value + Math.Sign(delta) * ScrollStep);
    }
}
