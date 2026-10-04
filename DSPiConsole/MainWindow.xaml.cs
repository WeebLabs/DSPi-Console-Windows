using System.Globalization;
using System.Linq;
using DSPiConsole.Controls;
using DSPiConsole.Core.GraphEditing;
using DSPiConsole.Core.Models;
using DSPiConsole.Models;
using DSPiConsole.Dialogs;
using DSPiConsole.Services;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using WinRT;
using System.Runtime.InteropServices;

namespace DSPiConsole;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainViewModel ViewModel { get; }
    public IReadOnlyList<Channel> InputChannels => Channel.Inputs;
    public IReadOnlyList<Channel> OutputChannels => Channel.Outputs;

    private Channel? _selectedChannel;
    // Which filter page the channel editor's status-bar tab is showing.
    // false = PEQ, true = crossover (XO). Reset to PEQ whenever the editor
    // switches to a different channel (tracked by _filterPageChannelId).
    private bool _filterPageIsXover;
    private int _filterPageChannelId = -1;
    private Slider? _inputPreampSlider;
    private SliderDrag? _inputPreampDrag;
    private TextBox? _inputPreampValueText;
    private bool _isScrollAdjusting;
    private DateTime _lastFilterScrollTime = DateTime.MinValue;
    private bool _isUpdatingDelay;
    private bool _isUpdatingGain;
    private bool _closeConfirmed;
    private StatsWindow? _statsWindow;
    private InterruptMonitorWindow? _interruptMonitorWindow;
    private GraphWindow? _graphWindow;
    private LoudnessWindow? _loudnessWindow;
    private CrossfeedWindow? _crossfeedWindow;
    private PsychoacousticBassWindow? _psybassWindow;
    private SubharmonicSynthWindow? _subharmWindow;
    private TubeModellerWindow? _tubeWindow;
    private SpectrumAnalyserWindow? _spectrumWindow;
    private readonly Controls.Rta.SpectrumBarStrip _barStrip;
    private UpmixerWindow? _upmixerWindow;
    private VolumeLevellerWindow? _levellerWindow;
    private MatrixMixerWindow? _matrixMixerWindow;
    private TestSignalsWindow? _testSignalsWindow;
    private Settings.SettingsWindow? _settingsWindow;

    // Track output controls for live updates
    private TextBox? _currentGainTextBox;
    private TextBox? _currentDelayTextBox;
    private Slider? _currentGainSlider;
    private SliderDrag? _currentGainDrag;
    private ToggleButton? _currentMuteButton;
    private Slider? _currentDelaySlider;
    private SliderDrag? _currentDelayDrag;
    private TextBlock? _currentDelayUnitText;

    // Route indicator controls for current output channel
    private readonly Dictionary<int, Border> _currentRouteCircles = new();
    private readonly Dictionary<int, TextBlock> _currentRouteNameTexts = new();
    private readonly Dictionary<int, TextBox> _currentRouteGainTexts = new();
    private readonly Dictionary<int, TextBlock> _currentRouteInvTexts = new();
    private int _currentOutputIndex = -1;

    // Graph resize state
    private const double GraphMinHeight = 200;
    private const double GraphMaxHeight = 350;
    private bool _isResizingGraph;
    private double _graphResizeStartY;
    private double _graphResizeStartHeight;

    // Simple channel selection: 0 = dashboard, 1-5 = channel index
    private int _selectedChannelIndex = 0;
    private readonly List<ListViewItem> _channelListItems = new();
    private readonly Dictionary<int, TextBlock> _channelNameTexts = new();
    /// <summary>Starts the inline rename of a sidebar row, for Alt-click.</summary>
    private readonly Dictionary<int, Action> _sidebarRenamers = new();

    // Inline per-channel meters: keyed by ChannelId
    private readonly Dictionary<int, HorizontalMeterBar> _channelMeters = new();

    // Sidebar badge restyle callbacks (graph-visibility toggles): keyed by ChannelId
    private readonly Dictionary<int, Action> _channelBadgeStylers = new();

    /// <summary>
    /// Resolve a firmware-side channel ID back to the Channel object
    /// whose metadata the UI should display. <see cref="Channel.FromId"/>
    /// alone can't do this because <c>(ChannelId)6</c> means
    /// <c>Spdif3L</c> on RP2350 and PDM on RP2040 — same enum value,
    /// different Channel instance. Iterate the platform-aware input/
    /// output lists first so PDM on RP2040 lands on <see cref="Channel.PdmRp2040"/>
    /// (Name="PDM") rather than Spdif3L (Name="SPDIF 3 L").
    /// </summary>
    private Channel LookupChannelById(int channelId)
    {
        foreach (var ch in Channel.AllInputs)
            if ((int)ch.Id == channelId) return ch;
        foreach (var ch in ViewModel.ActiveOutputs)
            if ((int)ch.Id == channelId) return ch;
        return Channel.FromIndex(channelId);
    }

    // Preset combo guard
    private bool _isUpdatingPresetCombo;

    // Dashboard rebuild debounce
    private DispatcherTimer? _dashboardDebounce;

    // Live TextBlocks owned by the dashboard cards currently on screen, keyed by
    // channelId. Rebuilt from _dashboardCardTexts so they only ever reference
    // elements that are actually in the visual tree.
    private readonly Dictionary<int, TextBlock> _dashboardHeaderStats = new();
    private readonly Dictionary<int, TextBlock> _dashboardNameTexts = new();

    /// <summary>The live TextBlocks one dashboard card owns, keyed by channel id.
    /// Collected per card because UpdateDashboardCards builds every desired card
    /// but only inserts the new ones — registering globally from each freshly
    /// built card would leave the maps above pointing at the discarded (off-tree)
    /// twins of the cards that stayed put, silently freezing their stats.</summary>
    private sealed class DashboardCardTexts
    {
        public readonly Dictionary<int, TextBlock> Stats = new();
        public readonly Dictionary<int, TextBlock> Names = new();
    }

    // Per-card text registrations for the cards currently on screen: keyed by card key
    private readonly Dictionary<string, DashboardCardTexts> _dashboardCardTexts = new();

    // Pre-built output channel items: keyed by output index
    private readonly Dictionary<int, ListViewItem> _outputChannelItems = new();


    // Acrylic backdrop
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _configurationSource;

    public MainWindow()
    {
        InitializeComponent();
        this.ExtendsContentIntoTitleBar = true;

        SetupAcrylicBackdrop();
        this.SetTitleBar(AppTitleBar);

        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarDragRegion();
        TitleBarMenuButton.SizeChanged += (_, _) => UpdateTitleBarDragRegion();

        ViewModel = new MainViewModel();
        ViewModel.MasterPeqLinked = AppSettings.Instance.MasterPeqLinked;
        for (int pair = 1; pair <= 3; pair++)
            ViewModel.SetInputPairLinked(pair,
                AppSettings.Instance.InputPairLinkedExt is { Length: >= 3 } ext && ext[pair - 1]);
        BodePlot.DataContext = ViewModel;
        BodePlot.SetDottedInactiveEnabled(AppSettings.Instance.DottedInactiveChannels);
        // The spectrum's bars, between the graph's gripper and the page below,
        // in the row the legend pills once used.
        _barStrip = new Controls.Rta.SpectrumBarStrip(ViewModel, this, OpenSpectrumAnalyser) { Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(_barStrip, 2);
        ContentGrid.Children.Add(_barStrip);
        SyncLinkedPairGradient();
        UpdateGraphPopoutButtonMargin();

        // Set window size (scale for DPI)
        var appWindow = GetAppWindow();
        if (appWindow != null)
        {
            double dpiScale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
            appWindow.Resize(new Windows.Graphics.SizeInt32((int)(1000 * dpiScale), (int)(825 * dpiScale)));
            appWindow.Title = "DSPi Console";
            appWindow.Closing += OnAppWindowClosing;
            // The spectrum bars stop animating while the window is minimised.
            appWindow.Changed += (s, e) =>
            {
                if (e.DidPresenterChange || e.DidSizeChange)
                    _barStrip.HostActive = s.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            };
        }


        // Initialize channel lists
        InitializeChannelLists();

        // Initialize dashboard
        InitializeDashboard();

        // Subscribe to ViewModel events
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.FiltersChanged += (_, _) =>
        {
            BodePlot.Invalidate();
            ScheduleDashboardRefresh();
            if (_selectedChannel != null && !_isScrollAdjusting && !_isUpdatingGain && !_isUpdatingDelay)
            {
                if (_inlineFilterCommits > 0) RefreshPeqRowValuesInPlace();
                else ShowChannelEditor(_selectedChannel);
            }
        };
        // On-graph editing: the band list follows a graph drag through live
        // values, and shares the graph's selection and hover.
        ViewModel.GraphBandLive += OnGraphBandLive;
        ViewModel.PeqSelection.SelectionChanged += OnPeqSelectionChanged;
        ViewModel.PeqSelection.HoverChanged += UpdatePeqRowHighlights;
        ViewModel.PeqSelection.RevealRow += RevealPeqRow;
        // Bulk refreshes (preset load, factory reset, BULK_INVALIDATED) fire
        // FiltersChanged too, so the 50ms ScheduleDashboardRefresh debounce
        // would otherwise hold the dashboard back ~50ms while the BodePlot
        // animation is already racing ahead. The debounce exists to coalesce
        // EQ-drag thrashing — preset loads are single, intentional events,
        // so cancel the pending debounce and rebuild the dashboard
        // immediately on this dispatcher tick.
        ViewModel.BulkRefreshed += (_, _) =>
        {
            _dashboardDebounce?.Stop();
            if (DashboardPanel.Visibility == Visibility.Visible)
                InitializeDashboard();
        };
        ViewModel.BypassChanged += (_, _) => BodePlot.Invalidate();
        AppSettings.Instance.SettingsChanged += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(UpdatePresetDirtyIndicator);
            DispatcherQueue.TryEnqueue(UpdateGraphPopoutButtonMargin);
            if (_graphWindow == null) return;
            bool follows = AppSettings.Instance.PopoutFollowsSelectedChannel;
            _graphWindow.SetIgnoreVisibility(!follows);
            if (follows && _selectedChannel != null)
                _graphWindow.SetSelectedChannel((int)_selectedChannel.Id);
            else if (!follows)
                _graphWindow.SetSelectedChannel(-1);
        };
        ViewModel.VisibilityChanged += (_, _) =>
        {
            UpdateChannelBadges();
            BodePlot.Invalidate();
        };

        ViewModel.ChannelNameChanged += channelId =>
        {
            if (_channelNameTexts.TryGetValue(channelId, out var tb))
                tb.Text = ViewModel.GetChannelName(LookupChannelById(channelId));
            // Routing rows on the output pages label the matrix inputs with the
            // same sidebar names; ids 0/1 double as matrix input indices.
            if (_currentRouteNameTexts.TryGetValue(channelId, out var route))
                route.Text = ViewModel.GetChannelName(LookupChannelById(channelId));
            if (_dashboardNameTexts.TryGetValue(channelId, out var cardName))
                cardName.Text = ViewModel.GetChannelName(LookupChannelById(channelId));
        };

        ViewModel.InputPreampExtChanged += _ =>
            DispatcherQueue.TryEnqueue(UpdateInputPreampEditor);

        ViewModel.ActiveOutputsChanged += (s, e) =>
            DispatcherQueue.TryEnqueue(InitializeChannelLists);

        ViewModel.OutputEnabledChanged += (outputIndex, enabled) =>
            DispatcherQueue.TryEnqueue(() => { OnOutputEnabledChanged(outputIndex, enabled); if (DashboardPanel.Visibility == Visibility.Visible) UpdateDashboardCards(); });

        ViewModel.MatrixOutputGainChanged += outputIndex =>
            DispatcherQueue.TryEnqueue(() => { SyncGainFromViewModel(outputIndex); BodePlot.Invalidate(); });

        ViewModel.MatrixOutputDelayChanged += outputIndex =>
            DispatcherQueue.TryEnqueue(() => { SyncDelayFromViewModel(outputIndex); BodePlot.Invalidate(); });

        // A mute can arrive from anywhere: this window's own button, the matrix
        // window, or a device push (control surface, remote, macro, UART/I2C).
        ViewModel.MatrixOutputMuteChanged += outputIndex =>
            DispatcherQueue.TryEnqueue(() => SyncMuteFromViewModel(outputIndex));

        ViewModel.MatrixRouteChanged += (input, output) =>
            DispatcherQueue.TryEnqueue(() => SyncRouteIndicator(input, output));

        ViewModel.PresetsChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(RefreshPresetComboBox);

        ViewModel.InputSourceChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(RefreshSourceComboBox);

        // Right-click context menu on preset combo
        PresetComboBox.RightTapped += OnPresetComboRightTapped;


        // Right-click slider to reset. Master: snap back to the saved snapshot
        // value (or 0 if no snapshot yet). User: 0 dB via REQ_SET_USER_VOLUME,
        // matches macOS UserVolumeSection.onRightClick.
        MasterVolumeSlider.RightTapped += (s, e) =>
        {
            e.Handled = true;
            if (_sidebarVolumeIsMaster)
                ViewModel.MasterVolumeDb = ViewModel.SavedSnapshot?.MasterVolumeDb ?? 0f;
            else
                ViewModel.UserVolumeDb = 0f;
        };

        // Multi-device: register unsaved changes dialog
        ViewModel.ShowUnsavedChangesDialog = ShowUnsavedChangesDialogAsync;
        ViewModel.PromptForPresetName = PromptForPresetNameAsync;

        // Multi-device: update device selector when available devices change
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.AvailableDevices) ||
                e.PropertyName == nameof(MainViewModel.SelectedDeviceItem))
            {
                DispatcherQueue.TryEnqueue(UpdateDeviceSelector);
            }
            else if (e.PropertyName == nameof(MainViewModel.ActivePreset))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateActivePresetSelection();
                    UpdatePresetDirtyIndicator();
                    UpdateWindowTitle();
                });
            }
            else if (e.PropertyName == nameof(MainViewModel.PresetsDirty))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdatePresetDirtyIndicator();
                    UpdateWindowTitle();
                });
            }
        };
        ViewModel.AvailableDevices.CollectionChanged += (s, e) =>
            DispatcherQueue.TryEnqueue(UpdateDeviceSelector);

        // Initial UI state
        UpdateConnectionStatus();
        // Apply the persisted sidebar volume mode (label, menu checkmark,
        // slider tint, current dB readout). UpdateMasterVolumeDisplay is
        // invoked inside ApplySidebarVolumeMode, so we don't double-call.
        var savedMode = AppSettings.Instance.SidebarVolumeMode ?? "master";
        ApplySidebarVolumeMode(isMaster: savedMode != "user", persist: false);
        UpdateBypassButton();

        // Initialize AutoEQ (load database in background)
        _ = InitializeAutoEQAsync();

        // The Getting Started wizard for a new user, and release notes after an update.
        InitializeOnboarding();
        InstallChannelClipboardShortcuts();
        ViewModel.InputPairLinksChanged += StoreDeviceLinks;

        // Ctrl+, opens Settings, the platform convention the macOS Console follows.
        // VirtualKey has no name for the comma key, so it is set here by code.
        SettingsMenuItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)188,
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        });
        SettingsMenuItem.KeyboardAcceleratorTextOverride = "Ctrl+,";
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

    private void SetupAcrylicBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported())
            return;

        _configurationSource = new SystemBackdropConfiguration
        {
            IsInputActive = true  // Always keep translucency visible, even when unfocused
        };
        this.Closed += (s, e) =>
        {
            _acrylicController?.Dispose();
            _acrylicController = null;
            _configurationSource = null;

            // Closing the main window quits the app. WinUI 3 doesn't do this on
            // its own — secondary windows (Stats, Matrix, Leveller, Crossfeed,
            // Loudness, Graph) and the ViewModel's background threads (poll
            // timer + notify reader on EP3) would otherwise keep the process
            // alive. Dispose the ViewModel first so the USB handle and threads
            // are torn down before Application.Exit closes the remaining
            // windows. Unsaved-changes confirmation already ran in
            // OnAppWindowClosing (AppWindow.Closing fires before Window.Closed).
            ViewModel.Dispose();
            Application.Current.Exit();
        };

        // Sidebar and titlebar translucency settings
        _acrylicController = new DesktopAcrylicController
        {
            TintColor = Windows.UI.Color.FromArgb(255, 32, 32, 32),
            TintOpacity = 0.5f,
            LuminosityOpacity = 0.8f
        };

        _acrylicController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_configurationSource);
    }

    /// <summary>Sidebar channel-row MinHeight, or null to leave the ListView's
    /// default (40px). Compacted stepwise for 4/6/8-input sources so the whole
    /// sidebar stays onscreen; 2 inputs render exactly as before.</summary>
    private int? ChannelRowMinHeight()
    {
        int n = ViewModel.ActiveInputs.Count;
        return n >= 7 ? 29 : n >= 5 ? 33 : n >= 3 ? 38 : null;
    }

    private void InitializeChannelLists()
    {
        // Build channel list items programmatically
        // Index 0 = dashboard (no item), 1+ = channels
        _channelListItems.Clear();
        _outputChannelItems.Clear();
        _channelMeters.Clear();
        _channelBadgeStylers.Clear();

        InputChannelsList.Items.Clear();
        OutputChannelsList.Items.Clear();

        if (!ViewModel.IsDeviceConnected) return;

        // Tighten the section headers in step with the row compaction.
        int inputs = ViewModel.ActiveInputs.Count;
        InputsHeader.Margin = new Thickness(0, inputs >= 7 ? 6 : inputs >= 5 ? 7 : 8, 0, 4);
        OutputsHeader.Margin = new Thickness(0, inputs >= 7 ? 11 : inputs >= 5 ? 13 : 16, 0, 4);

        int index = 1;
        foreach (var channel in ViewModel.ActiveInputs)
        {
            var item = CreateChannelListItem(channel, index++);
            _channelListItems.Add(item);
            InputChannelsList.Items.Add(item);
        }

        // Pre-build all output items and add enabled ones
        for (int o = 0; o < ViewModel.ActiveOutputs.Count; o++)
        {
            var channel = ViewModel.ActiveOutputs[o];
            var item = CreateChannelListItem(channel, index);
            _outputChannelItems[o] = item;
            if (ViewModel.IsOutputEnabled(o))
            {
                item.Tag = (channel, index++);
                _channelListItems.Add(item);
                OutputChannelsList.Items.Add(item);
            }
        }
    }

    private void OnOutputEnabledChanged(int outputIndex, bool enabled)
    {
        if (!_outputChannelItems.TryGetValue(outputIndex, out var item)) return;

        if (enabled)
        {
            if (OutputChannelsList.Items.Contains(item)) return;
            // Insert at the correct position to maintain output order
            int insertAt = 0;
            for (int o = 0; o < outputIndex; o++)
            {
                if (ViewModel.IsOutputEnabled(o) && OutputChannelsList.Items.Contains(_outputChannelItems[o]))
                    insertAt++;
            }
            OutputChannelsList.Items.Insert(insertAt, item);
        }
        else
        {
            OutputChannelsList.Items.Remove(item);
        }

        // Re-index the flat list for selection tracking
        int inputCount = ViewModel.ActiveInputs.Count;
        if (_channelListItems.Count > inputCount)
            _channelListItems.RemoveRange(inputCount, _channelListItems.Count - inputCount);

        int index = inputCount + 1;
        for (int o = 0; o < ViewModel.ActiveOutputs.Count; o++)
        {
            if (!ViewModel.IsOutputEnabled(o) || !_outputChannelItems.TryGetValue(o, out var outItem)) continue;
            outItem.Tag = (ViewModel.ActiveOutputs[o], index++);
            _channelListItems.Add(outItem);
        }

        UpdateChannelListSelection();
    }

    private ListViewItem CreateChannelListItem(Channel channel, int index)
    {
        // Store both channel and index in Tag
        var item = new ListViewItem
        {
            Tag = (channel, index),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        int? rowMinHeight = ChannelRowMinHeight();
        if (rowMinHeight is int minHeight) item.MinHeight = minHeight;
        item.Tapped += OnChannelItemTapped;

        // When a pair is linked, hovering one of its channels highlights both
        if (!channel.IsOutput)
        {
            item.PointerEntered += OnMasterItemPointerEntered;
            item.PointerExited += OnMasterItemPointerExited;
        }

        // The content grid floors the row height, so shrink it when the compacted
        // MinHeight drops below its normal 32px.
        var grid = new Grid
        {
            Height = rowMinHeight is int mh && mh < 32 ? mh : 32,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = ViewModel.GetChannelName(channel),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var nameBox = new TextBox
        {
            Text = ViewModel.GetChannelName(channel),
            MaxLength = ChannelNameLimit.MaxBytes,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Style = (Style)RootGrid.Resources["ChannelNameTextBoxStyle"]
        };
        var nameContainer = new Grid();
        nameContainer.Children.Add(nameText);
        nameContainer.Children.Add(nameBox);
        _channelNameTexts[(int)channel.Id] = nameText;
        Grid.SetColumn(nameContainer, 0);
        grid.Children.Add(nameContainer);

        void CommitSidebarName()
        {
            if (nameBox.Visibility != Visibility.Visible) return;
            nameBox.Visibility = Visibility.Collapsed;
            nameText.Visibility = Visibility.Visible;
            var name = nameBox.Text.Trim();
            if (!string.IsNullOrEmpty(name)) ViewModel.SetChannelName(channel, name);
            FocusSink.Focus(FocusState.Programmatic);
        }

        var flyout = new MenuFlyout();

        var copyItem = new MenuFlyoutItem { Text = "Copy Parameters" };
        copyItem.Click += (s, e) => ViewModel.CopyChannelParams(channel);

        var pasteItem = new MenuFlyoutItem { Text = "Paste Parameters" };
        pasteItem.Click += (s, e) =>
        {
            ViewModel.PasteChannelParams(channel);
            if (_selectedChannel == channel)
                ShowChannelEditor(channel);
        };

        void StartRename()
        {
            nameText.Visibility = Visibility.Collapsed;
            nameBox.Text = ViewModel.GetChannelName(channel);
            nameBox.Visibility = Visibility.Visible;
            nameBox.Focus(FocusState.Programmatic);
            nameBox.SelectAll();
        }
        _sidebarRenamers[(int)channel.Id] = StartRename;
        var renameItem = new MenuFlyoutItem { Text = "Rename" };
        renameItem.Click += (s, e) => StartRename();

        // Identify: play a short chirp on this output so the user can find the
        // physical speaker. Outputs only — the signal generator drives output
        // channels, so inputs have nothing to identify.
        MenuFlyoutItem? identifyItem = null;
        if (channel.IsOutput)
        {
            identifyItem = new MenuFlyoutItem { Text = "Identify" };
            identifyItem.Click += async (s, e) =>
            {
                var outputs = ViewModel.ActiveOutputs;
                for (int o = 0; o < outputs.Count; o++)
                {
                    if (outputs[o].Id == channel.Id)
                    {
                        await ViewModel.IdentifyOutputAsync(o);
                        break;
                    }
                }
            };
        }

        flyout.Opening += (s, e) =>
        {
            pasteItem.IsEnabled = ViewModel.HasChannelClipboard;
            if (identifyItem != null)
                identifyItem.IsEnabled = ViewModel.IsDeviceConnected && ViewModel.SiggenSupported;
        };

        if (identifyItem != null)
        {
            flyout.Items.Add(identifyItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        flyout.Items.Add(copyItem);
        flyout.Items.Add(pasteItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(renameItem);

        item.ContextFlyout = flyout;

        nameBox.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; CommitSidebarName(); }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                nameBox.Visibility = Visibility.Collapsed;
                nameText.Visibility = Visibility.Visible;
                FocusSink.Focus(FocusState.Programmatic);
            }
        };
        nameBox.LostFocus += (s, e) => CommitSidebarName();

        // Pill-shaped badge — doubles as the graph-visibility toggle (moved here
        // from the old legend under the graph): channel color while shown on the
        // graph, grey once hidden. Click to toggle.
        var badge = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(7, 2, 7, 2),
            MinWidth = 46,
            VerticalAlignment = VerticalAlignment.Center
        };

        var badgeContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var badgeText = new TextBlock
        {
            Text = channel.Descriptor,
            FontSize = 9,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            CharacterSpacing = 80
        };
        badgeContent.Children.Add(badgeText);
        badge.Child = badgeContent;

        void StyleBadge()
        {
            bool graphVisible = ViewModel.GetChannelVisibility(channel);
            var c = channel.Color;
            badge.Background = new SolidColorBrush(graphVisible
                ? Color.FromArgb(15, c.R, c.G, c.B) : Color.FromArgb(10, 150, 150, 150));
            badge.BorderBrush = new SolidColorBrush(graphVisible
                ? Color.FromArgb(80, c.R, c.G, c.B) : Color.FromArgb(60, 150, 150, 150));
            badgeText.Foreground = new SolidColorBrush(graphVisible
                ? Color.FromArgb(230, c.R, c.G, c.B) : Color.FromArgb(150, 165, 165, 165));
        }
        StyleBadge();
        _channelBadgeStylers[(int)channel.Id] = StyleBadge;

        // Oversized hit area (negative margin cancels the padding, so the layout
        // is unchanged): a slightly-off click still toggles the badge instead of
        // selecting the channel row. A chromeless Button rather than a Border —
        // the ListView doesn't select the row when the click lands on a button,
        // where handled Tapped/Pointer events on a plain element wouldn't stop
        // SelectionMode="Single". Right-clicks pass through to the row's context
        // menu as usual.
        var badgeHit = new Button
        {
            Style = (Style)RootGrid.Resources["BadgeHitButtonStyle"],
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(-10, -8, -10, -8),
            VerticalAlignment = VerticalAlignment.Center,
            Content = badge
        };
        badgeHit.Click += (s, e) => ViewModel.ToggleChannelVisibility(channel);
        // Channel selection is driven by the row's own Tapped handler
        // (OnChannelItemTapped), and Tapped bubbles up from the badge no matter
        // what the Button does with the pointer events — stop it here, and undo
        // any stray built-in ListView highlight the click may have caused.
        badgeHit.Tapped += (s, e) =>
        {
            e.Handled = true;
            UpdateChannelListSelection();
            // Pressing the pill makes the button capture the pointer, so the row
            // under the cursor receives a synthetic pointer-exit and drops its
            // hover visual (and a linked pair drops the partner's forced one)
            // even though the cursor never moved. Restore hover on this row and,
            // for linked inputs, its partner once the click has settled.
            void RestoreHover()
            {
                VisualStateManager.GoToState(item, "PointerOver", true);
                if (!channel.IsOutput)
                {
                    var partner = GetPairedInputItem(item);
                    if (partner != null)
                        VisualStateManager.GoToState(partner, "PointerOver", true);
                }
            }
            RestoreHover();
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RestoreHover);
        };
        Grid.SetColumn(badgeHit, 2);
        grid.Children.Add(badgeHit);

        // Inline meter bar
        var meter = new HorizontalMeterBar
        {
            MeterColor = channel.Color,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 12, 0)
        };
        Grid.SetColumn(meter, 1);
        grid.Children.Add(meter);
        _channelMeters[(int)channel.Id] = meter;

        item.Content = grid;
        return item;
    }

    /// <summary>Repaint every sidebar badge from its channel's current graph
    /// visibility (the badges replaced the old legend pills under the graph).</summary>
    private void UpdateChannelBadges()
    {
        foreach (var styler in _channelBadgeStylers.Values)
            styler();
    }

    private void ScheduleDashboardRefresh()
    {
        if (DashboardPanel.Visibility != Visibility.Visible) return;
        _dashboardDebounce?.Stop();
        _dashboardDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _dashboardDebounce.Tick += (s, e) =>
        {
            _dashboardDebounce!.Stop();
            InitializeDashboard();
        };
        _dashboardDebounce.Start();
    }

    private void InitializeDashboard()
    {
        _dashboardCardTexts.Clear();

        var savedTransitions = DashboardPanel.ChildrenTransitions;
        DashboardPanel.ChildrenTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();

        DashboardPanel.Children.Clear();

        if (!ViewModel.IsDeviceConnected)
        {
            RebuildDashboardTextMaps();
            DashboardPanel.ChildrenTransitions = savedTransitions;
            return;
        }

        DashboardPanel.CardsPerRow = Math.Clamp(AppSettings.Instance.DashboardCardsPerRow, 0, 3);
        foreach (var (key, card, texts) in BuildDashboardCards())
        {
            card.Tag = key;
            DashboardPanel.Children.Add(card);
            _dashboardCardTexts[key] = texts;
        }

        RebuildDashboardTextMaps();
        DashboardPanel.ChildrenTransitions = savedTransitions;
    }

    private void UpdateDashboardCards()
    {
        if (!ViewModel.IsDeviceConnected) return;

        var desired = BuildDashboardCards();
        var desiredKeys = desired.Select(d => d.key).ToList();

        // Remove cards that should no longer exist
        for (int i = DashboardPanel.Children.Count - 1; i >= 0; i--)
        {
            var key = ((FrameworkElement)DashboardPanel.Children[i]).Tag as string;
            if (key == null || !desiredKeys.Contains(key))
            {
                DashboardPanel.Children.RemoveAt(i);
                if (key != null) _dashboardCardTexts.Remove(key);
            }
        }

        // Get current keys after removal
        var currentKeys = DashboardPanel.Children
            .Cast<FrameworkElement>()
            .Select(c => c.Tag as string)
            .ToList();

        // Add missing cards at correct positions. Cards that stayed put keep the
        // TextBlocks they already had — only the freshly inserted ones register.
        for (int i = 0; i < desired.Count; i++)
        {
            var (key, card, texts) = desired[i];
            if (!currentKeys.Contains(key))
            {
                card.Tag = key;
                DashboardPanel.Children.Insert(Math.Min(i, DashboardPanel.Children.Count), card);
                currentKeys.Insert(Math.Min(i, currentKeys.Count), key);
                _dashboardCardTexts[key] = texts;
            }
        }

        RebuildDashboardTextMaps();
    }

    /// <summary>Flatten the per-card registrations into the channel-keyed maps the
    /// live-refresh helpers use. Each channel appears on exactly one card.</summary>
    private void RebuildDashboardTextMaps()
    {
        _dashboardHeaderStats.Clear();
        _dashboardNameTexts.Clear();
        foreach (var texts in _dashboardCardTexts.Values)
        {
            foreach (var (id, tb) in texts.Stats) _dashboardHeaderStats[id] = tb;
            foreach (var (id, tb) in texts.Names) _dashboardNameTexts[id] = tb;
        }
    }

    private List<(string key, FrameworkElement card, DashboardCardTexts texts)> BuildDashboardCards()
    {
        var cards = new List<(string key, FrameworkElement card, DashboardCardTexts texts)>();

        // Input cards: the active input set (2 on a stereo source, up to 8 on
        // USB/ADAT/I2S), paired L/R the same way the sidebar lists them.
        var inputs = ViewModel.ActiveInputs;
        for (int i = 0; i < inputs.Count; i += 2)
        {
            var texts = new DashboardCardTexts();
            if (i + 1 < inputs.Count)
            {
                var (left, right) = (inputs[i], inputs[i + 1]);
                cards.Add(($"in-{left.ShortName}-{right.ShortName}",
                    WithLayoutGear(CreateStereoDashboardCard(left, right, false, texts)), texts));
            }
            else
            {
                // Odd input count (a source reporting an unpaired channel).
                cards.Add(($"in-{inputs[i].ShortName}",
                    WithLayoutGear(CreateMonoDashboardCard(inputs[i], false, texts)), texts));
            }
        }

        // Build output cards for enabled channels, pairing stereo L/R.
        var outputs = ViewModel.ActiveOutputs;

        for (int o = 0; o < outputs.Count; o++)
        {
            if (!ViewModel.IsOutputEnabled(o)) continue;

            var texts = new DashboardCardTexts();

            // Stereo pairs are fixed by position — (0,1), (2,3), … — so a pair
            // only forms when this channel is the left member and its partner is
            // enabled. Matching on "the next channel has the adjacent id" instead
            // slides the pairing by one whenever a left channel is disabled,
            // which produces cards like "SPDIF 3 R / SPDIF 4 L" and pairs the
            // mono PDM output in as a right half. PDM always sits last at an even
            // index, so it can never become the right half here.
            if (o % 2 == 0 && o + 1 < outputs.Count && ViewModel.IsOutputEnabled(o + 1))
            {
                var (left, right) = (outputs[o], outputs[o + 1]);
                cards.Add(($"{left.ShortName}-{right.ShortName}",
                    WithLayoutGear(CreateStereoDashboardCard(left, right, true, texts)), texts));
                o++; // partner consumed
            }
            else
            {
                cards.Add((outputs[o].ShortName, WithLayoutGear(CreateMonoDashboardCard(outputs[o], true, texts)), texts));
            }
        }

        return cards;
    }

    // Horizontal gradient brush used for dashboard card outlines: leftColor on
    // the left edge, rightColor on the right. For mono cards both args are the
    // same color, which renders as a solid outline.
    private static LinearGradientBrush CreateChannelGradientBrush(Color leftColor, Color rightColor)
    {
        const byte alpha = 102;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        brush.GradientStops.Add(new GradientStop
        {
            Offset = 0,
            Color = Color.FromArgb(alpha, leftColor.R, leftColor.G, leftColor.B)
        });
        brush.GradientStops.Add(new GradientStop
        {
            Offset = 1,
            Color = Color.FromArgb(alpha, rightColor.R, rightColor.G, rightColor.B)
        });
        return brush;
    }

    private Border CreateStereoDashboardCard(Channel left, Channel right, bool showDelay, DashboardCardTexts texts)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(178, 36, 36, 36)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = CreateChannelGradientBrush(left.Color, right.Color),
            BorderThickness = new Thickness(1)
        };

        var mainStack = new StackPanel();

        // Header row
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        headerGrid.Children.Add(CreateChannelHeader(left, showDelay, 0, texts));
        headerGrid.Children.Add(CreateChannelHeader(right, showDelay, 1, texts, reserveGear: true));

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

    /// <summary>One channel's header strip. The card's last one keeps its right
    /// edge clear for the layout gear that appears there on hover.</summary>
    private Border CreateChannelHeader(Channel channel, bool showDelay, int column, DashboardCardTexts texts, bool reserveGear = false)
    {
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(102, 38, 38, 38)),
            Padding = new Thickness(8, 8, reserveGear ? 30 : 8, 8)
        };
        Grid.SetColumn(header, column);

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        panel.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(channel.Color)
        });

        // Renames live in the ViewModel (and on the device) — channel.Name is only
        // the factory default, and the sidebar, matrix and editor all show the
        // override, so the cards have to as well.
        var nameText = new TextBlock
        {
            Text = ViewModel.GetChannelName(channel),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(
                255,
                (byte)(channel.Color.R * 0.7),
                (byte)(channel.Color.G * 0.7),
                (byte)(channel.Color.B * 0.7)))
        };
        texts.Names[(int)channel.Id] = nameText;
        panel.Children.Add(nameText);

        if (showDelay)
        {
            var gain = ViewModel.GetChannelGain(channel);
            var isMuted = ViewModel.GetChannelMute(channel);

            var statsText = new TextBlock
            {
                Text = $"{gain:F1}dB  {ViewModel.GetChannelDelay(channel):F0}ms{(isMuted ? "  MUTED" : "")}",
                FontSize = 9,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Foreground = new SolidColorBrush(isMuted ? Color.FromArgb(255, 200, 80, 80) : Colors.Gray),
                Margin = new Thickness(8, 0, 0, 0)
            };
            texts.Stats[(int)channel.Id] = statsText;
            panel.Children.Add(statsText);
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
            row.Background = new SolidColorBrush(i % 2 == 0 ? Color.FromArgb(40, 0, 0, 0) : Colors.Transparent);
            stack.Children.Add(row);
        }

        return stack;
    }

    private Grid CreateDashboardFilterRow(int band, FilterParams p, Color color)
    {
        var grid = new Grid { Height = 24, Padding = new Thickness(8, 0, 8, 0), ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });  // 0: Band #
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });  // 1: Type
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2: Spacer
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });  // 3: Freq
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });  // 4: Gain
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });  // 5: Q

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
            Foreground = new SolidColorBrush(isActive
                ? Color.FromArgb(255, (byte)(color.R * 0.7), (byte)(color.G * 0.7), (byte)(color.B * 0.7))
                : Color.FromArgb(102, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(typeText, 1);
        grid.Children.Add(typeText);

        if (isActive)
        {
            var secondaryBrush = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            var tertiaryBrush = (SolidColorBrush)Application.Current.Resources["TextFillColorTertiaryBrush"];

            var freqPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
            freqPanel.Children.Add(new TextBlock
            {
                Text = $"{p.Frequency:F0}",
                FontSize = 10,
                FontFamily = new FontFamily("Cascadia Code"),
                Foreground = secondaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            freqPanel.Children.Add(new TextBlock
            {
                Text = "Hz",
                FontSize = 8,
                Foreground = tertiaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(freqPanel, 3);
            grid.Children.Add(freqPanel);

            if (p.Type.HasGain())
            {
                var gainPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
                gainPanel.Children.Add(new TextBlock
                {
                    Text = FormatFilterValueSigned(p.Gain),
                    FontSize = 10,
                    FontFamily = new FontFamily("Cascadia Code"),
                    Foreground = secondaryBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                gainPanel.Children.Add(new TextBlock
                {
                    Text = "dB",
                    FontSize = 8,
                    Foreground = tertiaryBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(gainPanel, 4);
                grid.Children.Add(gainPanel);
            }

            // Q only for a peaking band, as on the macOS Console: on a cut or a
            // shelf it is a shape detail the row has no room to explain.
            if (p.Type == FilterType.Peaking)
            {
                var qPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
                qPanel.Children.Add(new TextBlock
                {
                    Text = FormatFilterValue(p.Q, 3),
                    FontSize = 10,
                    FontFamily = new FontFamily("Cascadia Code"),
                    Foreground = secondaryBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                qPanel.Children.Add(new TextBlock
                {
                    Text = "Q",
                    FontSize = 8,
                    Foreground = tertiaryBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(qPanel, 5);
                grid.Children.Add(qPanel);
            }
        }
        else
        {
            var dash = new TextBlock
            {
                Text = "—",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(dash, 3);
            grid.Children.Add(dash);
        }

        return grid;
    }

    private Border CreateMonoDashboardCard(Channel channel, bool showDelay, DashboardCardTexts texts)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(178, 36, 36, 36)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = CreateChannelGradientBrush(channel.Color, channel.Color),
            BorderThickness = new Thickness(1)
        };

        var stack = new StackPanel();
        stack.Children.Add(CreateChannelHeader(channel, showDelay, 0, texts, reserveGear: true));
        stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(51, channel.Color.R, channel.Color.G, channel.Color.B)) });
        stack.Children.Add(CreateDashboardFilterList(channel));

        card.Child = stack;
        return card;
    }

    private void ShowChannelEditor(Channel channel)
    {
        _selectedChannel = channel;

        // Reset the filter page to PEQ when moving to a different channel.
        // Same-channel rebuilds (e.g. from FiltersChanged) preserve the page.
        if ((int)channel.Id != _filterPageChannelId)
        {
            _filterPageChannelId = (int)channel.Id;
            _filterPageIsXover = false;
            ViewModel.PeqSelection.Reset();
        }

        // Set gradient state before SetSelectedChannel to avoid a redraw without it
        SyncLinkedPairGradient();
        UpdateGraphEditingSuspension(channel);

        BodePlot.SetSelectedChannel((int)channel.Id);
        if (AppSettings.Instance.PopoutFollowsSelectedChannel)
            _graphWindow?.SetSelectedChannel((int)channel.Id);
        ViewModel.SetRtaPage((int)channel.Id);

        DashboardPanel.Visibility = Visibility.Collapsed;
        ChannelEditorPanel.Visibility = Visibility.Visible;

        ChannelEditorPanel.Children.Clear();
        // The per-channel header (output card / master header) lives in a static
        // host above the scroll, so it stays put while the filter list scrolls.
        ChannelHeaderHost.Child = null;
        ChannelHeaderHost.Visibility = Visibility.Visible;
        _inputPreampSlider = null;
        _inputPreampDrag = null;
        _inputPreampValueText = null;

        if (!channel.IsOutput)
        {
            bool isMaster = channel.Id is ChannelId.MasterLeft or ChannelId.MasterRight;
            bool isLeft = channel.Id == ChannelId.MasterLeft;
            int wireInput = InputWireIndex(channel);

            // Same bottom pad as the output card so the header-to-filter gap
            // matches the gap between filter rows.
            var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Pair-aware link label. Channel numbers only mean anything on the
            // multichannel sources (USB, ADAT); everywhere else — and on the
            // master pair — it's just "Link Pair".
            int pairStartWire = wireInput & ~1;
            bool numberedSource = ViewModel.ActiveInputSource is InputSource.Usb or InputSource.Adat;
            string linkLabel = isMaster || !numberedSource
                ? "Link Pair"
                : $"Link {pairStartWire + 1}/{pairStartWire + 2}";
            var linkBtn = new ToggleButton
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE71B", FontSize = 14 },
                        new TextBlock { Text = linkLabel, FontSize = 12 }
                    }
                },
                IsChecked = ViewModel.IsInputPairLinked((int)channel.Id),
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center
            };
            // Grey while unlinked; linked, the darker accent with white text.
            // The content takes the button's foreground, so both states apply.
            var linkIdleText = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            linkBtn.Resources["ToggleButtonForeground"] = linkIdleText;
            linkBtn.Resources["ToggleButtonForegroundPointerOver"] = linkIdleText;
            linkBtn.Resources["ToggleButtonForegroundPressed"] = linkIdleText;
            AccentToggle.Apply(linkBtn);
            linkBtn.Click += async (s, e) =>
            {
                bool wantLink = linkBtn.IsChecked == true;
                int pair = MainViewModel.InputPairIndex((int)channel.Id);

                // When enabling and the pair's filters disagree, ask the user
                // which channel's bank should win — silently overwriting one
                // would lose work the user might still want.
                int sourceChannel = (int)channel.Id;
                if (wantLink && (ViewModel.InputPairFiltersDiffer((int)channel.Id) || ViewModel.InputPairPreampsDiffer((int)channel.Id)))
                {
                    var chosen = await AskWhichFiltersToKeep(channel);
                    if (chosen == null)
                    {
                        // Cancelled: revert toggle visual state, keep link off.
                        linkBtn.IsChecked = false;
                        return;
                    }
                    sourceChannel = chosen.Value;
                }

                // Commit the link state BEFORE the sync. SyncInputPairFilters
                // fires FiltersChanged, which causes MainWindow to rebuild
                // the channel editor — and the rebuild reads the pair-link
                // state to set the new link button's IsChecked. If we set it
                // after, the rebuilt button is born unchecked and only
                // illuminates next time you re-enter the editor. On a sync
                // failure we revert below.
                ViewModel.SetInputPairLinked(pair, wantLink);
                PersistPairLink(pair, wantLink);

                if (wantLink)
                {
                    var ok = await ViewModel.SyncInputPairFilters(sourceChannel);
                    if (!ok)
                    {
                        // Revert the link state and rebuild the editor so the
                        // (now stale, off-screen) link button is replaced with
                        // a fresh unchecked one.
                        ViewModel.SetInputPairLinked(pair, false);
                        PersistPairLink(pair, false);
                        if (_selectedChannel != null) ShowChannelEditor(_selectedChannel);
                        SyncLinkedPairGradient();
                        await ShowErrorDialog("Failed to sync filters to the linked channel — link not enabled.");
                        return;
                    }
                }

                SyncLinkedPairGradient();
                ViewModel.UpdateChannelSelection(channel);
                UpdateChannelListSelection();
            };
            Grid.SetColumn(linkBtn, 0);
            headerRow.Children.Add(linkBtn);

            // Per-input preamp strip (label · slider · value) occupies the middle column
            var preampStrip = new Grid
            {
                VerticalAlignment = VerticalAlignment.Stretch
            };
            preampStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            preampStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            preampStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var preampLabel = new TextBlock
            {
                Text = "Preamp",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            Grid.SetColumn(preampLabel, 0);
            preampStrip.Children.Add(preampLabel);

            var preampSlider = new Slider
            {
                Minimum = -60,
                Maximum = 10,
                StepFrequency = 0.1,
                SmallChange = 0.1,
                LargeChange = 3,
                Value = ViewModel.InputPreampAt(wireInput),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0),
                Padding = new Thickness(0)
            };
            // A drag sends live values to the device and commits once on release.
            var preampDrag = new SliderDrag(preampSlider,
                live: v => ViewModel.SendInputPreampLive(wireInput, v),
                commit: v =>
                {
                    if (Math.Abs(ViewModel.InputPreampAt(wireInput) - v) > 0.05f)
                        ViewModel.SetInputPreampAt(wireInput, v);
                },
                snap: v => MathF.Round(v * 10) / 10);
            // Right-click resets to 0 dB (the linked partner follows), as on the
            // macOS Console.
            preampSlider.RightTapped += (_, e) =>
            {
                e.Handled = true;
                ViewModel.SetInputPreampAt(wireInput, 0f);
            };
            Grid.SetColumn(preampSlider, 1);
            preampStrip.Children.Add(preampSlider);

            // Typeable: Enter or leaving the field applies, anything that does not
            // parse puts the current value back.
            var preampValue = new TextBox
            {
                FontSize = 12,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Width = 64,
                TextAlignment = TextAlignment.Right,
                Style = (Style)RootGrid.Resources["InlineValueTextBoxStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            // Only text the user typed is applied: a blur alone, or a value
            // that changed elsewhere while the field had focus, writes nothing.
            bool preampEdited = false;
            preampValue.TextChanged += (_, _) => { if (preampValue.FocusState != FocusState.Unfocused) preampEdited = true; };
            void CommitPreampText()
            {
                if (!preampEdited) { UpdateInputPreampEditor(); return; }
                preampEdited = false;
                var text = preampValue.Text.Replace("dB", "").Trim();
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float typed)
                    || float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out typed))
                    ViewModel.SetInputPreampAt(wireInput, Math.Clamp(MathF.Round(typed, 1), -60f, 10f));
                UpdateInputPreampEditor();
            }
            preampValue.LostFocus += (_, _) => CommitPreampText();
            preampValue.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; FocusSink.Focus(FocusState.Programmatic); }
                else if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    // Cancel: put the current value back before the blur that
                    // would otherwise apply what was typed.
                    e.Handled = true;
                    preampEdited = false;
                    preampValue.Text = $"{ViewModel.InputPreampAt(wireInput):F1} dB";
                    FocusSink.Focus(FocusState.Programmatic);
                }
            };
            Grid.SetColumn(preampValue, 2);
            preampStrip.Children.Add(preampValue);

            // Wrap strip in a Border with the same background and rounded corners
            // as the adjacent buttons so it reads as a unified control.
            var preampBox = new Border
            {
                Background = (Brush)Application.Current.Resources["ButtonBackground"],
                BorderBrush = (Brush)Application.Current.Resources["ButtonBorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(8, 0, 8, 0),
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Child = preampStrip
            };
            Grid.SetColumn(preampBox, 1);
            headerRow.Children.Add(preampBox);

            preampDrag.Moved += v => preampValue.Text = $"{v:F1} dB";
            _inputPreampSlider = preampSlider;
            _inputPreampDrag = preampDrag;
            _inputPreampValueText = preampValue;
            UpdateInputPreampEditor();

            // No confirmation, as on the macOS Console: a cleared bank is a
            // few clicks from any preset, and the label says what it covers.
            bool clearLinked = ViewModel.IsInputPairLinked((int)channel.Id);
            var clearBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = clearLinked ? $"Clear {pairStartWire + 1}/{pairStartWire + 2} PEQ" : "Clear PEQ",
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                },
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(clearBtn, clearLinked
                ? "Reset all PEQ bands on both channels of the linked pair"
                : "Reset all PEQ bands on this input");
            clearBtn.Click += async (s, e) =>
            {
                // ViewModel.SetFilter mirrors each write to a linked partner, so
                // one loop covers both channels of a linked pair.
                var defaultFilter = new FilterParams(FilterType.Flat, 1000f, 0.707f, 0f);
                int targetChannel = (int)channel.Id;
                int bandCount = ViewModel.GetFilters(channel).Count;
                for (int b = 0; b < bandCount; b++)
                    await ViewModel.SetFilter(targetChannel, b, defaultFilter.Clone());
            };
            Grid.SetColumn(clearBtn, 2);
            headerRow.Children.Add(clearBtn);

            ChannelHeaderHost.Child = headerRow;
        }

        // Output channel controls: Gain, Delay, Mute
        _currentMuteButton = null;   // only outputs have one; drop the last page's
        _limiterButton = null;       // likewise the limiter icon under it
        _limiterIcon = null;
        if (channel.IsOutput)
        {
            // Determine output index for matrix routing
            _currentRouteCircles.Clear();
            _currentRouteNameTexts.Clear();
            _currentRouteGainTexts.Clear();
            _currentRouteInvTexts.Clear();
            _currentOutputIndex = -1;
            var activeOutputs = ViewModel.ActiveOutputs;
            for (int i = 0; i < activeOutputs.Count; i++)
                if (activeOutputs[i].Id == channel.Id) { _currentOutputIndex = i; break; }

            bool isMuted = ViewModel.GetChannelMute(channel);
            var dimBrush = new SolidColorBrush(Color.FromArgb(160, 180, 180, 180));
            var unitBrush = new SolidColorBrush(Color.FromArgb(140, 180, 180, 180));

            var outputCard = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(128, 45, 45, 48)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 12, 6),
                // Bottom margin (12) + the first filter row's 2px top margin equals
                // the 14px gap between rows (StackPanel Spacing 12 + 2), so the
                // card-to-list gap matches the gap between filters.
                Margin = new Thickness(0, 0, 0, 12)
            };

            var cardGrid = new Grid { ColumnSpacing = 16 };
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // ── Gain section (col 0) ──
            var gainSection = new StackPanel { Spacing = 4 };

            Slider gainSlider = null!;
            SliderDrag gainDrag = null!;
            TextBox gainTextBox = null!;
            var gainHeaderRow = new Grid { Margin = new Thickness(0, 11, 0, 0) };
            gainHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gainHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var gainLabelPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            gainLabelPanel.Children.Add(new TextBlock
            {
                Text = "GAIN", FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = dimBrush
            });
            bool gainLocked = AppSettings.Instance.GainLocked.TryGetValue((int)channel.Id, out var gl) && gl;
            var gainLockIcon = new FontIcon
            {
                Glyph = gainLocked ? "\uE72E" : "\uE785",
                FontSize = 10,
                Foreground = dimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -4, 0, 0)
            };
            gainLockIcon.Tapped += (s, e) =>
            {
                bool locking = gainLockIcon.Glyph == "\uE785";
                gainLockIcon.Glyph = locking ? "\uE72E" : "\uE785";
                gainSlider.IsEnabled = !locking;
                gainTextBox.IsEnabled = !locking;
                AppSettings.Instance.GainLocked[(int)channel.Id] = locking;
                AppSettings.Instance.Save();
            };
            gainLabelPanel.Children.Add(gainLockIcon);
            Grid.SetColumn(gainLabelPanel, 0);
            gainHeaderRow.Children.Add(gainLabelPanel);

            var gainValuePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            gainTextBox = new TextBox
            {
                Tag = channel, Width = 50,
                Text = ViewModel.GetChannelGain(channel).ToString("0.00", CultureInfo.InvariantCulture),
                FontSize = 13,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Style = (Style)RootGrid.Resources["InlineValueTextBoxStyle"]
            };
            // Typed gain applies on Enter (which moves focus away) or on leaving
            // the field, not per keystroke.
            gainTextBox.LostFocus += OnGainTextCommitted;
            gainTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    FocusSink.Focus(FocusState.Programmatic);
                }
            };
            gainValuePanel.Children.Add(gainTextBox);
            gainValuePanel.Children.Add(new TextBlock { Text = "dB", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = unitBrush });
            gainValuePanel.PointerWheelChanged += (s, ev) =>
            {
                var delta = ev.GetCurrentPoint(gainValuePanel).Properties.MouseWheelDelta;
                if (delta == 0) return;
                int direction = delta > 0 ? 1 : -1;
                float current = ViewModel.GetChannelGain(channel);
                float newVal = Math.Clamp(current + direction * 0.01f, -60, 10);
                _isUpdatingGain = true;
                ViewModel.SetChannelGain((int)channel.Id, newVal);
                gainTextBox.Text = newVal.ToString("0.00", CultureInfo.InvariantCulture);
                gainDrag.Show(newVal);
                _isUpdatingGain = false;
                ev.Handled = true;
            };
            Grid.SetColumn(gainValuePanel, 1);
            gainHeaderRow.Children.Add(gainValuePanel);
            _currentGainTextBox = gainTextBox;

            gainSection.Children.Add(gainHeaderRow);

            gainSlider = new Slider
            {
                Minimum = -60, Maximum = 10,
                Value = ViewModel.GetChannelGain(channel),
                Tag = channel, StepFrequency = 1, SnapsTo = SliderSnapsTo.StepValues,
                IsEnabled = !gainLocked
            };
            gainTextBox.IsEnabled = !gainLocked;
            int gainChannelId = (int)channel.Id;
            gainDrag = new SliderDrag(gainSlider,
                live: v => ViewModel.SendChannelGainLive(gainChannelId, v),
                commit: v => ViewModel.SetChannelGain(gainChannelId, v),
                snap: v => MathF.Round(v));
            gainDrag.Moved += v => gainTextBox.Text = v.ToString("0.00", CultureInfo.InvariantCulture);
            gainSlider.RightTapped += (s, e) =>
            {
                e.Handled = true;
                if (s is Slider sl && sl.Tag is Channel ch && sl.IsEnabled)
                {
                    // Right-click resets to 0 dB, as on the macOS Console.
                    float saved = 0f;
                    _isUpdatingGain = true;
                    ViewModel.SetChannelGain((int)ch.Id, saved);
                    gainDrag.Show(saved);
                    if (_currentGainTextBox != null)
                        _currentGainTextBox.Text = saved.ToString("0.00", CultureInfo.InvariantCulture);
                    _isUpdatingGain = false;
                }
            };
            _currentGainSlider = gainSlider;
            _currentGainDrag = gainDrag;

            gainSection.Children.Add(gainSlider);
            Grid.SetColumn(gainSection, 0);
            cardGrid.Children.Add(gainSection);

            // Vertical separator (col 1)
            var separator = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                Width = 1, VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 4)
            };
            Grid.SetColumn(separator, 1);
            cardGrid.Children.Add(separator);

            // ── Delay section (col 2) ──
            var delaySection = new StackPanel { Spacing = 4 };

            Slider delaySlider = null!;
            SliderDrag delayDrag = null!;
            TextBox delayTextBox = null!;
            var delayHeaderRow = new Grid { Margin = new Thickness(0, 11, 0, 0) };
            delayHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            delayHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var delayLabelPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            delayLabelPanel.Children.Add(new TextBlock
            {
                Text = "DELAY", FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = dimBrush
            });
            bool delayLocked = AppSettings.Instance.DelayLocked.TryGetValue((int)channel.Id, out var dl) && dl;
            var delayLockIcon = new FontIcon
            {
                Glyph = delayLocked ? "\uE72E" : "\uE785",
                FontSize = 10,
                Foreground = dimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -4, 0, 0)
            };
            delayLockIcon.Tapped += (s, e) =>
            {
                bool locking = delayLockIcon.Glyph == "\uE785";
                delayLockIcon.Glyph = locking ? "\uE72E" : "\uE785";
                delaySlider.IsEnabled = !locking;
                delayTextBox.IsEnabled = !locking;
                AppSettings.Instance.DelayLocked[(int)channel.Id] = locking;
                AppSettings.Instance.Save();
            };
            delayLabelPanel.Children.Add(delayLockIcon);
            Grid.SetColumn(delayLabelPanel, 0);
            delayHeaderRow.Children.Add(delayLabelPanel);

            var delayValuePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            delayTextBox = new TextBox
            {
                Tag = channel, Width = 58,
                Text = ViewModel.GetChannelDelay(channel).ToString("0.00##", CultureInfo.InvariantCulture),
                FontSize = 13,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Style = (Style)RootGrid.Resources["InlineValueTextBoxStyle"]
            };
            delayTextBox.LostFocus += OnDelayTextCommitted;
            delayTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    FocusSink.Focus(FocusState.Programmatic);
                }
            };
            var delayCmOverlay = new TextBlock
            {
                FontSize = 13,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 1, 0),
                Foreground = delayTextBox.Foreground,
                Visibility = Visibility.Collapsed
            };
            var delayBoxContainer = new Grid { Width = 58 };
            delayBoxContainer.Children.Add(delayTextBox);
            delayBoxContainer.Children.Add(delayCmOverlay);
            delayValuePanel.Children.Add(delayBoxContainer);

            var delayUnitText = new TextBlock { Text = "ms", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = unitBrush, Width = 20 };
            delayUnitText.PointerPressed += (s, ev) =>
            {
                ev.Handled = true;
                float ms = ViewModel.GetChannelDelay(channel);
                delayCmOverlay.Text = FormatDelayCm(ms);
                delayCmOverlay.Visibility = Visibility.Visible;
                delayTextBox.Opacity = 0;
                delayUnitText.Text = "cm";
            };
            delayUnitText.PointerReleased += (s, ev) =>
            {
                delayCmOverlay.Visibility = Visibility.Collapsed;
                delayTextBox.Opacity = 1;
                delayUnitText.Text = "ms";
            };
            delayUnitText.PointerExited += (s, ev) =>
            {
                delayCmOverlay.Visibility = Visibility.Collapsed;
                delayTextBox.Opacity = 1;
                delayUnitText.Text = "ms";
            };
            _currentDelayUnitText = delayUnitText;
            delayValuePanel.Children.Add(delayUnitText);
            delayValuePanel.PointerWheelChanged += (s, ev) =>
            {
                var delta = ev.GetCurrentPoint(delayValuePanel).Properties.MouseWheelDelta;
                if (delta == 0) return;
                int direction = delta > 0 ? 1 : -1;
                float current = ViewModel.GetChannelDelay(channel);
                float newVal = Math.Clamp(current + direction, 0, ViewModel.MaxOutputDelayMs);
                _isUpdatingDelay = true;
                ViewModel.SetDelay((int)channel.Id, newVal);
                delayTextBox.Text = newVal.ToString("0.00##", CultureInfo.InvariantCulture);
                delayDrag.Show(newVal);
                _isUpdatingDelay = false;
                ev.Handled = true;
            };
            Grid.SetColumn(delayValuePanel, 1);
            delayHeaderRow.Children.Add(delayValuePanel);
            _currentDelayTextBox = delayTextBox;

            delaySection.Children.Add(delayHeaderRow);

            delaySlider = new Slider
            {
                Minimum = 0, Maximum = ViewModel.MaxOutputDelayMs,
                Value = ViewModel.GetChannelDelay(channel),
                Tag = channel,
                StepFrequency = 1,
                SnapsTo = SliderSnapsTo.StepValues,
                IsEnabled = !delayLocked
            };
            delayTextBox.IsEnabled = !delayLocked;
            int delayChannelId = (int)channel.Id;
            delayDrag = new SliderDrag(delaySlider,
                live: v => ViewModel.SendDelayLive(delayChannelId, v),
                commit: v => ViewModel.SetDelay(delayChannelId, v),
                snap: v => MathF.Round(v));
            delayDrag.Moved += v => delayTextBox.Text = v.ToString("0.00##", CultureInfo.InvariantCulture);
            delaySlider.RightTapped += (s, e) =>
            {
                e.Handled = true;
                if (s is Slider sl && sl.Tag is Channel ch && sl.IsEnabled)
                {
                    // Right-click resets to 0 ms, as on the macOS Console.
                    float saved = 0f;
                    _isUpdatingDelay = true;
                    ViewModel.SetDelay((int)ch.Id, saved);
                    delayDrag.Show(saved);
                    if (_currentDelayTextBox != null)
                        _currentDelayTextBox.Text = saved.ToString("0.00##", CultureInfo.InvariantCulture);
                    _isUpdatingDelay = false;
                }
            };
            _currentDelaySlider = delaySlider;
            _currentDelayDrag = delayDrag;

            delaySection.Children.Add(delaySlider);

            Grid.SetColumn(delaySection, 2);
            cardGrid.Children.Add(delaySection);

            // Vertical separator (col 3)
            var muteSeparator = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                Width = 1, VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 4)
            };
            Grid.SetColumn(muteSeparator, 3);
            cardGrid.Children.Add(muteSeparator);

            // ── Mute icon (col 4) ──
            var muteBtn = new ToggleButton
            {
                Tag = channel,
                Padding = new Thickness(8),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new FontIcon { FontSize = 16 }
            };
            ApplyMuteButtonState(muteBtn, isMuted);
            muteBtn.Click += OnMuteToggleClick;
            _currentMuteButton = muteBtn;
            // The output limiter's icon sits under the mute button (V32+).
            var muteColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
            muteColumn.Children.Add(muteBtn);
            if (CreateLimiterCell(_currentOutputIndex) is { } limiterCell)
                muteColumn.Children.Add(limiterCell);
            Grid.SetColumn(muteColumn, 4);
            cardGrid.Children.Add(muteColumn);

            // ── Routing section (left side) ──
            var dimGray = Color.FromArgb(90, 160, 160, 170);
            var routeSection = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

            for (int input = 0; input < Channel.Inputs.Count; input++)
            {
                var inputCh = Channel.Inputs[input];
                bool routed = _currentOutputIndex >= 0 && ViewModel.GetMatrixRouting(input, _currentOutputIndex);
                float routeGain = _currentOutputIndex >= 0 ? ViewModel.GetMatrixGain(input, _currentOutputIndex) : 0f;
                bool inverted = _currentOutputIndex >= 0 && ViewModel.GetMatrixInvert(input, _currentOutputIndex);
                int capturedInput = input;

                var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

                // Connection circle
                var circle = new Border
                {
                    Width = 14, Height = 14,
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = routed ? new Thickness(0) : new Thickness(2),
                    BorderBrush = new SolidColorBrush(dimGray),
                    Background = routed ? new SolidColorBrush(inputCh.Color) : new SolidColorBrush(Colors.Transparent),
                    VerticalAlignment = VerticalAlignment.Center
                };
                void ToggleRoute()
                {
                    if (_currentOutputIndex < 0) return;
                    bool nowRouted = !ViewModel.GetMatrixRouting(capturedInput, _currentOutputIndex);
                    float g = ViewModel.GetMatrixGain(capturedInput, _currentOutputIndex);
                    bool inv = ViewModel.GetMatrixInvert(capturedInput, _currentOutputIndex);
                    ViewModel.SetMatrixRoute(capturedInput, _currentOutputIndex, nowRouted, g, inv);
                }
                circle.Tapped += (s, e) => ToggleRoute();
                _currentRouteCircles[input] = circle;
                cell.Children.Add(circle);

                // Input name
                var nameText = new TextBlock
                {
                    Text = ViewModel.GetChannelName(inputCh),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(routed ? inputCh.Color : dimGray),
                    VerticalAlignment = VerticalAlignment.Center
                };
                // The name toggles too: the circle and the name read as one control.
                nameText.Tapped += (s, e) => ToggleRoute();
                string outputName = ViewModel.GetChannelName(channel).Trim();
                ToolTipService.SetToolTip(circle, routed ? $"Disconnect from {outputName}" : $"Connect to {outputName}");
                ToolTipService.SetToolTip(nameText, routed ? $"Disconnect from {outputName}" : $"Connect to {outputName}");
                _currentRouteNameTexts[input] = nameText;
                cell.Children.Add(nameText);

                // Gain text — always shown, grayed out and non-interactive when unrouted.
                // Master R's "R" glyph is wider than Master L's "L", so nudge the right
                // row 2px left to keep the gain/inv columns visually aligned.
                double gainLeftMargin = input == 1 ? -11 : -9;
                var gainText = new TextBox
                {
                    Text = routeGain == 0f ? "0.00 dB" : string.Format(CultureInfo.InvariantCulture, "{0:+0.00;-0.00} dB", routeGain),
                    FontSize = 10,
                    FontFamily = new FontFamily("Cascadia Code, Consolas"),
                    Foreground = GetRouteGainBrush(routed),
                    Style = (Style)RootGrid.Resources["InlineValueTextBoxStyle"],
                    Width = 64,
                    // Editable while unrouted, so a level can be set before
                    // connecting; it is stored with the route either way.
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(gainLeftMargin, 3, 0, 0)
                };
                gainText.RightTapped += (s, e) =>
                {
                    e.Handled = true;
                    if (_currentOutputIndex < 0) return;
                    bool en = ViewModel.GetMatrixRouting(capturedInput, _currentOutputIndex);
                    bool inv = ViewModel.GetMatrixInvert(capturedInput, _currentOutputIndex);
                    ViewModel.SetMatrixRoute(capturedInput, _currentOutputIndex, en, 0f, inv);
                    // Shown now: a focused field is not refreshed, and its blur
                    // would write the old text back.
                    gainText.Text = "0.00 dB";
                };
                gainText.LostFocus += (s, e) =>
                {
                    if (_currentOutputIndex < 0 || s is not TextBox tb) return;
                    var str = tb.Text.Replace("dB", "").Trim();
                    if (float.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    {
                        val = Math.Clamp(val, -60f, 12f);
                        bool en = ViewModel.GetMatrixRouting(capturedInput, _currentOutputIndex);
                        bool inv = ViewModel.GetMatrixInvert(capturedInput, _currentOutputIndex);
                        ViewModel.SetMatrixRoute(capturedInput, _currentOutputIndex, en, val, inv);
                    }
                };
                gainText.KeyDown += (s, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Enter)
                    {
                        e.Handled = true;
                        FocusSink.Focus(FocusState.Programmatic);
                    }
                };
                gainText.PointerWheelChanged += (s, ev) =>
                {
                    ev.Handled = true;
                    if (_currentOutputIndex < 0) return;
                    int delta = ev.GetCurrentPoint(gainText).Properties.MouseWheelDelta;
                    float step = delta > 0 ? 0.5f : -0.5f;
                    float current = ViewModel.GetMatrixGain(capturedInput, _currentOutputIndex);
                    float newGain = Math.Clamp(current + step, -60f, 12f);
                    bool en = ViewModel.GetMatrixRouting(capturedInput, _currentOutputIndex);
                    bool inv = ViewModel.GetMatrixInvert(capturedInput, _currentOutputIndex);
                    ViewModel.SetMatrixRoute(capturedInput, _currentOutputIndex, en, newGain, inv);
                };
                _currentRouteGainTexts[input] = gainText;
                cell.Children.Add(gainText);

                // INV label — always shown, grayed out and non-interactive when unrouted
                var invText = new TextBlock
                {
                    Text = "INV",
                    FontSize = 9,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = GetRouteInvBrush(routed, inverted),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = routed,
                    Margin = new Thickness(-2, 0, 0, 0)
                };
                invText.Tapped += (s, e) =>
                {
                    if (_currentOutputIndex < 0) return;
                    bool nowInv = !ViewModel.GetMatrixInvert(capturedInput, _currentOutputIndex);
                    bool en = ViewModel.GetMatrixRouting(capturedInput, _currentOutputIndex);
                    float g = ViewModel.GetMatrixGain(capturedInput, _currentOutputIndex);
                    ViewModel.SetMatrixRoute(capturedInput, _currentOutputIndex, en, g, nowInv);
                };
                _currentRouteInvTexts[input] = invText;
                cell.Children.Add(invText);

                routeSection.Children.Add(cell);
            }

            // ── Reorganize card: Route section | separator | Gain | sep | Delay | sep | Mute ──
            cardGrid.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
            cardGrid.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = new GridLength(1) });

            // Shift existing children right by 2 columns
            foreach (var child in cardGrid.Children)
            {
                if (child is FrameworkElement fe)
                    Grid.SetColumn(fe, Grid.GetColumn(fe) + 2);
            }

            // Add route section at col 0
            Grid.SetColumn(routeSection, 0);
            cardGrid.Children.Add(routeSection);

            // Add separator at col 1
            var routeSep = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                Width = 1, VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 4)
            };
            Grid.SetColumn(routeSep, 1);
            cardGrid.Children.Add(routeSep);

            outputCard.Child = cardGrid;
            ChannelHeaderHost.Child = outputCard;
        }

        // Filter rows (PEQ or crossover) + the bottom status bar.
        AddFilterSection(channel);
    }

    /// <summary>
    /// Render the active filter page (PEQ or crossover) into the editor panel,
    /// followed by the status bar (Enable All / Bypass All + PEQ|XO tab). The XO
    /// page is only reachable on output channels with V11+ firmware; the tab is
    /// disabled (but visible) on master/input channels.
    /// </summary>
    private void AddFilterSection(Channel channel)
    {
        bool xoverAvailable = channel.IsOutput && ViewModel.CrossoverSupported;
        bool showXover = _filterPageIsXover && xoverAvailable;

        if (showXover)
        {
            _peqRows.Clear();
            // No column-header row — matches the PEQ page, which relies on the
            // self-describing controls (family/type/slope dropdowns + "Hz" suffix).
            var xbands = ViewModel.GetXoverFilters(channel);
            for (int i = 0; i < xbands.Count; i++)
                ChannelEditorPanel.Children.Add(CreateXoverEditorRow(channel, i, xbands[i]));
        }
        else
        {
            _peqRows.Clear();
            _pendingLiveRows.Clear();
            var filters = ViewModel.GetFilters(channel);
            for (int i = 0; i < filters.Count; i++)
                ChannelEditorPanel.Children.Add(CreateFilterEditorRow(channel, i, filters[i]));
            UpdatePeqRowHighlights();
        }

        // The status bar is pinned (lives outside the ScrollViewer), so it stays
        // visible while the filter list scrolls. Refresh its content each rebuild.
        FilterStatusBarHost.Child = BuildFilterStatusBar(channel, showXover, xoverAvailable);
        FilterStatusBarHost.Visibility = Visibility.Visible;
    }

    /// <summary>The PEQ row's columns: bypass dot, number, type, frequency,
    /// gain and Q, in the macOS Console's order.</summary>
    private static Grid BuildPeqGrid(bool bypassSupported)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        if (bypassSupported)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });  // Bypass toggle
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });  // Band number
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FilterTypeWidth) }); // Type
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });  // Freq
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });  // Gain
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });  // Q
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    // ── On-graph editing integration ──
    // The graph and the band list share PeqSelection: a band selected or hovered
    // on the graph lights its row, and a row's number selects its band (Ctrl and
    // Shift as on a dot). During a graph drag the rows show live values without
    // a rebuild; the model is written once, on release.

    private sealed class PeqRowRefs
    {
        public required Border Row;
        public TextBox? Freq, Q, Gain;
        public bool Active;
    }

    private readonly Dictionary<int, PeqRowRefs> _peqRows = new();
    private HashSet<int> _previousPeqSelection = new();

    private static readonly Color PeqRowBase = Color.FromArgb(128, 45, 45, 48);

    private static Color BandColor(int band)
    {
        var (r, g, b) = PeqBandPalette.Bytes(band);
        return Color.FromArgb(255, r, g, b);
    }

    private void OnGraphBandLive(int channelId, int band, FilterParams p)
    {
        if (_selectedChannel == null || _filterPageIsXover) return;
        int sel = (int)_selectedChannel.Id;
        if (sel != channelId &&
            !(ViewModel.IsInputPairLinked(channelId) && MainViewModel.GetLinkedInputChannel(channelId) == sel))
            return;
        // A drag reports at pointer rate; the rows take the latest value once a frame.
        _pendingLiveRows[band] = p;
        if (_liveRowsQueued) return;
        _liveRowsQueued = true;
        CompositionTarget.Rendering += FlushLiveRows;
    }

    private readonly Dictionary<int, FilterParams> _pendingLiveRows = new();
    private bool _liveRowsQueued;

    private void FlushLiveRows(object? sender, object e)
    {
        CompositionTarget.Rendering -= FlushLiveRows;
        _liveRowsQueued = false;
        foreach (var (band, p) in _pendingLiveRows)
        {
            if (!_peqRows.TryGetValue(band, out var refs)) continue;
            SetIfChanged(refs.Freq, FormatFilterValue(p.Frequency, 2));
            SetIfChanged(refs.Q, FormatFilterValue(p.Q, 3));
            SetIfChanged(refs.Gain, FormatFilterValue(p.Gain, 2));
        }
        _pendingLiveRows.Clear();

        static void SetIfChanged(TextBox? box, string text)
        {
            if (box != null && box.Text != text) box.Text = text;
        }
    }

    /// <summary>Scrolls the band list just far enough to show a band newly
    /// selected on the graph (not in the list itself); a row already in view
    /// does not move.</summary>
    private void OnPeqSelectionChanged()
    {
        var selected = ViewModel.PeqSelection.Selected;
        var added = selected.Except(_previousPeqSelection).OrderBy(b => b).ToList();
        _previousPeqSelection = new HashSet<int>(selected);
        UpdatePeqRowHighlights();
        if (ViewModel.PeqSelection.MadeByList is { } byList && byList.SetEquals(selected)) return;
        if (added.Count > 0) RevealPeqRow(added[0]);
    }

    private void RevealPeqRow(int band)
    {
        if (_peqRows.TryGetValue(band, out var refs))
            refs.Row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
    }

    private void UpdatePeqRowHighlights()
    {
        var sel = ViewModel.PeqSelection;
        foreach (var (band, refs) in _peqRows)
        {
            bool selected = refs.Active && sel.Selected.Contains(band);
            bool hovered = refs.Active && sel.GraphHovered == band;
            var c = BandColor(band);
            // Stronger than the macOS Console's 13 % / 7 %, which barely shows on
            // these rows. Hover gets a half-strength bar so it reads at a glance,
            // and selection stays the stronger of the two.
            double mix = selected ? 0.24 : hovered ? 0.18 : 0;
            refs.Row.Background = new SolidColorBrush(Blend(PeqRowBase, c, mix));
            refs.Row.BorderBrush = new SolidColorBrush(
                selected ? c : hovered ? Color.FromArgb(140, c.R, c.G, c.B) : Colors.Transparent);
        }
    }

    /// <summary><paramref name="baseColor"/> with <paramref name="tint"/> laid
    /// over it at <paramref name="amount"/>, opaque enough to read as a tint.</summary>
    private static Color Blend(Color baseColor, Color tint, double amount)
    {
        if (amount <= 0) return baseColor;
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromArgb((byte)Math.Max((int)baseColor.A, 150), Mix(baseColor.R, tint.R), Mix(baseColor.G, tint.G), Mix(baseColor.B, tint.B));
    }

    /// <summary>Graph editing is suspended while the page lists crossover bands.</summary>
    private void UpdateGraphEditingSuspension(Channel channel)
    {
        bool suspended = _filterPageIsXover && channel.IsOutput && ViewModel.CrossoverSupported;
        BodePlot.SetEditingSuspended(suspended);
        _graphWindow?.SetEditingSuspended(suspended);
    }

    private Border CreateFilterEditorRow(Channel channel, int bandIndex, FilterParams p)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(PeqRowBase),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 12, 6),
            Margin = new Thickness(0, 2, 0, 0)
        };
        bool bandIsActive = p.Type != FilterType.Flat;
        var refs = new PeqRowRefs { Row = row, Active = bandIsActive };
        _peqRows[bandIndex] = refs;
        row.PointerEntered += (_, _) => { if (bandIsActive) ViewModel.PeqSelection.ListHovered = bandIndex; };
        row.PointerExited += (_, _) =>
        {
            if (ViewModel.PeqSelection.ListHovered == bandIndex) ViewModel.PeqSelection.ListHovered = null;
        };

        bool bypassSupported = ViewModel.BandBypassSupported;
        var grid = BuildPeqGrid(bypassSupported);
        int col = 0;

        // Bypass toggle — firmware 1.1.4+ only. Filled dot = active band, hollow
        // ring = user-bypassed or Flat. Disabled when type is Flat (no audio to
        // bypass). See band_bypass_spec.md §6.6 for display recommendations.
        if (bypassSupported)
        {
            // The dot carries the band's graph colour, as on the macOS Console.
            bool bandActive = !p.Bypass && p.Type != FilterType.Flat;
            var bandColor = BandColor(bandIndex);
            var dot = new Ellipse
            {
                Width = 12,
                Height = 12,
                StrokeThickness = 1.2,
                Stroke = new SolidColorBrush(p.Type == FilterType.Flat
                    ? Color.FromArgb(140, 160, 160, 160)
                    : bandColor),
                Fill = bandActive
                    ? new SolidColorBrush(bandColor)
                    : new SolidColorBrush(Colors.Transparent)
            };
            var bypassButton = new Button
            {
                Content = dot,
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                MinWidth = 22,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Tag = (channel, bandIndex),
                IsEnabled = p.Type != FilterType.Flat,
                Opacity = p.Type == FilterType.Flat ? 0.35 : 1.0,
                VerticalAlignment = VerticalAlignment.Center
            };
            // No tooltip — the filled-vs-hollow dot is self-explanatory once
            // the user has clicked one, and WinUI 3's default tooltip timing
            // pops the label whenever the cursor brushes one of the twelve
            // per-band toggles, which reads as noise.
            bypassButton.Click += OnFilterBypassToggled;
            Grid.SetColumn(bypassButton, col);
            grid.Children.Add(bypassButton);
            col++;
        }

        // Band number. Without per-band bypass (whose dot carries the band's
        // graph colour) the number does. Clicking it selects the band on the
        // graph: Ctrl toggles, Shift takes the run of rows from the last click.
        var bandLabel = new TextBlock
        {
            Text = $"{bandIndex + 1}",
            FontSize = 13,
            Foreground = !bandIsActive || p.Bypass
                ? (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
                : bypassSupported
                    ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                    : new SolidColorBrush(BandColor(bandIndex)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var bandHit = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        bandHit.Children.Add(bandLabel);
        bandHit.Tapped += (_, _) =>
        {
            if (!bandIsActive) return;
            static bool Down(Windows.System.VirtualKey k) =>
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var filters = ViewModel.GetFilters(channel);
            ViewModel.PeqSelection.ListClick(bandIndex,
                ctrl: Down(Windows.System.VirtualKey.Control),
                shift: Down(Windows.System.VirtualKey.Shift),
                isActive: i => i < filters.Count && filters[i].Type != FilterType.Flat);
        };
        Grid.SetColumn(bandHit, col);
        grid.Children.Add(bandHit);
        col++;

        // Filter type: the macOS Console's menu, with the shelves, cuts and
        // all-pass as submenus by slope. A button drawn like the dropdown it
        // replaced, so the row keeps its look. Low/high pass are named for what
        // they remove ("Low Cut" = high-pass). A stray crossover type in a PEQ
        // slot reads as Off.
        var typeButton = BuildFilterTypeButton(channel, bandIndex, p);
        typeButton.Opacity = p.Bypass ? 0.4 : 1.0;
        Grid.SetColumn(typeButton, col);
        grid.Children.Add(typeButton);
        col++;

        if (p.Type.IsLinkwitzTransform())
        {
            // LT reuses freq=f0, Q=Q0, gain=fp (Hz) plus the Qp sidecar — four
            // fields don't fit the three value columns, so a compact "Edit…" button
            // opens a flyout with all four (matching the macOS popover).
            var ltButton = BuildLinkwitzEditorButton(channel, bandIndex, p);
            ltButton.Opacity = p.Bypass ? 0.4 : 1.0;
            ltButton.Margin = new Thickness(15, 0, 0, 0); // breathing room off the type combo
            Grid.SetColumn(ltButton, col);   // freq column
            Grid.SetColumnSpan(ltButton, 3); // span freq/Q/gain
            grid.Children.Add(ltButton);
            col += 3;
        }
        else
        {
            // Frequency
            if (p.Type != FilterType.Flat)
            {
                var freqPanel = CreateValueField("Hz", p.Frequency, 58, (channel, bandIndex, "freq"));
                refs.Freq = (TextBox)freqPanel.Children[0];
                freqPanel.Opacity = p.Bypass ? 0.4 : 1.0;
                Grid.SetColumn(freqPanel, col);
                grid.Children.Add(freqPanel);
            }
            col++;

            // Gain (for peaking, low shelf, high shelf)
            if (p.Type.HasGain())
            {
                var gainPanel = CreateValueField("dB", p.Gain, 40, (channel, bandIndex, "gain"));
                refs.Gain = (TextBox)gainPanel.Children[0];
                gainPanel.Opacity = p.Bypass ? 0.4 : 1.0;
                Grid.SetColumn(gainPanel, col);
                grid.Children.Add(gainPanel);
            }
            col++;

            // Q (the width the header names)
            if (p.Type.HasQ())
            {
                var qPanel = CreateValueField("Q", p.Q, 44, (channel, bandIndex, "q"), decimals: 3);
                refs.Q = (TextBox)qPanel.Children[0];
                qPanel.Opacity = p.Bypass ? 0.4 : 1.0;
                Grid.SetColumn(qPanel, col);
                grid.Children.Add(qPanel);
            }
        }

        row.Child = grid;
        return row;
    }

    // ── Crossover (XO) filter page ─────────────────────────────────────────
    // Shared column geometry for the XO header row and each XO band row so the
    // "Family / Type / Slope / Frequency" labels line up over their controls.
    private const double XoDotWidth = 22;     // col 0 (bypass dot)
    private const double XoFamilyWidth = 140; // col 1
    private const double XoTypeWidth = 110;   // col 2 (fits "High Pass" + chevron)
    private const double XoSlopeWidth = 110;  // col 3
    private const double XoFreqWidth = 58;    // col 4 (text box; +Hz suffix)

    private static Grid BuildXoverGrid()
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(XoDotWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(XoFamilyWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(XoTypeWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(XoSlopeWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private Border CreateXoverEditorRow(Channel channel, int localBand, FilterParams p)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(128, 45, 45, 48)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 2, 0, 0)
        };

        var grid = BuildXoverGrid();
        bool isXover = CrossoverFilter.TryGetMeta(p.Type, out var meta);
        var family = isXover ? meta.Family : XoverFamily.None;
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        // ── Col 0: bypass dot (filled = active, hollow = bypassed / off) ──
        if (ViewModel.BandBypassSupported)
        {
            bool bandActive = !p.Bypass && isXover;
            var dot = new Ellipse
            {
                Width = 12,
                Height = 12,
                StrokeThickness = 1.2,
                Stroke = new SolidColorBrush(bandActive
                    ? Color.FromArgb(255, 128, 128, 128)
                    : Color.FromArgb(140, 160, 160, 160)),
                Fill = bandActive
                    ? new SolidColorBrush(Color.FromArgb(255, 128, 128, 128))
                    : new SolidColorBrush(Colors.Transparent)
            };
            var bypassButton = new Button
            {
                Content = dot,
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                MinWidth = 22,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Tag = (channel, localBand),
                IsEnabled = isXover,          // nothing to bypass when family is Off
                Opacity = isXover ? 1.0 : 0.35,
                VerticalAlignment = VerticalAlignment.Center
            };
            bypassButton.Click += OnXoverBypassToggled;
            Grid.SetColumn(bypassButton, 0);
            grid.Children.Add(bypassButton);
        }

        // ── Col 1: Family (includes "Off" = disabled band) ──
        var familyCombo = new ComboBox { Width = XoFamilyWidth, Foreground = secondary, Opacity = p.Bypass ? 0.4 : 1.0 };
        familyCombo.Items.Add(new ComboBoxItem { Content = "Off", Tag = XoverFamily.None });
        foreach (var fam in CrossoverFilter.Families)
            familyCombo.Items.Add(new ComboBoxItem { Content = CrossoverFilter.FamilyName(fam), Tag = fam });
        familyCombo.SelectedIndex = family == XoverFamily.None
            ? 0
            : Array.IndexOf(CrossoverFilter.Families, family) + 1;
        Grid.SetColumn(familyCombo, 1);
        grid.Children.Add(familyCombo);

        ComboBox? typeCombo = null;
        ComboBox? slopeCombo = null;

        if (family != XoverFamily.None)
        {
            // ── Col 2: Type (Low Pass / High Pass) ──
            typeCombo = new ComboBox { Width = XoTypeWidth, Foreground = secondary, Opacity = p.Bypass ? 0.4 : 1.0 };
            typeCombo.Items.Add(new ComboBoxItem { Content = "Low Pass", Tag = false });
            typeCombo.Items.Add(new ComboBoxItem { Content = "High Pass", Tag = true });
            typeCombo.SelectedIndex = meta.IsHighPass ? 1 : 0;
            Grid.SetColumn(typeCombo, 2);
            grid.Children.Add(typeCombo);

            // ── Col 3: Slope (orders valid for the family) ──
            slopeCombo = new ComboBox { Width = XoSlopeWidth, Foreground = secondary, Opacity = p.Bypass ? 0.4 : 1.0 };
            var orders = CrossoverFilter.OrdersFor(family);
            int slopeSel = 0;
            for (int i = 0; i < orders.Count; i++)
            {
                slopeCombo.Items.Add(new ComboBoxItem { Content = CrossoverFilter.SlopeLabel(orders[i]), Tag = orders[i] });
                if (orders[i] == meta.Order) slopeSel = i;
            }
            slopeCombo.SelectedIndex = slopeSel;
            Grid.SetColumn(slopeCombo, 3);
            grid.Children.Add(slopeCombo);

            // ── Col 4: Frequency (Hz) ──
            var freqPanel = CreateXoverFreqField(channel, localBand, p);
            freqPanel.Opacity = p.Bypass ? 0.4 : 1.0;
            Grid.SetColumn(freqPanel, 4);
            grid.Children.Add(freqPanel);
        }

        // Compose the three pickers into a single FilterType and push it. Handlers
        // are attached AFTER initial population so seeding SelectedIndex above
        // doesn't trigger a spurious write.
        async void Apply()
        {
            var selFamily = (XoverFamily)((ComboBoxItem)familyCombo.SelectedItem).Tag;
            FilterType newType;
            if (selFamily == XoverFamily.None)
            {
                newType = FilterType.Flat;
            }
            else
            {
                bool isHigh = typeCombo?.SelectedItem is ComboBoxItem ti && (bool)ti.Tag;
                // From Off there is no slope yet (0). A family that lacks the
                // current slope takes the nearest it has, the lower on a tie,
                // as on the macOS Console: BW3 to LR is LR2, Off to BW is BW1.
                int order = slopeCombo?.SelectedItem is ComboBoxItem si ? (int)si.Tag : 0;
                var orders = CrossoverFilter.OrdersFor(selFamily);
                if (!orders.Contains(order))
                    order = orders.OrderBy(o => Math.Abs(o - order)).ThenBy(o => o).First();
                newType = CrossoverFilter.Compose(selFamily, isHigh, order) ?? FilterType.Flat;
            }

            float freq = p.Frequency > 0 ? p.Frequency : 1000f;
            float q = p.Q > 0 ? p.Q : 0.707f;
            var np = new FilterParams(newType, freq, q, p.Gain) { Bypass = p.Bypass };
            await ViewModel.SetXoverFilter((int)channel.Id, localBand, np);
        }

        familyCombo.SelectionChanged += (_, _) => Apply();
        if (typeCombo != null) typeCombo.SelectionChanged += (_, _) => Apply();
        if (slopeCombo != null) slopeCombo.SelectionChanged += (_, _) => Apply();

        row.Child = grid;
        return row;
    }

    private StackPanel CreateXoverFreqField(Channel channel, int localBand, FilterParams p)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        var textBox = new TextBox
        {
            Width = XoFreqWidth,
            Text = FormatFilterValue(p.Frequency, 2),
            FontSize = 13,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            Style = (Style)RootGrid.Resources["InlineValueTextBoxStyle"]
        };

        async void Commit()
        {
            if (!float.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz)) return;
            // Unchanged: no write, and no editor rebuild (see OnFilterValueChanged).
            if (float.TryParse(FormatFilterValue(p.Frequency, 2), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var shown) && shown == hz)
                return;
            hz = Math.Clamp(hz, 10f, 20000f);
            var np = new FilterParams(p.Type, hz, p.Q, p.Gain) { Bypass = p.Bypass };
            p.Frequency = hz; // keep closure in sync, as the wheel handler does
            textBox.Text = FormatFilterValue(hz, 2);
            _inlineFilterCommits++;
            try { await ViewModel.SetXoverFilter((int)channel.Id, localBand, np); }
            catch { }
            finally { _inlineFilterCommits--; }
        }

        textBox.LostFocus += (_, _) => Commit();
        textBox.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Commit();
                FocusSink.Focus(FocusState.Programmatic);
            }
        };

        panel.Children.Add(textBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Hz",
            FontSize = 10,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center
        });

        // Wheel-scrub the cutoff (debounced USB write), mirroring the PEQ fields.
        // Ctrl-gated: a bare wheel over the field scrolls the filter list instead
        // of accidentally editing the value.
        panel.PointerWheelChanged += (s, e) =>
        {
            if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
            var delta = e.GetCurrentPoint(panel).Properties.MouseWheelDelta;
            if (delta == 0) return;

            var now = DateTime.UtcNow;
            bool fast = (now - _lastFilterScrollTime).TotalMilliseconds < 40;
            _lastFilterScrollTime = now;

            int direction = delta > 0 ? 1 : -1;
            float hz = Math.Clamp(p.Frequency + direction * (fast ? 10 : 1), 10, 20000);
            var np = new FilterParams(p.Type, hz, p.Q, p.Gain) { Bypass = p.Bypass };

            _isScrollAdjusting = true;
            ViewModel.SetXoverFilterDeferred((int)channel.Id, localBand, np);
            _isScrollAdjusting = false;
            p.Frequency = hz; // keep closure in sync for successive ticks
            textBox.Text = FormatFilterValue(hz, 2);
            e.Handled = true;
        };

        return panel;
    }

    private void OnXoverBypassToggled(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is (Channel channel, int localBand))
        {
            var bands = ViewModel.GetXoverFilters(channel);
            if (localBand < bands.Count)
            {
                bool newBypass = !bands[localBand].Bypass;
                _ = ViewModel.SetXoverBandBypass((int)channel.Id, localBand, newBypass);
            }
        }
    }

    /// <summary>
    /// The status bar pinned below the filter list: "Enable All" / "Bypass All"
    /// (acting on the currently visible page) on the left, and the PEQ | XO tab
    /// on the right. The XO tab is disabled on master/input channels and when
    /// the firmware doesn't expose crossover (pre-V11).
    /// </summary>
    private Border BuildFilterStatusBar(Channel channel, bool showXover, bool xoverAvailable)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ── Bulk bypass buttons (act on the visible page) ──
        var bulkPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        // The bands an Enable All or Bypass All acts on: every band with a type.
        // A half that would change nothing is disabled (and dims), as on the
        // macOS Console; with every band Off, both are.
        var pageBands = showXover ? ViewModel.GetXoverFilters(channel) : ViewModel.GetFilters(channel);
        var actionable = pageBands.Where(b => showXover ? b.Type.IsCrossover() : b.Type != FilterType.Flat).ToList();

        Button BulkButton(string text, bool bypass)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = text, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
                Height = 26,
                MinHeight = 0,
                Padding = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = actionable.Any(x => x.Bypass != bypass),
            };
            b.Click += async (_, _) =>
            {
                // Bypassing a crossover removes the high/low-pass protection and
                // sends full-range audio to the drivers, which can destroy a
                // tweeter. Re-enabling is safe, so only bypass asks.
                if (showXover && bypass)
                {
                    var confirm = new ContentDialog
                    {
                        Title = "Bypass this output's crossovers?",
                        Content = "This sends full-range audio to this output with no crossover protection, " +
                                  "which can damage unprotected drivers such as tweeters. Continue only if you are sure.",
                        PrimaryButtonText = "Bypass All",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = Content.XamlRoot
                    };
                    if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                }

                if (showXover) await ViewModel.SetAllXoverBypass((int)channel.Id, bypass);
                else await ViewModel.SetAllBandsBypass((int)channel.Id, bypass);
            };
            return b;
        }

        bulkPanel.Children.Add(BulkButton("Enable All", false));
        bulkPanel.Children.Add(BulkButton("Bypass All", true));
        // Per-band bypass is firmware 1.1.4+; without it there is nothing to act on.
        bulkPanel.Visibility = ViewModel.BandBypassSupported ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(bulkPanel, 0);
        grid.Children.Add(bulkPanel);

        // Clear All on output pages, beside the tabs: every band of the page
        // back to flat, after asking.
        if (channel.IsOutput)
        {
            var clearAll = new Button
            {
                Content = new TextBlock { Text = "Clear All", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
                Height = 26, MinHeight = 0, Padding = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            };
            ToolTipService.SetToolTip(clearAll, "Reset all bands");
            clearAll.Click += async (_, _) =>
            {
                var confirm = new ContentDialog
                {
                    Title = "Clear All Bands?",
                    Content = "Every band in this list will be reset to its default (flat) state. This cannot be undone.",
                    PrimaryButtonText = "Clear All",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                int id = (int)channel.Id;
                if (showXover)
                {
                    int n = ViewModel.GetXoverFilters(channel).Count;
                    for (int b = 0; b < n; b++)
                        await ViewModel.SetXoverFilter(id, b, new FilterParams(FilterType.Flat, 1000f, 0.707f, 0f));
                }
                else
                {
                    int n = ViewModel.GetFilters(channel).Count;
                    for (int b = 0; b < n; b++)
                        await ViewModel.SetFilter(id, b, new FilterParams(FilterType.Flat, 1000f, 0.707f, 0f));
                }
            };
            Grid.SetColumn(clearAll, 1);
            grid.Children.Add(clearAll);
        }

        // ── PEQ | XO segmented tab ──
        var tabPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };

        ToggleButton Tab(string text, bool isXoverTab)
        {
            var t = new ToggleButton
            {
                Content = new TextBlock { Text = text, FontSize = 11 },
                MinWidth = 44,
                MinHeight = 0,
                Height = 26,
                Padding = new Thickness(8, 0, 8, 0),
                IsChecked = isXoverTab == showXover,
                VerticalAlignment = VerticalAlignment.Center
            };
            // The darker accent with white text, as the Link Pair toggle.
            AccentToggle.Apply(t);
            t.Click += (_, _) =>
            {
                _filterPageIsXover = isXoverTab;
                if (_selectedChannel != null) ShowChannelEditor(_selectedChannel);
            };
            return t;
        }

        var peqTab = Tab("PEQ", false);
        var xoTab = Tab("XO", true);
        // Crossover only exists on output channels with V11+ firmware.
        xoTab.IsEnabled = xoverAvailable;
        ToolTipService.SetToolTip(xoTab, xoverAvailable
            ? null
            : new ToolTip { Content = "Crossover filters apply to output channels (V11+ firmware)" });

        tabPanel.Children.Add(peqTab);
        tabPanel.Children.Add(xoTab);
        Grid.SetColumn(tabPanel, 2);
        grid.Children.Add(tabPanel);

        return new Border
        {
            // Translucent in-app acrylic so the filter list shows through as it
            // scrolls behind the bar. TintLuminosityOpacity keeps the controls
            // readable over busy content; FallbackColor covers the case where
            // OS transparency effects are disabled.
            Background = new AcrylicBrush
            {
                TintColor = Color.FromArgb(255, 45, 45, 48),
                TintOpacity = 0.25,
                TintLuminosityOpacity = 0.45,
                FallbackColor = Color.FromArgb(210, 45, 45, 48)
            },
            // Same hairline tone as the top card's underline so the header and
            // footer read as a matched pair framing the list.
            BorderBrush = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 4, 12, 4),
            Child = grid
        };
    }

    private static string FormatFilterValue(float value, int decimals = 2) =>
        decimals > 0 ? value.ToString($"F{decimals}", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.') : value.ToString("F0", CultureInfo.InvariantCulture);

    private static string FormatFilterValueSigned(float value) =>
        (value >= 0 ? "+" : "") + FormatFilterValue(value);

    // Linkwitz Transform editor limits. f0/fp match the inline frequency field's
    // range; Qp matches the wire clamp in FilterParams.QpEncoded.
    private const float LtFreqMin = 20f, LtFreqMax = 20000f;
    private const float LtQMin = 0.1f, LtQMax = 20f;

    /// <summary>A compact button that opens a flyout to edit the four Linkwitz
    /// Transform parameters (driver f0/Q0, target fp/Qp) plus a DC-boost readout.
    /// The four fields don't fit the three inline value columns, so they live in a
    /// popover (matching the macOS reference).
    ///
    /// The popover edits a *draft* and only pushes it to the device on Apply.
    /// Committing per keystroke (as the inline fields do) was wrong here twice
    /// over: SetFilter raises FiltersChanged, which rebuilds the channel editor
    /// and so destroys the button hosting this flyout — the popover snapped shut
    /// the moment you tabbed or clicked between fields — and a half-typed target
    /// frequency would reach the speakers in the meantime, where LT's DC boost
    /// can be tens of dB.</summary>
    private Button BuildLinkwitzEditorButton(Channel channel, int bandIndex, FilterParams p)
    {
        var button = new Button
        {
            Content = $"f0 {p.Frequency:F0} · fp {p.Gain:F0} Hz",
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            // Match the inline freq/Q/gain fields (InlineValueTextBoxStyle).
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };

        // A fixed width with no sideways scrolling: the presenter's default lets
        // content scroll horizontally, so the notes never wrapped and the panel
        // panned left and right.
        var flyout = new Flyout
        {
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters =
                {
                    new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled),
                    new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled),
                },
            },
        };

        var content = new StackPanel { Spacing = 10, Width = 300 };
        content.Children.Add(new TextBlock
        {
            Text = "Linkwitz Transform",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "Re-align a sealed woofer's rolloff from the driver's (f0, Q0) to a target (fp, Qp).",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        var f0Box = LtValueBox(p.Frequency, 66, 0);
        var q0Box = LtValueBox(p.Q, 56, 3);
        var fpBox = LtValueBox(p.Gain, 66, 0);
        var qpBox = LtValueBox(p.Qp, 56, 3);

        content.Children.Add(LtRow("Driver", LtLabelled(f0Box, "Hz"), LtLabelled(q0Box, "Q0")));
        content.Children.Add(LtRow("Target", LtLabelled(fpBox, "Hz"), LtLabelled(qpBox, "Qp")));

        var secondaryBrush = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var orangeBrush = new SolidColorBrush(Color.FromArgb(255, 255, 159, 10));
        content.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });

        // DC boost: the real low-frequency gain the transform asks for. Orange
        // past +15 dB, where excursion and amplifier headroom start to run out.
        var boostRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        boostRow.Children.Add(new TextBlock { Text = "DC boost", FontSize = 11, Foreground = secondaryBrush, VerticalAlignment = VerticalAlignment.Center });
        var boostValue = new TextBlock
        {
            FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = new FontFamily("Cascadia Code, Consolas"), VerticalAlignment = VerticalAlignment.Center,
        };
        var boostWarn = new FontIcon { Glyph = "\uE7BA", FontSize = 12, Foreground = orangeBrush, VerticalAlignment = VerticalAlignment.Center };
        boostRow.Children.Add(boostValue);
        boostRow.Children.Add(boostWarn);
        content.Children.Add(boostRow);
        content.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });

        // Status, then Revert and Apply, both only while the draft differs.
        var statusText = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var revertButton = new Button { Content = "Revert", MinWidth = 72 };
        var applyButton = new Button
        {
            Content = "Apply",
            MinWidth = 72,
            Style = Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent)
                ? accent as Style
                : null
        };
        var statusRow = new Grid { ColumnSpacing = 8 };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusRow.Children.Add(statusText);
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttonRow.Children.Add(revertButton);
        buttonRow.Children.Add(applyButton);
        Grid.SetColumn(buttonRow, 1);
        statusRow.Children.Add(buttonRow);
        content.Children.Add(statusRow);

        var warnBrush = new SolidColorBrush(Color.FromArgb(255, 240, 180, 90));

        // Reads the draft out of the four boxes; false if anything is unparseable
        // or out of range. Non-short-circuiting `&` so every out value is assigned.
        bool TryReadDraft(out float f0, out float q0, out float fp, out float qp) =>
            LtTryRead(f0Box, LtFreqMin, LtFreqMax, out f0)
          & LtTryRead(q0Box, LtQMin, LtQMax, out q0)
          & LtTryRead(fpBox, LtFreqMin, LtFreqMax, out fp)
          & LtTryRead(qpBox, LtQMin, LtQMax, out qp);

        FilterParams? Live()
        {
            var filters = ViewModel.GetFilters(channel);
            return bandIndex < filters.Count ? filters[bandIndex] : null;
        }

        void Seed()
        {
            if (Live() is not { } cur) return;
            // Decimals kept, so a fractional f0 or fp reads back as applied.
            f0Box.Text = FormatFilterValue(cur.Frequency, 2);
            q0Box.Text = FormatFilterValue(cur.Q, 3);
            fpBox.Text = FormatFilterValue(cur.Gain, 2);
            qpBox.Text = FormatFilterValue(cur.Qp, 3);
        }

        void RefreshPreview()
        {
            bool valid = TryReadDraft(out float f0, out float q0, out float fp, out float qp);
            var cur = Live();
            // The macOS Console's tolerances: what the fields can show.
            bool dirty = !valid || cur == null
                || Math.Abs(f0 - cur.Frequency) >= 0.05f || Math.Abs(fp - cur.Gain) >= 0.05f
                || Math.Abs(q0 - cur.Q) >= 0.0005f || Math.Abs(qp - cur.Qp) >= 1f / 512;
            applyButton.IsEnabled = valid && dirty;
            revertButton.IsEnabled = dirty;

            if (!valid)
            {
                boostValue.Text = "-";
                boostWarn.Visibility = Visibility.Collapsed;
                statusText.Text = $"f0 and fp: {LtFreqMin:F0}-{LtFreqMax:F0} Hz, Q0 and Qp: {LtQMin:0.#}-{LtQMax:0.#}";
                statusText.Foreground = warnBrush;
                return;
            }

            double dcBoost = f0 > 0 && fp > 0 ? 40.0 * Math.Log10(f0 / fp) : 0;
            boostValue.Text = dcBoost.ToString("+0.0;-0.0;+0.0", CultureInfo.InvariantCulture) + " dB";
            bool tooMuch = dcBoost > 15;
            boostValue.Foreground = tooMuch ? orangeBrush : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            boostWarn.Visibility = tooMuch ? Visibility.Visible : Visibility.Collapsed;
            statusText.Text = dirty ? "Not applied yet" : "Applied";
            statusText.Foreground = dirty ? orangeBrush : secondaryBrush;
        }

        void Apply()
        {
            if (!TryReadDraft(out float f0, out float q0, out float fp, out float qp)) return;
            if (Live() is not { } cur) return;
            var draft = cur.Clone();
            draft.Frequency = f0;
            draft.Q = q0;
            draft.Gain = fp;   // LT carries the target frequency in the gain field
            draft.Qp = qp;

            // Hide first: SetFilter rebuilds the channel editor out from under
            // this button, and dismissing an already-orphaned flyout throws.
            flyout.Hide();
            _ = ViewModel.SetFilter((int)channel.Id, bandIndex, draft);
        }

        foreach (var box in new[] { f0Box, q0Box, fpBox, qpBox })
        {
            box.TextChanged += (_, _) => RefreshPreview();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    Apply();
                }
                else if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    e.Handled = true;
                    flyout.Hide();
                }
            };
        }

        revertButton.Click += (_, _) => { Seed(); RefreshPreview(); };
        applyButton.Click += (_, _) => Apply();

        // Re-seed from the live filter each time it opens, so a discarded edit
        // (Escape or light dismiss) doesn't linger in the boxes.
        flyout.Opening += (_, _) => { Seed(); RefreshPreview(); };

        RefreshPreview();
        flyout.Content = content;
        button.Flyout = flyout;
        ToolTipService.SetToolTip(button, "Edit Linkwitz Transform parameters (f0, Q0, fp, Qp)");
        return button;
    }

    private TextBox LtValueBox(float value, double width, int decimals) => new()
    {
        Width = width,
        Text = FormatFilterValue(value, decimals),
        FontSize = 13,
        FontFamily = new FontFamily("Cascadia Code, Consolas"),
        Style = (Style)RootGrid.Resources["InlineValueTextBoxStyle"]
    };

    private static bool LtTryRead(TextBox box, float min, float max, out float value) =>
        float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && value >= min && value <= max;

    private static StackPanel LtLabelled(TextBox box, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private static Grid LtRow(string label, FrameworkElement a, FrameworkElement b)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        Grid.SetColumn(lbl, 0); g.Children.Add(lbl);
        Grid.SetColumn(a, 1); g.Children.Add(a);
        Grid.SetColumn(b, 2); g.Children.Add(b);
        return g;
    }

    private StackPanel CreateValueField(string label, float value, double width, (Channel channel, int band, string param) tag, int decimals = 2)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        var textBox = new TextBox
        {
            Width = width,
            Text = FormatFilterValue(value, decimals),
            Tag = tag,
            FontSize = 13,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            Style = (Style)RootGrid.Resources["InlineValueTextBoxStyle"]
        };
        textBox.LostFocus += OnFilterValueChanged;
        textBox.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                OnFilterValueChanged(s, null!);
                // Move focus to hidden sink to clear selection and cursor
                FocusSink.Focus(FocusState.Programmatic);
            }
        };

        panel.Children.Add(textBox);
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center
        });

        // Ctrl-gated like the crossover cutoff: a bare wheel scrolls the filter
        // list; holding Ctrl scrubs the value.
        panel.PointerWheelChanged += (s, e) =>
        {
            if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
            var delta = e.GetCurrentPoint(panel).Properties.MouseWheelDelta;
            if (delta == 0) return;

            var now = DateTime.UtcNow;
            bool fast = (now - _lastFilterScrollTime).TotalMilliseconds < 40;
            _lastFilterScrollTime = now;

            int direction = delta > 0 ? 1 : -1;

            var filters = ViewModel.GetFilters(tag.channel);
            if (tag.band >= filters.Count) return;
            var p = filters[tag.band].Clone();

            switch (tag.param)
            {
                case "freq":
                    p.Frequency = Math.Clamp(p.Frequency + direction * (fast ? 10 : 1), 20, 20000);
                    break;
                case "q":
                    p.Q = Math.Clamp(p.Q + direction * (fast ? 0.1f : 0.01f), 0.1f, 20);
                    break;
                case "gain":
                    p.Gain = Math.Clamp(p.Gain + direction * (fast ? 0.1f : 0.01f), -20, 20);
                    break;
            }

            _isScrollAdjusting = true;
            ViewModel.SetFilterDeferred((int)tag.channel.Id, tag.band, p);
            _isScrollAdjusting = false;
            textBox.Text = FormatFilterValue(
                tag.param == "freq" ? p.Frequency : tag.param == "q" ? p.Q : p.Gain,
                tag.param == "q" ? 3 : 2);
            e.Handled = true;
        };

        return panel;
    }

    private void ShowDashboard()
    {
        _selectedChannel = null;
        FilterStatusBarHost.Visibility = Visibility.Collapsed;
        ChannelHeaderHost.Visibility = Visibility.Collapsed;
        ChannelHeaderHost.Child = null;
        SyncLinkedPairGradient();
        _filterPageChannelId = -1;
        ViewModel.PeqSelection.Reset();
        BodePlot.SetSelectedChannel(-1);
        if (AppSettings.Instance.PopoutFollowsSelectedChannel)
            _graphWindow?.SetSelectedChannel(-1);
        ViewModel.SetRtaPage(-1);
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
                    UpdateFirmwareMismatchBar();
                    UpdateLimiterCell();
                    break;
                case nameof(MainViewModel.FirmwareMatch):
                    UpdateFirmwareMismatchBar();
                    break;
                case nameof(MainViewModel.ActiveInputChannelCount):
                    // The number of USB input channels changed (Windows format /
                    // input source) — rebuild the input rows, and the dashboard
                    // with them since it carries one card per input pair.
                    if (ViewModel.IsDeviceConnected)
                    {
                        InitializeChannelLists();
                        if (DashboardPanel.Visibility == Visibility.Visible)
                            InitializeDashboard();
                    }
                    break;
                case nameof(MainViewModel.ErrorMessage):
                    UpdateConnectionStatus();
                    break;
                case nameof(MainViewModel.SelectedDeviceItem):
                    UpdateConnectionStatus();
                    break;
                case nameof(MainViewModel.MasterVolumeDb):
                case nameof(MainViewModel.UserVolumeDb):
                    UpdateMasterVolumeDisplay();
                    break;
                case nameof(MainViewModel.InputPreampLDb):
                case nameof(MainViewModel.InputPreampRDb):
                    UpdateInputPreampEditor();
                    break;
                case nameof(MainViewModel.Bypass):
                    UpdateBypassButton();
                    break;
                case nameof(MainViewModel.LoudnessEnabled):
                    UpdateShortcutIconStates();
                    break;
                case nameof(MainViewModel.CrossfeedEnabled):
                    UpdateShortcutIconStates();
                    break;
                case nameof(MainViewModel.PsybassEnabled):
                    UpdateShortcutIconStates();
                    break;
                // SpdifRxPin doubles as the "S/PDIF input config changed" signal
                // (pins, enables, instance count) — resync the Source items.
                case nameof(MainViewModel.SpdifRxPin):
                case nameof(MainViewModel.MultiSpdifSupported):
                case nameof(MainViewModel.AdatInputSelectable):
                    RefreshSourceComboBox();
                    break;
                case nameof(MainViewModel.LevellerEnabled):
                    UpdateShortcutIconStates();
                    break;
                case nameof(MainViewModel.Status):
                    UpdateMeters();
                    break;
            }
        });
    }

    private void UpdateConnectionStatus()
    {
        var accentColor = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        ConnectionIndicator.Fill = new SolidColorBrush(ViewModel.IsDeviceConnected ? accentColor : Colors.Red);
        UpdateConnectionTooltip();
        UpdateDeviceSelector();

        if (!ViewModel.IsDeviceConnected)
        {
            InputChannelsList.Items.Clear();
            OutputChannelsList.Items.Clear();
            _channelListItems.Clear();
            _outputChannelItems.Clear();
            _channelMeters.Clear();

            FadeCurves(0);

            // Hide preset and source sections
            PresetSection.Visibility = Visibility.Collapsed;
            SourceSection.Visibility = Visibility.Collapsed;

            // Return to empty dashboard view
            _selectedChannel = null;
            FilterStatusBarHost.Visibility = Visibility.Collapsed;
            ChannelHeaderHost.Visibility = Visibility.Collapsed;
            ChannelHeaderHost.Child = null;
            ChannelEditorPanel.Visibility = Visibility.Collapsed;
            ChannelEditorPanel.Children.Clear();
            DashboardPanel.Visibility = Visibility.Visible;
            DashboardPanel.Children.Clear();
            _dashboardCardTexts.Clear();
            RebuildDashboardTextMaps();
        }
        else
        {
            RestorePairLinksForDevice();
            InitializeChannelLists();
            FadeCurves(1);
        }
    }

    /// <summary>A channel of input pair <paramref name="pair"/> (0 = 1/2), for
    /// the link queries that take a channel.</summary>
    private static int PairFirstChannel(int pair) =>
        pair == 0 ? (int)ChannelId.MasterLeft : ChannelMap.ExtraInputFirstId + (pair - 1) * 2;

    /// <summary>Put back the links this device had, when it has an entry; a
    /// device seen for the first time keeps the links already on screen.</summary>
    /// <summary>Keep the connected device's links on every change, whatever
    /// made it (the link toggle, a preset, a restore), as on the macOS Console.</summary>
    private void StoreDeviceLinks()
    {
        if (!ViewModel.IsDeviceConnected) return;
        if (ViewModel.Device.SelectedDeviceInfo?.Serial is not { Length: > 0 } serial) return;
        int mask = 0;
        for (int p = 0; p < 4; p++)
            if (ViewModel.IsInputPairLinked(PairFirstChannel(p))) mask |= 1 << p;
        if (AppSettings.Instance.LinkedInputPairsBySerial.TryGetValue(serial, out int stored) && stored == mask) return;
        AppSettings.Instance.LinkedInputPairsBySerial[serial] = mask;
        AppSettings.Instance.Save();
    }

    private void RestorePairLinksForDevice()
    {
        if (ViewModel.Device.SelectedDeviceInfo?.Serial is not { Length: > 0 } serial) return;
        if (!AppSettings.Instance.LinkedInputPairsBySerial.TryGetValue(serial, out int mask)) return;
        for (int pair = 0; pair < 4; pair++)
            ViewModel.SetInputPairLinked(pair, (mask & (1 << pair)) != 0);
        SyncLinkedPairGradient();
    }

    /// <summary>Ctrl+C / Ctrl+V copy and paste the open channel page's
    /// parameters, as Cmd+C / Cmd+V do on the macOS Console. A text field with
    /// focus keeps them for text.</summary>
    private void InstallChannelClipboardShortcuts()
    {
        bool TextHasFocus() => FocusManager.GetFocusedElement(Content.XamlRoot)
            is TextBox or PasswordBox or RichEditBox or AutoSuggestBox;
        var copy = new KeyboardAccelerator { Key = Windows.System.VirtualKey.C, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        copy.Invoked += (_, args) =>
        {
            if (TextHasFocus() || WizardShown || _selectedChannel == null || ChannelEditorPanel.Visibility != Visibility.Visible) return;
            args.Handled = true;
            ViewModel.CopyChannelParams(_selectedChannel);
        };
        var paste = new KeyboardAccelerator { Key = Windows.System.VirtualKey.V, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        paste.Invoked += (_, args) =>
        {
            if (TextHasFocus() || WizardShown || _selectedChannel == null || ChannelEditorPanel.Visibility != Visibility.Visible) return;
            if (!ViewModel.HasChannelClipboard) return;
            args.Handled = true;
            ViewModel.PasteChannelParams(_selectedChannel);
            ShowChannelEditor(_selectedChannel);
        };
        foreach (var a in new[] { copy, paste })
        {
            // Not shown as tooltips on the window's content.
            a.ScopeOwner = null;
            RootGrid.KeyboardAccelerators.Add(a);
        }
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>The connection dot says what it means on hover: connected, the
    /// last connection error, or how to retry. After the macOS Console.</summary>
    private void UpdateConnectionTooltip()
    {
        string tip = ViewModel.IsDeviceConnected ? "Connected"
            : !string.IsNullOrWhiteSpace(ViewModel.ErrorMessage) && ViewModel.ErrorMessage != "Disconnected"
                ? ViewModel.ErrorMessage!
                : "Not connected. Right-click here to retry.";
        ToolTipService.SetToolTip(ConnectionIndicator, tip);
    }

    // Multi-device UI

    private void UpdateDeviceSelector()
    {
        var selected = ViewModel.SelectedDeviceItem
                       ?? ViewModel.Device.SelectedDeviceInfo;

        if (selected != null)
            DeviceSelectorText.Text = selected.DisplayName;
        else
            DeviceSelectorText.Text = ViewModel.ErrorMessage ?? "Disconnected";
    }

    private void OnDeviceSelectorPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        DeviceSelectorBtn.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        DeviceSelectorBtn.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    }

    private void OnDeviceSelectorPointerExited(object sender, PointerRoutedEventArgs e)
    {
        DeviceSelectorBtn.Background = new SolidColorBrush(Colors.Transparent);
        DeviceSelectorBtn.BorderBrush = new SolidColorBrush(Colors.Transparent);
    }

    private void OnDeviceSelectorTapped(object sender, TappedRoutedEventArgs e)
    {
        var devices = ViewModel.AvailableDevices;
        if (devices.Count == 0) return;

        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var current = ViewModel.Device.SelectedDeviceInfo;

        foreach (var d in devices)
        {
            var device = d;
            var item = new MenuFlyoutItem { Text = device.DisplayName };
            if (current != null && device.Serial == current.Serial)
                item.Icon = new FontIcon { Glyph = "\uE73E" };
            item.Click += async (s, args) =>
            {
                if (current != null && device.Serial == current.Serial) return;

                // Staged Settings changes describe the current device; applying
                // them after a switch would write them to the wrong board.
                var tracker = _settingsWindow?.Tracker;
                if (ViewModel.IsDeviceConnected && tracker != null && tracker.Count > 0)
                {
                    var confirm = new ContentDialog
                    {
                        Title = "Unsaved Settings Changes",
                        Content = "Settings has pending changes for the current device that have not been saved. " +
                                  "Switching devices will discard them.",
                        PrimaryButtonText = "Discard and Switch",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = Content.XamlRoot
                    };
                    if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                    tracker.DiscardAll();
                }

                ViewModel.SwitchToDeviceCommand.Execute(device);
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(DeviceSelectorBtn);
    }



    /// <summary>
    /// Prompt the user to pick which input channel's filters should win when
    /// enabling a pair link against differing banks. Returns the chosen channel
    /// id, or null if the user cancelled.
    /// </summary>
    private async Task<int?> AskWhichFiltersToKeep(Channel channel)
    {
        int partnerId = ChannelMap.LinkedPartnerId((int)channel.Id);
        var partner = Channel.FromId((ChannelId)partnerId);
        var first = (int)channel.Id < partnerId ? channel : partner;
        var second = first == channel ? partner : channel;
        var firstName = ViewModel.GetChannelName(first);
        var secondName = ViewModel.GetChannelName(second);

        // What differs, as on the macOS Console: linking copies filters and trim.
        bool filters = ViewModel.InputPairFiltersDiffer((int)channel.Id);
        bool trims = ViewModel.InputPairPreampsDiffer((int)channel.Id);
        string what = filters && trims ? "different filters and input trims" : filters ? "different filters" : "different input trims";
        var dialog = new ContentDialog
        {
            Title = $"{firstName} and {secondName} don't match",
            Content = $"These inputs have {what}. Linking mirrors future edits across both channels, but it cannot merge "
                    + "settings that already differ.\n\nChoose which channel's filters and trim to copy onto the other. "
                    + "This overwrites the other channel and cannot be undone.",
            PrimaryButtonText = $"Keep {firstName}",
            SecondaryButtonText = $"Keep {secondName}",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => (int)first.Id,
            ContentDialogResult.Secondary => (int)second.Id,
            _ => (int?)null
        };
    }

    /// <summary>Persist one pair's link state to AppSettings (pair 0 keeps the
    /// legacy MasterPeqLinked key).</summary>
    private void PersistPairLink(int pair, bool value)
    {
        if (pair == 0)
        {
            AppSettings.Instance.MasterPeqLinked = value;
        }
        else
        {
            if (AppSettings.Instance.InputPairLinkedExt is not { Length: >= 3 })
                AppSettings.Instance.InputPairLinkedExt = new bool[3];
            AppSettings.Instance.InputPairLinkedExt[pair - 1] = value;
        }
        AppSettings.Instance.Save();
    }

    /// <summary>Ids of every input channel whose stereo pair is currently linked.</summary>
    private IEnumerable<int> AllLinkedInputIds()
    {
        if (ViewModel.GetInputPairLinked(0))
        {
            yield return (int)ChannelId.MasterLeft;
            yield return (int)ChannelId.MasterRight;
        }
        for (int pair = 1; pair <= 3; pair++)
            if (ViewModel.GetInputPairLinked(pair))
            {
                int a = (int)ChannelId.Input3 + (pair - 1) * 2;
                yield return a;
                yield return a + 1;
            }
    }

    /// <summary>Keep the graph pop-out button clear of the phase degree axis:
    /// when the phase overlay is on, the plot reserves ~34px on the right for
    /// the scale, so shift the button left of it.</summary>
    private void UpdateGraphPopoutButtonMargin() =>
        GraphPopoutButton.Margin = new Thickness(0, 8, AppSettings.Instance.ShowPhase ? 38 : 8, 0);

    /// <summary>Push the linked-pair set for the current selection to the plot:
    /// the selected input's pair when linked, every linked pair on the dashboard
    /// (no selection), none when an output is selected.</summary>
    private void SyncLinkedPairGradient()
    {
        IEnumerable<int> ids;
        if (_selectedChannel is { } ch)
            ids = !ch.IsOutput && ViewModel.IsInputPairLinked((int)ch.Id)
                ? new[] { (int)ch.Id, ChannelMap.LinkedPartnerId((int)ch.Id) }
                : Array.Empty<int>();
        else
            ids = AllLinkedInputIds();
        BodePlot.SetLinkedInputs(ids);
    }

    private async Task<UnsavedAction> ShowUnsavedChangesDialogAsync(string? summary)
    {
        var message = summary != null
            ? $"You have unsaved changes to the current preset:\n\n{summary}\n\nSave before switching devices?"
            : "You have unsaved changes to the current preset.\n\nSave before switching devices?";

        var dialog = new ContentDialog
        {
            Title = "Unsaved Changes",
            Content = message,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => UnsavedAction.Save,
            ContentDialogResult.Secondary => UnsavedAction.Discard,
            _ => UnsavedAction.Cancel
        };
    }

    private DispatcherTimer? _curveFadeTimer;
    private double _curveFadeTarget;

    private void FadeCurves(double targetOpacity)
    {
        _curveFadeTarget = targetOpacity;
        _curveFadeTimer?.Stop();
        _curveFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _curveFadeTimer.Tick += (s, e) =>
        {
            double current = BodePlot.GetCurveOpacity();
            double diff = _curveFadeTarget - current;
            if (Math.Abs(diff) < 0.02)
            {
                BodePlot.SetCurveOpacity(_curveFadeTarget);
                _curveFadeTimer.Stop();
            }
            else
            {
                BodePlot.SetCurveOpacity(current + diff * 0.15);
            }
        };
        _curveFadeTimer.Start();
    }

    private void FadeElement(UIElement element, double targetOpacity)
    {
        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = targetOpacity == 0 ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        var sb = new Storyboard();
        sb.Children.Add(animation);
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        sb.Begin();
    }

    private void UpdateInputPreampEditor()
    {
        if (_inputPreampSlider == null || _inputPreampValueText == null) return;
        if (_selectedChannel == null || _selectedChannel.IsOutput) return;
        float v = ViewModel.InputPreampAt(InputWireIndex(_selectedChannel));
        if (_inputPreampDrag?.IsDragging == true) return;
        if (Math.Abs(_inputPreampSlider.Value - v) > 0.05)
            _inputPreampDrag?.Show(v);
        if (_inputPreampValueText.FocusState == FocusState.Unfocused)
            _inputPreampValueText.Text = $"{v:F1} dB";
    }

    /// <summary>Wire input index (0..7) for an input channel: Master L/R → 0/1,
    /// Input3..8 (ids 11..16) → 2..7.</summary>
    private static int InputWireIndex(Channel ch) => ch.Id switch
    {
        ChannelId.MasterLeft => 0,
        ChannelId.MasterRight => 1,
        _ => (int)ch.Id - ChannelMap.ExtraInputFirstId + 2,
    };

    // ── Volume slider taper (user + master) ─────────────────────────────────
    //
    // Continuous log-shaped curve shared by both modes — same dB lives at the
    // same slider position regardless of which mode is active. With curve =
    // log2(3) ≈ 1.585, the top 50% covers exactly 0…-20 dB.
    //
    //   pos_norm = 1 - (-db / -min_db)^(1/curve)
    //   db       = min_db * (1 - pos_norm)^curve
    //
    // The slider uses a wide integer range (VolumeSliderMax = 1000 ticks) so
    // dragging feels smooth even with a piecewise-zero second derivative; the
    // dB value sent to firmware is snapped to a coarser quantum:
    //   • user volume   → 1.0 dB (matches firmware's preset vol_index storage,
    //                     avoiding the save/reload rounding step)
    //   • master volume → 0.5 dB (matches the prior discrete taper's resolution
    //                     in the loud region)
    //
    // Master mode reserves slider position 0 for the -128 dB mute sentinel.
    // Positions [1, VolumeSliderMax] map to the same log curve over [-60, 0]
    // that user mode uses, so the visible scale is identical between modes;
    // dragging onto position 0 snaps to mute. The firmware's master-volume
    // range below -60 dB is not reachable through this slider — sub-60 dB
    // masters are an inaudible / pre-mute region and the mute sentinel covers
    // "silence" cleanly.
    private const double VolumeMinDb             = -60.0;
    private const double VolumeMaxDb             = 0.0;
    private const double MasterVolumeMuteDb      = -128.0;   // sentinel below the log range
    private const double LogTaperCurve           = 1.5849625007211563;  // log2(3) → -20 dB at pos 50%
    private const double VolumeSliderMax         = 1000.0;
    private const double UserVolumeQuantumDb     = 1.0;
    private const double MasterVolumeQuantumDb   = 0.5;

    // Kept as aliases so older references in this file still compile if any
    // were missed during the refactor; both ranges are now identical.
    private const double UserVolumeMinDb   = VolumeMinDb;
    private const double UserVolumeMaxDb   = VolumeMaxDb;

    // dB → normalized 0..1 position along the log curve.
    private static double LogTaperDbToNormalized(double db, double minDb)
    {
        if (db >= 0.0) return 1.0;
        if (db <= minDb) return 0.0;
        double ratio = db / minDb;                       // 0..1, 0 at top
        return 1.0 - Math.Pow(ratio, 1.0 / LogTaperCurve);
    }

    // Normalized 0..1 → dB along the log curve.
    private static double LogTaperNormalizedToDb(double posNorm, double minDb)
    {
        var clamped = Math.Clamp(posNorm, 0.0, 1.0);
        return minDb * Math.Pow(1.0 - clamped, LogTaperCurve);
    }

    private static double QuantizeDb(double db, double quantum)
    {
        var q = Math.Round(db / quantum) * quantum;
        // IEEE 754 keeps the sign bit through Math.Round, so a tiny negative
        // input (e.g. -60 * 0^curve = -0.0 at the top of the log taper, or a
        // -0.0001 echoed back from firmware quantization) rounds to -0.0 and
        // formats as "-0 dB". Add 0.0 to canonicalize: -0.0 + 0.0 = +0.0.
        return q == 0.0 ? 0.0 : q;
    }

    private static double UserVolumeDbToSliderPos(double db)
    {
        var norm = LogTaperDbToNormalized(Math.Clamp(db, VolumeMinDb, VolumeMaxDb), VolumeMinDb);
        return norm * VolumeSliderMax;
    }

    private static double UserVolumeSliderPosToDb(double pos)
    {
        var clamped = Math.Clamp(pos, 0, VolumeSliderMax);
        var db = LogTaperNormalizedToDb(clamped / VolumeSliderMax, VolumeMinDb);
        return QuantizeDb(db, UserVolumeQuantumDb);
    }

    private static double MasterVolumeDbToSliderPos(double db)
    {
        if (db <= MasterVolumeMuteDb + 0.5) return 0.0;  // mute sentinel sits at the bottom tick
        var norm = LogTaperDbToNormalized(Math.Clamp(db, VolumeMinDb, VolumeMaxDb), VolumeMinDb);
        // Reserve position 0 for mute; the log curve spans [1, VolumeSliderMax].
        return 1.0 + norm * (VolumeSliderMax - 1.0);
    }

    private static double MasterVolumeSliderPosToDb(double pos)
    {
        var clamped = Math.Clamp(pos, 0, VolumeSliderMax);
        if (clamped < 1.0) return MasterVolumeMuteDb;
        var posNorm = (clamped - 1.0) / (VolumeSliderMax - 1.0);
        var db = LogTaperNormalizedToDb(posNorm, VolumeMinDb);
        return QuantizeDb(db, MasterVolumeQuantumDb);
    }

    private bool _updatingMasterVolumeSlider;
    private bool _sidebarVolumeIsMaster = true;

    // macOS systemRed-ish; reads well against the dark acrylic sidebar.
    private static readonly Color SidebarVolumeMasterTint = Color.FromArgb(255, 230, 70, 60);

    /// <summary>
    /// Apply the chosen sidebar volume mode: rewrite the label, toggle the
    /// menu checkmarks, retint the slider track/thumb (red for master), and
    /// resync the slider position + readout from the corresponding dB value.
    /// </summary>
    private void ApplySidebarVolumeMode(bool isMaster, bool persist)
    {
        _sidebarVolumeIsMaster = isMaster;
        SidebarVolumeModeLabel.Text = isMaster ? "Master Volume ▾" : "User Volume ▾";

        UserVolumeModeMenuItem.Icon = isMaster ? null : new FontIcon { Glyph = "" };  // checkmark
        MasterVolumeModeMenuItem.Icon = isMaster ? new FontIcon { Glyph = "" } : null;

        var tint = isMaster
            ? SidebarVolumeMasterTint
            : (Color)Application.Current.Resources["SystemAccentColor"];
        SetSidebarSliderTint(tint);

        // Both modes share the same log-tapered slider range (0..VolumeSliderMax);
        // only the underlying dB↔position mapping differs (user covers [-60, 0],
        // master covers [-125, 0] with a mute sentinel at position 0). XAML sets
        // Maximum once to VolumeSliderMax, so there's nothing to switch here.
        UpdateMasterVolumeDisplay();

        if (persist && AppSettings.Instance.SidebarVolumeMode != (isMaster ? "master" : "user"))
        {
            AppSettings.Instance.SidebarVolumeMode = isMaster ? "master" : "user";
            AppSettings.Instance.Save();
        }
    }

    private void SetSidebarSliderTint(Color c)
    {
        // The six brushes were declared in MainWindow.xaml's Slider.Resources;
        // rewriting their Color updates the slider visuals live.
        foreach (var key in new[] {
            "SliderTrackValueFill", "SliderTrackValueFillPointerOver",
            "SliderTrackValueFillPressed", "SliderThumbBackground",
            "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed" })
        {
            if (MasterVolumeSlider.Resources[key] is SolidColorBrush brush)
                brush.Color = c;
        }
    }

    /// <summary>
    /// Refresh the slider position and dB readout from whichever source the
    /// current mode tracks. Called whenever MasterVolumeDb / UserVolumeDb
    /// change or the mode itself toggles.
    /// </summary>
    private void UpdateMasterVolumeDisplay()
    {
        _updatingMasterVolumeSlider = true;
        try
        {
            if (_sidebarVolumeIsMaster)
            {
                var v = ViewModel.MasterVolumeDb;
                var pos = MasterVolumeDbToSliderPos(v);
                // 1-tick tolerance — the slider snaps to integer positions, so
                // any difference smaller than that is just the round-trip
                // through QuantizeDb (e.g., a preset-loaded -12.3 will round
                // to -12.5, which is a positional drift well under one tick).
                if (Math.Abs(MasterVolumeSlider.Value - pos) > 1.0)
                    MasterVolumeSlider.Value = pos;
                MasterVolumeValueText.Text = v <= MasterVolumeMuteDb + 0.5
                    ? "-inf dB" : $"{v:F1} dB";
            }
            else
            {
                // Snap the displayed value through the same quantum the taper
                // uses on output (1 dB for user volume) so a preset-loaded
                // fractional value lands on a clean readout immediately.
                var quantized = QuantizeDb(Math.Clamp((double)ViewModel.UserVolumeDb,
                                                     UserVolumeMinDb, UserVolumeMaxDb),
                                           UserVolumeQuantumDb);
                var pos = UserVolumeDbToSliderPos(quantized);
                if (Math.Abs(MasterVolumeSlider.Value - pos) > 1.0)
                    MasterVolumeSlider.Value = pos;
                MasterVolumeValueText.Text = $"{quantized:F0} dB";
            }
        }
        finally
        {
            _updatingMasterVolumeSlider = false;
        }
    }

    private void OnSidebarVolumeModeLabelTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            FlyoutBase.ShowAttachedFlyout(fe);
    }

    private void OnUserVolumeModeMenuClick(object sender, RoutedEventArgs e)
    {
        ApplySidebarVolumeMode(isMaster: false, persist: true);
    }

    private void OnMasterVolumeModeMenuClick(object sender, RoutedEventArgs e)
    {
        ApplySidebarVolumeMode(isMaster: true, persist: true);
    }

    private void UpdateBypassButton()
    {
        UpdateShortcutIconStates();
    }

    private void UpdateMeters()
    {
        var status = ViewModel.Status;

        // Update inline per-channel meters
        foreach (var (channelId, meter) in _channelMeters)
        {
            if (channelId < status.Peaks.Length)
                meter.Level = status.Peaks[channelId];
            meter.IsClipping = status.IsClipping((ChannelId)channelId);
            var channel = LookupChannelById(channelId);
            meter.IsMuted = channel.IsOutput && ViewModel.GetChannelMute(channel);
        }

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

        // Alt-click renames, as Option-click does on the macOS Console, and
        // leaves the selection alone.
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            && _sidebarRenamers.TryGetValue((int)channel.Id, out var rename))
        {
            e.Handled = true;
            // The list selected the row on the press; put the highlight back on
            // the page that is actually showing.
            UpdateChannelListSelection();
            rename();
            return;
        }

        // A linked pair shares one editor page, so clicking either member of
        // the currently shown pair counts as clicking the shown channel.
        bool isShownPairPartner = _selectedChannel != null
            && !_selectedChannel.IsOutput && !channel.IsOutput
            && ViewModel.IsInputPairLinked((int)_selectedChannel.Id)
            && ChannelMap.LinkedPartnerId((int)_selectedChannel.Id) == (int)channel.Id;

        if (_selectedChannelIndex == index || isShownPairPartner)
        {
            // Same channel (or its linked partner) clicked - go back to dashboard
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

    /// <summary>
    /// When hovering a linked input channel item, force its pair partner's
    /// item into PointerOver visual state so both highlight together.
    /// </summary>
    private void OnMasterItemPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var other = GetPairedInputItem(sender);
        if (other != null)
            VisualStateManager.GoToState(other, "PointerOver", true);
    }

    private void OnMasterItemPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var other = GetPairedInputItem(sender);
        if (other == null) return;

        // If the other item is selected, restore to Selected state, not Normal
        bool isSelected = InputChannelsList.SelectedItems.Contains(other);
        VisualStateManager.GoToState(other, isSelected ? "Selected" : "Normal", true);
    }

    /// <summary>
    /// Finds the pair partner's ListViewItem in the InputChannelsList, or null
    /// when the sender's pair isn't linked.
    /// </summary>
    private ListViewItem? GetPairedInputItem(object sender)
    {
        if (sender is not ListViewItem item || item.Tag is not (Channel ch, int _)) return null;
        if (!ViewModel.IsInputPairLinked((int)ch.Id)) return null;
        int targetId = ChannelMap.LinkedPartnerId((int)ch.Id);
        foreach (var child in InputChannelsList.Items)
        {
            if (child is ListViewItem other && other.Tag is (Channel otherCh, int _) && (int)otherCh.Id == targetId)
                return other;
        }
        return null;
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

            // When a linked input is selected, highlight both channels of its pair
            if (item.Tag is (Channel ch, int _) && !ch.IsOutput &&
                ViewModel.IsInputPairLinked((int)ch.Id))
            {
                int partnerId = ChannelMap.LinkedPartnerId((int)ch.Id);
                InputChannelsList.SelectionMode = ListViewSelectionMode.Multiple;
                InputChannelsList.SelectedItems.Clear();
                foreach (var inputItem in InputChannelsList.Items)
                    if (inputItem is ListViewItem lvi && lvi.Tag is (Channel inCh, int _) &&
                        ((int)inCh.Id == (int)ch.Id || (int)inCh.Id == partnerId))
                        InputChannelsList.SelectedItems.Add(inputItem);
            }
            else
            {
                InputChannelsList.SelectionMode = ListViewSelectionMode.Single;
                if (InputChannelsList.Items.Contains(item))
                    InputChannelsList.SelectedItem = item;
                else if (OutputChannelsList.Items.Contains(item))
                    OutputChannelsList.SelectedItem = item;
            }
        }
        else
        {
            // Nothing selected — ensure single mode
            InputChannelsList.SelectionMode = ListViewSelectionMode.Single;
        }
    }

    private void OnMasterVolumeSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingMasterVolumeSlider) return;
        if (_sidebarVolumeIsMaster)
        {
            float db = (float)MasterVolumeSliderPosToDb(e.NewValue);
            if (Math.Abs(ViewModel.MasterVolumeDb - db) > 0.05f)
                ViewModel.MasterVolumeDb = db;
            MasterVolumeValueText.Text = db <= MasterVolumeMuteDb + 0.5
                ? "-inf dB" : $"{db:F1} dB";
        }
        else
        {
            // User volume drives the firmware's audio_state.volume directly
            // via REQ_SET_USER_VOLUME (0xDA). On USB the UAC1 host mirror
            // also writes that field from the system-tray slider; the two
            // writers coexist (last write wins) and firmware notifications
            // keep this slider in sync with external changes.
            // UserVolumeSliderPosToDb already snaps to UserVolumeQuantumDb,
            // so no extra rounding here.
            float db = (float)UserVolumeSliderPosToDb(e.NewValue);
            if (Math.Abs(ViewModel.UserVolumeDb - db) > 0.05f)
                ViewModel.UserVolumeDb = db;
            MasterVolumeValueText.Text = $"{db:F0} dB";
        }
    }

    private void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ReconnectCommand.Execute(null);
    }

    private void OnConnectionStatusRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ViewModel.ReconnectCommand.Execute(null);
    }

    private void OnClearAllMasterClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearAllMasterCommand.Execute(null);
    }

    /// <summary>Applies a typed delay when the field loses focus (Enter moves
    /// focus away), not per keystroke: typing "12" must not send 1 ms first.
    /// Unreadable text reverts to the current value.</summary>
    private void OnDelayTextCommitted(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingDelay) return;
        if (sender is not TextBox textBox || textBox.Tag is not Channel channel) return;

        _isUpdatingDelay = true;
        if (float.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            ViewModel.SetDelay((int)channel.Id, value);
            _currentDelayDrag?.Show(ViewModel.GetChannelDelay(channel));
        }
        textBox.Text = ViewModel.GetChannelDelay(channel).ToString("0.00##", CultureInfo.InvariantCulture);
        _isUpdatingDelay = false;
    }

    private string FormatDelayCm(float ms)
    {
        if (ms == 0f) return "0";
        uint sr = ViewModel.SampleRateHz;
        if (sr == 0) sr = 48000;
        float samples = MathF.Round(ms / 1000f * sr);
        float cm = samples / sr * 34300f;
        return string.Format(CultureInfo.InvariantCulture, "{0:0.#}", cm);
    }

    /// <summary>Applies a typed gain when the field loses focus (Enter moves
    /// focus away), not per keystroke. Unreadable text reverts.</summary>
    private void OnGainTextCommitted(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingGain) return;
        if (sender is not TextBox textBox || textBox.Tag is not Channel channel) return;

        _isUpdatingGain = true;
        if (float.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            ViewModel.SetChannelGain((int)channel.Id, Math.Clamp(value, -60, 10));
            _currentGainDrag?.Show(ViewModel.GetChannelGain(channel));
        }
        textBox.Text = ViewModel.GetChannelGain(channel).ToString("0.00", CultureInfo.InvariantCulture);
        _isUpdatingGain = false;
    }

    private void SyncGainFromViewModel(int outputIndex)
    {
        var outputs = ViewModel.ActiveOutputs;
        if (outputIndex < 0 || outputIndex >= outputs.Count) return;
        var channel = outputs[outputIndex];

        RefreshDashboardHeaderStats(channel);

        if (_selectedChannel != null && _selectedChannel.Id == channel.Id)
        {
            float gain = ViewModel.GetChannelGain(_selectedChannel);
            _isUpdatingGain = true;
            _currentGainDrag?.Show(gain);
            if (_currentGainTextBox != null && _currentGainTextBox.FocusState == FocusState.Unfocused)
                _currentGainTextBox.Text = gain.ToString("0.00", CultureInfo.InvariantCulture);
            _isUpdatingGain = false;
        }
    }

    private void SyncDelayFromViewModel(int outputIndex)
    {
        var outputs = ViewModel.ActiveOutputs;
        if (outputIndex < 0 || outputIndex >= outputs.Count) return;
        var channel = outputs[outputIndex];

        RefreshDashboardHeaderStats(channel);

        if (_selectedChannel != null && _selectedChannel.Id == channel.Id)
        {
            float delay = ViewModel.GetChannelDelay(_selectedChannel);
            _isUpdatingDelay = true;
            _currentDelayDrag?.Show(delay);
            if (_currentDelayTextBox != null && _currentDelayTextBox.FocusState == FocusState.Unfocused)
                _currentDelayTextBox.Text = delay.ToString("0.00##", CultureInfo.InvariantCulture);
            _isUpdatingDelay = false;
        }
    }

    private void SyncRouteIndicator(int input, int output)
    {
        if (output != _currentOutputIndex) return;
        if (!_currentRouteCircles.ContainsKey(input)) return;

        var inputCh = Channel.Inputs[input];
        bool routed = ViewModel.GetMatrixRouting(input, output);
        float gain = ViewModel.GetMatrixGain(input, output);
        bool inverted = ViewModel.GetMatrixInvert(input, output);
        var dimGray = Color.FromArgb(90, 160, 160, 170);

        var circle = _currentRouteCircles[input];
        circle.Background = routed ? new SolidColorBrush(inputCh.Color) : new SolidColorBrush(Colors.Transparent);
        circle.BorderThickness = routed ? new Thickness(0) : new Thickness(2);

        var nameText = _currentRouteNameTexts[input];
        nameText.Foreground = new SolidColorBrush(routed ? inputCh.Color : dimGray);

        var gainText = _currentRouteGainTexts[input];
        gainText.Foreground = GetRouteGainBrush(routed);
        string outputName = _selectedChannel != null ? ViewModel.GetChannelName(_selectedChannel).Trim() : $"Out {output + 1}";
        ToolTipService.SetToolTip(circle, routed ? $"Disconnect from {outputName}" : $"Connect to {outputName}");
        ToolTipService.SetToolTip(nameText, routed ? $"Disconnect from {outputName}" : $"Connect to {outputName}");
        if (gainText.FocusState == FocusState.Unfocused)
            gainText.Text = gain == 0f ? "0.00 dB" : string.Format(CultureInfo.InvariantCulture, "{0:+0.00;-0.00} dB", gain);

        var invText = _currentRouteInvTexts[input];
        invText.IsHitTestVisible = routed;
        invText.Foreground = GetRouteInvBrush(routed, inverted);
    }

    private static Brush GetRouteGainBrush(bool routed) =>
        new SolidColorBrush(routed ? Color.FromArgb(140, 255, 255, 255) : Color.FromArgb(45, 255, 255, 255));

    private static Brush GetRouteInvBrush(bool routed, bool inverted)
    {
        if (!routed) return new SolidColorBrush(Color.FromArgb(30, 200, 200, 220));
        // An active INV is orange, as on the macOS Console.
        return new SolidColorBrush(inverted ? Color.FromArgb(255, 255, 159, 10) : Color.FromArgb(60, 200, 200, 220));
    }

    private void RefreshDashboardHeaderStats(Channel channel)
    {
        if (!_dashboardHeaderStats.TryGetValue((int)channel.Id, out var tb)) return;
        float gain = ViewModel.GetChannelGain(channel);
        float delay = ViewModel.GetChannelDelay(channel);
        bool muted = ViewModel.GetChannelMute(channel);
        tb.Text = $"{gain:F1}dB  {delay:F0}ms{(muted ? "  MUTED" : "")}";
        tb.Foreground = new SolidColorBrush(muted ? Color.FromArgb(255, 200, 80, 80) : Colors.Gray);
    }

    private void OnMuteToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton btn && btn.Tag is Channel channel)
        {
            bool muted = btn.IsChecked == true;
            ApplyMuteButtonState(btn, muted);
            // Raises MatrixOutputMuteChanged, which comes back through
            // SyncMuteFromViewModel and refreshes the dashboard header line.
            ViewModel.SetChannelMute((int)channel.Id, muted);
        }
    }

    /// <summary>Put a mute toggle into a given state: checked, plus the speaker or
    /// muted-speaker glyph and its dimmed colour. Setting IsChecked here cannot
    /// re-enter <see cref="OnMuteToggleClick"/>, which is a Click handler.</summary>
    private static void ApplyMuteButtonState(ToggleButton btn, bool muted)
    {
        btn.IsChecked = muted;
        if (btn.Content is FontIcon icon)
        {
            icon.Glyph = muted ? "\uE74F" : "\uE767";
            // Red when muted, as on the macOS Console.
            icon.Foreground = muted
                ? new SolidColorBrush(Color.FromArgb(255, 255, 69, 58))
                : new SolidColorBrush(Color.FromArgb(200, 200, 200, 200));
        }
    }

    /// <summary>Reflect an output's mute state into this window: the output card's
    /// toggle (when that channel is the one on screen) and the dashboard header
    /// line. The inline meters read it on their own poll tick.</summary>
    private void SyncMuteFromViewModel(int outputIndex)
    {
        var outputs = ViewModel.ActiveOutputs;
        if (outputIndex < 0 || outputIndex >= outputs.Count) return;
        var channel = outputs[outputIndex];

        RefreshDashboardHeaderStats(channel);

        if (_selectedChannel != null && _selectedChannel.Id == channel.Id && _currentMuteButton != null)
            ApplyMuteButtonState(_currentMuteButton, ViewModel.GetChannelMute(channel));
    }

    /// <summary>The face the type button shows, as on the macOS Console.</summary>
    private static string FilterTypeFace(FilterType t) => t switch
    {
        FilterType.Peaking => "Peaking",
        FilterType.LowShelf => "Low Shelf (12dB)",
        FilterType.LowShelf1 => "Low Shelf (6dB)",
        FilterType.HighShelf => "High Shelf (12dB)",
        FilterType.HighShelf1 => "High Shelf (6dB)",
        FilterType.LowPass => "High Cut (12dB)",
        FilterType.LowPass1 => "High Cut (6dB)",
        FilterType.HighPass => "Low Cut (12dB)",
        FilterType.HighPass1 => "Low Cut (6dB)",
        FilterType.Notch => "Notch",
        FilterType.AllPass => "All Pass (360\u00b0)",
        FilterType.AllPass1 => "All Pass (180\u00b0)",
        FilterType.LinkwitzTransform => "Linkwitz Transform",
        _ => "Off",
    };

    /// <summary>Wide enough for the longest face, "Linkwitz Transform", and the chevron.</summary>
    private const double FilterTypeWidth = 150;

    private DropDownButton BuildFilterTypeButton(Channel channel, int bandIndex, FilterParams p)
    {
        bool firstOrderCuts = ViewModel.FirstOrderPassSupported;
        var groups = new List<(string Title, (string Label, FilterType Type)[] Variants)>
        {
            ("Off", new[] { ("Off", FilterType.Flat) }),
            ("Peaking", new[] { ("Peaking", FilterType.Peaking) }),
            ("Low Shelf", new[] { ("6 dB/oct", FilterType.LowShelf1), ("12 dB/oct", FilterType.LowShelf) }),
            ("High Shelf", new[] { ("6 dB/oct", FilterType.HighShelf1), ("12 dB/oct", FilterType.HighShelf) }),
            // 6 dB/oct cuts (V28+): older firmware would misread the type byte,
            // though the band's own type always stays listed so it can display.
            ("High Cut", new[] { ("6 dB/oct", FilterType.LowPass1), ("12 dB/oct", FilterType.LowPass) }
                .Where(v => firstOrderCuts || v.Item2 != FilterType.LowPass1 || p.Type == FilterType.LowPass1).ToArray()),
            ("Low Cut", new[] { ("6 dB/oct", FilterType.HighPass1), ("12 dB/oct", FilterType.HighPass) }
                .Where(v => firstOrderCuts || v.Item2 != FilterType.HighPass1 || p.Type == FilterType.HighPass1).ToArray()),
            ("Notch", new[] { ("Notch", FilterType.Notch) }),
            ("All Pass", new[] { ("180\u00b0", FilterType.AllPass1), ("360\u00b0", FilterType.AllPass) }),
        };
        // Linkwitz Transform (V22+): outputs only, a sealed-box bass tool.
        if (channel.IsOutput && ViewModel.LinkwitzTransformSupported)
            groups.Add(("Linkwitz Transform", new[] { ("Linkwitz Transform", FilterType.LinkwitzTransform) }));

        var current = p.Type.IsCrossover() ? FilterType.Flat : p.Type;
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        ToggleMenuFlyoutItem Item(string text, FilterType type)
        {
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = type == current };
            item.Click += (_, _) =>
            {
                if (type == current) { item.IsChecked = true; return; }
                ApplyFilterType(channel, bandIndex, type);
            };
            return item;
        }
        foreach (var (title, variants) in groups)
        {
            // A family with one variant left is a plain item under its own name.
            if (variants.Length == 1) { flyout.Items.Add(Item(title, variants[0].Type)); continue; }
            var sub = new MenuFlyoutSubItem { Text = title };
            if (variants.Any(v => v.Type == current))
                sub.Icon = new FontIcon { Glyph = "\uE73E" };
            foreach (var (label, type) in variants) sub.Items.Add(Item(label, type));
            flyout.Items.Add(sub);
        }
        return new DropDownButton
        {
            Content = FilterTypeFace(current),
            Width = FilterTypeWidth,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Flyout = flyout,
        };
    }

    private void ApplyFilterType(Channel channel, int bandIndex, FilterType newType)
    {
        {
            {
                var filters = ViewModel.GetFilters(channel);
                if (bandIndex < filters.Count)
                {
                    var p = filters[bandIndex].Clone();
                    bool wasLt = p.Type.IsLinkwitzTransform();
                    bool isLt = newType.IsLinkwitzTransform();
                    p.Type = newType;

                    // LT overloads the wire fields — Gain carries fp in *Hz*, Q
                    // carries the driver Q0 — so the values are not transferable
                    // in either direction. Carrying an LT band's Gain into a
                    // peaking band would turn a 30 Hz target into +30 dB of
                    // boost; carrying a peaking band's Gain into LT would set a
                    // negative or zero target frequency. Crossing the boundary
                    // discards them.
                    if (wasLt && !isLt)
                    {
                        // Back to a no-op band. f0 is a genuine frequency, so it
                        // survives as the new band's centre.
                        p.Gain = 0f;
                        p.Q = FilterParams.DefaultQ;
                        p.Qp = FilterParams.DefaultQp;
                    }
                    else if (isLt && !wasLt)
                    {
                        // Seed an identity transform (fp == f0, Qp == Q0): audibly
                        // nothing until the user applies real driver/target values
                        // in the popover.
                        p.Q = FilterParams.DefaultQ;
                        p.Qp = FilterParams.DefaultQ;
                        p.Gain = p.Frequency;
                    }

                    _ = ViewModel.SetFilter((int)channel.Id, bandIndex, p);

                    // Refresh the row (freq/Q/gain field visibility follows the type)
                    if (_selectedChannel != null)
                    {
                        ShowChannelEditor(_selectedChannel);
                    }
                }
            }
        }
    }

    private void OnFilterBypassToggled(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is (Channel channel, int bandIndex))
        {
            var filters = ViewModel.GetFilters(channel);
            if (bandIndex < filters.Count)
            {
                bool newBypass = !filters[bandIndex].Bypass;
                _ = ViewModel.SetBandBypass((int)channel.Id, bandIndex, newBypass);

                // No synchronous ShowChannelEditor here. SetBandBypass updates
                // the local _channelData cache before awaiting the USB transfer,
                // then fires FiltersChanged when the transfer completes — that
                // handler already rebuilds the editor (with the dot filled/
                // emptied and labels dimmed) on the next dispatcher tick.
                // Calling ShowChannelEditor here too produced two back-to-back
                // full editor rebuilds (~60-100ms each, including a
                // BodePlot.Redraw(gridChanged: true) that clears and rebuilds
                // every channel's polyline) — a ~150ms UI freeze per click,
                // which is what was reading as "blocking".
            }
        }
    }

    // Inline filter-field writes in flight. While non-zero, FiltersChanged
    // updates the band rows in place instead of rebuilding the editor, which
    // would destroy the box the user just tabbed or clicked into.
    private int _inlineFilterCommits;

    private async void OnFilterValueChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not (Channel channel, int bandIndex, string param)) return;
        if (!float.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) return;
        var filters = ViewModel.GetFilters(channel);
        if (bandIndex >= filters.Count) return;
        var p = filters[bandIndex].Clone();

        // Same precision the row displays (CreateValueField / FlushLiveRows).
        // LT's fp/Qp are not edited inline — they live in the Apply/Cancel
        // popover (BuildLinkwitzEditorButton).
        (float current, int decimals) = param switch
        {
            "freq" => (p.Frequency, 2),
            "q" => (p.Q, 3),
            "gain" => (p.Gain, 2),
            _ => (float.NaN, 0),
        };
        if (float.IsNaN(current)) return;

        // Unchanged (focus left without an edit, or Enter's second call when it
        // moves focus): no write. This also keeps a value set on the graph
        // beyond this field's range from being clamped just by visiting it.
        if (float.TryParse(FormatFilterValue(current, decimals), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var shown) && shown == value)
            return;

        float applied;
        switch (param)
        {
            case "freq": applied = p.Frequency = Math.Clamp(value, 20, 20000); break;
            case "q": applied = p.Q = Math.Clamp(value, 0.1f, 20); break;
            default: applied = p.Gain = Math.Clamp(value, -20, 20); break;
        }
        textBox.Text = FormatFilterValue(applied, decimals);

        _inlineFilterCommits++;
        try { await ViewModel.SetFilter((int)channel.Id, bandIndex, p); }
        catch { }
        finally { _inlineFilterCommits--; }
    }

    /// <summary>Re-reads every band row's values from the model without
    /// rebuilding the rows. The focused box is left alone so typing into it
    /// isn't overwritten.</summary>
    private void RefreshPeqRowValuesInPlace()
    {
        if (_selectedChannel == null || _filterPageIsXover) return;
        var filters = ViewModel.GetFilters(_selectedChannel);
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        foreach (var (band, refs) in _peqRows)
        {
            if (band >= filters.Count) continue;
            var p = filters[band];
            Set(refs.Freq, FormatFilterValue(p.Frequency, 2));
            Set(refs.Q, FormatFilterValue(p.Q, 3));
            Set(refs.Gain, FormatFilterValue(p.Gain, 2));
        }

        void Set(TextBox? box, string text)
        {
            if (box != null && !ReferenceEquals(box, focused) && box.Text != text) box.Text = text;
        }
    }

    #endregion

    #region Menu Handlers

    #region Preset Handlers

    private void RefreshPresetComboBox()
    {
        _isUpdatingPresetCombo = true;
        try
        {
            if (!ViewModel.PresetsSupported)
            {
                PresetSection.Visibility = Visibility.Collapsed;
                PresetComboBox.Items.Clear();
                return;
            }

            PresetSection.Visibility = ViewModel.IsDeviceConnected ? Visibility.Visible : Visibility.Collapsed;

            // Only clear+rebuild items if their content has actually changed.
            // Tearing down and re-adding items while the ComboBox is in a focus
            // or flyout transition can throw inside Microsoft.ui.xaml.
            bool itemsMatch = PresetComboBox.Items.Count == MainViewModel.PresetSlotCount;
            if (itemsMatch)
            {
                for (int i = 0; i < MainViewModel.PresetSlotCount; i++)
                {
                    if (PresetComboBox.Items[i] is ComboBoxItem cbi &&
                        cbi.Content is string s &&
                        s == ViewModel.GetPresetDisplayName(i))
                        continue;
                    itemsMatch = false;
                    break;
                }
            }

            if (!itemsMatch)
            {
                PresetComboBox.Items.Clear();
                for (int i = 0; i < MainViewModel.PresetSlotCount; i++)
                {
                    PresetComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = ViewModel.GetPresetDisplayName(i),
                        Tag = i
                    });
                }
            }

            UpdateActivePresetSelection();
            UpdatePresetDirtyIndicator();
        }
        finally
        {
            _isUpdatingPresetCombo = false;
        }
    }

    private void UpdateActivePresetSelection()
    {
        _isUpdatingPresetCombo = true;
        try
        {
            int target = ViewModel.ActivePreset >= 0 && ViewModel.ActivePreset < PresetComboBox.Items.Count
                ? ViewModel.ActivePreset
                : -1;
            if (PresetComboBox.SelectedIndex != target)
                PresetComboBox.SelectedIndex = target;
        }
        finally
        {
            _isUpdatingPresetCombo = false;
        }
    }

    private void UpdateWindowTitle()
    {
        var title = ViewModel.PresetsDirty
            ? "DSPi Console — Unsaved Changes"
            : "DSPi Console";
        if (AppTitleText != null)
            AppTitleText.Text = title;
        var appWindow = GetAppWindow();
        if (appWindow != null)
            appWindow.Title = title;
    }

    // Positions an overlay "*" immediately after the active preset's name inside
    // the ComboBox, without letting it affect the ComboBox's measured width.
    private void UpdatePresetDirtyIndicator()
    {
        try
        {
            if (!ViewModel.PresetsDirty || ViewModel.ActivePreset < 0 ||
                ViewModel.ActivePreset >= MainViewModel.PresetSlotCount ||
                !ViewModel.PresetsSupported)
            {
                PresetDirtyIndicator.Visibility = Visibility.Collapsed;
                PresetSaveButton.Visibility = Visibility.Collapsed;
                return;
            }

            PresetSaveButton.Visibility = AppSettings.Instance.ShowPresetSaveButton
                ? Visibility.Visible
                : Visibility.Collapsed;

            var name = ViewModel.GetPresetDisplayName(ViewModel.ActivePreset);
            var measure = new TextBlock
            {
                Text = name,
                FontSize = PresetComboBox.FontSize,
                FontFamily = PresetComboBox.FontFamily,
                FontWeight = PresetComboBox.FontWeight,
                FontStyle = PresetComboBox.FontStyle
            };
            measure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

            var leftOffset = PresetComboBox.Padding.Left + measure.DesiredSize.Width + 2;
            PresetDirtyIndicator.Margin = new Thickness(leftOffset, 0, 0, 0);
            PresetDirtyIndicator.Visibility = Visibility.Visible;
        }
        catch
        {
            PresetDirtyIndicator.Visibility = Visibility.Collapsed;
            PresetSaveButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPresetComboPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        PresetComboBox.BorderBrush = (Brush)Application.Current.Resources["ComboBoxBorderBrush"];
        PresetComboBox.Background = (Brush)Application.Current.Resources["ComboBoxBackground"];
    }

    private void OnPresetComboPointerExited(object sender, PointerRoutedEventArgs e)
    {
        PresetComboBox.BorderBrush = new SolidColorBrush(Colors.Transparent);
        PresetComboBox.Background = new SolidColorBrush(Colors.Transparent);
    }

    // ── Input Source selector (V7+ firmware) ──

    private bool _isUpdatingSourceCombo;

    private void RefreshSourceComboBox()
    {
        _isUpdatingSourceCombo = true;
        try
        {
            bool show = ViewModel.IsDeviceConnected && ViewModel.InputSourceSupported;
            SourceSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            // I2S is only a valid choice on firmware that bundles I2S input
            // (V12+). Keep the item visible but disabled on older firmware.
            SourceI2sItem.IsEnabled = ViewModel.InputI2sSupported;

            // ADAT input (V24+, RP2350): listed only once it's enabled with a
            // valid RX pin — a disabled ADAT input is hidden entirely.
            SourceAdatItem.Visibility = ViewModel.AdatInputSelectable
                ? Visibility.Visible : Visibility.Collapsed;

            // Extra S/PDIF receivers (multi-instance firmware): show one item per
            // enabled instance and number the first item once siblings appear.
            int spdifCount = ViewModel.MultiSpdifSupported ? ViewModel.SpdifEnabledCount : 1;
            string spdifLabel = spdifCount > 1 ? "S/PDIF 1" : "S/PDIF";
            bool spdifRelabeled = !Equals(SourceSpdifItem.Content, spdifLabel);
            SourceSpdifItem.Content = spdifLabel;
            SourceSpdif2Item.Visibility = spdifCount >= 2 ? Visibility.Visible : Visibility.Collapsed;
            SourceSpdif3Item.Visibility = spdifCount >= 3 ? Visibility.Visible : Visibility.Collapsed;
            SourceSpdif4Item.Visibility = spdifCount >= 4 ? Visibility.Visible : Visibility.Collapsed;

            // The closed combo caches its selection-box content, so renaming the
            // selected item doesn't repaint until the dropdown opens. Bounce the
            // selection to force a refresh (guarded — no device write).
            if (spdifRelabeled && SourceComboBox.SelectedItem == SourceSpdifItem)
            {
                SourceComboBox.SelectedIndex = -1;
                SourceComboBox.SelectedItem = SourceSpdifItem;
            }

            // Items are ordered for display (S/PDIF group together), so map the
            // active source to its item by Tag, never by index. A source this
            // combo doesn't list (unknown future value) leaves the selection
            // unchanged — setting an out-of-range SelectedIndex would throw and
            // surface as a stowed-exception crash on connect.
            int target = SourceItemIndexFor((byte)ViewModel.ActiveInputSource);
            if (target < 0)
                return;
            if (SourceComboBox.SelectedIndex != target)
                SourceComboBox.SelectedIndex = target;
        }
        finally
        {
            _isUpdatingSourceCombo = false;
        }
    }

    /// <summary>Index of the Source combo item whose Tag is the given
    /// <see cref="DSPiConsole.Usb.InputSource"/> wire value, or -1.</summary>
    private int SourceItemIndexFor(byte source)
    {
        for (int i = 0; i < SourceComboBox.Items.Count; i++)
            if (SourceComboBox.Items[i] is ComboBoxItem item
                && byte.TryParse(item.Tag?.ToString(), out var v) && v == source)
                return i;
        return -1;
    }

    private async void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSourceCombo) return;
        if (!ViewModel.IsDeviceConnected || !ViewModel.InputSourceSupported) return;
        if (SourceComboBox.SelectedItem is not ComboBoxItem item) return;

        // Tag is the InputSource value ("0".."6") from XAML — parse to InputSource.
        if (!byte.TryParse(item.Tag?.ToString(), out var raw)) return;
        var target = (DSPiConsole.Usb.InputSource)raw;
        if (target == ViewModel.ActiveInputSource) return;

        await ViewModel.SetInputSourceAsync(target);
    }

    private void OnSourceComboPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SourceComboBox.BorderBrush = (Brush)Application.Current.Resources["ComboBoxBorderBrush"];
        SourceComboBox.Background = (Brush)Application.Current.Resources["ComboBoxBackground"];
    }

    private void OnSourceComboPointerExited(object sender, PointerRoutedEventArgs e)
    {
        SourceComboBox.BorderBrush = new SolidColorBrush(Colors.Transparent);
        SourceComboBox.Background = new SolidColorBrush(Colors.Transparent);
    }

    private bool _presetSwitchInProgress;

    private async void OnPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingPresetCombo) return;
        if (PresetComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not int slot) return;

        if (!ViewModel.IsDeviceConnected) return;

        // Prevent re-entry: if a previous switch is mid-flight (dialog open or
        // LoadPreset awaiting), ignore new selection changes. Without this, a
        // second click can race the first and crash during the refresh.
        if (_presetSwitchInProgress) { RevertPresetCombo(); return; }
        _presetSwitchInProgress = true;
        try
        {
            await PresetSwitchAsync(slot);
        }
        finally
        {
            _presetSwitchInProgress = false;
        }
    }

    private async Task PresetSwitchAsync(int slot)
    {

        // If dirty, ask about unsaved changes
        if (ViewModel.PresetsDirty && ViewModel.ActivePreset >= 0)
        {
            var summary = ViewModel.GetChangeSummary();
            var message = summary != null
                ? $"You have unsaved changes to the current preset:\n\n{summary}"
                : "You have unsaved changes to the current preset.";

            var dialog = new ContentDialog
            {
                Title = "Unsaved Changes",
                Content = message,
                PrimaryButtonText = "Save & Switch",
                SecondaryButtonText = "Discard & Switch",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                // Save current preset first; prompt for a name if it's empty.
                string? name = null;
                if (!ViewModel.IsPresetOccupied(ViewModel.ActivePreset))
                {
                    name = await PromptForPresetNameAsync(ViewModel.ActivePreset);
                    if (name == null) { RevertPresetCombo(); return; }
                }
                var saveResult = await ViewModel.SavePreset(ViewModel.ActivePreset, name);
                if (saveResult != Usb.PresetResult.Ok)
                {
                    await ShowErrorDialog("Failed to save current preset");
                    RevertPresetCombo();
                    return;
                }
            }
            else if (result == ContentDialogResult.None)
            {
                // Cancel — revert combo
                RevertPresetCombo();
                return;
            }
        }

        // Load the selected preset
        var loadResult = await ViewModel.LoadPreset(slot);
        if (loadResult != Usb.PresetResult.Ok)
        {
            await ShowErrorDialog("Failed to load preset");
            RevertPresetCombo();
        }
    }

    private void RevertPresetCombo()
    {
        _isUpdatingPresetCombo = true;
        if (ViewModel.ActivePreset >= 0 && ViewModel.ActivePreset < PresetComboBox.Items.Count)
            PresetComboBox.SelectedIndex = ViewModel.ActivePreset;
        else
            PresetComboBox.SelectedIndex = -1;
        _isUpdatingPresetCombo = false;
    }

    private async Task CopyPresetToSlot(int slot)
    {
        if (!ViewModel.IsDeviceConnected)
        {
            await ShowErrorDialog("Not connected to device");
            return;
        }

        string? name;
        if (ViewModel.IsPresetOccupied(slot))
        {
            var confirm = new ContentDialog
            {
                Title = "Overwrite Preset",
                Content = $"Overwrite \"{ViewModel.GetPresetName(slot)}\" with the current configuration?",
                PrimaryButtonText = "Overwrite",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            name = null; // keep existing name
        }
        else
        {
            name = await PromptForPresetNameAsync(slot);
            if (name == null) return;
        }

        var result = await ViewModel.CopyToPreset(slot, name);
        if (result != Usb.PresetResult.Ok)
            await ShowErrorDialog("Failed to copy preset");
    }

    /// <summary>
    /// "Save to..." — persist the current (modified) live state into the chosen
    /// slot and switch the active preset to it, leaving the original preset
    /// untouched on disk. Prompts for a name and, when the target is occupied,
    /// confirms the overwrite (both in one dialog). Offered only while dirty.
    /// </summary>
    private async Task SavePresetToSlot(int slot)
    {
        if (!ViewModel.IsDeviceConnected)
        {
            await ShowErrorDialog("Not connected to device");
            return;
        }

        bool occupied = ViewModel.IsPresetOccupied(slot);

        // Default the name to the current preset's name (the state being saved)
        // so the user can keep or tweak it; fall back to a slot label.
        string defaultName = ViewModel.ActivePreset >= 0
            ? ViewModel.GetPresetName(ViewModel.ActivePreset)
            : $"Preset {slot + 1}";

        var nameBox = new TextBox { Text = defaultName, MaxLength = 31 };
        nameBox.SelectAll();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(nameBox);
        if (occupied)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"This will overwrite “{ViewModel.GetPresetName(slot)}”.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }

        var dialog = new ContentDialog
        {
            Title = occupied
                ? $"Save to {ViewModel.GetPresetName(slot)}"
                : $"Save to Slot {slot + 1}",
            Content = panel,
            PrimaryButtonText = occupied ? "Overwrite" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) name = $"Preset {slot + 1}";

        // SavePreset writes the live state to the slot, switches the active
        // preset to it, and clears the dirty flag (Save-As semantics).
        var result = await ViewModel.SavePreset(slot, name);
        if (result != Usb.PresetResult.Ok)
            await ShowErrorDialog("Failed to save preset");
    }

    private void OnPresetComboRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var flyout = new MenuFlyout();

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Save",
            Icon = new FontIcon { Glyph = "\uE74E" }
        });
        ((MenuFlyoutItem)flyout.Items[0]).Click += async (s, _) => await QuickSavePreset();

        // Dirty → "Save to..." (save the modified live state into another slot
        // and switch to it, leaving the original untouched). Clean → "Copy to..."
        // (duplicate the saved preset to another slot, active unchanged). One
        // submenu; the label and per-slot action depend on the dirty state,
        // captured once here so the click matches the label the user saw.
        bool presetDirty = ViewModel.PresetsDirty;
        var copyToSub = new MenuFlyoutSubItem
        {
            Text = presetDirty ? "Save to..." : "Copy to...",
            Icon = new FontIcon { Glyph = "" },
        };
        for (int i = 0; i < MainViewModel.PresetSlotCount; i++)
        {
            if (i == ViewModel.ActivePreset) continue;
            int slot = i;
            // Save-to lists slots distinguishably (so an unused target is easy
            // to pick); copy-to keeps the existing name-or-"Empty" labels.
            string label = !presetDirty
                ? ViewModel.GetPresetDisplayName(slot)
                : ViewModel.IsPresetOccupied(slot)
                    ? $"Slot {slot + 1}: {ViewModel.GetPresetName(slot)}"
                    : $"Slot {slot + 1} (empty)";
            var item = new MenuFlyoutItem { Text = label };
            item.Click += async (s, _) =>
            {
                if (presetDirty) await SavePresetToSlot(slot);
                else await CopyPresetToSlot(slot);
            };
            copyToSub.Items.Add(item);
        }
        flyout.Items.Add(copyToSub);

        if (ViewModel.ActivePreset >= 0 && ViewModel.IsPresetOccupied(ViewModel.ActivePreset))
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Reload",
                Icon = new FontIcon { Glyph = "\uE72C" }
            });
            ((MenuFlyoutItem)flyout.Items[^1]).Click += async (s, _) =>
            {
                var result = await ViewModel.LoadPreset(ViewModel.ActivePreset);
                if (result != Usb.PresetResult.Ok)
                    await ShowErrorDialog("Failed to reload preset");
            };

            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Rename...",
                Icon = new FontIcon { Glyph = "\uE8AC" }
            });
            ((MenuFlyoutItem)flyout.Items[^1]).Click += async (s, _) => await ShowRenamePresetDialog(ViewModel.ActivePreset);

            bool isAlreadyDefault = ViewModel.PresetStartupMode == 0 && ViewModel.PresetDefaultSlot == ViewModel.ActivePreset;
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Set as Default",
                Icon = new FontIcon { Glyph = "\uE735" },
                IsEnabled = !isAlreadyDefault
            });
            ((MenuFlyoutItem)flyout.Items[^1]).Click += async (s, _) =>
            {
                await ViewModel.SetPresetStartup(0, (byte)ViewModel.ActivePreset);
            };

            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Clear This Preset",
                Icon = new FontIcon { Glyph = "\uE74D" }
            });
            ((MenuFlyoutItem)flyout.Items[^1]).Click += async (s, _) =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Clear Preset",
                    Content = $"Delete \"{ViewModel.GetPresetName(ViewModel.ActivePreset)}\"?",
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    var result = await ViewModel.DeletePreset(ViewModel.ActivePreset);
                    if (result != Usb.PresetResult.Ok)
                        await ShowErrorDialog("Failed to delete preset");
                }
            };
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Clear All Presets",
            Icon = new FontIcon { Glyph = "\uE750" }
        });
        ((MenuFlyoutItem)flyout.Items[^1]).Click += async (s, _) =>
        {
            var dialog = new ContentDialog
            {
                Title = "Clear All Presets",
                Content = "Delete all presets from device flash? This cannot be undone.",
                PrimaryButtonText = "Delete All",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var result = await ViewModel.ClearAllPresets();
                if (result != Usb.PresetResult.Ok)
                    await ShowErrorDialog("Failed to clear presets");
            }
        };

        flyout.ShowAt(PresetComboBox, new FlyoutShowOptions
        {
            Position = e.GetPosition(PresetComboBox)
        });
    }

    private async Task QuickSavePreset()
    {
        if (!ViewModel.IsDeviceConnected) return;

        if (ViewModel.ActivePreset >= 0)
        {
            // Quick-save to active slot. Empty slots have no stored name yet,
            // so prompt the user to name it before writing.
            string? name = null;
            if (!ViewModel.IsPresetOccupied(ViewModel.ActivePreset))
            {
                name = await PromptForPresetNameAsync(ViewModel.ActivePreset);
                if (name == null) return; // user cancelled
            }
            var result = await ViewModel.SavePreset(ViewModel.ActivePreset, name);
            if (result != Usb.PresetResult.Ok)
                await ShowErrorDialog("Failed to save preset");
        }
        else
        {
            // No active preset — show slot picker
            await ShowSaveToSlotDialog();
        }
    }

    /// <summary>
    /// Prompt the user for a name when saving into an empty slot. Returns the
    /// chosen name (or default fallback) on Save, or null if the user cancelled.
    /// </summary>
    private async Task<string?> PromptForPresetNameAsync(int slot)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = $"Preset {slot + 1}",
            MaxLength = 31
        };

        var dialog = new ContentDialog
        {
            Title = $"Name Preset {slot + 1}",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return string.IsNullOrWhiteSpace(nameBox.Text) ? $"Preset {slot + 1}" : nameBox.Text.Trim();
    }

    private async Task SaveToPresetSlot(int slot)
    {
        // Prompt for name
        var nameBox = new TextBox
        {
            PlaceholderText = $"Preset {slot + 1}",
            MaxLength = 31,
            Text = ViewModel.IsPresetOccupied(slot) ? ViewModel.GetPresetName(slot) : ""
        };

        var dialog = new ContentDialog
        {
            Title = $"Save to Preset {slot + 1}",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var name = string.IsNullOrWhiteSpace(nameBox.Text) ? $"Preset {slot + 1}" : nameBox.Text.Trim();
            var result = await ViewModel.SavePreset(slot, name);
            if (result != Usb.PresetResult.Ok)
                await ShowErrorDialog("Failed to save preset");
        }
        else
        {
            RevertPresetCombo();
        }
    }

    private async Task ShowSaveToSlotDialog()
    {
        var panel = new StackPanel { Spacing = 12 };

        var slotCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int i = 0; i < MainViewModel.PresetSlotCount; i++)
        {
            slotCombo.Items.Add(new ComboBoxItem
            {
                Content = ViewModel.GetPresetDisplayName(i),
                Tag = i
            });
        }
        slotCombo.SelectedIndex = 0;

        var nameBox = new TextBox
        {
            PlaceholderText = "Preset name",
            MaxLength = 31
        };

        panel.Children.Add(new TextBlock { Text = "Save to slot:" });
        panel.Children.Add(slotCombo);
        panel.Children.Add(nameBox);

        var dialog = new ContentDialog
        {
            Title = "Save Preset",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (slotCombo.SelectedItem is ComboBoxItem item && item.Tag is int slot)
            {
                var name = string.IsNullOrWhiteSpace(nameBox.Text) ? $"Preset {slot + 1}" : nameBox.Text.Trim();
                var result = await ViewModel.SavePreset(slot, name);
                if (result != Usb.PresetResult.Ok)
                    await ShowErrorDialog("Failed to save preset");
            }
        }
    }

    private async Task ShowRenamePresetDialog(int slot)
    {
        var nameBox = new TextBox
        {
            Text = ViewModel.GetPresetName(slot),
            MaxLength = 31
        };
        nameBox.SelectAll();

        var dialog = new ContentDialog
        {
            Title = "Rename Preset",
            Content = nameBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var name = nameBox.Text.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                var ok = await ViewModel.RenamePreset(slot, name);
                if (!ok) await ShowErrorDialog("Failed to rename preset");
            }
        }
    }

    private void OnMainMenuOpening(object? sender, object e)
    {
        // "Save Master Volume" only applies when master volume is not stored
        // per-preset. In with-preset mode, regular Save Preset already does it.
        SaveMasterVolumeMenuItem.IsEnabled =
            ViewModel.IsDeviceConnected && ViewModel.MasterVolumeMode == 0;
        // Same shape for "Save Output Config" — only meaningful in independent
        // mode (otherwise IO travels with the preset and is captured by Save
        // Preset). See output_config_independent_load_spec.md.
        SaveOutputConfigMenuItem.IsEnabled =
            ViewModel.IsDeviceConnected && ViewModel.OutputConfigMode == 0;
        // A whole-device configuration is captured from, and pushed to, live
        // device state — neither direction means anything while disconnected.
        ImportPresetMenuItem.IsEnabled = ViewModel.IsDeviceConnected;
        ExportPresetMenuItem.IsEnabled = ViewModel.IsDeviceConnected;
        // While the wizard has the window, only Help, Settings and Exit act.
        ApplyWizardMenuState();
    }

    private async void OnSaveMasterVolumeClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDeviceConnected)
        {
            await ShowErrorDialog("Not connected to device");
            return;
        }
        var status = await ViewModel.SaveMasterVolume();
        if (status != 0)
            await ShowErrorDialog($"Failed to save master volume (status 0x{status:X2})");
    }

    private async void OnSaveOutputConfigClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDeviceConnected)
        {
            await ShowErrorDialog("Not connected to device");
            return;
        }
        var status = await ViewModel.SaveOutputConfig();
        if (status != 0)
            await ShowErrorDialog($"Failed to save output config (status 0x{status:X2})");
    }

    private async void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDeviceConnected)
        {
            await ShowErrorDialog("Not connected to device");
            return;
        }

        if (ViewModel.PresetsSupported)
        {
            await QuickSavePreset();
        }
        else
        {
            // Legacy: fall back to SaveParams
            var flashResult = await ViewModel.SaveParams();
            if (flashResult == Usb.FlashResult.Ok)
                await ShowSuccessDialog("Parameters saved successfully");
            else
                await ShowErrorDialog("Failed to save parameters");
        }
    }

    private async void OnRevertPresetClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDeviceConnected)
        {
            await ShowErrorDialog("Not connected to device");
            return;
        }

        if (ViewModel.PresetsSupported && ViewModel.ActivePreset >= 0)
        {
            var summary = ViewModel.GetChangeSummary();
            var message = summary != null
                ? $"Revert to saved \"{ViewModel.GetPresetName(ViewModel.ActivePreset)}\"?\n\nPending changes:\n{summary}"
                : $"Revert to saved \"{ViewModel.GetPresetName(ViewModel.ActivePreset)}\"?";

            var dialog = new ContentDialog
            {
                Title = "Revert Preset",
                Content = message,
                PrimaryButtonText = "Revert",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var result = await ViewModel.LoadPreset(ViewModel.ActivePreset);
                if (result != Usb.PresetResult.Ok)
                    await ShowErrorDialog("Failed to revert preset");
            }
        }
        else
        {
            // Pre-preset firmware (V2 and earlier) is no longer supported here:
            // the 0x52 opcode that used to mean REQ_LOAD_PARAMS has been
            // repurposed by current firmware to REQ_SAVE_OUTPUT_CONFIG. There
            // is no safe "revert" path against legacy firmware without that
            // opcode, so surface a clear message instead.
            await ShowInfoDialog("Revert is unavailable on this firmware. Update the firmware to use presets.");
        }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // The wizard's install is a write to the board like the update window's.
        if (_gettingStarted?.IsWriting == true)
        {
            args.Cancel = true;
            return;
        }
        // Quitting mid-write would leave the board half flashed, in bootloader mode.
        if (_firmwareUpdateWindow?.IsWriting == true)
        {
            args.Cancel = true;
            _firmwareUpdateWindow.Activate();
            return;
        }
        if (_closeConfirmed) return;

        if (!ViewModel.PresetsDirty || !ViewModel.IsDeviceConnected)
            return;

        args.Cancel = true;

        var summary = ViewModel.GetChangeSummary();
        var message = summary != null
            ? $"You have unsaved changes:\n\n{summary}"
            : "You have unsaved changes.";

        var dialog = new ContentDialog
        {
            Title = "Unsaved Changes",
            Content = message,
            PrimaryButtonText = ViewModel.PresetsSupported && ViewModel.ActivePreset >= 0 ? "Save & Quit" : "Quit",
            SecondaryButtonText = ViewModel.PresetsSupported && ViewModel.ActivePreset >= 0 ? "Discard & Quit" : null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && ViewModel.PresetsSupported && ViewModel.ActivePreset >= 0)
        {
            string? name = null;
            if (!ViewModel.IsPresetOccupied(ViewModel.ActivePreset))
            {
                name = await PromptForPresetNameAsync(ViewModel.ActivePreset);
                if (name == null) return; // user cancelled — abort close
            }
            var saveResult = await ViewModel.SavePreset(ViewModel.ActivePreset, name);
            if (saveResult != Usb.PresetResult.Ok)
            {
                await ShowErrorDialog("Failed to save preset. Close anyway?");
            }
            _closeConfirmed = true;
            Close();
        }
        else if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            _closeConfirmed = true;
            Close();
        }
    }

    #endregion

    private async void OnFactoryResetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Factory Reset",
            Content = "Do you wish to clear all active parameters?\n\nThis will not overwrite your saved presets.",
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

            var flashResult = await ViewModel.FactoryResetParams();
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

    private void OnLoudnessClick(object sender, RoutedEventArgs e)
    {
        if (_loudnessWindow == null)
        {
            _loudnessWindow = new LoudnessWindow(ViewModel);
            _loudnessWindow.Closed += (s, e) => _loudnessWindow = null;
        }
        _loudnessWindow.Activate();
    }

    private void OnCrossfeedClick(object sender, RoutedEventArgs e)
    {
        if (_crossfeedWindow == null)
        {
            _crossfeedWindow = new CrossfeedWindow(ViewModel);
            _crossfeedWindow.Closed += (s, e) => _crossfeedWindow = null;
        }
        _crossfeedWindow.Activate();
    }

    private async void OnPsybassClick(object sender, RoutedEventArgs e)
    {
        // Refresh from the device so a value changed elsewhere (e.g. a control
        // surface) is reflected; the window shows an unsupported notice if absent.
        if (ViewModel.IsDeviceConnected)
            await Task.Run(() => ViewModel.FetchPsybass());

        if (_psybassWindow == null)
        {
            _psybassWindow = new PsychoacousticBassWindow(ViewModel);
            _psybassWindow.Closed += (s, e) => _psybassWindow = null;
        }
        _psybassWindow.Activate();
    }

    private async void OnSubharmClick(object sender, RoutedEventArgs e)
    {
        // Refresh from the device so a value changed elsewhere is reflected; the
        // window shows a notice of its own on firmware without the feature.
        if (ViewModel.IsDeviceConnected && ViewModel.SubharmSupported)
            await Task.Run(() => ViewModel.FetchSubharm());

        if (_subharmWindow == null)
        {
            _subharmWindow = new SubharmonicSynthWindow(ViewModel);
            _subharmWindow.Closed += (s, e) => _subharmWindow = null;
        }
        _subharmWindow.Activate();
    }

    private async void OnTubeClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsDeviceConnected && ViewModel.TubeSupported)
            await Task.Run(() => ViewModel.FetchTube());

        if (_tubeWindow == null)
        {
            _tubeWindow = new TubeModellerWindow(ViewModel);
            _tubeWindow.Closed += (s, e) => _tubeWindow = null;
        }
        _tubeWindow.Activate();
    }

    private void OnSpectrumAnalyserClick(object sender, RoutedEventArgs e) => OpenSpectrumAnalyser();

    private void OpenSpectrumAnalyser()
    {
        if (_spectrumWindow == null)
        {
            _spectrumWindow = new SpectrumAnalyserWindow(ViewModel);
            _spectrumWindow.Closed += (s, e) => _spectrumWindow = null;
        }
        else _spectrumWindow.StartFromPage();
        _spectrumWindow.Activate();
    }

    private async void OnUpmixClick(object sender, RoutedEventArgs e)
    {
        // Refresh from the device so a value changed elsewhere (control surface,
        // preset load) is reflected; the window shows an unsupported notice if absent.
        if (ViewModel.IsDeviceConnected)
            await Task.Run(() => ViewModel.FetchUpmix());

        if (_upmixerWindow == null)
        {
            _upmixerWindow = new UpmixerWindow(ViewModel);
            _upmixerWindow.Closed += (s, e) => _upmixerWindow = null;
        }
        _upmixerWindow.Activate();
    }

    private async void OnMatrixMixerClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDeviceConnected)
        {
            var dialog = new ContentDialog
            {
                Title = "Device Not Connected",
                Content = "Please connect a DSPi device first.",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
            return;
        }

        if (_matrixMixerWindow != null)
        {
            _matrixMixerWindow.Close();
            return;
        }

        _matrixMixerWindow = new MatrixMixerWindow(ViewModel);
        _matrixMixerWindow.Closed += (s, e) => { _matrixMixerWindow = null; UpdateShortcutIconStates(); };
        _matrixMixerWindow.Activate();
        UpdateShortcutIconStates();
    }

    private async void OnTestSignalsClick(object sender, RoutedEventArgs e)
    {
        if (_testSignalsWindow != null)
        {
            _testSignalsWindow.Activate();
            return;
        }

        if (!ViewModel.IsDeviceConnected)
        {
            var dialog = new ContentDialog
            {
                Title = "Device Not Connected",
                Content = "Please connect a DSPi device first.",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
            return;
        }

        // Probe caps + descriptors before opening so the window builds fully populated.
        await ViewModel.FetchSiggenAsync();

        _testSignalsWindow = new TestSignalsWindow(ViewModel);
        _testSignalsWindow.Closed += (s, e) => _testSignalsWindow = null;
        _testSignalsWindow.Activate();
    }

    private void OnStatsClick(object sender, RoutedEventArgs e)
    {
        if (_statsWindow != null)
        {
            _statsWindow.Close();
            return;
        }

        _statsWindow = new StatsWindow(ViewModel);
        _statsWindow.Closed += (s, e) => { _statsWindow = null; UpdateShortcutIconStates(); };
        _statsWindow.Activate();
        UpdateShortcutIconStates();
    }

    private void OnInterruptMonitorClick(object sender, RoutedEventArgs e)
    {
        // Toggle behavior mirrors OnStatsClick: clicking again while open closes
        // the monitor. Singleton — only one log stream per device.
        if (_interruptMonitorWindow != null)
        {
            _interruptMonitorWindow.Close();
            return;
        }
        _interruptMonitorWindow = new InterruptMonitorWindow(ViewModel.Device);
        _interruptMonitorWindow.Closed += (s, e) => { _interruptMonitorWindow = null; };
        _interruptMonitorWindow.Activate();
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        // Modeless settings window — single instance. Reactivating an
        // already-open window beats spawning a duplicate; mirrors the
        // Interrupt Monitor / Stats singleton pattern elsewhere in this file
        // except we don't auto-close on a second click (the new layout
        // is large enough that you'd rather raise focus than dismiss).
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        try
        {
            _settingsWindow = new Settings.SettingsWindow(ViewModel);
            _settingsWindow.Closed += (s, e) => { _settingsWindow = null; UpdateShortcutIconStates(); };
            _settingsWindow.Activate();
            UpdateShortcutIconStates();
        }
        catch (System.Exception ex)
        {
            // Window construction failed (XAML parse, interop, etc.).
            // SettingsWindow.WriteCrashLog already logged the detail to
            // %LOCALAPPDATA%\DSPiConsole\settings-crash.log; surface a
            // user-visible dialog so the failure isn't silent.
            Settings.SettingsWindow.WriteCrashLog("OnSettingsClick", ex);
            _settingsWindow = null;
            var dialog = new ContentDialog
            {
                Title = "Couldn't open Settings",
                Content = $"{ex.GetType().Name}: {ex.Message}\n\nDetails written to %LOCALAPPDATA%\\DSPiConsole\\settings-crash.log",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }

    // Sidebar shortcut icon tap handlers

    private void OnSidebarMatrixMixerTapped(object sender, TappedRoutedEventArgs e)
    {
        OnMatrixMixerClick(sender, new RoutedEventArgs());
    }

    /// <summary>The sidebar gear toggles Settings, as on the macOS Console: a
    /// second click closes it (through its own pending-changes prompt). The
    /// menu item and Ctrl+, only ever open or bring it forward.</summary>
    private void OnSidebarSettingsTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.RequestClose();
            return;
        }
        OnSettingsClick(sender, new RoutedEventArgs());
    }

    private void OnSidebarLoudnessTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.LoudnessEnabled = !ViewModel.LoudnessEnabled;
    }

    private void OnSidebarLoudnessRightClick(object sender, RightTappedRoutedEventArgs e)
    {
        OnLoudnessClick(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void OnSidebarCrossfeedTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.CrossfeedEnabled = !ViewModel.CrossfeedEnabled;
    }

    private void OnSidebarCrossfeedRightClick(object sender, RightTappedRoutedEventArgs e)
    {
        OnCrossfeedClick(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void OnSidebarPsybassTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.PsybassEnabled = !ViewModel.PsybassEnabled;
    }

    private void OnSidebarPsybassRightClick(object sender, RightTappedRoutedEventArgs e)
    {
        OnPsybassClick(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void OnSidebarLevellerTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.LevellerEnabled = !ViewModel.LevellerEnabled;
    }

    private void OnSidebarLevellerRightClick(object sender, RightTappedRoutedEventArgs e)
    {
        OpenLevellerWindow();
        e.Handled = true;
    }

    private void OnLevellerClick(object sender, RoutedEventArgs e) => OpenLevellerWindow();

    private void OpenLevellerWindow()
    {
        if (_levellerWindow == null)
        {
            _levellerWindow = new VolumeLevellerWindow(ViewModel);
            _levellerWindow.Closed += (s, e) => { _levellerWindow = null; UpdateShortcutIconStates(); };
        }
        _levellerWindow.Activate();
        UpdateShortcutIconStates();
    }

    private void OnSidebarStatsTapped(object sender, TappedRoutedEventArgs e)
    {
        OnStatsClick(sender, new RoutedEventArgs());
    }

    private void OnSidebarBypassTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.Bypass = !ViewModel.Bypass;
    }

    // Shortcut icon illumination

    private static readonly Windows.UI.Color _iconDimColor = Windows.UI.Color.FromArgb(0xFF, 0x88, 0x88, 0x88);
    private static readonly Windows.UI.Color _iconHoverColor = Windows.UI.Color.FromArgb(0xFF, 0xBB, 0xBB, 0xBB);
    private static readonly Windows.UI.Color _iconActiveColor = Windows.UI.Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0);
    private static readonly Windows.UI.Color _iconBypassColor = Windows.UI.Color.FromArgb(0xFF, 0xF0, 0x50, 0x50);

    private void UpdateShortcutIconStates()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetIconColor(MatrixMixerIcon, _matrixMixerWindow != null ? _iconActiveColor : _iconDimColor);
            SetIconColor(SettingsIcon, _settingsWindow != null ? _iconActiveColor : _iconDimColor);
            SetIconColor(LoudnessIcon, ViewModel.LoudnessEnabled ? _iconActiveColor : _iconDimColor);
            SetIconColor(CrossfeedIcon, ViewModel.CrossfeedEnabled ? _iconActiveColor : _iconDimColor);
            SetIconColor(PsybassIcon, ViewModel.PsybassEnabled ? _iconActiveColor : _iconDimColor);
            SetIconColor(LevellerIcon, ViewModel.LevellerEnabled ? _iconActiveColor : _iconDimColor);
            SetIconColor(StatsIcon, _statsWindow != null ? _iconActiveColor : _iconDimColor);
            SetIconColor(BypassIcon, ViewModel.Bypass ? _iconBypassColor : _iconDimColor);
        });
    }

    private static void SetIconColor(FontIcon icon, Windows.UI.Color color)
    {
        icon.Foreground = new SolidColorBrush(color);
    }

    private bool IsShortcutIconActive(FontIcon icon)
    {
        if (icon == MatrixMixerIcon) return _matrixMixerWindow != null;
        if (icon == LoudnessIcon) return ViewModel.LoudnessEnabled;
        if (icon == CrossfeedIcon) return ViewModel.CrossfeedEnabled;
        if (icon == PsybassIcon) return ViewModel.PsybassEnabled;
        if (icon == LevellerIcon) return ViewModel.LevellerEnabled;
        if (icon == StatsIcon) return _statsWindow != null;
        if (icon == SettingsIcon) return _settingsWindow != null;
        if (icon == BypassIcon) return ViewModel.Bypass;
        return false;
    }

    private FontIcon? GetIconFromBorder(object sender)
    {
        if (sender is Border border && border.Child is FontIcon icon)
            return icon;
        return null;
    }

    private void OnShortcutIconPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var icon = GetIconFromBorder(sender);
        if (icon != null && !IsShortcutIconActive(icon))
        {
            AnimateIconForeground(icon, _iconHoverColor, TimeSpan.FromMilliseconds(150));
        }
    }

    private void OnShortcutIconPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var icon = GetIconFromBorder(sender);
        if (icon != null && !IsShortcutIconActive(icon))
        {
            AnimateIconForeground(icon, _iconDimColor, TimeSpan.FromMilliseconds(200));
        }
    }

    private void AnimateIconForeground(FontIcon icon, Windows.UI.Color targetColor, TimeSpan duration)
    {
        // Ensure icon has its own mutable brush instance for animation
        if (icon.Foreground is not SolidColorBrush currentBrush || currentBrush.Dispatcher == null)
        {
            var existingColor = (icon.Foreground as SolidColorBrush)?.Color ?? _iconDimColor;
            currentBrush = new SolidColorBrush(existingColor);
            icon.Foreground = currentBrush;
        }

        var animation = new ColorAnimation
        {
            To = targetColor,
            Duration = new Duration(duration),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, currentBrush);
        Storyboard.SetTargetProperty(animation, "Color");
        storyboard.Begin();
    }

    private async void OnAutoEQUpdateClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Update AutoEQ Database",
            Content = "Choose how to update the AutoEQ database:",
            PrimaryButtonText = "Import File",
            SecondaryButtonText = "Reset to Built-in",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            // Import file
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".json");

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                try
                {
                    var json = await Windows.Storage.FileIO.ReadTextAsync(file);
                    // Validate by attempting to deserialize
                    var testParse = System.Text.Json.JsonSerializer.Deserialize<AutoEQDatabase>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (testParse?.Entries == null || testParse.Entries.Count == 0)
                    {
                        await ShowErrorDialog("Invalid database file: no entries found.");
                        return;
                    }

                    var appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSPiConsole");
                    Directory.CreateDirectory(appDataPath);
                    var destPath = System.IO.Path.Combine(appDataPath, "autoeq_database.json");
                    File.WriteAllText(destPath, json);

                    AutoEQManager.Instance.LoadFromJson(json);
                    RefreshAutoEQFavoritesMenu();
                    await ShowSuccessDialog($"Database imported: {testParse.Entries.Count} entries loaded.");
                }
                catch (Exception ex)
                {
                    await ShowErrorDialog($"Failed to import database: {ex.Message}");
                }
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            // Reset to built-in
            var appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSPiConsole");
            var userDbPath = System.IO.Path.Combine(appDataPath, "autoeq_database.json");
            if (File.Exists(userDbPath))
            {
                File.Delete(userDbPath);
            }
            await AutoEQManager.Instance.LoadDatabaseAsync();
            RefreshAutoEQFavoritesMenu();
            await ShowSuccessDialog("Database reset to built-in version.");
        }
    }

    private void OnUpdateFirmwareClick(object sender, RoutedEventArgs e) => ShowFirmwareUpdate();

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
                await ImportMultiChannelFilters(result.ChannelFilters, result.ChannelXoverFilters);
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
        dialog.ConfigureForSingleChannel(filters.Count, ViewModel.ActiveInputs, ViewModel.ActiveOutputs,
            ViewModel.IsOutputEnabled, ch => ViewModel.GetChannelName(ch));

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            dialog.CollectSelectedChannels();
            foreach (var channelId in dialog.SelectedChannelIds)
            {
                if (!await ApplyFiltersToChannel(channelId, filters))
                {
                    await ShowErrorDialog("Communication Failure - Unable to perform operation");
                    return;
                }
            }

            if (dialog.SelectedChannelIds.Count > 0)
            {
                await ShowSuccessDialog($"Filters imported to {dialog.SelectedChannelIds.Count} channel(s)");
            }
        }
    }

    private async Task ImportMultiChannelFilters(
        Dictionary<int, List<FilterParams>> channelFilters,
        Dictionary<int, List<FilterParams>>? channelXover = null)
    {
        // Every channel the file mentions, PEQ or crossover.
        var inFile = new HashSet<int>(channelFilters.Keys);
        if (channelXover != null)
            inFile.UnionWith(channelXover.Keys);

        // Channels the file carries that this device can't take (e.g. an
        // 8-input export opened against a stereo device). Called out explicitly
        // rather than dropped, which is what used to happen.
        var selectable = new HashSet<int>(
            ViewModel.ActiveInputs.Select(c => (int)c.Id)
                .Concat(ViewModel.ActiveOutputs.Select(c => (int)c.Id)));
        var unavailable = inFile.Where(id => !selectable.Contains(id)).ToList();

        var dialog = new ChannelSelectionDialog { XamlRoot = Content.XamlRoot };
        dialog.ConfigureForMultiChannel(inFile, ViewModel.ActiveInputs, ViewModel.ActiveOutputs,
            ViewModel.IsOutputEnabled, ch => ViewModel.GetChannelName(ch));

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        dialog.CollectSelectedChannels();
        int applied = 0;
        foreach (var channelId in dialog.SelectedChannelIds)
        {
            channelFilters.TryGetValue(channelId, out var filters);
            List<FilterParams>? xover = null;
            channelXover?.TryGetValue(channelId, out xover);

            // A channel selected but absent from the file has nothing to apply —
            // skip it rather than counting it as imported.
            if (filters == null && xover == null) continue;

            if (!await ApplyFiltersToChannel(channelId, filters, xover))
            {
                await ShowErrorDialog("Communication Failure - Unable to perform operation");
                return;
            }
            applied++;
        }

        var skipped = string.Join(", ", unavailable
            .Select(id => Channel.All.FirstOrDefault(c => (int)c.Id == id)?.Name ?? $"channel {id}"));

        if (applied == 0)
        {
            var message = unavailable.Count > 0
                ? $"No filters imported. The file's channels are not available on this device: {skipped}."
                : "No filters imported — none of the selected channels are present in the file.";
            await ShowErrorDialog(message);
            return;
        }

        var summary = $"Filters imported to {applied} channel(s)";
        if (unavailable.Count > 0)
            summary += $"\n\nNot imported (not available on this device): {skipped}";
        await ShowSuccessDialog(summary);
    }

    private async Task<bool> ApplyFiltersToChannel(
        int channelId, List<FilterParams>? filters, List<FilterParams>? xover = null)
    {
        var channel = Channel.All.FirstOrDefault(c => (int)c.Id == channelId);
        if (channel == null) return false;

        var bandCount = channel.BandCount;

        // A null list means the file carried no PEQ section for this channel
        // (crossover only) — leave the channel's existing EQ alone.
        if (filters != null)
        {
            // Apply imported filters
            for (int i = 0; i < Math.Min(filters.Count, bandCount); i++)
            {
                if (!await SetFilterWithRetry(channelId, i, filters[i].Clone()))
                    return false;
            }

            // Clear remaining bands
            for (int i = filters.Count; i < bandCount; i++)
            {
                if (!await SetFilterWithRetry(channelId, i, new FilterParams(FilterType.Flat, 1000, 0.707f, 0)))
                    return false;
            }
        }

        // Crossover bands — only when the file specified them for this channel and
        // the target is a crossover-capable output (V11+). A null xover list means
        // the file had no crossover for this channel, so we leave it untouched.
        if (xover != null && channel.IsOutput && ViewModel.CrossoverSupported)
        {
            for (int i = 0; i < CrossoverFilter.MaxXoverBands; i++)
            {
                var band = i < xover.Count
                    ? xover[i].Clone()
                    : new FilterParams(FilterType.Flat, 1000, 0.707f, 0);
                if (!await SetXoverFilterWithRetry(channelId, i, band))
                    return false;
            }
        }

        return true;
    }

    private async Task<bool> SetFilterWithRetry(int channelId, int band, FilterParams p)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (await ViewModel.SetFilter(channelId, band, p))
                return true;
        }
        return false;
    }

    private async Task<bool> SetXoverFilterWithRetry(int channelId, int localBand, FilterParams p)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (await ViewModel.SetXoverFilter(channelId, localBand, p))
                return true;
        }
        return false;
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
            var xoverData = new Dictionary<int, IReadOnlyList<FilterParams>>();
            foreach (var channel in Channel.All)
            {
                var filters = ViewModel.GetFilters(channel);
                channelData[(int)channel.Id] = filters.ToList();

                // Crossover bands exist only on output channels, V11+ firmware.
                if (channel.IsOutput && ViewModel.CrossoverSupported)
                {
                    var xover = ViewModel.GetXoverFilters(channel);
                    if (xover.Count > 0)
                        xoverData[(int)channel.Id] = xover.ToList();
                }
            }

            var output = FilterFileService.GenerateExportString(
                channelData, xoverData.Count > 0 ? xoverData : null);
            await Windows.Storage.FileIO.WriteTextAsync(file, output);
            await ShowSuccessDialog("Filters exported successfully");
        }
        catch (Exception ex)
        {
            await ShowErrorDialog($"Failed to write file: {ex.Message}");
        }
    }

    // ── Whole-device configuration (.dspipreset) ──

    private async void OnExportPresetClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDeviceConnected)
        {
            await ShowErrorDialog("Not connected to device");
            return;
        }

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "DSPi Preset";
        picker.FileTypeChoices.Add("DSPi Preset File",
            new List<string> { PresetFileService.FileExtension });

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            var doc = PresetFileService.Capture(
                ViewModel, System.IO.Path.GetFileNameWithoutExtension(file.Name));

            // Firmware version is only available from the device, and the fetch
            // is a blocking control transfer — keep it off the UI thread and
            // treat a failure as "unknown" rather than failing the export.
            var info = await Task.Run(() => ViewModel.Device.GetDeviceInfo());
            if (info.HasValue)
            {
                doc.Meta.FirmwareVersion = info.Value.FirmwareVersion;
                if (!string.IsNullOrWhiteSpace(info.Value.Platform))
                    doc.Meta.Platform = info.Value.Platform;
            }

            await Windows.Storage.FileIO.WriteTextAsync(file, PresetFileService.Serialize(doc));

            int bands = doc.Channels.Sum(c => c.Eq.Count(b => b.Type != 0));
            int xover = doc.Channels.Sum(c => c.Crossover.Count(b => b.Type != 0));
            await ShowSuccessDialog(
                $"Preset file exported.\n\n" +
                $"{doc.Channels.Count} channels, {bands} active EQ bands, " +
                $"{xover} crossover bands, {doc.Matrix.Count} crosspoints.");
        }
        catch (Exception ex)
        {
            await ShowErrorDialog($"Failed to write file: {ex.Message}");
        }
    }

    private async void OnImportPresetClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDeviceConnected)
        {
            await ShowErrorDialog("Not connected to device");
            return;
        }

        var picker = new FileOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(PresetFileService.FileExtension);
        picker.FileTypeFilter.Add(".json");

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        PresetDocument doc;
        try
        {
            var json = await Windows.Storage.FileIO.ReadTextAsync(file);
            doc = PresetFileService.Deserialize(json);
        }
        catch (Exception ex)
        {
            await ShowErrorDialog($"Failed to read file: {ex.Message}");
            return;
        }

        var options = await AskPresetImportOptions(doc);
        if (options == null) return;

        // The apply issues hundreds of control transfers; unplugging the device
        // partway through surfaces as a USB exception. Catch it here — this is
        // an async void handler, so an escaping exception takes the app down,
        // and the device is left half-configured either way.
        PresetApplyReport report;
        try
        {
            report = await ApplyPresetWithProgress(doc, options);
        }
        catch (Exception ex)
        {
            await ShowErrorDialog(
                $"The import stopped partway through: {ex.Message}\n\n" +
                "The device now holds a mix of its previous settings and the imported ones. " +
                "Reconnect and import again, or load a stored preset to get back to a known state.");
            return;
        }

        await ShowPresetImportResult(report);
    }

    /// <summary>
    /// Ask what to bring in. Audio processing is the point of the file so it is
    /// fixed on; volume and physical wiring are opt-in, since neither
    /// necessarily belongs to the machine the file is being applied to.
    /// </summary>
    private async Task<PresetApplyOptions?> AskPresetImportOptions(PresetDocument doc)
    {
        var volumeCheck = new CheckBox { Content = "Volume levels (master / listening volume)" };
        var ioCheck = new CheckBox { Content = "Hardware I/O configuration (GPIO pins, clocks, ADAT, inputs)" };

        var panel = new StackPanel { Spacing = 8 };

        var provenance = new List<string>();
        if (!string.IsNullOrWhiteSpace(doc.Meta.Platform)) provenance.Add(doc.Meta.Platform!);
        if (!string.IsNullOrWhiteSpace(doc.Meta.FirmwareVersion)) provenance.Add($"firmware {doc.Meta.FirmwareVersion}");
        if (doc.Meta.SavedUtc != default) provenance.Add(doc.Meta.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = provenance.Count > 0
                ? $"Saved from {string.Join(", ", provenance)}."
                : "Applies the settings in this file to the connected device.",
        });

        // A document from a device with a different channel count still applies;
        // say so up front rather than burying it in the result. Only when both
        // platforms are actually known — MainViewModel.Platform is "" until the
        // device reports in, and "" is not a mismatch worth warning about.
        var sourcePlatform = doc.Meta.Platform;
        if (!string.IsNullOrWhiteSpace(sourcePlatform) &&
            !string.IsNullOrWhiteSpace(ViewModel.Platform) &&
            !string.Equals(sourcePlatform, ViewModel.Platform, StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"This file came from a {sourcePlatform} device and you are connected to " +
                       $"{ViewModel.Platform}. Anything the connected device doesn't have " +
                       $"will be skipped.",
                Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "EQ, crossover, delays, gains, routing and the DSP features are always applied.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(volumeCheck);
        panel.Children.Add(ioCheck);

        var dialog = new ContentDialog
        {
            Title = "Import Preset File",
            Content = panel,
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        return new PresetApplyOptions
        {
            AudioProcessing = true,
            VolumeLevels = volumeCheck.IsChecked == true,
            HardwareIo = ioCheck.IsChecked == true,
        };
    }

    /// <summary>
    /// Run the import behind a modal progress dialog. Applying a full document
    /// is several hundred control transfers, so the window would otherwise sit
    /// unresponsive-looking for a few seconds with the device half-configured.
    /// </summary>
    private async Task<PresetApplyReport> ApplyPresetWithProgress(
        PresetDocument doc, PresetApplyOptions options)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Width = 280 };
        var progressDialog = new ContentDialog
        {
            Title = "Importing Preset File",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Writing settings to the device..." },
                    bar,
                },
            },
            XamlRoot = Content.XamlRoot,
        };

        var progress = new Progress<double>(v => bar.Value = v);
        var showTask = progressDialog.ShowAsync();

        PresetApplyReport report;
        try
        {
            report = await PresetFileService.ApplyAsync(doc, ViewModel, options, progress);
        }
        finally
        {
            progressDialog.Hide();
            try { await showTask; } catch { }
        }

        return report;
    }

    private async Task ShowPresetImportResult(PresetApplyReport report)
    {
        var lines = new List<string>
        {
            $"Applied {report.ChannelsApplied} channels, {report.BandsApplied} EQ bands, " +
            $"{report.CrossoverBandsApplied} crossover bands, {report.CrosspointsApplied} crosspoints.",
        };

        if (report.MissingChannels.Count > 0)
            lines.Add($"Not present on this device: {string.Join(", ", report.MissingChannels)}");

        foreach (var skipped in report.Skipped)
            lines.Add($"Skipped: {skipped}");

        // Everything landed in RAM. Saying so avoids the trap of power-cycling
        // and losing the whole import. "Preset slot" rather than "preset", to
        // keep it distinct from the file that was just imported.
        lines.Add("These changes are live but not yet stored on the device. " +
                  "Save them to a preset slot to keep them.");

        // Anything the device refused or couldn't do isn't a success, so don't
        // put a "Success" heading over it.
        bool clean = report.MissingChannels.Count == 0 && report.Skipped.Count == 0;
        var text = string.Join("\n\n", lines);
        if (clean)
            await ShowSuccessDialog(text);
        else
            await ShowInfoDialog(text);
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
            if (!await ApplyAutoEQProfile(dialog.SelectedProfile))
            {
                await ShowErrorDialog("Communication Failure - Unable to perform operation");
                return;
            }
            await ShowSuccessDialog($"Applied profile: {dialog.SelectedProfile.DisplayName}");
        }
    }

    private async Task<bool> ApplyAutoEQProfile(HeadphoneEntry profile)
    {
        var filters = AutoEQManager.ConvertFilters(profile);

        var dialog = new ChannelSelectionDialog { XamlRoot = Content.XamlRoot };
        dialog.ConfigureForAutoEQ(
            filters.Count,
            ViewModel.ActiveInputs,
            ViewModel.ActiveOutputs,
            ViewModel.IsOutputEnabled,
            ch => ViewModel.GetChannelName(ch));

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return true; // user cancelled

        dialog.CollectSelectedChannels();
        if (dialog.SelectedChannelIds.Count == 0) return true;

        // Set preamp only after user confirms
        // Apply profile preamp to both input channels (AutoEQ preamp is a
        // global headroom compensation, applied pre-EQ).
        ViewModel.InputPreampLDb = (float)profile.Preamp;
        ViewModel.InputPreampRDb = (float)profile.Preamp;

        // Apply filters to each selected channel
        foreach (var channelId in dialog.SelectedChannelIds)
        {
            if (!await ApplyFiltersToChannel(channelId, filters))
                return false;
        }

        // Refresh editor if selected channel was affected
        if (_selectedChannel != null &&
            dialog.SelectedChannelIds.Contains((int)_selectedChannel.Id))
            ShowChannelEditor(_selectedChannel);

        return true;
    }

    private void RefreshAutoEQFavoritesMenu()
    {
        PopulateFavoritesMenu(AutoEQFavoritesMenu);
    }

    private void PopulateFavoritesMenu(MenuFlyoutSubItem menu)
    {
        menu.Items.Clear();

        var favorites = AutoEQManager.Instance.Favorites;
        if (favorites.Count == 0)
        {
            var emptyItem = new MenuFlyoutItem
            {
                Text = "No favorites yet",
                IsEnabled = false
            };
            menu.Items.Add(emptyItem);
        }
        else
        {
            foreach (var entry in favorites)
            {
                var item = new MenuFlyoutItem { Text = entry.DisplayName, Tag = entry };
                item.Click += OnAutoEQFavoriteClick;
                menu.Items.Add(item);
            }

            menu.Items.Add(new MenuFlyoutSeparator());

            var clearItem = new MenuFlyoutItem { Text = "Clear Favorites" };
            clearItem.Click += async (s, e) =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Clear Favorites",
                    Content = "Are you sure you want to clear all AutoEQ favorites?",
                    PrimaryButtonText = "Clear",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    AutoEQManager.Instance.ClearFavorites();
                    RefreshAutoEQFavoritesMenu();
                }
            };
            menu.Items.Add(clearItem);
        }
    }

    private async void OnAutoEQFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is HeadphoneEntry profile)
        {
            if (!await ApplyAutoEQProfile(profile))
            {
                await ShowErrorDialog("Communication Failure - Unable to perform operation");
                return;
            }
            await ShowSuccessDialog($"Applied profile: {profile.DisplayName}");
        }
    }

    #endregion

    #region Graph Resize

    private RowDefinition GraphRow => ContentGrid.RowDefinitions[0];

    private void OnGraphGripperPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el)
        {
            _isResizingGraph = true;
            _graphResizeStartY = e.GetCurrentPoint(ContentGrid).Position.Y;
            _graphResizeStartHeight = GraphRow.Height.Value;
            el.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnGraphGripperPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingGraph) return;
        var delta = e.GetCurrentPoint(ContentGrid).Position.Y - _graphResizeStartY;
        var newHeight = Math.Clamp(_graphResizeStartHeight + delta, GraphMinHeight, GraphMaxHeight);
        GraphRow.Height = new GridLength(newHeight);
        e.Handled = true;
    }

    private void OnGraphGripperPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingGraph) return;
        _isResizingGraph = false;
        if (sender is UIElement el)
            el.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    #endregion

    #region Graph Popout

    private DispatcherTimer? _popoutFadeTimer;
    private double _popoutFadeTarget;

    /// <summary>The graph options are open: the gear stays shown, brighter,
    /// even after the pointer leaves the graph for the panel.</summary>
    private bool _graphOptionsOpen;
    private bool _pointerOverGraph;

    private void OnGraphAreaPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverGraph = true;
        GraphPopoutButton.IsHitTestVisible = true;
        if (!_graphOptionsOpen) FadePopoutButton(0.6);
    }

    private void OnGraphAreaPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverGraph = false;
        if (_graphOptionsOpen) return;
        GraphPopoutButton.IsHitTestVisible = false;
        FadePopoutButton(0);
    }

    private void FadePopoutButton(double target)
    {
        _popoutFadeTarget = target;
        if (_popoutFadeTimer == null)
        {
            _popoutFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _popoutFadeTimer.Tick += (_, _) =>
            {
                double diff = _popoutFadeTarget - GraphPopoutButton.Opacity;
                if (Math.Abs(diff) < 0.02)
                {
                    GraphPopoutButton.Opacity = _popoutFadeTarget;
                    _popoutFadeTimer.Stop();
                }
                else
                {
                    GraphPopoutButton.Opacity += diff * 0.25;
                }
            };
        }
        _popoutFadeTimer.Start();
    }

    /// <summary>The gear over the graph: the page's spectrum, Graph Setup and
    /// Pop Out Graph.</summary>
    private void OnGraphOptionsClick(object sender, RoutedEventArgs e)
    {
        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = GraphOptionsPresenterStyle();
        flyout.Content = new Controls.GraphOptionsPanel(inPopOutWindow: false, onPopOut: () =>
        {
            flyout.Hide();
            OpenGraphPopout();
        }, ViewModel);
        flyout.Opened += (_, _) =>
        {
            _graphOptionsOpen = true;
            FadePopoutButton(0.95);
        };
        flyout.Closed += (_, _) =>
        {
            _graphOptionsOpen = false;
            GraphPopoutButton.IsHitTestVisible = _pointerOverGraph;
            FadePopoutButton(_pointerOverGraph ? 0.6 : 0);
        };
        GraphOptionsPlacement.ShowAt(flyout, GraphPopoutButton, this);
    }

    internal static Style GraphOptionsPresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 640.0));
        // The flyout's acrylic backdrop shows through (GraphOptionsPlacement).
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
        return style;
    }

    private void OnGraphReturnClick(object sender, RoutedEventArgs e) => _graphWindow?.Close();

    private double _graphHeightBeforePopout = 250;

    private void OpenGraphPopout()
    {
        if (_graphWindow != null)
        {
            _graphWindow.Activate();
            return;
        }

        // Animate graph row collapsing
        _graphHeightBeforePopout = Math.Clamp(GraphRow.Height.Value, GraphMinHeight, GraphMaxHeight);
        GraphGripperControl.Visibility = Visibility.Collapsed;
        AnimateGraphRow(GraphRow.Height.Value, 0, 250, () =>
        {
            GraphArea.Visibility = Visibility.Collapsed;
            GraphRow.Height = GridLength.Auto;
            if (_graphWindow != null) GraphReturnButton.Visibility = Visibility.Visible;
        });

        // Open popout window
        _graphWindow = new GraphWindow(ViewModel);
        if (_selectedChannel != null) UpdateGraphEditingSuspension(_selectedChannel);
        bool follows = AppSettings.Instance.PopoutFollowsSelectedChannel;
        _graphWindow.SetIgnoreVisibility(!follows);
        if (_selectedChannel != null && follows)
            _graphWindow.SetSelectedChannel((int)_selectedChannel.Id);
        _graphWindow.Closed += (_, _) =>
        {
            _graphWindow = null;
            GraphReturnButton.Visibility = Visibility.Collapsed;

            // Restore and animate graph row expanding
            GraphArea.Visibility = Visibility.Visible;
            GraphArea.Opacity = 0;
            GraphRow.Height = new GridLength(0);

            AnimateGraphRow(0, _graphHeightBeforePopout, 300, () =>
            {
                GraphGripperControl.Visibility = Visibility.Visible;
                GraphArea.Opacity = 1;
            });
        };
        _graphWindow.Activate();
    }

    private void AnimateGraphRow(double from, double to, int durationMs, Action? onComplete = null)
    {
        const int frameMs = 16;
        int totalFrames = Math.Max(1, durationMs / frameMs);
        int frame = 0;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(frameMs) };
        timer.Tick += (_, _) =>
        {
            frame++;
            double t = Math.Min(1.0, (double)frame / totalFrames);
            // Ease out cubic
            double eased = 1.0 - Math.Pow(1.0 - t, 3);
            double height = from + (to - from) * eased;
            GraphRow.Height = new GridLength(Math.Max(0, height));

            // Fade the graph area proportionally
            double opacity = to > from ? eased : 1.0 - eased;
            GraphArea.Opacity = opacity;

            if (t >= 1.0)
            {
                timer.Stop();
                onComplete?.Invoke();
            }
        };
        timer.Start();
    }

    #endregion

    #region Title Bar

    private void UpdateTitleBarDragRegion()
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        var buttonPos = TitleBarMenuButton.TransformToVisual(AppTitleBar).TransformPoint(new Windows.Foundation.Point(0, 0));

        int titleBarWidth = (int)(AppTitleBar.ActualWidth * scale);
        int titleBarHeight = (int)(AppTitleBar.ActualHeight * scale);
        int btnX = (int)(buttonPos.X * scale);
        int btnW = (int)(TitleBarMenuButton.ActualWidth * scale);

        // Two drag rectangles: left of button and right of button
        var left = new Windows.Graphics.RectInt32(0, 0, btnX, titleBarHeight);
        var right = new Windows.Graphics.RectInt32(btnX + btnW, 0, titleBarWidth - btnX - btnW, titleBarHeight);

        var nonClientInput = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)));
        nonClientInput.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough,
            new[] { new Windows.Graphics.RectInt32(btnX, (int)(buttonPos.Y * scale), btnW, (int)(TitleBarMenuButton.ActualHeight * scale)) });
    }

    #endregion
}
