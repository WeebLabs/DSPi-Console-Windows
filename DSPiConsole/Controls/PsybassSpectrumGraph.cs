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
/// A schematic, not a magnitude, of what psychoacoustic bass does: the original
/// band below the cutoff, lowered by Original Bass, and the synthesized
/// harmonics from the cutoff to four times it at the Harmonics level, with fc
/// and 4fc marked. Port of the macOS Console's PsybassSpectrumView.
/// </summary>
public sealed class PsybassSpectrumGraph : UserControl
{
    private const double MinFreq = 20, MaxFreq = 20000;
    private const float AxisStrip = 14;
    private static readonly Color Accent = (Color)Application.Current.Resources["SystemAccentColor"];
    private static readonly Color Orange = Color.FromArgb(255, 255, 159, 10);
    private static readonly Color GridColor = Color.FromArgb(38, 128, 128, 128);
    private static readonly Color LabelColor = Color.FromArgb(153, 200, 200, 205);

    private readonly CanvasControl _canvas = new();
    private float _cutoff = PsybassLimits.CutoffDefaultHz, _harmonics, _original;
    private float? _liveCutoff, _liveHarmonics, _liveOriginal;
    private bool _enabled;

    public PsybassSpectrumGraph()
    {
        Content = _canvas;
        _canvas.Draw += (_, e) => Draw(e.DrawingSession, (float)_canvas.ActualWidth, (float)_canvas.ActualHeight);
    }

    /// <summary>Releases the canvas for good; the host window calls it on
    /// close. Not on Unloaded: a window that swaps its body out and back in
    /// would get a canvas that never draws again.</summary>
    public void Dispose() => _canvas.RemoveFromVisualTree();

    /// <summary>The committed values. A drag's live values are dropped only
    /// when these change, so an unrelated refresh mid-drag does not snap the
    /// picture back.</summary>
    public void SetValues(float cutoff, float harmonicsDb, float originalDb, bool enabled)
    {
        if ((cutoff, harmonicsDb, originalDb) != (_cutoff, _harmonics, _original))
            _liveCutoff = _liveHarmonics = _liveOriginal = null;
        (_cutoff, _harmonics, _original, _enabled) = (cutoff, harmonicsDb, originalDb, enabled);
        _canvas.Invalidate();
    }

    public void SetLiveCutoff(float hz) { _liveCutoff = hz; _canvas.Invalidate(); }
    public void SetLiveHarmonics(float db) { _liveHarmonics = db; _canvas.Invalidate(); }
    public void SetLiveOriginal(float db) { _liveOriginal = db; _canvas.Invalidate(); }

    private static float X(double freq, float w)
    {
        double f = Math.Clamp(freq, MinFreq, MaxFreq);
        return (float)((Math.Log10(f) - Math.Log10(MinFreq)) / (Math.Log10(MaxFreq) - Math.Log10(MinFreq)) * w);
    }

    private static CanvasTextFormat Mono(bool bold = false) => new()
    {
        FontSize = 7, FontFamily = "Cascadia Code",
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
        HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    private void Draw(CanvasDrawingSession ds, float w, float h)
    {
        if (w < 20 || h < AxisStrip + 10) return;
        float cutoff = _liveCutoff ?? _cutoff, harmonics = _liveHarmonics ?? _harmonics, original = _liveOriginal ?? _original;
        float baseline = h - AxisStrip;

        foreach (double f in new[] { 100.0, 1000, 10000 }) ds.DrawLine(X(f, w), 0, X(f, w), baseline, GridColor, 0.5f);
        ds.DrawLine(0, baseline, w, baseline, GridColor, 0.5f);

        if (_enabled)
        {
            float fc = X(cutoff, w), fc4 = X(cutoff * 4, w);
            // 0 dB is a full band and -60 dB none; harmonics run -24 to +12 dB.
            float originalH = baseline * Math.Clamp((original + 60) / 60, 0, 1);
            float harmonicsH = baseline * Math.Clamp((harmonics + 24) / 36, 0, 1);
            ds.FillRectangle(new Rect(0, baseline - originalH, Math.Max(0, fc), originalH), With(Accent, 0.35));
            ds.FillRectangle(new Rect(fc, baseline - harmonicsH, Math.Max(0, fc4 - fc), harmonicsH), With(Orange, 0.55));

            using var dash = new CanvasStrokeStyle { CustomDashStyle = new[] { 3f, 3f } };
            using var fine = new CanvasStrokeStyle { CustomDashStyle = new[] { 2f, 3f } };
            ds.DrawLine(fc, 0, fc, baseline, Color.FromArgb(102, 255, 255, 255), 1, dash);
            ds.DrawLine(fc4, 0, fc4, baseline, Color.FromArgb(51, 255, 255, 255), 1, fine);
            using var bold = Mono(bold: true);
            ds.DrawText("fc", new Rect(fc - 12, 2, 24, 12), Color.FromArgb(153, 255, 255, 255), bold);
            ds.DrawText("4fc", new Rect(fc4 - 12, 2, 24, 12), Color.FromArgb(102, 255, 255, 255), bold);

            using var legend = new CanvasTextFormat
            {
                FontSize = 7, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap,
            };
            var box = new Rect(w - 74, 8, 66, 28);
            ds.FillRoundedRectangle(box, 4, 4, Color.FromArgb(217, 30, 30, 32));
            (string Name, Color Color)[] items = { ("Original", Accent), ("Harmonics", Orange) };
            for (int i = 0; i < items.Length; i++)
            {
                float y = (float)box.Y + 3 + i * 11 + 5.5f;
                ds.FillRoundedRectangle(new Rect(box.X + 6, y - 3, 10, 6), 1, 1, items[i].Color);
                ds.DrawText(items[i].Name, new Rect(box.X + 20, y - 6, box.Width - 22, 12), Color.FromArgb(255, 230, 230, 230), legend);
            }
        }
        else
        {
            using var disabled = new CanvasTextFormat
            {
                FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
            };
            ds.DrawText("Disabled", new Rect(0, 0, w, h), Color.FromArgb(128, 200, 200, 205), disabled);
        }

        using var labels = Mono();
        foreach (double f in new[] { 100.0, 1000, 10000 })
            ds.DrawText(f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}", new Rect(X(f, w) - 15, h - 11, 30, 12), LabelColor, labels);
    }

    private static Color With(Color c, double opacity) => Color.FromArgb((byte)(opacity * 255), c.R, c.G, c.B);
}
