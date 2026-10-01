using System.Numerics;
using DSPiConsole.Core.Models;
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
/// What the subharmonic synthesizer makes, on a real dB scale: the three
/// program bands it listens to, the three subs it synthesizes an octave below
/// them, the LF boost bell over the dry path, and the ceiling the sub is held
/// under. Port of the macOS Console's SubharmBandView.
/// <para>
/// The bell is the analog prototype the firmware's bell implements, so it is a
/// true magnitude. A sub block is the steady-state level: band level plus the
/// divider's own 0.849 gain, times the bell there, so the axis reads as dBFS
/// for a full-scale band and the ceiling line sits where it bites.
/// </para>
/// </summary>
public sealed class SubharmBandGraph : UserControl
{
    private const float MinFreq = 16, MaxFreq = 250;
    private const float DbMin = -42;
    /// <summary>A band at +12 plus the divider gain and the bell at its maximum
    /// is about +16.6 dB, so the hottest setting stays on scale.</summary>
    private const float DbMax = 18;
    /// <summary>Room under the plot for the frequency labels.</summary>
    private const float AxisStrip = 13;
    /// <summary>Room above the plot for the source brackets and the octave
    /// arrows, so neither lands on the curve or the blocks.</summary>
    private const float AnnotationStrip = 38;
    private const float BoostHz = 70, BoostQ = 0.9f, DividerGain = 0.849f;

    private readonly CanvasControl _canvas = new();
    private readonly Dictionary<SubharmField, float> _live = new();
    private float _low, _high, _top = SubharmLimits.LevelMinDb, _boost, _ceiling = SubharmLimits.CeilingMaxDb;
    private bool _effectEnabled;

    public SubharmBandGraph()
    {
        Content = _canvas;
        _canvas.Draw += (_, e) => Draw(e.DrawingSession, (float)_canvas.ActualWidth, (float)_canvas.ActualHeight);
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
    }

    /// <summary>The committed values; clears any live overrides.</summary>
    public void SetValues(float low, float high, float top, float boost, float ceiling, bool enabled)
    {
        (_low, _high, _top, _boost, _ceiling, _effectEnabled) = (low, high, top, boost, ceiling, enabled);
        _live.Clear();
        _canvas.Invalidate();
    }

    /// <summary>A value during a drag, shown until the next <see cref="SetValues"/>.</summary>
    public void SetLive(SubharmField field, float value)
    {
        _live[field] = value;
        _canvas.Invalidate();
    }

    private float Get(SubharmField f, float committed) => _live.TryGetValue(f, out var v) ? v : committed;

    // ── Geometry ──

    private static float X(float freq, float w)
    {
        double lo = Math.Log10(MinFreq), hi = Math.Log10(MaxFreq);
        double v = Math.Log10(Math.Clamp(freq, MinFreq, MaxFreq));
        return (float)((v - lo) / (hi - lo) * w);
    }

    private static float Y(float db, float h)
    {
        float top = AnnotationStrip, bottom = h - AxisStrip;
        float c = Math.Clamp(db, DbMin, DbMax);
        return bottom - (c - DbMin) / (DbMax - DbMin) * (bottom - top);
    }

    private static float Baseline(float h) => h - AxisStrip;

    /// <summary>The 70 Hz bell in dB: the standard analog peaking prototype,
    /// whose peak gain A^2 is what the firmware's mix produces.</summary>
    private static float BellDb(float boost, float freq)
    {
        if (boost <= 0) return 0;
        double a = Math.Pow(10, boost / 40.0), w = freq / BoostHz;
        double b = (1 - w * w) * (1 - w * w);
        double num = b + Math.Pow(a * w / BoostQ, 2), den = b + Math.Pow(w / (a * BoostQ), 2);
        return (float)(10 * Math.Log10(num / den));
    }

    private static float SubDb(float band, float boost, float freq) =>
        band + 20 * MathF.Log10(DividerGain) + BellDb(boost, freq);

    private static bool Off(float band) => band <= SubharmLimits.LevelMinDb;

    // ── Drawing ──

    private static Color Gray(byte alpha) => Color.FromArgb(alpha, 128, 128, 128);
    private static Color Secondary(double opacity) => Color.FromArgb((byte)(opacity * 255), 200, 200, 205);
    private static Color With(Color c, double opacity) => Color.FromArgb((byte)(opacity * 255), c.R, c.G, c.B);

    private static readonly Color Accent = (Color)Application.Current.Resources["SystemAccentColor"];
    private static readonly Color Orange = Color.FromArgb(255, 255, 159, 10);
    private static readonly Color Purple = Color.FromArgb(255, 191, 90, 242);
    private static readonly Color Green = Color.FromArgb(255, 48, 209, 88);
    private static readonly Color Red = Color.FromArgb(255, 255, 69, 58);
    private static readonly Color LabelBack = Color.FromArgb(217, 30, 30, 32);

    private static CanvasTextFormat Font(float size, bool bold = false, bool mono = false) => new()
    {
        FontSize = size,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
        FontFamily = mono ? "Cascadia Code, Consolas" : "Segoe UI",
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    /// <summary>Text centred on a point, over a small backdrop when asked.</summary>
    private static void Label(CanvasDrawingSession ds, string text, float x, float y, CanvasTextFormat format, Color color, bool backdrop = false)
    {
        if (backdrop)
        {
            using var layout = new CanvasTextLayout(ds, text, format, 0, 0);
            var b = layout.LayoutBounds;
            ds.FillRectangle(new Rect(x - b.Width / 2 - 2, y - b.Height / 2, b.Width + 4, b.Height), LabelBack);
        }
        ds.DrawText(text, x, y, color, format);
    }

    private void Draw(CanvasDrawingSession ds, float w, float h)
    {
        if (w < 20 || h < AnnotationStrip + AxisStrip + 10) return;
        float low = Get(SubharmField.Low, _low), high = Get(SubharmField.High, _high), top = Get(SubharmField.Top, _top);
        float boost = Get(SubharmField.Boost, _boost), ceiling = Get(SubharmField.Ceiling, _ceiling);
        bool allOff = Off(low) && Off(high) && Off(top);

        DrawGrid(ds, w, h);

        if (_effectEnabled)
        {
            // The program bands, behind the subs so overlaps read as a stack.
            SourceBand(ds, 48, 72, w, h, Off(low));
            SourceBand(ds, 72, 112, w, h, Off(high));
            SourceBand(ds, 112, 160, w, h, Off(top));

            // The LF boost, only when it does something.
            if (boost > 0) BellCurve(ds, boost, w, h);

            if (!Off(low)) { SubBlock(ds, 24, 36, low, boost, Accent, w, h); OctaveArrow(ds, 60, 30, 15, w); }
            if (!Off(high)) { SubBlock(ds, 36, 56, high, boost, Orange, w, h); OctaveArrow(ds, 92, 46, 23, w); }
            if (!Off(top)) { SubBlock(ds, 56, 80, top, boost, Purple, w, h); OctaveArrow(ds, 136, 68, 31, w); }

            // Where the limiter starts holding the sub down, across the sub
            // range only, since it limits the sub and not the program.
            if (ceiling < SubharmLimits.CeilingMaxDb && !allOff) CeilingLine(ds, ceiling, w, h);

            if (allOff)
                Label(ds, "All bands off", w / 2, (AnnotationStrip + Baseline(h)) / 2, Font(10, bold: true), Secondary(0.6));
        }
        else
        {
            Label(ds, "Disabled", w / 2, (AnnotationStrip + Baseline(h)) / 2, Font(11, bold: true), Secondary(0.5));
        }

        foreach (int f in new[] { 20, 50, 100, 200 })
            Label(ds, f.ToString(), X(f, w), h - 5, Font(7, mono: true), Secondary(0.6));
    }

    private static void DrawGrid(CanvasDrawingSession ds, float w, float h)
    {
        var grid = Gray(38);
        foreach (float f in new[] { 20f, 50f, 100f, 200f })
            ds.DrawLine(X(f, w), AnnotationStrip, X(f, w), Baseline(h), grid, 0.5f);
        foreach (float db in new[] { 12f, -12f, -24f, -36f })
            ds.DrawLine(0, Y(db, h), w, Y(db, h), grid, 0.5f);
        ds.DrawLine(0, Baseline(h), w, Baseline(h), grid, 0.5f);
        ds.DrawLine(0, Y(0, h), w, Y(0, h), Gray(102), 0.5f);

        // Level labels hug the left edge, which nothing the effect makes reaches.
        foreach (int db in new[] { 12, 0, -12, -24, -36 })
            Label(ds, db > 0 ? $"+{db}" : $"{db}", 9, Y(db, h) - 5, Font(7, mono: true), Secondary(0.5));
    }

    private static void SourceBand(CanvasDrawingSession ds, float lo, float hi, float w, float h, bool dimmed)
    {
        float x0 = X(lo, w), x1 = X(hi, w);
        ds.FillRectangle(new Rect(x0, AnnotationStrip, Math.Max(0, x1 - x0), Math.Max(0, Baseline(h) - AnnotationStrip)),
                         Color.FromArgb((byte)(dimmed ? 10 : 26), 255, 255, 255));
        // A bracket over the band keeps neighbours apart where they meet.
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(x0 + 1, 9);
        pb.AddLine(x0 + 1, 5);
        pb.AddLine(x1 - 1, 5);
        pb.AddLine(x1 - 1, 9);
        pb.EndFigure(CanvasFigureLoop.Open);
        using var bracket = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(bracket, Secondary(dimmed ? 0.25 : 0.5), 1);
        Label(ds, $"{lo:0}-{hi:0}", (x0 + x1) / 2, 5, Font(7, bold: true, mono: true), Secondary(dimmed ? 0.4 : 0.85), backdrop: true);
    }

    private static void BellCurve(CanvasDrawingSession ds, float boost, float w, float h)
    {
        using var pb = new CanvasPathBuilder(ds);
        const int steps = 96;
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            float f = MathF.Pow(10, MathF.Log10(MinFreq) + t * (MathF.Log10(MaxFreq) - MathF.Log10(MinFreq)));
            var p = new Vector2(t * w, Y(BellDb(boost, f), h));
            if (i == 0) pb.BeginFigure(p); else pb.AddLine(p);
        }
        pb.EndFigure(CanvasFigureLoop.Open);
        using var curve = CanvasGeometry.CreatePath(pb);
        using var style = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
        ds.DrawGeometry(curve, With(Green, 0.8), 1.4f, style);
        Label(ds, "LF boost", X(BoostHz, w), Math.Max(AnnotationStrip + 5, Y(BellDb(boost, BoostHz), h) - 8),
              Font(7, bold: true), With(Green, 0.9), backdrop: true);
    }

    private static void CeilingLine(CanvasDrawingSession ds, float ceiling, float w, float h)
    {
        float y = Y(ceiling, h), x1 = X(80, w);
        using var dashes = new CanvasStrokeStyle { CustomDashStyle = new[] { 3f, 2f } };
        ds.DrawLine(0, y, x1, y, With(Red, 0.65), 1, dashes);
        Label(ds, "ceiling", x1 + 18, y, Font(7, bold: true), With(Red, 0.8), backdrop: true);
    }

    /// <summary>One synthesized sub band. Its top follows the bell across the
    /// band, so a boost visibly tilts it.</summary>
    private static void SubBlock(CanvasDrawingSession ds, float lo, float hi, float level, float boost, Color color, float w, float h)
    {
        float x0 = X(lo, w), x1 = X(hi, w), bottom = Baseline(h);
        float TopAt(float f) => Y(SubDb(level, boost, f), h);
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(x0, bottom);
        const int steps = 16;
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            pb.AddLine(x0 + t * (x1 - x0), TopAt(lo * MathF.Pow(hi / lo, t)));
        }
        pb.AddLine(x1, bottom);
        pb.EndFigure(CanvasFigureLoop.Closed);
        using var shape = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(shape, With(color, 0.4));
        ds.DrawGeometry(shape, With(color, 0.9), 1);

        // Inside the block when it is tall enough, just above it when not.
        float midTop = TopAt(MathF.Sqrt(lo * hi));
        bool inside = bottom - midTop > 18;
        Label(ds, $"{lo:0}-{hi:0}", (x0 + x1) / 2, inside ? midTop + 8 : midTop - 6, Font(7, bold: true, mono: true),
              inside ? Color.FromArgb(230, 255, 255, 255) : Color.FromArgb(178, 255, 255, 255));
    }

    /// <summary>A dashed hop from a program band down to the sub it makes, the
    /// one thing the axes do not say: that it is an octave.</summary>
    private static void OctaveArrow(CanvasDrawingSession ds, float from, float to, float y, float w)
    {
        float x0 = X(from, w), x1 = X(to, w);
        using var dashes = new CanvasStrokeStyle { CustomDashStyle = new[] { 2f, 2f } };
        ds.DrawLine(x0, y, x1 + 3, y, Color.FromArgb(77, 255, 255, 255), 1, dashes);
        ds.DrawLine(x1 + 4, y - 3, x1, y, Color.FromArgb(115, 255, 255, 255), 1);
        ds.DrawLine(x1, y, x1 + 4, y + 3, Color.FromArgb(115, 255, 255, 255), 1);
        Label(ds, "÷2", (x0 + x1) / 2, y, Font(6, bold: true, mono: true), Secondary(0.8), backdrop: true);
    }
}
