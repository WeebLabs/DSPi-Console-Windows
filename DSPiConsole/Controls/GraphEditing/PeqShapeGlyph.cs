using DSPiConsole.Core.GraphEditing;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;

namespace DSPiConsole.Controls.GraphEditing;

/// <summary>Small drawn glyphs for the shape button and the shape page, 18×12,
/// after the macOS Console's PeqShapeGlyph.</summary>
public static class PeqShapeGlyph
{
    public static Path Create(PeqShape shape, Brush stroke)
    {
        var figure = new PathFigure { IsClosed = false, IsFilled = false };
        void Move(double x, double y) => figure.StartPoint = new Point(x, y);
        void Line(double x, double y) => figure.Segments.Add(new LineSegment { Point = new Point(x, y) });
        void Curve(double x, double y, double c1x, double c1y, double c2x, double c2y) =>
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(c1x, c1y), Point2 = new Point(c2x, c2y), Point3 = new Point(x, y),
            });

        const double mid = 7;
        switch (shape)
        {
            case PeqShape.Bell:
                Move(1, 9);
                Curve(9, 2, 5, 9, 6.5, 2);
                Curve(17, 9, 11.5, 2, 13, 9);
                break;
            case PeqShape.LowShelf:
                Move(1, 3);
                Line(6, 3);
                Curve(12, 9, 9, 3, 9, 9);
                Line(17, 9);
                break;
            case PeqShape.HighShelf:
                Move(1, 9);
                Line(6, 9);
                Curve(12, 3, 9, 9, 9, 3);
                Line(17, 3);
                break;
            case PeqShape.LowCut:
                Move(3, 11);
                Curve(10, 4, 5, 5, 7, 4);
                Line(17, 4);
                break;
            case PeqShape.HighCut:
                Move(1, 4);
                Line(8, 4);
                Curve(15, 11, 11, 4, 13, 5);
                break;
            case PeqShape.Notch:
                Move(1, 3);
                Line(6.5, 3);
                Curve(9, 11, 8.2, 3, 8.6, 11);
                Curve(11.5, 3, 9.4, 11, 9.8, 3);
                Line(17, 3);
                break;
            case PeqShape.AllPass:
                Move(1, mid);
                Line(5, mid);
                Curve(13, mid, 8, 0, 10, 14);
                Line(17, mid);
                break;
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return new Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 18,
            Height = 12,
            IsHitTestVisible = false,
        };
    }
}
