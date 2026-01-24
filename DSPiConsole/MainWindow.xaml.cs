using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using DSPiConsole.Dialogs;
using DSPiConsole.Services;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using WinRT;

namespace DSPiConsole;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    public IReadOnlyList<Channel> InputChannels => Channel.Inputs;
    public IReadOnlyList<Channel> OutputChannels => Channel.Outputs;

    private Channel? _selectedChannel;
    private bool _isUpdatingDelay;
    private MicaController? _micaController;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;

    // Simple channel selection: 0 = dashboard, 1-5 = channel index
    private int _selectedChannelIndex = 0;
    private readonly List<ListViewItem> _channelListItems = new();

    public MainWindow()
    {
        InitializeComponent();
        this.ExtendsContentIntoTitleBar = true;
       // this.SetTitleBar(AppTitleBar);

        ViewModel = new MainViewModel();
        BodePlot.DataContext = ViewModel;

        // Set window size
        var appWindow = GetAppWindow();
        if (appWindow != null)
        {
            appWindow.Resize(new Windows.Graphics.SizeInt32(1000, 825));
            appWindow.Title = "DSPi Console";
            appWindow.TitleBar.BackgroundColor = Microsoft.UI.Colors.Black;
        }

        // Try to apply Mica backdrop, fall back to Acrylic
        TrySetSystemBackdrop();

        // Initialize channel lists
        InitializeChannelLists();

        // Initialize legend
        InitializeLegend();

        // Initialize dashboard
        InitializeDashboard();

        // Subscribe to ViewModel events
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.FiltersChanged += (_, _) =>
        {
            BodePlot.Invalidate();
            // Refresh dashboard if it's visible
            if (DashboardPanel.Visibility == Visibility.Visible)
            {
                InitializeDashboard();
            }
        };
        ViewModel.VisibilityChanged += (_, _) =>
        {
            UpdateLegend();
            BodePlot.Invalidate();
        };

        // Initial UI state
        UpdateConnectionStatus();
        UpdatePreampDisplay();
        UpdateBypassButton();

        // Initialize AutoEQ (load database in background)
        _ = InitializeAutoEQAsync();
    }

    private async Task InitializeAutoEQAsync()
    {
        await AutoEQManager.Instance.LoadDatabaseAsync();
        DispatcherQueue.TryEnqueue(RefreshAutoEQFavoritesMenu);
    }

    private AppWindow? GetAppWindow()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private bool TrySetSystemBackdrop()
    {
        if (MicaController.IsSupported())
        {
            _backdropConfig = new SystemBackdropConfiguration();
            _micaController = new MicaController();

            Activated += (s, e) =>
            {
                _backdropConfig.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated;
            };

            ((FrameworkElement)Content).ActualThemeChanged += (s, e) =>
            {
                if (_backdropConfig != null)
                {
                    SetConfigurationSourceTheme();
                }
            };

            SetConfigurationSourceTheme();
            _micaController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
            _micaController.SetSystemBackdropConfiguration(_backdropConfig);

            // Apply semi-transparent background to sidebar for Mica effect
            SidebarGrid.Background = new SolidColorBrush(Color.FromArgb(200, 32, 32, 32));

            return true;
        }
        
        if (DesktopAcrylicController.IsSupported())
        {
            _backdropConfig = new SystemBackdropConfiguration();
            _acrylicController = new DesktopAcrylicController();

            Activated += (s, e) =>
            {
                _backdropConfig.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated;
            };

            ((FrameworkElement)Content).ActualThemeChanged += (s, e) =>
            {
                if (_backdropConfig != null)
                {
                    SetConfigurationSourceTheme();
                }
            };

            SetConfigurationSourceTheme();
            _acrylicController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);

            SidebarGrid.Background = new SolidColorBrush(Color.FromArgb(180, 32, 32, 32));

            return true;
        }

        // Fallback - solid color
        SidebarGrid.Background = new SolidColorBrush(Color.FromArgb(255, 40, 40, 44));
        return false;
    }

    private void SetConfigurationSourceTheme()
    {
        if (_backdropConfig == null) return;

        _backdropConfig.Theme = ((FrameworkElement)Content).ActualTheme switch
        {
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            ElementTheme.Light => SystemBackdropTheme.Light,
            _ => SystemBackdropTheme.Default
        };
    }

    private void InitializeChannelLists()
    {
        // Build channel list items programmatically
        // Index 0 = dashboard (no item), 1-5 = channels
        _channelListItems.Clear();

        InputChannelsList.Items.Clear();
        int index = 1;
        foreach (var channel in Channel.Inputs)
        {
            var item = CreateChannelListItem(channel, index++);
            _channelListItems.Add(item);
            InputChannelsList.Items.Add(item);
        }

        OutputChannelsList.Items.Clear();
        foreach (var channel in Channel.Outputs)
        {
            var item = CreateChannelListItem(channel, index++);
            _channelListItems.Add(item);
            OutputChannelsList.Items.Add(item);
        }
    }

    private ListViewItem CreateChannelListItem(Channel channel, int index)
    {
        // Store both channel and index in Tag
        var item = new ListViewItem { Tag = (channel, index) };
        item.Tapped += OnChannelItemTapped;

        var grid = new Grid { Height = 32, Padding = new Thickness(4, 0, 4, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = channel.Name,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nameText, 0);
        grid.Children.Add(nameText);

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(51, channel.Color.R, channel.Color.G, channel.Color.B)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 2, 8, 2)
        };
        var badgeText = new TextBlock
        {
            Text = channel.Descriptor,
            FontSize = 9,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(channel.Color)
        };
        badge.Child = badgeText;
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        item.Content = grid;
        return item;
    }

    private void InitializeLegend()
    {
        LegendPanel.Children.Clear();

        foreach (var channel in Channel.All)
        {
            var btn = new Button
            {
                Tag = channel,
                Padding = new Thickness(8, 4, 8, 4),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0)
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

            var indicator = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = new SolidColorBrush(channel.Color)
            };

            var label = new TextBlock
            {
                Text = channel.ShortName,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold
            };

            panel.Children.Add(indicator);
            panel.Children.Add(label);
            btn.Content = panel;

            btn.Click += (s, e) =>
            {
                if (s is Button b && b.Tag is Channel ch)
                {
                    ViewModel.ToggleChannelVisibility(ch);
                }
            };

            LegendPanel.Children.Add(btn);
        }

        UpdateLegend();
    }

    private void UpdateLegend()
    {
        foreach (var child in LegendPanel.Children)
        {
            if (child is Button btn && btn.Tag is Channel channel)
            {
                bool isVisible = ViewModel.GetChannelVisibility(channel);
                var panel = btn.Content as StackPanel;
                if (panel != null)
                {
                    var ellipse = panel.Children[0] as Ellipse;
                    var text = panel.Children[1] as TextBlock;

                    if (ellipse != null)
                    {
                        ellipse.Fill = new SolidColorBrush(isVisible ? channel.Color : Colors.Gray);
                        ellipse.Opacity = isVisible ? 1.0 : 0.5;
                    }

                    if (text != null)
                    {
                        text.Opacity = isVisible ? 1.0 : 0.5;
                    }
                }

                btn.Background = new SolidColorBrush(
                    isVisible ? Color.FromArgb(38, channel.Color.R, channel.Color.G, channel.Color.B) : Colors.Transparent);
            }
        }
    }

    private void InitializeDashboard()
    {
        DashboardPanel.Children.Clear();

        // Stereo Input Card
        DashboardPanel.Children.Add(CreateStereoDashboardCard("STEREO INPUT (USB)", Channel.MasterLeft, Channel.MasterRight, false));

        // Bottom row with SPDIF and Sub
        var bottomRow = new Grid();
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        bottomRow.ColumnSpacing = 16;

        var spdifCard = CreateStereoDashboardCard("STEREO OUTPUT (SPDIF)", Channel.OutLeft, Channel.OutRight, true);
        Grid.SetColumn(spdifCard, 0);
        bottomRow.Children.Add(spdifCard);

        var subCard = CreateMonoDashboardCard(Channel.Sub);
        Grid.SetColumn(subCard, 1);
        bottomRow.Children.Add(subCard);

        DashboardPanel.Children.Add(bottomRow);
    }

    private Border CreateStereoDashboardCard(string title, Channel left, Channel right, bool showDelay)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(153, 45, 45, 48)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)),
            BorderThickness = new Thickness(1)
        };

        var mainStack = new StackPanel();

        // Header row
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        headerGrid.Children.Add(CreateChannelHeader(left, showDelay, 0));
        headerGrid.Children.Add(CreateChannelHeader(right, showDelay, 1));

        mainStack.Children.Add(headerGrid);
        mainStack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)) });

        // Filter rows
        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftFilters = CreateDashboardFilterList(left);
        Grid.SetColumn(leftFilters, 0);
        contentGrid.Children.Add(leftFilters);

        var divider = new Border { Background = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)) };
        Grid.SetColumn(divider, 1);
        contentGrid.Children.Add(divider);

        var rightFilters = CreateDashboardFilterList(right);
        Grid.SetColumn(rightFilters, 2);
        contentGrid.Children.Add(rightFilters);

        mainStack.Children.Add(contentGrid);
        card.Child = mainStack;

        return card;
    }

    private Border CreateChannelHeader(Channel channel, bool showDelay, int column)
    {
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(25, channel.Color.R, channel.Color.G, channel.Color.B)),
            Padding = new Thickness(8)
        };
        Grid.SetColumn(header, column);

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        panel.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(channel.Color)
        });

        panel.Children.Add(new TextBlock
        {
            Text = channel.Name,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(channel.Color)
        });

        if (showDelay)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Delay: {ViewModel.GetChannelDelay(channel):F0}ms",
                FontSize = 9,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Foreground = new SolidColorBrush(Colors.Gray),
                Margin = new Thickness(8, 0, 0, 0)
            });
        }

        header.Child = panel;
        return header;
    }

    private StackPanel CreateDashboardFilterList(Channel channel)
    {
        var stack = new StackPanel();
        var filters = ViewModel.GetFilters(channel);

        for (int i = 0; i < filters.Count; i++)
        {
            var row = CreateDashboardFilterRow(i + 1, filters[i], channel.Color);
            row.Background = new SolidColorBrush(i % 2 == 0 ? Color.FromArgb(8, 255, 255, 255) : Colors.Transparent);
            stack.Children.Add(row);
        }

        return stack;
    }

    private Grid CreateDashboardFilterRow(int band, FilterParams p, Color color)
    {
        var grid = new Grid { Height = 24, Padding = new Thickness(8, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        bool isActive = p.Type != FilterType.Flat;

        var bandText = new TextBlock
        {
            Text = band.ToString(),
            FontSize = 10,
            FontFamily = new FontFamily("Cascadia Code"),
            Foreground = new SolidColorBrush(Color.FromArgb(178, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(bandText, 0);
        grid.Children.Add(bandText);

        var typeText = new TextBlock
        {
            Text = p.Type.GetShortName(),
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(isActive ? color : Color.FromArgb(102, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(typeText, 1);
        grid.Children.Add(typeText);

        var valuesPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };

        if (isActive)
        {
            valuesPanel.Children.Add(new TextBlock
            {
                Text = $"{p.Frequency:F0}",
                FontSize = 10,
                FontFamily = new FontFamily("Cascadia Code"),
                VerticalAlignment = VerticalAlignment.Center
            });
            valuesPanel.Children.Add(new TextBlock
            {
                Text = "Hz",
                FontSize = 8,
                Foreground = new SolidColorBrush(Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center
            });

            if (p.Type.HasGain())
            {
                valuesPanel.Children.Add(new TextBlock
                {
                    Text = $" {p.Gain:+0.0;-0.0}",
                    FontSize = 10,
                    FontFamily = new FontFamily("Cascadia Code"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                });
                valuesPanel.Children.Add(new TextBlock
                {
                    Text = "dB",
                    FontSize = 8,
                    Foreground = new SolidColorBrush(Colors.Gray),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (p.Type.HasQ())
            {
                valuesPanel.Children.Add(new TextBlock
                {
                    Text = $" {p.Q:F1}",
                    FontSize = 10,
                    FontFamily = new FontFamily("Cascadia Code"),
                    Foreground = new SolidColorBrush(Colors.Gray),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                });
                valuesPanel.Children.Add(new TextBlock
                {
                    Text = "Q",
                    FontSize = 8,
                    Foreground = new SolidColorBrush(Colors.Gray),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }
        else
        {
            valuesPanel.Children.Add(new TextBlock
            {
                Text = "—",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        Grid.SetColumn(valuesPanel, 2);
        grid.Children.Add(valuesPanel);

        return grid;
    }

    private Border CreateMonoDashboardCard(Channel channel)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(153, 45, 45, 48)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(76, channel.Color.R, channel.Color.G, channel.Color.B)),
            BorderThickness = new Thickness(1)
        };

        var stack = new StackPanel();
        stack.Children.Add(CreateChannelHeader(channel, true, 0));
        stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(51, channel.Color.R, channel.Color.G, channel.Color.B)) });
        stack.Children.Add(CreateDashboardFilterList(channel));

        card.Child = stack;
        return card;
    }

    private void ShowChannelEditor(Channel channel)
    {
        _selectedChannel = channel;
        DashboardPanel.Visibility = Visibility.Collapsed;
        ChannelEditorPanel.Visibility = Visibility.Visible;

        ChannelEditorPanel.Children.Clear();

        // Title
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = $"{channel.Name} Filters",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        Grid.SetColumn(title, 0);
        titleRow.Children.Add(title);

        if (channel.Id == ChannelId.MasterLeft || channel.Id == ChannelId.MasterRight)
        {
            var clearBtn = new Button { Content = "Clear All Master PEQ" };
            clearBtn.Click += (s, e) => ViewModel.ClearAllMasterCommand.Execute(null);
            Grid.SetColumn(clearBtn, 1);
            titleRow.Children.Add(clearBtn);
        }

        ChannelEditorPanel.Children.Add(titleRow);

        // Delay control for output channels
        if (channel.IsOutput)
        {
            var delayPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(128, 45, 45, 48)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 8, 0, 0)
            };

            var delayGrid = new Grid();
            delayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            delayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            delayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            delayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            delayGrid.Children.Add(new FontIcon { Glyph = "\uED5A", Foreground = new SolidColorBrush(Colors.Gray), Margin = new Thickness(0, 0, 8, 0) });

            var delayLabel = new TextBlock { Text = "Output Delay:", VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.Medium };
            Grid.SetColumn(delayLabel, 1);
            delayGrid.Children.Add(delayLabel);

            var delaySlider = new Slider { Minimum = 0, Maximum = 170, Value = ViewModel.GetChannelDelay(channel), Margin = new Thickness(12, 0, 12, 0) };
            delaySlider.Tag = channel;
            delaySlider.ValueChanged += OnDelaySliderChanged;
            Grid.SetColumn(delaySlider, 2);
            delayGrid.Children.Add(delaySlider);

            var delayValuePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var delayTextBox = new TextBox { Width = 50, Text = ViewModel.GetChannelDelay(channel).ToString("F0"), Tag = channel };
            delayTextBox.TextChanged += OnDelayTextChanged;
            delayValuePanel.Children.Add(delayTextBox);
            delayValuePanel.Children.Add(new TextBlock { Text = "ms", VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Colors.Gray) });
            Grid.SetColumn(delayValuePanel, 3);
            delayGrid.Children.Add(delayValuePanel);

            delayPanel.Child = delayGrid;
            ChannelEditorPanel.Children.Add(delayPanel);
        }

        // Filter rows
        var filters = ViewModel.GetFilters(channel);
        for (int i = 0; i < filters.Count; i++)
        {
            ChannelEditorPanel.Children.Add(CreateFilterEditorRow(channel, i, filters[i]));
        }
    }

    private Border CreateFilterEditorRow(Channel channel, int bandIndex, FilterParams p)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(128, 45, 45, 48)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 0)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnSpacing = 12;

        // Band label
        var bandLabel = new TextBlock
        {
            Text = $"Band {bandIndex + 1}",
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Code"),
            Foreground = new SolidColorBrush(Colors.Gray),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(bandLabel, 0);
        grid.Children.Add(bandLabel);

        // Filter type selector
        var typeCombo = new ComboBox { Width = 120, Tag = (channel, bandIndex) };
        foreach (var type in Enum.GetValues<FilterType>())
        {
            typeCombo.Items.Add(new ComboBoxItem { Content = type.GetDisplayName(), Tag = type });
        }
        typeCombo.SelectedIndex = (int)p.Type;
        typeCombo.SelectionChanged += OnFilterTypeChanged;
        Grid.SetColumn(typeCombo, 1);
        grid.Children.Add(typeCombo);

        // Frequency
        if (p.Type != FilterType.Flat)
        {
            var freqPanel = CreateValueField("Hz", p.Frequency, 70, (channel, bandIndex, "freq"));
            Grid.SetColumn(freqPanel, 2);
            grid.Children.Add(freqPanel);
        }

        // Q (only for peaking)
        if (p.Type == FilterType.Peaking)
        {
            var qPanel = CreateValueField("Q", p.Q, 50, (channel, bandIndex, "q"));
            Grid.SetColumn(qPanel, 3);
            grid.Children.Add(qPanel);
        }

        // Gain (for peaking, low shelf, high shelf)
        if (p.Type.HasGain())
        {
            var gainPanel = CreateValueField("dB", p.Gain, 50, (channel, bandIndex, "gain"));
            Grid.SetColumn(gainPanel, 4);
            grid.Children.Add(gainPanel);
        }

        row.Child = grid;
        return row;
    }

    private StackPanel CreateValueField(string label, float value, double width, (Channel channel, int band, string param) tag)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var textBox = new TextBox
        {
            Width = width,
            Text = value.ToString("F1"),
            Tag = tag
        };
        textBox.LostFocus += OnFilterValueChanged;
        textBox.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                OnFilterValueChanged(s, null!);
            }
        };

        panel.Children.Add(textBox);
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.Gray),
            VerticalAlignment = VerticalAlignment.Center
        });

        return panel;
    }

    private void ShowDashboard()
    {
        _selectedChannel = null;
        ChannelEditorPanel.Visibility = Visibility.Collapsed;
        DashboardPanel.Visibility = Visibility.Visible;
        InitializeDashboard(); // Refresh
    }

    #region Event Handlers

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsDeviceConnected):
                    UpdateConnectionStatus();
                    break;
                case nameof(MainViewModel.ErrorMessage):
                    UpdateConnectionStatus();
                    break;
                case nameof(MainViewModel.PreampDb):
                    UpdatePreampDisplay();
                    break;
                case nameof(MainViewModel.Bypass):
                    UpdateBypassButton();
                    break;
                case nameof(MainViewModel.Status):
                    UpdateMeters();
                    break;
            }
        });
    }

    private void UpdateConnectionStatus()
    {
        ConnectionIndicator.Fill = new SolidColorBrush(ViewModel.IsDeviceConnected ? Colors.LimeGreen : Colors.Red);
        ConnectionStatusText.Text = ViewModel.IsDeviceConnected ? "Connected" : (ViewModel.ErrorMessage ?? "Disconnected");
    }

    private void UpdatePreampDisplay()
    {
        if (!_isUpdatingDelay)
        {
            PreampSlider.Value = ViewModel.PreampDb;
        }
        PreampValueText.Text = $"{ViewModel.PreampDb:F1} dB";
    }

    private void UpdateBypassButton()
    {
        BypassButton.IsChecked = ViewModel.Bypass;
    }

    private void UpdateMeters()
    {
        var status = ViewModel.Status;
        MeterMasterL.Level = status.Peaks[0];
        MeterMasterR.Level = status.Peaks[1];
        MeterOutL.Level = status.Peaks[2];
        MeterOutR.Level = status.Peaks[3];
        MeterSub.Level = status.Peaks[4];

        // Workaround: firmware reports 100% for Core 1 when idle/no audio
        // Treat 0%/100% as uninitialized and show 0% for both
        if (status.Cpu0Load == 0 && status.Cpu1Load == 100)
        {
            Cpu0Meter.Load = 0;
            Cpu1Meter.Load = 0;
        }
        else
        {
            Cpu0Meter.Load = status.Cpu0Load;
            Cpu1Meter.Load = status.Cpu1Load;
        }
    }

    private void OnChannelItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not ListViewItem item || item.Tag is not (Channel channel, int index))
            return;

        if (_selectedChannelIndex == index)
        {
            // Same channel clicked - go back to dashboard
            _selectedChannelIndex = 0;
            UpdateChannelListSelection();
            ViewModel.UpdateChannelSelection(null);
            ShowDashboard();
        }
        else
        {
            // Different channel clicked - select it
            _selectedChannelIndex = index;
            UpdateChannelListSelection();
            ViewModel.UpdateChannelSelection(channel);
            ShowChannelEditor(channel);
        }
    }

    private void UpdateChannelListSelection()
    {
        // Clear all selections first
        InputChannelsList.SelectedItem = null;
        OutputChannelsList.SelectedItem = null;

        // If a channel is selected (index > 0), highlight it
        if (_selectedChannelIndex > 0 && _selectedChannelIndex <= _channelListItems.Count)
        {
            var item = _channelListItems[_selectedChannelIndex - 1];
            if (InputChannelsList.Items.Contains(item))
                InputChannelsList.SelectedItem = item;
            else if (OutputChannelsList.Items.Contains(item))
                OutputChannelsList.SelectedItem = item;
        }
    }

    private void OnPreampSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Math.Abs(ViewModel.PreampDb - (float)e.NewValue) > 0.1f)
        {
            ViewModel.PreampDb = (float)e.NewValue;
        }
    }

    private void OnBypassToggled(object sender, RoutedEventArgs e)
    {
        ViewModel.Bypass = BypassButton.IsChecked == true;
    }

    private void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ReconnectCommand.Execute(null);
    }

    private void OnClearAllMasterClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearAllMasterCommand.Execute(null);
    }

    private void OnDelaySliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingDelay) return;
        if (sender is Slider slider && slider.Tag is Channel channel)
        {
            ViewModel.SetDelay((int)channel.Id, (float)e.NewValue);
        }
    }

    private void OnDelayTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingDelay) return;
        if (sender is TextBox textBox && textBox.Tag is Channel channel)
        {
            if (float.TryParse(textBox.Text, out float value))
            {
                _isUpdatingDelay = true;
                ViewModel.SetDelay((int)channel.Id, Math.Clamp(value, 0, 170));
                _isUpdatingDelay = false;
            }
        }
    }

    private void OnFilterTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.Tag is (Channel channel, int bandIndex))
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is FilterType newType)
            {
                var filters = ViewModel.GetFilters(channel);
                if (bandIndex < filters.Count)
                {
                    var p = filters[bandIndex].Clone();
                    p.Type = newType;
                    ViewModel.SetFilter((int)channel.Id, bandIndex, p);

                    // Refresh the row
                    if (_selectedChannel != null)
                    {
                        ShowChannelEditor(_selectedChannel);
                    }
                }
            }
        }
    }

    private void OnFilterValueChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.Tag is (Channel channel, int bandIndex, string param))
        {
            if (float.TryParse(textBox.Text, out float value))
            {
                var filters = ViewModel.GetFilters(channel);
                if (bandIndex < filters.Count)
                {
                    var p = filters[bandIndex].Clone();

                    switch (param)
                    {
                        case "freq":
                            p.Frequency = Math.Clamp(value, 20, 20000);
                            break;
                        case "q":
                            p.Q = Math.Clamp(value, 0.1f, 20);
                            break;
                        case "gain":
                            p.Gain = Math.Clamp(value, -20, 20);
                            break;
                    }

                    ViewModel.SetFilter((int)channel.Id, bandIndex, p);
                }
            }
        }
    }

    #endregion

    #region Menu Handlers

    private async void OnCommitParametersClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Commit Parameters",
            Content = "Save current parameters to device?\n\nThis may cause a brief audio interruption.",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (!ViewModel.IsDeviceConnected)
            {
                await ShowErrorDialog("Not connected to device");
                return;
            }

            var flashResult = ViewModel.SaveParams();
            if (flashResult == Usb.FlashResult.Ok)
            {
                await ShowSuccessDialog("Parameters saved successfully");
            }
            else
            {
                await ShowErrorDialog("Failed to save parameters");
            }
        }
    }

    private async void OnRevertToSavedClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Revert to Saved",
            Content = "Revert to last saved parameters?\n\nCurrent unsaved changes will be lost.",
            PrimaryButtonText = "Revert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (!ViewModel.IsDeviceConnected)
            {
                await ShowErrorDialog("Not connected to device");
                return;
            }

            var flashResult = ViewModel.LoadParams();
            switch (flashResult)
            {
                case Usb.FlashResult.Ok:
                    await ShowSuccessDialog("Parameters reverted successfully");
                    break;
                case Usb.FlashResult.ErrNoData:
                    await ShowInfoDialog("No saved parameters found.\n\nThe device is using factory defaults.");
                    break;
                case Usb.FlashResult.ErrCrc:
                    await ShowErrorDialog("Saved data is corrupted");
                    break;
                default:
                    await ShowErrorDialog("Failed to load parameters");
                    break;
            }
        }
    }

    private async void OnFactoryResetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Factory Reset",
            Content = "Do you wish to clear all active parameters?\n\nThis will not overwrite your saved parameters unless you run 'Commit Parameters'.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (!ViewModel.IsDeviceConnected)
            {
                await ShowErrorDialog("Not connected to device");
                return;
            }

            var flashResult = ViewModel.FactoryResetParams();
            if (flashResult == Usb.FlashResult.Ok)
            {
                await ShowSuccessDialog("Factory reset complete");
            }
            else
            {
                await ShowErrorDialog("Failed to reset parameters");
            }
        }
    }

    private async Task ShowSuccessDialog(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Success",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task ShowErrorDialog(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Error",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task ShowInfoDialog(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Information",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion

    #region Import/Export Handlers

    private async void OnImportFiltersClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".txt");

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        try
        {
            var contents = await Windows.Storage.FileIO.ReadTextAsync(file);
            var result = FilterFileService.ParseFile(contents);

            if (result.Format == FilterFileFormat.Unknown)
            {
                await ShowErrorDialog("Could not parse filter file. Unsupported format.");
                return;
            }

            if (result.Format == FilterFileFormat.DSPiConsole && result.ChannelFilters != null)
            {
                await ImportMultiChannelFilters(result.ChannelFilters);
            }
            else if (result.Format == FilterFileFormat.REW && result.SingleChannelFilters != null)
            {
                await ImportSingleChannelFilters(result.SingleChannelFilters);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialog($"Failed to read file: {ex.Message}");
        }
    }

    private async Task ImportSingleChannelFilters(List<FilterParams> filters)
    {
        var dialog = new ChannelSelectionDialog { XamlRoot = Content.XamlRoot };
        dialog.ConfigureForSingleChannel(filters.Count);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            dialog.CollectSelectedChannels();
            foreach (var channelId in dialog.SelectedChannelIds)
            {
                ApplyFiltersToChannel(channelId, filters);
            }

            if (dialog.SelectedChannelIds.Count > 0)
            {
                await ShowSuccessDialog($"Filters imported to {dialog.SelectedChannelIds.Count} channel(s)");
            }
        }
    }

    private async Task ImportMultiChannelFilters(Dictionary<int, List<FilterParams>> channelFilters)
    {
        var dialog = new ChannelSelectionDialog { XamlRoot = Content.XamlRoot };
        dialog.ConfigureForMultiChannel(channelFilters.Keys);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            dialog.CollectSelectedChannels();
            foreach (var channelId in dialog.SelectedChannelIds)
            {
                if (channelFilters.TryGetValue(channelId, out var filters))
                {
                    ApplyFiltersToChannel(channelId, filters);
                }
            }

            if (dialog.SelectedChannelIds.Count > 0)
            {
                await ShowSuccessDialog("Filters imported successfully");
            }
        }
    }

    private void ApplyFiltersToChannel(int channelId, List<FilterParams> filters)
    {
        var channel = Channel.All.FirstOrDefault(c => (int)c.Id == channelId);
        if (channel == null) return;

        var bandCount = channel.BandCount;

        // Apply imported filters
        for (int i = 0; i < Math.Min(filters.Count, bandCount); i++)
        {
            ViewModel.SetFilter(channelId, i, filters[i].Clone());
        }

        // Clear remaining bands
        for (int i = filters.Count; i < bandCount; i++)
        {
            ViewModel.SetFilter(channelId, i, new FilterParams(FilterType.Flat, 1000, 0.707f, 0));
        }
    }

    private async void OnExportFiltersClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "DSPi Filters";
        picker.FileTypeChoices.Add("Text Files", new List<string> { ".txt" });

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            // Build channel data dictionary
            var channelData = new Dictionary<int, IReadOnlyList<FilterParams>>();
            foreach (var channel in Channel.All)
            {
                var filters = ViewModel.GetFilters(channel);
                channelData[(int)channel.Id] = filters.ToList();
            }

            var output = FilterFileService.GenerateExportString(channelData);
            await Windows.Storage.FileIO.WriteTextAsync(file, output);
            await ShowSuccessDialog("Filters exported successfully");
        }
        catch (Exception ex)
        {
            await ShowErrorDialog($"Failed to write file: {ex.Message}");
        }
    }

    #endregion

    #region AutoEQ Handlers

    private async void OnAutoEQBrowseClick(object sender, RoutedEventArgs e)
    {
        // Ensure database is loaded
        if (!AutoEQManager.Instance.IsLoaded)
        {
            await AutoEQManager.Instance.LoadDatabaseAsync();
        }

        if (!AutoEQManager.Instance.IsLoaded)
        {
            await ShowErrorDialog(AutoEQManager.Instance.ErrorMessage ?? "Failed to load AutoEQ database");
            return;
        }

        var dialog = new AutoEQBrowserDialog { XamlRoot = Content.XamlRoot };
        var result = await dialog.ShowAsync();

        // Always refresh favorites menu after dialog closes (user may have added/removed favorites)
        RefreshAutoEQFavoritesMenu();

        if (result == ContentDialogResult.Primary && dialog.SelectedProfile != null)
        {
            ApplyAutoEQProfile(dialog.SelectedProfile);
            await ShowSuccessDialog($"Applied profile: {dialog.SelectedProfile.DisplayName}");
        }
    }

    private void ApplyAutoEQProfile(HeadphoneEntry profile)
    {
        // Set preamp
        ViewModel.PreampDb = (float)profile.Preamp;

        // Convert and apply filters to both master channels
        var filters = AutoEQManager.ConvertFilters(profile);
        var masterChannels = new[] { (int)ChannelId.MasterLeft, (int)ChannelId.MasterRight };

        foreach (var channelId in masterChannels)
        {
            ApplyFiltersToChannel(channelId, filters);
        }
    }

    private void RefreshAutoEQFavoritesMenu()
    {
        AutoEQFavoritesMenu.Items.Clear();

        var favorites = AutoEQManager.Instance.Favorites;
        if (favorites.Count == 0)
        {
            var emptyItem = new MenuFlyoutItem
            {
                Text = "No favorites yet",
                IsEnabled = false
            };
            AutoEQFavoritesMenu.Items.Add(emptyItem);
        }
        else
        {
            foreach (var entry in favorites)
            {
                var item = new MenuFlyoutItem { Text = entry.DisplayName, Tag = entry };
                item.Click += OnAutoEQFavoriteClick;
                AutoEQFavoritesMenu.Items.Add(item);
            }

            AutoEQFavoritesMenu.Items.Add(new MenuFlyoutSeparator());

            var clearItem = new MenuFlyoutItem { Text = "Clear Favorites" };
            clearItem.Click += async (s, e) =>
            {
                AutoEQManager.Instance.ClearFavorites();
                RefreshAutoEQFavoritesMenu();
                await ShowInfoDialog("Favorites cleared");
            };
            AutoEQFavoritesMenu.Items.Add(clearItem);
        }
    }

    private async void OnAutoEQFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is HeadphoneEntry profile)
        {
            ApplyAutoEQProfile(profile);
            await ShowSuccessDialog($"Applied profile: {profile.DisplayName}");
        }
    }

    #endregion
}
