using System.Globalization;
using System.Runtime.InteropServices;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using WinRT.Interop;

namespace DSPiConsole;

public sealed partial class MatrixMixerWindow : Window
{
    private const int SM_CYCAPTION = 4;
    private const int SM_CYFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);
    private readonly MainViewModel _viewModel;

    // Route UI controls: key = (inputIndex, outputIndex)
    private readonly Dictionary<(int, int), Border> _routeCircles = new();
    private readonly Dictionary<(int, int), TextBox> _routeGainTexts = new();
    private readonly Dictionary<(int, int), TextBlock> _routeInvButtons = new();
    private readonly Dictionary<(int, int), bool> _routeConnected = new();
    private readonly Dictionary<(int, int), bool> _routeInverted = new();
    private readonly Dictionary<(int, int), StackPanel> _routeCells = new();

    // Column header name TextBoxes: key = outputIndex
    private readonly Dictionary<int, TextBox> _headerNameTexts = new();

    // Input row name labels: key = inputIndex
    private readonly Dictionary<int, TextBlock> _inputLabelTexts = new();
    /// <summary>Per-input trim fields, multichannel only (wire input -> field).</summary>
    private readonly Dictionary<int, TextBox> _inputTrimTexts = new();
    /// <summary>What BuildUI added to the window: the card, or the scroll
    /// viewer around it; a rebuild removes this.</summary>
    private FrameworkElement? _tableRoot;

    // Output controls: key = outputIndex
    private readonly Dictionary<int, Button> _outputEnableButtons = new();
    private readonly Dictionary<int, TextBox> _outputGainTexts = new();
    private readonly Dictionary<int, TextBox> _outputDelayTexts = new();
    private readonly Dictionary<int, Button> _outputMuteButtons = new();
    private readonly Dictionary<int, bool> _outputMuted = new();

    private Border? _card;
    private int _nonClientH;
    private int _builtInputCount;
    private List<MatrixRow> _builtRows = new();
    private bool _closed;

    // Shared cell border brush (reused across all cells)
    private static readonly SolidColorBrush CellBorderBrush =
        new(Windows.UI.Color.FromArgb(25, 255, 255, 255));

    public MatrixMixerWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        appWindow!.Title = "Matrix Mixer";

        // Start at a minimal size so the later ResizeToContent expands rather
        // than shrinking from the OS default — avoids a visible flash.
        appWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));

        if (appWindow.TitleBar is { } titleBar)
        {
            titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
            titleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 130, 130, 130);
            titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 210, 210, 210);
            titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 120, 120, 120);
            titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 48, 48, 48);
        }

        BuildUI();
        UpdateDisconnectOverlay();

        Closed += (s, e) => _closed = true;

        _viewModel.PropertyChanged += (s, e) =>
        {
            if (_closed) return;
            if (e.PropertyName == nameof(MainViewModel.IsDeviceConnected))
                DispatcherQueue.TryEnqueue(() => { if (!_closed) UpdateDisconnectOverlay(); });
            else if (e.PropertyName is nameof(MainViewModel.InputPreampLDb) or nameof(MainViewModel.InputPreampRDb))
                DispatcherQueue.TryEnqueue(() => { if (!_closed) SyncInputTrims(); });
            else if (e.PropertyName is nameof(MainViewModel.ActiveInputs)
                                    or nameof(MainViewModel.UpmixRowsActive)
                                    or nameof(MainViewModel.UpmixSurroundRowsActive)
                                    or nameof(MainViewModel.UpmixCenterRowSilent))
                DispatcherQueue.TryEnqueue(() =>
                {
                    // ActiveOutputs changes also raise ActiveInputs; only rebuild
                    // here when the source rows actually changed (count or label —
                    // the upmix C row is relabelled in place when it goes silent).
                    if (!_closed && !BuildRowList().SequenceEqual(_builtRows))
                        RebuildUI();
                });
        };

        _viewModel.ChannelNameChanged += channelId =>
        {
            if (_closed) return;
            var active = _viewModel.ActiveOutputs;
            for (int o = 0; o < active.Count; o++)
            {
                if ((int)active[o].Id == channelId && _headerNameTexts.TryGetValue(o, out var tb))
                {
                    if (tb.FocusState == FocusState.Unfocused)
                        tb.Text = _viewModel.GetChannelName(active[o]);
                    break;
                }
            }
            var activeInputs = _viewModel.ActiveInputs;
            for (int i = 0; i < activeInputs.Count; i++)
            {
                if ((int)activeInputs[i].Id == channelId && _inputLabelTexts.TryGetValue(i, out var lbl))
                {
                    lbl.Text = _viewModel.GetChannelName(activeInputs[i]);
                    break;
                }
            }
        };

        _viewModel.ActiveOutputsChanged += (s, e) =>
        {
            if (_closed) return;
            DispatcherQueue.TryEnqueue(() => { if (!_closed) RebuildUI(); });
        };

        _viewModel.MatrixRouteChanged += (input, output) =>
        {
            if (_closed) return;
            DispatcherQueue.TryEnqueue(() => { if (!_closed) SyncRouteUI(input, output); });
        };

        _viewModel.MatrixOutputGainChanged += output =>
        {
            if (_closed) return;
            DispatcherQueue.TryEnqueue(() => { if (!_closed) SyncOutputGainUI(output); });
        };

        _viewModel.MatrixOutputMuteChanged += output =>
        {
            if (_closed) return;
            DispatcherQueue.TryEnqueue(() => { if (!_closed) SyncOutputMuteUI(output); });
        };

        _viewModel.MatrixOutputDelayChanged += output =>
        {
            if (_closed) return;
            DispatcherQueue.TryEnqueue(() => { if (!_closed) SyncOutputDelayUI(output); });
        };

        _viewModel.InputPreampExtChanged += _ =>
        {
            if (_closed) return;
            DispatcherQueue.TryEnqueue(() => { if (!_closed) SyncInputTrims(); });
        };

        _viewModel.OutputEnabledChanged += (output, enabled) =>
        {
            if (_closed) return;
            DispatcherQueue.TryEnqueue(() => { if (!_closed) SyncOutputEnableUI(output); });
        };

        // Size window to content after first layout: fonts are measured and DPI scale is known.
        // DesiredSize is in DIPs; AppWindow.Resize takes physical pixels.
        // Non-client height (titlebar + frame) is derived empirically — TitleBar.Height returns
        // 0 as an int (not null) so a null-coalescing fallback would silently miss it.
        bool sized = false;
        RootGrid.Loaded += (s, e) =>
        {
            if (sized) return;
            sized = true;
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            int dpi = (int)(96 * scale);
            _nonClientH = GetSystemMetricsForDpi(SM_CYCAPTION, (uint)dpi)
                        + 2 * GetSystemMetricsForDpi(SM_CYFRAME, (uint)dpi)
                        + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, (uint)dpi);
            ResizeToContent();
        };
    }

    private void BuildUI()
    {
        var outputs = _viewModel.ActiveOutputs;
        int outputCount = outputs.Count;

        // ── Inner table grid ─────────────────────────────────────────
        var grid = new Grid();

        // Columns: label (col 0) + one per output + flexible spacer
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        for (int o = 0; o < outputCount; o++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 95 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Rows: 0=headers, 1=routing bar, then one row per matrix source (active
        // inputs plus any upmix-derived rows) with a divider between each pair,
        // then the output bar + enable/gain/delay/mute.
        var sourceRows = BuildRowList();
        int inputCount = sourceRows.Count;
        _builtInputCount = inputCount;
        _builtRows = sourceRows;
        int outputBarRow = 2 * inputCount + 1;
        for (int r = 0; r < outputBarRow + 5; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Row 0: Header background ──
        var headerBg = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 30, 30)),
            BorderBrush = CellBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            IsHitTestVisible = false
        };
        Grid.SetColumnSpan(headerBg, outputCount + 2);
        grid.Children.Add(headerBg);

        // ── Row 0: Output column headers ──
        for (int o = 0; o < outputCount; o++)
        {
            var ch = outputs[o];
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 6,
                Padding = new Thickness(0, 16, 0, 16)
            };

            var headerName = new TextBox
            {
                Text = _viewModel.GetChannelName(ch),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxLength = ChannelNameLimit.MaxBytes,
                Style = (Style)RootGrid.Resources["InlineTextBoxStyle"]
            };
            _headerNameTexts[o] = headerName;
            headerName.KeyDown += (s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    var name = headerName.Text.Trim();
                    if (!string.IsNullOrEmpty(name)) _viewModel.SetChannelName(ch, name);
                    FocusSink.Focus(FocusState.Programmatic);
                }
                else if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    headerName.Text = _viewModel.GetChannelName(ch);
                    FocusSink.Focus(FocusState.Programmatic);
                }
            };
            headerName.LostFocus += (s, e) =>
            {
                var name = headerName.Text.Trim();
                if (!string.IsNullOrEmpty(name)) _viewModel.SetChannelName(ch, name);
            };
            panel.Children.Add(headerName);
            panel.ContextFlyout = HeaderMenu(o, ch, headerName);
            // The name field fills most of the header, and would otherwise show
            // its own text menu instead.
            headerName.ContextFlyout = HeaderMenu(o, ch, headerName);
            panel.Background = new SolidColorBrush(Colors.Transparent);

            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, ch.Color.R, ch.Color.G, ch.Color.B)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(75, ch.Color.R, ch.Color.G, ch.Color.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = ch.Descriptor,
                    FontSize = 9,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(195, ch.Color.R, ch.Color.G, ch.Color.B)),
                    CharacterSpacing = 80
                }
            });

            Grid.SetColumn(panel, o + 1);
            grid.Children.Add(panel);
        }

        // ── Row 1: ROUTING section bar (quick routes for a multichannel input) ──
        AddSectionBar(grid, 1, "ROUTING", outputCount, IsMultichannel ? QuickRoutes() : null);

        // ── Source rows (one per active input / upmix row, dividers between) ──
        for (int i = 0; i < inputCount; i++)
        {
            AddInputRow(grid, 2 + 2 * i, sourceRows[i].WireInput, sourceRows[i].Channel,
                sourceRows[i].Label, outputCount);
            if (i == inputCount - 1) break;

            var inputDivider = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 255, 255, 255)),
                IsHitTestVisible = false
            };
            Grid.SetColumnSpan(inputDivider, outputCount + 2);
            Grid.SetRow(inputDivider, 3 + 2 * i);
            grid.Children.Add(inputDivider);
        }

        // ── OUTPUT section bar ──
        AddSectionBar(grid, outputBarRow, "OUTPUT", outputCount);

        // ── Output data rows ──
        AddOutputDataRow(grid, outputBarRow + 1, "ENABLE", outputCount, isLast: false,
            makeCell: o =>
            {
                bool isEnabled = _viewModel.IsOutputEnabled(o);
                bool conflict = !isEnabled && _viewModel.WouldConflict(o);
                var btn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE7E8", FontSize = 15,
                        Foreground = new SolidColorBrush(isEnabled
                            ? Windows.UI.Color.FromArgb(255, 74, 143, 227)
                            : conflict
                                ? Windows.UI.Color.FromArgb(220, 230, 180, 50)
                                : Windows.UI.Color.FromArgb(120, 200, 200, 220)) },
                    Background = new SolidColorBrush(isEnabled
                        ? Windows.UI.Color.FromArgb(40, 74, 143, 227)
                        : Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 4, 8, 4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Tag = o
                };
                btn.Click += OnEnableClick;
                _outputEnableButtons[o] = btn;
                return btn;
            });

        AddOutputDataRow(grid, outputBarRow + 2, "GAIN", outputCount, isLast: false, unit: "dB",
            makeCell: o =>
            {
                float initGain = _viewModel.GetOutputGainDb(o);
                var text = new TextBox
                {
                    Text = FormatGain(initGain),
                    FontSize = 11,
                    FontFamily = new FontFamily("Cascadia Code, Consolas"),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Style = (Style)RootGrid.Resources["InlineTextBoxStyle"],
                    Width = 56
                };
                text.PointerWheelChanged += (s, e) =>
                {
                    if (!WheelEdits(text, e)) return;
                    e.Handled = true;
                    int delta = e.GetCurrentPoint(text).Properties.MouseWheelDelta;
                    float step = delta > 0 ? 0.5f : -0.5f;
                    float current = _viewModel.GetOutputGainDb(o);
                    float newGain = Math.Clamp(current + step, -60f, 12f);
                    _viewModel.SetOutputGainDb(o, newGain);
                    // SyncOutputGainUI skips focused boxes; refresh directly.
                    text.Text = FormatGain(_viewModel.GetOutputGainDb(o));
                };
                text.KeyDown += (s, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Enter)
                    {
                        e.Handled = true;
                        if (ParseGainText(text.Text, out float val))
                            _viewModel.SetOutputGainDb(o, Math.Clamp(val, -60f, 12f));
                        FocusSink.Focus(FocusState.Programmatic);
                    }
                    else if (e.Key == Windows.System.VirtualKey.Escape)
                    {
                        text.Text = FormatGain(_viewModel.GetOutputGainDb(o));
                        FocusSink.Focus(FocusState.Programmatic);
                    }
                };
                text.LostFocus += (s, e) =>
                {
                    if (ParseGainText(text.Text, out float val))
                        _viewModel.SetOutputGainDb(o, Math.Clamp(val, -60f, 12f));
                    else
                        text.Text = FormatGain(_viewModel.GetOutputGainDb(o));
                };
                text.RightTapped += (s, e) =>
                {
                    e.Handled = true;
                    _viewModel.SetOutputGainDb(o, 0f);
                    text.Text = FormatGain(_viewModel.GetOutputGainDb(o));
                };
                _outputGainTexts[o] = text;
                return text;
            });

        AddOutputDataRow(grid, outputBarRow + 3, "DELAY", outputCount, isLast: false, unit: "ms",
            makeCell: o =>
            {
                float initDelay = _viewModel.GetOutputDelayMs(o);
                var text = new TextBox
                {
                    Text = FormatDelay(initDelay),
                    FontSize = 11,
                    FontFamily = new FontFamily("Cascadia Code, Consolas"),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Style = (Style)RootGrid.Resources["InlineTextBoxStyle"],
                    Width = 56
                };
                text.PointerWheelChanged += (s, e) =>
                {
                    if (!WheelEdits(text, e)) return;
                    e.Handled = true;
                    int delta = e.GetCurrentPoint(text).Properties.MouseWheelDelta;
                    float step = delta > 0 ? 1f : -1f;
                    float current = _viewModel.GetOutputDelayMs(o);
                    float newDelay = Math.Clamp(current + step, 0f, _viewModel.MaxOutputDelayMs);
                    _viewModel.SetOutputDelayMs(o, newDelay);
                    text.Text = FormatDelay(_viewModel.GetOutputDelayMs(o));
                };
                text.KeyDown += (s, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Enter)
                    {
                        e.Handled = true;
                        // Unchanged text commits nothing: re-applying the cap would
                        // cut a delay stored at a lower sample rate.
                        if (text.Text != FormatDelay(_viewModel.GetOutputDelayMs(o)) && ParseDelayText(text.Text, out float val))
                            _viewModel.SetOutputDelayMs(o, Math.Clamp(val, 0f, _viewModel.MaxOutputDelayMs));
                        FocusSink.Focus(FocusState.Programmatic);
                    }
                    else if (e.Key == Windows.System.VirtualKey.Escape)
                    {
                        text.Text = FormatDelay(_viewModel.GetOutputDelayMs(o));
                        FocusSink.Focus(FocusState.Programmatic);
                    }
                };
                text.LostFocus += (s, e) =>
                {
                    if (text.Text == FormatDelay(_viewModel.GetOutputDelayMs(o))) return;
                    if (ParseDelayText(text.Text, out float val))
                        _viewModel.SetOutputDelayMs(o, Math.Clamp(val, 0f, _viewModel.MaxOutputDelayMs));
                    else
                        text.Text = FormatDelay(_viewModel.GetOutputDelayMs(o));
                };
                text.RightTapped += (s, e) =>
                {
                    e.Handled = true;
                    _viewModel.SetOutputDelayMs(o, 0f);
                    text.Text = FormatDelay(_viewModel.GetOutputDelayMs(o));
                };
                _outputDelayTexts[o] = text;
                return text;
            });

        AddOutputDataRow(grid, outputBarRow + 4, "MUTE", outputCount, isLast: true,
            makeCell: o =>
            {
                bool initMuted = _viewModel.GetOutputMuted(o);
                _outputMuted[o] = initMuted;
                var btn = new Button
                {
                    Content = new FontIcon { Glyph = initMuted ? "\uE74F" : "\uE767", FontSize = 15,
                        Foreground = new SolidColorBrush(initMuted
                            ? Windows.UI.Color.FromArgb(200, 210, 70, 70)
                            : Windows.UI.Color.FromArgb(120, 200, 200, 220)) },
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 4, 8, 4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Tag = o
                };
                btn.Click += (s, e) =>
                {
                    bool nowMuted = !_outputMuted[o];
                    _viewModel.SetOutputMuted(o, nowMuted);
                };
                _outputMuteButtons[o] = btn;
                return btn;
            });

        // ── Initial route cell dimming for disabled outputs ──────────
        for (int o = 0; o < outputCount; o++)
        {
            if (!_viewModel.IsOutputEnabled(o))
            {
                for (int input = 0; input < inputCount; input++)
                {
                    if (_routeCells.TryGetValue((input, o), out var cell))
                        cell.Opacity = 0.3;
                }
            }
        }

        // ── Card wrapper ─────────────────────────────────────────────
        var card = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(55, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(16),
            Child = grid
        };

        _card = card;
        // Eight input rows make a tall, wide table: it scrolls, and the window
        // is capped to the screen (ResizeToContent). Fewer rows fit outright.
        if (IsMultichannel)
        {
            card.Margin = new Thickness(0);
            _tableRoot = new ScrollViewer
            {
                Content = new Border { Padding = new Thickness(16), Child = card },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
        }
        else _tableRoot = card;
        RootGrid.Children.Add(_tableRoot);

        // Keep overlay on top of card content
        RootGrid.Children.Remove(DisconnectOverlay);
        RootGrid.Children.Add(DisconnectOverlay);
    }

    /// <summary>An output header's right-click menu: Identify (plays the ident
    /// tone; a disabled output would play nothing), Rename, Copy and Paste.</summary>
    private MenuFlyout HeaderMenu(int output, Channel ch, TextBox name)
    {
        var menu = new MenuFlyout();
        var identify = new MenuFlyoutItem { Text = "Identify" };
        identify.Click += async (_, _) => await _viewModel.IdentifyOutputAsync(output);
        var identifySeparator = new MenuFlyoutSeparator();
        var rename = new MenuFlyoutItem { Text = "Rename" };
        rename.Click += (_, _) =>
        {
            name.Focus(FocusState.Programmatic);
            name.SelectAll();
        };
        var copy = new MenuFlyoutItem { Text = "Copy Parameters" };
        copy.Click += (_, _) => _viewModel.CopyChannelParams(ch);
        var paste = new MenuFlyoutItem { Text = "Paste Parameters" };
        paste.Click += (_, _) => _viewModel.PasteChannelParams(ch);
        menu.Items.Add(identify);
        menu.Items.Add(identifySeparator);
        menu.Items.Add(rename);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Opening += (_, _) =>
        {
            bool canIdentify = _viewModel.IsDeviceConnected && _viewModel.SiggenSupported;
            identify.Visibility = identifySeparator.Visibility = canIdentify ? Visibility.Visible : Visibility.Collapsed;
            identify.IsEnabled = _viewModel.IsOutputEnabled(output);
            paste.IsEnabled = _viewModel.HasChannelClipboard;
        };
        return menu;
    }

    /// <summary>Whether the wheel over a value field steps it. In the scrolling
    /// (multichannel) table the wheel scrolls, unless Ctrl is held or the field
    /// has focus, so reading down the table never changes a value.</summary>
    private bool WheelEdits(Control field, PointerRoutedEventArgs e) =>
        _tableRoot is not ScrollViewer
        || field.FocusState != FocusState.Unfocused
        || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);

    private void ResizeToContent()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        // Measure the card itself: a scroll viewer would report the size it was given.
        FrameworkElement measured = _card ?? (FrameworkElement)RootGrid;
        measured.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = measured.DesiredSize;
        // A stereo card's own margin is in its desired size; in the scroll viewer
        // the card has none and the 16 px padding is around it instead.
        double margin = _card != null && _card.Margin.Left == 0 ? 32 : 0;
        int width = (int)Math.Ceiling((desired.Width + margin + 40) * scale);
        int height = (int)Math.Ceiling((desired.Height + margin) * scale) + _nonClientH;
        if (appWindow != null)
        {
            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            width = Math.Min(width, area.Width);
            height = Math.Min(height, area.Height);
        }
        appWindow?.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    private void UpdateDisconnectOverlay()
    {
        if (_viewModel.IsDeviceConnected)
        {
            var fadeOut = new DoubleAnimation
            {
                To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var sb = new Storyboard();
            sb.Children.Add(fadeOut);
            Storyboard.SetTarget(fadeOut, DisconnectOverlay);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            sb.Completed += (s, e) =>
            {
                DisconnectOverlay.Visibility = Visibility.Collapsed;
                DisconnectOverlay.Opacity = 1;
            };
            sb.Begin();
        }
        else
        {
            DisconnectOverlay.Opacity = 0;
            DisconnectOverlay.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation
            {
                To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var sb = new Storyboard();
            sb.Children.Add(fadeIn);
            Storyboard.SetTarget(fadeIn, DisconnectOverlay);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            sb.Begin();
        }
    }

    private void RebuildUI()
    {
        if (_tableRoot != null) RootGrid.Children.Remove(_tableRoot);
        _routeCircles.Clear();
        _routeGainTexts.Clear();
        _routeInvButtons.Clear();
        _routeConnected.Clear();
        _routeInverted.Clear();
        _routeCells.Clear();
        _headerNameTexts.Clear();
        _inputLabelTexts.Clear();
        _inputTrimTexts.Clear();
        _outputEnableButtons.Clear();
        _outputGainTexts.Clear();
        _outputDelayTexts.Clear();
        _outputMuteButtons.Clear();
        _outputMuted.Clear();
        BuildUI();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ResizeToContent);
    }

    // One matrix source row: wire input index (crosspoint row), a channel for the
    // row color, and the display label. Rows 2-4 double as the upmix-derived
    // C/Ls/Rs feeds while the upmixer runs on a stereo input (same crosspoint
    // storage as multichannel inputs 3-5 — the label is contextual by design).
    private readonly record struct MatrixRow(int WireInput, Channel Channel, string Label);

    private List<MatrixRow> BuildRowList()
    {
        var rows = new List<MatrixRow>();
        var inputs = _viewModel.ActiveInputs;
        for (int i = 0; i < inputs.Count; i++)
            rows.Add(new MatrixRow(i, inputs[i], _viewModel.GetChannelName(inputs[i])));
        if (_viewModel.UpmixRowsActive)
        {
            // Only reachable with exactly 2 active inputs, so the grid position
            // of each upmix row equals its wire input index. Row 2 stays listed
            // with the centre engine off (withdrawing it would renumber Ls/Rs
            // onto rows 2-3 and repoint existing routing) but carries no signal.
            rows.Add(new MatrixRow(2, Channel.Input3,
                _viewModel.UpmixCenterRowSilent ? "Upmix C (off)" : "Upmix C"));
            if (_viewModel.UpmixSurroundRowsActive)
            {
                rows.Add(new MatrixRow(3, Channel.Input4, "Upmix Ls"));
                rows.Add(new MatrixRow(4, Channel.Input5, "Upmix Rs"));
            }
        }
        return rows;
    }

    /// <summary>A multichannel input (more than the stereo pair live) gets the
    /// quick routes and per-input trims; stereo, with or without the upmixer,
    /// keeps the plain table.</summary>
    private bool IsMultichannel => _viewModel.ActiveInputChannelCount > 2;

    /// <summary>Out of the box an 8-channel stream is silent until routes are
    /// set, so the routing bar offers the diagonal and a clear.</summary>
    private FrameworkElement QuickRoutes()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        Button Make(string text, string tip, Action act)
        {
            var b = new Button { Content = text, FontSize = 11, Padding = new Thickness(10, 2, 10, 3), MinHeight = 0 };
            ToolTipService.SetToolTip(b, tip);
            b.Click += (_, _) => act();
            return b;
        }
        row.Children.Add(Make("Direct 1:1", "Route each input to the matching output (FL\u2192OUT1, FR\u2192OUT2, \u2026) and disable the PDM sub",
            _viewModel.ApplyDirectRouting));
        row.Children.Add(Make("Clear", "Disconnect every crosspoint", _viewModel.ClearAllRoutes));
        return row;
    }

    /// <summary>A compact per-input trim (the input's preamp), for correcting
    /// level or a host's channel-mapping differences (spec §14).</summary>
    private TextBox TrimField(int wireInput)
    {
        var text = new TextBox
        {
            Text = FormatGain(_viewModel.InputPreampAt(wireInput)),
            FontSize = 10,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Left,
            TextAlignment = TextAlignment.Left,
            Style = (Style)RootGrid.Resources["InlineTextBoxStyle"],
            Width = 56,
            Margin = new Thickness(14, 0, 0, 0),
        };
        ToolTipService.SetToolTip(text, "Input trim (preamp)");
        void Commit(float db)
        {
            _viewModel.SetInputPreampAt(wireInput, Math.Clamp(db, -60f, 12f));
            text.Text = FormatGain(_viewModel.InputPreampAt(wireInput));
        }
        text.PointerWheelChanged += (s, e) =>
        {
            if (!WheelEdits(text, e)) return;
            e.Handled = true;
            int delta = e.GetCurrentPoint(text).Properties.MouseWheelDelta;
            Commit(_viewModel.InputPreampAt(wireInput) + (delta > 0 ? 0.5f : -0.5f));
        };
        text.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                if (ParseGainText(text.Text, out float val)) Commit(val);
                FocusSink.Focus(FocusState.Programmatic);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                text.Text = FormatGain(_viewModel.InputPreampAt(wireInput));
                FocusSink.Focus(FocusState.Programmatic);
            }
        };
        text.LostFocus += (s, e) =>
        {
            if (ParseGainText(text.Text, out float val)) Commit(val);
            else text.Text = FormatGain(_viewModel.InputPreampAt(wireInput));
        };
        text.RightTapped += (s, e) =>
        {
            e.Handled = true;
            Commit(0f);
        };
        _inputTrimTexts[wireInput] = text;
        return text;
    }

    private void SyncInputTrims()
    {
        foreach (var (wire, text) in _inputTrimTexts)
            if (text.FocusState == FocusState.Unfocused)
                text.Text = FormatGain(_viewModel.InputPreampAt(wire));
    }

    // Adds an input row (routing section): colored label + route cells per output
    private void AddInputRow(Grid grid, int row, int inputIndex, Channel inputCh, string label0, int outputCount)
    {
        var label = new TextBlock
        {
            Text = label0,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(inputCh.Color),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(14, 0, 4, 0)
        };
        _inputLabelTexts[inputIndex] = label;
        FrameworkElement labelCell = label;
        // A real input row of a multichannel input carries its trim; upmix
        // rows have no preamp of their own.
        if (IsMultichannel && inputIndex < _viewModel.ActiveInputChannelCount)
        {
            var stack = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(label);
            stack.Children.Add(TrimField(inputIndex));
            labelCell = stack;
        }
        Grid.SetColumn(labelCell, 0);
        Grid.SetRow(labelCell, row);
        grid.Children.Add(labelCell);

        for (int outp = 0; outp < outputCount; outp++)
        {
            var cell = BuildRouteCell(inputIndex, outp, inputCh.Color);
            Grid.SetColumn(cell, outp + 1);
            Grid.SetRow(cell, row);
            grid.Children.Add(cell);
        }
    }

    // Adds an output data row (bottom section) with label in col 0 and cell content per output
    private void AddOutputDataRow(Grid grid, int row, string labelText, int outputCount,
        bool isLast, Func<int, UIElement> makeCell, string? unit = null)
    {
        var bottomBorder = isLast ? 0 : 1;

        // Label cell (col 0) — row header, with optional small unit suffix.
        var labelCell = new Border
        {
            BorderBrush = CellBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, bottomBorder),
            Padding = new Thickness(14, 12, 0, 12)
        };
        var labelColor = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 255, 255, 255));
        if (unit == null)
        {
            labelCell.Child = new TextBlock
            {
                Text = labelText,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = labelColor,
                CharacterSpacing = 80,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new TextBlock
            {
                Text = labelText,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = labelColor,
                CharacterSpacing = 80,
                VerticalAlignment = VerticalAlignment.Bottom
            });
            stack.Children.Add(new TextBlock
            {
                Text = unit,
                FontSize = 8,
                Foreground = labelColor,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 1)
            });
            labelCell.Child = stack;
        }
        Grid.SetColumn(labelCell, 0);
        Grid.SetRow(labelCell, row);
        grid.Children.Add(labelCell);

        // Content cells (cols 1+)
        for (int o = 0; o < outputCount; o++)
        {
            var contentCell = new Border
            {
                BorderBrush = CellBorderBrush,
                BorderThickness = new Thickness(0, 0, 0, bottomBorder),
                Padding = new Thickness(0, 12, 0, 12)
            };
            contentCell.Child = makeCell(o);
            Grid.SetColumn(contentCell, o + 1);
            Grid.SetRow(contentCell, row);
            grid.Children.Add(contentCell);
        }
    }

    private FrameworkElement BuildRouteCell(int input, int output, Windows.UI.Color inputColor)
    {
        // Tighten the cells when many source rows are shown so an 8-input
        // matrix still fits on screen.
        bool compact = _builtInputCount > 4;
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = compact ? 6 : 10,
            Margin = compact ? new Thickness(0, 8, 0, 8) : new Thickness(0, 18, 0, 18)
        };

        // Initialize from ViewModel state
        bool initConnected = _viewModel.GetMatrixRouting(input, output);
        float initGain = _viewModel.GetMatrixGain(input, output);
        bool initInverted = _viewModel.GetMatrixInvert(input, output);

        // Gain text (above circle) — type, scroll, or right-click to reset
        var gainText = new TextBox
        {
            Text = FormatGain(initGain),
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Style = (Style)RootGrid.Resources["InlineTextBoxStyle"],
            Width = 56
        };
        gainText.PointerWheelChanged += (s, e) =>
        {
            if (!WheelEdits(gainText, e)) return;
            e.Handled = true;
            int delta = e.GetCurrentPoint(gainText).Properties.MouseWheelDelta;
            float step = delta > 0 ? 0.5f : -0.5f;
            float current = _viewModel.GetMatrixGain(input, output);
            float newGain = Math.Clamp(current + step, -60f, 12f);
            bool enabled = _viewModel.GetMatrixRouting(input, output);
            bool inv = _viewModel.GetMatrixInvert(input, output);
            _viewModel.SetMatrixRoute(input, output, enabled, newGain, inv);
            gainText.Text = FormatGain(_viewModel.GetMatrixGain(input, output));
        };
        gainText.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                if (ParseGainText(gainText.Text, out float val))
                {
                    val = Math.Clamp(val, -60f, 12f);
                    bool enabled = _viewModel.GetMatrixRouting(input, output);
                    bool inv = _viewModel.GetMatrixInvert(input, output);
                    _viewModel.SetMatrixRoute(input, output, enabled, val, inv);
                }
                FocusSink.Focus(FocusState.Programmatic);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                gainText.Text = FormatGain(_viewModel.GetMatrixGain(input, output));
                FocusSink.Focus(FocusState.Programmatic);
            }
        };
        gainText.LostFocus += (s, e) =>
        {
            if (ParseGainText(gainText.Text, out float val))
            {
                val = Math.Clamp(val, -60f, 12f);
                bool enabled = _viewModel.GetMatrixRouting(input, output);
                bool inv = _viewModel.GetMatrixInvert(input, output);
                _viewModel.SetMatrixRoute(input, output, enabled, val, inv);
            }
            else
            {
                gainText.Text = FormatGain(_viewModel.GetMatrixGain(input, output));
            }
        };
        gainText.RightTapped += (s, e) =>
        {
            e.Handled = true;
            bool enabled = _viewModel.GetMatrixRouting(input, output);
            bool inv = _viewModel.GetMatrixInvert(input, output);
            _viewModel.SetMatrixRoute(input, output, enabled, 0f, inv);
            gainText.Text = FormatGain(_viewModel.GetMatrixGain(input, output));
        };
        _routeGainTexts[(input, output)] = gainText;
        panel.Children.Add(gainText);

        // Connection circle — clickable toggle
        _routeConnected[(input, output)] = initConnected;
        var circle = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderThickness = initConnected ? new Thickness(0) : new Thickness(2),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 160, 160, 170)),
            Background = initConnected
                ? new SolidColorBrush(inputColor)
                : new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Center,
            Tag = (input, output, inputColor)
        };
        circle.Tapped += (s, e) =>
        {
            var key = (input, output);
            bool nowConnected = !_routeConnected[key];
            float gain = _viewModel.GetMatrixGain(input, output);
            bool inv = _viewModel.GetMatrixInvert(input, output);
            _viewModel.SetMatrixRoute(input, output, nowConnected, gain, inv);
        };
        _routeCircles[(input, output)] = circle;
        panel.Children.Add(circle);

        // INV text (below circle) — dims when off, illuminates when on, fades on hover
        _routeInverted[(input, output)] = initInverted;
        var invColor = initInverted ? InvOnColor : InvOffColor;
        var invBrush = new SolidColorBrush(invColor);
        var invText = new TextBlock
        {
            Text = "INV",
            FontSize = 9,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = invBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        void AnimateInvColor(Color to, int ms)
        {
            var anim = new ColorAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
                EnableDependentAnimation = true
            };
            var sb = new Storyboard();
            Storyboard.SetTarget(anim, invBrush);
            Storyboard.SetTargetProperty(anim, "Color");
            sb.Children.Add(anim);
            sb.Begin();
        }

        invText.Tapped += (s, e) =>
        {
            bool nowInverted = !_routeInverted[(input, output)];
            bool enabled = _viewModel.GetMatrixRouting(input, output);
            float gain = _viewModel.GetMatrixGain(input, output);
            _viewModel.SetMatrixRoute(input, output, enabled, gain, nowInverted);
        };
        invText.PointerEntered += (s, e) =>
        {
            if (!_routeInverted[(input, output)])
                AnimateInvColor(Color.FromArgb(130, 210, 210, 225), 150);
        };
        invText.PointerExited += (s, e) =>
        {
            if (!_routeInverted[(input, output)])
                AnimateInvColor(InvOffColor, 200);
        };
        _routeInvButtons[(input, output)] = invText;
        panel.Children.Add(invText);

        _routeCells[(input, output)] = panel;
        ApplyRouteState(input, output);
        return panel;
    }

    // Full-width section header bar spanning all columns
    private void AddSectionBar(Grid grid, int row, string text, int outputCount, FrameworkElement? trailing = null)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        content.Children.Add(new Border
        {
            Width = 2,
            Height = 10,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        });

        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
            CharacterSpacing = 150,
            VerticalAlignment = VerticalAlignment.Center
        });

        FrameworkElement barContent = content;
        if (trailing != null)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(content);
            Grid.SetColumn(trailing, 1);
            g.Children.Add(trailing);
            barContent = g;
        }
        var bar = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 36, 36)),
            BorderBrush = CellBorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(14, trailing != null ? 4 : 8, 14, trailing != null ? 4 : 8),
            Child = barContent
        };

        Grid.SetColumnSpan(bar, outputCount + 2);
        Grid.SetRow(bar, row);
        grid.Children.Add(bar);
    }

    /// <summary>
    /// Toggles a route circle between connected (solid fill) and disconnected (hollow ring).
    /// </summary>
    private void SetRouteConnected(int input, int output, bool connected)
    {
        if (!_routeCircles.TryGetValue((input, output), out var circle))
            return;

        if (circle.Tag is not (int _, int _, Windows.UI.Color inputColor))
            return;

        if (connected)
        {
            circle.Background = new SolidColorBrush(inputColor);
            circle.BorderThickness = new Thickness(0);
        }
        else
        {
            circle.Background = new SolidColorBrush(Colors.Transparent);
            circle.BorderThickness = new Thickness(2);
        }
    }

    // ── Sync helpers: update UI from ViewModel state ──

    private void SyncRouteUI(int input, int output)
    {
        bool connected = _viewModel.GetMatrixRouting(input, output);
        _routeConnected[(input, output)] = connected;
        SetRouteConnected(input, output, connected);

        float gain = _viewModel.GetMatrixGain(input, output);
        if (_routeGainTexts.TryGetValue((input, output), out var gt) &&
            gt.FocusState == FocusState.Unfocused)
            gt.Text = FormatGain(gain);

        _routeInverted[(input, output)] = _viewModel.GetMatrixInvert(input, output);
        ApplyRouteState(input, output);
    }

    private static readonly Color InvOnColor = Color.FromArgb(255, 255, 159, 10);
    private static readonly Color InvOffColor = Color.FromArgb(60, 200, 200, 220);
    private static readonly Color RingColor = Color.FromArgb(90, 160, 160, 170);
    private static readonly Color ConflictRingColor = Color.FromArgb(115, 255, 159, 10);

    /// <summary>A crosspoint's gain and INV show only while it is connected
    /// (kept in place at zero opacity, so the rows never change height); an
    /// active INV is orange and bold. The PDM column's empty rings turn orange
    /// while enabling PDM would switch other outputs off.</summary>
    private void ApplyRouteState(int input, int output)
    {
        bool connected = _viewModel.GetMatrixRouting(input, output);
        bool inverted = _viewModel.GetMatrixInvert(input, output);
        if (_routeGainTexts.TryGetValue((input, output), out var gain))
        {
            gain.Opacity = connected ? 1 : 0;
            gain.IsHitTestVisible = connected;
            gain.IsTabStop = connected;
        }
        if (_routeInvButtons.TryGetValue((input, output), out var inv))
        {
            inv.Opacity = connected ? 1 : 0;
            inv.IsHitTestVisible = connected;
            inv.FontWeight = inverted ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold;
            if (inv.Foreground is SolidColorBrush brush) brush.Color = inverted ? InvOnColor : InvOffColor;
        }
        if (!connected && _routeCircles.TryGetValue((input, output), out var circle))
        {
            bool ring = output == _viewModel.PdmOutputIndex && _viewModel.WouldConflict(output);
            circle.BorderBrush = new SolidColorBrush(ring ? ConflictRingColor : RingColor);
        }
    }

    private void RefreshConflictRings()
    {
        int pdm = _viewModel.PdmOutputIndex;
        foreach (var (input, output) in _routeCells.Keys)
            if (output == pdm) ApplyRouteState(input, output);
    }

    private void SyncOutputGainUI(int output)
    {
        if (_outputGainTexts.TryGetValue(output, out var text) &&
            text.FocusState == FocusState.Unfocused)
            text.Text = FormatGain(_viewModel.GetOutputGainDb(output));
    }

    private void SyncOutputMuteUI(int output)
    {
        bool muted = _viewModel.GetOutputMuted(output);
        _outputMuted[output] = muted;
        if (_outputMuteButtons.TryGetValue(output, out var btn) && btn.Content is FontIcon icon)
        {
            icon.Glyph = muted ? "\uE74F" : "\uE767";
            icon.Foreground = muted
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(200, 210, 70, 70))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(120, 200, 200, 220));
        }
    }

    private void SyncOutputDelayUI(int output)
    {
        if (_outputDelayTexts.TryGetValue(output, out var text) &&
            text.FocusState == FocusState.Unfocused)
            text.Text = FormatDelay(_viewModel.GetOutputDelayMs(output));
    }

    private void SyncOutputEnableUI(int output)
    {
        bool enabled = _viewModel.IsOutputEnabled(output);
        if (_outputEnableButtons.TryGetValue(output, out var btn))
        {
            bool conflict = !enabled && _viewModel.WouldConflict(output);
            if (btn.Content is FontIcon icon)
                icon.Foreground = new SolidColorBrush(enabled
                    ? Windows.UI.Color.FromArgb(255, 74, 143, 227)
                    : conflict
                        ? Windows.UI.Color.FromArgb(220, 230, 180, 50)
                        : Windows.UI.Color.FromArgb(120, 200, 200, 220));
            btn.Background = enabled
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 74, 143, 227))
                : new SolidColorBrush(Colors.Transparent);
        }

        // Dim / un-dim route cells for this output
        for (int input = 0; input < MainViewModel.MatrixMaxInputs; input++)
        {
            // Dimmed but still clickable: a route can be set up before the
            // output is switched on.
            if (_routeCells.TryGetValue((input, output), out var cell))
                cell.Opacity = enabled ? 1.0 : 0.3;
        }

        // Refresh conflict styling on all enable buttons and the PDM rings
        RefreshConflictStyling();
        RefreshConflictRings();
    }

    private async void OnEnableClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int o) return;

        bool currentlyEnabled = _viewModel.IsOutputEnabled(o);
        if (currentlyEnabled)
        {
            // Disabling — always allowed, no conflict check
            _viewModel.SetOutputEnabled(o, false);
            _viewModel.SetOutputEnableUsb(o, false);
            return;
        }

        // Enabling — check for conflict
        if (_viewModel.WouldConflict(o))
        {
            bool enablingPdm = o == _viewModel.PdmOutputIndex;
            var dialog = new ContentDialog
            {
                Title = "Warning",
                Content = GetConflictMessage(o),
                PrimaryButtonText = enablingPdm ? "Enable PDM" : "Disable PDM",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            // Perform the switch
            if (o == _viewModel.PdmOutputIndex)
                await _viewModel.SwitchToPdmAsync();
            else
                await _viewModel.SwitchFromPdmAsync(o);
            return;
        }

        // No conflict — enable directly
        _viewModel.SetOutputEnabled(o, true);
        _viewModel.SetOutputEnableUsb(o, true);
    }

    /// <summary>The outputs that share Core 1 with the PDM sub, named by
    /// their 1-based numbers, as the macOS Console words it.</summary>
    private string GetConflictMessage(int outputIndex)
    {
        if (outputIndex == _viewModel.PdmOutputIndex)
        {
            var (first, last) = _viewModel.EqWorkerRange;
            return $"Outputs {first + 1}-{last + 1} will be disabled. Are you sure?";
        }
        return "The PDM output will be disabled. Are you sure?";
    }

    private void RefreshConflictStyling()
    {
        var outputs = _viewModel.ActiveOutputs;
        for (int o = 0; o < outputs.Count; o++)
        {
            if (!_outputEnableButtons.TryGetValue(o, out var btn)) continue;
            if (btn.Content is not FontIcon icon) continue;
            bool enabled = _viewModel.IsOutputEnabled(o);
            if (enabled) continue; // active outputs keep blue — already set
            bool conflict = _viewModel.WouldConflict(o);
            icon.Foreground = new SolidColorBrush(conflict
                ? Windows.UI.Color.FromArgb(220, 230, 180, 50)
                : Windows.UI.Color.FromArgb(120, 200, 200, 220));
        }
    }

    private static string FormatGain(float db) =>
        string.Format(CultureInfo.InvariantCulture, "{0:+0.00;-0.00;0.00}", db);

    private static string FormatDelay(float ms) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.00##}", ms);

    private static bool ParseGainText(string text, out float value)
    {
        var s = text.Replace("dB", "").Trim();
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool ParseDelayText(string text, out float value)
    {
        var s = text.Replace("ms", "").Trim();
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
