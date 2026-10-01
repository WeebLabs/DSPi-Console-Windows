using System.Diagnostics;
using System.Numerics;
using DSPiConsole.Core.Rta;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DSPiConsole.Controls.Rta;

/// <summary>
/// One channel's third-octave bars. Bands are equally spaced on a log axis by
/// construction, so they are drawn as equal-width bars, labelled from the
/// device's own centre table so picture and labels cannot disagree. Draws only;
/// the host subscribes to the engine. Port of the macOS Console's RtaBandsView.
/// </summary>
public sealed class RtaBandsView : UserControl
{
    private static readonly double[] LabelledCentres = { 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
    private static readonly Color Secondary = Color.FromArgb(255, 200, 200, 205);
    private const float LabelStrip = 12;

    private readonly MainViewModel _vm;
    private readonly CanvasControl _canvas = new() { IsHitTestVisible = false };
    private readonly RtaRenderCache _cache = new();
    private readonly RtaBarSmoother _bars = new(), _caps = new();
    private readonly byte _tap;
    private readonly int _channel;
    private readonly Color _color;
    private readonly bool _showLabels, _showLevelLabels;
    private bool _active, _rendering;
    private long _drawnVersion = -1;

    public RtaBandsView(MainViewModel vm, byte tap, int channel, Color color, bool showLabels = true, bool showLevelLabels = true)
    {
        _vm = vm;
        _tap = tap;
        _channel = channel;
        _color = color;
        _showLabels = showLabels;
        _showLevelLabels = showLevelLabels;
        IsHitTestVisible = false;
        Content = _canvas;
        _canvas.Draw += (_, e) => Draw(e.DrawingSession, (float)_canvas.ActualWidth, (float)_canvas.ActualHeight);
        Loaded += (_, _) => SetRendering(_active);
        Unloaded += (_, _) => SetRendering(false);
    }

    /// <summary>Whether the host is on screen; the frame loop runs only then.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            SetRendering(value && IsLoaded);
        }
    }

    public void Dispose()
    {
        SetRendering(false);
        _canvas.RemoveFromVisualTree();
    }

    private void SetRendering(bool on)
    {
        if (_rendering == on) return;
        _rendering = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
        _canvas.Invalidate();
    }

    private void OnRendering(object? sender, object e)
    {
        if (_vm.Rta.FrameVersion != _drawnVersion || AppSettings.Instance.RtaSmoothing > 0)
            _canvas.Invalidate();
    }

    private void Draw(CanvasDrawingSession ds, float w, float h)
    {
        _drawnVersion = _vm.Rta.FrameVersion;
        var configuration = RtaDisplayConfiguration.From(_vm.Rta);
        var scale = _vm.RtaScale;
        var settings = AppSettings.Instance;
        bool showPeak = settings.RtaShowPeakHold;
        float labelHeight = _showLabels ? LabelStrip : 0;
        var plot = new RtaPlot(0, 0, w, Math.Max(0, h - labelHeight));
        int count = configuration.BarCount;
        if (plot.Height <= 2 || count <= 0) return;
        var visible = _cache.VisibleBands(configuration, count);
        if (visible.Length == 0) return;

        if (_showLabels)
        {
            DrawGrid(ds, plot, scale);
            DrawFrequencyLabels(ds, plot, configuration.Centres, visible, plot.Width / visible.Length, h - 11);
        }

        // Levels in dBFS per slot; no frame reads as silence, so the bars rise
        // into view rather than appearing at full height.
        var frame = _vm.Rta.Snapshot.Frame(_channel, _tap);
        var avg = new double[count];
        var peak = new double[count];
        for (int i = 0; i < count; i++)
        {
            avg[i] = frame != null && i < frame.Avg.Length ? configuration.LevelDb(frame.Avg[i]) : scale.FloorDb;
            peak[i] = frame != null && i < frame.Peak.Length ? configuration.LevelDb(frame.Peak[i]) : scale.FloorDb;
        }
        double tau = RtaMath.FallTau(_vm.Rta.ChannelRefreshInterval, settings.RtaSmoothing);
        if (tau > 0)
        {
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            // A channel's numbers snap rather than slide over from another's.
            long identity = (long)_tap << 24 | (long)_channel << 8 | (long)count;
            avg = _bars.Step(now, avg, identity, tau * 0.4, tau);
            // A peak cap that eased upward would stop being a peak; only its
            // fall is interpolated.
            peak = _caps.Step(now, peak, identity, 0, tau);
        }

        float slot = plot.Width / visible.Length;
        float gap = Math.Min(2f, Math.Max(0.5f, slot * 0.18f));
        float barWidth = Math.Max(1, slot - gap);
        float radius = Math.Min(1.5f, barWidth / 3);
        using var fill = new CanvasLinearGradientBrush(ds, With(_color, 0.95), With(_color, 0.45));
        var capColor = With(_color, 0.9);
        for (int pos = 0; pos < visible.Length; pos++)
        {
            int i = visible[pos];
            if (i >= avg.Length) continue;
            float x = plot.X + pos * slot + gap / 2;
            double level = scale.Norm(avg[i]);
            if (level > 0.001)
            {
                float bh = plot.Height * (float)level, top = plot.Bottom - bh;
                fill.StartPoint = new Vector2(0, top);
                fill.EndPoint = new Vector2(0, plot.Bottom);
                ds.FillRoundedRectangle(new Rect(x, top, barWidth, bh), radius, radius, fill);
            }
            if (showPeak)
            {
                double p = scale.Norm(peak[i]);
                if (p > 0.001)
                {
                    float y = plot.Bottom - plot.Height * (float)p;
                    ds.FillRectangle(new Rect(x, Math.Max(plot.Y, y - 1), barWidth, 1.5), capColor);
                }
            }
        }
    }

    internal static Color With(Color c, double opacity) => Color.FromArgb((byte)Math.Clamp(opacity * 255, 0, 255), c.R, c.G, c.B);

    internal static CanvasTextFormat LabelFormat(CanvasHorizontalAlignment align) => new()
    {
        FontSize = 8,
        FontFamily = "Cascadia Code",
        HorizontalAlignment = align,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    internal static string ShortHz(double hz) => hz >= 1000 ? $"{Math.Round(hz / 1000):0}k" : $"{Math.Round(hz):0}";

    /// <summary>A line every 12 dB: close enough to read a level off, sparse
    /// enough that the data stays the loudest thing in the picture.</summary>
    internal static void DrawLevelGrid(CanvasDrawingSession ds, RtaPlot plot, RtaScale scale, bool labels)
    {
        using var format = LabelFormat(CanvasHorizontalAlignment.Right);
        double db = Math.Floor(scale.CeilingDb / 12) * 12;
        while (db > scale.FloorDb)
        {
            float y = plot.Bottom - plot.Height * (float)scale.Norm(db);
            ds.DrawLine(plot.X, y, plot.Right, y, With(Secondary, db == 0 ? 0.35 : 0.12), db == 0 ? 1 : 0.5f);
            if (labels)
                ds.DrawText($"{db:0}", new Rect(plot.Right - 42, y - 12, 40, 11), With(Secondary, 0.6), format);
            db -= 12;
        }
    }

    private void DrawGrid(CanvasDrawingSession ds, RtaPlot plot, RtaScale scale) => DrawLevelGrid(ds, plot, scale, _showLevelLabels);

    /// <summary>A narrow cell cannot fit every label: decades first, then the
    /// 2s and 5s wherever they clear what is placed and the cell's edges, so a
    /// crowded axis thins out instead of overprinting.</summary>
    private static void DrawFrequencyLabels(CanvasDrawingSession ds, RtaPlot plot, IReadOnlyList<double> centres, int[] bands, float slot, float labelY)
    {
        if (centres.Count == 0) return;
        var candidates = new List<(double Hz, float X, string Text)>();
        for (int pos = 0; pos < bands.Length; pos++)
        {
            int i = bands[pos];
            if (i >= centres.Count) continue;
            double hz = centres[i];
            // The table rounds nominal centres to whole hertz (31.5 arrives as
            // 31 or 32), so match on proportion.
            double nominal = LabelledCentres.FirstOrDefault(c => Math.Abs(hz - c) < c * 0.03);
            if (nominal == 0) continue;
            candidates.Add((nominal, plot.X + (pos + 0.5f) * slot, ShortHz(hz)));
        }
        const float charWidth = 4.9f, gap = 4;
        var decades = new HashSet<double> { 10, 100, 1000, 10000 };
        var placed = new List<(float Lo, float Hi)>();
        using var format = LabelFormat(CanvasHorizontalAlignment.Center);
        foreach (var label in candidates.Where(c => decades.Contains(c.Hz)).Concat(candidates.Where(c => !decades.Contains(c.Hz))))
        {
            float half = label.Text.Length * charWidth / 2, lo = label.X - half, hi = label.X + half;
            if (lo < plot.X || hi > plot.Right || placed.Any(p => lo < p.Hi + gap && hi > p.Lo - gap)) continue;
            placed.Add((lo, hi));
            ds.DrawText(label.Text, new Rect(label.X - 20, labelY, 40, 11), Secondary, format);
        }
    }

    /// <summary>The dB lines and the 1-2-5 frequency lines on a log axis,
    /// behind the analyser window's curves.</summary>
    internal static void DrawLogGrid(CanvasDrawingSession ds, RtaPlot plot, RtaScale scale, double minHz, double maxHz, float labelY)
    {
        DrawLevelGrid(ds, plot, scale, labels: true);
        if (maxHz <= minHz) return;
        double lo = Math.Log10(minHz), hi = Math.Log10(maxHz);
        using var format = LabelFormat(CanvasHorizontalAlignment.Center);
        foreach (double hz in LabelledCentres)
        {
            if (hz < minHz || hz > maxHz) continue;
            float x = plot.X + (float)((Math.Log10(hz) - lo) / (hi - lo)) * plot.Width;
            ds.DrawLine(x, plot.Y, x, plot.Bottom, With(Secondary, 0.10), 0.5f);
            ds.DrawText(ShortHz(hz), new Rect(x - 20, labelY, 40, 11), Secondary, format);
        }
    }
}
