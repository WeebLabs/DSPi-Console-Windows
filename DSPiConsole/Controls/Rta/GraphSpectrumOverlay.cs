using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using DSPiConsole.Core.Rta;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Controls.Rta;

/// <summary>
/// The live spectrum drawn inside a frequency graph rather than in a strip of
/// its own. The two pictures share only the frequency axis: the response curves
/// are relative dB, the spectrum absolute dBFS, so it maps the analyser's floor
/// and ceiling onto the plot and is drawn as a translucent fill under the
/// curves. Port of the macOS Console's GraphSpectrumOverlay.
/// <para>
/// It shows the open page's selection. One channel gets the bass bands and the
/// FFT bins above them; several get band curves only, since the device keeps
/// just the last transformed channel's bins. It watches the engine while it is
/// loaded, <see cref="Active"/>, and has something to show, and the last view
/// to stop watching stops the device's analyser.
/// </para>
/// </summary>
public sealed class GraphSpectrumOverlay : UserControl
{
    private readonly MainViewModel _vm;
    /// <summary>The response graph shows the spectrum only when the page's
    /// graph preference is on; the analyser window shows it regardless.</summary>
    private readonly bool _followsGraphPreference;
    private readonly CanvasControl _canvas = new() { IsHitTestVisible = false };
    private readonly RtaCurveSmoothing _smoothing = new();
    private Plan? _plan;
    private Guid? _token;
    private bool _loaded, _active = true, _rendering, _disposed;
    private long _drawnVersion = -1;
    private RtaDisplayConfiguration? _configuration;
    private IReadOnlySet<int> _hidden = new HashSet<int>();
    private double _minFreq = 20, _maxFreq = 20000;

    private sealed record Plan(byte Tap, (int Channel, Color Color)[] Channels, bool WantsBins, RtaRequest Request);

    public GraphSpectrumOverlay(MainViewModel vm, bool followsGraphPreference)
    {
        _vm = vm;
        _followsGraphPreference = followsGraphPreference;
        IsHitTestVisible = false;
        Content = _canvas;
        _canvas.Draw += (_, e) => Draw(e.DrawingSession);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        if (_loaded || _disposed) return;
        _loaded = true;
        _vm.RtaSelectionChanged += OnInputsChanged;
        _vm.RtaStateChanged += OnEngineStateChanged;
        _vm.ActiveOutputsChanged += OnInputsChanged;
        _vm.OutputEnabledChanged += OnOutputEnabledChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        AppSettings.Instance.SettingsChanged += OnInputsChanged;
        Rebuild();
    }

    /// <summary>Unhooks everything and releases the subscription. Unloaded
    /// is not reliably raised when a window closes, so a closing host calls
    /// this itself through <see cref="Dispose"/>.</summary>
    private void Detach()
    {
        if (!_loaded) return;
        _loaded = false;
        _vm.RtaSelectionChanged -= OnInputsChanged;
        _vm.RtaStateChanged -= OnEngineStateChanged;
        _vm.ActiveOutputsChanged -= OnInputsChanged;
        _vm.OutputEnabledChanged -= OnOutputEnabledChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        AppSettings.Instance.SettingsChanged -= OnInputsChanged;
        Rebuild();
    }

    /// <summary>False while the host is not on screen (a closed or minimised
    /// window): the subscription is dropped so the device can stop.</summary>
    public bool Active
    {
        get => _active;
        set { if (_active != value) { _active = value; Rebuild(); } }
    }

    /// <summary>Channels at the selection's tap left out of the drawing only:
    /// the subscription is always the whole selection, so hiding a channel
    /// here never narrows what the engine analyses for anyone else.</summary>
    public IReadOnlySet<int> HiddenChannels
    {
        get => _hidden;
        set { _hidden = value; Rebuild(); }
    }

    /// <summary>The frequency axis, matching the graph it sits in.</summary>
    public void SetAxis(double minFreq, double maxFreq)
    {
        if (minFreq == _minFreq && maxFreq == _maxFreq) return;
        (_minFreq, _maxFreq) = (minFreq, maxFreq);
        _canvas.Invalidate();
    }

    /// <summary>Releases the subscription and the canvas for good; call when
    /// the host window closes.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _disposed = true;
        _canvas.RemoveFromVisualTree();
    }

    // ── What to ask the device for ──

    private void OnInputsChanged(object? sender, EventArgs e) => Rebuild();

    /// <summary>A disabled output leaves the selection and the rotation.</summary>
    private void OnOutputEnabledChanged(int output, bool enabled) => DispatcherQueue.TryEnqueue(Rebuild);

    private void OnEngineStateChanged(object? sender, EventArgs e)
    {
        _configuration = null;
        Rebuild();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsDeviceConnected) or nameof(MainViewModel.ActiveInputSource)
            or nameof(MainViewModel.UsbInputChannelCount) or nameof(MainViewModel.ActiveInputChannelCount))
            DispatcherQueue.TryEnqueue(Rebuild);
    }

    /// <summary>Works out the plan from the page's selection, then subscribes,
    /// updates or releases to match it.</summary>
    private void Rebuild()
    {
        _plan = _disposed ? null : BuildPlan();
        bool watch = _loaded && _active && _plan != null;
        if (watch)
        {
            if (_token is { } t) _vm.Rta.Update(t, _plan!.Request);
            else
            {
                // A view resuming also refreshes the options it asks for.
                _vm.Rta.SetOptions(MainViewModel.RtaOptionsFromSettings);
                _token = _vm.Rta.Subscribe(_plan!.Request);
            }
        }
        else if (_token is { } t)
        {
            _vm.Rta.Release(t);
            _token = null;
        }
        SetRendering(watch);
        _drawnVersion = -1;
        _canvas.Invalidate();
    }

    private Plan? BuildPlan()
    {
        if (!_vm.IsDeviceConnected || !_vm.Rta.Supported) return null;
        if (_followsGraphPreference && !AppSettings.Instance.RtaShows(bars: false, _vm.RtaOnDashboard)) return null;
        var selection = _vm.RtaSelection;
        if (selection.IsEmpty) return null;
        var channels = selection.Channels.Where(c => !_hidden.Contains(c))
            .Select(c => (c, _vm.RtaChannelColor(selection.Tap, c))).ToArray();
        if (channels.Length == 0) return null;
        // The fine curve needs the whole selection to be one channel: one left
        // visible out of several is still rotating with the rest.
        bool wantsBins = selection.Channels.Count == 1;
        return new Plan(selection.Tap, channels, wantsBins, new RtaRequest(selection.Tap, selection.Mask, wantsBins));
    }

    // ── Frame loop ──

    private void SetRendering(bool on)
    {
        if (_rendering == on) return;
        _rendering = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>Redraws for a new frame, and every display frame while the
    /// smoothing is gliding between device frames.</summary>
    private void OnRendering(object? sender, object e)
    {
        if (_vm.Rta.FrameVersion != _drawnVersion || AppSettings.Instance.RtaSmoothing > 0)
            _canvas.Invalidate();
    }

    // ── Drawing ──

    private sealed class Trace
    {
        public Color Color;
        public CanvasGeometry? Curve, Fill, Peak;
        public float Start = float.PositiveInfinity;
    }

    private void Draw(CanvasDrawingSession ds)
    {
        _drawnVersion = _vm.Rta.FrameVersion;
        var plan = _plan;
        float w = (float)_canvas.ActualWidth, h = (float)_canvas.ActualHeight;
        if (plan == null || w <= 4 || h <= 4) return;
        var snapshot = _vm.Rta.Snapshot;
        if (snapshot.Tap != plan.Tap) return;
        var configuration = _configuration ??= RtaDisplayConfiguration.From(_vm.Rta);
        if (configuration.Tap != plan.Tap && configuration.BandCount > 0) return;

        var settings = AppSettings.Instance;
        double tau = RtaMath.FallTau(_vm.Rta.ChannelRefreshInterval, settings.RtaSmoothing);
        double? now = tau > 0 ? Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency : null;
        var plot = new RtaPlot(0, 0, w, h);
        var builder = new RtaCurveBuilder(configuration, _smoothing, _minFreq, _maxFreq, plot, _vm.RtaScale, now, tau);

        var traces = new List<Trace>();
        foreach (var (channel, color) in plan.Channels)
        {
            var band = snapshot.Frame(channel, plan.Tap);
            var bands = band is { HasData: true } ? builder.BandPoints(band, peak: false, channel) : new List<Vector2>();
            var curve = bands;
            bool dense = false;
            if (plan.WantsBins)
            {
                var bins = snapshot.Bins is { } b && b.Channel == channel ? builder.BinPoints(b, channel) : new List<Vector2>();
                curve = builder.Blend(bands, bins);
                dense = bins.Count > 1;
            }
            var trace = new Trace { Color = color };
            if (curve.Count > 1)
            {
                // The blend is already a point per column: smoothing it only
                // overshoots around a tone. The thirty-odd band points need it.
                trace.Curve = dense ? Polyline(ds, curve) : SmoothPath(ds, curve, plot, close: false);
                trace.Fill = dense ? Polyline(ds, curve, plot.Bottom) : SmoothPath(ds, curve, plot, close: true);
                trace.Start = curve[0].X;
            }
            // Peak hold rides on top as a thin contour, from the bands.
            if (settings.RtaShowPeakHold && band is { HasData: true })
            {
                var peak = builder.BandPoints(band, peak: true, channel);
                if (peak.Count > 1)
                {
                    trace.Peak = SmoothPath(ds, peak, plot, close: false);
                    trace.Start = Math.Min(trace.Start, peak[0].X);
                }
            }
            if (trace.Curve != null || trace.Peak != null) traces.Add(trace);
        }
        DrawTraces(ds, traces, plot, Math.Clamp(settings.RtaGraphOpacity, 0, 1), settings.ShowGraphGlow);
        foreach (var t in traces) { t.Curve?.Dispose(); t.Fill?.Dispose(); t.Peak?.Dispose(); }
    }

    /// <summary>Fill, glow, edge and peak contour for every channel. Channels
    /// whose data starts at the same place share one fade layer and one blur;
    /// within a group every fill goes down before any line, so no channel's
    /// fill washes over another's edge.</summary>
    private static void DrawTraces(CanvasDrawingSession ds, List<Trace> traces, RtaPlot plot, double opacity, bool glow)
    {
        foreach (var group in traces.GroupBy(t => (int)Math.Round(t.Start)).OrderBy(g => g.Key))
        {
            float x0 = group.Min(t => t.Start);
            // The spectrum starts where the transform resolves; a fade over 30
            // px hides the hard wall there without inventing a roll-off.
            using var mask = x0 > plot.X + 1
                ? new CanvasLinearGradientBrush(ds, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(255, 0, 0, 0))
                { StartPoint = new Vector2(x0, 0), EndPoint = new Vector2(x0 + 30, 0) }
                : null;
            using var layer = mask != null ? ds.CreateLayer(mask) : null;

            foreach (var t in group)
            {
                if (t.Fill == null) continue;
                using var fill = new CanvasLinearGradientBrush(ds, With(t.Color, 0.34 * opacity), With(t.Color, 0.02 * opacity))
                { StartPoint = new Vector2(0, plot.Y), EndPoint = new Vector2(0, plot.Bottom) };
                ds.FillGeometry(t.Fill, fill);
            }

            if (glow)
            {
                // One blur for the whole group rather than one per stroke.
                using var strokes = new CanvasCommandList(ds);
                using (var cl = strokes.CreateDrawingSession())
                    foreach (var t in group)
                        if (t.Curve != null) cl.DrawGeometry(t.Curve, With(t.Color, 0.35 * opacity), 2);
                using var blur = new GaussianBlurEffect { Source = strokes, BlurAmount = 3, BorderMode = EffectBorderMode.Soft };
                ds.DrawImage(blur);
            }

            using var round = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
            foreach (var t in group)
            {
                if (t.Curve != null) ds.DrawGeometry(t.Curve, With(t.Color, 0.55 * opacity), 1, round);
                if (t.Peak != null) ds.DrawGeometry(t.Peak, With(t.Color, 0.35 * opacity), 1, round);
            }
        }
    }

    private static Color With(Color c, double opacity) => Color.FromArgb((byte)Math.Clamp(opacity * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>Straight segments through a series that already has a point
    /// per column; closed down to <paramref name="closeAt"/> for a fill.</summary>
    private static CanvasGeometry Polyline(ICanvasResourceCreator rc, List<Vector2> points, float? closeAt = null)
    {
        using var pb = new CanvasPathBuilder(rc);
        pb.BeginFigure(points[0]);
        for (int i = 1; i < points.Count; i++) pb.AddLine(points[i]);
        if (closeAt is { } y)
        {
            pb.AddLine(points[^1].X, y);
            pb.AddLine(points[0].X, y);
            pb.EndFigure(CanvasFigureLoop.Closed);
        }
        else pb.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(pb);
    }

    /// <summary>A Catmull-Rom curve through the points as cubic Béziers, its
    /// control points clamped into the plot so the overshoot cannot dive below
    /// the baseline the fill closes along.</summary>
    private static CanvasGeometry SmoothPath(ICanvasResourceCreator rc, List<Vector2> points, RtaPlot plot, bool close)
    {
        float ClampY(float y) => Math.Clamp(y, plot.Y, plot.Bottom);
        using var pb = new CanvasPathBuilder(rc);
        pb.BeginFigure(points[0]);
        for (int i = 0; i < points.Count - 1; i++)
        {
            var p0 = i > 0 ? points[i - 1] : points[i];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = i + 2 < points.Count ? points[i + 2] : p2;
            var c1 = new Vector2(p1.X + (p2.X - p0.X) / 6, ClampY(p1.Y + (p2.Y - p0.Y) / 6));
            var c2 = new Vector2(p2.X - (p3.X - p1.X) / 6, ClampY(p2.Y - (p3.Y - p1.Y) / 6));
            pb.AddCubicBezier(c1, c2, p2);
        }
        if (close)
        {
            pb.AddLine(points[^1].X, plot.Bottom);
            pb.AddLine(points[0].X, plot.Bottom);
            pb.EndFigure(CanvasFigureLoop.Closed);
        }
        else pb.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(pb);
    }
}
