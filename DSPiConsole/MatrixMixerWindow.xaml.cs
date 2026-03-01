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

    // Output controls: key = outputIndex
    private readonly Dictionary<int, Button> _outputEnableButtons = new();
    private readonly Dictionary<int, TextBox> _outputGainTexts = new();
    private readonly Dictionary<int, TextBox> _outputDelayTexts = new();
    private readonly Dictionary<int, Button> _outputMuteButtons = new();
    private readonly Dictionary<int, bool> _outputMuted = new();

    private Border? _card;
    private int _nonClientH;
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
        var appWindow = AppWindow.GetFromWindowId(windowId) ??
            throw new InvalidOperationException("Failed to retrieve AppWindow");

        appWindow.Title = "Matrix Mixer";

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
            if (_closed || e.PropertyName != nameof(MainViewModel.IsDeviceConnected)) return;
            DispatcherQueue.TryEnqueue(() => { if (!_closed) UpdateDisconnectOverlay(); });
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

        // Rows: 0=headers, 1=routing bar, 2=input L, 3=divider, 4=input R, 5=output bar,
        //       6=enable, 7=gain, 8=delay, 9=mute
        for (int r = 0; r < 10; r++)
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

        // ── Row 1: ROUTING section bar ──
        AddSectionBar(grid, 1, "ROUTING", outputCount);

        // ── Row 2: Input L ──
        var inputs = Channel.Inputs;
        AddInputRow(grid, 2, inputs[0], "Input L", outputCount);

        // ── Row 3: Input divider ──
        var inputDivider = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 255, 255, 255)),
            IsHitTestVisible = false
        };
        Grid.SetColumnSpan(inputDivider, outputCount + 2);
        Grid.SetRow(inputDivider, 3);
        grid.Children.Add(inputDivider);

        // ── Row 4: Input R ──
        AddInputRow(grid, 4, inputs[1], "Input R", outputCount);

        // ── Row 5: OUTPUT section bar ──
        AddSectionBar(grid, 5, "OUTPUT", outputCount);

        // ── Rows 6–9: Output data rows ──
        AddOutputDataRow(grid, 6, "ENABLE", outputCount, isLast: false,
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

        AddOutputDataRow(grid, 7, "GAIN", outputCount, isLast: false,
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
                    Style = (Style)RootGrid.Resources["InlineTextBoxStyle"],
                    Width = 60
                };
                text.PointerWheelChanged += (s, e) =>
                {
                    e.Handled = true;
                    int delta = e.GetCurrentPoint(text).Properties.MouseWheelDelta;
                    float step = delta > 0 ? 0.5f : -0.5f;
                    float current = _viewModel.GetOutputGainDb(o);
                    float newGain = Math.Clamp(current + step, -60f, 12f);
                    _viewModel.SetOutputGainDb(o, newGain);
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
                };
                _outputGainTexts[o] = text;
                return text;
            });

        AddOutputDataRow(grid, 8, "DELAY", outputCount, isLast: false,
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
                    Style = (Style)RootGrid.Resources["InlineTextBoxStyle"],
                    Width = 60
                };
                text.PointerWheelChanged += (s, e) =>
                {
                    e.Handled = true;
                    int delta = e.GetCurrentPoint(text).Properties.MouseWheelDelta;
                    float step = delta > 0 ? 1f : -1f;
                    float current = _viewModel.GetOutputDelayMs(o);
                    float newDelay = Math.Max(0f, MathF.Round(current + step));
                    _viewModel.SetOutputDelayMs(o, newDelay);
                };
                text.KeyDown += (s, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Enter)
                    {
                        e.Handled = true;
                        if (ParseDelayText(text.Text, out float val))
                            _viewModel.SetOutputDelayMs(o, Math.Max(0f, MathF.Round(val)));
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
                    if (ParseDelayText(text.Text, out float val))
                        _viewModel.SetOutputDelayMs(o, Math.Max(0f, MathF.Round(val)));
                    else
                        text.Text = FormatDelay(_viewModel.GetOutputDelayMs(o));
                };
                text.RightTapped += (s, e) =>
                {
                    e.Handled = true;
                    _viewModel.SetOutputDelayMs(o, 0f);
                };
                _outputDelayTexts[o] = text;
                return text;
            });

        AddOutputDataRow(grid, 9, "MUTE", outputCount, isLast: true,
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
                for (int input = 0; input < 2; input++)
                {
                    if (_routeCells.TryGetValue((input, o), out var cell))
                    {
                        cell.Opacity = 0.25;
                        cell.IsHitTestVisible = false;
                    }
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
        RootGrid.Children.Add(card);

        // Keep overlay on top of card content
        RootGrid.Children.Remove(DisconnectOverlay);
        RootGrid.Children.Add(DisconnectOverlay);
    }

    private void ResizeToContent()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = RootGrid.DesiredSize;
        appWindow?.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Ceiling((desired.Width + 40) * scale),
            (int)Math.Ceiling(desired.Height * scale) + _nonClientH));
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
        if (_card != null) RootGrid.Children.Remove(_card);
        _routeCircles.Clear();
        _routeGainTexts.Clear();
        _routeInvButtons.Clear();
        _routeConnected.Clear();
        _routeInverted.Clear();
        _routeCells.Clear();
        _headerNameTexts.Clear();
        _outputEnableButtons.Clear();
        _outputGainTexts.Clear();
        _outputDelayTexts.Clear();
        _outputMuteButtons.Clear();
        _outputMuted.Clear();
        BuildUI();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ResizeToContent);
    }

    // Adds an input row (routing section): colored label + route cells per output
    private void AddInputRow(Grid grid, int row, Channel inputCh, string labelText, int outputCount)
    {
        var label = new TextBlock
        {
            Text = labelText,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(inputCh.Color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        };
        Grid.SetColumn(label, 0);
        Grid.SetRow(label, row);
        grid.Children.Add(label);

        int inputIndex = inputCh == Channel.Inputs[0] ? 0 : 1;
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
        bool isLast, Func<int, UIElement> makeCell)
    {
        var bottomBorder = isLast ? 0 : 1;

        // Label cell (col 0)
        var labelCell = new Border
        {
            BorderBrush = CellBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, bottomBorder),
            Padding = new Thickness(14, 12, 0, 12)
        };
        labelCell.Child = new TextBlock
        {
            Text = labelText,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 255, 255, 255)),
            CharacterSpacing = 80,
            VerticalAlignment = VerticalAlignment.Center
        };
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
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10,
            Margin = new Thickness(0, 18, 0, 18)
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
            Style = (Style)RootGrid.Resources["InlineTextBoxStyle"],
            Width = 60
        };
        gainText.PointerWheelChanged += (s, e) =>
        {
            e.Handled = true;
            int delta = e.GetCurrentPoint(gainText).Properties.MouseWheelDelta;
            float step = delta > 0 ? 0.5f : -0.5f;
            float current = _viewModel.GetMatrixGain(input, output);
            float newGain = Math.Clamp(current + step, -60f, 12f);
            bool enabled = _viewModel.GetMatrixRouting(input, output);
            bool inv = _viewModel.GetMatrixInvert(input, output);
            _viewModel.SetMatrixRoute(input, output, enabled, newGain, inv);
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
        var invColor = initInverted
            ? Color.FromArgb(175, 255, 255, 255)
            : Color.FromArgb(60, 200, 200, 220);
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
                AnimateInvColor(Color.FromArgb(60, 200, 200, 220), 200);
        };
        _routeInvButtons[(input, output)] = invText;
        panel.Children.Add(invText);

        _routeCells[(input, output)] = panel;
        return panel;
    }

    // Full-width section header bar spanning all columns
    private void AddSectionBar(Grid grid, int row, string text, int outputCount)
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

        var bar = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 36, 36)),
            BorderBrush = CellBorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(14, 8, 14, 8),
            Child = content
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
            circle.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 160, 160, 170));
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

        bool inverted = _viewModel.GetMatrixInvert(input, output);
        _routeInverted[(input, output)] = inverted;
        if (_routeInvButtons.TryGetValue((input, output), out var inv) &&
            inv.Foreground is SolidColorBrush invBrush)
        {
            invBrush.Color = inverted
                ? Color.FromArgb(175, 255, 255, 255)
                : Color.FromArgb(60, 200, 200, 220);
        }
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
        for (int input = 0; input < 2; input++)
        {
            if (_routeCells.TryGetValue((input, output), out var cell))
            {
                cell.Opacity = enabled ? 1.0 : 0.25;
                cell.IsHitTestVisible = enabled;
            }
        }

        // Refresh conflict styling on all enable buttons
        RefreshConflictStyling();
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
            var dialog = new ContentDialog
            {
                Title = "Output Conflict",
                Content = GetConflictMessage(o),
                PrimaryButtonText = "Proceed",
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

    private string GetConflictMessage(int outputIndex)
    {
        bool isPdm = outputIndex == _viewModel.PdmOutputIndex;
        bool isRp2040 = _viewModel.Platform == "RP2040";

        if (isPdm)
            return isRp2040
                ? "Enabling PDM will disable SPDIF 2. Do you wish to proceed?"
                : "Enabling PDM will disable SPDIF 2, 3 and 4. Do you wish to proceed?";
        else
            return isRp2040
                ? "Enabling SPDIF 2 will disable PDM. Do you wish to proceed?"
                : "Enabling SPDIF 2, 3 or 4 will disable PDM. Do you wish to proceed?";
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
        db == 0f ? "0 dB" : $"{db:+0.#;-0.#} dB";

    private static string FormatDelay(float ms) =>
        ms == 0f ? "0 ms" : $"{ms:0} ms";

    private static bool ParseGainText(string text, out float value)
    {
        var s = text.Replace("dB", "").Trim();
        return float.TryParse(s, out value);
    }

    private static bool ParseDelayText(string text, out float value)
    {
        var s = text.Replace("ms", "").Trim();
        return float.TryParse(s, out value);
    }
}
