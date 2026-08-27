using DSPiConsole.Core;
using DSPiConsole.Core.Models;
using DSPiConsole.Models;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// On-graph filter editing: draggable PEQ/crossover band handles rendered on a
/// dedicated overlay canvas (FabFilter-style). Active only while a channel is
/// selected and Settings → Graphing → Style → "Edit filters on the graph" is on.
///
/// Interaction summary:
///   drag          frequency + gain (gain types) / frequency only (others)
///   Shift+drag    fine adjustment
///   Ctrl+drag     lock to the dominant axis
///   wheel         Q (PEQ) / slope order (crossover); Shift = fine
///   Alt+click     toggle band bypass
///   double-click  empty area: add a band · handle: open the band flyout
///   right-click   open the band flyout
///   Delete        remove the selected band · Esc: deselect
///
/// Input is handled on _rootGrid (the overlay canvas is hit-test-invisible) so
/// the dB-scale zoom strip and legend keep their own pointer behavior.
/// </summary>
public sealed partial class BodePlotControl
{
    private Canvas? _editCanvas;

    private readonly record struct EditBandRef(bool IsXover, int Index);

    private EditBandRef? _editHover;
    private EditBandRef? _editSelected;

    private bool _editDragActive;
    private bool _editDragMoved;
    private Pointer? _editDragPointer;
    private Point _editLastPointerPos;
    private double _editAccumX, _editAccumY;
    private float _editStartFreq, _editStartGain;
    private int _editAxisLock;               // 0 = free, 1 = freq only, 2 = gain only
    private InputSystemCursor? _editCursorMove, _editCursorHoriz;

    private const double EditHandleRadius = 8.0;
    private const double EditHandleHitRadius = 12.0;
    private const float EditPeqMinFreq = 20f, EditXoMinFreq = 10f, EditMaxFreq = 20000f;
    private const float EditMinGain = -20f, EditMaxGain = 20f;
    private const float EditMinQ = 0.1f, EditMaxQ = 20f;

    /// <summary>Raised when the user selects (or creates) a band on the graph;
    /// arg is true for a crossover band. The main window uses it to flip the
    /// PEQ|XO filter page so the row list matches what is being edited.</summary>
    public event Action<bool>? EditBandSelected;

    private bool EditingActive =>
        AppSettings.Instance.GraphEditingEnabled && _viewModel != null && _selectedChannelId >= 0;

    private void InitializeEditing()
    {
        _editCanvas = new Canvas { IsHitTestVisible = false };
        _rootGrid!.Children.Add(_editCanvas);

        // Focusable so Delete/Esc reach OnEditKeyDown after a handle click, but
        // without the system focus rectangle flashing around the whole plot.
        IsTabStop = true;
        UseSystemFocusVisuals = false;

        _rootGrid.PointerPressed += OnEditPointerPressed;
        _rootGrid.PointerMoved += OnEditPointerMoved;
        _rootGrid.PointerReleased += OnEditPointerReleased;
        _rootGrid.PointerCaptureLost += OnEditPointerCaptureLost;
        _rootGrid.PointerExited += OnEditPointerExited;
        _rootGrid.PointerWheelChanged += OnEditPointerWheelChanged;
        _rootGrid.DoubleTapped += OnEditDoubleTapped;
        _rootGrid.RightTapped += OnEditRightTapped;
        KeyDown += OnEditKeyDown;
    }

    /// <summary>Drop hover/selection and any in-flight drag; called on channel
    /// switches and unload so stale band refs never outlive their channel.</summary>
    private void ResetEditState()
    {
        if (_editDragActive)
        {
            _editDragActive = false;
            _viewModel?.EndInteractiveFilterEdit();
            if (_editDragPointer != null)
            {
                try { _rootGrid?.ReleasePointerCapture(_editDragPointer); } catch { }
                _editDragPointer = null;
            }
        }
        _editHover = null;
        _editSelected = null;
        ProtectedCursor = null;
    }

    // ── Geometry helpers ──

    private bool TryGetPlotDims(out double plotWidth, out double plotHeight)
    {
        plotWidth = ActualWidth - LeftMargin - RightMargin;
        plotHeight = ActualHeight - TopMargin - BottomMargin;
        return plotWidth > 0 && plotHeight > 0;
    }

    private bool EditIsInsidePlot(Point pt) =>
        TryGetPlotDims(out var pw, out var ph) &&
        pt.X >= LeftMargin && pt.X <= LeftMargin + pw &&
        pt.Y >= TopMargin && pt.Y <= TopMargin + ph;

    private float EditFreqAtX(double x, double plotWidth)
    {
        double logMin = Math.Log10(MinFreq), logMax = Math.Log10(MaxFreq);
        double t = (x - LeftMargin) / plotWidth;
        return (float)Math.Pow(10, logMin + t * (logMax - logMin));
    }

    private float EditDbAtY(double y, double plotHeight) =>
        DbBottom + (float)((TopMargin + plotHeight - y) / plotHeight) * DbSpan;

    /// <summary>The gain/preamp offset folded into the drawn curves when
    /// "Gain affects displayed level" is on — handles sit on the same baseline
    /// the curve uses so they visually ride the curve.</summary>
    private float EditLevelOffset(Channel channel)
    {
        if (_viewModel == null || !AppSettings.Instance.GraphLevelIncludesGain) return 0f;
        if (channel.IsOutput) return _viewModel.GetChannelGain(channel);
        int id = (int)channel.Id;
        int wireInput = ChannelMap.IsExtraInput(id)
            ? ChannelMap.AppInputCount + (id - ChannelMap.ExtraInputFirstId)
            : id;
        return _viewModel.InputPreampAt(wireInput);
    }

    private Point EditHandlePos(FilterParams p, bool isXover, double plotWidth, double plotHeight, float levelOffset)
    {
        double x = XPos(p.Frequency, plotWidth);
        double y = !isXover && p.Type.HasGain()
            ? YPos(p.Gain + levelOffset, plotHeight)
            : YPos(levelOffset, plotHeight);
        // Keep the handle grabbable when zoom pushes its dB position off-plot.
        y = Math.Clamp(y, TopMargin, TopMargin + plotHeight);
        return new Point(x, y);
    }

    // ── Band model access ──

    private Channel? EditChannel() =>
        _selectedChannelId >= 0 ? Channel.FromId((ChannelId)_selectedChannelId) : null;

    private FilterParams? EditGetParams(EditBandRef band)
    {
        var channel = EditChannel();
        if (channel == null || _viewModel == null) return null;
        if (band.IsXover)
        {
            if (!channel.IsOutput || !_viewModel.CrossoverSupported) return null;
            var xbands = _viewModel.GetXoverFilters(channel);
            return band.Index >= 0 && band.Index < xbands.Count ? xbands[band.Index] : null;
        }
        var filters = _viewModel.GetFilters(channel);
        return band.Index >= 0 && band.Index < filters.Count ? filters[band.Index] : null;
    }

    private bool EditRefValid(EditBandRef band)
    {
        var p = EditGetParams(band);
        if (p == null) return false;
        return band.IsXover ? CrossoverFilter.TryGetMeta(p.Type, out _) : p.Type != FilterType.Flat;
    }

    /// <summary>All bands of the selected channel that exist on the graph:
    /// non-Flat PEQ bands, plus configured crossover bands on outputs.</summary>
    private IEnumerable<(EditBandRef Band, FilterParams P)> EditBands(Channel channel)
    {
        if (_viewModel == null) yield break;
        var filters = _viewModel.GetFilters(channel);
        for (int i = 0; i < filters.Count; i++)
            if (filters[i].Type != FilterType.Flat)
                yield return (new EditBandRef(false, i), filters[i]);

        if (channel.IsOutput && _viewModel.CrossoverSupported)
        {
            var xbands = _viewModel.GetXoverFilters(channel);
            for (int i = 0; i < xbands.Count; i++)
                if (CrossoverFilter.TryGetMeta(xbands[i].Type, out _))
                    yield return (new EditBandRef(true, i), xbands[i]);
        }
    }

    private EditBandRef? EditFindBandAt(Point pt)
    {
        if (!EditingActive || !TryGetPlotDims(out var pw, out var ph)) return null;
        var channel = EditChannel();
        if (channel == null) return null;
        float offset = EditLevelOffset(channel);

        EditBandRef? best = null;
        double bestDist = double.MaxValue;
        foreach (var (bref, p) in EditBands(channel))
        {
            var pos = EditHandlePos(p, bref.IsXover, pw, ph, offset);
            double dx = pt.X - pos.X, dy = pt.Y - pos.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (bref == _editSelected) d -= 2;   // selection is slightly sticky
            if (d <= EditHandleHitRadius && d < bestDist) { best = bref; bestDist = d; }
        }
        return best;
    }

    // ── Pointer interaction ──

    private void OnEditPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!EditingActive) return;
        var pt = e.GetCurrentPoint(_rootGrid);
        if (!pt.Properties.IsLeftButtonPressed) return;

        var band = EditFindBandAt(pt.Position);
        if (band == null)
        {
            if (_editSelected != null && EditIsInsidePlot(pt.Position))
            {
                _editSelected = null;
                RefreshEditOverlay();
            }
            return;
        }

        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu))
        {
            EditToggleBypass(band.Value);
            e.Handled = true;
            return;
        }

        var p = EditGetParams(band.Value);
        if (p == null) return;

        _editSelected = band;
        Focus(FocusState.Programmatic);
        EditBandSelected?.Invoke(band.Value.IsXover);

        // A Linkwitz Transform band's wire fields aren't (freq, dB, Q) — its
        // handle is selectable (flyout, bypass, delete) but not draggable.
        if (!band.Value.IsXover && p.Type.IsLinkwitzTransform())
        {
            RefreshEditOverlay();
            e.Handled = true;
            return;
        }

        _editDragActive = true;
        _editDragMoved = false;
        _editDragPointer = e.Pointer;
        _editLastPointerPos = pt.Position;
        _editAccumX = 0; _editAccumY = 0;
        _editAxisLock = 0;
        _editStartFreq = p.Frequency;
        _editStartGain = p.Gain;
        _rootGrid!.CapturePointer(e.Pointer);
        _viewModel!.BeginInteractiveFilterEdit();
        RefreshEditOverlay();
        e.Handled = true;
    }

    private void OnEditPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!EditingActive) return;
        var pos = e.GetCurrentPoint(_rootGrid).Position;

        if (_editDragActive)
        {
            if (_editSelected == null || !TryGetPlotDims(out var pw, out var ph)) return;
            var cur = EditGetParams(_editSelected.Value);
            if (cur == null) return;

            // Accumulate pointer deltas with the fine factor applied per-segment,
            // so pressing Shift mid-drag slows the handle from that point on
            // instead of jumping.
            double fine = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift) ? 0.2 : 1.0;
            _editAccumX += (pos.X - _editLastPointerPos.X) * fine;
            _editAccumY += (pos.Y - _editLastPointerPos.Y) * fine;
            _editLastPointerPos = pos;

            if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
                _editAxisLock = 0;
            else if (_editAxisLock == 0 && (Math.Abs(_editAccumX) > 3 || Math.Abs(_editAccumY) > 3))
                _editAxisLock = Math.Abs(_editAccumX) >= Math.Abs(_editAccumY) ? 1 : 2;

            double px = _editAxisLock == 2 ? 0 : _editAccumX;
            double py = _editAxisLock == 1 ? 0 : _editAccumY;

            var band = _editSelected.Value;
            var p = cur.Clone();
            double logSpan = Math.Log10(MaxFreq) - Math.Log10(MinFreq);
            float minFreq = band.IsXover ? EditXoMinFreq : EditPeqMinFreq;
            p.Frequency = (float)Math.Clamp(
                _editStartFreq * Math.Pow(10, px / pw * logSpan), minFreq, EditMaxFreq);
            if (!band.IsXover && p.Type.HasGain())
                p.Gain = Math.Clamp(_editStartGain - (float)(py * DbSpan / ph), EditMinGain, EditMaxGain);

            _editDragMoved = true;
            if (band.IsXover)
                _viewModel!.SetXoverFilterLive(_selectedChannelId, band.Index, p);
            else
                _viewModel!.SetFilterLive(_selectedChannelId, band.Index, p);
            // Overlay + curve refresh arrives via FiltersChanged → snap redraw.
            e.Handled = true;
            return;
        }

        var hover = EditFindBandAt(pos);
        if (hover != _editHover)
        {
            _editHover = hover;
            RefreshEditOverlay();
        }
        EditUpdateCursor(hover);
    }

    private void OnEditPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_editDragActive) return;
        EndEditDrag(commit: true);
        e.Handled = true;
    }

    private void OnEditPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_editDragActive) EndEditDrag(commit: true);
    }

    private void OnEditPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_editDragActive) return;
        if (_editHover != null)
        {
            _editHover = null;
            RefreshEditOverlay();
        }
        ProtectedCursor = null;
    }

    private void EndEditDrag(bool commit)
    {
        _editDragActive = false;
        if (_editDragPointer != null)
        {
            try { _rootGrid?.ReleasePointerCapture(_editDragPointer); } catch { }
            _editDragPointer = null;
        }
        _viewModel?.EndInteractiveFilterEdit();

        // Authoritative final write (SetFilter cancels any queued live send), and
        // its FiltersChanged rebuilds the row list now that the edit session ended.
        if (commit && _editDragMoved && _editSelected != null && _viewModel != null)
        {
            var band = _editSelected.Value;
            var p = EditGetParams(band)?.Clone();
            if (p != null)
            {
                if (band.IsXover) _ = _viewModel.SetXoverFilter(_selectedChannelId, band.Index, p);
                else _ = _viewModel.SetFilter(_selectedChannelId, band.Index, p);
            }
        }
        _editDragMoved = false;
        RefreshEditOverlay();
    }

    private void OnEditPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!EditingActive) return;
        var pt = e.GetCurrentPoint(_rootGrid);
        var band = _editDragActive ? _editSelected : (_editHover ?? EditFindBandAt(pt.Position));
        if (band == null) return;
        var cur = EditGetParams(band.Value);
        if (cur == null) return;

        int dir = pt.Properties.MouseWheelDelta > 0 ? 1 : -1;
        bool fine = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        bool bracket = !_editDragActive;   // drags already hold an edit session
        if (bracket) _viewModel!.BeginInteractiveFilterEdit();
        try
        {
            if (band.Value.IsXover)
            {
                // Wheel steps the crossover slope through the family's orders.
                if (!CrossoverFilter.TryGetMeta(cur.Type, out var meta)) return;
                var orders = CrossoverFilter.OrdersFor(meta.Family);
                int idx = 0;
                for (int i = 0; i < orders.Count; i++) if (orders[i] == meta.Order) { idx = i; break; }
                int nidx = Math.Clamp(idx + dir, 0, orders.Count - 1);
                if (nidx == idx) return;
                var nt = CrossoverFilter.Compose(meta.Family, meta.IsHighPass, orders[nidx]);
                if (nt == null || nt == FilterType.Flat) return;
                var p = cur.Clone();
                p.Type = nt.Value;
                _viewModel!.SetXoverFilterLive(_selectedChannelId, band.Value.Index, p);
            }
            else
            {
                if (!cur.Type.HasQ()) return;
                var p = cur.Clone();
                p.Q = (float)Math.Clamp(p.Q * Math.Pow(10, dir * (fine ? 0.01 : 0.04)), EditMinQ, EditMaxQ);
                _viewModel!.SetFilterLive(_selectedChannelId, band.Value.Index, p);
            }
            _editHover ??= band;
        }
        finally
        {
            if (bracket) _viewModel!.EndInteractiveFilterEdit();
            e.Handled = true;
        }
    }

    private void OnEditDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!EditingActive) return;
        var pos = e.GetPosition(_rootGrid);
        var band = EditFindBandAt(pos);
        if (band != null)
        {
            // The second press of the double-tap re-entered a drag; drop it (no
            // motion happened) before opening the flyout.
            if (_editDragActive) EndEditDrag(commit: false);
            _editSelected = band;
            EditShowFlyout(band.Value, pos);
            e.Handled = true;
            return;
        }
        if (!EditIsInsidePlot(pos)) return;
        EditCreateBandAt(pos);
        e.Handled = true;
    }

    private void OnEditRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!EditingActive || _editDragActive) return;
        var pos = e.GetPosition(_rootGrid);
        var band = EditFindBandAt(pos);
        if (band == null) return;
        _editSelected = band;
        RefreshEditOverlay();
        EditShowFlyout(band.Value, pos);
        e.Handled = true;
    }

    private void OnEditKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!EditingActive || _editSelected == null || _editDragActive) return;
        if (e.Key is VirtualKey.Delete or VirtualKey.Back)
        {
            EditRemoveBand(_editSelected.Value);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            _editSelected = null;
            RefreshEditOverlay();
            e.Handled = true;
        }
    }

    // ── Band operations ──

    private void EditCreateBandAt(Point pos)
    {
        var channel = EditChannel();
        if (channel == null || _viewModel == null || !TryGetPlotDims(out var pw, out var ph)) return;

        var filters = _viewModel.GetFilters(channel);
        int slot = -1;
        for (int i = 0; i < filters.Count; i++)
            if (filters[i].Type == FilterType.Flat) { slot = i; break; }
        if (slot < 0) return;   // every band in use

        float offset = EditLevelOffset(channel);
        float freq = Math.Clamp((float)Math.Round(EditFreqAtX(pos.X, pw)), EditPeqMinFreq, EditMaxFreq);
        float gain = Math.Clamp((float)Math.Round((EditDbAtY(pos.Y, ph) - offset) * 10f) / 10f,
                                EditMinGain, EditMaxGain);

        _editSelected = new EditBandRef(false, slot);
        EditBandSelected?.Invoke(false);
        _ = _viewModel.SetFilter(_selectedChannelId, slot,
            new FilterParams(FilterType.Peaking, freq, FilterParams.DefaultQ, gain));
    }

    private void EditRemoveBand(EditBandRef band)
    {
        if (_viewModel == null) return;
        var cur = EditGetParams(band);
        if (cur == null) return;
        var p = new FilterParams(FilterType.Flat, cur.Frequency, FilterParams.DefaultQ, 0f);
        if (band.IsXover) _ = _viewModel.SetXoverFilter(_selectedChannelId, band.Index, p);
        else _ = _viewModel.SetFilter(_selectedChannelId, band.Index, p);
        if (_editSelected == band) _editSelected = null;
        if (_editHover == band) _editHover = null;
    }

    private void EditToggleBypass(EditBandRef band)
    {
        if (_viewModel == null || !_viewModel.BandBypassSupported) return;
        var p = EditGetParams(band);
        if (p == null) return;
        if (band.IsXover) _ = _viewModel.SetXoverBandBypass(_selectedChannelId, band.Index, !p.Bypass);
        else _ = _viewModel.SetBandBypass(_selectedChannelId, band.Index, !p.Bypass);
    }

    // ── Cursor ──

    private void EditUpdateCursor(EditBandRef? hover)
    {
        if (hover == null)
        {
            ProtectedCursor = null;
            return;
        }
        var p = EditGetParams(hover.Value);
        bool twoAxis = p != null && !hover.Value.IsXover && p.Type.HasGain();
        ProtectedCursor = twoAxis
            ? (_editCursorMove ??= InputSystemCursor.Create(InputSystemCursorShape.SizeAll))
            : (_editCursorHoriz ??= InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
    }

    // ── Overlay rendering ──

    private void RefreshEditOverlay()
    {
        if (TryGetPlotDims(out var pw, out var ph))
            UpdateEditOverlay(pw, ph);
    }

    private void UpdateEditOverlay(double plotWidth, double plotHeight)
    {
        if (_editCanvas == null) return;
        _editCanvas.Children.Clear();
        if (!EditingActive) return;

        var channel = EditChannel();
        if (channel == null || _viewModel == null || !IsChannelVisible(channel)) return;

        // Bands can vanish underneath us (preset load, remove, type → Off).
        if (_editSelected != null && !EditRefValid(_editSelected.Value)) _editSelected = null;
        if (_editHover != null && !EditRefValid(_editHover.Value)) _editHover = null;

        float offset = EditLevelOffset(channel);
        var color = channel.Color;

        var focusRef = _editSelected ?? _editHover;
        if (focusRef != null && EditGetParams(focusRef.Value) is { } fp)
            DrawSoloBandCurve(fp, color, plotWidth, plotHeight, offset);

        bool any = false;
        foreach (var (bref, p) in EditBands(channel))
        {
            any = true;
            var pos = EditHandlePos(p, bref.IsXover, plotWidth, plotHeight, offset);
            if (pos.X < LeftMargin - 4 || pos.X > LeftMargin + plotWidth + 4) continue;
            DrawBandHandle(bref, p, pos, color);
        }

        var chipRef = _editDragActive ? _editSelected : _editHover;
        if (chipRef != null && EditGetParams(chipRef.Value) is { } cp)
        {
            var pos = EditHandlePos(cp, chipRef.Value.IsXover, plotWidth, plotHeight, offset);
            DrawReadoutChip(chipRef.Value, cp, pos, plotWidth, plotHeight);
        }

        if (!any)
        {
            var hint = new TextBlock
            {
                Text = "Double-click to add a band",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255))
            };
            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(hint, LeftMargin + (plotWidth - hint.DesiredSize.Width) / 2);
            Canvas.SetTop(hint, TopMargin + plotHeight - hint.DesiredSize.Height - 10);
            _editCanvas.Children.Add(hint);
        }
    }

    /// <summary>The focused band's isolated response: a soft fill down to the
    /// baseline plus a thin stroke (dashed when the band is bypassed).</summary>
    private void DrawSoloBandCurve(FilterParams p, Color color, double plotWidth, double plotHeight, float offset)
    {
        var solo = p.Clone();
        solo.Bypass = false;      // show what a bypassed band would contribute
        solo.IsActive = true;
        var (_, mags) = DspMath.GenerateResponseCurve(new[] { solo });

        var curve = new PointCollection();
        for (int i = 0; i < mags.Length; i++)
        {
            float freq = DataFreqAt(i);
            curve.Add(new Point(XPos(freq, plotWidth),
                Math.Clamp(YPos(mags[i] + offset, plotHeight), TopMargin - 2, TopMargin + plotHeight + 2)));
        }

        if (!p.Bypass)
        {
            double baseY = Math.Clamp(YPos(offset, plotHeight), TopMargin - 2, TopMargin + plotHeight + 2);
            var fillPts = new PointCollection();
            foreach (var pt in curve) fillPts.Add(pt);
            fillPts.Add(new Point(curve[curve.Count - 1].X, baseY));
            fillPts.Add(new Point(curve[0].X, baseY));
            _editCanvas!.Children.Add(new Polygon
            {
                Points = fillPts,
                Fill = new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B))
            });
        }

        _editCanvas!.Children.Add(new Polyline
        {
            Points = curve,
            Stroke = new SolidColorBrush(Color.FromArgb((byte)(p.Bypass ? 90 : 140), color.R, color.G, color.B)),
            StrokeThickness = 1.25,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeDashArray = p.Bypass ? new DoubleCollection { 3, 3 } : null
        });
    }

    private void DrawBandHandle(EditBandRef bref, FilterParams p, Point pos, Color color)
    {
        bool isSel = bref == _editSelected;
        bool isHover = bref == _editHover;
        bool hot = isSel || isHover;
        double r = EditHandleRadius * (hot ? 1.18 : 1.0);

        if (isSel)
        {
            double hr = r + 5;
            var haloColor = Color.FromArgb(110, color.R, color.G, color.B);
            if (bref.IsXover)
            {
                _editCanvas!.Children.Add(EditDiamond(pos, hr, null, haloColor, 1.5));
            }
            else
            {
                var halo = new Ellipse
                {
                    Width = hr * 2, Height = hr * 2,
                    Stroke = new SolidColorBrush(haloColor),
                    StrokeThickness = 1.5
                };
                Canvas.SetLeft(halo, pos.X - hr);
                Canvas.SetTop(halo, pos.Y - hr);
                _editCanvas!.Children.Add(halo);
            }
        }

        var fillColor = p.Bypass
            ? Colors.Transparent
            : Color.FromArgb(235, color.R, color.G, color.B);
        var strokeColor = p.Bypass
            ? Color.FromArgb(210, color.R, color.G, color.B)
            : Color.FromArgb((byte)(hot ? 230 : 140), 255, 255, 255);
        double strokeTh = p.Bypass ? 1.8 : 1.4;

        if (bref.IsXover)
        {
            _editCanvas!.Children.Add(EditDiamond(pos, r, fillColor, strokeColor, strokeTh));
        }
        else
        {
            var dot = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Fill = new SolidColorBrush(fillColor),
                Stroke = new SolidColorBrush(strokeColor),
                StrokeThickness = strokeTh
            };
            Canvas.SetLeft(dot, pos.X - r);
            Canvas.SetTop(dot, pos.Y - r);
            _editCanvas!.Children.Add(dot);
        }

        // Band number: white or near-black for contrast on the fill; the channel
        // color itself on hollow (bypassed) handles.
        bool lightFill = !p.Bypass && (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) > 160;
        var num = new TextBlock
        {
            Text = (bref.Index + 1).ToString(),
            FontSize = 9,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(p.Bypass
                ? Color.FromArgb(220, color.R, color.G, color.B)
                : lightFill ? Color.FromArgb(230, 15, 15, 18) : Colors.White)
        };
        num.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(num, pos.X - num.DesiredSize.Width / 2);
        Canvas.SetTop(num, pos.Y - num.DesiredSize.Height / 2);
        _editCanvas!.Children.Add(num);
    }

    private static Polygon EditDiamond(Point center, double r, Color? fill, Color stroke, double strokeThickness)
    {
        var poly = new Polygon
        {
            Points = new PointCollection
            {
                new Point(center.X, center.Y - r),
                new Point(center.X + r, center.Y),
                new Point(center.X, center.Y + r),
                new Point(center.X - r, center.Y),
            },
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = strokeThickness,
            StrokeLineJoin = PenLineJoin.Round
        };
        if (fill.HasValue) poly.Fill = new SolidColorBrush(fill.Value);
        return poly;
    }

    private void DrawReadoutChip(EditBandRef band, FilterParams p, Point pos, double plotWidth, double plotHeight)
    {
        string text;
        if (band.IsXover)
        {
            text = $"{CrossoverFilter.Describe(p.Type)} · {EditFreqText(p.Frequency)}";
        }
        else if (p.Type.IsLinkwitzTransform())
        {
            text = $"LT · f0 {p.Frequency:0} Hz → fp {p.Gain:0} Hz";
        }
        else
        {
            text = EditFreqText(p.Frequency);
            if (p.Type.HasGain()) text += $" · {p.Gain:+0.0;-0.0;0.0} dB";
            if (p.Type.HasQ()) text += $" · Q {p.Q:0.00}";
        }
        if (p.Bypass) text += " · bypassed";

        var chip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 20, 20, 24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 3, 8, 4),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Foreground = new SolidColorBrush(Color.FromArgb(232, 232, 232, 235))
            }
        };
        chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = chip.DesiredSize.Width, h = chip.DesiredSize.Height;
        double x = Math.Clamp(pos.X - w / 2, LeftMargin + 4, Math.Max(LeftMargin + 4, LeftMargin + plotWidth - w - 4));
        double y = pos.Y - EditHandleRadius - 10 - h;
        if (y < TopMargin + 4) y = pos.Y + EditHandleRadius + 10;
        Canvas.SetLeft(chip, x);
        Canvas.SetTop(chip, y);
        _editCanvas!.Children.Add(chip);
    }

    private static string EditFreqText(float freq) =>
        freq >= 1000 ? $"{freq / 1000:0.00} kHz" : $"{freq:0} Hz";

    // ── Band flyout editor ──

    private void EditShowFlyout(EditBandRef band, Point anchor)
    {
        var channel = EditChannel();
        if (channel == null || _viewModel == null) return;

        var content = new StackPanel { Spacing = 10, MinWidth = 232 };
        var flyout = new Flyout { Content = content };

        void Render()
        {
            content.Children.Clear();
            var p = EditGetParams(band);
            bool alive = p != null &&
                (band.IsXover ? CrossoverFilter.TryGetMeta(p.Type, out _) : p.Type != FilterType.Flat);
            if (!alive) { flyout.Hide(); return; }

            content.Children.Add(EditFlyoutHeader(band, p!, flyout, Render));
            if (band.IsXover) EditFlyoutXoBody(content, band, p!, Render);
            else EditFlyoutPeqBody(content, band, p!, Render);
        }

        Render();
        if (content.Children.Count == 0) return;
        flyout.ShowAt(_rootGrid, new FlyoutShowOptions
        {
            Position = new Point(anchor.X, anchor.Y - EditHandleRadius - 6),
            Placement = FlyoutPlacementMode.Top
        });
    }

    private Grid EditFlyoutHeader(EditBandRef band, FilterParams p, Flyout flyout, Action rerender)
    {
        var channel = EditChannel()!;
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        var c = channel.Color;
        if (band.IsXover)
            titleRow.Children.Add(WrapShape(EditDiamond(new Point(5, 7), 5, c, Color.FromArgb(60, 255, 255, 255), 1), 10, 14));
        else
            titleRow.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center });
        titleRow.Children.Add(new TextBlock
        {
            Text = band.IsXover ? $"Crossover {band.Index + 1}" : $"Band {band.Index + 1}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(titleRow, 0);
        header.Children.Add(titleRow);

        if (_viewModel!.BandBypassSupported)
        {
            var bypassBtn = new ToggleButton
            {
                Content = new FontIcon { Glyph = "\uE7E8", FontSize = 12 },   // power glyph
                IsChecked = p.Bypass,
                Width = 30, Height = 30, MinWidth = 30,
                Padding = new Thickness(0)
            };
            ToolTipService.SetToolTip(bypassBtn, "Bypass band");
            bypassBtn.Click += async (_, _) =>
            {
                bool newBypass = !p.Bypass;
                if (band.IsXover) await _viewModel.SetXoverBandBypass(_selectedChannelId, band.Index, newBypass);
                else await _viewModel.SetBandBypass(_selectedChannelId, band.Index, newBypass);
                rerender();
            };
            Grid.SetColumn(bypassBtn, 2);
            header.Children.Add(bypassBtn);
        }

        var removeBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 },       // delete glyph
            Width = 30, Height = 30, MinWidth = 30,
            Padding = new Thickness(0)
        };
        ToolTipService.SetToolTip(removeBtn, "Remove band");
        removeBtn.Click += (_, _) =>
        {
            EditRemoveBand(band);
            flyout.Hide();
        };
        Grid.SetColumn(removeBtn, 3);
        header.Children.Add(removeBtn);

        return header;
    }

    /// <summary>Hosts an absolutely-positioned shape (the header diamond) inside
    /// a fixed-size canvas so it lays out inline with the title text.</summary>
    private static Canvas WrapShape(Shape shape, double width, double height)
    {
        var canvas = new Canvas { Width = width, Height = height, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(shape);
        return canvas;
    }

    private void EditFlyoutPeqBody(StackPanel content, EditBandRef band, FilterParams p, Action rerender)
    {
        if (p.Type.IsLinkwitzTransform())
        {
            content.Children.Add(new TextBlock
            {
                Text = "Linkwitz Transform — edit f0/Q0 and fp/Qp in the band list below the graph.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 232,
                Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            return;
        }

        // Same order and labels as the band list's type picker (LT excluded —
        // its four-field editor lives in the row's popover).
        var typeItems = new List<(string Label, FilterType Type)>
        {
            ("Peaking", FilterType.Peaking),
            ("Low Shelf 6dB", FilterType.LowShelf1),
            ("Low Shelf 12dB", FilterType.LowShelf),
            ("High Shelf 6dB", FilterType.HighShelf1),
            ("High Shelf 12dB", FilterType.HighShelf),
            ("Low Cut 6dB", FilterType.HighPass1),
            ("Low Cut 12dB", FilterType.HighPass),
            ("High Cut 6dB", FilterType.LowPass1),
            ("High Cut 12dB", FilterType.LowPass),
            ("Notch", FilterType.Notch),
            ("All Pass 6dB", FilterType.AllPass1),
            ("All Pass 12dB", FilterType.AllPass),
        };
        var typeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        int sel = 0;
        for (int i = 0; i < typeItems.Count; i++)
        {
            typeCombo.Items.Add(new ComboBoxItem { Content = typeItems[i].Label, Tag = typeItems[i].Type });
            if (typeItems[i].Type == p.Type) sel = i;
        }
        typeCombo.SelectedIndex = sel;
        typeCombo.SelectionChanged += (_, _) =>
        {
            if (typeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not FilterType t || t == p.Type) return;
            var np = p.Clone();
            np.Type = t;
            if (!t.HasGain()) np.Gain = 0f;
            _ = _viewModel!.SetFilter(_selectedChannelId, band.Index, np);
            rerender();
        };
        content.Children.Add(typeCombo);

        var values = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        values.Children.Add(EditValueBox("Hz", p.Frequency, 0, v =>
        {
            var np = p.Clone();
            np.Frequency = Math.Clamp(v, EditPeqMinFreq, EditMaxFreq);
            _ = _viewModel!.SetFilter(_selectedChannelId, band.Index, np);
            rerender();
        }));
        if (p.Type.HasGain())
            values.Children.Add(EditValueBox("dB", p.Gain, 1, v =>
            {
                var np = p.Clone();
                np.Gain = Math.Clamp(v, EditMinGain, EditMaxGain);
                _ = _viewModel!.SetFilter(_selectedChannelId, band.Index, np);
                rerender();
            }));
        if (p.Type.HasQ())
            values.Children.Add(EditValueBox("Q", p.Q, 3, v =>
            {
                var np = p.Clone();
                np.Q = Math.Clamp(v, EditMinQ, EditMaxQ);
                _ = _viewModel!.SetFilter(_selectedChannelId, band.Index, np);
                rerender();
            }));
        content.Children.Add(values);
    }

    private void EditFlyoutXoBody(StackPanel content, EditBandRef band, FilterParams p, Action rerender)
    {
        CrossoverFilter.TryGetMeta(p.Type, out var meta);

        void Apply(XoverFamily family, bool isHighPass, int order, float freq)
        {
            FilterType newType;
            if (family == XoverFamily.None)
            {
                newType = FilterType.Flat;
            }
            else
            {
                var orders = CrossoverFilter.OrdersFor(family);
                if (!orders.Contains(order)) order = orders.Contains(4) ? 4 : orders[0];
                newType = CrossoverFilter.Compose(family, isHighPass, order) ?? FilterType.Flat;
            }
            var np = p.Clone();
            np.Type = newType;
            np.Frequency = Math.Clamp(freq, EditXoMinFreq, EditMaxFreq);
            _ = _viewModel!.SetXoverFilter(_selectedChannelId, band.Index, np);
            rerender();
        }

        var familyCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        familyCombo.Items.Add(new ComboBoxItem { Content = "Off", Tag = XoverFamily.None });
        foreach (var fam in CrossoverFilter.Families)
            familyCombo.Items.Add(new ComboBoxItem { Content = CrossoverFilter.FamilyName(fam), Tag = fam });
        familyCombo.SelectedIndex = Array.IndexOf(CrossoverFilter.Families, meta.Family) + 1;
        content.Children.Add(familyCombo);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        var shapeCombo = new ComboBox { MinWidth = 96 };
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Low Pass", Tag = false });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "High Pass", Tag = true });
        shapeCombo.SelectedIndex = meta.IsHighPass ? 1 : 0;
        row.Children.Add(shapeCombo);

        var slopeCombo = new ComboBox { MinWidth = 104 };
        var famOrders = CrossoverFilter.OrdersFor(meta.Family);
        int slopeSel = 0;
        for (int i = 0; i < famOrders.Count; i++)
        {
            slopeCombo.Items.Add(new ComboBoxItem { Content = CrossoverFilter.SlopeLabel(famOrders[i]), Tag = famOrders[i] });
            if (famOrders[i] == meta.Order) slopeSel = i;
        }
        slopeCombo.SelectedIndex = slopeSel;
        row.Children.Add(slopeCombo);
        content.Children.Add(row);

        content.Children.Add(EditValueBox("Hz", p.Frequency, 0, v =>
            Apply(meta.Family, meta.IsHighPass, meta.Order, v)));

        (XoverFamily, bool, int) ReadCombos()
        {
            var fam = familyCombo.SelectedItem is ComboBoxItem fi && fi.Tag is XoverFamily f ? f : meta.Family;
            bool hp = shapeCombo.SelectedItem is ComboBoxItem si && si.Tag is bool b ? b : meta.IsHighPass;
            int order = slopeCombo.SelectedItem is ComboBoxItem oi && oi.Tag is int o ? o : meta.Order;
            return (fam, hp, order);
        }

        familyCombo.SelectionChanged += (_, _) => { var (f, hp, o) = ReadCombos(); Apply(f, hp, o, p.Frequency); };
        shapeCombo.SelectionChanged += (_, _) => { var (f, hp, o) = ReadCombos(); Apply(f, hp, o, p.Frequency); };
        slopeCombo.SelectionChanged += (_, _) => { var (f, hp, o) = ReadCombos(); Apply(f, hp, o, p.Frequency); };
    }

    private StackPanel EditValueBox(string suffix, float value, int decimals, Action<float> commit)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var box = new TextBox
        {
            Width = 64,
            Text = value.ToString(decimals switch { 0 => "0", 1 => "0.0", _ => "0.###" },
                System.Globalization.CultureInfo.InvariantCulture),
            FontSize = 13,
            FontFamily = new FontFamily("Cascadia Code, Consolas")
        };
        void Commit()
        {
            if (float.TryParse(box.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                commit(v);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };
        box.LostFocus += (_, _) => Commit();
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = suffix,
            FontSize = 10,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }
}
