using DSPiConsole.Core.Models;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinRT.Interop;
using System.Runtime.InteropServices;

namespace DSPiConsole;

public sealed partial class GraphWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly MainViewModel _viewModel;

    public GraphWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        BodePlot.DataContext = _viewModel;
        BodePlot.SetIsPopout(true);
        Closed += (_, _) => BodePlot.ReleaseSpectrum();

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        double dpiScale = GetDpiForWindow(hWnd) / 96.0;
        appWindow?.Resize(new Windows.Graphics.SizeInt32((int)(800 * dpiScale), (int)(500 * dpiScale)));
        appWindow!.Title = "Filter Response";

        if (appWindow.TitleBar is { } titleBar)
        {
            titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
            titleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 140, 140);
            titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
            titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 140, 140);
            titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 50, 50, 50);
        }

        _viewModel.VisibilityChanged += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(UpdateLegend);
        };

        _viewModel.ActiveOutputsChanged += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(InitializeLegend);
        };

        RootGrid.Loaded += (_, _) => InitializeLegend();
    }

    public void SetSelectedChannel(int channelId) => BodePlot.SetSelectedChannel(channelId);

    public void SetEditingSuspended(bool suspended) => BodePlot.SetEditingSuspended(suspended);

    /// <summary>The graph options are open: the gear stays shown, brighter,
    /// even after the pointer leaves the graph for the panel.</summary>
    private bool _graphOptionsOpen;
    private bool _pointerOverGraph;

    private void OnGraphAreaPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerOverGraph = true;
        if (!_graphOptionsOpen) GraphOptionsButton.Opacity = 0.6;
    }

    private void OnGraphAreaPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerOverGraph = false;
        if (!_graphOptionsOpen) GraphOptionsButton.Opacity = 0;
    }

    /// <summary>The pop-out's gear: Graph Setup, with Follow Channel Selection.</summary>
    private void OnGraphOptionsClick(object sender, RoutedEventArgs e)
    {
        var flyout = new Flyout
        {
            FlyoutPresenterStyle = MainWindow.GraphOptionsPresenterStyle(),
            Content = new Controls.GraphOptionsPanel(inPopOutWindow: true, onPopOut: null, _viewModel),
        };
        flyout.Opened += (_, _) =>
        {
            _graphOptionsOpen = true;
            GraphOptionsButton.Opacity = 0.95;
        };
        flyout.Closed += (_, _) =>
        {
            _graphOptionsOpen = false;
            GraphOptionsButton.Opacity = _pointerOverGraph ? 0.6 : 0;
        };
        Controls.GraphOptionsPlacement.ShowAt(flyout, GraphOptionsButton, this);
    }

    public void SetIgnoreVisibility(bool ignore)
    {
        BodePlot.SetIgnoreVisibility(ignore, _viewModel);
        UpdateLegend();
    }

    private void InitializeLegend()
    {
        LegendPanel.Children.Clear();

        foreach (var channel in _viewModel.ActiveInputs)
            AddLegendButton(channel);

        for (int o = 0; o < _viewModel.ActiveOutputs.Count; o++)
        {
            if (!_viewModel.IsOutputEnabled(o)) continue;
            AddLegendButton(_viewModel.ActiveOutputs[o]);
        }

        UpdateLegend();
    }

    private void AddLegendButton(Channel channel)
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
            Text = channel.Descriptor,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold
        };

        panel.Children.Add(indicator);
        panel.Children.Add(label);
        btn.Content = panel;

        btn.Click += (s, _) =>
        {
            if (s is Button b && b.Tag is Channel ch)
            {
                if (!AppSettings.Instance.PopoutFollowsSelectedChannel)
                {
                    BodePlot.ToggleLocalVisibility((int)ch.Id);
                    UpdateLegend();
                }
                else
                {
                    _viewModel.ToggleChannelVisibility(ch);
                }
            }
        };

        LegendPanel.Children.Add(btn);
    }

    private void UpdateLegend()
    {
        foreach (var child in LegendPanel.Children)
        {
            if (child is not Button btn || btn.Tag is not Channel channel) continue;

            bool isVisible = AppSettings.Instance.PopoutFollowsSelectedChannel
                ? _viewModel.GetChannelVisibility(channel)
                : BodePlot.GetLocalVisibility((int)channel.Id);
            if (btn.Content is StackPanel panel)
            {
                if (panel.Children[0] is Ellipse ellipse)
                {
                    ellipse.Fill = new SolidColorBrush(isVisible ? channel.Color : Colors.Gray);
                    ellipse.Opacity = isVisible ? 1.0 : 0.5;
                }

                if (panel.Children[1] is TextBlock text)
                    text.Opacity = isVisible ? 1.0 : 0.5;
            }

            btn.Background = new SolidColorBrush(
                isVisible ? Windows.UI.Color.FromArgb(38, channel.Color.R, channel.Color.G, channel.Color.B) : Colors.Transparent);
        }
    }
}
