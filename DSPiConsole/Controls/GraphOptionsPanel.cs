using DSPiConsole.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Globalization;

namespace DSPiConsole.Controls;

/// <summary>
/// The graph's options, opened from the gear that appears when the pointer is
/// over the graph, as on the macOS Console. A main page (Graph Setup, Pop Out
/// Graph) and a Graph Setup page holding the graph's scale, grids and curve
/// style: the same preferences as Settings › Graphing, adjustable while
/// looking at the graph they change. The spectrum display's options join the
/// main page with the analyser.
/// </summary>
public sealed class GraphOptionsPanel : UserControl
{
    private const double PanelWidth = 280;
    private readonly bool _inPopOutWindow;
    private readonly Action? _onPopOut;
    private readonly Grid _host = new() { Width = PanelWidth };
    private bool _building;

    /// <param name="inPopOutWindow">Offers the pop-out's Follow Channel
    /// Selection switch, which means nothing in the main window.</param>
    /// <param name="onPopOut">Null in the pop-out window, which has nowhere
    /// further to pop out to.</param>
    public GraphOptionsPanel(bool inPopOutWindow, Action? onPopOut)
    {
        _inPopOutWindow = inPopOutWindow;
        _onPopOut = onPopOut;
        Content = _host;
        // The panel takes focus when it swaps pages: a flyout closes when focus
        // leaves it, and swapping removes the button that was just clicked.
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        ShowMain();
    }

    private static AppSettings S => AppSettings.Instance;

    private static void Changed()
    {
        S.Save();
        S.NotifyChanged();
    }

    // ── Pages ───────────────────────────────────────────────────────────────

    private void ShowMain()
    {
        var page = new StackPanel { Padding = new Thickness(0, 4, 0, 4) };
        page.Children.Add(ActionRow("", "Graph Setup", chevron: true, ShowSetup));
        if (_onPopOut != null)
            page.Children.Add(ActionRow("", "Pop Out Graph", chevron: false, _onPopOut));
        Swap(page);
    }

    private void ShowSetup()
    {
        _building = true;
        var page = new StackPanel { Padding = new Thickness(0, 0, 0, 8) };

        // Header: Back on the left, the title centred.
        var header = new Grid { Height = 32, Padding = new Thickness(8, 0, 12, 0) };
        var back = new HyperlinkButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                Children = { new FontIcon { Glyph = "", FontSize = 10 }, new TextBlock { Text = "Back", FontSize = 12 } },
            },
            Padding = new Thickness(4, 2, 4, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        back.Click += (_, _) => ShowMain();
        header.Children.Add(new TextBlock
        {
            Text = "Graph Setup", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(back);
        page.Children.Add(header);
        page.Children.Add(Divider());

        // SCALE
        var reset = new HyperlinkButton { Content = "Reset", FontSize = 10, Padding = new Thickness(4, 0, 4, 0) };
        ToolTipService.SetToolTip(reset, "Restore the default frequency and dB range");
        reset.Click += (_, _) =>
        {
            S.GraphMinFrequency = 15;
            S.GraphMaxFrequency = 20000;
            S.GraphDbRange = 50;
            S.GraphDbCenter = 0;
            Changed();
            ShowSetup();
        };
        page.Children.Add(SectionHeader("SCALE", reset));
        var scale = new StackPanel { Spacing = 6, Padding = new Thickness(12, 0, 12, 0) };
        var freqRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        freqRow.Children.Add(RowLabel("Frequency"));
        freqRow.Children.Add(Picker(new[] { 10.0, 15, 20, 50, 100 }, f => $"{f:0} Hz", S.GraphMinFrequency,
            v => { S.GraphMinFrequency = v; Changed(); }));
        freqRow.Children.Add(new TextBlock { Text = "to", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
        freqRow.Children.Add(Picker(new[] { 5000.0, 10000, 20000 }, f => $"{f / 1000:0} kHz", S.GraphMaxFrequency,
            v => { S.GraphMaxFrequency = v; Changed(); }));
        scale.Children.Add(freqRow);
        scale.Children.Add(SliderRow("Range", 10, 100, 1, S.GraphDbRange, v => $"{v:0} dB",
            v => { S.GraphDbRange = Math.Round(v); Changed(); }));
        scale.Children.Add(SliderRow("Center", -40, 20, 1, S.GraphDbCenter,
            v => (v >= 0 ? "+" : "") + v.ToString("0", CultureInfo.InvariantCulture) + " dB",
            v => { S.GraphDbCenter = Math.Round(v); Changed(); }));
        page.Children.Add(scale);

        // GRID & LABELS
        page.Children.Add(SectionHeader("GRID & LABELS"));
        page.Children.Add(ToggleRow("Frequency Grid", S.ShowFrequencyGrid, b => S.ShowFrequencyGrid = b));
        page.Children.Add(ToggleRow("Frequency Labels", S.ShowFrequencyLabels, b => S.ShowFrequencyLabels = b));
        page.Children.Add(ToggleRow("dB Grid", S.ShowDbGrid, b => S.ShowDbGrid = b));
        page.Children.Add(ToggleRow("dB Labels", S.ShowDbLabels, b => S.ShowDbLabels = b));
        page.Children.Add(ToggleRow("Frequency Readout", S.ShowFrequencyReadout, b => S.ShowFrequencyReadout = b));
        page.Children.Add(ToggleRow("Gain Readout", S.ShowGainReadout, b => S.ShowGainReadout = b));
        var opacity = SliderRow("Grid Opacity", 0, 200, 1, S.GraphGridOpacity * 100, v => $"{v:0}%",
            v => { S.GraphGridOpacity = v / 100; Changed(); });
        opacity.Padding = new Thickness(12, 2, 12, 2);
        opacity.IsHitTestVisible = S.ShowFrequencyGrid || S.ShowDbGrid;
        opacity.Opacity = opacity.IsHitTestVisible ? 1 : 0.4;
        page.Children.Add(opacity);

        // CURVES
        page.Children.Add(SectionHeader("CURVES"));
        var width = SliderRow("Line Width", 1, 4, 0.5, S.GraphLineWidth,
            v => v.ToString("0.0", CultureInfo.InvariantCulture) + " pt",
            v => { S.GraphLineWidth = v; Changed(); });
        width.Padding = new Thickness(12, 0, 12, 2);
        page.Children.Add(width);
        page.Children.Add(ToggleRow("Glow", S.ShowGraphGlow, b => S.ShowGraphGlow = b));
        page.Children.Add(ToggleRow("Phase Response", S.ShowPhase, b => { S.ShowPhase = b; ShowSetupLater(); }));
        page.Children.Add(ToggleRow("Unwrap Phase", S.PhaseUnwrapped, b => S.PhaseUnwrapped = b, enabled: S.ShowPhase));
        if (_inPopOutWindow)
            page.Children.Add(ToggleRow("Follow Channel Selection", S.PopoutFollowsSelectedChannel,
                b => S.PopoutFollowsSelectedChannel = b));

        Swap(page);
        _building = false;
    }

    /// <summary>Rebuilds the setup page after the current event, so a switch
    /// that enables another (Phase → Unwrap) is not torn down mid-toggle.</summary>
    private void ShowSetupLater() => DispatcherQueue.TryEnqueue(ShowSetup);

    private void Swap(UIElement page)
    {
        bool hadFocus = _host.Children.Count > 0;
        if (hadFocus) Focus(FocusState.Programmatic);
        _host.Children.Clear();
        _host.Children.Add(page);
    }

    // ── Rows ────────────────────────────────────────────────────────────────

    private static Border Divider() => new()
    {
        Height = 1,
        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
    };

    private static Grid SectionHeader(string title, UIElement? trailing = null)
    {
        var g = new Grid { Padding = new Thickness(12, 10, 8, 4) };
        g.Children.Add(new TextBlock
        {
            Text = title, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (trailing is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Right;
            fe.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(fe);
        }
        return g;
    }

    private static TextBlock RowLabel(string text) => new()
    {
        Text = text, FontSize = 12, Width = 72, VerticalAlignment = VerticalAlignment.Center,
    };

    private static Button ActionRow(string glyph, string title, bool chevron, Action action)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        var label = new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (chevron)
        {
            var c = new FontIcon { Glyph = "", FontSize = 10, Opacity = 0.6 };
            Grid.SetColumn(c, 2);
            grid.Children.Add(c);
        }
        var b = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(4, 0, 4, 0),
            MinHeight = 0,
        };
        b.Click += (_, _) => action();
        return b;
    }

    private Grid ToggleRow(string title, bool value, Action<bool> set, bool enabled = true)
    {
        var g = new Grid { Padding = new Thickness(12, 0, 10, 0), Height = 30 };
        g.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Opacity = enabled ? 1 : 0.45,
        });
        var t = new ToggleSwitch
        {
            IsOn = value, OnContent = "", OffContent = "", MinWidth = 0, Width = 44,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = enabled,
        };
        t.Toggled += (_, _) =>
        {
            if (_building) return;
            set(t.IsOn);
            Changed();
        };
        g.Children.Add(t);
        return g;
    }

    private Grid SliderRow(string title, double min, double max, double step, double value,
                           Func<double, string> format, Action<double> set)
    {
        var g = new Grid { ColumnSpacing = 6 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        g.Children.Add(RowLabel(title));
        var readout = new TextBlock
        {
            Text = format(value), FontSize = 11, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, StepFrequency = step, Value = value,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -6, 0, -6),
        };
        slider.ValueChanged += (_, e) =>
        {
            readout.Text = format(e.NewValue);
            if (!_building) set(e.NewValue);
        };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(readout, 2);
        g.Children.Add(slider);
        g.Children.Add(readout);
        return g;
    }

    private ComboBox Picker(double[] values, Func<double, string> label, double current, Action<double> set)
    {
        var box = new ComboBox { Width = 86, FontSize = 12, MinHeight = 0, Padding = new Thickness(8, 2, 0, 2) };
        int selected = 0;
        for (int i = 0; i < values.Length; i++)
        {
            box.Items.Add(new ComboBoxItem { Content = label(values[i]), Tag = values[i] });
            if (Math.Abs(values[i] - current) < 0.5) selected = i;
        }
        box.SelectedIndex = selected;
        box.SelectionChanged += (_, _) =>
        {
            if (_building || box.SelectedItem is not ComboBoxItem { Tag: double v }) return;
            set(v);
        };
        return box;
    }
}
