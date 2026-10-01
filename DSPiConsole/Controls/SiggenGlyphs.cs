using DSPiConsole.Core.Models;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DSPiConsole.Controls;

/// <summary>
/// A miniature waveform per signal type for the signal generator's tiles,
/// drawn as a stroked path over the tile, as the macOS Console's SiggenGlyph
/// draws them.
/// </summary>
public static class SiggenGlyphs
{
    public static Geometry For(SiggenType type, double w, double h)
    {
        var g = new PathGeometry();
        double midY = h / 2;
        Point P(double t, double v) => new(t * w, midY - v * h * 0.42);

        PathFigure Start(Point p)
        {
            var f = new PathFigure { StartPoint = p, IsClosed = false, IsFilled = false };
            g.Figures.Add(f);
            return f;
        }
        void Line(PathFigure f, Point p) => f.Segments.Add(new LineSegment { Point = p });
        void Sampled(Func<double, double> fn, int points = 64)
        {
            var f = Start(P(0, fn(0)));
            for (int i = 1; i <= points; i++)
            {
                double t = (double)i / points;
                Line(f, P(t, fn(t)));
            }
        }
        // Deterministic "random" for the noise glyphs.
        double Jitter(double t, double scale) =>
            Math.Sin(t * 91.7 + 1.3) * 0.5 + Math.Sin(t * 173.3) * 0.35 + Math.Sin(t * 47.9 + 4.1) * 0.15 * scale;

        switch (type)
        {
            case SiggenType.Square:
            {
                var f = Start(P(0, 1));
                double t = 0;
                bool high = true;
                while (t < 1)
                {
                    double next = Math.Min(t + 0.25, 1);
                    Line(f, P(next, high ? 1 : -1));
                    if (next < 1) Line(f, P(next, high ? -1 : 1));
                    high = !high;
                    t = next;
                }
                break;
            }
            case SiggenType.White: Sampled(t => Jitter(t, 1) * 1.5, 40); break;
            case SiggenType.Pink: Sampled(t => Math.Sin(t * 2 * Math.PI * 1.3) * 0.7 + Jitter(t, 1) * 0.5, 40); break;
            case SiggenType.SweepLog: Sampled(t => Math.Sin(2 * Math.PI * 1.2 * (Math.Pow(6, t) - 1))); break;
            case SiggenType.SweepLin: Sampled(t => Math.Sin(2 * Math.PI * (1 + 4 * t) * t)); break;
            case SiggenType.SweepStep:
                for (int i = 0; i < 4; i++)
                {
                    double t0 = i / 4.0, t1 = (i + 1) / 4.0, v = i / 3.0 * 1.6 - 0.8;
                    Line(Start(P(t0, v)), P(t1 - 0.04, v));
                }
                break;
            case SiggenType.Impulse:
            {
                var f = Start(P(0, 0));
                foreach (var (t, v) in new[] { (0.45, 0.0), (0.48, 1.0), (0.51, 0.0), (1.0, 0.0) }) Line(f, P(t, v));
                break;
            }
            case SiggenType.ClicksAlt:
            {
                var f = Start(P(0, 0));
                foreach (var (t, v) in new[] { (0.28, 0.0), (0.31, 1.0), (0.34, 0.0), (0.64, 0.0), (0.67, -1.0), (0.70, 0.0), (1.0, 0.0) })
                    Line(f, P(t, v));
                break;
            }
            case SiggenType.Polarity: Sampled(t => t > 0.3 && t < 0.7 ? Math.Sin((t - 0.3) / 0.4 * Math.PI) : 0); break;
            case SiggenType.ToneBurst:
                Sampled(t => Math.Sin(t * 2 * Math.PI * 6) * (t > 0.15 && t < 0.55 ? Math.Sin((t - 0.15) / 0.4 * Math.PI) : 0));
                break;
            case SiggenType.TonePair: Sampled(t => Math.Sin(t * 2 * Math.PI * 1.5) * 0.72 + Math.Sin(t * 2 * Math.PI * 11) * 0.28); break;
            case SiggenType.Multitone:
                Sampled(t => (Math.Sin(t * 2 * Math.PI * 1.5) + Math.Sin(t * 2 * Math.PI * 3.7 + 1) + Math.Sin(t * 2 * Math.PI * 7.3 + 2)) / 2.6);
                break;
            case SiggenType.Isp:
            {
                // The +1 +1 -1 -1 staircase with the implied over-peak arc.
                Line(Start(P(0.05, 0.7)), P(0.45, 0.7));
                Line(Start(P(0.55, -0.7)), P(0.95, -0.7));
                Start(P(0.05, 0.7)).Segments.Add(new QuadraticBezierSegment { Point1 = P(0.25, 1.15), Point2 = P(0.45, 0.7) });
                break;
            }
            case SiggenType.ChannelId:
            {
                double[] centres = { 0.18, 0.5, 0.82 };
                for (int i = 0; i < centres.Length; i++)
                {
                    double width = 0.11, c = centres[i], amp = 0.45 + i * 0.27;
                    var f = Start(P(c - width, 0));
                    for (int s = 1; s <= 12; s++)
                    {
                        double t = c - width + s / 12.0 * width * 2, ph = (t - (c - width)) / (width * 2);
                        Line(f, P(t, Math.Sin(ph * Math.PI) * amp * Math.Sin(ph * Math.PI * 6)));
                    }
                }
                break;
            }
            default: Sampled(t => Math.Sin(t * 2 * Math.PI * 2)); break;
        }
        return g;
    }
}
