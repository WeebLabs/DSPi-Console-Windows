using System.Numerics;
using DSPiConsole.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// The Tube Modeller's Basic-mode showcase: the selected tube, its heater glow
/// while the stage is on, and a bloom that follows the signal on the outputs
/// the stage processes. Each of the three layers is drawn once per family and
/// size; afterwards only their opacities change, so following the audio costs
/// no drawing at all. Port of the macOS Console's TubeIllustration.
/// </summary>
public sealed class TubeIllustration : Grid
{
    private readonly CanvasControl _tube = new() { IsHitTestVisible = false };
    private readonly CanvasControl _heater = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly CanvasControl _bloom = new() { IsHitTestVisible = false, Opacity = 0 };
    private TubeFamily _family = TubeFamily.NovalTriode;
    private bool _lit;
    private double _bloomOpacity;

    public TubeIllustration()
    {
        IsHitTestVisible = false;
        Children.Add(_tube);
        Children.Add(_heater);
        Children.Add(_bloom);
        _tube.Draw += (s, e) => TubeArt.DrawTube(e.DrawingSession, TubeArt.Geometry(_family), s.Size);
        _heater.Draw += (s, e) => TubeArt.DrawGlow(e.DrawingSession, TubeArt.Geometry(_family), s.Size, bloom: false);
        _bloom.Draw += (s, e) => TubeArt.DrawGlow(e.DrawingSession, TubeArt.Geometry(_family), s.Size, bloom: true);
        Unloaded += (_, _) =>
        {
            _tube.RemoveFromVisualTree();
            _heater.RemoveFromVisualTree();
            _bloom.RemoveFromVisualTree();
        };
    }

    public TubeFamily Family
    {
        get => _family;
        set
        {
            if (_family == value) return;
            _family = value;
            _tube.Invalidate();
            _heater.Invalidate();
            _bloom.Invalidate();
        }
    }

    /// <summary>The heater: 0.9 s to glow, 0.6 s to go dark.</summary>
    public bool Lit
    {
        get => _lit;
        set
        {
            if (_lit == value) return;
            _lit = value;
            TubeIcon.Fade(_heater, value ? 1 : 0, TimeSpan.FromSeconds(value ? 0.9 : 0.6));
            if (!value) Bloom = 0;
        }
    }

    /// <summary>The bloom's opacity, 0..1: the square root of the loudest
    /// processed output's peak, so ordinary listening levels show. Changes
    /// under 1/255 are skipped.</summary>
    public double Bloom
    {
        set
        {
            double v = Math.Round(Math.Clamp(value, 0, 1) * 255) / 255;
            if (v == _bloomOpacity) return;
            _bloomOpacity = v;
            TubeIcon.Fade(_bloom, v, TimeSpan.FromSeconds(0.1));
        }
    }
}

/// <summary>The tube drawings, after the macOS Console's TubeRenderer. Every
/// family is one <see cref="Shape"/> on a 120 by 200 design grid (x from -60
/// to 60 about the axis, y from 0 at the top to 200 at the pin tips), so the
/// families differ only in numbers.</summary>
internal static class TubeArt
{
    internal sealed record Plate(Rect Rect, float Fins = 0, float[]? Ribs = null, bool Mesh = false);
    internal sealed record Glow(Vector2 At, float Radius);
    internal sealed record Bakelite(float Top, float Bottom, float TopHalf, float BottomHalf, bool Key);

    internal sealed record Shape
    {
        /// <summary>Left edge of the envelope as (half-width, y), bottom to top;
        /// the right edge mirrors it and a dome closes it at <see cref="Top"/>.</summary>
        public required Vector2[] Profile { get; init; }
        public required float Top { get; init; }
        public bool Metal { get; init; }
        public Bakelite? Base { get; init; }
        public required float[] Pins { get; init; }
        public required float PinTop { get; init; }
        public required float PinBottom { get; init; }
        public float PinWidth { get; init; } = 1.6f;
        public (float From, float To)? Getter { get; init; }
        public (float Y, float Half)[] Micas { get; init; } = Array.Empty<(float, float)>();
        public (float X, float From, float To)[] Rods { get; init; } = Array.Empty<(float, float, float)>();
        public Plate[] Plates { get; init; } = Array.Empty<Plate>();
        public Vector2[]? Filament { get; init; }
        public Glow[] Glows { get; init; } = Array.Empty<Glow>();
        /// <summary>Steel envelopes carry a pressed ring near the base.</summary>
        public (float From, float To)? Ring { get; init; }

        public float GlassBottom => Profile[0].Y;
        public float WidestHalf => Profile.Max(p => p.X);
    }

    private static Vector2 V(float x, float y) => new(x, y);
    private static Rect R(double x, double y, double w, double h) => new(x, y, w, h);

    internal static Shape Geometry(TubeFamily family) => family switch
    {
        TubeFamily.NovalPentode => new Shape
        {
            Profile = new[] { V(28, 166), V(28, 72) }, Top = 52,
            Pins = new float[] { -20, -10, 0, 10, 20 }, PinTop = 166, PinBottom = 190,
            Getter = (52, 67),
            Micas = new[] { (80f, 25f), (142f, 25f) },
            Rods = new[] { (-22f, 76f, 164f), (22f, 76f, 164f) },
            Plates = new[] { new Plate(R(-17, 86, 34, 52), Ribs: new float[] { -6, 6 }) },
            Glows = new[] { new Glow(V(0, 81), 14), new Glow(V(0, 143), 12) },
        },
        TubeFamily.NovalPower => new Shape
        {
            Profile = new[] { V(32, 168), V(32, 58) }, Top = 34,
            Pins = new float[] { -22, -11, 0, 11, 22 }, PinTop = 168, PinBottom = 192,
            Getter = (34, 50),
            Micas = new[] { (60f, 29f), (144f, 29f) },
            Rods = new[] { (-29f, 56f, 166f), (29f, 56f, 166f) },
            Plates = new[] { new Plate(R(-20, 66, 40, 72), Fins: 5, Ribs: new float[] { -7, 7 }) },
            Glows = new[] { new Glow(V(0, 61), 16), new Glow(V(0, 144), 14) },
        },
        TubeFamily.OctalGlass => new Shape
        {
            Profile = new[] { V(30, 148), V(30, 50) }, Top = 26,
            Base = new Bakelite(146, 176, 33, 31, true),
            Pins = new float[] { -21, -7, 7, 21 }, PinTop = 176, PinBottom = 196, PinWidth = 2.6f,
            Getter = (26, 41),
            Micas = new[] { (58f, 27f), (128f, 27f) },
            Rods = new[] { (-26f, 54f, 146f), (0f, 54f, 146f), (26f, 54f, 146f) },
            Plates = new[] { new Plate(R(-22, 62, 19, 62), Ribs: new[] { -12.5f }), new Plate(R(3, 62, 19, 62), Ribs: new[] { 12.5f }) },
            Glows = new[] { new Glow(V(-12.5f, 58), 12), new Glow(V(12.5f, 58), 12), new Glow(V(-12.5f, 128), 11), new Glow(V(12.5f, 128), 11) },
        },
        // A metal tube shows no glow; its one light is a faint warmth where
        // the can meets the base.
        TubeFamily.OctalMetal => new Shape
        {
            Profile = new[] { V(27, 150), V(27, 60) }, Top = 52, Metal = true,
            Base = new Bakelite(148, 176, 30, 28, true),
            Pins = new float[] { -19, -6.5f, 6.5f, 19 }, PinTop = 176, PinBottom = 196, PinWidth = 2.6f,
            Glows = new[] { new Glow(V(0, 149), 9) },
            Ring = (126, 132),
        },
        TubeFamily.OctalPowerLarge => new Shape
        {
            Profile = new[] { V(34, 152), V(34, 44) }, Top = 14,
            Base = new Bakelite(150, 180, 37, 35, true),
            Pins = new float[] { -24, -8, 8, 24 }, PinTop = 180, PinBottom = 199, PinWidth = 2.8f,
            Getter = (14, 31),
            Micas = new[] { (46f, 31f), (134f, 31f) },
            Rods = new[] { (-30f, 42f, 150f), (30f, 42f, 150f) },
            Plates = new[] { new Plate(R(-23, 52, 46, 78), Fins: 6, Ribs: new float[] { 0 }) },
            Glows = new[] { new Glow(V(0, 47), 18), new Glow(V(0, 135), 16) },
        },
        TubeFamily.OctalPowerSmall => new Shape
        {
            Profile = new[] { V(27, 152), V(27, 66) }, Top = 44,
            Base = new Bakelite(150, 178, 31, 29, true),
            Pins = new float[] { -20, -7, 7, 20 }, PinTop = 178, PinBottom = 197, PinWidth = 2.6f,
            Getter = (44, 59),
            Micas = new[] { (68f, 24f), (136f, 24f) },
            Rods = new[] { (-24f, 64f, 150f), (24f, 64f, 150f) },
            Plates = new[] { new Plate(R(-17, 74, 34, 58), Fins: 5, Ribs: new float[] { 0 }) },
            Glows = new[] { new Glow(V(0, 69), 14), new Glow(V(0, 137), 12) },
        },
        TubeFamily.ShoulderedPower => new Shape
        {
            Profile = new[] { V(26, 152), V(30, 140), V(36, 116), V(36, 104), V(27, 76), V(26, 60) }, Top = 24,
            Base = new Bakelite(150, 180, 34, 32, true),
            Pins = new float[] { -22, -7, 7, 22 }, PinTop = 180, PinBottom = 199, PinWidth = 2.8f,
            Getter = (24, 40),
            Micas = new[] { (66f, 22f), (136f, 30f) },
            Rods = new[] { (-24f, 62f, 150f), (24f, 62f, 150f) },
            Plates = new[] { new Plate(R(-20, 72, 40, 60), Fins: 7, Ribs: new float[] { 0 }) },
            Glows = new[] { new Glow(V(0, 68), 16), new Glow(V(0, 137), 14) },
        },
        TubeFamily.BeamBottle => new Shape
        {
            Profile = new[] { V(30, 156), V(32, 144), V(42, 108), V(42, 40) }, Top = 8,
            Base = new Bakelite(154, 184, 36, 34, true),
            Pins = new float[] { -24, -8, 8, 24 }, PinTop = 184, PinBottom = 200, PinWidth = 2.8f,
            Getter = (8, 25),
            Micas = new[] { (46f, 39f), (138f, 36f) },
            Rods = new[] { (-34f, 42f, 154f), (34f, 42f, 154f) },
            Plates = new[] { new Plate(R(-26, 54, 52, 80), Fins: 7, Ribs: new float[] { -9, 9 }) },
            Glows = new[] { new Glow(V(0, 49), 20), new Glow(V(0, 139), 16) },
        },
        // The filament is the cathode, strung above the plate on springs, so it
        // is the thing that glows rather than anything inside the plate.
        TubeFamily.DirectlyHeated => new Shape
        {
            Profile = new[] { V(22, 162), V(25, 152), V(42, 118), V(43, 98), V(34, 56), V(23, 36) }, Top = 10,
            Base = new Bakelite(160, 188, 36, 34, false),
            Pins = new float[] { -13, 13 }, PinTop = 188, PinBottom = 200, PinWidth = 4.5f,
            Getter = (10, 22),
            Micas = new[] { (50f, 24f), (140f, 27f) },
            Rods = new[] { (-24f, 48f, 160f), (24f, 48f, 160f), (-7f, 50f, 57f), (7f, 50f, 57f) },
            Plates = new[] { new Plate(R(-19, 74, 38, 62), Mesh: true) },
            Filament = new[] { V(-15, 74), V(-7, 57), V(0, 74), V(7, 57), V(15, 74) },
            Glows = new[] { new Glow(V(0, 66), 34) },
        },
        _ => new Shape
        {
            Profile = new[] { V(28, 166), V(28, 72) }, Top = 52,
            Pins = new float[] { -20, -10, 0, 10, 20 }, PinTop = 166, PinBottom = 190,
            Getter = (52, 67),
            Micas = new[] { (80f, 25f), (142f, 25f) },
            Rods = new[] { (-25f, 76f, 164f), (0f, 76f, 164f), (25f, 76f, 164f) },
            Plates = new[] { new Plate(R(-23, 84, 20, 54), Ribs: new float[] { -13 }), new Plate(R(3, 84, 20, 54), Ribs: new float[] { 13 }) },
            Glows = new[] { new Glow(V(-13, 80), 12), new Glow(V(13, 80), 12), new Glow(V(-13, 142), 11), new Glow(V(13, 142), 11) },
        },
    };

    // ── Colours ──

    private static readonly Color HeaterColor = Rgb(1.0, 0.52, 0.16);
    private static readonly Color CoreColor = Rgb(1.0, 0.78, 0.45);

    private static Color Rgb(double r, double g, double b, double a = 1) =>
        Color.FromArgb((byte)(a * 255), (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    private static Color White(double a) => Color.FromArgb((byte)(a * 255), 255, 255, 255);
    private static Color Black(double a) => Color.FromArgb((byte)(a * 255), 0, 0, 0);
    private static Color Grey(double v, double a = 1) => Rgb(v, v, v, a);
    private static Color With(Color c, double a) => Color.FromArgb((byte)(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

    private static CanvasLinearGradientBrush Linear(ICanvasResourceCreator rc, Vector2 from, Vector2 to, params (float At, Color Color)[] stops) =>
        new(rc, stops.Select(s => new CanvasGradientStop { Position = s.At, Color = s.Color }).ToArray(), CanvasEdgeBehavior.Clamp, CanvasAlphaMode.Premultiplied)
        { StartPoint = from, EndPoint = to };

    private static CanvasLinearGradientBrush Even(ICanvasResourceCreator rc, Vector2 from, Vector2 to, params Color[] colors) =>
        Linear(rc, from, to, colors.Select((c, i) => ((float)i / (colors.Length - 1), c)).ToArray());

    /// <summary>Centres the 120 by 200 grid in <paramref name="size"/>,
    /// keeping its aspect.</summary>
    private static Matrix3x2 Place(Size size)
    {
        float s = (float)Math.Min(size.Height / 200, size.Width / 120);
        return Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation((float)size.Width / 2, (float)(size.Height - 200 * s) / 2);
    }

    // ── Paths ──

    /// <summary>Catmull-Rom through <paramref name="pts"/>, from the current point.</summary>
    private static void Smooth(CanvasPathBuilder p, IReadOnlyList<Vector2> pts)
    {
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector2 p0 = pts[Math.Max(i - 1, 0)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(i + 2, pts.Count - 1)];
            p.AddCubicBezier(p1 + (p2 - p0) / 6, p2 - (p3 - p1) / 6, p2);
        }
    }

    private static CanvasGeometry Envelope(ICanvasResourceCreator rc, Shape g)
    {
        var left = g.Profile.Select(v => V(-v.X, v.Y)).ToList();
        var right = g.Profile.Reverse().ToList();
        var last = g.Profile[^1];
        const float k = 0.5523f;
        float rise = last.Y - g.Top;
        using var p = new CanvasPathBuilder(rc);
        p.BeginFigure(left[0]);
        Smooth(p, left);
        // An elliptical dome, a quarter each side of the crown.
        p.AddCubicBezier(V(-last.X, last.Y - rise * k), V(-last.X * k, g.Top), V(0, g.Top));
        p.AddCubicBezier(V(last.X * k, g.Top), V(last.X, last.Y - rise * k), last);
        Smooth(p, right);
        // A 9-pin tube's glass ends in a pressed button that bulges slightly.
        p.AddQuadraticBezier(V(0, g.GlassBottom + 5), left[0]);
        p.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(p);
    }

    /// <summary>Half-width of the envelope at <paramref name="y"/>.</summary>
    private static float HalfWidth(Shape g, float y)
    {
        var pts = g.Profile;
        for (int i = 0; i < pts.Length - 1; i++)
        {
            if (y <= pts[i].Y && y >= pts[i + 1].Y)
            {
                float t = (pts[i].Y - y) / Math.Max(pts[i].Y - pts[i + 1].Y, 0.001f);
                return pts[i].X + (pts[i + 1].X - pts[i].X) * t;
            }
        }
        return pts[^1].X;
    }

    private static CanvasGeometry RoundRect(ICanvasResourceCreator rc, Rect r, float radius) =>
        CanvasGeometry.CreateRoundedRectangle(rc, r, radius, radius);

    /// <summary>A blurred drawing in design units.</summary>
    private static void Blurred(CanvasDrawingSession ds, float amount, Action<CanvasDrawingSession> draw)
    {
        using var list = new CanvasCommandList(ds);
        using (var inner = list.CreateDrawingSession()) draw(inner);
        using var blur = new GaussianBlurEffect { Source = list, BlurAmount = amount, BorderMode = EffectBorderMode.Soft };
        ds.DrawImage(blur);
    }

    // ── Tube ──

    internal static void DrawTube(CanvasDrawingSession ds, Shape g, Size size)
    {
        if (size.Width < 4 || size.Height < 4) return;
        ds.Transform = Place(size);
        using var env = Envelope(ds, g);
        float w = g.WidestHalf;

        // Contact shadow on the shelf.
        Blurred(ds, 3, s => s.FillEllipse(0, g.PinBottom + 0.5f, w * 0.9f, 3.5f, Black(0.45)));

        DrawPins(ds, g);
        DrawBase(ds, g);

        if (g.Metal)
        {
            DrawCan(ds, g, env);
            return;
        }

        // Glass body: a faint cylinder shade, lighter at the rims.
        ds.FillGeometry(env, Black(0.22));
        using (var shade = Linear(ds, V(-w, 0), V(w, 0), (0, White(0.16)), (0.2f, White(0.04)), (0.65f, White(0.02)), (0.92f, White(0.10)), (1, White(0.14))))
            ds.FillGeometry(env, shade);

        using (ds.CreateLayer(1, env))
        {
            foreach (var rod in g.Rods)
                ds.DrawLine(rod.X, rod.From, rod.X, rod.To, White(0.32), 0.8f);
            foreach (var plate in g.Plates) DrawPlate(ds, plate);
            foreach (var mica in g.Micas)
                using (var m = RoundRect(ds, R(-mica.Half, mica.Y - 0.9, mica.Half * 2, 1.8), 0.9f))
                    ds.FillGeometry(m, White(0.38));
            if (g.Filament is { } fil)
            {
                // The unlit filament: a dull wire. The glow layer lights it.
                using var join = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
                for (int i = 0; i < fil.Length - 1; i++) ds.DrawLine(fil[i], fil[i + 1], White(0.45), 0.9f, join);
            }

            // Getter flash: the silvered patch inside the crown.
            if (g.Getter is { } getter)
            {
                float span = getter.To - getter.From;
                using var flash = Linear(ds, V(0, getter.From), V(0, getter.To),
                    (0, Grey(0.88)), (0.45f, Grey(0.62, 0.95)), (1, Grey(0.40, 0)));
                ds.FillRectangle(R(-60, getter.From - 2, 120, span + 2), flash);
            }

            // Reflection down the left flank.
            float reflTop = g.Profile[^1].Y - 2, reflBottom = g.GlassBottom - 10;
            using var pb = new CanvasPathBuilder(ds);
            for (int i = 0; i <= 12; i++)
            {
                float y = reflTop + (reflBottom - reflTop) * i / 12f;
                var pt = V(-HalfWidth(g, y) + 5, y);
                if (i == 0) pb.BeginFigure(pt); else pb.AddLine(pt);
            }
            pb.EndFigure(CanvasFigureLoop.Open);
            using var refl = CanvasGeometry.CreatePath(pb);
            using var fade = Linear(ds, V(0, reflTop), V(0, reflBottom), (0, White(0.20)), (1, White(0.02)));
            using var caps = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
            ds.DrawGeometry(refl, fade, 2.5f, caps);
        }

        ds.DrawGeometry(env, White(0.5), 1.0f);

        // Exhaust tip, where the glass was sealed off.
        using var tip = RoundRect(ds, R(-2.6, g.Top - 6, 5.2, 7.5), 2.6f);
        ds.FillGeometry(tip, White(0.12));
        ds.DrawGeometry(tip, White(0.5), 0.9f);
    }

    private static void DrawPlate(CanvasDrawingSession ds, Plate plate)
    {
        var r = plate.Rect;
        if (plate.Fins > 0)
        {
            foreach (double side in new[] { r.X - plate.Fins, r.X + r.Width })
            {
                var fin = R(side, r.Y + 4, plate.Fins, r.Height - 8);
                ds.FillRectangle(fin, Grey(0.30));
                ds.DrawRectangle(fin, White(0.18), 0.5f);
            }
        }
        using var body = RoundRect(ds, r, 1.2f);
        using (var metal = Even(ds, V((float)r.X, 0), V((float)(r.X + r.Width), 0), Grey(0.20), Grey(0.36), Grey(0.24), Grey(0.16)))
            ds.FillGeometry(body, metal);
        if (plate.Mesh)
        {
            using (ds.CreateLayer(1, body))
            {
                for (double x = r.X - r.Height; x < r.X + r.Width; x += 3.2)
                {
                    ds.DrawLine((float)x, (float)(r.Y + r.Height), (float)(x + r.Height), (float)r.Y, White(0.13), 0.45f);
                    ds.DrawLine((float)x, (float)r.Y, (float)(x + r.Height), (float)(r.Y + r.Height), White(0.13), 0.45f);
                }
            }
        }
        foreach (float rib in plate.Ribs ?? Array.Empty<float>())
        {
            ds.DrawLine(rib, (float)r.Y + 2, rib, (float)(r.Y + r.Height) - 2, Black(0.45), 1.0f);
            ds.DrawLine(rib + 0.9f, (float)r.Y + 2, rib + 0.9f, (float)(r.Y + r.Height) - 2, White(0.14), 0.6f);
        }
        ds.DrawGeometry(body, White(0.22), 0.6f);
    }

    private static void DrawPins(CanvasDrawingSession ds, Shape g)
    {
        foreach (float x in g.Pins)
        {
            using var pin = RoundRect(ds, R(x - g.PinWidth / 2, g.PinTop - 2, g.PinWidth, g.PinBottom - g.PinTop + 2), g.PinWidth / 2);
            using var brush = Even(ds, V(x - g.PinWidth / 2, 0), V(x + g.PinWidth / 2, 0), Grey(0.45), Grey(0.85), Grey(0.50));
            ds.FillGeometry(pin, brush);
        }
    }

    private static void DrawBase(CanvasDrawingSession ds, Shape g)
    {
        if (g.Base is not { } b) return;
        if (b.Key)
        {
            // The octal locating key, a moulded stub between the pins.
            using var stub = RoundRect(ds, R(-2.8, b.Bottom - 2, 5.6, 9), 1.5f);
            ds.FillGeometry(stub, Rgb(0.16, 0.12, 0.10));
        }
        const float r = 3.5f;
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(-b.TopHalf, b.Top);
        pb.AddLine(b.TopHalf, b.Top);
        pb.AddLine(b.BottomHalf, b.Bottom - r);
        pb.AddQuadraticBezier(V(b.BottomHalf, b.Bottom), V(b.BottomHalf - r, b.Bottom));
        pb.AddLine(-b.BottomHalf + r, b.Bottom);
        pb.AddQuadraticBezier(V(-b.BottomHalf, b.Bottom), V(-b.BottomHalf, b.Bottom - r));
        pb.EndFigure(CanvasFigureLoop.Closed);
        using var shape = CanvasGeometry.CreatePath(pb);
        using (var bakelite = Even(ds, V(-b.TopHalf, 0), V(b.TopHalf, 0), Rgb(0.09, 0.07, 0.06), Rgb(0.25, 0.19, 0.15), Rgb(0.11, 0.08, 0.07)))
            ds.FillGeometry(shape, bakelite);
        ds.DrawGeometry(shape, White(0.14), 0.7f);
        ds.DrawLine(-b.TopHalf + 1, b.Top + 1.2f, b.TopHalf - 1, b.Top + 1.2f, White(0.18), 0.6f);
    }

    private static void DrawCan(CanvasDrawingSession ds, Shape g, CanvasGeometry env)
    {
        float w = g.WidestHalf;
        CanvasLinearGradientBrush Steel(float half) => Linear(ds, V(-half, 0), V(half, 0),
            (0, Grey(0.30)), (0.28f, Grey(0.66)), (0.6f, Grey(0.48)), (1, Grey(0.26)));
        using (var steel = Steel(w)) ds.FillGeometry(env, steel);
        if (g.Ring is { } ring)
        {
            using var band = RoundRect(ds, R(-w - 3, ring.From, (w + 3) * 2, ring.To - ring.From), 2);
            using var steel = Steel(w + 3);
            ds.FillGeometry(band, steel);
            ds.DrawGeometry(band, Black(0.35), 0.6f);
        }
        ds.DrawGeometry(env, White(0.25), 0.8f);
        // The sealing cap on the crown.
        using var cap = RoundRect(ds, R(-7, g.Top - 3, 14, 4), 1.5f);
        ds.FillGeometry(cap, Grey(0.55));
        ds.DrawGeometry(cap, Black(0.3), 0.5f);
    }

    // ── Glow ──

    /// <summary>The heater glow, or with <paramref name="bloom"/> the flare
    /// laid over it when the stage is driven.</summary>
    internal static void DrawGlow(CanvasDrawingSession ds, Shape g, Size size, bool bloom)
    {
        if (size.Width < 4 || size.Height < 4) return;
        ds.Transform = Place(size);
        using var env = Envelope(ds, g);
        var color = g.Filament != null ? CoreColor : HeaterColor;

        // A faint warm cast through the whole envelope.
        if (!g.Metal && !bloom) ds.FillGeometry(env, With(HeaterColor, 0.05));

        foreach (var glow in g.Glows)
        {
            float r = bloom ? glow.Radius * 1.7f : glow.Radius;
            var stops = new[]
            {
                new CanvasGradientStop { Position = 0, Color = With(color, (bloom ? 0.35 : 0.55) * (g.Metal ? 0.45 : 1)) },
                new CanvasGradientStop { Position = 0.45f, Color = With(HeaterColor, bloom ? 0.12 : 0.2) },
                new CanvasGradientStop { Position = 1, Color = With(HeaterColor, 0) },
            };
            using var halo = new CanvasRadialGradientBrush(ds, stops, CanvasEdgeBehavior.Clamp, CanvasAlphaMode.Premultiplied)
            { Center = glow.At, RadiusX = r, RadiusY = r };
            ds.FillEllipse(glow.At, r, r, halo);
            if (bloom || g.Filament != null || g.Metal) continue;

            // The visible end of the heater: a hot streak with a soft edge.
            var streak = R(glow.At.X - 1.1, glow.At.Y - 2.6, 2.2, 5.2);
            Blurred(ds, 1.6f, s =>
            {
                using var soft = RoundRect(s, streak, 1.1f);
                s.FillGeometry(soft, HeaterColor);
            });
            using var hot = RoundRect(ds, streak, 1.1f);
            ds.FillGeometry(hot, CoreColor);
        }

        if (g.Filament is { } fil)
        {
            using var join = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
            Blurred(ds, bloom ? 4 : 2, s =>
            {
                for (int i = 0; i < fil.Length - 1; i++) s.DrawLine(fil[i], fil[i + 1], HeaterColor, bloom ? 3 : 2.2f, join);
            });
            if (!bloom)
                for (int i = 0; i < fil.Length - 1; i++) ds.DrawLine(fil[i], fil[i + 1], Rgb(1, 0.9, 0.7), 1.0f, join);
        }
    }
}
