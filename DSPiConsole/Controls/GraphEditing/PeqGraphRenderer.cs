using System.Numerics;
using DSPiConsole.Core;
using DSPiConsole.Core.GraphEditing;
using DSPiConsole.Core.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace DSPiConsole.Controls.GraphEditing;

/// <summary>
/// Draws the editor's picture with Win2D: every band's lobe (faint, fading to
/// nothing at 0 dB), the outlines of hovered and selected bands, the edited
/// channel's combined curve with its glow, the dots, and the marquee. After the
/// macOS Console's Metal PeqGraphRenderer.
/// <para>
/// The lobes are one GPU pass (Shaders/PeqLobes.hlsl) over per-column curve
/// data, however many bands there are. Each band's response row is computed
/// only when that band changes, so a drag evaluates the dragged band alone and
/// hover and selection animations evaluate nothing.
/// </para>
/// </summary>
public sealed class PeqGraphRenderer : IDisposable
{
    private const int BandRows = PeqGraphEditor.Tuning.BandRows;
    /// <summary>Columns of curve data the lobe shader holds (PeqLobes.hlsl).</summary>
    private const int ShaderColumns = 1024;

    private static readonly Lazy<byte[]> LobeShaderCode = new(() =>
    {
        using var stream = typeof(PeqGraphRenderer).Assembly
            .GetManifestResourceStream("DSPiConsole.Controls.GraphEditing.Shaders.PeqLobes.bin")
            ?? throw new InvalidOperationException("PeqLobes.bin is not embedded");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    // ── Response table ──────────────────────────────────────────────────────
    // Per column of the plot: x, φ for the filter maths, one dB row per band,
    // the crossover (static) row, and the combined curve.

    private int _columns;
    private double[] _xs = Array.Empty<double>();
    private double[] _phis = Array.Empty<double>();
    private PeqGraphGeometry _tableGeometry;
    private readonly double[]?[] _rows = new double[BandRows][];
    private readonly FilterParams?[] _rowBands = new FilterParams?[BandRows];
    private double[] _staticRow = Array.Empty<double>();
    private FilterParams[] _tableStatics = Array.Empty<FilterParams>();
    private double[] _combined = Array.Empty<double>();
    private float _combinedOffset = float.NaN;
    private bool _combinedFlat;
    private bool _combinedStale = true;
    /// <summary>Bumped whenever any row changes, so the GPU copy follows.</summary>
    private int _tableVersion;

    // ── GPU state ───────────────────────────────────────────────────────────

    private PixelShaderEffect? _lobes;
    private readonly Vector4[] _curveData = new Vector4[ShaderColumns * 3];
    private readonly Vector4[] _colors = new Vector4[BandRows];
    private readonly Vector4[] _reaches = new Vector4[BandRows];
    private readonly int[] _uploadedRows = new int[BandRows];
    private int _uploadedVersion = -1;
    private int _uploadedCount = -1;
    private PeqGraphGeometry _uploadedGeometry;
    private GaussianBlurEffect? _glowBlur;
    private OpacityEffect? _glowFade;
    private readonly CanvasStrokeStyle _round = new()
    {
        LineJoin = CanvasLineJoin.Round,
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
    };
    private readonly CanvasStrokeStyle _dashes = new() { CustomDashStyle = new[] { 4f, 3f } };

    /// <summary>Band rows evaluated so far, for tests and tuning.</summary>
    public int RowPasses { get; private set; }

    public void Dispose()
    {
        _lobes?.Dispose();
        _glowBlur?.Dispose();
        _glowFade?.Dispose();
        _round.Dispose();
        _dashes.Dispose();
        _lobes = null;
        _glowBlur = null;
        _glowFade = null;
    }

    public void Draw(CanvasDrawingSession ds, PeqPicture pic, Color background)
    {
        var g = pic.Geometry;
        if (g.Width <= 4 || g.Height <= 4) return;
        EnsureTable(pic);
        // Bands are drawn on axes shifted by the level offset, which the
        // combined curve includes, so dots, fills and outlines stay on it.
        var bg = g with { DbTop = g.DbTop - pic.OffsetDb, DbBottom = g.DbBottom - pic.OffsetDb };
        float zero = (float)bg.Y(0);

        DrawLobes(ds, pic, bg, zero);

        // A band's outline dissolves as it nears 0 dB, where the band does
        // nothing, so no band ever draws a baseline across the graph. All the
        // outlines share one layer under that fade.
        bool anyOutline = false;
        foreach (var style in pic.BandStyles)
            if (style.LineOpacity > 0.001f && _rows[style.Row] != null) { anyOutline = true; break; }
        if (anyOutline)
        {
            using var fade = ZeroFade(ds, zero);
            using (ds.CreateLayer(fade))
            {
                foreach (var style in pic.BandStyles)
                {
                    if (style.LineOpacity <= 0.001f || _rows[style.Row] is not { } row) continue;
                    using var line = CurveGeometry(ds, row, bg);
                    ds.DrawGeometry(line, ToColor(style.Color, style.LineOpacity), 1.25f, _round);
                }
            }
        }

        using (var combined = CurveGeometry(ds, _combined, g))
        {
            if (pic.Glow)
            {
                // Glow: the combined curve stroked wide and blurred.
                using var glowList = new CanvasCommandList(ds);
                using (var gds = glowList.CreateDrawingSession())
                    gds.DrawGeometry(combined, ToColor(pic.CurveColor, 0.7f), pic.LineWidth * 2.5f, _round);
                _glowBlur ??= new GaussianBlurEffect { BlurAmount = 3.5f, BorderMode = EffectBorderMode.Soft };
                _glowFade ??= new OpacityEffect { Source = _glowBlur, Opacity = 0.85f };
                _glowBlur.Source = glowList;
                ds.DrawImage(_glowFade);
                _glowBlur.Source = null;
            }
            ds.DrawGeometry(combined, ToColor(pic.CurveColor, 1f), pic.LineWidth, _round);
        }

        foreach (var n in pic.Nodes)
        {
            var c = new Vector2((float)n.X, (float)n.Y);
            ds.FillCircle(c, (float)n.Radius, ToColor(n.Color, n.Opacity));
            if (n.Select > 0.001f)
            {
                // A selected dot grows a centre in the graph's background colour
                // as its selection eases in.
                float pip = 2.2f * n.Select;
                float a = Math.Min(n.Select * 2, 1f) * n.Opacity;
                ds.FillCircle(c, pip, Color.FromArgb((byte)(a * 255), background.R, background.G, background.B));
            }
        }

        if (pic.Marquee is { } m)
        {
            var rect = new Rect(m.X, m.Y, m.Width, m.Height);
            ds.FillRectangle(rect, Color.FromArgb(15, 255, 255, 255));
            ds.DrawRectangle(rect, Color.FromArgb(115, 255, 255, 255), 1f, _dashes);
        }
    }

    // ── Lobes (GPU) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every band's lobe in one shader pass. The curve data is uploaded only
    /// when a row (or the set of bands drawn) changes; colours, opacities and
    /// reaches go up every frame, since hover and selection animate them.
    /// <paramref name="g"/> is the band axes (offset applied).
    /// </summary>
    private void DrawLobes(CanvasDrawingSession ds, PeqPicture pic, PeqGraphGeometry g, float zero)
    {
        int count = 0;
        bool rowsMoved = false;
        foreach (var style in pic.BandStyles)
        {
            if (count == BandRows) break;
            if (style.FillOpacity <= 0.001f || _rows[style.Row] == null) continue;
            float top = (float)Math.Max(g.Y(style.ReachHigh) - 2, 0);
            float bottom = (float)Math.Min(g.Y(style.ReachLow) + 2, g.Height);
            if (bottom - top <= 0.5f) continue;
            _colors[count] = new Vector4(style.Color.R, style.Color.G, style.Color.B, style.FillOpacity);
            _reaches[count] = new Vector4(top, bottom, 0, 0);
            if (_uploadedRows[count] != style.Row) rowsMoved = true;
            _uploadedRows[count] = style.Row;
            count++;
        }
        if (count == 0) return;

        _lobes ??= new PixelShaderEffect(LobeShaderCode.Value);
        int columns = Math.Min(_columns, ShaderColumns);
        float width = (float)g.Width, height = (float)g.Height;
        if (rowsMoved || count != _uploadedCount || _tableVersion != _uploadedVersion || g != _uploadedGeometry)
        {
            // Columns beyond what the shader holds are resampled; the lobes are
            // soft enough that a few DIPs between samples never shows.
            for (int c = 0; c < columns; c++)
            {
                double at = _columns <= ShaderColumns ? c : (double)c * (_columns - 1) / (columns - 1);
                int i0 = (int)at, i1 = Math.Min(i0 + 1, _columns - 1);
                double f = at - i0;
                for (int q = 0; q < 3; q++)
                {
                    var v = Vector4.Zero;
                    for (int lane = 0; lane < 4; lane++)
                    {
                        int k = q * 4 + lane;
                        if (k >= count) break;
                        var row = _rows[_uploadedRows[k]]!;
                        float y = CurveY(row[i0] + (row[i1] - row[i0]) * f, g);
                        v = lane switch { 0 => v with { X = y }, 1 => v with { Y = y }, 2 => v with { Z = y }, _ => v with { W = y } };
                    }
                    _curveData[c * 3 + q] = v;
                }
            }
            _lobes.Properties["curves"] = _curveData;
            _uploadedVersion = _tableVersion;
            _uploadedCount = count;
            _uploadedGeometry = g;
        }
        _lobes.Properties["count"] = (float)count;
        _lobes.Properties["columns"] = (float)columns;
        _lobes.Properties["colStep"] = width / (columns - 1);
        _lobes.Properties["zero"] = zero;
        _lobes.Properties["pxPerDip"] = ds.Dpi / 96f;
        _lobes.Properties["colors"] = _colors;
        _lobes.Properties["reaches"] = _reaches;
        var area = new Rect(0, 0, width, height);
        ds.DrawImage(_lobes, area, area);
    }

    // ── Curves ──────────────────────────────────────────────────────────────

    private static Color ToColor((float R, float G, float B) c, float alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255),
                       (byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255));

    private static float CurveY(double db, PeqGraphGeometry g)
    {
        if (!double.IsFinite(db)) db = -400;
        return (float)Math.Clamp(g.Y(db), -2 * g.Height, 3 * g.Height);
    }

    /// <summary>
    /// A row as an open path, one point per column where the curve bends and
    /// fewer where it runs straight (most of a curve lies along 0 dB or a
    /// constant slope), so a stroke costs what its shape needs.
    /// </summary>
    private CanvasGeometry CurveGeometry(ICanvasResourceCreator rc, double[] row, PeqGraphGeometry g)
    {
        const float bend = 0.02f;
        const int maxRun = 6;
        using var pb = new CanvasPathBuilder(rc);
        int n = row.Length;
        float prevY = CurveY(row[0], g), y = CurveY(row[Math.Min(1, n - 1)], g);
        pb.BeginFigure((float)_xs[0], prevY);
        int run = 0;
        for (int i = 1; i < n - 1; i++)
        {
            float nextY = CurveY(row[i + 1], g);
            run++;
            if (run >= maxRun || Math.Abs(prevY - 2 * y + nextY) > bend)
            {
                pb.AddLine((float)_xs[i], y);
                run = 0;
            }
            prevY = y;
            y = nextY;
        }
        if (n > 1) pb.AddLine((float)_xs[n - 1], CurveY(row[n - 1], g));
        pb.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(pb);
    }

    /// <summary>An opacity mask clear within 2 px of 0 dB and solid from 10 px.</summary>
    private static CanvasLinearGradientBrush ZeroFade(ICanvasResourceCreator rc, float zero)
    {
        var stops = new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(255, 255, 255, 255) },
            new CanvasGradientStop { Position = 0.4f, Color = Color.FromArgb(0, 255, 255, 255) },
            new CanvasGradientStop { Position = 0.6f, Color = Color.FromArgb(0, 255, 255, 255) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(255, 255, 255, 255) },
        };
        return new CanvasLinearGradientBrush(rc, stops, CanvasEdgeBehavior.Clamp, CanvasAlphaMode.Straight)
        {
            StartPoint = new Vector2(0, zero - 10),
            EndPoint = new Vector2(0, zero + 10),
        };
    }

    // ── Response table ──────────────────────────────────────────────────────

    private static bool SameShape(FilterParams a, FilterParams b) =>
        a.Type == b.Type && a.Frequency == b.Frequency && a.Q == b.Q && a.Gain == b.Gain && a.Qp == b.Qp;

    private void EnsureTable(PeqPicture pic)
    {
        var g = pic.Geometry;
        int columns = Math.Clamp((int)Math.Ceiling(g.Width) + 1, 2, 8192);
        if (columns != _columns || g.Width != _tableGeometry.Width
            || g.MinFreq != _tableGeometry.MinFreq || g.MaxFreq != _tableGeometry.MaxFreq)
        {
            // New axis: every row is recomputed below.
            _columns = columns;
            _tableGeometry = g;
            _xs = new double[columns];
            _phis = new double[columns];
            for (int i = 0; i < columns; i++)
            {
                _xs[i] = g.Width * i / (columns - 1);
                _phis[i] = DspMath.PhiAt(g.Freq(_xs[i]));
            }
            Array.Clear(_rows);
            Array.Clear(_rowBands);
            _tableStatics = Array.Empty<FilterParams>();
            _staticRow = new double[columns];
            _combined = new double[columns];
            _combinedStale = true;
        }

        // Only the bands that changed are evaluated again.
        for (int r = 0; r < BandRows; r++)
        {
            var band = r < pic.Bands.Count ? pic.Bands[r] : null;
            var had = _rowBands[r];
            if (band == null)
            {
                if (_rows[r] == null) continue;
                _rows[r] = null;
                _rowBands[r] = null;
                _combinedStale = true;
                _tableVersion++;
                continue;
            }
            if (had != null && _rows[r] != null && had.SameAs(band)) continue;
            // Bypass alone changes only the combined curve.
            if (had == null || _rows[r] == null || !SameShape(had, band))
            {
                _rows[r] ??= new double[columns];
                DspMath.CascadeDbInto(DspMath.SectionsFor(band), _phis, _rows[r]);
                RowPasses++;
                _tableVersion++;
            }
            _rowBands[r] = band.Clone();
            _combinedStale = true;
        }

        if (!PeqEditorConfig.BandsSame(_tableStatics, pic.Statics))
        {
            Array.Clear(_staticRow);
            var scratch = new double[columns];
            foreach (var s in pic.Statics)
            {
                if (s.Type == FilterType.Flat || s.Bypass || !s.IsActive) continue;
                DspMath.CascadeDbInto(DspMath.SectionsFor(s), _phis, scratch);
                for (int i = 0; i < columns; i++) _staticRow[i] += scratch[i];
            }
            _tableStatics = pic.Statics.Select(b => b.Clone()).ToArray();
            _combinedStale = true;
        }

        if (_combinedStale || _combinedOffset != pic.OffsetDb || _combinedFlat != pic.Flat)
        {
            // A bypassed band still draws its own dimmed shape, but stays out of
            // the combined curve; Flat (the master EQ bypass) leaves only the offset.
            Array.Fill(_combined, pic.OffsetDb);
            if (!pic.Flat)
            {
                for (int r = 0; r < BandRows; r++)
                {
                    if (_rows[r] is not { } row || _rowBands[r] is not { Bypass: false }) continue;
                    for (int i = 0; i < columns; i++) _combined[i] += row[i];
                }
                for (int i = 0; i < columns; i++) _combined[i] += _staticRow[i];
            }
            _combinedOffset = pic.OffsetDb;
            _combinedFlat = pic.Flat;
            _combinedStale = false;
        }
    }
}
