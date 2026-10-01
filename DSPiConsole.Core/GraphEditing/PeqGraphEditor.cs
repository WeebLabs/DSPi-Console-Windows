using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.GraphEditing;

/// <summary>
/// On-graph PEQ band editing, modelled on FabFilter Pro-Q 4. A port of the
/// macOS Console's PeqGraphEditor.swift: the interaction state machine, kept
/// apart from drawing so it runs (and is tested) without a window. The view
/// forwards pointer, wheel and key events, draws <see cref="Picture"/>, and
/// mirrors the chip, card, labels and cursor this class describes.
///
/// A drag never touches the model: the device follows at up to 30 writes a
/// second through the host, the band list follows through
/// <see cref="IPeqEditorHost.ShowLive"/>, and the model is written once, on
/// release.
///
/// Mouse (see <see cref="PeqMods"/> for the macOS key mapping):
///   hover a band's fill    highlights it; only its dot takes clicks, so every
///                          click gesture below treats a fill as empty graph
///   hover empty graph      a faint dot where a double-click would create a
///                          bell, with frequency and level readouts
///   double-click empty     create it, always a bell
///   Ctrl-click empty       a card of shapes opens there; click a shape, then
///                          for a shelf, cut or all-pass its slope
///   click empty            deselect
///   drag the curve         pull a new bell (shelf near either end) out of it
///   drag empty graph       marquee selection
///   click a dot            select; Ctrl toggles, Shift selects a range
///   drag a dot             frequency and gain (Q for second-order cuts;
///                          frequency only for notches, all-passes and
///                          first-order cuts)
///   Ctrl-drag              Q of the selection
///   Shift-drag fine; Alt-drag lock to one axis; Ctrl pressed mid-drag scales
///   a selection's gains in proportion
///   Alt-click              bypass
///   double-click a dot     type values (Tab moves between them)
///   wheel over a band      Q; Ctrl-wheel gain; Shift fine (dot or fill)
///   right-click            band menu on a dot, graph menu elsewhere
/// Keys: Delete removes the selection, Escape deselects, Ctrl-A selects all,
/// arrows move frequency and gain (Alt-arrows Q), Tab steps through bands.
/// </summary>
public sealed class PeqGraphEditor : IDisposable
{
    public static class Tuning
    {
        public const double NodeHitRadius = 10;
        public const double CurveHitDistance = 6;
        public const double DragThreshold = 2;
        public const double QPointsPerOctave = 60;
        public const double Fine = 0.12;
        public const double BandAnimation = 0.22;
        public const double EmphasisTau = 0.07;
        public const double CommitDelay = 0.45;
        public const double DeviceInterval = 1.0 / 30.0;
        /// <summary>Wheel events closer together than this belong to one gesture.</summary>
        public const double WheelSession = 0.5;
        /// <summary>How long the pointer rests on a band before its list row scrolls into view.</summary>
        public const double RevealDwell = 0.25;
        public const double HudHideDelay = 0.35;
        /// <summary>Most band rows the graph shows.</summary>
        public const int BandRows = 12;
        public static readonly PeqSize CardSize = new(110, 78);
    }

    private enum GestureKind { Idle, Press, Background, Drag, Marquee }

    private enum Axis { Horizontal, Vertical }

    private sealed class DragContext
    {
        public int Grabbed;
        public Dictionary<int, FilterParams> Start = new();
        public PeqPoint Last;
        /// <summary>Pointer movement applied so far, after fine scaling and any lock.</summary>
        public double OffsetX, OffsetY;
        public bool QMode;
        /// <summary>The axis Alt has locked the drag to; null when free.</summary>
        public Axis? Axis;
        /// <summary>Alt is held but no axis is chosen yet: movement gathers here,
        /// with the band held still, until it shows a direction.</summary>
        public (double X, double Y)? Pending;
    }

    private struct Emphasis
    {
        public float Hover, Select, HoverTarget, SelectTarget;
        public readonly bool Settled => Hover == HoverTarget && Select == SelectTarget;
    }

    private readonly IPeqEditorHost _host;
    private readonly IPeqScheduler _clock;

    private PeqEditorConfig _config = new();
    private PeqGraphGeometry _geometry = new(0, 0, 20, 20000, 25, -25);
    private IReadOnlySet<FilterType> _available = new HashSet<FilterType>();

    // Bands: _committed mirrors the model, _live holds edits not yet committed,
    // _shown is what is drawn (committed, live or mid-animation).
    private List<FilterParams> _committed = new();
    private List<FilterParams> _shown = new();
    private readonly Dictionary<int, FilterParams> _live = new();
    private List<FilterParams>? _animationFrom;
    private double _animationStart;

    private HashSet<int> _selection = new();
    private int? _anchor;
    private int? _hovered;
    private int? _listHovered;
    private FilterParams? _ghost;
    private FilterParams? _ghostShown;
    private float _ghostAmount;
    private PeqPoint? _pointer;

    private GestureKind _gesture = GestureKind.Idle;
    private int _pressBand;
    private PeqPoint _pressAt;
    private PeqMods _pressMods;
    private bool _backgroundOnCurve;
    private DragContext? _drag;
    private PeqPoint _marqueeFrom, _marqueeTo;
    private HashSet<int> _marqueeBase = new();

    private Emphasis[] _emphasis = new Emphasis[Tuning.BandRows];
    private double _lastFrame;

    private int? _hudBand;
    private bool _hudVisible;
    private bool _hudEditing;
    private bool _showsShapes;
    private PeqSize? _pageSize;
    private PeqPoint? _pagedOrigin;
    private FilterParams? _hudAdjustStart;
    private PeqHudField? _hudWheelField;
    private double _hudWheelTime;

    private PeqPoint? _cardPoint;
    private PeqPoint _cardOrigin;

    private IPeqTimer? _hudHideTimer, _revealTimer, _deviceTimer, _commitTimer;
    private readonly HashSet<int> _deviceDirty = new();
    private int? _wheelBand;
    private double _wheelTime;

    private string? _axisLabel;
    private double _axisLabelX;
    private string? _gainLabel;
    private double _gainLabelY;
    private PeqCursor _cursor = PeqCursor.Arrow;

    private PeqPicture _picture = new();
    /// <summary>The state moved since <see cref="_picture"/> was built; it is
    /// rebuilt when next asked for, at most once a drawn frame however fast
    /// input arrives.</summary>
    private bool _pictureStale = true;

    public PeqGraphEditor(IPeqEditorHost host, IPeqScheduler clock)
    {
        _host = host;
        _clock = clock;
        _host.Selection.SelectionChanged += OnSharedSelectionChanged;
        _host.Selection.HoverChanged += OnSharedHoverChanged;
    }

    public void Dispose()
    {
        CommitLive();
        _host.Selection.SelectionChanged -= OnSharedSelectionChanged;
        _host.Selection.HoverChanged -= OnSharedHoverChanged;
        foreach (var t in new[] { _hudHideTimer, _revealTimer, _deviceTimer, _commitTimer }) t?.Cancel();
    }

    // ── Outputs for the view ────────────────────────────────────────────────

    /// <summary>Raised whenever anything the view shows may have changed.</summary>
    public event Action? Changed;
    /// <summary>The system beep: nothing to create, or text that didn't parse.</summary>
    public event Action? Beep;
    /// <summary>The chip should start typing into a field.</summary>
    public event Action<PeqHudField>? BeginHudEditRequested;
    /// <summary>The chip should stop typing (without applying).</summary>
    public event Action? EndHudEditRequested;

    public bool Editing => _config.Channel != null;
    public int? Channel => _config.Channel;
    public PeqGraphGeometry Geometry => _geometry;
    public PeqPicture Picture
    {
        get
        {
            if (_pictureStale)
            {
                _picture = BuildPicture();
                _pictureStale = false;
            }
            return _picture;
        }
    }
    public PeqCursor Cursor => _cursor;
    public IReadOnlySet<int> Selection => _selection;
    public int? Hovered => _hovered;
    public string? AxisLabel => _axisLabel;
    public double AxisLabelX => _axisLabelX;
    public string? GainLabel => _gainLabel;
    public double GainLabelY => _gainLabelY;
    public IReadOnlySet<FilterType> AvailableTypes => _available;
    public bool BypassSupported => _config.BypassSupported;

    /// <summary>Whether an animation is running and the view should keep calling
    /// <see cref="Frame"/>.</summary>
    public bool Animating =>
        _animationFrom != null || _ghostAmount != (_ghost != null ? 1 : 0) || _emphasis.Any(e => !e.Settled);

    // Chip
    public int? HudBand => _hudBand;
    public bool HudVisible => _hudVisible && _hudBand != null;
    public FilterParams? HudParams => _hudBand is { } b && IsBand(b) ? Current(b) : null;
    public bool ShowsShapes => _showsShapes;
    public PeqRect HudFrame { get; private set; }
    public bool HudEditing => _hudEditing;

    // Ctrl-click card
    public bool CardOpen => _cardPoint != null;
    public PeqPoint CardOrigin => _cardOrigin;

    /// <summary>The chip's size for <paramref name="p"/>, fixed per shape so
    /// the view and the editor agree on where it goes.</summary>
    public static PeqSize HudSizeFor(FilterParams p)
    {
        int rows;
        if (PeqShapes.Of(p.Type) != null) rows = 1 + (p.Type.HasGain() ? 1 : 0) + 1;
        else rows = 2;
        return new PeqSize(110, 28 + rows * 15 + 5);
    }

    // ── Configuration ───────────────────────────────────────────────────────

    public void Apply(PeqEditorConfig next)
    {
        if (next.SameAs(_config) && _geometry.Width > 0) return;
        // A pending wheel or key edit belongs to the channel it was made on, so
        // it is committed before the new one takes over.
        if (next.Channel != _config.Channel) CommitLive();
        var old = _config;
        _config = next;
        _available = next.AvailableTypes;
        UpdateGeometry();
        var bands = next.Bands.Select(b => b.Clone()).ToList();

        if (old.Channel != next.Channel || !Editing)
        {
            ResetInteraction();
            _committed = bands;
            _shown = bands.Select(b => b.Clone()).ToList();
            _animationFrom = null;
        }
        else if (!PeqEditorConfig.BandsSame(bands, _committed))
        {
            _committed = bands;
            if (_gesture == GestureKind.Idle && _live.Count == 0)
            {
                StartBandAnimation();
            }
            else
            {
                // Mid-edit, only the bands not being edited follow the model.
                if (_shown.Count != _committed.Count)
                    _shown = _committed.Select((p, i) => (_live.TryGetValue(i, out var l) ? l : p).Clone()).ToList();
                else
                    for (int i = 0; i < _shown.Count; i++)
                        if (!_live.ContainsKey(i)) _shown[i] = _committed[i].Clone();
            }
        }
        RefreshHud();
        Invalidate();
    }

    public void Resize(double width, double height)
    {
        if (width == _geometry.Width && height == _geometry.Height) return;
        _geometry = _geometry with { Width = width, Height = height };
        LayoutHud();
        Invalidate();
    }

    private void UpdateGeometry() =>
        _geometry = new PeqGraphGeometry(_geometry.Width, _geometry.Height, _config.MinFreq, _config.MaxFreq,
                                         _config.DbTop, _config.DbBottom);

    private void ResetInteraction()
    {
        CloseCard();
        _gesture = GestureKind.Idle;
        _drag = null;
        _selection = new HashSet<int>();
        _anchor = null;
        _hovered = null;
        _listHovered = null;
        _ghost = null;
        _ghostShown = null;
        _ghostAmount = 0;
        HideAxisLabels();
        HideHud();
        _emphasis = new Emphasis[Tuning.BandRows];
    }

    private void OnSharedSelectionChanged()
    {
        var set = _host.Selection.Selected;
        if (set.SetEquals(_selection)) return;
        _selection = new HashSet<int>(set);
        _anchor = set.Count == 1 ? set.First() : _anchor;
        PinHudToSelection();
        Invalidate();
    }

    private void OnSharedHoverChanged()
    {
        if (_listHovered == _host.Selection.ListHovered) return;
        _listHovered = _host.Selection.ListHovered;
        Invalidate();
    }

    // ── Band queries ────────────────────────────────────────────────────────

    private int BandCount => Math.Min(_shown.Count, Tuning.BandRows);

    private bool IsBand(int i) => i >= 0 && i < BandCount && _shown[i].IsGraphBand();

    private List<int> GraphBands => Enumerable.Range(0, BandCount).Where(IsBand).ToList();

    private static PeqNodeRole Role(FilterParams p) => PeqNodeRole.Of(p);

    /// <summary>
    /// The graph's axes as a band's own response reads on them. The combined
    /// curve includes the channel's level offset (gain or preamp, when the
    /// graph shows it), so every band's dot, fill and fill baseline sits
    /// <see cref="PeqEditorConfig.OffsetDb"/> lower or higher too, and stays on
    /// the curve. Band values themselves never include the offset: a +3 dB bell
    /// reads +3 dB wherever it is drawn, and a band placed at a point takes the
    /// gain that puts it there.
    /// </summary>
    private PeqGraphGeometry BandGeometry => _geometry with
    {
        DbTop = _geometry.DbTop - _config.OffsetDb,
        DbBottom = _geometry.DbBottom - _config.OffsetDb,
    };

    private PeqPoint NodePoint(FilterParams p)
    {
        double y = BandGeometry.Y(Role(p).DbFor(p));
        return new PeqPoint(_geometry.X(p.Frequency), Math.Min(Math.Max(y, 6), Math.Max(_geometry.Height - 6, 6)));
    }

    private static double BandDb(FilterParams p, double freq)
    {
        var probe = p.Clone();
        probe.Bypass = false;
        probe.IsActive = true;
        return DspMath.ResponseAt((float)freq, new[] { probe });
    }

    private double CombinedDb(double freq)
    {
        double db = _config.OffsetDb;
        if (_config.Flat) return db;
        var audible = GraphBands.Select(b => _shown[b]).Where(p => !p.Bypass).Concat(_config.Statics);
        return db + DspMath.ResponseAt((float)freq, audible);
    }

    /// <summary>The dot under <paramref name="point"/>, preferring selected and
    /// hovered dots, which are drawn on top.</summary>
    private int? NodeAt(PeqPoint point)
    {
        int? best = null;
        double bestScore = double.MaxValue;
        foreach (int b in GraphBands)
        {
            double d = NodePoint(_shown[b]).DistanceTo(point);
            if (d > Tuning.NodeHitRadius) continue;
            double score = d - (_selection.Contains(b) ? 3 : 0) - (_hovered == b ? 2 : 0);
            if (best == null || score < bestScore) { best = b; bestScore = score; }
        }
        return best;
    }

    /// <summary>The band whose filled area (between its curve and 0 dB) contains
    /// <paramref name="point"/>. A fill highlights its band and takes the wheel,
    /// but clicks go through it to the graph, so a band can be placed anywhere;
    /// only the dot selects or grabs a band.</summary>
    private int? LobeAt(PeqPoint point)
    {
        var g = BandGeometry;
        double freq = g.Freq(point.X);
        double zero = g.Y(0);
        int? best = null;
        double bestDistance = double.MaxValue;
        foreach (int b in GraphBands)
        {
            if (Role(_shown[b]).Kind == PeqNodeRole.RoleKind.Locked) continue;
            double y = g.Y(BandDb(_shown[b], freq));
            if (Math.Abs(y - zero) < 3 || point.Y < Math.Min(y, zero) || point.Y > Math.Max(y, zero)) continue;
            double d = Math.Abs(point.Y - y);
            if (best == null || d < bestDistance) { best = b; bestDistance = d; }
        }
        return best;
    }

    /// <summary>The band the wheel adjusts at a point: its dot or its fill.</summary>
    private int? BandAt(PeqPoint point) => NodeAt(point) ?? LobeAt(point);

    private bool IsNearCurve(PeqPoint point) =>
        Math.Abs(_geometry.Y(CombinedDb(_geometry.Freq(point.X))) - point.Y) <= Tuning.CurveHitDistance;

    private int? FreeSlot
    {
        get
        {
            for (int i = 0; i < Math.Min(_committed.Count, Tuning.BandRows); i++)
                if (_committed[i].Type == FilterType.Flat && !_live.ContainsKey(i)) return i;
            return null;
        }
    }

    private (double Lo, double Hi) FreqRange
    {
        get
        {
            double lo = Math.Max(PeqLimits.FreqMin, _config.MinFreq);
            double hi = Math.Min(PeqLimits.FreqMax, _config.MaxFreq);
            return (lo, Math.Max(hi, lo));
        }
    }

    private double ClampToRange(double f) { var (lo, hi) = FreqRange; return PeqLimits.Clamp(f, lo, hi); }

    private List<int> FrequencyOrder(IEnumerable<int> bands) =>
        bands.OrderBy(b => _shown[b].Frequency).ThenBy(b => b).ToList();

    private FilterParams Current(int b) => _live.TryGetValue(b, out var l) ? l : _shown[b];

    // ── Drawing ─────────────────────────────────────────────────────────────

    /// <summary>Recomputes what the view shows and tells it so.</summary>
    private void Invalidate()
    {
        UpdateEmphasisTargets();
        _pictureStale = true;
        Changed?.Invoke();
    }

    private void UpdateEmphasisTargets()
    {
        var dragged = _gesture == GestureKind.Drag && _drag != null ? _drag.Start.Keys.ToHashSet() : new HashSet<int>();
        for (int i = 0; i < _emphasis.Length; i++)
        {
            bool shown = IsBand(i);
            _emphasis[i].SelectTarget = shown && _selection.Contains(i) ? 1 : 0;
            _emphasis[i].HoverTarget = shown && (_hovered == i || _listHovered == i || dragged.Contains(i)
                                                 || (_hudBand == i && _hudVisible)) ? 1 : 0;
        }
    }

    /// <summary>Advances animations to <paramref name="now"/> (seconds;
    /// <see cref="double.PositiveInfinity"/> finishes everything) and rebuilds
    /// the picture. Returns true while any animation is still running.</summary>
    public bool Frame(double now)
    {
        bool finite = double.IsFinite(now);
        double dt = finite ? (_lastFrame == 0 ? 1.0 / 60 : Math.Min(now - _lastFrame, 0.1)) : 1;
        _lastFrame = finite ? now : 0;
        bool running = false;

        if (_animationFrom is { } from)
        {
            double t = finite ? (now - _animationStart) / Tuning.BandAnimation : 1;
            var target = _committed.Select((p, i) => _live.TryGetValue(i, out var l) ? l : p).ToList();
            if (t >= 1 || from.Count != target.Count)
            {
                _shown = target.Select(p => p.Clone()).ToList();
                _animationFrom = null;
                RefreshHud();
            }
            else
            {
                double eased = 1 - Math.Pow(1 - t, 3);
                _shown = from.Zip(target, (a, b) => a.Interpolated(b, eased)).ToList();
                running = true;
                LayoutHud();
            }
        }

        UpdateEmphasisTargets();
        float k = (float)(1 - Math.Exp(-dt / Tuning.EmphasisTau));
        static void Approach(ref float v, float target, float k)
        {
            v += (target - v) * k;
            if (Math.Abs(target - v) < 0.004f) v = target;
        }
        for (int i = 0; i < _emphasis.Length; i++)
        {
            Approach(ref _emphasis[i].Hover, _emphasis[i].HoverTarget, k);
            Approach(ref _emphasis[i].Select, _emphasis[i].SelectTarget, k);
            if (!_emphasis[i].Settled) running = true;
        }
        Approach(ref _ghostAmount, _ghost != null ? 1 : 0, k);
        if (_ghost != null) _ghostShown = _ghost;
        if (_ghostAmount == 0) _ghostShown = null;
        if (_ghostAmount != (_ghost != null ? 1 : 0)) running = true;

        _pictureStale = true;
        if (!running) _lastFrame = 0;
        return running;
    }

    private PeqPicture BuildPicture()
    {
        float dimAll = _config.Flat ? 0.5f : 1f;
        var styles = new List<PeqBandStyle>();
        var nodes = new List<(float Order, PeqNode Node)>();
        foreach (int b in GraphBands)
        {
            var band = _shown[b];
            var e = _emphasis[b];
            float lift = Math.Max(e.Hover, e.Select);
            var color = PeqBandPalette.Color(b);
            if (band.Bypass)
            {
                float grey = (color.R + color.G + color.B) / 3;
                const float keep = 0.35f;
                color = (grey + (color.R - grey) * keep, grey + (color.G - grey) * keep, grey + (color.B - grey) * keep);
            }
            // Every audible band keeps a faint fill and shows its outline while
            // hovered or selected; a bypassed one stays as a grey ghost of both.
            var (lo, hi) = LobeReach(band);
            styles.Add(new PeqBandStyle(b, color,
                (band.Bypass ? 0.35f + 0.25f * lift : 0.9f * lift) * dimAll,
                (band.Bypass ? 0.06f + 0.06f * lift : 0.22f + 0.2f * lift) * dimAll,
                lo, hi));
            var c = NodePoint(band);
            // Flat discs; hovering enlarges them, selecting also adds a centre dot.
            nodes.Add((lift + e.Select, new PeqNode(c.X, c.Y, 5 + 1.5 * lift + 0.5 * e.Select, color,
                (band.Bypass ? 0.55f : 1f) * dimAll, e.Select)));
        }
        if (_ghostShown is { } g && _ghostAmount > 0)
        {
            var c = NodePoint(g);
            nodes.Add((-1, new PeqNode(c.X, c.Y, 5, (1, 1, 1), 0.3f * _ghostAmount, 0)));
        }

        PeqRect? marquee = null;
        if (_gesture == GestureKind.Marquee)
            marquee = new PeqRect(Math.Min(_marqueeFrom.X, _marqueeTo.X), Math.Min(_marqueeFrom.Y, _marqueeTo.Y),
                                  Math.Abs(_marqueeTo.X - _marqueeFrom.X), Math.Abs(_marqueeTo.Y - _marqueeFrom.Y));

        return new PeqPicture
        {
            Geometry = _geometry,
            Bands = Enumerable.Range(0, Tuning.BandRows).Select(i => IsBand(i) ? _shown[i] : null).ToList(),
            Statics = _config.Statics,
            OffsetDb = _config.OffsetDb,
            Flat = _config.Flat,
            CurveColor = _config.CurveColor,
            LineWidth = _config.LineWidth,
            Glow = _config.Glow,
            BandStyles = styles,
            // Emphasised dots last, so they draw on top.
            Nodes = nodes.OrderBy(n => n.Order).Select(n => n.Node).ToList(),
            Marquee = marquee,
        };
    }

    /// <summary>The dB range a band's lobe spans, to bound its fill: 0 dB to the
    /// gain for bells and shelves, down off the plot for cuts and notches.</summary>
    private (double Lo, double Hi) LobeReach(FilterParams p)
    {
        // In the band's own dB, so off the plot whatever the level offset.
        double bottom = _config.DbBottom - _config.OffsetDb - 1;
        return p.Type switch
        {
            FilterType.Peaking or FilterType.LowShelf or FilterType.HighShelf
                or FilterType.LowShelf1 or FilterType.HighShelf1 => (Math.Min(p.Gain, 0), Math.Max(p.Gain, 0)),
            FilterType.LowPass or FilterType.HighPass => (bottom, Math.Max(20 * Math.Log10(Math.Max(p.Q, 0.01)), 0)),
            FilterType.AllPass or FilterType.AllPass1 => (0, 0),
            _ => (bottom, _config.DbTop - _config.OffsetDb + 1),
        };
    }

    private void StartBandAnimation()
    {
        var target = _committed;
        if (_shown.Count != target.Count)
        {
            _shown = target.Select(p => p.Clone()).ToList();
            return;
        }
        bool significant = _shown.Zip(target).Any(t =>
        {
            var (a, b) = t;
            return a.Type != b.Type || a.Bypass != b.Bypass || Math.Abs(a.Gain - b.Gain) > 0.02
                || Math.Abs(Math.Log(Math.Max(a.Frequency, 1.0) / Math.Max(b.Frequency, 1.0))) > 0.002
                || Math.Abs(Math.Log(Math.Max(a.Q, 0.01) / Math.Max(b.Q, 0.01))) > 0.002;
        });
        if (significant)
        {
            _animationFrom = _shown.Select(p => p.Clone()).ToList();
            _animationStart = _clock.Now;
        }
        else
        {
            _shown = target.Select(p => p.Clone()).ToList();
        }
    }

    // ── Editing pipeline ────────────────────────────────────────────────────

    /// <summary>A change in progress: drawn, shown in the chip and the list,
    /// sent to the device at most 30 times a second, and committed later.</summary>
    private void SetLive(Dictionary<int, FilterParams> changes)
    {
        if (changes.Count == 0) return;
        if (_animationFrom != null)
        {
            _animationFrom = null;
            _shown = _committed.Select((p, i) => (_live.TryGetValue(i, out var l) ? l : p).Clone()).ToList();
        }
        foreach (var (b, p) in changes)
        {
            if (b >= _shown.Count) continue;
            _live[b] = p;
            _shown[b] = p;
            if (_config.Channel is { } ch) _host.ShowLive(ch, b, p);
        }
        _deviceDirty.UnionWith(changes.Keys);
        if (_deviceTimer == null) SendToDevice();
        RefreshHud();
        Invalidate();
    }

    private void SendToDevice()
    {
        if (_config.Channel is not { } ch || _deviceDirty.Count == 0) return;
        var changes = _deviceDirty.OrderBy(b => b)
            .Where(b => _live.ContainsKey(b))
            .Select(b => (b, _live[b].Clone()))
            .ToList();
        _deviceDirty.Clear();
        _host.SendGraphBandsToDevice(ch, changes);
        _deviceTimer = _clock.Schedule(Tuning.DeviceInterval, () =>
        {
            _deviceTimer = null;
            SendToDevice();
        });
    }

    /// <summary>Writes live edits to the model, once.</summary>
    private void CommitLive()
    {
        _deviceTimer?.Cancel();
        _deviceTimer = null;
        _deviceDirty.Clear();
        _commitTimer?.Cancel();
        _commitTimer = null;
        if (_config.Channel is { } endCh && _live.Count > 0) _host.EndLive(endCh);
        var changes = _live.Keys.OrderBy(b => b)
            .Where(b => b < _committed.Count && !_live[b].SameAs(_committed[b]))
            .Select(b => (b, _live[b].Clone()))
            .ToList();
        _live.Clear();
        if (changes.Count == 0 || _config.Channel is not { } ch) return;
        foreach (var (band, p) in changes) _committed[band] = p.Clone();
        _host.CommitGraphBands(ch, changes);
    }

    /// <summary>For wheel and key edits: live now, committed once they pause.</summary>
    private void SetLiveThenCommit(Dictionary<int, FilterParams> changes)
    {
        SetLive(changes);
        _commitTimer?.Cancel();
        _commitTimer = _clock.Schedule(Tuning.CommitDelay, CommitLive);
    }

    /// <summary>Discrete edits (create, delete, shape, typed values) commit at once.</summary>
    private void CommitNow(Dictionary<int, FilterParams> changes)
    {
        CommitLive();
        if (_config.Channel is not { } ch || changes.Count == 0) return;
        _animationFrom = null;
        foreach (var (b, p) in changes)
        {
            if (b >= _committed.Count) continue;
            _committed[b] = p.Clone();
            _shown[b] = p.Clone();
        }
        _host.CommitGraphBands(ch, changes.Keys.OrderBy(b => b).Select(b => (b, changes[b].Clone())).ToList());
        RefreshHud();
        Invalidate();
    }

    private void SetSelection(IEnumerable<int> next)
    {
        var valid = next.Where(IsBand).ToHashSet();
        if (valid.SetEquals(_selection)) return;
        _selection = valid;
        if (!_host.Selection.Selected.SetEquals(valid)) _host.Selection.SetSelected(valid);
        PinHudToSelection();
        Invalidate();
    }

    private void SetHovered(int? band)
    {
        if (band == _hovered) return;
        _hovered = band;
        if (_host.Selection.GraphHovered != band) _host.Selection.GraphHovered = band;
        ScheduleReveal();
        Invalidate();
    }

    /// <summary>Once the pointer rests on a band, its list row scrolls into view.
    /// A sweep across the graph passes over bands too briefly to move the list,
    /// and the list holds still while a band is dragged or wheeled.</summary>
    private void ScheduleReveal()
    {
        _revealTimer?.Cancel();
        _revealTimer = null;
        if (_hovered is not { } band) return;
        _revealTimer = _clock.Schedule(Tuning.RevealDwell, () =>
        {
            _revealTimer = null;
            if (_hovered != band) return;
            if (_gesture != GestureKind.Idle || GraphWheelActive || HudWheelActive)
            {
                ScheduleReveal();
                return;
            }
            _host.Selection.RequestReveal(band);
        });
    }

    // ── Band operations ─────────────────────────────────────────────────────

    private int? CreateBand(FilterParams p, bool select = true)
    {
        if (FreeSlot is not { } slot) { Beep?.Invoke(); return null; }
        CommitNow(new Dictionary<int, FilterParams> { [slot] = p });
        if (select)
        {
            SetSelection(new[] { slot });
            _anchor = slot;
        }
        _ghost = null;
        return slot;
    }

    private void DeleteBands(IEnumerable<int> bands)
    {
        var targets = bands.Where(IsBand).ToHashSet();
        if (targets.Count == 0) return;
        var changes = targets.ToDictionary(b => b, _ => new FilterParams(FilterType.Flat, 1000f, FilterParams.DefaultQ, 0f));
        SetSelection(_selection.Except(targets));
        if (_hudBand is { } h && targets.Contains(h)) HideHud();
        CommitNow(changes);
        PinHudToSelection();
    }

    private void ToggleBypass(IEnumerable<int> bands)
    {
        if (!_config.BypassSupported || _config.Channel is not { } ch) return;
        var targets = bands.Where(IsBand).OrderBy(b => b).ToList();
        if (targets.Count == 0) return;
        CommitLive();
        bool bypass = !_shown[targets[0]].Bypass;
        _animationFrom = null;
        foreach (int b in targets)
        {
            _committed[b].Bypass = bypass;
            _shown[b].Bypass = bypass;
        }
        _host.SetGraphBandBypass(ch, targets, bypass);
        RefreshHud();
        Invalidate();
    }

    /// <summary>Changes shape (and order), keeping frequency, and carrying gain
    /// and Q over where the new shape uses them.</summary>
    private void SetShape(IEnumerable<int> bands, PeqShape shape, int order)
    {
        var changes = new Dictionary<int, FilterParams>();
        foreach (int b in bands.Where(IsBand))
        {
            if ((shape.Type(order) ?? shape.Type(2)) is not { } type || !_available.Contains(type)) continue;
            var p = Current(b).Retyped(type);
            if (!type.HasGain()) p.Gain = 0;
            if (type.HasQ() && !Current(b).Type.HasQ()) p.Q = shape is PeqShape.Bell or PeqShape.Notch ? 1f : 0.707f;
            changes[b] = p;
        }
        CommitNow(changes);
    }

    /// <summary>FabFilter applies a band action to the whole selection when the
    /// band is part of it, and to the band alone otherwise.</summary>
    private HashSet<int> TargetsFor(int b) => _selection.Contains(b) ? new HashSet<int>(_selection) : new HashSet<int> { b };

    // ── Pointer ─────────────────────────────────────────────────────────────

    public void PointerMoved(PeqPoint p)
    {
        if (!Editing) return;
        _pointer = p;
        UpdateHover(p);
    }

    public void PointerExited()
    {
        _pointer = null;
        if (_gesture != GestureKind.Idle) return;
        SetHovered(null);
        _ghost = null;
        HideAxisLabels();
        ScheduleHudHide();
        _cursor = PeqCursor.Arrow;
        Invalidate();
    }

    private void UpdateHover(PeqPoint p)
    {
        if (_gesture != GestureKind.Idle) return;
        var bounds = new PeqRect(0, 0, _geometry.Width, _geometry.Height);
        if (!bounds.Contains(p))
        {
            SetHovered(null);
            _ghost = null;
            HideAxisLabels();
            ScheduleHudHide();
            Invalidate();
            return;
        }
        // While the Ctrl-click card is open, the faint dot marks where its band
        // will go, and nothing else on the graph responds to hovering.
        if (_cardPoint is { } at)
        {
            SetHovered(null);
            _ghost = PeqCreation.Band(at.X, at.Y, BandGeometry, _available, fromCurve: false);
            HideAxisLabels();
            _cursor = PeqCursor.Arrow;
            Invalidate();
            return;
        }
        if (HudVisible && HudFrame.Inset(-4, -4).Contains(p))
        {
            CancelHudHide();
            _ghost = null;
            HideAxisLabels();
            _cursor = PeqCursor.Arrow;
            Invalidate();
            return;
        }
        int? dot = NodeAt(p);
        int? hit = dot ?? LobeAt(p);
        SetHovered(hit);
        if (SelectedChipBand is { } pin)
        {
            // The chip stays on the selection; hovering another band lights it
            // and its list row instead.
            if (!HudVisible || _hudBand != pin) ShowHud(pin);
        }
        else if (dot is { } d)
        {
            if (_hudBand != d || !HudVisible) ShowHud(d); else CancelHudHide();
        }
        else if (HudVisible && !_hudEditing)
        {
            ScheduleHudHide();
        }
        if (dot == null)
        {
            if (FreeSlot != null)
            {
                _ghost = PeqCreation.Band(p.X, p.Y, BandGeometry, _available, fromCurve: false);
                if (_config.ShowFrequencyReadout) ShowAxisLabel(PeqValueText.Frequency(_geometry.Freq(p.X)), p.X);
                else _axisLabel = null;
                // The pointer's level on the graph's axis. A band placed here
                // takes this less the level offset, which puts its dot here.
                if (_config.ShowLevelReadout) ShowGainLabel(PeqValueText.Gain(PeqLimits.ClampGain(_geometry.Db(p.Y))), p.Y);
                else _gainLabel = null;
            }
            else
            {
                _ghost = null;
                _gainLabel = null;
                ShowAxisLabel($"All {Math.Min(_committed.Count, Tuning.BandRows)} bands in use", p.X);
            }
        }
        else
        {
            _ghost = null;
            HideAxisLabels();
        }
        _cursor = dot != null ? PeqCursor.OpenHand : PeqCursor.Arrow;
        Invalidate();
    }

    /// <summary>A press with the primary button. <paramref name="clickCount"/>
    /// is 2 for the second press of a double-click.</summary>
    public void PointerPressed(PeqPoint p, PeqMods mods, int clickCount)
    {
        if (!Editing) return;
        CloseShapePage();
        if (_hudEditing) EndHudEditRequested?.Invoke();

        // The card takes its own clicks; one anywhere else dismisses it without
        // acting on the graph.
        if (_cardPoint != null)
        {
            CloseCard();
            _gesture = GestureKind.Idle;
            UpdateHover(p);
            return;
        }

        if (clickCount == 2)
        {
            if (NodeAt(p) is { } b)
            {
                ShowHud(b);
                BeginHudEditRequested?.Invoke(PeqHudField.Freq);
            }
            else
            {
                CreateBand(PeqCreation.Band(p.X, p.Y, BandGeometry, _available, fromCurve: false));
            }
            _gesture = GestureKind.Idle;
            Invalidate();
            return;
        }

        if (NodeAt(p) is { } band)
        {
            // Modified presses wait for release (a click) or movement (a drag)
            // before doing anything, so Alt and Shift can mean both.
            if ((mods & (PeqMods.Ctrl | PeqMods.Alt | PeqMods.Shift)) == 0 && !_selection.Contains(band))
            {
                SetSelection(new[] { band });
                _anchor = band;
            }
            _gesture = GestureKind.Press;
            _pressBand = band;
            _pressAt = p;
            _pressMods = mods;
            // A modified press on a band outside the selection leaves the chip on
            // the selection.
            ShowHud(_selection.Count == 0 || _selection.Contains(band) ? band : (SelectedChipBand ?? band));
            return;
        }

        if ((mods & PeqMods.Ctrl) != 0)
        {
            OpenCard(p);
            _gesture = GestureKind.Idle;
            return;
        }
        _gesture = GestureKind.Background;
        _pressAt = p;
        _pressMods = mods;
        _backgroundOnCurve = FreeSlot != null && IsNearCurve(p);
    }

    /// <summary>Pointer movement with the primary button held.</summary>
    public void PointerDragged(PeqPoint p, PeqMods mods)
    {
        if (!Editing) return;
        _pointer = p;
        switch (_gesture)
        {
            case GestureKind.Press:
            {
                if (p.DistanceTo(_pressAt) < Tuning.DragThreshold) return;
                int b = _pressBand;
                var bands = _selection.Contains(b) ? new HashSet<int>(_selection) : new HashSet<int> { b };
                bool q = (_pressMods & PeqMods.Ctrl) != 0;
                if (q) bands.Add(b);
                if (Role(Current(b)).Kind == PeqNodeRole.RoleKind.Locked) { _gesture = GestureKind.Idle; return; }
                if (q && !_selection.Contains(b))
                {
                    SetSelection(_selection.Append(b));
                }
                else if (!_selection.Contains(b))
                {
                    SetSelection(new[] { b });
                    _anchor = b;
                }
                BeginDrag(b, bands, _pressAt, q);
                ContinueDrag(p, mods);
                break;
            }
            case GestureKind.Background:
            {
                if (p.DistanceTo(_pressAt) < Tuning.DragThreshold) return;
                _ghost = null;
                HideAxisLabels();
                if (_backgroundOnCurve && FreeSlot is { } slot)
                {
                    // Pull a new band out of the curve, starting flat so the curve
                    // does not jump, then follow the pointer.
                    var band = PeqCreation.Band(_pressAt.X, _pressAt.Y, BandGeometry, _available, fromCurve: true);
                    band.Gain = 0;
                    _live[slot] = band;
                    if (slot < _shown.Count) _shown[slot] = band;
                    SetSelection(new[] { slot });
                    _anchor = slot;
                    BeginDrag(slot, new HashSet<int> { slot }, _pressAt, qMode: false);
                    ContinueDrag(p, mods);
                }
                else
                {
                    // Ctrl opens the shape card, so only Shift reaches here to add.
                    _marqueeBase = (_pressMods & PeqMods.Shift) != 0 ? new HashSet<int>(_selection) : new HashSet<int>();
                    _marqueeFrom = _pressAt;
                    _marqueeTo = p;
                    _gesture = GestureKind.Marquee;
                    UpdateMarquee();
                }
                break;
            }
            case GestureKind.Drag:
                ContinueDrag(p, mods);
                break;
            case GestureKind.Marquee:
                _marqueeTo = p;
                UpdateMarquee();
                break;
        }
    }

    public void PointerReleased(PeqPoint p)
    {
        if (!Editing) return;
        switch (_gesture)
        {
            case GestureKind.Press:
            {
                int b = _pressBand;
                // Released without moving: a click.
                if ((_pressMods & PeqMods.Alt) != 0)
                {
                    ToggleBypass(TargetsFor(b));
                }
                else if ((_pressMods & PeqMods.Ctrl) != 0)
                {
                    var next = new HashSet<int>(_selection);
                    if (!next.Remove(b)) next.Add(b);
                    SetSelection(next);
                    _anchor = b;
                }
                else if ((_pressMods & PeqMods.Shift) != 0)
                {
                    var order = FrequencyOrder(GraphBands);
                    int i = _anchor is { } a ? order.IndexOf(a) : -1;
                    int j = order.IndexOf(b);
                    if (i >= 0 && j >= 0)
                    {
                        SetSelection(order.GetRange(Math.Min(i, j), Math.Abs(i - j) + 1));
                    }
                    else
                    {
                        SetSelection(new[] { b });
                        _anchor = b;
                    }
                }
                else
                {
                    SetSelection(new[] { b });
                    _anchor = b;
                }
                break;
            }
            case GestureKind.Background:
                // A single click on empty graph only deselects; creating a band
                // takes a double-click (or Ctrl-click), so a click to focus or
                // dismiss never adds one by accident.
                SetSelection(Array.Empty<int>());
                // An explicit deselect dismisses the chip, and with it the band's
                // highlight, at once. The hover grace delay is only there so the
                // pointer can travel from a dot to its chip.
                HideHud();
                break;
            case GestureKind.Drag:
                _gesture = GestureKind.Idle;
                _drag = null;
                CommitLive();
                _cursor = PeqCursor.OpenHand;
                break;
        }
        _gesture = GestureKind.Idle;
        _drag = null;
        Invalidate();
        UpdateHover(p);
    }

    /// <summary>The pointer was taken away mid-gesture (capture lost): finish as
    /// a release where it last was.</summary>
    public void PointerCanceled()
    {
        if (_gesture == GestureKind.Idle) return;
        PointerReleased(_pointer ?? _pressAt);
    }

    private void BeginDrag(int grabbed, HashSet<int> bands, PeqPoint at, bool qMode)
    {
        _commitTimer?.Cancel();
        _commitTimer = null;
        var start = new Dictionary<int, FilterParams>();
        foreach (int b in bands)
            if (IsBand(b) || _live.ContainsKey(b)) start[b] = Current(b).Clone();
        start[grabbed] = Current(grabbed).Clone();
        _drag = new DragContext { Grabbed = grabbed, Start = start, Last = at, QMode = qMode };
        _gesture = GestureKind.Drag;
        _ghost = null;
        HideAxisLabels();
        ShowHud(grabbed);
        _cursor = qMode ? PeqCursor.UpDown : PeqCursor.ClosedHand;
    }

    /// <summary>
    /// Alt locks the drag to one axis whenever it is held, not only from the
    /// start, and never makes the band jump: while locked, movement on the other
    /// axis is simply not applied, so that axis stays where it was when the lock
    /// engaged, and releasing Alt carries on from there. Engaging mid-drag takes
    /// the axis the drag has mostly moved along; with too little movement to
    /// tell (or Alt held from the start), the next few points decide.
    /// </summary>
    private void ContinueDrag(PeqPoint p, PeqMods mods)
    {
        if (_drag is not { } ctx) return;
        double scale = (mods & PeqMods.Shift) != 0 ? Tuning.Fine : 1;
        double mx = (p.X - ctx.Last.X) * scale, my = (p.Y - ctx.Last.Y) * scale;
        ctx.Last = p;
        static Axis Dominant(double x, double y) => Math.Abs(x) >= Math.Abs(y) ? Axis.Horizontal : Axis.Vertical;

        if ((mods & PeqMods.Alt) == 0)
        {
            ctx.Axis = null;
            ctx.Pending = null;
            ctx.OffsetX += mx;
            ctx.OffsetY += my;
        }
        else
        {
            if (ctx.Axis == null && ctx.Pending == null)
            {
                if (Math.Sqrt(ctx.OffsetX * ctx.OffsetX + ctx.OffsetY * ctx.OffsetY) >= 4)
                    ctx.Axis = Dominant(ctx.OffsetX, ctx.OffsetY);
                else
                    ctx.Pending = (0, 0);
            }
            if (ctx.Pending is { } pending)
            {
                pending = (pending.X + mx, pending.Y + my);
                if (Math.Sqrt(pending.X * pending.X + pending.Y * pending.Y) >= 4)
                {
                    var axis = Dominant(pending.X, pending.Y);
                    ctx.Axis = axis;
                    ctx.Pending = null;
                    if (axis == Axis.Horizontal) ctx.OffsetX += pending.X; else ctx.OffsetY += pending.Y;
                }
                else
                {
                    ctx.Pending = pending;
                }
            }
            else if (ctx.Axis == Axis.Horizontal)
            {
                ctx.OffsetX += mx;
            }
            else
            {
                ctx.OffsetY += my;
            }
        }
        SetLive(DragResult(ctx, ctx.OffsetX, ctx.OffsetY, scaleGains: (mods & PeqMods.Ctrl) != 0));
    }

    /// <summary>Modifiers changed without the pointer moving. Mid-drag, Ctrl
    /// switches the selection between offset and scaled gains at once.</summary>
    public void ModifiersChanged(PeqMods mods)
    {
        if (_gesture != GestureKind.Drag || _drag is not { } ctx || ctx.QMode) return;
        SetLive(DragResult(ctx, ctx.OffsetX, ctx.OffsetY, scaleGains: (mods & PeqMods.Ctrl) != 0));
    }

    /// <summary>Applies a drag offset to the bands that were grabbed: the
    /// grabbed dot follows the pointer and the others move by the same frequency
    /// ratio. Their gains move by the same number of dB, or with
    /// <paramref name="scaleGains"/> are scaled in proportion, as FabFilter
    /// always does, so cuts deepen as boosts grow.</summary>
    private Dictionary<int, FilterParams> DragResult(DragContext ctx, double dx, double dy, bool scaleGains)
    {
        var output = new Dictionary<int, FilterParams>();
        if (!ctx.Start.TryGetValue(ctx.Grabbed, out var g0)) return output;
        double qFactor = Math.Pow(2, -dy / Tuning.QPointsPerOctave);
        if (ctx.QMode)
        {
            foreach (var (b, s) in ctx.Start)
            {
                if (!s.Type.HasQ()) continue;
                var p = s.Clone();
                p.Q = (float)PeqLimits.ClampQ(s.Q * qFactor);
                output[b] = p;
            }
            return output;
        }

        double startX = _geometry.X(g0.Frequency);
        double newFreq = ClampToRange(_geometry.Freq(startX + dx));
        double ratio = newFreq / g0.Frequency;
        double dbDelta = -dy * _geometry.DbPerPoint;
        var r0 = Role(g0);
        var g = g0.Clone();
        g.Frequency = (float)newFreq;
        switch (r0.Kind)
        {
            case PeqNodeRole.RoleKind.Gain:
                g.Gain = (float)PeqLimits.ClampGain(g0.Gain + dbDelta / r0.Scale);
                break;
            case PeqNodeRole.RoleKind.Resonance:
                double db = r0.DbFor(g0) + dbDelta;
                g.Q = (float)PeqLimits.ClampQ(Math.Pow(10, db / 20));
                break;
            case PeqNodeRole.RoleKind.Fixed:
                // The dot sits at a fixed level (a notch or all-pass on 0 dB, a
                // first-order cut at its corner), so it cannot follow the pointer
                // vertically; a drag moves frequency only. Q is the wheel's.
                break;
            case PeqNodeRole.RoleKind.Locked:
                return output;
        }
        output[ctx.Grabbed] = g;

        double? gainFactor = scaleGains && r0.Kind == PeqNodeRole.RoleKind.Gain && Math.Abs(g0.Gain) >= 0.25
            ? (double)g.Gain / g0.Gain : null;
        // Offset by what the grabbed band's gain actually moved, after its clamp;
        // a grabbed band without gain passes on the pointer's travel.
        double? gainOffset = r0.Kind == PeqNodeRole.RoleKind.Gain ? g.Gain - g0.Gain : null;
        foreach (var (b, s) in ctx.Start)
        {
            if (b == ctx.Grabbed) continue;
            var role = Role(s);
            if (role.Kind == PeqNodeRole.RoleKind.Locked) continue;
            var p = s.Clone();
            p.Frequency = (float)ClampToRange(s.Frequency * ratio);
            if (s.Type.HasGain())
            {
                double gain = gainFactor is { } f ? s.Gain * f
                    : s.Gain + (gainOffset ?? dbDelta / Math.Max(role.GainScale, 0.05));
                p.Gain = (float)PeqLimits.ClampGain(gain);
            }
            output[b] = p;
        }
        return output;
    }

    private void UpdateMarquee()
    {
        var rect = new PeqRect(Math.Min(_marqueeFrom.X, _marqueeTo.X), Math.Min(_marqueeFrom.Y, _marqueeTo.Y),
                               Math.Abs(_marqueeTo.X - _marqueeFrom.X), Math.Abs(_marqueeTo.Y - _marqueeFrom.Y));
        var inside = GraphBands.Where(b => rect.Contains(NodePoint(_shown[b]))).ToList();
        SetSelection(_marqueeBase.Union(inside));
        if (FrequencyOrder(inside).FirstOrDefault(-1) is var first && first >= 0) _anchor = first;
        Invalidate();
    }

    // ── Wheel ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One wheel step over the graph, in points (a mouse notch is about 8).
    /// Returns false when no band takes it, so the view can zoom or scroll
    /// instead. A scroll gesture stays with the band it started on: Ctrl-wheel
    /// gain moves the dot away from the pointer and a cut shrinks the band's
    /// area, so looking the band up afresh on every event would lose it.
    /// An explicit selection owns the wheel: wherever the wheel would adjust a
    /// band, it adjusts the selection instead.
    /// </summary>
    public bool Wheel(PeqPoint p, double rawDelta, PeqMods mods, bool begins = false)
    {
        if (!Editing) return false;
        if (begins) _hudWheelField = null;
        double now = _clock.Now;
        int? owner = SelectedChipBand;
        int? target;
        if (_gesture == GestureKind.Drag && _drag != null)
        {
            target = _drag.Grabbed;
        }
        else if (!begins && _wheelBand is { } w && IsBand(w) && now - _wheelTime < Tuning.WheelSession
                 && (owner == null || _selection.Contains(w)))
        {
            target = w;
        }
        else
        {
            // The chip is drawn over the graph, so it wins over a band area beneath it.
            int? chip = _hudBand is { } h && HudVisible && HudFrame.Contains(p) ? h : null;
            int? under = chip ?? BandAt(p);
            target = under == null ? null : owner ?? under;
        }
        if (target is not { } b)
        {
            _wheelBand = null;
            return false;
        }
        _wheelBand = b;
        _wheelTime = now;
        _hudWheelField = null;
        bool fine = (mods & PeqMods.Shift) != 0;
        double delta = rawDelta * (fine ? Tuning.Fine : 1);
        if (delta == 0) return true;
        var bands = TargetsFor(b);

        // Each wheel step is one transformation of a band, applied to the live
        // band and, mid-drag, to the drag's starting snapshot too: every drag
        // movement rebuilds the band from that snapshot, so changing only the
        // live band would let the next movement undo the wheel.
        Func<FilterParams, FilterParams?> transform;
        if ((mods & PeqMods.Ctrl) != 0)
        {
            transform = p0 =>
            {
                if (!p0.Type.HasGain()) return null;
                var q = p0.Clone();
                q.Gain = (float)PeqLimits.ClampGain(p0.Gain + delta * 0.05);
                return q;
            };
        }
        else
        {
            // Q for every shape that has one, cuts included; slope is set from the
            // chip's shape page or the band menu.
            double factor = Math.Pow(2, delta / 100);
            transform = p0 =>
            {
                if (!p0.Type.HasQ()) return null;
                var q = p0.Clone();
                q.Q = (float)PeqLimits.ClampQ(p0.Q * factor);
                return q;
            };
        }

        var changes = new Dictionary<int, FilterParams>();
        foreach (int i in bands)
            if (i < _shown.Count && transform(Current(i)) is { } changed) changes[i] = changed;
        if (_gesture == GestureKind.Drag && _drag is { } ctx)
        {
            foreach (int i in ctx.Start.Keys.ToList())
                if (changes.ContainsKey(i) && transform(ctx.Start[i]) is { } s) ctx.Start[i] = s;
            SetLive(changes);
        }
        else
        {
            SetLiveThenCommit(changes);
        }
        if (_hovered == null) SetHovered(b);
        ShowHud(b);
        return true;
    }

    /// <summary>The selected band the chip is pinned to and the wheel adjusts:
    /// the one the chip already shows if it is selected, else the selection
    /// anchor, else the lowest.</summary>
    private int? SelectedChipBand
    {
        get
        {
            if (_hudBand is { } h && _selection.Contains(h)) return h;
            if (_anchor is { } a && _selection.Contains(a)) return a;
            var ordered = FrequencyOrder(_selection.Where(b => b < _shown.Count));
            return ordered.Count > 0 ? ordered[0] : null;
        }
    }

    public bool GraphWheelActive => _wheelBand != null && _clock.Now - _wheelTime < Tuning.WheelSession;

    private bool HudWheelActive =>
        _hudWheelField != null && _hudBand != null && _clock.Now - _hudWheelTime < Tuning.WheelSession;

    // ── Right-click ─────────────────────────────────────────────────────────

    /// <summary>A right press. Returns the menu to show there, or null when the
    /// press only closed the Ctrl-click card.</summary>
    public IReadOnlyList<PeqMenuItem>? RightPressed(PeqPoint p)
    {
        if (!Editing) return null;
        if (_cardPoint != null)
        {
            CloseCard();
            _gesture = GestureKind.Idle;
            Invalidate();
            return null;
        }
        CloseShapePage();
        if (NodeAt(p) is { } b)
        {
            if (!_selection.Contains(b)) { SetSelection(new[] { b }); _anchor = b; }
            return BandMenu(b);
        }
        return GraphMenu();
    }

    private IReadOnlyList<PeqMenuItem> BandMenu(int b)
    {
        var bands = TargetsFor(b);
        var p = Current(b);
        var menu = new List<PeqMenuItem>
        {
            new() { Title = bands.Count > 1 ? $"{bands.Count} Bands" : $"Band {b + 1}", IsHeader = true, Enabled = false },
        };
        // Shape and typed values are set on the chip; the menu does not repeat them.
        if (PeqShapes.Of(p.Type) is { } so && so.Shape.Type(1) is { } t1 && so.Shape.Type(2) is { } t2
            && _available.Contains(t1) && _available.Contains(t2))
        {
            var (shape, order) = so;
            menu.Add(new PeqMenuItem
            {
                Title = shape == PeqShape.AllPass ? "Order" : "Slope",
                Submenu = new[] { 1, 2 }.Select(o => new PeqMenuItem
                {
                    Title = shape.OrderTitle(o),
                    Checked = o == order,
                    Invoke = () => SetShape(bands, shape, o),
                }).ToList(),
            });
        }
        menu.Add(PeqMenuItem.Separator);
        if (_config.BypassSupported)
            menu.Add(new PeqMenuItem { Title = p.Bypass ? "Enable" : "Bypass", Invoke = () => ToggleBypass(bands) });
        if (p.Type.HasGain())
        {
            menu.Add(new PeqMenuItem
            {
                Title = "Invert Gain",
                Invoke = () =>
                {
                    var changes = new Dictionary<int, FilterParams>();
                    foreach (int i in bands)
                    {
                        if (!Current(i).Type.HasGain()) continue;
                        var q = Current(i).Clone();
                        q.Gain = -q.Gain;
                        changes[i] = q;
                    }
                    CommitNow(changes);
                },
            });
        }
        menu.Add(PeqMenuItem.Separator);
        menu.Add(new PeqMenuItem
        {
            Title = bands.Count > 1 ? $"Delete {bands.Count} Bands" : "Delete Band",
            Invoke = () => DeleteBands(bands),
        });
        return menu;
    }

    private IReadOnlyList<PeqMenuItem> GraphMenu()
    {
        var menu = new List<PeqMenuItem>
        {
            new() { Title = "Select All Bands", Enabled = GraphBands.Count > 0, Invoke = () => SetSelection(GraphBands) },
            new() { Title = "Deselect All", Enabled = _selection.Count > 0, Invoke = () => SetSelection(Array.Empty<int>()) },
        };
        if (_selection.Count > 0)
        {
            int n = _selection.Count;
            menu.Add(PeqMenuItem.Separator);
            menu.Add(new PeqMenuItem
            {
                Title = n > 1 ? $"Delete {n} Selected Bands" : "Delete Selected Band",
                Invoke = () => DeleteBands(_selection.ToList()),
            });
        }
        return menu;
    }

    // ── Keyboard ────────────────────────────────────────────────────────────

    /// <summary>A key press while the graph has focus. Returns true when the
    /// editor used it.</summary>
    public bool KeyDown(PeqKey key, PeqMods mods)
    {
        if (!Editing) return false;
        switch (key)
        {
            case PeqKey.Delete:
                DeleteBands(_selection.ToList());
                return true;
            case PeqKey.Escape:
                if (_cardPoint != null) { CloseCard(); Invalidate(); return true; }
                if (HudVisible && _showsShapes) { CloseShapePage(); Invalidate(); return true; }
                SetSelection(Array.Empty<int>());
                HideHud();
                Invalidate();
                return true;
            case PeqKey.Tab:
                StepSelection(backwards: (mods & PeqMods.Shift) != 0);
                return true;
            case PeqKey.Left or PeqKey.Right or PeqKey.Up or PeqKey.Down:
                Nudge(key, mods);
                return true;
            case PeqKey.A when mods == PeqMods.Ctrl:
                SetSelection(GraphBands);
                return true;
            default:
                return false;
        }
    }

    private void StepSelection(bool backwards)
    {
        var order = FrequencyOrder(GraphBands);
        if (order.Count == 0) return;
        int from = _anchor is { } a ? order.IndexOf(a) : -1;
        int next = from >= 0
            ? (((from + (backwards ? -1 : 1)) % order.Count) + order.Count) % order.Count
            : (backwards ? order.Count - 1 : 0);
        SetSelection(new[] { order[next] });
        _anchor = order[next];
        ShowHud(order[next]);
    }

    private void Nudge(PeqKey key, PeqMods mods)
    {
        bool fine = (mods & PeqMods.Shift) != 0;
        var changes = new Dictionary<int, FilterParams>();
        foreach (int b in _selection.Where(IsBand))
        {
            var p = Current(b).Clone();
            if (key is PeqKey.Left or PeqKey.Right)
            {
                double octaves = (fine ? 1.0 / 96 : 1.0 / 12) * (key == PeqKey.Right ? 1 : -1);
                p.Frequency = (float)ClampToRange(p.Frequency * Math.Pow(2, octaves));
            }
            else
            {
                bool up = key == PeqKey.Up;
                if ((mods & PeqMods.Alt) != 0 || !p.Type.HasGain())
                {
                    if (!p.Type.HasQ()) continue;
                    p.Q = (float)PeqLimits.ClampQ(p.Q * Math.Pow(2, (fine ? 0.02 : 0.1) * (up ? 1 : -1)));
                }
                else
                {
                    p.Gain = (float)PeqLimits.ClampGain(p.Gain + (fine ? 0.1 : 0.5) * (up ? 1 : -1));
                }
            }
            changes[b] = p;
        }
        SetLiveThenCommit(changes);
        if (_anchor is { } a && _selection.Contains(a)) ShowHud(a);
    }

    // ── Chip ────────────────────────────────────────────────────────────────

    /// <summary>The chip's power button.</summary>
    public void HudBypass()
    {
        if (_hudBand is { } b) ToggleBypass(TargetsFor(b));
    }

    /// <summary>The chip's shape button: turn to the shape page, or back.</summary>
    public void HudShapeButton()
    {
        if (_hudBand == null) return;
        if (_showsShapes)
        {
            _showsShapes = false;
            _pageSize = null;
        }
        else
        {
            if (_hudEditing) EndHudEditRequested?.Invoke();
            _showsShapes = true;
            _pageSize = HudFrame.Width > 0 ? new PeqSize(HudFrame.Width, HudFrame.Height) : null;
        }
        LayoutHud();
        Invalidate();
    }

    /// <summary>The shape page's back arrow on its first step.</summary>
    public void HudShapePageBack()
    {
        CloseShapePage();
        Invalidate();
    }

    /// <summary>A shape and order picked on the chip's shape page.</summary>
    public void HudPick(PeqShape shape, int order)
    {
        if (_hudBand is not { } b) return;
        SetShape(TargetsFor(b), shape, order);
        CloseShapePage();
        Invalidate();
    }

    public void SetHudEditing(bool editing)
    {
        if (_hudEditing == editing) return;
        _hudEditing = editing;
        if (!editing) ScheduleHudHide();
    }

    /// <summary>A scroll gesture on a chip field keeps that field, even when
    /// adjusting it moves the dot, the chip follows, and another row (or the
    /// graph) ends up under the pointer.</summary>
    public void HudScroll(PeqHudField field, double delta, bool fine)
    {
        if (_hudBand is not { } b) return;
        double now = _clock.Now;
        var locked = HudWheelActive ? (_hudWheelField ?? field) : field;
        _hudWheelField = locked;
        _hudWheelTime = now;
        _wheelBand = null;
        double d = delta * (fine ? Tuning.Fine : 1);
        if (Adjusted(Current(b), locked, d * 0.5) is { } p)
            SetLiveThenCommit(new Dictionary<int, FilterParams> { [b] = p });
    }

    /// <summary>The field a running scroll on the chip is locked to, if any.</summary>
    public PeqHudField? HudWheelField => HudWheelActive ? _hudWheelField : null;

    private FilterParams? Adjusted(FilterParams p, PeqHudField field, double delta)
    {
        var q = p.Clone();
        switch (field)
        {
            case PeqHudField.Freq:
                q.Frequency = (float)ClampToRange(p.Frequency * Math.Pow(2, delta / 100));
                break;
            case PeqHudField.Gain:
                if (!p.Type.HasGain()) return null;
                q.Gain = (float)PeqLimits.ClampGain(p.Gain + delta * 0.1);
                break;
            case PeqHudField.Q:
                if (!p.Type.HasQ()) return null;
                q.Q = (float)PeqLimits.ClampQ(p.Q * Math.Pow(2, delta / 80));
                break;
        }
        return q;
    }

    /// <summary>A vertical drag on a chip value: points moved upward since the
    /// press, Shift-scaled by the chip.</summary>
    public void HudAdjust(PeqHudField field, double delta, PeqAdjustPhase phase)
    {
        if (_hudBand is not { } b) return;
        switch (phase)
        {
            case PeqAdjustPhase.Began:
                _commitTimer?.Cancel();
                _commitTimer = null;
                _hudAdjustStart = Current(b).Clone();
                break;
            case PeqAdjustPhase.Changed:
                if (_hudAdjustStart is { } start && Adjusted(start, field, delta) is { } p)
                    SetLive(new Dictionary<int, FilterParams> { [b] = p });
                break;
            case PeqAdjustPhase.Ended:
                _hudAdjustStart = null;
                CommitLive();
                Invalidate();
                break;
        }
    }

    /// <summary>Typed text for a chip field. False when it did not parse, which
    /// keeps the old value (and beeps).</summary>
    public bool HudText(PeqHudField field, string text)
    {
        if (_hudBand is not { } b) return false;
        var p = Current(b).Clone();
        switch (field)
        {
            case PeqHudField.Freq:
                if (PeqValueText.ParseFrequency(text) is not { } f) return Fail();
                p.Frequency = (float)PeqLimits.ClampFreq(f);
                break;
            case PeqHudField.Gain:
                if (!p.Type.HasGain() || PeqValueText.ParseNumber(text, "db") is not { } g) return Fail();
                p.Gain = (float)PeqLimits.ClampGain(g);
                break;
            case PeqHudField.Q:
                if (!p.Type.HasQ() || PeqValueText.ParseNumber(text, "") is not { } q) return Fail();
                p.Q = (float)PeqLimits.ClampQ(q);
                break;
        }
        if (!p.SameAs(Current(b))) CommitNow(new Dictionary<int, FilterParams> { [b] = p });
        return true;

        bool Fail()
        {
            Beep?.Invoke();
            return false;
        }
    }

    private void ShowHud(int b)
    {
        if (!IsBand(b)) return;
        CancelHudHide();
        if (_hudBand != b) CloseShapePage();
        _hudBand = b;
        _hudVisible = true;
        RefreshHud();
        Invalidate();
    }

    private void RefreshHud()
    {
        if (_hudBand is not { } b) return;
        if (!IsBand(b)) { HideHud(); return; }
        LayoutHud();
    }

    private void LayoutHud()
    {
        if (_hudBand is not { } b || !IsBand(b)) return;
        var p = Current(b);
        var size = _showsShapes && _pageSize is { } ps ? ps : HudSizeFor(p);
        // The shape page stays where it opened: a pick moves the band's dot, and
        // a chip that followed it would leave the pointer behind and hide.
        if (_showsShapes && _pagedOrigin is { } o)
        {
            HudFrame = new PeqRect(o.X, o.Y, size.Width, size.Height);
            return;
        }
        _pagedOrigin = null;
        var frame = PanelFrame(NodePoint(p), size, boost: Role(p).DbFor(p) >= 0);
        HudFrame = frame;
        if (_showsShapes) _pagedOrigin = new PeqPoint(frame.X, frame.Y);
    }

    /// <summary>Where a floating panel of <paramref name="size"/> sits beside
    /// <paramref name="point"/>. Away from 0 dB first, as FabFilter does, so a
    /// chip never sits on its band's own lobe; then beside the point; the lobe
    /// side last. The Ctrl-click card shares the rule so a new band's chip opens
    /// on the side the card was on.</summary>
    public PeqRect PanelFrame(PeqPoint point, PeqSize size, bool boost)
    {
        const double gap = 16;
        var area = new PeqRect(0, 0, _geometry.Width, _geometry.Height).Inset(4, 4);
        double ClampedX(double x) => Math.Min(Math.Max(x, area.MinX), Math.Max(area.MaxX - size.Width, area.MinX));
        double ClampedY(double y) => Math.Min(Math.Max(y, area.MinY), Math.Max(area.MaxY - size.Height, area.MinY));
        var above = new PeqRect(ClampedX(point.X - size.Width / 2), point.Y - gap - size.Height, size.Width, size.Height);
        var below = new PeqRect(ClampedX(point.X - size.Width / 2), point.Y + gap, size.Width, size.Height);
        var right = new PeqRect(point.X + gap, ClampedY(point.Y - size.Height / 2), size.Width, size.Height);
        var left = new PeqRect(point.X - gap - size.Width, ClampedY(point.Y - size.Height / 2), size.Width, size.Height);
        var candidates = boost ? new[] { above, right, left, below } : new[] { below, right, left, above };
        var frame = candidates.FirstOrDefault(area.Contains, candidates[0]);
        return frame with { X = ClampedX(frame.X), Y = ClampedY(frame.Y) };
    }

    /// <summary>While bands are selected the chip stays up on the selection,
    /// wherever the pointer goes; with none selected it follows the hovered dot.</summary>
    private void PinHudToSelection()
    {
        if (SelectedChipBand is { } pin)
        {
            if (_gesture == GestureKind.Marquee) return;
            if (!HudVisible || _hudBand != pin) ShowHud(pin);
        }
        else if (_hudBand is { } h && (_pointer is not { } ptr || NodeAt(ptr) != h))
        {
            ScheduleHudHide();
        }
    }

    private void ScheduleHudHide()
    {
        if (!HudVisible || _hudEditing) return;
        if (_hudBand is { } h && _selection.Contains(h)) return;
        if (_gesture == GestureKind.Drag) return;
        CancelHudHide();
        _hudHideTimer = _clock.Schedule(Tuning.HudHideDelay, () =>
        {
            _hudHideTimer = null;
            HideHud();
            Invalidate();
        });
    }

    private void CancelHudHide()
    {
        _hudHideTimer?.Cancel();
        _hudHideTimer = null;
    }

    private void HideHud()
    {
        CancelHudHide();
        CloseShapePage();
        if (_hudEditing) EndHudEditRequested?.Invoke();
        _hudBand = null;
        _hudVisible = false;
    }

    private void CloseShapePage()
    {
        if (!_showsShapes) return;
        _showsShapes = false;
        _pageSize = null;
        LayoutHud();
    }

    // ── Ctrl-click card ─────────────────────────────────────────────────────

    /// <summary>A new band of <paramref name="shape"/> at <paramref name="order"/>
    /// placed at a point: the pointer's frequency, and its level as the gain for
    /// shapes that have one.</summary>
    private FilterParams? BandAt(PeqShape shape, int order, PeqPoint p)
    {
        if (shape.Type(order) is not { } type) return null;
        var band = new FilterParams(type, (float)ClampToRange(_geometry.Freq(p.X)),
                                    shape is PeqShape.Bell or PeqShape.Notch ? 1f : 0.707f, 0f);
        if (type.HasGain()) band.Gain = (float)PeqLimits.ClampGain(BandGeometry.Db(p.Y));
        return band;
    }

    /// <summary>Opens the shape card for a band at <paramref name="p"/>, placed
    /// as the band's chip will be.</summary>
    private void OpenCard(PeqPoint p)
    {
        if (FreeSlot == null)
        {
            // Every band is in use: nothing to create, and the press is not a
            // click on the graph either.
            Beep?.Invoke();
            return;
        }
        CloseShapePage();
        // Boost or cut by the click's level, which is where a bell's dot lands.
        var frame = PanelFrame(p, Tuning.CardSize, boost: BandGeometry.Db(p.Y) >= 0);
        _cardOrigin = new PeqPoint(frame.X, frame.Y);
        _cardPoint = p;
        UpdateHover(p);
        Invalidate();
    }

    /// <summary>A shape and order picked on the Ctrl-click card: the band is
    /// created where Ctrl was clicked, selected, and its chip takes over.</summary>
    public void CardPick(PeqShape shape, int order)
    {
        if (_cardPoint is not { } at) return;
        var band = BandAt(shape, order, at);
        CloseCard();
        if (band != null && CreateBand(band) is { } slot) ShowHud(slot);
        Invalidate();
    }

    /// <summary>The card's back arrow on its first step: cancel.</summary>
    public void CardCancel()
    {
        CloseCard();
        Invalidate();
    }

    private void CloseCard()
    {
        if (_cardPoint == null) return;
        _cardPoint = null;
        _ghost = null;
    }

    // ── Labels ──────────────────────────────────────────────────────────────

    private void ShowAxisLabel(string text, double x)
    {
        _axisLabel = text;
        _axisLabelX = x;
    }

    private void ShowGainLabel(string text, double y)
    {
        _gainLabel = text;
        _gainLabelY = y;
    }

    private void HideAxisLabels()
    {
        _axisLabel = null;
        _gainLabel = null;
    }

    // ── Test hooks ──────────────────────────────────────────────────────────

    /// <summary>Where band <paramref name="b"/>'s dot is drawn now.</summary>
    public PeqPoint NodePointForTesting(int b) => NodePoint(_shown[b]);

    /// <summary>The band as drawn now (live, animating or committed).</summary>
    public FilterParams ShownForTesting(int b) => _shown[b];

    /// <summary>Settle every animation at once.</summary>
    public void SettleForTesting()
    {
        UpdateEmphasisTargets();
        Frame(double.PositiveInfinity);
    }
}
