#if DEBUG
using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DSPiConsole.Debugging;

/// <summary>
/// Debug builds only: the Subharmonic Synthesizer window's controls with
/// sample values and no device, so its drawing and layout can be exercised
/// offline. Opened instead of the main window when DSPI_SUBHARM_HARNESS is set.
/// </summary>
public sealed class SubharmHarness : Window
{
    public SubharmHarness()
    {
        ToolWindowChrome.Apply(this, "Subharm Harness", 780, 660);
        var stack = new StackPanel { Spacing = 14, Padding = new Thickness(16) };

        var graph = new SubharmBandGraph();
        stack.Children.Add(new Border
        {
            Height = 188,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Background = new SolidColorBrush(ToolWindowChrome.PanelColor),
            Child = graph,
        });
        graph.SetValues(0, -6, SubharmLimits.LevelMinDb, 3, -12, true);

        var headroom = new TextBlock { Text = "+3.0 dB" };
        var row = new Grid();
        row.Children.Add(headroom);
        stack.Children.Add(row);
        ToolTipService.SetToolTip(row, "headroom");

        var select = new SegmentedPicker(new[] { "All material", "Percussive", "Sustained" }, height: 26, fontSize: 12);
        var picker = new StackPanel();
        picker.Children.Add(select);
        stack.Children.Add(picker);
        select.Selected = 1;
        ToolTipService.SetToolTip(picker, "select");

        var band = new ParameterRow("24 - 36 Hz", "dB", SubharmLimits.LevelMinDb, SubharmLimits.LevelMaxDb, set: _ => { }, live: v => graph.SetLive(SubharmField.Low, v))
        {
            Subtitle = "Derived from 48 - 72 Hz",
            ScrollStep = 0.5f,
            MaxDecimals = 1,
            Ends = ("Off", "+12 dB"),
            DisplayOverride = v => v <= SubharmLimits.LevelMinDb ? "Off" : null,
            Help = "help",
        };
        stack.Children.Add(band);
        band.Value = SubharmLimits.LevelMinDb;
        band.IsEnabled = false;

        var chips = new Grid { ColumnSpacing = 6 };
        for (int o = 0; o < 4; o++)
        {
            chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var chip = new ToggleButton { Content = (o + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 26, Padding = new Thickness(0) };
            var track = new Grid { Height = 3 };
            track.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0 });
            var cell = new StackPanel { Spacing = 3 };
            cell.Children.Add(chip);
            cell.Children.Add(track);
            Grid.SetColumn(cell, o);
            chips.Children.Add(cell);
        }
        stack.Children.Add(chips);

        Content = new ScrollViewer { Content = stack };
    }
}
#endif
