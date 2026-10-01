#if DEBUG
using DSPiConsole.Controls.GraphEditing;
using DSPiConsole.Core.GraphEditing;
using DSPiConsole.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DSPiConsole.Debugging;

/// <summary>
/// Debug builds only: the on-graph editor over an in-memory channel, so it can
/// be seen and driven without a device. Opened at startup when the environment
/// variable DSPI_EDITOR_HARNESS is set. Not part of the app's behaviour.
/// </summary>
public sealed class GraphEditorHarness : Window, IPeqEditorHost
{
    private readonly List<FilterParams> _bands;
    private float _offsetDb;
    private readonly PeqGraphEditorView _view;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statsTimer;
    private readonly TextBlock _log = new() { FontSize = 11, Foreground = new SolidColorBrush(Colors.Gray), TextWrapping = TextWrapping.Wrap };

    public PeqGraphSelection Selection { get; } = new();

    public GraphEditorHarness()
    {
        Title = "Graph Editor Harness";
        _bands = Enumerable.Range(0, 10).Select(_ => new FilterParams()).ToList();
        _bands[0] = new FilterParams(FilterType.HighPass, 40, 0.707f, 0);
        _bands[1] = new FilterParams(FilterType.LowShelf, 120, 0.707f, 4);
        _bands[2] = new FilterParams(FilterType.Peaking, 1000, 1.4f, -6);
        _bands[3] = new FilterParams(FilterType.Peaking, 3500, 3, 5) { Bypass = true };
        _bands[4] = new FilterParams(FilterType.Notch, 6000, 4, 0);
        _bands[5] = new FilterParams(FilterType.HighShelf1, 9000, 0.707f, -3);

        _view = new PeqGraphEditorView(this) { Width = 860, Height = 300 };
        var card = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 38, 38, 38)),
            CornerRadius = new CornerRadius(8),
            Child = _view,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var root = new StackPanel { Padding = new Thickness(20), Spacing = 12, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)) };
        root.Children.Add(card);
        var options = new Button { Content = "Graph options" };
        options.Click += (_, _) => Controls.GraphOptionsPlacement.ShowAt(new Flyout
        {
            FlyoutPresenterStyle = MainWindow.GraphOptionsPresenterStyle(),
            Content = new Controls.GraphOptionsPanel(inPopOutWindow: true, onPopOut: () => Log("pop out")),
        }, options, this);
        root.Children.Add(options);
        // The channel's gain or preamp as the graph shows it.
        var offset = new Slider { Header = "Level offset (dB)", Minimum = -20, Maximum = 12, StepFrequency = 0.5, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        offset.ValueChanged += (_, e) => { _offsetDb = (float)e.NewValue; Push(); };
        root.Children.Add(offset);
        root.Children.Add(_log);
        Content = root;
        root.Loaded += (_, _) => Push();
        var stats = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Colors.LightGray) };
        root.Children.Insert(2, stats);
        long lastCount = 0, lastTicks = 0;
        var statsTimer = _statsTimer = DispatcherQueue.CreateTimer();
        statsTimer.Interval = TimeSpan.FromSeconds(1);
        statsTimer.Tick += (_, _) =>
        {
            long n = _view.DrawCount - lastCount, t = _view.DrawTicks - lastTicks;
            lastCount = _view.DrawCount; lastTicks = _view.DrawTicks;
            if (n > 0) stats.Text = $"{n} draws/s, {t * 1000.0 / System.Diagnostics.Stopwatch.Frequency / n:0.00} ms each";
        };
        statsTimer.Start();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(920, 600));
    }

    private void Push() => _view.Apply(new PeqEditorConfig
    {
        Channel = 0,
        Bands = _bands.Select(b => b.Clone()).ToList(),
        OffsetDb = _offsetDb,
        CurveColor = (0.36f, 0.62f, 0.98f),
        LineWidth = 2,
        Glow = true,
        MinFreq = 15,
        MaxFreq = 20000,
        DbTop = 25,
        DbBottom = -25,
        AvailableTypes = FilterTypeExtensions.PeqTypes.Where(t => t != FilterType.Flat && t != FilterType.LinkwitzTransform).ToHashSet(),
        BypassSupported = true,
    });

    private void Log(string text) => _log.Text = text + "\n" + string.Join("\n", _log.Text.Split('\n').Take(6));

    public void CommitGraphBands(int channel, IReadOnlyList<(int Band, FilterParams Params)> changes)
    {
        foreach (var (b, p) in changes) _bands[b] = p.Clone();
        Log("commit " + string.Join(", ", changes.Select(c => $"#{c.Band + 1} {c.Params.Type} {c.Params.Frequency:0} Hz {c.Params.Gain:0.00} dB Q{c.Params.Q:0.000}")));
        Push();
    }

    public void SendGraphBandsToDevice(int channel, IReadOnlyList<(int Band, FilterParams Params)> changes) { }

    public void SetGraphBandBypass(int channel, IReadOnlyList<int> bands, bool bypass)
    {
        foreach (int b in bands) _bands[b].Bypass = bypass;
        Log($"bypass {string.Join(",", bands.Select(b => b + 1))} = {bypass}");
        Push();
    }

    public void ShowLive(int channel, int band, FilterParams p) { }
    public void EndLive(int channel) { }
}
#endif
