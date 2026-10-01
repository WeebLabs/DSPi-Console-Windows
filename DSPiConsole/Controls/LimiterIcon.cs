using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DSPiConsole.Controls;

/// <summary>
/// The output limiter's icon, after the speed-limiter symbol on car
/// dashboards: a gauge with a needle, and an arrowhead pointing in at the dial
/// from outside. Drawn rather than bundled so it stays sharp at any size and
/// takes its colour from the state. Port of the macOS Console's LimiterGlyph.
/// </summary>
public sealed class LimiterIcon : Grid
{
    private readonly Path _lines;
    private readonly Path _hub;

    public LimiterIcon(double size = 19, double lineWidth = 1.5)
    {
        Width = size;
        Height = size;
        IsHitTestVisible = false;
        _lines = new Path
        {
            Data = Lines(size),
            StrokeThickness = lineWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _hub = new Path { Data = Hub(size) };
        Children.Add(_lines);
        Children.Add(_hub);
    }

    public Brush? Brush
    {
        set
        {
            _lines.Stroke = value;
            _hub.Fill = value;
        }
    }

    // Degrees clockwise from straight up.
    private const double NeedleDeg = -40, ArrowDeg = -45;

    private static (Point Centre, double Radius) Dial(double s) =>
        // The dial sits low and right so the arrow has the top-left corner.
        (new Point(s / 2 + s * 0.07, s / 2 + s * 0.1), s * 0.33);

    private static Point At(Point c, double deg, double r)
    {
        double a = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
    }

    private static Geometry Hub(double s)
    {
        var (c, rad) = Dial(s);
        // A small solid hub, so most of the needle's length stays visible.
        double hub = rad * 0.17;
        return new EllipseGeometry { Center = c, RadiusX = hub, RadiusY = hub };
    }

    private static Geometry Lines(double s)
    {
        var (c, rad) = Dial(s);
        var g = new PathGeometry();

        // Dial: a 270-degree arc, open at the bottom.
        var dial = new PathFigure { StartPoint = At(c, -135, rad), IsClosed = false };
        var arc = new PolyLineSegment();
        for (int i = 1; i <= 60; i++) arc.Points.Add(At(c, -135 + 270.0 * i / 60, rad));
        dial.Segments.Add(arc);
        g.Figures.Add(dial);

        // Needle, from under the hub.
        var needle = new PathFigure { StartPoint = c };
        needle.Segments.Add(new LineSegment { Point = At(c, NeedleDeg, rad * 0.72) });
        g.Figures.Add(needle);

        // The arrow, a head alone pointing in along the radius, its tip stopping
        // short of the dial so the two never merge into one mark.
        var tip = At(c, ArrowDeg, rad * 1.3);
        var back = At(c, ArrowDeg, rad * 1.95);
        double dx = back.X - tip.X, dy = back.Y - tip.Y, len = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / len, uy = dy / len, head = rad * 0.34;
        var arrow = new PathFigure { StartPoint = new Point(tip.X + head * (ux - uy), tip.Y + head * (uy + ux)) };
        var arrowLines = new PolyLineSegment();
        arrowLines.Points.Add(tip);
        arrowLines.Points.Add(new Point(tip.X + head * (ux + uy), tip.Y + head * (uy - ux)));
        arrow.Segments.Add(arrowLines);
        g.Figures.Add(arrow);
        return g;
    }
}
