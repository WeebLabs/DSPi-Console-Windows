using DSPiConsole.Core.Models;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// Custom control for rendering Bode plot frequency response curves.
/// Uses a dual-canvas layout: _plotCanvas (clipped) for grid/curves, _labelCanvas (unclipped) for axis labels.
/// </summary>
public sealed partial class BodePlotControl : UserControl
{
    private Grid? _rootGrid;
    private Canvas? _plotCanvas;
    private Canvas? _labelCanvas;
    private Canvas? _dbScaleHitArea;
    private MainViewModel? _viewModel;
    private int _selectedChannelId = -1;
    private bool _dottedInactiveEnabled = true;
    private bool _isPopout;
    private bool _ignoreVisibility;
    private HashSet<int> _linkedInputIds = new();
    private Dictionary<int, bool>? _localVisibility;

    /// <summary>
    /// Set the selected channel ID for dotted-line treatment of non-selected channels.
    /// Pass -1 (or any invalid ID) to show all channels as solid (dashboard mode).
    /// </summary>
    public void SetSelectedChannel(int channelId)
    {
        if (_selectedChannelId == channelId) return;
        _selectedChannelId = channelId;
        RefreshEditor(redraw: false);
        Redraw(gridChanged: true);
    }

    /// <summary>
    /// Channel ids belonging to linked input pairs. Their curves render with a
    /// horizontal gradient blending the pair's two colors, and neither member is
    /// dotted while the other is selected. Does not trigger a redraw — call
    /// SetSelectedChannel or Invalidate after to apply the change.
    /// </summary>
    public void SetLinkedInputs(IEnumerable<int> channelIds)
    {
        _linkedInputIds = new HashSet<int>(channelIds);
    }

    /// <summary>
    /// Enable or disable dotted lines for non-selected channels.
    /// </summary>
    public void SetDottedInactiveEnabled(bool enabled)
    {
        if (_dottedInactiveEnabled == enabled) return;
        _dottedInactiveEnabled = enabled;
        Redraw(gridChanged: true);
    }

    /// <summary>
    /// Mark this control as the popout instance so it reads the correct setting.
    /// </summary>
    public void SetIsPopout(bool isPopout)
    {
        _isPopout = isPopout;
        _dottedInactiveEnabled = AppSettings.Instance.DottedInactiveChannels;
    }

    /// <summary>
    /// When true, use local visibility state instead of ViewModel's.
    /// Used by the popout graph when "follows selected channel" is off.
    /// </summary>
    public void SetIgnoreVisibility(bool ignore, MainViewModel? viewModelOverride = null)
    {
        if (_ignoreVisibility == ignore) return;
        _ignoreVisibility = ignore;
        var vm = viewModelOverride ?? _viewModel;
        if (ignore && vm != null)
        {
            // Snapshot current ViewModel visibility state
            _localVisibility = new Dictionary<int, bool>();
            foreach (var ch in Channel.All)
                _localVisibility[(int)ch.Id] = vm.GetChannelVisibility(ch);
        }
        else
        {
            _localVisibility = null;
        }
        Redraw(gridChanged: true);
    }

    /// <summary>
    /// Toggle visibility in local mode. Used by popout legend pills when ignoring ViewModel visibility.
    /// </summary>
    public void ToggleLocalVisibility(int channelId)
    {
        if (_localVisibility == null) return;
        _localVisibility[channelId] = !(_localVisibility.TryGetValue(channelId, out var v) && v);
        Redraw(gridChanged: true);
    }

    public bool GetLocalVisibility(int channelId) =>
        _localVisibility == null || !_localVisibility.TryGetValue(channelId, out var v) || v;

    private const int NumPoints = 201;

    // Plot area margins (px)
    private double LeftMargin => AppSettings.Instance.ShowDbUnits ? 36 : 22;
    private const double BottomMargin = 16;
    private const double TopMargin = 9;
    // Right gutter grows to hold the phase (degree) axis labels when phase is shown.
    private double RightMargin => AppSettings.Instance.ShowPhase ? 34 : 8;

    // Settings-derived properties
    private float MinFreq => (float)AppSettings.Instance.GraphMinFrequency;
    private float MaxFreq => (float)AppSettings.Instance.GraphMaxFrequency;
    private float DbTop => (float)(AppSettings.Instance.GraphDbCenter + AppSettings.Instance.GraphDbRange / 2.0);
    private float DbBottom => (float)(AppSettings.Instance.GraphDbCenter - AppSettings.Instance.GraphDbRange / 2.0);
    private float DbSpan => (float)AppSettings.Instance.GraphDbRange;

    // Fixed frequency set for the data pipeline (201 points, 20–20kHz log-spaced)
    private const float DataMinFreq = 10.0f;
    private const float DataMaxFreq = 20000.0f;

    // Animation state
    private readonly Dictionary<int, float[]> _currentMagnitudes = new();
    private readonly Dictionary<int, float[]> _targetMagnitudes = new();
    // Phase curves (degrees), same 201-point grid; animated the same way.
    private readonly Dictionary<int, float[]> _currentPhases = new();
    private readonly Dictionary<int, float[]> _targetPhases = new();
    private readonly Dictionary<int, Polyline> _phaseLines = new();
    private readonly DispatcherTimer _animTimer;
    private bool _isAnimating;

    // Curve fade opacity (1 = visible, 0 = hidden)
    private double _curveOpacity = 1.0;

    // Cached polyline references per channel (for glow: 3 per channel, otherwise 1)
    private readonly Dictionary<int, List<Polyline>> _channelPolylines = new();

    public BodePlotControl()
    {
        _rootGrid = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(128, 32, 32, 32))
        };

        _plotCanvas = new Canvas();
        _labelCanvas = new Canvas { IsHitTestVisible = false };
        _dbScaleHitArea = new Canvas
        {
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = LeftMargin
        };
        _dbScaleHitArea.PointerWheelChanged += OnDbScalePointerWheelChanged;

        _rootGrid.Children.Add(_plotCanvas);
        _rootGrid.Children.Add(_labelCanvas);
        _rootGrid.Children.Add(_dbScaleHitArea);
        Content = _rootGrid;

        InitializeEditing();   // on-graph band editor (BodePlotControl.Editor.cs)

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _animTimer.Tick += OnAnimationTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            _viewModel = vm;
            _viewModel.FiltersChanged += OnFiltersChanged;
            _viewModel.VisibilityChanged += OnVisibilityChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.MatrixOutputGainChanged += OnOutputGainChanged;
            _viewModel.InputPreampExtChanged += OnPreampExtChanged;
            AppSettings.Instance.SettingsChanged += OnSettingsChanged;

            foreach (var channel in Channel.All)
            {
                var id = (int)channel.Id;
                _currentMagnitudes[id] = new float[NumPoints];
                _targetMagnitudes[id] = new float[NumPoints];
                _currentPhases[id] = new float[NumPoints];
                _targetPhases[id] = new float[NumPoints];
            }

            UpdateTargets();
            foreach (var channel in Channel.All)
            {
                var id = (int)channel.Id;
                Array.Copy(_targetMagnitudes[id], _currentMagnitudes[id], NumPoints);
                Array.Copy(_targetPhases[id], _currentPhases[id], NumPoints);
            }
            AttachEditor();
            Redraw(gridChanged: true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _animTimer.Stop();
        _isAnimating = false;
        if (_viewModel != null)
        {
            _viewModel.FiltersChanged -= OnFiltersChanged;
            _viewModel.VisibilityChanged -= OnVisibilityChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.MatrixOutputGainChanged -= OnOutputGainChanged;
            _viewModel.InputPreampExtChanged -= OnPreampExtChanged;
        }
        AppSettings.Instance.SettingsChanged -= OnSettingsChanged;
    }

    private void OnFiltersChanged(object? sender, EventArgs e)
    {
        UpdateTargets();
        StartAnimation();
        RefreshEditor();
    }

    private void OnOutputGainChanged(int outputIndex) { OnLevelOffsetChanged(); RefreshEditor(); }
    private void OnPreampExtChanged(int wireInput) { OnLevelOffsetChanged(); RefreshEditor(); }

    private void OnVisibilityChanged(object? sender, EventArgs e)
    {
        RefreshEditor(redraw: false);
        Redraw(gridChanged: true);
    }
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_dbScaleHitArea != null)
            _dbScaleHitArea.Width = LeftMargin;
        _dottedInactiveEnabled = AppSettings.Instance.DottedInactiveChannels;
        // Phase data depends on the unwrap setting; recompute and snap so toggling
        // Show Phase / Unwrap applies immediately (magnitude targets are unchanged).
        UpdateTargets();
        SnapToTargets();
        // Show Phase changes RightMargin — refresh the canvas clip too, or the
        // wider grid stays sheared at the old phase-axis edge until a resize.
        UpdatePlotClip();
        RefreshEditor(redraw: false);
        Redraw(gridChanged: true);
    }

    /// <summary>Copy every channel's target buffers into its current buffers
    /// (no animation) — used when a settings change should apply instantly.</summary>
    private void SnapToTargets()
    {
        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            if (_targetMagnitudes.TryGetValue(id, out var tm) && _currentMagnitudes.TryGetValue(id, out var cm))
                Array.Copy(tm, cm, NumPoints);
            if (_targetPhases.TryGetValue(id, out var tp) && _currentPhases.TryGetValue(id, out var cp))
                Array.Copy(tp, cp, NumPoints);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnViewModelPropertyChanged(sender, e));
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.Bypass))
        {
            UpdateTargets();
            StartAnimation();
            RefreshEditor();
        }
        else if (e.PropertyName is nameof(MainViewModel.IsDeviceConnected)
                                or nameof(MainViewModel.BandBypassSupported)
                                or nameof(MainViewModel.FirstOrderPassSupported)
                                or nameof(MainViewModel.CrossoverSupported))
        {
            RefreshEditor();
        }
        // Preamp moves the input curves when the level-includes-gain setting is on.
        else if (e.PropertyName is nameof(MainViewModel.InputPreampLDb)
                                or nameof(MainViewModel.InputPreampRDb))
        {
            OnLevelOffsetChanged();
            RefreshEditor();
        }
    }

    // Output gain / ext-input preamp changed (locally or via a device
    // notification — both arrive on the UI thread). Only matters while the
    // curves include the gain offset.
    private void OnLevelOffsetChanged()
    {
        if (!AppSettings.Instance.GraphLevelIncludesGain) return;
        UpdateTargets();
        StartAnimation();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePlotClip();
        Redraw(gridChanged: true);
    }

    private void UpdatePlotClip()
    {
        if (_plotCanvas == null) return;
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double plotWidth = w - LeftMargin - RightMargin;
        double plotHeight = h - TopMargin - BottomMargin;
        if (plotWidth <= 0 || plotHeight <= 0) return;

        _plotCanvas.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(LeftMargin, TopMargin, plotWidth, plotHeight)
        };

        LayoutEditor();
    }

    private void UpdateTargets()
    {
        if (_viewModel == null) return;

        bool unwrap = AppSettings.Instance.PhaseUnwrapped;
        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            var (_, magnitudes) = _viewModel.GetResponseCurve(channel);
            CopyResampled(magnitudes, _targetMagnitudes[id]);

            var (_, phases) = _viewModel.GetPhaseCurve(channel, unwrap);
            if (_targetPhases.TryGetValue(id, out var pbuf))
                CopyResampled(phases, pbuf);
        }
    }

    /// <summary>Copy a source curve into a 201-point buffer, nearest-resampling if
    /// the source length differs and clearing if it is empty.</summary>
    private static void CopyResampled(float[] src, float[] dst)
    {
        if (src.Length == NumPoints)
        {
            Array.Copy(src, dst, NumPoints);
        }
        else if (src.Length > 0)
        {
            for (int i = 0; i < NumPoints; i++)
            {
                float pct = i / (float)(NumPoints - 1);
                int srcIdx = Math.Clamp((int)(pct * (src.Length - 1)), 0, src.Length - 1);
                dst[i] = src[srcIdx];
            }
        }
        else
        {
            Array.Clear(dst);
        }
    }

    private void StartAnimation()
    {
        if (!_isAnimating)
        {
            _isAnimating = true;
            _animTimer.Start();
        }
    }

    private void OnAnimationTick(object? sender, object e)
    {
        float speed = (float)AppSettings.Instance.GraphAnimationSpeed;
        float lerpFactor = Math.Clamp(speed, 0.05f, 0.5f);
        bool allDone = true;

        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            var current = _currentMagnitudes[id];
            var target = _targetMagnitudes[id];

            for (int i = 0; i < NumPoints; i++)
            {
                float diff = target[i] - current[i];
                if (MathF.Abs(diff) > 0.01f)
                {
                    current[i] += diff * lerpFactor;
                    allDone = false;
                }
                else
                {
                    current[i] = target[i];
                }
            }

            if (_currentPhases.TryGetValue(id, out var curP) && _targetPhases.TryGetValue(id, out var tgtP))
            {
                for (int i = 0; i < NumPoints; i++)
                {
                    float diff = tgtP[i] - curP[i];
                    if (MathF.Abs(diff) > 0.05f)
                    {
                        curP[i] += diff * lerpFactor;
                        allDone = false;
                    }
                    else
                    {
                        curP[i] = tgtP[i];
                    }
                }
            }
        }

        Redraw(gridChanged: false);

        if (allDone)
        {
            _animTimer.Stop();
            _isAnimating = false;
        }
    }

    private double XPos(float freq, double plotWidth)
    {
        float logMin = MathF.Log10(MinFreq);
        float logMax = MathF.Log10(MaxFreq);
        float logVal = MathF.Log10(freq);
        return LeftMargin + (logVal - logMin) / (logMax - logMin) * plotWidth;
    }

    private double YPos(float db, double plotHeight)
    {
        float normalized = (db - DbBottom) / DbSpan;
        return TopMargin + plotHeight - (normalized * plotHeight);
    }

    // Phase axis is centered at 0° and scales with the dB zoom (±180° at the
    // default 50 dB range), so magnitude (left, dB) and phase (right, degrees)
    // zoom together — matching the macOS reference.
    private double PhaseTop
    {
        get { double t = 180.0 * DbSpan / 50.0; return t > 0 ? t : 180.0; }
    }

    private double YPosPhase(float deg, double plotHeight)
    {
        double top = PhaseTop;
        double normalized = (deg + top) / (2.0 * top);
        return TopMargin + plotHeight - (normalized * plotHeight);
    }

    /// <summary>Whether a channel's curve should be drawn, honoring popout
    /// local-visibility / output-enabled state.</summary>
    private bool IsChannelVisible(Channel channel)
    {
        if (_viewModel == null) return false;
        var id = (int)channel.Id;
        if (_ignoreVisibility)
        {
            if (!GetLocalVisibility(id)) return false;
            if (channel.IsOutput)
            {
                int outputIndex = _viewModel.GetOutputIndex(id);
                if (outputIndex < 0 || !_viewModel.IsOutputEnabled(outputIndex)) return false;
            }
            return true;
        }
        return _viewModel.GetChannelVisibility(channel);
    }

    public void Invalidate()
    {
        UpdateTargets();
        StartAnimation();
    }

    public double GetCurveOpacity() => _curveOpacity;

    public void SetCurveOpacity(double opacity)
    {
        _curveOpacity = Math.Clamp(opacity, 0.0, 1.0);
        foreach (var polylines in _channelPolylines.Values)
        {
            foreach (var p in polylines)
                p.Opacity = _curveOpacity;
        }
        foreach (var line in _phaseLines.Values)
            line.Opacity = _curveOpacity;
    }

    /// <summary>
    /// Compute the frequency for a given data point index (0..NumPoints-1) in the 20–20kHz log space.
    /// </summary>
    private static float DataFreqAt(int index)
    {
        float t = index / (float)(NumPoints - 1);
        float logMin = MathF.Log10(DataMinFreq);
        float logMax = MathF.Log10(DataMaxFreq);
        return MathF.Pow(10, logMin + t * (logMax - logMin));
    }

    private void Redraw(bool gridChanged)
    {
        if (_plotCanvas == null || _labelCanvas == null) return;

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        double plotWidth = width - LeftMargin - RightMargin;
        double plotHeight = height - TopMargin - BottomMargin;
        if (plotWidth <= 0 || plotHeight <= 0) return;

        if (gridChanged)
        {
            _plotCanvas.Children.Clear();
            _labelCanvas.Children.Clear();
            _channelPolylines.Clear();
            _phaseLines.Clear();

            DrawGrid(plotWidth, plotHeight);
            DrawLabels(plotWidth, plotHeight);
            DrawCurves(plotWidth, plotHeight);
        }
        else if (GroupsKey(CurveGroups()) != _drawnGroups)
        {
            // Curves that were identical have parted (or met) mid-animation.
            _plotCanvas.Children.Clear();
            _labelCanvas.Children.Clear();
            _channelPolylines.Clear();
            _phaseLines.Clear();
            DrawGrid(plotWidth, plotHeight);
            DrawLabels(plotWidth, plotHeight);
            DrawCurves(plotWidth, plotHeight);
        }
        else
        {
            UpdateCurvePoints(plotWidth, plotHeight);
        }
    }

    private void DrawGrid(double plotWidth, double plotHeight)
    {
        var settings = AppSettings.Instance;

        // Frequency grid (vertical lines)
        if (settings.ShowFrequencyGrid)
        {
            var minorColor = GridColor(15);
            var majorColor = GridColor(38);

            // All decade subdivisions from 10 to 20000
            float[] decades = { 10, 100, 1000, 10000 };
            foreach (var decade in decades)
            {
                for (int m = 1; m <= 9; m++)
                {
                    float freq = decade * m;
                    if (freq < MinFreq || freq > MaxFreq) continue;

                    bool isMajor = m == 1 && freq >= 100;
                    double x = XPos(freq, plotWidth);

                    _plotCanvas!.Children.Add(new Line
                    {
                        X1 = x, Y1 = TopMargin,
                        X2 = x, Y2 = TopMargin + plotHeight,
                        Stroke = new SolidColorBrush(isMajor ? majorColor : minorColor),
                        StrokeThickness = 1
                    });
                }
            }
            // Also draw 20kHz if in range
            if (20000 <= MaxFreq && 20000 >= MinFreq)
            {
                double x = XPos(20000, plotWidth);
                _plotCanvas!.Children.Add(new Line
                {
                    X1 = x, Y1 = TopMargin,
                    X2 = x, Y2 = TopMargin + plotHeight,
                    Stroke = new SolidColorBrush(minorColor),
                    StrokeThickness = 1
                });
            }
        }

        // dB grid (horizontal lines)
        if (settings.ShowDbGrid)
        {
            var gridColor = GridColor(25);
            var zeroLineColor = GridColor(76);

            double step = GetDbStep();
            // Find first grid line at or above DbBottom
            double firstDb = Math.Ceiling(DbBottom / step) * step;

            for (double db = firstDb; db <= DbTop; db += step)
            {
                double y = YPos((float)db, plotHeight);
                bool isZero = Math.Abs(db) < 0.01;
                _plotCanvas!.Children.Add(new Line
                {
                    X1 = LeftMargin, Y1 = y,
                    X2 = LeftMargin + plotWidth, Y2 = y,
                    Stroke = new SolidColorBrush(isZero ? zeroLineColor : gridColor),
                    StrokeThickness = 1
                });
            }
        }
    }

    /// <summary>A grid line at its standard alpha, scaled by Grid Opacity
    /// (0 hides the grid, 1 is standard, 2 twice as strong).</summary>
    private static Color GridColor(int standardAlpha) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(standardAlpha * AppSettings.Instance.GraphGridOpacity), 0, 255), 255, 255, 255);

    private void DrawLabels(double plotWidth, double plotHeight)
    {
        var settings = AppSettings.Instance;
        var labelColor = new SolidColorBrush(Color.FromArgb(102, 255, 255, 255));

        // Frequency labels (bottom edge)
        if (settings.ShowFrequencyLabels)
        {
            float[] freqLabels = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
            foreach (var freq in freqLabels)
            {
                if (freq < MinFreq || freq > MaxFreq) continue;

                double x = XPos(freq, plotWidth);
                string text = FormatFrequency(freq);

                var tb = new TextBlock
                {
                    Text = text,
                    FontSize = 9,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = labelColor
                };

                // Measure and center horizontally
                tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double tbWidth = tb.DesiredSize.Width;

                Canvas.SetLeft(tb, x - tbWidth / 2);
                Canvas.SetTop(tb, TopMargin + plotHeight + 2);
                _labelCanvas!.Children.Add(tb);
            }
        }

        // dB labels (left edge)
        if (settings.ShowDbLabels)
        {
            double step = GetDbStep();
            double firstDb = Math.Ceiling(DbBottom / step) * step;

            for (double db = firstDb; db <= DbTop; db += step)
            {
                double y = YPos((float)db, plotHeight);

                // Skip labels that fall outside the plot area
                if (y < TopMargin - 4 || y > TopMargin + plotHeight + 4) continue;

                string text = FormatDb(db);
                var tb = new TextBlock
                {
                    Text = text,
                    FontSize = 9,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = labelColor
                };

                tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double tbHeight = tb.DesiredSize.Height;
                double tbWidth = tb.DesiredSize.Width;

                Canvas.SetLeft(tb, LeftMargin - tbWidth - 4);
                Canvas.SetTop(tb, y - tbHeight / 2);
                _labelCanvas!.Children.Add(tb);
            }
        }

        // Phase axis labels (degrees, right edge) when the phase overlay is on.
        if (settings.ShowPhase)
        {
            var phaseLabelColor = new SolidColorBrush(Color.FromArgb(150, 235, 235, 235));
            double top = PhaseTop;
            double[] ticks = { top, top / 2, 0, -top / 2, -top };
            double rightEdge = ActualWidth - RightMargin;
            foreach (var p in ticks)
            {
                double normalized = (p + top) / (2.0 * top);
                double y = TopMargin + plotHeight - normalized * plotHeight;
                string label = Math.Abs(p) < 0.01 ? "0°" : $"{(p > 0 ? "+" : "")}{(int)Math.Round(p)}°";
                var tb = new TextBlock
                {
                    Text = label,
                    FontSize = 9,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = phaseLabelColor
                };
                tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tb, rightEdge + 3);
                Canvas.SetTop(tb, y - tb.DesiredSize.Height / 2);
                _labelCanvas!.Children.Add(tb);
            }
        }
    }

    /// <summary>The curves drawn last, as groups of channels sharing one
    /// identical curve (leader first).</summary>
    private string _drawnGroups = "";

    /// <summary>
    /// Visible channels grouped by identical curves: several channels with the
    /// same response are drawn as one line with a colour gradient (macOS
    /// Console). The channel being edited on the graph is left out — the editor
    /// overlay draws it, curve and all.
    /// </summary>
    private List<List<int>> CurveGroups()
    {
        var groups = new List<List<int>>();
        if (_viewModel == null) return groups;
        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            if (id == _editedChannelId || !IsChannelVisible(channel)) continue;
            if (!_currentMagnitudes.TryGetValue(id, out var mags)) continue;
            var home = groups.FirstOrDefault(g => SameCurve(_currentMagnitudes[g[0]], mags));
            if (home != null) home.Add(id); else groups.Add(new List<int> { id });
        }
        return groups;
    }

    private static bool SameCurve(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 0.01f) return false;
        return true;
    }

    private static string GroupsKey(List<List<int>> groups) =>
        string.Join("|", groups.Select(g => string.Join(",", g)));

    private void DrawCurves(double plotWidth, double plotHeight)
    {
        if (_viewModel == null) return;

        var settings = AppSettings.Instance;
        bool showGlow = settings.ShowGraphGlow;
        float lineWidth = (float)settings.GraphLineWidth;
        var groups = CurveGroups();
        _drawnGroups = GroupsKey(groups);

        foreach (var group in groups)
        {
            var id = group[0];
            var channel = Channel.FromId((ChannelId)id);
            var magnitudes = _currentMagnitudes[id];
            var polylines = new List<Polyline>();

            var points = BuildPoints(magnitudes, plotWidth, plotHeight);
            // Don't dot linked-pair inputs — both members of the pair are "active" —
            // nor a shared line the selected channel is part of.
            bool isLinkedInput = group.Any(_linkedInputIds.Contains);
            bool isDotted = _dottedInactiveEnabled && _selectedChannelId >= 0
                            && !group.Contains(_selectedChannelId) && !isLinkedInput;
            var dashArray = isDotted ? new DoubleCollection { 4, 3 } : null;

            if (showGlow && !isDotted)
            {
                var outerGlow = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(50, channel.Color.R, channel.Color.G, channel.Color.B)),
                    StrokeThickness = lineWidth * 4,
                    StrokeLineJoin = PenLineJoin.Round,
                    Points = ClonePoints(points)
                };
                _plotCanvas!.Children.Add(outerGlow);
                polylines.Add(outerGlow);

                var innerGlow = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(100, channel.Color.R, channel.Color.G, channel.Color.B)),
                    StrokeThickness = lineWidth * 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    Points = ClonePoints(points)
                };
                _plotCanvas!.Children.Add(innerGlow);
                polylines.Add(innerGlow);
            }

            // One line for several identical curves blends their colours; a
            // linked input pair always does.
            var colors = group.Select(g => Channel.FromId((ChannelId)g).Color).ToList();
            if (group.Count == 1 && _linkedInputIds.Contains(id))
            {
                int partnerId = ChannelMap.LinkedPartnerId(id);
                colors = new List<Color>
                {
                    Channel.FromId((ChannelId)Math.Min(id, partnerId)).Color,
                    Channel.FromId((ChannelId)Math.Max(id, partnerId)).Color,
                };
            }
            Brush strokeBrush;
            if (colors.Count > 1)
            {
                var gradient = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0.5),
                    EndPoint = new Windows.Foundation.Point(1, 0.5)
                };
                for (int i = 0; i < colors.Count; i++)
                {
                    // Two colours meet in the middle third, as the linked pair
                    // gradient always did; more spread evenly.
                    double offset = colors.Count == 2 ? (i == 0 ? 0.3 : 0.7) : (double)i / (colors.Count - 1);
                    gradient.GradientStops.Add(new GradientStop { Color = colors[i], Offset = offset });
                }
                strokeBrush = gradient;
            }
            else
            {
                strokeBrush = new SolidColorBrush(channel.Color);
            }

            var mainLine = new Polyline
            {
                Stroke = strokeBrush,
                StrokeThickness = lineWidth,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeDashArray = dashArray,
                Points = points
            };
            _plotCanvas!.Children.Add(mainLine);
            polylines.Add(mainLine);

            foreach (var pl in polylines)
                pl.Opacity = _curveOpacity;
            _channelPolylines[id] = polylines;
        }

        if (AppSettings.Instance.ShowPhase)
            DrawPhaseCurves(plotWidth, plotHeight);
    }

    // Phase overlay: dotted curve on the right-side degree axis. Draws the selected
    // channel's phase (light gray, like the macOS ref); if no channel is selected
    // (e.g. an un-followed popout), draws each visible channel's phase in its own
    // color so it stays associable with its magnitude curve.
    private void DrawPhaseCurves(double plotWidth, double plotHeight)
    {
        if (_viewModel == null) return;
        float lineWidth = (float)AppSettings.Instance.GraphLineWidth;
        bool haveSelection = _selectedChannelId >= 0;
        var grayColor = Color.FromArgb(235, 235, 235, 235);

        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            if (haveSelection && id != _selectedChannelId) continue;
            if (!IsChannelVisible(channel)) continue;
            if (!_currentPhases.TryGetValue(id, out var phase)) continue;

            var line = new Polyline
            {
                Stroke = new SolidColorBrush(haveSelection ? grayColor : channel.Color),
                StrokeThickness = lineWidth * 0.9,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeDashArray = new DoubleCollection { 1, 3 },
                Opacity = _curveOpacity * (haveSelection ? 1.0 : 0.6),
                Points = BuildPhasePoints(phase, plotWidth, plotHeight)
            };
            _plotCanvas!.Children.Add(line);
            _phaseLines[id] = line;
        }
    }

    private void UpdateCurvePoints(double plotWidth, double plotHeight)
    {
        if (_viewModel == null) return;

        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            if (!_channelPolylines.ContainsKey(id)) continue;
            if (!_currentMagnitudes.ContainsKey(id)) continue;

            var magnitudes = _currentMagnitudes[id];
            var points = BuildPoints(magnitudes, plotWidth, plotHeight);

            foreach (var polyline in _channelPolylines[id])
            {
                polyline.Points = ClonePoints(points);
            }
        }

        if (AppSettings.Instance.ShowPhase)
        {
            foreach (var kv in _phaseLines)
            {
                if (_currentPhases.TryGetValue(kv.Key, out var phase))
                    kv.Value.Points = BuildPhasePoints(phase, plotWidth, plotHeight);
            }
        }
    }

    private PointCollection BuildPoints(float[] magnitudes, double plotWidth, double plotHeight)
    {
        var points = new PointCollection();
        for (int i = 0; i < NumPoints; i++)
        {
            float freq = DataFreqAt(i);
            double x = XPos(freq, plotWidth);
            double y = YPos(magnitudes[i], plotHeight);
            points.Add(new Windows.Foundation.Point(x, y));
        }
        return points;
    }

    private PointCollection BuildPhasePoints(float[] phases, double plotWidth, double plotHeight)
    {
        var points = new PointCollection();
        for (int i = 0; i < NumPoints; i++)
        {
            float freq = DataFreqAt(i);
            double x = XPos(freq, plotWidth);
            double y = YPosPhase(phases[i], plotHeight);
            points.Add(new Windows.Foundation.Point(x, y));
        }
        return points;
    }

    private double GetDbStep()
    {
        double span = DbSpan;
        if (span <= 12) return 1;
        if (span <= 30) return 3;
        if (span <= 60) return 5;
        return 10;
    }

    private static string FormatFrequency(float freq)
    {
        if (freq >= 1000) return $"{freq / 1000:0.#}k";
        return freq.ToString("0");
    }

    private static string FormatDb(double db)
    {
        int rounded = (int)Math.Round(db);
        string unit = AppSettings.Instance.ShowDbUnits ? " dB" : "";
        if (rounded > 0) return $"+{rounded}{unit}";
        if (rounded < 0) return $"{rounded}{unit}";
        return $"0{unit}";
    }

    private void OnDbScalePointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        var settings = AppSettings.Instance;

        // Scroll up = zoom in (smaller range), scroll down = zoom out. Continuous:
        // 3 dB a mouse notch, finer steps from a precision touchpad.
        double newRange = Math.Clamp(settings.GraphDbRange - delta / 120.0 * 3.0, 10, 100);
        if (Math.Abs(newRange - settings.GraphDbRange) > 0.01)
        {
            settings.GraphDbRange = newRange;
            settings.NotifyChanged();
        }

        e.Handled = true;
    }

    private static PointCollection ClonePoints(PointCollection source)
    {
        var clone = new PointCollection();
        foreach (var pt in source)
            clone.Add(pt);
        return clone;
    }
}
