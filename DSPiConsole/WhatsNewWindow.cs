using DSPiConsole.Controls;
using DSPiConsole.Core.Onboarding;
using DSPiConsole.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DSPiConsole;

/// <summary>
/// The release notes: shown once after an update, and from Help whenever
/// asked. Opening it marks this version's notes read. After the macOS
/// Console's WhatsNewView.
/// </summary>
public sealed class WhatsNewWindow : Window
{
    public WhatsNewWindow(IReadOnlyList<ReleaseNotes>? releases = null)
    {
        ToolWindowChrome.Apply(this, "What's New", 460, 440);
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        releases ??= Onboarding.ReleaseNotes;

        var list = new StackPanel { Spacing = 24, Padding = new Thickness(20) };
        foreach (var release in releases)
        {
            var section = new StackPanel { Spacing = 10 };
            var heading = new Grid { ColumnSpacing = 12 };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(new TextBlock { Text = release.Headline, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var version = new TextBlock { Text = release.Version, FontSize = 11, Foreground = secondary, VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetColumn(version, 1);
            heading.Children.Add(version);
            section.Children.Add(heading);
            foreach (var item in release.Items)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock { Text = "•", Foreground = secondary });
                var text = new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                section.Children.Add(row);
            }
            list.Children.Add(section);
        }
        if (releases.Count == 0)
            list.Children.Add(new TextBlock { Text = "No release notes are available in this build.", Foreground = secondary });

        var done = new Button { Content = "Done", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        done.Click += (_, _) => Close();
        var root = new Grid { Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new ScrollViewer { Content = list });
        var rule = new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };
        Grid.SetRow(rule, 1);
        root.Children.Add(rule);
        var footer = new Border { Padding = new Thickness(16), Child = done };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;

        Onboarding.MarkReleaseNotesRead();
    }
}
