using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// A row of equal segments with one selected, like a native segmented control:
/// the selected segment lifts, the others stay flat. Used where the macOS
/// Console has a segmented picker (the limiter's link group, the subharmonic
/// synthesizer's selectivity).
/// </summary>
public sealed class SegmentedPicker : UserControl
{
    private readonly Button[] _segments;
    private int _selected = -1;

    /// <summary>Raised with the index of a segment the user picks.</summary>
    public event Action<int>? Picked;

    public SegmentedPicker(IReadOnlyList<string> labels, IReadOnlyList<string>? accessibleNames = null, double height = 24, double fontSize = 12)
    {
        var grid = new Grid { Height = height };
        _segments = new Button[labels.Count];
        for (int i = 0; i < labels.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int index = i;
            var b = new Button
            {
                Content = labels[i],
                FontSize = fontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(0),
                Margin = new Thickness(1),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Colors.Transparent),
            };
            if (accessibleNames != null && i < accessibleNames.Count)
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, accessibleNames[i]);
            b.Click += (_, _) => Picked?.Invoke(index);
            Grid.SetColumn(b, i);
            grid.Children.Add(b);
            _segments[i] = b;
        }
        Content = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(33, 255, 255, 255)),
            BorderThickness = new Thickness(1),
        };
        IsEnabledChanged += (_, _) =>
        {
            foreach (var b in _segments) b.IsEnabled = IsEnabled;
            Opacity = IsEnabled ? 1 : 0.5;
        };
    }

    public int Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            for (int i = 0; i < _segments.Length; i++)
                _segments[i].Background = new SolidColorBrush(i == value ? Color.FromArgb(74, 255, 255, 255) : Colors.Transparent);
        }
    }
}
