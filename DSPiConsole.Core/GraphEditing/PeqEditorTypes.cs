using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.GraphEditing;

/// <summary>
/// Modifier keys as the editor reads them. The macOS Console uses three
/// modifiers where Windows has two free, so they map as follows:
/// Command → <see cref="Ctrl"/>, Option → <see cref="Alt"/>, Shift → Shift.
/// macOS's Control ("scale the selection's gains in proportion") becomes Ctrl
/// pressed or held after a drag has begun: Ctrl held at the press already
/// means a Q drag, and a Q drag ignores proportional scaling, so the two never
/// collide.
/// </summary>
[Flags]
public enum PeqMods
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
}

public enum PeqKey { Other, Delete, Escape, Tab, Left, Right, Up, Down, A }

public enum PeqCursor { Arrow, OpenHand, ClosedHand, UpDown }

public enum PeqHudField { Freq = 0, Gain = 1, Q = 2 }

public enum PeqAdjustPhase { Began, Changed, Ended }

public readonly record struct PeqPoint(double X, double Y)
{
    public double DistanceTo(PeqPoint o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
}

public readonly record struct PeqSize(double Width, double Height);

public readonly record struct PeqRect(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public bool Contains(PeqPoint p) => p.X >= X && p.X <= MaxX && p.Y >= Y && p.Y <= MaxY;
    public bool Contains(PeqRect r) => r.X >= X && r.MaxX <= MaxX && r.Y >= Y && r.MaxY <= MaxY;
    public PeqRect Inset(double dx, double dy) => new(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);
}

/// <summary>What the editor needs from the app: the shared selection, and
/// somewhere to send and commit bands. The view model is the real host; tests
/// supply one that records instead of talking to a device.</summary>
public interface IPeqEditorHost
{
    PeqGraphSelection Selection { get; }

    /// <summary>Writes bands to the model and the device, once, mirrored onto
    /// the other half of a linked input pair.</summary>
    void CommitGraphBands(int channel, IReadOnlyList<(int Band, FilterParams Params)> changes);

    /// <summary>Drag-time device writes, without touching the model.</summary>
    void SendGraphBandsToDevice(int channel, IReadOnlyList<(int Band, FilterParams Params)> changes);

    void SetGraphBandBypass(int channel, IReadOnlyList<int> bands, bool bypass);

    /// <summary>A band's live value, for the band list to follow a drag.</summary>
    void ShowLive(int channel, int band, FilterParams p);

    /// <summary>The live display is over; rows go back to the model.</summary>
    void EndLive(int channel);
}

public interface IPeqTimer
{
    void Cancel();
}

/// <summary>Time and one-shot timers, injected so tests can run the editor's
/// clock by hand.</summary>
public interface IPeqScheduler
{
    /// <summary>Seconds, monotonic.</summary>
    double Now { get; }
    IPeqTimer Schedule(double seconds, Action action);
}

/// <summary>Everything the editor is told about the graph it sits on.</summary>
public sealed class PeqEditorConfig
{
    /// <summary>The channel being edited; null leaves the editor inert.</summary>
    public int? Channel { get; init; }
    public IReadOnlyList<FilterParams> Bands { get; init; } = Array.Empty<FilterParams>();
    /// <summary>Crossover bands, fixed during a PEQ edit.</summary>
    public IReadOnlyList<FilterParams> Statics { get; init; } = Array.Empty<FilterParams>();
    /// <summary>The channel's level offset (gain or preamp) the graph shows:
    /// added to the combined curve, and every band is drawn and placed that
    /// much higher or lower so it stays on the curve. Band values exclude it.</summary>
    public float OffsetDb { get; init; }
    /// <summary>Master EQ bypass: the combined curve is flat.</summary>
    public bool Flat { get; init; }
    public (float R, float G, float B) CurveColor { get; init; } = (1, 1, 1);
    public float LineWidth { get; init; } = 2;
    public bool Glow { get; init; } = true;
    public double MinFreq { get; init; } = 20;
    public double MaxFreq { get; init; } = 20000;
    public double DbTop { get; init; } = 25;
    public double DbBottom { get; init; } = -25;
    public IReadOnlySet<FilterType> AvailableTypes { get; init; } = new HashSet<FilterType>();
    public bool BypassSupported { get; init; }
    /// <summary>The frequency and level readouts shown while hovering empty graph.</summary>
    public bool ShowFrequencyReadout { get; init; } = true;
    public bool ShowLevelReadout { get; init; } = true;

    /// <summary>Same graph and same bands, field for field.</summary>
    public bool SameAs(PeqEditorConfig o) =>
        Channel == o.Channel && OffsetDb == o.OffsetDb && Flat == o.Flat && CurveColor == o.CurveColor
        && LineWidth == o.LineWidth && Glow == o.Glow && MinFreq == o.MinFreq && MaxFreq == o.MaxFreq
        && DbTop == o.DbTop && DbBottom == o.DbBottom && BypassSupported == o.BypassSupported
        && ShowFrequencyReadout == o.ShowFrequencyReadout && ShowLevelReadout == o.ShowLevelReadout
        && AvailableTypes.SetEquals(o.AvailableTypes)
        && BandsSame(Bands, o.Bands) && BandsSame(Statics, o.Statics);

    public static bool BandsSame(IReadOnlyList<FilterParams> a, IReadOnlyList<FilterParams> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!a[i].SameAs(b[i])) return false;
        return true;
    }
}

/// <summary>One menu entry for the view to show; the editor runs <see cref="Invoke"/>.</summary>
public sealed class PeqMenuItem
{
    public string Title { get; init; } = "";
    public bool Checked { get; init; }
    public bool Enabled { get; init; } = true;
    public bool IsSeparator { get; init; }
    /// <summary>A disabled title line (the band menu's "Band N").</summary>
    public bool IsHeader { get; init; }
    public Action? Invoke { get; init; }
    public IReadOnlyList<PeqMenuItem>? Submenu { get; init; }

    public static PeqMenuItem Separator => new() { IsSeparator = true };
}

/// <summary>How one band's lobe and outline are drawn. The reach is in the
/// band's own dB, before the level offset.</summary>
public readonly record struct PeqBandStyle(
    int Row, (float R, float G, float B) Color, float LineOpacity, float FillOpacity, double ReachLow, double ReachHigh);

/// <summary>One dot: a flat disc; <see cref="Select"/> (0..1) grows a centre in
/// the graph's background colour.</summary>
public readonly record struct PeqNode(
    double X, double Y, double Radius, (float R, float G, float B) Color, float Opacity, float Select);

/// <summary>What the renderer draws, rebuilt by the editor each frame.</summary>
public sealed class PeqPicture
{
    public PeqGraphGeometry Geometry { get; init; }
    /// <summary>One entry per band row; null rows are empty slots.</summary>
    public IReadOnlyList<FilterParams?> Bands { get; init; } = Array.Empty<FilterParams?>();
    public IReadOnlyList<FilterParams> Statics { get; init; } = Array.Empty<FilterParams>();
    public float OffsetDb { get; init; }
    public bool Flat { get; init; }
    public (float R, float G, float B) CurveColor { get; init; }
    public float LineWidth { get; init; } = 2;
    public bool Glow { get; init; }
    public IReadOnlyList<PeqBandStyle> BandStyles { get; init; } = Array.Empty<PeqBandStyle>();
    /// <summary>Back to front.</summary>
    public IReadOnlyList<PeqNode> Nodes { get; init; } = Array.Empty<PeqNode>();
    public PeqRect? Marquee { get; init; }
}
