using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// A tool window's response graph, as the macOS Console's loudness and
/// crossfeed windows draw it: one or more curves on a log axis from 20 Hz to
/// 20 kHz, the dB axis fitted to the curves (at least 10 dB, with 20 % spare
/// above and below), a grid at 100 Hz, 1 kHz and 10 kHz, and either a badge,
/// a legend, or "Disabled" when the effect is off.
/// </summary>
public sealed class ToolCurveGraph : UserControl
{
    public sealed record Series(string Name, Color Color, IReadOnlyList<(float Freq, float Db)> Points);

    private const double MinFreq = 20, MaxFreq = 20000;
    private static readonly Color GridColor = Color.FromArgb(38, 128, 128, 128);
    private static readonly Color LabelColor = Color.FromArgb(153, 200, 200, 205);

    private readonly CanvasControl _canvas = new();
    private IReadOnlyList<Series> _series = Array.Empty<Series>();
    private bool _enabled, _legend;
    private string? _badge;
    private Color _badgeColor;

    public ToolCurveGraph()
    {
        Content = _canvas;
        _canvas.Draw += (_, e) => Draw(e.DrawingSession, (float)_canvas.ActualWidth, (float)_canvas.ActualHeight);
    }

    /// <summary>Releases the canvas for good; the host window calls it on
    /// close. Not on Unloaded: a window that swaps its body out and back in
    /// would get a canvas that never draws again.</summary>
    public void Dispose() => _canvas.RemoveFromVisualTree();

    /// <param name="badge">A note in the top right, in the first curve's colour.</param>
    /// <param name="legend">Names the curves in the top right instead.</param>
    public void SetCurves(IReadOnlyList<Series> series, bool enabled, string? badge = null, bool legend = false)
    {
        _series = series;
        _enabled = enabled;
        _badge = badge;
        _legend = legend;
        _badgeColor = series.Count > 0 ? series[0].Color : default;
        _canvas.Invalidate();
    }

    private static float X(double freq, float w) =>
        (float)((Math.Log10(freq) - Math.Log10(MinFreq)) / (Math.Log10(MaxFreq) - Math.Log10(MinFreq)) * w);

    private static CanvasTextFormat Mono(CanvasHorizontalAlignment align) => new()
    {
        FontSize = 7, FontFamily = "Cascadia Code",
        HorizontalAlignment = align, VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    private void Draw(CanvasDrawingSession ds, float w, float h)
    {
        if (w < 20 || h < 20) return;
        float lo = -30, hi = 5;
        var all = _series.SelectMany(s => s.Points).ToArray();
        if (all.Length > 0) { lo = all.Min(p => p.Db); hi = all.Max(p => p.Db); }
        float span = Math.Max(hi - lo, 10), pad = span * 0.2f;
        float domainMin = lo - pad, domainMax = hi + pad;
        float Y(float db) => h - (db - domainMin) / (domainMax - domainMin) * h;

        float step = domainMax - domainMin > 40 ? 10 : 5;
        using (var left = Mono(CanvasHorizontalAlignment.Left))
            for (float db = MathF.Ceiling(domainMin / step) * step; db <= domainMax; db += step)
            {
                float y = Y(db);
                ds.DrawLine(0, y, w, y, GridColor, 0.5f);
                // + 0 turns a -0 from Ceiling into 0, so the label never reads "-0".
                ds.DrawText($"{db + 0f:0}", new Rect(2, y - 6, 30, 12), LabelColor, left);
            }
        using (var centre = Mono(CanvasHorizontalAlignment.Center))
            foreach (double f in new[] { 100.0, 1000, 10000 })
            {
                float x = X(f, w);
                ds.DrawLine(x, 0, x, h, GridColor, 0.5f);
                ds.DrawText(f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}", new Rect(x - 15, h - 11, 30, 12), LabelColor, centre);
            }

        if (!_enabled)
        {
            using var disabled = new CanvasTextFormat
            {
                FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
            };
            ds.DrawText("Disabled", new Rect(0, 0, w, h), Color.FromArgb(128, 200, 200, 205), disabled);
            return;
        }

        using var style = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
        // The first series is drawn last, on top.
        foreach (var s in _series.Reverse())
        {
            if (s.Points.Count < 2) continue;
            using var pb = new CanvasPathBuilder(ds);
            for (int i = 0; i < s.Points.Count; i++)
            {
                var p = new Vector2(X(s.Points[i].Freq, w), Y(s.Points[i].Db));
                if (i == 0) pb.BeginFigure(p); else pb.AddLine(p);
            }
            pb.EndFigure(CanvasFigureLoop.Open);
            using var path = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(path, s.Color, 2, style);
        }

        if (_badge != null)
        {
            using var text = new CanvasTextFormat
            {
                FontSize = 8, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
            };
            var badge = new Rect(w - 74, 8, 66, 14);
            ds.FillRoundedRectangle(badge, 4, 4, Color.FromArgb(204, _badgeColor.R, _badgeColor.G, _badgeColor.B));
            ds.DrawText(_badge, badge, Color.FromArgb(255, 255, 255, 255), text);
        }
        else if (_legend && _series.Count > 0)
        {
            using var text = new CanvasTextFormat
            {
                FontSize = 7, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap,
            };
            var box = new Rect(w - 70, 6, 62, 6 + _series.Count * 11);
            ds.FillRoundedRectangle(box, 4, 4, Color.FromArgb(217, 30, 30, 32));
            for (int i = 0; i < _series.Count; i++)
            {
                float y = (float)box.Y + 3 + i * 11 + 5.5f;
                ds.FillRoundedRectangle(new Rect(box.X + 6, y - 1, 10, 2), 1, 1, _series[i].Color);
                ds.DrawText(_series[i].Name, new Rect(box.X + 20, y - 6, box.Width - 22, 12), Color.FromArgb(255, 230, 230, 230), text);
            }
        }
    }
}
