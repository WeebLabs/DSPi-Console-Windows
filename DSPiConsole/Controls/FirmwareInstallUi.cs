using DSPiConsole.Core.Firmware;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// The pieces the Firmware Update window and the Getting Started wizard share,
/// so both draw <see cref="FirmwareInstaller"/>'s states alike: a spinner means
/// the app or the board is working, a still icon that the next move is the
/// user's, and the card says what that move is. After the macOS Console's
/// FirmwareInstallUI.
/// </summary>
public static class FirmwareInstallUi
{
    public static readonly Color Green = Color.FromArgb(255, 48, 209, 88);
    public static readonly Color Orange = Color.FromArgb(255, 255, 159, 10);

    private static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    public static Color Accent => (Color)Application.Current.Resources["SystemAccentColor"];

    /// <summary>The rounded panel every status and summary card sits on.</summary>
    public static Border Card(UIElement child, Thickness padding) => new()
    {
        Child = child, Padding = padding, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Background = new SolidColorBrush(ToolWindowChrome.PanelColor),
        BorderBrush = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)),
    };

    /// <summary>One "LABEL   value" line, with a fixed label column so stacked
    /// rows line up.</summary>
    public static FrameworkElement ValueRow(string label, string value, bool secondary = false, double labelWidth = 120)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(), Width = labelWidth, FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center,
        });
        var text = new TextBlock
        {
            Text = value, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            FontFamily = new FontFamily("Cascadia Code, Consolas"), VerticalAlignment = VerticalAlignment.Center,
        };
        if (secondary) text.Foreground = Secondary;
        row.Children.Add(text);
        return row;
    }

    /// <summary>Fill <paramref name="strip"/> with labelled dots: passed steps
    /// ticked, the last one green when reached, dimmed on a failure.</summary>
    public static void FillStepStrip(Grid strip, IReadOnlyList<string> labels, int current, bool dimmed, double dotWidth = 52)
    {
        int last = labels.Count - 1;
        var accent = Accent;
        strip.Children.Clear();
        strip.ColumnDefinitions.Clear();
        strip.Padding = new Thickness(8, 0, 8, 0);
        strip.Opacity = dimmed ? 0.4 : 1;
        var faint = Color.FromArgb(64, 128, 128, 128);
        for (int i = 0; i < labels.Count; i++)
        {
            if (i > 0)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var connector = new Rectangle
                {
                    Height = 2, Margin = new Thickness(4, 6, 4, 0), VerticalAlignment = VerticalAlignment.Top,
                    Fill = new SolidColorBrush(i <= current ? Color.FromArgb(153, accent.R, accent.G, accent.B) : Color.FromArgb(51, 128, 128, 128)),
                };
                Grid.SetColumn(connector, strip.ColumnDefinitions.Count - 1);
                strip.Children.Add(connector);
            }
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(dotWidth) });
            Color fill = i < current ? accent : i == current ? (current == last ? Green : accent) : faint;
            var dot = new Grid { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Center };
            dot.Children.Add(new Ellipse { Fill = new SolidColorBrush(fill) });
            if (i < current || (i == last && current == last))
                dot.Children.Add(new FontIcon { Glyph = "", FontSize = 8, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var stop = new StackPanel { Spacing = 3 };
            stop.Children.Add(dot);
            var label = new TextBlock
            {
                Text = labels[i], FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center,
                FontWeight = i == current ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            };
            if (i != current) label.Foreground = Secondary;
            stop.Children.Add(label);
            Grid.SetColumn(stop, strip.ColumnDefinitions.Count - 1);
            strip.Children.Add(stop);
        }
    }

    /// <summary>One centred state: an icon or a spinner, a title, a sentence
    /// or two, and optionally a control that acts on what the card describes.</summary>
    public static Border StateCard(string? glyph, Brush tint, bool spinning, string title, string message,
        double iconSize = 28, UIElement? accessory = null)
    {
        var stack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (spinning || glyph == null)
            stack.Children.Add(new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center });
        else
            stack.Children.Add(new FontIcon { Glyph = glyph, FontSize = iconSize, Foreground = tint, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = message, FontSize = 11, Foreground = Secondary, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (accessory is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            fe.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(fe);
        }
        return Card(stack, new Thickness(14));
    }

    /// <summary>A failure dressed to its severity: detection stumbles read as
    /// ordinary, a failure after bytes moved gets the warning.</summary>
    public static Border FailureCard(FirmwareInstallError error) => StateCard(
        error.IsMundane ? "" : "", new SolidColorBrush(Orange), false,
        error.IsMundane ? "Not quite ready" : "The update did not complete", error.Message);

    /// <summary>The write: who, what, how far, and a line saying the
    /// alarming-looking ending (the drive vanishing) is the normal one. Returns
    /// the bar and percentage so progress can move them in place.</summary>
    public static (Border Card, ProgressBar Bar, TextBlock Percent) WritingCard(double fraction, string? boardName, string version)
    {
        var stack = new Grid { RowSpacing = 10 };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            stack.RowDefinitions.Add(new RowDefinition { Height = h });
        var title = new Grid();
        title.Children.Add(new TextBlock { Text = "Writing firmware", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var percent = new TextBlock
        {
            Text = $"{Math.Round(fraction * 100)}%", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            FontFamily = new FontFamily("Cascadia Code, Consolas"), Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Right,
        };
        title.Children.Add(percent);
        stack.Children.Add(title);
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = fraction * 100 };
        Grid.SetRow(bar, 1);
        stack.Children.Add(bar);
        var facts = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
        if (boardName != null) facts.Children.Add(ValueRow("Board", boardName));
        facts.Children.Add(ValueRow("Firmware", version));
        Grid.SetRow(facts, 2);
        stack.Children.Add(facts);
        var note = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        note.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Top });
        note.Children.Add(new TextBlock
        {
            Text = "Near the end the board restarts itself and its drive disappears. That is normal; do not unplug it.",
            FontSize = 10, Foreground = Secondary, TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
        });
        Grid.SetRow(note, 4);
        stack.Children.Add(note);
        return (Card(stack, new Thickness(14)), bar, percent);
    }

    /// <summary>The BOOTSEL instruction, for anyone who may need to put a board
    /// into bootloader mode by hand.</summary>
    public static FrameworkElement BootselHint()
    {
        var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        hint.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Top });
        hint.Children.Add(new TextBlock
        {
            Text = "Hold the BOOTSEL button on your Pico-compatible device while plugging it into your computer.",
            FontSize = 10, Foreground = Secondary, TextWrapping = TextWrapping.Wrap, MaxWidth = 400,
        });
        return hint;
    }
}
