using DSPiConsole.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Dialogs;

public sealed partial class AutoEQBrowserDialog : ContentDialog
{
    private readonly AutoEQManager _manager = AutoEQManager.Instance;
    private List<HeadphoneEntry> _currentResults = new();
    private HeadphoneEntry? _selectedEntry;

    public HeadphoneEntry? SelectedProfile => _selectedEntry;

    public AutoEQBrowserDialog()
    {
        InitializeComponent();
        LoadEntries();
    }

    private void LoadEntries()
    {
        _currentResults = _manager.Entries.ToList();
        PopulateList(_currentResults);
    }

    private void PopulateList(IEnumerable<HeadphoneEntry> entries)
    {
        ResultsList.Items.Clear();

        foreach (var entry in entries.Take(100)) // Limit for performance
        {
            var item = CreateListItem(entry);
            ResultsList.Items.Add(item);
        }
    }

    private ListViewItem CreateListItem(HeadphoneEntry entry)
    {
        var item = new ListViewItem { Tag = entry };

        var grid = new Grid { Padding = new Thickness(8, 6, 8, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnSpacing = 12;

        // Form factor icon
        var icon = new FontIcon
        {
            Glyph = entry.FormFactorIcon,
            FontSize = 20,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 120, 215))
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        // Name and source
        var infoStack = new StackPanel { Spacing = 2 };

        var nameText = new TextBlock
        {
            Text = entry.DisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        infoStack.Children.Add(nameText);

        var detailsStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var sourceBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2)
        };
        sourceBadge.Child = new TextBlock
        {
            Text = entry.SourceDisplayName,
            FontSize = 10,
            Foreground = new SolidColorBrush(Colors.Gray)
        };
        detailsStack.Children.Add(sourceBadge);

        var filterCountText = new TextBlock
        {
            Text = $"{entry.Filters.Count} filters",
            FontSize = 10,
            Foreground = new SolidColorBrush(Colors.Gray),
            VerticalAlignment = VerticalAlignment.Center
        };
        detailsStack.Children.Add(filterCountText);

        infoStack.Children.Add(detailsStack);
        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        // Favorite button
        var favoriteBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            Tag = entry
        };
        var isFavorite = _manager.IsFavorite(entry);
        favoriteBtn.Content = new FontIcon
        {
            Glyph = isFavorite ? "\uEB52" : "\uEB51",
            FontSize = 14,
            Foreground = new SolidColorBrush(isFavorite ? Colors.Red : Colors.Gray)
        };
        favoriteBtn.Click += OnFavoriteClick;
        Grid.SetColumn(favoriteBtn, 2);
        grid.Children.Add(favoriteBtn);

        item.Content = grid;
        return item;
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var query = sender.Text;
            if (string.IsNullOrWhiteSpace(query))
            {
                _currentResults = _manager.Entries.ToList();
            }
            else
            {
                _currentResults = _manager.Search(query).ToList();
            }
            PopulateList(_currentResults);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is ListViewItem item && item.Tag is HeadphoneEntry entry)
        {
            _selectedEntry = entry;
            IsPrimaryButtonEnabled = true;
            UpdateSelectedInfo();
        }
        else
        {
            _selectedEntry = null;
            IsPrimaryButtonEnabled = false;
            SelectedInfoPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSelectedInfo()
    {
        if (_selectedEntry != null)
        {
            SelectedNameText.Text = _selectedEntry.DisplayName;
            SelectedDetailsText.Text = $"{_selectedEntry.SourceDisplayName} • {_selectedEntry.Filters.Count} filters";
            PreampText.Text = $"Preamp: {_selectedEntry.Preamp:+0.0;-0.0} dB";
            SelectedInfoPanel.Visibility = Visibility.Visible;
        }
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HeadphoneEntry entry)
        {
            _manager.ToggleFavorite(entry);

            // Update button appearance
            var isFavorite = _manager.IsFavorite(entry);
            if (btn.Content is FontIcon icon)
            {
                icon.Glyph = isFavorite ? "\uEB52" : "\uEB51";
                icon.Foreground = new SolidColorBrush(isFavorite ? Colors.Red : Colors.Gray);
            }
        }
    }
}
