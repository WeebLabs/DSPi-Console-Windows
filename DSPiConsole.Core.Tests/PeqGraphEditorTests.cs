using DSPiConsole.Core;
using DSPiConsole.Core.GraphEditing;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Tests;

/// <summary>
/// On-graph PEQ editing: the dot and creation rules, value parsing, and the
/// editor driven by pointer and key events against a recording host. Ported
/// from the macOS Console's PeqGraphEditorTests.swift; the Mac's Command is
/// Ctrl here and its Option is Alt (see <see cref="PeqMods"/>).
/// </summary>
public class PeqGraphEditorTests
{
    // ── Fixtures ────────────────────────────────────────────────────────────

    private static FilterParams Band(FilterType t, float f, float q, float g) => new(t, f, q, g);

    private static readonly FilterParams[] HardBands =
    {
        Band(FilterType.Peaking, 10, 20, 24),
        Band(FilterType.Peaking, 12, 20, -24),
        Band(FilterType.Peaking, 25, 8, 12),
        Band(FilterType.Peaking, 1000, 1, 6),
        Band(FilterType.Peaking, 18000, 5, -12),
        Band(FilterType.LowShelf, 30, 0.707f, 15),
        Band(FilterType.HighShelf, 8000, 1.2f, -9),
        Band(FilterType.LowShelf1, 200, 0.707f, 6),
        Band(FilterType.HighShelf1, 3000, 0.707f, -6),
        Band(FilterType.HighPass, 15, 10, 0),
        Band(FilterType.HighPass, 80, 0.5f, 0),
        Band(FilterType.LowPass, 12000, 2, 0),
        Band(FilterType.HighPass1, 40, 0.707f, 0),
        Band(FilterType.LowPass1, 5000, 0.707f, 0),
        Band(FilterType.AllPass, 500, 3, 0),
        Band(FilterType.AllPass1, 500, 0.707f, 0),
        Band(FilterType.Notch, 60, 4, 0),
    };

    private sealed class FakeClock : IPeqScheduler
    {
        private sealed class Timer : IPeqTimer
        {
            public double Due;
            public Action Action = () => { };
            public bool Cancelled;
            public void Cancel() => Cancelled = true;
        }

        private readonly List<Timer> _timers = new();
        public double Now { get; private set; } = 100;

        public IPeqTimer Schedule(double seconds, Action action)
        {
            var t = new Timer { Due = Now + seconds, Action = action };
            _timers.Add(t);
            return t;
        }

        public void Advance(double seconds)
        {
            double end = Now + seconds;
            while (true)
            {
                var next = _timers.Where(t => !t.Cancelled && t.Due <= end).OrderBy(t => t.Due).FirstOrDefault();
                if (next == null) break;
                _timers.Remove(next);
                Now = Math.Max(Now, next.Due);
                next.Action();
            }
            Now = end;
        }
    }

    private sealed class RecordingHost : IPeqEditorHost
    {
        public PeqGraphSelection Selection { get; } = new();
        public List<List<(int Band, FilterParams Params)>> Commits { get; } = new();
        public int Sends { get; private set; }
        public List<(int Band, bool Bypass)> Bypasses { get; } = new();
        public List<int> Revealed { get; } = new();

        public RecordingHost() => Selection.RevealRow += b => Revealed.Add(b);

        public void CommitGraphBands(int channel, IReadOnlyList<(int Band, FilterParams Params)> changes) =>
            Commits.Add(changes.ToList());
        public void SendGraphBandsToDevice(int channel, IReadOnlyList<(int Band, FilterParams Params)> changes) => Sends++;
        public void SetGraphBandBypass(int channel, IReadOnlyList<int> bands, bool bypass)
        {
            foreach (int b in bands) Bypasses.Add((b, bypass));
        }
        public void ShowLive(int channel, int band, FilterParams p) { }
        public void EndLive(int channel) { }

        public FilterParams LastCommitted => Commits.Last().First().Params;

        public Dictionary<int, FilterParams> Latest()
        {
            var output = new Dictionary<int, FilterParams>();
            foreach (var commit in Commits) foreach (var (b, p) in commit) output[b] = p;
            return output;
        }
    }

    private sealed class Rig
    {
        public RecordingHost Host = new();
        public FakeClock Clock = new();
        public PeqGraphEditor View = null!;
        public PeqGraphGeometry G = new(800, 300, 20, 20000, 25, -25);
        public PeqEditorConfig Config = null!;

        public void Click(PeqPoint p, PeqMods mods = PeqMods.None)
        {
            View.PointerPressed(p, mods, 1);
            View.PointerReleased(p);
        }

        public void DoubleClick(PeqPoint p)
        {
            Click(p);
            View.PointerPressed(p, PeqMods.None, 2);
            View.PointerReleased(p);
        }

        public void Drag(PeqPoint a, PeqPoint b, PeqMods mods = PeqMods.None, int steps = 8)
        {
            View.PointerPressed(a, mods, 1);
            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                View.PointerDragged(new PeqPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t), mods);
            }
            View.PointerReleased(b);
        }

        public PeqPoint At(double freq, double db) => new(G.X(freq), G.Y(db));

        public void Apply(Func<PeqEditorConfig, PeqEditorConfig> change)
        {
            Config = change(Config);
            View.Apply(Config);
        }
    }

    private static FilterParams[] Empty(int n = 10) =>
        Enumerable.Range(0, n).Select(_ => new FilterParams()).ToArray();

    private static Rig MakeRig(FilterParams[]? bands = null)
    {
        var rig = new Rig();
        rig.View = new PeqGraphEditor(rig.Host, rig.Clock);
        rig.View.Resize(800, 300);
        rig.Config = new PeqEditorConfig
        {
            Channel = 0,
            Bands = bands ?? Empty(),
            MinFreq = 20,
            MaxFreq = 20000,
            AvailableTypes = FilterTypeExtensions.PeqTypes.Where(t => t != FilterType.Flat).ToHashSet(),
            BypassSupported = true,
        };
        rig.View.Apply(rig.Config);
        return rig;
    }

    private static FilterParams[] With(params (int Index, FilterParams P)[] set)
    {
        var bands = Empty();
        foreach (var (i, p) in set) bands[i] = p;
        return bands;
    }

    private static void Near(double expected, double actual, double tolerance, string? why = null) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance,
            $"{why ?? "value"}: expected {expected} ± {tolerance}, got {actual}");

    // ── Geometry and dots ───────────────────────────────────────────────────

    [Fact]
    public void GeometryRoundTripsAndMatchesTheGraph()
    {
        var g = new PeqGraphGeometry(900, 250, 15, 20000, 25, -25);
        Near(0, g.X(15), 1e-6);
        Near(900, g.X(20000), 1e-6);
        Near(0, g.Y(25), 1e-6);
        Near(125, g.Y(0), 1e-6);
        foreach (double f in new[] { 20.0, 440, 1000, 12345 }) Near(f, g.Freq(g.X(f)), f * 1e-9);
        foreach (double db in new[] { -20.0, 0, 7.5 }) Near(db, g.Db(g.Y(db)), 1e-9);
    }

    [Fact]
    public void DotsSitOnTheirBandsOwnCurve()
    {
        foreach (var band in HardBands)
        {
            if (PeqShapes.Of(band.Type) == null) continue;
            double dot = PeqNodeRole.Of(band).DbFor(band);
            if (band.Type is FilterType.Notch or FilterType.AllPass or FilterType.AllPass1)
                Near(0, dot, 1e-9, $"{band.Type} sits on 0 dB");
            else
                Near(DspMath.ResponseAt(band.Frequency, new[] { band }), dot, 0.05, $"{band.Type} dot on its curve");
        }
        var shelf = PeqNodeRole.Of(Band(FilterType.LowShelf, 100, 0.707f, 6));
        Assert.Equal(PeqNodeRole.RoleKind.Gain, shelf.Kind);
        Near(0.5, shelf.Scale, 0.001, "an RBJ shelf is half its gain at the corner");
        Assert.Equal(PeqNodeRole.RoleKind.Resonance, PeqNodeRole.Of(Band(FilterType.LowPass, 100, 2, 0)).Kind);
    }

    [Fact]
    public void DoubleClickAlwaysMakesABell()
    {
        var g = new PeqGraphGeometry(1000, 300, 20, 20000, 25, -25);
        var all = FilterTypeExtensions.PeqTypes.ToHashSet();
        foreach (var (x, y) in new[] { (20.0, 150.0), (980, 150), (500, 290), (500, 60) })
        {
            var band = PeqCreation.Band(x, y, g, all, fromCurve: false);
            Assert.Equal(FilterType.Peaking, band.Type);
            Near(g.Freq(x), band.Frequency, 0.01);
            Near(g.Db(y), band.Gain, 0.01);
            Assert.Equal(1f, band.Q);
        }
        FilterType CurveType(double x) => PeqCreation.Band(x, 150, g, all, fromCurve: true).Type;
        Assert.Equal(FilterType.LowShelf, CurveType(60));
        Assert.Equal(FilterType.HighShelf, CurveType(940));
        Assert.Equal(FilterType.Peaking, CurveType(500));
    }

    [Fact]
    public void ShapeMappingRoundTrips()
    {
        foreach (var shape in PeqShapes.All)
            foreach (int order in new[] { 1, 2 })
            {
                if (shape.Type(order) is not { } t) continue;
                Assert.Equal(shape, PeqShapes.Of(t)!.Value.Shape);
                Assert.Equal(order, PeqShapes.Of(t)!.Value.Order);
            }
        Assert.Null(PeqShapes.Of(FilterType.LinkwitzTransform));
        Assert.Null(PeqShapes.Of(FilterType.Lr4Lp));
    }

    [Fact]
    public void ValueEntryAcceptsFabFilterShortcuts()
    {
        Near(2000, PeqValueText.ParseFrequency("2k")!.Value, 1e-9);
        Near(1500, PeqValueText.ParseFrequency("1.5 kHz")!.Value, 1e-9);
        Near(100, PeqValueText.ParseFrequency("100hz")!.Value, 1e-9);
        Near(440, PeqValueText.ParseFrequency("A4")!.Value, 1e-9);
        Near(261.6256, PeqValueText.ParseFrequency("C4")!.Value, 1e-3);
        Near(233.0819, PeqValueText.ParseFrequency("Bb3")!.Value, 1e-3);
        Near(69.2957 * Math.Pow(2, 13.0 / 1200), PeqValueText.ParseFrequency("C#2+13")!.Value, 1e-3);
        Assert.Null(PeqValueText.ParseFrequency("loud"));
        Assert.Equal(3.5, PeqValueText.ParseNumber("+3.5 dB", "db"));
        Assert.Equal(2, PeqValueText.ParseNumber("Q 2", ""));
    }

    [Fact]
    public void ChipValuesTruncateToTwoDecimals()
    {
        Assert.Equal("4.99", PeqValueText.Truncated(4.999));
        Assert.Equal("1.10", PeqValueText.Truncated((float)1.1));
        Assert.Equal("-6.50", PeqValueText.Truncated(-6.506, sign: true));
        Assert.Equal("+5.00", PeqValueText.Truncated(5, sign: true));
        Assert.Equal("+0.00", PeqValueText.Truncated(-0.004, sign: true));
        Assert.Equal("1.79", PeqValueText.Truncated(1799.99 / 1000));
    }

    // ── Band list ───────────────────────────────────────────────────────────

    /// <summary>A band number in the list takes the graph's click modifiers:
    /// Ctrl toggles one band, Shift takes the run of rows from the last click,
    /// skipping empty rows, and a plain click selects the band alone.</summary>
    [Fact]
    public void ListNumberClicksSelectLikeDots()
    {
        var selection = new PeqGraphSelection();
        bool Active(int i) => i != 4;
        selection.ListClick(2, ctrl: false, shift: false, Active);
        Assert.Equal(new[] { 2 }, selection.Selected.Order());
        selection.ListClick(6, ctrl: false, shift: true, Active);
        Assert.Equal(new[] { 2, 3, 5, 6 }, selection.Selected.Order());
        selection.ListClick(0, ctrl: false, shift: true, Active);
        Assert.Equal(new[] { 0, 1, 2 }, selection.Selected.Order());
        selection.ListClick(8, ctrl: true, shift: false, Active);
        Assert.Equal(new[] { 0, 1, 2, 8 }, selection.Selected.Order());
        selection.ListClick(1, ctrl: true, shift: false, Active);
        Assert.Equal(new[] { 0, 2, 8 }, selection.Selected.Order());
        Assert.Equal(new[] { 0, 2, 8 }, selection.MadeByList!.Order());

        selection.SetSelected(new[] { 7 });
        selection.ListClick(9, ctrl: false, shift: true, Active);
        Assert.Equal(new[] { 7, 8, 9 }, selection.Selected.Order());
        selection.SetSelected(new[] { 1, 5 });
        selection.ListClick(9, ctrl: false, shift: true, Active);
        Assert.Equal(new[] { 9 }, selection.Selected.Order());
        selection.ListClick(3, ctrl: false, shift: false, Active);
        Assert.Equal(new[] { 3 }, selection.Selected.Order());
    }

    // ── Editor interaction ──────────────────────────────────────────────────

    [Fact]
    public void SingleClickOnEmptyGraphCreatesNothing()
    {
        var rig = MakeRig();
        rig.Click(new PeqPoint(400, 90));
        Assert.Empty(rig.Host.Commits);
    }

    [Fact]
    public void DoubleClickOnEmptyGraphCreatesTheBandUnderThePointer()
    {
        var rig = MakeRig();
        var p = new PeqPoint(400, 90);
        rig.DoubleClick(p);
        Assert.Single(rig.Host.Commits);
        var created = rig.Host.Commits[0][0];
        Assert.Equal(0, created.Band);
        Assert.Equal(FilterType.Peaking, created.Params.Type);
        Near(rig.G.Freq(p.X), created.Params.Frequency, 0.01);
        Near(rig.G.Db(p.Y), created.Params.Gain, 0.01);
        Assert.Equal(new[] { 0 }, rig.Host.Selection.Selected.Order());
    }

    [Fact]
    public void DoubleClickWithEveryBandInUseBeeps()
    {
        var bands = Enumerable.Range(0, 10).Select(i => Band(FilterType.Peaking, 100 * (i + 1), 1, 3)).ToArray();
        var rig = MakeRig(bands);
        int beeps = 0;
        rig.View.Beep += () => beeps++;
        rig.DoubleClick(rig.At(15000, -20));
        Assert.Empty(rig.Host.Commits);
        Assert.Equal(1, beeps);
        rig.View.PointerMoved(rig.At(15000, -20));
        Assert.Equal("All 10 bands in use", rig.View.AxisLabel);
        Assert.Null(rig.View.GainLabel);
    }

    [Fact]
    public void ClickOnEmptyGraphDeselectsWhenBandsAreSelected()
    {
        var rig = MakeRig(With((3, Band(FilterType.Peaking, 1000, 1, 6))));
        rig.Click(rig.At(1000, 6));
        Assert.Equal(new[] { 3 }, rig.Host.Selection.Selected.Order());
        rig.Click(new PeqPoint(150, 60));
        Assert.Empty(rig.Host.Selection.Selected);
        Assert.Empty(rig.Host.Commits);
    }

    /// <summary>A drag reaches the device while it happens and the model
    /// exactly once, on release.</summary>
    [Fact]
    public void DragCommitsOnceOnRelease()
    {
        var rig = MakeRig(With((2, Band(FilterType.Peaking, 1000, 1.5f, 3))));
        var start = rig.At(1000, 3);
        var end = rig.At(2000, 9);
        rig.View.PointerPressed(start, PeqMods.None, 1);
        for (int i = 1; i <= 10; i++)
        {
            double t = i / 10.0;
            rig.View.PointerDragged(new PeqPoint(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t), PeqMods.None);
            rig.Clock.Advance(0.016);
        }
        Assert.Empty(rig.Host.Commits);
        Assert.True(rig.Host.Sends > 0, "the device follows the drag");
        rig.View.PointerReleased(end);
        Assert.Single(rig.Host.Commits);
        var moved = rig.Host.Commits[0][0];
        Assert.Equal(2, moved.Band);
        Near(2000, moved.Params.Frequency, 2);
        Near(9, moved.Params.Gain, 0.05);
        Assert.Equal(1.5f, moved.Params.Q);
    }

    /// <summary>Scrolling Q in the middle of a drag must survive further
    /// movement: the drag rebuilds the band from its starting snapshot.</summary>
    [Fact]
    public void WheelQDuringDragSurvivesMovement()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 1.5f, 3))));
        var dot = rig.At(1000, 3);
        rig.View.PointerPressed(dot, PeqMods.None, 1);
        rig.View.PointerDragged(new PeqPoint(dot.X + 10, dot.Y), PeqMods.None);
        Assert.True(rig.View.Wheel(dot, 100, PeqMods.None));
        rig.View.PointerDragged(new PeqPoint(dot.X + 30, dot.Y - 10), PeqMods.None);
        rig.View.PointerReleased(new PeqPoint(dot.X + 30, dot.Y - 10));
        var p = rig.Host.LastCommitted;
        Assert.True(p.Q > 1.6, "the scrolled Q is kept after the drag moves on");
        Assert.True(p.Frequency > 1000, "and the drag still moved the band");
    }

    /// <summary>Ctrl-wheel gain moves the dot away from the pointer; the
    /// gesture must stay with its band so the gain can come back up.</summary>
    [Fact]
    public void CtrlWheelGainStaysWithItsBand()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 4, 3))));
        var dot = rig.At(1000, 3);
        for (int i = 0; i < 18; i++) Assert.True(rig.View.Wheel(dot, -10, PeqMods.Ctrl));
        rig.Clock.Advance(0.3);
        for (int i = 0; i < 12; i++) Assert.True(rig.View.Wheel(dot, 10, PeqMods.Ctrl));
        rig.Clock.Advance(0.7);
        Near(0, rig.Host.LastCommitted.Gain, 0.01, "down 9 dB then up 6 dB, all on the one band");
    }

    /// <summary>The chip follows its dot, so Ctrl-wheel gain can slide a chip
    /// field under the pointer; the gesture keeps adjusting gain. (Ctrl-wheel on
    /// a chip field is forwarded to the graph, as on the Mac.)</summary>
    [Fact]
    public void GraphWheelGestureIgnoresChipFieldSlidingUnderPointer()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 2, 6))));
        var dot = rig.At(1000, 6);
        rig.View.PointerMoved(dot);
        Assert.True(rig.View.Wheel(dot, -10, PeqMods.Ctrl));
        var onChip = new PeqPoint(rig.View.HudFrame.X + 60, rig.View.HudFrame.MaxY - 8);
        Assert.True(rig.View.GraphWheelActive);
        Assert.True(rig.View.Wheel(onChip, -10, PeqMods.Ctrl));
        rig.Clock.Advance(0.7);
        var p = rig.Host.LastCommitted;
        Assert.Equal(2f, p.Q);
        Assert.True(p.Gain < 5.6, "both steps went to the gain");
    }

    /// <summary>A pause longer than a gesture ends the graph's hold, but
    /// Ctrl-wheel over the chip is still the band's gain.</summary>
    [Fact]
    public void CtrlWheelOnChipAfterPauseAdjustsGain()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 2, 6))));
        var dot = rig.At(1000, 6);
        rig.View.PointerMoved(dot);
        Assert.True(rig.View.Wheel(dot, -10, PeqMods.Ctrl));
        rig.Clock.Advance(0.7);
        Assert.True(rig.View.HudVisible);
        var onChip = new PeqPoint(rig.View.HudFrame.X + 60, rig.View.HudFrame.MaxY - 8);
        Assert.True(rig.View.Wheel(onChip, -10, PeqMods.Ctrl));
        Assert.True(rig.View.Wheel(onChip, -10, PeqMods.Ctrl));
        rig.Clock.Advance(0.7);
        var p = rig.Host.LastCommitted;
        Assert.Equal(2f, p.Q);
        Assert.Equal(1000f, p.Frequency);
        Near(4.5, p.Gain, 0.01, "all three steps went to the gain");
    }

    /// <summary>An explicit selection owns the wheel: scrolling over another
    /// band's dot or area adjusts the selection, not the band under the pointer.</summary>
    [Fact]
    public void SelectionOwnsTheWheel()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 2, 6)), (1, Band(FilterType.Peaking, 4000, 1, 6))));
        var selectedDot = rig.At(1000, 6);
        var otherDot = rig.At(4000, 6);
        var otherArea = rig.At(4000, 3);
        rig.Click(selectedDot);
        Assert.Equal(new[] { 0 }, rig.Host.Selection.Selected.Order());

        Assert.True(rig.View.Wheel(otherDot, -10, PeqMods.Ctrl, begins: true));
        Assert.True(rig.View.Wheel(otherArea, 100, PeqMods.None, begins: true));
        Assert.False(rig.View.Wheel(rig.At(200, -15), 10, PeqMods.Ctrl, begins: true), "empty graph still zooms or scrolls");
        rig.Clock.Advance(1.1);
        var p = rig.Host.Latest();
        Near(5.5, p[0].Gain, 0.01, "the gain step went to the selection");
        Near(4, p[0].Q, 0.01, "and so did the Q step");
        Assert.False(p.ContainsKey(1), "the band under the pointer was not touched");
        Assert.Equal(0, rig.View.HudBand);

        // The selected band's own chip fields still take the wheel.
        rig.View.HudScroll(PeqHudField.Freq, 100, fine: false);
        rig.Clock.Advance(0.7);
        Assert.True(rig.Host.Latest()[0].Frequency > 1000);

        // With nothing selected the wheel adjusts the band under the pointer.
        rig.Click(rig.At(200, -15));
        Assert.Empty(rig.Host.Selection.Selected);
        Assert.True(rig.View.Wheel(otherDot, -10, PeqMods.Ctrl, begins: true));
        rig.Clock.Advance(0.7);
        p = rig.Host.Latest();
        Near(5.5, p[1].Gain, 0.01);
        Near(5.5, p[0].Gain, 0.01, "the earlier band is unchanged");
    }

    /// <summary>With a selection the chip stays on it, wherever the pointer
    /// goes; with none it follows the hovered dot.</summary>
    [Fact]
    public void ChipStaysOnTheSelection()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 2, 6)), (1, Band(FilterType.Peaking, 4000, 1, 6))));
        var dot0 = rig.At(1000, 6);
        var dot1 = rig.At(4000, 6);
        var empty = rig.At(200, -15);
        rig.Click(dot0);
        rig.View.PointerMoved(dot1);
        Assert.Equal(0, rig.View.HudBand);
        Assert.Equal(1, rig.Host.Selection.GraphHovered);
        rig.View.PointerMoved(empty);
        rig.Clock.Advance(0.6);
        Assert.Equal(0, rig.View.HudBand);

        rig.Host.Selection.SetSelected(new[] { 1 });
        Assert.Equal(1, rig.View.HudBand);

        rig.Click(empty);
        Assert.Empty(rig.Host.Selection.Selected);
        Assert.Null(rig.View.HudBand);
        rig.Clock.Advance(0.6);
        Assert.Null(rig.View.HudBand);
        rig.View.PointerMoved(dot1);
        Assert.Equal(1, rig.View.HudBand);
    }

    [Fact]
    public void ChipHidesShortlyAfterLeavingADotAndComesBack()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 2, 6)), (1, Band(FilterType.Peaking, 4000, 1, 6))));
        rig.View.PointerMoved(rig.At(1000, 6));
        Assert.True(rig.View.HudVisible);
        rig.View.PointerMoved(rig.At(200, -15));
        rig.Clock.Advance(0.2);
        Assert.True(rig.View.HudVisible, "a grace period lets the pointer reach the chip");
        rig.Clock.Advance(0.3);
        Assert.False(rig.View.HudVisible);
        rig.View.PointerMoved(rig.At(4000, 6));
        Assert.Equal(1, rig.View.HudBand);
        Assert.True(rig.View.HudVisible);
        rig.View.PointerMoved(rig.At(200, -15));
        rig.Clock.Advance(0.42);
        Assert.True(rig.View.Wheel(rig.At(1000, 6), 10, PeqMods.None, begins: true));
        Assert.Equal(0, rig.View.HudBand);
        Assert.True(rig.View.HudVisible, "wheeling a band shows its chip");
    }

    /// <summary>A band's fill highlights it and takes the wheel, but clicks go
    /// through to the graph: only the dot selects.</summary>
    [Fact]
    public void ClicksPassThroughBandFills()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 0.5f, 10))));
        var dot = rig.At(1000, 10);
        var fill = rig.At(400, 3);

        rig.View.PointerMoved(fill);
        Assert.Equal(0, rig.Host.Selection.GraphHovered);
        Assert.NotNull(rig.View.AxisLabel);

        rig.Click(dot);
        Assert.Equal(new[] { 0 }, rig.Host.Selection.Selected.Order());
        rig.Click(fill);
        Assert.Empty(rig.Host.Selection.Selected);

        rig.DoubleClick(fill);
        var created = rig.Host.Commits.Last()[0];
        Assert.Equal(1, created.Band);
        Assert.Equal(FilterType.Peaking, created.Params.Type);
        Near(400, created.Params.Frequency, 4);
        Near(3, created.Params.Gain, 0.2);
    }

    /// <summary>With the channel's gain shown on the graph, the curve moves by
    /// it and so do the dots, the fills and anything placed by pointer; band
    /// values stay the band's own.</summary>
    [Fact]
    public void BandsFollowTheLevelOffset()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 0.5f, 3))));
        rig.Apply(c => new PeqEditorConfig
        {
            Channel = c.Channel, Bands = c.Bands, MinFreq = c.MinFreq, MaxFreq = c.MaxFreq,
            AvailableTypes = c.AvailableTypes, BypassSupported = c.BypassSupported, OffsetDb = -6,
        });

        // The +3 dB bell's dot sits at -3 dB on the axis, on the shifted curve.
        rig.View.PointerMoved(rig.At(1000, -3));
        Assert.Equal(0, rig.Host.Selection.GraphHovered);
        rig.View.PointerMoved(new PeqPoint(5, 5));
        rig.View.PointerMoved(rig.At(1000, 3));
        Assert.Null(rig.Host.Selection.GraphHovered);

        // Its fill lies between -6 dB (its baseline now) and the curve.
        rig.View.PointerMoved(rig.At(700, -4.5));
        Assert.Equal(0, rig.Host.Selection.GraphHovered);

        // Dragging the dot moves it by what the pointer moved.
        rig.Drag(rig.At(1000, -3), rig.At(1000, 1));
        Near(7, rig.Host.LastCommitted.Gain, 0.1, "dragged 4 dB up from +3");

        // A bell placed at -4 dB on the axis is a +2 dB bell, its dot under the pointer.
        rig.DoubleClick(rig.At(100, -4));
        var created = rig.Host.Commits.Last()[0];
        Assert.Equal(1, created.Band);
        Near(2, created.Params.Gain, 0.05);
    }

    /// <summary>Ctrl-click on empty graph opens the shape card there. A shape
    /// with one order creates the band at once; one with two asks for the
    /// order. A click elsewhere, the back arrow, right-click or Escape cancels.</summary>
    [Fact]
    public void CtrlClickCardCreatesShapeThenSlope()
    {
        var rig = MakeRig();
        var chooser = new PeqShapeChooserState();
        chooser.Configure(rig.View.AvailableTypes);
        void Pick(PeqShape shape, int? order = null)
        {
            var picked = chooser.PickShape(shape);
            if (picked == null && order is { } o) picked = chooser.PickOrder(o);
            if (picked is { } pk) { rig.View.CardPick(pk.Shape, pk.Order); chooser.Reset(); }
        }

        var at = rig.At(300, 4);
        rig.Click(at, PeqMods.Ctrl);
        Assert.True(rig.View.CardOpen);
        Assert.Empty(rig.Host.Commits);
        Pick(PeqShape.Bell);
        Assert.False(rig.View.CardOpen);
        var created = rig.Host.Commits.Last()[0];
        Assert.Equal(new[] { created.Band }, rig.Host.Selection.Selected.Order());
        Assert.Equal(created.Band, rig.View.HudBand);
        Assert.True(rig.View.HudVisible);
        Assert.Equal(FilterType.Peaking, created.Params.Type);
        Near(rig.G.Freq(at.X), created.Params.Frequency, 0.5);
        Near(4, created.Params.Gain, 0.05);

        // A shelf asks for its slope first, with neither marked.
        var shelfAt = rig.At(120, -6);
        rig.Click(shelfAt, PeqMods.Ctrl);
        int count = rig.Host.Commits.Count;
        Assert.Null(chooser.PickShape(PeqShape.LowShelf));
        Assert.Equal(count, rig.Host.Commits.Count);
        Assert.False(chooser.IsOrderMarked(1));
        var pk = chooser.PickOrder(1)!.Value;
        rig.View.CardPick(pk.Shape, pk.Order);
        chooser.Reset();
        created = rig.Host.Commits.Last()[0];
        Assert.Equal(FilterType.LowShelf1, created.Params.Type);
        Near(rig.G.Freq(shelfAt.X), created.Params.Frequency, 0.5);
        Near(-6, created.Params.Gain, 0.05);
        Assert.False(rig.View.CardOpen);

        // A cut at 12 dB.
        rig.Click(rig.At(8000, 0), PeqMods.Ctrl);
        Pick(PeqShape.HighCut, 2);
        Assert.Equal(FilterType.LowPass, rig.Host.Commits.Last()[0].Params.Type);

        // Every way out cancels without making anything.
        int before = rig.Host.Commits.Count;
        rig.Click(rig.At(1000, 0), PeqMods.Ctrl);
        Assert.Null(chooser.PickShape(PeqShape.LowCut));
        Assert.False(chooser.Back(), "back returns to the shapes");
        Assert.Null(chooser.PendingShape);
        Assert.True(chooser.Back());
        rig.View.CardCancel();
        Assert.False(rig.View.CardOpen);
        rig.Click(rig.At(1000, 0), PeqMods.Ctrl);
        rig.View.KeyDown(PeqKey.Escape, PeqMods.None);
        Assert.False(rig.View.CardOpen, "Escape cancels");
        rig.Click(rig.At(1000, 0), PeqMods.Ctrl);
        Assert.Null(rig.View.RightPressed(rig.At(2000, -20)));
        Assert.False(rig.View.CardOpen, "right-click only closes the card");
        rig.Click(rig.At(1000, 0), PeqMods.Ctrl);
        rig.Click(rig.At(2000, -20));
        Assert.False(rig.View.CardOpen, "a click away dismisses it");
        Assert.Equal(before, rig.Host.Commits.Count);
    }

    /// <summary>The Ctrl-click card and the chip of the bell it makes sit on the
    /// same side of the point: away from 0 dB where there is room, else beside it.</summary>
    [Fact]
    public void CtrlClickCardAndNewChipShareASide()
    {
        var rig = MakeRig();
        static string Side(PeqRect f, PeqPoint p)
        {
            if (f.MaxY <= p.Y) return "north";
            if (f.MinY >= p.Y) return "south";
            return f.MinX >= p.X ? "east" : "west";
        }
        foreach (var (freq, db, expected) in new[] { (100.0, 4.0, "north"), (300, 22, "east"), (1000, -3, "south"), (3000, -22, "east") })
        {
            var at = rig.At(freq, db);
            rig.Click(at, PeqMods.Ctrl);
            var card = new PeqRect(rig.View.CardOrigin.X, rig.View.CardOrigin.Y,
                                   PeqGraphEditor.Tuning.CardSize.Width, PeqGraphEditor.Tuning.CardSize.Height);
            string cardSide = Side(card, at);
            rig.View.CardPick(PeqShape.Bell, 2);
            string chipSide = Side(rig.View.HudFrame, at);
            Assert.Equal(expected, cardSide);
            Assert.Equal(cardSide, chipSide);
        }
    }

    /// <summary>The chip's shape button turns it to a page the size of its
    /// values, held still; nothing changes before the last click, and the chip
    /// returns to its values after it.</summary>
    [Fact]
    public void ChipShapePagePicksShapeThenSlope()
    {
        var rig = MakeRig(With((0, Band(FilterType.LowShelf1, 200, 0.707f, 6))));
        rig.Host.Selection.SetSelected(new[] { 0 });
        var values = rig.View.HudFrame;
        rig.View.HudShapeButton();
        Assert.True(rig.View.ShowsShapes);
        Assert.Equal(values, rig.View.HudFrame);

        var chooser = new PeqShapeChooserState();
        chooser.Configure(rig.View.AvailableTypes);
        chooser.Own = PeqShapes.Of(rig.View.HudParams!.Type);
        int count = rig.Host.Commits.Count;
        Assert.Null(chooser.PickShape(PeqShape.HighShelf));
        Assert.Equal(count, rig.Host.Commits.Count);
        Assert.False(chooser.IsOrderMarked(1), "another shape's slope starts unselected");
        Assert.Equal(values, rig.View.HudFrame);
        chooser.Back();
        chooser.PickShape(PeqShape.LowShelf);
        Assert.True(chooser.IsOrderMarked(1), "the band's own 6 dB is shown");
        Assert.False(chooser.IsOrderMarked(2));
        chooser.Back();
        chooser.PickShape(PeqShape.HighShelf);
        var pk = chooser.PickOrder(2)!.Value;
        rig.View.HudPick(pk.Shape, pk.Order);
        Assert.Equal(FilterType.HighShelf, rig.Host.LastCommitted.Type);
        Assert.Equal(count + 1, rig.Host.Commits.Count);
        Assert.False(rig.View.ShowsShapes);

        // A bell has one order, so it applies straight away.
        rig.View.HudShapeButton();
        chooser.Reset();
        var bell = chooser.PickShape(PeqShape.Bell)!.Value;
        rig.View.HudPick(bell.Shape, bell.Order);
        Assert.Equal(FilterType.Peaking, rig.Host.LastCommitted.Type);
        Assert.Equal(0.707f, rig.Host.LastCommitted.Q);   // a shelf already had a Q, so it carries over
        Assert.False(rig.View.ShowsShapes);

        // Escape abandons a choice half made.
        int before = rig.Host.Commits.Count;
        rig.View.HudShapeButton();
        rig.View.KeyDown(PeqKey.Escape, PeqMods.None);
        Assert.False(rig.View.ShowsShapes);
        Assert.Equal(before, rig.Host.Commits.Count);
        Assert.Equal(new[] { 0 }, rig.Host.Selection.Selected.Order());
    }

    [Fact]
    public void ShapeChangeKeepsFrequencyZeroesGainAndSeedsQ()
    {
        var rig = MakeRig(With((0, Band(FilterType.LowShelf1, 200, 0.707f, 6))));
        rig.Host.Selection.SetSelected(new[] { 0 });
        rig.View.HudPick(PeqShape.Notch, 2);
        var p = rig.Host.LastCommitted;
        Assert.Equal(FilterType.Notch, p.Type);
        Assert.Equal(200f, p.Frequency);
        Assert.Equal(0f, p.Gain);
        Assert.Equal(1f, p.Q);
    }

    /// <summary>Hovering empty graph reads out the pointer's frequency and
    /// level; over a band neither shows. Each can be switched off.</summary>
    [Fact]
    public void EmptyGraphReadsOutFrequencyAndGain()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 2, 6))));
        rig.View.PointerMoved(rig.At(200, 4));
        Assert.NotNull(rig.View.AxisLabel);
        Assert.Equal("+4.00 dB", rig.View.GainLabel);

        rig.View.PointerMoved(new PeqPoint(2, rig.G.Y(-3)));
        Assert.NotNull(rig.View.AxisLabel);
        Assert.Equal("-3.00 dB", rig.View.GainLabel);

        rig.View.PointerMoved(rig.At(1000, 6));
        Assert.Null(rig.View.AxisLabel);
        Assert.Null(rig.View.GainLabel);

        rig.Apply(c => new PeqEditorConfig
        {
            Channel = c.Channel, Bands = c.Bands, MinFreq = c.MinFreq, MaxFreq = c.MaxFreq,
            AvailableTypes = c.AvailableTypes, BypassSupported = c.BypassSupported, ShowFrequencyReadout = false,
        });
        rig.View.PointerMoved(rig.At(200, 4));
        Assert.Null(rig.View.AxisLabel);
        Assert.Equal("+4.00 dB", rig.View.GainLabel);
        rig.Apply(c => new PeqEditorConfig
        {
            Channel = c.Channel, Bands = c.Bands, MinFreq = c.MinFreq, MaxFreq = c.MaxFreq,
            AvailableTypes = c.AvailableTypes, BypassSupported = c.BypassSupported, ShowLevelReadout = false,
        });
        rig.View.PointerMoved(rig.At(300, 4));
        Assert.NotNull(rig.View.AxisLabel);
        Assert.Null(rig.View.GainLabel);
    }

    /// <summary>The list scrolls to a band only once the pointer rests on it,
    /// and not while a band is being wheeled.</summary>
    [Fact]
    public void RestingOnABandRevealsItsRow()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 2, 6)), (1, Band(FilterType.Peaking, 4000, 1, 6))));
        var dot0 = rig.At(1000, 6);
        var dot1 = rig.At(4000, 6);
        rig.View.PointerMoved(dot0);
        rig.Clock.Advance(0.1);
        rig.View.PointerMoved(dot1);
        Assert.Empty(rig.Host.Revealed);
        rig.Clock.Advance(0.4);
        Assert.Equal(new[] { 1 }, rig.Host.Revealed);

        Assert.True(rig.View.Wheel(dot1, -10, PeqMods.Ctrl, begins: true));
        rig.View.PointerMoved(dot0);
        rig.Clock.Advance(0.35);
        Assert.Equal(new[] { 1 }, rig.Host.Revealed);
        rig.Clock.Advance(0.6);
        Assert.Equal(new[] { 1, 0 }, rig.Host.Revealed);
    }

    /// <summary>Alt held on a dot: a drag locks to one axis, a click bypasses.</summary>
    [Fact]
    public void AltDragLocksToOneAxis()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 1, 3))));
        var dot = rig.At(1000, 3);
        rig.Drag(dot, new PeqPoint(dot.X + 60, dot.Y + 12), PeqMods.Alt);
        var p = rig.Host.LastCommitted;
        Assert.True(p.Frequency > 1100);
        Assert.Equal(3f, p.Gain);
        Assert.Empty(rig.Host.Bypasses);
    }

    /// <summary>Alt pressed part-way through a drag locks to the axis moved
    /// along so far, freezing the other where it is (no jump); releasing it
    /// frees the drag again from there, also without a jump.</summary>
    [Fact]
    public void AltMidDragLocksWithoutAJump()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 1, 0))));
        var g = rig.G;
        var dot = rig.At(1000, 0);
        rig.View.PointerPressed(dot, PeqMods.None, 1);
        var a = new PeqPoint(dot.X + 60, g.Y(2));
        rig.View.PointerDragged(new PeqPoint(dot.X + 30, g.Y(1)), PeqMods.None);
        rig.View.PointerDragged(a, PeqMods.None);
        rig.View.PointerDragged(new PeqPoint(a.X + 20, g.Y(8)), PeqMods.Alt);
        var b = new PeqPoint(a.X + 40, g.Y(-5));
        rig.View.PointerDragged(b, PeqMods.Alt);
        rig.View.PointerDragged(new PeqPoint(b.X, b.Y - (g.Y(0) - g.Y(3))), PeqMods.None);
        rig.View.PointerReleased(b);
        var p = rig.Host.LastCommitted;
        Near(g.Freq(dot.X + 100), p.Frequency, 2, "frequency followed all the sideways movement");
        Near(5, p.Gain, 0.05, "held at +2 dB while locked, then +3 dB more once free");
    }

    [Fact]
    public void ShiftDragIsFine()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 1, 0))));
        var dot = rig.At(1000, 0);
        rig.Drag(dot, new PeqPoint(dot.X, rig.G.Y(10)), PeqMods.Shift);
        Near(10 * 0.12, rig.Host.LastCommitted.Gain, 0.15, "fine drag moves about an eighth as far");
    }

    /// <summary>Dragging one of several selected bands moves every gain by the
    /// same number of dB; Ctrl pressed after the drag has begun scales them in
    /// proportion instead (the Mac's Control), so a cut deepens as a boost grows.</summary>
    [Fact]
    public void SelectionGainsOffsetAndCtrlMidDragScales()
    {
        var bands = With((0, Band(FilterType.Peaking, 1000, 1, 6)), (1, Band(FilterType.Peaking, 200, 1, -4)));
        (double, double) Gains(bool scale)
        {
            var rig = MakeRig(bands.Select(b => b.Clone()).ToArray());
            rig.Host.Selection.SetSelected(new[] { 0, 1 });
            var dot = rig.At(1000, 6);
            var up = new PeqPoint(dot.X, rig.G.Y(9));
            rig.View.PointerPressed(dot, PeqMods.None, 1);
            rig.View.PointerDragged(new PeqPoint(dot.X, dot.Y - 3), PeqMods.None);
            if (scale) rig.View.ModifiersChanged(PeqMods.Ctrl);
            for (int i = 1; i <= 8; i++)
                rig.View.PointerDragged(new PeqPoint(dot.X, dot.Y - 3 + (up.Y - dot.Y + 3) * i / 8.0),
                                        scale ? PeqMods.Ctrl : PeqMods.None);
            rig.View.PointerReleased(up);
            var last = rig.Host.Commits.Last();
            return (last.First(c => c.Band == 0).Params.Gain, last.First(c => c.Band == 1).Params.Gain);
        }
        var offset = Gains(false);
        Near(9, offset.Item1, 0.1);
        Near(-1, offset.Item2, 0.1, "the cut moves up by the same 3 dB");
        var scaled = Gains(true);
        Near(9, scaled.Item1, 0.1);
        Near(-6, scaled.Item2, 0.1, "the cut deepens by the boost's 1.5 times");
    }

    [Fact]
    public void NotchDragMovesFrequencyOnly()
    {
        var rig = MakeRig(With((0, Band(FilterType.Notch, 1000, 3, 0))));
        var dot = rig.At(1000, 0);
        rig.Drag(dot, new PeqPoint(dot.X + 40, dot.Y - 50));
        var p = rig.Host.LastCommitted;
        Assert.True(p.Frequency > 1050);
        Assert.Equal(3f, p.Q);
    }

    [Fact]
    public void WheelOnACutChangesQNotSlope()
    {
        var rig = MakeRig(With((0, Band(FilterType.HighPass, 100, 0.707f, 0))));
        var dot = rig.At(100, 20 * Math.Log10(0.707));
        for (int i = 0; i < 5; i++) Assert.True(rig.View.Wheel(dot, 20, PeqMods.None));
        rig.Clock.Advance(0.7);
        var p = rig.Host.LastCommitted;
        Assert.Equal(FilterType.HighPass, p.Type);
        Assert.True(p.Q > 0.9);
    }

    [Fact]
    public void CtrlDragChangesOnlyQ()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 500, 1, 4))));
        var dot = rig.At(500, 4);
        rig.Drag(dot, new PeqPoint(dot.X + 40, dot.Y - 60), PeqMods.Ctrl);
        var p = rig.Host.LastCommitted;
        Assert.Equal(500f, p.Frequency);
        Assert.Equal(4f, p.Gain);
        Near(2, p.Q, 0.01, "60 points up doubles Q");
    }

    [Fact]
    public void DraggingASecondOrderCutSetsQFromItsHeight()
    {
        var rig = MakeRig(With((0, Band(FilterType.HighPass, 100, 0.707f, 0))));
        var dot = rig.At(100, 20 * Math.Log10(0.707));
        rig.Drag(dot, new PeqPoint(dot.X, rig.G.Y(6)));
        Near(Math.Pow(10, 6.0 / 20), rig.Host.LastCommitted.Q, 0.02);
    }

    [Fact]
    public void MultiSelectionMovesTogether()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 200, 1, 4)), (1, Band(FilterType.Peaking, 2000, 1, -2))));
        rig.Host.Selection.SetSelected(new[] { 0, 1 });
        rig.Drag(rig.At(200, 4), rig.At(400, 8));
        var byBand = rig.Host.Commits.Last().ToDictionary(c => c.Band, c => c.Params);
        Near(400, byBand[0].Frequency, 1);
        Near(4000, byBand[1].Frequency, 4, "same frequency ratio");
        Near(2, byBand[1].Gain, 0.05, "gains move by the same 4 dB");
    }

    [Fact]
    public void AltClickBypassesAndDeleteKeyRemoves()
    {
        var rig = MakeRig(With((5, Band(FilterType.Peaking, 3000, 2, -6))));
        var dot = rig.At(3000, -6);
        rig.Click(dot, PeqMods.Alt);
        Assert.Equal((5, true), rig.Host.Bypasses.Last());
        rig.Click(dot);
        Assert.True(rig.View.KeyDown(PeqKey.Delete, PeqMods.None));
        var removed = rig.Host.Commits.Last()[0];
        Assert.Equal(5, removed.Band);
        Assert.Equal(FilterType.Flat, removed.Params.Type);
        Assert.Empty(rig.Host.Selection.Selected);
    }

    [Fact]
    public void AltClickOnASelectionBypassesItAll()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 200, 1, 4)), (1, Band(FilterType.Peaking, 2000, 1, -2))));
        rig.Host.Selection.SetSelected(new[] { 0, 1 });
        rig.Click(rig.At(2000, -2), PeqMods.Alt);
        Assert.Equal(new[] { (0, true), (1, true) }, rig.Host.Bypasses.OrderBy(b => b.Band));
    }

    [Fact]
    public void PullingTheCurveCreatesABandFromFlat()
    {
        var rig = MakeRig();
        var onCurve = rig.At(800, 0);
        rig.Drag(onCurve, new PeqPoint(onCurve.X, rig.G.Y(-7)));
        var created = rig.Host.Commits.Last()[0];
        Assert.Equal(FilterType.Peaking, created.Params.Type);
        Near(-7, created.Params.Gain, 0.05);
        Near(800, created.Params.Frequency, 1);
    }

    [Fact]
    public void MarqueeSelectsTheDotsInside()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 100, 1, 6)), (1, Band(FilterType.Peaking, 300, 1, 6)),
                               (2, Band(FilterType.Peaking, 5000, 1, 6))));
        rig.Drag(rig.At(70, 12), rig.At(500, 2));
        Assert.Equal(new[] { 0, 1 }, rig.Host.Selection.Selected.Order());
        Assert.Empty(rig.Host.Commits);
    }

    [Fact]
    public void ShiftClickSelectsAFrequencyRangeAndTabSteps()
    {
        // Rows out of frequency order: 0 = 4 kHz, 1 = 100 Hz, 2 = 1 kHz.
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 4000, 1, 3)), (1, Band(FilterType.Peaking, 100, 1, 3)),
                               (2, Band(FilterType.Peaking, 1000, 1, 3))));
        rig.Click(rig.At(100, 3));
        rig.Click(rig.At(1000, 3), PeqMods.Shift);
        Assert.Equal(new[] { 1, 2 }, rig.Host.Selection.Selected.Order());
        rig.Click(rig.At(100, 3));
        rig.Click(rig.At(4000, 3), PeqMods.Shift);
        Assert.Equal(new[] { 0, 1, 2 }, rig.Host.Selection.Selected.Order());

        rig.Click(rig.At(1000, 3));
        rig.View.KeyDown(PeqKey.Tab, PeqMods.None);
        Assert.Equal(new[] { 0 }, rig.Host.Selection.Selected.Order());
        rig.View.KeyDown(PeqKey.Tab, PeqMods.None);
        Assert.Equal(new[] { 1 }, rig.Host.Selection.Selected.Order());
        rig.View.KeyDown(PeqKey.Tab, PeqMods.Shift);
        Assert.Equal(new[] { 0 }, rig.Host.Selection.Selected.Order());
        rig.View.KeyDown(PeqKey.A, PeqMods.Ctrl);
        Assert.Equal(new[] { 0, 1, 2 }, rig.Host.Selection.Selected.Order());
    }

    [Fact]
    public void ArrowKeysNudgeAndCommitAfterAPause()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 1, 0)), (1, Band(FilterType.Notch, 500, 2, 0))));
        rig.Host.Selection.SetSelected(new[] { 0, 1 });
        rig.View.KeyDown(PeqKey.Right, PeqMods.None);
        rig.View.KeyDown(PeqKey.Up, PeqMods.None);
        rig.View.KeyDown(PeqKey.Up, PeqMods.Shift);
        Assert.Empty(rig.Host.Commits);
        rig.Clock.Advance(0.5);
        var p = rig.Host.Latest();
        Near(1000 * Math.Pow(2, 1.0 / 12), p[0].Frequency, 0.5, "a semitone up");
        Near(0.6, p[0].Gain, 0.001, "0.5 dB then 0.1 dB");
        Assert.Equal(0f, p[1].Gain);
        Assert.True(p[1].Q > 2, "a notch has no gain, so Up raises Q");
        rig.View.KeyDown(PeqKey.Down, PeqMods.Alt);
        rig.Clock.Advance(0.5);
        Assert.True(rig.Host.Latest()[0].Q < 1, "Alt-Down lowers Q");
    }

    [Fact]
    public void BandMenuActsOnTheSelection()
    {
        var rig = MakeRig(With((0, Band(FilterType.LowShelf, 200, 0.707f, 4)), (1, Band(FilterType.Peaking, 2000, 1, -2))));
        rig.Host.Selection.SetSelected(new[] { 0, 1 });
        var menu = rig.View.RightPressed(rig.At(200, PeqNodeRole.Of(Band(FilterType.LowShelf, 200, 0.707f, 4)).DbFor(Band(FilterType.LowShelf, 200, 0.707f, 4))))!;
        Assert.Equal("2 Bands", menu[0].Title);
        Assert.Contains(menu, m => m.Title == "Slope" && m.Submenu!.Count == 2);
        menu.First(m => m.Title == "Invert Gain").Invoke!();
        var p = rig.Host.Latest();
        Assert.Equal(-4f, p[0].Gain);
        Assert.Equal(2f, p[1].Gain);
        menu.First(m => m.Title == "Delete 2 Bands").Invoke!();
        Assert.Equal(FilterType.Flat, rig.Host.Latest()[0].Type);
        Assert.Equal(FilterType.Flat, rig.Host.Latest()[1].Type);

        var graphMenu = rig.View.RightPressed(rig.At(100, -20))!;
        Assert.Equal(new[] { "Select All Bands", "Deselect All" }, graphMenu.Select(m => m.Title));
        Assert.False(graphMenu[0].Enabled, "nothing to select");
    }

    [Fact]
    public void TypedValuesParseOrBeep()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 1, 0))));
        rig.Host.Selection.SetSelected(new[] { 0 });
        int beeps = 0;
        rig.View.Beep += () => beeps++;
        Assert.True(rig.View.HudText(PeqHudField.Freq, "A4"));
        Near(440, rig.Host.LastCommitted.Frequency, 0.01);
        Assert.False(rig.View.HudText(PeqHudField.Gain, "loud"));
        Assert.Equal(1, beeps);
        Assert.True(rig.View.HudText(PeqHudField.Gain, "+40 dB"));
        Assert.Equal(30f, rig.Host.LastCommitted.Gain);
        Assert.True(rig.View.HudText(PeqHudField.Q, "q2.5"));
        Assert.Equal(2.5f, rig.Host.LastCommitted.Q);
    }

    [Fact]
    public void ModelChangesElsewhereAnimateIntoPlace()
    {
        var rig = MakeRig(With((0, Band(FilterType.Peaking, 1000, 1, 0))));
        rig.Apply(c => new PeqEditorConfig
        {
            Channel = c.Channel, Bands = With((0, Band(FilterType.Peaking, 1000, 1, 12))), MinFreq = c.MinFreq,
            MaxFreq = c.MaxFreq, AvailableTypes = c.AvailableTypes, BypassSupported = c.BypassSupported,
        });
        Assert.True(rig.View.Animating);
        Assert.True(rig.View.Frame(rig.Clock.Now + 0.05));
        float mid = rig.View.ShownForTesting(0).Gain;
        Assert.InRange(mid, 0.5f, 11.5f);
        rig.View.Frame(rig.Clock.Now + 0.3);
        Assert.Equal(12f, rig.View.ShownForTesting(0).Gain);
    }

    [Fact]
    public void LinkwitzTransformCannotBeDragged()
    {
        var lt = new FilterParams(FilterType.LinkwitzTransform, 55, 1.1f, 25f) { Qp = 0.55f };
        var rig = MakeRig(With((0, lt)));
        var dot = rig.View.NodePointForTesting(0);
        rig.Drag(dot, new PeqPoint(dot.X + 50, dot.Y - 40));
        Assert.Empty(rig.Host.Commits);
    }

    [Fact]
    public void CrossoverBandsAreNotGraphBands()
    {
        Assert.False(Band(FilterType.Lr4Hp, 80, 0.707f, 0).IsGraphBand());
        Assert.False(new FilterParams().IsGraphBand());
        Assert.True(Band(FilterType.Notch, 80, 2, 0).IsGraphBand());
    }
}
