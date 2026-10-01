using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DSPiConsole.Controls;

/// <summary>
/// A compact typed number with its unit, after the macOS Console's ValueField:
/// Enter or leaving the field commits, Escape reverts, the scroll wheel steps,
/// and the value is clamped to its range. Commits only text the user changed,
/// so a value set from outside while the field has focus is not undone.
/// </summary>
public sealed class ValueField : UserControl
{
    private readonly TextBox _box;
    private readonly Action<float> _commit;
    private readonly float _min, _max, _step;
    private readonly int _decimals;
    private float _value;
    private string _shown = "";

    public ValueField(string unit, float value, Action<float> commit, float min = float.MinValue, float max = float.MaxValue,
                      float step = 1, int decimals = 1, double width = 66)
    {
        _commit = commit;
        (_min, _max, _step, _decimals) = (min, max, step, decimals);
        _box = new TextBox
        {
            Width = width, MinWidth = 0, FontSize = 12, TextAlignment = TextAlignment.Right,
            Padding = new Thickness(6, 3, 6, 3), MinHeight = 0,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(_box);
        if (unit.Length > 0)
            row.Children.Add(new TextBlock
            {
                Text = unit, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        Content = row;
        Value = value;

        _box.KeyDown += OnKeyDown;
        _box.LostFocus += (_, _) => CommitText();
        _box.PointerWheelChanged += (_, e) =>
        {
            e.Handled = true;
            int delta = e.GetCurrentPoint(_box).Properties.MouseWheelDelta;
            Set(_value + (delta > 0 ? _step : -_step));
        };
        IsEnabledChanged += (_, _) => _box.IsEnabled = IsEnabled;
    }

    public float Value
    {
        get => _value;
        set
        {
            _value = value;
            if (_box.FocusState == FocusState.Unfocused) Show(value);
        }
    }

    private void Show(float v)
    {
        var s = v.ToString("F" + _decimals, CultureInfo.InvariantCulture);
        if (_decimals > 0) s = s.TrimEnd('0').TrimEnd('.');
        _shown = s == "-0" ? "0" : s;
        _box.Text = _shown;
    }

    private void Set(float v)
    {
        v = (float)Math.Clamp(Math.Round(v, _decimals), _min, _max);
        _value = v;
        Show(v);
        _commit(v);
    }

    private void CommitText()
    {
        if (_box.Text == _shown) return;
        if (float.TryParse(_box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float typed)) Set(typed);
        else Show(_value);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            CommitText();
            _box.SelectAll();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Show(_value);
            _box.SelectAll();
        }
    }
}
