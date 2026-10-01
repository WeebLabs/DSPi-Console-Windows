using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DSPiConsole.Controls;

/// <summary>
/// A vacuum tube drawn on a 24-unit grid for the Tube Modeller's header. The
/// glass, base and pins take the foreground colour; the filament glows only
/// while the effect is on, warming over 0.9 s and cooling over 0.6 s, so the
/// header doubles as a status light. Port of the macOS Console's TubeIcon.
/// </summary>
public sealed class TubeIcon : Grid
{
    private static readonly Color Heater = Color.FromArgb(255, 255, 159, 10);
    private readonly Path _coldFilament;
    private readonly Grid _litFilament = new();
    private bool _lit;

    public TubeIcon(double size, Brush foreground)
    {
        Width = size;
        Height = size;
        IsHitTestVisible = false;
        double s = size / 24, line = size * 1.9 / 27;
        Point P(double x, double y) => new(x * s, y * s);

        // Straight-sided glass with a round dome, and the exhaust tip.
        var envelope = new PathGeometry();
        var glass = new PathFigure { StartPoint = P(5.5, 17) };
        glass.Segments.Add(new LineSegment { Point = P(5.5, 8.5) });
        glass.Segments.Add(new ArcSegment { Point = P(18.5, 8.5), Size = new Size(6.5 * s, 6.5 * s), SweepDirection = SweepDirection.Clockwise });
        glass.Segments.Add(new LineSegment { Point = P(18.5, 17) });
        envelope.Figures.Add(glass);
        envelope.Figures.Add(Line(P(12, 2), P(12, 0.8)));
        Children.Add(Stroke(envelope, foreground, line));

        Children.Add(new Path
        {
            Data = new RectangleGeometry { Rect = new Rect(4.5 * s, 16.8 * s, 15 * s, 3.6 * s) },
            Fill = foreground,
        });

        var pins = new PathGeometry();
        foreach (double x in new[] { 8.5, 12, 15.5 }) pins.Figures.Add(Line(P(x, 20.4), P(x, 23.2)));
        Children.Add(Stroke(pins, foreground, line));

        // The heater's support wires, rising from the base.
        var rods = new PathGeometry();
        rods.Figures.Add(Line(P(10.5, 16.8), P(10.5, 11)));
        rods.Figures.Add(Line(P(13.5, 16.8), P(13.5, 11)));
        Children.Add(Stroke(rods, foreground, line * 0.8));

        Geometry Filament()
        {
            var g = new PathGeometry();
            var f = new PathFigure { StartPoint = P(10.5, 11) };
            f.Segments.Add(new QuadraticBezierSegment { Point1 = P(12, 6.5), Point2 = P(13.5, 11) });
            g.Figures.Add(f);
            return g;
        }
        // Both stay in place and cross-fade, so the filament warms and cools
        // rather than switching.
        _coldFilament = Stroke(Filament(), foreground, line * 0.8);
        Children.Add(_coldFilament);
        _litFilament.Children.Add(Stroke(Filament(), new SolidColorBrush(Color.FromArgb(90, Heater.R, Heater.G, Heater.B)), line * 3));
        _litFilament.Children.Add(Stroke(Filament(), new SolidColorBrush(Heater), line));
        _litFilament.Opacity = 0;
        Children.Add(_litFilament);
    }

    private static PathFigure Line(Point a, Point b)
    {
        var f = new PathFigure { StartPoint = a };
        f.Segments.Add(new LineSegment { Point = b });
        return f;
    }

    private static Path Stroke(Geometry data, Brush brush, double width) => new()
    {
        Data = data,
        Stroke = brush,
        StrokeThickness = width,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    public bool Lit
    {
        get => _lit;
        set
        {
            if (_lit == value) return;
            _lit = value;
            // A heater takes a moment to glow and a little less to go dark.
            var duration = TimeSpan.FromSeconds(value ? 0.9 : 0.6);
            Fade(_litFilament, value ? 1 : 0, duration);
            Fade(_coldFilament, value ? 0 : 1, duration);
        }
    }

    internal static void Fade(UIElement element, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(duration),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(animation);
        sb.Begin();
    }
}
