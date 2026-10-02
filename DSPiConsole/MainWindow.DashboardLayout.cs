using DSPiConsole.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// The dashboard layout: a gear in the corner of every card, shown on hover,
/// opening a choice of Auto (as many cards per row as the window fits) or 1 to
/// 3 per row. After the macOS Console's DashboardCardFrame and
/// DashboardLayoutPanel.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>A card with the layout gear over its top-right corner, faded
    /// rather than removed so the open popover keeps its anchor.</summary>
    private FrameworkElement WithLayoutGear(FrameworkElement card)
    {
        var host = new Grid();
        host.Children.Add(card);
        var gear = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 11 },
            Padding = new Thickness(4, 3, 4, 3),
            MinWidth = 0, MinHeight = 0,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 6, 0),
            Opacity = 0,
            IsHitTestVisible = false,
            IsTabStop = false,
        };
        ToolTipService.SetToolTip(gear, "Dashboard layout");
        bool hovered = false, open = false;
        void Show()
        {
            bool shown = hovered || open;
            gear.Opacity = shown ? 1 : 0;
            gear.IsHitTestVisible = shown;
            // Keyboard focus never lands on a gear that cannot be seen.
            gear.IsTabStop = shown;
        }
        host.PointerEntered += (_, _) => { hovered = true; Show(); };
        host.PointerExited += (_, _) => { hovered = false; Show(); };
        gear.Click += (_, _) =>
        {
            var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };
            flyout.Content = DashboardLayoutPanel(flyout.Hide);
            flyout.Opened += (_, _) => { open = true; Show(); };
            flyout.Closed += (_, _) => { open = false; Show(); };
            flyout.ShowAt(gear);
        };
        host.Children.Add(gear);
        return host;
    }

    private FrameworkElement DashboardLayoutPanel(Action close)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        int chosen = Math.Clamp(AppSettings.Instance.DashboardCardsPerRow, 0, 3);
        var panel = new StackPanel { Width = 220, Spacing = 8, Padding = new Thickness(0, 0, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "DASHBOARD LAYOUT", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = secondary,
        });
        var tiles = new Grid { ColumnSpacing = 6 };
        for (int n = 0; n <= 3; n++)
        {
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tile = LayoutTile(n, n == chosen, secondary, close);
            Grid.SetColumn(tile, n);
            tiles.Children.Add(tile);
        }
        panel.Children.Add(tiles);
        panel.Children.Add(new TextBlock
        {
            Text = chosen == 0 ? "Fits as many cards per row as the window allows."
                 : $"Up to {chosen} {(chosen == 1 ? "card" : "cards")} per row.",
            FontSize = 10, Foreground = secondary, TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private Button LayoutTile(int n, bool on, Brush secondary, Action close)
    {
        var accent = (Color)Application.Current.Resources["SystemAccentColor"];
        var tint = on ? new SolidColorBrush(accent) : secondary;
        FrameworkElement glyph;
        if (n == 0)
        {
            glyph = new FontIcon { Glyph = "", FontSize = 11, Foreground = tint, Width = 22, Height = 13 };
        }
        else
        {
            // Two rows of cells in n columns: a picture of the layout.
            var cells = new Grid { Width = 22, Height = 13, RowSpacing = 2, ColumnSpacing = 2 };
            cells.RowDefinitions.Add(new RowDefinition());
            cells.RowDefinitions.Add(new RowDefinition());
            for (int c = 0; c < n; c++)
            {
                cells.ColumnDefinitions.Add(new ColumnDefinition());
                for (int r = 0; r < 2; r++)
                {
                    var block = new Rectangle { RadiusX = 1.5, RadiusY = 1.5, Fill = tint };
                    Grid.SetRow(block, r);
                    Grid.SetColumn(block, c);
                    cells.Children.Add(block);
                }
            }
            glyph = cells;
        }
        var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock
        {
            Text = n == 0 ? "Auto" : n.ToString(), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, Foreground = tint,
            FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        });
        var tile = new Button
        {
            Content = content,
            Height = 38, MinHeight = 0, MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(on ? Color.FromArgb(41, accent.R, accent.G, accent.B) : Color.FromArgb(13, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(on ? Color.FromArgb(178, accent.R, accent.G, accent.B) : Colors.Transparent),
            BorderThickness = new Thickness(1),
        };
        ToolTipService.SetToolTip(tile, n == 0 ? "Fit cards to the window width" : $"{n} {(n == 1 ? "card" : "cards")} per row");
        tile.Click += (_, _) =>
        {
            var s = AppSettings.Instance;
            s.DashboardCardsPerRow = n;
            s.Save();
            DashboardPanel.CardsPerRow = n;
            close();
        };
        return tile;
    }
}
