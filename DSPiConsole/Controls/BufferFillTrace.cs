using DSPiConsole.Core.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// One buffer's last 15 s, under its statistics row: a band across the min and
/// max watermarks, a dashed 50 % guide, and the trace scrolling right to left,
/// the oldest kept sample on the left edge. A missing sample lifts the pen; the
/// PDM ring buffer's trace is dashed. After the macOS Console's
/// BufferFillSparklineNSView.
/// </summary>
public sealed class BufferFillTrace : UserControl
{
    /// <summary>The strip the row draws its name and values in.</summary>
    private const float HeaderHeight = 17;

    private readonly CanvasControl _canvas = new() { IsHitTestVisible = false };
    private readonly BufferFillHistory _history;
    private readonly int _series;
    private Color _color;
    private byte _min, _max;

    public BufferFillTrace(BufferFillHistory history, int series, Color color)
    {
        _history = history;
        _series = series;
        _color = color;
        IsHitTestVisible = false;
        Content = _canvas;
        _canvas.Draw += (_, e) => Draw(e.DrawingSession, (float)_canvas.ActualWidth, (float)_canvas.ActualHeight);
    }

    /// <summary>Moves the watermark band, and redraws with the new sample.</summary>
    public void Update(byte min, byte max)
    {
        (_min, _max) = (min, max);
        _canvas.Invalidate();
    }

    public void Dispose() => _canvas.RemoveFromVisualTree();

    private void Draw(CanvasDrawingSession ds, float w, float h)
    {
        if (w < 4 || h < HeaderHeight + 4) return;
        float Y(float pct)
        {
            // The 2 px inset keeps a trace at 0 % or 100 % from losing half its width.
            float bottom = h - 2, top = Math.Min(bottom, HeaderHeight);
            return bottom - pct / 100 * (bottom - top);
        }

        float lo = Y(Math.Min(_min, _max)), hi = Y(Math.Max(_min, _max));
        ds.FillRectangle(new Rect(0, hi, w, Math.Max(1, lo - hi)), Color.FromArgb(36, _color.R, _color.G, _color.B));
        using (var guide = new CanvasStrokeStyle { CustomDashStyle = new[] { 2f, 3f } })
            ds.DrawLine(0, Y(50), w, Y(50), Color.FromArgb(46, 200, 200, 205), 0.5f, guide);

        int total = _history.Total;
        if (total < 2) return;
        float step = w / (BufferFillHistory.Capacity - 1);
        int first = Math.Max(0, total - BufferFillHistory.Capacity);
        using var pb = new CanvasPathBuilder(ds);
        bool penDown = false, any = false;
        for (int s = first; s < total; s++)
        {
            if (_history.Value(_series, s) is not { } v)
            {
                if (penDown) pb.EndFigure(CanvasFigureLoop.Open);
                penDown = false;
                continue;
            }
            float x = w - (total - 1 - s) * step;
            var point = new System.Numerics.Vector2(x, Y(v));
            if (penDown) pb.AddLine(point);
            else { pb.BeginFigure(point); penDown = true; any = true; }
        }
        if (penDown) pb.EndFigure(CanvasFigureLoop.Open);
        if (!any) return;
        using var path = CanvasGeometry.CreatePath(pb);
        using var style = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        // The PDM ring buffer's trace is dashed (lengths in stroke widths).
        if (_series == BufferFillHistory.PdmRingSeries) style.CustomDashStyle = new[] { 3f / 1.25f, 2f / 1.25f };
        ds.DrawGeometry(path, _color, 1.25f, style);
    }
}
