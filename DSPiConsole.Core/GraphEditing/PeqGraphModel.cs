using System.Globalization;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.GraphEditing;

// The pure logic behind on-graph PEQ editing: coordinate mapping, what each
// filter type's dot means, the shapes a click creates, band colours and value
// text. No views here, so all of it is unit-testable. A port of the macOS
// Console's PeqGraphModel.swift; behaviour should stay in step with it.

/// <summary>
/// Point ↔ frequency / dB mapping for the plot area: log frequency across the
/// full width, dB top to bottom. Coordinates are local to the plot rect, with
/// y growing downward.
/// </summary>
public readonly record struct PeqGraphGeometry(
    double Width, double Height, double MinFreq, double MaxFreq, double DbTop, double DbBottom)
{
    private double LogMin => Math.Log10(Math.Max(MinFreq, 1));
    private double LogSpan => Math.Max(Math.Log10(Math.Max(MaxFreq, 2)) - LogMin, 1e-6);
    public double DbSpan => Math.Max(DbTop - DbBottom, 1e-6);

    public double X(double freq) => (Math.Log10(Math.Max(freq, 1e-3)) - LogMin) / LogSpan * Width;
    public double Freq(double x) => Math.Pow(10, LogMin + x / Math.Max(Width, 1) * LogSpan);
    public double Y(double db) => (DbTop - db) / DbSpan * Height;
    public double Db(double y) => DbTop - y / Math.Max(Height, 1) * DbSpan;

    /// <summary>dB per point, for turning a vertical drag into a gain change.</summary>
    public double DbPerPoint => DbSpan / Math.Max(Height, 1);
}

/// <summary>The shapes the graph creates and offers, named as FabFilter names
/// them. Each maps onto the firmware's types; an order picks the 6 dB/oct
/// (first order) or 12 dB/oct (second order) variant where both exist.</summary>
public enum PeqShape { Bell, LowShelf, LowCut, HighShelf, HighCut, Notch, AllPass }

public static class PeqShapes
{
    public static readonly PeqShape[] All =
        { PeqShape.Bell, PeqShape.LowShelf, PeqShape.LowCut, PeqShape.HighShelf, PeqShape.HighCut, PeqShape.Notch, PeqShape.AllPass };

    public static string Title(this PeqShape shape) => shape switch
    {
        PeqShape.Bell => "Bell",
        PeqShape.LowShelf => "Low Shelf",
        PeqShape.LowCut => "Low Cut",
        PeqShape.HighShelf => "High Shelf",
        PeqShape.HighCut => "High Cut",
        PeqShape.Notch => "Notch",
        _ => "All Pass",
    };

    /// <summary>The firmware type for this shape at <paramref name="order"/>
    /// (1 or 2), or null when the shape has no such order.</summary>
    public static FilterType? Type(this PeqShape shape, int order) => (shape, order) switch
    {
        (PeqShape.Bell, 2) => FilterType.Peaking,
        (PeqShape.LowShelf, 2) => FilterType.LowShelf,
        (PeqShape.LowShelf, 1) => FilterType.LowShelf1,
        (PeqShape.LowCut, 2) => FilterType.HighPass,
        (PeqShape.LowCut, 1) => FilterType.HighPass1,
        (PeqShape.HighShelf, 2) => FilterType.HighShelf,
        (PeqShape.HighShelf, 1) => FilterType.HighShelf1,
        (PeqShape.HighCut, 2) => FilterType.LowPass,
        (PeqShape.HighCut, 1) => FilterType.LowPass1,
        (PeqShape.Notch, 2) => FilterType.Notch,
        (PeqShape.AllPass, 2) => FilterType.AllPass,
        (PeqShape.AllPass, 1) => FilterType.AllPass1,
        _ => null,
    };

    /// <summary>The shape and order of a firmware type; null for types the graph
    /// does not edit (off, crossovers, the Linkwitz Transform).</summary>
    public static (PeqShape Shape, int Order)? Of(FilterType type)
    {
        foreach (var shape in All)
            foreach (int order in new[] { 2, 1 })
                if (shape.Type(order) == type) return (shape, order);
        return null;
    }

    /// <summary>Slope labels for the two orders, when a shape has both.</summary>
    public static string OrderTitle(this PeqShape shape, int order) =>
        shape == PeqShape.AllPass
            ? (order == 1 ? "1st Order" : "2nd Order")
            : (order == 1 ? "6 dB/oct" : "12 dB/oct");

    /// <summary>The compact label for an order on the shape page: the slope in
    /// dB, or the phase for an all-pass, whose orders are not slopes.</summary>
    public static string OrderLabel(this PeqShape shape, int order) =>
        shape == PeqShape.AllPass ? (order == 1 ? "180°" : "360°") : (order == 1 ? "6 dB" : "12 dB");

    public static bool IsCut(this PeqShape shape) => shape is PeqShape.LowCut or PeqShape.HighCut;

    /// <summary>The two-letter code the rest of the app shows for this shape.</summary>
    public static string Code(this PeqShape shape) => shape switch
    {
        PeqShape.Bell => "PK",
        PeqShape.LowShelf => "LS",
        PeqShape.LowCut => "LC",
        PeqShape.HighShelf => "HS",
        PeqShape.HighCut => "HC",
        PeqShape.Notch => "NT",
        _ => "AP",
    };

    /// <summary>The order a shape creates on its own: second where available.</summary>
    public static int? DefaultOrder(PeqShape shape, IReadOnlySet<FilterType> available)
    {
        foreach (int order in new[] { 2, 1 })
            if (shape.Type(order) is { } t && available.Contains(t)) return order;
        return null;
    }

    /// <summary>The orders of a shape the firmware supports, ascending.</summary>
    public static int[] Orders(PeqShape shape, IReadOnlySet<FilterType> available) =>
        new[] { 1, 2 }.Where(o => shape.Type(o) is { } t && available.Contains(t)).ToArray();
}

/// <summary>What a band's dot stands for, which decides where it sits and what
/// a vertical drag changes.</summary>
public readonly record struct PeqNodeRole(PeqNodeRole.RoleKind Kind, double Scale = 1, double FixedDb = 0)
{
    public enum RoleKind
    {
        /// <summary>The dot sits on the band's own curve at its frequency,
        /// <see cref="Scale"/> dB per dB of gain (1 for a bell, about one half
        /// for a shelf), and follows the cursor by changing gain.</summary>
        Gain,
        /// <summary>A second-order cut: the dot sits at the curve's level at the
        /// cutoff, which is 20·log10(Q), so following the cursor changes Q.</summary>
        Resonance,
        /// <summary>Fixed height (0 dB for a notch or all-pass, the cutoff level
        /// of a first-order cut). A drag moves frequency only; Q is the wheel's.</summary>
        Fixed,
        /// <summary>Shown and selectable, but not dragged (the Linkwitz
        /// Transform, whose four values are edited in its own panel).</summary>
        Locked,
    }

    public static PeqNodeRole Of(FilterParams p)
    {
        var probe = p.Clone();
        probe.Bypass = false;
        probe.IsActive = true;
        switch (p.Type)
        {
            case FilterType.Peaking or FilterType.LowShelf or FilterType.HighShelf
                or FilterType.LowShelf1 or FilterType.HighShelf1:
                // Measured rather than assumed, so the dot stays on the curve for
                // whatever shelf formula the firmware uses.
                probe.Gain = 12;
                double atF0 = DspMath.ResponseAt(probe.Frequency, new[] { probe });
                return new PeqNodeRole(RoleKind.Gain, Math.Max(Math.Abs(atF0 / 12), 0.05));
            case FilterType.LowPass or FilterType.HighPass:
                return new PeqNodeRole(RoleKind.Resonance);
            case FilterType.LowPass1 or FilterType.HighPass1:
                return new PeqNodeRole(RoleKind.Fixed, FixedDb: DspMath.ResponseAt(probe.Frequency, new[] { probe }));
            case FilterType.LinkwitzTransform:
                return new PeqNodeRole(RoleKind.Locked, FixedDb: DspMath.ResponseAt(probe.Frequency, new[] { probe }));
            default:
                return new PeqNodeRole(RoleKind.Fixed, FixedDb: 0);
        }
    }

    /// <summary>The dot's height in dB for <paramref name="p"/>.</summary>
    public double DbFor(FilterParams p) => Kind switch
    {
        RoleKind.Gain => p.Gain * Scale,
        RoleKind.Resonance => 20 * Math.Log10(Math.Max(p.Q, 0.01)),
        _ => FixedDb,
    };

    /// <summary>The gain scale for a gain role, 1 otherwise.</summary>
    public double GainScale => Kind == RoleKind.Gain ? Scale : 1;
}

/// <summary>Limits the graph enforces. Q and frequency match the firmware's own
/// clamps; gain is FabFilter's ±30 dB, which the firmware does not bound itself.</summary>
public static class PeqLimits
{
    public const double GainMin = -30, GainMax = 30;
    public const double QMin = 0.1, QMax = 20;
    public const double FreqMin = 10;
    public static double FreqMax => DspMath.SampleRate * 0.45;

    public static double Clamp(double v, double lo, double hi) => Math.Min(Math.Max(v, lo), hi);
    public static double ClampGain(double v) => Clamp(v, GainMin, GainMax);
    public static double ClampQ(double v) => Clamp(v, QMin, QMax);
    public static double ClampFreq(double v) => Clamp(v, FreqMin, FreqMax);
}

public static class PeqFilterExtensions
{
    /// <summary>True for the bands the graph draws a dot for.</summary>
    public static bool IsGraphBand(this FilterParams p) => p.Type != FilterType.Flat && !p.Type.IsCrossover();

    /// <summary>The band as <paramref name="newType"/>, carrying over what
    /// carries: entering a Linkwitz Transform seeds a neutral target, leaving
    /// one clears its fp-in-gain.</summary>
    public static FilterParams Retyped(this FilterParams p, FilterType newType)
    {
        var q = p.Clone();
        q.Type = newType;
        if (newType.IsLinkwitzTransform() && !p.Type.IsLinkwitzTransform())
        {
            q.Frequency = Math.Max(p.Frequency, (float)PeqLimits.FreqMin);
            q.Q = (float)PeqLimits.ClampQ(p.Q);
            q.Gain = q.Frequency;
            q.Qp = q.Q;
        }
        else if (!newType.IsLinkwitzTransform() && p.Type.IsLinkwitzTransform())
        {
            q.Gain = 0;
        }
        return q;
    }

    /// <summary>A band part-way to <paramref name="target"/>, for animating a
    /// change made elsewhere: frequency and Q move in the log domain, as the eye
    /// reads them. A change of type cannot be blended, so it lands at once.</summary>
    public static FilterParams Interpolated(this FilterParams from, FilterParams target, double t)
    {
        if (from.Type != target.Type || from.Bypass != target.Bypass || t >= 1) return target.Clone();
        var p = target.Clone();
        p.Frequency = (float)Math.Exp(Math.Log(from.Frequency) + (Math.Log(target.Frequency) - Math.Log(from.Frequency)) * t);
        double q0 = Math.Max(from.Q, 0.01), q1 = Math.Max(target.Q, 0.01);
        p.Q = (float)Math.Exp(Math.Log(q0) + (Math.Log(q1) - Math.Log(q0)) * t);
        p.Gain = from.Gain + (target.Gain - from.Gain) * (float)t;
        return p;
    }

    /// <summary>Exact field-for-field equality, for change detection (the
    /// model's own Equals tolerates rounding noise).</summary>
    public static bool SameAs(this FilterParams a, FilterParams b) =>
        a.Type == b.Type && a.Frequency == b.Frequency && a.Q == b.Q && a.Gain == b.Gain
        && a.Bypass == b.Bypass && a.Qp == b.Qp;
}

/// <summary>Which band a double-click on the graph makes: always a bell at the
/// pointer's frequency and level, wherever it lands; other shapes come from the
/// chip's shape page or the Ctrl-click card. Dragging the curve itself makes a
/// shelf near either end and a bell elsewhere.</summary>
public static class PeqCreation
{
    public const double ShelfZone = 0.12;

    public static FilterParams Band(double x, double y, PeqGraphGeometry g, IReadOnlySet<FilterType> available, bool fromCurve)
    {
        double fx = x / Math.Max(g.Width, 1);
        double freq = PeqLimits.ClampFreq(g.Freq(x));
        var p = new FilterParams(FilterType.Peaking, (float)freq, 1f, 0f);
        if (fromCurve)
        {
            if (fx < ShelfZone && available.Contains(FilterType.LowShelf)) { p.Type = FilterType.LowShelf; p.Q = 0.707f; }
            else if (fx > 1 - ShelfZone && available.Contains(FilterType.HighShelf)) { p.Type = FilterType.HighShelf; p.Q = 0.707f; }
            return p;
        }
        p.Gain = (float)PeqLimits.ClampGain(g.Db(y));
        return p;
    }
}

/// <summary>One hue per band, so a band keeps its colour in the list below the
/// graph. Soft, mid-saturation colours so the dots read as part of the app.</summary>
public static class PeqBandPalette
{
    private static readonly (float R, float G, float B)[] Rgb =
    {
        (0.93f, 0.47f, 0.45f), // coral
        (0.95f, 0.64f, 0.36f), // orange
        (0.92f, 0.79f, 0.40f), // amber
        (0.55f, 0.80f, 0.52f), // sage
        (0.36f, 0.77f, 0.68f), // teal
        (0.44f, 0.68f, 0.94f), // sky
        (0.58f, 0.60f, 0.94f), // periwinkle
        (0.73f, 0.57f, 0.92f), // lavender
        (0.89f, 0.54f, 0.72f), // rose
        (0.80f, 0.62f, 0.50f), // clay
    };

    public static (float R, float G, float B) Color(int band) => Rgb[((band % Rgb.Length) + Rgb.Length) % Rgb.Length];

    public static (byte R, byte G, byte B) Bytes(int band)
    {
        var c = Color(band);
        return ((byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255));
    }
}

/// <summary>Display and entry of band values, including FabFilter's shortcuts:
/// "2k" for 2000 Hz and note names such as "A4" or "C#2+13" (cents).</summary>
public static class PeqValueText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Frequency(double hz)
    {
        if (hz >= 10_000) return (hz / 1000).ToString("0.00", Inv) + " kHz";
        if (hz >= 1000) return (hz / 1000).ToString("0.000", Inv) + " kHz";
        if (hz >= 100) return hz.ToString("0.0", Inv) + " Hz";
        return hz.ToString("0.00", Inv) + " Hz";
    }

    public static string ShortFrequency(double hz)
    {
        if (hz >= 1000) return (hz / 1000).ToString(hz >= 10_000 ? "0.0" : "0.00", Inv) + "k";
        return hz.ToString(hz >= 100 ? "0" : "0.0", Inv);
    }

    public static string Gain(double db)
    {
        double v = Math.Abs(db) < 0.005 ? 0 : db;
        return (v >= 0 ? "+" : "") + v.ToString("0.00", Inv) + " dB";
    }

    public static string Q(double q) => q.ToString("0.000", Inv);

    /// <summary>Two decimal places, truncated rather than rounded, so the chip
    /// never shows a value the band does not quite have. The small epsilon keeps
    /// float noise (1.1 stored as 1.0999999) from dropping a digit.</summary>
    public static string Truncated(double v, bool sign = false)
    {
        double t = Math.Floor(Math.Abs(v) * 100 + 1e-5) / 100;
        string text = t.ToString("0.00", Inv);
        if (t == 0) return sign ? "+" + text : text;
        return v < 0 ? "-" + text : (sign ? "+" + text : text);
    }

    /// <summary>A frequency as (number, unit) for the chip.</summary>
    public static (string Number, string Unit) FrequencyParts(double hz) =>
        hz >= 1000 ? (Truncated(hz / 1000), "kHz") : (Truncated(hz), "Hz");

    public static double? ParseFrequency(string text)
    {
        string s = text.Trim().ToLowerInvariant();
        if (ParseNote(s) is { } note) return note;
        s = s.Replace(" ", "");
        double scale = 1;
        if (s.EndsWith("khz")) { scale = 1000; s = s[..^3]; }
        else if (s.EndsWith("hz")) { s = s[..^2]; }
        if (s.EndsWith("k")) { scale = 1000; s = s[..^1]; }
        if (!double.TryParse(s, NumberStyles.Float, Inv, out double v) || !double.IsFinite(v)) return null;
        return v * scale;
    }

    public static double? ParseNumber(string text, string unit)
    {
        string s = text.Trim().ToLowerInvariant().Replace(" ", "");
        if (unit.Length > 0 && s.EndsWith(unit)) s = s[..^unit.Length];
        if (s.StartsWith("q")) s = s[1..];
        if (s.StartsWith("+")) s = s[1..];
        if (!double.TryParse(s, NumberStyles.Float, Inv, out double v) || !double.IsFinite(v)) return null;
        return v;
    }

    /// <summary>"A4" = 440 Hz; "C#2+13" is C sharp 2 raised 13 cents. Octave
    /// numbers follow the C4 = middle C convention.</summary>
    public static double? ParseNote(string s)
    {
        if (s.Length == 0) return null;
        int semitone = s[0] switch { 'c' => 0, 'd' => 2, 'e' => 4, 'f' => 5, 'g' => 7, 'a' => 9, 'b' => 11, _ => -1 };
        if (semitone < 0) return null;
        int i = 1;
        if (i < s.Length && s[i] == '#') { semitone += 1; i++; }
        else if (i < s.Length && s[i] == 'b' && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || s[i + 1] == '-'))
        {
            semitone -= 1; i++;
        }
        string octaveText = "";
        if (i < s.Length && s[i] == '-') { octaveText += "-"; i++; }
        while (i < s.Length && char.IsDigit(s[i])) { octaveText += s[i]; i++; }
        if (!int.TryParse(octaveText, NumberStyles.AllowLeadingSign, Inv, out int octave)) return null;
        double cents = 0;
        if (i < s.Length)
        {
            if (!double.TryParse(s[i..], NumberStyles.Float, Inv, out cents)) return null;
        }
        double midi = (octave + 1) * 12 + semitone + cents / 100;
        return 440 * Math.Pow(2, (midi - 69) / 12);
    }
}
