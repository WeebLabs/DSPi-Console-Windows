using System.Numerics;
using DSPiConsole.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// The tube stage's output against its input over one full-scale swing, with
/// the straight line a clean stage would draw. Inputs that drive the stage past
/// either knee are shaded: that is where the curve goes flat and the harmonics
/// come from. Asymmetry shows as the two shaded regions starting at different
/// distances from the centre, bias as the bend sitting off-centre. Port of the
/// macOS Console's TubeTransferView.
/// </summary>
public sealed class TubeTransferGraph : UserControl
{
    /// <summary>Output range drawn: hot trim and asymmetry can exceed full
    /// scale, so there is room above 1, and a line marks full scale itself.</summary>
    private const double YMax = 1.4;

    private readonly CanvasControl _canvas = new();
    private TubeShaper _committed = new(-12, 10, 3, 40, 100, 0);
    private TubeShaper? _live;
    private bool _effectEnabled;

    public TubeTransferGraph()
    {
        Content = _canvas;
        _canvas.Draw += (_, e) => Draw(e.DrawingSession, (float)_canvas.ActualWidth, (float)_canvas.ActualHeight);
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
    }

    /// <summary>The committed curve; clears any drag override.</summary>
    public void SetShaper(TubeShaper shaper, bool enabled)
    {
        _committed = shaper;
        _effectEnabled = enabled;
        _live = null;
        _canvas.Invalidate();
    }

    /// <summary>The curve while a parameter is dragged.</summary>
    public void SetLive(TubeShaper shaper)
    {
        _live = shaper;
        _canvas.Invalidate();
    }

    public TubeShaper Shown => _live ?? _committed;

    private static float X(double x, float w) => (float)((x + 1) / 2) * w;
    private static float Y(double y, float h) => (float)((YMax - Math.Clamp(y, -YMax, YMax)) / (2 * YMax)) * h;

    private static readonly Color Accent = (Color)Application.Current.Resources["SystemAccentColor"];
    private static readonly Color Orange = Color.FromArgb(26, 255, 159, 10);

    private static CanvasTextFormat Font(float size, bool bold = false) => new()
    {
        FontSize = size,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
        FontFamily = "Cascadia Code, Consolas",
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    private void Draw(CanvasDrawingSession ds, float w, float h)
    {
        if (w < 20 || h < 20) return;
        var s = Shown;

        // Grid, full scale out, and the axes.
        var grid = Color.FromArgb(38, 128, 128, 128);
        foreach (double x in new[] { -1.0, -0.5, 0.5, 1.0 }) ds.DrawLine(X(x, w), 0, X(x, w), h, grid, 0.5f);
        foreach (double y in new[] { -0.5, 0.5 }) ds.DrawLine(0, Y(y, h), w, Y(y, h), grid, 0.5f);
        using (var dashes = new CanvasStrokeStyle { CustomDashStyle = new[] { 3f, 2f } })
            foreach (double y in new[] { -1.0, 1.0 }) ds.DrawLine(0, Y(y, h), w, Y(y, h), Color.FromArgb(77, 255, 69, 58), 0.5f, dashes);
        var axis = Color.FromArgb(102, 128, 128, 128);
        ds.DrawLine(X(0, w), 0, X(0, w), h, axis, 0.5f);
        ds.DrawLine(0, Y(0, h), w, Y(0, h), axis, 0.5f);

        if (_effectEnabled)
        {
            // Clipped inputs, solved from the knees so the edge is exact.
            var (pos, neg) = s.ClipPoints;
            if (pos < 1)
            {
                float x0 = X(Math.Max(pos, -1), w);
                ds.FillRectangle(new Rect(x0, 0, Math.Max(0, w - x0), h), Orange);
            }
            if (neg > -1)
            {
                float x1 = X(Math.Min(neg, 1), w);
                ds.FillRectangle(new Rect(0, 0, Math.Max(0, x1), h), Orange);
            }

            using (var dashes = new CanvasStrokeStyle { CustomDashStyle = new[] { 3f, 3f } })
                ds.DrawLine(X(-1, w), Y(-1, h), X(1, w), Y(1, h), Color.FromArgb(64, 255, 255, 255), 1, dashes);

            using var pb = new CanvasPathBuilder(ds);
            const int steps = 240;
            for (int i = 0; i <= steps; i++)
            {
                double x = -1 + 2.0 * i / steps;
                var p = new Vector2(X(x, w), Y(s.Output(x), h));
                if (i == 0) pb.BeginFigure(p); else pb.AddLine(p);
            }
            pb.EndFigure(CanvasFigureLoop.Open);
            using var curve = CanvasGeometry.CreatePath(pb);
            using var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
            ds.DrawGeometry(curve, Accent, 1.6f, round);
        }
        else
        {
            ds.DrawText("Disabled", w / 2, h / 2, Color.FromArgb(128, 200, 200, 205), Font(11, bold: true));
        }

        var label = Color.FromArgb(153, 200, 200, 205);
        ds.DrawText("in", w - 8, Y(0, h) + 7, label, Font(7, bold: true));
        ds.DrawText("out", X(0, w) + 10, 6, label, Font(7, bold: true));
        ds.DrawText("0 dBFS", 18, Y(1, h) - 6, Color.FromArgb(128, 255, 69, 58), Font(7));
    }
}
